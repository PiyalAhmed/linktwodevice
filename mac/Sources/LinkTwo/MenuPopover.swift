import AppKit
import SwiftUI

/// What the menu bar icon opens: status, Now Playing and quick actions, in the style of Control Center.
struct MenuPopover: View {
    @ObservedObject var model: AppModel
    /// Wired by AppDelegate; each closes the popover first where it opens another window.
    var openDashboard: () -> Void
    var quit: () -> Void
    var close: () -> Void = {}

    private var status: (String, Color) {
        if model.reconnecting { return ("Reconnecting…", .blue) }
        if model.reconnectFailed { return ("Phone didn't reconnect", .orange) }
        if model.paused { return ("Paused", .orange) }
        if !model.connected { return (model.phonePaused ? "Paused on phone" : "Not connected", .secondary) }
        if !model.macActive { return ("Paused while locked", .orange) }
        return ("Connected", .green)
    }

    private var canSend: Bool { model.connected && model.macActive && !model.paused }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 10) {
                Image(systemName: "smartphone")
                    .font(.system(size: 15, weight: .medium))
                    .foregroundStyle(.white)
                    .frame(width: 30, height: 30)
                    .background(Circle().fill(Color.accentColor))
                VStack(alignment: .leading, spacing: 1) {
                    Text(model.phoneName ?? model.pairedDevice ?? "No phone paired")
                        .font(.headline)
                        .lineLimit(1)
                    HStack(spacing: 5) {
                        StatusDot(color: status.1, busy: model.reconnecting)
                        Text(status.0).font(.caption).foregroundStyle(.secondary)
                    }
                }
                Spacer()
                Button(action: openDashboard) {
                    Image(systemName: "gearshape")
                }
                .buttonStyle(.borderless)
                .help("Open LinkTwo")
            }

            if let np = model.nowPlaying {
                NowPlayingView(np: np) { action, value in model.mediaControl(action, value) }
                    .transition(.asymmetric(insertion: .scale(scale: 0.96).combined(with: .opacity), removal: .opacity))
            }

            Grid(horizontalSpacing: 8, verticalSpacing: 8) {
                GridRow {
                    Tile(symbol: "arrow.up.doc", title: "Send Files", enabled: canSend, action: model.chooseFiles)
                    Tile(symbol: "doc.on.clipboard", title: "Clipboard", enabled: canSend, action: model.sendClipboard)
                }
                GridRow {
                    Tile(symbol: model.paused ? "play.circle" : "pause.circle",
                         title: model.paused ? "Resume" : "Pause",
                         tint: model.paused ? .orange : nil) { model.setPaused(!model.paused) }
                    Tile(symbol: "arrow.clockwise", title: model.reconnecting ? "Reconnecting…" : "Reconnect",
                         enabled: !model.reconnecting, busy: model.reconnecting, action: model.reconnect)
                }
            }

            Divider()
            HStack {
                Button("Open LinkTwo…", action: openDashboard)
                Spacer()
                Button("Quit", action: quit)
            }
            .buttonStyle(.borderless)
        }
        .padding(14)
        .frame(width: 320)
        .animation(.spring(response: 0.4, dampingFraction: 0.85), value: model.nowPlaying?.key)
        .focusEffectDisabled()
    }
}

/// Control Center–style rounded action tile.
private struct Tile: View {
    let symbol: String
    let title: String
    var enabled = true
    var tint: Color? = nil
    /// Show a spinner instead of the symbol (and keep full opacity while disabled).
    var busy = false
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 8) {
                ZStack {
                    if busy {
                        ProgressView().controlSize(.small)
                    } else {
                        Image(systemName: symbol)
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundStyle(tint ?? .primary)
                    }
                }
                .frame(width: 26, height: 26)
                .background(Circle().fill((tint ?? .primary).opacity(0.12)))
                Text(title).font(.callout).lineLimit(1)
                Spacer(minLength: 0)
            }
            .padding(8)
            .frame(maxWidth: .infinity)
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(.quaternary.opacity(hover ? 1 : 0.6)))
            .contentShape(RoundedRectangle(cornerRadius: 10))
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .opacity(enabled || busy ? 1 : 0.45)
        .onHover { hover = $0 }
    }
}
