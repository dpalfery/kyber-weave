#!/usr/bin/env bash
# Short test of the plan reviewer alone: puts a saved fixture plan into a fresh demo worktree,
# dispatches plan-reviewer as a sub-agent (the way the conductor does), and reports which skills
# it loaded, its findings and its PR notes. One reviewer pass instead of a whole conductor run.
#
# Usage: plan-review-test.sh <fixture plan.md> [note]   # prints the test directory first
set -uo pipefail
source "$(dirname "$0")/config.sh"

FIXTURE=${1:?fixture plan file}
TESTS="$HC_HOME/harness/plan-review/tests"; mkdir -p "$TESTS"
N=$(( $(ls -1d "$TESTS"/test-* 2>/dev/null | wc -l) + 1 ))
OUT="$TESTS/test-$(printf %02d "$N")"; mkdir -p "$OUT"
echo "$OUT"

PLAN_REL="6-Docs/plans/$(basename "$FIXTURE")"
WT="$HC_DEMO_REPO.worktrees/hc-plan-review-test-$N"
git -C "$HC_DEMO_REPO" worktree add -q --detach "$WT" HEAD || hc_die "worktree add failed"
cp "$FIXTURE" "$WT/$PLAN_REL"
printf '{"test": %d, "note": "%s", "fixture": "%s", "kyberWeaveVersion": "%s", "started": "%s"}\n' \
    "$N" "${2:-}" "$FIXTURE" "$(kyber-weave --version | awk '{print $2}')" "$(date -u +%FT%TZ)" >"$OUT/meta.json"

# The conductor's own dispatch text from run 8, pointed at this worktree.
DISPATCH="KYBER-ARBITER: true
PLAN_FILE: $PLAN_REL
Review this saved Draft in $WT, base $(git -C "$HC_DEMO_REPO" rev-parse --short HEAD). Autonomous test-first. Review only; return structured findings."

cd "$WT"
timeout 1200 copilot -p "Dispatch the plan-reviewer agent as a sub-agent with exactly the text between the markers as its prompt, then print its full reply verbatim and stop.
<<<
$DISPATCH
>>>" --model "${HC_TEST_MODEL:-gpt-6.1-terra}" --allow-all --no-ask-user --output-format json \
    >"$OUT/session.jsonl" 2>"$OUT/stderr.log"
echo "{\"exitCode\": $?}" >"$OUT/exit.json"
cd - >/dev/null
git -C "$HC_DEMO_REPO" worktree remove --force "$WT"

python3 - "$OUT" <<'EOF'
import json, re, sys
out = sys.argv[1]
ev = [json.loads(l) for l in open(f"{out}/session.jsonl") if l.strip()]
inside = False; skills = []; reads = []; reply = ""
for e in ev:
    t, d = e["type"], e.get("data") or {}
    if t == "subagent.started" and d.get("agentName") == "plan-reviewer": inside = True
    if t == "subagent.completed" and d.get("agentName") == "plan-reviewer": inside = False
    if inside and t == "tool.execution_start":
        a = json.dumps(d.get("arguments"))
        if d.get("toolName") == "skill": skills.append(d["arguments"].get("skill"))
        elif "SKILL.md" in a or "/lenses/" in a: reads.append(a[:120])
    if t == "assistant.message" and d.get("content"): reply = d["content"]
findings = re.findall(r"- id: (\S+)[\s\S]*?severity: (\w+)[\s\S]*?claim: ([^\n]+)", reply)
notes = re.findall(r"PR note: ([^\n]+)", reply)
status = re.findall(r"STATUS: \w+", reply)
r = {"skillsLoaded": skills, "skillFileReads": reads, "status": status[-1:] , "findings": [f"{i} {s} | {c[:140]}" for i, s, c in findings], "prNotes": [n[:160] for n in notes]}
json.dump(r, open(f"{out}/result.json", "w"), indent=1)
print(json.dumps(r, indent=1))
EOF
