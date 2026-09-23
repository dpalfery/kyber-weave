---
id: archive/todo/factory
title: Add a native Factory (factory-droids) renderer to Kyber-Squad
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-23
status: superseded
---

# Add a native Factory (factory-droids) renderer to Kyber-Squad

**Status:** Superseded and archived
**Archive Date:** 2026-09-23

`FactoryRenderer` is implemented and registered.

---

> [!NOTE]
> **Superseded by implementation plan:** This todo has been superseded by plan
> [docs/archive/plans/2026-09-14-factory-native-renderer.md](../plans/2026-09-14-factory-native-renderer.md),
> which defined the first `FactoryRenderer`. Custom-droid paths and `--global` were
> corrected by
> [docs/archive/plans/2026-09-16-factory-renderer-droids-and-global.md](../plans/2026-09-16-factory-renderer-droids-and-global.md).

This is **context for planning the work, not a plan** — it states what is known, what is
assumed and unverified, and where the seam is. It does not sequence tasks or commit to an
implementation.

## Why this exists

Historically, `squad install --target factory` failed in preflight before any network call
because `SquadRendererRegistry` only had renderers for Copilot, Cursor, Claude, Codex,
Antigravity, and OpenCode. This gap is addressed by `FactoryRenderer`
(`src/KyberWeave.Core/Squad/Rendering/FactoryRenderer.cs`).
See [architecture.md §8](../../kyber-squad/architecture.md#8-rendering) for how the render pipeline
as a whole works, and [onboarding.md](../../kyber-squad/onboarding.md#harness-targets-and-auto-detection)
for the full target roster and its current coverage.

## Classification

**Native agent target.** Canonical agents render as Factory custom droids (Markdown with YAML frontmatter) at `.factory/droids/<name>.md`. Personal / `--global` droids are `droids/<name>.md` under `~/.factory`. All canonical skills render as harness skills at `.factory/skills/<name>/SKILL.md` (personal `~/.factory/skills/<name>/SKILL.md`). Profile-declared shared identities suppress their skill projections. Squad does not write `.factory/agents/`, `~/.agents/skills/`, or `~/.agent/skills/`.

## What is known (from the canonical source and the codebase)

- Strong detection marker: `.factory/`
- Alias(es): `factory-droids`
- The 21 canonical agents and 24 canonical skills this renderer must cover live under
  `products/kyber-squad/agents/*.md` and `products/kyber-squad/skills/*/SKILL.md`, loaded via
  `SquadSourceLoader.Load` (`src/KyberWeave.Core/Squad/Parsing/SquadSourceLoader.cs`) into a
  `SquadSource` — the same model `CopilotRenderer` renders from.
- Per-agent model resolution and per-agent capability/permission profiles are declared in
  `products/kyber-squad/profiles/models.yml` and `profiles/capabilities.yml`. Neither file is
  target-specific by construction — check the target-specific note below for whether this
  target has real entries in `models.yml` yet.

## What is assumed and needs verification, not trusted as-is

`products/kyber-squad/profiles/models.yml` has no `factory:` entry either — same open question as Kilo.

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
- **Permissions**: Factory documents omit-`tools` as allow-all, so Squad always emits an
  explicit YAML array of documented tool IDs and never emits `tools: all`. Only `allow`
  grants a tool; `ask` withholds and records `safety-narrowed`. Unmapped `network.publish`
  and `delegate`, plus `mcpServers: []` (parent MCP not inherited), record
  `permission-not-expressible`. Do not invent Factory keys (`permission`, `permissions`)
  or MCP server names.
- **Validation will hold this renderer to the same invariants as Copilot's**: portable output
  paths contained under the extraction root, only requested targets in the output, the
  native/fallback projection rules (seven distinct-body collisions and no shared identities),
  and every degradation's `InstructionDigest` matching the
  named agent's real `SquadAgent.BodyDigest`. See `SquadRendererRegistry.ValidateRenderResult`
  for the exact checks — this runs against every renderer, not something to reimplement.

## How to verify

- Reuse the pattern in `tests/KyberWeave.Tests/SquadRenderingContractTests.cs`: render the
  real, checked-in `products/kyber-squad` corpus (not a synthetic fixture) through the new
  renderer, and assert against the *loaded* `SquadSource` model rather than hardcoded
  literals, so the test can't silently drift from the canonical source it's supposed to be
  checking.
- Confirm `kyber-weave squad install --target factory --dry-run` plans a file for every
  agent and skill this target should cover at `.factory/droids/<name>.md` and
  `.factory/skills/<name>/SKILL.md` (never `.factory/agents/`).
- Confirm `kyber-weave squad doctor` reports `factory` under renderers available, not
  pending.
