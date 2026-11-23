#!/usr/bin/env bash
# Short test of the architect alone: one planning dispatch on the demo feature prompt, in a fresh
# worktree, the way the conductor sends it. Saves the Draft plan as a fixture and flags the plan
# shapes the owner has rejected (new test databases, CI or credential steps, events for operations
# the request does not name, hard-rated tasks). One architect pass instead of a whole conductor run.
#
# Usage: architect-test.sh [note]     # prints the test directory first
set -uo pipefail
source "$(dirname "$0")/config.sh"

TESTS="$HC_HOME/harness/architect/tests"; mkdir -p "$TESTS"
N=$(( $(ls -1d "$TESTS"/test-* 2>/dev/null | wc -l) + 1 ))
OUT="$TESTS/test-$(printf %02d "$N")"; mkdir -p "$OUT"
echo "$OUT"

BRANCH="hc/architect-test-$N"
WT="$HC_DEMO_REPO.worktrees/hc-architect-test-$N"
git -C "$HC_DEMO_REPO" worktree add -q -b "$BRANCH" "$WT" HEAD || hc_die "worktree add failed"
BASE=$(git -C "$HC_DEMO_REPO" rev-parse HEAD)
PLAN_REL="6-Docs/plans/dashboard-job-status-cards.md"
printf '{"test": %d, "note": "%s", "kyberWeaveVersion": "%s", "started": "%s"}\n' \
    "$N" "${1:-}" "$(kyber-weave --version | awk '{print $2}')" "$(date -u +%FT%TZ)" >"$OUT/meta.json"

FEATURE=$(awk 'NR==1&&/^---$/{f=1;next} f&&/^---$/{f=0;next} !f' "$HC_DEMO_PROMPT")
DISPATCH="KYBER-ARBITER: true
PLAN_FILE: $PLAN_REL
Run worktree: $WT
Branch: $BRANCH; base $BASE. autonomy: autonomous; development-mode: test-first. Author Draft plan and return structured digest, no implementation.

autonomy: autonomous

$FEATURE"

cd "$WT"
timeout 2400 copilot -p "Dispatch the architect agent as a sub-agent with exactly the text between the markers as its prompt. When it returns, print its full reply verbatim and stop: do nothing else, dispatch nobody else.
<<<
$DISPATCH
>>>" --model "${HC_TEST_MODEL:-gpt-6.1-terra}" --allow-all --no-ask-user --output-format json \
    >"$OUT/session.jsonl" 2>"$OUT/stderr.log"
echo "{\"exitCode\": $?}" >"$OUT/exit.json"
cd - >/dev/null

PLAN=$(ls -t "$WT"/6-Docs/plans/*.md 2>/dev/null | grep -v -i readme | head -n 1)
[ -n "$PLAN" ] && cp "$PLAN" "$OUT/$(basename "$PLAN")"
git -C "$HC_DEMO_REPO" worktree remove --force "$WT"
git -C "$HC_DEMO_REPO" branch -D "$BRANCH" >/dev/null 2>&1

python3 - "$OUT" <<'EOF'
import glob, json, re, sys
out = sys.argv[1]
plans = [p for p in glob.glob(f"{out}/*.md")]
ev = [json.loads(l) for l in open(f"{out}/session.jsonl") if l.strip()]
skills = [e["data"]["arguments"].get("skill") for e in ev
          if e["type"] == "tool.execution_start" and e["data"].get("toolName") == "skill"]
r = {"plan": plans[0] if plans else None, "skillsLoaded": skills}
if plans:
    t = open(plans[0]).read()
    r["tasks"] = re.findall(r"^### (T\d+:[^\n]*)", t, re.M)
    r["difficulty"] = re.findall(r"\*\*Difficulty:\*\* (\w+)", t)
    flags = {
        "new test database/catalog": r"\b(disposable|throwaway|per-test|unique) (sql )?(catalog|database)|CREATE DATABASE|hotshot_[a-z]+_<",
        "CI or workflow change": r"\.github/workflows|ci\.yml",
        "credential or secret": r"SA_PASSWORD|SQL_ADMIN|credential|secret",
        "event for an unnamed operation": r"JobDeleted|JOB_DELETED|deletion (signal|event|broadcast)",
        "hard task": r"\*\*Difficulty:\*\* hard",
    }
    r["redFlags"] = {k: len(re.findall(p, t, re.I)) for k, p in flags.items() if re.search(p, t, re.I)}
    r["prNotes"] = re.findall(r"PR note: ([^\n]{0,160})", t)
json.dump(r, open(f"{out}/result.json", "w"), indent=1)
print(json.dumps(r, indent=1))
EOF
