#!/usr/bin/env bash
# Fake elapsed-time lookup for scripts/proc-guard.sh, selected by KW_PROC_GUARD_ETIME.
# Reports pid 2147483003 as elapsed one hour (old) and pid 2147483004 as elapsed five
# seconds (new). Starts nothing.
set -euo pipefail

printf '%s %s\n' 2147483003 01:00:00
printf '%s %s\n' 2147483004 00:05
