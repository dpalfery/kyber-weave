#!/usr/bin/env bash
# Live, readable view of a conductor run for the owner to watch in tmux: sub-agent starts and ends,
# guard refusals, shell commands and other tool calls, conductor messages and the run's end
# (formatting in watch-run.py). Follows whichever stream the run writes (run-events.sh).
#
# Usage: watch-run.sh <run dir>      # waits for the run's event stream to appear
set -uo pipefail
HERE=$(dirname "$0")
EVENTS=$(bash "$HERE/run-events.sh" "${1:?run dir}")
echo "following $EVENTS"
tail -n +1 -F "$EVENTS" | python3 -u "$HERE/watch-run.py"
