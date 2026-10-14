#!/bin/bash
# Flarial.app -> Flarial.dmg (drag-to-Applications layout).   tools/make-dmg-macos.sh <Flarial.app> <out.dmg>
set -euo pipefail
APP="${1:?Flarial.app}"; OUT="${2:?out.dmg}"
STAGE="$(mktemp -d)"; trap 'rm -rf "$STAGE"' EXIT
ditto "$APP" "$STAGE/Flarial.app"
ln -s /Applications "$STAGE/Applications"
rm -f "$OUT"
hdiutil create -volname Flarial -srcfolder "$STAGE" -fs HFS+ -format UDZO -imagekey zlib-level=9 -ov "$OUT" >/dev/null
echo "$OUT"
