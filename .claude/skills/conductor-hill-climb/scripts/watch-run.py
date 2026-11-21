"""Formats a conductor run's session.jsonl, read from stdin, as one readable line per event that matters."""
import json
import sys

COLOR = {"sub": "\033[36m", "ok": "\033[32m", "bad": "\033[31m", "msg": "\033[33m", "dim": "\033[2m"}
RESET = "\033[0m"


def out(kind, text, ts):
    print(f"{COLOR['dim']}{ts[11:19]}{RESET} {COLOR[kind]}{text}{RESET}", flush=True)


for line in sys.stdin:
    try:
        e = json.loads(line)
    except ValueError:
        continue
    t, d, ts = e.get("type"), e.get("data") or {}, e.get("timestamp", "")
    if t == "subagent.started":
        out("sub", f"> {d.get('agentName')}", ts)
    elif t == "subagent.completed":
        out("ok", f"done {d.get('agentName')}", ts)
    elif t == "subagent.failed":
        out("bad", f"FAILED {d.get('agentName')}", ts)
    elif t == "tool.execution_start" and d.get("toolName") in ("bash", "powershell"):
        cmd = str((d.get("arguments") or {}).get("command", "")).replace("\n", " ")
        out("dim", "  $ " + cmd[:150], ts)
    elif t == "tool.execution_start" and d.get("toolName") not in ("task", "report_intent"):
        args = d.get("arguments") or {}
        what = (args.get("path") or args.get("query") or args.get("pattern") or "") if isinstance(args, dict) else args
        out("dim", f"  . {d.get('toolName')} {str(what)[:110]}", ts)
    elif t == "tool.execution_complete":
        s = json.dumps(d)
        if "KW-ARB-" in s and ("deny" in s.lower() or "refus" in s.lower()):
            i = s.find("KW-ARB-")
            out("bad", "GUARD " + s[i:i + 150], ts)
    elif t == "assistant.message" and d.get("content") and not d.get("parentToolCallId"):
        out("msg", "* " + d["content"].strip().splitlines()[0][:180], ts)
    elif t in ("session.error", "error"):
        out("bad", "ERROR " + str(d)[:200], ts)
    elif t == "result":
        out("ok", f"== run ended, exit {e.get('exitCode')}", ts)
