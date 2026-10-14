#!/bin/bash
# Builds xodus-cli for macOS arm64 from flarialmc/xodus (branch flarial-macos: upstream + the BedrockOnLinux patches) with the file
# keyring the launcher reads, and packages xodus-cli-<rev>-macos-arm64.tar.gz for the flarialmc/macos release xodus-<rev>.
# Prints the SHA-256 to pin in src/Flarial.Runtime.Linux/Xodus/XodusClient.cs (the MACOS CliSha). Needs rustup, protobuf, cmake.
#   build-xodus-macos.sh <work dir> <out dir>
set -euo pipefail
WORK="${1:?work dir}"; OUT="${2:?out dir}"
REV="64d39eb87a56-p7"
TREE="$WORK/xodus"
[ -d "$TREE/.git" ] || git clone -q -b flarial-macos https://github.com/flarialmc/xodus "$TREE"
git -C "$TREE" fetch -q origin flarial-macos && git -C "$TREE" checkout -q FETCH_HEAD
cargo build --release --manifest-path "$TREE/Cargo.toml" -p xodus-cli --features xodus/key-chain-file --locked
PKG="$WORK/pkg"; rm -rf "$PKG"; mkdir -p "$PKG"
cp "$TREE/target/release/xodus-cli" "$PKG/"
cp "$TREE/LICENSE"* "$PKG/" 2>/dev/null || true
echo "xodus-cli $REV, GPL-3.0. Source: https://github.com/flarialmc/xodus/tree/$(git -C "$TREE" rev-parse HEAD)" > "$PKG/SOURCE.txt"
mkdir -p "$OUT"
tar -C "$PKG" -czf "$OUT/xodus-cli-$REV-macos-arm64.tar.gz" .
shasum -a 256 "$OUT/xodus-cli-$REV-macos-arm64.tar.gz"
