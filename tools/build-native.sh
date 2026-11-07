#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="${1:?Pass an output directory}"
if [[ "$(uname -s)" != Darwin ]]; then echo 'The macOS bridge must be built on macOS.' >&2; exit 1; fi
mkdir -p "$output"
xcrun --sdk macosx clang -arch arm64 -mmacosx-version-min=14.0 -fobjc-arc -fvisibility=hidden \
  -dynamiclib -framework Foundation -framework Security \
  "$root/native/macos/YuWangMac.m" -o "$output/libYuWangMac.dylib"
