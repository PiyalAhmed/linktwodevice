# LinkTwo

A lightweight Phone Link–style bridge between an Android phone and a **Mac or Windows PC**, over your home network only.

- **Notifications** from the phone appear on the computer as native notifications, with inline **Reply** and
  **Clear on phone**. Pictures (photos, screenshots, sender avatars) come along. Choose which apps: all, only an
  allow list, or everything except a block list.
- **Calls** pop up on the computer with **Answer** / **Decline**. Call audio stays on the phone or your earbuds.
- **Now Playing**: YouTube, Spotify and other players show on the computer with play/pause, next/previous, seeking
  and phone volume. On Windows they also appear in Windows' own media controls and work with media keys.
- **Clipboard**: computer → phone syncs automatically. Phone → computer takes one tap (Android 10+ blocks background
  clipboard reading), from wherever you are:
  - selected text → **To Mac / To PC** in the text-selection menu;
  - after any Copy button → the **Clipboard → Mac/PC** Quick Settings tile, or long-press the LinkTwo icon →
    **Send clipboard**. Copied images work too;
  - or open LinkTwo: the **Phone clipboard** card previews what's copied and has a **Send** button.
- **Files and photos**: Share → your computer (pinned in the share sheet's top row). Saved to `Downloads/LinkTwo`
  by default (changeable). From the computer: drop files on the dashboard or use *Send files*.
- Everything is end-to-end encrypted with a key exchanged by QR code. No cloud, no account.

**Live only.** It only syncs while you're actually using the computer: when it's locked, asleep, or its display is
off, the phone sends nothing and keeps everything to itself. Nothing is queued or replayed later.

**Pause / Reconnect.** Either side can pause syncing; a pause is remembered until you resume. *Reconnect* drops the
connection and starts a fresh one, for when something looks stuck.

**Home only.** The phone only connects while it's on Wi-Fi and the paired computer answers. It needs no location
permission and keeps no always-on service: Android keeps the notification listener alive, and that listener wakes
networking when Wi-Fi connects. Away from home, it makes no connections and shows no permanent notification.

## Download

Get the latest builds from the [**Releases**](../../releases) page:

| Device | File | First run |
|---|---|---|
| Android 12+ | `LinkTwo-android-*.apk` | Allow installing from this source. In LinkTwo, turn on notification access (greyed out? App info → ⋮ → *Allow restricted settings*). |
| Mac, macOS 14+ (Apple Silicon or Intel) | `LinkTwo-macOS-*.zip` | Unzip and move to Applications. Not notarized: right-click → **Open** the first time. |
| Windows 10 (2004+) / 11, x64 | `LinkTwo-windows-x64-*.exe` | Run it. Not code-signed: SmartScreen → **More info → Run anyway**. Allow it on **private networks** when the firewall asks. |

Then open LinkTwo on the computer (menu bar on a Mac, system tray on Windows), and scan its QR code from the phone app.

## Project layout

```
android/   Kotlin phone app (Material 3)
mac/       Swift menu-bar app (SwiftUI + AppKit)
windows/   .NET 8 WPF tray app (Fluent / WPF-UI)
docs/      PROTOCOL.md — the wire format all three speak
.github/   CI: builds all three on every push; a v* tag publishes a release
```

## Building

### Android

```sh
cd android && ./gradlew assembleDebug
~/Library/Android/sdk/platform-tools/adb install -r app/build/outputs/apk/debug/app-debug.apk
```

Uses Android Studio's JDK (`JAVA_HOME=/Applications/Android Studio.app/Contents/jbr/Contents/Home`). Installing with
`adb` avoids Android's "restricted settings" block on notification access.

### Mac

```sh
cd mac
./build.sh --install     # builds, copies to /Applications, launches
```

`UNIVERSAL=1 ./build.sh` builds for Apple Silicon + Intel. `LinkTwo --render-previews <dir>` writes PNGs of the call
banner, dashboard and activity page for design review.

### Windows

Needs the .NET 8 SDK. It also builds on macOS/Linux (`EnableWindowsTargeting`), though it only runs on Windows.

```sh
cd windows/LinkTwo
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o ../dist      # → windows/dist/LinkTwo.exe
```

Logs: `%LOCALAPPDATA%\LinkTwo\logs` (dashboard → *Open log folder*). Pairing and settings: `%APPDATA%\LinkTwo`
(the pairing key is encrypted with Windows DPAPI).

## Releasing

Push a version tag; GitHub Actions builds all three and publishes a release with the downloads:

```sh
git tag v0.2.0 && git push origin v0.2.0
```

Android release builds are signed with the debug key unless you add these repository secrets, in which case updates
install over each other: `ANDROID_KEYSTORE_BASE64` (base64 of a `.jks`), `ANDROID_KEYSTORE_PASSWORD`,
`ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`.

## Known limits

- **Phone → computer clipboard needs a tap.** Android 10+ blocks background apps from reading the clipboard.
- **Some apps can't be replied to.** Reply works only for notifications that have an inline reply button on the phone.
- **Unsigned builds.** The Mac app is ad-hoc signed and the Windows exe isn't code-signed, so first launch shows a
  warning; macOS may re-ask about incoming connections after an update.
- **Mac pairing key isn't in the Keychain.** It's in `~/Library/Application Support/LinkTwo/pairing.json` (mode 0600),
  because the Keychain would re-prompt after every ad-hoc rebuild.
