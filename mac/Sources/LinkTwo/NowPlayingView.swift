import AppKit
import SwiftUI

/// What a phone media player is doing right now.
struct NowPlaying: Equatable {
    let key: String
    let app: String
    var title: String
    var artist: String
    var playing: Bool
    var art: NSImage?
    /// Seconds. 0 = unknown (live streams).
    var duration: Double = 0
    /// Position in seconds at `positionDate`; advances at `rate` while playing.
    var position: Double = 0
    var positionDate = Date()
    var rate: Double = 1
    var canSeek = false
    var volume = 0
    var maxVolume = 0

    func elapsed(at now: Date) -> Double {
        var t = position
        if playing { t += now.timeIntervalSince(positionDate) * max(rate, 0) }
        return duration > 0 ? min(max(t, 0), duration) : max(t, 0)
    }
}

/// Control Center–style player: artwork over a blurred backdrop, live progress with scrubbing,
/// transport controls and phone volume.
struct NowPlayingView: View {
    let np: NowPlaying
    /// (action, value): "previous" | "playpause" | "next" | "seek" (seconds) | "volume" (step).
    let send: (String, Double?) -> Void

    @State private var volume: Double = 0
    @State private var editingVolume = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 14) {
                Artwork(image: np.art, playing: np.playing)
                VStack(alignment: .leading, spacing: 3) {
                    Text(np.title)
                        .font(.headline)
                        .lineLimit(1)
                    if !np.artist.isEmpty {
                        Text(np.artist)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                    HStack(spacing: 6) {
                        EqualizerBars(playing: np.playing)
                        Text(np.app)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    .padding(.top, 2)
                }
                Spacer(minLength: 0)
            }

            if np.duration > 0 {
                Scrubber(np: np) { send("seek", $0) }
            }

            HStack(spacing: 34) {
                TransportButton(symbol: "backward.fill", size: 17, label: "Previous") { send("previous", nil) }
                TransportButton(symbol: np.playing ? "pause.fill" : "play.fill", size: 28,
                                label: np.playing ? "Pause" : "Play") { send("playpause", nil) }
                TransportButton(symbol: "forward.fill", size: 17, label: "Next") { send("next", nil) }
            }
            .frame(maxWidth: .infinity)

            if np.maxVolume > 0 {
                HStack(spacing: 8) {
                    Image(systemName: "speaker.fill").font(.caption).foregroundStyle(.secondary)
                    Slider(value: $volume, in: 0...Double(np.maxVolume)) { editing in
                        editingVolume = editing
                        if !editing { send("volume", volume) }
                    }
                    .controlSize(.small)
                    Image(systemName: "speaker.wave.3.fill").font(.caption).foregroundStyle(.secondary)
                }
                .help("Phone media volume")
            }
        }
        .padding(14)
        .background { Backdrop(image: np.art) }
        .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(.separator, lineWidth: 0.5))
        .animation(.spring(response: 0.45, dampingFraction: 0.8), value: np.playing)
        .animation(.easeInOut(duration: 0.35), value: np.title)
        .onAppear { volume = Double(np.volume) }
        .onChange(of: np.volume) { if !editingVolume { volume = Double(np.volume) } }
    }
}

/// Artwork that settles back slightly while paused, like Music.
private struct Artwork: View {
    let image: NSImage?
    let playing: Bool

    var body: some View {
        Group {
            if let image {
                Image(nsImage: image).resizable().aspectRatio(contentMode: .fill)
            } else {
                ZStack {
                    LinearGradient(colors: [Color(white: 0.55), Color(white: 0.4)], startPoint: .top, endPoint: .bottom)
                    Image(systemName: "music.note").font(.system(size: 26, weight: .medium)).foregroundStyle(.white.opacity(0.9))
                }
            }
        }
        .frame(width: 72, height: 72)
        .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
        .shadow(color: .black.opacity(playing ? 0.35 : 0.15), radius: playing ? 10 : 4, y: playing ? 5 : 2)
        .scaleEffect(playing ? 1 : 0.9)
    }
}

/// The artwork blurred into a soft wash behind the card.
private struct Backdrop: View {
    let image: NSImage?

    var body: some View {
        ZStack {
            Rectangle().fill(.regularMaterial)
            if let image {
                Image(nsImage: image)
                    .resizable()
                    .aspectRatio(contentMode: .fill)
                    .blur(radius: 40, opaque: true)
                    .opacity(0.45)
                    .transition(.opacity)
                Rectangle().fill(.thinMaterial)
            }
        }
    }
}

/// Three bars that dance while playing and rest when paused.
private struct EqualizerBars: View {
    let playing: Bool

    var body: some View {
        TimelineView(.animation(minimumInterval: 1 / 30, paused: !playing)) { ctx in
            let t = ctx.date.timeIntervalSinceReferenceDate
            HStack(alignment: .bottom, spacing: 1.5) {
                ForEach(0..<3) { i in
                    let phase = t * [5.1, 6.7, 4.3][i] + Double(i)
                    let h = playing ? 3 + 7 * (0.5 + 0.5 * sin(phase)) : 3
                    Capsule().frame(width: 2.5, height: h)
                }
            }
            .frame(width: 11, height: 10, alignment: .bottom)
            .foregroundStyle(playing ? Color.accentColor : Color.secondary)
        }
        .accessibilityHidden(true)
    }
}

/// Progress bar that moves in real time; drag (or click) to seek when the player allows it.
private struct Scrubber: View {
    let np: NowPlaying
    let seek: (Double) -> Void

    @State private var dragging: Double?
    @State private var hover = false

    var body: some View {
        TimelineView(.periodic(from: .now, by: 0.5)) { ctx in
            let current = dragging ?? np.elapsed(at: ctx.date)
            let fraction = np.duration > 0 ? current / np.duration : 0
            VStack(spacing: 4) {
                GeometryReader { geo in
                    let w = geo.size.width
                    ZStack(alignment: .leading) {
                        Capsule().fill(.quaternary)
                        Capsule().fill(Color.primary.opacity(0.75))
                            .frame(width: max(0, min(w, w * fraction)))
                        if np.canSeek && (hover || dragging != nil) {
                            Circle().fill(Color.primary)
                                .frame(width: 10, height: 10)
                                .offset(x: max(0, min(w, w * fraction)) - 5)
                                .transition(.scale)
                        }
                    }
                    .frame(height: hover || dragging != nil ? 6 : 4)
                    .frame(maxHeight: .infinity)
                    .contentShape(Rectangle())
                    .gesture(np.canSeek ? DragGesture(minimumDistance: 0)
                        .onChanged { g in dragging = max(0, min(1, g.location.x / w)) * np.duration }
                        .onEnded { _ in
                            if let d = dragging { seek(d) }
                            dragging = nil
                        } : nil)
                }
                .frame(height: 12)
                .onHover { h in withAnimation(.easeOut(duration: 0.15)) { hover = h } }

                HStack {
                    Text(Self.format(current))
                    Spacer()
                    Text("-" + Self.format(max(0, np.duration - current)))
                }
                .font(.caption2.monospacedDigit())
                .foregroundStyle(.secondary)
            }
        }
    }

    static func format(_ s: Double) -> String {
        let t = Int(s.rounded(.down))
        return t >= 3600 ? String(format: "%d:%02d:%02d", t / 3600, t / 60 % 60, t % 60) : String(format: "%d:%02d", t / 60, t % 60)
    }
}

private struct TransportButton: View {
    let symbol: String
    let size: CGFloat
    let label: String
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: size, weight: .semibold))
                .contentTransition(.symbolEffect(.replace))
                .frame(width: size + 18, height: size + 18)
                .background(Circle().fill(Color.primary.opacity(hover ? 0.1 : 0)))
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .help(label)
        .accessibilityLabel(label)
    }
}
