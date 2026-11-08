#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ -z "${MACOS_SIGNING_CERTIFICATE_P12:-}" && -z "${MACOS_SIGNING_CERTIFICATE_PASSWORD:-}" ]]; then
  if [[ "${YUWANG_REQUIRE_SIGNING:-0}" == 1 ]]; then
    echo 'The main repository requires its persistent signing identity.' >&2
    exit 1
  fi
  echo 'No signing identity supplied; this build uses ad-hoc signatures.'
  exit 0
fi
: "${MACOS_SIGNING_CERTIFICATE_P12:?Missing signing certificate}"
: "${MACOS_SIGNING_CERTIFICATE_PASSWORD:?Missing signing password}"
: "${GITHUB_ENV:?Run inside GitHub Actions}"
work="${RUNNER_TEMP:?}/yuwang-signing"
mkdir -p "$work"
chmod 700 "$work"
umask 077
keychain="$work/signing.keychain-db"
p12="$work/identity.p12"
password="$(openssl rand -hex 32)"
echo "::add-mask::$password"
printf '%s' "$MACOS_SIGNING_CERTIFICATE_P12" | base64 --decode > "$p12"
trap 'rm -f "$p12"' EXIT
security create-keychain -p "$password" "$keychain"
security set-keychain-settings -lut 21600 "$keychain"
security unlock-keychain -p "$password" "$keychain"
security import "$p12" -k "$keychain" -P "$MACOS_SIGNING_CERTIFICATE_PASSWORD" -T /usr/bin/codesign
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$password" "$keychain" >/dev/null
# Only the runner trusts the preview certificate. End-user system trust is never changed.
sudo security add-trusted-cert -d -r trustRoot -p codeSign -k /Library/Keychains/System.keychain "$root/docs/signing/preview-signing.pem"
identity="$(openssl x509 -in "$root/docs/signing/preview-signing.pem" -noout -fingerprint -sha1 | cut -d= -f2 | tr -d ':')"
security find-identity -v -p codesigning "$keychain" | grep -F "$identity" >/dev/null
printf 'YUWANG_SIGNING_KEYCHAIN=%s\nYUWANG_SIGNING_IDENTITY=%s\n' "$keychain" "$identity" >> "$GITHUB_ENV"
echo 'Loaded the persistent preview signing identity.'
