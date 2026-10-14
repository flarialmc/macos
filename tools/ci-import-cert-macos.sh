#!/bin/bash
# CI only: imports the Developer ID certificate (CERT_P12 = base64 .p12, CERT_PASSWORD) into a temporary keychain for codesign.
set -euo pipefail
KC="$RUNNER_TEMP/signing.keychain-db"; KCPW="$(uuidgen)"
security create-keychain -p "$KCPW" "$KC"
security set-keychain-settings -lut 21600 "$KC"
security unlock-keychain -p "$KCPW" "$KC"
printf '%s' "$CERT_P12" | base64 -D > "$RUNNER_TEMP/cert.p12"
security import "$RUNNER_TEMP/cert.p12" -k "$KC" -P "$CERT_PASSWORD" -T /usr/bin/codesign
rm -f "$RUNNER_TEMP/cert.p12"
security set-key-partition-list -S apple-tool:,apple: -k "$KCPW" "$KC" >/dev/null
security list-keychains -d user -s "$KC" $(security list-keychains -d user | tr -d '"')
security find-identity -v -p codesigning "$KC"
