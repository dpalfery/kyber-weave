#!/usr/bin/env bash
# Checks that a run can start: demo checkout clean on its default branch and up to date,
# conductor deployed for Copilot, and the conductor model answering. Changes nothing
# except a fast-forward pull of the demo's default branch.
set -euo pipefail
source "$(dirname "$0")/config.sh"

command -v copilot >/dev/null || hc_die "copilot CLI not on PATH"
command -v jq >/dev/null || hc_die "jq not on PATH"
[ -f "$HC_DEMO_PROMPT" ] || hc_die "demo prompt not found: $HC_DEMO_PROMPT"
[ -f "$HOME/.copilot/agents/conductor.agent.md" ] || hc_die "conductor is not deployed for Copilot (~/.copilot/agents/conductor.agent.md)"

cd "$HC_DEMO_REPO"
git fetch -q origin
DEFAULT=$(git symbolic-ref --short refs/remotes/origin/HEAD 2>/dev/null | sed 's#^origin/##'); DEFAULT=${DEFAULT:-main}
[ "$(git branch --show-current)" = "$DEFAULT" ] || hc_die "demo checkout is on '$(git branch --show-current)', not '$DEFAULT'; run collect-and-reset.sh first"
[ -z "$(git status --porcelain)" ] || hc_die "demo checkout has uncommitted changes; run collect-and-reset.sh first"
git merge -q --ff-only "origin/$DEFAULT" || hc_die "demo '$DEFAULT' cannot fast-forward to origin/$DEFAULT"

EXTRA=$(git worktree list --porcelain | grep -c '^worktree ' || true)
[ "$EXTRA" -le 1 ] || hc_log "note: demo has $((EXTRA - 1)) other worktree(s); they are left alone (only worktrees a run creates are removed)"

ANSWER=$(cd "$HC_DEMO_REPO" && timeout 120 copilot -p "Reply with the word OK." --model "$HC_CONDUCTOR_MODEL" \
    --allow-all --no-ask-user --output-format json 2>/dev/null \
    | jq -r 'select(.type=="assistant.message" or .type=="session.error") | (.data.content // .data.message)' | tail -n 1)
case "$ANSWER" in *OK*) ;; *) hc_die "model '$HC_CONDUCTOR_MODEL' did not answer: ${ANSWER:-no output}";; esac

hc_log "preflight ok: demo on $DEFAULT at $(git rev-parse --short HEAD), model $HC_CONDUCTOR_MODEL answers"
