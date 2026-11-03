#!/usr/bin/env bash
# Fake elapsed-time lookup for scripts/proc-guard.sh, selected by KW_PROC_GUARD_ETIME.
# Reports no elapsed time for any pid, the shape of a process that exited between the
# table read and the lookup. Starts nothing.
set -euo pipefail
