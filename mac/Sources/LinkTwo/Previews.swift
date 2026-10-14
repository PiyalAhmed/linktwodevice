import AppKit
import SwiftUI

/// `LinkTwo --render-previews <dir>` writes PNGs of the call banner and dashboard (light + dark) for design review.
enum Previews {
    @MainActor
    static func render(to dir: String) {
        _ = NSApplication.shared
        let out = URL(fileURLWithPath: dir)
        try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)

        let model = AppModel()
        model.phoneName = "Galaxy A34 5G"
        model.pairedDevice = "Galaxy A34 5G"
        model.connectedSince = Date().addingTimeInterval(-3600)
        model.saveFolder = SaveFolder.displayPath
        model.log(Activity(kind: .clipboard, title: "Clipboard from phone", detail: "https://maps.app.goo.gl/x8Qd"))
        model.log(Activity(kind: .fileIn, title: "IMG_2041.jpg", detail: "Saved to LinkTwo"))
        model.log(Activity(kind: .call, title: "Ammu Rahman", detail: "Incoming call +60 12-345 6789"))
        model.log(Activity(kind: .notification, title: "Ammu", detail: "Are you coming for dinner tonight?", app: "WhatsApp"))

        // Fake artwork: a warm gradient square.
        let art = NSImage(size: NSSize(width: 300, height: 300), flipped: false) { r in
            NSGradient(colors: [NSColor.systemPink, NSColor.systemOrange, NSColor.systemPurple])?.draw(in: r, angle: 45)
            return true
        }
        var np = NowPlaying(key: "m", app: "YouTube Music", title: "Kun Faya Kun", artist: "A. R. Rahman, Javed Ali", playing: true, art: art)
        np.duration = 471; np.position = 128; np.positionDate = Date(); np.canSeek = true; np.volume = 9; np.maxVolume = 15
        model.nowPlaying = np

        for (suffix, name) in [("light", NSAppearance.Name.aqua), ("dark", .darkAqua)] {
            let appearance = NSAppearance(named: name)!
            save(MenuPopover(model: model, openDashboard: {}, quit: {}), size: nil, "popover-\(suffix)", out, appearance)
            var paused = np; paused.playing = false
            save(NowPlayingView(np: paused) { _, _ in }.frame(width: 320).padding(16), size: nil, "nowplaying-paused-\(suffix)", out, appearance)
            save(DashboardView(model: model), size: NSSize(width: 780, height: 700), "dashboard-\(suffix)", out, appearance)
            save(ActivityPage(model: model), size: NSSize(width: 580, height: 480), "activity-\(suffix)", out, appearance)
            let empty = AppModel()
            empty.activityFilter = .activities
            save(ActivityPage(model: empty), size: NSSize(width: 580, height: 480), "activity-empty-\(suffix)", out, appearance)
            save(CallBanner(name: "Ammu Rahman", number: "+60 12-345 6789", phone: "Galaxy A34 5G", onAccept: {}, onDecline: {})
                    .padding(20)
                    .background(Color(nsColor: .windowBackgroundColor)),
                 size: nil, "call-\(suffix)", out, appearance)
        }
        print("rendered to \(out.path)")
    }

    /// Draws through a real NSHostingView in an offscreen window so AppKit-backed controls (Form, Toggle) render.
    @MainActor
    private static func save<V: View>(_ view: V, size: NSSize?, _ name: String, _ dir: URL, _ appearance: NSAppearance) {
        let host = NSHostingView(rootView: view)
        let s = size ?? host.fittingSize
        let window = NSWindow(contentRect: NSRect(origin: .zero, size: s), styleMask: [.borderless], backing: .buffered, defer: false)
        window.appearance = appearance
        window.contentView = host
        host.frame = NSRect(origin: .zero, size: s)
        host.layoutSubtreeIfNeeded()
        RunLoop.current.run(until: Date().addingTimeInterval(0.3))
        guard let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds) else { return }
        host.cacheDisplay(in: host.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: dir.appendingPathComponent(name + ".png"))
    }
}
