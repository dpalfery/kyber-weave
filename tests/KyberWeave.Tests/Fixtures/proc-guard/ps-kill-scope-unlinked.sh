#!/usr/bin/env bash
# Fake process table for scripts/proc-guard.sh, selected by KW_PROC_GUARD_PS.
# Reports two re-parented (ppid 1) rows that name the repository root:
#   - the real parent of the running proc-guard.sh (its caller, as an orphaned harness shell
#     would be), which must never be a leftover or be signalled;
#   - fake pid 2147483031, with no link to the run (no etime, not a descendant), which is
#     reported but must not be signalled.
# Fake pids sit far above any real pid_max. This script starts nothing but ps.
set -euo pipefail

root="${KW_PROC_GUARD_ROOT:?fixture needs KW_PROC_GUARD_ROOT}"
user="$(id -un)"

# Walk up from this process to the proc-guard.sh run and print its parent.
cur=$$
parent=""
for _ in 1 2 3 4 5 6 7 8; do
  next="$(ps -o ppid= -p "${cur}" | tr -d ' ')"
  if ps -o args= -p "${next}" | grep -q 'proc-guard\.sh'; then
    parent="$(ps -o ppid= -p "${next}" | tr -d ' ')"
    break
  fi
  cur="${next}"
done

if [[ -n "${parent}" ]]; then
  printf '%s %s %s %s %s\n' "${parent}" 1 "${user}" zsh "/bin/zsh -c harness-wrapper ${root}"
fi
printf '%s %s %s %s %s\n' 2147483031 1 "${user}" sh "sh -c ${root}/dash/node_modules/.bin/vitest run"
