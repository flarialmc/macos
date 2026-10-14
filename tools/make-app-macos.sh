#!/bin/bash
# Wraps a published osx-arm64 launcher into Flarial.app (name, bundle id, icon, version) and signs it.
#   tools/make-app-macos.sh [out dir]      (default: publish)
# Signed with MACOS_SIGN_IDENTITY (Developer ID, hardened runtime) when set, otherwise ad hoc (tools/sign-macos.sh).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:-$ROOT/publish}"
PUB="$OUT/osx-arm64"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
"$DOTNET" publish "$ROOT/src/Flarial.Launcher" -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o "$PUB" >/dev/null
VERSION="$(tr -d '[:space:]' < "$ROOT/version.txt")"
APP="$OUT/Flarial.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
# Contents/MacOS may hold only code (or the bundle signature cannot seal it): the single-file launcher and native dylibs. Data goes to Resources.
for f in "$PUB"/*; do
  case "$f" in *.pdb|*.dbg) continue;; esac
  if file -b "$f" | grep -q "Mach-O"; then cp "$f" "$APP/Contents/MacOS/"; else cp -R "$f" "$APP/Contents/Resources/"; fi
done
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
"$ROOT/tools/sign-macos.sh" "$APP" >/dev/null
echo "$APP"
