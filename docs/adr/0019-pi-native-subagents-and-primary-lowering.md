---
id: adr/0019-pi-native-subagents-and-primary-lowering
title: Native Pi Agents via pi-subagents, with Primary-Agent Skill Lowering
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-09-14
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

## Related

- [Kyber-Squad architecture](../kyber-squad/architecture.md)
- [Kyber-Squad onboarding](../kyber-squad/onboarding.md)
- [Plan: Add Pi as a Kyber-Squad harness target](../archive/plans/2026-09-14-pi-harness-target.md) (archived)
