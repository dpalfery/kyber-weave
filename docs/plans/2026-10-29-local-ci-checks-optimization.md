---
id: plans/2026-10-29-local-ci-checks-optimization
title: "Local CI checks and inner loop performance optimization"
doc-type: plan
status: current
component: ReviewCouncil
owner: dpalfery
last-reviewed: 2026-10-29
development-mode: test-first
documentation-impact: extends-component
impact-components: [ReviewCouncil]
---

# Local CI checks and inner loop performance optimization

**Status: Ready — approved for execution (AP1: autonomous: recommendation accepted).**

**Development mode:** `test-first` (default; no user opt-out).  
**Autonomy:** `autonomous` (resolved from repository Config Reg `<autonomy>`).  
**Scale:** `extends-component`, component `ReviewCouncil`.  
**Branch:** `perf/local-ci-checks-optimization` (branched from `origin/main` at `a342739`).

> Discovery: codegraph 8, docs 3, direct reads 25.
> - CodeGraph exploration returned: `CouncilGateSelection`, `ReviewGatesCommand`, `ReviewGatesSettings`, `TaskGateSelection`, `ReviewGateYaml`, `KyberWeaveConfigLoader`, `ChangedFiles`, `GateCatalogue`, `ProjectRootDetector`, `ReviewBlockWriter`, and `ReviewGatesCouncilCommandTests`.
> - Docs status: `root=/Users/dave/git/personal/agy/kyber-weave rev=a342739 dirty=yes documents=139`. Explored `ship-gate-catalogue` specification and `the shipped gate catalogue` architecture.
> - Direct reads (with reasons):
>   1. `docs/plans/2026-10-29-local-ci-checks-optimization.md`: Target plan under revision.
>   2. `src/KyberWeave.Cli/Commands/Review/ReviewGatesCommand.cs`: To inspect `ExecuteRepositoryGates`, `ResolveCouncilChangedSet`, and council gate execution.
>   3. `tests/KyberWeave.Tests/ReviewGatesCouncilCommandTests.cs`: To inspect all 10 tests and verify the existing contract for empty changed sets (`Count == 0`) and path scoping.
>   4. `docs/code-review/architecture.md`: To inspect council gates contract and the shipped catalogue section.
>   5. `tests/KyberWeave.Tests/CouncilGateSelectionTests.cs`: To inspect `CouncilGateSelection.Plan` behavior on `changed = null` versus `changed = []`.
>   6. `src/KyberWeave.Core/Review/ChangedFiles.cs`: To inspect `ResolveSinceMergeBase` git invocations, merge-base resolution, and untracked file handling.
>   7. `tests/KyberWeave.Tests/ReviewGatesAgentCommandTests.cs`: To inspect git child process spawning in agent task gate tests.
>   8. `tests/KyberWeave.Tests/ReviewGuidanceCouncilContractTests.cs`: To inspect contract tests directly asserting `REVIEW.md` and `products/kyber-squad/` checklist structures.
>   9. `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`: To inspect assertions directly reading and validating `products/kyber-squad/` agents, profiles, and templates.
>   10. `.kyber-weave/kyber-weave.yml`: To inspect all 27 gates in `review.gates` and all agent gates in `review.task-gates`.
>   11. `docs/archive/specs/ship-gate-catalogue/README.md`: To verify shipped gate catalogue specification, requirements, and H1–H10 harvest mappings.
>   12. `src/KyberWeave.Cli/Commands/Review/ReviewCatalogueCommand.cs`: To inspect `ReviewCatalogueCommand` options and execution.
>   13. `src/KyberWeave.Core/Docs/Scaffolding/ReviewBlockWriter.cs`: To inspect `ReviewGatesPlan` and `GateCatalogue` family loading.
>   14. `src/KyberWeave.Core/Review/Catalogue/GateCatalogue.cs`: To inspect embedded catalogue families and root detection logic.
>   15. `tests/KyberWeave.Tests/GateCatalogueResourceTests.cs`: To inspect embedded resource tests for `Gates.*.yml`.
>   16. `src/KyberWeave.Core/KyberWeave.Core.csproj`: To inspect embedded standards, `Gates.*.yml` mappings, and the `VerifyResponseGuidanceDefault` MSBuild hash target.
>   17. `dash/vitest.config.ts`: To inspect Vitest setupFiles, pool, coverage, and test environment configuration.
>   18. `dash/tests/setup/env-isolation.ts`: To inspect sandbox environment isolation and global env var sanitization.
>   19. `dash/src/analysis/review.test.ts`: To inspect mock provider usage and `vi.restoreAllMocks()` patterns.
>   20. `dash/web/vitest.setup.ts`: To inspect Vitest matchers.
>   21. `dash/src/data-handling.test.ts`: To inspect telemetry data handling guarantees and markers.
>   22. `dash/package.json`: To inspect Vitest `test` and `test:locks` scripts.
>   23. `dash/web/src/App.test.tsx`: To inspect module-level shims (`globalThis.window`, `document`) and hook state dispatchers.
>   24. `tests/KyberWeave.Tests/SquadGlobalHomeCliTests.cs`: To inspect execution bottleneck in multi-deployment fleet simulations.
>   25. `docs/plans/README.md`: To inspect the active plans inventory table.

---

## 1. Problem and goal

### 1.1 Exhaustive review and empirical measurements

Local coding runs, council review gates, and agent task-gates have grown increasingly slow due to compounding architectural bottlenecks across the inner loop, council review gates, and developer task-gates. An empirical measurement on `main` (`a342739`) reveals the following breakdown:

1. **Zero Path Scoping in `review.gates` (>4.5 minutes on every run)**:
   - Although `ReviewGateYaml` defines `public List<string>? Include` and `CouncilGateSelection.cs` implements complete glob filtering via `PathGlob.IsMatch`, **none of the 27 gates in `.kyber-weave/kyber-weave.yml` declare `include:`**.
   - Consequently, running `kyber-weave review gates .` or `./scripts/fast-loop.sh --full` executes all 27 checks unconditionally across all three ecosystems (.NET, TypeScript, Rust), taking **> 4.5 minutes (270+ seconds)** even when touching a single Markdown document or a one-line change in `dash/`.
2. **Cross-File Test Ownership & Asset Dependencies**:
   - `KyberWeave.Tests` does **not** merely test C# source files under `src/`. It directly asserts and depends upon non-C# repository assets:
     - `products/kyber-squad/**` (`*.md`, `*.yml`, capabilities, agent definitions, profiles, presets) is directly tested by `SquadCanonicalContentTests`, `SquadRenderingContractTests`, `SquadConfigurationTests`, and preset/model-selection tests. Furthermore, `KyberWeave.Core.csproj` defines an MSBuild pre-build target `VerifyResponseGuidanceDefault` that computes SHA-256 hashes between `products/kyber-squad/skills/response-format/user-guidance.md` and `src/KyberWeave.Core/Squad/Deployment/Resources/response-guidance.default.md`, failing the build if they diverge.
     - `REVIEW.md` is directly tested by `ReviewGuidanceCouncilContractTests`.
     - `.kyber-weave/kyber-weave.yml`, `apm.yml`, `.apm/**`, `global.json`, `.config/dotnet-tools.json`, and `scripts/**` are directly tested by `ApmManifestGuardTests`, `HostConfigYamlTests`, `GateCatalogueResourceTests`, and `CoverageCollectorTests`.
   - Lumping all .NET gates into a narrow `**/*.cs` glob would dangerously skip .NET `build` and `test` when editing `products/kyber-squad/agents/conductor.md` or `REVIEW.md`.
   - Conversely, pure C# formatting and static analysis (`format-whitespace`, `format-style`, `inspectcode`, `duplicates`) only parse C# syntax and project definitions. Separating pure C# source paths from .NET build/test paths ensures that edits to `REVIEW.md` or `products/**` run .NET tests without paying the 48s `inspectcode` or `dotnet format` overhead, while pure `dash/` and `docs/` edits skip both.
3. **.NET Test Suite Bottlenecks (3m 11s wall-clock, 1,509s summed duration)**:
   - The .NET test suite consists of 4,195 tests across 193 test classes.
   - 135 fast-loop classes (`scripts/fast-loop-classes.txt`) account for only **117.2s (7.8%)** of summed test time (~9.8s wall-clock in parallel).
   - 58 excluded classes account for **1,391.9s (92.2%)** of summed test time. Deep inspection reveals two specific drivers:
     - **Multi-Deployment Fleet Simulations**: `SquadGlobalHomeCliTests` (101.1s), `SquadDeploymentStateTests` (66.9s), `SquadGlobalRootTests` (66.0s), and `SquadCapturePluginStepTests` (63.0s) simulate complete multi-repository Squad installations and updates (`fleet.Alpha`, `fleet.Beta`, `fleet.Stale`) across 13 target harnesses, repeatedly performing directory walks, template rendering, and lock file validations on disk.
     - **Subprocess Spawning**: `ReviewGatesAgentCommandTests` (82.2s) and `ReviewGatesCouncilCommandTests` (52.8s) execute `CommandApp` against temporary git fixtures, spawning dozens of `git` child processes (`git init`, `git add`, `git commit`, `git merge-base`, `git diff`, `git ls-files`, `git config`) per test.
   - When non-.NET files (TypeScript, Rust, documentation) are modified, running the 3m 11s .NET test suite is completely avoidable.
4. **Council Gate MSBuild Project-Evaluation Overhead (15 `dotnet run` calls, ~8–10s)**:
   - In `.kyber-weave/kyber-weave.yml`, 15 gates (`skill-*` [12 gates], `docs-validate`, `docs-drift`, and `duplicates`) invoke `dotnet run --project src/KyberWeave.Cli -c Release --no-build -- ...`.
   - Even with `--no-build`, `dotnet run` invokes MSBuild project evaluation of `KyberWeave.Cli.csproj` to resolve the target assembly, taking ~500–700ms per invocation. Paying this startup 15 times costs ~8–10s.
   - Scoping gates by `include:` eliminates this overhead whenever skills and documentation are untouched.
5. **Task Gate Redundancy in SAST / Analyzers (23.6s per agent task)**:
   - Developer task-gates for `csharp-dev`, `dal-dev`, `maui-dev`, `pulumi-dev`, `sql-database-architect`, `github-devops`, and `test-dev` all run `dotnet format analyzers` (~23.6s, 80s CPU) on every task completion.
   - Immediately following `format-analyzers`, the task-gate runs `dotnet-build` (`dotnet build KyberWeave.sln -c Release`).
   - `Directory.Build.props` enables `EnableNETAnalyzers=true`, `AnalysisLevel=latest-All`, `AnalysisMode=all`, `TreatWarningsAsErrors=true`, and `RunAnalyzersDuringBuild=true`. The compiler already executes all Roslyn code analysis rules and fails the build on any warning. CI (`.github/workflows/ci.yml` line 52) explicitly notes: *"Skip `dotnet format analyzers`: CA quality rules already fail the build via TreatWarningsAsErrors / AnalysisMode=all"*.
6. **InspectCode Full Solution Scan Overhead (48.3s)**:
   - ReSharper InspectCode (`inspectcode`) scans the entire solution on every council run without specifying `--caches-home`. Adding a persistent cache under gitignored `artifacts/inspectcode-cache` and scoping the gate to .NET file changes avoids cold re-parsing.
7. **Vitest Isolation & Concurrency in KyberDash (499 files, ~29s wall-clock)**:
   - `vitest` in `dash` runs 499 test files.
   - Inspection of test files reveals that multiple suites (such as `dash/web/src/App.test.tsx`) install module-level browser shims on `globalThis.window` / `globalThis.document` and manage module-level hook dispatchers, while provider tests invoke `vi.restoreAllMocks()` and mock providers.
   - Disabling isolation (`isolate: false`) shares the Node.js module cache and global scope across test files within each worker process, creating high risk of non-deterministic mock collisions and state bleed between test files.
   - Safe optimization: Maintain worker isolation (`isolate: true`) for full verification runs, while optimizing worker concurrency and leveraging targeted test execution (`vitest related` or `npm run test -- <changed-files>`) in the inner loop (`fast-loop.sh`).
8. **Inner Loop (`scripts/fast-loop.sh`) Lacks Component Targeting & Uses `--no-incremental`**:
   - `scripts/fast-loop.sh` unconditionally builds .NET and runs 135 .NET test classes even when only `dash/` or `docs/` changed.
   - It runs zero TypeScript or Rust checks when `dash/` or `dash/tray/src-tauri` changed.
   - It unnecessarily passes `--no-incremental` (costing 4–6s) to `dotnet build`, even though coverage instrumentation is not active in the fast tier.

### 1.2 Goal

Optimize local CI checks, council review gates, agent task-gates, and `scripts/fast-loop.sh` so that:
- Pre-PR council review gates (`review gates`) execute only the gates applicable to the modified components, dropping review time on `dash/` or `docs/` from >4.5 minutes to <45s and <2s respectively.
- Build and test gates properly cover non-C# tested assets (`products/**`, `REVIEW.md`, config files) while excluding pure `dash/` and `docs/` edits.
- An explicit `--all` flag allows holistic audits and CI verification to run all 27 gates unscoped, while clean-tree and scoped runs strictly adhere to the shipped `KW-REVIEW-026` / `KW-REVIEW-034` contract in `ReviewGatesCouncilCommandTests.cs`.
- Task-gates drop the redundant 23.6s `format-analyzers` step, saving 24s per task while preserving 100% of analyzer diagnostics via `dotnet build` with `TreatWarningsAsErrors`.
- InspectCode uses persistent local caching under `artifacts/inspectcode-cache`.
- KyberDash testing remains safe against `vi.mock` and global state collisions while achieving instantaneous inner-loop feedback.
- `scripts/fast-loop.sh` intelligently targets the appropriate component loop (.NET, TypeScript, Rust, Docs) and enables incremental compilation.

---

## 2. Scope and boundary

### 2.1 In scope
- Scoping all 27 gates in `review.gates` in `.kyber-weave/kyber-weave.yml` with `include:` globs partitioned by ecosystem:
  - Pure C# format & static analysis (`format-whitespace`, `format-style`, `inspectcode`, `duplicates`): scoped to `&net-source-paths`.
  - .NET build and test (`build`, `test`): scoped to `&net-test-paths` (covering C# source, `products/**`, `REVIEW.md`, `.kyber-weave/**`, `apm.yml`, `.apm/**`, `scripts/**`, while strictly excluding `dash/**` and `docs/**`).
  - APM skill gates (`skill-*`): scoped to respective `.apm/skills/<skill-name>/**`, `src/KyberWeave.Cli/**`, and `src/KyberWeave.Core/Skills/**`.
  - Docs gates (`docs-validate`, `docs-drift`): `docs-validate` scoped to `docs/**`, `**/*.md`, `.kyber-weave/kyber-weave.yml`, `src/**`; `docs-drift` additionally includes `dash/**` so `KyberDash` `code-refs` and `api-endpoints` are checked on `dash/` edits (~0.8s).
  - TypeScript gates (`ts-*`): scoped to `dash/**`.
  - Rust gates (`cargo-*`): scoped to `dash/tray/src-tauri/**`.
- Updating `ReviewGatesSettings` to add the `--all` flag.
- Updating `ReviewGatesCommand` to support `--all` (setting `changed = null` unscoped), while preserving the shipped gate catalogue contract where an empty diff against base skips include-scoped gates (`KW-REVIEW-026`).
- Updating `review.task-gates` in `.kyber-weave/kyber-weave.yml` to remove the redundant `format-analyzers` step from all .NET agent definitions.
- Updating the `inspectcode` gate in `.kyber-weave/kyber-weave.yml` to include `--caches-home=artifacts/inspectcode-cache`.
- Updating `scripts/fast-loop.sh` to support component-aware inner loops (.NET, TypeScript, Rust, Docs) with cross-file awareness (`products/**` and `REVIEW.md` route to .NET) and incremental compilation (removing `--no-incremental`).
- Updating tests in `CouncilGateSelectionTests`, `ReviewTaskGatesConfigTests`, and `ReviewGatesCouncilCommandTests` to pin new behaviors.

### 2.2 Out of scope
- **Sub-component partitioning of .NET unit tests in `review.gates` is explicitly OUT OF SCOPE**:
  - *Risk Analysis*: All .NET projects (`KyberWeave.Core`, `KyberWeave.Cli`, `KyberWeave.Mcp`) compile into a single test assembly `KyberWeave.Tests.dll`. Squad installer, lifecycle, and CLI commands directly depend on Core parsing, process execution, configuration, and diagnostics.
  - *ADR 0005 Alignment*: ADR 0005 firmly established that for the pre-PR merge question (*"May this change be committed"*), a hand-picked subset of tests within the assembly creates a false sense of security.
  - *Decision*: In `review.gates`, when any file matching `&net-test-paths` changes, the full .NET test suite runs to guarantee deterministic quality across the solution. Intra-.NET test selection remains confined to the inner loop (`fast-loop.sh` fast tier and `--class` / `--method` options).
- No removal or weakening of compiler warnings, code analyzers, coverage floor collection, or formatting rules.
- Global disabling of Vitest isolation (`isolate: false`) without per-file mock audit is out of scope to avoid cross-suite flakiness; fast-loop test targeting is used instead.

---

## 3. Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | How should `review.gates` be scoped by `include:` globs in `.kyber-weave/kyber-weave.yml`? | Scope gates by two distinct .NET path sets plus technology-specific sets across ALL 27 gates:<br>- **Pure C# format & SAST** (`format-whitespace`, `format-style`, `inspectcode`, `duplicates`): `include: &net-source-paths ["**/*.cs", "**/*.csproj", "**/*.props", "**/*.targets", "**/*.sln*", "**/NuGet.config", "**/.editorconfig"]`<br>- **.NET Build & Test** (`build`, `test`): `include: &net-test-paths ["**/*.cs", "**/*.csproj", "**/*.props", "**/*.targets", "**/*.sln*", "**/NuGet.config", "**/.editorconfig", "global.json", ".config/dotnet-tools.json", ".kyber-weave/kyber-weave.yml", "apm.yml", ".apm/**", "products/**", "REVIEW.md", "scripts/**"]`<br>- **Skill gates** (`skill-*` [12 gates]): `include: [".apm/skills/<skill-name>/**", "src/KyberWeave.Cli/**", "src/KyberWeave.Core/Skills/**"]`<br>- **Docs gates**: `docs-validate` `include: ["docs/**", "**/*.md", ".kyber-weave/kyber-weave.yml", "src/**"]`; `docs-drift` `include: ["docs/**", "**/*.md", ".kyber-weave/kyber-weave.yml", "src/**", "dash/**"]`<br>- **TypeScript gates** (`ts-typecheck`, `ts-test`, `ts-lint`, `ts-reachable`): `include: ["dash/**"]`<br>- **Rust gates** (`cargo-fmt`, `cargo-clippy`, `cargo-test`): `include: ["dash/tray/src-tauri/**"]`. | Separating `net-source-paths` from `net-test-paths` ensures that changes to tested non-.cs assets (`products/kyber-squad/**`, `REVIEW.md`, config files) trigger `build` and `test` without triggering 48s `inspectcode` or `dotnet format`. Pure `dash/` and `docs/` edits skip both, reducing review time on `dash/` from 270s to ~46s and docs to <2s. | autonomous: recommendation accepted |
| D2 | How should clean working trees and full audit runs behave in `review gates`? | Add `--all` to `ReviewGatesSettings`. In `ReviewGatesCommand`: if `--all` is set, set `changed = null` (unscoped), executing all 27 gates unconditionally (refusing `--all` combined with `--agent`, `--base`, or `--paths` as invalid flag combinations if contradictory, or letting `--all` run unscoped repository council gates). If `--all` is not set, compute `changed` against `--base` (default `origin/HEAD`). If `changed` is empty (`Count == 0`, e.g. clean tree against base), gates with `include:` are skipped with `KW-REVIEW-026` ("no changed file matches its include"). If git cannot resolve `--base`, set `changed = null` and emit informational diagnostic `KW-REVIEW-034`. | Aligns 100% with the shipped gate catalogue contract in `ReviewGatesCouncilCommandTests.cs` and `docs/archive/specs/ship-gate-catalogue/README.md`. No conflicting `KW-REVIEW-035` rule id is needed. Full repository audits and CI push verification explicitly pass `--all`. | autonomous: recommendation accepted |
| D3 | Should `format-analyzers` remain in developer `review.task-gates`? | Remove `format-analyzers` from all agent task-gates in `.kyber-weave/kyber-weave.yml`. | `dotnet format analyzers` takes 23.6s (80s CPU). `dotnet-build` immediately follows it and already runs all Roslyn analyzers with `TreatWarningsAsErrors=true` and `AnalysisMode=all` (as verified in `Directory.Build.props` and `.github/workflows/ci.yml`). Removing it saves ~24s per task with zero loss of analyzer enforcement. | autonomous: recommendation accepted |
| D4 | Should ReSharper InspectCode use a persistent cache? | Yes, add `--caches-home=artifacts/inspectcode-cache` to the `inspectcode` gate argv in `.kyber-weave/kyber-weave.yml`. | `artifacts/` is already in `.gitignore`. Persisting the AST cache between local runs substantially accelerates ReSharper InspectCode analysis. | autonomous: recommendation accepted |
| D5 | How should KyberDash Vitest execution be optimized safely? | Retain `isolate: true` for the full comprehensive gate run in CI and `ts-test` to prevent cross-test module contamination from `vi.mock` and `globalThis` shims (such as `App.test.tsx`). Add a `test:related` script (`vitest related --run`) in `dash/package.json` for `scripts/fast-loop.sh` so inner-loop iterations run only tests related to modified `dash/` files (<2s). | Global `isolate: false` carries high risk across 499 test files due to module-level shims and mocked providers. Targeted `vitest related` execution provides the inner-loop speedup without test pollution risk, while full `ts-test` stays isolated and deterministic. | autonomous: recommendation accepted |
| D6 | How should `scripts/fast-loop.sh` handle component targeting & build performance? | 1. Remove `--no-incremental` from `dotnet build` in `fast-loop.sh` (saving 4–6s since coverage instrumentation is excluded).<br>2. Inspect git status and diff against HEAD: if changed files are exclusively in `dash/` (excluding `dash/tray/src-tauri`), run TypeScript `typecheck`, `lint`, and `vitest related --run` over changed `dash/` files; if exclusively in `dash/tray/src-tauri`, run `cargo fmt --check`, `cargo clippy -- -D warnings`, and `cargo test`; if exclusively in `docs/` and root `README.md`, run `docs validate .` and `docs drift .`; if in `products/kyber-squad/`, `REVIEW.md`, .NET files, or mixed/clean tree, run the .NET fast-tier loop (plus TypeScript/Rust fast checks if mixed across ecosystems).<br>3. Keep `--full` delegating to `review gates . --all --out artifacts/gates.json` (or scoped `review gates .` via `--scoped` if desired; `--full` runs `--all` for full local pre-push confidence). | Developers working on `dash/` or docs get instantaneous feedback (<2s to 10s). Non-C# tested assets correctly trigger the .NET loop, mixed diffs run all touched ecosystems' fast checks, and incremental compilation accelerates rebuilds. | autonomous: recommendation accepted |
| D7 | Should .NET unit tests be partitioned into sub-component gates in `review.gates`? | No. Maintain a single unified .NET `test` gate in `review.gates` whenever files matching `net-test-paths` change. | Preserves ADR 0005 invariant for the merge gate. Cross-namespace dependencies between Core and Squad make intra-.NET gating unsafe at review time. Fast-tier partitioning remains in `fast-loop.sh`. | autonomous: recommendation accepted |
| D8 | Autonomy and Approval Provenance | All decisions resolved under `autonomy: autonomous` per repository Config Reg `<autonomy>`. | Recorded as `autonomous: recommendation accepted`. | autonomous: recommendation accepted |
| AP1 | Approve and execute the Ready plan? | Approved for execution (`development-mode: test-first`). | All architectural checks resolved (`D1`–`D8`); `docs validate .` passes with 0 findings. | autonomous: recommendation accepted |

---

## 4. Test contract (Test-first mode)

In accordance with `development-mode: test-first`, tests verifying all new behaviors MUST be written and fail (RED) before implementation changes are applied, and pass (GREEN) afterwards.

### 4.1 Unit Tests (`tests/KyberWeave.Tests`)
1. **`CouncilGateSelectionTests`**:
   - `WhenAllFlagSpecified_PlanRunsAllGatesUnscoped`: Verifies that passing `changed = null` (which `--all` sets) includes gates with non-matching `include:` globs while preserving OS matching.
2. **`ReviewGatesCouncilCommandTests`**:
   - `ReviewGates_WithAllFlag_RunsAllGatesEvenWhenDiffMatchesSubset`: Executes `review gates --all` against a repository fixture with changes only in `dash/` and proves that .NET, Rust, and Docs gates run instead of skipping; also verifies `--all` with `--agent` is rejected as client-input error (exit 2, `KW-REVIEW-028`) just like `--task`/`--fix` without `--agent`.
   - `ReviewGates_WithCleanTree_SkipsIncludeGatesWithKWREVIEW026`: Proves that on a clean working tree against `--base`, gates with `include:` report `KW-REVIEW-026` ("no changed file matches its include") and gates without `include:` run, preserving the shipped contract.
   - `ReviewGates_WithMatchingInclude_RunsOnlyMatchingGates`: Proves that changes matching only `dash/` run only `ts-*` (and `docs-drift`) gates, reporting .NET and Rust gates as `KW-REVIEW-026`.
3. **`ReviewTaskGatesConfigTests`**:
   - `RepositoryReviewGatesDeclareIncludeGlobs`: Asserts that all 27 gates in `review.gates` in `.kyber-weave/kyber-weave.yml` declare non-empty `Include` globs.
   - `NetTestPathsIncludeProductsAndReviewGuidance`: Asserts that `build` and `test` include `products/**`, `REVIEW.md`, `.kyber-weave/kyber-weave.yml`, `apm.yml`, `.apm/**`, and `scripts/**`.
   - `NetSourcePathsExcludeProductsAndDocs`: Asserts that `format-whitespace`, `format-style`, and `inspectcode` do not include `products/**`, `REVIEW.md`, or `docs/**`.
   - `InspectCodeGateCarriesCachesHome`: Asserts that `inspectcode` includes `--caches-home=artifacts/inspectcode-cache`.
   - `TaskGatesDoNotContainRedundantFormatAnalyzers`: Asserts that no agent in `review.task-gates` declares `format-analyzers`.

---

## 5. Implementation tasks

```
       [T0: Test-First RED Contract Pins]
                       │
       ┌───────────────┼───────────────┐
       ▼               ▼               ▼
[T1: ReviewGates  [T2: Vitest    [T3: Gate Scoping &
  --all Support   Fast Targeting  InspectCode Cache
  in CLI]          in Dash]        in kyber-weave.yml]
       │               │               │
       └───────────────┼───────────────┘
                       │
                       ▼
       [T4: Fast-Loop Component Targeting & Incremental Build]
                       │
                       ▼
       [T5: Verification & Gate Benchmark]
                       │
                       ▼
       [T6: Docs Closeout & Harvest]
```

### Task T0: Author Test-First RED Contract Pins
- **Agent**: `test-dev`
- **Inputs**: `CouncilGateSelectionTests.cs`, `ReviewTaskGatesConfigTests.cs`, `ReviewGatesCouncilCommandTests.cs`.
- **Outputs**: New test methods asserting:
  - `ReviewGatesSettings` accepts `--all`.
  - Passing `--all` runs all gates unscoped even when changed files match a subset (and `--all` with `--agent` exits `2` with `KW-REVIEW-028`).
  - All 27 repository review gates in `.kyber-weave/kyber-weave.yml` declare `include:` globs.
  - `build` and `test` gates include `products/**` and `REVIEW.md`.
  - `format-*` and `inspectcode` gates exclude `products/**`, `REVIEW.md`, and `docs/**`.
  - `inspectcode` gate carries `--caches-home=artifacts/inspectcode-cache`.
  - Task gates omit `format-analyzers`.
- **Verification**: Run `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release -filter "FullyQualifiedName~ReviewGatesCouncilCommandTests|FullyQualifiedName~CouncilGateSelectionTests|FullyQualifiedName~ReviewTaskGatesConfigTests"`. Tests MUST fail (RED) on missing `--all`, missing `include:` globs, or missing flags.

### Task T1: Implement `ReviewGatesCommand` `--all` Flag
- **Agent**: `csharp-dev`
- **Inputs**: `src/KyberWeave.Cli/Commands/Review/ReviewSettings.cs`, `src/KyberWeave.Cli/Commands/Review/ReviewGatesCommand.cs`.
- **Implementation**:
  - Add `[CommandOption("--all")] public bool All { get; set; }` to `ReviewGatesSettings` with clear `[Description]`.
  - Refuse `--all` combined with `--agent` (client-input error exit `2`, `KW-REVIEW-028`) and `--all` combined with `--base` or `--paths` (client-input error exit `2`, `KW-REVIEW-028`) if mutually exclusive, or document the exact precedence clearly. Specifically: `--all` combined with `--agent`, `--base`, or `--paths` is refused with `KW-REVIEW-028` (exit `2`) so contradictory scoping flags never silently ignore user input.
  - In `ReviewGatesCommand.ExecuteRepositoryGates`:
    - If `settings.All` is true: set `changed = null;` (bypassing changed set resolution so `CouncilGateSelection.Plan` runs all gates for the current OS).
    - If `settings.All` is false: resolve `changed` via `ResolveCouncilChangedSet` as normal.
- **Verification**: T0 tests for `--all` turn GREEN.

### Task T2: Optimize KyberDash Test Inner Loop
- **Agent**: `typescript-dev`
- **Inputs**: `dash/package.json`.
- **Implementation**:
  - Keep `test` script (`vitest run --coverage`) isolated (`isolate: true`) and deterministic for council gate runs.
  - Add `"test:related": "vitest related --run"` in `dash/package.json` so changed-file runs in `scripts/fast-loop.sh` execute only affected suites without coverage instrumentation overhead.
- **Verification**: Run `npm --prefix dash run typecheck`, `npm --prefix dash run lint`, `npm --prefix dash run test:related src/canon/paths.ts`, and `npm --prefix dash run test`.

### Task T3: Configure Gate Scoping, Task-Gates, and InspectCode Cache in `kyber-weave.yml`
- **Agent**: `csharp-dev`
- **Inputs**: `.kyber-weave/kyber-weave.yml`.
- **Implementation**:
  - Define `&net-source-paths` for `format-whitespace`, `format-style`, `inspectcode`, and `duplicates`.
  - Define `&net-test-paths` (including `products/**`, `REVIEW.md`, config files, scripts, and C# source) for `build` and `test`.
  - Add `include:` globs to each gate under `review.gates`:
    - `format-whitespace`, `format-style`, `duplicates`: `include: *net-source-paths`.
    - `inspectcode`: `include: *net-source-paths` and add `--caches-home=artifacts/inspectcode-cache` to `run:`.
    - `build`, `test`: `include: *net-test-paths`.
    - `skill-*` (12 gates): `include: [".apm/skills/<skill-name>/**", "src/KyberWeave.Cli/**", "src/KyberWeave.Core/Skills/**"]`.
    - `docs-validate`: `include: ["docs/**", "**/*.md", ".kyber-weave/kyber-weave.yml", "src/**"]`.
    - `docs-drift`: `include: ["docs/**", "**/*.md", ".kyber-weave/kyber-weave.yml", "src/**", "dash/**"]`.
    - `ts-*`: `include: ["dash/**"]`.
    - `cargo-*`: `include: ["dash/tray/src-tauri/**"]`.
  - Remove `format-analyzers` anchor definition and `- *fmt-analyzers` aliases from all agent task-gates under `review.task-gates`.
- **Verification**: T0 config tests turn GREEN. `dotnet run --project src/KyberWeave.Cli -c Release -- review gates . --paths dash/src/launcher.ts` runs only `ts-*` and `docs-drift` gates and skips .NET, Rust, and Skill gates.

### Task T4: Component-Aware & Incremental `scripts/fast-loop.sh`
- **Agent**: `csharp-dev`
- **Inputs**: `scripts/fast-loop.sh`.
- **Implementation**:
  - Remove `--no-incremental` from the `dotnet build` step in `fast-loop.sh`.
  - Update `scripts/fast-loop.sh` to detect modified/untracked files (`git status --porcelain` / `git diff --name-only HEAD`) when neither `--full`, `--regenerate`, `--class`, nor `--method` is passed:
    - Classify touched ecosystems (`needs_dotnet`, `needs_dash`, `needs_rust`, `needs_docs`).
    - When working tree is clean (`0` changed files), default to the `.NET` + `docs validate` fast loop (preserving clean-tree smoke check behavior).
    - When `needs_dash` is set (`dash/**` outside `dash/tray/src-tauri/**`), run `npm --prefix dash run typecheck`, `npm --prefix dash run lint`, and `npm --prefix dash run test:related -- <changed-dash-files>` (plus `npm --prefix dash/tray/ui run typecheck` and `test` if `dash/tray/ui/**` was touched and `dash/tray/ui/node_modules` exists).
    - When `needs_rust` is set (`dash/tray/src-tauri/**`), run `cargo fmt --check`, `cargo clippy -- -D warnings`, and `cargo test` in `dash/tray/src-tauri`.
    - When `needs_docs` is set (`docs/**`, `**/*.md`), run `docs validate .` (using `--no-build` if `.NET` binary already exists in `Release`, building CLI first if absent).
    - When `needs_dotnet` is set (any `.NET`, `products/**`, `REVIEW.md`, `.kyber-weave/**`, `apm.yml`, `.apm/**`, `scripts/**`, or unknown root file), run incremental `dotnet build`, fast-tier `.NET` tests, `docs validate .`, and `dotnet format whitespace`.
    - Retain `--full` (`review gates . --all --out artifacts/gates.json`), `--class`, `--method`, and `--regenerate`.
- **Verification**: Run `scripts/fast-loop.sh` across clean and simulated component-scoped states.

### Task T5: Holistic Benchmark and Verification Gate Run
- **Agent**: `test-dev`
- **Inputs**: Full repository workspace.
- **Verification**:
  - Run full test suite: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build`.
  - Run scoped review gates against `dash/` (`--paths dash/package.json`) and `docs/` (`--paths docs/README.md`) to measure scoped wall-clock duration.
  - Run full unscoped review gates: `dotnet run --project src/KyberWeave.Cli -c Release -- review gates . --all --out artifacts/gates.json`.
  - Confirm all 27 gates pass and record before/after wall-clock timings.

### Task T6: Documentation Harvest and Closeout
- **Agent**: `docs-dev`
- **Inputs**: Plan file, `docs/code-review/architecture.md`, `docs/cli-command-surface.md`, `docs/standards/csharp/README.md`, `AGENTS.md`, `docs/plans/README.md`.
- **Implementation**:
  - Harvest design changes into `docs/code-review/architecture.md` (two-tier council gate `include:` scoping, removal of `format-analyzers` from task-gates list, `--caches-home` on `inspectcode`, and `--all` flag).
  - Update `docs/cli-command-surface.md` to document `review gates --all` and its exit-code `2` rule on contradictory scoping flags.
  - Update `docs/standards/csharp/README.md` if it mentions `format-analyzers` or `InspectCode` cache.
  - Update `AGENTS.md` inner loop and pre-PR sections (`fast-loop.sh` component targeting + incremental build, `review gates . --all`).
  - Archive the completed plan via `git mv` to `docs/archive/plans/2026-10-29-local-ci-checks-optimization.md`, populate its `## Closeout mapping` section covering `D1`–`D8`, `AP1`, and `H1`–`H5`, update `docs/plans/README.md`, and verify `docs validate . --merge-ready` and `docs drift .` pass with 0 findings.

---

## 6. Documentation impact & design harvest

```yaml
documentation-impact: extends-component
impact-components: [ReviewCouncil]
```

### Design Harvest Table

| Item | Description | Target Component | Doc Type | Target Document |
|---|---|---|---|---|
| H1 | Council gate path scoping, two-tier .NET asset paths, task-gate anchor list update, and `--all` flag contract | ReviewCouncil | architecture | `docs/code-review/architecture.md` |
| H2 | CLI command surface for `kyber-weave review gates --all` and exit-code rule | ReviewCouncil | reference | `docs/cli-command-surface.md` |
| H3 | Task-gate analyzer deduplication and InspectCode cache | ReviewCouncil | standard | `docs/standards/csharp/README.md` |
| H4 | Inner loop component targeting and incremental build in `scripts/fast-loop.sh` | ReviewCouncil | guide | `AGENTS.md` |
| H5 | Cross-file test asset dependencies (`products/**`, `REVIEW.md` in `net-test-paths`) | ReviewCouncil | architecture | `docs/code-review/architecture.md` |

**ADR Waiver**:
No new ADR required — ADR waiver: This optimization refines existing gate selection contracts ([ADR 0005](../adr/0005-task-level-fast-review.md)), utilizing the already implemented `CouncilGateSelection` include-matching engine without altering architectural boundaries, storage models, or public external schemas.

---

## 7. Verification and gates

Before merging, the following gates MUST pass:
```bash
dotnet restore KyberWeave.sln
dotnet format KyberWeave.sln whitespace --verify-no-changes --no-restore -v minimal
dotnet format KyberWeave.sln style --verify-no-changes --severity warn --no-restore -v minimal
dotnet build KyberWeave.sln -c Release --no-restore
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . --merge-ready
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
dotnet run --project src/KyberWeave.Cli -- review gates . --all --out artifacts/gates.json
```
