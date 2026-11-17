---
id: specs/interface-driven-unit-tests/inventory/core
title: Inventory of concrete dependencies, Domain, Contracts, Kernel and Core
doc-type: reference
status: draft
owner: dpalfery
last-reviewed: 2026-11-17
component: CI Pipelines
---

# Inventory of concrete dependencies: Domain, Contracts, Kernel and Core

Produced 2026-11-17 by a read-only pass over every `.cs` file in scope, at commit `a5af901f` of the
role-based worker agents branch. Evidence for this specification, not living guidance: it goes stale
as the tasks land.


Date: 2026-11-17. Repository: `kyber-weave` at `9ec3357e`, detached worktree `.../inventory`. Read-only job.

## Headline

The bulk of the product (Domain: 445 files, 70k lines) is **not** built from injectable services at all — it is
**static classes over data**. So the owner's framing ("no concrete classes") only partly fits: Domain has almost no
constructor injection to fix, because it has almost no constructors. What it has instead is ~27 static classes that
touch the file system, the OS or an embedded resource directly, and no seam can replace a static method. The real
work is (a) give those static classes a seam (parameter or instance service), and (b) delete the static
"service locator" facades in `KyberWeave.Core`, `Core.Docs` and `Domain` that `new` up production adapters.

Measured, not estimated: 685 `.cs` files were scanned mechanically (class/record/struct/interface parsing, then
per-line rules for I/O, `new`, concrete-typed fields and parameters, and static members). 65 classes carry
116 flagged sites, plus 22 data-typed parameters that were reviewed and passed as category D. Nothing in Contracts was flagged — it is interfaces and records only, which is correct.

Two structural facts dominate everything below:

1. **`KyberWeave.Core` references `KyberWeave.Data` and `KyberWeave.Application`** (not just Contracts). 18 of its
   21 files `using KyberWeave.Data`. That is why 18 of Core's 21 files can `new PhysicalFileSystem()`,
   `new FileReviewCycleStore(...)` and friends. The layering the specification wants is already broken at the
   project-reference level, so no amount of interface extraction inside Core will hold until that reference goes.
2. **`KyberWeave.Domain` references only `KyberWeave.Kernel`**, so none of the Contracts seams (`IFileSystem`,
   `IProcessRunner`, `IProcessEnvironment`, `TimeProvider`) are even visible to the 70k lines that do most of the
   parsing, validation and rendering. Domain declares 18 interfaces of its own and has 33 interface-typed
   parameters in 445 files.
3. `KyberWeave.Core.Squad` **contains no `.cs` files** (only `.csproj`, `packages.lock.json`, `AGENTS.md`,
   `CLAUDE.md`). Nothing to inventory; it is also the one product project with no test project.

## Coverage

| Project | .cs files | Classes with findings | A | B | C | D (judged acceptable) |
|---|---|---|---|---|---|---|
| KyberWeave.Domain | 445 | 36 | 33 | 22* | 17 | 36 |
| KyberWeave.Contracts | 197 | 0 | 0 | 0 | 0 | — |
| KyberWeave.Kernel | 18 | 7 | 12 | 0 | 0 | 14 |
| KyberWeave.Core | 21 | 18 | 0 | 4 | 38 | 0 |
| KyberWeave.Core.Docs | 4 | 4 | 0 | 0 | 12 | 0 |
| KyberWeave.Core.Squad | 0 | — | — | — | — | — |

\* Domain's 22 B hits were reviewed individually and are all **data** (YAML projections such as `ArbiterRuleYaml`,
`ReviewPolicyYaml`, `McpServerYaml`, plus the `Skill`/`RoutingEvalFile`/`BriefCondition` models). They are counted
in category D in the tables, which is why the D column is non-zero.

Every `.cs` file in all six projects was parsed. No folder was skipped. Findings are attributed to the innermost
enclosing class; a finding on a line outside any class is attributed to `<file-scope>` (none occurred).

---

## KyberWeave.Kernel — the OS boundary (18 files, 12 A findings)

Kernel is the project that *should* hold every direct OS call, and mostly does. Its problem is the opposite of
Domain's: of its 18 files it holds 10 static classes, 2 instance classes (`StdioSession`, `DiagnosticReport`) and 2
interfaces, so nothing above it can substitute it, and the static classes cannot even fake each other. Its findings are acceptable **as production adapters** but not acceptable as things other
code calls directly.

| file:line | class | finding | cat | should depend on | size |
|---|---|---|---|---|---|
| `FileSystem/AtomicFile.cs:70,83,101,107,123,127,140` + `:95,128` | AtomicFile (static) | `File.WriteAllBytes/WriteAllText/Move/Delete`, `Directory.CreateDirectory`, `FileInfo`, `ResolveLinkTarget`, `Path.GetFullPath`; whole class is unmockable | A | new `IAtomicFileWriter.Write(target, bytes/text, throughSymlink)` + `IPathInfo.ResolveLinks` | M |
| `FileSystem/FileFailure.cs:20` | FileFailure (static) | exception classification only, no I/O — but its inputs are raw `Exception` from the above | D | — (pure) | S |
| `FileSystem/BoundedFileReader.cs:29,58` | BoundedFileReader (static) | `new FileStream(path, ...)`, `new StreamReader`; callers cannot supply bytes | A | `IFileWindow.ReadText(path, limitBytes)`; stream overload already exists — take a `Func<string,Stream>` | M |
| `Parsing/FileReferenceExtractor.cs:376,406,407,419,438,444,481,484,486,489,513,533,557,558,570,575` | FileReferenceExtractor (static) | 10 `File.Exists`/`Directory.Exists`/`Directory.EnumerateFileSystemEntries` calls + 6 `Path.GetFullPath`, plus `OperatingSystem.IsWindows` for the comparer. A parser that reads the disk cannot be unit-tested beyond string cases | A | `IFileSystem` (exists in Contracts) passed per call; `IPlatformFacts` for the comparer | L |
| `Parsing/MarkdownFrontmatterReader.cs:49,65,74` | MarkdownFrontmatterReader (static) | text-in/text-out; no I/O | D | — | S |
| `Processes/ProcessRunner.cs:81,210,246,271` | ProcessRunner (static) | two `Process.Start`, `StreamReader`/`StreamWriter` plumbing, `RunInteractive`. This is the one place a real process should start | A | `IProcessRunner` (Contracts) is its interface; keep the class as the adapter, forbid static callers | M |
| `Processes/StdioSession.cs:83,84,158` | StdioSession | `DateTime.UtcNow + budget` and `_deadline - DateTime.UtcNow` for its timeout, `Process.Start` | A | `TimeProvider`; `IProcessFactory.Start(...)` | M |
| `Processes/ShellQuote.cs:15` | ShellQuote (static) | `OperatingSystem.IsWindows()` picks the quoting rule | A | `IPlatformFacts.IsWindows` as a parameter (overload without it stays for prod) | S |
| `VersionControl/CodeGraphIndexStaleness.cs:98,101` | CodeGraphIndexStaleness (static) | `File.Exists`, `File.GetLastWriteTimeUtc` → compares mtime against an index stamp | A | `IFileStampReader.GetLastWriteUtc(path)` | S |
| `Diagnostics/*`, `Networking/LoopbackAddress.cs`, `Text/PathGlob.cs`, `Text/StringDistance.cs`, `Text/TextVectorizer.cs` | Diagnostic, DiagnosticReport, LoopbackAddress, PathGlob, StringDistance, TextVectorizer | pure functions and records; no I/O, no clock, no random | D | — | S |
| `Processes/IProcessExecutor.cs` | IProcessExecutor | second process abstraction in the repo (Contracts has `IProcessRunner`); 1 file in Domain, 6 in Contracts, 1 in Kernel, 2 in Core reference it | D | merge with `IProcessRunner` or document why both exist | S |

**Kernel totals:** A 12, B 0, C 0, D 14. Interfaces declared: `IDoctorFileSystem`, `IProcessExecutor` (2).
**Most findings:** FileReferenceExtractor (17), AtomicFile (9), ProcessRunner (5), StdioSession (3),
CodeGraphIndexStaleness (2).
**New interfaces:** `IAtomicFileWriter`, `IFileWindowReader` (bounded read by path), `IFileStampReader`,
`IPlatformFacts`. All four are small; the rest of Kernel's needs are already met by Contracts interfaces it does
not reference.

---

## KyberWeave.Domain — 445 files, 70k lines (33 A, 17 C, 22 B-as-data)

Domain's shape matters for the report: 18 interfaces, 33 interface-typed parameters, and a large majority of
classes are `static` or `record`. The tables below list **every** Domain class with a finding.

### A — direct outside-resource use (needs a seam)

| file:line | class | finding | cat | interface it should depend on | size |
|---|---|---|---|---|---|
| `Docs/Mcp/McpWiringReceipt.cs:101,107,260,262` | McpWiringReceipt (static) | `File.Exists`, `File.ReadAllText`, `Directory.CreateDirectory`, `File.WriteAllText` — a receipt writer that owns real disk writes | A | `IFileSystem`/`IWritableFileSystem` (Contracts) | M |
| `Docs/Validation/DocSpecValidator.cs:88,313,528,533,535,568` | DocSpecValidator | `Path.GetFullPath` on the repo root, then 5 `File.Exists`/`Directory.Exists` probes to resolve a standard's path. The class is the corpus gate; its tests must fabricate trees today | A | `IFileSystem` + `IContainedFileReader` (both exist) | L |
| `Agents/Parsing/MarkdownAgentParser.cs:30,79`, `Agents/Parsing/TomlAgentParser.cs:16,88` | MarkdownAgentParser, TomlAgentParser (static) | `Path.GetFullPath(filePath)` + `Path.GetDirectoryName` to build a `FilePath` on the parse result; CWD-dependent for a relative input | A | `IPathNormalizer` (new) or take the directory as a parameter | S each |
| `Agents/Validation/AgentSpecValidator.cs:114` | AgentSpecValidator | `Path.GetFullPath(Path.Combine(directoryPath, reference))` to decide whether a referenced file escapes the agent folder | A | same `IPathNormalizer` | S |
| `Arbiter/Hooks/ArbiterProvenance.cs:21` | ArbiterProvenance (static) | `Path.GetFullPath(root)` inside a provenance string | A | `IPathNormalizer` | S |
| `Arbiter/ArbiterConfigLoader.cs:332` (`FolderRulesDocument`), `Arbiter/Squad/ArbiterSquadCatalog.cs:159`, `Docs/Scaffolding/KyberStandardsTemplates.cs:248,312` (`Part`), `Review/Catalogue/GateCatalogueRenderer.cs:46`, `Squad/Deployment/SquadResponseGuidanceSource.cs:27`, `Squad/Briefs/BriefMap.cs:116`, `CopilotPrompt/CopilotPromptFile.cs:28` | FolderRulesDocument, ArbiterSquadCatalog, Part, GateCatalogueRenderer, SquadResponseGuidanceSource, BriefMap, CopilotPromptFile | `new StreamReader(...)` over either an embedded manifest resource or a file opened by the caller. Seven classes read shipped text through a stream they obtain internally | A | `ITextResource` (new): `string ReadEmbedded(string name)`, `Stream OpenEmbedded(string name)`, `string ReadFile(string path)` | M |
| `Configuration/ReviewConfigParser.cs:550,552` | ReviewConfigParser | `Path.GetFullPath` twice to test whether a coverage glob leaves the repo root | A | `IPathNormalizer` | S |
| `Docs/Mcp/McpRepositoryPath.cs:82,84`, `Review/TaskGatePlanning.cs:68`, `Squad/Deployment/SquadPathPolicy.cs:19,20`, `Doctor/CodeGraphIndexRules.cs:117` | McpRepositoryPath, TaskGatePlanning, SquadPathPolicy, CodeGraphIndexRules | `Path.GetFullPath` containment checks; relative inputs resolve against the process CWD, so a test's result depends on where it runs | A | `IPathNormalizer` (`GetFullPath`, `PathComparer`) — `IFileSystemPathSemantics` already exists for the symlink-aware half and 5 Domain files use it | S each |
| `Configuration/DocsAnalysisConfigLoader.cs:240` | DocsAnalysisConfigLoader | `Dns.GetHostAddresses(endpoint.DnsSafeHost)` — a config loader performing a DNS lookup | A | `IDnsResolver` (new) or move the check to Doctor | M |
| `CopilotPrompt/CopilotPromptFile.cs:110,111,112` | CopilotPromptFile (static) | `Path.GetFullPath` twice + `OperatingSystem.IsWindows()` for case-insensitive comparison; unguarded for relative inputs | A | `IPathNormalizer` + `IPlatformFacts` | S |
| `CopilotPrompt/CopilotPromptHome.cs:27` | CopilotPromptHome (static) | `OperatingSystem.IsWindows()` even though the environment already arrives as `Func<string,string?>` — the OS half was missed | A | `IPlatformFacts` | S |
| `FileSystem/DocsRootPath.cs:33` | DocsRootPath (static) | `OperatingSystem.IsWindows()` for the comparer; also exposes `static StringComparer PathComparer` — process-wide state read by other classes | A | `IPlatformFacts`; pass the comparer in | S |
| `Hooks/PathContainment.cs:12,25,26` | PathContainment (static) | `OperatingSystem.IsWindows()/IsMacOS()` and `Path.GetFullPath` — but every input is validated fully-qualified first, so no disk or CWD access. Judged **acceptable** with a caveat | D (was A) | `IPlatformFacts` would make the OS branch testable on one host; advisable, not required | S |
| `Review/Commands/ReviewGateReporting.cs:77` | ReviewGateReporting (static) | `CurrentOs()` returns a shell token from `OperatingSystem.IsWindows()` | A | `IPlatformFacts` | S |
| `RunStart/RunBaseStateFormat.cs:43,48` | RunBaseStateFormat (static) | `OperatingSystem.IsLinux()` picks the comparer; `Path.GetFullPath(directory).Replace('\\','/')` in the state path | A | `IPlatformFacts` + `IPathNormalizer` | S |
| `Skills/Parsing/SkillParser.cs:144,147` | SkillParser (static) | `Path.GetFullPath(file)` to de-duplicate a resource against the skill file | A | `IPathNormalizer` | S |
| `Squad/Deployment/SquadGlobalRoots.cs:115,116,282` | SquadGlobalRoots | `OperatingSystem.IsWindows()` default, `Path.GetFullPath` for the instances directory and for containment | A | `IPlatformFacts`, `IPathNormalizer` | M |
| `Update/PlatformRid.cs:18,19,20` | PlatformRid (static) | three `OperatingSystem.Is*` calls to derive a runtime identifier (there *is* an injectable overload `Detect(bool,bool,bool,Architecture)` — the default overload is the leak) | A | `IPlatformFacts`; keep the pure overload | S |
| `Arbiter/ArbiterConfigLoader.cs:764,1381,1512,1576`, `Configuration/McpConfigLoader.cs:43`, `Configuration/ReviewConfigParser.cs:140,821,837,884`, `Docs/Scaffolding/HostConfigYaml.cs:432,504`, `Skills/Parsing/SkillLoadResult.cs:9`, `Skills/Routing/RoutingEvaluator.cs:41,57`, `Skills/Security/SkillScanner.cs:19`, `Skills/Validation/DescriptionScorer.cs:73`, `Skills/Validation/RoutingLinter.cs:56`, `Skills/Validation/SpecValidator.cs:28`, `Squad/Briefs/BriefMap.cs:85` | 16 classes | parameters/ctor params typed as `ArbiterRuleYaml`, `ArbiterStoresYaml`, `ArbiterProviderYaml`, `McpServerYaml`, `ReviewAppliesWhenYaml`, `ReviewCoverageYaml`, `ReviewDuplicatesYaml`, `ReviewPolicyYaml`, `HostConfigLines`, `Skill`, `RoutingEvalCase`, `RoutingEvalFile`, `BriefCondition` | D (data) | — these are the parsed shapes, not services. Correct as-is | S |

### C — a service created with `new` inside another

| file:line | class | finding | cat | interface it should depend on | size |
|---|---|---|---|---|---|
| `Arbiter/Hooks/CommandHookAdapters.cs:15-22` | CommandHookAdapters (static) | builds all 7 harness adapters (`ClaudeHookAdapter`, `CopilotHookAdapter` ×2, `Codex`, `Cursor`, `Antigravity`, `Factory`, `Devin`) — a static factory that cannot be swapped, and every adapter choice is invisible to a test | C | `IHookAdapterRegistry`/`IHarnessHookAdapter` (both exist) injected, or a `Func<string, IHarnessHookAdapter>` | M |
| `Arbiter/Hooks/PluginHookAdapters.cs:28` | PluginHookAdapters (static) | same shape for `PluginHookAdapter` | C | same registry | S |
| `Squad/Rendering/ClaudeRenderer.cs:192,193,336`, `CommandCodeRenderer.cs:106,107,172`, `CopilotRenderer.cs:79,80,81,258,267`, `FactoryRenderer.cs:144,145,231,235,322`, `PiRenderer.cs:234,235,354` | 5 renderers | each renderer holds a `private readonly SquadYamlEmitter` **field** constructed inline, plus its own YamlDotNet flow-sequence converter type constructed per call. A renderer cannot be tested against a recording emitter, and the emitter cannot be shared or configured | C (B for the field) | `IYamlEmitter` (new, 1–2 members: `string Emit(object)`) injected through the renderer constructor | M each |
| `Arbiter/ArbiterGlobalHookPaths.cs:21` | ArbiterGlobalHookPaths (static) | `new SquadGlobalRoots(...)` | C | `ISquadGlobalRootResolver` (exists) as a parameter | S |
| `Skills/Review/SkillReviewExchange.cs:35` | SkillReviewExchange (static) | `new RoutingLinter()` inside an export routine | C | `IRoutingLinter` (new, tiny) or pass the linter's verdict in | S |
| `Squad/Briefs/BriefMap.cs:128`, `Arbiter/Facts/GitDiffParser.cs:155`, `Configuration/KyberWeaveYamlParser.cs:24` | BriefMap, GitDiffParser, KyberWeaveYamlParser | `new MapDocument(...)`, `new PendingHunk(...)`, `new KyberWeaveYamlDocument(...)` — internal parse-builders | D (data/local helper) | — | S |
| `Review/SeatOutputParser.cs:115,144,159,367,376,443` (+5 more), `Review/SeatFlatYamlReader.cs:72,79,97,154,161,162`, `Squad/Validation/*`, `Docs/Validation/*`, `Update/*`, `StatusLine/*`, `Squad/Deployment/*`, `Squad/Rendering/*` (~40 sites) | SeatOutputParser and 20 others | `new SomethingException(...)` — exceptions are constructed, never injected. `SeatOutputParser` alone throws 11 distinct construction sites | D | — | S |

### Domain totals

A 33, B 0 real (22 reviewed as data), C 17 (of which 8 real service constructions, 9 local helpers/exceptions),
D ~36. Top five by findings: `DocSpecValidator` (6), `SquadHarnessRenderer` (4 C), `SquadGlobalRoots` (3),
`McpWiringReceipt` (4), `SeatOutputParser` (11, all D).

**Existing interfaces in Domain (18)** and how widely used: `IFileSystemPathSemantics` (5 files — the one seam
Domain actually uses), `ISquadGlobalRootResolver`, `ISquadUserPaths`, `ISquadRenderer`, `IRoutingStrategy`,
`IAgentParser`, `IDocsEngineConfig`, `ICredentialStore`, `ICredentialProcessRunner`, `IArbiterProvider`,
`IHarnessHookAdapter`, `IHookDecisionEngine`, `IContextualHookDecisionEngine`, `IBackgroundDispatchEngine`,
`IFactoryTranscriptSource`, `IPlanTaskAgentPicker`, `ISquadTransactionObserver`,
`ISquadTransactionCheckpointObserver`. Every one is a domain concept; **none is an I/O seam**. That is the gap.

**New interfaces Domain needs:** `IPathNormalizer` (`GetFullPath`, `PathComparer`), `IPlatformFacts`
(`IsWindows/IsLinux/IsMacOS`, `Architecture`), `ITextResource` (embedded + file text), `IFileSystem` +
`IWritableFileSystem` (already in Contracts — Domain must be allowed to reference Contracts or get a local
duplicate), `IDnsResolver`, `IYamlEmitter`, `IHookAdapterRegistry`, `IRoutingLinter`.

---

## KyberWeave.Core — 21 files, 13 of them static service locators (38 C, 4 B)

This is the worst project in scope by density: **18 of its 21 files build their own production dependencies**, and
they can do so only because the project references `KyberWeave.Data` and `KyberWeave.Application`. Each row below
is a static class whose static fields or properties `new` up a chain of Data adapters. They are unit-testable only
by whatever the log/docs gates redirect, never by substitution.

| file:line | class | finding | cat | should depend on | size |
|---|---|---|---|---|---|
| `Doctor/DoctorServices.cs:22,27,30,32,34,36,43` | DoctorServices (internal static) | static readonly `Files = new PhysicalDoctorFileSystem()`, `Settings = new ConfigFileToolSettings(...)`; properties build `CodeGraphIndexProvisioner(new FileCodeGraphIndexWorkspace())`, `WorktreeIndexEnsurer`, `StdioMcpSessionFactory`, `FileGitHookFiles`, `RepositoryDoctorService`, `ProcessEnvironment`. 9 constructions | C | the contracts it already names (`IRepositoryDoctor`, `IWorktreeIndexGuard`, `ICodeGraphProvisioner`) taken from the container; delete this class | L |
| `Doctor/DoctorFileSystem.cs:17` | DoctorFileSystem | `new PhysicalDoctorFileSystem()` | C | `IDoctorFileSystem` parameter | S |
| `RunStart/RunBaseState.cs:24` | RunBaseState (static) | `internal static IRunStateStore Store { get; } = new FileRunStateStore(new PhysicalFileSystem(), new GitFactsBranchReader())` — process-wide singleton, real disk, real git | C | `IRunStateStore` passed in; the static entry points become an adapter named `RunBaseStateFileStore` | L |
| `RunStart/RunCheckout.cs:13,14` | RunCheckout | `B:field: RunCheckoutGuard` + `new PhysicalFileSystem()` | B + C | `IRunStateStore`, `IFileSystem`, `IGitCommandRunner` | M |
| `RunStart/RunStartEvidence.cs:20` | RunStartEvidence | field typed `RunStartEvidenceService` (concrete) | B | `IRunStartEvidence` (new, 1 member) or the service's interface | S |
| `Configuration/KyberWeaveConfigLoader.cs:25`, `Configuration/KyberWeaveYamlFile.cs:13,14` | KyberWeaveConfigLoader, KyberWeaveYamlFile | `new FileKyberWeaveConfigRepository(new FileRepositoryTreeProbe())` — the config loader owns its repository | B + C | `IKyberWeaveConfigRepository` (exists) injected | M |
| `Configuration/ReviewConfigLoader.cs:12` | ReviewConfigLoader | `B:field: FileRepositoryTreeProbe` | B | `IRepositoryTreeProbe` (new) | S |
| `Review/ChangedFiles.cs:17` | ChangedFiles | `new GitChangeReader(new ProcessProgramRunner())` | C | `IGitChangeReader` (exists) | M |
| `Review/ReviewCycles.cs:11` | ReviewCycles | `new FileReviewCycleStore(...)` | C | `IReviewCycleStore` (new) | M |
| `Review/ReviewDecisionModel.cs:38,41` | ReviewDecisionModel | `new DecisionLog(...)`, `new FileReviewDecisionCacheStore(...)` | C | `IReviewDecisionCacheStore` (exists — 1 Contracts file already names it) | M |
| `Review/ReviewLensApplicability.cs:18`, `Review/ReviewVerdictDecisions.cs:18` | ReviewLensApplicability, ReviewVerdictDecisions | identical triple: `new ReviewDecisionSteps(new FileCitedSourceFiles(), new FileContainedFileReader())` in both | C | `IReviewDecisionSteps`, `ICitedSourceFiles`, `IContainedFileReader` (the last exists) | M each |
| `Review/TaskGateSelection.cs:20`, `Review/TestClassMapping.cs:15` | TaskGateSelection, TestClassMapping | `new TaskGateSelectionService(new FileRepositoryTreeProbe(), new FileTestClassSources())` in both — duplicated wiring | C | `ITaskGateSelectionService` + its two probes injected | M each |
| `Review/SeverityOverrideFiles.cs:15` | SeverityOverrideFiles | `new FileSeverityOverrideSource(...)` | C | `ISeverityOverrideSource` (new) | S |
| `Arbiter/Providers/ConfiguredProvider.cs:27` | ConfiguredProvider | `new ConfiguredArbiterProviderFactory(new PhysicalFileSystem())` | C | `IArbiterProviderFactory` (exists) | S |
| `CodeGraph/CodeGraphResolvers.cs:15` | CodeGraphResolvers | `new CodeGraphPortsFactory()` | C | `ICodeGraphPortsFactory` (new) | S |
| `Review/Catalogue/GateCatalogue.cs`, `Review/ReviewDecisionModel.cs`, `Review/ChangedFiles.cs`, `Review/ReviewLensApplicability.cs` | — | see above; `GateCatalogue.cs` is pure data and clean | D | — | — |

**Core totals:** A 0 (it does no direct I/O itself — it delegates to Data, which is the point), B 4, C 38, D 0.
Top five: `DoctorServices` (9), `RunBaseState` (3), `TaskGateSelection` (3), `TestClassMapping` (3),
`ReviewLensApplicability` (3) / `ReviewVerdictDecisions` (3) tie.

**Existing interfaces:** Core uses `IDoctorFileSystem` (2 files), `IProcessExecutor` (2), `TimeProvider` (1)
and 30+ Contracts interfaces by name. It never *takes* them in a constructor in the static facades — it builds
the implementations and assigns them to interface-typed statics, which is why the compiler was satisfied.
**New interfaces Core needs:** none of substance. It needs (a) the `KyberWeave.Data` / `KyberWeave.Application`
project references removed, (b) its 13 static facades turned into instance services with constructor injection, and
(c) an `ICompositionRoot` in Application for the container-less callers the facades exist to serve.

---

## KyberWeave.Core.Docs — 4 files, all four are locators (12 C)

| file:line | class | finding | cat | should depend on | size |
|---|---|---|---|---|---|
| `LegacyPorts.cs:12` | LegacyPorts (internal static) | `static readonly IRepositoryFileStore Files = new PhysicalRepositoryFileStore()`; its own comment says it exists so classes that have not moved can hand physical adapters to ones that have | C | delete; callers take `IRepositoryFileStore` | S |
| `Validation/PlanTaskTarget.cs:21,22,23` | PlanTaskTarget | `new PlanTaskTargetService`, `new PlanTaskService`, `new TaskGateSelectionService`, `new FileRepositoryTreeProbe`, `new FileTestClassSources` — five-level graph built inline | C | the five contracts as constructor parameters | L |
| `Validation/PacketTestCommand.cs:22,23,24,28` | PacketTestCommand | the same five, duplicated from PlanTaskTarget | C | same | M |
| `Closeout/PlanPathResolver.cs:13` | PlanPathResolver | `new PhysicalRepositoryFileStore()` | C | `IRepositoryFileStore` | S |

**Totals:** C 12, A 0, B 0, D 0. **Interfaces used:** `IRepositoryFileStore` (Contracts) is named but never
injected. **New interfaces:** none — `IRepositoryFileStore`, `IPlanTaskService`, `ITaskGateSelectionService`
already exist or are trivial to declare.

---

## KyberWeave.Contracts — 197 files, zero findings

207 interfaces, the rest records/DTOs/enums. Every seam the other projects need already exists here or is a
one-member addition. Two notes worth carrying into the plan: `IProcessRunner` and `IProcessExecutor` (Kernel) are
duplicates, and `IFileSystem`/`IWritableFileSystem` are referenced by exactly **2 files each** while 685 product
files exist — the seam is declared but barely used.

## How the code is tested today (context for the D5 decision)

| Test project | References product projects | Gap |
|---|---|---|
| `KyberWeave.Domain.Tests` | Domain | exists; Domain is static so most cases are text-in/text-out |
| `KyberWeave.Tests` | Arbiter, Cli, Core, Core.Docs, Core.Squad, Mcp | catches everything with no home |
| `KyberWeave.Application.Tests` | Application, Contracts, Domain | — |
| `KyberWeave.Hooks.Tests` | Hooks, Contracts, Domain | — |
| `KyberWeave.Architecture.Tests` | (none listed) | — |
| `KyberWeave.RepoPolicy.Tests` | Core | — |
| `KyberWeave.PromptContent.Tests` | (none listed) | — |
| — | **Kernel, Contracts, Core.Docs, Core.Squad have no dedicated test project** | the "one test project per product project" requirement in D5 is unmet for four of the six projects in this scope |

Because Domain's I/O is static, its tests must fabricate real directory trees; because Core's wiring is static,
its tests cannot substitute `FileReviewCycleStore` at all. Both are the "integration test by necessity" the owner
wants gone.

## What this implies for the specification (short)

1. Remove `KyberWeave.Core` → `KyberWeave.Data` and `KyberWeave.Application` references; Core becomes a domain
   service layer over Contracts only. Without this, every interface extraction in Core is cosmetic.
2. Give Domain read access to Contracts (or move `IFileSystem`/`IWritableFileSystem`/`IPlatformFacts`/
   `IPathNormalizer` into Kernel, which Domain already references). Domain cannot be unit-testable otherwise.
3. Add four tiny seams: `IPathNormalizer`, `IPlatformFacts`, `ITextResource`, `IYamlEmitter`. They retire 26 of
   Domain's 33 A findings and all 5 renderer B/C findings.
4. Convert the 17 static locators (13 in Core, 4 in Core.Docs, plus `DoctorServices`, `RunBaseState`, `LegacyPorts`)
   to injected services. That is 54 of the 67 C findings in one mechanical sweep.
5. Kernel stays the only project allowed to call `File`/`Process`/`DateTime.UtcNow`; it should house 4 new
   adapters (`IAtomicFileWriter`, `IFileWindowReader`, `IFileStampReader`, `IProcessFactory`) and drop
   `IProcessExecutor` in favour of Contracts' `IProcessRunner`.
6. Add the missing four test projects (Kernel, Contracts, Core.Docs, Core.Squad) as D5 already requires;
   Core.Squad currently has no source to test, so it should either be filled or deleted.

## Limits of this inventory

- Findings are line-level and mechanical: a constructor whose parameter list is split across many lines was parsed
  by joining continuation lines, and primary constructors were included, but a dependency passed only through a
  method body (a local variable holding a concrete type) is not counted.
- `D` judgements for `Path.GetFullPath` distinguish inputs already proven fully-qualified (no disk/CWD access —
  `PathContainment`) from inputs that can be relative (CWD-dependent — everything else).
- Exception construction and local builder types were reviewed and classed D; they are listed, not silently
  dropped.
- Nothing was built, run or tested. All evidence is source reading plus the mechanical scan described above.
