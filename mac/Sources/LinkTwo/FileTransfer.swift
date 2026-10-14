import Foundation
import UniformTypeIdentifiers

/// Where files from the phone are saved. Defaults to ~/Downloads/LinkTwo (mirrors Download/LinkTwo on the phone).
enum SaveFolder {
    private static let key = "saveFolder"

    static var defaultURL: URL {
        FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("LinkTwo", isDirectory: true)
    }

    /// Thread-safe (UserDefaults). Falls back to the default if the chosen folder was deleted or renamed.
    static var url: URL {
        get {
            if let path = UserDefaults.standard.string(forKey: key) {
                var isDir: ObjCBool = false
                if FileManager.default.fileExists(atPath: path, isDirectory: &isDir), isDir.boolValue {
                    return URL(fileURLWithPath: path, isDirectory: true)
                }
            }
            return defaultURL
        }
        set {
            if newValue.standardizedFileURL == defaultURL.standardizedFileURL {
                UserDefaults.standard.removeObject(forKey: key)
            } else {
                UserDefaults.standard.set(newValue.path, forKey: key)
            }
        }
    }

    static var isDefault: Bool { url.standardizedFileURL == defaultURL.standardizedFileURL }

    /// The folder, created if needed.
    static func ensure() -> URL {
        let u = url
        try? FileManager.default.createDirectory(at: u, withIntermediateDirectories: true)
        return u
    }

    /// "~/Downloads/LinkTwo" style display path.
    static var displayPath: String { (url.path as NSString).abbreviatingWithTildeInPath }
}

/// Receives files from the phone into [SaveFolder]. Runs on the server queue.
final class FileReceiver {
    private struct Incoming {
        let url: URL
        let handle: FileHandle
        let mime: String
        let toClipboard: Bool
    }

    private var files: [String: Incoming] = [:]

    /// (file, mime, toClipboard)
    var onComplete: ((URL, String, Bool) -> Void)?

    func handle(_ m: Message) {
        let id = m.string("id")
        switch m.type {
        case "fileStart":
            let url = Self.uniqueURL(name: m.string("name"))
            guard FileManager.default.createFile(atPath: url.path, contents: nil),
                  let h = try? FileHandle(forWritingTo: url) else { return }
            files[id] = Incoming(url: url, handle: h, mime: m.string("mime"), toClipboard: m.bool("toClipboard"))
        case "fileChunk":
            files[id]?.handle.write(m.payload)
        case "fileEnd":
            guard let f = files.removeValue(forKey: id) else { return }
            try? f.handle.close()
            onComplete?(f.url, f.mime, f.toClipboard)
        case "fileAbort":
            abort(id)
        default:
            break
        }
    }

    func abortAll() {
        for id in Array(files.keys) { abort(id) }
    }

    private func abort(_ id: String) {
        guard let f = files.removeValue(forKey: id) else { return }
        try? f.handle.close()
        try? FileManager.default.removeItem(at: f.url)
    }

    private static func uniqueURL(name: String) -> URL {
        let downloads = SaveFolder.ensure()
        let safe = name.replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: ":", with: "_")
        let clean = safe.isEmpty || safe.hasPrefix(".") ? "file-\(Int(Date().timeIntervalSince1970))" + safe : safe
        let base = (clean as NSString).deletingPathExtension, ext = (clean as NSString).pathExtension
        var url = downloads.appendingPathComponent(clean)
        var n = 2
        while FileManager.default.fileExists(atPath: url.path) {
            url = downloads.appendingPathComponent(ext.isEmpty ? "\(base) \(n)" : "\(base) \(n).\(ext)")
            n += 1
        }
        return url
    }
}

enum FileSender {
    private static let chunkSize = 256 * 1024

    /// Streams a file to the phone chunk by chunk; each chunk waits for the previous send to complete.
    static func send(_ url: URL, name: String? = nil, via server: LinkServer, done: ((Bool) -> Void)? = nil) {
        guard let fh = try? FileHandle(forReadingFrom: url) else { done?(false); return }
        let size = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
        let mime = UTType(filenameExtension: url.pathExtension)?.preferredMIMEType ?? "application/octet-stream"
        let id = UUID().uuidString

        func finish(_ ok: Bool) {
            try? fh.close()
            if !ok { server.send(Message(["t": "fileAbort", "id": id])) }
            done?(ok)
        }

        func next() {
            let data = (try? fh.read(upToCount: chunkSize)) ?? nil
            guard let data, !data.isEmpty else {
                server.send(Message(["t": "fileEnd", "id": id])) { finish($0) }
                return
            }
            server.send(Message(["t": "fileChunk", "id": id], payload: data)) { ok in ok ? next() : finish(false) }
        }

        let start: [String: Any] = ["t": "fileStart", "id": id, "name": name ?? url.lastPathComponent, "size": size, "mime": mime]
        server.send(Message(start)) { ok in ok ? next() : finish(false) }
    }
}
