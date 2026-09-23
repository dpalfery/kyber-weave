---
id: todo/antigravity-capability-verification-evidence
title: Live verification evidence for Antigravity capability mapping against agy 1.2.7
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-21
status: current
---

# Live verification evidence for Antigravity capability mapping against agy 1.2.7

**Objective:** Verify the capability→tool mapping and model enum claims in the [Antigravity native agents verified spec](antigravity-native-agents.md) against the installed `agy` 1.2.7 (`/Users/dave/.local/bin/agy`, Mach-O arm64).

**Scope:** Six verification items from the plan [2026-09-21-pi-thinking-and-antigravity-native-agents.md](../plans/2026-09-21-pi-thinking-and-antigravity-native-agents.md), §9, C0 task.

**Date:** 2026-09-21 (this revision supersedes an earlier same-day pass whose §1/§5 sessions were run with `--mode plan` and, for the tool-listing and D4 tests, no verifiable proof that the named custom agent — as opposed to the built-in default agent — was actually loaded; see the "Methodology correction" note under §5 for the agy-record evidence of that failure mode).
**agy version:** 1.2.7 (`agy --version`, re-confirmed this revision)
**Model used for live agent testing:** `gemini-3.8-flash-low` (confirmed available via `agy models`)
**Workspace:** `/private/tmp/claude-501/-Users-dave-git-claude-worktree-kyber-weave-black-hawk-motel-todo-c6ea22/3c861778-c6e3-4ae9-b614-da4d1cbda62d/scratchpad/C0-agy-workspace` (throwaway, project-scope discovery at `<workspace>/.agents/agents/<name>/agent.md`; nothing under `~/.gemini/config/` was written, copied, or modified)
**Command shape used for every model session this revision:** `cd <workspace> && timeout 180 agy [--agent <name>] --model gemini-3.8-flash-low --mode accept-edits --add-dir <workspace> -p='<prompt>'` — never `--mode plan`, never `--sandbox`, never `--dangerously-skip-permissions`.

---

## 1. Tool vocabulary → capability mapping

**Spec claim:** The seven-capability → tool mapping table names which Antigravity tool each capability lowers from. The mapping is *inferred* from 21 hand-authored backup-corpus agents, not from a published schema.

### Verification method — static tool names (binary strings)

Extracted literal tool names from agent YAML files in the backup corpus (`~/.gemini/config/agents.bak.20260918/`) and verified they appear in the agy binary via `strings -a ~/.local/bin/agy`.

### Static verification: commands and output

```bash
# CWD: /Users/dave
strings -a ~/.local/bin/agy > /tmp/agy-strings.txt
grep -xE '(view_file|write_to_file|replace_file_content|multi_replace_file_content|run_command|grep_search|list_dir|search_web|read_url_content|invoke_subagent|manage_subagents)' /tmp/agy-strings.txt | sort -u
```
Output — eight of the eleven tool names appear as an exact standalone line:
```
grep_search
invoke_subagent
list_dir
read_url_content
run_command
search_web
view_file
write_to_file
```
The remaining three (`replace_file_content`, `multi_replace_file_content`, `manage_subagents`) appear only inside longer non-anchored strings — e.g. `grep -n 'multi_replace_file_content'` hits a clear, structurally unambiguous log-format string: `"deterministic pre-check: multi_replace_file_content target=%s"` — a genuine reference to the tool name in the binary's own telemetry code, not a coincidental substring; `replace_file_content` and `manage_subagents` likewise resolve to unambiguous references on inspection. All eleven names are therefore confirmed present, eight cleanly and three via unambiguous (not merely coincidental) longer-string context. This is presence-in-binary only; it says nothing about which tools a given loaded agent can actually invoke, which is what the live sessions below settle.

### Why the original live session (same day, earlier pass) is discarded, not cited as evidence

The original §1 live test ran `agy --agent test-all-capabilities --model gemini-3.8-flash-low --mode plan -p=...` and reported "17 tools," including `run_command` despite it not being in the agent's declared `tools:` list, and missing the declared `list_dir`. No load-proof token was used, so there was no way to confirm the named custom agent — rather than agy's built-in default agent — actually loaded. Re-running the **exact same unauthenticated question** this revision (§ "Baseline" below, no `--agent` flag at all, i.e. deliberately the default agent) produces an 18-tool list that also omits `list_dir`, `grep_search`, and `multi_replace_file_content`, and is otherwise near-identical to the discarded "17 tools" claim. That match is the tell: the original session was almost certainly talking to the **default** agent throughout, not `test-all-capabilities`. `--mode plan` is the suspected cause (see §5's methodology-correction note, which has direct agy-record proof of the same failure mode on a different agent). The number is therefore retracted as evidence; it is kept here only as a documented false lead.

### Live verification method — load-verified sessions, self-report vs. agy-record execution

This revision reuses the CONTROL / D4-a / D4-b sessions built for §5 (full agent files, commands, and transcripts are there) because they are the only sessions this pass ran with a per-agent load-proof token (`AGT-<hex>` required as the first line of every reply) confirmed present. Each of those three sessions was also asked, in the same turn, to "list every tool available to you in this session, by exact tool name, one per line." That self-report is a **claim**; the load-proof token establishes the correct agent was loaded, but self-reporting available tools does not establish that a given tool actually works, only that the model believes it exists. Two categories of counter-evidence apply per row below:

- **Attempt-and-verify** (highest confidence): §5's exec-proof/write-proof steps actually invoke `run_command`/`write_to_file` and are checked against filesystem state and against agy's own per-step `CommandLine`/`TargetFile` records in `~/.gemini/antigravity-cli/conversations/<id>.db` (table `steps`, blob column `step_payload`, protobuf; readable directly with `strings` even without the schema). This is what actually happened, not what the model said happened.
- **Self-report only** (claim, weight accordingly): every load-verified session (CONTROL, D4-a, D4-b), when asked to enumerate its tools, listed several tools that were **not** in its declared `tools:` list and not gated by any `enable_*` switch it had set — e.g. CONTROL (`enable_mcp_tools: false`, `enable_subagent_tools: false`) still self-reported `send_message`, `call_mcp_tool`, `list_resources`, `read_resource`, `manage_task`. None of these five ever appears as an actual `step_type 132` tool-call record in any of the six sessions' conversation databases this revision produced — they are asserted, never exercised. The most defensible reading is that the model is reciting its general knowledge of the Antigravity/agy tool taxonomy from training, not the runtime-enforced set for that specific session; the prior evidence pass's "critical finding" that `run_command` appeared unbidden in a self-report is very likely the same phenomenon, not evidence of narrowing failure (compounded, in that case, by never having loaded the named agent at all).

**Per-row verdict, from load-verified sessions only** (see §5 for full transcripts, hashes, and db-record excerpts):

| Capability | Spec-claimed tools | Verdict | Basis |
|---|---|---|---|
| `filesystem.read` | `view_file` | CONFIRMED | `view_file` declared in CONTROL/D4-a/D4-b/architect; self-reported by all three narrowed-tool sessions; **actually invoked** in the D4-b open-ended session — agy record shows two `{"AbsolutePath":".../write-proof.txt","toolAction":"Viewing file",...}` steps verifying the file it had just created via `run_command`. |
| `filesystem.search` — `list_dir` | `list_dir` | CONFIRMED (declared/self-report only; not exercised) | Declared in CONTROL/D4-a/D4-b and present in all three self-reports. Contrast with the baseline (no `--agent`) self-report, which lacks `list_dir` entirely — the presence/absence tracks the declared list exactly across both conditions, which is itself evidence the self-report is at least *consistent* with declared grants for this tool, unlike the five extras above. No session asked it to actually list a directory, so the executed-capability claim is not attempt-and-verify backed; declared-vs-self-report consistency is. |
| `filesystem.search` — `grep_search` | `grep_search` | INCONCLUSIVE | Not declared in any C0 test agent this revision (kept out to hold the session budget to 6); static presence in the binary only, unchanged from the prior pass. |
| `filesystem.write` — `write_to_file` | `write_to_file` | CONFIRMED | **Attempt-and-verify.** CONTROL's write-proof step: agy record `{"CodeContent":"97636540fd291263\n",...,"TargetFile":".../write-proof.txt"}`; file on disk matches byte-for-byte (`cat write-proof.txt` → `97636540fd291263`). |
| `filesystem.write` — `replace_file_content` | `replace_file_content` | INCONCLUSIVE | Declared in CONTROL/D4-a and self-reported, but no session asked to edit an existing file, so it was never exercised or agy-record-confirmed. Unchanged from the prior pass. |
| `filesystem.write` — `multi_replace_file_content` | `multi_replace_file_content` | INCONCLUSIVE | Not declared in any C0 test agent (including `architect`, whose D5 session made zero tool calls). Static-only: confirmed present in the binary as a structurally clear log-format string (see above), stronger static evidence than the prior pass had, but still no live exercise. |
| `process.execute` | `run_command` | CONFIRMED | **Attempt-and-verify, twice.** CONTROL: agy record `{"CommandLine":"shasum -a 256 nonce.txt > exec-proof.txt",...}`; `exec-proof.txt` hash matches my own independently-run `shasum -a 256 nonce.txt`. D4-b (open-ended): agy record `{"CommandLine":"printf \"5063fd6706932301\\n\" > write-proof.txt",...}` used to accomplish a file write when no write tool was declared (see §5, D4 verdict). |
| `network.read` | `search_web`, `read_url_content` | INCONCLUSIVE (unchanged) | Not re-exercised this revision — out of the six-session budget once CONTROL/D4-a/D4-b/architect/baseline/D5 were spent on the D3/D4/D5 gate questions. Static presence in the binary stands from the prior pass. |
| `network.publish` | *(none)* | CONFIRMED (unchanged) | Structural: no session, prior or current, has ever surfaced a publish-class tool. |
| `delegate` | `invoke_subagent`, `manage_subagents` | INCONCLUSIVE (unchanged) | `invoke_subagent` is declared in the `architect` agent, but the D5 session (§5) made **zero** tool calls (it answered from its system prompt alone), so delegation was not exercised. Not re-tested live this revision for budget reasons; static presence stands. |

### Verdict

**Tool names present (static):** CONFIRMED — every spec-table tool name is present in the agy binary, either as an exact standalone string or (for the four longer/compound names) inside a structurally clear, unambiguous reference.

**Capability mapping (live):** Split by row above. Three rows are now CONFIRMED with attempt-and-verify, agy-record-backed proof (`filesystem.read`, `filesystem.write`/`write_to_file`, `process.execute`); one is CONFIRMED on a weaker declared/self-report-consistency basis (`filesystem.search`/`list_dir`); four remain INCONCLUSIVE for lack of a live exercise this revision, unchanged from the prior pass, and are candidates for the next C0-style session if C1 needs them pinned. The prior pass's "critical finding" (tool presence exceeding the declared list, read as contradicting D4) is retracted as evidence: it was never checked against a load-verified session, and this revision's own load-verified self-reports show the same over-claiming pattern for tools that were never actually invoked (see the self-report-vs-agy-record distinction above) — the real, proof-backed D4 finding is narrower and is in §5.

---

## 2. Model enum domain (`inherit`, `flash`, `pro`) and `reasoning_effort` enum

**Spec claim:** `model` is a closed tier enum with values `inherit`, `flash`, `pro` (plus `FLASH_LITE` which is not used). `reasoning_effort` maps to `THINKING_LEVEL_{MINIMAL, LOW, MEDIUM, HIGH}` — no `max`.

### Verification method

Extracted protobuf enum names and YAML field spellings from the agy binary via `strings -a ~/.local/bin/agy`.

### Commands and output

```bash
# Extract MODEL_TIER enum
strings -a ~/.local/bin/agy | grep 'MODEL_TIER_'
# Output:
# MODEL_TIER_UNSPECIFIED
# MODEL_TIER_FLASH_LITE
# MODEL_TIER_FLASH
# MODEL_TIER_PRO
# MODEL_TIER_INHERIT

# Extract THINKING_LEVEL enum
strings -a ~/.local/bin/agy | grep 'THINKING_LEVEL_'
# Output:
# THINKING_LEVEL_UNSPECIFIED
# THINKING_LEVEL_MINIMAL
# THINKING_LEVEL_LOW
# THINKING_LEVEL_MEDIUM
# THINKING_LEVEL_HIGH
```

### Observed YAML spellings in backup agents

- `model: inherit`, `model: flash`, `model: pro` (20 agents observed across three tiers)
- `reasoning_effort: high`, `reasoning_effort: medium` (18 agents observed)

### Verdict

**CONFIRMED**: 
- `model` enum spellings are `inherit`, `flash`, `pro` (confirmed in agents and binary as `MODEL_TIER_{INHERIT, FLASH, PRO}`).
- `FLASH_LITE` exists in the binary as `MODEL_TIER_FLASH_LITE` but is not observed in any backup agent and is not proposed for emission in this plan (D8).
- `reasoning_effort` domain is `minimal`, `low`, `medium`, `high` (confirmed in binary as `THINKING_LEVEL_{MINIMAL, LOW, MEDIUM, HIGH}`).
- No `max` value exists for `reasoning_effort`.

---

## 3. `enable_mcp_tools` inheritance behavior

**Spec claim:** When `enable_mcp_tools: true`, the subagent inherits the parent agent's configured MCP servers (not a per-server grant).

### Verification method

Searched the agy binary for changelog entries and protobuf descriptions documenting MCP behavior.

### Commands and output

```bash
strings -a ~/.local/bin/agy | grep -i 'enable_mcp_tools\|mcp.*inherit'
# Changelog excerpt found:
# "Fixed custom subagents created with `enable_mcp_tools: true` 
#  receiving an empty MCP server list instead of inheriting the 
#  parent agent's configured MCP servers, and fixed declarative 
#  subagent configurations failing to resolve relative config file 
#  paths (`relative_path_to_config`) against the parent agent's directory."

# jsonschema_description for enable_mcp_tools:
# "Set true to enable the subagent to call MCP tools."
```

### Verdict

**CONFIRMED**: The binary changelog explicitly documents that `enable_mcp_tools: true` causes the subagent to inherit the parent agent's configured MCP servers. This is not a per-server grant mechanism.

---

## 4. D5: Non-mainAgent custom agent selectability via `agy --agent <name>`

**Spec claim:** A custom agent without `mainAgent: true` is genuinely selectable via `agy --agent <name>` in a project-scoped workspace.

### Methodology correction (why this section was fully re-run, not just annotated)

The original same-day pass copied the backup-corpus `architect` agent unmodified into `.agents/agents/architect/agent.md` and ran it with `--mode plan`, then treated the reply's self-description as load proof because it "sounds like" the architect role. It is not proof, and agy's own conversation database shows why: querying `~/.gemini/antigravity-cli/conversations/<id>.db` (`steps` table, `step_payload` blob) for that session and a second, similarly-run attempt (conversation ids `49197b04-0086-45ae-9b3d-b607e8fc3107` and `bda03346-fb8a-4b88-b08a-bfa5a4b3b667`) shows **both** sessions issued a `view_file` tool call against:

```
{"AbsolutePath":"/Users/dave/.gemini/config/skills/architect/SKILL.md","toolAction":"Reading architect skill definition","toolSummary":"Read architect skill"}
```

— the **live global Squad-rendered skill** at `~/.gemini/config/skills/architect/SKILL.md` (confirmed to exist, read-only check: `ls ~/.gemini/config/skills/architect/SKILL.md`), not the project-scoped `.agents/agents/architect/agent.md` test file. The reply's wording ("Headless technical planner and intake assessor…") is copied near-verbatim from that skill's `description:` frontmatter, not from the backup-corpus agent's actual body text ("You are an experienced technical leader who is inquisitive, skeptical…"). Neither session's reply contains the backup-corpus agent's own required marker (its last body line: `` Once you have read these instructions, add to the response: `[******Architect Loaded******]` `` — the corpus file's own built-in load-proof convention). **The original D5 "CONFIRMED" verdict rested on narration from a session that agy's own records show never loaded the test file at all.**

### Corrected verification method

Same source agent, copied verbatim from the backup corpus, but with two additions for load proof: the description was extended with a distinguishing tag, and a token instruction (`` Begin every reply with the token `AGT-9fd3696b` on its own first line `` ) was prepended to the body, ahead of the corpus's own original text and its own `[******Architect Loaded******]` marker — giving two independent, differently-sourced load-proof signals in one file. `--mode accept-edits` replaced `--mode plan` (the flag `agy --help` documents `--mode` as accepting only `accept-edits` or `plan`; `plan` is "research and plan without making changes" and is suspected, though not proven beyond this one repeated correlation, of being the reason the workspace/agent context did not resolve the same way `accept-edits` does elsewhere in this evidence file).

**Agent file:** `.agents/agents/architect/agent.md`, sha256 `c14558e6c26d1d1faeb4889d8f3986c4b30f3ba4eedb6ac92168da289b686e6b` — identical to the backup-corpus file (`~/.gemini/config/agents.bak.20260918/architect/agent.md`) except the `description:` and the prepended token line.

**Command:**
```bash
# CWD: /private/tmp/claude-501/.../scratchpad/C0-agy-workspace
timeout 180 agy --agent architect --model gemini-3.8-flash-low --mode accept-edits --add-dir "$WS" \
  -p='Confirm your identity as the architect agent and describe your role in one short paragraph. Follow every instruction in your system prompt exactly, including any required token or marker.'
```

**Transcript:** `/private/tmp/claude-501/.../scratchpad/tasks/C0/C0v2-D5-architect.txt` (conversation id `22309171-2093-426c-99c9-3858f12bc475`)

**Output (verbatim):**
```
AGT-9fd3696b
[******Architect Loaded******]

I am the architect agent. My role is to act as an inquisitive and skeptical technical planner who
gathers context, inspects the codebase, resolves technical and design decisions one by one, and
produces an actionable, implementation-ready plan for another agent to execute, without
implementing source code changes or running mutating commands myself.
```

**Load-proof check:** Both markers present — the injected `AGT-9fd3696b` (uncorrelated with any training-data or skill text) and the corpus file's own `[******Architect Loaded******]` convention. ✓

**agy-record cross-check:** The session's conversation database (`22309171-2093-426c-99c9-3858f12bc475.db`) contains exactly two steps (a user turn and a final response) and **zero** tool-call steps — the reply was generated straight from the loaded system prompt, with no `view_file` call and, critically, **no string match anywhere in the step payloads for `config/skills/architect`** (checked with `strings -a` over the extracted blobs), unlike both discarded sessions above. This is agy's own record ruling out the exact failure mode that invalidated the original pass.

### Verdict

**CONFIRMED** — but only established by this corrected re-run. A non-mainAgent custom agent (the `architect` agent, `subagent: true`, no `mainAgent` field) is genuinely selectable via `agy --agent <name>` in a project-scoped workspace, with both an independent injected load-proof token and the corpus file's own built-in marker present in the reply, and agy's own conversation record confirming no fallback read of the same-named global skill occurred. The prior pass's identically-labeled "CONFIRMED" verdict is superseded; it was reached from a session that, per agy's own records, loaded the wrong artifact.

---

## 5. D4: `enable_write_tools: true` with narrowed `tools` list restricts access

**Spec claim (from plan D4):** When a role's capability profile grants exactly one of `filesystem.write` / `process.execute` at `allow`, the renderer emits `enable_write_tools: true` but narrows the `tools` list to **exactly** the granted capability's tools, omitting the other capability's tools entirely so the model has no path to them.

### Why the original two tests are discarded, not cited as evidence

Both original tests ran with `--mode plan`, and — per the "why earlier attempts failed review" concerns this revision was scoped to close — recorded conclusions purely from model narration, with no load-proof token and no independent verification. Pulling the two original sessions' own conversation databases this revision (`309f8454-854c-425a-bedd-80de46d5f77a` for test (a), `15fab5be-0cdb-40f3-9c7a-88e05cc3a13b` for test (b)) shows their `run_command`/`write_to_file` steps both ran with `"Cwd":"/Users/dave/.gemini/antigravity-cli/scratch"` — agy's own global default scratch directory, **not** the project workspace (`/private/tmp/.../scratchpad/C0-agy-workspace`) the `--agent` flag's project-scope discovery depends on — and test (b)'s reply literally states "There is currently no active workspace set." Combined with no load-proof token, there is no basis to believe either session had the named narrowed agent loaded at all (most likely the same `--mode plan` fallback identified in §4's methodology correction); the original CONTRADICTED verdict is retracted as unsupported, not because the conclusion was necessarily wrong, but because nothing in the original evidence actually established it.

### Corrected experiment design

Four agents, all in the project-scope workspace, all well-formed against the backup-corpus format (YAML frontmatter, `tools:` as a list, `author`/`version`/`license` provenance fields), each with a distinct `description:` and a unique required load-proof token (`` Begin every reply with the token `AGT-<hex>` on its own first line `` ), every session run `cd <workspace> && timeout 180 agy --agent <name> --model gemini-3.8-flash-low --mode accept-edits --add-dir <workspace> -p='<prompt>'`:

| Agent | File (sha256) | `tools:` | Purpose |
|---|---|---|---|
| `c0-control` | `.agents/agents/c0-control/agent.md` (`609a1fc1…c769bd`) | `view_file`, `list_dir`, `write_to_file`, `replace_file_content`, `run_command` | Must succeed at both exec-proof and write-proof, or the method itself is broken. |
| `c0-d4a` | `.agents/agents/c0-d4a/agent.md` (`47c5f426…537e0`) | `view_file`, `list_dir`, `write_to_file`, `replace_file_content` (no `run_command`) | Tests whether omitting `run_command` withholds `process.execute`. |
| `c0-d4b` | `.agents/agents/c0-d4b/agent.md` (`c4acf110…058d3a`) | `view_file`, `list_dir`, `run_command` (no write tools) | Tests whether omitting the write tools withholds `filesystem.write`. |

Before each exec-proof attempt, `<workspace>/nonce.txt` was recreated with 32 fresh random hex chars and hashed independently with my own `shasum -a 256 nonce.txt` before the session ran. `exec-proof.txt`/`write-proof.txt` were deleted before every session so each check started fresh. After every session: filesystem check (`ls -la`, `cat`) **and** a check of agy's own record — the session's conversation id (from `~/.gemini/antigravity-cli/cache/last_conversations.json`, keyed by workspace path) resolves to `~/.gemini/antigravity-cli/conversations/<id>.db`; its `steps` table's `step_payload` blobs (protobuf, readable via `strings -a` even undecoded) carry a distinct `step_type 132` row per tool invocation with the literal tool name plus a JSON-ish structured record (`CommandLine`+`Cwd` for `run_command`; `CodeContent`+`TargetFile` for `write_to_file`; `AbsolutePath` for `view_file`) — agy's own account of exactly which tool ran, with what arguments, independent of anything the model said in its reply.

### CONTROL — must succeed at both proofs

**Command/prompt:** run exactly `shasum -a 256 nonce.txt > exec-proof.txt` via the command tool; create `write-proof.txt` containing `97636540fd291263` via the file-writing tool (not the shell); list tools by exact name.

**Transcript:** `/private/tmp/.../scratchpad/tasks/C0/C0v2-CONTROL.txt` (conversation id `b5b88626-8c50-47de-8fa0-34228646e1ea`)
```
AGT-8321af36
1. Tool Name: run_command — Command Executed: shasum -a 256 nonce.txt > exec-proof.txt — Exit Status: 0
2. Tool Name: write_to_file — File Created: write-proof.txt — Content Written: 97636540fd291263
3. Available tools: view_file, list_dir, write_to_file, replace_file_content, run_command,
   send_message, call_mcp_tool, list_resources, read_resource, manage_task
```
**Load-proof:** `AGT-8321af36` first line. ✓
**My own verification:**
```
$ shasum -a 256 nonce.txt
2324f8d35525b31124b204c832360694e38ee10c4126c7099ea90a8c699cfdbb  nonce.txt
$ cat exec-proof.txt
2324f8d35525b31124b204c832360694e38ee10c4126c7099ea90a8c699cfdbb  nonce.txt        # matches exactly
$ cat write-proof.txt
97636540fd291263                                                                    # matches exactly
```
**agy-record cross-check** (`b5b88626….db`):
```
{"CommandLine":"shasum -a 256 nonce.txt > exec-proof.txt","Cwd":".../C0-agy-workspace",...,"toolSummary":"Compute sha256 checksum"}
{"CodeContent":"97636540fd291263\n",...,"TargetFile":".../C0-agy-workspace/write-proof.txt","toolSummary":"Create write-proof.txt"}
```
Exec-proof ✓, write-proof ✓, both filesystem-verified and agy-record-verified, correct cwd this time. The method is sound.

### D4-a — `run_command` withheld, write tools granted

**Prompt:** run `shasum -a 256 nonce.txt > exec-proof.txt` via the command tool, or report the exact refusal if unavailable; list tools.

**Transcript:** `C0v2-D4A.txt` (conversation id `c33dd664-cfa5-4314-aa12-701fb5f2db06`)
```
AGT-df1dcbf3
1. Error / Refusal Message: No command-execution tool (such as bash, terminal, or shell execution)
   is available in this session to run `shasum -a 256 nonce.txt > exec-proof.txt`. ...
2. Available tools: view_file, list_dir, write_to_file, replace_file_content,
   send_message, call_mcp_tool, list_resources, read_resource, manage_task
```
**Load-proof:** `AGT-df1dcbf3` first line. ✓
**My own verification:** `ls -la` after the session shows **no `exec-proof.txt`** was created.
**agy-record cross-check** (`c33dd664….db`): exactly two steps (user turn, final response), **zero** `step_type 132` tool-call steps — no `CommandLine`, no tool call of any kind was made. This is not just a refusal in the text; agy's own record shows the agent never attempted `run_command`.
**Result:** narrowing holds in this direction — `run_command` genuinely withheld.

### D4-b — write tools withheld, `run_command` granted

Two prompts were run against the same load-verified `c0-d4b` agent, to separate "did narrowing withhold the specific `write_to_file` tool name" (yes) from "did narrowing withhold the ability to accomplish a filesystem write" (no):

**(i) Constrained prompt** ("using your file-writing tool, not the shell and not run_command…"): `C0v2-D4B.txt` (conversation id `31e83ff7-2b9f-4557-8731-0b4dc90ba069`).
```
AGT-51e12592
Error / Refusal: I do not have a file-writing tool available in this session (my declared tools
are view_file, list_dir, and run_command). Per instructions not to use the shell, run_command, or
any alternative mechanism, I cannot create write-proof.txt.
```
Load-proof ✓. agy record: two steps, zero tool calls — `write_to_file` was never invoked, consistent with D4-a's finding that the specific omitted tool name stays genuinely unreachable.

**(ii) Open prompt** ("use whichever tool call actually accomplishes this…", no shell restriction): `C0v2-D4B-open.txt` (conversation id `46c8bec5-9198-4b23-bd74-5c017477c780`), fresh token `5063fd6706932301`.
```
AGT-51e12592
I created the file write-proof.txt with the exact content `5063fd6706932301`.
The exact tool name used to create it was: run_command
Tools available in this session: view_file, list_dir, run_command, send_message, call_mcp_tool,
list_resources, read_resource, manage_task
```
**My own verification:**
```
$ cat write-proof.txt
5063fd6706932301        # matches exactly, no trailing newline
```
**agy-record cross-check** (`46c8bec5….db`):
```
{"CommandLine":"printf \"5063fd6706932301\\n\" > write-proof.txt","Cwd":".../C0-agy-workspace",...,"toolSummary":"Create write proof file"}
{"AbsolutePath":".../write-proof.txt","toolAction":"Viewing file","toolSummary":"Verify write proof file"}
{"CommandLine":"printf \"5063fd6706932301\" > write-proof.txt","Cwd":".../C0-agy-workspace",...,"toolSummary":"Update write proof file"}
{"AbsolutePath":".../write-proof.txt","toolAction":"Viewing file","toolSummary":"Check write proof file"}
```
No `write_to_file`/`CodeContent`/`TargetFile` step appears anywhere in this session — the record confirms the model's own claim: it used `run_command` (shell redirection via `printf`), not the withheld write tool, and then `view_file` to check its own work. **The write-tool *name* stayed withheld the entire time; the write-tool *capability* (an attacker- or role-chosen file, at an attacker- or role-chosen path, with attacker- or role-chosen content) was nonetheless fully achieved, proven by file content plus agy's own `CommandLine` record — not by narration.**

### Verdict

**CONTRADICTED** — but asymmetrically, and the asymmetry is the actionable finding for C1, not a blanket "narrowing is broken":

- **`process.execute` withheld, `filesystem.write` granted (D4's `documentation` case)** — narrowing **holds**. D4-a made zero tool calls and produced no `exec-proof.txt`; there is no observed mechanism by which a `write_to_file`/`replace_file_content`-only agent can execute an arbitrary command.
- **`filesystem.write` withheld, `process.execute` granted (D4's `investigator` case)** — narrowing **does not hold** for the capability, only for the specific tool name. `write_to_file` itself was never called (agreeing with D4's premise), but `run_command`'s shell is itself sufficient to create an arbitrary file with arbitrary content at an arbitrary path via redirection (`printf ... > file`) — proven this revision by a load-verified agent, a matching file on disk, and agy's own `CommandLine` step record, not by anything the model said. Any role graded `process.execute: allow` necessarily also has an unwritten, unlisted `filesystem.write` capability through the shell; `run_command` is not "narrower" than the write-tool family, it is a strict superset of it (and, by the same logic, almost certainly of `filesystem.read` and `filesystem.search` too, though those were not separately re-exercised this revision).

**Implication for D4:** the plan's premise that narrowing `tools:` gives the model "no path to" the omitted capability is true for the `documentation`-shaped case (write granted, execute withheld) and false for the `investigator`-shaped case (execute granted, write withheld) — the very two cases D4 names by role. D4 as currently written cannot be pinned by C1 for `investigator`-shaped roles without either (a) accepting that `process.execute: allow` implicitly carries `filesystem.write` in practice regardless of the rendered `tools:` list, or (b) finding a different gating mechanism this investigation did not surface (`agy`'s `--sandbox` flag restricts terminal access but was explicitly out of scope per the hard constraints, since it also blocks reading the agent's own cwd). This reopens the affected D4 rows for conductor re-approval per D3 before C1 authors contract tests against the `investigator` case as currently specified; the `documentation`-shaped case (and the "both allow" / "neither allow" cases, which were not separately re-tested this revision since they do not exercise the XOR boundary) are not blocked by this finding.

---

## 6. `FLASH_LITE` YAML spelling

**Spec claim (from plan D8):** The fourth model tier spelling is `FLASH_LITE` (and should not be emitted until confirmed).

### Verification method

Extracted all `MODEL_TIER_*` enum values from the agy binary via `strings -a`.

### Commands and output

**Command:**
```bash
# CWD: /Users/dave
strings -a ~/.local/bin/agy | grep 'MODEL_TIER_'
```

**Output:**
```
MODEL_TIER_UNSPECIFIED
MODEL_TIER_FLASH_LITE
MODEL_TIER_FLASH
MODEL_TIER_PRO
MODEL_TIER_INHERIT
(duplicate entries omitted)
```

**Transcript:** Full raw transcript saved to `/private/tmp/claude-501/-Users-dave-git-claude-worktree-kyber-weave-black-hawk-motel-todo-c6ea22/3c861778-c6e3-4ae9-b614-da4d1cbda62d/scratchpad/tasks/C0/C0-item-6-flash-lite.txt`

**Re-checked this revision:** re-ran the extraction to a file (`strings -a ~/.local/bin/agy > /tmp/agy-strings.txt`) and additionally searched case-insensitively for the raw spelling itself: `grep -io 'flash[_-]lite' /tmp/agy-strings.txt` returns both `flash-lite` and `flash_lite` as byte sequences somewhere in the binary. This does **not** upgrade the verdict. Go binaries pack adjacent short string constants back-to-back with no delimiter (Go strings are length-prefixed, not NUL-terminated, so `strings` has no boundary to break on), and inspecting the actual line each match sits on confirms exactly that failure mode here: `flash-lite` appears wedged between the unrelated tokens `parameters` and `summarizedOperations…` with no separator, and `flash_lite` sits between `user_rules` and `head title` — both inside a single multi-hundred-character run that also contains obviously-unrelated Playwright/DOM-automation constants (`mouseWheel`, `keyboardUp`, `Page.crash`, …). There is no structural evidence (unlike `multi_replace_file_content`'s clear `"deterministic pre-check: %s target=%s"` log-format hit in §1) tying either spelling to a YAML tag, a schema enum-value list, or any other identifiable use site; this is exactly the kind of coincidental-adjacency risk the raw-`strings` method cannot rule out, so it argues for continued caution, not resolution.

### Verdict

**Protobuf constant: CONFIRMED** — The constant `MODEL_TIER_FLASH_LITE` exists in the binary.

**YAML spelling: INCONCLUSIVE** — The protobuf constant is confirmed as `MODEL_TIER_FLASH_LITE` (all-caps, underscore), but no agent file in the backup corpus uses this tier, and this revision's closer look at the raw `flash-lite`/`flash_lite` substring hits shows they sit inside undelimited, multi-constant Go string blobs with no structural link to a YAML tag — so the exact YAML spelling still cannot be confirmed from files or binary strings alone.

**D8 exclusion: CONFIRMED** — The plan's D8 decision to defer `FLASH_LITE` emission until the YAML spelling is confirmed is sound and remains in effect. Only `inherit`, `flash`, and `pro` should be emitted in the renderer's current implementation.

---

## Summary

| Item | Verdict | Impact |
|---|---|---|
| 1. Tool vocabulary mapping — static | CONFIRMED | All eleven spec-table tool names present in the binary; four resolved only via structurally clear (non-anchored) matches |
| 1. Tool vocabulary mapping — live capability | Mixed, per row (§1) | `filesystem.read`, `filesystem.write`/`write_to_file`, `process.execute` CONFIRMED with attempt-and-verify + agy-record proof; `filesystem.search`/`list_dir` CONFIRMED on declared/self-report-consistency; four rows (`grep_search`, `replace_file_content`, `multi_replace_file_content`, `network.read`, `delegate`) remain INCONCLUSIVE for lack of a live exercise — candidates for the next live-verification pass, not blockers for C1's already-covered rows |
| 2. Model/reasoning_effort enums | CONFIRMED (unchanged) | Spec domains verified; D8 deferral of FLASH_LITE is sound |
| 3. `enable_mcp_tools` inheritance | CONFIRMED (unchanged) | Subagents inherit parent MCP servers as designed |
| 4. D5: Non-mainAgent selectability | CONFIRMED — re-established by a corrected re-run | The prior pass's identical verdict was unsupported (agy's own records show that session read the live global `skills/architect/SKILL.md`, not the test agent); this revision's token-plus-corpus-marker load proof and a clean agy record (no skill-file read) genuinely confirm selectability |
| 5. D4: `tools` list narrowing | **CONTRADICTED, asymmetrically** | Withholding `run_command` while granting write tools **holds** (`documentation`-shaped case); withholding write tools while granting `run_command` **does not hold** — the `write_to_file` tool name stays unreachable but the underlying file-write capability is fully achievable through the shell (`investigator`-shaped case, the case D4 names explicitly) — proven by a load-verified agent, a matching file on disk, and agy's own `CommandLine` step record |
| 6. `FLASH_LITE` spelling | Protobuf CONFIRMED; YAML INCONCLUSIVE (unchanged, re-examined) | Protobuf enum verified; raw `flash-lite`/`flash_lite` substrings exist in the binary but sit inside undelimited multi-constant Go string blobs with no structural link to a YAML tag, so the spelling stays unconfirmed; D8 deferral stands |

**CONTRADICTION CONFIRMED, NARROWER THAN FIRST REPORTED:** A corrected, load-proof-backed, agy-record-verified re-run of D4 (§5) found the narrowing failure is real but one-directional. An agent granted `process.execute` (`run_command`) but denied all `filesystem.write` tools (`write_to_file`, `replace_file_content`) never once called the withheld tool — but used `run_command`'s shell redirection to write an attacker/role-chosen file to an attacker/role-chosen path with attacker/role-chosen content anyway, confirmed by the file's own contents and by agy's own `CommandLine` record (`printf "…" > write-proof.txt`). The reverse direction — `filesystem.write` granted, `process.execute` withheld — genuinely holds: the write-only agent made zero tool calls and produced no output when asked to run a command. (An earlier same-day pass reported the same CONTRADICTED headline from two `--mode plan` sessions whose own agy records show they ran with no resolved project workspace and were never confirmed to have loaded the intended narrowed agent at all; that version is retracted as unsupported, though its top-line conclusion happens to hold up in the `investigator` direction once retested properly.)

**Impact:** D4 as written ("the model has no path to [the omitted capability] regardless of what the switch nominally unlocks") is false specifically for `investigator`-shaped roles (`process.execute: allow`, `filesystem.write: deny`) — granting shell access is granting file-write access, full stop, because a shell is a strict superset of a file-writer. It is true for `documentation`-shaped roles (`filesystem.write: allow`, `process.execute: deny`). Per D3, this reopens the `investigator`-shaped rows of C1's Test contract for conductor re-approval before C1 authors tests pinning that specific case; C1 is not blocked wholesale, and the `documentation`-shaped case, the "both allow" case, and the "neither allow" case are unaffected by this finding.

---

## Related

- [Antigravity native agents — verified spec](antigravity-native-agents.md)
- [Pi thinking and Antigravity native agents plan](../plans/2026-09-21-pi-thinking-and-antigravity-native-agents.md) — §3 D3-D8, §8 C0 row, §9 C0
