#!/bin/bash
# Builds build/LinkTwo.app (ad-hoc signed). Usage: ./build.sh [--install]
set -euo pipefail
cd "$(dirname "$0")"

# UNIVERSAL=1 builds one binary for Apple Silicon + Intel (used for releases).
if [[ "${UNIVERSAL:-}" == "1" ]]; then
    swift build -c release --arch arm64 --arch x86_64
    BIN=.build/apple/Products/Release/LinkTwo
else
    swift build -c release
    BIN=.build/release/LinkTwo
fi

APP=build/LinkTwo.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/LinkTwo"
cp Info.plist "$APP/Contents/Info.plist"
# VERSION=1.2.3 stamps the bundle version (used for releases).
if [[ -n "${VERSION:-}" ]]; then
    /usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$APP/Contents/Info.plist"
    /usr/libexec/PlistBuddy -c "Set :CFBundleVersion ${BUILD_NUMBER:-1}" "$APP/Contents/Info.plist"
fi
cp Icon/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
codesign --force --sign - "$APP"
echo "Built $APP"

if [[ "${1:-}" == "--install" ]]; then
    pkill -x LinkTwo 2>/dev/null || true
    rm -rf /Applications/LinkTwo.app
    cp -R "$APP" /Applications/
    # Refresh Launch Services so Finder, the Dock and Notification Center pick up the icon.
    /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f /Applications/LinkTwo.app
    open /Applications/LinkTwo.app
    echo "Installed to /Applications/LinkTwo.app"
fi
