#!/usr/bin/env bash
# Stand-in for kill, selected by KW_PROC_GUARD_KILL. proc-guard.sh calls it as
# `<this> <signal> <pid>` instead of signalling the pid; it appends "<signal> <pid>" to the
# file named by KW_PROC_GUARD_KILL_LOG. It signals nothing and starts nothing else.
set -euo pipefail

printf '%s %s\n' "${1:?signal}" "${2:?pid}" >>"${KW_PROC_GUARD_KILL_LOG:?log path}"
