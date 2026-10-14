using System.Diagnostics;
using System.Windows;
using LinkTwo.Core;
using Microsoft.Win32;

namespace LinkTwo.UI;

/// <summary>
/// The Windows counterpart of the Mac app's AppDelegate: wires the server, presence, clipboard, files, toasts and
/// media controls together and owns the windows. All model changes happen on the UI thread.
/// </summary>
public sealed class AppController
{
    /// <summary>Anything older than this when it arrives isn't "live" any more and is dropped.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(90);

    public static AppController Shared { get; private set; } = null!;

    public AppModel Model { get; } = new();

    private readonly LinkServer _server = new();
    private readonly FileReceiver _receiver = new();
    private readonly MediaBridge _media = new();
    private readonly Dictionary<string, (byte[] Png, string Path)> _icons = new();
    private readonly Dictionary<string, byte[]> _art = new();
    private MessageWindow _messages = null!;
    private Presence _presence = null!;
    private ClipboardSync _clipboard = null!;
    private MainWindow? _dashboard;
    private PairingWindow? _pairing;
    private TrayFlyout? _flyout;
    private int _reconnectGeneration;

    private bool Live => _presence.Active && !Settings.Shared.Paused;

    public AppController() => Shared = this;

    private static void OnUi(Action a) => Application.Current.Dispatcher.BeginInvoke(a);

    public void Start()
    {
        _messages = new MessageWindow();
        _presence = new Presence(_messages);
        _clipboard = new ClipboardSync(_messages);

        Model.Paused = Settings.Shared.Paused;
        Model.ClipboardSync = Settings.Shared.ClipboardSync;
        Model.StartWithWindows = StartWithWindows.Enabled;
        Model.PairedDevice = PairingStore.Shared.PairedDevice;
        Model.NotificationsAllowed = Toasts.Allowed;
        RefreshSaveFolder();

        // SystemEvents may fire on their own thread; hop to the UI thread.
        _presence.Changed += active => OnUi(() =>
        {
            Model.ComputerActive = active;
            if (!Live) ClearLiveState();
            SendPresence();
        });

        _clipboard.LocalText += text =>
        {
            if (!Model.Connected || !Live) return;
            _server.Send(Message.Of("clip", ("text", text)));
            Model.Log(new ActivityItem { Kind = ActivityKind.Clipboard, Title = "Clipboard → phone", Detail = Snippet(text) });
        };

        _receiver.Completed += (path, mime, toClipboard) => OnUi(() => Received(path, mime, toClipboard));

        _server.Connected += name => OnUi(() =>
        {
            Model.PhoneName = name;
            Model.PairedDevice = name;
            Model.ConnectedSince = DateTime.Now;
            Model.PhonePaused = false;
            Model.Reconnecting = false;
            Model.ReconnectFailed = false;
            _pairing?.Close();
            SendPresence();
        });
        _server.Disconnected += () =>
        {
            _receiver.AbortAll();
            OnUi(() =>
            {
                Model.PhoneName = null;
                Model.ConnectedSince = null;
                ClearLiveState();
            });
        };
        _server.MessageReceived += Handle;
        _server.Start();

        Toasts.Reply += (key, text) => OnUi(() =>
        {
            _server.Send(Message.Of("notifReply", ("key", key), ("text", text)));
            Model.Log(new ActivityItem { Kind = ActivityKind.Notification, Title = "Replied", Detail = text });
        });
        Toasts.ClearOnPhone += key => _server.Send(Message.Of("notifDismiss", ("key", key)));
        Toasts.Open += key => OnUi(() => ShowDashboard(DashboardPage.Activity, ActivityFilter.Notifications, key));
        Toasts.CallAction += action => _server.Send(Message.Of("callAction", ("action", action)));
        Toasts.MediaAction += (key, action) => OnUi(() => MediaAction(key, action));
        Toasts.OpenMedia += () => OnUi(() => ShowDashboard(DashboardPage.Overview));
        Toasts.RevealFile += path => RevealInExplorer(path);

        _media.Command += cmd => OnUi(() => { if (Model.NowPlaying is { } np) MediaAction(np.Key, cmd); });
        _media.Seek += seconds => OnUi(() => { if (Model.NowPlaying is { } np) MediaAction(np.Key, "seek", seconds); });

        try { _flyout = new TrayFlyout(Model); }
        catch (Exception e) { WindowFailed("the tray panel", e); }
        if (PairingStore.Shared.PairedDevice == null) ShowDashboard(DashboardPage.Overview, pair: true);
    }

    private void SendPresence()
    {
        var reason = Settings.Shared.Paused ? "paused" : _presence.Active ? "" : "locked";
        _server.Send(Message.Of("presence", ("active", Live), ("reason", reason)));
    }

    /// <summary>Locked, asleep, paused or disconnected: nothing should linger on screen.</summary>
    private void ClearLiveState()
    {
        Toasts.ClearAll();
        Model.NowPlaying = null;
        _media.Clear();
    }

    // MARK: Messages (network thread)

    private void Handle(Message m)
    {
        switch (m.Type)
        {
            case "fileStart" or "fileChunk" or "fileEnd" or "fileAbort":
                _receiver.Handle(m);
                break;
            case "notif":
                OnUi(() => ShowNotification(m));
                break;
            case "notifRemoved":
                Toasts.RemovePhone(m.Str("key"));
                break;
            case "call":
                OnUi(() => Call(m));
                break;
            case "clip":
                OnUi(() =>
                {
                    if (!Live) return;
                    var text = m.Str("text");
                    _clipboard.SetFromPhone(text);
                    Model.Log(new ActivityItem { Kind = ActivityKind.Clipboard, Title = "Clipboard from phone", Detail = Snippet(text) });
                });
                break;
            case "media":
                OnUi(() => Media(m));
                break;
            case "mediaRemoved":
                OnUi(() =>
                {
                    var key = m.Str("key");
                    Toasts.RemoveMedia(key);
                    _art.Remove(key);
                    if (Model.NowPlaying?.Key == key)
                    {
                        Model.NowPlaying = null;
                        _media.Clear();
                    }
                });
                break;
            case "bye":
                OnUi(() => Model.PhonePaused = m.Str("reason") == "paused");
                break;
        }
    }

    private bool IsFresh(Message m)
    {
        var ms = m.Long("time");
        return ms == 0 || DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(ms) < MaxAge;
    }

    private void ShowNotification(Message m)
    {
        if (!Live || !IsFresh(m)) return;
        var pkg = m.Str("pkg");
        // Payload = app icon (iconLen bytes, first time per app) + the notification's own picture (imageLen bytes).
        var iconLen = (int)Math.Min(m.Long("iconLen"), m.Payload.Length);
        var imageLen = (int)Math.Min(m.Long("imageLen"), m.Payload.Length - iconLen);
        if (iconLen > 0)
        {
            var png = m.Payload[..iconLen];
            var path = Path.Combine(AppPaths.Temp, "icon-" + SafeName(pkg) + ".png");
            File.WriteAllBytes(path, png);
            _icons[pkg] = (png, path);
        }
        string? imagePath = null;
        byte[]? image = null;
        if (imageLen > 0)
        {
            image = m.Payload[iconLen..(iconLen + imageLen)];
            imagePath = Path.Combine(AppPaths.Temp, $"img-{Guid.NewGuid():N}.jpg");
            File.WriteAllBytes(imagePath, image);
        }
        _icons.TryGetValue(pkg, out var icon);
        var app = m.Str("app");
        var title = m.Str("title");
        var text = m.Str("text");
        Toasts.ShowPhone(m.Str("key"), app, title, text, icon.Path, imagePath, m.Bool("canReply"));
        Model.Log(new ActivityItem
        {
            Kind = ActivityKind.Notification, Title = string.IsNullOrEmpty(title) ? app : title, Detail = text, App = app,
            Icon = AppModel.ImageFrom(icon.Png), Image = AppModel.ImageFrom(image), Key = m.Str("key"),
        });
    }

    private void Call(Message m)
    {
        var state = m.Str("state");
        if (state == "ringing")
        {
            if (!Live) return;
            Toasts.ShowCall(m.Str("name"), m.Str("number"), Model.PhoneTitle);
            Model.Log(new ActivityItem { Kind = ActivityKind.Call, Title = m.Str("name"), Detail = "Incoming call " + m.Str("number") });
        }
        else
        {
            Toasts.DismissCall();
        }
    }

    /// <summary>Now Playing from a phone media player. Only a new track shows a (quiet) toast.</summary>
    private void Media(Message m)
    {
        if (!Live) return;
        var key = m.Str("key");
        if (m.Payload.Length > 0) _art[key] = m.Payload;
        _art.TryGetValue(key, out var art);

        var np = Model.NowPlaying?.Key == key ? Model.NowPlaying : new NowPlayingState { Key = key, App = m.Str("app") };
        var sent = m.Long("time") is var t and > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime : DateTime.UtcNow;
        np.Title = m.Str("title");
        np.Artist = m.Str("artist");
        np.Playing = m.Bool("playing");
        np.Duration = m.Long("duration") / 1000.0;
        np.Position = m.Long("position") / 1000.0;
        np.PositionAt = sent > DateTime.UtcNow ? DateTime.UtcNow : sent; // clocks are synced; never trust the future
        var rate = m.Double("speed");
        np.Rate = rate == 0 && np.Playing ? 1 : rate;
        np.CanSeek = m.Bool("canSeek");
        np.Volume = (int)m.Long("volume");
        np.MaxVolume = (int)m.Long("maxVolume");
        if (m.Payload.Length > 0 || np.Art == null) np.Art = AppModel.ImageFrom(art);
        Model.NowPlaying = np;

        _media.Update(key, np.Title, np.Artist, np.Playing, m.Payload.Length > 0 ? m.Payload : null, np.Duration, np.Elapsed(DateTime.UtcNow), np.CanSeek);

        if (m.Bool("banner"))
        {
            string? artPath = null;
            if (art != null)
            {
                artPath = Path.Combine(AppPaths.Temp, "art-" + SafeName(key) + ".jpg");
                File.WriteAllBytes(artPath, art);
            }
            Toasts.ShowMedia(key, np.App, np.Title, np.Artist, artPath);
            Model.Log(new ActivityItem { Kind = ActivityKind.Media, Title = np.Title, Detail = np.Artist, App = np.App, Key = key, Image = np.Art });
        }
    }

    private void Received(string path, string mime, bool toClipboard)
    {
        if (toClipboard && mime.StartsWith("image/")) _clipboard.SetImageFromFile(path);
        var folder = Path.GetFileName(Path.GetDirectoryName(path));
        var detail = toClipboard ? $"Copied to clipboard · saved to {folder}" : $"Saved to {folder}";
        Model.Log(new ActivityItem { Kind = ActivityKind.FileIn, Title = Path.GetFileName(path), Detail = detail });
        if (Live) Toasts.ShowFile(path, detail);
    }

    // MARK: Actions (UI thread)

    /// <summary>"previous" | "playpause" | "play" | "pause" | "next" | "seek" (seconds) | "volume" (step).</summary>
    public void MediaAction(string key, string action, double? value = null)
    {
        if (!Live) return;
        var np = Model.NowPlaying?.Key == key ? Model.NowPlaying : null;
        var cmd = action;
        object? sendValue = null;
        switch (action)
        {
            case "playpause":
            case "play":
            case "pause":
                var playing = np?.Playing ?? true;
                cmd = action == "playpause" ? (playing ? "pause" : "play") : action;
                if (np != null)
                {
                    // Optimistic; the phone's next update confirms or corrects it.
                    np.Position = np.Elapsed(DateTime.UtcNow);
                    np.PositionAt = DateTime.UtcNow;
                    np.Playing = cmd == "play";
                }
                break;
            case "seek":
                sendValue = (long)Math.Round((value ?? 0) * 1000);
                if (np != null)
                {
                    np.Position = value ?? 0;
                    np.PositionAt = DateTime.UtcNow;
                }
                break;
            case "volume":
                sendValue = (long)Math.Round(value ?? 0);
                if (np != null) np.Volume = (int)Math.Round(value ?? 0);
                break;
        }
        var msg = sendValue == null
            ? Message.Of("mediaAction", ("key", key), ("action", cmd))
            : Message.Of("mediaAction", ("key", key), ("action", cmd), ("value", sendValue));
        _server.Send(msg);
    }

    public void MediaControl(string action, double? value = null)
    {
        if (Model.NowPlaying is { } np) MediaAction(np.Key, action, value);
    }

    public void ChooseFiles()
    {
        _flyout?.HideAnimated();
        var dlg = new OpenFileDialog { Multiselect = true, Title = "Send to phone" };
        if (dlg.ShowDialog() == true) SendFiles(dlg.FileNames);
    }

    public void SendFiles(IEnumerable<string> paths)
    {
        if (!Live || !Model.Connected) return;
        foreach (var path in paths.Where(File.Exists))
        {
            _ = Task.Run(async () =>
            {
                var ok = await FileSender.SendAsync(path, _server);
                OnUi(() => Model.Log(new ActivityItem
                {
                    Kind = ActivityKind.FileOut, Title = Path.GetFileName(path),
                    Detail = ok ? "Sent to phone · Download/LinkTwo" : "Failed — phone disconnected",
                }));
            });
        }
    }

    public void SendClipboard()
    {
        if (!Live || !Model.Connected) return;
        if (_clipboard.CurrentText() is { } text)
        {
            _server.Send(Message.Of("clip", ("text", text)));
            Model.Log(new ActivityItem { Kind = ActivityKind.Clipboard, Title = "Clipboard → phone", Detail = Snippet(text) });
        }
        else if (_clipboard.CurrentImagePng() is { } png)
        {
            var path = Path.Combine(AppPaths.Temp, $"clipboard-{DateTimeOffset.Now.ToUnixTimeSeconds()}.png");
            File.WriteAllBytes(path, png);
            _ = Task.Run(async () =>
            {
                await FileSender.SendAsync(path, _server);
                try { File.Delete(path); } catch { /* ignore */ }
            });
            Model.Log(new ActivityItem { Kind = ActivityKind.FileOut, Title = "Clipboard image", Detail = "Sent to phone" });
        }
    }

    public void SetPaused(bool paused)
    {
        Settings.Shared.Paused = paused;
        Settings.Shared.Save();
        Model.Paused = paused;
        if (paused) ClearLiveState();
        SendPresence();
    }

    /// <summary>
    /// The PC can't dial the phone, so this drops the connection and re-announces itself; the phone is told to
    /// redial right away. The UI shows "Reconnecting…" until it's back or we give up.
    /// </summary>
    public async void Reconnect()
    {
        if (Model.Reconnecting) return;
        Model.PhonePaused = false;
        Model.ReconnectFailed = false;
        Model.Reconnecting = true;
        var generation = ++_reconnectGeneration;
        await _server.RestartAsync();
        await Task.Delay(TimeSpan.FromSeconds(25));
        if (generation != _reconnectGeneration || !Model.Reconnecting) return;
        Model.Reconnecting = false;
        Model.ReconnectFailed = true;
        await Task.Delay(TimeSpan.FromSeconds(5));
        Model.ReconnectFailed = false;
    }

    public void SetClipboardSync(bool on)
    {
        Settings.Shared.ClipboardSync = on;
        Settings.Shared.Save();
        Model.ClipboardSync = on;
    }

    public void SetStartWithWindows(bool on)
    {
        try { StartWithWindows.Enabled = on; }
        catch (Exception e) { Log.Error("start with Windows", e); }
        Model.StartWithWindows = StartWithWindows.Enabled;
    }

    public void ChooseSaveFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose where files from your phone are saved", InitialDirectory = SaveFolder.Ensure() };
        if (dlg.ShowDialog() == true)
        {
            SaveFolder.Set(dlg.FolderName);
            RefreshSaveFolder();
        }
    }

    public void ResetSaveFolder()
    {
        SaveFolder.Set(null);
        RefreshSaveFolder();
    }

    public void OpenSaveFolder() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SaveFolder.Ensure()}\"") { UseShellExecute = true });

    private void RefreshSaveFolder()
    {
        Model.SaveFolder = SaveFolder.Path_;
        Model.SaveFolderIsDefault = SaveFolder.IsDefault;
    }

    public void ResetPairing()
    {
        var ok = System.Windows.MessageBox.Show(
            "The current phone is disconnected and has to scan a new code.", "Reset pairing?",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!ok) return;
        PairingStore.Shared.Reset();
        _server.DisconnectAll();
        Model.PairedDevice = null;
        ShowPairing();
    }

    public void OpenNotificationSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });

    public void OpenLogs() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Logs}\"") { UseShellExecute = true });

    // MARK: Windows

    public void ToggleFlyout()
    {
        Model.NotificationsAllowed = Toasts.Allowed;
        RefreshSaveFolder();
        if (_flyout == null)
        {
            ShowDashboard();
            return;
        }
        _flyout.Toggle();
    }

    public void ShowDashboard(DashboardPage page = DashboardPage.Overview, ActivityFilter? filter = null, string? highlightKey = null, bool pair = false)
    {
        _flyout?.HideAnimated();
        Model.NotificationsAllowed = Toasts.Allowed;
        Model.StartWithWindows = StartWithWindows.Enabled;
        RefreshSaveFolder();
        if (_dashboard == null)
        {
            try { _dashboard = new MainWindow(Model); }
            catch (Exception e)
            {
                WindowFailed("the LinkTwo window", e);
                return;
            }
            _dashboard.Closed += (_, _) => _dashboard = null;
        }
        _dashboard.Navigate(page, filter, highlightKey);
        _dashboard.Show();
        if (_dashboard.WindowState == WindowState.Minimized) _dashboard.WindowState = WindowState.Normal;
        _dashboard.Activate();
        if (pair) ShowPairing();
    }

    public void ShowPairing()
    {
        _flyout?.HideAnimated();
        if (_pairing != null)
        {
            _pairing.Activate();
            return;
        }
        try { _pairing = new PairingWindow(PairingStore.Shared.PairingUri(), PairingStore.Shared.ComputerName); }
        catch (Exception e)
        {
            WindowFailed("the pairing window", e);
            return;
        }
        _pairing.Closed += (_, _) => _pairing = null;
        if (_dashboard?.IsVisible == true) _pairing.Owner = _dashboard;
        _pairing.Show();
        _pairing.Activate();
    }

    /// <summary>A window couldn't be built: log it, tell the user, keep running in the tray.</summary>
    private static void WindowFailed(string what, Exception e)
    {
        Log.Error($"couldn't open {what}", e);
        System.Windows.MessageBox.Show(
            $"LinkTwo couldn't open {what}:\n\n{e.GetBaseException().Message}\n\nLinkTwo keeps running in the system tray. " +
            $"Details are in {AppPaths.Logs}.", "LinkTwo", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public void Quit()
    {
        DnsSd.Unregister();
        Toasts.ClearAll();
        Application.Current.Shutdown();
    }

    private static void RevealInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    private static string Snippet(string s)
    {
        var one = s.Replace("\r", " ").Replace("\n", " ");
        return one.Length > 90 ? one[..90] + "…" : one;
    }

    private static string SafeName(string s) =>
        new(s.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_').Take(80).ToArray());
}
