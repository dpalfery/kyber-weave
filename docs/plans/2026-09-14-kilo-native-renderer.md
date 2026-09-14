---
id: plans/2026-09-14-kilo-native-renderer
title: Add a native Kilo renderer to Kyber-Squad
doc-type: plan
status: current
owner: dpalfery
last-reviewed: 2026-09-14
component: KyberSquad
---

# Add a native Kilo renderer to Kyber-Squad

**Status:** Ready  
**Date:** 2026-09-14  
**Goal:** Implement and register an `ISquadRenderer` for `SquadTarget.Kilo` (`kilo`) so `kyber-weave squad install --target kilo` succeeds with native Kilo agent and skill layouts, verified against repository safety and rendering invariants.

---

## 1. Problem / Motivation

`squad install --target kilo` and `squad update --target kilo` fail during preflight before attempting any asset downloads or writes:
`SquadRendererRegistry` (`src/KyberWeave.Core/Squad/Rendering/SquadRendererRegistry.cs`) only dispatches targets registered in `SquadCommandComposition.ResolveRenderer()` (`src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs`). Currently, only `CopilotRenderer`, `CursorRenderer`, `ClaudeRenderer`, `AntigravityRenderer`, and `CodexRenderer` (and `OpenCodeRenderer` in progress) are wired.

Requesting `kilo` fails closed with:
```text
No renderer is implemented yet for target(s): kilo. See docs/todo/<target>.md for what is needed to add support. Targets available today: ...
```

Kilo is classified in the architecture and todo catalog as a **native agent target**:
- Strong detection marker: `.kilo/` (already present in `SquadTargetResolver.cs`)
- Canonical token: `kilo` (defined in `SquadTargetCatalog.cs`)
- Native agent primitive: `.kilo/agents/<name>.md`
- Native skill primitive: `.kilo/skills/<name>/SKILL.md`
- Canonical corpus: 21 agents and 24 skills in `products/kyber-squad/`, loaded via `SquadSourceLoader.Load`

The system lacks an `ISquadRenderer` implementation for Kilo, registration in CLI composition, test coverage, and documentation alignment.

---

## 2. Approved decisions

- **D1 (Target and File Layouts):**
  - Target: `SquadTarget.Kilo` (`kilo`).
  - Project-scope agent path: `.kilo/agents/<name>.md`.
  - Project-scope skill path: `.kilo/skills/<name>/SKILL.md`.
  - Linked resources for agents and skills projected beside principals via `SquadResourceProjection.Append` (`.kilo/agents/<name>/...`, `.kilo/skills/<name>/...`).
  - Strong detection marker: `.kilo/`.

- **D2 (Agent Frontmatter Schema):**
  - Agents are formatted in Markdown with YAML frontmatter bounded by `---`.
  - Required keys: `name: string`, `description: string`, `mode: string` (`primary` for conductor / primary agents, `subagent` for delegate subagents, mapped from `agent.Invocation`).
  - Optional key: `model: string` (resolved from `models.yml` for harness `kilo`; omitted when `inherit` or unresolved default).
  - Body: Verbatim `agent.InstructionBody` following frontmatter, normalized with LF line endings (`\n`) and guaranteed trailing newline.

- **D3 (Skill Frontmatter Schema & Single-Projection Rule):**
  - Skills are formatted in Markdown with YAML frontmatter bounded by `---` at `.kilo/skills/<name>/SKILL.md`.
  - Required keys: `name: string`, `description: string` (multi-line descriptions collapsed to single line), and `license: MIT`.
  - Single-projection rule: profile-declared shared identities (from `source.FallbackProfiles.Profiles.Values.SelectMany(p => p.SharedIdentities)`) suppress their skill projection. No `role-` prefixes are emitted on native targets.

- **D4 (Permissions & Degradation-over-guessing Approach):**
  - Rather than inventing unverified permission mappings from the semantic capability vocabulary (`filesystem.read`, `filesystem.write`, `process.execute`, `network.read`, `network.publish`, `delegate`) to Kilo, adhere to the established pattern in `CopilotRenderer` and `CursorRenderer`.
  - Capabilities that cannot be natively enforced without risk of silent widening are left at safe defaults, recording a `SquadDegradationRecord` with code `permission-not-expressible` or `safety-narrowed`.
  - Invariant: Every degradation record MUST carry `InstructionDigest: agent.BodyDigest` matching the canonical agent's SHA-256 digest.

- **D5 (Model Resolution & `models.yml`):**
  - Support model resolution from `products/kyber-squad/profiles/models.yml` for target key `kilo`.
  - Defined Kilo model tokens mapped per profile:
    - `deep-planning`: `glm5.3`
    - `general`: `muse-spark1.3 contributor`
    - `fast`: `spark1.3 contributor`
    - `mai-code-flash`: `kimi k2.7 code`
    - `orchestration`: `inherit` (only conductor inherits)
  - If `kilo` is not specified in a profile, fallback to profile default or `inherit`. If resolved value is `"inherit"`, omit `model` from frontmatter.

- **D6 (Deterministic Serialization):**
  - Serialization must be 100% deterministic and thread-safe using `SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, body)` with a serializer lock.

- **D7 (CLI Registration & Doctor):**
  - Register `new KiloRenderer()` in `SquadCommandComposition.ResolveRenderer()`.
  - Update `SquadDoctorCommand` tests and expectations to reflect `kilo` as an available renderer.

---

## 3. Investigation findings

### Live Source Verification
- `SquadTarget.cs`: `SquadTarget.Kilo` is already defined in `SquadTarget` enum and mapped to token `"kilo"` in `SquadTargetCatalog`.
- `SquadTargetResolver.cs`: Already contains `[SquadTarget.Kilo] = [new(".kilo", true)]` in marker detection.
- `SquadRendererRegistry.cs`: Line 165 classifies `SquadTarget.Kilo` as `isNative = true` (`target is ... SquadTarget.Kilo ...`). Native invariants automatically apply (rejecting `role-` prefixes, enforcing path containment, verifying SHA-256 digests).
- `models.yml`: Currently has no `kilo:` key; resolving Kilo models safely falls back to `default: inherit`, which omits the frontmatter key unless explicitly configured.
- Canonical corpus: 21 agents and 24 skills under `products/kyber-squad/` with 68 linked resources, yielding 113 total files.

---

## 4. Task list

| # | Phase | Component | Description | Skills |
|---|-------|-----------|-------------|--------|
| T1 | Test-first | `KyberWeave.Tests` | Author `tests/KyberWeave.Tests/KiloRendererContractTests.cs` verifying supported targets, preflight failure for unsupported targets, guard rejecting non-Kilo targets, corpus rendering (113 files), deterministic output, agent/skill frontmatter, resource projection, and degradation digest integrity. | `test-dev`, `csharp-dev` |
| T2 | Core | `KyberWeave.Core` | Implement `KiloRenderer : ISquadRenderer` in `src/KyberWeave.Core/Squad/Rendering/KiloRenderer.cs` per D1–D6. | `csharp-dev` |
| T3 | Configuration | `KyberSquad` | Update `products/kyber-squad/profiles/models.yml` to declare `kilo:` fallback or default mappings if applicable. | `csharp-dev` |
| T4 | Composition | `KyberWeave.Cli` | Register `new KiloRenderer()` in `SquadCommandComposition.ResolveRenderer()` in `src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs`. Update remarks doc comment. | `csharp-dev` |
| T5 | CLI Tests | `KyberWeave.Tests` | Update `Doctor_ReportsRendererCoverageAndMcpProbeStatus` in `tests/KyberWeave.Tests/SquadCliCommandTests.cs` to assert `kilo` is reported under available renderers. | `test-dev`, `csharp-dev` |
| T6 | Docs / Hygiene | `docs` | Update `docs/kyber-squad/architecture.md` §8 and `docs/kyber-squad/onboarding.md` target tables; update `docs/todo/kyber-squad-renderer-coverage.md` and `docs/todo/kilo.md` status. | `app-docs-standard` |
| T7 | Verification | Gates | Execute test suite (`dotnet test`), code formatting (`dotnet format --verify-no-changes`), dry run (`kyber-weave squad install --target kilo --dry-run`), doctor check, and doc lint (`docs validate .`). | `csharp-dev`, `test-dev` |

### Task Details and Acceptance Criteria

#### Task 1: Contract Tests (`tests/KyberWeave.Tests/KiloRendererContractTests.cs`)
- Author test fixture mirroring `CursorRendererContractTests.cs` / `ClaudeRendererContractTests.cs`.
- Validate:
  - `SupportedTargets_IsExactlyKilo`: Registry reports exactly `[SquadTarget.Kilo]`.
  - `RenderAsync_Guard_RejectsNonKiloTarget`: Passing any target other than `SquadTarget.Kilo` throws `ArgumentException`.
  - `RenderAsync_Kilo_RendersTheRealCanonicalCorpus`: 113 files rendered (45 principals + 68 resources); agents at `.kilo/agents/<name>.md`, skills at `.kilo/skills/<name>/SKILL.md`.
  - Frontmatter properties: agent has `name`, `description`, optional `model`; skill has `name`, `description`, `license: MIT`.
  - Degradation records: any degradation emitted has `InstructionDigest == agent.BodyDigest`.
  - Deterministic: consecutive renders match byte-for-byte.

#### Task 2: Core Implementation (`src/KyberWeave.Core/Squad/Rendering/KiloRenderer.cs`)
- Class `KiloRenderer : ISquadRenderer` in namespace `KyberWeave.Core.Squad.Rendering`.
- `SupportedTargets` returns `[SquadTarget.Kilo]`.
- Implements `RenderAsync`:
  - Validates single target `SquadTarget.Kilo`.
  - Loads source via `SquadSourceLoader.Load(request.SourceDirectory)`.
  - Emits agents to `.kilo/agents/<name>.md` and skills to `.kilo/skills/<name>/SKILL.md`.
  - Emits linked resources via `SquadResourceProjection.Append`.
  - Emits YAML frontmatter and body via `SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, body)`.
  - Adheres to `SerializerLock` thread safety.

#### Task 3: Configuration (`products/kyber-squad/profiles/models.yml`)
- Ensure profile models handle Kilo cleanly (inherit or designated model aliases).

#### Task 4: CLI Composition (`src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs`)
- Add `new KiloRenderer()` to `SquadRendererRegistry` instantiation in `ResolveRenderer()`.
- Update XML documentation comment.

#### Task 5: CLI Tests (`tests/KyberWeave.Tests/SquadCliCommandTests.cs`)
- Update `Doctor_ReportsRendererCoverageAndMcpProbeStatus` to verify `kilo` appears in available renderers and is not listed under not yet implemented.

#### Task 6: Documentation and Todo Updates
- Update `docs/kyber-squad/architecture.md` §8 to document `KiloRenderer` (.kilo/agents/*.md, .kilo/skills/*/SKILL.md).
- Update `docs/kyber-squad/onboarding.md` target roster.
- Move `kilo` in `docs/todo/kyber-squad-renderer-coverage.md` from gap table to covered list.
- Update status in `docs/todo/kilo.md`.

#### Task 7: Verification Harness
- Run solution-wide unit tests, code formatting checks, CLI dry-run smoke test, and doc validation.

---

## 5. Sequencing / dependency graph

```text
T1 (Contract tests - red)
 └─► T2 (Core KiloRenderer implementation)
      ├─► T3 (models.yml profile updates)
      └─► T4 (CLI composition registration)
           ├─► T5 (SquadCliCommandTests doctor update)
           ├─► T6 (Documentation and todo updates)
           └─► T7 (Full verification gate)
```

---

## 6. Residual decisions / risks

| Risk | Mitigation |
|---|---|
| Kilo-specific permission vocabulary unverified | Follow Copilot/Cursor degradation-over-guessing pattern: avoid synthetic widening, emit safe defaults, and record `permission-not-expressible` or `safety-narrowed` degradations with canonical SHA-256 digests. |
| Model token compatibility | Fall back to `inherit` (omitted `model` key in frontmatter) when no explicit Kilo model profile is configured. |

---

## 7. Out of scope

- Implementing other pending targets (`factory`, `warp`, `pi`).
- Modifying canonical agent instructions or skill scripts.
- Modifying harness detection logic (already present in `SquadTargetResolver`).

---

## 8. Required skills

- `csharp-dev` — C# core renderer implementation, composition wiring, and model resolution.
- `test-dev` — Contract testing, CLI command tests, and test assertions.
- `app-docs-standard` — Documentation ontology compliance and updates.

---

## 9. Verification harness

1. **Unit & Contract Tests:** `dotnet test --filter KiloRendererContractTests` passes 100%. All sibling renderer contract tests continue to pass.
2. **CLI & Doctor Tests:** `dotnet test --filter SquadCliCommandTests` passes with `kilo` confirmed available.
3. **Build & Quality Gates:** `dotnet build -c Release` with 0 warnings/errors (`TreatWarningsAsErrors`); `dotnet format --verify-no-changes` passes.
4. **Dry Run Smoke:** `kyber-weave squad install --target kilo --dry-run` reports 113 planned deployment files.
5. **Documentation Integrity:** `kyber-weave docs validate .` and `kyber-weave docs drift .` pass cleanly.
