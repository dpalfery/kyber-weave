---
id: adr/0025-devin-native-agents-and-skill-lowering
title: Native Devin Agents, Pinned Models, and In-Process Delegation
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-09-27
---

# ADR 0025: Native Devin Agents, Pinned Models, and In-Process Delegation

## Status

Accepted, 2026-09-27. Records the Devin-specific rendering decisions delivered with the `devin`
harness target, and the two canonical-content changes that target needed. Does not change how
any other target lowers agents, grants tools, or picks models.

## Context

Devin Desktop is Cognition's desktop app for Windows and macOS, shipped on 2026-06-02 as the
successor to Windsurf. Its local agent, Devin Local, is the Devin CLI's agent harness, so it
reads the CLI's subagent and skill formats. The facts below were read on 2026-09-27 from
`docs.devin.ai` — the subagents, skills, creating-skills, permissions, configuration-import,
lifecycle-hooks, models, and MCP pages — and from the Devin CLI changelog.

Five properties shape the rendering.

**Subagents are native, a primary agent is not.** Custom subagents load from
`.devin/agents/<name>.md` or `.devin/agents/<name>/AGENT.md`, with the documented keys `name`,
`description`, `model`, `allowed-tools` (alias `tools`), and `max-nesting`. On a subagent
`allowed-tools` is a true restriction. Devin Local itself is the only top-level agent, so the
canonical `conductor` cannot render as an agent. In Desktop, subagents sit behind a
**Subagents (Preview)** setting, and an administrator can switch them off.

**A skill's tool keys pre-approve rather than restrict.** On a skill, `allowed-tools`
auto-approves the listed tools and `permissions.allow` does the same; neither removes anything.
`permissions.deny` is documented as the way to hard-block a tool "during skill execution" for an
inline skill. How long an inline skill counts as executing, and whether its rules reach the
subagents it dispatches in that time, is not documented.

**Nested delegation has no roster.** A subagent reaches further subagents only when its
`max-nesting` allows it, and then it reaches every profile: there is no `allowed_subagents`
equivalent. Foreground subagents prompt for approval under the session's permission mode, which
can already approve the call; background subagents never prompt and auto-deny anything not
pre-approved.

**Tools vary by model.** Devin's core tool names include `edit`, `write`, `apply_patch`, and
`notebook_edit` for file changes and `exec` with `get_output`, `write_to_process`, and
`kill_shell` for processes. With `agent.codex_tools` on, a GPT model edits through
`apply_patch` and reads through the shell. `write` is recognized in `allowed-tools` from CLI
v3000.11.1 (2026-09-21).

**A subagent without `model` does not inherit.** It runs on the default subagent model, which
a server-side router chooses at spawn time unless an administrator pins one. `model` accepts a
family alias (`opus`, `sonnet`, `swe`, …) that resolves to the family's latest model, or an
exact model id with the effort level in it (`swe-1-7-medium`).

And Devin reads more than its own tree: `.agents/agents/` and `.agents/skills/` natively, and by
default `.claude/skills/`, `.claude/commands/`, `.github/skills/`, and `.windsurf/skills/`
through `read_config_from`.

Devin Cloud — the hosted agent, also reached through `devin --cloud` and `/handoff` — shares
the skill format but not the subagent one. It discovers `SKILL.md` files from `.devin/skills/`,
`.agents/skills/`, `.claude/skills/`, `.github/skills/`, `.cognition/skills/`, and
`.windsurf/skills/`, and honours a skill's `triggers`, but custom subagents are "CLI/Desktop-only
today". Cloud also offers fewer models than Desktop.

## Decision

1. **Agents render in the directory layout under `.devin/` only.** Each subagent is
   `.devin/agents/<name>/AGENT.md`, with its resource closure beneath `<name>/` inside that
   directory, so authored links resolve verbatim and no resource lands where Devin looks for a
   definition file. The flat form would put the closure in the directory Devin's other layout
   claims for the same name. Skills are `.devin/skills/<name>/SKILL.md`. Nothing is written to
   `.agents/`, which is Antigravity's output, or to `.windsurf/`, which is an import from another
   tool.

2. **The conductor lowers to a skill that carries no tool keys.** As on Pi
   ([ADR 0019](0019-pi-native-subagents-and-primary-lowering.md)), a primary agent whose
   fallback profile declares `no-primary-agent: skill` renders as a skill, failing closed if a
   canonical skill already holds the name. No skill carries `allowed-tools` or
   `permissions.allow`, because those pre-approve. `permissions.deny` is withheld as well — see
   the alternatives — so the lowered conductor's capability decisions and its `delegates-to`
   roster are recorded as `permission-not-expressible`, and its orchestrator boundary is
   instruction-only, as it is on Pi. The lowered skill does carry `triggers: [user]`, so it
   runs only when the operator starts it — see decision 7.

3. **Every tool that performs a granted capability is granted.** `filesystem.write` lowers to
   `edit`, `write`, `apply_patch`, and `notebook_edit`; `filesystem.read` to `read` and
   `notebook_read`; `process.execute` to `exec` and its three companions; `network.read` to
   `webfetch` and `web_search`. `todo_write` and `skill` are granted to every subagent, as
   `TodoWrite` and `Skill` are on Claude and ZCode. `ask` withholds the tool and records
   `safety-narrowed`: a subagent profile has no key that forces the prompt, and the session's
   permission mode or an earlier grant can approve the call without one. MCP is granted by the
   exact `mcp__<server>__<tool>` names in `toolchain.yml`, never through Devin's generic
   `mcp_call_tool` family, which reaches every configured server.

4. **Subagents do not delegate, and the delegating roles carry a fallback.** Neither
   `run_subagent` nor `max-nesting` is emitted, and an allowed `delegate` records
   `permission-not-expressible` naming the lost roster. The canonical bodies of the roles that
   delegate say what to do without an agent tool: `code-reviewer` applies each lens itself, one
   at a time, runs its own refutation pass, and states in the report that the council ran
   in-process; `architect` gathers its own sweeps and returns a live Azure question as
   `STATUS: BLOCKED`, which the conductor's plan path now puts to `azure-reader` and answers.
   `product-owner` already delegated research only "when available". Both body edits are
   harness-neutral, so they are reviewed evolutions of the Hotshot golden rather than
   Devin-only text.

5. **Every subagent pins an exact Devin model id.** Leaving `model` out would put planners and
   reviewers on whatever the router picks, so each non-orchestration model profile carries a
   `devin:` value, with the effort in the id:

   | Profile | Model | Why |
   |---|---|---|
   | `deep-planning` | `claude-opus-5-5-high` | Frontier reasoning at high effort; low volume, so the effort is affordable where it shapes everything downstream. Matches the Claude target's `opus` |
   | `general` | `swe-2-high` | Cognition's current coding model, and Devin's own implementation sidekick in Fusion |
   | `fast` | `deepseek-v4-1-flash-high` | The highest-volume tier, so price per token dominates; the owner's judgement is that V4.1 Flash is both far cheaper and stronger than the alternatives here |
   | `reviewer` | `grok-4-7-high` | A third vendor: the implementers are DeepSeek and Cognition and the planner is Anthropic, so review is independent of both |

   Each role family runs on a different vendor, so a review never grades its own model's
   work. `orchestration` is `inherit`: the conductor is a skill in the main session and runs on
   the model the operator picked. `claude-opus-5-5-high` and `swe-2-high` are in Devin's
   published model list as read on 2026-09-27; `grok-4-7-high` and `deepseek-v4-1-flash-high`
   follow its naming (`grok-4-6-high`, `deepseek-v4-flash-high`) but were not yet listed, and
   were chosen from the in-app picker.

6. **Duplicate loading is reported, not prevented.** `squad doctor` warns, per tree, when a
   workspace holds a Squad identity under `.devin/` and under a tree Devin also loads, naming
   the `read_config_from` switch for an imported tree and "pick one target" for `.agents/`. The
   renderer does not write `.devin/config.json`, because the `claude` import also carries
   `CLAUDE.md` rules and Claude's MCP servers, and giving those up is the operator's trade.

7. **The conductor starts only when asked, because Devin Cloud has no roster.** Cloud sessions
   discover skills from `.devin/skills/` and five other roots, and invoke them by description
   match, but load no custom subagents — Devin documents those as CLI and Desktop only. An
   auto-invoked conductor in Cloud would route work to agents that do not exist. The lowered
   skill therefore carries `triggers: [user]`, which both Cloud and the CLI honour, and the
   operator starts it with `/conductor`. This is ZCode's reasoning
   ([ADR 0021](0021-zcode-command-lowering-and-resource-relocation.md)) reached by a different
   route. No model fallback is needed for Cloud: the pins live only in subagent profiles, which
   Cloud never reads, and skills carry no `model`.

## Alternatives rejected

- **Flat `agents/<name>.md`.** Simpler paths, but the resource closure would sit in
  `agents/<name>/`, the directory the other layout reads as the same identity.
- **Granting nested delegation with `max-nesting`.** It would restore the review council's
  fan-out, but a reviewer could then dispatch a write-capable implementer: the grant cannot be
  confined to the roster, so it widens.
- **Family aliases for models.** `opus` and `swe` track the newest model automatically, which
  also moves price and behaviour without a change here. Every other target pins versions.
- **Fusion or Adaptive as a subagent model.** Fusion is chosen in an interactive picker, not by
  id, and Adaptive routes per request — possibly to a GPT model, reintroducing the tool
  variance decision 3 exists for — so neither pins anything.
- **GPT models for write-capable profiles.** Cheap per token (`gpt-6-luna-medium` at
  $0.10 / $0.50), and viable once decision 3's `apply_patch` grant is verified on a real
  install; not chosen while that is unverified.
- **A Claude reviewer.** `claude-sonnet-5-medium` would match the Claude target, but it shares a
  vendor with the planner; `grok-4-7-high` keeps every role family on a different vendor.
- **Per-surface model pins for Cloud.** Devin Cloud offers fewer models than Desktop, but a
  profile file serves both and Cloud does not load it today, so there is nothing to fall back
  from yet.
- **Emitting `allowed-tools` on the conductor skill.** It reads like a restriction and is the
  opposite: it pre-approves.
- **Emitting the conductor's denials as `permissions.deny`.** It would enforce the orchestrator
  boundary, and it only narrows the conductor. But the conductor stays the running skill while
  it dispatches implementers, and it denies `edit`, `write`, and `exec` — the tools every
  implementer needs. If Devin applies a running skill's deny rules to the subagents it
  dispatches, the whole roster loses its write tools. Revisit once a real install shows the
  rules stay in the conductor's own turn.

## Consequences

- Devin is the first native target on which the delegating roles cannot delegate. Their
  fallback is weaker evidence than a fanned-out council, and the report says so.
- The conductor's orchestrator boundary is instruction-only on Devin until the
  `permissions.deny` question is settled on a real install.
- The model table is an owner decision that ages. When Devin retires an id, `models.yml` and
  the renderer contract test change together. Two ids were pinned ahead of Devin's published
  list, so `devin doctor` and `/session-stats` after install are the confirmation.
- If Devin brings custom subagents to Cloud, a Desktop-only model id could be unavailable
  there; that is the point at which a per-surface fallback would be needed.
- Several readings remain to confirm on a real install: MCP names in `allowed-tools`, the
  `apply_patch` grant under `agent.codex_tools`, how Devin orders two same-named definitions,
  and `XDG_CONFIG_HOME` on macOS.

## Related

- [ADR 0019](0019-pi-native-subagents-and-primary-lowering.md) — the primary-agent skill
  lowering this record follows
- [ADR 0021](0021-zcode-command-lowering-and-resource-relocation.md) — MCP by enumerated tool
  name, and a doctor check paired with a renderer decision
- [ADR 0022](0022-antigravity-native-agents.md) — the `.agents/` tree Devin also reads
- [Kyber-Squad architecture](../kyber-squad/architecture.md) — §8 rendering
- [Kyber-Squad onboarding](../kyber-squad/onboarding.md) — Devin notes
