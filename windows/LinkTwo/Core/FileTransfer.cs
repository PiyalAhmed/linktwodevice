using Microsoft.Win32;

namespace LinkTwo.Core;

/// <summary>Where files from the phone go. Defaults to Downloads\LinkTwo (mirrors Download/LinkTwo on the phone).</summary>
public static class SaveFolder
{
    public static string DefaultPath => Path.Combine(KnownFolders.Downloads, "LinkTwo");

    /// <summary>Falls back to the default if the chosen folder was deleted or renamed.</summary>
    public static string Path_
    {
        get
        {
            var custom = Settings.Shared.SaveFolder;
            return !string.IsNullOrEmpty(custom) && Directory.Exists(custom) ? custom : DefaultPath;
        }
    }

    public static bool IsDefault => string.Equals(
        System.IO.Path.GetFullPath(Path_).TrimEnd('\\'), System.IO.Path.GetFullPath(DefaultPath).TrimEnd('\\'),
        StringComparison.OrdinalIgnoreCase);

    public static void Set(string? path)
    {
        Settings.Shared.SaveFolder = path == null || string.Equals(path, DefaultPath, StringComparison.OrdinalIgnoreCase) ? null : path;
        Settings.Shared.Save();
    }

    public static string Ensure()
    {
        var p = Path_;
        Directory.CreateDirectory(p);
        return p;
    }
}

/// <summary>Receives files from the phone into <see cref="SaveFolder"/>. Called on the network thread.</summary>
public sealed class FileReceiver
{
    private sealed record Incoming(string Path, FileStream Stream, string Mime, bool ToClipboard);

    private readonly Dictionary<string, Incoming> _files = new();
    private readonly object _gate = new();

    /// <summary>(path, mime, toClipboard)</summary>
    public event Action<string, string, bool>? Completed;

    public void Handle(Message m)
    {
        var id = m.Str("id");
        lock (_gate)
        {
            switch (m.Type)
            {
                case "fileStart":
                    var path = UniquePath(m.Str("name"));
                    _files[id] = new Incoming(path, File.Create(path), m.Str("mime"), m.Bool("toClipboard"));
                    break;
                case "fileChunk":
                    if (_files.TryGetValue(id, out var f)) f.Stream.Write(m.Payload);
                    break;
                case "fileEnd":
                    if (_files.Remove(id, out var done))
                    {
                        done.Stream.Dispose();
                        Completed?.Invoke(done.Path, done.Mime, done.ToClipboard);
                    }
                    break;
                case "fileAbort":
                    Abort(id);
                    break;
            }
        }
    }

    public void AbortAll()
    {
        lock (_gate)
        {
            foreach (var id in _files.Keys.ToList()) Abort(id);
        }
    }

    private void Abort(string id)
    {
        if (!_files.Remove(id, out var f)) return;
        f.Stream.Dispose();
        try { File.Delete(f.Path); } catch { /* ignore */ }
    }

    private static string UniquePath(string name)
    {
        var dir = SaveFolder.Ensure();
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (string.IsNullOrEmpty(safe) || safe.StartsWith('.')) safe = $"file-{DateTimeOffset.Now.ToUnixTimeSeconds()}{safe}";
        var stem = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        var path = Path.Combine(dir, safe);
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{stem} ({n}){ext}");
        return path;
    }
}

public static class FileSender
{
    private const int ChunkSize = 256 * 1024;

    /// <summary>Streams a file to the phone chunk by chunk; each chunk waits for the previous send.</summary>
    public static async Task<bool> SendAsync(string path, LinkServer server, string? name = null)
    {
        var id = Guid.NewGuid().ToString();
        try
        {
            await using var fs = File.OpenRead(path);
            var start = Message.Of("fileStart",
                ("id", id), ("name", name ?? Path.GetFileName(path)), ("size", fs.Length), ("mime", MimeOf(path)));
            if (!await server.SendAsync(start)) return false;
            var buf = new byte[ChunkSize];
            int n;
            while ((n = await fs.ReadAsync(buf)) > 0)
            {
                if (!await server.SendAsync(Message.With("fileChunk", buf[..n], ("id", id)))) return false;
            }
            return await server.SendAsync(Message.Of("fileEnd", ("id", id)));
        }
        catch (Exception e)
        {
            Log.Error($"sending {path}", e);
            server.Send(Message.Of("fileAbort", ("id", id)));
            return false;
        }
    }

    public static string MimeOf(string path)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(Path.GetExtension(path));
            if (key?.GetValue("Content Type") is string ct) return ct;
        }
        catch { /* ignore */ }
        return "application/octet-stream";
    }
}
