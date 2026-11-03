#!/usr/bin/env bash
# Fake process table for scripts/proc-guard.sh, selected by KW_PROC_GUARD_PS.
# Reports two survivors that carry the checked repository root, both re-parented to pid 1.
# Used to prove that check-leftovers --since counts only the survivors that started at or
# after the epoch: one of the two is old, one is new.
#
# Protocol the guard is pinned to (see ProcGuardSinceTests):
#   KW_PROC_GUARD_ROOT  the root being checked; the survivors' args embed it.
#
# Fake pids sit far above any real pid_max, so a signal aimed at them can never reach a
# live process. This script starts nothing.
set -euo pipefail

root="${KW_PROC_GUARD_ROOT:?fixture needs KW_PROC_GUARD_ROOT}"
user="$(id -un)"

printf '%s %s %s %s %s\n' 2147483003 1 "${user}" sh "sh -c ${root}/dash/node_modules/.bin/vitest run"
printf '%s %s %s %s %s\n' 2147483004 1 "${user}" sh "sh -c ${root}/dash/node_modules/.bin/vitest run --shard 2"
