---
id: adr/0036-primary-agent-canonical-skill-entry-point
title: A Primary Agent Sharing a Canonical Skill Identity Reuses the Skill as Its Entry Point
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-11
---

# ADR 0036: A Primary Agent Sharing a Canonical Skill Identity Reuses the Skill as Its Entry Point

## Status

Accepted, 2026-10-11. Introduces the **primary shared identity** and its cross-target projection.
Supersedes the "every agent/skill name intersection is a distinct-body collision" rule stated in
[ADR 0034](0034-factory-droid-entry-point-skill.md) and the fallback taxonomy in the Kyber-Squad
requirements. Does not change how a subagent shared identity projects, and does not change the
native single-projection rule for shared identities beyond the primary case it now carves out.

## Context

`product-owner` and `bug-crusher` each exist twice: as a canonical agent and as a canonical skill of
the same name. Both were subagents that lowered to a `role-`-prefixed projection on a fallback
target, and `product-owner` also existed as a distinct-body collision on the native targets. Neither
was a **root** — a role a human can start directly — even though both carry a complete workflow that
a human reaches for on its own: "spec this one feature" and "this one test is red, fix it".

Promoting them to `invocation: primary` collided with two existing rules:

1. **Claude (and Factory) render a primary as both a subagent and an entry-point skill.** Both would
   claim `.claude/skills/<name>/SKILL.md` when a canonical skill already holds that identity, and the
   renderer fails closed with `SquadRenderValidationException` rather than resolving the collision.
2. **A canonical agent/skill intersection with differing bodies is a distinct-body collision**, which
   on a fallback target projects the agent to `role-<name>` and keeps the canonical skill at `<name>`.

The wrapper design is what makes the intersection resolvable: the agent body is a **thin pointer** at
the same-name canonical skill, which carries the role. The two bodies differ by design — so the
"identical bodies" rule a subagent shared identity relies on cannot apply — but there is only ever
**one** thing to deploy, because the skill *is* the entry point. The duplication that forced the
fail-closed throw and the `role-` prefix does not exist for this shape.

## Decision

1. **A primary shared identity reuses its canonical skill as its entry point.** The identity is
   declared in `profiles/fallbacks.yml` under `role-skill` `shared-identities`, alongside the
   subagent shared identities, and is resolved from source at render time. `product-owner` and
   `bug-crusher` are the first two.

2. **The canonical skill is emitted on every target and is never suppressed.** A primary shared
   identity's skill carries the role, so suppressing it — as a subagent shared identity does, because
   the agent file already holds the identical body — would leave the wrapper's pointer dangling. The
   renderer resolves suppression per identity: a **subagent** shared identity suppresses its skill; a
   **primary** shared identity emits it.

3. **A target that adds an entry-point form skips the duplicate instead of failing closed.** Claude
   and Factory emit the agent file (subagent / droid) plus the canonical skill and **no** derived
   entry-point skill. The canonical skill at `.claude/skills/<name>/SKILL.md` (or `.factory/...`) is
   the entry point; emitting a second file at that path would overwrite the role with the wrapper.

4. **A target that lowers a primary reuses the canonical skill instead of lowering.** Cursor, Pi,
   ZCode, Devin, and Warp emit the canonical skill and no projection of their own — no lowered skill,
   no slash command, no `role-` prefix.

5. **The fail-closed throw is retained for an unresolvable collision.** When a canonical skill
   occupies a primary agent's entry-point identity and the identity is **not** a declared shared
   identity, the render still fails with `SquadRenderValidationException`. A native target has no
   `role-` escape hatch, and the two files would claim one path. This is a real collision, not a
   naming problem the renderer may resolve on its own.

6. **The bodies may differ for a primary shared identity.** The source validator requires identical
   bodies only for a `subagent`-invocation shared identity, where one projection is dropped and reuse
   must be lossless. A `primary` shared identity's bodies are complementary — the agent points, the
   skill carries — and the validator permits the difference.

7. **The degradation record stays honest.** Every target still records `role-skill-fallback` naming
   the reuse, so the receipt states what happened rather than implying a derived entry point exists.

## Alternatives Considered

- **Author a canonical skill whose body is the wrapper.** Rejected: the canonical skill carries the
  role; overwriting it with a pointer would remove the instructions the pointer points at.
- **Keep the fail-closed throw and never declare a shared identity.** Rejected: it makes a primary
  root wrapper impossible on Claude and Factory, which is the whole point of the change.
- **Rename the skill so the identity is unoccupied.** Rejected: it breaks the shipped
  `product-owner` and `bug-crusher` skill names every consumer already references, and it duplicates
  the role into two files that then drift.
- **Project the agent to `role-<name>` on the lowering targets, as a distinct-body collision does.**
  Rejected: the wrapper body is a pointer, so `role-product-owner` would point at a skill that is
  already the entry point; the second projection adds nothing and the canonical skill is the correct
  entry point.
- **Treat the canonical skill as suppressed (the subagent rule) on native targets.** Rejected: the
  agent file is the wrapper, so the role-carrying skill would not ship at all.

## Consequences

- **The agent/skill intersection taxonomy gains a third branch.** `SquadSharedIdentityProjection`
  resolves shared identities into suppressed (subagent) and reused (primary) sets; every renderer
  that lowered or suppressed a shared identity reads that resolver, so the rule lives in one place.
  The registry's `occupiedIdentities` / `distinctBodyCollisions` / `ResolveFallbackOutputIdentity`
  validations key on the suppressed set, not the raw shared set, because a primary shared identity
  claims an output path like any other emitted skill.
- **Adding a primary root wrapper is now a two-file change** — the agent body and a `shared-identities`
  entry — not a renderer change.
- **OpenCode's MCP grant keys on the suppressed set.** A primary shared identity is not a suppressed
  identity, so it keeps the kyber-weave MCP tools it needs to edit files and run commands; only a
  suppressed subagent identity withholds them.
- **A canonical skill occupying a primary identity remains a fail-closed render error** unless the
  identity is declared shared first; adding one means revisiting this ADR.

## Related

- [ADR 0034: Factory Keeps the Conductor Droid and Adds an Entry-Point Skill](0034-factory-droid-entry-point-skill.md)
- [ADR 0019: Native Pi Agents via pi-subagents, with Primary-Agent Skill Lowering](0019-pi-native-subagents-and-primary-lowering.md)
- [ADR 0025: Native Devin Agents, Pinned Models, and In-Process Delegation](0025-devin-native-agents-and-skill-lowering.md)
- [Kyber-Squad architecture: role-skill lowering](../kyber-squad/architecture.md#3-role-skill-lowering-and-namespace-resolution)
- [Kyber-Squad requirements: degradation matrix](../kyber-squad/requirements.md)
