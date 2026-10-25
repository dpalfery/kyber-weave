---
id: adr/0019-pi-native-subagents-and-primary-lowering
title: Native Pi Agents via pi-subagents, with Primary-Agent Skill Lowering
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-24
---

# ADR 0019: Native Pi Agents via pi-subagents, with Primary-Agent Skill Lowering

## Status

Accepted, 2026-09-14. Records the Pi rendering classification delivered with the Pi harness
target. Does not change how other native or fallback targets lower agents.

## Context

Pi core ships no sub-agents and no per-agent primary selection. `.pi/SYSTEM.md` and
`--system-prompt` replace the prompt for the whole session. The original coverage todo
therefore treated Pi as a primitive-less target and would have lowered every canonical agent
to a skill.

The owner runs Pi with `@tintinweb/pi-subagents`, which defines a Claude Code-like custom-agent
file format under `.pi/agents/` and a skill tree under `.pi/skills/`. That extension is what
makes a native agent projection possible. It is not first-party Pi.

The extension's default `maxSubagentDepth` is 2 (main session 0, subagent 1, nested child 2).
A conductor rendered as a subagent would run at depth 1, which puts its specialists at depth 2
and removes the nested delegation that `architect` and `code-reviewer` need. `/agents` lists
subagent types only; nothing promotes a custom agent into the main session.

## Decision

1. **Pi is a native target.** `PiRenderer` emits subagent-invocation agents as
   `.pi/agents/<name>.md` in the `@tintinweb/pi-subagents` custom-agent format, and canonical
   skills as `.pi/skills/<name>/SKILL.md`. No Pi output may carry a `role-` prefix. Agents and
   skills occupy separate namespaces, so the seven agent/skill name intersections are not
   collisions on Pi.
2. **Primary-invocation agents lower, they do not become subagents.** The fallback profile's
   `no-primary-agent` value decides the outcome, read from the loaded source. The current
   corpus has one such agent, `conductor`, with value `skill`: it renders as
   `.pi/skills/conductor/SKILL.md` at depth 0, recording `role-skill-fallback`. Value `omit`
   emits nothing and records `omitted`.
3. **A lowered identity that is already a canonical skill fails closed.** Native classification
   forbids a `role-` escape hatch. `PiRenderer` throws `SquadRenderValidationException` rather
   than emitting a duplicate.
4. **The extension is a prerequisite, not a deployed artifact.** Squad does not install
   `@tintinweb/pi-subagents`. Without 0.19.0 or later (peer: Pi ≥ 0.84.0), the emitted agent
   files have no effect. `squad doctor` does not detect the extension.

## Alternatives Considered

- **Lower every agent to a skill (original todo).** Rejected. The owner has the extension
  configured, and native agent files give `/agents`, `@handle` mentions, and
  `allowed_subagents` that a skill-only tree cannot.
- **Render the conductor as a subagent, matching Claude/Cursor/Copilot/Codex.** Rejected. Pi
  has no primary-agent primitive, so the file would spawn at depth 1 and nested specialist
  delegation would hit the default depth cap. `/agents` cannot replace `main` with that file.
  Reconfirmed when the live-Pi observations reopened the plan (U7).
- **Wait for first-party Pi sub-agents.** Rejected. The owner named the third-party format as
  the rendering target now; a later first-party primitive would be a superseding ADR.

## Consequences

- Every future `invocation: primary` agent on Pi inherits the same lowering. Changing that
  rule means rewriting `PiRenderer`, its contract tests, and the onboarding depth guidance.
- Operators invoke the Squad conductor with `/skill:conductor`, not from `/agents`. Project
  trust is required before `.pi/skills/` loads; project `.pi/agents/` load without it.
- Format drift in `@tintinweb/pi-subagents` is now a Squad maintenance obligation. A
  first-party Pi agent primitive does not automatically replace this renderer.
- Project `.pi/agents/<name>.md` silently overrides a same-named global agent. That is
  intended project authority, and it narrows any pre-existing global files that share a
  canonical identity.

## Amendment 2026-10-24 (revised): `network.read` via canonical per-target overlay — Option A REVERSED

Owner decision Option A 2026-10-23 (`network.read → mcp` renderer lowering, eight-tool
vocabulary) is **REVERSED 2026-10-24 on verified live evidence and is not implemented**
(plan `2026-10-23-pi-renderer-network-read-mcp`, D1). Bare `mcp` in `tools:` is refused by
the Pi loader under the unknown-name rule — plain `tools:` names must be built-ins, and
subagents inherit the documented `extensions: ["-builtin:mcp"]` global opt-out — so the
revision-1 mapping emitted invalid frontmatter. The original 2026-10-24 amendment text
recording that lowering is superseded by this revision; no reader should re-derive the
reverted mechanism from it.

The operative fix lives one layer up, in the canonical Squad agent file: two optional flat
`kyber-squad.agent/v1` keys, `pi-extensions` (array of Pi extension ids) and `pi-tools`
(array of additional Pi tool names, `ext:<extension>/<tool>` form only — bare names are
rejected so the overlay cannot smuggle a built-in past the capability lattice). The Pi
renderer lowers the overlay onto the verified wiring: `extensions: [pi-mcp-adapter]` (YAML
sequence, the exact live-probed form) plus the declared `ext:` tool names appended to the
capability-derived `tools` CSV. Basis: 2026-10-24 live probes (run twice) — a subagent
carrying that wiring started the context7 server, resolved a library ID, and returned
sourced docs, while the scope gate refused the call without the nested
`mcp__<server>__<tool>` allowlisting. Scope is `research-agent` only, declaring exactly the
verified entries (`ext:pi-mcp-adapter/mcp`,
`ext:pi-mcp-adapter/mcp__context7__resolve_library_id`,
`ext:pi-mcp-adapter/mcp__context7__query_docs`); the extension prerequisite is the
`pi-mcp-adapter` npm package in the deployment harness.
This supersession leaves Q1-A intact: the renderer vocabulary returns to the seven-tool
`read, grep, find, ls, edit, write, bash`; `network.read` still has no expressible built-in
Pi tool and still records `permission-not-expressible` (true of the renderer alone — the
overlay is the additive per-agent exception that supplies web access); `network.publish`
still has no built-in tool and still records `permission-not-expressible`; `extensions:
false` is still emitted unconditionally for every agent without an overlay; `tools` is still
always emitted; and `process.execute: deny` stays withheld for read-only roles. Caveat: the
loader behaviour for a missing `pi-mcp-adapter` extension on a deployment machine is
unverified and is not assumed.

## Related

- [Kyber-Squad architecture](../kyber-squad/architecture.md)
- [Kyber-Squad onboarding](../kyber-squad/onboarding.md)
- [Plan: Add Pi as a Kyber-Squad harness target](../archive/plans/2026-09-14-pi-harness-target.md) (archived)
