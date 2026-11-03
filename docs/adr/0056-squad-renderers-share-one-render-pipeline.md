---
id: adr/0056-squad-renderers-share-one-render-pipeline
title: Squad renderers share one render pipeline
doc-type: adr
status: current
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-03
code-refs:
  - SquadHarnessRenderer
  - SquadHarnessRenderContext
  - SquadHarnessSpec
  - SquadToolMap
  - SquadAgentProjection
---

# ADR 0056: Squad renderers share one render pipeline

## Status

Accepted, 2026-11-03.

## Context

Kyber Squad ships thirteen harness renderers. Each implemented `ISquadRenderer` on its own and
restated the same steps: the wrong-target guard, source loading, project/global directory
resolution, the skill document, the canonical skill loop with shared-identity suppression,
model lowering from `models.yml`, the capability-to-tool lowering, the primary-agent lowering
decision, and the Arbiter hook block. The copies were similar but not identical, and each
harness's test suite pinned its copy against the real corpus.

That shape made the per-harness corpus tests the largest source of edit fan-out: a change to a
shared step touched every renderer and every suite. Which harnesses are native and which fall
back to role skills was also hard-coded in `SquadRendererRegistry` validation, so adding a
harness meant a registry edit.

## Decision

1. **Every shipped renderer derives from `SquadHarnessRenderer`.** The base class owns the guard,
   the source load, and result assembly. Its default `Render` is the native shape: each agent
   through `RenderAgent`, then the canonical skills, then Arbiter wiring. A harness whose output
   order differs (Warp, Antigravity) overrides `Render` and still uses the shared steps.
2. **A harness is a `SquadHarnessSpec` plus its true format quirks.** The spec carries the
   target, project root, agent directory, model policy, description line-break rule, and skill
   license. Everything else a harness decides — its agent file format, tool names
   (`SquadToolMap`), permission lowering, and degradation wording — stays in its renderer.
3. **Shared steps live on `SquadHarnessRenderContext`**, created once per render: the skill
   document, the skill loop, the primary-agent lowering decision (onto a skill, or the
   additional entry-point skill of [ADR 0034](0034-factory-droid-entry-point-skill.md)), the
   discovery-guarded agent file ([ADR 0040](0040-flat-agent-dir-discovery-guarded-resources.md)),
   the matcher-group hook block, and per-render state a harness resolves once.
4. **Native versus fallback is a catalog fact.** `SquadTargetCatalog.GetAgentProjection` names it
   per target, and the registry's validation reads it for every renderer.
5. **Moving a renderer onto the pipeline must not change a byte.** Where two renderers disagreed
   only by accident, the spec keeps both forms (for example, `SquadLineBreaks.LineFeedOnly`), so
   unifying them is a separate, deliberate output change.
6. **Shared behaviour is tested once.** `SquadRendererSharedContractTests` holds every renderer to
   its own spec for the skill document, file roots, global-scope layout, and record identity. A
   harness suite keeps only what that harness decides.

## Alternatives Considered

- **Keep thirteen independent renderers and share only helpers.** Rejected. The helpers already
  existed (`SquadMarkdownDocument`, `SquadResourceProjection`) and the duplication grew around
  them, because nothing made a renderer use them.
- **One table-driven renderer for every harness.** Rejected. Harnesses differ in agent file
  format (TOML, hand-rolled YAML, per-agent directories), permission model and lowering. A table
  rich enough to express that would be a second programming language with worse diagnostics.
- **Keep native/fallback in the registry, or put it on the spec.** Rejected. The registry
  validates renderers that do not use the pipeline (test fakes), so the fact belongs to the
  target, beside its token.

## Consequences

- A new harness is a spec and a `RenderAgent`. It inherits the skill document, the shared
  contract rows, and the registry's projection check by being added to
  `SquadCommandComposition.CreateRenderers()` and the catalog.
- A change to a shared step changes every harness at once. That is the intent, and it means a
  shared-step change needs the cross-harness output review a harness-local change does not.
- The pipeline carries some accidental differences as spec fields until someone decides to
  unify them as a deliberate output change.
- The base class is public with internal members, because renderers are public and the spec and
  context are internal types.
- The registry's per-target output-path tables used for resource collision checks still list each
  harness; they are not yet derived from the spec.
