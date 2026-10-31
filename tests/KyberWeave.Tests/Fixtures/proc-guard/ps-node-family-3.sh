#!/usr/bin/env bash
# Fake process table for scripts/proc-guard.sh, selected by KW_PROC_GUARD_PS.
# Reports three user JavaScript runtimes under different names (bun, nodejs, tsx), so a
# --max-node 3 cap must trip. Nothing here is a real process: this script starts nothing, and
# the fake pids are far above any real pid_max.
#
# Output: one row per process, "<pid> <ppid> <user> <comm> <args...>".
set -euo pipefail

user="$(id -un)"

printf '%s %s %s %s %s\n' 2147483041 1 "${user}" /opt/homebrew/bin/bun "bun fixture-a"
printf '%s %s %s %s %s\n' 2147483042 1 "${user}" /usr/bin/nodejs "nodejs fixture-b"
printf '%s %s %s %s %s\n' 2147483043 1 "${user}" tsx "tsx fixture-c"
