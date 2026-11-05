#!/usr/bin/env bash
# Fake process table for scripts/proc-guard.sh, selected by KW_PROC_GUARD_PS.
# Reports three user node processes, so a --max-node 3 cap must trip. Nothing here is a
# real node process: this script starts nothing, and the fake pids are far above any real
# pid_max.
#
# Output: one row per process, "<pid> <ppid> <user> <comm> <args...>".
set -euo pipefail

user="$(id -un)"

printf '%s %s %s %s %s\n' 2147483011 1 "${user}" node "node fixture-worker-a"
printf '%s %s %s %s %s\n' 2147483012 1 "${user}" node "node fixture-worker-b"
printf '%s %s %s %s %s\n' 2147483013 1 "${user}" node "node fixture-worker-c"
