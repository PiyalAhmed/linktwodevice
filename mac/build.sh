#!/bin/bash
# Builds build/LinkTwo.app (ad-hoc signed). Usage: ./build.sh [--install]
set -euo pipefail
cd "$(dirname "$0")"

swift build -c release

APP=build/LinkTwo.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/release/LinkTwo "$APP/Contents/MacOS/LinkTwo"
cp Info.plist "$APP/Contents/Info.plist"
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
