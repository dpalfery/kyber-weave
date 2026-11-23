#!/usr/bin/env bash
# Prints the path of a run's live event stream, waiting until it exists. A headless run writes
# <run>/session.jsonl as it goes; an interactive run (HC_INTERACTIVE=1) writes only Copilot's own
# session store, ~/.copilot/session-state/<id>/events.jsonl, found by the run's prompt text and
# start time. Other Copilot sessions on the machine are ignored.
#
# Usage: run-events.sh <run dir>
set -uo pipefail
RUN=${1:?run dir}
until [ -f "$RUN/prompt.md" ]; do sleep 2; done
NEEDLE=$(head -c 60 "$RUN/prompt.md" | tail -c 40)
while true; do
    [ -s "$RUN/session.jsonl" ] && { echo "$RUN/session.jsonl"; exit 0; }
    for d in $(ls -td "$HOME"/.copilot/session-state/*/ 2>/dev/null | head -n 10); do
        f="$d/events.jsonl"
        [ -f "$f" ] && [ "$f" -nt "$RUN/prompt.md" ] && grep -qF "$NEEDLE" "$f" 2>/dev/null && { echo "$f"; exit 0; }
    done
    sleep 2
done
