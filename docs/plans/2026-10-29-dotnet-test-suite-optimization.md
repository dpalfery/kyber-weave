---
id: plans/2026-10-29-dotnet-test-suite-optimization
title: "Bounded, high-confidence optimization and consolidation of KyberWeave.Tests"
doc-type: plan
status: current
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-29
development-mode: test-first
documentation-impact: extends-component
impact-components: [KyberSquad, ReviewCouncil]
---

# Bounded, high-confidence optimization and consolidation of KyberWeave.Tests

**Status: Ready — approved for execution.** Approved autonomously under Config Reg <autonomy>: autonomous (AP1: autonomous: recommendation accepted).

**Development mode:** `test-first` (default; no user opt-out).  
**Autonomy:** `autonomous` (resolved from repository Config Reg `<autonomy>`).  
**Scale:** `extends-component`, components `KyberSquad`, `ReviewCouncil`.  
**Branch:** `perf/dotnet-unit-test-optimization` (branched from `fad3247`).

> Discovery: codegraph 10, docs 2, direct reads 12.
> - CodeGraph exploration returned verbatim source for: `SquadOperatorChannel`, `ProcessRunner`, `SquadRendererRegistry`, `SquadRenderModels`, `DevinRenderer`, `CopilotRenderer`, `SquadOperatorChannelOptInTests`, `ProcessConsoleCapture`, `SquadDoctorCommand`, `SquadHotshotLifecycleCopilotGlobalLinksTests`, `SquadRenderSource`, `SquadSourceLoader`, `CorpusSquadReleaseSource`, `SquadStateMigration`, `SquadCommandComposition`, `ReviewGatesAgentCommandTests`, `ReviewGatesCouncilCommandTests`, `UpstreamSeveranceTests`, and `SquadUpdateCommand`.
> - Docs status: `root=/Users/dave/git/personal/agy/kyber-weave rev=fad3247 dirty=no documents=138`. Explored `plans README frontmatter doc-type` and `documentation ontology`.
> - Direct reads (with reasons):
>   1. `docs/documentation-ontology.md`: To verify required frontmatter keys and closed vocabulary for `plan`.
>   2. `docs/catalog.md`: To verify components (`KyberSquad`, `ReviewCouncil`) and owners (`dpalfery`).
>   3. `docs/plans/README.md`: To inspect the active plans inventory table format.
>   4. `docs/archive/plans/2026-10-29-local-ci-checks-optimization.md`: To inspect recent plan structure, closeout mapping, and precedent.
>   5. `tests/KyberWeave.Tests/SquadPathSafetyTests.cs`: To verify the historical PR #334 resolver-injection pattern and the warning against disabling parallelization across collections.
>   6. `tests/KyberWeave.Tests/Squad/SquadOperatorChannelOptInTests.cs`: To inspect `SquadOperatorChannelCollection`, `RunWithGate`, and environment variable mutation.
>   7. `tests/KyberWeave.Tests/SquadHotshotLifecycleCopilotGlobalLinksTests.cs`: To inspect `InstallGlobalCopilotAsync` and `SquadDoctorCommand` execution.
>   8. `src/KyberWeave.Core/Squad/Rendering/SquadRenderSource.cs`: To inspect `SquadRenderSource.Load` and preset application.
>   9. `tests/KyberWeave.Tests/Fakes/CorpusSquadReleaseSource.cs`: To inspect `CopyCorpus` disk copying of 203 files per extraction.
>   10. `tests/KyberWeave.Tests/SquadDeploymentStateTests.cs`: To inspect line 1359 Claude full corpus render in skill retirement test.
>   11. `tests/KyberWeave.Tests/ReviewGatesAgentCommandTests.cs`: To inspect `MustGit`, `Commit`, and process spawning per test fixture.
>   12. `tests/KyberWeave.Tests/UpstreamSeveranceTests.cs`: To inspect sequential `git ls-files` scan and `Encoding.Latin1` file reads.

---

## Decisions

| ID | Question | Answer / Decision | Mitigating and Supporting Information | Status / Approval Provenance |
|---|---|---|---|---|
| D1 | How should the operator channel test collection (`SquadOperatorChannelCollection`) be remediated to eliminate process-wide environment mutation without serializing tests? | Follow the PR #334 resolver-injection pattern: thread an optional `Func<string, string?>? getEnvironmentVariable = null` (defaulting to `Environment.GetEnvironmentVariable` in production) through `SquadOperatorChannel.IsGateOn`, `SquadOperatorChannel.ResolveServerName`, `SquadRenderRequest`, and the CLI command constructors (`SquadDoctorCommand`, `SquadInstallCommand`, `SquadUpdateCommand`, `SquadCommandComposition`). Operator channel tests will pass an in-memory dictionary lookup and never call `Environment.SetEnvironmentVariable`. Delete `SquadOperatorChannelCollection` so its tests run in parallel. | In PR #334 (`8a8dfc80`), xUnit collection-level `DisableParallelization = true` was proven ineffective at protecting tests outside the collection, because tests running concurrently in other classes still observe process-wide environment variables. By removing mutation entirely, all tests become completely thread-safe. `ProcessConsoleCapture` already holds a private static lock (`lock (Gate)`), serializing only the temporary console redirection and preventing output bleed. | Approved (`autonomous: recommendation accepted`) |
| D2 | Should `ScaleAcceptanceCollection` (`DocumentationAnalysisScaleTests.cs:305`) also have `DisableParallelization = true` removed? | **No.** Retain `DisableParallelization = true` strictly on `ScaleAcceptanceCollection`. | `ScaleAcceptanceCollection` monitors `GC.GetAllocatedBytesForCurrentThread()` and enforces strict allocation and elapsed time ceilings across 10,000 DocGraph claims. Parallel execution on other threads would pollute thread-allocated garbage metrics and trigger non-deterministic ceiling failures. | Approved (`autonomous: recommendation accepted`) |
| D3 | How should repeated disk I/O and YAML/Markdown parsing of `products/kyber-squad` be eliminated in `SquadRendererRegistry` and `SquadDoctorCommand`? | Add an optional `SquadSource? PreloadedSource = null` property to `SquadRenderRequest`. In `SquadRenderSource.Load`, use `request.PreloadedSource ?? SquadSourceLoader.Load(request.SourceDirectory)`. In `SquadRendererRegistry.RenderAsync`, load `SquadSource sharedSource = request.PreloadedSource ?? SquadSourceLoader.Load(request.SourceDirectory)` once and thread it to all child renderers. In `SquadDoctorCommand.ReportGlobalCollisions`, load `SquadSource` once before the 13-target loop and pass it in `SquadRenderRequest`. | Loading the 203-file corpus once per batch or render operation preserves all validation semantics (agent digests, skill rosters, capabilities) while eliminating 26 redundant full-directory loads per doctor run and hundreds of duplicate parses in tests. | Approved (`autonomous: recommendation accepted`) |
| D4 | How should test suites asserting read-only renderer contracts and model presets be accelerated without weakening assertions? | Introduce a thread-safe test fixture helper `TestSquadCorpus` providing lazy, immutable `SquadSource` instances and cached baseline renders per target. Test theories in `SquadModelPresetRenderTests`, `CopilotRendererTests`, `DevinRendererContractTests`, and related specialist contract tests will reuse these instances rather than re-reading all 203 files from disk on every theory row. | `SquadModelPresetRenderTests` alone runs 52 theory iterations that each performed 5 full loads of 203 files (~260 loads total, 61.5s). Reusing immutable memory models cuts execution time to ~1–2s while preserving 100% of byte-exact comparisons and path verifications. | Approved (`autonomous: recommendation accepted`) |
| D5 | How should redundant OS `git` child process spawning be eliminated in `ReviewGatesAgentCommandTests`, `ReviewGatesCouncilCommandTests`, and `DocsInitReviewGatesTests`? | Introduce a shared `TestGitRepositoryTemplate` helper. On first use, it runs `git init`, stages baseline files (`src/a.cs`, `src/gone.cs`, `notes/readme.md`, `.kyber-weave/kyber-weave.yml`), and commits them once. Each test then performs a directory copy of the template's `.git` folder (<2ms disk copy, 0 processes) instead of spawning 4–6 OS `git` processes per test. Retain dedicated real-`git` integration tests for untracked discovery and merge-base resolution. | 72 tests across these classes currently spawn ~720 OS child processes. Pre-seeding a template eliminates >300 redundant `git init`, `git add`, and `git commit` child processes, saving 40–60s of wall-clock time and preventing OS subprocess exhaustion. | Approved (`autonomous: recommendation accepted`) |
| D6 | How should `UpstreamSeveranceTests.NoTrackedFileOutsideDocsNamesTheUpstream` (13.94s) be optimized? | Parallelize the file reading and marker scanning across all available CPU cores using `Parallel.ForEach` over the tracked file list with a thread-safe `ConcurrentBag<string> offenders`. | The severance test checks every tracked file outside `docs/` against banned markers using `Encoding.Latin1`. Parallelizing disk reads and string matching drops runtime from 13.94s to <1.0s without changing a single rule or skipping any tracked file. | Approved (`autonomous: recommendation accepted`) |
| D7 | How should plumbing, lifecycle, and CLI tests that do not assert canonical agent prose be migrated from 203-file disk copies? | Provide a fast minimal synthetic release source (`SyntheticValidSquadReleaseSource`) containing valid schemas, 1 minimal agent, and 1 minimal skill that passes `SquadSourceLoader.Load`. Use it in plumbing and state tests (`SquadGlobalHomeCliTests`, `SquadGlobalRootTests`, `SquadDeploymentStateTests`, `SquadCapturePluginStepTests`) where tests verify state machines, receipts, locks, and exit codes. In `SquadDeploymentStateTests:1359`, replace the full 17.9s Claude corpus render with a targeted synthetic render. Dedicated real-install contract tests retain `CorpusSquadReleaseSource`. | Copying 203 files repeatedly in fleet simulations and state transition tests accounts for >300s of disk I/O. Switching plumbing tests to synthetic valid packages or cached extraction directories drastically accelerates the test suite while keeping real corpus tests intact. | Approved (`autonomous: recommendation accepted`) |
| D8 | What documentation closeout mechanism applies to this optimization plan? | Close out under an **ADR Waiver**: this plan refines internal test execution, test helpers, and non-breaking DI/caching seams in internal classes without altering public CLI flags, storage schemas, agent capability profiles, or external interfaces. Update relevant documentation in `docs/standards/test/README.md` and `docs/plans/README.md`. | Conforms to repo ADR waiver policy: "Permitted when decisions refine existing architecture, adhere to established patterns, or represent implementation details that do not warrant a lasting architectural record." | Approved (`autonomous: recommendation accepted`) |
| AP1 | Approve and execute the Ready plan on branch perf/dotnet-unit-test-optimization? | Approve and execute. | Decisions D1–D8 are grounded in repository evidence and historical PR #326/#334/#453 provenance. | Approved (autonomous: recommendation accepted) |

---

## Design Harvest

| Harvest ID | Target Component | Target Doc Type | Proposed Location | Summary of Durable Knowledge |
|---|---|---|---|---|
| H1 | KyberSquad | `coding-standard` | `docs/standards/test/README.md` | Guidelines for zero-flakiness test authoring: mandatory environment delegate injection (`Func<string, string?>`) instead of `Environment.SetEnvironmentVariable`, prohibition of `DisableParallelization = true` across shared collections, and immutable test corpus caching. |
| H2 | KyberSquad | `architecture` | `docs/kyber-squad/architecture.md` | Preloaded `SquadSource` caching seam in `SquadRenderRequest` and batched collision rendering in `SquadDoctorCommand`. |
| H3 | ReviewCouncil | `reference` | `docs/code-review/architecture.md` | Fast template-cloning pattern for test git repositories avoiding redundant OS child processes. |

---

## 1. Problem and Goal

### 1.1 Empirical Baseline & Bottleneck Analysis

An exhaustive profile of `tests/KyberWeave.Tests` on commit `fad3247` revealed that the .NET test suite consists of **4,209 tests** across 193 test classes:
- **Summed test duration:** **2,083.7s (~34.7 CPU-minutes)**.
- **Wall-clock execution time:** **195.8s–233.5s** (`145s user`, `211s sys`).
- **Disk I/O & Child Process Bound:** The enormous system CPU time (`211s sys`) is driven by redundant file copies (203 files per `CorpusSquadReleaseSource` extraction), repeated YAML/Markdown parsing of canonical agent source, and hundreds of OS `git` process executions.
- **Concentration:**
  - KyberSquad tests: 2,254 tests, consuming **1,423.2s (68.3% of total CPU)**.
  - Review Council / Gates tests: 553 tests, consuming **259.0s (12.4% of total CPU)**.
  - Together, KyberSquad and Review Gates account for **80.7%** of the entire test suite execution time.

### 1.2 Identified High-Impact Bottlenecks

1. **Latent Flakiness Hazard & Serial Pole in `SquadOperatorChannelCollection` (88.6s–96.2s serial pole)**:
   - PR #453 introduced `SquadOperatorChannelCollection` (`DisableParallelization = true`) on `SquadOperatorChannelOptInTests.cs:632` because `SquadOperatorChannelFixture.RunWithGate` mutates process-wide environment variables (`KYBER_SQUAD_OPERATOR_CHANNEL`, `KYBER_SQUAD_OPERATOR_CHANNEL_SERVER`, `COPILOT_HOME`).
   - As documented in PR #334 (`SquadPathSafetyTests.cs:920`), disabling parallelization on an xUnit collection only serializes tests within that collection. Tests running in other parallel collections continue to execute and invoke `SquadRendererRegistry.RenderAsync` -> `SquadOperatorChannel.IsGateOn()` -> `Environment.GetEnvironmentVariable(...)`, creating a live race condition whenever `RunWithGate` mutates the process environment.
   - Forcing `SquadOperatorChannelOptInTests`, `SquadLifecycleRenderWarningFilterTests`, and `SquadOperatorChannelCliTests` into a single serial queue creates an 88–96s serial bottleneck at the end of the test run.
2. **`SquadHotshotLifecycleCopilotGlobalLinksTests` Collision Scan Bottleneck (39.56s under load)**:
   - Added in PR #326 to verify that `squad doctor --global` passes `GlobalRoots` to its comparison render so that `agents/conductor.agent.md` is not flagged as an unmanaged collision.
   - `SquadDoctorCommand.ReportGlobalCollisions` loops over all 13 `SupportedTargets` sequentially, calling `renderer.RenderAsync(...)` which reloads `SquadSourceLoader.Load` from disk 13 times. Combined with `CorpusSquadReleaseSource` copying 203 files from disk, the test takes ~40s under parallel load.
3. **Repeated Parsing of Canonical `products/kyber-squad` (141 Call Sites, ~350 Full Renders)**:
   - 141 call sites in `tests/KyberWeave.Tests` invoke `SquadSourceLoader.Load(ProductRoot)`.
   - Read-only contract and preset tests (`SquadModelPresetRenderTests` [61.5s], `CopilotRendererTests` [53.6s], `DevinRendererContractTests` [38.0s], `ZCodeRendererContractTests` [34.5s], `SquadRootWrapperAgentTests` [34.4s], `AntigravityRendererContractTests` [29.4s], `SquadRustGoSpecialistTests` [20.1s]) execute hundreds of full 203-file renders with zero caching.
4. **Heavy Disk Copies in Plumbing/Lifecycle Tests**:
   - `SquadGlobalHomeCliTests` (39 tests, 133.8s serial), `SquadGlobalRootTests` (111 tests, 95.2s), `SquadDeploymentStateTests` (498 tests, 82.8s), `SquadCapturePluginStepTests` (19 tests, 75.1s), `SquadCliCommandTests` (64.4s), `SquadLifecycleTests` (61.5s), and `SquadModelPresetLifecycleTests` (58.2s) repeatedly copy all 203 files via `CorpusSquadReleaseSource` just to test receipt updates, lock serialization, or CLI flag parsing.
   - Specifically, `SquadDeploymentStateTests.cs:1359` takes 17.9s rendering the full Claude corpus simply to test retiring one skill (`create-pull-request-github`).
5. **Redundant Git Subprocess Spawning in Review Gate Tests**:
   - `ReviewGatesAgentCommandTests` (37 tests, 101.0s), `ReviewGatesCouncilCommandTests` (18 tests, 66.1s), and `DocsInitReviewGatesTests` (17 tests, 42.7s) spawn 8–12 OS `git` child processes per test (~720 OS child processes total), dominating system CPU and disk wait.
6. **Sequential Single-Thread File Read in `UpstreamSeveranceTests` (13.94s)**:
   - `NoTrackedFileOutsideDocsNamesTheUpstream` iterates through thousands of tracked files sequentially, reading each file's bytes synchronously.

### 1.3 Goal & Measurable Targets

Optimize `tests/KyberWeave.Tests` and minimal supporting seams in `KyberWeave.Core` and `KyberWeave.Cli` to achieve:
1. **Full `KyberWeave.Tests` wall-clock execution time ≤ 60–75s** (reduced from 195.8s–233.5s, a >65% speedup).
2. **Summed CPU test duration ≤ 700–800s** (reduced from 2,083.7s, a >60% reduction in CPU cycles).
3. **Zero flakiness & zero global-state mutations**: thread environment readers through `SquadOperatorChannel` and remove `SquadOperatorChannelCollection`.
4. **100% test pass rate preserved**: all 4,209 tests passing, no assertions weakened, no targets omitted.
5. **Zero compiler warnings** (`TreatWarningsAsErrors=true`, `AnalysisMode=all`).
6. **27/27 repository review gates clean** on `kyber-weave review gates . --all`.

---

## 2. Technical Design

### 2.1 Zero-Flakiness Environment Resolver Seam

#### 2.1.1 `SquadOperatorChannel` Resolver Seam (`src/KyberWeave.Core/Squad/Rendering/SquadOperatorChannel.cs`)
Update `SquadOperatorChannel` methods to accept an optional environment getter:
```csharp
internal static bool IsGateOn(Func<string, string?>? getEnvironmentVariable = null)
{
    Func<string, string?> getter = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    return string.Equals(getter(GateVariable), "true", StringComparison.Ordinal);
}

internal static ServerName ResolveServerName(Func<string, string?>? getEnvironmentVariable = null)
{
    Func<string, string?> getter = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    string? raw = getter(ServerVariable);
    ...
}
```

#### 2.1.2 `SquadRenderRequest` Seam (`src/KyberWeave.Core/Squad/Rendering/SquadRenderModels.cs`)
Add `EnvironmentReader` to `SquadRenderRequest`:
```csharp
public sealed record SquadRenderRequest(
    string SourceDirectory,
    IReadOnlyList<SquadTarget> Targets,
    SquadDeploymentScope Scope,
    string? UserScopeDirectory = null,
    string TranslationMode = "best-effort",
    ISquadGlobalRootResolver? GlobalRoots = null,
    IReadOnlyDictionary<SquadTarget, SquadModelPreset>? ModelPresets = null,
    SquadSource? PreloadedSource = null,
    Func<string, string?>? EnvironmentReader = null);
```

#### 2.1.3 Renderer Registry & Copilot Renderer Wiring
- In `SquadRendererRegistry.RenderAsync`: pass `request.EnvironmentReader` to `SquadOperatorChannel.IsGateOn(request.EnvironmentReader)`.
- In `CopilotRenderer.RenderAsync`: pass `request.EnvironmentReader` to `SquadOperatorChannel.IsGateOn(...)` and `SquadOperatorChannel.ResolveServerName(...)`.

#### 2.1.4 CLI Command Injection
- In `SquadDoctorCommand`, `SquadInstallCommand`, `SquadUpdateCommand`, and `SquadCommandComposition`: accept optional `Func<string, string?>? environmentReader = null` parameter.
- When creating `SquadRenderRequest`, thread `_environmentReader` into the request.

#### 2.1.5 Test Remediation (`SquadOperatorChannelOptInTests.cs`, `SquadLifecycleRenderWarningFilterTests.cs`, `SquadOperatorChannelCliTests.cs`)
- Update `SquadOperatorChannelFixture`:
  - Replace `RunWithGate` and `RunWithGateAsync` with delegate-based helpers that construct an in-memory dictionary-backed resolver:
    ```csharp
    internal static Func<string, string?> CreateEnvironmentReader(
        string? gateValue,
        string? serverName = DefaultServerName,
        string? copilotHome = null) =>
        variableName => variableName switch
        {
            GateVariable => gateValue,
            ServerVariable => serverName,
            CopilotHomeVariable => copilotHome,
            _ => null
        };
    ```
  - Pass the environment reader into `SquadRenderRequest` or CLI command constructors.
  - Delete `[CollectionDefinition(Name, DisableParallelization = true)]` and remove `[Collection(...)]` attributes so all 3 test classes run in parallel without serial delay.

### 2.2 Preloaded `SquadSource` and Shared Corpus Caching

#### 2.2.1 `SquadRenderSource.Load` & `SquadRendererRegistry` Seam
- In `SquadRenderSource.cs`:
  ```csharp
  public static SquadSource Load(SquadRenderRequest request, SquadTarget target)
  {
      ArgumentNullException.ThrowIfNull(request);
      SquadSource source = request.PreloadedSource ?? SquadSourceLoader.Load(request.SourceDirectory);
      if (request.ModelPresets != null && request.ModelPresets.TryGetValue(target, out SquadModelPreset? preset) && preset is not null)
      {
          return source with { ModelProfiles = preset.ApplyTo(source.ModelProfiles) };
      }
      return source;
  }
  ```
- In `SquadRendererRegistry.RenderAsync`:
  - Load `SquadSource sharedSource = request.PreloadedSource ?? SquadSourceLoader.Load(request.SourceDirectory);` once at the beginning of the render.
  - Forward `PreloadedSource = sharedSource` to each child renderer group:
    ```csharp
    SquadRenderResult partial = await group.Key.RenderAsync(
        request with { Targets = group.ToArray(), PreloadedSource = sharedSource },
        cancellationToken).ConfigureAwait(false);
    ```
  - Use `sharedSource` for operator channel checks and `ValidateRenderResult`.

#### 2.2.2 `SquadDoctorCommand.ReportGlobalCollisions` Batching
In `SquadDoctorCommand.cs`:
- Before iterating through `renderer.SupportedTargets`:
  ```csharp
  SquadSource sharedSource = SquadSourceLoader.Load(canonicalSourcePath);
  ```
- Pass `PreloadedSource: sharedSource` into `SquadRenderRequest` on each target iteration.
- Eliminates 26 redundant loads of 203 files from disk per doctor invocation.

#### 2.2.3 Test Corpus Fixture (`TestSquadCorpus.cs`)
Create `tests/KyberWeave.Tests/Fakes/TestSquadCorpus.cs`:
- Thread-safe, lazy-initialized singleton `TestSquadCorpus.CanonicalSource` reading `products/kyber-squad` once.
- Thread-safe cache of default renders per target `TestSquadCorpus.GetDefaultRender(target)` for read-only contract assertions.
- Update `SquadModelPresetRenderTests`, `CopilotRendererTests`, `DevinRendererContractTests`, `ZCodeRendererContractTests`, `SquadRootWrapperAgentTests`, `AntigravityRendererContractTests`, and `SquadRustGoSpecialistTests` to consume `TestSquadCorpus.CanonicalSource`.

### 2.3 Synthetic Release Source & Targeted State Test Optimization

#### 2.3.1 `SyntheticValidSquadReleaseSource`
Create `tests/KyberWeave.Tests/Fakes/SyntheticValidSquadReleaseSource.cs`:
- Generates a minimal, valid Squad source tree containing:
  - `squad.yml`, `toolchain.yml`, `schemas/*.schema.json`
  - 1 minimal valid agent (`architect.agent.md`)
  - 1 minimal valid skill (`response-format/SKILL.md`)
  - Valid `models.yml`, `capabilities.yml`, `fallbacks.yml`
- Emits ~8 files instead of 203 files during extraction.
- Fully valid under `SquadSourceLoader.Load`.
- Used in `SquadGlobalHomeCliTests`, `SquadGlobalRootTests`, `SquadDeploymentStateTests`, and `SquadCapturePluginStepTests` for tests asserting state transitions, receipts, locks, and CLI exit codes.

#### 2.3.2 Targeted Fix in `SquadDeploymentStateTests:1359`
- Replace the 17.9s `new SquadRendererRegistry([new ClaudeRenderer()]).RenderAsync(...)` call in `CreateUpdateRetiresCreatePullRequestGithubFromAPriorReleaseUnlessLocallyEdited` with a cached Claude render or synthetic `SquadRenderResult` that asserts the retirement invariant without re-rendering the full corpus from disk.

### 2.4 Fast Git Repository Template for Review Gate Tests

#### 2.4.1 `TestGitRepositoryTemplate` Helper
Create `tests/KyberWeave.Tests/Fakes/TestGitRepositoryTemplate.cs`:
- Thread-safe singleton that initializes a base git repository in a temp directory once:
  - Runs `git init --quiet`.
  - Writes initial test files (`src/a.cs`, `src/gone.cs`, `notes/readme.md`, `.kyber-weave/kyber-weave.yml`).
  - Commits `initial` and `configure gates`.
- Provides `CreateRepositoryCopy(string targetDirectory)`:
  - Performs a fast filesystem copy of the pre-seeded repository directory (<2ms, 0 OS child processes).
- Update `ReviewGatesAgentCommandTests`, `ReviewGatesCouncilCommandTests`, and `DocsInitReviewGatesTests` to initialize their per-test repos from `TestGitRepositoryTemplate.CreateRepositoryCopy`.
- Retain real `git` CLI process calls only for the actual test scenarios under evaluation (e.g. `MustGit("add", ...)`, `CommandApp.Run(...)`).

### 2.5 Parallel Upstream Severance Scan

#### 2.5.2 `UpstreamSeveranceTests.NoTrackedFileOutsideDocsNamesTheUpstream`
Update `UpstreamSeveranceTests.cs`:
```csharp
[Fact]
public void NoTrackedFileOutsideDocsNamesTheUpstream()
{
    ConcurrentBag<string> offenders = new();
    string repoRoot = RepoRoot;
    string[] trackedFiles = ListTrackedFiles();

    Parallel.ForEach(trackedFiles, relativePath =>
    {
        if (IsExempt(relativePath)) return;

        string absolutePath = Path.Combine(repoRoot, relativePath);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(absolutePath);
        }
        catch (IOException)
        {
            return;
        }

        string content = Encoding.Latin1.GetString(bytes);
        if (BannedMarkers.Any(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            offenders.Add(relativePath);
        }
    });

    Assert.True(
        offenders.IsEmpty,
        "Requirement 1.6: tracked files outside docs/ must not name the upstream. " +
        "Remove the reference, or move the record under docs/ where the fork's " +
        $"history is allowed to name it:\n  {string.Join("\n  ", offenders.OrderBy(x => x, StringComparer.Ordinal))}");
}
```

---

## 3. Test-First Delivery Contract

Under `development-mode: test-first`:
1. Every task begins with a Red test or regression verification pin demonstrating that the seam or optimization works under contract before implementation proceeds.
2. Invariant preservation pins:
   - Zero tests dropped: suite must maintain all 4,209 passing tests.
   - Zero assertions weakened: all path checks, byte-identical comparisons, receipt digests, and error messages remain identical.
   - Zero flakiness: concurrent runs must pass 100% reliably without race conditions.

---

## 4. Implementation Tasks

### Task 1: Regression Pins & Seam Contracts
**Assigned role:** `test-dev`  
**Description:**
- Author contract tests for `SquadOperatorChannel` environment getter injection verifying that custom delegate overrides take precedence over `Environment.GetEnvironmentVariable`.
- Author contract test for `SquadRenderRequest.PreloadedSource` verifying that when supplied, `SquadRenderSource.Load` and `SquadRendererRegistry` do not touch disk.
- Record baseline timings for slowest test classes.

### Task 2: Zero-Flakiness Environment Injection & Parallelization of Operator Channel Tests
**Assigned roles:** `csharp-dev` (Core/Cli seams) and `test-dev` (Tests)  
**Description:**
- `csharp-dev`: Update `SquadOperatorChannel.cs`, `SquadRenderModels.cs`, `CopilotRenderer.cs`, `SquadRendererRegistry.cs`, `SquadDoctorCommand.cs`, `SquadInstallCommand.cs`, `SquadUpdateCommand.cs`, and `SquadCommandComposition.cs` to thread `Func<string, string?>? getEnvironmentVariable`.
- `test-dev`:
  - Update `SquadOperatorChannelOptInTests.cs`, `SquadLifecycleRenderWarningFilterTests.cs`, and `SquadOperatorChannelCliTests.cs` to use in-memory environment readers and eliminate all `Environment.SetEnvironmentVariable` calls.
  - Delete `SquadOperatorChannelCollection` (`DisableParallelization = true`) and remove `[Collection]` attributes.
  - Verify all 3 classes execute concurrently without failure.

### Task 3: Preloaded `SquadSource` Seam & `SquadHotshotLifecycleCopilotGlobalLinksTests` Acceleration
**Assigned roles:** `csharp-dev` (Core/Cli seams) and `test-dev` (Tests)  
**Description:**
- `csharp-dev`:
  - Implement `SquadRenderRequest.PreloadedSource` in `SquadRenderModels.cs`.
  - Wire `SquadRenderSource.Load` and `SquadRendererRegistry.RenderAsync` to reuse `PreloadedSource`.
  - Update `SquadDoctorCommand.ReportGlobalCollisions` to pre-load `SquadSource` once before the target loop.
- `test-dev`:
  - Optimize `SquadHotshotLifecycleCopilotGlobalLinksTests`: inject focused renderer (`new SquadRendererRegistry([new CopilotRenderer()])`) or cached source in `SquadDoctorCommand` while retaining the full end-to-end Copilot global install -> `SquadDoctorCommand(Global = true)` collision check.
  - Verify execution time drops from ~40s to <5s.

### Task 4: Thread-Safe `TestSquadCorpus` Helper & Contract Test Consolidation
**Assigned role:** `test-dev`  
**Description:**
- Create `tests/KyberWeave.Tests/Fakes/TestSquadCorpus.cs` providing cached immutable `SquadSource` and lazy target renders.
- Update `SquadModelPresetRenderTests.cs` to reuse `TestSquadCorpus.CanonicalSource` and cache baseline unpreset renders.
- Update slowest read-only renderer tests (`CopilotRendererTests`, `DevinRendererContractTests`, `ZCodeRendererContractTests`, `SquadRootWrapperAgentTests`, `AntigravityRendererContractTests`, and `SquadRustGoSpecialistTests`) to use `TestSquadCorpus.CanonicalSource`.
- Verify 100% of assertions and theories pass with zero disk I/O.

### Task 5: Fast Git Repository Template Helper
**Assigned role:** `test-dev`  
**Description:**
- Create `tests/KyberWeave.Tests/Fakes/TestGitRepositoryTemplate.cs` to pre-seed and copy a template git repository.
- Update `ReviewGatesAgentCommandTests.cs`, `ReviewGatesCouncilCommandTests.cs`, and `DocsInitReviewGatesTests.cs` to instantiate fixtures via fast directory copies.
- Retain dedicated real-git subprocess assertions for untracked files and merge-base resolution.
- Verify ~300 OS subprocesses are eliminated and all gate contract assertions pass.

### Task 6: Synthetic Minimal Release Source & Targeted State Test Optimization
**Assigned role:** `test-dev`  
**Description:**
- Implement `SyntheticValidSquadReleaseSource` for fast, lightweight corpus extraction.
- Update `SquadGlobalHomeCliTests`, `SquadGlobalRootTests`, `SquadDeploymentStateTests`, and `SquadCapturePluginStepTests` to use synthetic sources where canonical agent prose is not asserted.
- In `SquadDeploymentStateTests.cs:1359`, replace the 17.9s full Claude corpus render with a targeted cached/synthetic render.
- Ensure dedicated real-install contract tests retain `CorpusSquadReleaseSource`.

### Task 7: Parallelize `UpstreamSeveranceTests`
**Assigned role:** `test-dev`  
**Description:**
- Update `UpstreamSeveranceTests.NoTrackedFileOutsideDocsNamesTheUpstream` to scan tracked files using `Parallel.ForEach` with `ConcurrentBag<string>`.
- Verify execution drops from 13.94s to <1.0s while retaining exact severance detection.

### Task 8: Full Verification, Council Review Gates & Closeout
**Assigned role:** `docs-dev` (documentation) and `conductor` (gate execution)  
**Description:**
- Run full .NET unit test suite (`dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`) to measure end-to-end wall-clock and summed CPU duration.
- Run `review gates . --all` (all 27 gates across .NET, TypeScript, Rust, and Docs).
- Run `docs validate .` and `docs drift .`.
- Perform docs harvest and closeout mapping under ADR waiver.

---

## 5. Verification and Acceptance Criteria

1. **Suite Duration**:
   - Total wall-clock time of `tests/KyberWeave.Tests` ≤ 60–75s (baseline 195.8s–233.5s).
   - Summed CPU time ≤ 700–800s (baseline 2,083.7s).
2. **Zero Flakiness**:
   - `SquadOperatorChannelCollection` removed; operator channel tests run concurrently without mutating `Environment.SetEnvironmentVariable`.
   - `ScaleAcceptanceCollection` retained serial to protect thread GC allocations.
3. **Correctness**:
   - 4,209/4,209 tests passing; 0 failed, 0 skipped regressions.
   - All 13 harness targets verified with exact byte preservation outside model lines.
4. **Gates**:
   - 0 compiler warnings (`TreatWarningsAsErrors=true`, `AnalysisMode=all`).
   - 27/27 council review gates passing (`review gates . --all`).
   - `docs validate .` and `docs drift .` 0 findings.

---

## 6. Closeout Mapping

| Decision / Harvest ID | Destination in Canonical Documentation | Rationale / Waiver |
|---|---|---|
| D1 | `docs/standards/test/README.md` | Guidelines for environment delegate injection in tests and eliminating process-wide mutation. |
| D2 | `docs/standards/test/README.md` | Rationale for retaining `ScaleAcceptanceCollection` isolation. |
| D3 | `docs/kyber-squad/architecture.md` | Documentation of preloaded `SquadSource` caching seam in `SquadRenderRequest`. |
| D4 | `docs/standards/test/README.md` | Pattern for immutable test corpus caching across read-only contract tests. |
| D5 | `docs/code-review/architecture.md` | Reference documentation for test git repository template caching. |
| D6 | `docs/standards/test/README.md` | Parallel file-scanning best practice in whole-repository assertion tests. |
| D7 | `docs/standards/test/README.md` | Synthetic release source usage guidelines for state and lifecycle testing. |
| D8 | `docs/plans/README.md` | ADR waiver: internal test optimization and non-breaking DI seams with no schema or public CLI changes. |
| AP1 | — | Documentation waiver: transient execution-approval gate row with no lasting architectural or operational rule. |
| H1 | `docs/standards/test/README.md` | Harvested test-authoring guidelines. |
| H2 | `docs/kyber-squad/architecture.md` | Harvested architecture seam for batched rendering. |
| H3 | `docs/code-review/architecture.md` | Harvested review-gates test fixture pattern. |
