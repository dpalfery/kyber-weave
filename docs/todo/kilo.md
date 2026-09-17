---
id: todo/kilo
title: Add a native Kilo renderer to Kyber-Squad
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-14
status: superseded
---

# Add a native Kilo renderer to Kyber-Squad

> [!NOTE]
> **Status: Completed**  
> This todo has been completed and superseded by implementation plan
> [docs/plans/2026-09-14-kilo-native-renderer.md](../plans/2026-09-14-kilo-native-renderer.md).
>
> **Implementation summary:**
> - **Core Renderer:** [`KiloRenderer.cs`](../../src/KyberWeave.Core/Squad/Rendering/KiloRenderer.cs) implements `ISquadRenderer` for `SquadTarget.Kilo`, projecting 21 native agents to `.kilo/agents/<name>.md` and 24 skills to `.kilo/skills/<name>/SKILL.md` along with linked resource closures (113 files total). On native targets like Kilo, agents and skills have separate directory structures (`.kilo/agents/` and `.kilo/skills/`), so all 21 agents and all 24 skills (plus 68 resources = 113 files) are emitted. `shared-identities` in `fallbacks.yml` is empty (`[]`), so no skills are suppressed (the 7 corpus collisions only trigger `role-` prefixes on fallback targets where agents are lowered into skills).
> - **Contract Tests:** [`KiloRendererContractTests.cs`](../../tests/KyberWeave.Tests/KiloRendererContractTests.cs) validates supported targets, non-Kilo target guards, canonical corpus rendering, deterministic serialization, frontmatter schemas, and degradation SHA-256 digest invariants.
> - **CLI Wiring:** Registered in `SquadCommandComposition.ResolveRenderer()` (`src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs`) and verified via `SquadCliCommandTests.cs` doctor assertions.

This is **context for planning the work, not a plan** — it states what is known, what is
assumed and unverified, and where the seam is. It does not sequence tasks or commit to an
implementation.

## Why this exists

Historically, `squad install --target kilo` failed in preflight before any network call
because `SquadRendererRegistry` only had renderers for Copilot, Cursor, Claude, Codex,
Antigravity, and OpenCode. This gap is resolved by `KiloRenderer`
(`src/KyberWeave.Core/Squad/Rendering/KiloRenderer.cs`). See
[architecture.md §8](../kyber-squad/architecture.md#8-rendering) for how the render pipeline
as a whole works, and
[onboarding.md](../kyber-squad/onboarding.md#harness-targets-and-auto-detection) for the
full target roster and its current coverage.

## Classification

**Native agent target.** Canonical agents render as this harness's own native agent primitive (Markdown with YAML frontmatter) at `.kilo/agents/<name>.md`. All 24 canonical skills render as harness skills at `.kilo/skills/<name>/SKILL.md`. Because agents and skills occupy separate directory structures (`.kilo/agents/` and `.kilo/skills/`), and `shared-identities` in `fallbacks.yml` is empty (`[]`), all 21 agents and all 24 skills are emitted without suppressing any skill files or colliding (the seven distinct-body collisions only apply to fallback targets where agents are lowered into skills).

## What is known (from the canonical source and the codebase)

- Strong detection marker: `.kilo/`
- Alias(es): none
- The 21 canonical agents and 24 canonical skills this renderer must cover live under
  `products/kyber-squad/agents/*.md` and `products/kyber-squad/skills/*/SKILL.md`, loaded via
  `SquadSourceLoader.Load` (`src/KyberWeave.Core/Squad/Parsing/SquadSourceLoader.cs`) into a
  `SquadSource` — the same model `CopilotRenderer` renders from.
- Per-agent model resolution and per-agent capability/permission profiles are declared in
  `products/kyber-squad/profiles/models.yml` and `profiles/capabilities.yml`. Neither file is
  target-specific by construction — check the target-specific note below for whether this
  target has real entries in `models.yml` yet.

## What is assumed and needs verification, not trusted as-is

`products/kyber-squad/profiles/models.yml` has no `kilo:` entry in any profile, so every agent would resolve to `inherit` (omit `model`) under the current profiles — confirm whether Kilo needs an explicit model token before shipping, since an always-omitted field may just mean the profiles are incomplete rather than that omission is correct.

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
  native projection rules (separate directories for agents and skills, all 24 canonical skills emitted because `shared-identities` is empty, and no `role-` prefixes because Kilo supports native agents),
  and every degradation's `InstructionDigest` matching the
  named agent's real `SquadAgent.BodyDigest`. See `SquadRendererRegistry.ValidateRenderResult`
  for the exact checks — this runs against every renderer, not something to reimplement.

## How to verify

- Reuse the pattern in `tests/KyberWeave.Tests/SquadRenderingContractTests.cs`: render the
  real, checked-in `products/kyber-squad` corpus (not a synthetic fixture) through the new
  renderer, and assert against the *loaded* `SquadSource` model rather than hardcoded
  literals, so the test can't silently drift from the canonical source it's supposed to be
  checking.
- Confirm `kyber-weave squad install --target kilo --dry-run` plans a file for every
  agent and skill this target should cover (native: 21 agents + 24 skills = 45 principals plus
  68 resources = 113 files, matching Copilot's native count; in contrast to fallback targets
  where 21 role-lowered agents and 24 skills result in seven `role-` collisions).
- Confirm `kyber-weave squad doctor` reports `kilo` under renderers available, not
  pending.
