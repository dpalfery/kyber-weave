---
id: todo/antigravity-native-agents
title: Antigravity has native agents, but the Squad renderer still lowers roles to skills
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-21
status: draft
---

# Antigravity has native agents, but the Squad renderer still lowers roles to skills

This is **context for planning the work, not a plan**. It records what the harness actually
does, so the renderer can be written without guessing.

Originally written 2026-09-18 against `agy` 1.2.2 with four open questions. All four were
closed on **2026-09-21** against `agy` **1.2.7**; the sections below carry the answers and the
evidence for each. One cosmetic unknown remains and is named as such.

---

## Why this exists

`AntigravityRenderer` is a fallback renderer: every Squad role becomes a skill under
`skills/`, prefixed `role-` when its name clashes with an existing skill. That was correct
when it was verified. `SquadGlobalRoots` still records Antigravity as `skills/` under
`~/.gemini/config/`, "no override, no agent primitive".

Antigravity has an agent primitive:

- **`agy agent`** / **`agy agents`** — "List available agents".
- **`--agent <name>`** — selects one for a session.
- **`--mode`** — `accept-edits` or `plan`.

So a delegated Antigravity session cannot load a Squad role as an agent. It can only be told
to follow the role's skill. That is weaker for the conductor in particular, which exists to
spawn specialists.

---

## Evidence sources

Three independent sources, all on this machine:

1. **21 hand-authored agents**, moved aside to `~/.gemini/config/agents.bak.20260918/` so that
   `agy --agent <role>` could not load an outdated role. They are the format evidence.
2. **The `agy` binary** (`~/.local/bin/agy`, Mach-O arm64). It embeds its own changelog, Go
   struct tags with `jsonschema_description` text, protobuf enum names, and literal path
   templates. Extract with `strings -a`.
3. **The built-in `agy-customizations` skill** at
   `~/.gemini/antigravity-cli/builtin/skills/agy-customizations/SKILL.md`, which documents
   discovery locations and loading precedence.

---

## Answer 1 — discovery paths, both scopes

| Scope | Path |
|---|---|
| Global | `~/.gemini/config/agents/<name>/agent.md` |
| Project | `<workspace>/.agents/agents/<name>/agent.md` |

The project path is a literal template in the binary: `{workspace}/.agents/agents/{agent_name}/`.

The customizations skill corroborates the enclosing rule — workspace customizations live under
`.agents/` at the project root (`.agent/`, `_agents/`, `_agent/` are accepted alternates), and
the agent walks from the working directory up to the repository root to find them. Global
customizations live under `~/.gemini/config/`.

The binary also carries `agent %q resolves outside the defined agents directory`, so the loader
enforces containment; a rendered path must stay inside its agents root.

**Loading precedence**, highest first, from the customizations skill: workspace project →
declared configurations (`skills.json` / `plugins.json`) → global discovery (`~/.gemini/config/`)
→ built-in → global declared configurations. Project therefore overrides global, which matches
how the ZCode renderer already reasons about agent precedence.

## Answer 2 — what the `enable_*` switches gate

From `jsonschema_description` strings in the binary:

| Field | Meaning, verbatim |
|---|---|
| `enable_write_tools` | "Set true to equip the subagent with tools to create and edit files, and run commands." |
| `enable_subagent_tools` | "Set true to equip the subagent with tools to define and invoke its own subagents" |
| `enable_mcp_tools` | "Set true to enable the subagent to call MCP tools." |

So they **equip tool families**, and one switch spans two canonical capabilities:
`enable_write_tools` covers `filesystem.write` *and* `process.execute` together. That is a real
lowering constraint — an agent entitled to write but not to execute cannot be expressed by the
switch alone, and must be narrowed through the `tools` list instead.

`enable_mcp_tools: true` inherits the parent agent's configured MCP servers (a changelog entry
records fixing it to do exactly that), so it is not a per-server grant.

The backed-up `conductor-v3` sets `enable_subagent_tools: false` while listing
`invoke_subagent` and `manage_subagents` in `tools`. Given the switch wording, that file is
internally inconsistent — it is hand-authored, not generated, and should not be treated as a
pattern to copy.

## Answer 3 — the IDE and the CLI share one agents directory

Both read `~/.gemini/config/`, the "Global Configuration (Machine-Local)" path named by the
customizations skill. `~/.gemini/antigravity-cli/` and `~/.gemini/antigravity-ide/` hold
per-surface *runtime state* — conversations, caches, `brain`, `builtin` — and neither contains
an `agents` or `customizations` directory. There is one agent directory per scope, not one per
surface.

## Answer 4 — `model` is a closed tier enum, and `reasoning_effort` maps to a thinking level

`model` is **not** an arbitrary model id. The binary's protobuf enum is
`MODEL_TIER_{UNSPECIFIED, INHERIT, FLASH, FLASH_LITE, PRO}`, and the observed YAML spellings are
lowercase: `inherit` (4 agents), `flash` (13), `pro` (3).

`reasoning_effort` maps to `THINKING_LEVEL_{UNSPECIFIED, MINIMAL, LOW, MEDIUM, HIGH}` — note
there is no `max`. Observed: `high` (11), `medium` (7). The binary also carries
`unsupported reasoning_effort for thinking budget: %d`, so an out-of-range value is rejected
rather than ignored.

**The one remaining unknown is cosmetic**: the YAML spelling of `FLASH_LITE` (`flash-lite` vs
`flash_lite`) was not observed in any file and no quoted literal for it appears in the binary.
It blocks nothing — a `models.yml` `antigravity` column only needs the three confirmed
spellings — but do not emit the fourth tier until it is confirmed.

---

## The agent file format

One directory per agent holding a single `agent.md`: YAML frontmatter, then a Markdown body
that is the agent's system prompt. The changelog describes these as "custom agents using
Markdown files (`agent.md`) with YAML frontmatter and H1-delimited system prompts"; the
observed bodies open with `# Role`.

Frontmatter key frequency across the 21 agents:

| Key | Count | Type / domain |
|---|---|---|
| `name` | 23 | string |
| `description` | 23 | string |
| `tools` | 20 | YAML list, see vocabulary below |
| `model` | 20 | `inherit` \| `flash` \| `pro` (tier enum) |
| `subagent` | 19 | bool |
| `enable_write_tools` | 19 | bool |
| `enable_subagent_tools` | 19 | bool |
| `enable_mcp_tools` | 19 | bool |
| `reasoning_effort` | 18 | `minimal` \| `low` \| `medium` \| `high` |
| `author` / `version` / `license` | 18 | provenance, no functional effect |
| `mainAgent` | 1 | bool, **camelCase unlike every other key**, only on the orchestrator |

The changelog names four further fields supported by Markdown agents: `mainAgent`, `subagent`,
`hidden`, `inheritMcp`, and `commandExecutionPolicy`. The last three appear in the binary as
`yaml:"inheritMcp,omitempty"` and `yaml:"commandExecutionPolicy,omitempty"` — camelCase, like
`mainAgent`. None was observed in a file, and none is needed for a first renderer.

The orchestrator shape, for a primary-invocation agent:

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

## The tool vocabulary

Observed across the 21 agents, with frequency, and its proposed capability mapping:

| Capability | Antigravity tools | Observed count |
|---|---|---|
| `filesystem.read` | `view_file` | 18 |
| `filesystem.search` | `list_dir`, `grep_search` | 16, 16 |
| `filesystem.write` | `write_to_file`, `replace_file_content`, `multi_replace_file_content` | 14, 14, 14 |
| `process.execute` | `run_command` | 14 |
| `network.read` | `search_web`, `read_url_content` | 8, 8 |
| `network.publish` | *(none — record `permission-not-expressible`)* | — |
| `delegate` | `invoke_subagent`; `manage_subagents` for the orchestrator only | 6, 2 |

**This mapping is inferred from hand-authored files, not read from a published schema.** It is
the weakest claim in this document. The tool *names* are certain; which capability each one
should lower from is a judgement that should be confirmed against a live `agy` session before
the renderer is trusted.

---

## The code seam

- `src/KyberWeave.Core/Squad/Rendering/AntigravityRenderer.cs` — moves from fallback to native,
  as the Factory and Kilo renderers did. Note the output shape is a **directory plus
  `agent.md`**, which no existing target uses; every other native target writes a single file
  named for the agent.
- `src/KyberWeave.Core/Squad/Rendering/SquadRendererRegistry.cs` — `isNative` currently excludes
  Antigravity, and the fallback branch *asserts* that such a target emits no `/agents/` path and
  that every role-skill collision produces a `role-`-prefixed pair. Both assertions invert.
  `AgentOutputPath` and `SkillOutputPath` need Antigravity branches.
- `src/KyberWeave.Core/Squad/Deployment/SquadGlobalRoots.cs` — the Antigravity entry and its
  "no agent primitive" remark are both wrong now. The global root stays `~/.gemini/config`;
  only the subtree beneath it changes.
- `products/kyber-squad/profiles/models.yml` and `schemas/model-profiles.schema.json` — an
  `antigravity` column, whose values are tier names rather than model ids.
- Docs: [architecture §8](../kyber-squad/architecture.md#8-rendering) rendering table,
  [requirements](../kyber-squad/requirements.md) target matrix, this page's row in
  [renderer coverage](kyber-squad-renderer-coverage.md), and an ADR — reclassifying a target
  from fallback to native is the same class of decision
  [ADR 0020](../adr/0020-zcode-command-lowering-and-resource-relocation.md) records for ZCode.

## Deployment consequence

Antigravity is currently deployed as skills and working, so there is no outage forcing this.
When the change lands, the existing global receipt needs an uninstall and reinstall, because
the target's file shape changes from `skills/<name>/SKILL.md` to `agents/<name>/agent.md`.

## How to verify the result

After a global install, `agy agent` lists the Squad roles, and `agy --agent conductor` starts a
session that can invoke the specialist roles as subagents. Note that `agy agent` printed
nothing during this investigation even with agents present on disk, so treat an empty listing
as inconclusive rather than as a failure, and confirm inside a real session.

## Related

- [Black Hawk Hotel handover](black-hawk-hotel-todo.md) — the handover this task belongs to
- [Renderer coverage](kyber-squad-renderer-coverage.md) — lists Antigravity as
  fallback-complete, which this change supersedes
