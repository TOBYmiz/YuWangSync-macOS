#!/usr/bin/env bash
set -euo pipefail
target="${1:?Pass a code path}"
identifier="${2:?Pass its stable identifier}"
shift 2
if [[ -n "${YUWANG_SIGNING_IDENTITY:-}" ]]; then
  : "${YUWANG_SIGNING_KEYCHAIN:?Signing keychain is missing}"
  echo "Signing $identifier with the persistent preview identity."
  codesign --force --sign "$YUWANG_SIGNING_IDENTITY" --keychain "$YUWANG_SIGNING_KEYCHAIN" --timestamp=none \
    --identifier "$identifier" \
    --requirements "=designated => identifier \"$identifier\" and certificate leaf = H\"$YUWANG_SIGNING_IDENTITY\"" "$@" "$target"
else
  codesign --force --sign - --identifier "$identifier" "$@" "$target"
fi
