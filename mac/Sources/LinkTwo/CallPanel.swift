import AppKit
import SwiftUI

/// Incoming-call banner in the style of macOS's own call alerts: a translucent rounded panel, top-right,
/// with round Decline / Accept buttons.
final class CallPanel {
    private var panel: NSPanel?
    private var ringTimer: Timer?
    var onAction: ((_ action: String) -> Void)?
    var phoneName = "Phone"

    func update(state: String, name: String, number: String) {
        if state == "ringing" { show(name: name, number: number) } else { dismiss() }
    }

    private func show(name: String, number: String) {
        let view = CallBanner(name: name, number: number, phone: phoneName,
                              onAccept: { [weak self] in self?.act("answer") },
                              onDecline: { [weak self] in self?.act("decline") })
        let p = panel ?? makePanel()
        let host = NSHostingView(rootView: view)
        p.contentView = host
        let size = host.fittingSize
        if let screen = NSScreen.main?.visibleFrame {
            p.setFrame(NSRect(x: screen.maxX - size.width - 12, y: screen.maxY - size.height - 12,
                              width: size.width, height: size.height), display: true)
        }
        p.orderFrontRegardless()
        panel = p
        if ringTimer == nil {
            ring()
            ringTimer = Timer.scheduledTimer(withTimeInterval: 2.5, repeats: true) { [weak self] _ in self?.ring() }
        }
    }

    func dismiss() {
        ringTimer?.invalidate()
        ringTimer = nil
        panel?.orderOut(nil)
    }

    private func act(_ action: String) {
        onAction?(action)
        dismiss()
    }

    private func ring() {
        NSSound(named: "Glass")?.play()
    }

    private func makePanel() -> NSPanel {
        let p = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 360, height: 80),
                        styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = true
        p.level = .statusBar
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        p.hidesOnDeactivate = false
        p.isReleasedWhenClosed = false
        return p
    }
}

struct CallBanner: View {
    let name: String
    let number: String
    let phone: String
    let onAccept: () -> Void
    let onDecline: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            ContactAvatar(name: name, size: 42)
            VStack(alignment: .leading, spacing: 1) {
                Text(name)
                    .font(.headline)
                    .lineLimit(1)
                Text(number.isEmpty || number == name ? "Incoming Call" : number)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                Text(phone)
                    .font(.caption)
                    .foregroundStyle(.tertiary)
                    .lineLimit(1)
            }
            Spacer(minLength: 8)
            RoundCallButton(symbol: "phone.down.fill", color: .red, label: "Decline", action: onDecline)
            RoundCallButton(symbol: "phone.fill", color: .green, label: "Accept", action: onAccept)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 12)
        .frame(width: 372)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 18, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 18, style: .continuous).strokeBorder(.separator, lineWidth: 0.5))
    }
}

/// Grey gradient disc with initials, like Contacts.
struct ContactAvatar: View {
    let name: String
    let size: CGFloat

    private var initials: String {
        let words = name.split(separator: " ").filter { $0.first?.isLetter == true }
        let s = words.prefix(2).compactMap(\.first).map { String($0).uppercased() }.joined()
        return s
    }

    var body: some View {
        ZStack {
            Circle().fill(LinearGradient(colors: [Color(white: 0.66), Color(white: 0.52)], startPoint: .top, endPoint: .bottom))
            if initials.isEmpty {
                Image(systemName: "person.fill").font(.system(size: size * 0.45)).foregroundStyle(.white)
            } else {
                Text(initials).font(.system(size: size * 0.4, weight: .medium, design: .rounded)).foregroundStyle(.white)
            }
        }
        .frame(width: size, height: size)
    }
}

private struct RoundCallButton: View {
    let symbol: String
    let color: Color
    let label: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(.white)
                .frame(width: 36, height: 36)
                .background(Circle().fill(color))
        }
        .buttonStyle(.plain)
        .help(label)
        .accessibilityLabel(label)
    }
}
