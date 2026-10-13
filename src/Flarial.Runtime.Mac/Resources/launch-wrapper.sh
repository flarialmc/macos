#!/bin/bash
# macOS twin of the Linux launch wrapper. Run by `xodus-cli run <game_dir> <this script> <NT path of the exe>` after xodus decrypted the
# encrypted game files into unnamed files and published them as WINE_DLL_FILE_MAP="<fd>:\??\Z:\<path>|...".
# Whisky's Wine has no WINE_DLL_FILE_MAP support, so instead the game folder is mirrored into a private RAM disk: every entry is a symlink
# to the real file except the decrypted ones, which are copied there. Wine then runs the exe from the mirror (its app dir), so the game
# finds its own DLLs and data through the symlinks. The D3D12 stub goes next to the exe: it refuses device creation, so Minecraft stays
# on D3D11 (DXMT -> Metal). The RAM disk is detached when Wine exits. bash 3.2 compatible (the system bash).
# Env: FLARIAL_WINE (wine64), FLARIAL_D3D12_STUB, FLARIAL_EXE_NAME, FLARIAL_GO_FILE, FLARIAL_LAUNCH_LOG, FLARIAL_STAGE_FALLBACK.
ts() { [ -n "${FLARIAL_LAUNCH_LOG:-}" ] && echo "$(date +%FT%T)                   wrapper: $1" >> "$FLARIAL_LAUNCH_LOG"; }
lower() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }
ts "started (xodus decrypt + licence check done)"
exe="${FLARIAL_EXE_NAME:-Minecraft.Windows.exe}"
game="$(pwd -P)"

# ---- decrypted files: fd -> path relative to the game dir
fds=(); rels=(); bytes=0
IFS='|' read -ra entries <<< "${WINE_DLL_FILE_MAP:-}"
for e in "${entries[@]}"; do
  fd="${e%%:*}"; nt="${e#*:}"
  [[ "$fd" =~ ^[0-9]+$ && -n "$nt" ]] || continue
  unix="${nt#\\??\\Z:}"; unix="${unix//\\//}"
  rel="${unix#"$game"/}"
  [ "$rel" != "$unix" ] || { echo "flarial: $unix is outside the game folder $game" >&2; exit 1; }
  fds+=("$fd"); rels+=("$rel")
  bytes=$(( bytes + $(stat -f %z "/dev/fd/$fd") ))
done
[ ${#fds[@]} -gt 0 ] || { echo "flarial: xodus-cli passed no decrypted files" >&2; exit 1; }

# ---- private RAM disk (falls back to a 0700 folder in the launcher's run directory)
dev=""; stage=""
sectors=$(( (bytes + 64 * 1024 * 1024) / 512 ))
if dev=$(/usr/bin/hdiutil attach -nomount "ram://$sectors" 2>/dev/null | tr -d '[:space:]') && [ -n "$dev" ] \
   && /sbin/newfs_hfs -v FlarialStage "$dev" >/dev/null 2>&1 && stage=$(mktemp -d "${TMPDIR:-/tmp}/flarial-stage.XXXXXX") \
   && /sbin/mount -t hfs -o nobrowse,nodev,nosuid "$dev" "$stage" 2>/dev/null; then
  ts "RAM disk $dev mounted"
else
  [ -n "$dev" ] && /usr/bin/hdiutil detach -force "$dev" >/dev/null 2>&1
  dev=""; stage="${FLARIAL_STAGE_FALLBACK:?}/stage.$$"; rm -rf "$stage"; mkdir -p -m 700 "$stage" || exit 1
  ts "no RAM disk, staging in $stage"
fi
chmod 700 "$stage"
cleanup() {
  cd /
  if [ -n "$dev" ]; then /sbin/umount -f "$stage" >/dev/null 2>&1; /usr/bin/hdiutil detach -force "$dev" >/dev/null 2>&1; rmdir "$stage" 2>/dev/null
  else rm -rf "$stage"; fi
}
trap cleanup EXIT
trap 'kill -INT "$wine_pid" 2>/dev/null' INT TERM

# ---- mirror: one symlink per top-level entry; a folder holding a decrypted file becomes a real folder of symlinks
for f in "$game"/* "$game"/.[!.]*; do
  [ -e "$f" ] || [ -L "$f" ] || continue
  ln -s "$f" "$stage/${f##*/}"
done
materialize() { # $1 = folder relative to the game dir
  local d="$1" parent="${1%/*}"
  [ "$parent" != "$d" ] && materialize "$parent"
  if [ -L "$stage/$d" ]; then
    rm "$stage/$d"; mkdir -m 700 "$stage/$d"
    for f in "$game/$d"/* "$game/$d"/.[!.]*; do [ -e "$f" ] || [ -L "$f" ] || continue; ln -s "$f" "$stage/$d/${f##*/}"; done
  fi
}
target=""
for i in "${!fds[@]}"; do
  rel="${rels[$i]}"; dir="${rel%/*}"
  [ "$dir" != "$rel" ] && materialize "$dir"
  rm -f "$stage/$rel"
  ( umask 077; cat < "/dev/fd/${fds[$i]}" > "$stage/$rel" ) || { echo "flarial: cannot stage $rel" >&2; exit 1; }
  if [ "$(lower "${rel##*/}")" = "$(lower "$exe")" ]; then
    target="$stage/$rel"
    # raise the PE stack reserve to 16 MiB (settings/pause screen stack overflow under Wine)
    lfanew=$(od -An -tu4 -j60 -N4 "$target" | tr -d ' ')
    if [ "$(head -c2 "$target")" = "MZ" ] && [ "$(od -An -tx2 -j$((lfanew + 24)) -N2 "$target" | tr -d ' ')" = "020b" ]; then
      off=$((lfanew + 24 + 72))
      cur=$(od -An -tu8 -j"$off" -N8 "$target" | tr -d ' ')
      [ "$cur" -lt 16777216 ] && printf '\000\000\000\001\000\000\000\000' | dd of="$target" bs=1 seek="$off" conv=notrunc 2>/dev/null
    fi
  fi
done
[ -n "$target" ] || { echo "flarial: $exe was not among the decrypted files" >&2; exit 1; }
[ -n "${FLARIAL_D3D12_STUB:-}" ] && { rm -f "$stage/d3d12.dll"; cp "$FLARIAL_D3D12_STUB" "$stage/d3d12.dll"; }
unset WINE_DLL_FILE_MAP
ts "staged ${#fds[@]} decrypted file(s), $((bytes / 1048576)) MiB"

# the launcher may have started xodus before its Xbox session / prefix prep finished: wait (up to ~6 min) for the file that completes the env
if [ -n "${FLARIAL_GO_FILE:-}" ]; then
  for ((i=0; i<18000; i++)); do [ -e "$FLARIAL_GO_FILE" ] && break; sleep 0.02; done
  [ -e "$FLARIAL_GO_FILE" ] || { echo "flarial: launcher never signalled go" >&2; exit 1; }
  # shellcheck disable=SC1090
  . "$FLARIAL_GO_FILE"; rm -f "$FLARIAL_GO_FILE"
  ts "go received"
fi
export HOME="${FLARIAL_REAL_HOME:-$HOME}"
unset FLARIAL_REAL_HOME
cd "$stage" || exit 1
"${FLARIAL_WINE:?}" "Z:${target//\//\\}" &
wine_pid=$!
wait "$wine_pid"; code=$?
# the window may close before every Wine process of the session is gone: keep the staged files until the prefix's wineserver exits
"${FLARIAL_WINE%/*}/wineserver" -w 2>/dev/null
ts "wine exited ($code)"
exit "$code"
