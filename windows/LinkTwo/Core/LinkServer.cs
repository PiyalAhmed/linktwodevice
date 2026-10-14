using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LinkTwo.Core;

/// <summary>
/// Listens on the LAN (TCP + DNS-SD) and keeps at most one authenticated phone session.
/// Events fire on background threads.
/// </summary>
public sealed class LinkServer
{
    /// <summary>47820; overridable with LINKTWO_PORT (used by the protocol test harness).</summary>
    public static int Port { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("LINKTWO_PORT"), out var p) ? p : 47820;

    private readonly object _gate = new();
    private readonly List<PhoneSession> _sessions = new();
    private PhoneSession? _active;
    private TcpListener? _listener;

    public event Action<string>? Connected;
    public event Action? Disconnected;
    public event Action<Message>? MessageReceived;

    public void Start()
    {
        _ = Task.Run(AcceptLoop);
        DnsSd.Register(PairingStore.Shared.ComputerName, Port, PairingStore.Shared.ComputerId);
    }

    private async Task AcceptLoop()
    {
        while (true)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, Port);
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Start();
                _listener = listener;
                Log.Info($"listening on :{Port}");
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync();
                    Accept(client);
                }
            }
            catch (Exception e)
            {
                Log.Error("listener failed; retrying in 5 s", e);
                try { _listener?.Stop(); } catch { /* ignore */ }
                await Task.Delay(5000);
            }
        }
    }

    private void Accept(TcpClient client)
    {
        var s = new PhoneSession(client);
        lock (_gate) _sessions.Add(s);
        s.Authenticated += () =>
        {
            PhoneSession? old;
            lock (_gate)
            {
                old = _active;
                _active = s;
            }
            if (old != null && old != s) old.Close();
            PairingStore.Shared.MarkPaired(s.DeviceName);
            Log.Info($"phone connected: {s.DeviceName}");
            Connected?.Invoke(s.DeviceName);
        };
        s.MessageReceived += m => MessageReceived?.Invoke(m);
        s.Closed += () =>
        {
            bool wasActive;
            lock (_gate)
            {
                _sessions.Remove(s);
                wasActive = _active == s;
                if (wasActive) _active = null;
            }
            if (wasActive)
            {
                Log.Info("phone disconnected");
                Disconnected?.Invoke();
            }
        };
        s.Start();
    }

    public Task<bool> SendAsync(Message m)
    {
        PhoneSession? a;
        lock (_gate) a = _active;
        return a?.SendAsync(m) ?? Task.FromResult(false);
    }

    public void Send(Message m) => _ = SendAsync(m);

    /// <summary>
    /// Manual Reconnect: ask the phone to redial (it may not notice a dropped socket for a while otherwise),
    /// then drop everything and re-announce.
    /// </summary>
    public async Task RestartAsync()
    {
        await SendAsync(Message.Of("reconnect"));
        await Task.Delay(300);
        DisconnectAll();
        DnsSd.Unregister();
        DnsSd.Register(PairingStore.Shared.ComputerName, Port, PairingStore.Shared.ComputerId);
    }

    public void DisconnectAll()
    {
        List<PhoneSession> all;
        lock (_gate) all = _sessions.ToList();
        foreach (var s in all) s.Close();
    }
}

/// <summary>One TCP connection from the phone: plaintext hello exchange, then AES-GCM frames.</summary>
internal sealed class PhoneSession
{
    private const int MaxFrame = 4 * 1024 * 1024;

    private readonly TcpClient _client;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NetworkStream? _stream;
    private FrameCipher? _send;
    private FrameCipher? _recv;
    private bool _authenticated;
    private volatile bool _closed;
    private DateTime _lastReceived = DateTime.UtcNow;
    private Timer? _ping;

    public string DeviceName { get; private set; } = "Phone";

    public event Action? Authenticated;
    public event Action<Message>? MessageReceived;
    public event Action? Closed;

    public PhoneSession(TcpClient client) => _client = client;

    public void Start()
    {
        _client.NoDelay = true;
        _stream = _client.GetStream();
        _ = Task.Run(ReadLoop);
        _ = Task.Delay(10_000).ContinueWith(_ => { if (!_authenticated) Close(); });
    }

    private async Task ReadLoop()
    {
        try
        {
            while (!_closed)
            {
                var frame = await ReadFrame();
                _lastReceived = DateTime.UtcNow;
                if (_recv == null)
                {
                    await HandleHello(frame);
                    continue;
                }
                var msg = Message.Decode(_recv.Open(frame));
                if (!_authenticated)
                {
                    if (msg.Type != "auth") break;
                    _authenticated = true;
                    await SendAsync(Message.Of("authOk", ("name", PairingStore.Shared.ComputerName)));
                    StartPings();
                    Authenticated?.Invoke();
                    continue;
                }
                switch (msg.Type)
                {
                    case "ping": _ = SendAsync(Message.Of("pong")); break;
                    case "pong": break;
                    default: MessageReceived?.Invoke(msg); break;
                }
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException or CryptographicException or InvalidDataException or SocketException)
        {
            // Normal end of a connection, a wrong key (not our phone), tampering or replay.
        }
        catch (Exception e)
        {
            Log.Error("session error", e);
        }
        finally
        {
            Close();
        }
    }

    private async Task<byte[]> ReadFrame()
    {
        var hdr = await ReadExactly(4);
        var len = (int)BinaryPrimitives.ReadUInt32BigEndian(hdr);
        if (len <= 0 || len > MaxFrame) throw new InvalidDataException($"bad frame length {len}");
        return await ReadExactly(len);
    }

    private async Task<byte[]> ReadExactly(int n)
    {
        var buf = new byte[n];
        await _stream!.ReadExactlyAsync(buf);
        return buf;
    }

    private async Task HandleHello(byte[] frame)
    {
        if (JsonNode.Parse(frame) is not JsonObject obj || obj["t"]?.GetValue<string>() != "hello")
            throw new InvalidDataException("expected hello");
        var phoneNonce = Convert.FromBase64String(obj["nonce"]?.GetValue<string>() ?? "");
        if (phoneNonce.Length != 16) throw new InvalidDataException("bad nonce");
        DeviceName = obj["device"]?.GetValue<string>() ?? "Phone";

        var store = PairingStore.Shared;
        var computerNonce = RandomNumberGenerator.GetBytes(16);
        var reply = new JsonObject
        {
            ["t"] = "hello",
            ["v"] = 1,
            ["nonce"] = Convert.ToBase64String(computerNonce),
            ["macId"] = store.ComputerId, // field name kept for protocol compatibility
        };
        await WriteFrame(Encoding.UTF8.GetBytes(reply.ToJsonString()));

        var (p2c, c2p) = LinkCrypto.SessionKeys(store.Key, phoneNonce, computerNonce);
        _recv = new FrameCipher(p2c);
        _send = new FrameCipher(c2p);
    }

    public async Task<bool> SendAsync(Message m)
    {
        if (_closed || _send == null) return false;
        await _writeLock.WaitAsync();
        try
        {
            // Seal inside the lock so counters go out in order.
            await WriteFrame(_send.Seal(m.Encode()));
            return true;
        }
        catch
        {
            Close();
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteFrame(byte[] body)
    {
        var buf = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)body.Length);
        body.CopyTo(buf, 4);
        await _stream!.WriteAsync(buf);
    }

    /// <summary>The computer pings (not the phone) so a dozing phone isn't forced to wake itself on a timer.</summary>
    private void StartPings()
    {
        _ping = new Timer(_ =>
        {
            if (DateTime.UtcNow - _lastReceived > TimeSpan.FromSeconds(75)) Close();
            else _ = SendAsync(Message.Of("ping"));
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _ping?.Dispose();
        try { _client.Close(); } catch { /* ignore */ }
        Closed?.Invoke();
    }
}
