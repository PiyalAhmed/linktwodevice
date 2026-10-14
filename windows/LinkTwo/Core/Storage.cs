using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace LinkTwo.Core;

/// <summary>%APPDATA%\LinkTwo — pairing, settings. Logs go to %LOCALAPPDATA%\LinkTwo\logs.</summary>
public static class AppPaths
{
    public static string Roaming { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LinkTwo"));

    public static string Local { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinkTwo"));

    public static string Temp { get; } = Ensure(Path.Combine(Path.GetTempPath(), "LinkTwo"));

    public static string Logs { get; } = Ensure(Path.Combine(Local, "logs"));

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public static class Log
{
    private static readonly object Gate = new();
    private static readonly string File_ = Path.Combine(AppPaths.Logs, "linktwo.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? e = null) => Write("ERROR", e == null ? message : $"{message}: {e}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(File_);
                if (info.Exists && info.Length > 2_000_000) info.MoveTo(File_ + ".1", overwrite: true);
                File.AppendAllText(File_, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}

/// <summary>
/// Computer identity + the shared pairing key. The key is encrypted with DPAPI (only this Windows user can read it).
/// One phone at a time: "Reset pairing" rotates the key, which locks out the old phone.
/// </summary>
public sealed class PairingStore
{
    public static PairingStore Shared { get; } = new();

    private sealed class State
    {
        public string ComputerId { get; set; } = Guid.NewGuid().ToString().ToUpperInvariant();
        public string ProtectedKey { get; set; } = "";
        public string? PairedDevice { get; set; }
    }

    private readonly object _gate = new();
    private readonly string _path = Path.Combine(AppPaths.Roaming, "pairing.json");
    private State _state;
    private byte[] _key;

    private PairingStore()
    {
        try
        {
            _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? new State();
            _key = ProtectedData.Unprotect(Convert.FromBase64String(_state.ProtectedKey), null, DataProtectionScope.CurrentUser);
            if (_key.Length != 32) throw new CryptographicException("bad key length");
        }
        catch
        {
            _state = new State();
            _key = RandomNumberGenerator.GetBytes(32);
            Save();
        }
    }

    public string ComputerId { get { lock (_gate) return _state.ComputerId; } }
    public byte[] Key { get { lock (_gate) return _key; } }
    public string? PairedDevice { get { lock (_gate) return _state.PairedDevice; } }

    /// <summary>The name shown on the phone: the PC's name from Settings → System → About.</summary>
    public string ComputerName => Environment.MachineName;

    public void MarkPaired(string device)
    {
        lock (_gate)
        {
            if (_state.PairedDevice == device) return;
            _state.PairedDevice = device;
            Save();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _key = RandomNumberGenerator.GetBytes(32);
            _state.PairedDevice = null;
            Save();
        }
    }

    /// <summary>Contents of the pairing QR code. <c>os=windows</c> tells the phone to say "PC" instead of "Mac".</summary>
    public string PairingUri()
    {
        var hosts = string.Join(",", NetUtil.LocalIPv4());
        return "linktwo://pair?v=1" +
               $"&id={Uri.EscapeDataString(ComputerId)}" +
               $"&name={Uri.EscapeDataString(ComputerName)}" +
               $"&key={LinkCrypto.Base64Url(Key)}" +
               $"&hosts={Uri.EscapeDataString(hosts)}" +
               $"&port={LinkServer.Port}" +
               "&os=windows";
    }

    private void Save()
    {
        _state.ProtectedKey = Convert.ToBase64String(ProtectedData.Protect(_key, null, DataProtectionScope.CurrentUser));
        File.WriteAllText(_path, JsonSerializer.Serialize(_state));
    }
}

/// <summary>User settings (%APPDATA%\LinkTwo\settings.json).</summary>
public sealed class Settings
{
    public static Settings Shared { get; } = Load();

    public bool ClipboardSync { get; set; } = true;
    public bool Paused { get; set; }
    public string? SaveFolder { get; set; }

    private static string PathOf => System.IO.Path.Combine(AppPaths.Roaming, "settings.json");

    private static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOf)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save()
    {
        try { File.WriteAllText(PathOf, JsonSerializer.Serialize(this)); }
        catch (Exception e) { Log.Error("saving settings", e); }
    }
}

public static class NetUtil
{
    private static readonly string[] VirtualHints = { "virtual", "vethernet", "hyper-v", "vmware", "virtualbox", "bluetooth", "loopback", "tap", "wsl" };

    /// <summary>IPv4 addresses of real Wi-Fi / Ethernet adapters. Only a hint for the phone; DNS-SD is the fallback.</summary>
    public static List<string> LocalIPv4()
    {
        var result = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)) continue;
            var name = (ni.Name + " " + ni.Description).ToLowerInvariant();
            if (VirtualHints.Any(name.Contains)) continue;
            foreach (var a in ni.GetIPProperties().UnicastAddresses)
            {
                if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var s = a.Address.ToString();
                if (!s.StartsWith("169.254.")) result.Add(s);
            }
        }
        return result;
    }
}

/// <summary>Known folders that .NET doesn't expose (Downloads).</summary>
public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out string pszPath);

    public static string Downloads
    {
        get
        {
            try
            {
                if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var path) == 0) return path;
            }
            catch { /* fall through */ }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }
}
