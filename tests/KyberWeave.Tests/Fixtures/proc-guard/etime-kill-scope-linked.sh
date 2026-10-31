#!/usr/bin/env bash
# Fake elapsed-time lookup for scripts/proc-guard.sh, selected by KW_PROC_GUARD_ETIME.
# Reports pid 2147483033 as elapsed zero seconds: it started no earlier than the wrapped
# command. Starts nothing.
set -euo pipefail

printf '%s %s\n' 2147483033 00:00
