using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace LinkTwo.Core;

/// <summary>A hidden window that receives clipboard and display-power notifications.</summary>
public sealed class MessageWindow : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    private readonly HwndSource _source;
    private readonly IntPtr _powerHandle;

    public event Action? ClipboardChanged;
    /// <summary>0 = off, 1 = on, 2 = dimmed.</summary>
    public event Action<int>? DisplayStateChanged;

    public MessageWindow()
    {
        // A real (hidden) top-level window: message-only windows miss some broadcasts.
        _source = new HwndSource(new HwndSourceParameters("LinkTwoMessages") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
        AddClipboardFormatListener(_source.Handle);
        var guid = GUID_CONSOLE_DISPLAY_STATE;
        _powerHandle = RegisterPowerSettingNotification(_source.Handle, ref guid, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            ClipboardChanged?.Invoke();
        }
        else if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_POWERSETTINGCHANGE)
        {
            var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
            if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE) DisplayStateChanged?.Invoke(setting.Data);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(_source.Handle);
        if (_powerHandle != IntPtr.Zero) UnregisterPowerSettingNotification(_powerHandle);
        _source.Dispose();
    }
}

/// <summary>
/// "Am I being used right now?" — false while the session is locked, the PC is asleep, the display is off,
/// or another user's session is in front. The phone only forwards things while this is true.
/// </summary>
public sealed class Presence
{
    private bool _locked, _asleep, _displayOff, _remote;

    public bool Active => !_locked && !_asleep && !_displayOff && !_remote;
    public event Action<bool>? Changed;

    public Presence(MessageWindow window)
    {
        SystemEvents.SessionSwitch += (_, e) => Update(() =>
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock: _locked = true; break;
                case SessionSwitchReason.SessionUnlock: _locked = false; break;
                case SessionSwitchReason.ConsoleDisconnect: _remote = true; break;
                case SessionSwitchReason.ConsoleConnect: _remote = false; break;
            }
        });
        SystemEvents.PowerModeChanged += (_, e) => Update(() =>
        {
            if (e.Mode == PowerModes.Suspend) _asleep = true;
            else if (e.Mode == PowerModes.Resume) _asleep = false;
        });
        window.DisplayStateChanged += state => Update(() => _displayOff = state == 0);
    }

    private void Update(Action change)
    {
        var before = Active;
        change();
        if (Active != before) Changed?.Invoke(Active);
    }
}

/// <summary>Windows clipboard ⇄ phone. Event-driven (WM_CLIPBOARDUPDATE), no polling.</summary>
public sealed class ClipboardSync
{
    /// <summary>Password managers mark secrets with these formats; never send them to the phone.</summary>
    private static readonly string[] SkipFormats =
    {
        "ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore", "CanIncludeInClipboardHistory",
    };

    private DateTime _ignoreUntil = DateTime.MinValue;

    public event Action<string>? LocalText;

    public ClipboardSync(MessageWindow window) => window.ClipboardChanged += OnChanged;

    private void OnChanged()
    {
        if (DateTime.UtcNow < _ignoreUntil || !Settings.Shared.ClipboardSync) return;
        var text = CurrentText();
        if (text != null) LocalText?.Invoke(text);
    }

    public string? CurrentText()
    {
        return Retry(() =>
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return null;
            if (SkipFormats.Any(f => data.GetDataPresent(f))) return null;
            if (!data.GetDataPresent(DataFormats.UnicodeText)) return null;
            var s = data.GetData(DataFormats.UnicodeText) as string;
            return string.IsNullOrEmpty(s) || s.Length > 1_000_000 ? null : s;
        });
    }

    /// <summary>PNG of the current clipboard image, if any.</summary>
    public byte[]? CurrentImagePng()
    {
        return Retry(() =>
        {
            if (!Clipboard.ContainsImage()) return null;
            var img = Clipboard.GetImage();
            if (img == null) return null;
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        });
    }

    public void SetFromPhone(string text)
    {
        _ignoreUntil = DateTime.UtcNow.AddMilliseconds(800);
        Retry(() =>
        {
            Clipboard.SetText(text);
            return true;
        });
    }

    public void SetImageFromFile(string path)
    {
        _ignoreUntil = DateTime.UtcNow.AddMilliseconds(800);
        Retry(() =>
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            Clipboard.SetImage(bmp);
            return true;
        });
    }

    /// <summary>The clipboard can be briefly locked by another app (CLIPBRD_E_CANT_OPEN); try a few times.</summary>
    private static T? Retry<T>(Func<T?> body)
    {
        for (var i = 0; i < 5; i++)
        {
            try { return body(); }
            catch (COMException) { Thread.Sleep(40); }
            catch (ExternalException) { Thread.Sleep(40); }
            catch (Exception e)
            {
                Log.Error("clipboard", e);
                return default;
            }
        }
        return default;
    }
}

/// <summary>Start LinkTwo when the user signs in (HKCU\…\Run).</summary>
public static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "LinkTwo";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(Name, $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}
