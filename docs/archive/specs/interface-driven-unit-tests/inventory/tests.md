---
id: specs/interface-driven-unit-tests/inventory/tests
title: Inventory of concrete dependencies, Test projects
doc-type: reference
status: draft
owner: dpalfery
last-reviewed: 2026-11-17
component: CI Pipelines
---

# Inventory of concrete dependencies: Test projects

Produced 2026-11-17 by a read-only pass over every `.cs` file in scope, at commit `a5af901f` of the
role-based worker agents branch. Evidence for this specification, not living guidance: it goes stale
as the tasks land.


Read-only inventory of `tests/` in the kyber-weave checkout at
`/Users/dave/git/opencode-worktrees/kyber-weave/inventory` (detached HEAD). No
implementation was changed, nothing was built and no test was run.

## Bottom line

- **615 test classes** in seven test projects, **6,795** `[Fact]`/`[Theory]`
  cases. Only **117 classes (989 cases, 15%)** are true unit tests: in memory,
  through fakes, no disk, no process, no clock, no environment.
- **126 classes (1,025 cases)** are *scratch-disk only*: they arrange fixtures in
  a temp directory and drive a real adapter or store over it. They are not unit
  tests, but they need no process, clock or environment, and most of them can be
  converted by swapping the concrete store for the interface that already
  exists.
- **372 classes (4,781 cases, 70%)** need a real outside resource: 176 read the
  real product corpus from this checkout, 91 start processes (`git`, `dotnet`,
  `codegraph`, `codex`, `bash`), 56 depend on the wall clock, 25 on the
  environment, 22 on `Guid.NewGuid`/`Random`, 1 on a listening socket, 1 on
  `Console`.
- The single biggest cause is not a missing abstraction: it is **tests calling
  concrete product classes that already have an interface**. `PhysicalFileSystem`
  (27 test classes), `DocumentLoader`/`IDocumentSource` (31),
  `GateReportFacts`/`IGateReportFacts` (28), `SquadStateStore` (12) are
  constructed directly even though fakes for the same interface already exist
  in other test files.
- The genuinely **missing** product seams, in order of test classes freed:
  1. a corpus/document-source seam the tests can point at a fixture directory
     (`IDocumentSource` exists but is bypassed by 176 corpus test classes and by
     the `KyberWeaveTestPaths` helper, which re-implements repository-root
     discovery);
  2. `IProcessRunner` for the **static** `KyberWeave.Core.Processes.ProcessRunner`
     — `src` calls it directly in 23 places across 12 files, and only **one**
     constructor in the whole product takes `IProcessRunner`;
  3. `IGuidProvider`/`IIdGenerator` (nothing exists; `Guid.NewGuid` inside
     `SquadStateStore`, `SquadTransaction`, preset stores and ledger writers);
  4. `IConsole` (nothing exists; `Console.*` in 10 CLI command/renderer classes);
  5. `IProcessEnvironment` is already used in Arbiter but bypassed by
     `HooksCli`, `SquadHarnessLauncher`, `DocsMcpMigrateCommand`,
     `LaunchCommandResolver` and 20+ others.
- **Decision D5 needs one correction**: a large slice of the "not-unit" set is
  *adapter* tests (physical file system, kernel process runner, git facts,
  sqlite persistence, real release download). No interface removes those — they
  belong in `Integration.Tests`. The rest are logic tests that must be moved
  onto fakes, and the seam list below says which.

## How the classification was made

Every `.cs` file in the seven projects was scanned (643 files, ~222k lines,
including `tests/fixtures`). Each class body was extracted by brace matching and
scanned for the resource patterns the brief lists: `File.*`, `Directory.*`,
`FileInfo`, `FileStream`, `Path.GetTempPath/GetTempFileName`, `Process`/
`ProcessStartInfo`, program literals (`"git"`, `"dotnet"`, `"codegraph"`, …),
`Environment.*`, `OS` checks, `DateTime.Now/UtcNow`, `Stopwatch`, `Task.Delay`,
`Thread.Sleep`, `Random`, `Guid.NewGuid`, `HttpClient`/sockets, `Console.*`, and
`new ProductType(...)` for every type declared under `src/`.

Verdicts:

| verdict | meaning |
|---|---|
| `UNIT` | No outside resource of any kind. Fakes/stubs/value objects only. |
| `SCRATCH-DISK` | System.IO on a temp/scratch directory only; no process, clock, environment, network or corpus read. |
| `NOT-UNIT` | At least one of: real product corpus, real process, environment, clock, randomness/ids, network, console. |

Seam codes (the product abstraction that removes the real resource):

| code | real resource | product interface today | status |
|---|---|---|---|
| S1 | real product corpus / repository root (`KyberWeaveTestPaths`, `RepositoryRoot()`, `TestSquadCorpus`, `CorpusSquadReleaseSource`) | `IDocumentSource`, `IDocumentSourceFactory`, `ISquadSourceLocator`, `IProductRootLocator`, `ITestClassSources` | exists, bypassed; corpus *conformance* tests cannot be removed, only moved |
| S2 | file system (`File`, `Directory`, `FileInfo`, stores, sqlite, zip) | `IFileSystem` / `IWritableFileSystem` (`Data/Hooks/PhysicalFileSystem.cs`) plus ~40 store interfaces | exists, bypassed; adapter tests belong in Integration.Tests |
| S3 | process (`git`, `dotnet`, `codegraph`, `codex`, `bash`, `ollama`, MCP stdio) | `IProcessRunner` (`Data/Hooks/KernelProcessRunner.cs`), `IGitCommandRunner`, `IProcessExecutor` | exists, but static `ProcessRunner` is called directly 23 times |
| S4 | environment (`GetEnvironmentVariable`, `CurrentDirectory`, `UserName`, OS checks) | `IProcessEnvironment` (`Contracts/Repositories/IProcessEnvironment.cs`) | exists, used in Arbiter only |
| S5 | wall clock (`DateTime.UtcNow`, `Stopwatch`, `Task.Delay`) | `System.TimeProvider` + `FakeClock` (`Application.Tests/Hooks/Fakes.cs`) | the substitutable abstraction exists; product classes take no clock |
| S6 | ids/randomness (`Guid.NewGuid`, `Random`) | **none** | missing |
| S7 | network (`HttpClient`, sockets) | `IArbiterHttpHandlers`, `HttpMessageHandler` subclass | exists; the one case is acceptable (D) |
| S8 | console (`Console.*`) | **none** | missing |

Caveats: classification is by static pattern, not by tracing execution, so it is
an upper bound on "not-unit" (a scratch directory created only to arrange bytes
is still counted as disk). S1 is detected per file, so two test classes in a file
that uses `KyberWeaveTestPaths` are both marked S1. Spot checks of 25 classes
agreed with the tool 24/25 times; treat the counts as ±5%, not exact.

## Per project

| project | test classes | cases | UNIT | SCRATCH-DISK | NOT-UNIT | dominant seam |
|---|--:|--:|--:|--:|--:|---|
| KyberWeave.Tests | 567 | 6,396 | 114 | 125 | 328 | S1 corpus 139, S2 disk 125, S3 process 85 |
| KyberWeave.Application.Tests | 5 | 47 | 1 | 0 | 4 | S3 process 4 |
| KyberWeave.Domain.Tests | 5 | 55 | 2 | 0 | 3 | S3 2, S4 1 |
| KyberWeave.Hooks.Tests | 1 | 10 | 0 | 1 | 0 | S2 disk 1 |
| KyberWeave.Architecture.Tests | 1 | 4 | 0 | 0 | 1 | S1 corpus 1 |
| KyberWeave.PromptContent.Tests | 11 | 84 | 0 | 0 | 11 | S1 corpus 11 |
| KyberWeave.RepoPolicy.Tests | 25 | 199 | 0 | 0 | 25 | S1 corpus 25 |

Seam totals across all projects: **S1 176, S2 126, S3 91, S5 56, S4 25, S6 22,
S7 1, S8 1, none 117** (a class is counted under its primary seam only; 325
classes touch two or more seam families).

Two projects are effectively corpus test suites: `PromptContent.Tests` (11/11)
and `RepoPolicy.Tests` (25/25) read checked-in skills, agents, workflows, docs
and scripts from the checkout and assert on their text. `Application.Tests`,
`Domain.Tests` and `Hooks.Tests` are the intended shape — small, mostly fakes —
and are the model the other projects should follow.

## Grouped by missing seam

### S1 — real product corpus read from disk: 176 test classes (175 files)

What happens: the test walks up from `AppContext.BaseDirectory` to
`KyberWeave.sln` and then reads `docs/`, `products/`, `.apm/skills`, `scripts/`
and workflow files. The helper is `tests/KyberWeave.Tests/KyberWeaveTestPaths.cs`
(`LocateToolRoot`, `ToolRoot`, `McpProjectPath`, …); 159 files reference it, and
14 test files call a private `RepositoryRoot()` helper of their own (five of them
declare the identical `[CallerFilePath]` version).

Why it is not unit-testable: the bytes under test are *this checkout*, not a
fixture. Any edit to a skill, agent definition, doc or workflow changes the test
input; the suite cannot run from a packaged binary; and the tests fail if the
source tree is absent (that is why they run from a build output directory).

Product seams involved:

| seam | product file:class | how the test bypasses it |
|---|---|---|
| `IDocumentSource` | `src/KyberWeave.Data/Docs/Parsing/DocumentLoader.cs:19` | tests construct `DocumentLoader` with a real root (31 classes) or read files directly |
| `IDocumentSourceFactory` | `src/KyberWeave.Contracts/Repositories/IDocumentSourceFactory.cs` | unused by tests |
| `ISquadSourceLocator` / `ISquadSourceLoader` | `src/KyberWeave.Contracts/Repositories/ISquadSourceLocator.cs` | `SquadSourceTests` calls the **static** `KyberWeave.Core.Squad.Parsing.SquadSourceLoader.Load(path)`; `Fakes/CorpusSquadReleaseSource.cs`, `TestSquadCorpus.cs`, `SyntheticValidSquadReleaseSource.cs` read the real `products/` tree |
| `IProductRootLocator` | `src/KyberWeave.Contracts/Repositories/IProductRootLocator.cs` | `KyberWeaveTestPaths` duplicates its job |
| `ITestClassSources` | `src/KyberWeave.Contracts/Repositories/ITestClassSources.cs` | unused by tests |

What the specification should say: this group splits in two.
(a) *Content conformance* tests (most of `PromptContent.Tests`, `RepoPolicy.Tests`,
`SquadCanonicalContentTests`, `SkillProvenanceTests`, `WorkerRosterTests`,
renderer contract tests) assert that the checked-in artefacts say the right
thing. No interface makes them unit tests; they belong in `Integration.Tests`
(or a `Corpus.Tests` project) and should load the corpus through one shared
reader instead of 159 private copies of the root walk.
`SquadSourceLoader.Load` and `SquadSourceLoader`-style statics are the S1
equivalent of the static `ProcessRunner`: they take a directory and touch disk, so
no test can supply an in-memory corpus even when `ISquadSourceLoader` exists.

(b) Tests that only *need a corpus-shaped tree* (`SquadSourceTests`,
`SquadDeploymentStateTests`, `DocsScaffolderTests`, …) should take
`IDocumentSource`/`ISquadSourceLoader` over an in-memory or fixture-dictionary
corpus.

Largest S1 classes: `SquadDeploymentStateTests` (120 cases),
`SeatOutputParserTests` (71), `SquadSourceTests` (67), `SquadCliCommandTests`
(62), `SquadCanonicalContentTests` (53).

### S2 — real file system: 126 test classes

What happens: `File.*`, `Directory.*`, `FileInfo`, zip archives and sqlite files
under `Path.GetTempPath()`; the test then drives the real adapter over that
directory.

This group is the one most often mis-diagnosed as a missing seam. The interface
usually exists; the test chose the concrete implementation:

| concrete class constructed in tests | interface that already exists | test classes |
|---|---|--:|
| `PhysicalFileSystem` | `IWritableFileSystem` (`Data/Hooks/PhysicalFileSystem.cs:6`) | 27 |
| `DocumentLoader` | `IDocumentSource` | 31 |
| `GateReportFacts` | `IGateReportFacts` (`Data/Arbiter/Facts/GateReportFacts.cs:25`) | 28 |
| `SquadStateStore` | `ISquadStateStore` (`Data/Squad/Deployment/SquadStateStore.cs:13`) | 12 |
| `FileUserHostConfigStore` | `IUserHostConfigStore` | 10 |
| `SquadTransaction` | `ISquadTransaction` | 10 |
| `GitFacts` | `IGitRepositoryFacts` (`Data/Arbiter/Facts/GitFacts.cs:18`) | 10 |
| `PlanPathResolver` | `IPlanFileResolver` | 6 |
| `ArbiterRunStore` | `IArbiterRunStore` | 4 |
| `SqliteAnalysisPersistence` | `IAnalysisPersistence` | 2 |

Product classes with real I/O and **no interface at all** (the actual seam gap):
`DocSpecValidator` (`src/KyberWeave.Domain/Docs/Validation/DocSpecValidator.cs:18`),
`DocumentIndexHost` (`src/KyberWeave.Application/Docs/Search/DocumentIndexHost.cs:31`),
`TaskPromptService` (`src/KyberWeave.Application/Docs/Validation/TaskPromptService.cs:28`),
`TaskGateResolver` (`src/KyberWeave.Application/Review/TaskGateResolver.cs:55`),
`McpWiringInspector` (`src/KyberWeave.Application/Docs/Mcp/McpWiringInspector.cs:28`),
`McpWiringRepairer` (`src/KyberWeave.Application/Docs/Mcp/McpWiringRepairer.cs:41`),
`McpWiringReceipt` (`src/KyberWeave.Domain/Docs/Mcp/McpWiringReceipt.cs:60`),
`EditHookService` (`src/KyberWeave.Application/Hooks/EditHookService.cs:12`),
`RunDelivery` (`src/KyberWeave.Application/Docs/Delivery/RunDelivery.cs:29`),
`DocGraphExporter` (`src/KyberWeave.Data/Docs/Export/DocGraphExporter.cs:19`),
`PresentFile` (`src/KyberWeave.Data/Docs/Closeout/PresentFile.cs:7`),
`SquadInstanceLinkSync` (`src/KyberWeave.Data/Squad/Instances/SquadInstanceLinkSync.cs:22`),
`SquadHarnessLauncher` (`src/KyberWeave.Data/Squad/Instances/SquadHarnessLauncher.cs:14`),
`DevinImportOverlap` (`src/KyberWeave.Data/Squad/Deployment/DevinImportOverlap.cs:33`),
`StandardsFolderMigration` (`src/KyberWeave.Application/Docs/Scaffolding/StandardsFolderMigration.cs:55`),
`LaunchCommandResolver` (`src/KyberWeave.Data/Docs/Mcp/LaunchCommandResolver.cs:31`),
`KyberStandardsTemplates` (`src/KyberWeave.Domain/Docs/Scaffolding/KyberStandardsTemplates.cs:27`),
`FileReferenceExtractor` (`src/KyberWeave.Kernel/Parsing/FileReferenceExtractor.cs:104`).

Classify each as *adapter* (move the test to Integration.Tests) or *logic*
(take `IFileSystem`/`IWritableFileSystem` in the constructor and test over
`FakeFileSystem`). `Domain` classes such as `DocSpecValidator` and
`KyberStandardsTemplates` are the worst offenders because the Domain layer is
supposed to be pure; their tests currently need a temp directory.

Largest S2 classes: `DocsScaffolderTests` (51 cases), `DocsMcpMigrateCommandTests`
(38), `SquadPresetCliTests` (35), `ArbiterSetupCommandTests`, `DoctorEnsureTests`
(35).

### S3 — real process: 91 test classes

Programs actually started, counted by literal in the test bodies: `git` (17
classes), `codegraph` (16), `dotnet` (13), `bash`/`sh` (6), `codex`/`node` (a
few), plus MCP stdio servers started from the built product.

Root cause: `src/KyberWeave.Kernel/Processes/ProcessRunner.cs:23` is a **static
class**, so every caller is forced onto a real child process. `IProcessRunner`
exists in Contracts (`Data/Hooks/KernelProcessRunner.cs:9` adapter) but `src`
uses it as a constructor dependency in exactly **one** place, while
`ProcessRunner.Run` is called directly from 23 sites, including
`src/KyberWeave.Domain/StatusLine/UtilitiesStatusLineHarnessProbe.cs`,
`src/KyberWeave.Kernel/Processes/StdioSession.cs`,
`src/KyberWeave.Kernel/VersionControl/CodeGraphIndexStaleness.cs`,
`src/KyberWeave.Cli/Commands/Docs/DocsInitCommand.cs`,
`src/KyberWeave.Mcp/CodeGraphFreshness.cs`,
`src/KyberWeave.Data/Update/SelfUpdateSystem.cs`,
`src/KyberWeave.Data/Docs/Analysis/Persistence/SqliteAnalysisPersistence.cs`,
`src/KyberWeave.Data/Docs/Analysis/Persistence/AnalysisCacheSafety.cs`,
`src/KyberWeave.Data/Docs/FileSystem/KernelBoundedProcessExecutor.cs`,
`src/KyberWeave.Data/Docs/Mcp/GitWorktreeIdentity.cs`,
`src/KyberWeave.Data/Docs/Mcp/GitCheckoutProbe.cs`. There are also 12 direct
`Process.Start`/`new ProcessStartInfo` sites.

Fixes needed:
- delete the static `ProcessRunner` from the Domain-facing surface and inject
  `IProcessRunner`; a static class cannot be faked, which is why
  `ProcessRunnerTests`/`ProcessRunnerInputTests`/`StdioSessionTests` need real
  binaries;
- move git command construction behind `IGitCommandRunner` (exists) so tests do
  not need `Fakes/TestGitRepositoryTemplate.cs:1` (685 lines that run
  `git init`, `git add`, `git commit`, `git worktree`);
- add `IProcessRunner` to `SquadHarnessLauncher`, `SquadRunCommand`, `RunDelivery`
  and `HooksCli` (all currently no-interface classes).

Largest S3 classes: `UpdateCommandTests` (78 cases),
`DocumentationAnalyzerTests` (42), `RunDeliveryTests` (42),
`McpCodeGraphRefreshTests` (54), `UtilitiesCliCommandTests` (50),
`ReviewFullSuiteGuardTests` (47).

### S4 — environment: 25 test classes

`Environment.GetEnvironmentVariable` (mostly `HOME`/`USERPROFILE`,
`KYBER_*`, `XDG_*`, `PATH`), `Environment.CurrentDirectory`,
`Environment.UserName`, `OperatingSystem.IsWindows/IsMacOS`.

`IProcessEnvironment` exists (`src/KyberWeave.Contracts/Repositories/IProcessEnvironment.cs`)
and Arbiter tests already fake it (`ArbiterTestHost.cs:53 FakeProcessEnvironment`),
but these product classes read `Environment` directly and are therefore untestable
without mutating the process: `HooksCli` (`src/KyberWeave.Hooks/HooksCli.cs:11`),
`SquadHarnessLauncher`, `DocsInitCommand`, `DocsMcpMigrateCommand`,
`LaunchCommandResolver`, `PathContainment` (Domain tests), plus the release
download path in `GitHubSquadReleaseSource`.

Fix: inject `IProcessEnvironment` everywhere; ban `Environment.` in `src` by
analyzer (RepoPolicy already has a `ProcessSpawnSitesTests`-style guard test, so
the pattern is established).

### S5 — wall clock: 56 test classes

`DateTime.UtcNow`, `Stopwatch`, `Task.Delay`/`Thread.Sleep` used as logic.
`TimeProvider` is the accepted substitutable abstraction and `FakeClock`
(`tests/KyberWeave.Application.Tests/Hooks/Fakes.cs:67`) already proves it works,
but the product classes are not given one: `ArbiterEvaluator`
(`src/KyberWeave.Application/Arbiter/ArbiterEvaluator.cs:33`),
`ArbiterRunStore` (`Data/Arbiter/Stores/ArbiterRunStore.cs:27`),
`SquadStateMigration` (`Data/Squad/Deployment/SquadStateMigration.cs:21`),
`DocsCloseoutCommand`, `McpWiringRepairer`.

Fix: constructor `TimeProvider` (default `TimeProvider.System`) on those classes;
tests use `FakeClock`. No new interface is needed — this is category **D**
(`TimeProvider` is a framework abstract class designed for substitution).

### S6 — ids and randomness: 22 test classes

`Guid.NewGuid()` inside `SquadStateStore` (`Data/Squad/Deployment/SquadStateStore.cs:13`),
`SquadTransaction` (`:27`), `SquadCanonicalProfileStore` (`:26`),
`SquadModelPresetSelectionStore` (`:18`), `PhysicalSquadFileAccess`
(`Data/Squad/Deployment/SquadDeploymentAdapters.cs:108`), and the ledger/event
writers. Tests assert on ids and ordering only by reading them back, and
`AgentGovernanceTests`/`SquadDeploymentStateTests` additionally use `Random`.

No interface exists. **New seam: `IGuidProvider`** with
`Guid NewGuid()` and `string NewId(string prefix)`; default implementation wraps
`Guid.NewGuid`, test implementation returns a deterministic sequence. Replaces
the 6 product call sites above and removes the `.random` tag from ~22 test
classes.

### S7 — network: 1 test class (acceptable)

`Arbiter/ArbiterSystemOneClientTests.cs` uses `StubHandler : HttpMessageHandler`.
`HttpMessageHandler` is a framework class with `SendAsync` virtual — category
**D**, acceptable, no seam needed. `IArbiterHttpHandlers` already exists for the
Arbiter-specific case.

### S8 — console: 1 test class

`ReviewGatesCouncilCommandTests` captures `Console` output. Ten product classes
write through `Console.*` with no abstraction: `DocsInitCommand`,
`DocsPacketCommand`, `DocsTasksCommand`, `DoctorCommand`, `DoctorReportRenderer`,
`ReviewCatalogueCommand`, `ReviewSuppressionsCommand`, `RunStartCommand`,
`SquadRunCommand`, `DocsMcpMigrateCommand`. **New seam: `IConsole`** (`Out`,
`Error`, `Write*`, `IsInputRedirected`) or a narrower `IOutputWriter`; the CLI
already mostly returns exit codes, so a writer is enough.

## Top five classes per project by non-unit size

KyberWeave.Tests: `SquadDeploymentStateTests` (120 cases, 7,862 lines, S1+S2+S3+S4+S5),
`SquadCliCommandTests` (62, 2,279, S1+S2+S5), `UpdateCommandTests` (78, 2,195,
S2+S3), `UtilitiesStatusLineArtifactTests` (S1+S2, 1,830), `SquadSourceTests`
(67, 1,714, S1+S2+S3+S4).

Application.Tests: `EditHookServiceTests` (23, S3 — starts real process for the
edit hook), `RealDataCompositionTests` (13, S3), `HooksConfigSourceTests` (5,
S2+S3), `KernelProcessRunnerTests` (5, S3 adapter — belongs in Integration.Tests).

Domain.Tests: `HookRouterTests` (22, S3), `HooksDefaultsTests` (10, S3),
`PathContainmentTests` (6, S4). Two classes are clean units.

RepoPolicy.Tests: `ReleaseTests` (69), `ReleaseNotesTests` (20),
`FastLoopScriptTests` (10), `DistributionManifestTests` (9), `PrNamingScriptTests`
(8) — all read repository files and start scripts; whole project belongs in
Integration.Tests or a repo-policy suite that is explicitly allowed to read the
checkout.

## Existing fakes

`tests/KyberWeave.Tests/Fakes/` holds 20 files, but only **4 are true fakes of a
product interface**; the rest are fixtures, harnesses and corpus readers. A fifth
(`FakeFileSystem`) lives in `KyberWeave.Application.Tests/Hooks/Fakes.cs`:

| file | class | implements | notes |
|---|---|---|---|
| `Application.Tests/Hooks/Fakes.cs:7` | `FakeFileSystem` | `IFileSystem` | in-memory; the only generic FS fake |
| `FakeProcessExecutor.cs:12` | `FakeProcessExecutor` | `IProcessExecutor` | |
| `FakeSquadReleaseSource.cs:8` | `FakeSquadReleaseSource` | `ISquadReleaseSource` | |
| `FakeSquadRenderer.cs:13` | `FakeSquadRenderer` | `ISquadRenderer` | |
| `FakeSquadUserPaths.cs:8` | `FakeSquadUserPaths` | `ISquadUserPaths` | |
| `Application.Tests/Hooks/Fakes.cs:93` | `FakeProcessRunner` | `IProcessRunner` | |
| `Application.Tests/Hooks/Fakes.cs:67` | `FakeClock` | `TimeProvider` | |
| `CorpusSquadReleaseSource`, `TestSquadCorpus`, `SyntheticValidSquadReleaseSource`, `TestGitRepositoryTemplate`, `SquadHomeHarness`, `SquadMcpGrantProjection`, `SquadTestCommands`, `DocsTestCommands` | | — | fixtures that read the real tree or run real git |

Everything else lives as **private nested classes inside test files**. Duplicates
for the same interface (the maintenance problem this inventory found):

| interface | fake classes found | where |
|---|--:|---|
| `IArbiterProvider` | 30 | 20+ files (`FakeProvider`, `StubProvider`, `ScriptedProvider`, …) |
| `ISquadReleaseSource` | 11 | `Fakes/` + 6 test files |
| `ISquadRenderer` | 11 | `Fakes/FakeSquadRenderer` + 10 files |
| `ISquadUserPaths` | 9 | 6 files (`FakeUserPaths` ×4, `FakeSquadUserPaths` ×2, …) |
| `IProcessEnvironment` | 9 | `ArbiterTestHost`, Doctor, Mcp, Squad tests |
| `ISquadGlobalRootResolver` | 8 | 6 files |
| `IArbiterPlanReader` | 8 | 5 files |
| `IArbiterGitFacts` | 8 | 4 files |
| `IHookDecisionEngine` / `IContextualHookDecisionEngine` | 7 / 5 | hook tests |
| `IProcessExecutor` | 7 | `Fakes/` + 3 files |
| `ICodeGraphResolver` | 5 | 4 files |
| `ICredentialProcessRunner` | 3 | Arbiter tests |
| `IFileSystem` | 2 | `FakeFileSystem`, `PhysicalTestFileSystem` |
| `IProcessRunner` | 2 | `FakeProcessRunner`, `StubRunner` |
| `ISquadLinkFileSystem` | 2 | `SquadInstanceServiceTests:426`, `SquadInstanceLinkSyncTests:731` (same class twice) |
| `ICredentialStore` | 2 | `ArbiterSetupCommandTests:474`, `ArbiterUninstallCommandTests:682` (same class twice) |

`ISquadLinkFileSystem` and `ICredentialStore` are byte-identical copies; they
should be hoisted into one shared `Fakes` library (or a
`KyberWeave.Testing.Fakes` project) before the per-project test split that D5
requires, otherwise the split multiplies them by seven.

## What this means for the D5 specification

1. Add a `KyberWeave.Testing.Fakes` project (or a shared `Fakes/` folder linked
   into each test project) and move the duplicated fakes there first. The split
   into one project per product project will otherwise duplicate the 100+ nested
   fakes.
2. Fix the process seam first: remove the static `ProcessRunner.Run` from every
   layer above `Data`, inject `IProcessRunner`. That alone unblocks 91 test
   classes and 12 `src` files, and is required before `Domain`/`Application`
   tests can be process-free.
3. Add `IGuidProvider` and `IConsole`; inject `TimeProvider` and
   `IProcessEnvironment` where they are currently read statically.
4. Replace `KyberWeaveTestPaths` with one injected corpus reader, and declare the
   *corpus conformance* tests (176 classes, 175 files) an explicit
   Integration/Corpus suite. They cannot become unit tests; they should be
   honest about what they read instead of 159 copies of a root walk.
5. Move the adapter tests (physical FS, kernel process runner, git facts, sqlite,
   release download) to `Integration.Tests`, as D5 already says, and make the
   remaining logic tests take interfaces. The S2 table above is the work list.
6. Keep the `Arbiter`, `Domain.Tests` and `Application.Tests` patterns: they are
   the only places where the product's own interfaces are consistently faked.

## Coverage and gaps

Every `.cs` file under `tests/KyberWeave.Tests`, `tests/KyberWeave.Application.Tests`,
`tests/KyberWeave.Domain.Tests`, `tests/KyberWeave.Hooks.Tests`,
`tests/KyberWeave.Architecture.Tests`, `tests/KyberWeave.PromptContent.Tests` and
`tests/KyberWeave.RepoPolicy.Tests` was scanned, including `Fakes/`, `Arbiter/`,
`Squad/`, `ProseRules/`, `CopilotPrompt/`, `Fixtures/`, `Networking/`,
`Configuration/` and `Hooks/`. `tests/fixtures/` holds 13 tiny C# files for the
gate-catalogue fixture repos; they contain no tests and need no classification
beyond being fixture data (category **D**).

Not done, because of the time box: the per-class rows below are machine-produced
and were spot-checked, not read one by one; test classes whose file only arranges
a scratch tree before driving fakes may be over-counted as `SCRATCH-DISK`; and
`Contract` tests that assert on file *bytes* (e.g. `McpDeclarationSurfaceTests`)
were not separated from those asserting on *content*. A follow-up should re-read
the ~150 classes whose evidence lists three or more resources and reclassify by
hand.

## Appendix: every test class

| # | project | file:line | test class | tests | verdict | seam | evidence |
|--:|---|---|---|--:|---|---|---|
| 1 | Tests | KyberWeave.Tests/AgentGovernanceTests.cs:10 | AgentGovernanceTests | 9 | NOT-UNIT | S6 | disk+tempdir+random |
| 2 | Tests | KyberWeave.Tests/AgentGovernanceTests.cs:263 | AgentSyncLinterTests | 2 | SCRATCH-DISK | S2 | disk |
| 3 | Tests | KyberWeave.Tests/AgentGovernanceTests.cs:318 | AgentSpecValidatorTests | 12 | SCRATCH-DISK | S2 | disk+tempdir |
| 4 | Tests | KyberWeave.Tests/AnalysisPersistenceTests.cs:16 | AnalysisPersistenceTests | 23 | NOT-UNIT | S3 | disk+tempdir+Process+cli+env+time |
| 5 | Tests | KyberWeave.Tests/AntigravityRendererContractTests.cs:24 | AntigravityRendererContractTests | 28 | NOT-UNIT | S1 | disk+cli+corpus |
| 6 | Tests | KyberWeave.Tests/Arbiter/AntigravityHookAdapterTests.cs:25 | AntigravityHookAdapterTests | 16 | NOT-UNIT | S1 | disk+tempdir+corpus; sut=AntigravityHookAdapter |
| 7 | Tests | KyberWeave.Tests/Arbiter/ArbiterCalibrationTests.cs:20 | ArbiterCalibrationTests | 11 | NOT-UNIT | S4 | disk+tempdir+env+time+net |
| 8 | Tests | KyberWeave.Tests/Arbiter/ArbiterCanonicalCheckoutTests.cs:24 | ArbiterCanonicalCheckoutTests | 18 | NOT-UNIT | S3 | disk+tempdir+Process+cli+time |
| 9 | Tests | KyberWeave.Tests/Arbiter/ArbiterChangedHunksTests.cs:24 | ArbiterChangedHunksTests | 20 | SCRATCH-DISK | S2 | disk+tempdir |
| 10 | Tests | KyberWeave.Tests/Arbiter/ArbiterClassificationTests.cs:15 | ArbiterClassificationTests | 38 | UNIT | - |  |
| 11 | Tests | KyberWeave.Tests/Arbiter/ArbiterCommentShadowToolTests.cs:24 | ArbiterCommentShadowToolTests | 6 | SCRATCH-DISK | S2 | disk+tempdir |
| 12 | Tests | KyberWeave.Tests/Arbiter/ArbiterConfigTests.cs:15 | ArbiterConfigTests | 20 | UNIT | - | sut=ArbiterConfig |
| 13 | Tests | KyberWeave.Tests/Arbiter/ArbiterDecisionRecordSavingsFieldsTests.cs:14 | ArbiterDecisionRecordSavingsFieldsTests | 6 | UNIT | - |  |
| 14 | Tests | KyberWeave.Tests/Arbiter/ArbiterDefaultRulesTests.cs:24 | ArbiterDefaultRulesTests | 29 | UNIT | - |  |
| 15 | Tests | KyberWeave.Tests/Arbiter/ArbiterDispatchBudgetTests.cs:16 | ArbiterDispatchBudgetTests | 18 | NOT-UNIT | S3 | disk+tempdir+cli |
| 16 | Tests | KyberWeave.Tests/Arbiter/ArbiterDuplicateFindingRuleTests.cs:38 | ArbiterDuplicateFindingRuleTests | 18 | NOT-UNIT | S5 | disk+tempdir+time+random |
| 17 | Tests | KyberWeave.Tests/Arbiter/ArbiterEscalationEnvelopeTests.cs:12 | ArbiterEscalationEnvelopeTests | 17 | UNIT | - | sut=ArbiterEscalationEnvelope |
| 18 | Tests | KyberWeave.Tests/Arbiter/ArbiterEvaluatorModelWriteFailureTests.cs:21 | ArbiterEvaluatorModelWriteFailureTests | 2 | NOT-UNIT | S5 | disk+tempdir+time |
| 19 | Tests | KyberWeave.Tests/Arbiter/ArbiterEvaluatorTests.cs:19 | ArbiterEvaluatorTests | 17 | NOT-UNIT | S5 | disk+tempdir+time+random; sut=ArbiterEvaluator |
| 20 | Tests | KyberWeave.Tests/Arbiter/ArbiterExistsFalseRuleTests.cs:16 | ArbiterExistsFalseRuleTests | 6 | UNIT | - |  |
| 21 | Tests | KyberWeave.Tests/Arbiter/ArbiterFactBuilderTests.cs:16 | ArbiterFactBuilderTests | 10 | UNIT | - |  |
| 22 | Tests | KyberWeave.Tests/Arbiter/ArbiterFailOpenContractTests.cs:20 | ArbiterFailOpenContractTests | 10 | NOT-UNIT | S5 | disk+tempdir+time+random |
| 23 | Tests | KyberWeave.Tests/Arbiter/ArbiterFamilyEffectsTests.cs:15 | ArbiterFamilyEffectsTests | 24 | UNIT | - |  |
| 24 | Tests | KyberWeave.Tests/Arbiter/ArbiterFindingChangedLinesBoundTests.cs:13 | ArbiterFindingChangedLinesBoundTests | 3 | UNIT | - |  |
| 25 | Tests | KyberWeave.Tests/Arbiter/ArbiterFindingKeyTests.cs:11 | ArbiterFindingKeyTests | 1 | UNIT | - |  |
| 26 | Tests | KyberWeave.Tests/Arbiter/ArbiterFix1ContractTests.cs:23 | ArbiterFix1ContractTests | 6 | NOT-UNIT | S1 | disk+Process+corpus |
| 27 | Tests | KyberWeave.Tests/Arbiter/ArbiterForEachDuplicateItemTests.cs:15 | ArbiterForEachDuplicateItemTests | 1 | NOT-UNIT | S6 | disk+tempdir+random |
| 28 | Tests | KyberWeave.Tests/Arbiter/ArbiterForEachEmptyListTests.cs:16 | ArbiterForEachEmptyListTests | 2 | NOT-UNIT | S6 | disk+tempdir+random |
| 29 | Tests | KyberWeave.Tests/Arbiter/ArbiterForEachExpansionTests.cs:18 | ArbiterForEachExpansionTests | 2 | NOT-UNIT | S5 | disk+tempdir+time+random |
| 30 | Tests | KyberWeave.Tests/Arbiter/ArbiterForEachScopingTests.cs:15 | ArbiterForEachScopingTests | 1 | NOT-UNIT | S6 | disk+tempdir+random |
| 31 | Tests | KyberWeave.Tests/Arbiter/ArbiterForEachUnreadableListTests.cs:17 | ArbiterForEachUnreadableListTests | 1 | NOT-UNIT | S6 | disk+tempdir+random |
| 32 | Tests | KyberWeave.Tests/Arbiter/ArbiterGateReportRuleTests.cs:30 | ArbiterGateReportRuleTests | 17 | NOT-UNIT | S1 | disk+tempdir+cli+random+corpus |
| 33 | Tests | KyberWeave.Tests/Arbiter/ArbiterHeaderlessCopilotCliTests.cs:18 | ArbiterHeaderlessCopilotCliTests | 6 | SCRATCH-DISK | S2 | disk+tempdir |
| 34 | Tests | KyberWeave.Tests/Arbiter/ArbiterHeaderlessDelegateIdentityTests.cs:18 | ArbiterHeaderlessDelegateIdentityTests | 10 | SCRATCH-DISK | S2 | disk+tempdir |
| 35 | Tests | KyberWeave.Tests/Arbiter/ArbiterHookHostTests.cs:20 | ArbiterHookHostTests | 12 | NOT-UNIT | S1 | disk+corpus |
| 36 | Tests | KyberWeave.Tests/Arbiter/ArbiterHookOutcomeTests.cs:22 | ArbiterHookOutcomeTests | 7 | NOT-UNIT | S5 | tempdir+time |
| 37 | Tests | KyberWeave.Tests/Arbiter/ArbiterKeyResolutionTests.cs:21 | ArbiterKeyResolutionTests | 24 | NOT-UNIT | S3 | disk+tempdir+Process+env |
| 38 | Tests | KyberWeave.Tests/Arbiter/ArbiterKiloCouncilItem3Tests.cs:27 | ArbiterKiloCouncilItem3Tests | 1 | SCRATCH-DISK | S2 | disk+tempdir |
| 39 | Tests | KyberWeave.Tests/Arbiter/ArbiterKiloLeadsTests.cs:35 | ArbiterKiloLeadsTests | 11 | NOT-UNIT | S7 | disk+tempdir+time+net |
| 40 | Tests | KyberWeave.Tests/Arbiter/ArbiterLedgerFeedTests.cs:20 | ArbiterLedgerFeedTests | 22 | NOT-UNIT | S5 | disk+tempdir+time+random |
| 41 | Tests | KyberWeave.Tests/Arbiter/ArbiterLedgerTests.cs:22 | ArbiterLedgerTests | 22 | NOT-UNIT | S5 | disk+tempdir+time+random |
| 42 | Tests | KyberWeave.Tests/Arbiter/ArbiterLensCleanTreeTests.cs:34 | ArbiterLensCleanTreeTests | 2 | SCRATCH-DISK | S2 | disk |
| 43 | Tests | KyberWeave.Tests/Arbiter/ArbiterLensNoCodeChangeRuleTests.cs:26 | ArbiterLensNoCodeChangeRuleTests | 8 | NOT-UNIT | S5 | disk+tempdir+time |
| 44 | Tests | KyberWeave.Tests/Arbiter/ArbiterMcpPackagingTests.cs:27 | ArbiterMcpPackagingTests | 21 | NOT-UNIT | S1 | disk+tempdir+Console+corpus |
| 45 | Tests | KyberWeave.Tests/Arbiter/ArbiterMode002FailingRunFirstTests.cs:18 | ArbiterMode002FailingRunFirstTests | 12 | SCRATCH-DISK | S2 | disk |
| 46 | Tests | KyberWeave.Tests/Arbiter/ArbiterModelDeliveryTests.cs:29 | ArbiterModelDeliveryTests | 6 | NOT-UNIT | S1 | disk+tempdir+corpus |
| 47 | Tests | KyberWeave.Tests/Arbiter/ArbiterModelTargetFactsTests.cs:22 | ArbiterModelTargetFactsTests | 14 | SCRATCH-DISK | S2 | tempdir |
| 48 | Tests | KyberWeave.Tests/Arbiter/ArbiterModelTargetRulesTests.cs:26 | ArbiterModelTargetRulesTests | 14 | NOT-UNIT | S5 | disk+tempdir+time |
| 49 | Tests | KyberWeave.Tests/Arbiter/ArbiterModelTargetsReaderTests.cs:14 | ArbiterModelTargetsReaderTests | 6 | SCRATCH-DISK | S2 | disk+tempdir |
| 50 | Tests | KyberWeave.Tests/Arbiter/ArbiterNitpickRuleTests.cs:33 | ArbiterNitpickRuleTests | 24 | NOT-UNIT | S5 | disk+tempdir+time+random |
| 51 | Tests | KyberWeave.Tests/Arbiter/ArbiterNotEvaluatedEffectTests.cs:16 | ArbiterNotEvaluatedEffectTests | 2 | NOT-UNIT | S6 | disk+tempdir+random |
| 52 | Tests | KyberWeave.Tests/Arbiter/ArbiterNothingTakesPartTests.cs:15 | ArbiterNothingTakesPartTests | 3 | NOT-UNIT | S6 | disk+tempdir+random |
| 53 | Tests | KyberWeave.Tests/Arbiter/ArbiterNullConfidenceFloorTests.cs:15 | ArbiterNullConfidenceFloorTests | 2 | NOT-UNIT | S6 | disk+tempdir+random |
| 54 | Tests | KyberWeave.Tests/Arbiter/ArbiterParallelDispatchAttributionTests.cs:25 | ArbiterParallelDispatchAttributionTests | 11 | NOT-UNIT | S3 | disk+tempdir+Process+cli+time |
| 55 | Tests | KyberWeave.Tests/Arbiter/ArbiterPlanParserTests.cs:12 | ArbiterPlanParserTests | 18 | NOT-UNIT | S1 | disk+corpus |
| 56 | Tests | KyberWeave.Tests/Arbiter/ArbiterPlanReadRuleTests.cs:32 | ArbiterPlanReadRuleTests | 8 | NOT-UNIT | S1 | disk+corpus |
| 57 | Tests | KyberWeave.Tests/Arbiter/ArbiterPlanWorktreeTests.cs:19 | ArbiterPlanWorktreeTests | 7 | NOT-UNIT | S5 | disk+tempdir+time |
| 58 | Tests | KyberWeave.Tests/Arbiter/ArbiterPlannerDiffRuleTests.cs:23 | ArbiterPlannerDiffRuleTests | 12 | SCRATCH-DISK | S2 | disk+tempdir |
| 59 | Tests | KyberWeave.Tests/Arbiter/ArbiterPlannerRuleTests.cs:19 | ArbiterPlannerRuleTests | 7 | UNIT | - |  |
| 60 | Tests | KyberWeave.Tests/Arbiter/ArbiterPreEventBlankModelTests.cs:20 | ArbiterPreEventBlankModelTests | 1 | NOT-UNIT | S5 | disk+tempdir+time |
| 61 | Tests | KyberWeave.Tests/Arbiter/ArbiterPrimaryShadowTests.cs:24 | ArbiterPrimaryShadowTests | 3 | NOT-UNIT | S5 | tempdir+time |
| 62 | Tests | KyberWeave.Tests/Arbiter/ArbiterProviderFactoryFailSafeTests.cs:18 | ArbiterProviderFactoryFailSafeTests | 2 | SCRATCH-DISK | S2 | disk+tempdir |
| 63 | Tests | KyberWeave.Tests/Arbiter/ArbiterReadGuardTests.cs:18 | ArbiterReadGuardTests | 23 | NOT-UNIT | S1 | disk+corpus |
| 64 | Tests | KyberWeave.Tests/Arbiter/ArbiterReadersTests.cs:16 | ArbiterReadersTests | 11 | NOT-UNIT | S3 | disk+tempdir+Process+cli+random |
| 65 | Tests | KyberWeave.Tests/Arbiter/ArbiterRealEngineTests.cs:24 | ArbiterRealEngineTests | 18 | NOT-UNIT | S4 | disk+tempdir+env |
| 66 | Tests | KyberWeave.Tests/Arbiter/ArbiterReviewCommandHandOffTests.cs:11 | ArbiterReviewCommandHandOffTests | 1 | UNIT | - |  |
| 67 | Tests | KyberWeave.Tests/Arbiter/ArbiterReviewRunAndFindingCapTests.cs:29 | ArbiterReviewRunAndFindingCapTests | 6 | NOT-UNIT | S5 | disk+tempdir+time+random |
| 68 | Tests | KyberWeave.Tests/Arbiter/ArbiterReworkDispatchTests.cs:24 | ArbiterReworkDispatchTests | 2 | NOT-UNIT | S5 | disk+tempdir+time+random |
| 69 | Tests | KyberWeave.Tests/Arbiter/ArbiterRosterRuleTests.cs:14 | ArbiterRosterRuleTests | 1 | UNIT | - |  |
| 70 | Tests | KyberWeave.Tests/Arbiter/ArbiterRuleEngineTests.cs:12 | ArbiterRuleEngineTests | 43 | UNIT | - |  |
| 71 | Tests | KyberWeave.Tests/Arbiter/ArbiterRuleIdAtSignTests.cs:15 | ArbiterRuleIdAtSignTests | 2 | UNIT | - |  |
| 72 | Tests | KyberWeave.Tests/Arbiter/ArbiterRunStartGuardTests.cs:22 | ArbiterRunStartGuardTests | 23 | NOT-UNIT | S3 | disk+tempdir+Process+cli+time |
| 73 | Tests | KyberWeave.Tests/Arbiter/ArbiterRunStoreTests.cs:26 | ArbiterRunStoreTests | 21 | NOT-UNIT | S5 | disk+tempdir+time+random; sut=ArbiterRunStore |
| 74 | Tests | KyberWeave.Tests/Arbiter/ArbiterSameFailureRuleTests.cs:38 | ArbiterSameFailureRuleTests | 13 | NOT-UNIT | S5 | disk+tempdir+time+random |
| 75 | Tests | KyberWeave.Tests/Arbiter/ArbiterSavingsConfigTests.cs:15 | ArbiterSavingsConfigTests | 11 | NOT-UNIT | S1 | corpus |
| 76 | Tests | KyberWeave.Tests/Arbiter/ArbiterSavingsRoleContractTests.cs:15 | ArbiterSavingsRoleContractTests | 1 | NOT-UNIT | S1 | disk+corpus |
| 77 | Tests | KyberWeave.Tests/Arbiter/ArbiterSecondaryAllowEquivalentTests.cs:15 | ArbiterSecondaryAllowEquivalentTests | 1 | NOT-UNIT | S6 | disk+tempdir+random |
| 78 | Tests | KyberWeave.Tests/Arbiter/ArbiterSecondaryShadowStateTests.cs:15 | ArbiterSecondaryShadowStateTests | 1 | NOT-UNIT | S6 | disk+tempdir+random |
| 79 | Tests | KyberWeave.Tests/Arbiter/ArbiterSecondaryTargetTests.cs:18 | ArbiterSecondaryTargetTests | 2 | NOT-UNIT | S5 | tempdir+time |
| 80 | Tests | KyberWeave.Tests/Arbiter/ArbiterSecondaryTriggerTests.cs:21 | ArbiterSecondaryTriggerTests | 16 | NOT-UNIT | S5 | disk+tempdir+time+random |
| 81 | Tests | KyberWeave.Tests/Arbiter/ArbiterSecretToolMissingTests.cs:15 | ArbiterSecretToolMissingTests | 4 | NOT-UNIT | S3 | Process |
| 82 | Tests | KyberWeave.Tests/Arbiter/ArbiterSectionAndCommentRuleTests.cs:19 | ArbiterSectionAndCommentRuleTests | 5 | NOT-UNIT | S1 | disk+corpus |
| 83 | Tests | KyberWeave.Tests/Arbiter/ArbiterSectionBoundsTests.cs:17 | ArbiterSectionBoundsTests | 4 | SCRATCH-DISK | S2 | disk+tempdir |
| 84 | Tests | KyberWeave.Tests/Arbiter/ArbiterSectionReadBoundTests.cs:12 | ArbiterSectionReadBoundTests | 6 | SCRATCH-DISK | S2 | disk+tempdir |
| 85 | Tests | KyberWeave.Tests/Arbiter/ArbiterSectionsAndCommentToolTests.cs:24 | ArbiterSectionsAndCommentToolTests | 3 | SCRATCH-DISK | S2 | disk+tempdir |
| 86 | Tests | KyberWeave.Tests/Arbiter/ArbiterSemanticProfilesParityTests.cs:13 | ArbiterSemanticProfilesParityTests | 1 | NOT-UNIT | S1 | corpus |
| 87 | Tests | KyberWeave.Tests/Arbiter/ArbiterServeCallerSourceTests.cs:19 | ArbiterServeCallerSourceTests | 1 | SCRATCH-DISK | S2 | disk+tempdir |
| 88 | Tests | KyberWeave.Tests/Arbiter/ArbiterServeToolResponseTests.cs:23 | ArbiterServeToolResponseTests | 10 | SCRATCH-DISK | S2 | disk+tempdir |
| 89 | Tests | KyberWeave.Tests/Arbiter/ArbiterServedContainmentTests.cs:25 | ArbiterServedContainmentTests | 4 | SCRATCH-DISK | S2 | disk+tempdir |
| 90 | Tests | KyberWeave.Tests/Arbiter/ArbiterShadowVetoTests.cs:15 | ArbiterShadowVetoTests | 4 | NOT-UNIT | S6 | disk+tempdir+random |
| 91 | Tests | KyberWeave.Tests/Arbiter/ArbiterShortcutRuleTests.cs:37 | ArbiterShortcutRuleTests | 31 | NOT-UNIT | S5 | disk+tempdir+time+random |
| 92 | Tests | KyberWeave.Tests/Arbiter/ArbiterSquadCatalogTests.cs:14 | ArbiterSquadCatalogTests | 6 | NOT-UNIT | S1 | disk+corpus; sut=ArbiterSquadCatalog |
| 93 | Tests | KyberWeave.Tests/Arbiter/ArbiterStepOneFactsTests.cs:19 | ArbiterStepOneFactsTests | 10 | SCRATCH-DISK | S2 | disk+tempdir |
| 94 | Tests | KyberWeave.Tests/Arbiter/ArbiterSummaryCommentTriageTests.cs:30 | ArbiterSummaryCommentTriageTests | 14 | NOT-UNIT | S5 | disk+tempdir+time |
| 95 | Tests | KyberWeave.Tests/Arbiter/ArbiterSummaryCommentTriageToolTests.cs:26 | ArbiterSummaryCommentTriageToolTests | 5 | SCRATCH-DISK | S2 | disk+tempdir |
| 96 | Tests | KyberWeave.Tests/Arbiter/ArbiterSuppressedSecondaryEffectTests.cs:15 | ArbiterSuppressedSecondaryEffectTests | 2 | NOT-UNIT | S6 | disk+tempdir+random |
| 97 | Tests | KyberWeave.Tests/Arbiter/ArbiterSystemOneClientTests.cs:17 | ArbiterSystemOneClientTests | 39 | NOT-UNIT | S5 | time |
| 98 | Tests | KyberWeave.Tests/Arbiter/ArbiterTechnologyRuleScopeTests.cs:14 | ArbiterTechnologyRuleScopeTests | 9 | SCRATCH-DISK | S2 | tempdir |
| 99 | Tests | KyberWeave.Tests/Arbiter/ArbiterUnobservableReturnsTests.cs:27 | ArbiterUnobservableReturnsTests | 10 | SCRATCH-DISK | S2 | disk+tempdir |
| 100 | Tests | KyberWeave.Tests/Arbiter/ArbiterUntrackedSymlinkTests.cs:15 | ArbiterUntrackedSymlinkTests | 2 | SCRATCH-DISK | S2 | disk |
| 101 | Tests | KyberWeave.Tests/Arbiter/ArchitectShellGuardSyncTests.cs:16 | ArchitectShellGuardSyncTests | 3 | NOT-UNIT | S1 | disk+corpus |
| 102 | Tests | KyberWeave.Tests/Arbiter/ArchitectShellGuardTests.cs:19 | ArchitectShellGuardTests | 22 | NOT-UNIT | S1 | corpus; sut=ArchitectShellGuard |
| 103 | Tests | KyberWeave.Tests/Arbiter/ClaudeHandbackTests.cs:23 | ClaudeHandbackTests | 7 | NOT-UNIT | S1 | disk+random+corpus |
| 104 | Tests | KyberWeave.Tests/Arbiter/ClaudeHookAdapterModelWriteTests.cs:17 | ClaudeHookAdapterModelWriteTests | 5 | NOT-UNIT | S1 | disk+corpus |
| 105 | Tests | KyberWeave.Tests/Arbiter/ClaudeHookAdapterTests.cs:20 | ClaudeHookAdapterTests | 33 | NOT-UNIT | S1 | disk+random+corpus; sut=ClaudeHookAdapter |
| 106 | Tests | KyberWeave.Tests/Arbiter/CodexHookAdapterTests.cs:21 | CodexHookAdapterTests | 14 | NOT-UNIT | S1 | disk+corpus; sut=CodexHookAdapter |
| 107 | Tests | KyberWeave.Tests/Arbiter/CommentSyntaxGoDirectiveSpacingTests.cs:12 | CommentSyntaxGoDirectiveSpacingTests | 1 | UNIT | - |  |
| 108 | Tests | KyberWeave.Tests/Arbiter/CommentSyntaxTests.cs:16 | CommentSyntaxTests | 20 | UNIT | - | sut=CommentSyntax |
| 109 | Tests | KyberWeave.Tests/Arbiter/CommentTriageDisabledRuleTests.cs:31 | CommentTriageDisabledRuleTests | 8 | NOT-UNIT | S5 | disk+tempdir+time |
| 110 | Tests | KyberWeave.Tests/Arbiter/CommentTriageServiceTests.cs:28 | CommentTriageServiceTests | 7 | NOT-UNIT | S5 | disk+tempdir+time; sut=CommentTriageService |
| 111 | Tests | KyberWeave.Tests/Arbiter/CommentTriageShadowTests.cs:27 | CommentTriageShadowTests | 11 | NOT-UNIT | S5 | disk+tempdir+time |
| 112 | Tests | KyberWeave.Tests/Arbiter/ConductorGuardFastPathTests.cs:12 | ConductorGuardFastPathTests | 7 | NOT-UNIT | S3 | cli |
| 113 | Tests | KyberWeave.Tests/Arbiter/ConductorGuardTests.cs:17 | ConductorGuardTests | 27 | NOT-UNIT | S3 | disk+tempdir+Process+cli; sut=ConductorGuard |
| 114 | Tests | KyberWeave.Tests/Arbiter/CopilotBackgroundReturnHookProcessTests.cs:24 | CopilotBackgroundReturnHookProcessTests | 6 | NOT-UNIT | S3 | disk+tempdir+Process+cli+time |
| 115 | Tests | KyberWeave.Tests/Arbiter/CopilotBackgroundReturnTests.cs:19 | CopilotBackgroundReturnTests | 12 | SCRATCH-DISK | S2 | disk+tempdir |
| 116 | Tests | KyberWeave.Tests/Arbiter/CopilotHookAdapterTests.cs:24 | CopilotHookAdapterTests | 28 | NOT-UNIT | S1 | disk+corpus; sut=CopilotHookAdapter |
| 117 | Tests | KyberWeave.Tests/Arbiter/CursorHookAdapterTests.cs:24 | CursorHookAdapterTests | 15 | NOT-UNIT | S1 | disk+corpus; sut=CursorHookAdapter |
| 118 | Tests | KyberWeave.Tests/Arbiter/DevinHookAdapterTests.cs:28 | DevinHookAdapterTests | 15 | NOT-UNIT | S1 | disk+corpus; sut=DevinHookAdapter |
| 119 | Tests | KyberWeave.Tests/Arbiter/DocumentSectionIndexTests.cs:19 | DocumentSectionIndexTests | 6 | UNIT | - | sut=DocumentSectionIndex |
| 120 | Tests | KyberWeave.Tests/Arbiter/FactoryHookAdapterTests.cs:26 | FactoryHookAdapterTests | 31 | NOT-UNIT | S1 | disk+tempdir+random+corpus; sut=FactoryHookAdapter |
| 121 | Tests | KyberWeave.Tests/Arbiter/FileFailureGuardTests.cs:19 | FileFailureGuardTests | 7 | NOT-UNIT | S4 | disk+tempdir+env+time |
| 122 | Tests | KyberWeave.Tests/Arbiter/FileTextFactsByteCapTests.cs:16 | FileTextFactsByteCapTests | 2 | SCRATCH-DISK | S2 | disk+tempdir |
| 123 | Tests | KyberWeave.Tests/Arbiter/FileTextFactsTests.cs:22 | FileTextFactsTests | 4 | SCRATCH-DISK | S2 | disk+tempdir; sut=FileTextFacts |
| 124 | Tests | KyberWeave.Tests/Arbiter/HookFastPathTests.cs:7 | HookFastPathTests | 6 | UNIT | - | sut=HookFastPath |
| 125 | Tests | KyberWeave.Tests/Arbiter/HookGitFactsBaseStateTests.cs:25 | HookGitFactsBaseStateTests | 5 | SCRATCH-DISK | S2 | disk+tempdir |
| 126 | Tests | KyberWeave.Tests/Arbiter/HookGitFactsSnapshotTests.cs:29 | HookGitFactsSnapshotTests | 16 | NOT-UNIT | S5 | disk+tempdir+time+random |
| 127 | Tests | KyberWeave.Tests/Arbiter/KiloHookAdapterTests.cs:21 | KiloHookAdapterTests | 7 | SCRATCH-DISK | S2 | disk |
| 128 | Tests | KyberWeave.Tests/Arbiter/ModelOverrideCapabilityTests.cs:22 | ModelOverrideCapabilityTests | 7 | NOT-UNIT | S1 | disk+corpus; sut=ModelOverrideCapability |
| 129 | Tests | KyberWeave.Tests/Arbiter/ModelTargetResolverInputTests.cs:21 | ModelTargetResolverInputTests | 4 | NOT-UNIT | S5 | disk+tempdir+time |
| 130 | Tests | KyberWeave.Tests/Arbiter/ModelTargetResolverTests.cs:29 | ModelTargetResolverTests | 20 | NOT-UNIT | S5 | disk+tempdir+time; sut=ModelTargetResolver |
| 131 | Tests | KyberWeave.Tests/Arbiter/ModelTargetingTieredTests.cs:20 | ModelTargetingTieredTests | 7 | NOT-UNIT | S5 | disk+tempdir+time |
| 132 | Tests | KyberWeave.Tests/Arbiter/ModelTokenAliasesCaseTests.cs:10 | ModelTokenAliasesCaseTests | 1 | UNIT | - |  |
| 133 | Tests | KyberWeave.Tests/Arbiter/ModelTokenAliasesTests.cs:11 | ModelTokenAliasesTests | 3 | UNIT | - | sut=ModelTokenAliases |
| 134 | Tests | KyberWeave.Tests/Arbiter/PiHookAdapterTests.cs:19 | PiHookAdapterTests | 7 | SCRATCH-DISK | S2 | disk |
| 135 | Tests | KyberWeave.Tests/Arbiter/PlanPathResolverTests.cs:22 | PlanPathResolverTests | 12 | NOT-UNIT | S6 | disk+tempdir+random; sut=PlanPathResolver |
| 136 | Tests | KyberWeave.Tests/Arbiter/PlanReadPacketCommandTests.cs:10 | PlanReadPacketCommandTests | 2 | UNIT | - |  |
| 137 | Tests | KyberWeave.Tests/Arbiter/PluginHookAdapterTests.cs:20 | PluginHookAdapterTests | 13 | SCRATCH-DISK | S2 | disk; sut=PluginHookAdapter |
| 138 | Tests | KyberWeave.Tests/Arbiter/SectionSelectorTests.cs:23 | SectionSelectorTests | 5 | UNIT | - | sut=SectionSelector |
| 139 | Tests | KyberWeave.Tests/Arbiter/SubstituteDigestsAllowListTests.cs:10 | SubstituteDigestsAllowListTests | 1 | UNIT | - |  |
| 140 | Tests | KyberWeave.Tests/Arbiter/SubstituteDigestsTests.cs:16 | SubstituteDigestsTests | 5 | UNIT | - | sut=SubstituteDigests |
| 141 | Tests | KyberWeave.Tests/Arbiter/TieredRosterDispatchTests.cs:24 | TieredRosterDispatchTests | 4 | SCRATCH-DISK | S2 | disk+tempdir |
| 142 | Tests | KyberWeave.Tests/ArbiterAntigravityRenderingTests.cs:20 | ArbiterAntigravityRenderingTests | 7 | UNIT | - |  |
| 143 | Tests | KyberWeave.Tests/ArbiterAuditModelEvaluationTests.cs:18 | ArbiterAuditModelEvaluationTests | 6 | NOT-UNIT | S5 | disk+tempdir+time |
| 144 | Tests | KyberWeave.Tests/ArbiterAuditPlanArgumentTests.cs:9 | ArbiterAuditPlanArgumentTests | 1 | NOT-UNIT | S5 | disk+tempdir+time |
| 145 | Tests | KyberWeave.Tests/ArbiterCanonicalSquadTests.cs:21 | ArbiterCanonicalSquadTests | 5 | NOT-UNIT | S1 | corpus |
| 146 | Tests | KyberWeave.Tests/ArbiterClaudeHandbackRenderingTests.cs:18 | ArbiterClaudeHandbackRenderingTests | 4 | UNIT | - |  |
| 147 | Tests | KyberWeave.Tests/ArbiterClaudeRenderingTests.cs:18 | ArbiterClaudeRenderingTests | 5 | NOT-UNIT | S1 | corpus |
| 148 | Tests | KyberWeave.Tests/ArbiterCliCommandTests.cs:20 | ArbiterCliCommandTests | 26 | NOT-UNIT | S1 | disk+tempdir+time+corpus |
| 149 | Tests | KyberWeave.Tests/ArbiterCodexRenderingTests.cs:18 | ArbiterCodexRenderingTests | 4 | UNIT | - |  |
| 150 | Tests | KyberWeave.Tests/ArbiterCommandAllowListTests.cs:24 | ArbiterCommandAllowListTests | 18 | NOT-UNIT | S1 | disk+tempdir+cli+corpus |
| 151 | Tests | KyberWeave.Tests/ArbiterCommentBodyCliTests.cs:17 | ArbiterCommentBodyCliTests | 1 | SCRATCH-DISK | S2 | disk+tempdir |
| 152 | Tests | KyberWeave.Tests/ArbiterCommentBodyLimitCliTests.cs:16 | ArbiterCommentBodyLimitCliTests | 1 | SCRATCH-DISK | S2 | disk+tempdir |
| 153 | Tests | KyberWeave.Tests/ArbiterCommentRootContainmentCliTests.cs:17 | ArbiterCommentRootContainmentCliTests | 1 | SCRATCH-DISK | S2 | disk+tempdir |
| 154 | Tests | KyberWeave.Tests/ArbiterCommentShadowCliTests.cs:25 | ArbiterCommentShadowCliTests | 5 | SCRATCH-DISK | S2 | disk+tempdir |
| 155 | Tests | KyberWeave.Tests/ArbiterCommentUsageCliTests.cs:16 | ArbiterCommentUsageCliTests | 2 | SCRATCH-DISK | S2 | tempdir |
| 156 | Tests | KyberWeave.Tests/ArbiterConfigLoaderTests.cs:11 | ArbiterConfigLoaderTests | 2 | UNIT | - | sut=ArbiterConfigLoader |
| 157 | Tests | KyberWeave.Tests/ArbiterCopilotRenderingTests.cs:19 | ArbiterCopilotRenderingTests | 11 | NOT-UNIT | S3 | cli |
| 158 | Tests | KyberWeave.Tests/ArbiterCursorRenderingTests.cs:18 | ArbiterCursorRenderingTests | 7 | UNIT | - |  |
| 159 | Tests | KyberWeave.Tests/ArbiterDevinRenderingTests.cs:20 | ArbiterDevinRenderingTests | 7 | UNIT | - |  |
| 160 | Tests | KyberWeave.Tests/ArbiterDoctorCommandTests.cs:18 | ArbiterDoctorCommandTests | 7 | NOT-UNIT | S5 | disk+tempdir+time; sut=ArbiterDoctorCommand |
| 161 | Tests | KyberWeave.Tests/ArbiterDoctorModelTargetsStatTests.cs:15 | ArbiterDoctorModelTargetsStatTests | 3 | NOT-UNIT | S4 | disk+tempdir+env |
| 162 | Tests | KyberWeave.Tests/ArbiterFactoryRenderingTests.cs:22 | ArbiterFactoryRenderingTests | 30 | NOT-UNIT | S5 | disk+tempdir+time |
| 163 | Tests | KyberWeave.Tests/ArbiterGlobalClaudeCopilotRenderingTests.cs:15 | ArbiterGlobalClaudeCopilotRenderingTests | 2 | NOT-UNIT | S3 | disk+cli |
| 164 | Tests | KyberWeave.Tests/ArbiterGlobalHandbackTests.cs:15 | ArbiterGlobalHandbackTests | 3 | NOT-UNIT | S1 | disk+corpus |
| 165 | Tests | KyberWeave.Tests/ArbiterGlobalHookCoverageTests.cs:15 | ArbiterGlobalHookCoverageTests | 6 | NOT-UNIT | S1 | disk+corpus |
| 166 | Tests | KyberWeave.Tests/ArbiterGlobalHookFileRenderingTests.cs:20 | ArbiterGlobalHookFileRenderingTests | 4 | NOT-UNIT | S5 | disk+tempdir+time |
| 167 | Tests | KyberWeave.Tests/ArbiterGlobalHookRuntimeTests.cs:16 | ArbiterGlobalHookRuntimeTests | 16 | NOT-UNIT | S1 | disk+tempdir+time+random+corpus |
| 168 | Tests | KyberWeave.Tests/ArbiterGlobalHookRuntimeTests.cs:452 | ArbiterHookCommandTests | 1 | NOT-UNIT | S1 | disk+tempdir+random+corpus |
| 169 | Tests | KyberWeave.Tests/ArbiterGlobalPluginDirRenderingTests.cs:17 | ArbiterGlobalPluginDirRenderingTests | 3 | SCRATCH-DISK | S2 | disk+tempdir |
| 170 | Tests | KyberWeave.Tests/ArbiterGlobalWarpFallbackTests.cs:16 | ArbiterGlobalWarpFallbackTests | 2 | SCRATCH-DISK | S2 | disk+tempdir |
| 171 | Tests | KyberWeave.Tests/ArbiterGlobalWiringTests.cs:16 | ArbiterGlobalWiringTests | 4 | UNIT | - |  |
| 172 | Tests | KyberWeave.Tests/ArbiterHarnessGatingTests.cs:25 | ArbiterHarnessGatingTests | 11 | NOT-UNIT | S5 | disk+tempdir+time; sut=ArbiterHarnessGating |
| 173 | Tests | KyberWeave.Tests/ArbiterHookRefresherRenameNoticeTests.cs:18 | ArbiterHookRefresherRenameNoticeTests | 2 | SCRATCH-DISK | S2 | disk+tempdir |
| 174 | Tests | KyberWeave.Tests/ArbiterHookWiringTests.cs:20 | ArbiterHookWiringTests | 28 | NOT-UNIT | S1 | corpus; sut=ArbiterHookWiring |
| 175 | Tests | KyberWeave.Tests/ArbiterInstallCommandTests.cs:31 | ArbiterInstallCommandTests | 20 | SCRATCH-DISK | S2 | disk+tempdir; sut=ArbiterInstallCommand |
| 176 | Tests | KyberWeave.Tests/ArbiterKiloRenderingTests.cs:18 | ArbiterKiloRenderingTests | 13 | UNIT | - |  |
| 177 | Tests | KyberWeave.Tests/ArbiterKiloReviewRemediationTests.cs:28 | ArbiterKiloReviewRemediationTests | 15 | NOT-UNIT | S1 | disk+tempdir+time+corpus |
| 178 | Tests | KyberWeave.Tests/ArbiterLogFilesReadTests.cs:12 | ArbiterLogFilesReadTests | 11 | SCRATCH-DISK | S2 | disk+tempdir |
| 179 | Tests | KyberWeave.Tests/ArbiterLogWindowReportTests.cs:17 | ArbiterLogWindowReportTests | 12 | SCRATCH-DISK | S2 | disk+tempdir |
| 180 | Tests | KyberWeave.Tests/ArbiterMcpGrantRenderingTests.cs:22 | ArbiterMcpGrantRenderingTests | 9 | SCRATCH-DISK | S2 | disk |
| 181 | Tests | KyberWeave.Tests/ArbiterModelTargetsFileTests.cs:21 | ArbiterModelTargetsFileTests | 7 | NOT-UNIT | S1 | disk+tempdir+corpus; sut=ArbiterModelTargetsFile |
| 182 | Tests | KyberWeave.Tests/ArbiterModelTargetsGlobalInstallTests.cs:21 | ArbiterModelTargetsGlobalInstallTests | 4 | NOT-UNIT | S1 | disk+tempdir+time+corpus |
| 183 | Tests | KyberWeave.Tests/ArbiterOpenCodeRenderingTests.cs:19 | ArbiterOpenCodeRenderingTests | 13 | UNIT | - |  |
| 184 | Tests | KyberWeave.Tests/ArbiterPiRenderingTests.cs:18 | ArbiterPiRenderingTests | 9 | UNIT | - |  |
| 185 | Tests | KyberWeave.Tests/ArbiterPlanArgumentTests.cs:8 | ArbiterPlanArgumentTests | 1 | SCRATCH-DISK | S2 | disk+tempdir |
| 186 | Tests | KyberWeave.Tests/ArbiterProjectShimByteIdentityTests.cs:15 | ArbiterProjectShimByteIdentityTests | 3 | UNIT | - |  |
| 187 | Tests | KyberWeave.Tests/ArbiterRuleFolderLoadingTests.cs:15 | ArbiterRuleFolderLoadingTests | 30 | SCRATCH-DISK | S2 | disk+tempdir |
| 188 | Tests | KyberWeave.Tests/ArbiterRuleFolderSplitTests.cs:20 | ArbiterRuleFolderSplitTests | 4 | NOT-UNIT | S1 | disk+corpus |
| 189 | Tests | KyberWeave.Tests/ArbiterSectionsUsageCliTests.cs:16 | ArbiterSectionsUsageCliTests | 1 | UNIT | - |  |
| 190 | Tests | KyberWeave.Tests/ArbiterServeStartupFailureTests.cs:19 | ArbiterServeStartupFailureTests | 2 | NOT-UNIT | S3 | tempdir+Process+cli |
| 191 | Tests | KyberWeave.Tests/ArbiterServeStdioTransportTests.cs:20 | ArbiterServeStdioTransportTests | 1 | NOT-UNIT | S3 | disk+tempdir+Process+cli |
| 192 | Tests | KyberWeave.Tests/ArbiterSetupCommandTests.cs:22 | ArbiterSetupCommandTests | 21 | NOT-UNIT | S3 | disk+tempdir+Process; sut=ArbiterSetupCommand |
| 193 | Tests | KyberWeave.Tests/ArbiterShimBehaviourTests.cs:19 | ArbiterShimBehaviourTests | 11 | NOT-UNIT | S3 | disk+tempdir+Process+env |
| 194 | Tests | KyberWeave.Tests/ArbiterStatusCommandTests.cs:16 | ArbiterStatusCommandTests | 1 | SCRATCH-DISK | S2 | disk+tempdir; sut=ArbiterStatusCommand |
| 195 | Tests | KyberWeave.Tests/ArbiterSummaryCommentTriageCliTests.cs:24 | ArbiterSummaryCommentTriageCliTests | 4 | SCRATCH-DISK | S2 | disk+tempdir |
| 196 | Tests | KyberWeave.Tests/ArbiterTestsOnlyTaskTests.cs:14 | ArbiterTestsOnlyTaskTests | 6 | UNIT | - |  |
| 197 | Tests | KyberWeave.Tests/ArbiterTriageCliCommandTests.cs:24 | ArbiterTriageCliCommandTests | 5 | NOT-UNIT | S1 | disk+tempdir+random+corpus |
| 198 | Tests | KyberWeave.Tests/ArbiterTuneCommandTests.cs:35 | ArbiterTuneCommandTests | 9 | SCRATCH-DISK | S2 | disk+tempdir; sut=ArbiterTuneCommand |
| 199 | Tests | KyberWeave.Tests/ArbiterTuneUnreadableConfidenceTests.cs:17 | ArbiterTuneUnreadableConfidenceTests | 2 | SCRATCH-DISK | S2 | disk+tempdir |
| 200 | Tests | KyberWeave.Tests/ArbiterUninstallCommandTests.cs:25 | ArbiterUninstallCommandTests | 17 | NOT-UNIT | S5 | disk+tempdir+time; sut=ArbiterUninstallCommand |
| 201 | Tests | KyberWeave.Tests/ArchiveCloseoutScopeTests.cs:118 | ArchiveCloseoutScopeTests | 16 | NOT-UNIT | S6 | disk+tempdir+random |
| 202 | Tests | KyberWeave.Tests/ArchiveCloseoutValidatorTests.cs:171 | ArchiveCloseoutValidatorTests | 31 | UNIT | - | sut=ArchiveCloseoutValidator |
| 203 | Tests | KyberWeave.Tests/BoundedFileReaderLimitTests.cs:8 | BoundedFileReaderLimitTests | 2 | SCRATCH-DISK | S2 | disk+tempdir |
| 204 | Tests | KyberWeave.Tests/CapabilityDegradationsTests.cs:23 | CapabilityDegradationsTests | 8 | NOT-UNIT | S3 | Process+cli; sut=CapabilityDegradations |
| 205 | Tests | KyberWeave.Tests/CitedLineCheckTests.cs:11 | CitedLineCheckTests | 10 | SCRATCH-DISK | S2 | disk+tempdir; sut=CitedLineCheck |
| 206 | Tests | KyberWeave.Tests/ClaimExtractionTests.cs:12 | ClaimExtractionTests | 4 | NOT-UNIT | S8 | Console |
| 207 | Tests | KyberWeave.Tests/ClaudeHookContainerCasingTests.cs:14 | ClaudeHookContainerCasingTests | 9 | UNIT | - |  |
| 208 | Tests | KyberWeave.Tests/ClaudeRendererContractTests.cs:38 | ClaudeRendererContractTests | 18 | NOT-UNIT | S1 | tempdir+corpus |
| 209 | Tests | KyberWeave.Tests/CliAssemblyVersionTests.cs:12 | CliAssemblyVersionTests | 2 | UNIT | - | sut=CliAssemblyVersion |
| 210 | Tests | KyberWeave.Tests/CliHelpRenderingTests.cs:13 | CliHelpRenderingTests | 4 | NOT-UNIT | S3 | disk+Process+env |
| 211 | Tests | KyberWeave.Tests/CodeGraphNeighborhoodPortTests.cs:12 | CodeGraphNeighborhoodPortTests | 3 | UNIT | - |  |
| 212 | Tests | KyberWeave.Tests/CodeGraphPortTests.cs:18 | CodeGraphPortTests | 6 | SCRATCH-DISK | S2 | disk+tempdir |
| 213 | Tests | KyberWeave.Tests/CodexRendererContractTests.cs:26 | CodexRendererContractTests | 4 | NOT-UNIT | S1 | disk+tempdir+corpus |
| 214 | Tests | KyberWeave.Tests/CommandCodeRendererContractTests.cs:16 | CommandCodeRendererContractTests | 10 | NOT-UNIT | S1 | tempdir+cli+corpus |
| 215 | Tests | KyberWeave.Tests/ConfigRegTests.cs:16 | ConfigRegResolutionTests | 7 | NOT-UNIT | S3 | cli |
| 216 | Tests | KyberWeave.Tests/ConfigRegTests.cs:141 | ConfigRegLoadingTests | 14 | UNIT | - |  |
| 217 | Tests | KyberWeave.Tests/ConfigRegTests.cs:330 | ConfigRegValidatorTests | 18 | NOT-UNIT | S3 | disk+tempdir+cli |
| 218 | Tests | KyberWeave.Tests/CopilotLargeOutputDoctorTests.cs:12 | CopilotLargeOutputDoctorTests | 6 | SCRATCH-DISK | S2 | disk+tempdir |
| 219 | Tests | KyberWeave.Tests/CopilotPrompt/CopilotPromptFileTests.cs:14 | CopilotPromptFileTests | 8 | SCRATCH-DISK | S2 | tempdir; sut=CopilotPromptFile |
| 220 | Tests | KyberWeave.Tests/CopilotPrompt/CopilotPromptProbeTests.cs:17 | CopilotPromptProbeTests | 13 | SCRATCH-DISK | S2 | disk+tempdir; sut=CopilotPromptProbe |
| 221 | Tests | KyberWeave.Tests/CopilotPrompt/CopilotPromptRefreshTests.cs:11 | CopilotPromptRefreshTests | 3 | SCRATCH-DISK | S2 | disk+tempdir |
| 222 | Tests | KyberWeave.Tests/CopilotPrompt/CopilotPromptSettingsMergeTests.cs:19 | CopilotPromptSettingsMergeTests | 27 | NOT-UNIT | S4 | disk+tempdir+env |
| 223 | Tests | KyberWeave.Tests/CorpusProvenanceTests.cs:32 | CorpusProvenanceTests | 6 | SCRATCH-DISK | S2 | disk+tempdir; sut=CorpusProvenance |
| 224 | Tests | KyberWeave.Tests/CouncilGateSelectionTests.cs:13 | CouncilGateSelectionTests | 8 | NOT-UNIT | S3 | cli; sut=CouncilGateSelection |
| 225 | Tests | KyberWeave.Tests/CouncilSeatingTests.cs:10 | CouncilSeatingTests | 12 | SCRATCH-DISK | S2 | disk; sut=CouncilSeating |
| 226 | Tests | KyberWeave.Tests/CoverageCollectorTests.cs:10 | CoverageCollectorTests | 3 | NOT-UNIT | S5 | disk+tempdir+time |
| 227 | Tests | KyberWeave.Tests/CursorRendererContractTests.cs:24 | CursorRendererContractTests | 5 | NOT-UNIT | S1 | corpus |
| 228 | Tests | KyberWeave.Tests/DescriptionScorerTests.cs:6 | DescriptionScorerTests | 7 | UNIT | - | sut=DescriptionScorer |
| 229 | Tests | KyberWeave.Tests/DevHardModelUpgradeTests.cs:18 | DevHardModelUpgradeTests | 4 | NOT-UNIT | S1 | corpus |
| 230 | Tests | KyberWeave.Tests/DeveloperAgentContentTests.cs:13 | DeveloperAgentContentTests | 8 | NOT-UNIT | S1 | corpus |
| 231 | Tests | KyberWeave.Tests/DeveloperTierSelectionTests.cs:21 | DeveloperTierSelectionTests | 14 | SCRATCH-DISK | S2 | disk |
| 232 | Tests | KyberWeave.Tests/DevinImportOverlapTests.cs:16 | DevinImportOverlapTests | 10 | NOT-UNIT | S4 | disk+tempdir+env; sut=DevinImportOverlap |
| 233 | Tests | KyberWeave.Tests/DevinRendererContractTests.cs:38 | DevinRendererContractTests | 13 | NOT-UNIT | S1 | corpus |
| 234 | Tests | KyberWeave.Tests/DiagnosticLocationRenderingTests.cs:7 | DiagnosticLocationRenderingTests | 10 | NOT-UNIT | S4 | env |
| 235 | Tests | KyberWeave.Tests/DispatchPromptInsertionTests.cs:32 | DispatchPromptInsertionTests | 19 | NOT-UNIT | S5 | disk+tempdir+time; sut=DispatchPromptInsertion |
| 236 | Tests | KyberWeave.Tests/DocDriftStaleIndexTests.cs:20 | DocDriftStaleIndexTests | 6 | NOT-UNIT | S3 | disk+tempdir+Process+cli+time |
| 237 | Tests | KyberWeave.Tests/DocGovernanceTests.cs:90 | SkillAndScopeTests | 7 | NOT-UNIT | S3 | cli |
| 238 | Tests | KyberWeave.Tests/DocGovernanceTests.cs:238 | TechnologyDeclarationTests | 6 | NOT-UNIT | S3 | cli |
| 239 | Tests | KyberWeave.Tests/DocGovernanceTests.cs:332 | DocSpecValidatorTests | 21 | NOT-UNIT | S3 | Process |
| 240 | Tests | KyberWeave.Tests/DocGovernanceTests.cs:774 | DocLinkAndExportTests | 3 | UNIT | - |  |
| 241 | Tests | KyberWeave.Tests/DocGovernanceTests.cs:802 | DocDriftLinterTests | 1 | UNIT | - |  |
| 242 | Tests | KyberWeave.Tests/DocGovernanceTests.cs:830 | DiagnosticSubjectTests | 1 | UNIT | - |  |
| 243 | Tests | KyberWeave.Tests/DocGovernanceTests.cs:849 | DocGovernanceTests | 1 | UNIT | - |  |
| 244 | Tests | KyberWeave.Tests/DocGraphProjectionTests.cs:17 | DocGraphProjectionTests | 11 | NOT-UNIT | S3 | disk+tempdir+cli; sut=DocGraphProjection |
| 245 | Tests | KyberWeave.Tests/DocLinkFragmentValidationTests.cs:16 | DocLinkFragmentValidationTests | 1 | NOT-UNIT | S1 | corpus |
| 246 | Tests | KyberWeave.Tests/DocsAnalysisCliCommandTests.cs:19 | DocsAnalysisCliCommandTests | 13 | NOT-UNIT | S1 | disk+tempdir+corpus |
| 247 | Tests | KyberWeave.Tests/DocsAnalysisCompositionTests.cs:27 | DocsAnalysisCompositionTests | 7 | SCRATCH-DISK | S2 | disk+tempdir |
| 248 | Tests | KyberWeave.Tests/DocsAnalysisConfigTests.cs:12 | DocsAnalysisConfigTests | 11 | UNIT | - | sut=DocsAnalysisConfig |
| 249 | Tests | KyberWeave.Tests/DocsCloseoutCommandTests.cs:22 | DocsCloseoutCommandTests | 35 | NOT-UNIT | S3 | disk+Process+cli+env; sut=DocsCloseoutCommand |
| 250 | Tests | KyberWeave.Tests/DocsEnabledTests.cs:12 | DocsEnabledTests | 7 | SCRATCH-DISK | S2 | disk+tempdir; sut=DocsEnabled |
| 251 | Tests | KyberWeave.Tests/DocsGraphCliCommandTests.cs:9 | DocsGraphCliCommandTests | 2 | NOT-UNIT | S3 | disk+tempdir+cli |
| 252 | Tests | KyberWeave.Tests/DocsInitCommandTests.cs:13 | DocsInitCommandTests | 1 | SCRATCH-DISK | S2 | disk+tempdir; sut=DocsInitCommand |
| 253 | Tests | KyberWeave.Tests/DocsInitKyberDocsSkillTests.cs:11 | DocsInitKyberDocsSkillTests | 2 | SCRATCH-DISK | S2 | disk+tempdir |
| 254 | Tests | KyberWeave.Tests/DocsInitMcpCheckTests.cs:37 | DocsInitMcpCheckTests | 19 | NOT-UNIT | S3 | disk+tempdir+Process+cli+env+random |
| 255 | Tests | KyberWeave.Tests/DocsInitProvisionTests.cs:20 | DocsInitProvisionTests | 2 | NOT-UNIT | S3 | disk+tempdir+Process+cli |
| 256 | Tests | KyberWeave.Tests/DocsInitReviewGatesTests.cs:20 | DocsInitReviewGatesTests | 13 | NOT-UNIT | S1 | disk+tempdir+Process+cli+random+corpus |
| 257 | Tests | KyberWeave.Tests/DocsInitTemplateChoiceTests.cs:14 | DocsInitTemplateChoiceTests | 7 | SCRATCH-DISK | S2 | disk+tempdir |
| 258 | Tests | KyberWeave.Tests/DocsMcpMigrateCommandTests.cs:34 | DocsMcpMigrateCommandTests | 41 | NOT-UNIT | S3 | disk+tempdir+cli+env; sut=DocsMcpMigrateCommand |
| 259 | Tests | KyberWeave.Tests/DocsMcpWireCommandTests.cs:40 | DocsMcpWireCommandTests | 30 | NOT-UNIT | S1 | disk+tempdir+cli+corpus; sut=DocsMcpWireCommand |
| 260 | Tests | KyberWeave.Tests/DocsPacketCommandTests.cs:13 | DocsPacketCommandTests | 12 | SCRATCH-DISK | S2 | disk; sut=DocsPacketCommand |
| 261 | Tests | KyberWeave.Tests/DocsRootPathSymlinkContainmentTests.cs:11 | DocsRootPathSymlinkContainmentTests | 5 | SCRATCH-DISK | S2 | disk+tempdir |
| 262 | Tests | KyberWeave.Tests/DocsScaffolderTests.cs:21 | DocsScaffolderTests | 51 | NOT-UNIT | S1 | disk+tempdir+time+corpus; sut=DocsScaffolder |
| 263 | Tests | KyberWeave.Tests/DocsStandardsDiffCommandTests.cs:12 | DocsStandardsDiffCommandTests | 5 | SCRATCH-DISK | S2 | disk+tempdir; sut=DocsStandardsDiffCommand |
| 264 | Tests | KyberWeave.Tests/DocsStandardsMigrateCommandTests.cs:13 | DocsStandardsMigrateCommandTests | 10 | SCRATCH-DISK | S2 | disk+tempdir; sut=DocsStandardsMigrateCommand |
| 265 | Tests | KyberWeave.Tests/DocsTaskPromptToolTests.cs:20 | DocsTaskPromptToolTests | 4 | SCRATCH-DISK | S2 | disk |
| 266 | Tests | KyberWeave.Tests/DocsTasksCommandTests.cs:14 | DocsTasksCommandTests | 11 | SCRATCH-DISK | S2 | disk; sut=DocsTasksCommand |
| 267 | Tests | KyberWeave.Tests/DoctorCommandTests.cs:17 | DoctorCommandTests | 6 | NOT-UNIT | S3 | disk+tempdir+cli+env; sut=DoctorCommand |
| 268 | Tests | KyberWeave.Tests/DoctorEnsureTests.cs:35 | DoctorEnsureTests | 17 | NOT-UNIT | S3 | disk+tempdir+Process+cli |
| 269 | Tests | KyberWeave.Tests/DoctorPostCheckoutTests.cs:19 | DoctorPostCheckoutTests | 6 | NOT-UNIT | S3 | disk+tempdir+cli |
| 270 | Tests | KyberWeave.Tests/DoctorReportRendererTests.cs:18 | DoctorReportRendererTests | 3 | UNIT | - | sut=DoctorReportRenderer |
| 271 | Tests | KyberWeave.Tests/DocumentCorpusTests.cs:15 | DocumentCorpusTests | 8 | UNIT | - | sut=DocumentCorpus |
| 272 | Tests | KyberWeave.Tests/DocumentCorpusTests.cs:174 | DocumentIndexHostTests | 8 | NOT-UNIT | S5 | disk+time; sut=DocumentCorpus |
| 273 | Tests | KyberWeave.Tests/DocumentIndexHostSymlinkContainmentTests.cs:15 | DocumentIndexHostSymlinkContainmentTests | 4 | SCRATCH-DISK | S2 | disk+tempdir |
| 274 | Tests | KyberWeave.Tests/DocumentIndexTests.cs:18 | DocumentIndexRankingTests | 7 | UNIT | - | sut=DocumentIndex |
| 275 | Tests | KyberWeave.Tests/DocumentIndexTests.cs:110 | DocumentKeywordScoringTests | 3 | UNIT | - | sut=DocumentIndex |
| 276 | Tests | KyberWeave.Tests/DocumentIndexTests.cs:189 | DocumentAuthorityTests | 2 | UNIT | - | sut=DocumentIndex |
| 277 | Tests | KyberWeave.Tests/DocumentIndexTests.cs:232 | TitleScoringTests | 3 | UNIT | - | sut=DocumentIndex |
| 278 | Tests | KyberWeave.Tests/DocumentIndexTests.cs:295 | DocumentSectionTests | 3 | UNIT | - | sut=DocumentIndex |
| 279 | Tests | KyberWeave.Tests/DocumentIndexTests.cs:355 | CodeJoinScopingTests | 5 | UNIT | - | sut=DocumentIndex |
| 280 | Tests | KyberWeave.Tests/DocumentIndexTests.cs:459 | CodingStandardsLookupTests | 22 | UNIT | - | sut=DocumentIndex |
| 281 | Tests | KyberWeave.Tests/DocumentLoaderSymlinkContainmentTests.cs:14 | DocumentLoaderSymlinkContainmentTests | 7 | SCRATCH-DISK | S2 | disk+tempdir |
| 282 | Tests | KyberWeave.Tests/DocumentSectionTableTests.cs:5 | DocumentSectionTableTests | 8 | UNIT | - | sut=DocumentSectionTable |
| 283 | Tests | KyberWeave.Tests/DocumentationAnalysisScaleTests.cs:22 | DocumentationAnalysisScaleTests | 2 | NOT-UNIT | S3 | Process+time |
| 284 | Tests | KyberWeave.Tests/DocumentationAnalyzerTests.cs:20 | DocumentationAnalyzerTests | 42 | NOT-UNIT | S3 | cli; sut=DocumentationAnalyzer |
| 285 | Tests | KyberWeave.Tests/DocumentationReviewExchangeTests.cs:18 | DocumentationReviewExchangeTests | 19 | SCRATCH-DISK | S2 | tempdir; sut=DocumentationReviewExchange |
| 286 | Tests | KyberWeave.Tests/EmbeddingClientTests.cs:19 | EmbeddingClientTests | 16 | UNIT | - |  |
| 287 | Tests | KyberWeave.Tests/ExploreAnswerTests.cs:22 | ExploreAnswerTests | 19 | UNIT | - | sut=ExploreAnswer |
| 288 | Tests | KyberWeave.Tests/FactoryMcpConfigurationTests.cs:17 | FactoryMcpConfigurationTests | 15 | NOT-UNIT | S1 | disk+tempdir+cli+corpus; sut=FactoryMcpConfiguration |
| 289 | Tests | KyberWeave.Tests/FactoryRendererContractTests.cs:29 | FactoryRendererContractTests | 17 | NOT-UNIT | S1 | cli+corpus |
| 290 | Tests | KyberWeave.Tests/FailOnPolicyTests.cs:6 | FailOnPolicyTests | 4 | UNIT | - | sut=FailOnPolicy |
| 291 | Tests | KyberWeave.Tests/FileReferenceExtractorTests.cs:7 | FileReferenceExtractorTests | 26 | SCRATCH-DISK | S2 | disk+tempdir; sut=FileReferenceExtractor |
| 292 | Tests | KyberWeave.Tests/FindingLinesRenderingTests.cs:13 | FindingLinesRenderingTests | 4 | SCRATCH-DISK | S2 | tempdir |
| 293 | Tests | KyberWeave.Tests/GateCatalogueRenderTests.cs:20 | GateCatalogueRenderTests | 18 | NOT-UNIT | S1 | disk+tempdir+corpus |
| 294 | Tests | KyberWeave.Tests/GateCatalogueResourceTests.cs:18 | GateCatalogueResourceTests | 6 | NOT-UNIT | S3 | cli |
| 295 | Tests | KyberWeave.Tests/GateCatalogueToolchainTests.cs:47 | GateCatalogueToolchainTests | 3 | NOT-UNIT | S1 | disk+tempdir+Process+cli+env+random+corpus |
| 296 | Tests | KyberWeave.Tests/GateRunnerConcurrentDeadlineTests.cs:18 | GateRunnerConcurrentDeadlineTests | 4 | NOT-UNIT | S3 | disk+tempdir+Process+cli+env+time |
| 297 | Tests | KyberWeave.Tests/GateRunnerConcurrentGateTests.cs:18 | GateRunnerConcurrentGateTests | 7 | NOT-UNIT | S3 | disk+tempdir+Process+cli+env+time |
| 298 | Tests | KyberWeave.Tests/GateRunnerDeadlineTests.cs:19 | GateRunnerDeadlineTests | 12 | NOT-UNIT | S3 | disk+tempdir+cli+env+time |
| 299 | Tests | KyberWeave.Tests/GateRunnerOutcomeTests.cs:16 | GateRunnerOutcomeTests | 10 | NOT-UNIT | S3 | disk+tempdir+Process+cli+env |
| 300 | Tests | KyberWeave.Tests/GhJsonTests.cs:8 | GhJsonTests | 4 | UNIT | - | sut=GhJson |
| 301 | Tests | KyberWeave.Tests/GitHubSquadReleaseSourceAuthTests.cs:16 | GitHubSquadReleaseSourceAuthTests | 9 | SCRATCH-DISK | S2 | tempdir |
| 302 | Tests | KyberWeave.Tests/GitHubSquadReleaseSourceSizeLimitTests.cs:13 | GitHubSquadReleaseSourceSizeLimitTests | 3 | NOT-UNIT | S6 | disk+tempdir+random |
| 303 | Tests | KyberWeave.Tests/GlossaryGraphExportTests.cs:19 | GlossaryGraphExportTests | 4 | NOT-UNIT | S3 | disk+tempdir+cli+time |
| 304 | Tests | KyberWeave.Tests/HarnessProfileConfigTests.cs:15 | HarnessProfileConfigTests | 6 | NOT-UNIT | S6 | disk+tempdir+random; sut=HarnessProfileConfig |
| 305 | Tests | KyberWeave.Tests/HostConfigYamlTests.cs:11 | HostConfigYamlTests | 34 | UNIT | - | sut=HostConfigYaml |
| 306 | Tests | KyberWeave.Tests/IgnoreMarkupTests.cs:12 | IgnoreMarkupTests | 7 | UNIT | - |  |
| 307 | Tests | KyberWeave.Tests/IndexSectionCatalogTests.cs:5 | IndexSectionCatalogTests | 2 | UNIT | - | sut=IndexSectionCatalog |
| 308 | Tests | KyberWeave.Tests/IndexSectionExploreTests.cs:10 | IndexSectionExploreTests | 2 | UNIT | - |  |
| 309 | Tests | KyberWeave.Tests/JsonServerSpliceTests.cs:28 | JsonServerSpliceTests | 10 | UNIT | - | sut=JsonServerSplice |
| 310 | Tests | KyberWeave.Tests/KiloRendererContractTests.cs:25 | KiloRendererContractTests | 8 | NOT-UNIT | S1 | disk+tempdir+corpus |
| 311 | Tests | KyberWeave.Tests/KyberDocsSkillSetTests.cs:14 | KyberDocsSkillSetTests | 6 | NOT-UNIT | S1 | disk+corpus |
| 312 | Tests | KyberWeave.Tests/KyberStandardsTemplateShapeTests.cs:12 | KyberStandardsTemplateShapeTests | 9 | UNIT | - |  |
| 313 | Tests | KyberWeave.Tests/KyberStandardsTemplatesTests.cs:13 | KyberStandardsTemplatesTests | 13 | UNIT | - | sut=KyberStandardsTemplates |
| 314 | Tests | KyberWeave.Tests/LeafSplitTests.cs:18 | LeafSplitTests | 7 | UNIT | - |  |
| 315 | Tests | KyberWeave.Tests/LegacyHostMigrationTests.cs:22 | LegacyHostMigrationTests | 6 | SCRATCH-DISK | S2 | disk+tempdir |
| 316 | Tests | KyberWeave.Tests/LinkFragmentTests.cs:17 | LinkFragmentTests | 4 | UNIT | - |  |
| 317 | Tests | KyberWeave.Tests/ManagedGlossaryTests.cs:18 | ManagedGlossaryTests | 22 | NOT-UNIT | S5 | disk+tempdir+time; sut=ManagedGlossary |
| 318 | Tests | KyberWeave.Tests/McpAgentCodingStandardsTests.cs:34 | McpAgentCodingStandardsTests | 25 | NOT-UNIT | S1 | disk+corpus |
| 319 | Tests | KyberWeave.Tests/McpAnalysisToolsTests.cs:21 | McpAnalysisToolsTests | 11 | SCRATCH-DISK | S2 | disk+tempdir |
| 320 | Tests | KyberWeave.Tests/McpCodeGraphRefreshTests.cs:54 | McpCodeGraphRefreshTests | 15 | NOT-UNIT | S3 | disk+tempdir+cli+time |
| 321 | Tests | KyberWeave.Tests/McpCompositionRootTests.cs:35 | McpCompositionRootTests | 3 | NOT-UNIT | S3 | disk+tempdir+Process+cli |
| 322 | Tests | KyberWeave.Tests/McpConfigTests.cs:24 | McpConfigTests | 12 | NOT-UNIT | S3 | disk+tempdir+cli; sut=McpConfig |
| 323 | Tests | KyberWeave.Tests/McpDeclarationBlockWriterTests.cs:20 | McpDeclarationBlockWriterTests | 9 | NOT-UNIT | S3 | disk+tempdir+cli; sut=McpDeclarationBlockWriter |
| 324 | Tests | KyberWeave.Tests/McpDeclarationSurfaceTests.cs:19 | McpDeclarationSurfaceTests | 4 | NOT-UNIT | S3 | disk+tempdir+cli |
| 325 | Tests | KyberWeave.Tests/McpDocsGateToolsTests.cs:27 | McpDocsGateToolsTests | 16 | NOT-UNIT | S1 | disk+tempdir+random+corpus |
| 326 | Tests | KyberWeave.Tests/McpDocsProvenanceTests.cs:29 | McpDocsProvenanceTests | 7 | NOT-UNIT | S1 | disk+tempdir+corpus |
| 327 | Tests | KyberWeave.Tests/McpDocsToolsRefreshOptInTests.cs:36 | McpDocsToolsRefreshOptInTests | 1 | SCRATCH-DISK | S2 | tempdir |
| 328 | Tests | KyberWeave.Tests/McpLiveProbeTests.cs:18 | McpLiveProbeTests | 24 | NOT-UNIT | S3 | disk+tempdir+Process+cli |
| 329 | Tests | KyberWeave.Tests/McpPackagingTests.cs:16 | McpPackagingTests | 12 | NOT-UNIT | S1 | disk+corpus |
| 330 | Tests | KyberWeave.Tests/McpPlanLifecycleToolsTests.cs:19 | McpPlanLifecycleToolsTests | 11 | NOT-UNIT | S3 | disk+Process+cli |
| 331 | Tests | KyberWeave.Tests/McpProjectPathTests.cs:18 | McpProjectPathTests | 5 | NOT-UNIT | S3 | disk+tempdir+Process+cli |
| 332 | Tests | KyberWeave.Tests/McpRepositoryRootTests.cs:16 | McpRepositoryRootTests | 10 | NOT-UNIT | S3 | disk+tempdir+Process+cli |
| 333 | Tests | KyberWeave.Tests/McpStdioTransportTests.cs:22 | McpStdioTransportTests | 2 | NOT-UNIT | S3 | tempdir+Process+cli |
| 334 | Tests | KyberWeave.Tests/McpToolFailureTests.cs:37 | McpToolFailureTests | 3 | NOT-UNIT | S3 | disk+tempdir+cli |
| 335 | Tests | KyberWeave.Tests/McpToolNameContractTests.cs:32 | McpToolNameContractTests | 5 | UNIT | - |  |
| 336 | Tests | KyberWeave.Tests/McpToolingCheckToolTests.cs:17 | McpToolingCheckToolTests | 7 | NOT-UNIT | S3 | disk+tempdir+Process+cli |
| 337 | Tests | KyberWeave.Tests/McpWiringHardeningTests.cs:36 | McpWiringHardeningTests | 37 | NOT-UNIT | S4 | disk+tempdir+env |
| 338 | Tests | KyberWeave.Tests/McpWiringInspectorTests.cs:29 | McpWiringInspectorTests | 35 | NOT-UNIT | S1 | disk+tempdir+cli+corpus; sut=McpWiringInspector |
| 339 | Tests | KyberWeave.Tests/McpWiringReceiptTests.cs:30 | McpWiringReceiptTests | 13 | SCRATCH-DISK | S2 | disk+tempdir; sut=McpWiringReceipt |
| 340 | Tests | KyberWeave.Tests/McpWiringRepairerTests.cs:31 | McpWiringRepairerTests | 3 | SCRATCH-DISK | S2 | disk+tempdir; sut=McpWiringRepairer |
| 341 | Tests | KyberWeave.Tests/ModelProfileOptionalTests.cs:17 | ModelProfileOptionalTests | 5 | NOT-UNIT | S1 | corpus |
| 342 | Tests | KyberWeave.Tests/MotorcycleRagHostProfileTests.cs:17 | MotorcycleRagHostProfileTests | 6 | NOT-UNIT | S6 | disk+tempdir+random |
| 343 | Tests | KyberWeave.Tests/MultipleDocsRootTests.cs:18 | MultipleDocsRootTests | 31 | NOT-UNIT | S4 | disk+tempdir+env+random |
| 344 | Tests | KyberWeave.Tests/Networking/LoopbackAddressTests.cs:14 | LoopbackAddressTests | 4 | UNIT | - | sut=LoopbackAddress |
| 345 | Tests | KyberWeave.Tests/NotSeatedRecordTests.cs:11 | NotSeatedRecordTests | 4 | SCRATCH-DISK | S2 | disk+tempdir; sut=NotSeatedRecord |
| 346 | Tests | KyberWeave.Tests/OnboardingCommandTests.cs:21 | OnboardingCommandTests | 10 | SCRATCH-DISK | S2 | disk |
| 347 | Tests | KyberWeave.Tests/OntologyConfigTests.cs:15 | OntologyConfigTests | 14 | NOT-UNIT | S6 | disk+tempdir+random; sut=OntologyConfig |
| 348 | Tests | KyberWeave.Tests/OpenCodeRendererContractTests.cs:27 | OpenCodeRendererContractTests | 6 | NOT-UNIT | S1 | cli+corpus |
| 349 | Tests | KyberWeave.Tests/OpenWorkValidatorTests.cs:15 | OpenWorkValidatorTests | 9 | UNIT | - | sut=OpenWorkValidator |
| 350 | Tests | KyberWeave.Tests/PacketHostFactsTests.cs:12 | PacketHostFactsTests | 14 | NOT-UNIT | S6 | disk+tempdir+random; sut=PacketHostFacts |
| 351 | Tests | KyberWeave.Tests/PacketTestCommandTests.cs:13 | PacketTestCommandTests | 7 | NOT-UNIT | S1 | corpus; sut=PacketTestCommand |
| 352 | Tests | KyberWeave.Tests/PiRendererContractTests.cs:42 | PiRendererContractTests | 22 | NOT-UNIT | S1 | cli+corpus |
| 353 | Tests | KyberWeave.Tests/PiSquadLifecycleTests.cs:27 | PiSquadLifecycleTests | 3 | NOT-UNIT | S1 | disk+tempdir+corpus |
| 354 | Tests | KyberWeave.Tests/PlanExecutionDigestTests.cs:26 | PlanExecutionDigestTests | 7 | UNIT | - |  |
| 355 | Tests | KyberWeave.Tests/PlanFinalizeTests.cs:20 | PlanFinalizeTests | 36 | NOT-UNIT | S3 | disk+Process+env; sut=PlanFinalize |
| 356 | Tests | KyberWeave.Tests/PlanInventoryValidatorTests.cs:14 | PlanInventoryValidatorTests | 10 | UNIT | - | sut=PlanInventoryValidator |
| 357 | Tests | KyberWeave.Tests/PlanPathResolverTests.cs:11 | PlanPathResolverTests | 10 | NOT-UNIT | S1 | disk+tempdir+random+corpus; sut=PlanPathResolver |
| 358 | Tests | KyberWeave.Tests/PlanTaskAgentPickTests.cs:14 | PlanTaskAgentPickTests | 6 | SCRATCH-DISK | S2 | disk+tempdir; sut=PlanTaskAgentPick |
| 359 | Tests | KyberWeave.Tests/PlanTaskCapabilitiesTests.cs:11 | PlanTaskCapabilitiesTests | 6 | UNIT | - |  |
| 360 | Tests | KyberWeave.Tests/PlanTaskDifficultyAndTechnologyTests.cs:17 | PlanTaskDifficultyAndTechnologyTests | 5 | UNIT | - |  |
| 361 | Tests | KyberWeave.Tests/PlanTaskDifficultyRatingTests.cs:15 | PlanTaskDifficultyRatingTests | 5 | SCRATCH-DISK | S2 | disk+tempdir |
| 362 | Tests | KyberWeave.Tests/PlanTaskFileGeneratorReadTests.cs:13 | PlanTaskFileGeneratorReadTests | 4 | SCRATCH-DISK | S2 | disk |
| 363 | Tests | KyberWeave.Tests/PlanTaskFileScopeTests.cs:15 | PlanTaskFileScopeTests | 5 | SCRATCH-DISK | S2 | disk+tempdir |
| 364 | Tests | KyberWeave.Tests/PlanTaskFileValidatorTests.cs:47 | PlanTaskFileValidatorTests | 40 | NOT-UNIT | S4 | disk+env; sut=PlanTaskFileValidator |
| 365 | Tests | KyberWeave.Tests/PlanTaskFileYamlTests.cs:12 | PlanTaskFileYamlTests | 6 | SCRATCH-DISK | S2 | disk |
| 366 | Tests | KyberWeave.Tests/PlanTaskPacketTests.cs:11 | PlanTaskPacketTests | 7 | UNIT | - | sut=PlanTaskPacket |
| 367 | Tests | KyberWeave.Tests/PlatformAgentContentTests.cs:18 | PlatformAgentContentTests | 12 | NOT-UNIT | S1 | corpus |
| 368 | Tests | KyberWeave.Tests/PlatformReaderProfileTests.cs:12 | PlatformReaderProfileTests | 4 | NOT-UNIT | S1 | corpus |
| 369 | Tests | KyberWeave.Tests/PortableContentTests.cs:24 | PortableContentTests | 1 | NOT-UNIT | S1 | disk+corpus |
| 370 | Tests | KyberWeave.Tests/PostCheckoutHookInstallTests.cs:17 | PostCheckoutHookInstallTests | 9 | NOT-UNIT | S4 | disk+env |
| 371 | Tests | KyberWeave.Tests/PostCheckoutHookScriptTests.cs:25 | PostCheckoutHookScriptTests | 6 | NOT-UNIT | S4 | disk+env+time |
| 372 | Tests | KyberWeave.Tests/PostCheckoutHookTextTests.cs:15 | PostCheckoutHookTextTests | 17 | UNIT | - | sut=PostCheckoutHookText |
| 373 | Tests | KyberWeave.Tests/PresentFileTests.cs:10 | PresentFileTests | 6 | SCRATCH-DISK | S2 | disk+tempdir; sut=PresentFile |
| 374 | Tests | KyberWeave.Tests/ProcessConsoleCaptureTests.cs:6 | ProcessConsoleCaptureTests | 3 | NOT-UNIT | S5 | time+Console |
| 375 | Tests | KyberWeave.Tests/ProcessRunnerInputTests.cs:13 | ProcessRunnerInputTests | 8 | NOT-UNIT | S3 | tempdir+Process+env+random |
| 376 | Tests | KyberWeave.Tests/ProcessRunnerTests.cs:12 | ProcessRunnerTests | 4 | NOT-UNIT | S3 | Process+env; sut=ProcessRunner |
| 377 | Tests | KyberWeave.Tests/ProjectRootDetectorTests.cs:17 | ProjectRootDetectorTests | 24 | NOT-UNIT | S3 | disk+tempdir+Process+cli |
| 378 | Tests | KyberWeave.Tests/ProseRules/ProseRuleCatalogTests.cs:12 | ProseRuleCatalogTests | 11 | NOT-UNIT | S1 | disk+tempdir+corpus |
| 379 | Tests | KyberWeave.Tests/ProseRules/ProseRuleGradingTests.cs:17 | ProseRuleGradingTests | 9 | NOT-UNIT | S1 | disk+env+time+corpus |
| 380 | Tests | KyberWeave.Tests/PullRequestSkillConsolidationTests.cs:29 | PullRequestSkillConsolidationTests | 7 | NOT-UNIT | S1 | disk+tempdir+corpus |
| 381 | Tests | KyberWeave.Tests/RedEvidenceSaturationTests.cs:12 | RedEvidenceSaturationTests | 4 | UNIT | - |  |
| 382 | Tests | KyberWeave.Tests/ReleaseOriginTests.cs:14 | ReleaseOriginTests | 11 | UNIT | - | sut=ReleaseOrigin |
| 383 | Tests | KyberWeave.Tests/RemovedAgentResolutionTests.cs:21 | RemovedAgentResolutionTests | 10 | NOT-UNIT | S5 | disk+tempdir+time; sut=RemovedAgentResolution |
| 384 | Tests | KyberWeave.Tests/RepositoryDoctorTests.cs:15 | RepositoryDoctorTests | 18 | NOT-UNIT | S3 | disk+tempdir+Process+cli+env; sut=RepositoryDoctor |
| 385 | Tests | KyberWeave.Tests/RequiredMcpServerGuardTests.cs:18 | RequiredMcpServerGuardTests | 8 | NOT-UNIT | S3 | cli |
| 386 | Tests | KyberWeave.Tests/RetrievalRegressionTests.cs:37 | RetrievalRegressionTests | 2 | NOT-UNIT | S1 | disk+corpus |
| 387 | Tests | KyberWeave.Tests/RetrievalSourceDocumentTests.cs:27 | RetrievalSourceDocumentTests | 3 | SCRATCH-DISK | S2 | disk |
| 388 | Tests | KyberWeave.Tests/ReviewBlockWriterSectionDetectionTests.cs:11 | ReviewBlockWriterSectionDetectionTests | 2 | UNIT | - |  |
| 389 | Tests | KyberWeave.Tests/ReviewCatalogueCommandTests.cs:21 | ReviewCatalogueCommandTests | 8 | NOT-UNIT | S1 | disk+tempdir+Process+cli+random+corpus; sut=ReviewCatalogueCommand |
| 390 | Tests | KyberWeave.Tests/ReviewConfigTests.cs:14 | ReviewConfigTests | 26 | NOT-UNIT | S1 | disk+cli+corpus; sut=ReviewConfig |
| 391 | Tests | KyberWeave.Tests/ReviewCycleTests.cs:18 | ReviewCycleTests | 19 | NOT-UNIT | S3 | disk+tempdir+Process+cli |
| 392 | Tests | KyberWeave.Tests/ReviewDecisionModelTests.cs:15 | ReviewDecisionModelTests | 26 | NOT-UNIT | S5 | disk+tempdir+time; sut=ReviewDecisionModel |
| 393 | Tests | KyberWeave.Tests/ReviewDuplicatesTests.cs:18 | ReviewDuplicatesTests | 23 | SCRATCH-DISK | S2 | disk+tempdir; sut=ReviewDuplicates |
| 394 | Tests | KyberWeave.Tests/ReviewEvidenceTests.cs:12 | ReviewEvidenceTests | 3 | UNIT | - | sut=ReviewEvidence |
| 395 | Tests | KyberWeave.Tests/ReviewFullSuiteGuardTests.cs:47 | ReviewFullSuiteGuardTests | 4 | NOT-UNIT | S3 | cli |
| 396 | Tests | KyberWeave.Tests/ReviewGateApplicabilityTests.cs:15 | ReviewGateApplicabilityTests | 9 | NOT-UNIT | S3 | tempdir+cli |
| 397 | Tests | KyberWeave.Tests/ReviewGateKeysCliWarningTests.cs:12 | ReviewGateKeysCliWarningTests | 1 | SCRATCH-DISK | S2 | disk+tempdir |
| 398 | Tests | KyberWeave.Tests/ReviewGateKeysConfigTests.cs:19 | ReviewGateKeysConfigTests | 21 | SCRATCH-DISK | S2 | disk+tempdir |
| 399 | Tests | KyberWeave.Tests/ReviewGatePrerequisiteTests.cs:27 | ReviewGatePrerequisiteTests | 8 | NOT-UNIT | S3 | disk+tempdir+cli |
| 400 | Tests | KyberWeave.Tests/ReviewGatesAgentCommandTests.cs:23 | ReviewGatesAgentCommandTests | 32 | NOT-UNIT | S3 | disk+tempdir+Process+cli+env |
| 401 | Tests | KyberWeave.Tests/ReviewGatesCouncilCommandTests.cs:24 | ReviewGatesCouncilCommandTests | 19 | NOT-UNIT | S3 | disk+tempdir+Process+cli+env+Console |
| 402 | Tests | KyberWeave.Tests/ReviewGatesCycleReferenceTests.cs:10 | ReviewGatesCycleReferenceTests | 1 | NOT-UNIT | S1 | disk+corpus |
| 403 | Tests | KyberWeave.Tests/ReviewGatesCycleTests.cs:19 | ReviewGatesCycleTests | 33 | NOT-UNIT | S3 | disk+tempdir+Process+cli+time |
| 404 | Tests | KyberWeave.Tests/ReviewGatesFailingRunProofTests.cs:14 | ReviewGatesFailingRunProofTests | 1 | SCRATCH-DISK | S2 | disk |
| 405 | Tests | KyberWeave.Tests/ReviewGatesIndexTests.cs:27 | ReviewGatesIndexTests | 4 | NOT-UNIT | S3 | disk+tempdir+cli |
| 406 | Tests | KyberWeave.Tests/ReviewGatesInputTests.cs:10 | ReviewGatesInputTests | 1 | SCRATCH-DISK | S2 | disk+tempdir |
| 407 | Tests | KyberWeave.Tests/ReviewGatesRedModeTests.cs:18 | ReviewGatesRedModeTests | 31 | SCRATCH-DISK | S2 | disk+tempdir |
| 408 | Tests | KyberWeave.Tests/ReviewGatesTaskGateNextStepTests.cs:11 | ReviewGatesTaskGateNextStepTests | 7 | UNIT | - |  |
| 409 | Tests | KyberWeave.Tests/ReviewGatesTaskModeTests.cs:18 | ReviewGatesTaskModeTests | 6 | NOT-UNIT | S4 | disk+env |
| 410 | Tests | KyberWeave.Tests/ReviewGatesTechnologyGateTests.cs:14 | ReviewGatesTechnologyGateTests | 7 | NOT-UNIT | S3 | disk+tempdir+Process+cli+env |
| 411 | Tests | KyberWeave.Tests/ReviewJsonTests.cs:12 | ReviewJsonTests | 13 | UNIT | - | sut=ReviewJson |
| 412 | Tests | KyberWeave.Tests/ReviewLensCatalogueTests.cs:12 | ReviewLensCatalogueTests | 1 | NOT-UNIT | S1 | disk+corpus; sut=ReviewLensCatalogue |
| 413 | Tests | KyberWeave.Tests/ReviewLoopTests.cs:7 | ReviewLoopTests | 5 | UNIT | - | sut=ReviewLoop |
| 414 | Tests | KyberWeave.Tests/ReviewMigrateTaskGatesTests.cs:32 | ReviewMigrateTaskGatesTests | 19 | SCRATCH-DISK | S2 | disk+tempdir |
| 415 | Tests | KyberWeave.Tests/ReviewScopeExclusionTests.cs:16 | ReviewScopeExclusionTests | 7 | UNIT | - |  |
| 416 | Tests | KyberWeave.Tests/ReviewSeatInputTests.cs:10 | ReviewSeatInputTests | 7 | UNIT | - | sut=ReviewSeatInput |
| 417 | Tests | KyberWeave.Tests/ReviewSeverityOverrideTests.cs:14 | ReviewSeverityOverrideTests | 27 | NOT-UNIT | S5 | tempdir+time |
| 418 | Tests | KyberWeave.Tests/ReviewSeverityOverrideVerdictTests.cs:15 | ReviewSeverityOverrideVerdictTests | 5 | SCRATCH-DISK | S2 | disk+tempdir |
| 419 | Tests | KyberWeave.Tests/ReviewSuppressionsCommandTests.cs:27 | ReviewSuppressionsCommandTests | 7 | SCRATCH-DISK | S2 | disk+tempdir; sut=ReviewSuppressionsCommand |
| 420 | Tests | KyberWeave.Tests/ReviewTaskGatesConfigTests.cs:35 | ReviewTaskGatesConfigTests | 40 | NOT-UNIT | S1 | cli+corpus |
| 421 | Tests | KyberWeave.Tests/ReviewVerdictCliCommandTests.cs:12 | ReviewVerdictCliCommandTests | 7 | NOT-UNIT | S1 | disk+tempdir+corpus |
| 422 | Tests | KyberWeave.Tests/ReviewVerdictInputTests.cs:16 | ReviewVerdictInputTests | 29 | SCRATCH-DISK | S2 | disk+tempdir |
| 423 | Tests | KyberWeave.Tests/ReviewVerdictSeatsCommandTests.cs:19 | ReviewVerdictSeatsCommandTests | 80 | NOT-UNIT | S4 | disk+tempdir+env |
| 424 | Tests | KyberWeave.Tests/ReviewVerdictTests.cs:14 | ReviewVerdictTests | 29 | UNIT | - |  |
| 425 | Tests | KyberWeave.Tests/RunBaseStateTests.cs:12 | RunBaseStateTests | 6 | SCRATCH-DISK | S2 | disk+tempdir; sut=RunBaseState |
| 426 | Tests | KyberWeave.Tests/RunDeliveryTests.cs:25 | RunDeliveryTests | 42 | NOT-UNIT | S1 | disk+tempdir+Process+cli+env+time+random+corpus; sut=RunDelivery |
| 427 | Tests | KyberWeave.Tests/RunSettingsConfigTests.cs:43 | RunSettingsConfigTests | 3 | UNIT | - |  |
| 428 | Tests | KyberWeave.Tests/RunStartBaseRecordTests.cs:12 | RunStartBaseRecordTests | 2 | SCRATCH-DISK | S2 | tempdir |
| 429 | Tests | KyberWeave.Tests/RunStartBriefTests.cs:11 | RunStartBriefTests | 7 | NOT-UNIT | S1 | disk+corpus |
| 430 | Tests | KyberWeave.Tests/RunStartCommandTests.cs:30 | RunStartCommandTests | 20 | SCRATCH-DISK | S2 | disk; sut=RunStartCommand |
| 431 | Tests | KyberWeave.Tests/RunStartIndexTests.cs:21 | RunStartIndexTests | 5 | SCRATCH-DISK | S2 | disk |
| 432 | Tests | KyberWeave.Tests/RunStartOnboardingTests.cs:23 | RunStartOnboardingTests | 5 | SCRATCH-DISK | S2 | disk |
| 433 | Tests | KyberWeave.Tests/RunStartPlannerTests.cs:34 | RunStartPlannerTests | 17 | UNIT | - | sut=RunStartPlanner |
| 434 | Tests | KyberWeave.Tests/RunStartResumeLogTests.cs:19 | RunStartResumeLogTests | 6 | UNIT | - |  |
| 435 | Tests | KyberWeave.Tests/RunStartResumeTests.cs:30 | RunStartResumeTests | 4 | SCRATCH-DISK | S2 | disk |
| 436 | Tests | KyberWeave.Tests/ScannerAndRoutingTests.cs:9 | SkillScannerTests | 4 | SCRATCH-DISK | S2 | disk |
| 437 | Tests | KyberWeave.Tests/ScannerAndRoutingTests.cs:46 | RoutingTests | 3 | UNIT | - |  |
| 438 | Tests | KyberWeave.Tests/SeatOutputParserTests.cs:12 | SeatOutputParserTests | 71 | NOT-UNIT | S1 | disk+corpus; sut=SeatOutputParser |
| 439 | Tests | KyberWeave.Tests/SectionFrontmatterValidationTests.cs:15 | SectionFrontmatterValidationTests | 14 | UNIT | - |  |
| 440 | Tests | KyberWeave.Tests/SelfUpdaterArbiterRefreshTests.cs:24 | SelfUpdaterArbiterRefreshTests | 9 | NOT-UNIT | S5 | disk+tempdir+time |
| 441 | Tests | KyberWeave.Tests/SelfUpdaterExtractedBinarySearchTests.cs:7 | SelfUpdaterExtractedBinarySearchTests | 2 | NOT-UNIT | S4 | disk+tempdir+env+random |
| 442 | Tests | KyberWeave.Tests/SelfUpdaterPostUpdateHandoffTests.cs:28 | SelfUpdaterPostUpdateHandoffTests | 19 | NOT-UNIT | S3 | disk+tempdir+Process |
| 443 | Tests | KyberWeave.Tests/ServiceCollectionTypeRegistrarTests.cs:10 | ServiceCollectionTypeRegistrarTests | 4 | SCRATCH-DISK | S2 | tempdir; sut=ServiceCollectionTypeRegistrar |
| 444 | Tests | KyberWeave.Tests/SkillNewTemplateTests.cs:11 | SkillNewTemplateTests | 2 | UNIT | - |  |
| 445 | Tests | KyberWeave.Tests/SkillParserTests.cs:6 | SkillParserTests | 8 | SCRATCH-DISK | S2 | disk+tempdir; sut=SkillParser |
| 446 | Tests | KyberWeave.Tests/SkillProvenanceTests.cs:16 | SkillProvenanceTests | 4 | NOT-UNIT | S1 | disk+tempdir+random+corpus |
| 447 | Tests | KyberWeave.Tests/SkillResourceDispositionAuditTests.cs:32 | SkillResourceDispositionAuditTests | 10 | NOT-UNIT | S1 | disk+tempdir+corpus |
| 448 | Tests | KyberWeave.Tests/SkillReviewTests.cs:14 | SkillReviewTests | 16 | UNIT | - |  |
| 449 | Tests | KyberWeave.Tests/Squad/ArbiterHookWiringArchitectProfileTests.cs:11 | ArbiterHookWiringArchitectProfileTests | 1 | UNIT | - |  |
| 450 | Tests | KyberWeave.Tests/Squad/CopilotMcpServerProbeTests.cs:17 | CopilotMcpServerProbeTests | 9 | NOT-UNIT | S3 | disk+tempdir+Process+time+random; sut=CopilotMcpServerProbe |
| 451 | Tests | KyberWeave.Tests/Squad/CopilotRendererTests.cs:17 | CopilotRendererTests | 25 | NOT-UNIT | S1 | disk+tempdir+corpus; sut=CopilotRenderer |
| 452 | Tests | KyberWeave.Tests/Squad/SquadDeveloperAgentShellContractTests.cs:28 | SquadDeveloperAgentShellContractTests | 4 | NOT-UNIT | S1 | cli+corpus |
| 453 | Tests | KyberWeave.Tests/Squad/SquadKyberWeaveSkillsTests.cs:74 | SquadKyberWeaveSkillsTests | 19 | NOT-UNIT | S1 | disk+tempdir+corpus |
| 454 | Tests | KyberWeave.Tests/Squad/SquadOperatorChannelOptInTests.cs:28 | SquadOperatorChannelOptInTests | 18 | NOT-UNIT | S1 | disk+tempdir+env+random+corpus |
| 455 | Tests | KyberWeave.Tests/Squad/SquadPackResponsePreferencesTests.cs:20 | SquadPackResponsePreferencesTests | 12 | NOT-UNIT | S1 | disk+tempdir+Process+env+corpus |
| 456 | Tests | KyberWeave.Tests/Squad/SquadRendererSharedContractTests.cs:31 | SquadRendererSharedContractTests | 15 | NOT-UNIT | S1 | cli+corpus |
| 457 | Tests | KyberWeave.Tests/Squad/SquadResourceLinkRewriterTests.cs:10 | SquadResourceLinkRewriterTests | 17 | UNIT | - | sut=SquadResourceLinkRewriter |
| 458 | Tests | KyberWeave.Tests/Squad/SquadResponsePreferencesInjectorTests.cs:13 | SquadResponsePreferencesInjectorTests | 17 | NOT-UNIT | S6 | disk+tempdir+random; sut=SquadResponsePreferencesInjector |
| 459 | Tests | KyberWeave.Tests/Squad/SquadResponsePreferencesPerTargetTests.cs:16 | SquadResponsePreferencesPerTargetTests | 1 | NOT-UNIT | S1 | corpus |
| 460 | Tests | KyberWeave.Tests/SquadArbiterCliTests.cs:17 | SquadArbiterCliTests | 18 | NOT-UNIT | S5 | disk+tempdir+time |
| 461 | Tests | KyberWeave.Tests/SquadAutonomyRemediationTests.cs:11 | SquadAutonomyRemediationTests | 3 | NOT-UNIT | S1 | disk+corpus |
| 462 | Tests | KyberWeave.Tests/SquadBriefMapTests.cs:14 | SquadBriefMapTests | 3 | NOT-UNIT | S1 | disk+corpus |
| 463 | Tests | KyberWeave.Tests/SquadBriefSelectionTests.cs:11 | SquadBriefSelectionTests | 18 | SCRATCH-DISK | S2 | disk+tempdir |
| 464 | Tests | KyberWeave.Tests/SquadCanonicalContentTests.cs:10 | SquadCanonicalContentTests | 53 | NOT-UNIT | S1 | disk+corpus |
| 465 | Tests | KyberWeave.Tests/SquadCanonicalProfileStoreTests.cs:14 | SquadCanonicalProfileStoreTests | 17 | NOT-UNIT | S5 | disk+tempdir+time; sut=SquadCanonicalProfileStore |
| 466 | Tests | KyberWeave.Tests/SquadCapturePluginStepTests.cs:17 | SquadCapturePluginStepTests | 34 | NOT-UNIT | S1 | disk+tempdir+Process+corpus |
| 467 | Tests | KyberWeave.Tests/SquadCliCommandTests.cs:23 | SquadCliCommandTests | 62 | NOT-UNIT | S1 | disk+tempdir+time+corpus |
| 468 | Tests | KyberWeave.Tests/SquadCodeGraphGrantContractTests.cs:29 | SquadCodeGraphGrantContractTests | 1 | NOT-UNIT | S1 | cli+corpus |
| 469 | Tests | KyberWeave.Tests/SquadConfigurationTests.cs:15 | SquadConfigurationTests | 10 | SCRATCH-DISK | S2 | disk+tempdir |
| 470 | Tests | KyberWeave.Tests/SquadDeliveryExecutionSkillTests.cs:22 | SquadDeliveryExecutionSkillTests | 10 | NOT-UNIT | S1 | disk+corpus |
| 471 | Tests | KyberWeave.Tests/SquadDeployTimeGuidanceWiringTests.cs:19 | SquadDeployTimeGuidanceWiringTests | 12 | SCRATCH-DISK | S2 | disk+tempdir |
| 472 | Tests | KyberWeave.Tests/SquadDeploymentStateTests.cs:24 | SquadDeploymentStateTests | 120 | NOT-UNIT | S1 | disk+tempdir+Process+env+time+corpus |
| 473 | Tests | KyberWeave.Tests/SquadDocsHarvestContractTests.cs:24 | SquadDocsHarvestContractTests | 24 | NOT-UNIT | S1 | disk+corpus |
| 474 | Tests | KyberWeave.Tests/SquadDoctorMcpRenameTests.cs:19 | SquadDoctorMcpRenameTests | 4 | NOT-UNIT | S3 | disk+tempdir+cli |
| 475 | Tests | KyberWeave.Tests/SquadDoctorRetiredSkillTests.cs:17 | SquadDoctorRetiredSkillTests | 3 | NOT-UNIT | S5 | disk+tempdir+time |
| 476 | Tests | KyberWeave.Tests/SquadDoctorShadowedSkillTests.cs:14 | SquadDoctorShadowedSkillTests | 3 | NOT-UNIT | S5 | disk+tempdir+time |
| 477 | Tests | KyberWeave.Tests/SquadGlobalBlockReceiptTests.cs:31 | SquadGlobalBlockReceiptTests | 2 | NOT-UNIT | S1 | disk+tempdir+time+corpus |
| 478 | Tests | KyberWeave.Tests/SquadGlobalHomeCliTests.cs:32 | SquadGlobalHomeCliTests | 47 | NOT-UNIT | S1 | disk+tempdir+time+corpus |
| 479 | Tests | KyberWeave.Tests/SquadGlobalRootTests.cs:28 | SquadGlobalRootTests | 40 | NOT-UNIT | S1 | disk+tempdir+time+corpus |
| 480 | Tests | KyberWeave.Tests/SquadHarnessLauncherTests.cs:12 | SquadHarnessLauncherTests | 17 | NOT-UNIT | S3 | Process; sut=SquadHarnessLauncher |
| 481 | Tests | KyberWeave.Tests/SquadHarnessTokenRenderTests.cs:28 | SquadHarnessTokenRenderTests | 4 | NOT-UNIT | S1 | corpus |
| 482 | Tests | KyberWeave.Tests/SquadHomeStateStoreTests.cs:19 | SquadHomeStateStoreTests | 13 | NOT-UNIT | S5 | disk+tempdir+time |
| 483 | Tests | KyberWeave.Tests/SquadHookBlockReviewFixTests.cs:19 | SquadHookBlockReviewFixTests | 12 | NOT-UNIT | S5 | disk+tempdir+time |
| 484 | Tests | KyberWeave.Tests/SquadHookJsonBlockMalformedHooksTests.cs:7 | SquadHookJsonBlockMalformedHooksTests | 2 | UNIT | - |  |
| 485 | Tests | KyberWeave.Tests/SquadHookJsonBlockTests.cs:13 | SquadHookJsonBlockTests | 24 | NOT-UNIT | S6 | disk+tempdir+random; sut=SquadHookJsonBlock |
| 486 | Tests | KyberWeave.Tests/SquadHookJsonBlockTests.cs:526 | SquadHookFormatResolutionTests | 7 | UNIT | - | sut=SquadHookJsonBlock |
| 487 | Tests | KyberWeave.Tests/SquadHotshotLifecycleCopilotGlobalLinksTests.cs:21 | SquadHotshotLifecycleCopilotGlobalLinksTests | 2 | NOT-UNIT | S1 | disk+tempdir+corpus |
| 488 | Tests | KyberWeave.Tests/SquadHotshotLifecycleCopilotGlobalUpdateLinksTests.cs:14 | SquadHotshotLifecycleCopilotGlobalUpdateLinksTests | 1 | NOT-UNIT | S1 | disk+tempdir+corpus |
| 489 | Tests | KyberWeave.Tests/SquadHotshotLifecycleTests.cs:11 | SquadHotshotLifecycleTests | 1 | SCRATCH-DISK | S2 | disk+tempdir |
| 490 | Tests | KyberWeave.Tests/SquadInstanceCatalogTests.cs:10 | SquadInstanceCatalogTests | 12 | UNIT | - | sut=SquadInstanceCatalog |
| 491 | Tests | KyberWeave.Tests/SquadInstanceCliTests.cs:20 | SquadInstanceCliTests | 14 | NOT-UNIT | S1 | disk+tempdir+time+corpus |
| 492 | Tests | KyberWeave.Tests/SquadInstanceLinkPlannerTests.cs:11 | SquadInstanceLinkPlannerTests | 15 | UNIT | - | sut=SquadInstanceLinkPlanner |
| 493 | Tests | KyberWeave.Tests/SquadInstanceLinkSyncTests.cs:28 | SquadInstanceLinkSyncTests | 16 | NOT-UNIT | S4 | disk+tempdir+env; sut=SquadInstanceLinkSync |
| 494 | Tests | KyberWeave.Tests/SquadInstanceScopeTests.cs:23 | SquadInstanceScopeTests | 9 | NOT-UNIT | S1 | disk+tempdir+time+corpus |
| 495 | Tests | KyberWeave.Tests/SquadInstanceServiceTests.cs:28 | SquadInstanceServiceTests | 8 | SCRATCH-DISK | S2 | disk+tempdir; sut=SquadInstanceService |
| 496 | Tests | KyberWeave.Tests/SquadLegacySingleRootCliTests.cs:24 | SquadLegacySingleRootCliTests | 3 | SCRATCH-DISK | S2 | disk+tempdir |
| 497 | Tests | KyberWeave.Tests/SquadLifecycleRenderWarningFilterTests.cs:26 | SquadLifecycleRenderWarningFilterTests | 9 | NOT-UNIT | S1 | disk+tempdir+corpus |
| 498 | Tests | KyberWeave.Tests/SquadLifecycleTests.cs:15 | SquadLifecycleTests | 15 | SCRATCH-DISK | S2 | disk+tempdir |
| 499 | Tests | KyberWeave.Tests/SquadLinkFileSystemTests.cs:21 | SquadLinkFileSystemTests | 10 | NOT-UNIT | S4 | disk+tempdir+env |
| 500 | Tests | KyberWeave.Tests/SquadLockV2Tests.cs:11 | SquadLockV2Tests | 19 | SCRATCH-DISK | S2 | tempdir |
| 501 | Tests | KyberWeave.Tests/SquadModelPresetLifecycleTests.cs:21 | SquadModelPresetLifecycleTests | 16 | SCRATCH-DISK | S2 | disk+tempdir |
| 502 | Tests | KyberWeave.Tests/SquadModelPresetMergeTests.cs:18 | SquadModelPresetMergeTests | 19 | NOT-UNIT | S1 | corpus |
| 503 | Tests | KyberWeave.Tests/SquadModelPresetRenderTests.cs:18 | SquadModelPresetRenderTests | 7 | NOT-UNIT | S1 | disk+corpus |
| 504 | Tests | KyberWeave.Tests/SquadModelPresetSelectionTests.cs:17 | SquadModelPresetSelectionTests | 14 | SCRATCH-DISK | S2 | disk+tempdir; sut=SquadModelPresetSelection |
| 505 | Tests | KyberWeave.Tests/SquadModelPresetStoreTests.cs:16 | SquadModelPresetStoreTests | 16 | SCRATCH-DISK | S2 | disk+tempdir; sut=SquadModelPresetStore |
| 506 | Tests | KyberWeave.Tests/SquadOperatorChannelCliTests.cs:22 | SquadOperatorChannelCliTests | 8 | NOT-UNIT | S1 | disk+tempdir+corpus |
| 507 | Tests | KyberWeave.Tests/SquadOwnedBlockLifecycleTests.cs:22 | SquadOwnedBlockLifecycleTests | 22 | NOT-UNIT | S1 | disk+tempdir+time+corpus |
| 508 | Tests | KyberWeave.Tests/SquadOwnedBlockStatusTests.cs:20 | SquadOwnedBlockStatusTests | 9 | NOT-UNIT | S4 | disk+tempdir+env+time |
| 509 | Tests | KyberWeave.Tests/SquadPackAndReleaseTests.cs:25 | SquadPackAndReleaseTests | 12 | NOT-UNIT | S1 | disk+tempdir+corpus |
| 510 | Tests | KyberWeave.Tests/SquadPathSafetyTests.cs:54 | SquadPathSafetyTests | 18 | NOT-UNIT | S4 | disk+tempdir+env+time+Console |
| 511 | Tests | KyberWeave.Tests/SquadPerformanceContractTests.cs:12 | SquadPerformanceContractTests | 17 | NOT-UNIT | S1 | disk+corpus |
| 512 | Tests | KyberWeave.Tests/SquadPresetCliTests.cs:35 | SquadPresetCliTests | 35 | NOT-UNIT | S3 | disk+tempdir+Process+env+time |
| 513 | Tests | KyberWeave.Tests/SquadPresetRewriterBundleDigestTests.cs:16 | SquadPresetRewriterBundleDigestTests | 2 | UNIT | - |  |
| 514 | Tests | KyberWeave.Tests/SquadPresetRewriterTests.cs:16 | SquadPresetRewriterTests | 11 | UNIT | - |  |
| 515 | Tests | KyberWeave.Tests/SquadPresetStatusDoctorTests.cs:19 | SquadPresetStatusDoctorTests | 7 | NOT-UNIT | S1 | disk+tempdir+time+corpus |
| 516 | Tests | KyberWeave.Tests/SquadProjectPickerTests.cs:13 | SquadProjectPickerTests | 15 | UNIT | - |  |
| 517 | Tests | KyberWeave.Tests/SquadProjectRegistryTests.cs:18 | SquadProjectRegistryTests | 14 | NOT-UNIT | S5 | disk+tempdir+time |
| 518 | Tests | KyberWeave.Tests/SquadReleaseClientTests.cs:20 | SquadReleaseClientTests | 26 | NOT-UNIT | S3 | disk+tempdir+Process+env |
| 519 | Tests | KyberWeave.Tests/SquadRenderSeamContractTests.cs:21 | SquadRenderSeamContractTests | 8 | NOT-UNIT | S1 | disk+tempdir+env+random+corpus |
| 520 | Tests | KyberWeave.Tests/SquadRenderingContractTests.cs:24 | SquadRenderingContractTests | 2 | NOT-UNIT | S1 | corpus |
| 521 | Tests | KyberWeave.Tests/SquadResourceClosureTests.cs:12 | SquadResourceClosureTests | 10 | NOT-UNIT | S4 | disk+tempdir+env |
| 522 | Tests | KyberWeave.Tests/SquadResponseFormatRetirementTests.cs:21 | SquadResponseFormatRetirementTests | 1 | NOT-UNIT | S1 | disk+tempdir+corpus |
| 523 | Tests | KyberWeave.Tests/SquadResponseGuidanceDeployerTests.cs:18 | SquadResponseGuidanceDeployerTests | 7 | NOT-UNIT | S4 | disk+tempdir+env; sut=SquadResponseGuidanceDeployer |
| 524 | Tests | KyberWeave.Tests/SquadRetiredSkillsTests.cs:11 | SquadRetiredSkillsTests | 3 | NOT-UNIT | S5 | time; sut=SquadRetiredSkills |
| 525 | Tests | KyberWeave.Tests/SquadRootWrapperAgentTests.cs:22 | SquadRootWrapperAgentTests | 6 | NOT-UNIT | S1 | corpus |
| 526 | Tests | KyberWeave.Tests/SquadRunCommandTests.cs:19 | SquadRunCommandTests | 11 | NOT-UNIT | S3 | disk+tempdir+Process; sut=SquadRunCommand |
| 527 | Tests | KyberWeave.Tests/SquadSharedIdentityProjectionTests.cs:14 | SquadSharedIdentityProjectionTests | 3 | UNIT | - | sut=SquadSharedIdentityProjection |
| 528 | Tests | KyberWeave.Tests/SquadSkillsRootLineTests.cs:18 | SquadSkillsRootLineTests | 4 | NOT-UNIT | S1 | tempdir+corpus |
| 529 | Tests | KyberWeave.Tests/SquadSourceTests.cs:11 | SquadSourceTests | 67 | NOT-UNIT | S1 | disk+tempdir+cli+env+corpus; sut=SquadSource |
| 530 | Tests | KyberWeave.Tests/SquadStateMigrationTests.cs:23 | SquadStateMigrationTests | 29 | NOT-UNIT | S4 | disk+tempdir+env; sut=SquadStateMigration |
| 531 | Tests | KyberWeave.Tests/SquadTargetResolutionTests.cs:10 | SquadTargetResolutionTests | 22 | SCRATCH-DISK | S2 | disk+tempdir |
| 532 | Tests | KyberWeave.Tests/SquadTaskGateHintTests.cs:17 | SquadTaskGateHintTests | 9 | NOT-UNIT | S5 | disk+tempdir+time; sut=SquadTaskGateHint |
| 533 | Tests | KyberWeave.Tests/SquadToolchainRosterGuardTests.cs:24 | SquadToolchainRosterGuardTests | 2 | NOT-UNIT | S1 | corpus |
| 534 | Tests | KyberWeave.Tests/SquadUniversalMcpGrantContractTests.cs:29 | SquadUniversalMcpGrantContractTests | 8 | NOT-UNIT | S1 | cli+corpus |
| 535 | Tests | KyberWeave.Tests/SquadVersionOptionTests.cs:25 | SquadVersionOptionTests | 7 | NOT-UNIT | S5 | disk+tempdir+time |
| 536 | Tests | KyberWeave.Tests/SquadWorkerBaselineInjectionTests.cs:33 | SquadWorkerBaselineInjectionTests | 5 | NOT-UNIT | S1 | disk+tempdir+random+corpus |
| 537 | Tests | KyberWeave.Tests/SquadWorktreeMcpContractTests.cs:23 | SquadWorktreeMcpContractTests | 17 | NOT-UNIT | S1 | disk+tempdir+env+corpus |
| 538 | Tests | KyberWeave.Tests/StandardsBuildsOnTests.cs:13 | StandardsBuildsOnTests | 5 | UNIT | - |  |
| 539 | Tests | KyberWeave.Tests/StandardsFolderShapeTests.cs:14 | StandardsFolderShapeTests | 8 | NOT-UNIT | S3 | cli |
| 540 | Tests | KyberWeave.Tests/StdioSessionTests.cs:18 | StdioSessionTests | 5 | NOT-UNIT | S3 | Process+time; sut=StdioSession |
| 541 | Tests | KyberWeave.Tests/StringDistanceTests.cs:5 | StringDistanceTests | 11 | UNIT | - | sut=StringDistance |
| 542 | Tests | KyberWeave.Tests/TaskFileRuleReferenceTests.cs:11 | TaskFileRuleReferenceTests | 1 | NOT-UNIT | S1 | disk+corpus |
| 543 | Tests | KyberWeave.Tests/TaskGateResolverTests.cs:12 | TaskGateResolverTests | 16 | SCRATCH-DISK | S2 | disk+tempdir; sut=TaskGateResolver |
| 544 | Tests | KyberWeave.Tests/TaskGateSelectionTests.cs:13 | TaskGateSelectionTests | 14 | NOT-UNIT | S3 | disk+cli; sut=TaskGateSelection |
| 545 | Tests | KyberWeave.Tests/TaskPromptBudgetTests.cs:19 | TaskPromptBudgetTests | 6 | NOT-UNIT | S1 | disk+corpus |
| 546 | Tests | KyberWeave.Tests/TaskPromptFileFallbackTests.cs:18 | TaskPromptFileFallbackTests | 9 | NOT-UNIT | S1 | disk+corpus |
| 547 | Tests | KyberWeave.Tests/TaskPromptServiceTests.cs:20 | TaskPromptServiceTests | 15 | NOT-UNIT | S4 | disk+env; sut=TaskPromptService |
| 548 | Tests | KyberWeave.Tests/TaskTestDigestTests.cs:10 | TaskTestDigestTests | 8 | UNIT | - | sut=TaskTestDigest |
| 549 | Tests | KyberWeave.Tests/TestFixtureFakesTests.cs:12 | TestFixtureFakesTests | 6 | NOT-UNIT | S1 | disk+tempdir+random+corpus |
| 550 | Tests | KyberWeave.Tests/TieredAgentRenderTests.cs:14 | TieredAgentRenderTests | 2 | NOT-UNIT | S1 | disk+corpus |
| 551 | Tests | KyberWeave.Tests/TodoInventoryValidatorTests.cs:16 | TodoInventoryValidatorTests | 8 | UNIT | - | sut=TodoInventoryValidator |
| 552 | Tests | KyberWeave.Tests/TomlTableScannerTests.cs:27 | TomlTableScannerTests | 11 | UNIT | - | sut=TomlTableScanner |
| 553 | Tests | KyberWeave.Tests/UpdateCommandTests.cs:19 | UpdateCommandTests | 78 | NOT-UNIT | S3 | disk+tempdir+Process+cli; sut=UpdateCommand |
| 554 | Tests | KyberWeave.Tests/UserHostConfigTests.cs:11 | UserHostConfigTests | 4 | NOT-UNIT | S6 | disk+tempdir+random |
| 555 | Tests | KyberWeave.Tests/UtilitiesCliCommandTests.cs:50 | UtilitiesCliCommandTests | 22 | NOT-UNIT | S3 | disk+tempdir+Process+cli+env |
| 556 | Tests | KyberWeave.Tests/UtilitiesStatusLineArtifactTests.cs:63 | UtilitiesStatusLineArtifactTests | 36 | NOT-UNIT | S1 | disk+tempdir+Process+cli+env+time+net+corpus |
| 557 | Tests | KyberWeave.Tests/UtilitiesStatusLineDeploymentTests.cs:47 | UtilitiesStatusLineDeploymentTests | 20 | NOT-UNIT | S4 | disk+tempdir+env |
| 558 | Tests | KyberWeave.Tests/ValidationTests.cs:8 | SpecValidatorTests | 4 | SCRATCH-DISK | S2 | disk |
| 559 | Tests | KyberWeave.Tests/ValidationTests.cs:46 | RoutingLinterTests | 8 | UNIT | - |  |
| 560 | Tests | KyberWeave.Tests/VersionRetrievalTests.cs:13 | VersionRetrievalTests | 7 | NOT-UNIT | S3 | Process+cli |
| 561 | Tests | KyberWeave.Tests/WarpRendererContractTests.cs:20 | WarpRendererContractTests | 2 | NOT-UNIT | S1 | disk+corpus |
| 562 | Tests | KyberWeave.Tests/WorkerRosterTests.cs:17 | WorkerRosterTests | 17 | NOT-UNIT | S1 | disk+corpus |
| 563 | Tests | KyberWeave.Tests/WorkerRuleSingleHomeTests.cs:16 | WorkerRuleSingleHomeTests | 6 | NOT-UNIT | S1 | disk+corpus |
| 564 | Tests | KyberWeave.Tests/WorktreeIndexGuardTests.cs:18 | WorktreeIndexGuardTests | 10 | NOT-UNIT | S3 | disk+tempdir+Process+cli+env |
| 565 | Tests | KyberWeave.Tests/WorktreeIndexHookConfigTests.cs:15 | WorktreeIndexHookConfigTests | 3 | SCRATCH-DISK | S2 | disk+tempdir |
| 566 | Tests | KyberWeave.Tests/ZCodeMcpConfigurationTests.cs:16 | ZCodeMcpConfigurationTests | 8 | NOT-UNIT | S3 | disk+tempdir+cli; sut=ZCodeMcpConfiguration |
| 567 | Tests | KyberWeave.Tests/ZCodeRendererContractTests.cs:39 | ZCodeRendererContractTests | 22 | NOT-UNIT | S1 | disk+tempdir+cli+corpus |
| 568 | Application.Tests | KyberWeave.Application.Tests/Configuration/ConfigLoadServiceTests.cs:13 | ConfigLoadServiceTests | 1 | UNIT | - | sut=ConfigLoadService |
| 569 | Application.Tests | KyberWeave.Application.Tests/Hooks/EditHookServiceTests.cs:7 | EditHookServiceTests | 23 | NOT-UNIT | S3 | cli; sut=EditHookService |
| 570 | Application.Tests | KyberWeave.Application.Tests/Hooks/HooksConfigSourceTests.cs:12 | HooksConfigSourceTests | 5 | NOT-UNIT | S3 | disk+tempdir+cli |
| 571 | Application.Tests | KyberWeave.Application.Tests/Hooks/KernelProcessRunnerTests.cs:8 | KernelProcessRunnerTests | 5 | NOT-UNIT | S3 | disk+cli+env; sut=KernelProcessRunner |
| 572 | Application.Tests | KyberWeave.Application.Tests/Hooks/RealDataCompositionTests.cs:8 | RealDataCompositionTests | 13 | NOT-UNIT | S3 | disk+cli |
| 573 | Domain.Tests | KyberWeave.Domain.Tests/Hooks/ClaudeHookEventParserTests.cs:5 | ClaudeHookEventParserTests | 5 | UNIT | - | sut=ClaudeHookEventParser |
| 574 | Domain.Tests | KyberWeave.Domain.Tests/Hooks/HookFeedbackFormatterTests.cs:5 | HookFeedbackFormatterTests | 12 | UNIT | - | sut=HookFeedbackFormatter |
| 575 | Domain.Tests | KyberWeave.Domain.Tests/Hooks/HookRouterTests.cs:5 | HookRouterTests | 22 | NOT-UNIT | S3 | cli; sut=HookRouter |
| 576 | Domain.Tests | KyberWeave.Domain.Tests/Hooks/HooksDefaultsTests.cs:5 | HooksDefaultsTests | 10 | NOT-UNIT | S3 | cli; sut=HooksDefaults |
| 577 | Domain.Tests | KyberWeave.Domain.Tests/Hooks/PathContainmentTests.cs:5 | PathContainmentTests | 6 | NOT-UNIT | S4 | tempdir+env; sut=PathContainment |
| 578 | Hooks.Tests | KyberWeave.Hooks.Tests/HooksCliTests.cs:6 | HooksCliTests | 10 | SCRATCH-DISK | S2 | disk; sut=HooksCli |
| 579 | Architecture.Tests | KyberWeave.Architecture.Tests/LayerReferenceTests.cs:16 | LayerReferenceTests | 4 | NOT-UNIT | S1 | corpus |
| 580 | PromptContent.Tests | KyberWeave.PromptContent.Tests/ArbiterConductorContractTests.cs:12 | ArbiterConductorContractTests | 7 | NOT-UNIT | S1 | disk+corpus |
| 581 | PromptContent.Tests | KyberWeave.PromptContent.Tests/ArbiterFallbackContractTests.cs:13 | ArbiterFallbackContractTests | 7 | NOT-UNIT | S1 | disk+corpus |
| 582 | PromptContent.Tests | KyberWeave.PromptContent.Tests/ArbiterPlannerContractTests.cs:11 | ArbiterPlannerContractTests | 5 | NOT-UNIT | S1 | disk+corpus |
| 583 | PromptContent.Tests | KyberWeave.PromptContent.Tests/ArbiterReviewContractTests.cs:12 | ArbiterReviewContractTests | 10 | NOT-UNIT | S1 | disk+corpus |
| 584 | PromptContent.Tests | KyberWeave.PromptContent.Tests/PlannerDifficultyAndTechnologyContractTests.cs:10 | PlannerDifficultyAndTechnologyContractTests | 4 | NOT-UNIT | S1 | disk+corpus |
| 585 | PromptContent.Tests | KyberWeave.PromptContent.Tests/ReviewCouncilLoopContractTests.cs:10 | ReviewCouncilLoopContractTests | 1 | NOT-UNIT | S1 | disk+corpus |
| 586 | PromptContent.Tests | KyberWeave.PromptContent.Tests/ReviewGuidanceCouncilContractTests.cs:22 | ReviewGuidanceCouncilContractTests | 13 | NOT-UNIT | S1 | disk+corpus |
| 587 | PromptContent.Tests | KyberWeave.PromptContent.Tests/SquadAutonomyContractTests.cs:12 | SquadAutonomyContractTests | 11 | NOT-UNIT | S1 | disk+corpus |
| 588 | PromptContent.Tests | KyberWeave.PromptContent.Tests/SquadSpecGateCollapseTests.cs:12 | SquadSpecGateCollapseTests | 3 | NOT-UNIT | S1 | disk+corpus |
| 589 | PromptContent.Tests | KyberWeave.PromptContent.Tests/SquadStandardsToolContractTests.cs:26 | SquadStandardsToolContractTests | 4 | NOT-UNIT | S1 | disk+corpus |
| 590 | PromptContent.Tests | KyberWeave.PromptContent.Tests/TieredRosterConductorContractTests.cs:16 | TieredRosterConductorContractTests | 19 | NOT-UNIT | S1 | disk+corpus |
| 591 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/ApmManifestGuardTests.cs:19 | ApmManifestGuardTests | 6 | NOT-UNIT | S1 | disk+cli+corpus |
| 592 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/CiSkillGateCoverageTests.cs:15 | CiSkillGateCoverageTests | 3 | NOT-UNIT | S1 | disk+corpus |
| 593 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/CodeQlConfigTests.cs:13 | CodeQlConfigTests | 3 | NOT-UNIT | S1 | disk+corpus |
| 594 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/CoreAssemblySplitTests.cs:11 | CoreAssemblySplitTests | 1 | NOT-UNIT | S1 | corpus |
| 595 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/DistributionManifestTests.cs:25 | DistributionManifestTests | 9 | NOT-UNIT | S1 | disk+tempdir+Process+cli+env+corpus |
| 596 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/DomainPurityTests.cs:14 | DomainPurityTests | 1 | NOT-UNIT | S1 | disk+Process+corpus |
| 597 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/FastLoopFullRunRefusalTests.cs:25 | FastLoopFullRunRefusalTests | 5 | NOT-UNIT | S1 | disk+tempdir+Process+cli+env+corpus |
| 598 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/FastLoopScriptTests.cs:18 | FastLoopScriptTests | 10 | NOT-UNIT | S1 | disk+tempdir+Process+cli+env+corpus |
| 599 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/KyberWeaveDocsSkillVocabularyTests.cs:17 | KyberWeaveDocsSkillVocabularyTests | 3 | NOT-UNIT | S1 | disk+corpus |
| 600 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/McpRenameRepoGuardTests.cs:31 | McpRenameRepoGuardTests | 10 | NOT-UNIT | S1 | disk+cli+corpus |
| 601 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/PrNamingScriptTests.cs:31 | PrNamingScriptTests | 8 | NOT-UNIT | S1 | disk+Process+env+corpus |
| 602 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/PrNamingWorkflowTests.cs:23 | PrNamingWorkflowTests | 3 | NOT-UNIT | S1 | disk+corpus |
| 603 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/ProcessSpawnSitesTests.cs:19 | ProcessSpawnSitesTests | 4 | NOT-UNIT | S1 | disk+Process+corpus |
| 604 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/ReleaseNotesTests.cs:35 | ReleaseNotesTests | 20 | NOT-UNIT | S1 | disk+tempdir+Process+cli+env+corpus |
| 605 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/ReleaseTests.cs:36 | ReleaseTests | 69 | NOT-UNIT | S1 | disk+tempdir+Process+cli+env+random+corpus |
| 606 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/RemovedAgentNameRepoGuardTests.cs:16 | RemovedAgentNameRepoGuardTests | 1 | NOT-UNIT | S1 | disk+cli+corpus |
| 607 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/ReviewGateFullSuiteConfigTests.cs:29 | ReviewGateFullSuiteConfigTests | 4 | NOT-UNIT | S1 | disk+cli+corpus |
| 608 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/ReviewGateWorkflowTemplateTests.cs:17 | ReviewGateWorkflowTemplateTests | 10 | NOT-UNIT | S1 | disk+cli+corpus |
| 609 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/RootAgentsOperationalPointerTests.cs:17 | RootAgentsOperationalPointerTests | 6 | NOT-UNIT | S1 | disk+corpus |
| 610 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/SbomTests.cs:33 | SbomTests | 3 | NOT-UNIT | S1 | disk+tempdir+Process+env+random+corpus |
| 611 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/SessionStartIndexHookTests.cs:11 | SessionStartIndexHookTests | 3 | NOT-UNIT | S1 | disk+cli+corpus |
| 612 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/SquadAutonomousGatesInventoryTests.cs:10 | SquadAutonomousGatesInventoryTests | 7 | NOT-UNIT | S1 | disk+corpus |
| 613 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/StandardsCompletionSectionTests.cs:24 | StandardsCompletionSectionTests | 3 | NOT-UNIT | S1 | disk+corpus |
| 614 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/TaskGatesRepositoryConfigTests.cs:26 | TaskGatesRepositoryConfigTests | 4 | NOT-UNIT | S1 | corpus |
| 615 | RepoPolicy.Tests | KyberWeave.RepoPolicy.Tests/UpstreamSeveranceTests.cs:31 | UpstreamSeveranceTests | 3 | NOT-UNIT | S1 | disk+Process+cli+corpus |
