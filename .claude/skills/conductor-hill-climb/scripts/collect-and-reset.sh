#!/usr/bin/env bash
# After a run: save its evidence into the run directory, score it, then put the demo
# back where it started. Only what the run created is removed: worktrees and branches
# absent from the run's before-snapshot. Uncommitted work in the canonical checkout is
# stashed (recoverable), never discarded.
#
# Usage: collect-and-reset.sh [run-dir]      # defaults to the newest run
set -euo pipefail
source "$(dirname "$0")/config.sh"
RUN=${1:-$(hc_latest_run)}
[ -d "$RUN" ] && [ -f "$RUN/worktrees.before" ] || hc_die "not a run directory: ${RUN:-none}"
EV="$RUN/evidence"; mkdir -p "$EV"
cd "$HC_DEMO_REPO"
DEFAULT=$(git symbolic-ref --short refs/remotes/origin/HEAD 2>/dev/null | sed 's#^origin/##'); DEFAULT=${DEFAULT:-main}

git worktree list --porcelain | sed -n 's/^worktree //p' | sort >"$RUN/worktrees.after"
git for-each-ref --format='%(refname:short)' refs/heads | sort >"$RUN/branches.after"
NEW_WT=$(comm -13 "$RUN/worktrees.before" "$RUN/worktrees.after")
NEW_BR=$(comm -13 "$RUN/branches.before" "$RUN/branches.after")

# Evidence: commits on every new branch, uncommitted work everywhere the run wrote.
for b in $NEW_BR; do
    f="$EV/branch-${b//\//_}"
    git log --format='%h %s' "origin/$DEFAULT..$b" >"$f.log"
    git diff --stat "origin/$DEFAULT...$b" >"$f.stat"
done
if [ -n "$NEW_BR" ]; then git bundle create "$EV/run-branches.bundle" $NEW_BR "^origin/$DEFAULT" 2>/dev/null || true; fi
for wt in $NEW_WT "$HC_DEMO_REPO"; do
    [ -d "$wt" ] || continue
    s=$(git -C "$wt" status --porcelain)
    if [ -n "$s" ]; then
        echo "$s" >"$EV/uncommitted-$(basename "$wt").status"
        git -C "$wt" diff >"$EV/uncommitted-$(basename "$wt").diff"
    fi
done
git -C "$HC_DEMO_REPO" branch --show-current >"$EV/canonical-branch-at-end"

python3 "$HC_SCRIPTS/score-run.py" "$RUN" >"$RUN/metrics.json" || hc_log "warning: scoring failed; see $RUN/metrics.json"

# Reset. Stop anything still running from a run worktree (dev servers, watchers).
for wt in $NEW_WT; do pkill -f "$wt" 2>/dev/null || true; done
if [ -n "$(git status --porcelain)" ]; then
    git stash push -q -u -m "hill-climb $(basename "$RUN") leftovers" && hc_log "stashed uncommitted changes in the canonical checkout"
fi
[ "$(git branch --show-current)" = "$DEFAULT" ] || git checkout -q "$DEFAULT"
for wt in $NEW_WT; do git worktree remove --force "$wt" && hc_log "removed worktree $wt"; done
git worktree prune
for b in $NEW_BR; do git branch -D -q "$b" && hc_log "deleted branch $b"; done
git fetch -q origin && git merge -q --ff-only "origin/$DEFAULT"
hc_log "demo reset to $DEFAULT at $(git rev-parse --short HEAD); evidence in $EV"
