#!/bin/bash
# Packages the macOS engine (Wine + DXMT in Whisky's Libraries layout) as flarial-engine-<rev>.tar.xz for the flarialmc/macos release
# engine-<rev>, and prints the SHA-256 to pin in src/Flarial.Runtime.Mac/Engine/EngineManager.cs (EngineSha).
#   package-engine-macos.sh <Libraries-*.tgz from WhiskyWine, or an extracted Libraries dir> <rev> <out dir>
# Wine is LGPL-2.1 (source: https://github.com/Gcenx/macOS_Wine_builds, https://gitlab.winehq.org/wine/wine); DXMT is zlib
# (https://github.com/3Shain/dxmt). SOURCES.txt inside the archive records both.
set -euo pipefail
SRC="${1:?Libraries tgz or dir}"; REV="${2:?rev, e.g. wine11.0-dxmt0.80-r1}"; OUT="${3:?out dir}"
WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
if [ -d "$SRC" ]; then cp -R "$SRC" "$WORK/Libraries"; else tar -xzf "$SRC" -C "$WORK"; fi
L="$WORK/Libraries"
[ -x "$L/Wine/bin/wine64" ] && [ -f "$L/DXMT/x64/d3d11.dll" ] || { echo "not a Wine + DXMT Libraries tree" >&2; exit 1; }
rm -rf "$L/DXVK" # not used: DXMT renders
WINEVER="$(arch -x86_64 "$L/Wine/bin/wine64" --version 2>/dev/null || echo unknown)"
cat > "$L/SOURCES.txt" <<TXT
Flarial macOS engine $REV
Wine ($WINEVER), macOS build by Gcenx, LGPL-2.1-or-later.
  Source: https://gitlab.winehq.org/wine/wine and https://github.com/Gcenx/macOS_Wine_builds
DXMT $(plutil -extract dxmtVersion raw "$L/WhiskyWineVersion.plist" 2>/dev/null || echo "?"), zlib license. Source: https://github.com/3Shain/dxmt
Packaged from WhiskyWine (https://github.com/frankea/Whisky) by tools/package-engine-macos.sh in https://github.com/flarialmc/macos
TXT
mkdir -p "$OUT"
tar -C "$WORK" -cf - Libraries | xz -T0 -6 > "$OUT/flarial-engine-$REV.tar.xz"
shasum -a 256 "$OUT/flarial-engine-$REV.tar.xz"
