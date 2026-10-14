import AppKit
import SwiftUI
import ServiceManagement

final class AppDelegate: NSObject, NSApplicationDelegate {
    /// Anything older than this when it arrives isn't "live" any more and is dropped.
    private static let maxAge: TimeInterval = 90

    private var statusItem: NSStatusItem!
    private let popover = NSPopover()
    private var clickMonitors: [Any] = []
    private var reconnectGeneration = 0
    private var closing = false
    private var closeGeneration = 0
    private let server = LinkServer()
    private let notifier = Notifier()
    private let callPanel = CallPanel()
    private let clipboard = ClipboardSync()
    private let receiver = FileReceiver()
    private let presence = MacPresence()
    private let model = AppModel()
    private var dashboard: DashboardWindowController?
    private var iconCache: [String: Data] = [:]
    private var artCache: [String: Data] = [:]
    /// Paused by the user from the menu or dashboard. Kept across launches.
    private var paused = UserDefaults.standard.bool(forKey: "paused")
    /// Syncing happens only while the Mac is in use and not paused.
    private var live: Bool { presence.active && !paused }

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.target = self
        statusItem.button?.action = #selector(togglePopover)
        let host = NSHostingController(rootView: MenuPopover(
            model: model,
            openDashboard: { [weak self] in self?.openDashboard() },
            quit: { NSApp.terminate(nil) },
            close: { [weak self] in self?.closePopover() }))
        host.sizingOptions = .preferredContentSize
        popover.contentViewController = host
        // We close it ourselves (see closePopover): a transient popover vanishes without animating.
        popover.behavior = .applicationDefined
        popover.animates = true

        wireModel()
        wireNotifier()

        presence.onChange = { [weak self] active in
            guard let self else { return }
            self.model.macActive = active
            if !self.live {
                self.model.nowPlaying = nil
                // Locked / asleep: nothing should linger on screen, and the phone stops sending.
                self.notifier.clearAll()
                self.callPanel.dismiss()
            }
            self.sendPresence()
            self.updateIcon()
        }
        model.macActive = presence.active
        model.paused = paused

        clipboard.onLocalText = { [weak self] text in
            guard let self, self.model.connected, self.live else { return }
            self.server.send(Message(["t": "clip", "text": text]))
            self.model.log(Activity(kind: .clipboard, title: "Clipboard → phone", detail: Self.snippet(text)))
        }
        clipboard.start()

        receiver.onComplete = { [weak self] url, mime, toClipboard in
            DispatchQueue.main.async { self?.received(url, mime: mime, toClipboard: toClipboard) }
        }

        server.onConnected = { [weak self] name in
            DispatchQueue.main.async {
                guard let self else { return }
                self.model.phoneName = name
                self.model.pairedDevice = name
                self.model.connectedSince = Date()
                self.model.phonePaused = false
                withAnimation {
                    self.model.reconnecting = false
                    self.model.reconnectFailed = false
                }
                self.model.showPairing = false
                self.callPanel.phoneName = name
                self.sendPresence()
                self.updateIcon()
            }
        }
        server.onDisconnected = { [weak self] in
            self?.receiver.abortAll()
            DispatchQueue.main.async {
                guard let self else { return }
                self.model.phoneName = nil
                self.model.connectedSince = nil
                self.model.nowPlaying = nil
                self.callPanel.dismiss()
                self.notifier.clearAll()
                self.updateIcon()
            }
        }
        server.onMessage = { [weak self] m in self?.handle(m) }
        server.start()
        updateIcon()

        if PairingStore.shared.pairedDevice == nil { openDashboard() }
    }

    private func wireModel() {
        model.clipboardSync = clipboard.enabled
        model.openAtLogin = SMAppService.mainApp.status == .enabled
        model.pairingURI = PairingStore.shared.pairingURI()
        model.sendFiles = { [weak self] urls in self?.send(files: urls) }
        model.chooseFiles = { [weak self] in self?.chooseFiles() }
        model.sendClipboard = { [weak self] in self?.sendClipboard() }
        model.setClipboardSync = { [weak self] on in
            self?.clipboard.enabled = on
            self?.model.clipboardSync = on
        }
        model.setOpenAtLogin = { [weak self] on in self?.setOpenAtLogin(on) }
        model.resetPairing = { [weak self] in self?.resetPairing() }
        model.setPaused = { [weak self] p in self?.setPaused(p) }
        model.mediaControl = { [weak self] action, value in
            guard let self, let key = self.model.nowPlaying?.key else { return }
            self.mediaAction(key: key, action, value: value)
        }
        model.reconnect = { [weak self] in self?.reconnect() }
        model.saveFolder = SaveFolder.displayPath
        model.saveFolderIsDefault = SaveFolder.isDefault
        model.chooseSaveFolder = { [weak self] in self?.chooseSaveFolder() }
        model.resetSaveFolder = { [weak self] in
            SaveFolder.url = SaveFolder.defaultURL
            self?.refreshSaveFolder()
        }
        model.revealSaveFolder = { NSWorkspace.shared.open(SaveFolder.ensure()) }
        model.openNotificationSettings = {
            let id = Bundle.main.bundleIdentifier ?? "dev.piyal.linktwo"
            if let url = URL(string: "x-apple.systempreferences:com.apple.Notifications-Settings.extension?id=\(id)") {
                NSWorkspace.shared.open(url)
            }
        }
    }

    private func wireNotifier() {
        notifier.setup()
        notifier.onReply = { [weak self] key, text in
            self?.server.send(Message(["t": "notifReply", "key": key, "text": text]))
            self?.model.log(Activity(kind: .notification, title: "Replied", detail: text))
        }
        notifier.onClearOnPhone = { [weak self] key in self?.server.send(Message(["t": "notifDismiss", "key": key])) }
        notifier.onReveal = { url in NSWorkspace.shared.activateFileViewerSelecting([url]) }
        notifier.onOpen = { [weak self] key in self?.showInActivity(key: key) }
        notifier.onMedia = { [weak self] key, action in self?.mediaAction(key: key, action) }
        notifier.onOpenMedia = { [weak self] in
            self?.model.page = .overview
            self?.openDashboard()
        }
        callPanel.onAction = { [weak self] action in self?.server.send(Message(["t": "callAction", "action": action])) }
    }

    private func sendPresence() {
        let reason = paused ? "paused" : (presence.active ? "" : "locked")
        server.send(Message(["t": "presence", "active": live, "reason": reason]))
    }

    // MARK: Messages (called on the server queue)

    private func handle(_ m: Message) {
        switch m.type {
        case "fileStart", "fileChunk", "fileEnd", "fileAbort":
            receiver.handle(m)
        case "media":
            DispatchQueue.main.async { self.media(m) }
        case "mediaRemoved":
            DispatchQueue.main.async {
                let key = m.string("key")
                self.notifier.removeMedia(key: key)
                self.artCache[key] = nil
                if self.model.nowPlaying?.key == key { self.model.nowPlaying = nil }
            }
        case "notif":
            DispatchQueue.main.async { self.showNotification(m) }
        case "notifRemoved":
            DispatchQueue.main.async { self.notifier.removePhone(key: m.string("key")) }
        case "call":
            DispatchQueue.main.async { self.call(m) }
        case "bye":
            // The phone paused itself; it is about to disconnect.
            DispatchQueue.main.async { self.model.phonePaused = m.string("reason") == "paused" }
        case "clip":
            DispatchQueue.main.async {
                guard self.live else { return }
                let text = m.string("text")
                self.clipboard.setFromPhone(text: text)
                self.model.log(Activity(kind: .clipboard, title: "Clipboard from phone", detail: Self.snippet(text)))
            }
        default:
            break
        }
    }

    private func showNotification(_ m: Message) {
        guard live, isFresh(m) else { return }
        let pkg = m.string("pkg")
        // Payload = app icon (iconLen bytes, first time per app) + the notification's picture (imageLen bytes).
        let iconLen = min(m.int("iconLen"), m.payload.count)
        let imageLen = min(m.int("imageLen"), m.payload.count - iconLen)
        if iconLen > 0 { iconCache[pkg] = m.payload.prefix(iconLen) }
        let image = imageLen > 0 ? m.payload.subdata(in: iconLen..<(iconLen + imageLen)) : nil
        let png = iconCache[pkg]
        let app = m.string("app"), title = m.string("title"), text = m.string("text")
        notifier.showPhone(key: m.string("key"), pkg: pkg, app: app, title: title, body: text, icon: png, image: image,
                           canReply: m.bool("canReply"))
        model.log(Activity(kind: .notification, title: title.isEmpty ? app : title, detail: text, app: app,
                           icon: png.flatMap(NSImage.init(data:)), key: m.string("key"),
                           image: image.flatMap(NSImage.init(data:))))
    }

    /// Now Playing from a phone media player. Only a new track shows a (quiet) banner.
    private func media(_ m: Message) {
        guard live else { return }
        let key = m.string("key")
        if !m.payload.isEmpty { artCache[key] = m.payload }
        let art = artCache[key]
        let sent = (m.header["time"] as? NSNumber).map { Date(timeIntervalSince1970: $0.doubleValue / 1000) } ?? Date()
        var np = NowPlaying(key: key, app: m.string("app"), title: m.string("title"), artist: m.string("artist"),
                            playing: m.bool("playing"), art: art.flatMap(NSImage.init(data:)))
        np.duration = Double(m.int("duration")) / 1000
        np.position = Double(m.int("position")) / 1000
        // The phone stamped the position when it sent it; clocks are network-synced, never trust the future.
        np.positionDate = min(sent, Date())
        np.rate = (m.header["speed"] as? NSNumber)?.doubleValue ?? 1
        if np.rate == 0 && np.playing { np.rate = 1 }
        np.canSeek = m.bool("canSeek")
        np.volume = m.int("volume")
        np.maxVolume = m.int("maxVolume")
        withAnimation { model.nowPlaying = np }
        if m.bool("banner") {
            notifier.showMedia(key: key, app: np.app, title: np.title, artist: np.artist, art: art)
            model.log(Activity(kind: .media, title: np.title, detail: np.artist, app: np.app, key: key, image: np.art))
        }
    }

    private func mediaAction(key: String, _ action: String, value: Double? = nil) {
        guard live else { return }
        var cmd = action
        var msg: [String: Any] = ["t": "mediaAction", "key": key]
        let current = model.nowPlaying?.key == key ? model.nowPlaying : nil
        switch action {
        case "playpause":
            let playing = current?.playing ?? true
            cmd = playing ? "pause" : "play"
            // Optimistic; the phone's next state update confirms or corrects it.
            if var np = current {
                np.position = np.elapsed(at: Date())
                np.positionDate = Date()
                np.playing = !playing
                model.nowPlaying = np
            }
        case "seek":
            let seconds = value ?? 0
            msg["value"] = Int((seconds * 1000).rounded())
            if var np = current {
                np.position = seconds
                np.positionDate = Date()
                model.nowPlaying = np
            }
        case "volume":
            msg["value"] = Int((value ?? 0).rounded())
            model.nowPlaying?.volume = Int((value ?? 0).rounded())
        default:
            break
        }
        msg["action"] = cmd
        server.send(Message(msg))
    }

    private func call(_ m: Message) {
        let state = m.string("state")
        guard live || state != "ringing" else { return }
        callPanel.update(state: state, name: m.string("name"), number: m.string("number"))
        if state == "ringing" {
            model.log(Activity(kind: .call, title: m.string("name"), detail: "Incoming call " + m.string("number")))
        }
    }

    /// The phone stamps messages with its clock; both clocks are network-synced, so this is a good "is it live" test.
    private func isFresh(_ m: Message) -> Bool {
        guard let ms = m.header["time"] as? NSNumber else { return true }
        return Date().timeIntervalSince1970 - ms.doubleValue / 1000 < Self.maxAge
    }

    private func received(_ url: URL, mime: String, toClipboard: Bool) {
        if toClipboard && mime.hasPrefix("image/") { clipboard.setFromPhone(imageAt: url) }
        let folder = url.deletingLastPathComponent().lastPathComponent
        let detail = toClipboard ? "Copied to clipboard · saved to \(folder)" : "Saved to \(folder)"
        model.log(Activity(kind: .fileIn, title: url.lastPathComponent, detail: detail))
        guard presence.active else { return }
        notifier.showFile(url, detail: detail)
    }

    private static func snippet(_ s: String) -> String {
        let one = s.replacingOccurrences(of: "\n", with: " ")
        return one.count > 90 ? String(one.prefix(90)) + "…" : one
    }

    // MARK: Status item

    private func updateIcon() {
        let symbol: String
        if paused { symbol = "pause.circle" } else if !model.connected { symbol = "iphone.slash" } else if !model.macActive { symbol = "iphone.badge.exclamationmark" } else { symbol = "iphone" }
        let img = NSImage(systemSymbolName: symbol, accessibilityDescription: "LinkTwo")
            ?? NSImage(systemSymbolName: "iphone", accessibilityDescription: "LinkTwo")
        img?.isTemplate = true
        statusItem.button?.image = img
        statusItem.button?.toolTip = model.connected ? "LinkTwo — \(model.phoneName!) linked" : "LinkTwo — phone away"
    }

    @objc private func togglePopover() {
        if popover.isShown && !closing {
            closePopover()
        } else {
            showPopover()
        }
    }

    private func showPopover() {
        guard let button = statusItem.button else { return }
        refreshSaveFolder()
        // Reopened while fading out: cancel the pending close and bring it straight back.
        closeGeneration += 1
        closing = false
        if let window = popover.contentViewController?.view.window { window.alphaValue = 1 }
        // Opening uses NSPopover's own animation only (content animations on top made it flash).
        if !popover.isShown { popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY) }
        if let window = popover.contentViewController?.view.window {
            window.makeKey()
            window.makeFirstResponder(nil) // no control starts focused (no focus ring on the gear)
        }

        removeClickMonitors()
        // Close on any click outside: in other apps (global) or in our other windows (local).
        clickMonitors.append(NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] _ in
            self?.closePopover()
        } as Any)
        clickMonitors.append(NSEvent.addLocalMonitorForEvents(matching: [.keyDown]) { [weak self] event in
            guard event.keyCode == 53 else { return event } // Esc
            self?.closePopover()
            return nil
        } as Any)
        clickMonitors.append(NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] event in
            guard let self else { return event }
            let inside = event.window === self.popover.contentViewController?.view.window
                || event.window === self.statusItem.button?.window   // the icon toggles by itself
            if !inside { self.closePopover() }
            return event
        } as Any)
    }

    /// Fades the whole popover window out with Core Animation (composited, so no dropped frames),
    /// then removes it without a second animation.
    private func closePopover() {
        removeClickMonitors()
        guard popover.isShown, !closing else { return }
        guard let window = popover.contentViewController?.view.window else {
            popover.performClose(nil)
            return
        }
        closing = true
        closeGeneration += 1
        let generation = closeGeneration
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.2
            ctx.timingFunction = CAMediaTimingFunction(controlPoints: 0.4, 0, 0.2, 1)
            ctx.allowsImplicitAnimation = true
            window.animator().alphaValue = 0
        }, completionHandler: { [weak self] in
            guard let self, generation == self.closeGeneration else { return } // reopened meanwhile
            self.popover.animates = false
            self.popover.performClose(nil)
            self.popover.animates = true
            window.alphaValue = 1
            self.closing = false
        })
    }

    private func removeClickMonitors() {
        clickMonitors.forEach(NSEvent.removeMonitor)
        clickMonitors.removeAll()
    }

    // MARK: Actions

    @objc private func openDashboard() {
        closePopover()
        if dashboard == nil { dashboard = DashboardWindowController(model: model) }
        model.openAtLogin = SMAppService.mainApp.status == .enabled
        refreshSaveFolder()
        notifier.refreshAllowed()
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { self.model.notificationsAllowed = self.notifier.allowed }
        NSApp.activate(ignoringOtherApps: true)
        dashboard?.showWindow(nil)
        dashboard?.window?.makeKeyAndOrderFront(nil)
    }

    /// Opens the dashboard on Activity → Notifications, scrolled to the clicked notification.
    private func showInActivity(key: String) {
        model.page = .activity
        model.activityFilter = .notifications
        model.highlightKey = key
        openDashboard()
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in
            if self?.model.highlightKey == key { self?.model.highlightKey = nil }
        }
    }

    @objc private func chooseFiles() {
        closePopover()
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        panel.prompt = "Send"
        NSApp.activate(ignoringOtherApps: true)
        guard panel.runModal() == .OK else { return }
        send(files: panel.urls)
    }

    private func send(files urls: [URL]) {
        guard live else { return }
        for url in urls {
            FileSender.send(url, via: server) { [weak self] ok in
                DispatchQueue.main.async {
                    self?.model.log(Activity(kind: .fileOut, title: url.lastPathComponent,
                                             detail: ok ? "Sent to phone · Download/LinkTwo" : "Failed — phone disconnected"))
                }
            }
        }
    }

    @objc private func sendClipboard() {
        guard live else { return }
        if let text = clipboard.currentText() {
            server.send(Message(["t": "clip", "text": text]))
            model.log(Activity(kind: .clipboard, title: "Clipboard → phone", detail: Self.snippet(text)))
        } else if let png = clipboard.currentImagePNG() {
            let url = FileManager.default.temporaryDirectory.appendingPathComponent("clipboard-\(Int(Date().timeIntervalSince1970)).png")
            guard (try? png.write(to: url)) != nil else { return }
            FileSender.send(url, via: server) { _ in try? FileManager.default.removeItem(at: url) }
            model.log(Activity(kind: .fileOut, title: "Clipboard image", detail: "Sent to phone"))
        }
    }

    @objc private func togglePause() {
        setPaused(!paused)
    }

    private func setPaused(_ p: Bool) {
        paused = p
        UserDefaults.standard.set(p, forKey: "paused")
        model.paused = p
        if p {
            model.nowPlaying = nil
            notifier.clearAll()
            callPanel.dismiss()
        }
        sendPresence()
        updateIcon()
    }

    /// Drops the current connection and re-announces this Mac, so the phone reconnects from scratch.
    /// The Mac can't dial the phone, so this drops the connection and re-announces itself; the phone notices
    /// and reconnects within a few seconds. The UI shows "Reconnecting…" until it's back or we give up.
    @objc private func reconnect() {
        guard !model.reconnecting else { return }
        model.phonePaused = false
        model.reconnectFailed = false
        withAnimation { model.reconnecting = true }
        server.restart()
        reconnectGeneration += 1
        let generation = reconnectGeneration
        DispatchQueue.main.asyncAfter(deadline: .now() + 25) { [weak self] in
            guard let self, generation == self.reconnectGeneration, self.model.reconnecting else { return }
            withAnimation {
                self.model.reconnecting = false
                self.model.reconnectFailed = true
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in
                withAnimation { self?.model.reconnectFailed = false }
            }
        }
    }

    private func chooseSaveFolder() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = "Choose"
        panel.message = "Choose where files sent from your phone are saved."
        panel.directoryURL = SaveFolder.ensure()
        NSApp.activate(ignoringOtherApps: true)
        guard panel.runModal() == .OK, let url = panel.url else { return }
        SaveFolder.url = url
        refreshSaveFolder()
    }

    private func refreshSaveFolder() {
        model.saveFolder = SaveFolder.displayPath
        model.saveFolderIsDefault = SaveFolder.isDefault
    }

    private func resetPairing() {
        let alert = NSAlert()
        alert.messageText = "Reset pairing?"
        alert.informativeText = "The current phone is disconnected and has to scan a new code."
        alert.addButton(withTitle: "Reset")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        PairingStore.shared.reset()
        server.disconnectAll()
        model.pairedDevice = nil
        model.pairingURI = PairingStore.shared.pairingURI()
        model.showPairing = true
    }

    private func setOpenAtLogin(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch {
            NSAlert(error: error).runModal()
        }
        model.openAtLogin = SMAppService.mainApp.status == .enabled
    }
}
