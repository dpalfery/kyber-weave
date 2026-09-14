---
id: todo/pi
title: Add Pi as a Kyber-Squad harness target
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-12
status: draft
---

# Add Pi as a Kyber-Squad harness target

This is **context for planning the work, not a plan** — it states what is known, what is
assumed and unverified, and where the seam is. It does not sequence tasks or commit to an
implementation.

## Why this exists

Pi (the `pi` coding agent from `badlogic/pi-mono`) is not a Kyber-Squad target. It differs
from the other renderer todos: [warp.md](warp.md), [kilo.md](kilo.md),
[opencode.md](opencode.md), and [factory.md](factory.md) all cover **declared** targets that
have no renderer. Pi is not declared at all. `SquadTarget`
(`src/KyberWeave.Core/Squad/Deployment/SquadTarget.cs`) has no `Pi` member, and
`SquadTargetCatalog` has no `pi` token. `squad install --target pi` therefore fails in argument
parsing with "Unknown Squad target", not in the coverage preflight that points to
`docs/todo/<target>.md`. The README's harness table, the
[onboarding target roster](../kyber-squad/onboarding.md#harness-targets-and-auto-detection),
and every "nine harnesses" count across the Kyber-Squad docs leave it out.

Pi does appear in the repository, but only in KyberDash. The vendored codeburn provider
(`dash/src/providers/pi.ts`) reads session JSONL from `~/.pi/agent/sessions/`, and the
[refresh pipeline plan](../archive/plans/2026-09-06-kyberdash-refresh-pipeline.md) lists it as a
registered source. That reader observes Pi's telemetry. It deploys nothing to Pi, so it does
not count as harness support.

## Classification

**Fallback (role-skill lowering) target, provisionally.** Pi's coding-agent README
(`github.com/badlogic/pi-mono`, `packages/coding-agent/README.md`, read 2026-09-12) says Pi
skips sub-agents and plan mode by design, and suggests extensions or spawning more `pi`
processes instead. With no native agent primitive to render to, all 21 canonical agents would
lower to skills under the rules in `products/kyber-squad/profiles/fallbacks.yml`, the same way
Antigravity and Warp do.

## What is known (from the canonical source, the codebase, and Pi's README)

- Skills are `SKILL.md` directories. According to the README, Pi discovers them from
  `~/.pi/agent/skills/`, `~/.agents/skills/`, `.pi/skills/`, and `.agents/skills/`, walking up
  through parent directories. The comment at `dash/src/providers/pi.ts:41` names the same
  roots.
- Project configuration lives under `.pi/` (`.pi/settings.json`, `.pi/SYSTEM.md`,
  `.pi/prompts/`). Pi also loads `AGENTS.md` or `CLAUDE.md` as context files.
- Pi has no built-in permission model. The README says "No permission popups" and leaves
  confirmation gates to extensions.
- The 21 canonical agents and 24 canonical skills live under `products/kyber-squad/agents/*.md`
  and `products/kyber-squad/skills/*/SKILL.md`. `SquadSourceLoader.Load`
  (`src/KyberWeave.Core/Squad/Parsing/SquadSourceLoader.cs`) loads them into a `SquadSource`.
- `products/kyber-squad/profiles/models.yml` has no `pi:` entry, and the model-profile schema
  has no `pi` field.

## What is assumed and needs verification, not trusted as-is

- **The facts above come from one README read, not Pi's full docs.** Before shipping, confirm
  the discovery roots, the precedence between `.pi/skills/` and `.agents/skills/`, and the
  `SKILL.md` frontmatter Pi accepts. Pi moves quickly, so record what was verified and when in
  the renderer's doc comment, as `CopilotRenderer` and `AntigravityRenderer` do.
- **Output directory: `.pi/skills/` or `.agents/skills/`.** `AntigravityRenderer` already writes
  lowered skills to `.agents/skills/`. If the README is right that Pi reads that directory,
  `--target antigravity` may already expose the squad to Pi as an unrecorded side effect. A Pi
  renderer that also wrote `.agents/skills/` would produce the same relative paths as
  Antigravity. `SquadRendererRegistry.ValidatePortableOutputIdentities` keys collisions by
  target plus path, so it would not catch two targets claiming one file. How the deployment
  layer (receipts, leaf claims, compare-and-restore) handles a path owned by two targets is
  unverified. `.pi/skills/` avoids that question but duplicates the skills when both targets
  are installed, and Pi's handling of duplicate skill names across roots is also unverified.
- **Detection marker.** A project `.pi/` directory is the obvious strong marker, but it exists
  only if the user created project-level Pi configuration. `~/.pi/` is global and must not
  activate a project target. `.agents/skills/` is already a negative fixture and must stay one.
  Pi may need the same explicit-target-only treatment Antigravity gets.
- **Model tokens.** Pi is provider-agnostic, so the model token format for any `pi:` entry in
  `models.yml` is unknown. Omitting it (`inherit`) is likely right, but confirm rather than
  assume.
- **Whether "fallback" is the right long-term class.** Pi extensions or packages that add
  sub-agents might eventually justify a native projection. Do not target an extension's format
  unless Pi ships it as a first-party convention.

## The code seam

Pi needs a catalog entry before it can have a renderer.

**Declare the target.** These are the places a target is registered today; grep for `Warp`
across `src/`, `tests/`, and `products/kyber-squad/schemas/` to catch any that have moved:

- `SquadTarget` enum, `SquadTargetCatalog.ApprovedTargets`, `TargetsByToken`, and `GetToken`
  (`src/KyberWeave.Core/Squad/Deployment/SquadTarget.cs`). Adding a token is additive, but
  receipts persist target tokens, so choose `pi` once and keep it.
- `SquadTargetResolver.Markers` (`src/KyberWeave.Core/Squad/Deployment/SquadTargetResolver.cs`),
  or leave it out for explicit-only activation, as Antigravity does.
- `SquadSourceLoader.ModelProfileFields` and
  `products/kyber-squad/schemas/model-profiles.schema.json`.
- The target lists in the `--target` descriptions in
  `src/KyberWeave.Cli/Commands/Squad/SquadSettings.cs`.
- `SquadPacker`'s target-tree exclusions (`src/KyberWeave.Core/Squad/Packaging/SquadPacker.cs`),
  if the output lands under `.pi/`.
- Test rosters: `tests/KyberWeave.Tests/SquadTargetResolutionTests.cs` and the fallback
  classification in `tests/KyberWeave.Tests/Fakes/FakeSquadRenderer.cs`.

**Render it.**

- Implement `ISquadRenderer` (`src/KyberWeave.Core/Squad/Rendering/SquadRenderModels.cs`).
  `AntigravityRenderer` is the closest reference, since it is the only fallback renderer in
  the codebase.
- Add Pi's cases to `SquadRendererRegistry.AgentOutputPath` and `SkillOutputPath`. Leave Pi out
  of the `isNative` roster, so role-prefixed outputs are allowed.
- Register the renderer in `SquadCommandComposition.ResolveRenderer()`
  (`src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs`).
- **Permissions**: Pi has nothing to map the capability vocabulary onto. As with Antigravity's
  instruction-only skills, record a `permission-not-expressible` `SquadDegradationRecord` for
  every non-deny decision rather than implying enforcement.

**Update the docs.** Add a row to the README harness table and the onboarding target roster,
change "nine" to the new count wherever it appears (`README.md`, `docs/kyber-squad/README.md`,
`docs/kyber-squad/architecture.md`, `docs/kyber-squad/onboarding.md`,
`docs/kyber-squad/requirements.md`, `products/kyber-squad/README.md`), and add Pi to the
[renderer coverage index](kyber-squad-renderer-coverage.md). Re-run `docs validate` and
`docs drift`.

## How to verify

- Follow the pattern in `tests/KyberWeave.Tests/AntigravityRendererContractTests.cs`. Render
  the real, checked-in `products/kyber-squad` corpus and assert against the loaded
  `SquadSource`, not hardcoded literals.
- Confirm `kyber-weave squad install --target pi --dry-run` plans 45 files: 24 skills plus 21
  role-lowered agents, with seven `role-` collisions and the other identities emitted under
  their own names.
- Confirm `kyber-weave squad install --target antigravity,pi` either deploys cleanly or fails
  with a clear message. It must not let one target silently overwrite or orphan the other's
  files.
- Confirm `kyber-weave squad doctor` lists `pi` among the available renderers.
- In a scratch project with `pi` installed, check that the deployed skills appear in Pi's skill
  list and that a role-lowered agent loads when invoked.
