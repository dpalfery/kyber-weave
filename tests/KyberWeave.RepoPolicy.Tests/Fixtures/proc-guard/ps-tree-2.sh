#!/usr/bin/env bash
# Fake process table for scripts/proc-guard.sh, selected by KW_PROC_GUARD_PS.
# Reports two descendants of the wrapped command, so a --max-tree 1 cap must trip.
#
# Protocol the guard is pinned to (see ProcGuardScriptTests):
#   KW_PROC_GUARD_ROOT_PID  pid of the wrapped command; fake descendants attach to it.
#   KW_PROC_GUARD_ROOT      repository root (run) or --root value (check-leftovers).
#   Output: one row per process, "<pid> <ppid> <user> <comm> <args...>".
#
# Fake pids sit far above any real pid_max, so a signal aimed at them can never reach a
# live process. This script starts nothing.
set -euo pipefail

user="$(id -un)"
root_pid="${KW_PROC_GUARD_ROOT_PID:?fixture needs KW_PROC_GUARD_ROOT_PID}"

printf '%s %s %s %s %s\n' 2147483001 "${root_pid}" "${user}" sh "sh -c fixture-descendant-a"
printf '%s %s %s %s %s\n' 2147483002 "${root_pid}" "${user}" sh "sh -c fixture-descendant-b"
