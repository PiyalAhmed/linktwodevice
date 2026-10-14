import Foundation
import Network

/// Listens on the LAN (TCP + Bonjour) and keeps at most one authenticated phone session.
/// All state lives on `queue`; callbacks fire on `queue`.
final class LinkServer {
    static let port: UInt16 = 47820
    static let serviceType = "_linktwo._tcp"

    let queue = DispatchQueue(label: "dev.piyal.linktwo.net")
    private var listener: NWListener?
    private var sessions: [ObjectIdentifier: PhoneSession] = [:]
    private var active: PhoneSession?

    var onConnected: ((String) -> Void)?
    var onDisconnected: (() -> Void)?
    var onMessage: ((Message) -> Void)?

    func start() {
        queue.async { self.startListener() }
    }

    private func startListener() {
        do {
            let params = NWParameters.tcp
            params.allowLocalEndpointReuse = true
            let l = try NWListener(using: params, on: NWEndpoint.Port(rawValue: Self.port)!)
            l.service = NWListener.Service(type: Self.serviceType, txtRecord: NWTXTRecord(["id": PairingStore.shared.macId]))
            l.newConnectionHandler = { [weak self] c in self?.accept(c) }
            l.stateUpdateHandler = { [weak self] state in
                if case .failed(let error) = state {
                    NSLog("LinkTwo: listener failed: \(error)")
                    l.cancel()
                    self?.queue.asyncAfter(deadline: .now() + 5) { self?.startListener() }
                }
            }
            l.start(queue: queue)
            listener = l
        } catch {
            NSLog("LinkTwo: cannot listen: \(error)")
            queue.asyncAfter(deadline: .now() + 5) { [weak self] in self?.startListener() }
        }
    }

    private func accept(_ connection: NWConnection) {
        let s = PhoneSession(connection: connection, queue: queue)
        let id = ObjectIdentifier(s)
        sessions[id] = s
        s.onAuthenticated = { [weak self, weak s] in
            guard let self, let s else { return }
            let old = self.active
            self.active = s
            if let old, old !== s { old.closeNow() }
            PairingStore.shared.markPaired(device: s.deviceName)
            self.onConnected?(s.deviceName)
        }
        s.onMessage = { [weak self] m in self?.onMessage?(m) }
        s.onClosed = { [weak self, weak s] in
            guard let self else { return }
            self.sessions[id] = nil
            if let s, self.active === s {
                self.active = nil
                self.onDisconnected?()
            }
        }
        s.start()
    }

    /// Thread-safe. Completion reports whether the frame was handed to the network.
    func send(_ m: Message, completion: ((Bool) -> Void)? = nil) {
        queue.async {
            guard let a = self.active else { completion?(false); return }
            a.send(m, completion: completion)
        }
    }

    /// Close every connection and restart the listener + Bonjour advertisement. The phone reconnects by itself.
    func restart() {
        queue.async {
            // Ask the phone to redial right away; it may not notice a dropped socket for a while otherwise.
            self.active?.send(Message(["t": "reconnect"]))
            self.queue.asyncAfter(deadline: .now() + 0.3) { self.restartNow() }
        }
    }

    private func restartNow() {
        queue.async {
            for s in self.sessions.values { s.closeNow() }
            self.listener?.stateUpdateHandler = nil
            self.listener?.cancel()
            self.listener = nil
            self.queue.asyncAfter(deadline: .now() + 0.5) { self.startListener() }
        }
    }

    /// Drop the current phone (used by Reset Pairing).
    func disconnectAll() {
        queue.async {
            for s in self.sessions.values { s.closeNow() }
        }
    }
}

/// One TCP connection from the phone: plaintext hello exchange, then AES-GCM frames.
final class PhoneSession {
    private static let maxFrame = 4 * 1024 * 1024

    private let conn: NWConnection
    private let queue: DispatchQueue
    private var sendCipher: FrameCipher?
    private var recvCipher: FrameCipher?
    private var authenticated = false
    private var closed = false
    private var lastReceived = Date()
    private var pingTimer: DispatchSourceTimer?
    private(set) var deviceName = "Phone"

    var onAuthenticated: (() -> Void)?
    var onMessage: ((Message) -> Void)?
    var onClosed: (() -> Void)?

    init(connection: NWConnection, queue: DispatchQueue) {
        self.conn = connection
        self.queue = queue
    }

    func start() {
        conn.stateUpdateHandler = { [weak self] state in
            switch state {
            case .failed, .cancelled: self?.closeNow()
            default: break
            }
        }
        conn.start(queue: queue)
        readFrame()
        queue.asyncAfter(deadline: .now() + 10) { [weak self] in
            if let self, !self.authenticated { self.closeNow() }
        }
    }

    // MARK: Reading

    private func readExactly(_ n: Int, _ done: @escaping (Data) -> Void) {
        conn.receive(minimumIncompleteLength: n, maximumLength: n) { [weak self] data, _, _, _ in
            guard let self, !self.closed else { return }
            if let data, data.count == n { done(data) } else { self.closeNow() }
        }
    }

    private func readFrame() {
        readExactly(4) { [weak self] hdr in
            guard let self else { return }
            let len = Int(hdr.uint32BE(at: 0))
            guard len > 0, len <= Self.maxFrame else { self.closeNow(); return }
            self.readExactly(len) { body in
                self.handleFrame(body)
                if !self.closed { self.readFrame() }
            }
        }
    }

    private func handleFrame(_ frame: Data) {
        lastReceived = Date()
        guard let recv = recvCipher else { handleHello(frame); return }
        do {
            let msg = try Message.decode(recv.open(frame))
            if !authenticated {
                guard msg.type == "auth" else { closeNow(); return }
                authenticated = true
                send(Message(["t": "authOk", "name": PairingStore.shared.macName]))
                startPings()
                onAuthenticated?()
                return
            }
            switch msg.type {
            case "ping": send(Message(["t": "pong"]))
            case "pong": break
            default: onMessage?(msg)
            }
        } catch {
            // Wrong key (not our phone), tampering, or replay.
            closeNow()
        }
    }

    private func handleHello(_ frame: Data) {
        guard let obj = try? JSONSerialization.jsonObject(with: frame) as? [String: Any],
              obj["t"] as? String == "hello",
              let nonceB64 = obj["nonce"] as? String,
              let phoneNonce = Data(base64Encoded: nonceB64), phoneNonce.count == 16
        else { closeNow(); return }
        deviceName = obj["device"] as? String ?? "Phone"

        let store = PairingStore.shared
        let macNonce = LinkCrypto.randomBytes(16)
        let reply: [String: Any] = ["t": "hello", "v": 1, "nonce": macNonce.base64EncodedString(), "macId": store.macId]
        if let d = try? JSONSerialization.data(withJSONObject: reply) { writeFrame(d, completion: nil) }

        let keys = LinkCrypto.sessionKeys(pairingKey: store.key, phoneNonce: phoneNonce, macNonce: macNonce)
        recvCipher = FrameCipher(key: keys.phoneToMac)
        sendCipher = FrameCipher(key: keys.macToPhone)
    }

    // MARK: Writing

    func send(_ msg: Message, completion: ((Bool) -> Void)? = nil) {
        queue.async { [self] in
            guard !closed, let c = sendCipher, let sealed = try? c.seal(msg.encode()) else {
                completion?(false)
                return
            }
            writeFrame(sealed, completion: completion)
        }
    }

    private func writeFrame(_ body: Data, completion: ((Bool) -> Void)?) {
        var d = Data(capacity: body.count + 4)
        d.appendUInt32BE(UInt32(body.count))
        d.append(body)
        conn.send(content: d, completion: .contentProcessed { error in completion?(error == nil) })
    }

    /// The Mac pings (not the phone) so a dozing phone isn't forced to wake itself up on a timer.
    private func startPings() {
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now() + 30, repeating: 30)
        t.setEventHandler { [weak self] in
            guard let self else { return }
            if Date().timeIntervalSince(self.lastReceived) > 75 { self.closeNow() } else { self.send(Message(["t": "ping"])) }
        }
        t.resume()
        pingTimer = t
    }

    /// Must be called on `queue`.
    func closeNow() {
        guard !closed else { return }
        closed = true
        pingTimer?.cancel()
        conn.cancel()
        onClosed?()
    }
}
