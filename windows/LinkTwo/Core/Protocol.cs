using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LinkTwo.Core;

// Wire format: see docs/PROTOCOL.md. Byte-compatible with the Mac app (Protocol.swift) and the phone (Protocol.kt).

/// <summary>One application message: a JSON header plus an optional binary payload.</summary>
public sealed class Message
{
    public JsonObject Header { get; }
    public byte[] Payload { get; }

    public Message(JsonObject header, byte[]? payload = null)
    {
        Header = header;
        Payload = payload ?? Array.Empty<byte>();
    }

    public static Message Of(string type, params (string Key, object? Value)[] fields) => With(type, null, fields);

    public static Message With(string type, byte[]? payload, params (string Key, object? Value)[] fields)
    {
        var h = new JsonObject { ["t"] = type };
        foreach (var (k, v) in fields)
        {
            h[k] = v switch
            {
                null => null,
                string s => s,
                bool b => b,
                int i => i,
                long l => l,
                double d => d,
                _ => v.ToString(),
            };
        }
        return new Message(h, payload);
    }

    public string Type => Str("t");

    public string Str(string key) =>
        Header[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    public bool Bool(string key) =>
        Header[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public long Long(string key)
    {
        if (Header[key] is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        return 0;
    }

    public double Double(string key) =>
        Header[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : Long(key);

    public byte[] Encode()
    {
        var json = Encoding.UTF8.GetBytes(Header.ToJsonString());
        var buf = new byte[4 + json.Length + Payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)json.Length);
        json.CopyTo(buf, 4);
        Payload.CopyTo(buf, 4 + json.Length);
        return buf;
    }

    public static Message Decode(byte[] raw)
    {
        if (raw.Length < 4) throw new InvalidDataException("short message");
        var h = (int)BinaryPrimitives.ReadUInt32BigEndian(raw);
        if (h < 0 || 4 + h > raw.Length) throw new InvalidDataException("bad header length");
        var obj = JsonNode.Parse(raw.AsSpan(4, h)) as JsonObject ?? throw new InvalidDataException("header is not an object");
        return new Message(obj, raw[(4 + h)..]);
    }
}

public static class LinkCrypto
{
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("linktwo v1");

    /// <summary>HKDF-SHA256(pairingKey, salt = phoneNonce || computerNonce) → (phone→computer key, computer→phone key).</summary>
    public static (byte[] PhoneToComputer, byte[] ComputerToPhone) SessionKeys(byte[] pairingKey, byte[] phoneNonce, byte[] computerNonce)
    {
        var salt = new byte[phoneNonce.Length + computerNonce.Length];
        phoneNonce.CopyTo(salt, 0);
        computerNonce.CopyTo(salt, phoneNonce.Length);
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, pairingKey, 64, salt, Info);
        return (okm[..32], okm[32..]);
    }

    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

/// <summary>
/// AES-256-GCM for one direction of one session. Nonce = 4 zero bytes + 8-byte big-endian counter; the receiver
/// requires the counter to advance by exactly one, which rejects replays and reordering.
/// </summary>
public sealed class FrameCipher : IDisposable
{
    private readonly AesGcm _aes;
    private readonly object _gate = new();
    private ulong _counter;

    public FrameCipher(byte[] key) => _aes = new AesGcm(key, 16);

    private byte[] Nonce()
    {
        var n = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(n.AsSpan(4), _counter);
        return n;
    }

    public byte[] Seal(byte[] plain)
    {
        lock (_gate)
        {
            var n = Nonce();
            _counter++;
            var output = new byte[12 + plain.Length + 16];
            n.CopyTo(output, 0);
            _aes.Encrypt(n, plain, output.AsSpan(12, plain.Length), output.AsSpan(12 + plain.Length, 16));
            return output;
        }
    }

    public byte[] Open(byte[] frame)
    {
        lock (_gate)
        {
            if (frame.Length < 28) throw new InvalidDataException("short frame");
            var n = Nonce();
            if (!frame.AsSpan(0, 12).SequenceEqual(n)) throw new CryptographicException("unexpected nonce");
            _counter++;
            var len = frame.Length - 28;
            var plain = new byte[len];
            _aes.Decrypt(n, frame.AsSpan(12, len), frame.AsSpan(12 + len, 16), plain);
            return plain;
        }
    }

    public void Dispose() => _aes.Dispose();
}
