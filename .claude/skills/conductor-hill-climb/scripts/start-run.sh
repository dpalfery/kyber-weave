#!/usr/bin/env bash
# Runs the conductor once on the demo feature, headless in Copilot CLI, and records
# everything into a new run directory. Blocks until the run ends; launch it in the
# background and watch <run>/session.jsonl.
#
# Pushes are blocked for this process only (a per-process git config override, plus
# deny rules on the push-and-open-PR tools), so delivery reports NOT DELIVERED by design.
#
# Usage: start-run.sh [note]     # prints the run directory on the first line
set -uo pipefail
source "$(dirname "$0")/config.sh"

N=$(( $(ls -1d "$HC_RUNS"/run-* 2>/dev/null | wc -l) + 1 ))
[ "$N" -le "$HC_MAX_RUNS" ] || hc_die "run cap reached ($HC_MAX_RUNS)"
RUN="$HC_RUNS/run-$(printf %02d "$N")"
mkdir -p "$RUN/copilot-logs"
echo "$RUN"

cd "$HC_DEMO_REPO"
git worktree list --porcelain | sed -n 's/^worktree //p' | sort >"$RUN/worktrees.before"
git for-each-ref --format='%(refname:short)' refs/heads | sort >"$RUN/branches.before"
git rev-parse HEAD >"$RUN/canonical-head.before"

# Strip the frontmatter; the run gets the feature text plus the autonomy switch only.
{ printf 'autonomy: autonomous\n\n'; awk 'NR==1&&/^---$/{f=1;next} f&&/^---$/{f=0;next} !f' "$HC_DEMO_PROMPT"; } >"$RUN/prompt.md"

cat >"$RUN/meta.json" <<EOF
{"run": $N, "note": "${1:-}", "started": "$(date -u +%FT%TZ)",
 "kyberWeaveVersion": "$(kyber-weave --version | awk '{print $2}')",
 "kyberWeaveCommit": "$(git -C "$HC_KW_REPO" rev-parse --short HEAD)",
 "demoCommit": "$(git rev-parse --short HEAD)", "model": "$HC_CONDUCTOR_MODEL",
 "timeoutMin": $HC_RUN_TIMEOUT_MIN}
EOF

ARGS=(-p "$(cat "$RUN/prompt.md")" --agent conductor --model "$HC_CONDUCTOR_MODEL"
      --allow-all --no-ask-user --autopilot --max-autopilot-continues "$HC_AUTOPILOT_CONTINUES"
      --output-format json --log-dir "$RUN/copilot-logs" --log-level info --share "$RUN/session.md"
      --deny-tool 'shell(git push)' --deny-tool 'shell(gh pr create)'
      --deny-tool 'kw-docs(docs_push_run_branch_and_open_draft_pull_request)'
      --deny-tool 'kyber-weave(docs_push_run_branch_and_open_draft_pull_request)')
[ -n "$HC_MAX_AI_CREDITS" ] && ARGS+=(--max-ai-credits "$HC_MAX_AI_CREDITS")

START=$(date +%s)
GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=remote.origin.pushurl GIT_CONFIG_VALUE_0=blocked-by-hill-climb \
    timeout --signal=TERM --kill-after=60 "${HC_RUN_TIMEOUT_MIN}m" copilot "${ARGS[@]}" \
    2>>"$RUN/stderr.log" | cat >>"$RUN/session.jsonl"
CODE=${PIPESTATUS[0]}
# Run 4 ended with both redirected files emptied; Copilot also keeps the stream in its session store, so fall back
# to that copy when ours is empty.
if [ ! -s "$RUN/session.jsonl" ]; then
    SID=$(ls -td "$HOME"/.copilot/session-state/*/ 2>/dev/null | while read -r d; do grep -qF "$(head -c 60 "$RUN/prompt.md" | tail -c 40)" "$d/events.jsonl" 2>/dev/null && { echo "$d"; break; }; done)
    [ -n "$SID" ] && cp "$SID/events.jsonl" "$RUN/session.jsonl" && hc_log "session stream was empty; copied Copilot's own copy from $SID"
fi
END=$(date +%s)
OUTCOME=finished; [ "$CODE" -ne 0 ] && OUTCOME=crashed; [ "$CODE" -eq 124 ] || [ "$CODE" -eq 137 ] && OUTCOME=timeout
printf '{"exitCode": %d, "outcome": "%s", "seconds": %d, "ended": "%s"}\n' "$CODE" "$OUTCOME" $((END - START)) "$(date -u +%FT%TZ)" >"$RUN/exit.json"
hc_log "run $N $OUTCOME (exit $CODE) after $(( (END - START) / 60 )) min"
