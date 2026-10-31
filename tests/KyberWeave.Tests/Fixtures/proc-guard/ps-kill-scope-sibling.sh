#!/usr/bin/env bash
# Fake process table for scripts/proc-guard.sh, selected by KW_PROC_GUARD_PS.
# Reports one re-parented row whose command line names a sibling directory that merely
# shares the repository root as a prefix (<root>-2). It is not under the root. Starts nothing.
set -euo pipefail

root="${KW_PROC_GUARD_ROOT:?fixture needs KW_PROC_GUARD_ROOT}"
user="$(id -un)"

printf '%s %s %s %s %s\n' 2147483032 1 "${user}" sh "sh -c ${root}-2/dash/node_modules/.bin/vitest run"
