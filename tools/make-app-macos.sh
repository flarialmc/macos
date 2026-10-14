#!/bin/bash
# Wraps a published osx-arm64 launcher into Flarial.app (name, bundle id, icon, version) and signs it ad hoc.
#   tools/make-app-macos.sh [out dir]      (default: publish)
# Notarised signing and the DMG come later; an ad hoc signature is enough to run a locally built app.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:-$ROOT/publish}"
PUB="$OUT/osx-arm64"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
"$DOTNET" publish "$ROOT/src/Flarial.Launcher" -c Release -r osx-arm64 -o "$PUB" >/dev/null
VERSION="$(tr -d '[:space:]' < "$ROOT/version.txt")"
APP="$OUT/Flarial.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUB/." "$APP/Contents/MacOS/"
cp "$ROOT/assets/macos/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Flarial</string>
    <key>CFBundleDisplayName</key><string>Flarial</string>
    <key>CFBundleIdentifier</key><string>xyz.flarial.launcher</string>
    <key>CFBundleExecutable</key><string>Flarial.Launcher</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>LSMinimumSystemVersion</key><string>14.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.games</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
codesign --force --deep --sign - "$APP" 2>/dev/null
echo "$APP"
