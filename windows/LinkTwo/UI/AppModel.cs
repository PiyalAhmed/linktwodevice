using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace LinkTwo.UI;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    protected void Raise(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

public enum ActivityKind { Notification, Call, FileIn, FileOut, Clipboard, Media }

public sealed class ActivityItem
{
    public ActivityKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string App { get; init; } = "";
    public ImageSource? Icon { get; init; }
    public ImageSource? Image { get; init; }
    /// <summary>Phone notification key, so a clicked toast can find its row.</summary>
    public string? Key { get; init; }
    public DateTime Time { get; } = DateTime.Now;

    public string TimeText => Time.ToString("t");
    public bool HasIcon => Icon != null;
    public bool HasImage => Image != null;

    public SymbolRegular Symbol => Kind switch
    {
        ActivityKind.Notification => SymbolRegular.Alert24,
        ActivityKind.Call => SymbolRegular.Call24,
        ActivityKind.FileIn => SymbolRegular.ArrowDownload24,
        ActivityKind.FileOut => SymbolRegular.ArrowUpload24,
        ActivityKind.Clipboard => SymbolRegular.ClipboardPaste24,
        _ => SymbolRegular.MusicNote224,
    };

    public Brush SymbolBackground => new SolidColorBrush(Kind switch
    {
        ActivityKind.Notification => Color.FromRgb(0xE8, 0x48, 0x3C),
        ActivityKind.Call => Color.FromRgb(0x2E, 0xA0, 0x43),
        ActivityKind.FileIn or ActivityKind.FileOut => Color.FromRgb(0x1F, 0x6F, 0xEB),
        ActivityKind.Media => Color.FromRgb(0xD9, 0x3F, 0x87),
        _ => Color.FromRgb(0x6E, 0x76, 0x81),
    });
}

/// <summary>What a phone media player is doing right now.</summary>
public sealed class NowPlayingState : Observable
{
    private string _title = "", _artist = "";
    private bool _playing;
    private ImageSource? _art;
    private double _duration, _position;
    private DateTime _positionAt = DateTime.UtcNow;
    private double _rate = 1;
    private bool _canSeek;
    private int _volume, _maxVolume;

    public required string Key { get; init; }
    public required string App { get; init; }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Artist { get => _artist; set => Set(ref _artist, value); }
    public bool Playing { get => _playing; set => Set(ref _playing, value); }
    public ImageSource? Art { get => _art; set { if (Set(ref _art, value)) Raise(nameof(HasArt)); } }
    public bool HasArt => _art != null;
    /// <summary>Seconds; 0 = unknown (live streams).</summary>
    public double Duration { get => _duration; set { if (Set(ref _duration, value)) Raise(nameof(HasDuration)); } }
    public bool HasDuration => _duration > 0;
    public double Position { get => _position; set => Set(ref _position, value); }
    public DateTime PositionAt { get => _positionAt; set => Set(ref _positionAt, value); }
    public double Rate { get => _rate; set => Set(ref _rate, value); }
    public bool CanSeek { get => _canSeek; set => Set(ref _canSeek, value); }
    public int Volume { get => _volume; set => Set(ref _volume, value); }
    public int MaxVolume { get => _maxVolume; set { if (Set(ref _maxVolume, value)) Raise(nameof(HasVolume)); } }
    public bool HasVolume => _maxVolume > 0;

    public double Elapsed(DateTime utcNow)
    {
        var t = _position;
        if (_playing) t += (utcNow - _positionAt).TotalSeconds * Math.Max(_rate, 0);
        return _duration > 0 ? Math.Clamp(t, 0, _duration) : Math.Max(t, 0);
    }
}

public enum ActivityFilter { Notifications, Activities, All }

/// <summary>UI state shared by the tray flyout and the dashboard. Touched on the UI thread only.</summary>
public sealed class AppModel : Observable
{
    private string? _phoneName;
    private string? _pairedDevice;
    private bool _computerActive = true;
    private bool _paused;
    private bool _phonePaused;
    private bool _reconnecting;
    private bool _reconnectFailed;
    private DateTime? _connectedSince;
    private NowPlayingState? _nowPlaying;
    private bool _clipboardSync = true;
    private bool _startWithWindows;
    private string _saveFolder = "";
    private bool _saveFolderIsDefault = true;
    private bool _notificationsAllowed = true;

    public ObservableCollection<ActivityItem> Activity { get; } = new();

    public string? PhoneName { get => _phoneName; set { if (Set(ref _phoneName, value)) RaiseStatus(); } }
    public string? PairedDevice { get => _pairedDevice; set { if (Set(ref _pairedDevice, value)) RaiseStatus(); } }
    public bool ComputerActive { get => _computerActive; set { if (Set(ref _computerActive, value)) RaiseStatus(); } }
    public bool Paused { get => _paused; set { if (Set(ref _paused, value)) RaiseStatus(); } }
    public bool PhonePaused { get => _phonePaused; set { if (Set(ref _phonePaused, value)) RaiseStatus(); } }
    public bool Reconnecting { get => _reconnecting; set { if (Set(ref _reconnecting, value)) RaiseStatus(); } }
    public bool ReconnectFailed { get => _reconnectFailed; set { if (Set(ref _reconnectFailed, value)) RaiseStatus(); } }
    public DateTime? ConnectedSince { get => _connectedSince; set { if (Set(ref _connectedSince, value)) Raise(nameof(SinceText)); } }
    public NowPlayingState? NowPlaying { get => _nowPlaying; set { if (Set(ref _nowPlaying, value)) Raise(nameof(HasNowPlaying)); } }
    public bool HasNowPlaying => _nowPlaying != null;
    public bool ClipboardSync { get => _clipboardSync; set => Set(ref _clipboardSync, value); }
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
    public string SaveFolder { get => _saveFolder; set => Set(ref _saveFolder, value); }
    public bool SaveFolderIsDefault { get => _saveFolderIsDefault; set => Set(ref _saveFolderIsDefault, value); }
    public bool NotificationsAllowed { get => _notificationsAllowed; set => Set(ref _notificationsAllowed, value); }

    public bool Connected => _phoneName != null;
    public bool CanSend => Connected && _computerActive && !_paused;
    public string PhoneTitle => _phoneName ?? _pairedDevice ?? "No phone paired";
    public string PauseLabel => _paused ? "Resume" : "Pause";
    public SymbolRegular PauseSymbol => _paused ? SymbolRegular.PlayCircle24 : SymbolRegular.PauseCircle24;
    public string SinceText => _connectedSince is { } d && Connected ? $"Since {d:t}" : "";

    public string StatusText
    {
        get
        {
            if (_reconnecting) return "Reconnecting…";
            if (_reconnectFailed) return "Phone didn't reconnect — is it on this Wi-Fi?";
            if (_paused) return "Paused — nothing is synced until you resume";
            if (!Connected) return _phonePaused ? "Paused on your phone" : "Not connected — the phone isn't on this network";
            if (!_computerActive) return "Paused while this PC is locked";
            return "Connected";
        }
    }

    public Brush StatusBrush => new SolidColorBrush(
        _reconnecting ? Color.FromRgb(0x1F, 0x6F, 0xEB)
        : _reconnectFailed || _paused || _phonePaused || (Connected && !_computerActive) ? Color.FromRgb(0xE0, 0x8A, 0x00)
        : Connected ? Color.FromRgb(0x2E, 0xA0, 0x43)
        : Color.FromRgb(0x8A, 0x8A, 0x8A));

    private void RaiseStatus() => Raise(nameof(Connected), nameof(CanSend), nameof(PhoneTitle), nameof(StatusText),
        nameof(StatusBrush), nameof(PauseLabel), nameof(PauseSymbol), nameof(SinceText));

    public void Log(ActivityItem item)
    {
        Activity.Insert(0, item);
        while (Activity.Count > 60) Activity.RemoveAt(Activity.Count - 1);
    }

    public static ImageSource? ImageFrom(byte[]? data)
    {
        if (data == null || data.Length == 0) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(data);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
