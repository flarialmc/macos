#!/bin/bash
# Signs Flarial.app for distribution: every Mach-O inside with the hardened runtime and a secure timestamp, then the bundle with
# packaging/macos/Flarial.entitlements. Without an identity the app is signed ad hoc (local builds).
#   tools/sign-macos.sh <Flarial.app> ["Developer ID Application: ..." | SHA-1]   (or MACOS_SIGN_IDENTITY)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="${1:?Flarial.app}"; ID="${2:-${MACOS_SIGN_IDENTITY:-}}"
ENT="$ROOT/packaging/macos/Flarial.entitlements"
if [ -z "$ID" ]; then codesign --force --deep --sign - "$APP"; echo "signed ad hoc"; exit 0; fi
# inside-out: nested Mach-O files first (dylibs, helper executables), the main executable and bundle last
while IFS= read -r -d '' f; do
  if file -b "$f" | grep -q "Mach-O"; then
    [ "$f" = "$APP/Contents/MacOS/Flarial.Launcher" ] && continue
    codesign --force --timestamp --options runtime --sign "$ID" "$f"
  fi
done < <(find "$APP/Contents" -type f -print0)
codesign --force --timestamp --options runtime --entitlements "$ENT" --sign "$ID" "$APP"
codesign --verify --strict --deep --verbose=2 "$APP"
