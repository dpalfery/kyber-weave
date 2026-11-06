#!/usr/bin/env bash
# Fake process table for scripts/proc-guard.sh, selected by KW_PROC_GUARD_PS.
# Reports one survivor that carries the checked repository root in its command line, the
# shape a leftover test runner leaves behind after its parent has exited (ppid 1). Used for
# both `run` (orphan after a clean command) and `check-leftovers --root <dir>`.
#
# Protocol the guard is pinned to (see ProcGuardScriptTests):
#   KW_PROC_GUARD_ROOT  the root being checked; the survivor's args embed it.
#
# Pid 2147483003 is far above any real pid_max, so a signal aimed at it cannot reach a
# live process. This script starts nothing.
set -euo pipefail

root="${KW_PROC_GUARD_ROOT:?fixture needs KW_PROC_GUARD_ROOT}"

printf '%s %s %s %s %s\n' 2147483003 1 "$(id -un)" sh "sh -c ${root}/dash/node_modules/.bin/vitest run"
