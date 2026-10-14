# LinkTwo

A lightweight Phone Link–style bridge between a Mac and an Android phone, over your home network only.

- Phone notifications appear on the Mac as LinkTwo's own slips (top-right), with inline reply and "Clear on phone".
  Choose which apps: all, only an allow list, or everything except a block list. Pictures in notifications
  (photos, screenshots, sender avatars) come along.
- Now Playing: YouTube, Spotify and other players show on the Mac with play/pause, next and previous.
- Incoming calls pop up on the Mac with **Answer** / **Decline**. Call audio stays on the phone or your earbuds.
- Clipboard: Mac → phone syncs automatically. Phone → Mac takes one tap (Android 10+ blocks background clipboard
  reading), from wherever you are:
  - selected text → **To Mac** in the text-selection menu (Android places app actions after its own Copy / Share;
    LinkTwo asks to be first among them);
  - after any Copy button → the **Clipboard → Mac** Quick Settings tile, or long-press the LinkTwo icon →
    **Send clipboard** (pinnable; also usable from Modes and Routines). Copied images work too;
  - links, photos, files → Share → your Mac (pinned in the share sheet's top row).
  - or open LinkTwo: the **Phone clipboard** card previews what's copied and has a **Send to Mac** button.
- Files and photos: Share → *Send to Mac* (saved to `~/Downloads/LinkTwo` by default, changeable in the dashboard; a single image is also copied to the Mac clipboard).
  From the Mac: menu bar icon → *Send Files to Phone…* (saved to `Download/LinkTwo`).
- Everything is end-to-end encrypted with a key exchanged by QR code. No cloud, no account.

**Live only.** It only syncs while you're actually using the Mac: when the Mac is locked, asleep, or its
display is off, the phone sends nothing and keeps everything to itself. Nothing is queued or replayed later.

**Pause / Reconnect.** Either side can pause syncing (phone: status card; Mac: menu bar or dashboard); a pause
is remembered across restarts until you resume. *Reconnect* drops the connection and starts a fresh one, for when
something looks stuck.

**Home only.** The phone only connects while it's on Wi-Fi and the paired Mac answers. It needs no location
permission and keeps no always-on service: Android keeps the notification listener alive, and that listener wakes
networking when Wi-Fi connects. Away from home, it makes no connections and shows no permanent notification.

```
mac/       Swift menu-bar app (no Dock icon)
android/   Kotlin app, no AndroidX UI libraries; only Google's QR scanner
docs/      PROTOCOL.md
```

## Mac

```sh
cd mac
./build.sh --install     # builds, copies to /Applications, launches
```

On first launch:
- **Network:** allow incoming connections if the firewall asks, and allow local network access.
- **Pairing:** the dashboard opens with a pairing code.
- **Dashboard:** menu bar icon → *Open LinkTwo…*. *Overview* has status, pause/reconnect, sending files, the save folder and settings; *Activity* lists this session's notifications and other activity (tabs: Notifications / Activities / All).

`build/LinkTwo.app/Contents/MacOS/LinkTwo --render-previews <dir>` writes PNGs of the call banner, dashboard and activity page for design review.

## Android (Galaxy A34, Android 16)

Build: `cd android && ./gradlew assembleDebug` (uses Android Studio's JDK; set `JAVA_HOME` to
`/Applications/Android Studio.app/Contents/jbr/Contents/Home` if needed).

Install over USB (Developer options → USB debugging):

```sh
~/Library/Android/sdk/platform-tools/adb install -r android/app/build/outputs/apk/debug/app-debug.apk
```

Installing with `adb` avoids Android's "restricted settings" block. If you install the APK some other way and the
notification-access switch is greyed out: App info → ⋮ → **Allow restricted settings**.

Then open LinkTwo on the phone, scan the Mac's code, and finish the setup card (notification access, calls,
battery: unrestricted).

Optionally, add the **Clipboard → Mac** tile: pull down Quick Settings → ✎ → drag it in.

## Known limits

- **Phone → Mac clipboard needs a tap.** Android 10+ blocks background apps from reading the clipboard.
- **Some apps can't be replied to.** Reply from the Mac works only for notifications that have an inline reply button on the phone.
- **Rebuilds may re-ask for permissions.** The Mac app is ad-hoc signed, so macOS may re-ask about incoming connections after a rebuild.
- **The pairing key isn't in the Keychain.** It's stored in `~/Library/Application Support/LinkTwo/pairing.json` (mode 0600), because the Keychain would re-prompt after every ad-hoc rebuild.
