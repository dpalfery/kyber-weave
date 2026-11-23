#!/usr/bin/env python3
"""Summarise one hill-climb run from Copilot's JSONL event stream into metrics JSON.

Usage: score-run.py <run-dir>   (reads session.jsonl, exit.json, meta.json; prints JSON)

Field names in Copilot's stream are read defensively: an unknown shape yields a null
metric, never a crash, so a run is always scorable.

Two sources feed the per-role and token blocks. Sessions up to Copilot CLI 1.0.114 carry
`agentMetrics` and `providerModelMetrics` in the last usage checkpoint. 1.0.115 carries
neither, so those blocks are built from the per-dispatch `subagent.completed` events
instead; `byAgentSource` and `tokensSource` name the source actually used.
"""
import collections
import json
import math
import pathlib
import sys

run = pathlib.Path(sys.argv[1])


def load(name, default=None):
    p = run / name
    try:
        return json.loads(p.read_text())
    except Exception:
        return default


def is_num(x):
    """Whether x is an event-stream number: a finite int or float, never a bool, string, NaN or Infinity."""
    return isinstance(x, (int, float)) and not isinstance(x, bool) and math.isfinite(x)


def num(x):
    """x when it is an event-stream number, otherwise 0."""
    return x if is_num(x) else 0


events = []
for line in (run / "session.jsonl").read_text(errors="replace").splitlines():
    try:
        events.append(json.loads(line))
    except ValueError:
        pass

nano_aiu = None
checkpoint = None
model_calls = collections.Counter()
tokens = collections.Counter()
tool_names = {}
tool_calls = collections.Counter()
tool_failures = collections.Counter()
errors = []
agent_names = {}
subagent_dispatches = []
final_answer = None
result = None

for e in events:
    if not isinstance(e, dict):
        continue
    data = e.get("data")
    if data is None:
        d = {}
    elif not isinstance(data, dict):
        continue
    else:
        d = data
    t = e.get("type")
    if t == "session.usage_checkpoint" and is_num(d.get("totalNanoAiu")):
        nano_aiu = max(nano_aiu or 0, d["totalNanoAiu"])
        checkpoint = d
    elif t == "model.call_start":
        model_calls[d.get("model", "?")] += 1
    elif t == "assistant.message":
        for r in d.get("toolRequests") or []:
            tool_names[r.get("toolCallId")] = r.get("name", "?")
        if d.get("phase") == "final_answer" and d.get("content"):
            final_answer = d["content"]
    elif t == "tool.execution_complete":
        name = tool_names.get(d.get("toolCallId"), d.get("toolName", "?"))
        tool_calls[name] += 1
        if d.get("success") is False:
            tool_failures[name] += 1
    elif t == "subagent.started" and e.get("agentId"):
        agent_names[e["agentId"]] = d.get("agentName", "?")
    elif t == "subagent.completed":
        subagent_dispatches.append(d)
    elif t == "session.error":
        errors.append(d.get("message"))
    elif t == "result":
        result = e



def find_key(node, key):
    """First value stored under `key` anywhere in a nested dict/list."""
    if isinstance(node, dict):
        if key in node:
            return node[key]
        node = list(node.values())
    if isinstance(node, list):
        for item in node:
            found = find_key(item, key)
            if found is not None:
                return found
    return None


# The last usage checkpoint is cumulative: tokens per model, and spend per agent.
for m in (checkpoint or {}).get("providerModelMetrics") or []:
    if not isinstance(m, dict):
        continue
    metrics = m.get("metrics")
    usage = metrics.get("usage") if isinstance(metrics, dict) else None
    if not isinstance(usage, dict):
        continue
    for k, v in usage.items():
        if is_num(v):
            tokens[k] += v

# Copilot CLI 1.0.115 reports spend and tokens per sub-agent dispatch, but no longer
# aggregates either per role in the checkpoint.
subagents = collections.defaultdict(
    lambda: {"dispatches": 0, "tokens": 0, "toolCalls": 0, "durationMs": 0})
for d in subagent_dispatches:
    row = subagents[d.get("agentName") or "?"]
    row["dispatches"] += 1
    row["tokens"] += num(d.get("totalTokens"))
    row["toolCalls"] += num(d.get("totalToolCalls"))
    row["durationMs"] += num(d.get("durationMs"))

tokens_source = "providerModelMetrics" if tokens else None
if not tokens and subagents:
    tokens = collections.Counter({"totalTokens": sum(r["tokens"] for r in subagents.values())})
    tokens_source = "subagent.completed"

# Spend per agent role (conductor = "main"), summed over every dispatch of that role.
agents = collections.defaultdict(lambda: {"dispatches": 0, "aiUnits": 0.0, "apiMinutes": 0.0})
agent_metrics = find_key(checkpoint or {}, "agentMetrics") or {}
if not isinstance(agent_metrics, dict):
    agent_metrics = {}
for agent_id, a in agent_metrics.items():
    if not isinstance(a, dict):
        continue
    row = agents["conductor" if agent_id == "main" else agent_names.get(agent_id, agent_id)]
    row["dispatches"] += 1
    row["aiUnits"] = round(row["aiUnits"] + num(a.get("totalNanoAiu")) / 1e9, 2)
    row["apiMinutes"] = round(row["apiMinutes"] + num(a.get("totalApiDurationMs")) / 60000, 1)
agents = dict(sorted(agents.items(), key=lambda kv: -kv[1]["aiUnits"]))
by_agent_source = "agentMetrics" if agents else None
if not agents and subagents:
    # Without a split of spend per dispatch, estimate it: the run's total AI units shared out
    # by each role's tokens. The conductor's own tokens are the checkpoint's model totals,
    # when it still reports them; with neither, the shares are unknowable and stay null.
    conductor_tokens = sum(tokens.values()) if tokens_source == "providerModelMetrics" else None
    split_tokens = sum(r["tokens"] for r in subagents.values()) + (conductor_tokens or 0)
    total_ai_units = None if nano_aiu is None else nano_aiu / 1e9

    def estimate(role_tokens):
        if not conductor_tokens or not split_tokens or total_ai_units is None:
            return None
        return round(total_ai_units * role_tokens / split_tokens, 2)

    agents = {
        name: {
            "dispatches": r["dispatches"],
            "tokens": r["tokens"],
            "toolCalls": r["toolCalls"],
            "minutes": round(r["durationMs"] / 60000, 1),
            "aiUnitsEstimate": estimate(r["tokens"]),
        }
        for name, r in subagents.items()
    }
    if conductor_tokens:
        agents["conductor"] = {
            "dispatches": 1,
            "tokens": conductor_tokens,
            "toolCalls": None,
            "minutes": None,
            "aiUnitsEstimate": estimate(conductor_tokens),
        }
    agents = dict(sorted(agents.items(), key=lambda kv: -kv[1]["tokens"]))
    by_agent_source = "subagent.completed"

exit_info = load("exit.json", {})
print(json.dumps({
    "run": (load("meta.json", {}) or {}).get("run"),
    "outcome": exit_info.get("outcome"),
    "exitCode": exit_info.get("exitCode"),
    "minutes": round(exit_info.get("seconds", 0) / 60, 1),
    "aiUnits": None if nano_aiu is None else round(nano_aiu / 1e9, 2),
    "tokens": dict(tokens) or None,
    "tokensSource": tokens_source,
    "byAgent": agents or None,
    "byAgentSource": by_agent_source,
    # More than one plan review round means planning and the reviewer disagree: a tuning signal.
    "planReviewRounds": sum(1 for n in agent_names.values() if n == "plan-reviewer"),
    "modelCalls": dict(model_calls),
    "toolCalls": sum(tool_calls.values()),
    "toolFailures": dict(tool_failures),
    "toolCallsByName": dict(tool_calls.most_common(15)),
    "sessionErrors": errors,
    "codeChanges": ((result or {}).get("usage") or {}).get("codeChanges"),
    "finalAnswerTail": (final_answer or "")[-2000:],
}, indent=2))
