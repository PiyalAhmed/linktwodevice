import AppKit

/// "Am I being used right now?" — false while the screen is locked, the display is asleep, the Mac is asleep,
/// or another user's session is in front. The phone only forwards things while this is true.
final class MacPresence {
    private(set) var active: Bool
    var onChange: ((Bool) -> Void)?

    private var locked = MacPresence.isLockedNow()
    private var asleep = false
    private var screensAsleep = false
    private var sessionInactive = false
    private var tokens: [NSObjectProtocol] = []

    init() {
        active = !locked
        let dnc = DistributedNotificationCenter.default()
        observe(dnc, "com.apple.screenIsLocked") { $0.locked = true }
        observe(dnc, "com.apple.screenIsUnlocked") { $0.locked = false }

        let ws = NSWorkspace.shared.notificationCenter
        observe(ws, NSWorkspace.willSleepNotification.rawValue) { $0.asleep = true }
        observe(ws, NSWorkspace.didWakeNotification.rawValue) { p in
            p.asleep = false
            p.locked = MacPresence.isLockedNow()
        }
        observe(ws, NSWorkspace.screensDidSleepNotification.rawValue) { $0.screensAsleep = true }
        observe(ws, NSWorkspace.screensDidWakeNotification.rawValue) { $0.screensAsleep = false }
        observe(ws, NSWorkspace.sessionDidResignActiveNotification.rawValue) { $0.sessionInactive = true }
        observe(ws, NSWorkspace.sessionDidBecomeActiveNotification.rawValue) { $0.sessionInactive = false }
    }

    private func observe(_ center: NotificationCenter, _ name: String, _ change: @escaping (MacPresence) -> Void) {
        tokens.append(center.addObserver(forName: Notification.Name(name), object: nil, queue: .main) { [weak self] _ in
            guard let self else { return }
            change(self)
            self.recompute()
        })
    }

    private func recompute() {
        let now = !locked && !asleep && !screensAsleep && !sessionInactive
        guard now != active else { return }
        active = now
        onChange?(now)
    }

    static func isLockedNow() -> Bool {
        guard let d = CGSessionCopyCurrentDictionary() as? [String: Any] else { return false }
        return d["CGSSessionScreenIsLocked"] as? Bool ?? false
    }
}
