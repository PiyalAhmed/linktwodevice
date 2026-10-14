import AppKit
import UserNotifications

/// Phone notifications as real macOS notifications (banners + Notification Center), with Reply and
/// "Clear on Phone". Live only: everything is removed again when the Mac locks or the phone leaves.
final class Notifier: NSObject, UNUserNotificationCenterDelegate {
    private let center = UNUserNotificationCenter.current()

    var onReply: ((_ key: String, _ text: String) -> Void)?
    var onClearOnPhone: ((_ key: String) -> Void)?
    var onReveal: ((URL) -> Void)?
    /// The notification itself was clicked (not one of its buttons).
    var onOpen: ((_ key: String) -> Void)?
    /// A Now Playing button: "previous", "playpause" or "next".
    var onMedia: ((_ key: String, _ action: String) -> Void)?
    var onOpenMedia: (() -> Void)?

    /// False when notifications for LinkTwo are turned off in System Settings.
    private(set) var allowed = true

    func setup() {
        center.delegate = self
        center.requestAuthorization(options: [.alert, .sound]) { [weak self] _, _ in self?.refreshAllowed() }
        let reply = UNTextInputNotificationAction(identifier: "reply", title: "Reply", options: [],
                                                  textInputButtonTitle: "Send", textInputPlaceholder: "Message")
        let clear = UNNotificationAction(identifier: "clear", title: "Clear on Phone", options: [])
        let reveal = UNNotificationAction(identifier: "reveal", title: "Show in Finder", options: [.foreground])
        let previous = UNNotificationAction(identifier: "previous", title: "Previous", options: [],
                                            icon: UNNotificationActionIcon(systemImageName: "backward.fill"))
        let playPause = UNNotificationAction(identifier: "playpause", title: "Play / Pause", options: [],
                                             icon: UNNotificationActionIcon(systemImageName: "playpause.fill"))
        let next = UNNotificationAction(identifier: "next", title: "Next", options: [],
                                        icon: UNNotificationActionIcon(systemImageName: "forward.fill"))
        center.setNotificationCategories([
            UNNotificationCategory(identifier: "phone.reply", actions: [reply, clear], intentIdentifiers: [], options: []),
            UNNotificationCategory(identifier: "phone", actions: [clear], intentIdentifiers: [], options: []),
            UNNotificationCategory(identifier: "file", actions: [reveal], intentIdentifiers: [], options: []),
            UNNotificationCategory(identifier: "media", actions: [playPause, next, previous], intentIdentifiers: [], options: []),
        ])
    }

    func refreshAllowed() {
        center.getNotificationSettings { [weak self] s in
            DispatchQueue.main.async {
                self?.allowed = s.authorizationStatus == .authorized || s.authorizationStatus == .provisional
            }
        }
    }

    /// `image` is the notification's own picture (photo, screenshot, avatar); it wins over the app icon.
    func showPhone(key: String, pkg: String, app: String, title: String, body: String, icon: Data?, image: Data?, canReply: Bool) {
        let c = UNMutableNotificationContent()
        c.title = title.isEmpty ? app : title
        c.subtitle = title.isEmpty ? "" : app
        c.body = body
        c.sound = .default
        c.categoryIdentifier = canReply ? "phone.reply" : "phone"
        c.threadIdentifier = pkg
        c.userInfo = ["key": key]
        if let image, let att = attachment(image, ext: "jpg") {
            c.attachments = [att]
        } else if let icon, let att = attachment(icon, ext: "png") {
            c.attachments = [att]
        }
        center.add(UNNotificationRequest(identifier: "n:" + key, content: c, trigger: nil))
    }

    /// A new track: a quiet banner with transport buttons. Replaces the previous one from the same player.
    func showMedia(key: String, app: String, title: String, artist: String, art: Data?) {
        let c = UNMutableNotificationContent()
        c.title = title
        c.subtitle = artist
        c.body = "Playing on your phone · \(app)"
        c.categoryIdentifier = "media"
        c.threadIdentifier = "media"
        c.interruptionLevel = .passive
        c.userInfo = ["mediaKey": key]
        if let art, let att = attachment(art, ext: "jpg") { c.attachments = [att] }
        center.add(UNNotificationRequest(identifier: "m:" + key, content: c, trigger: nil))
    }

    func removeMedia(key: String) {
        center.removeDeliveredNotifications(withIdentifiers: ["m:" + key])
    }

    func removePhone(key: String) {
        center.removeDeliveredNotifications(withIdentifiers: ["n:" + key])
    }

    func showFile(_ url: URL, detail: String) {
        let c = UNMutableNotificationContent()
        c.title = url.lastPathComponent
        c.subtitle = "From your phone"
        c.body = detail
        c.categoryIdentifier = "file"
        c.userInfo = ["path": url.path]
        center.add(UNNotificationRequest(identifier: UUID().uuidString, content: c, trigger: nil))
    }

    func clearAll() {
        center.removeAllDeliveredNotifications()
    }

    /// The notification store moves attachment files, so each notification gets its own copy.
    private func attachment(_ data: Data, ext: String) -> UNNotificationAttachment? {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + "." + ext)
        do {
            try data.write(to: url)
            return try UNNotificationAttachment(identifier: "icon", url: url)
        } catch {
            return nil
        }
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list, .sound])
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                withCompletionHandler completionHandler: @escaping () -> Void) {
        let info = response.notification.request.content.userInfo
        DispatchQueue.main.async { [self] in
            if let key = info["key"] as? String {
                switch response.actionIdentifier {
                case "reply":
                    if let r = response as? UNTextInputNotificationResponse, !r.userText.isEmpty { onReply?(key, r.userText) }
                case "clear":
                    onClearOnPhone?(key)
                case UNNotificationDefaultActionIdentifier:
                    onOpen?(key)
                default:
                    break
                }
            } else if let key = info["mediaKey"] as? String {
                switch response.actionIdentifier {
                case "previous", "playpause", "next": onMedia?(key, response.actionIdentifier)
                case UNNotificationDefaultActionIdentifier: onOpenMedia?()
                default: break
                }
            } else if let path = info["path"] as? String {
                onReveal?(URL(fileURLWithPath: path))
            }
        }
        completionHandler()
    }
}
