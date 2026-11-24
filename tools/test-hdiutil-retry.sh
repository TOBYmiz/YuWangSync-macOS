#!/usr/bin/env bash
set -euo pipefail
printf 'Checking DMG retry handling with Bash %s\n' "$BASH_VERSION"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$root/tools/hdiutil-retry.sh"
test_parent="$(cd "${TMPDIR:-/tmp}" && pwd -P)"
test_work="$(mktemp -d "$test_parent/yuwang-dmg-tests.XXXXXX")"
cleanup_test_work() {
  local status=$?
  if [[ "$status" != 0 && -f "$test_work/output" ]]; then cat "$test_work/output" >&2; fi
  if [[ "${test_work%/*}" == "$test_parent" && "${test_work##*/}" == yuwang-dmg-tests.* ]]; then
    rm -rf "$test_work"
  fi
  return "$status"
}
trap cleanup_test_work EXIT
# Exercise trap quoting as well as cleanup under macOS's Bash 3.2.
export TMPDIR="$test_work/logs with spaces and 'quotes'"
mkdir -p "$TMPDIR"

# Stub only the external command and delays. The real retry function runs with
# errexit/pipefail enabled, including its subshell, logging and exit cleanup.
hdiutil() {
  local count
  [[ "${LC_ALL:-}" == C ]] || return 99
  count="$(cat "$test_work/count")"; count=$((count + 1))
  echo "$count" > "$test_work/count"
  printf '%s\n' "$@" > "$test_work/args"
  case "$scenario" in
    success) echo 'created: test image'; return 0 ;;
    transient|convert-transient)
      if [[ "$count" == 3 ]]; then echo 'created: test image'; return 0; fi
      echo 'hdiutil: create failed - Resource busy' >&2; return 1 ;;
    exhausted) echo 'hdiutil: create failed - Resource busy' >&2; return 1 ;;
    convert-exhausted) echo 'hdiutil: convert failed - Resource busy' >&2; return 34 ;;
    unrelated) echo 'hdiutil: create failed - No space left on device' >&2; return 28 ;;
    create-status-16) echo 'hdiutil: create failed' >&2; return 16 ;;
    detach) return 16 ;;
    detach-transient) [[ "$count" == 3 ]] && return 0; return 16 ;;
    detach-unrelated) echo 'hdiutil: detach failed - Invalid argument' >&2; return 22 ;;
  esac
}
sleep() { echo "$1" >> "$test_work/delays"; }

for scenario in success transient exhausted unrelated create-status-16 convert-transient convert-exhausted detach detach-transient detach-unrelated; do
  echo 0 > "$test_work/count"
  : > "$test_work/delays"
  operation=create
  case "$scenario" in
    convert-*) operation=convert ;;
    detach*) operation=detach ;;
  esac
  status=0
  # A new shell preserves production errexit behavior: calling a shell function
  # in an if condition would disable errexit throughout that function.
  export scenario test_work operation
  export -f hdiutil sleep hdiutil_retry
  "$BASH" -euo pipefail -c 'hdiutil_retry "$operation" -ov "image with spaces.dmg"; echo continued' \
    > "$test_work/output" 2>&1 || status=$?
  case "$scenario" in
    success) expected_status=0; expected_calls=1; expected_delays=0 ;;
    transient|convert-transient|detach-transient) expected_status=0; expected_calls=3; expected_delays=2 ;;
    exhausted) expected_status=1; expected_calls=5; expected_delays=4 ;;
    unrelated) expected_status=28; expected_calls=1; expected_delays=0 ;;
    create-status-16) expected_status=16; expected_calls=1; expected_delays=0 ;;
    convert-exhausted) expected_status=34; expected_calls=5; expected_delays=4 ;;
    detach) expected_status=16; expected_calls=5; expected_delays=4 ;;
    detach-unrelated) expected_status=22; expected_calls=1; expected_delays=0 ;;
  esac
  [[ "$status" == "$expected_status" ]]
  [[ "$(cat "$test_work/count")" == "$expected_calls" ]]
  : > "$test_work/expected-delays"
  for ((attempt=1; attempt<=expected_delays; attempt++)); do
    echo "$((attempt * 2))" >> "$test_work/expected-delays"
  done
  cmp "$test_work/delays" "$test_work/expected-delays"
  printf '%s\n' "$operation" -ov 'image with spaces.dmg' > "$test_work/expected-args"
  cmp "$test_work/args" "$test_work/expected-args"
  if [[ "$status" == 0 ]]; then grep -Fq continued "$test_work/output"
  else ! grep -Fq continued "$test_work/output"; fi
  if compgen -G "$TMPDIR/yuwang-hdiutil.*" > /dev/null; then
    echo "Temporary hdiutil log leaked in scenario: $scenario" >&2; exit 1
  fi
  echo "Passed hdiutil retry scenario: $scenario"
done

# Run the actual DMG stage without compiling the app or mounting a real image.
# The stubs track mount state so EXIT cleanup and early termination are tested
# together with the production trap registrations and command order.
sed -n '/^dmg=/,$p' "$root/tools/package-macos.sh" > "$test_work/package-dmg.sh"
test -s "$test_work/package-dmg.sh"
export dmg_test_root="$test_work/package with spaces"
mkdir -p "$dmg_test_root/artifacts" "$dmg_test_root/work"
hdiutil() {
  local operation=$1 detach_count
  echo "$operation" >> "$test_work/trace"
  case "$operation" in
    attach)
      : > "$test_work/mounted"
      case "$scenario" in
        attach-failure|cleanup-transient|cleanup-exhausted) return 23 ;;
      esac ;;
    detach)
      detach_count="$(grep -c '^detach$' "$test_work/trace")"
      if [[ "$scenario" == cleanup-exhausted || ( "$scenario" == cleanup-transient && "$detach_count" -lt 3 ) ]]; then
        echo 'hdiutil: detach failed - Resource busy' >&2
        return 16
      fi
      [[ "$scenario" != cleanup-failure ]] || return 27
      rm -f "$test_work/mounted" ;;
    verify) [[ "$scenario" != verify-failure ]] || return 25 ;;
  esac
  return 0
}
dmg_layout() {
  echo "layout-$2" >> "$test_work/trace"
  if [[ "$2" == write && ( "$scenario" == layout-failure || "$scenario" == cleanup-failure ) ]]; then return 24; fi
}
codesign() {
  echo codesign >> "$test_work/trace"
  [[ "$scenario" != signature-failure ]] || return 26
}
readlink() { echo /Applications; }
diff() { return 0; }
shasum() { echo 'synthetic checksum'; }
export -f hdiutil dmg_layout codesign readlink diff shasum

for scenario in package-success attach-failure layout-failure verify-failure signature-failure cleanup-failure cleanup-transient cleanup-exhausted; do
  : > "$test_work/trace"
  : > "$test_work/delays"
  rm -f "$test_work/mounted"
  export scenario
  status=0
  "$BASH" -euo pipefail -c '
    root="$dmg_test_root"; work="$root/work"; app="$root/app"; image_root="$root/image"
    release_version=1.0.0; dmg_python=dmg_layout; YUWANG_DMG_CAPTURE=0
    source "$test_work/package-dmg.sh"
  ' > "$test_work/output" 2>&1 || status=$?
  case "$scenario" in
    package-success) expected_status=0; expected_detaches=2 ;;
    attach-failure) expected_status=23; expected_detaches=1 ;;
    layout-failure|cleanup-failure) expected_status=24; expected_detaches=1 ;;
    verify-failure) expected_status=25; expected_detaches=1 ;;
    signature-failure) expected_status=26; expected_detaches=2 ;;
    cleanup-transient) expected_status=23; expected_detaches=3 ;;
    cleanup-exhausted) expected_status=23; expected_detaches=5 ;;
  esac
  [[ "$status" == "$expected_status" ]]
  [[ "$(grep -c '^detach$' "$test_work/trace")" == "$expected_detaches" ]]
  if [[ "$scenario" == cleanup-failure || "$scenario" == cleanup-exhausted ]]; then
    test -f "$test_work/mounted"
    grep -Fq 'Could not detach temporary DMG mount:' "$test_work/output"
  else test ! -f "$test_work/mounted"; fi
  : > "$test_work/expected-delays"
  case "$scenario" in
    cleanup-transient) printf '%s\n' 2 4 > "$test_work/expected-delays" ;;
    cleanup-exhausted) printf '%s\n' 2 4 6 8 > "$test_work/expected-delays" ;;
  esac
  cmp "$test_work/delays" "$test_work/expected-delays"
  if [[ "$status" == 0 ]]; then grep -Fq 'Created ' "$test_work/output"
  else ! grep -Fq 'Created ' "$test_work/output"; fi
  if compgen -G "$TMPDIR/yuwang-hdiutil.*" > /dev/null; then
    echo "Temporary hdiutil log leaked in scenario: $scenario" >&2; exit 1
  fi
  echo "Passed DMG packaging scenario: $scenario"
done
