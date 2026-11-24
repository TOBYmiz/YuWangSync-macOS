#!/usr/bin/env bash

# Hosted macOS runners can transiently hold a newly created image busy.
# Keep each attempt's original output and status; retry only EBUSY.
hdiutil_retry() (
  local attempt status delay log
  log="$(mktemp "${TMPDIR:-/tmp}/yuwang-hdiutil.XXXXXX")" || exit 1
  # Bash 3.2 can unwind function locals before running a subshell's EXIT trap.
  # Capture the escaped path now so cleanup does not depend on the local scope.
  trap "rm -f -- $(printf '%q' "$log")" EXIT
  for attempt in 1 2 3 4 5; do
    # Do not pipe hdiutil through tee: diskimages-helper can keep the pipe open.
    if LC_ALL=C hdiutil "$@" >"$log" 2>&1; then
      cat "$log"
      status=0
      break
    else
      status=$?
    fi
    cat "$log" >&2
    if ! grep -Fq 'Resource busy' "$log" && ! [[ "$1" == detach && "$status" == 16 ]]; then
      break
    fi
    if [[ "$attempt" == 5 ]]; then
      echo "hdiutil $1: resource remained busy after $attempt attempts." >&2
      break
    fi
    delay=$((attempt * 2))
    echo "hdiutil $1: resource busy; retrying in ${delay}s (attempt $attempt/5)." >&2
    sleep "$delay"
  done
  # Bash 3.2 can suppress a subshell's EXIT trap when called from an EXIT
  # handler (including failed-attach cleanup). Remove the log before exiting;
  # the trap above remains a fallback for unexpected early termination.
  rm -f -- "$log"
  trap - EXIT
  exit "$status"
)
