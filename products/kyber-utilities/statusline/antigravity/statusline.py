#!/usr/bin/env python3
"""Antigravity CLI (`agy`) status line.

Renders one bar on stdout:
    [AGY] 📁 repo │ 🌿 branch │ 🤖 model │ ⚡ total (sys tls skl rul msg) │ ⏳ 5h quota%

Telemetry: the raw stdin payload is handed, byte-for-byte, to
`kyberdash kyber antigravity-statusline` as a fire-and-forget background
hand-off (D10/C8). The recorder owns ingest; this script opens no sockets,
writes no files, and never waits on the hand-off. When kyberdash is not on
PATH the hand-off is skipped silently and the bar still renders.
"""

import json
import os
import shutil
import subprocess
import sys
import time

from datetime import datetime


def resolve_config_dir():
    """The agy config directory, derived at run time — never a baked-in home path.

    Resolution order mirrors the deployment contract: the AGY_CONFIG_DIR
    override wins, then the conventional ~/.gemini/antigravity-cli under the
    real HOME, then the script's own directory as the last resort so a staged
    copy still finds its neighbours.
    """
    override = os.environ.get("AGY_CONFIG_DIR")
    if override:
        return override
    home = os.environ.get("HOME")
    if home:
        return os.path.join(home, ".gemini", "antigravity-cli")
    return os.path.dirname(os.path.abspath(__file__))


def parse_reset_in_seconds(quota_dict):
    if not isinstance(quota_dict, dict):
        return None
    if "reset_in_seconds" in quota_dict:
        try:
            return float(quota_dict["reset_in_seconds"])
        except Exception:
            pass
    reset_val = quota_dict.get("reset_time") or quota_dict.get("reset_at") or quota_dict.get("reset")
    if reset_val is not None:
        try:
            val_float = float(reset_val)
            now = time.time()
            return val_float - now if val_float > now else 0
        except (ValueError, TypeError):
            if isinstance(reset_val, str):
                try:
                    dt = datetime.fromisoformat(reset_val.replace("Z", "+00:00"))
                    now = time.time()
                    return dt.timestamp() - now
                except Exception:
                    pass
    return None


def format_duration(seconds):
    if seconds is None:
        return ""
    total_sec = max(0, int(seconds))
    hrs = total_sec // 3600
    mins = (total_sec % 3600) // 60
    if hrs > 0:
        return f"{hrs}h {mins}m"
    return f"{mins}m"


def get_git_branch(cwd):
    """Best-effort branch lookup. `cwd` is only passed to git when it actually
    exists — subprocess chdirs into it, so a stale or synthetic path would
    fail the spawn, not just the branch read. None means git runs where the
    status line itself was launched, which for agy is the session directory.
    """
    try:
        branch = subprocess.check_output(
            ["git", "rev-parse", "--abbrev-ref", "HEAD"],
            cwd=cwd, stderr=subprocess.DEVNULL
        ).decode().strip()
        return branch
    except Exception:
        return ""


def extract_model_id(val):
    if not val:
        return ""
    if isinstance(val, dict):
        # display_name is the human-readable label the payload provides; the
        # bare id stays the fallback, not the other way around.
        res = (
            val.get("display_name") or
            val.get("displayName") or
            val.get("name") or
            val.get("id") or
            val.get("modelId") or
            val.get("model_id") or
            val.get("model")
        )
        if res and res != val:
            return extract_model_id(res)
        return str(val)
    if isinstance(val, str):
        val_str = val.strip()
        if val_str.startswith("{") and val_str.endswith("}"):
            try:
                parsed = json.loads(val_str)
                if isinstance(parsed, dict):
                    return extract_model_id(parsed)
            except Exception:
                pass
        return val_str
    return str(val)


def fmt_k(val):
    if val is None:
        return None
    try:
        n = float(val)
        if n >= 1000:
            return f"{n/1000:.1f}k"
        return f"{int(n)}"
    except Exception:
        return str(val)


def fmt_total(val):
    """The headline token count stays exact below five figures; k-notation is
    only worth it once the number is too wide to read at a glance."""
    if val is None:
        return None
    try:
        n = float(val)
        if n >= 10000:
            return f"{n/1000:.1f}k"
        return f"{int(n)}"
    except Exception:
        return str(val)


def hand_off_to_kyberdash(raw):
    """Pipe the raw stdin payload to `kyberdash kyber antigravity-statusline`.

    Fire-and-forget: the child is spawned detached and never waited on, so the
    status line returns while the recorder finishes in the background. A
    missing kyberdash, a spawn failure, or a closed pipe are all swallowed —
    the hand-off is optional by contract (C8/C9) and must never delay or
    corrupt the rendered bar.
    """
    exe = shutil.which("kyberdash")
    if not exe:
        return
    try:
        proc = subprocess.Popen(
            [exe, "kyber", "antigravity-statusline"],
            stdin=subprocess.PIPE,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            start_new_session=True,
        )
    except Exception:
        return
    try:
        proc.stdin.write(raw)
    except Exception:
        pass
    try:
        proc.stdin.close()
    except Exception:
        pass
    # Deliberately no proc.wait(): returning first is the whole point.


def main():
    try:
        raw = sys.stdin.buffer.read()
    except Exception:
        raw = b""
    try:
        data = json.loads(raw) if raw.strip() else {}
        if not isinstance(data, dict):
            data = {}
    except Exception:
        data = {}

    # The recorder gets exactly what agy sent, no re-shaping.
    hand_off_to_kyberdash(raw)

    # Working directory: top-level `cwd`/`working_directory`, else the
    # workspace object the published payload carries, else our own launch dir.
    workspace = data.get("workspace")
    workspace_dir = None
    if isinstance(workspace, dict):
        workspace_dir = workspace.get("current_dir") or workspace.get("project_dir")
    elif isinstance(workspace, str):
        workspace_dir = workspace
    cwd = data.get("cwd") or data.get("working_directory") or workspace_dir or os.getcwd()
    if not isinstance(cwd, str):
        cwd = os.getcwd()
    repo = os.path.basename(os.path.abspath(cwd)) or cwd

    # Branch: the payload's vcs object is the schema's source; git is the
    # fallback for payloads that do not carry it.
    vcs = data.get("vcs")
    branch = vcs.get("branch") if isinstance(vcs, dict) else None
    if not branch:
        branch = get_git_branch(cwd if os.path.isdir(cwd) else None)

    raw_model = (
        data.get("model") or data.get("modelName") or data.get("model_name") or
        data.get("modelId") or data.get("model_id") or ""
    )
    model = extract_model_id(raw_model)

    # ANSI color codes
    AGY_BADGE = "\033[1;37;44m AGY \033[0m"
    CYAN = "\033[1;36m"
    GREEN = "\033[1;32m"
    YELLOW = "\033[1;33m"
    MAGENTA = "\033[1;35m"
    BLUE = "\033[1;34m"
    GRAY = "\033[38;5;244m"
    RESET = "\033[0m"

    parts = [AGY_BADGE]

    # Repo / Directory
    parts.append(f"{CYAN}📁 {repo}{RESET}")
    if branch:
        parts.append(f"{GREEN}🌿 {branch}{RESET}")
    if model:
        parts.append(f"{MAGENTA}🤖 {model}{RESET}")

    # Breakdown Tokens Parsing
    sys_tok = (
        data.get("system_tokens") or data.get("sys_tokens") or
        data.get("systemTokens") or data.get("sysTokens") or
        data.get("system_prompt_tokens")
    )
    tool_tok = (
        data.get("tool_tokens") or data.get("tools_tokens") or
        data.get("toolTokens") or data.get("toolsTokens")
    )
    skill_tok = (
        data.get("skill_tokens") or data.get("skills_tokens") or
        data.get("skillTokens") or data.get("skillsTokens")
    )
    rule_tok = (
        data.get("rule_tokens") or data.get("rules_tokens") or
        data.get("ruleTokens") or data.get("rulesTokens") or
        data.get("agents_md_tokens")
    )
    msg_tok = (
        data.get("user_tokens") or data.get("message_tokens") or
        data.get("msg_tokens") or data.get("prompt_tokens") or
        data.get("userTokens") or data.get("msgTokens")
    )

    breakdown = (
        data.get("breakdown") or data.get("context_breakdown") or
        data.get("token_breakdown") or data.get("details") or {}
    )
    if isinstance(breakdown, dict):
        if not sys_tok: sys_tok = breakdown.get("sys") or breakdown.get("system")
        if not tool_tok: tool_tok = breakdown.get("tools") or breakdown.get("tool")
        if not skill_tok: skill_tok = breakdown.get("skills") or breakdown.get("skill")
        if not rule_tok: rule_tok = breakdown.get("rules") or breakdown.get("agents_md")
        if not msg_tok: msg_tok = breakdown.get("msg") or breakdown.get("user") or breakdown.get("message")

    # Missing is missing: an unreported section renders "--", never a
    # fabricated count.
    s_s = fmt_k(sys_tok) or "--"
    t_s = fmt_k(tool_tok) or "--"
    k_s = fmt_k(skill_tok) or "--"
    r_s = fmt_k(rule_tok) or "--"
    m_s = fmt_k(msg_tok) or "--"

    total_tokens = (
        data.get("total_tokens") or data.get("totalTokens") or
        data.get("tokens") or data.get("token_count") or data.get("tokenCount")
    )
    context_win = data.get("context_window") or {}
    if not total_tokens and isinstance(context_win, dict):
        inp = context_win.get("total_input_tokens") or 0
        outp = context_win.get("total_output_tokens") or 0
        if inp or outp:
            total_tokens = inp + outp
    if not total_tokens and isinstance(context_win, dict):
        # No session totals: current_usage.input_tokens is the context load
        # agy reported for the last call — the closest honest figure to show.
        usage = context_win.get("current_usage")
        if isinstance(usage, dict):
            cand = usage.get("input_tokens")
            if cand:
                total_tokens = cand

    tot_s = fmt_total(total_tokens) or "--"

    token_section = f"{YELLOW}⚡ {tot_s}{RESET} {GRAY}(sys:{s_s} tls:{t_s} skl:{k_s} rul:{r_s} msg:{m_s}){RESET}"
    parts.append(token_section)

    # 5-Hour Quota Window Percentage & Reset Time Extraction
    window_pct = None
    reset_in_sec = None
    quota_data = data.get("quota") or data.get("quota_remaining") or data.get("rate_limit") or data.get("limits") or {}

    is_3p_model = any(k in str(model).lower() for k in ["claude", "gpt", "o1", "o3", "llama", "deepseek", "mistral", "3p"])
    keys_to_check = ["3p-5h", "gemini-5h"] if is_3p_model else ["gemini-5h", "3p-5h"]

    if isinstance(quota_data, dict):
        selected_quota = None
        for k in keys_to_check:
            if k in quota_data and isinstance(quota_data[k], dict):
                selected_quota = quota_data[k]
                break
        if not selected_quota:
            for k, v in quota_data.items():
                if isinstance(v, dict) and ("remaining_fraction" in v or "reset_in_seconds" in v or "reset_time" in v):
                    selected_quota = v
                    break

        if selected_quota:
            if "remaining_fraction" in selected_quota:
                window_pct = float(selected_quota["remaining_fraction"]) * 100.0
            reset_in_sec = parse_reset_in_seconds(selected_quota)

        if window_pct is None:
            window_pct = (
                quota_data.get("window_remaining_percent") or quota_data.get("five_hour_percent") or
                quota_data.get("remaining_percent") or quota_data.get("percent_remaining") or
                quota_data.get("percent") or quota_data.get("remaining")
            )
        if reset_in_sec is None:
            reset_in_sec = parse_reset_in_seconds(quota_data)

        if window_pct is None and reset_in_sec is not None and reset_in_sec > 0:
            window_pct = min(100.0, max(0.0, (reset_in_sec / (5 * 3600)) * 100.0))
    elif isinstance(quota_data, (int, float)):
        window_pct = quota_data

    if window_pct is None:
        window_pct = (
            data.get("five_hour_percent") or data.get("window_percent") or
            data.get("quota_percent") or data.get("quota_remaining_percent")
        )
    if reset_in_sec is None:
        reset_in_sec = parse_reset_in_seconds(data)

    time_str = format_duration(reset_in_sec)
    time_suffix = f" ({time_str})" if time_str else ""

    if window_pct is not None:
        try:
            val = float(window_pct)
            if val > 50: color = GREEN
            elif val > 20: color = YELLOW
            else: color = "\033[1;31m"
            parts.append(f"{color}⏳ 5h: {val:.0f}%{time_suffix}{RESET}")
        except Exception:
            parts.append(f"{BLUE}⏳ 5h: {window_pct}{time_suffix}{RESET}")
    else:
        parts.append(f"{BLUE}⏳ 5h: --%{time_suffix}{RESET}")

    print(" │ ".join(parts))


if __name__ == "__main__":
    main()
