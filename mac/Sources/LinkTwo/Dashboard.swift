import AppKit
import CoreImage
import SwiftUI
import UniformTypeIdentifiers

struct Activity: Identifiable {
    enum Kind { case notification, call, fileIn, fileOut, clipboard, media }

    let id = UUID()
    let kind: Kind
    let title: String
    let detail: String
    var app: String = ""
    var icon: NSImage? = nil
    /// Phone notification key, so a clicked Mac notification can find its row.
    var key: String? = nil
    /// The notification's own picture (photo, screenshot, artwork), shown as a thumbnail.
    var image: NSImage? = nil
    let time = Date()
}

/// UI state shared by the dashboard; updated by AppDelegate on the main thread.
final class AppModel: ObservableObject {
    @Published var phoneName: String?
    @Published var pairedDevice: String? = PairingStore.shared.pairedDevice
    @Published var macActive = true
    @Published var clipboardSync = true
    @Published var openAtLogin = false
    @Published var showPairing = false
    @Published var pairingURI = ""
    @Published var notificationsAllowed = true
    @Published var paused = false
    /// The phone said it paused itself before disconnecting.
    @Published var phonePaused = false
    @Published var saveFolder = ""
    @Published var saveFolderIsDefault = true
    @Published private(set) var activity: [Activity] = []
    @Published var connectedSince: Date?
    @Published var nowPlaying: NowPlaying?
    /// A manual Reconnect is in progress (until the phone is back or it times out).
    @Published var reconnecting = false
    /// Set briefly when a manual Reconnect timed out without the phone coming back.
    @Published var reconnectFailed = false
    @Published var page: DashboardPage? = .overview
    @Published var activityFilter: ActivityFilter = .notifications
    /// Row to scroll to and briefly highlight (set when a Mac notification is clicked).
    @Published var highlightKey: String?

    var connected: Bool { phoneName != nil }

    // Wired up by AppDelegate.
    var sendFiles: ([URL]) -> Void = { _ in }
    var chooseFiles: () -> Void = {}
    var sendClipboard: () -> Void = {}
    var setClipboardSync: (Bool) -> Void = { _ in }
    var setOpenAtLogin: (Bool) -> Void = { _ in }
    var resetPairing: () -> Void = {}
    var openNotificationSettings: () -> Void = {}
    /// Now Playing control: "previous" | "playpause" | "next" | "seek" (seconds) | "volume" (step).
    var mediaControl: (String, Double?) -> Void = { _, _ in }
    var setPaused: (Bool) -> Void = { _ in }
    var reconnect: () -> Void = {}
    var chooseSaveFolder: () -> Void = {}
    var resetSaveFolder: () -> Void = {}
    var revealSaveFolder: () -> Void = {}

    func log(_ a: Activity) {
        activity.insert(a, at: 0)
        if activity.count > 60 { activity.removeLast(activity.count - 60) }
    }

    func clearActivity() { activity.removeAll() }
}

final class DashboardWindowController: NSWindowController {
    convenience init(model: AppModel) {
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 780, height: 640),
                         styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                         backing: .buffered, defer: false)
        w.title = "LinkTwo"
        w.isReleasedWhenClosed = false
        w.minSize = NSSize(width: 680, height: 480)
        w.contentView = NSHostingView(rootView: DashboardView(model: model))
        w.setContentSize(NSSize(width: 780, height: 640))
        w.center()
        self.init(window: w)
    }
}

enum DashboardPage: String, CaseIterable, Identifiable {
    case overview = "Overview"
    case activity = "Activity"

    var id: String { rawValue }
    var symbol: String { self == .overview ? "smartphone" : "clock.arrow.circlepath" }
}

/// System Settings–style window: sidebar on the left, the selected page on the right.
struct DashboardView: View {
    @ObservedObject var model: AppModel
    var body: some View {
        NavigationSplitView {
            List(DashboardPage.allCases, selection: $model.page) { p in
                Label(p.rawValue, systemImage: p.symbol)
                    .badge(p == .activity && !model.activity.isEmpty ? model.activity.count : 0)
                    .tag(p)
            }
            .navigationSplitViewColumnWidth(min: 170, ideal: 190, max: 240)
        } detail: {
            switch model.page ?? .overview {
            case .overview: OverviewPage(model: model)
            case .activity: ActivityPage(model: model)
            }
        }
        .frame(minWidth: 680, minHeight: 480)
        .sheet(isPresented: Binding(get: { model.showPairing || model.pairedDevice == nil },
                                    set: { if !$0 { model.showPairing = false } })) {
            PairingSheet(model: model)
        }
    }
}

struct OverviewPage: View {
    @ObservedObject var model: AppModel

    var body: some View {
        Form {
            Section {
                PhoneHeader(model: model)
                HStack {
                    Button(model.paused ? "Resume Syncing" : "Pause Syncing") { model.setPaused(!model.paused) }
                    Button(action: model.reconnect) {
                        if model.reconnecting {
                            HStack(spacing: 6) {
                                ProgressView().controlSize(.small)
                                Text("Reconnecting…")
                            }
                        } else {
                            Text("Reconnect")
                        }
                    }
                    .disabled(model.reconnecting)
                    .help("Drop the connection and let the phone connect again")
                    Spacer()
                }
            }

            if !model.notificationsAllowed {
                Section {
                    Label {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("Notifications are turned off")
                            Text("Phone notifications can't appear on this Mac until you allow them.")
                                .font(.callout).foregroundStyle(.secondary)
                        }
                    } icon: {
                        Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.yellow)
                    }
                    Button("Open Notification Settings…", action: model.openNotificationSettings)
                }
            }

            if let np = model.nowPlaying {
                Section("Now Playing") {
                    NowPlayingView(np: np) { action, value in model.mediaControl(action, value) }
                        .listRowInsets(EdgeInsets())
                }
            }

            Section("Send to Phone") {
                DropArea(model: model)
                HStack {
                    Button("Choose Files…", action: model.chooseFiles)
                    Button("Send Clipboard", action: model.sendClipboard)
                }
                .disabled(!model.connected || !model.macActive || model.paused)
            }

            Section {
                LabeledContent {
                    HStack(spacing: 8) {
                        Button("Choose…", action: model.chooseSaveFolder)
                        if !model.saveFolderIsDefault {
                            Button("Use Default", action: model.resetSaveFolder)
                        }
                    }
                } label: {
                    Text("Save to")
                    Button {
                        model.revealSaveFolder()
                    } label: {
                        Label(model.saveFolder, systemImage: "folder.fill")
                            .lineLimit(1)
                            .truncationMode(.middle)
                    }
                    .buttonStyle(.link)
                    .help("Show in Finder")
                }
            } header: {
                Text("Received from Phone")
            } footer: {
                Text("Files and photos you send from your phone are saved here.")
                    .font(.footnote).foregroundStyle(.secondary)
            }

            Section {
                Toggle(isOn: Binding(get: { model.clipboardSync }, set: { model.setClipboardSync($0) })) {
                    Text("Sync clipboard to phone")
                    Text("Text you copy on this Mac is ready to paste on your phone.")
                }
            } header: {
                Text("Clipboard")
            }

            Section("General") {
                Toggle("Open at login", isOn: Binding(get: { model.openAtLogin }, set: { model.setOpenAtLogin($0) }))
                LabeledContent("Paired phone") {
                    Text(model.pairedDevice ?? "None")
                }
                HStack {
                    Button("Pair New Phone…") { model.showPairing = true }
                    Spacer()
                    Button("Reset Pairing…", role: .destructive, action: model.resetPairing)
                }
            }
        }
        .formStyle(.grouped)
    }
}

// MARK: Activity page

enum ActivityFilter: String, CaseIterable, Identifiable {
    case notifications = "Notifications"
    case activities = "Activities"
    case all = "All"

    var id: String { rawValue }

    func matches(_ a: Activity) -> Bool {
        switch self {
        case .notifications: a.kind == .notification
        case .activities: a.kind != .notification
        case .all: true
        }
    }
}

struct ActivityPage: View {
    @ObservedObject var model: AppModel
    private var filter: ActivityFilter { model.activityFilter }
    private var items: [Activity] { model.activity.filter(filter.matches) }

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Picker("Show", selection: $model.activityFilter) {
                    ForEach(ActivityFilter.allCases) { f in
                        Text(f.rawValue).tag(f)
                    }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
                Spacer()
                Button("Clear", action: model.clearActivity)
                    .disabled(model.activity.isEmpty)
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 12)

            Divider()

            if items.isEmpty {
                ContentUnavailableView {
                    Label(emptyTitle, systemImage: filter == .notifications ? "bell.slash" : "tray")
                } description: {
                    Text("Things appear here as they happen while you're using this Mac. Nothing is sent while it's locked or asleep.")
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollViewReader { proxy in
                    List(items) { item in
                        ActivityRow(item: item)
                            .id(item.id)
                            .listRowBackground(
                                item.key != nil && item.key == model.highlightKey
                                    ? RoundedRectangle(cornerRadius: 6).fill(Color.accentColor.opacity(0.18)) : nil)
                    }
                    .listStyle(.inset(alternatesRowBackgrounds: true))
                    .onAppear { scrollToHighlight(proxy) }
                    .onChange(of: model.highlightKey) { scrollToHighlight(proxy) }
                }
            }

            Divider()
            Text("Live only — this list is forgotten when LinkTwo quits.")
                .font(.footnote)
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity)
                .padding(.vertical, 8)
        }
        // Keep the tab bar at the top whatever the list contains.
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .navigationTitle("Activity")
    }

    private func scrollToHighlight(_ proxy: ScrollViewProxy) {
        guard let key = model.highlightKey, let row = items.first(where: { $0.key == key }) else { return }
        DispatchQueue.main.async {
            withAnimation { proxy.scrollTo(row.id, anchor: .center) }
        }
    }

    private var emptyTitle: String {
        switch filter {
        case .notifications: "No Notifications"
        case .activities: "No Activity"
        case .all: "Nothing Yet"
        }
    }
}

// MARK: Phone header

private struct PhoneHeader: View {
    @ObservedObject var model: AppModel

    private var status: (String, Color) {
        if model.reconnecting { return ("Reconnecting…", .blue) }
        if model.reconnectFailed { return ("Phone didn't reconnect — is it on this Wi-Fi?", .orange) }
        if model.paused { return ("Paused — nothing is synced until you resume", .orange) }
        if !model.connected {
            return model.phonePaused ? ("Paused on your phone", .orange) : ("Not connected — the phone isn't on this network", .secondary)
        }
        if !model.macActive { return ("Paused while this Mac is locked", .orange) }
        return ("Connected", .green)
    }

    var body: some View {
        HStack(spacing: 14) {
            Image(systemName: "smartphone")
                .font(.system(size: 26, weight: .regular))
                .foregroundStyle(.white)
                .frame(width: 52, height: 52)
                .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(
                    LinearGradient(colors: [Color.accentColor.opacity(0.9), Color.accentColor], startPoint: .top, endPoint: .bottom)))
            VStack(alignment: .leading, spacing: 3) {
                Text(model.phoneName ?? model.pairedDevice ?? "No phone paired")
                    .font(.title2.weight(.semibold))
                HStack(spacing: 6) {
                    StatusDot(color: status.1, busy: model.reconnecting, size: 8)
                    Text(status.0).foregroundStyle(.secondary)
                }
                .font(.callout)
                if let since = model.connectedSince, model.connected {
                    Text("Since \(since.formatted(date: .omitted, time: .shortened))")
                        .font(.caption).foregroundStyle(.tertiary)
                }
            }
            Spacer()
        }
        .padding(.vertical, 4)
    }
}

// MARK: Send

private struct DropArea: View {
    @ObservedObject var model: AppModel
    @State private var targeted = false

    var body: some View {
        VStack(spacing: 6) {
            Image(systemName: "square.and.arrow.down")
                .font(.system(size: 22))
                .foregroundStyle(targeted ? Color.accentColor : .secondary)
            Text(targeted ? "Release to Send" : "Drop Files Here")
                .font(.callout)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 18)
        .background(
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .strokeBorder(targeted ? Color.accentColor : Color.secondary.opacity(0.4),
                              style: StrokeStyle(lineWidth: 1, dash: [4, 3]))
                .background(RoundedRectangle(cornerRadius: 8).fill(targeted ? Color.accentColor.opacity(0.08) : .clear))
        )
        .opacity(model.connected && model.macActive && !model.paused ? 1 : 0.5)
        .onDrop(of: [.fileURL], isTargeted: $targeted) { providers in
            guard model.connected else { return false }
            for p in providers {
                _ = p.loadObject(ofClass: URL.self) { url, _ in
                    if let url { DispatchQueue.main.async { model.sendFiles([url]) } }
                }
            }
            return true
        }
    }
}

// MARK: Activity

private struct ActivityRow: View {
    let item: Activity

    private var symbol: (String, Color) {
        switch item.kind {
        case .notification: ("bell.fill", .red)
        case .call: ("phone.fill", .green)
        case .fileIn: ("arrow.down.doc.fill", .blue)
        case .fileOut: ("arrow.up.doc.fill", .blue)
        case .clipboard: ("doc.on.clipboard.fill", .gray)
        case .media: ("music.note", .pink)
        }
    }

    var body: some View {
        HStack(spacing: 10) {
            Group {
                if let icon = item.icon {
                    Image(nsImage: icon).resizable()
                } else {
                    Image(systemName: symbol.0)
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(.white)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .background(symbol.1)
                }
            }
            .frame(width: 24, height: 24)
            .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))

            VStack(alignment: .leading, spacing: 1) {
                Text(item.title).lineLimit(1)
                if !item.detail.isEmpty {
                    Text(item.detail).font(.callout).foregroundStyle(.secondary).lineLimit(2)
                }
            }
            Spacer()
            if let image = item.image {
                Image(nsImage: image)
                    .resizable()
                    .aspectRatio(contentMode: .fill)
                    .frame(width: 40, height: 40)
                    .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
            }
            VStack(alignment: .trailing, spacing: 1) {
                Text(item.time.formatted(date: .omitted, time: .shortened))
                if !item.app.isEmpty { Text(item.app) }
            }
            .font(.caption)
            .foregroundStyle(.secondary)
        }
    }
}

// MARK: Pairing

enum QRCode {
    static func image(for text: String) -> NSImage? {
        guard let f = CIFilter(name: "CIQRCodeGenerator") else { return nil }
        f.setValue(Data(text.utf8), forKey: "inputMessage")
        f.setValue("M", forKey: "inputCorrectionLevel")
        guard let out = f.outputImage?.transformed(by: CGAffineTransform(scaleX: 10, y: 10)) else { return nil }
        let rep = NSCIImageRep(ciImage: out)
        let img = NSImage(size: rep.size)
        img.addRepresentation(rep)
        return img
    }
}

private struct PairingSheet: View {
    @ObservedObject var model: AppModel

    var body: some View {
        VStack(spacing: 14) {
            Text("Pair Your Phone").font(.title2.weight(.semibold))
            Text("Open LinkTwo on your phone and scan this code. Your phone needs to be on the same Wi-Fi network as this Mac.")
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            if let img = QRCode.image(for: model.pairingURI) {
                Image(nsImage: img).interpolation(.none).resizable()
                    .frame(width: 200, height: 200)
                    .padding(10)
                    .background(Color.white, in: RoundedRectangle(cornerRadius: 8))
            }
            Label("The code contains the encryption key. Don't share it.", systemImage: "lock.fill")
                .font(.footnote)
                .foregroundStyle(.secondary)
            HStack {
                Spacer()
                if model.pairedDevice != nil {
                    Button("Done") { model.showPairing = false }.keyboardShortcut(.defaultAction)
                } else {
                    Text("Waiting for your phone…").foregroundStyle(.secondary)
                    ProgressView().controlSize(.small)
                }
            }
        }
        .padding(24)
        .frame(width: 380)
    }
}

/// Status dot that turns into a small spinner while something is in progress.
struct StatusDot: View {
    let color: Color
    var busy = false
    var size: CGFloat = 7

    var body: some View {
        ZStack {
            if busy {
                ProgressView()
                    .controlSize(.mini)
                    .scaleEffect(size / 10)
                    .frame(width: size + 4, height: size + 4)
            } else {
                Circle().fill(color).frame(width: size, height: size)
            }
        }
        .frame(width: size + 4, height: size + 4)
        .animation(.easeInOut(duration: 0.2), value: busy)
    }
}
