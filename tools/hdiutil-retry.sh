#!/usr/bin/env bash

# Hosted macOS runners can transiently hold a newly created image busy.
# Keep each attempt's original output and status; retry only EBUSY.
hdiutil_retry() (
  local attempt status delay log
  log="$(mktemp "${TMPDIR:-/tmp}/yuwang-hdiutil.XXXXXX")" || exit 1
  trap 'rm -f "$log"' EXIT
  for attempt in 1 2 3 4 5; do
    # Do not pipe hdiutil through tee: diskimages-helper can keep the pipe open.
    if LC_ALL=C hdiutil "$@" >"$log" 2>&1; then
      cat "$log"
      exit 0
    else
      status=$?
    fi
    cat "$log" >&2
    if ! grep -Fq 'Resource busy' "$log" && ! [[ "$1" == detach && "$status" == 16 ]]; then
      exit "$status"
    fi
    if [[ "$attempt" == 5 ]]; then
      echo "hdiutil $1: resource remained busy after $attempt attempts." >&2
      exit "$status"
    fi
    delay=$((attempt * 2))
    echo "hdiutil $1: resource busy; retrying in ${delay}s (attempt $attempt/5)." >&2
    sleep "$delay"
  done
)
