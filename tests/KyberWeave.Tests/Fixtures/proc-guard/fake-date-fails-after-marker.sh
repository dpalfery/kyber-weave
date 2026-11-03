#!/usr/bin/env bash
# Stand-in for date, selected by KW_PROC_GUARD_DATE. Behaves like `date` until the file named by
# KW_PROC_GUARD_TEST_MARKER exists, then fails like a broken clock. Starts nothing but date.
set -euo pipefail

if [[ -e "${KW_PROC_GUARD_TEST_MARKER:?marker path}" ]]; then
  exit 1
fi
exec /bin/date "$@"
