---
id: archive/plans/2026-09-14-opencode-native-renderer
title: Add a native OpenCode renderer to Kyber-Squad
doc-type: plan
status: archived
owner: dpalfery
last-reviewed: 2026-09-24
component: KyberSquad
---

# Add a native OpenCode renderer to Kyber-Squad

**Status:** Archived
**Archive Date:** 2026-09-24
**Closeout:** Delivered by #90 (`OpenCodeRenderer`); archived 2026-09-24 — it was marked Completed but never moved.
**Date:** 2026-09-14  
**Goal:** Implement and register an `ISquadRenderer` for `SquadTarget.OpenCode` (`opencode`) so `kyber-weave squad install --target opencode` succeeds with native OpenCode agent and skill layouts, verified against OpenCode specifications and repository safety invariants.

---

## 1. Problem / Motivation

`squad install --target opencode` and `squad update --target opencode` fail in preflight before downloading release assets or performing file writes:
`SquadRendererRegistry` (`src/KyberWeave.Core/Squad/Rendering/SquadRendererRegistry.cs`) only dispatches targets claimed by registered renderers. Currently, `SquadCommandComposition.ResolveRenderer()` (`src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs`) registers `CopilotRenderer`, `CursorRenderer`, `ClaudeRenderer`, `AntigravityRenderer`, and `CodexRenderer`.

Requesting `opencode` fails closed with:

```text
No renderer is implemented yet for target(s): opencode. See docs/todo/<target>.md for what is needed to add support. Targets available today: antigravity, claude, codex, copilot, cursor.
```

OpenCode is classified in the architecture as a **native agent target**:
- Strong detection marker: `.opencode/`
- Target token: `opencode`
- Native agent primitive: `.opencode/agents/<name>.md`
- Native skill primitive: `.opencode/skills/<name>/SKILL.md`
- Target-specific model tokens already exist in `products/kyber-squad/profiles/models.yml` (`zai-coding-plan/glm-5.2`, `opencode-go/gpt-5.6-luna`, `opencode/big-pickle`).

The canonical source (21 agents, 24 skills) already loads cleanly into `SquadSource` through `SquadSourceLoader.Load`. The missing component is an `ISquadRenderer` implementation for OpenCode, registration in CLI composition, contract test suites, doctor CLI assertions, and canonical documentation updates.

---

## 2. Approved decisions

These decisions form the immutable implementation contract for the plan:

- **D1 (Target and File Layouts):**
  - Claim `SquadTarget.OpenCode` (`opencode`).
  - Project-scope agent path: `.opencode/agents/<name>.md` (stem matches canonical agent name).
  - Project-scope skill path: `.opencode/skills/<name>/SKILL.md` (directory matches skill name).
  - Linked agent and skill resources are projected beside principals via `SquadResourceProjection.Append` (`.opencode/agents/<name>/...`, `.opencode/skills/<name>/...`).
  - Strong detection marker: `.opencode/` (already present in `SquadTargetResolver.cs`).

- **D2 (Agent Frontmatter Schema):**
  - Agent files are authored in Markdown with YAML frontmatter bounded by `---`.
  - Required keys: `name: string`, `description: string`, `mode: "primary" | "subagent"` (derived from `agent.Invocation`: `primary` for `conductor`, `subagent` for all others).
  - Optional key: `model: string` (resolved from `models.yml` for target `opencode`; omitted when `inherit` or unresolved default).
  - Explicit `permission` key: Map of granted permissions (see D4 and D5).
  - Body: Verbatim `agent.InstructionBody` following the closing `---`, normalized to LF line endings (`\n`) with guaranteed trailing newline.

- **D3 (Skill Frontmatter Schema & Single-Projection Rule):**
  - Skill files are authored in Markdown with YAML frontmatter bounded by `---` at `.opencode/skills/<name>/SKILL.md`.
  - Required keys: `name: string`, `description: string` (multi-line descriptions collapsed to a single line with normalized whitespace), and `license: MIT`.
  - Conductor / Shared Identity Suppression: Profile-declared shared identities (from `source.FallbackProfiles.Profiles.Values.SelectMany(p => p.SharedIdentities)`) suppress their skill projection per the native single-projection rule enforced by `SquadRendererRegistry`. No `role-` prefixes are emitted on native targets.

- **D4 (Explicit Permissions Allowlist & Anti-Widening Invariant):**
  - OpenCode agent frontmatter MUST always emit an explicit `permission` map.
  - Omitting permissions inherits ambient/unrestricted tool access in OpenCode, causing silent permission widening for any canonical `deny` decision (violating KS-003 and anti-widening invariants).
  - Only capabilities with decision `allow` grant permissions; `ask` and `deny` withhold permissions.
  - Base ungoverned permissions included on every agent: `todowrite: allow`, `skill: allow`.

- **D5 (Capability-to-Permission Lowering Vocabulary):**
  - Grounded in OpenCode permission taxonomy:
    - `filesystem.read` -> `read: allow`
    - `filesystem.search` -> `grep: allow`, `glob: allow`
    - `filesystem.write` -> `edit: allow`
    - `process.execute` -> `bash: allow`
    - `network.read` -> `webfetch: allow`, `websearch: allow`
    - `network.publish` -> withheld (no built-in publish tool; recorded as `permission-not-expressible`)
    - `delegate` -> `task: allow` (or pattern map `task: { <delegate>: allow, ... }` when `agent.DelegatesTo` is non-empty)
  - Key ordering in serialized output is strictly deterministic: `todowrite`, `skill`, `read`, `grep`, `glob`, `edit`, `bash`, `webfetch`, `websearch`, `kyber-weave_*`, `task`.

- **D6 (Structured Degradation Accounting):**
  - `ask` decisions: OpenCode subagent frontmatter does not support an interactive per-tool confirmation gate. Any capability configured as `ask` is narrowed to withhold the tool (`safety-narrowed`) and recorded as a `SquadDegradationRecord` with code `safety-narrowed`.
  - Unexpressible capabilities: `network.publish: allow` and nested subagent delegation roster constraints (where `agent.DelegatesTo` is non-empty) are recorded with code `permission-not-expressible`.
  - Invariant: Every degradation record MUST carry `InstructionDigest: agent.BodyDigest` exactly matching the canonical agent's SHA-256 digest (enforced by `SquadRendererRegistry.ValidateRenderResult`).

- **D7 (Model Resolution from `models.yml`):**
  - Resolve agent model from `models.yml` under harness key `opencode`.
  - Canonical profiles:
    - `deep-planning` -> `zai-coding-plan/glm-5.2`
    - `fast` -> `opencode-go/gpt-5.6-luna`
    - `general` -> `zai-coding-plan/glm-5.2`
    - `orchestration` -> `opencode/big-pickle`
  - Fallback logic: If profile has no `opencode` entry, use `profile.Default` unless `profile.Default == "inherit"`. If resolved value is `"inherit"`, omit `model` from frontmatter.

- **D8 (MCP Server Integration):**
  - OpenCode configures MCP servers project-wide via `.opencode/mcp.json`.
  - When `filesystem.read: allow` and the agent is not a pure orchestrator (`agent.CapabilityProfile == "orchestrator"` or shared identity), server-scoped MCP access for the declared `kyber-weave` server is granted via `"kyber-weave_*": allow`; orchestrators have MCP tools withheld. Any unexpressible per-agent MCP isolation is recorded under `permission-not-expressible`.

- **D9 (Deterministic Serialization & Shared Document Assembly):**
  - Serialization must be 100% deterministic and thread-safe. YamlDotNet serialization takes a lock (`SerializerLock`) around `YamlSerializer`.
  - Permissions are serialized as an ordered YAML map with stable key ordering.
  - Document composition delegates to `SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, body)` ensuring normalized LF line endings (`\n`) and trailing newline.

---

## 3. Investigation findings

### Codebase Seam

- **Renderer Port & Models:** `src/KyberWeave.Core/Squad/Rendering/SquadRenderModels.cs` defines `ISquadRenderer`, `SquadRenderRequest`, `SquadRenderResult`, and `SquadDegradationRecord`.
- **Target Roster:** `src/KyberWeave.Core/Squad/Deployment/SquadTarget.cs` defines `SquadTarget.OpenCode` with token `"opencode"` and alias mapping.
- **Target Detection:** `src/KyberWeave.Core/Squad/Deployment/SquadTargetResolver.cs` maps strong marker `.opencode/` to `SquadTarget.OpenCode`.
- **Registry & Validation:** `src/KyberWeave.Core/Squad/Rendering/SquadRendererRegistry.cs` line 165 classifies `OpenCode` as `isNative = true`. It enforces:
  - Valid portable paths contained within root.
  - No `role-` prefixes on native targets.
  - Single-projection rule for shared identities.
  - SHA-256 `InstructionDigest` match between degradation record and `agent.BodyDigest`.
  - Zero permission widening.
- **CLI Composition:** `src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs` `ResolveRenderer()` builds the `SquadRendererRegistry`.
- **Reference Siblings:**
  - `ClaudeRenderer.cs`: Native Markdown + YAML frontmatter, explicit tools flow sequence, degradation records for `safety-narrowed` and `permission-not-expressible`.
  - `CursorRenderer.cs`: Native Markdown + YAML frontmatter, `readonly` boolean lowering, degradation records.
  - `CodexRenderer.cs`: Native TOML agents + YAML frontmatter skills.
  - `FakeSquadRenderer.cs`: Test fake already maps `SquadTarget.OpenCode` to `.opencode/agents/{name}.md` and `.opencode/skills/{name}/SKILL.md`.
- **CLI Doctor:** `src/KyberWeave.Cli/Commands/Squad/SquadDoctorCommand.cs` reports available vs pending renderers dynamically from `ISquadRenderer.SupportedTargets`.

### OpenCode Runtime Context

- `dash/src/providers/opencode.ts` documents the OpenCode tool taxonomy: `bash`, `read`, `edit`, `write`, `glob`, `grep`, `task`, `fetch`, `search`, `todo`, `skill`, `patch`.
- `products/kyber-squad/profiles/models.yml` contains active `opencode:` model identifiers for all profiles (`zai-coding-plan/glm-5.2`, `opencode-go/gpt-5.6-luna`, `opencode/big-pickle`).
- `products/kyber-squad/profiles/fallbacks.yml` has `shared-identities: []`.
- Canonical corpus: 21 agents and 24 skills under `products/kyber-squad/bundles/full.yml`.
- With C3 resource projection, total projected file count is 113 files (45 principals + 68 resources), matching Copilot. Expected file count formula:
  `source.Agents.Count + source.Agents.Sum(a => a.Resources.Count) + (source.Skills.Count - suppressedSkillCount) + source.Skills.Where(...).Sum(s => s.Resources.Count)`.

---

## 4. Task list

Each task defines an objective, exact files and symbols, acceptance criteria, required skills, and dependencies.

| # | Phase | Component | Description | Skills |
|---|-------|-----------|-------------|--------|
| T1 | Test-first | `KyberWeave.Tests` | Author `OpenCodeRendererContractTests.cs` validating all contracts: supported targets, preflight failure for unsupported targets, guard rejecting non-OpenCode targets, full corpus rendering, deterministic output, resource projection, agent frontmatter (`name`, `description`, `model`, `tools`), tool allowlist ordering and lowering, skill frontmatter (`name`, `description`, `license: MIT`), shared identity suppression, and degradation records with valid SHA-256 digests. | `test-dev`, `csharp-dev` |
| T2 | Implementation | `KyberWeave.Core` | Implement `OpenCodeRenderer : ISquadRenderer` in `src/KyberWeave.Core/Squad/Rendering/OpenCodeRenderer.cs` per D1–D9. Include XML doc comments citing specification dates. Implement `SupportedTargets`, `RenderAsync`, `RenderAgent`, `RenderSkill`, `ResolveOpenCodeModel`, `ResolveTools`, and `BuildDegradationRecords`. Use `SquadMarkdownDocument.Compose` and `SerializerLock`. Ensure zero warnings under `TreatWarningsAsErrors`. | `csharp-dev` |
| T3 | Composition | `KyberWeave.Cli` | Register `new OpenCodeRenderer()` in `SquadCommandComposition.ResolveRenderer()` in `src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs`. Update method XML documentation to include OpenCode in the list of native renderers. | `csharp-dev` |
| T4 | CLI Tests | `KyberWeave.Tests` | Update `Doctor_ReportsRendererCoverageAndMcpProbeStatus` in `tests/KyberWeave.Tests/SquadCliCommandTests.cs`: assert `opencode` is present in "Renderers available:" and absent from "Not yet implemented:". | `test-dev`, `csharp-dev` |
| T5 | Docs | `docs/kyber-squad` | Update `docs/kyber-squad/architecture.md` §8 to document `OpenCodeRenderer` (`.opencode/agents/*.md`, `.opencode/skills/*/SKILL.md`) under native renderers and update coverage summary. Update `docs/kyber-squad/onboarding.md` target table and renderer coverage section to mark `opencode` as "Implemented and registered". | `app-docs-standard` |
| T6 | Todo / Hygiene | `docs/todo` | Update `docs/todo/kyber-squad-renderer-coverage.md` to move `opencode` from remaining gaps table to covered renderers list. Update `docs/todo/opencode.md` frontmatter status to completed/retired or archive note. Update `docs/todo/README.md` if necessary. | `app-docs-standard` |
| T7 | Verification | Gates | Execute full verification harness: `dotnet format --verify-no-changes`, `dotnet build -c Release`, `dotnet test`, dry-run smoke test `kyber-weave squad install --target opencode --dry-run`, `kyber-weave squad doctor` verification, and documentation validation with `kyber-weave docs validate .` and `kyber-weave docs drift .`. | `csharp-dev`, `test-dev` |

### Per-task acceptance criteria

#### Task 1: Contract Tests (`tests/KyberWeave.Tests/OpenCodeRendererContractTests.cs`)

- New test class `OpenCodeRendererContractTests` implements `IDisposable`.
- Tests:
  - `SupportedTargets_IsExactlyOpenCode`: Registry with `OpenCodeRenderer` reports exactly `[SquadTarget.OpenCode]`.
  - `RenderAsync_UnsupportedTarget_FailsBeforeAnyRendererRuns`: Multi-target request with an unsupported target fails closed without side-effects.
  - `RenderAsync_Guard_RejectsNonOpenCodeTarget`: Invoking `OpenCodeRenderer.RenderAsync` directly with non-OpenCode target throws `ArgumentException`.
  - `RenderAsync_OpenCode_RendersTheRealCanonicalCorpus`: Renders real `products/kyber-squad` corpus:
    - Asserts total file count matches derived formula (45 principals + 68 resources = 113 files).
    - Every file starts with `.opencode/agents/` or `.opencode/skills/`.
    - Every agent file has valid YAML frontmatter with `name`, `description`, `mode` (`primary` or `subagent`), `model` (matching `models.yml`), and explicit `permission` map.
    - Every skill file has valid YAML frontmatter with `name`, `description` (single-line), and `license: MIT`.
    - Conductor / shared identity skill suppression verified if shared identities are configured.
    - Exact instruction body preserved with LF line endings.
    - Degradation records produced for `safety-narrowed` and `permission-not-expressible`; every record's `InstructionDigest` equals `agent.BodyDigest`.
  - `RenderAsync_IsDeterministic`: Successive renders produce byte-identical files and identical degradation records.
  - `RenderAsync_OpenCode_ProjectsLinkedAgentAndSkillResourcesDeterministically`: Resources are projected beside parent agents/skills.
- Fails initially before Task 2 (Red).

#### Task 2: Core Implementation (`src/KyberWeave.Core/Squad/Rendering/OpenCodeRenderer.cs`)

- Implements `ISquadRenderer`.
- `SupportedTargets` is `[SquadTarget.OpenCode]`.
- Implements `RenderAsync`:
  - Validates target set contains only `SquadTarget.OpenCode`.
  - Renders each `SquadAgent` to `.opencode/agents/<name>.md`.
  - Renders each non-suppressed `SquadSkill` to `.opencode/skills/<name>/SKILL.md`.
  - Projects resources beside principals via `SquadResourceProjection.Append`.
  - Derives `mode` from `agent.Invocation` (`primary` for conductor, `subagent` for others).
  - Resolves models from `models.yml` for target `opencode` (omits `model` when `inherit`).
  - Lowers capabilities to explicit permission map per D4–D5 (`todowrite`, `skill`, `read`, `grep`, `glob`, `edit`, `bash`, `webfetch`, `websearch`, `kyber-weave_*`, `task`).
  - Emits `safety-narrowed` degradation records for `ask` capabilities.
  - Emits `permission-not-expressible` degradation records for unexpressible capabilities (`network.publish`, nested delegation roster).
  - Uses `SquadMarkdownDocument.Compose` with `SerializerLock` for thread-safe deterministic output.
- Clean compilation under `TreatWarningsAsErrors` / `AnalysisMode=all`.

#### Task 3: CLI Composition Registration (`src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs`)

- `ResolveRenderer()` includes `new OpenCodeRenderer()` in the `SquadRendererRegistry` instantiation list.
- Method remarks XML comment updated to list OpenCode as a native renderer.

#### Task 4: CLI Command & Doctor Tests (`tests/KyberWeave.Tests/SquadCliCommandTests.cs`)

- `Doctor_ReportsRendererCoverageAndMcpProbeStatus` updated:
  - Asserts `opencode` is present in `availableSection`.
  - Asserts `opencode` is absent from `pendingSection`.
- All other tests in `SquadCliCommandTests` continue to pass.

#### Task 5: Architecture & Onboarding Documentation (`docs/kyber-squad/architecture.md`, `docs/kyber-squad/onboarding.md`)

- `docs/kyber-squad/architecture.md` §8 updated:
  - Mentions `OpenCodeRenderer` (`.opencode/agents/*.md`, `.opencode/skills/*/SKILL.md`).
  - Updates coverage paragraph from 5 to 6 supported targets.
- `docs/kyber-squad/onboarding.md` updated:
  - Target table row for `opencode` updated from "Unsupported; fails coverage preflight" to "Implemented and registered".
  - Coverage summary text updated to include `opencode`.

#### Task 6: Todo / Hygiene (`docs/todo/kyber-squad-renderer-coverage.md`, `docs/todo/opencode.md`, `docs/todo/README.md`)

- `docs/todo/kyber-squad-renderer-coverage.md`:
  - `opencode` moved from remaining targets table to implemented renderers text.
- `docs/todo/opencode.md`:
  - Status updated to `completed` or retired with link to canonical architecture.
- `docs/todo/README.md`:
  - Inventory updated to reflect status of `opencode.md`.

#### Task 7: Full Gate Verification & Dry-Run Smoke

- `dotnet format --verify-no-changes` passes cleanly.
- `dotnet build -c Release` builds with 0 errors, 0 warnings.
- `dotnet test` passes 100% across the solution.
- `kyber-weave squad install --target opencode --dry-run` plans 113 deployment files (or derived corpus count) without modifying disk.
- `kyber-weave squad doctor` lists `opencode` under "Renderers available:".
- `kyber-weave docs validate .` and `kyber-weave docs drift .` pass cleanly.

---

## 5. Sequencing / dependency graph

```text
T1 (Test-first: author failing OpenCodeRendererContractTests)
 └─► T2 (Core: implement OpenCodeRenderer)
      └─► T3 (Composition: register OpenCodeRenderer in ResolveRenderer)
           ├─► T4 (CLI Tests: update SquadCliCommandTests doctor assertions)
           ├─► T5 (Docs: update architecture.md §8 and onboarding.md)
           ├─► T6 (Todo: update renderer coverage and opencode todo)
           └─► T7 (Verification: gates, format, test, dry-run, doctor) [after T1–T6]
```

- **T1** is authored first to establish the red verification gate.
- **T2** implements the renderer satisfying T1.
- **T3** wires the renderer into CLI composition, turning T1 green in integrated dispatch.
- **T4**, **T5**, and **T6** can proceed in parallel once T3 lands.
- **T7** runs the full verification pipeline and local smoke gates once all code, tests, and documentation are in place.

---

## 6. Residual decisions / risks

| Risk | Mitigation / Owner |
|---|---|
| OpenCode tool vocabulary change in future releases | Tool vocabulary is pinned to verified runtime telemetry (`dash/src/providers/opencode.ts`). If OpenCode introduces breaking tool renames, updates require a deliberate versioned renderer update. |
| Nested subagent delegation roster enforcement | OpenCode subagent `task` tool invocation may not enforce the in-parentheses delegation roster at nested execution depth. Honesty is preserved by documenting this limitation in `permission-not-expressible` degradation records (D6). |
| Dynamic vs hardcoded corpus file counts | Tests must not hardcode static counts (e.g., 45 or 113) without checking loaded source. Formula `source.Agents.Count + source.Agents.Sum(...)` is used in contract tests (D1/T1). |

---

## 7. Out of scope

- Implementing renderers for remaining unsupported targets (`kilo`, `warp`, `factory`, `pi`).
- Modifying canonical agent bodies, skill bodies, or model profile tokens in `products/kyber-squad/`.
- Changing the OpenTelemetry ingestion provider or session parsers in `dash/src/providers/opencode.ts`.
- In-tree self-deployment of `.opencode/` into repository root (repository root deployments are human maintainer release decisions).

---

## 8. Required skills

- `csharp-dev` — C# core renderer implementation, composition wiring, and test maintenance.
- `test-dev` — xUnit contract tests, CLI doctor tests, and assertion design.
- `app-docs-standard` — documentation updates for architecture, onboarding, and todo inventory complying with Kyber-Weave documentation standards.

---

## 9. Verification harness

The implementation is verified complete when all the following gates pass:

1. **Unit & Contract Tests:**
   - `OpenCodeRendererContractTests` passes 100% against the real `products/kyber-squad` canonical corpus.
   - Regression suites for `CopilotRendererContractTests`, `CursorRendererContractTests`, `ClaudeRendererContractTests`, and `CodexRendererContractTests` pass without regressions.
2. **CLI & Doctor Tests:**
   - `SquadCliCommandTests` doctor coverage assertion passes with `opencode` in the available section.
3. **Compiler & Code Standards:**
   - `dotnet build -c Release` clean with zero warnings under `TreatWarningsAsErrors` and `AnalysisMode=all`.
   - `dotnet format --verify-no-changes` passes cleanly.
4. **End-to-End CLI Verification:**
   - `kyber-weave squad install --target opencode --dry-run` reports exact planned files (113 files).
   - `kyber-weave squad doctor` displays `opencode` under "Renderers available:".
5. **Documentation Integrity:**
   - `kyber-weave docs validate .` exits with code 0.
   - `kyber-weave docs drift .` exits with code 0.
