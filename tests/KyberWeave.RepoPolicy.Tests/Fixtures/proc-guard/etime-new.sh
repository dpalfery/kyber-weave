#!/usr/bin/env bash
# Fake elapsed-time lookup for scripts/proc-guard.sh, selected by KW_PROC_GUARD_ETIME.
# Reports pid 2147483003 as elapsed five seconds, in ps etime form. Starts nothing.
set -euo pipefail

printf '%s %s\n' 2147483003 00:05
