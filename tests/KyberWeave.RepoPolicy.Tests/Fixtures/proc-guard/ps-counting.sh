#!/usr/bin/env bash
# Fake process table for scripts/proc-guard.sh, selected by KW_PROC_GUARD_PS.
# Reports no processes and appends one line to the file named by KW_PROC_GUARD_PS_LOG for every
# time the guard reads the table, so a test can count the reads. Starts nothing.
set -euo pipefail

printf 'read\n' >>"${KW_PROC_GUARD_PS_LOG:?fixture needs KW_PROC_GUARD_PS_LOG}"
