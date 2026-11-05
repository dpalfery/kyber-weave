#!/usr/bin/env bash
# Fake process table for scripts/proc-guard.sh, selected by KW_PROC_GUARD_PS.
# Reports one re-parented row that names the repository root; pair it with
# etime-kill-scope-linked.sh, which says the row started as the run began. Starts nothing.
set -euo pipefail

root="${KW_PROC_GUARD_ROOT:?fixture needs KW_PROC_GUARD_ROOT}"
user="$(id -un)"

printf '%s %s %s %s %s\n' 2147483033 1 "${user}" sh "sh -c ${root}/dash/node_modules/.bin/vitest run"
