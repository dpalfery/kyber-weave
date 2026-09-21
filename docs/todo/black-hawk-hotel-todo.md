---
id: todo/black-hawk-hotel-todo
title: Black Hawk Hotel — handover of in-flight harness fidelity work
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-21
status: draft
---

# Black Hawk Hotel — handover of in-flight harness fidelity work

This is a **handover, not a plan**. It records what was delivered, what was found but not
built, and what the next agent needs in order to start cold. Three tasks are open; task C is
the large one.

Everything here was verified against a live harness or its installed source on **2026-09-21**.
Re-verify before treating any of it as current — two of the three findings exist precisely
because a previously verified fact went stale.

---

## 1. What is already delivered

Branch `claude/zcode-harness-subagents-e2fff4`, three commits, all 14 declared gates passing.

| Commit | What |
|---|---|
| `65e7cf37` | ZCode added as the eleventh harness target (`ZCodeRenderer`, native subagents + skills, conductor lowered to a slash command) |
| `a56e679e` | Closed a ZCode tool-list widening trap; added the `storage.dir` config tier; lossless block-scalar skill descriptions |
| `ac6a6f90` | Pinned every OpenCode permission key, because an omitted key inherits OpenCode's default-allow |

See [the ZCode plan](../plans/2026-09-21-zcode-harness-target.md) and
[ADR 0020](../adr/0020-zcode-command-lowering-and-resource-relocation.md).

### The machine state this work left behind

A global deployment of **nine targets** was installed from this working tree: `claude`,
`cursor`, `antigravity`, `zcode`, `opencode`, `kilo`, `copilot`, `codex`, `pi` — 1017 files
under one receipt. `squad status --global` reports every file matching.

Three older global receipts were uninstalled first (they were installed from
`/Users/dave/git/personal/kyber-weave`, `/Users/dave`, and `/Users/dave/.gemini`). All 565 of
their files were verified byte-identical to their recorded hashes before removal, so nothing
hand-edited was lost.

**42 pre-existing unmanaged files** were backed up to
`~/.kyber-weave-preinstall-backup/20260921-171012/` before being replaced — 20 Pi agents, 2 Pi
skills, 20 OpenCode agents. They are relevant to task C: they are the evidence for what the
previous generation of tooling emitted.

### Deploying from a working tree

`squad install` renders locally but downloads canonical *source* from a GitHub release matching
the CLI's own assembly version, so a working-tree deployment needs the local release loop:

```bash
./scripts/release-local.sh --version 0.1.0 --out .local-release
python3 scripts/local-release-server.py --root .local-release   # prints a loopback port
KYBER_WEAVE_RELEASE_ORIGIN=http://127.0.0.1:<port> \
  dotnet run --project src/KyberWeave.Cli --no-build -c Release -- \
  squad install --global --target <targets>
```

`KYBER_WEAVE_RELEASE_ORIGIN` accepts loopback authorities only. The version must match
`GetAssemblyVersion()` (currently `0.1.0`), because the release asset name is
`kyber-squad-<version>.zip` and the version is not overridable by design.

---

## 2. Task A — emit `thinking` for Pi

**Status: designed, not built. The working tree is clean; a half-finished edit was reverted
deliberately so the next agent starts from a consistent state.**

### Why

`PiRenderer` emits no reasoning-effort key. Pi supports one. The previously deployed Pi files
carried `reasoningEffort: high`, but that key is inert on Pi (see task B), so the intent was
never actually reaching the harness.

### The verified facts

`@tintinweb/pi-subagents` **0.19.0**, installed at
`~/.pi/agent/npm/node_modules/@tintinweb/pi-subagents`, maps frontmatter in
`src/custom-agents.ts`:

```ts
thinking: str(fm.thinking) as ThinkingLevel | undefined,
```

`ThinkingLevel` is re-exported from `@earendil-works/pi-ai`. The accepted values observed in
the package are `off`, `minimal`, `low`, `medium`, `high`, `max`.

### The design that was chosen

`models.yml` holds **one opaque string per harness**, and each renderer owns how it reads its
own. Cursor already carries bracket options (`gpt-5.6-sol[context=272k,reasoning=high,fast=false]`)
and `CursorRenderer` passes the whole string through verbatim, because Cursor parses the
brackets itself. Pi needs a *separate frontmatter key*, so `PiRenderer` must split the string
rather than pass it through.

So: append an optional `[thinking=<level>]` suffix to the `pi:` value, and have `PiRenderer`
parse it off.

The reverted `models.yml` edit was exactly:

| Profile | `pi:` value |
|---|---|
| `deep-planning` | `zai/glm-5.3[thinking=high]` |
| `reviewer` | `opencode-go/kimi-k2.7-code[thinking=high]` |
| `general` | `opencode/muse-spark-1.3-contributor-free[thinking=medium]` |
| `fast` | `opencode/muse-spark-1.3-contributor-free[thinking=low]` |
| `orchestration` | `inherit` — left alone; the conductor lowers to a Pi skill, which carries no frontmatter model |

Those levels are a proposal, not a requirement from the owner. Confirm them before building.

### Why the half-finished state was dangerous

`PiRenderer.ResolvePiModel` returns the harness string **verbatim**
(`src/KyberWeave.Core/Squad/Rendering/PiRenderer.cs:355-358`). With `models.yml` edited and the
renderer untouched, Pi would receive `model: zai/glm-5.3[thinking=high]` as a literal model id
and fail to resolve it. Land both halves together, or neither.

### What to build

1. `PiRenderer.ResolvePiModel`: split an optional trailing `[thinking=<level>]`, returning the
   bare model id and the level separately.
2. Validate the level against the six accepted values and **fail closed** on anything else —
   an unknown level silently ignored by Pi is the failure mode this renderer already avoids
   elsewhere.
3. Emit `thinking:` only when a level is present. `thinking` is in the accepted key list
   documented in `PiRenderer`'s own remarks, so no other renderer contract changes.
4. `PiRendererContractTests`: pin the split, the six-value domain, the fail-closed path, and
   that a model with no suffix still emits `model` and no `thinking`.
5. Update the `PiRenderer` remarks — they currently list `thinking` among the accepted keys
   this renderer does *not* emit.

---

## 3. Task B — Pi `model` and permissions

**Status: answered, no work required. Recorded so it is not re-investigated.**

**`model`: already emitted.** `PiRenderer` resolves the `pi` harness override and falls back to
the target-neutral `default`, omitting the key when either is `inherit`. Rendered output
carries `model: zai/glm-5.3` today.

**Permissions: already expressed, and they are enforced.** Pi's permission surface is the
`tools` frontmatter key — a closed seven-name vocabulary
(`read, grep, find, ls, edit, write, bash`) that `PiRenderer` always emits, plus
`extensions: false`. There is no separate permission map. Omitting `tools` grants every
built-in, which is why the renderer never omits it.

**The `permission:` block in the previously deployed Pi files was inert.** `custom-agents.ts`
never reads `fm.permission`, `fm.mode`, or `fm.reasoningEffort`; `reasoningEffort` appears zero
times in the whole package. Those files were OpenCode-format agents sitting in Pi's directory —
near-identical to the OpenCode ones, differing only in the model id. They are preserved in the
backup named above if anyone wants to confirm.

The practical consequence: a `permission:` block on a Pi agent looks like it denies things and
does not. `tools` is the only thing Pi enforces.

---

## 4. Task C — Antigravity has native agents; the renderer still lowers every role to a skill

**Status: the largest open item. Verified enough to design; two questions still block a
complete implementation.**

This restates and extends `docs/todo/antigravity-native-agents.md`, which was written
2026-09-18 and **lives only on the `claude/kyberdash-context-surfaces-spec-d5775e` branch
(PR #96)** — it is not on `main`, so it is referenced here by path rather than linked. Fold the
two together when that branch lands.

### The gap

`AntigravityRenderer` is a fallback renderer: every canonical role becomes a skill under
`skills/`, `role-`-prefixed on a name clash. `SquadGlobalRoots` records Antigravity as
`skills/` under `~/.gemini/config/`, "no override, no agent primitive". The renderer registry
asserts that a fallback target emits no `/agents/` path at all.

That was true when verified. It is not true now. The consequence is that a delegated
Antigravity session cannot load a Squad role *as an agent* — it can only be told to follow the
role's skill. The conductor suffers most, since it exists to spawn specialists.

### Verified on 2026-09-21

`agy` **1.2.7** at `~/.local/bin/agy`:

- `agy agent` and `agy agents` — "List available agents".
- `--agent <name>` — "Agent for the current CLI session".
- `--mode` — `accept-edits` or `plan`.

Agents live at **`~/.gemini/config/agents/<name>/agent.md`** — one directory per agent, holding
a single `agent.md`. That directory is currently empty on this machine; 21 hand-authored agents
were moved aside to `~/.gemini/config/agents.bak.20260918/` and remain there. They are the
format evidence below.

### The agent file format

Frontmatter key frequency across those 21 agents:

| Key | Count | Notes |
|---|---|---|
| `name` | 23 | |
| `description` | 23 | |
| `tools` | 20 | YAML list |
| `model` | 20 | `flash` (13), `inherit` (4), `pro` (3) |
| `subagent` | 19 | bool |
| `enable_write_tools` | 19 | bool |
| `enable_subagent_tools` | 19 | bool |
| `enable_mcp_tools` | 19 | bool |
| `reasoning_effort` | 18 | `high` (11), `medium` (7) |
| `author` / `version` / `license` | 18 | provenance, no functional effect |
| `mainAgent` | 1 | camelCase, unlike every other key; only on the orchestrator |

The orchestrator (`conductor-v3`) is the shape to copy for a primary-invocation agent:

```yaml
name: conductor-v3
subagent: false
mainAgent: true
model: inherit
enable_write_tools: true
enable_subagent_tools: false
enable_mcp_tools: true
tools:
  - list_dir
  - invoke_subagent
  - manage_subagents
```

Note `enable_subagent_tools: false` on the very agent whose `tools` list contains
`invoke_subagent` and `manage_subagents`. Whether the switch gates the tools or is independent
of them is one of the open questions below — do not guess.

### The tool vocabulary

Observed across the 21 agents, with frequency:

`view_file` (18), `list_dir` (16), `grep_search` (16), `write_to_file` (14), `run_command` (14),
`replace_file_content` (14), `multi_replace_file_content` (14), `search_web` (8),
`read_url_content` (8), `invoke_subagent` (6), `manage_subagents` (2).

It maps onto the canonical capability vocabulary more cleanly than most targets do:

| Capability | Proposed Antigravity tools |
|---|---|
| `filesystem.read` | `view_file` |
| `filesystem.search` | `list_dir`, `grep_search` |
| `filesystem.write` | `write_to_file`, `replace_file_content`, `multi_replace_file_content` |
| `process.execute` | `run_command` |
| `network.read` | `search_web`, `read_url_content` |
| `network.publish` | *(none — record `permission-not-expressible`)* |
| `delegate` | `invoke_subagent`; `manage_subagents` for the orchestrator only |

**This mapping is a proposal derived from hand-authored files, not from a published schema.**
It is the weakest link in this document. Treat it as a hypothesis to verify, not a contract.

### What still blocks a complete implementation

1. **The project-level agents path is unknown.** The global path is confirmed; a renderer
   emits scope-relative paths and needs both. `agy agent` prints nothing even with agents
   present, so it could not be probed from the CLI. A probe directory containing
   `.gemini/config/agents/<name>/agent.md` produced no listing either. Until this is settled,
   a native implementation is only correct under `--global`.
2. **Whether `enable_*` switches gate the `tools` list or are independent.** This decides
   whether the capability lowering is expressible at all. The orchestrator example above is
   direct evidence that the two can disagree.
3. **Whether the IDE and the CLI read the same directory.** The machine carries both
   `~/.gemini/antigravity-ide` and `~/.gemini/antigravity-cli` state.
4. **Whether `model` accepts arbitrary ids or only the observed `inherit`/`flash`/`pro`.**
   `models.yml` has no `antigravity` column today; one would be needed, and its value domain
   depends on this.

### The code seam

- `src/KyberWeave.Core/Squad/Rendering/AntigravityRenderer.cs` — moves from fallback to native.
- `src/KyberWeave.Core/Squad/Rendering/SquadRendererRegistry.cs` — `isNative` currently excludes
  Antigravity, and the fallback branch *asserts* that such a target emits no `/agents/` path and
  that every role-skill collision produces a `role-`-prefixed pair. Both assertions invert.
  `AgentOutputPath` and `SkillOutputPath` both need Antigravity branches; note the output is a
  **directory plus `agent.md`**, a shape no existing target uses.
- `src/KyberWeave.Core/Squad/Deployment/SquadGlobalRoots.cs` — the remarks state "no agent
  primitive" for Antigravity and must be corrected.
- `products/kyber-squad/profiles/models.yml` and its schema — an `antigravity` column, pending
  question 4.
- Docs: [architecture §8](../kyber-squad/architecture.md#8-rendering) rendering table,
  [requirements](../kyber-squad/requirements.md) target matrix,
  [renderer coverage](kyber-squad-renderer-coverage.md), and an ADR — this reclassifies a
  target, which is exactly the kind of decision
  [ADR 0020](../adr/0020-zcode-command-lowering-and-resource-relocation.md) records for ZCode.

### Sequencing note

Antigravity is currently deployed *as skills* on this machine and working. There is no outage
to fix, so this can be done carefully. When it lands, the existing global receipt will need an
uninstall/reinstall because the target's file shape changes.

---

## Related

- [Renderer coverage](kyber-squad-renderer-coverage.md) — the eleven-target roster this work
  sits inside
- [ZCode plugin packaging](zcode-plugin-packaging.md) — the other open ZCode decision
- [Claude/Pi ask-narrowing](claude-renderer-ask-narrowing.md) — the `ask` lowering that makes
  `architect` and `product-owner` unable to save their own plans on those harnesses; related in
  spirit to task B
- `docs/todo/antigravity-native-agents.md` on the PR #96 branch — the earlier record of task C
