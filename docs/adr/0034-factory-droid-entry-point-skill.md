---
id: adr/0034-factory-droid-entry-point-skill
title: Factory Keeps the Conductor Droid and Adds an Entry-Point Skill
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-10
---

# ADR 0034: Factory Keeps the Conductor Droid and Adds an Entry-Point Skill

## Status

Accepted, 2026-10-10. Records the Factory rendering classification delivered with the
primary-agent entry-point skill. Does not change how Devin, Pi, ZCode, or Warp lower primary
agents, and does not change the native single-projection rule for shared identities.

## Context

Factory custom droids are dispatched as subagents: the main-session agent invokes them
through `Task`, and `~/.factory/droids/` and `.factory/droids/` feed the same subagent
roster. The Factory app exposes no session-parent selector — there is no way to start a
conversation *as* a droid the way `claude --agent <name>` or Devin Cloud's skill entry
point allow. The conductor is the squad's primary agent and default entry point, so on
Factory it was projected as a droid the user could never start from, only reference.

Claude faced the same gap for interactive selection and solved it with a dual entry point:
the subagent file is kept for enforced invocation, and the primary agent is additionally
exposed as an entry-point skill carrying the canonical agent body verbatim, recorded as
`role-skill-fallback` ("native dual entry point"). Factory's shape is the same gap: the
native droid primitive works for delegation, and only the user-facing selection is missing.

## Decision

1. **Factory is a native target that renders a primary agent through an additional
   entry-point form.** `FactoryRenderer` emits every canonical agent as
   `.factory/droids/<name>.md` as before, and — for a primary agent whose fallback profile
   declares `no-primary-agent: skill` — additionally emits an entry-point skill at
   `.factory/skills/<name>/SKILL.md` with the canonical agent body verbatim and the agent's
   resource closure projected beside it, recording `role-skill-fallback`. The droid file is
   kept for Task dispatch.
2. **The entry-point skill runs unenforced.** A Factory skill is a Markdown instruction
   file: the droid's explicit `tools` allow-list, `mcpServers: []`, and the safety-narrowed
   `ask` handling do not apply to it. It runs in the main session under the session's
   permissions, like any hand-typed instruction. The shared
   `permission-not-expressible` record states this caveat.
3. **A canonical skill occupying the entry-point identity fails closed.** Factory renders
   both a droid and an entry-point skill for the identity, and there is no `role-` escape
   hatch on a native target. `FactoryRenderer` throws `SquadRenderValidationException`
   rather than renaming either projection.
4. **`no-primary-agent: omit` emits no entry-point skill.** The droid renders exactly as
   before, with unchanged degradation records. The shared `role-skill` profile currently
   declares `skill`, so the conductor ships the dual projection.

## Alternatives Considered

- **Author a canonical `conductor` skill (an eighth distinct-body intersection).** Rejected.
  It would grow the canonical inventory from 24 skills and duplicate a body the harnesses
  that need it already derive from the agent, forcing every conductor body edit through two
  files. Claude's dual entry point established that an agent-derived entry-point skill is
  the product's answer to a missing selection primitive, not new canonical content.
- **Declare the conductor a shared identity.** Rejected. A shared identity suppresses the
  skill projection on native targets, which would remove the one projection the user can
  start from — the opposite of the goal.
- **Drop the droid and lower the conductor to a skill only, as on Devin.** Rejected. Devin
  lowers because its subagents cannot delegate and Cloud sees no agents; Factory's droid
  primitive works for Task dispatch, and dropping it would take away enforced tool
  allow-lists for no benefit.

## Consequences

- Every future `invocation: primary` agent on Factory inherits the same dual projection.
  Changing that rule means rewriting `FactoryRenderer`, its contract tests, and the Factory
  row in the requirements matrix.
- Operators invoke the Squad conductor in the Factory app through the `conductor` skill;
  the droid remains reachable only by dispatch. The skill's instruction-only folder
  restriction and delegation boundary are advisory, as on every skill surface.
- A canonical skill named `conductor` becomes a fail-closed render error on Factory before
  it is a runtime problem; adding one means revisiting this ADR first.

## Related

- [ADR 0019: Native Pi Agents via pi-subagents, with Primary-Agent Skill Lowering](0019-pi-native-subagents-and-primary-lowering.md)
- [ADR 0025: Native Devin Agents, Pinned Models, and In-Process Delegation](0025-devin-native-agents-and-skill-lowering.md)
- [Kyber-Squad requirements: degradation matrix](../kyber-squad/requirements.md)
- [Kyber-Squad onboarding: Factory notes](../kyber-squad/onboarding.md)
