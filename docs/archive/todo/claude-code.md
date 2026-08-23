---
id: archive/todo/claude-code
title: Add a native Claude Code renderer to Kyber-Squad
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-08-23
status: archived
---

# Add a native Claude Code renderer to Kyber-Squad

**Status:** Archived
**Archive Date:** 2026-08-23

Closed by shipping `ClaudeRenderer` in Core and registering it in
`SquadCommandComposition.ResolveRenderer()` alongside `CopilotRenderer`. See
[2026-08-23-claude-code-native-renderer.md](../plans/2026-08-23-claude-code-native-renderer.md)
for the implementation contract and verification harness.

This is **context for planning the work, not a plan** — it states what is known, what is
assumed and unverified, and where the seam is. It does not sequence tasks or commit to an
implementation.

## Why this exists

`squad install --target claude` fails today, in preflight, before any network call:
`SquadRendererRegistry` (`src/KyberWeave.Core/Squad/Rendering/SquadRendererRegistry.cs`)
only has a renderer registered for `copilot`. Every other approved target — including this
one — has no `ISquadRenderer` implementation, so requesting it is rejected with a message
naming the gap and pointing here. See
[architecture.md §8](../../kyber-squad/architecture.md#8-rendering) for how the render pipeline
as a whole works, and
[onboarding.md](../../kyber-squad/onboarding.md#harness-targets-and-auto-detection) for the
full target roster and its current coverage.

## Classification

**Native agent target.** Canonical agents render as this harness's own native agent primitive (Markdown with YAML frontmatter (subagent format — verify field names)) at `.claude/agents/<name>.md`. Canonical skills render as harness skills at `.claude/skills/<name>/SKILL.md`, except `conductor` and `conductor-v3` — those are native primary agents on every native target and their skill projection is suppressed (the single-projection rule `CopilotRenderer` also follows).

## What is known (from the canonical source and the codebase)

- Strong detection marker: `.claude/`
- Alias(es): none
- The 22 canonical agents and 26 canonical skills this renderer must cover live under
  `products/kyber-squad/agents/*.md` and `products/kyber-squad/skills/*/SKILL.md`, loaded via
  `SquadSourceLoader.Load` (`src/KyberWeave.Core/Squad/Parsing/SquadSourceLoader.cs`) into a
  `SquadSource` — the same model `CopilotRenderer` renders from.
- Per-agent model resolution and per-agent capability/permission profiles are declared in
  `products/kyber-squad/profiles/models.yml` and `profiles/capabilities.yml`. Neither file is
  target-specific by construction — check the target-specific note below for whether this
  target has real entries in `models.yml` yet.

## What is assumed and needs verification, not trusted as-is

This is the harness Kyber-Weave itself runs under, which makes it the easiest target to verify empirically: write one real agent by hand, load it in Claude Code, and confirm which frontmatter keys it actually reads before trusting any assumed schema.

None of this has been checked against this harness's actual, current documentation. The one
concrete, verified reference implementation in the codebase is `CopilotRenderer`
(`src/KyberWeave.Core/Squad/Rendering/CopilotRenderer.cs`) — its doc comment records exactly
what was verified against GitHub's docs and when, and its degradation-over-guessing approach
to permissions (see below) is worth carrying into any new renderer rather than re-deriving.

## The code seam

- Implement `ISquadRenderer` (`src/KyberWeave.Core/Squad/Rendering/SquadRenderModels.cs`):
  `SupportedTargets` and `RenderAsync(SquadRenderRequest, CancellationToken)`.
- Register it in `SquadCommandComposition.ResolveRenderer()`
  (`src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs`) alongside `CopilotRenderer`.
  `SquadRendererRegistry` handles the coverage gate, dispatch, and post-render validation —
  a new renderer does not reimplement any of that.
- **Permissions**: do not invent a mapping from the semantic capability vocabulary
  (`filesystem.read`, `filesystem.write`, `process.execute`, `network.read`,
  `network.publish`, `delegate`) to this harness's own permission model unless that mapping
  is verified against real documentation. Where it cannot be verified, follow
  `CopilotRenderer`'s pattern: leave the harness's permission-equivalent field unset (whatever
  that harness's own safe default is) and record a `SquadDegradationRecord` with code
  `permission-not-expressible` naming what could not be enforced. A guessed mapping that
  turns out wrong is a silent permission *widening* — exactly what the registry's validation
  pass and the receipt's degradation records exist to make impossible to ship unnoticed.
- **Validation will hold this renderer to the same invariants as Copilot's**: portable output
  paths contained under the extraction root, only requested targets in the output, the
  native/fallback single-projection rules (conductor and conductor-v3 handled per this
  target's classification above), and every degradation's `InstructionDigest` matching the
  named agent's real `SquadAgent.BodyDigest`. See `SquadRendererRegistry.ValidateRenderResult`
  for the exact checks — this runs against every renderer, not something to reimplement.

## How to verify

- Reuse the pattern in `tests/KyberWeave.Tests/SquadRenderingContractTests.cs`: render the
  real, checked-in `products/kyber-squad` corpus (not a synthetic fixture) through the new
  renderer, and assert against the *loaded* `SquadSource` model rather than hardcoded
  literals, so the test can't silently drift from the canonical source it's supposed to be
  checking.
- Confirm `kyber-weave squad install --target claude --dry-run` plans a file for every
  agent and skill this target should cover (native: 22 agents + 24 non-conductor skills = 46,
  matching Copilot's count, unless this target's own agent-primitive support differs;
  fallback: 26 skills plus role-lowered skills per the collision rules above).
- Confirm `kyber-weave squad doctor` reports `claude` under renderers available, not
  pending.
