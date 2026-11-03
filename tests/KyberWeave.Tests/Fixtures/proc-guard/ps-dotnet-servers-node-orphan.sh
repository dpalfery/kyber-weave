#!/usr/bin/env bash
# Fake process table for scripts/proc-guard.sh, selected by KW_PROC_GUARD_PS.
# Reports a benign Roslyn compiler server plus a real leftover: a vitest node process that
# carries the repository root and was re-parented to pid 1. Starts nothing; fake pids sit
# far above any real pid_max.
#
# Output: one row per process, "<pid> <ppid> <user> <comm> <args...>".
set -euo pipefail

root="${KW_PROC_GUARD_ROOT:?fixture needs KW_PROC_GUARD_ROOT}"
user="$(id -un)"

printf '%s %s %s %s %s\n' 2147483021 1 "${user}" dotnet "dotnet ${root}/sdk/Roslyn/bincore/VBCSCompiler.dll -pipename:fixture"
printf '%s %s %s %s %s\n' 2147483023 1 "${user}" node "node ${root}/dash/node_modules/.bin/vitest run"
