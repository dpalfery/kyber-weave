#!/usr/bin/env bash
# Fake process table for scripts/proc-guard.sh, selected by KW_PROC_GUARD_PS.
# Reports no processes at all: the machine after a clean command. Prints nothing and
# starts nothing.
set -euo pipefail
