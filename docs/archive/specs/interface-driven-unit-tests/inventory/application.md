---
id: specs/interface-driven-unit-tests/inventory/application
title: Inventory of concrete dependencies, Application
doc-type: reference
status: draft
owner: dpalfery
last-reviewed: 2026-11-17
component: CI Pipelines
---

# Inventory of concrete dependencies: Application

Produced 2026-11-17 by a read-only pass over every `.cs` file in scope, at commit `a5af901f` of the
role-based worker agents branch. Evidence for this specification, not living guidance: it goes stale
as the tasks land.


**Read-only inventory, no code changed, nothing built or run.** Checkout: `/Users/dave/git/opencode-worktrees/kyber-weave/inventory` (detached, commit `9ec3357e`).

## Bottom line

`KyberWeave.Application` is already the most seam-rich project in the repository, but it is **not yet free of concrete dependencies and direct outside-resource use**, so not every class here can be tested by a true unit test today.

- **150 `.cs` files, 45,261 lines, 222 declared types** (169 classes, 53 records/structs/enums). All of them were scanned; every one appears in the tables below.
- **171 direct outside-resource sites (category A) across 61 types** (counted one per code line; a few lines carry two calls). The overwhelming majority are one habit: **`Path.GetFullPath` — 91 calls across 54 files** — used to turn a caller's relative path into an absolute one. `IFileSystem` and friends deliberately take *absolute* paths only, so every service does its own path resolution against the process working directory. That single gap is why so many otherwise-clean classes can only be tested by handing them a real path.
- **12 concrete-class dependencies (category B) across 8 types.** Only one class has more than two: `McpWiringService` depends on four concrete collaborators.
- **78 `new`-of-a-service (category C) across 35 types; 46 of those are inside composition roots and sanctioned factories, 32 are inside business services.** The worst cluster is `RepositoryDocsAnalysisReader`, which assembles a whole analysis stack (sqlite persistence, embedding generator, analyzer, claim sources) per call.
- **Zero** inline `HttpClient`, `Console.*`, `Random`, `Mutex`/file locks, or sockets. `Guid.NewGuid` is confined to 3 sites, `DateTimeOffset.UtcNow` to 3, `Thread.Sleep` to 2, raw process start (`Process.Start`) to zero — every child process goes through an injected executor, with `ProcessStartInfo` as the parameter of that seam.
- **The biggest unit-test blockers, in order:** `SelfUpdater` (29 A), `StandardsFolderMigration` (12 A, no seam at all — tested today by writing a real temp tree), `GateRunService` (10 A: deadline logic driven by `Stopwatch`), `TaskPromptService` (10 A, half of them a bypass of its own injected `IFileSystem`), `ReviewVerdictService` (9 A).

**The one new abstraction that buys the most:** a path-resolution port (`IPathResolver`, or extending `IFileSystem`/`IFileSystemPathSemantics` with it). It alone removes the single largest block of A-sites (91 of them) and unlocks clean unit tests for ~30 services. Second is `IFileStatReader` — **it already exists** in `KyberWeave.Contracts` and `DocumentIndexHost` simply does not use it.

## How this was produced, and its limits

- Every `.cs` file under `src/KyberWeave.Application` was read by a script that maps each line to its enclosing type, then pattern-matched for the outside resources, plus manual reading of every constructor and every class that the patterns flagged.
- Constructor analysis covers both primary constructors (`class X(IFoo foo)`) and explicit ones.
- **Entries marked `D (pure data)` are records, DTOs, value objects, options and enums** — they are listed so the table is complete, not because they need work.
- **Category D also covers genuine framework exceptions**, named per row: `TimeProvider` (framework abstraction, fine), `HttpMessageHandler` (abstract, `SelfUpdater` takes it), `ProcessStartInfo` (sealed DTO handed to `IBoundedProcessExecutor`; a fake asserts on its contents, so no wrapper needed), `Lock`/`SemaphoreSlim` (threading primitives), `JsonSerializerOptions`, `MarkdownPipeline`, `Regex`.
- `Environment.NewLine` (9 sites in 3 classes: `DocsQueryService`, `SquadLifecycleService`, `ArbiterHookRefresher`) is treated as a platform constant, not environment state — noted, not counted, because it only varies by OS.
- `Path.Combine`, `Path.GetDirectoryName`, `Path.GetRelativePath`, `Path.TrimEndingDirectorySeparator` are pure string work and are **not** counted; only `Path.GetFullPath` (which reads the process working directory) is.
- Not verified by execution: this is a static reading. No test was run to prove any specific class currently fails as a unit test.

## Categories used

| Category | Meaning |
|---|---|
| **A** | An outside resource used directly: file system, process, environment, time, randomness/ids, network, console, waits/locks, static mutable state. Needs a seam. |
| **B** | A constructor (or method) dependency on a **concrete service class** instead of an interface. Records, DTOs, value objects, options and enums are data, not dependencies. |
| **C** | A service created with `new` inside another service instead of being injected. |
| **D** | Acceptable: a sealed/abstract framework type with no interface, pure data, or a sanctioned composition root / DI factory. |

---

# Per-class inventory (every type, all 150 files)

Rows are grouped by folder, in repository order. `file:line` is the declaration line.

### (root) (1 types, 0 with findings)

| file:line | class | finding | category | should depend on | size |
|---|---|---|---|---|---|
| `ServiceCollectionExtensions.cs:37` | `ServiceCollectionExtensions` | The composition root: every `new` here is intended | D (composition root) | — | M |

### Agents (2 types, 1 with findings)

| file:line | class | finding | category | should depend on | size |
|---|---|---|---|---|---|
| `Agents/AgentFileReader.cs:12` | `AgentFileReader` | Resolves the project root with `Path.GetFullPath` (cwd-relative) before an injected read | A | IPathResolver | S |
| `Agents/AgentGovernanceService.cs:13` | `AgentGovernanceService` | — (no finding: constructor takes interfaces or data only) | D | — | S |

### Arbiter (51 types, 15 with findings)

| file:line | class | finding | category | should depend on | size |
|---|---|---|---|---|---|
| `Arbiter/ArbiterApplicationServiceCollectionExtensions.cs:16` | `ArbiterApplicationServiceCollectionExtensions` | Composition root: `new HookCommand(...)` inside a DI factory | D (composition root) | — | S |
| `Arbiter/ArbiterAuditService.cs:17` | `ArbiterAuditService` | — (no finding: constructor takes interfaces or data only) | D | — | M |
| `Arbiter/ArbiterCliPlanReader.cs:17` | `ArbiterCliPlanReader` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Arbiter/ArbiterCliPlanReader.cs:29` | `ArbiterCliGitFacts` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Arbiter/ArbiterConfigResolver.cs:18` | `ArbiterConfigResolver` | — (no finding: constructor takes interfaces or data only) | D | — | M |
| `Arbiter/ArbiterDoctorService.cs:25` | `ArbiterDoctorService` | — (no finding: constructor takes interfaces or data only) | D | — | L |
| `Arbiter/ArbiterDryRunService.cs:17` | `ArbiterDryRunService` | Builds `ArbiterCliGitFacts` and `ArbiterCliPlanReader` per call, and wraps the provider in `ArbiterEvaluatorSession` | C | IArbiterGitFacts / IArbiterPlanReader; an `IArbiterEvaluatorSessionFactory` | M |
| `Arbiter/ArbiterDryRunService.cs:114` | `ArbiterEvaluatorSession` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Arbiter/ArbiterEvaluator.cs:33` | `ArbiterEvaluator` | `Stopwatch` timings flow into the model prompt and the ledger (5 sites): the same input yields different text per run | A | IMonotonicClock (new) — or drop the timing from the prompt | L |
| `Arbiter/ArbiterEvaluator.cs:194` | `Question` | — (record / data) | D (pure data) | — | M |
| `Arbiter/ArbiterEvaluator.cs:200` | `TriggerPlan` | — (record / data) | D (pure data) | — | M |
| `Arbiter/ArbiterEvaluator.cs:212` | `FindingJudgement` | — (record / data) | D (pure data) | — | M |
| `Arbiter/ArbiterEvaluator.cs:215` | `JudgedFinding` | — (record / data) | D (pure data) | — | M |
| `Arbiter/ArbiterEvaluator.cs:218` | `Step1Call` | Record carrying a `Stopwatch` | A (data, but nondeterministic) | IMonotonicClock | M |
| `Arbiter/ArbiterEvaluatorFactory.cs:9` | `ArbiterEvaluatorFactory` | Builds `ArbiterEvaluator` — the sanctioned factory seam | D (factory) | — | S |
| `Arbiter/ArbiterEvaluatorFactory.cs:39` | `CommentTriageFactory` | Builds `CommentTriageService` — sanctioned factory seam | D (factory) | — | S |
| `Arbiter/ArbiterHarnessGating.cs:24` | `ArbiterHarnessGating` | `Path.GetFullPath` used as path identity when comparing a shim with a rendered hook path | A | IFileSystemPathSemantics.AreSame | L |
| `Arbiter/ArbiterHarnessGating.cs:450` | `Installed` | — (record / data) | D (pure data) | — | S |
| `Arbiter/ArbiterInstallLifecycle.cs:10` | `ArbiterInstallLifecycle` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Arbiter/ArbiterInstallService.cs:26` | `ArbiterInstallService` | `Path.GetFullPath` on the target path and on `environment.HomeDirectory` | A | IPathResolver | L |
| `Arbiter/ArbiterKeyService.cs:14` | `ArbiterKeyService` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Arbiter/ArbiterLogProblems.cs:9` | `ArbiterLogProblems` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Arbiter/ArbiterPlanService.cs:11` | `ArbiterPlanService` | Resolves a relative plan path with `Path.GetFullPath(relative, root)` | A | IPathResolver | S |
| `Arbiter/ArbiterRepositoryLocator.cs:11` | `ArbiterRepositoryLocator` | `Path.GetFullPath` to normalise a start path and a working path | A | IPathResolver | S |
| `Arbiter/ArbiterSectionSelectionService.cs:19` | `ArbiterSectionSelectionService` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Arbiter/ArbiterTuneService.cs:13` | `ArbiterTuneService` | `new RuleTally(...)` — pure local accumulator | D (pure data) | — | M |
| `Arbiter/ArbiterTuneService.cs:156` | `RuleTally` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Arbiter/ArbiterUninstaller.cs:14` | `ArbiterUninstaller` | 4 × `Path.GetFullPath` deciding which recorded paths are inside a root | A | IFileSystemPathSemantics (IsWithin/Canonicalize) | L |
| `Arbiter/CommentTriageService.cs:34` | `CommentTriageService` | — (no finding: constructor takes interfaces or data only) | D | — | M |
| `Arbiter/Facts/AttributedChangeFacts.cs:20` | `AttributedChangeFacts` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Arbiter/Facts/LedgerFacts.cs:10` | `LedgerFacts` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Arbiter/Facts/ModelTargetFacts.cs:18` | `ModelTargetFacts` | — (no finding: constructor takes interfaces or data only) | D | — | M |
| `Arbiter/Facts/RosterDelegatesTo.cs:21` | `RosterDelegatesTo` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Arbiter/Facts/TriggerFactBuilder.cs:22` | `TriggerFactBuilder` | — (no finding: constructor takes interfaces or data only) | D | — | L |
| `Arbiter/Hooks/ArbiterHookDecisionEngine.cs:38` | `ArbiterHookDecisionEngine` | `Path.GetFullPath` for repository-root identity; `Guid.NewGuid()` as the default decision-id factory when none is passed | A / C | IPathResolver + IIdGenerator (new) | L |
| `Arbiter/Hooks/ArbiterHookDecisionEngine.cs:284` | `with` | — (record / data) | D (pure data) | — | S |
| `Arbiter/Hooks/ArbiterHostConfig.cs:14` | `ArbiterHostConfig` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Arbiter/Hooks/ConductorGuard.cs:29` | `ConductorGuard` | `Path.GetFullPath` on the working directory before containment checks | A | IPathResolver | M |
| `Arbiter/Hooks/HookCommand.cs:23` | `HookCommand` | `_newDecisionId = newDecisionId ?? (() => Guid.NewGuid().ToString("N"))` — nondeterministic default; depends on the concrete `HarnessAdapterRegistry` (Domain service class) | A + B | IIdGenerator (new) + IHarnessAdapterRegistry (new) | L |
| `Arbiter/Hooks/HookCommand.cs:373` | `ClaimResult` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Arbiter/Hooks/HookGitFacts.cs:45` | `HookGitFacts` | `Thread.Sleep(20ms)` polling wait for a transcript window; `Stopwatch` in the same loop | A | IMonotonicClock + IDelay (new) | L |
| `Arbiter/Hooks/HookGitFactsFactory.cs:7` | `HookGitFactsFactory` | Builds `HookGitFacts` — sanctioned factory seam | D (factory) | — | S |
| `Arbiter/Hooks/HookPlanReader.cs:12` | `HookPlanReader` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Arbiter/Hooks/WorkerGuard.cs:30` | `WorkerGuard` | Static helpers bypass the injected seams: `File.Exists`, `Directory.Exists`, `new DirectoryInfo`, `Path.GetFullPath`, and `new InFlightLedger(directory)` (Data type) | A + C | IFileSystem + IRunStateStore-style ledger port | M |
| `Arbiter/Mcp/ArbiterRootResolver.cs:13` | `ArbiterRootResolver` | 3 × `Path.GetFullPath`, one against `environment.CurrentDirectory` | A | IPathResolver (or IFileSystemPathSemantics.Canonicalize) | S |
| `Arbiter/Mcp/ArbiterServeContextFactory.cs:11` | `ArbiterServeContextFactory` | Depends on the concrete `ArbiterRootResolver` | B | IArbiterRootResolver (new; 2 members) | S |
| `Arbiter/Mcp/ArbiterServeService.cs:33` | `ArbiterServeService` | — (no finding: constructor takes interfaces or data only) | D | — | L |
| `Arbiter/Mcp/ArbiterServeService.cs:693` | `EvaluationUnit` | — (record / data) | D (pure data) | — | S |
| `Arbiter/Mcp/ArbiterServeService.cs:695` | `FindingBlock` | — (record / data) | D (pure data) | — | S |
| `Arbiter/Mcp/ArbiterServeService.cs:697` | `PendingEvaluation` | — (record / data) | D (pure data) | — | S |
| `Arbiter/OllamaProbe.cs:9` | `OllamaProbe` | — (no finding: constructor takes interfaces or data only) | D | — | S |

### Configuration (1 types, 0 with findings)

| file:line | class | finding | category | should depend on | size |
|---|---|---|---|---|---|
| `Configuration/ConfigLoadService.cs:10` | `ConfigLoadService` | — (no finding: constructor takes interfaces or data only) | D | — | S |

### Docs (100 types, 27 with findings)

| file:line | class | finding | category | should depend on | size |
|---|---|---|---|---|---|
| `Docs/Analysis/DocumentationAnalyzer.cs:15` | `DocumentationAnalyzer` | Depends on the concrete `ClaimExtractor` | B | IClaimExtractor (new; 1 member) | L |
| `Docs/Analysis/Embeddings/EmbeddingCoordinator.cs:10` | `EmbeddingCoordinator` | — (no finding: constructor takes interfaces or data only) | D | — | M |
| `Docs/Analysis/Glossary/ManagedGlossary.cs:25` | `ManagedGlossary` | `Path.GetFullPath` on the repository root and on a resolved document path | A | IPathResolver | L |
| `Docs/Analysis/Glossary/ManagedGlossary.cs:1089` | `ParsedDocument` | — (record / data) | D (pure data) | — | S |
| `Docs/Analysis/Glossary/ManagedGlossary.cs:1095` | `ParsedSection` | — (record / data) | D (pure data) | — | S |
| `Docs/Analysis/Glossary/ManagedGlossary.cs:1106` | `ParsedRow` | — (record / data) | D (pure data) | — | S |
| `Docs/Analysis/Glossary/ManagedGlossary.cs:1115` | `ParsedEvidence` | — (record / data) | D (pure data) | — | S |
| `Docs/Analysis/Glossary/ManagedGlossary.cs:1121` | `CatalogData` | — (record / data) | D (pure data) | — | S |
| `Docs/Analysis/Glossary/ManagedGlossaryFactory.cs:10` | `ManagedGlossaryFactory` | Builds `ManagedGlossary` — sanctioned factory seam (`IManagedGlossaryFactory`) | D (factory) | — | S |
| `Docs/Analysis/Glossary/ManagedGlossaryGraphContributor.cs:9` | `ManagedGlossaryGraphContributor` | — (no finding: constructor takes interfaces or data only) | D | — | M |
| `Docs/Analysis/Glossary/ManagedGlossaryGraphContributor.cs:211` | `TermSnapshot` | — (record / data) | D (pure data) | — | S |
| `Docs/Analysis/Glossary/ManagedGlossaryGraphContributor.cs:213` | `SenseSnapshot` | — (record / data) | D (pure data) | — | S |
| `Docs/Analysis/Review/DocumentationReviewExchange.cs:15` | `DocumentationReviewExchange` | — (no finding: constructor takes interfaces or data only) | D | — | L |
| `Docs/Closeout/ActivePlanFiles.cs:15` | `ActivePlanFiles` | Builds a `ProcessStartInfo` and hands it to the injected `IBoundedProcessExecutor` — the sanctioned pattern, so no seam is missing | D (framework DTO through an existing seam) | — | M |
| `Docs/Closeout/PlanCloseout.cs:30` | `PlanCloseout` | `Path.GetFullPath`; builds `ActivePlanFiles` and `ArchiveCloseoutValidator` per call | A + C | IActivePlanFiles, IArchiveCloseoutValidator; IPathResolver | M |
| `Docs/Closeout/PlanFileResolver.cs:24` | `PlanFileResolver` | — (no finding: constructor takes interfaces or data only) | D | — | M |
| `Docs/Closeout/PlanFinalize.cs:25` | `PlanFinalize` | `Path.GetFullPath`; builds `ActivePlanFiles` and `PlanTaskFileGenerator` per call | A + C | IActivePlanFiles, IPlanTaskFileGenerator; IPathResolver | L |
| `Docs/Closeout/PlanLifecycleServices.cs:9` | `PlanCloseoutService` | Adapter that builds `PlanCloseout` per call (documented port pattern) | D (factory) | — | S |
| `Docs/Closeout/PlanLifecycleServices.cs:26` | `PlanFinalizeService` | Adapter that builds `PlanFinalize` per call (documented port pattern) | D (factory) | — | S |
| `Docs/Closeout/PlanTaskCheckboxes.cs:14` | `PlanTaskCheckboxes` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Docs/Delivery/DeliveryFile.cs:13` | `DeliveryFile` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Docs/Delivery/PullRequestDescription.cs:19` | `PullRequestDescription` | `Directory.Exists` / `Directory.EnumerateFiles` to find a host template; reads the embedded resource through `typeof(...).Assembly` | A | IFileSystem (read) + an `IEmbeddedResource`/template port | M |
| `Docs/Delivery/PullRequestDescription.cs:98` | `HostTemplate` | — (record / data) | D (pure data) | — | S |
| `Docs/Delivery/PullRequestDescription.cs:206` | `Section` | — (record / data) | D (pure data) | — | S |
| `Docs/Delivery/RunDelivery.cs:29` | `RunDelivery` | `Path.GetFullPath`; builds `ActivePlanFiles` per call (`ProcessStartInfo` itself goes through the injected executor, which is fine) | A + C | IActivePlanFiles; IPathResolver | L |
| `Docs/Delivery/RunDelivery.cs:814` | `BoundedExecutor` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Docs/Delivery/RunDeliveryPorts.cs:12` | `RunDeliveryPorts` | — (record / data) | D (pure data) | — | S |
| `Docs/Delivery/RunDeliveryPorts.cs:22` | `RunDeliveryRunner` | `RunDeliveryPorts` is a data record (good); the runner builds `RunDelivery` per call | D (factory) | — | S |
| `Docs/Graph/DocGraphProjectionBuilder.cs:10` | `DocGraphProjectionBuilder` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Docs/Mcp/CorpusProvenanceService.cs:33` | `CorpusProvenanceService` | `Path.GetFullPath` on the repository root, and a **process-wide `static ConcurrentDictionary` cache** that no test can flush | A (incl. static mutable state) | IPathResolver + an injected cache (or a per-process `ICorpusProvenanceCache`) | S |
| `Docs/Mcp/CorpusProvenanceService.cs:79` | `CachedIdentity` | — (record / data) | D (pure data) | — | S |
| `Docs/Mcp/DocsGateRunner.cs:17` | `DocsGateRunner` | Builds `DocDriftLinter` per call | C | IDocDriftLinter (new; 2 members) | S |
| `Docs/Mcp/DocsQueryService.cs:20` | `DocsQueryService` | Depends on the concrete `DocumentIndexHost` (explicit constructor), which itself touches the disk for its staleness stamp | B + A | IDocumentIndexHost (new) — `Current()`, `DocsRelativeRoot`, `RepositoryRoot`, `DocumentCount` | L |
| `Docs/Mcp/DocsQueryService.cs:1241` | `WithheldRuleIds` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Docs/Mcp/DocsToolSupport.cs:9` | `DocsToolSupport` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Docs/Mcp/HostConfiguration.cs:8` | `HostConfiguration` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Docs/Mcp/McpDeclarationReader.cs:8` | `McpDeclarationReader` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Docs/Mcp/McpServerRenamer.cs:33` | `McpServerRenamer` | `Path.GetFullPath` ×2; `DateTimeOffset.UtcNow` written into the rename receipt | A | IPathResolver + TimeProvider | L |
| `Docs/Mcp/McpServerRenamer.cs:1179` | `ConfiguredFile` | — (record / data) | D (pure data) | — | S |
| `Docs/Mcp/McpServerRenamer.cs:1182` | `RuleState` | — (record / data) | D (pure data) | — | S |
| `Docs/Mcp/McpServerRenamer.cs:1197` | `FileAnalysis` | — (record / data) | D (pure data) | — | S |
| `Docs/Mcp/McpWiringFixes.cs:33` | `McpWiringFixes` | Depends on the concrete `McpServerRenamer`; `Path.GetFullPath` | B + A | IMcpServerRenamer (new) + IPathResolver | M |
| `Docs/Mcp/McpWiringFixes.cs:142` | `HarnessPlan` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Docs/Mcp/McpWiringInspector.cs:28` | `McpWiringInspector` | `Path.GetFullPath` on the repository root | A | IPathResolver | L |
| `Docs/Mcp/McpWiringInspector.cs:1238` | `Detection` | — (record / data) | D (pure data) | — | S |
| `Docs/Mcp/McpWiringInspector.cs:1253` | `McpEntryReading` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Docs/Mcp/McpWiringInspector.cs:1280` | `EntryBody` | — (record / data) | D (pure data) | — | S |
| `Docs/Mcp/McpWiringInspector.cs:1312` | `ConfiguredFile` | — (record / data) | D (pure data) | — | S |
| `Docs/Mcp/McpWiringRepairer.cs:41` | `McpWiringRepairer` | `Path.GetFullPath` ×2; `DateTimeOffset.UtcNow` in the repair receipt | A | IPathResolver + TimeProvider | L |
| `Docs/Mcp/McpWiringRepairer.cs:1359` | `ReceiptJournal` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Docs/Mcp/McpWiringService.cs:8` | `McpWiringService` | Depends on four concrete collaborators: `McpWiringInspector`, `McpWiringRepairer`, `McpServerRenamer`, `McpWiringFixes` | B | IMcpWiringInspector, IMcpWiringRepairer, IMcpServerRenamer, IMcpWiringFixes (new) | S |
| `Docs/Mcp/NoCodeGraphRefresh.cs:7` | `NoCodeGraphRefresh` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Docs/Mcp/PlanLifecycleService.cs:15` | `PlanLifecycleService` | Builds `PlanFileResolver` per call | C | IPlanFileResolver (interface exists: `IPlanFileResolver`) | M |
| `Docs/Mcp/RepositoryDocsAnalysisReader.cs:26` | `RepositoryDocsAnalysisReader` | Builds an entire analysis stack inline per call: `SqliteAnalysisPersistence`, `OpenAiCompatibleEmbeddingGenerator`, `EmbeddingCoordinator`, `DocumentationAnalyzer`, `ClaimExtractor`, both claim sources; plus `Path.GetFullPath` | C + A | IAnalysisPersistenceFactory, IEmbeddingResolverFactory, IClaimCandidateSource[] — injected once | M |
| `Docs/Mcp/RunDeliveryService.cs:16` | `RunDeliveryService` | Builds `RunDelivery` per call from `RunDeliveryPorts` (documented port pattern) | D (factory) | — | S |
| `Docs/Mcp/ToolingCheckService.cs:17` | `ToolingCheckService` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Docs/Mcp/ToolingCheckService.cs:76` | `BoundedExecutor` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Docs/Scaffolding/DocsScaffolder.cs:28` | `DocsScaffolder` | `Path.GetFullPath` ×2; builds `ReviewBlockWriter` inside the service | A + C | IReviewGateCatalogue/IReviewBlockWriter; IPathResolver | L |
| `Docs/Scaffolding/ReviewBlockWriter.cs:30` | `ReviewBlockWriter` | — (no finding: constructor takes interfaces or data only) | D | — | M |
| `Docs/Scaffolding/StandardShape.cs:16` | `StandardShape` | — (no finding: constructor takes interfaces or data only) | D | — | M |
| `Docs/Scaffolding/StandardShape.cs:119` | `ReshapeResult` | — (record / data) | D (pure data) | — | S |
| `Docs/Scaffolding/StandardShape.cs:123` | `Section` | — (record / data) | D (pure data) | — | S |
| `Docs/Scaffolding/StandardsFolderMigration.cs:9` | `StandardsFolderChangeKind` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Docs/Scaffolding/StandardsFolderMigration.cs:25` | `StandardsFolderChange` | — (record / data) | D (pure data) | — | M |
| `Docs/Scaffolding/StandardsFolderMigration.cs:31` | `StandardSectionMove` | — (record / data) | D (pure data) | — | M |
| `Docs/Scaffolding/StandardsFolderMigration.cs:37` | `StandardsFolderMigrationResult` | — (record / data) | D (pure data) | — | M |
| `Docs/Scaffolding/StandardsFolderMigration.cs:55` | `StandardsFolderMigration` | No seam at all: 10 × `File.*`, `Directory.*`, `Path.GetFullPath`; tested today by writing a real temp tree (`LegacyHostMigrationTests`) | A | IFileSystem + IWritableFileSystem (+ IPathResolver) | M |
| `Docs/Scaffolding/StandardsTemplateDiff.cs:10` | `StandardsFolderDiff` | — (record / data) | D (pure data) | — | M |
| `Docs/Scaffolding/StandardsTemplateDiff.cs:17` | `StandardsDiffResult` | — (record / data) | D (pure data) | — | M |
| `Docs/Scaffolding/StandardsTemplateDiff.cs:27` | `StandardsTemplateDiff` | No seam: `File.Exists`, `File.ReadAllText`, `Directory.Exists`, `Path.GetFullPath`; constructed directly by `DocsStandardsDiffCommand` | A | IFileSystem + IPathResolver | M |
| `Docs/Search/DocumentIndex.Explore.cs:20` | `DocumentIndex` | — (no finding: constructor takes interfaces or data only) | D | — | L |
| `Docs/Search/DocumentIndex.Explore.cs:98` | `BodyBlock` | — (record / data) | D (pure data) | — | S |
| `Docs/Search/DocumentIndex.Explore.cs:967` | `RankExploreResult` | — (record / data) | D (pure data) | — | M |
| `Docs/Search/DocumentIndex.Explore.cs:969` | `LeafTopicAssignment` | — (record / data) | D (pure data) | — | M |
| `Docs/Search/DocumentIndex.Explore.cs:976` | `ScoredLeaf` | Record wrapping a `DocumentModel` | D (pure data) | — | M |
| `Docs/Search/DocumentIndex.Explore.cs:988` | `LeafTopicContext` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Docs/Search/DocumentIndex.Explore.cs:1088` | `ExploreCursor` | — (record / data) | D (pure data) | — | S |
| `Docs/Search/DocumentIndex.cs:28` | `DocumentIndex` | — (no finding: constructor takes interfaces or data only) | D | — | L |
| `Docs/Search/DocumentIndex.cs:906` | `StandardsBundle` | — (record / data) | D (pure data) | — | S |
| `Docs/Search/DocumentIndexHost.cs:31` | `DocumentIndexHost` | `Directory.Exists`, `File.GetLastWriteTimeUtc` ×2, `File.Exists`, `new FileInfo`, `Path.GetFullPath` | A | IFileSystem + IFileStatReader (new: last-write time and length) | M |
| `Docs/Validation/ArchiveCloseoutValidator.cs:33` | `ArchiveCloseoutValidator` | `Path.GetFullPath` on the repository root | A | IPathResolver | L |
| `Docs/Validation/ArchiveCloseoutValidator.cs:275` | `Failure` | — (record / data) | D (pure data) | — | S |
| `Docs/Validation/ConfigRegValidator.cs:32` | `ConfigRegValidator` | `Path.GetFullPath` on the repository root | A | IPathResolver | M |
| `Docs/Validation/ConfiguredPlanTaskAgentPickerFactory.cs:12` | `ConfiguredPlanTaskAgentPickerFactory` | Depends on the concrete `ConfiguredArbiterProviderFactory` | B | IArbiterProviderFactory (interface already exists) | S |
| `Docs/Validation/DocDriftLinter.cs:17` | `DocDriftLinter` | — (no finding: constructor takes interfaces or data only) | D | — | M |
| `Docs/Validation/DocsSchemaGate.cs:22` | `DocsSchemaGate` | Builds five validators per call: `ConfigRegValidator`, `PlanTaskFileValidator`, `ManagedGlossary`, `OpenWorkValidator`, `ArchiveCloseoutValidator` | C | Register each as an interface (`IConfigRegValidator`, `IPlanTaskFileValidator`, `IArchiveCloseoutValidator`, `IOpenWorkValidator`, `IManagedGlossaryFactory`) | M |
| `Docs/Validation/OpenWorkValidator.cs:34` | `OpenWorkValidator` | — (no finding: constructor takes interfaces or data only) | D | — | M |
| `Docs/Validation/PacketHostFacts.cs:35` | `PacketHostFacts` | — (no finding: constructor takes interfaces or data only) | D | — | M |
| `Docs/Validation/PacketHostFacts.cs:214` | `RuleSections` | — (record / data) | D (pure data) | — | S |
| `Docs/Validation/PlanFileScope.cs:15` | `PlanFileScope` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Docs/Validation/PlanTaskAgentPick.cs:18` | `PlanTaskAgentPick` | — (no finding: constructor takes interfaces or data only) | D | — | M |
| `Docs/Validation/PlanTaskAgentPick.cs:353` | `RemovedAgentResolution` | — (record / data) | D (pure data) | — | S |
| `Docs/Validation/PlanTaskFileGenerator.cs:31` | `PlanTaskFileGenerator` | — (no finding: constructor takes interfaces or data only) | D | — | L |
| `Docs/Validation/PlanTaskFileValidator.cs:43` | `PlanTaskFileValidator` | — (no finding: constructor takes interfaces or data only) | D | — | L |
| `Docs/Validation/PlanTaskService.cs:12` | `PlanTaskService` | Builds `PlanTaskFileGenerator` and `PlanFileResolver` per call; `Path.GetFullPath` | C + A | IPlanTaskFileGenerator, IPlanFileResolver; IPathResolver | S |
| `Docs/Validation/TaskPromptService.cs:28` | `TaskPromptService` | Injects `IFileSystem` yet writes with `File.WriteAllText` and `Directory.CreateDirectory`, probes with `Directory.Exists`/`new FileInfo`/`new DirectoryInfo`, walks links with `ResolveLinkTarget`, and resolves with `Path.GetFullPath` ×4 | A (bypass) | IWritableFileSystem (write/create) + ILinkResolver (new) + IPathResolver | L |
| `Docs/Validation/TaskPromptService.cs:610` | `Part` | — (record / data) | D (pure data) | — | S |
| `Docs/Validation/TaskPromptService.cs:613` | `CutUnit` | — (record / data) | D (pure data) | — | S |
| `Docs/Validation/TaskPromptService.cs:616` | `PromptContent` | — (record / data) | D (pure data) | — | S |
| `Docs/Validation/TaskPromptServiceFactory.cs:16` | `TaskPromptServiceFactory` | Builds `DocumentLoader` (Data) and `TaskPromptService`; `Path.GetFullPath` | C + A | IDocumentSourceFactory + IPathResolver | S |

### Doctor (7 types, 4 with findings)

| file:line | class | finding | category | should depend on | size |
|---|---|---|---|---|---|
| `Doctor/CodeGraphIndexProvisioner.cs:30` | `CodeGraphIndexProvisioner` | `Thread.Sleep` between index probes (the codegraph child itself goes through the injected `IProcessExecutor`) | A | IMonotonicClock.Delay | L |
| `Doctor/CodeGraphIndexProvisioner.cs:674` | `ChildRun` | — (record / data) | D (pure data) | — | S |
| `Doctor/CodeGraphIndexProvisioner.cs:690` | `ChildRunFailure` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Doctor/McpServerLiveProbe.cs:25` | `McpServerLiveProbe` | `Path.GetFullPath` when normalising a reported root | A | IPathResolver | M |
| `Doctor/PostCheckoutHookInstaller.cs:22` | `PostCheckoutHookInstaller` | 3 × `Path.GetFullPath` (`git` runs through the injected executor, which is fine) | A | IPathResolver | M |
| `Doctor/RepositoryDoctorService.cs:23` | `RepositoryDoctorService` | Depends on the concrete `McpServerLiveProbe` and `PostCheckoutHookInstaller` | B | IMcpServerLiveProbe, IPostCheckoutHookInstaller | M |
| `Doctor/WorktreeIndexEnsurer.cs:19` | `WorktreeIndexEnsurer` | — (no finding: constructor takes interfaces or data only) | D | — | S |

### Hooks (2 types, 1 with findings)

| file:line | class | finding | category | should depend on | size |
|---|---|---|---|---|---|
| `Hooks/EditHookService.cs:12` | `EditHookService` | Injects `IFileSystem` but takes the cwd from `Directory.GetCurrentDirectory()` and resolves paths with `Path.GetFullPath` | A (bypass) | IProcessEnvironment.CurrentDirectory + IPathResolver | M |
| `Hooks/ProjectLocator.cs:6` | `ProjectLocator` | — (no finding: constructor takes interfaces or data only) | D | — | S |

### Review (33 types, 17 with findings)

| file:line | class | finding | category | should depend on | size |
|---|---|---|---|---|---|
| `Review/CitedLineChecker.cs:9` | `CitedLineChecker` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Review/CitedLineChecker.cs:72` | `FileLines` | — (record / data) | D (pure data) | — | S |
| `Review/DuplicateDetectionService.cs:38` | `DuplicateDetectionService` | — (no finding: constructor takes interfaces or data only) | D | — | M |
| `Review/GateRunService.cs:27` | `GateRunService` | Deadline logic is `Stopwatch`-driven (8 sites) and a `SemaphoreSlim.Wait` blocks a thread; a deadline test can only be written by really waiting | A | IMonotonicClock (new) — then a test advances the clock | L |
| `Review/GateRunService.cs:358` | `RunBudget` | Record that resolves paths with `Path.GetFullPath` | A | IPathResolver | S |
| `Review/PacketTestCommandRenderer.cs:21` | `PacketTestCommandRenderer` | — (no finding: constructor takes interfaces or data only) | D | — | M |
| `Review/PlanTaskTargetService.cs:19` | `PlanTaskTargetService` | `Directory.Exists` and 2 × `Path.GetFullPath` to compare worktree roots | A | IFileSystemPathSemantics (AreSame) or IPathResolver | M |
| `Review/ReviewCataloguePlanner.cs:12` | `ReviewCataloguePlanner` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Review/ReviewCatalogueService.cs:15` | `ReviewCatalogueService` | `Path.GetFullPath` on the requested path | A | IPathResolver | M |
| `Review/ReviewCycleSelector.cs:16` | `ReviewCycleSelector` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Review/ReviewCycleService.cs:23` | `ReviewCycleService` | `Path.GetFullPath` on the requested path | A | IPathResolver | M |
| `Review/ReviewCycleView.cs:15` | `ReviewCycleView` | Static parser resolving `out` paths with `Path.GetFullPath` ×2 | A | IPathResolver | M |
| `Review/ReviewDecisionCache.cs:13` | `ReviewDecisionCache` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Review/ReviewDecisionModelFactory.cs:17` | `ReviewDecisionModelFactory` | Builds `ReviewDecisionSession` per call — sanctioned factory seam | D (factory) | — | S |
| `Review/ReviewDecisionSession.cs:22` | `ReviewDecisionSession` | — (no finding: constructor takes interfaces or data only) | D | — | M |
| `Review/ReviewDecisionSession.cs:187` | `Sent` | Record carrying a `Stopwatch` | A (data, but nondeterministic) | IMonotonicClock | S |
| `Review/ReviewDecisionSteps.cs:18` | `ReviewDecisionSteps` | — (no finding: constructor takes interfaces or data only) | D | — | L |
| `Review/ReviewDuplicatesService.cs:17` | `ReviewDuplicatesService` | `Path.GetFullPath` on the requested path | A | IPathResolver | S |
| `Review/ReviewEvidenceService.cs:12` | `ReviewEvidenceService` | `Path.GetFullPath` on the gates path it records | A | IPathResolver | S |
| `Review/ReviewGatesService.cs:38` | `ReviewGatesService` | 3 × `Path.GetFullPath` (root, out folder, second invocation) | A | IPathResolver | L |
| `Review/ReviewGatesService.cs:303` | `CouncilCycle` | — (record / data) | D (pure data) | — | S |
| `Review/ReviewReportWriter.cs:8` | `ReviewReportWriter` | Static writer: `Path.GetFullPath` then writes through the injected `IWritableFileSystem` | A | IPathResolver | S |
| `Review/ReviewSeatsService.cs:33` | `ReviewSeatsService` | `Path.GetFullPath` on the requested path | A | IPathResolver | M |
| `Review/ReviewSuppressionsService.cs:12` | `ReviewSuppressionsService` | `Path.GetFullPath` on the requested path | A | IPathResolver | S |
| `Review/ReviewVerdictService.cs:42` | `ReviewVerdictService` | 9 × `Path.GetFullPath` — mostly deciding which review folder a path belongs to | A | IPathResolver + IFileSystemPathSemantics | L |
| `Review/ReviewWorkTreeService.cs:13` | `ReviewWorkTreeService` | `Path.GetFullPath` + `new DirectoryInfo(...).Parent` walk for the containing worktree, while `fileSystem` is injected | A (partial bypass) | IPathResolver + IFileSystem.DirectoryExists/EnumerateDirectories walk | M |
| `Review/SuppressionScanService.cs:13` | `SuppressionScanService` | `Path.GetFullPath` on the working directory | A | IPathResolver | S |
| `Review/SuppressionScanService.cs:77` | `GitRunResult` | — (record / data) | D (pure data) | — | S |
| `Review/TaskGateResolver.cs:8` | `TaskGateSource` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Review/TaskGateResolver.cs:32` | `TaskGateResolution` | — (record / data) | D (pure data) | — | S |
| `Review/TaskGateResolver.cs:55` | `TaskGateResolver` | Static class reading standards with `File.Exists`/`File.ReadAllText` (`ParentTechnology`) — unfakeable from any test | A | IFileSystem passed in (or an `IStandardBuildsOnReader`) | M |
| `Review/TaskGateResolver.cs:163` | `StandardBuildsOn` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Review/TaskGateSelectionService.cs:23` | `TaskGateSelectionService` | — (no finding: constructor takes interfaces or data only) | D | — | S |

### RunStart (5 types, 2 with findings)

| file:line | class | finding | category | should depend on | size |
|---|---|---|---|---|---|
| `RunStart/RunStartEvidenceService.cs:9` | `RunStartEvidenceService` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `RunStart/RunStartRunner.cs:17` | `RunStartRunner` | `Path.GetFullPath` when normalising the repository root | A | IPathResolver | L |
| `RunStart/RunStartRunnerFactory.cs:7` | `RunStartRunnerFactory` | Builds `RunStartRunner` — sanctioned factory seam | D (factory) | — | S |
| `RunStart/RunStartService.cs:24` | `RunStartService` | Wraps the injected executor in a private `BoundedIndexExecutor` | C | IBoundedProcessExecutor (compose at the root) | L |
| `RunStart/RunStartService.cs:516` | `BoundedIndexExecutor` | — (no finding: constructor takes interfaces or data only) | D | — | S |

### Skills (2 types, 1 with findings)

| file:line | class | finding | category | should depend on | size |
|---|---|---|---|---|---|
| `Skills/SkillFileReader.cs:10` | `SkillFileReader` | `Path.GetFullPath` on the skill path | A | IPathResolver | S |
| `Skills/SkillGovernanceService.cs:14` | `SkillGovernanceService` | — (no finding: constructor takes interfaces or data only) | D | — | M |

### Squad (10 types, 3 with findings)

| file:line | class | finding | category | should depend on | size |
|---|---|---|---|---|---|
| `Squad/Briefs/SquadBriefBuilder.cs:19` | `SquadBriefBuilder` | Builds `PlanFileResolver` per call | C | IPlanFileResolver | M |
| `Squad/Briefs/SquadBriefService.cs:8` | `SquadBriefService` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Squad/Deployment/FactoryMcpConfiguration.cs:19` | `FactoryMcpConfiguration` | — (no finding: constructor takes interfaces or data only) | D | — | M |
| `Squad/Deployment/SquadLifecycleService.cs:18` | `SquadLifecycleService` | — (no finding: constructor takes interfaces or data only) | D | — | L |
| `Squad/Instances/SquadInstanceService.cs:18` | `SquadInstanceService` | 2 × `Path.GetFullPath` deciding whether an instance root sits under the expected base | A | IFileSystemPathSemantics.IsWithin | M |
| `Squad/Packaging/SquadPackService.cs:12` | `SquadPackService` | `Path.GetFullPath(request.OutDirectory, workingDirectory)` | A | IPathResolver | S |
| `Squad/Rendering/SquadRendererRegistry.cs:22` | `SquadRendererRegistry` | — (no finding: constructor takes interfaces or data only) | D | — | L |
| `Squad/SquadReleaseSources.cs:8` | `SquadReleaseSources` | Static factory over release sources | D (factory) | — | S |
| `Squad/SquadServiceCollectionExtensions.cs:17` | `SquadServiceCollectionExtensions` | Composition root | D (composition root) | — | S |
| `Squad/SquadServiceCollectionExtensions.cs:73` | `InstanceReleaseSource` | — (no finding: constructor takes interfaces or data only) | D | — | S |

### Update (5 types, 2 with findings)

| file:line | class | finding | category | should depend on | size |
|---|---|---|---|---|---|
| `Update/ArbiterHookRefresher.cs:23` | `ArbiterHookRefresher` | — (no finding: constructor takes interfaces or data only) | D | — | L |
| `Update/ArbiterHookRefresherFactory.cs:14` | `ArbiterHookRefresherFactory` | Builds `ArbiterHookRefresher` — sanctioned factory seam | D (factory) | — | S |
| `Update/CliArbiterRefreshTargets.cs:13` | `CliArbiterRefreshTargets` | — (no finding: constructor takes interfaces or data only) | D | — | S |
| `Update/SelfUpdateService.cs:12` | `SelfUpdateService` | Builds `SelfUpdater` inside the service | C | ISelfUpdater (new) — inject it and keep the builder at the root | S |
| `Update/SelfUpdater.cs:19` | `SelfUpdater` | The heaviest I/O class in the project: 15 × `File.*`, 3 × `Directory.*`, 4 × `Environment.*`, 3 × `Environment.SpecialFolder`, `new FileInfo`, `new DirectoryInfo`, `Directory.CreateTempSubdirectory` ×2, `Path.GetFullPath` ×2 — while `ISelfUpdateSystem` already covers processes/archives/binaries | A (bypass of ISelfUpdateSystem's file half) | Extend ISelfUpdateSystem with the staging file operations (or inject IWritableFileSystem + IProcessEnvironment + IPathResolver) | L |

### Utilities (3 types, 2 with findings)

| file:line | class | finding | category | should depend on | size |
|---|---|---|---|---|---|
| `Utilities/StatusLineDeploymentService.cs:16` | `StatusLineDeploymentService` | — (no finding: constructor takes interfaces or data only) | D | — | L |
| `Utilities/StatusLineHostService.cs:17` | `StatusLineHostService` | Builds `StatusLineProductArtifactSource` per call | C | IStatusLineProductArtifactSource (new; 1 member) | S |
| `Utilities/StatusLineProductArtifactSource.cs:16` | `StatusLineProductArtifactSource` | `Path.GetFullPath(productRoot)` | A | IPathResolver | S |

---

# Per-project totals

| Category | Usages | Types affected |
|---|---|---|
| **A** — outside resource used directly | **171** | 61 of 222 |
| **B** — concrete service dependency | **12** | 8 of 222 |
| **C** — `new` of a service inside a service | **78** (46 in roots/factories, 32 in services) | 35 of 222 |
| **D** — acceptable / pure data / framework | 159 types carry no finding | 159 |

By folder (usages, not types):

| Folder | A | B | C | Note |
|---|---|---|---|---|
| `(root)` | 0 | 0 | 26 | `ServiceCollectionExtensions` — the intended composition root |
| `Agents` | 1 | 0 | 0 | |
| `Arbiter` | 31 | 1 | 9 | Worst: `WorkerGuard` (A+C), `ArbiterHookDecisionEngine` (ids), `ArbiterEvaluator` (Stopwatch) |
| `Configuration` | 0 | 0 | 0 | clean |
| `Docs` | 56 | 8 | 28 | Worst folder: scaffolding, search host, validation, MCP wiring |
| `Doctor` | 5 | 2 | 0 | |
| `Hooks` | 3 | 0 | 0 | `EditHookService` bypasses its own `IFileSystem` for the cwd |
| `Review` | 40 | 0 | 1 | 33 of the 40 are `Path.GetFullPath` |
| `RunStart` | 1 | 0 | 2 | |
| `Skills` | 1 | 0 | 0 | |
| `Squad` | 3 | 0 | 8 | `SquadLifecycleService` is otherwise fully port-driven |
| `Update` | 29 | 0 | 3 | `SelfUpdater` alone accounts for 29 |
| `Utilities` | 1 | 0 | 1 | |

## The five classes with the most findings

| # | Class | Findings | What exactly |
|---|---|---|---|
| 1 | `src/KyberWeave.Application/Update/SelfUpdater.cs:19` `SelfUpdater` (L, 1,171 lines) | **29 A** | 15 × `File.*` (exists/copy/delete/open/read), 3 × `Directory.*` (incl. `CreateTempSubdirectory` ×2), 1 × `new FileInfo`, 1 × `new DirectoryInfo(...).EnumerateFileSystemInfos()`, 4 × `Environment.*` + 3 × `Environment.SpecialFolder.UserProfile`, 2 × `Path.GetFullPath`, and `new GitHubReleaseClient(...)` (C). It already has `ISelfUpdateSystem` for processes, archives and binary replacement — the **file half of that interface is missing**, which is why staging is untestable. |
| 2 | `src/KyberWeave.Application/Docs/Scaffolding/StandardsFolderMigration.cs:55` `StandardsFolderMigration` (M, 273) | **12 A** | 10 × `File.*`, `Directory.*`, `Path.GetFullPath`. **No injected seam at all.** Its only test (`tests/KyberWeave.Tests/LegacyHostMigrationTests.cs:258`) writes a real temporary repository, i.e. an integration test for pure planning logic. |
| 3 | `src/KyberWeave.Application/Review/GateRunService.cs:27` `GateRunService` (L, 479) | **10 A** | `Stopwatch.GetTimestamp`/`GetElapsedTime` in the deadline arithmetic (8 sites), `SemaphoreSlim.Wait` (1), `Path.GetFullPath` (1). A `TimeProvider` is injected for the report's date but **not** for the deadline, so any test of "deadline passed" must really wait. |
| 4 | `src/KyberWeave.Application/Docs/Validation/TaskPromptService.cs:28` `TaskPromptService` (L, 629) | **10 A** | Injects `IFileSystem`, then **bypasses it**: `File.WriteAllText`, `Directory.CreateDirectory`, `Directory.Exists`, `new DirectoryInfo`/`new FileInfo`, `ResolveLinkTarget` (full symlink-chain walk in `RealPath`), `Path.GetFullPath` ×4. |
| 5 | `src/KyberWeave.Application/Review/ReviewVerdictService.cs:42` `ReviewVerdictService` (L, 1,302) | **9 A** | 9 × `Path.GetFullPath`, almost all deciding *which review folder a path belongs to* — identity work `IFileSystemPathSemantics` already models. |

Runners-up worth naming: `DocumentIndexHost` (6 A — `File.GetLastWriteTimeUtc`, `File.Exists`, `new FileInfo`, `Directory.Exists`, `Path.GetFullPath`, while **`IFileStatReader` exists and is unused here**), `DocsSchemaGate` (5 C), `RepositoryDocsAnalysisReader` (4 C + 1 A), and `McpWiringService` (4 B — the largest concrete-dependency cluster).

## Existing interfaces and how widely they are used

`src` declares ~230 interfaces. In this project's constructors, **122 distinct interface types** are used. The load-bearing ones:

| Interface | Constructor uses in this project | Where declared | Notes |
|---|---|---|---|
| `IProcessEnvironment` | **26** | `Contracts/Repositories` | Best-used seam: `CurrentDirectory`, `HomeDirectory`, `TempDirectory`, `IsInputRedirected`, `GetVariable`. **`EditHookService` and `TaskPromptService` bypass equivalent calls.** |
| `IFileSystem` | **19** | `Contracts/Repositories` | Read-only, **absolute paths only** — no write, no stat, no path resolution |
| `IWritableFileSystem` | **12** | `Contracts/Repositories` | Adds write/create/delete/`GetLinkTarget` (one hop) |
| `IRepositoryFileStore` | **9** | `Contracts/Repositories` | `IWritableFileSystem` + bytes, lines, bounded read, atomic write, streams, enumeration |
| `IBoundedProcessExecutor` | **8** | `Contracts/Repositories` | Takes a `ProcessStartInfo`; used by Docs/Review/Delivery |
| `IRunStateStore`, `IReviewCycleStore`, `IGitChangeReader` | 7 each | `Contracts/Repositories` | |
| `IGitRepositoryFacts`, `IArbiterStateFactory`, `IArbiterUserSettings`, `IConfigLoadService` | 6 each | `Contracts`/`Domain` | |
| `IDoctorFileSystem` | 4 | `Kernel/FileSystem` | Probe-shaped: bytes, entries without following links, log tail |
| `IMcpConfigFiles` | 4 | `Contracts/Repositories` | The seam that makes the MCP wiring engines testable |
| `IFileSystemPathSemantics` | 2 | `Domain/Squad/Deployment` | **`AreSame`, `IsWithin`, `Canonicalize`, `ResolvePhysicalRoot` already exist** — used only by Squad. ~10 `Path.GetFullPath` identity comparisons elsewhere should be using it. |
| `IFileStatReader` | 1 | `Contracts/Repositories` | `GetLength`, `GetLastWriteTimeUtc` — **exists, under-used** |
| `IProcessRunner`, `IProgramRunner`, `IExclusiveFileWriter`, `IContainedFileReader`, `IFileTextFacts`, `ISelfUpdateSystem` | 1–3 each | `Contracts`/`Domain` | Each covers only part of the resource it names |

**Pattern that emerges:** the file-system seam family is wide but shallow. Each variant (`IFileSystem`, `IWritableFileSystem`, `IRepositoryFileStore`, `IDoctorFileSystem`, `IMcpConfigFiles`, `IContainedFileReader`, `ISelfUpdateSystem`) was added for one caller, and **none of them owns path resolution or file stat**, so services reach past all of them for `Path.GetFullPath` and `File.GetLastWriteTimeUtc`.

## New interfaces this project needs

Ordered by the number of call sites each would replace. "Members" is the minimum surface; existing contracts should be extended where one already covers part of it.

| # | Interface | Members it must cover | Replaces (call sites) |
|---|---|---|---|
| 1 | **`IPathResolver`** (or `IFileSystem.GetFullPath`, or reuse `IFileSystemPathSemantics.Canonicalize`) | `string Absolute(string path)`, `string Absolute(string path, string baseDirectory)`, `bool AreSame(string, string)`, `bool IsWithin(string root, string candidate)` | **91 × `Path.GetFullPath` in 54 files** — `ReviewVerdictService` 9, `ReviewGatesService` 3, `ArbiterUninstaller` 4, `TaskPromptService` 4, `PostCheckoutHookInstaller` 3, `SquadInstanceService` 2, `ArbiterRootResolver` 3, plus ~30 single-site classes (see A rows) |
| 2 | **`IMonotonicClock`** | `long GetTimestamp()`, `TimeSpan GetElapsed(long since)`, and `Task Delay(TimeSpan, CancellationToken)` | `GateRunService` (deadline logic, 8), `ArbiterEvaluator` (5, timings reach model prompts), `HookGitFacts` (poll loop), `ReviewDecisionSession`, `CodeGraphIndexProvisioner` (`Thread.Sleep`) |
| 3 | **`IIdGenerator`** | `string NewId()` | `HookCommand` (default `Guid.NewGuid`), `ArbiterHookDecisionEngine` (2) |
| 4 | **`ILinkResolver`** | `string? FinalTarget(string path)` honouring every hop, `string? Target(string path)` for one hop | `TaskPromptService.RealPath` (`ResolveLinkTarget` chain). `IWritableFileSystem.GetLinkTarget` covers one hop only; `IDoctorFileSystem.IsPresentFile` covers the "resolves to a file" case |
| 5 | **`IFileStatReader` — already exists, wire it** | `GetLength`, `GetLastWriteTimeUtc` | `DocumentIndexHost.ComputeDocsStamp`/`ComputeCodeGraphStamp` (`File.GetLastWriteTimeUtc`, `new FileInfo(...).Length`), `SelfUpdater`, `ArbiterDoctorService` (already uses it) |
| 6 | **`IStandardBuildsOnReader`** (or pass `IFileSystem` into the static) | `string? ParentTechnology(string repoRoot, KyberWeaveConfig config, string? technology)` as an instance method | `TaskGateResolver.ParentTechnology` — a **static** method doing `File.Exists` + `File.ReadAllText`, called from `PacketHostFacts:107`, `TriggerFactBuilder:192`, `ReviewGatesService:573`, `PacketTestCommandRenderer:116` |
| 7 | **`IEmbeddedTextResource`** (or a template port) | `string Read(string resourceName)` | `PullRequestDescription:243` (`typeof(...).Assembly.GetManifestResourceStream`) and its `Directory.Exists`/`EnumerateFiles` host-template lookup |
| 8 | **`ISelfUpdater`** + **`ISelfUpdateStaging`** | `SelfUpdateOutcome Run(...)`, `bool IsInstalled(...)` / `string Stage(string archive, string name)` | `SelfUpdateService:29` builds `SelfUpdater`; `SelfUpdater` stages through `File.Copy`/`Directory.CreateTempSubdirectory` (15 + 3 sites) |
| 9 | **`IAnalysisStackFactory`** (splitting `RepositoryDocsAnalysisReader`) | `(IAnalysisPersistence, IEmbeddingResolver?, IClaimCandidateSource[]) Create(string repoRoot, KyberWeaveConfig)` | `RepositoryDocsAnalysisReader:68-77` — `new SqliteAnalysisPersistence`, `new OpenAiCompatibleEmbeddingGenerator`, `new EmbeddingCoordinator`, `new DocumentationAnalyzer`, `new ClaimExtractor`, `new GraphClaimCandidateSource`, `new SparseLexicalCandidateSource` |
| 10 | **`IMcpServerRenamer`, `IMcpWiringInspector`, `IMcpWiringRepairer`, `IMcpWiringFixes`** | each the public method set it already has | `McpWiringService` (4 B), `McpWiringFixes` (1 B) |
| 11 | **`IArbiterRootResolver`**, **`IMcpServerLiveProbe`**, **`IPostCheckoutHookInstaller`**, **`IClaimExtractor`**, **`IArbiterProviderFactory`** (exists — use it), **`IPlanFileResolver`** (exists — use it) | their current public methods | `ArbiterServeContextFactory` (B), `RepositoryDoctorService` (2 B), `DocumentationAnalyzer` (B), `ConfiguredPlanTaskAgentPickerFactory` (B), `PlanLifecycleService`/`PlanTaskService`/`SquadBriefBuilder` (C) |
| 12 | **`IActivePlanFiles`, `IPlanTaskFileGenerator`, `IArchiveCloseoutValidator`, `IConfigRegValidator`, `IOpenWorkValidator`, `IPlanTaskFileValidator`, `IReviewBlockWriter`, `IDocDriftLinter`, `IStatusLineProductArtifactSource`, `IArbiterEvaluatorSessionFactory`** | their current public methods | 32 C-usages inside business services (`DocsSchemaGate` 5, `PlanCloseout` 2, `PlanFinalize` 2, `DocsScaffolder`, `DocsGateRunner`, `StatusLineHostService`, `ArbiterDryRunService`, …) |

## Where the "no integration test needed" goal is currently out of reach, and why

1. **Path resolution is unownable.** 54 files call `Path.GetFullPath`, which silently reads the working directory. Any test of them either sets the process cwd (a global, order-dependent mutation) or hands in an absolute temp path. Fixing item 1 above converts ~30 of those classes into pure fakes-and-strings tests.
2. **Three classes cannot be faked at all** because they take no seam: `StandardsFolderMigration`, `StandardsTemplateDiff`, and the static `TaskGateResolver.ParentTechnology`. Their tests today are real-filesystem tests.
3. **Time is only half-seamed.** `TimeProvider` is injected in 17 places for *stamps*, but the two places where time drives *decisions* — `GateRunService`'s deadline and `ArbiterEvaluator`'s elapsed-time prompt text — use `Stopwatch`.
4. **`IFileSystem` is bypassed where the write side is needed.** `TaskPromptService` and `EditHookService` both hold `IFileSystem` and then use `File.*`/`Directory.*`/`Directory.GetCurrentDirectory()`. These are the clearest "seam exists, code walks past it" cases.
5. **Static mutable state.** `CorpusProvenanceService.Cache` is a `static ConcurrentDictionary` shared process-wide: two tests over two repositories interfere, and the cache cannot be flushed. Everything else in the project is instance state.
6. **Composition is coarse in one place.** `RepositoryDocsAnalysisReader` is registered as a singleton but rebuilds its dependency graph on every `Analyze` call, so nothing about the analysis run can be substituted per test.

## Suggested order of work (highest testability gain per line changed)

1. `IPathResolver` (item 1) — mechanical, 91 sites, unblocks the most tests.
2. `DocumentIndexHost` → `IFileStatReader` + `IFileSystem` (already-existing interfaces only).
3. `TaskPromptService` — route writes through `IWritableFileSystem`, add `ILinkResolver`.
4. `StandardsFolderMigration` / `StandardsTemplateDiff` — inject `IFileSystem`/`IWritableFileSystem`; their existing tests then become unit tests.
5. `GateRunService` — `IMonotonicClock` for the deadline.
6. `McpWiringService` + `McpWiringFixes` — interfaces over the four concrete collaborators.
7. `RepositoryDocsAnalysisReader` — split the analysis-stack construction into a factory.
8. `SelfUpdater` — finish `ISelfUpdateSystem`'s file half.
9. `IIdGenerator`, `IStandardBuildsOnReader`, `IEmbeddedTextResource`, `CorpusProvenanceService` cache — small, isolated.
10. The remaining C-items (item 12) — lowest risk, mostly `new` inside `DocsSchemaGate`/`PlanCloseout`/`PlanFinalize`.

## Coverage statement

**Complete:** all 150 `.cs` files under `src/KyberWeave.Application` were scanned line by line (45,261 lines), and every declared type appears in the tables above — including the 53 records, which are marked D. Nothing in this project was left out.

**Not in this report's scope** (owned by other jobs): `src/KyberWeave.Cli`, `src/KyberWeave.Mcp`, `src/KyberWeave.Data`, `src/KyberWeave.Domain`, `src/KyberWeave.Kernel`, `src/KyberWeave.Contracts`, `dash/`, `tests/`. Note that removing the concrete dependencies found here also requires the *interfaces* to live in `Contracts`/`Domain` and the *implementations* to be registered in `Data`/the composition root — cross-project work this inventory did not audit.
