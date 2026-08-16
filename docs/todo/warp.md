---
id: todo/warp
title: Add a native Warp renderer to Kyber-Squad
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-08-16
status: draft
---

# Add a native Warp renderer to Kyber-Squad

This is **context for planning the work, not a plan** — it states what is known, what is
assumed and unverified, and where the seam is. It does not sequence tasks or commit to an
implementation.

## Why this exists

`squad install --target warp` fails today, in preflight, before any network call:
`SquadRendererRegistry` (`src/KyberWeave.Core/Squad/Rendering/SquadRendererRegistry.cs`)
only has a renderer registered for `copilot`. Every other approved target — including this
one — has no `ISquadRenderer` implementation, so requesting it is rejected with a message
naming the gap and pointing here. See
[architecture.md §8](../kyber-squad/architecture.md#8-rendering) for how the render pipeline
as a whole works, and
[onboarding.md](../kyber-squad/onboarding.md#harness-targets-and-auto-detection) for the
full target roster and its current coverage.

## Classification

**Fallback (role-skill lowering) target.** No native agent primitive is assumed. All 20 canonical agents lower to skills at `.warp/skills/<name>/SKILL.md` per the role-skill rules in `products/kyber-squad/profiles/fallbacks.yml` — see the target-specific note below for the exact projection rules.

## What is known (from the canonical source and the codebase)

- Strong detection marker: `.warp/`
- Alias(es): none
- The 20 canonical agents and 25 canonical skills this renderer must cover live under
  `products/kyber-squad/agents/*.md` and `products/kyber-squad/skills/*/SKILL.md`, loaded via
  `SquadSourceLoader.Load` (`src/KyberWeave.Core/Squad/Parsing/SquadSourceLoader.cs`) into a
  `SquadSource` — the same model `CopilotRenderer` renders from.
- Per-agent model resolution and per-agent capability/permission profiles are declared in
  `products/kyber-squad/profiles/models.yml` and `profiles/capabilities.yml`. Neither file is
  target-specific by construction — check the target-specific note below for whether this
  target has real entries in `models.yml` yet.

## What is assumed and needs verification, not trusted as-is

Was mis-documented as a native target in `onboarding.md`'s target table until this pass corrected it — both `SquadRendererRegistry`'s validation (native-target roster: Codex, Cursor, Claude, Copilot, OpenCode, Kilo, Factory) and `FakeSquadRenderer`'s fallback classification (Gemini, Antigravity, Warp) agree Warp is fallback. Same role-skill lowering rules as Gemini apply.

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
- Confirm `kyber-weave squad install --target warp --dry-run` plans a file for every
  agent and skill this target should cover (native: 20 agents + 23 non-conductor skills = 43,
  matching Copilot's count, unless this target's own agent-primitive support differs;
  fallback: 25 skills plus role-lowered skills per the collision rules above).
- Confirm `kyber-weave squad doctor` reports `warp` under renderers available, not
  pending.
