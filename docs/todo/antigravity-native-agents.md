---
id: todo/antigravity-native-agents
title: Antigravity now has native agents, but the Squad renderer still lowers roles to skills
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-18
status: draft
---

# Antigravity now has native agents, but the Squad renderer still lowers roles to skills

This is **context for planning the work, not a plan**. It records what is known, what needs
verifying, and where the seam is.

## Why this exists

`AntigravityRenderer` is a fallback renderer: every Squad role becomes a skill under
`skills/`, prefixed `role-` when its name clashes with an existing skill. That was correct
when it was verified. `SquadGlobalRoots` records Antigravity as `skills/` under
`~/.gemini/config/`, "no override, no agent primitive".

The Antigravity CLI installed on 2026-09-18, `agy` 1.2.2, has an agent primitive:

- **`agy agents`** lists available agents.
- **`--agent <name>`** selects one for a session.
- **Agents live at `~/.gemini/config/agents/<name>/agent.md`.**

So a delegated Antigravity session cannot load a Squad role as an agent. It can only be told
to follow the role's skill. That is weaker for the conductor in particular, which needs to
spawn specialists.

## What is known

The agent file format, from 21 hand-authored agents, now backed up at
`~/.gemini/config/agents.bak.20260918/`:

- **One directory per agent**, holding `agent.md`.
- **YAML frontmatter:** `name`, `description`, `subagent` (bool), `mainAgent` (bool, on the
  orchestrator), `model` (for example `inherit` or `flash`), `reasoning_effort`,
  `enable_write_tools`, `enable_subagent_tools`, `enable_mcp_tools`, and `tools` (a list such
  as `view_file`, `write_to_file`, `list_dir`, `invoke_subagent`, `manage_subagents`).
- **A Markdown body** carrying the role's instructions.

Those backups were stale copies of Squad roles (including `conductor-v2` and
`conductor-v3`). They were moved aside so that `agy --agent <role>` cannot load an outdated
role.

## What needs verifying

- Whether Antigravity reads a project-level agents directory as well as the global one, and
  at what path.
- How Squad's model and capability profiles map onto `model`, `reasoning_effort`, the
  `enable_*` switches and `tools`, and which tool names grant what.
- Whether the IDE and the CLI read the same agent directory. The machine has both
  `~/.gemini/antigravity-ide` and `~/.gemini/antigravity-cli` state.

## The code seam

- `src/KyberWeave.Core/Squad/Rendering/AntigravityRenderer.cs`: moves from fallback to
  native, as the Factory and Kilo renderers did.
- `src/KyberWeave.Core/Squad/Deployment/SquadGlobalRoots.cs`: the Antigravity entry and its
  "no agent primitive" remark.
- [kyber-squad-renderer-coverage](kyber-squad-renderer-coverage.md): lists Antigravity as
  fallback-complete.

## How to verify

After a global install, `agy agents` lists the Squad roles, and `agy --agent conductor`
starts a session that can invoke the specialist roles as subagents.
