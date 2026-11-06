#!/usr/bin/env bash
# Fake process table for scripts/proc-guard.sh, selected by KW_PROC_GUARD_PS.
# Reports only benign long-lived .NET servers that carry the repository root: a Roslyn
# compiler server and an MSBuild reusable node, both re-parented to pid 1. Starts nothing;
# fake pids sit far above any real pid_max.
#
# Output: one row per process, "<pid> <ppid> <user> <comm> <args...>".
set -euo pipefail

root="${KW_PROC_GUARD_ROOT:?fixture needs KW_PROC_GUARD_ROOT}"
user="$(id -un)"

printf '%s %s %s %s %s\n' 2147483021 1 "${user}" dotnet "dotnet ${root}/sdk/Roslyn/bincore/VBCSCompiler.dll -pipename:fixture"
printf '%s %s %s %s %s\n' 2147483022 1 "${user}" dotnet "dotnet ${root}/sdk/MSBuild.dll /nodemode:1 /nodeReuse:true"
