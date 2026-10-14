import Foundation
import CryptoKit

// Wire format: see docs/PROTOCOL.md.

enum LinkError: Error {
    case badFrame
    case replay
}

/// One application message: a JSON header plus an optional binary payload.
struct Message {
    var header: [String: Any]
    var payload: Data

    init(_ header: [String: Any], payload: Data = Data()) {
        self.header = header
        self.payload = payload
    }

    var type: String { header["t"] as? String ?? "" }
    func string(_ key: String) -> String { header[key] as? String ?? "" }
    func bool(_ key: String) -> Bool { header[key] as? Bool ?? false }
    func int(_ key: String) -> Int { (header[key] as? NSNumber)?.intValue ?? 0 }

    func encode() -> Data {
        let json = (try? JSONSerialization.data(withJSONObject: header)) ?? Data("{}".utf8)
        var d = Data(capacity: 4 + json.count + payload.count)
        d.appendUInt32BE(UInt32(json.count))
        d.append(json)
        d.append(payload)
        return d
    }

    static func decode(_ raw: Data) throws -> Message {
        let d = Data(raw)
        guard d.count >= 4 else { throw LinkError.badFrame }
        let h = Int(d.uint32BE(at: 0))
        guard d.count >= 4 + h,
              let obj = try JSONSerialization.jsonObject(with: d.subdata(in: 4..<(4 + h))) as? [String: Any]
        else { throw LinkError.badFrame }
        return Message(obj, payload: d.subdata(in: (4 + h)..<d.count))
    }
}

enum LinkCrypto {
    private static let info = Data("linktwo v1".utf8)

    /// HKDF-SHA256(pairingKey, salt = phoneNonce || macNonce) -> (phone→mac key, mac→phone key).
    static func sessionKeys(pairingKey: Data, phoneNonce: Data, macNonce: Data) -> (phoneToMac: SymmetricKey, macToPhone: SymmetricKey) {
        let okm = HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: pairingKey),
            salt: phoneNonce + macNonce,
            info: info,
            outputByteCount: 64
        )
        let bytes = okm.withUnsafeBytes { Data($0) }
        return (SymmetricKey(data: bytes.prefix(32)), SymmetricKey(data: bytes.suffix(32)))
    }

    static func randomBytes(_ n: Int) -> Data {
        var d = Data(count: n)
        _ = d.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, n, $0.baseAddress!) }
        return d
    }
}

/// AES-256-GCM for one direction of one session. Nonce = 4 zero bytes + 8-byte big-endian counter,
/// and the receiver requires the counter to advance by exactly one, which rejects replays and reordering.
final class FrameCipher {
    private let key: SymmetricKey
    private var counter: UInt64 = 0

    init(key: SymmetricKey) { self.key = key }

    private func currentNonce() -> Data {
        var d = Data(count: 4)
        var be = counter.bigEndian
        withUnsafeBytes(of: &be) { d.append(contentsOf: $0) }
        return d
    }

    func seal(_ plain: Data) throws -> Data {
        let nonceData = currentNonce()
        counter += 1
        let box = try AES.GCM.seal(plain, using: key, nonce: AES.GCM.Nonce(data: nonceData))
        return nonceData + box.ciphertext + box.tag
    }

    func open(_ frame: Data) throws -> Data {
        let frame = Data(frame)
        guard frame.count >= 28 else { throw LinkError.badFrame }
        let nonceData = currentNonce()
        guard frame.prefix(12) == nonceData else { throw LinkError.replay }
        counter += 1
        let box = try AES.GCM.SealedBox(
            nonce: AES.GCM.Nonce(data: nonceData),
            ciphertext: frame.subdata(in: 12..<(frame.count - 16)),
            tag: frame.suffix(16)
        )
        return try AES.GCM.open(box, using: key)
    }
}

extension Data {
    func uint32BE(at i: Int) -> UInt32 {
        let s = startIndex + i
        return UInt32(self[s]) << 24 | UInt32(self[s + 1]) << 16 | UInt32(self[s + 2]) << 8 | UInt32(self[s + 3])
    }

    mutating func appendUInt32BE(_ v: UInt32) {
        append(contentsOf: [UInt8(v >> 24 & 0xff), UInt8(v >> 16 & 0xff), UInt8(v >> 8 & 0xff), UInt8(v & 0xff)])
    }

    func base64URLEncoded() -> String {
        base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}
