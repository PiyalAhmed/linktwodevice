# LinkTwo wire protocol (v1)

Phone (client) ⇄ Mac (server) over TCP port **47820** on the local network. The Mac also advertises
Bonjour service `_linktwo._tcp` with TXT record `id=<macId>`.

## Pairing

The Mac shows a QR code:

```
linktwo://pair?v=1&id=<macId>&name=<mac name>&key=<base64url 32-byte key>&hosts=<ip,ip>&port=47820
```

`key` is the long-term shared secret. "Reset Pairing" on the Mac rotates it, which locks out the old phone.

## Framing

Every frame on the socket: `uint32 big-endian length` + `length` bytes (max 4 MiB).

## Handshake

1. Phone → Mac (plaintext JSON frame): `{"t":"hello","v":1,"nonce":"<b64 16 bytes>","device":"Galaxy A34 5G"}`
2. Mac → Phone (plaintext JSON frame): `{"t":"hello","v":1,"nonce":"<b64 16 bytes>","macId":"…"}`
   The phone aborts if `macId` isn't the paired Mac.
3. Both derive `okm = HKDF-SHA256(ikm = key, salt = phoneNonce ‖ macNonce, info = "linktwo v1", 64 bytes)`.
   `okm[0..32]` encrypts phone→Mac, `okm[32..64]` encrypts Mac→phone.
4. Phone → Mac (encrypted): `{"t":"auth"}`. Mac → Phone (encrypted): `{"t":"authOk","name":"…"}`.
   A failed decryption closes the connection.

## Encrypted frames

`nonce (12) ‖ AES-256-GCM ciphertext ‖ tag (16)`. The nonce is `00 00 00 00` followed by a
big-endian uint64 counter that starts at 0 per direction per session. Receivers require the exact next
counter, which rejects replay and reordering.

Plaintext = `uint32 BE json length` ‖ JSON header ‖ optional binary payload.

## Messages

| `t` | Direction | Fields / payload |
|---|---|---|
| `ping` / `pong` | Mac → phone / reply | Mac pings every 30 s and drops the phone after 75 s of silence; the phone also pings before time-sensitive messages |
| `presence` | Mac → phone | `active` — false while the Mac is locked, asleep, display off, or another user is in front. The phone sends nothing (notifications, calls, clipboard, files) while false. Sent right after `authOk` and on every change. |
| `presence` `reason` | Mac → phone | `"locked"` or `"paused"` (paused from the Mac's menu/dashboard), empty when active |
| `reconnect` | Mac → phone | The Mac's Reconnect button: the phone drops the socket and redials after ~1 s |
| `bye` | phone → Mac | `reason: "paused"` — sent just before the phone disconnects because the user paused it |
| `notif` | phone → Mac | `key, pkg, app, title, text, canReply, time` (post time, ms since epoch; the Mac drops anything older than 90 s), `iconLen, imageLen`; payload = 96 px PNG app icon (`iconLen` bytes, first time per app per session) followed by the notification's own picture as JPEG ≤ 1024 px (`imageLen` bytes: big picture → message image → large icon) |
| `media` | phone → Mac | `key, pkg, app, title, artist, playing, banner, time, position, duration` (ms; position projected to `time`), `speed, canSeek, volume, maxVolume`; payload = artwork JPEG (new track or resend only). Sent on every player change (track, play state, seek, volume) via a MediaController callback; `banner` is true only for a new track |
| `mediaRemoved` | phone → Mac | `key` — the player's notification went away |
| `mediaAction` | Mac → phone | `key, action` (`play` / `pause` / `next` / `previous` / `seek` / `volume`), `value` (ms for seek, volume step), executed through the player's MediaSession |
| `notifRemoved` | phone → Mac | `key` |
| `notifDismiss` | Mac → phone | `key` |
| `notifReply` | Mac → phone | `key, text` |
| `call` | phone → Mac | `state` (`ringing` / `offhook` / `idle`), `name`, `number` |
| `callAction` | Mac → phone | `action` (`answer` / `decline`) |
| `clip` | both | `text` |
| `fileStart` | both | `id, name, size, mime, toClipboard?` |
| `fileChunk` | both | `id`; payload = up to 256 KiB |
| `fileEnd` / `fileAbort` | both | `id` |

## Live-only rule

Nothing is queued or replayed. If the Mac isn't connected and active at the moment something happens on the
phone, it stays on the phone. The one exception is a call that is *still ringing* when the Mac becomes active,
because that is still live.
