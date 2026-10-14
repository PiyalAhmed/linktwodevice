import Foundation

/// Mac identity + the shared pairing key, stored in ~/Library/Application Support/LinkTwo/pairing.json (mode 0600).
/// One phone at a time: "Reset Pairing" rotates the key, which locks out the old phone.
final class PairingStore {
    static let shared = PairingStore()

    private struct State: Codable {
        var macId: String
        var key: Data
        var pairedDevice: String?
    }

    private let lock = NSLock()
    private let url: URL
    private var state: State

    private init() {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("LinkTwo", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        url = dir.appendingPathComponent("pairing.json")
        if let d = try? Data(contentsOf: url), let s = try? JSONDecoder().decode(State.self, from: d) {
            state = s
        } else {
            state = State(macId: UUID().uuidString, key: LinkCrypto.randomBytes(32), pairedDevice: nil)
            save()
        }
    }

    var macId: String { lock.withLock { state.macId } }
    var key: Data { lock.withLock { state.key } }
    var pairedDevice: String? { lock.withLock { state.pairedDevice } }

    var macName: String { Host.current().localizedName ?? "Mac" }

    func markPaired(device: String) {
        lock.withLock {
            guard state.pairedDevice != device else { return }
            state.pairedDevice = device
            save()
        }
    }

    func reset() {
        lock.withLock {
            state.key = LinkCrypto.randomBytes(32)
            state.pairedDevice = nil
            save()
        }
    }

    /// Contents of the pairing QR code.
    func pairingURI() -> String {
        var c = URLComponents()
        c.scheme = "linktwo"
        c.host = "pair"
        c.queryItems = [
            URLQueryItem(name: "v", value: "1"),
            URLQueryItem(name: "id", value: macId),
            URLQueryItem(name: "name", value: macName),
            URLQueryItem(name: "key", value: key.base64URLEncoded()),
            URLQueryItem(name: "hosts", value: NetUtil.localIPv4().joined(separator: ",")),
            URLQueryItem(name: "port", value: String(LinkServer.port)),
        ]
        return c.string ?? ""
    }

    private func save() {
        guard let d = try? JSONEncoder().encode(state) else { return }
        try? d.write(to: url, options: .atomic)
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }
}

enum NetUtil {
    /// IPv4 addresses of active en* interfaces (Wi-Fi / Ethernet). Only a hint for the phone; Bonjour is the fallback.
    static func localIPv4() -> [String] {
        var result: [String] = []
        var ifaddr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddr) == 0, let first = ifaddr else { return [] }
        defer { freeifaddrs(ifaddr) }
        for ptr in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let ifa = ptr.pointee
            guard let addr = ifa.ifa_addr, addr.pointee.sa_family == UInt8(AF_INET) else { continue }
            let flags = Int32(ifa.ifa_flags)
            guard flags & IFF_UP != 0, flags & IFF_LOOPBACK == 0,
                  String(cString: ifa.ifa_name).hasPrefix("en") else { continue }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            if getnameinfo(addr, socklen_t(addr.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0 {
                result.append(String(cString: host))
            }
        }
        return result
    }
}
