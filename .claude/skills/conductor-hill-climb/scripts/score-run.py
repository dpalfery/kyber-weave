#!/usr/bin/env python3
"""Summarise one hill-climb run from Copilot's JSONL event stream into metrics JSON.

Usage: score-run.py <run-dir>   (reads session.jsonl, exit.json, meta.json; prints JSON)

Field names in Copilot's stream are read defensively: an unknown shape yields a null
metric, never a crash, so a run is always scorable.
"""
import collections
import json
import pathlib
import sys

run = pathlib.Path(sys.argv[1])


def load(name, default=None):
    p = run / name
    try:
        return json.loads(p.read_text())
    except Exception:
        return default


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
final_answer = None
result = None

for e in events:
    t, d = e.get("type"), e.get("data") or {}
    if t == "session.usage_checkpoint" and isinstance(d.get("totalNanoAiu"), (int, float)):
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
    for k, v in ((m.get("metrics") or {}).get("usage") or {}).items():
        if isinstance(v, (int, float)):
            tokens[k] += v
# Spend per agent role (conductor = "main"), summed over every dispatch of that role.
agents = collections.defaultdict(lambda: {"dispatches": 0, "aiUnits": 0.0, "apiMinutes": 0.0})
for agent_id, a in (find_key(checkpoint or {}, "agentMetrics") or {}).items():
    row = agents["conductor" if agent_id == "main" else agent_names.get(agent_id, agent_id)]
    row["dispatches"] += 1
    row["aiUnits"] = round(row["aiUnits"] + (a.get("totalNanoAiu") or 0) / 1e9, 2)
    row["apiMinutes"] = round(row["apiMinutes"] + (a.get("totalApiDurationMs") or 0) / 60000, 1)
agents = dict(sorted(agents.items(), key=lambda kv: -kv[1]["aiUnits"]))

exit_info = load("exit.json", {})
print(json.dumps({
    "run": (load("meta.json", {}) or {}).get("run"),
    "outcome": exit_info.get("outcome"),
    "exitCode": exit_info.get("exitCode"),
    "minutes": round(exit_info.get("seconds", 0) / 60, 1),
    "aiUnits": None if nano_aiu is None else round(nano_aiu / 1e9, 2),
    "tokens": dict(tokens) or None,
    "byAgent": agents or None,
    "modelCalls": dict(model_calls),
    "toolCalls": sum(tool_calls.values()),
    "toolFailures": dict(tool_failures),
    "toolCallsByName": dict(tool_calls.most_common(15)),
    "sessionErrors": errors,
    "codeChanges": ((result or {}).get("usage") or {}).get("codeChanges"),
    "finalAnswerTail": (final_answer or "")[-2000:],
}, indent=2))
