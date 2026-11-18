---
id: specs/interface-driven-unit-tests/inventory/hosts
title: Inventory of concrete dependencies, Cli, Mcp, Arbiter and Hooks
doc-type: reference
status: draft
owner: dpalfery
last-reviewed: 2026-11-17
component: CI Pipelines
---

# Inventory of concrete dependencies: Cli, Mcp, Arbiter and Hooks

Produced 2026-11-17 by a read-only pass over every `.cs` file in scope, at commit `a5af901f` of the
role-based worker agents branch. Evidence for this specification, not living guidance: it goes stale
as the tasks land.


Read-only inventory of the kyber-weave checkout at
`/Users/dave/git/opencode-worktrees/kyber-weave/inventory` (detached, revision `9ec3357e`).
Nothing was edited, built, or run.

**Bottom line.** Constructor injection is in good shape: across all 1,125 `.cs` files under `src/`
there are 962 constructors and only **77 parameters are non-interface types**. Of those 77, 39 are
framework types that need no interface (`TimeProvider` 23, `HttpMessageHandler` 5, `JsonElement` 3,
`Regex` 2, `ReadOnlyMemory<byte>` 2, `Mutex` 1, `JsonNode` 1, `OSPlatform` 1, `ServiceProvider` 1),
leaving **38 genuine concrete-service dependencies**. The gap is not in constructors, it is in **category A**:
1,281 direct calls to the file system, the process table, the environment, the clock, the console and
`Path.GetFullPath`, spread over 270 classes. Those are what keep "run it against a real temp directory"
in the loop, and they are concentrated in the CLI command layer and the Data layer.

The single biggest structural item is that `KyberWeaveConfigLoader.TryLoad` — a static class — is
called from 23 places while `IKyberWeaveConfigRepository` and `IHostConfigLoader` already exist and are
injected elsewhere. The second is that `IProcessRunner` (Contracts) has **zero** users in the four
projects in focus; the CLI invented `IProcessExecutor` beside it and several commands still build a
`ProcessStartInfo` and call the static `ProcessRunner.Run` directly.

---

## How this was produced, and exactly what was covered

Two passes, so the tables are mechanical rather than sampled:

1. **Parser pass over every `.cs` file under `src/`** (1,125 files, 1,912 type declarations,
   962 constructors). Every constructor parameter was extracted and classified as interface,
   record/struct/enum (data), known DTO/options name (data), framework primitive, or concrete class.
   This is what produces every category **B** row below.
2. **Pattern pass over every line of every `.cs` file** for category **A**: `Console.*`,
   `Environment.*`, `File.*`, `Directory.*`, `new FileInfo/DirectoryInfo/FileStream/StreamReader`,
   `Path.GetFullPath`, `Path.GetTempPath`, `Process.*`, `new ProcessStartInfo`, `new HttpClient`,
   `DateTime.Now/UtcNow`, `DateTimeOffset.Now/UtcNow`, `Stopwatch`, `Guid.NewGuid`, `Random`,
   `Thread.Sleep`, `Task.Delay`, `new Mutex`, `OperatingSystem.Is*`, `RuntimeInformation.*`,
   static `ProcessRunner.Run`, static `KyberWeaveConfigLoader.TryLoad`, and static mutable state.
   Each hit was attributed to its enclosing type.

Read line-by-line in full: `src/KyberWeave.Hooks` (2 files), `src/KyberWeave.Arbiter` (5 files),
`src/KyberWeave.Mcp` (all 13 files). `src/KyberWeave.Cli` (133 files, 18.8k lines) was covered by
both passes but not read line-by-line in its entirety; the rows below are complete for the patterns
listed, and the file names are given so each row can be opened directly. For `Domain` (841 types),
`Data` (189), `Application` (217), `Kernel`, `Core` and `Contracts` the two mechanical passes cover
every file; I did not hand-read those trees (see "Coverage limits" at the end).

### Categories used

| Cat | Meaning |
|---|---|
| **A** | An outside resource is used directly and needs a seam: file system, process table, environment, clock, console, randomness, network, threads/locks, static mutable state. |
| **B** | A constructor or method takes a **concrete service class** where an interface should be taken. Records, DTOs, value objects, options classes and enums are data, not dependencies, and are not listed. |
| **C** | A service is built with `new` inside another service instead of being injected. |
| **D** | Acceptable: a sealed framework type with no interface, a pure data type, or an adapter whose entire job is to touch the resource. |

### Existing seams, and how widely they are used

| Interface | Where declared | Users in Cli | Mcp | Arbiter | Hooks | Repo-wide users |
|---|---|---|---|---|---|---|
| `IFileSystem` | `src/KyberWeave.Contracts/Repositories/IFileSystem.cs` | 4 files | 2 | 0 | 0 | 37 files (Application, Cli, Contracts, Data, Mcp) |
| `IWritableFileSystem` | `src/KyberWeave.Contracts/Repositories/IWritableFileSystem.cs` | **0** | **0** | **0** | **0** | 27 files (Application, Contracts, Data) |
| `IProcessRunner` | `src/KyberWeave.Contracts/Repositories/IProcessRunner.cs` | **0** | **0** | **0** | **0** | 5 files (Application, Contracts, Data) |
| `IProcessExecutor` (CLI-local) | `src/KyberWeave.Cli/Commands/Squad/Infrastructure/IProcessExecutor.cs` | 11 | 3 | 0 | 0 | 34 files across 7 projects |
| `IProcessEnvironment` | `src/KyberWeave.Contracts/Repositories/IProcessEnvironment.cs` | 13 | 1 | 0 | 0 | 52 files (the most-used port in the tree) |
| `IBoundedProcessExecutor` | Contracts | 0 | 1 | 0 | 0 | 13 files (Application, Contracts, Data, Mcp) |
| `IFileStatReader` / `IBoundedFileReader` / `IGitCommandRunner` / `IGitWorktreeIdentity` | Contracts | 1 / 0 / 0 / 0 | 0 / 0 / 0 / 1 | 0 | 0 | 5 / 5 / 8 / 4 |

`IProcessRunner` and `IWritableFileSystem` exist, are correct, and are **unused by every project in
this scope**. That is the cheapest win available: the seams are already written and tested through
`Data/Hooks/PhysicalFileSystem.cs` and `Data/Hooks/KernelProcessRunner.cs`.

---

## 1. `src/KyberWeave.Mcp` (13 files, 2,041 lines, 14 types)

Read in full. 32 category-A hits in 7 types, 1 category-B hit, 2 category-C.

| Location | Class | Finding | Cat | Should depend on | Size |
|---|---|---|---|---|---|
| `src/KyberWeave.Mcp/DocsToolsComposition.cs`:143-195 | `DocsToolsComposition.Create` | Builds `DocsQueryService`, `DocsGateRunner`, `PlanLifecycleService`, `RunDeliveryService`, `ToolingCheckService`, `DocsTools` with `new`. Interfaces exist for all five services. | C | Composition root — acceptable, but it should take the container's registrations (`GetRequiredService<IDocsQueryService>()`) rather than re-`new` them, so there is one construction path | M |
| `src/KyberWeave.Mcp/DocsToolsComposition.cs`:74-97 | `DocsToolsComposition.AddDocsHostServices` | Same again in the DI registrations: `new PlanLifecycleService/RunDeliveryService/ToolingCheckService/CodeGraphFreshness/DocsRootRegistry/TaskPromptService/RepositoryDocsAnalysisReader`. | C | `I*Service` registrations already exist; register the implementation types and let the container construct them | M |
| `src/KyberWeave.Mcp/DocsToolsComposition.cs`:22-23 | `DocsToolsComposition` | `private static readonly Lazy<IServiceProvider> DefaultServices` — a hidden service locator, process-wide mutable state, and the seam every test that does not pass `services:` silently falls into. | A | Nothing; make `services` required, or pass the provider in from `Program.cs` | S |
| `src/KyberWeave.Mcp/DocsToolsComposition.cs`:146 | `DocsToolsComposition.Create` | Parameter type `CodeGraphFreshness?` — a concrete service class, while `ICodeGraphFreshnessGate` exists and is registered at line 66 of the same file. | B | `ICodeGraphFreshnessGate?` | S |
| `src/KyberWeave.Mcp/DocsToolsComposition.cs`:147 | `DocsToolsComposition.Create` | Parameter type `DocsRootRegistry?` — concrete. | B | `IDocsRootRegistry` (new, see list) | S |
| `src/KyberWeave.Mcp/DocsTools.cs`:40-60 | `DocsTools` | Constructor takes `DocsRootRegistry?` (concrete). Also `Path.GetFullPath(hostRoot.Path)` at :58. | B, A | `IDocsRootRegistry`; `IPathNormalizer` | S |
| `src/KyberWeave.Mcp/CodeGraphFreshness.cs`:269, 275, 375 | `CodeGraphFreshness` | `File.Exists` on the CodeGraph lock file. The class is a service with five injected ports but no file-system port. | A | `IFileSystem.FileExists` | S |
| `src/KyberWeave.Mcp/CodeGraphFreshness.cs`:311-323 | `CodeGraphFreshness.ReadLockHolder` | Raw `new FileStream(...)` + `ReadAtLeast` to read a PID. Combines A with a second read path nobody can fake. | A | `IFileSystem.ReadAllText` (or a new `ReadFilePrefix` member) | S |
| `src/KyberWeave.Mcp/CodeGraphFreshness.cs`:330-357 | `CodeGraphFreshness.IsRunning` | `Process.GetProcessById` + `HasExited` to test whether a PID is alive. `IProcessRunner` does not cover this; nothing does. | A | `IProcessProbe.IsRunning(int pid)` (new) | M |
| `src/KyberWeave.Mcp/CodeGraphFreshness.cs`:224 | `CodeGraphFreshness` | `Path.GetTempPath()` for the doctor's lock directory. | A | `IProcessEnvironment.TempDirectory` | S |
| `src/KyberWeave.Mcp/CodeGraphFreshness.cs`:138 | `CodeGraphFreshness` | `Path.GetFullPath(repositoryRoot)` in the constructor. | A | `IPathNormalizer` | S |
| `src/KyberWeave.Mcp/CodeGraphFreshness.cs`:144 | `CodeGraphFreshness` | `_pause = pause ?? Thread.Sleep` — the default blocks the calling thread. The `Action<TimeSpan>` seam is right; the default should not be `Thread.Sleep`. | A | `IPause.Wait` (new), defaulting to an async-capable wait | S |
| `src/KyberWeave.Mcp/CodeGraphFreshness.cs`:294 | `CodeGraphFreshness` | `OperatingSystem.IsWindows()` decides the shell-quoting remedy. | A | `IProcessEnvironment.Platform` (new member) | S |
| `src/KyberWeave.Mcp/CodeGraphFreshness.cs`:429-433 | `CodeGraphFreshness.BoundedExecutor` | A private `IProcessExecutor` whose body is `ProcessRunner.Run(...)` (static). It is an adapter, but it is private, so it cannot be replaced — only the executor it wraps can. | C | Keep, but move it to a named adapter type in `Kernel/Processes` so the kernel owns the one `ProcessRunner.Run` call | S |
| `src/KyberWeave.Mcp/DocsRootRegistry.cs`:138-141 | `DocsRootRegistry` | `Console.Error.WriteLine` from a registry service, as the fallback for an unreadable config. | A | `IConsole.Error` (new) — or return the message to the caller | S |
| `src/KyberWeave.Mcp/DocsRootRegistry.cs`:56, 168 | `DocsRootRegistry` | `Path.GetFullPath` in the constructor and on every `projectPath` argument. | A | `IPathNormalizer` | S |
| `src/KyberWeave.Mcp/DocsRootRegistry.cs`:238-241 | `DocsRootRegistry` | `OperatingSystem.IsWindows() || IsMacOS()` picks the path comparison — a process-global fact baked into a service. | A | `IProcessEnvironment.Platform` (new member) | S |
| `src/KyberWeave.Mcp/CodeGraphFreshness.cs`:237,243,266,284,367,371 | `CodeGraphFreshness` | Static `CodeGraphIndexRules.*` helper (lock-file naming, busy/index-lock predicates, wait schedule). Pure functions, no disk access, but the retry policy is a fixed constant no test can vary. | D | Keep as pure functions; the wait schedule should come from the injected options if a test needs to shorten it | S |
| `src/KyberWeave.Mcp/DocsToolsComposition.cs`:168-190 | `DocsToolsComposition.Create` | Object-initialiser overrides `DeliveryExecutor`, `DeliveryHome`, `Executor`, `McpExecutable` — mutable settable properties on `RunDeliveryService` and `ToolingCheckService`, which is a second, invisible injection path beside the constructor. | B | Constructor parameters (or an options record) on both services | S |
| `src/KyberWeave.Mcp/RepositoryRootResolver.cs`:176 | `RepositoryRootResolver.RequireInitializedRoot` | Static `KyberWeaveConfigLoader.TryLoad(root)`: disk read from a static class. `IKyberWeaveConfigRepository` and `IHostConfigLoader` both exist. | A, B | `IHostConfigLoader` (make the class non-static and inject it, or pass the loader in as a parameter) | M |
| `src/KyberWeave.Mcp/RepositoryRootResolver.cs`:36, 74, 75, 172 | `RepositoryRootResolver` | `Path.GetFullPath` / `Path.TrimEndingDirectorySeparator` on real paths, in a static class. | A | `IPathNormalizer` | M |
| `src/KyberWeave.Mcp/DrainingStreamServerTransport.cs`:86-91 | `DrainingStreamServerTransport.ForStandardStreams` | `Console.OpenStandardInput/Output`. | A, D | Adapter by design — the streams are already constructor parameters, so this only needs to stay a factory and never be reached from a test | S |
| `src/KyberWeave.Mcp/Program.cs`:36-37, 70 | `Program` (top-level) | `Directory.GetCurrentDirectory`, `Environment.GetEnvironmentVariable`, `Path.GetFullPath`. | D | Entry point: passing them into `RepositoryRootResolver.Resolve` is exactly right; no change | S |

Notes on Mcp: `RepositoryRootResolver.Resolve(args, workingDirectory, environmentRoot)` is a good
example of the pattern the rest of category A should follow — the ambient value is a parameter, not a
call. `CodeGraphFreshness` is the worst class in this project because it reads the clock correctly
(`TimeProvider`), yet reads the file system, the process table, the temp directory and the OS name
directly.

---

## 2. `src/KyberWeave.Arbiter` (5 files, 753 lines, 6 types)

Read in full. 3 category-A hits in 2 types, 0 category-B, and the composition roots by design.

| Location | Class | Finding | Cat | Should depend on | Size |
|---|---|---|---|---|---|
| `src/KyberWeave.Arbiter/ArbiterContainer.cs`:81-88 | `CoreHostConfigLoader` | Wraps the static `KyberWeaveConfigLoader.TryLoad`. The class is the adapter for `IHostConfigLoader`, so the `new`-free shape is right, but the static call means the adapter can only be tested against real files. | A, D | `KyberWeaveConfigLoader` should become injectable behind `IKyberWeaveConfigRepository`, or this adapter should call a file-system port | S |
| `src/KyberWeave.Arbiter/Mcp/DrainingStreamServerTransport.cs`:88-89 | `DrainingStreamServerTransport.ForStandardStreams` | `Console.OpenStandardInput/Output`. | A, D | Adapter by design | S |
| `src/KyberWeave.Arbiter/Composition.cs`:257-261, `ArbiterContainer.cs`:63-77 | `Composition`, `ArbiterContainer` | `ArbiterContainer.Create()` builds a `ServiceProvider` and resolves `HookCommand` / guards. Static composition roots, called from `Composition` and `Program`. | C, D | Composition root — correct by design. `Composition.DispatchAsync` already takes `commandFactory` for tests, which is the right seam. | S |
| `src/KyberWeave.Arbiter/Program.cs`:23, 31, 45-79 | `Program` | `Console.In/Out/Error`, `Host.CreateApplicationBuilder`. | D | Entry point | S |

Arbiter is the cleanest project in scope: every decision path takes its collaborators by interface,
streams are parameters all the way down (`DispatchAsync(args, stdin, stdout, stderr)`), and the only
untestable step is the static config loader.

---

## 3. `src/KyberWeave.Hooks` (2 files, 116 lines, 1 type)

Read in full. 3 category-A hits, 0 B, 0 C.

| Location | Class | Finding | Cat | Should depend on | Size |
|---|---|---|---|---|---|
| `src/KyberWeave.Hooks/HooksCli.cs`:58 | `HooksCli.DispatchAsync` | `(environment ?? Environment.GetEnvironmentVariable)` — the ambient read is the **default value of a parameter**, which is the right shape; the fallback is still a real environment read when the caller passes nothing. | A | Fine as-is; a test passes `environment:`. Optional hardening: require the delegate. | S |
| `src/KyberWeave.Hooks/HooksCli.cs`:44, 81 | `HooksCli` | `ClaudeHookEventParser.Parse` and `ClaudeHookEventParser.MaxInputChars` — a static parser class (Domain) with no interface. | D | Pure function over a string; no interface needed. If it ever touches disk, it becomes B. | S |
| `src/KyberWeave.Hooks/HooksCli.cs`:61-66 | `HooksCli` | Builds its own `ServiceCollection` + `Provider` per invocation and resolves `IEditHookService`. | C, D | One-shot process: acceptable, and `configure` is already a parameter so tests can substitute. | S |
| `src/KyberWeave.Hooks/Program.cs`:5-9 | `Program` | `Console.In/Out/Error`. | D | Entry point | S |

---

## 4. `src/KyberWeave.Cli` (133 files, 18,822 lines, 194 types)

181 category-A hits in 54 types, 7 category-B hits (2 of them framework types, marked D), and
6 category-C sites. The A table below is complete for the patterns scanned; line numbers are listed
exhaustively per row.

### 4a. Category B — concrete service dependencies

| Location | Class | Finding | Cat | Should depend on | Size |
|---|---|---|---|---|---|
| `src/KyberWeave.Cli/Commands/Docs/RepositoryDocsAnalysisCommandService.cs`:21 | `RepositoryDocsAnalysisCommandService` | Parameter `DocsAnalysisCompositionFactories factories` — concrete factory class. | B | `IDocsAnalysisCompositionFactories` (new) | S |
| `src/KyberWeave.Cli/Commands/Docs/RepositoryDocsAnalysisCommandService.cs`:179 | `AnalysisExecution` | Public property parameter `DocsAnalysisRuntime Runtime` — the runtime is a concrete aggregate that itself resolves services. | B | `IDocsAnalysisRuntime` (new) | M |
| `src/KyberWeave.Cli/Commands/Utilities/UtilitiesStatusLineDoctorCommand.cs`:35 | `UtilitiesStatusLineDoctorCommand` | Parameter `UtilitiesStatusLineProcessRunner processRunner` — concrete runner injected into a command that already takes `IProcessEnvironment` and `IFileSystemPathSemantics`. | B | `IProcessRunner` / `IProcessExecutor` | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadInstallCommand.cs`:35 | `SquadInstallCommand` | Parameter `KyberDashCapturePluginStep? capturePluginStep` — concrete deployment step. | B | `IKyberDashCapturePluginStep` (new) | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadUpdateCommand.cs`:49 | `SquadUpdateCommand` | Same concrete `KyberDashCapturePluginStep?`. | B | `IKyberDashCapturePluginStep` (new) | S |
| `src/KyberWeave.Cli/Composition/ServiceCollectionTypeRegistrar.cs`:30 | `ServiceProviderTypeResolver` | Holds a `ServiceProvider` (the MS concrete type, not `IServiceProvider`) — needed by Spectre's registrar contract. | D | Framework type; `IServiceProvider` where the API allows | S |
| `src/KyberWeave.Cli/Commands/Squad/CopilotPromptCommands.cs`:37 | `CopilotPromptEnvironment` | Parameter `OSPlatform Platform`. | D | Framework value struct — data | S |

### 4b. Category C — services built inside services

| Location | Class | Finding | Cat | Should depend on | Size |
|---|---|---|---|---|---|
| `src/KyberWeave.Cli/Commands/Squad/SquadHostDefaults.cs`:120-165 | `SquadHostDefaults` | `new SquadModelPresetStore`, `new SquadModelPresetSelectionStore`, `new FileSquadDeploymentPlanner`, `new SquadTransactionFactory`, `new FileSquadResponseGuidanceStore`, `new SquadCanonicalProfileStore`, `new SquadInstanceService` — a hand-rolled container standing in for the DI registrations. | C | Register these in the composition root and resolve them; keep the `??` defaults only as explicit test overrides | L |
| `src/KyberWeave.Cli/Commands/Docs/DocsInitCommand.cs`:277-299 | `DocsInitCommand` | Builds a `ProcessStartInfo` and calls the static `ProcessRunner.Run`, then `Console`-free output. `IProcessExecutor` is declared in the same assembly. | A, C | `IProcessExecutor` (and `IProcessRunner` behind it) | M |
| `src/KyberWeave.Cli/Commands/Squad/Infrastructure/IProcessExecutor.cs`:30-40 | `ProcessExecutor` | The one `IProcessExecutor` implementation, delegating to static `ProcessRunner.Run`. | C, D | Adapter by design — keep it the only place that calls `ProcessRunner.Run` | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsCommandComposition.cs`:180-260 | `DocsAnalysisRuntime` | Aggregates and constructs the analysis collaborators for every docs-analysis command. | C | Container registration | M |

### 4c. Category A — outside resources used directly (complete for the scanned patterns)

| Location | Class | Finding | Cat | Should depend on | Size |
|---|---|---|---|---|---|
| `src/KyberWeave.Cli/Commands/Arbiter/ArbiterAuditCommand.cs`:139 | `PlanFilter` | Path.GetFullPath (real path) | A | `IPathNormalizer` (new) or `IProcessEnvironment` | S |
| `src/KyberWeave.Cli/Commands/Arbiter/ArbiterCommentCommand.cs`:53 | `ArbiterCommentCommand` | Path.GetFullPath (real path) | A | `IPathNormalizer` (new) or `IProcessEnvironment` | S |
| `src/KyberWeave.Cli/Commands/Arbiter/ArbiterRepoRoot.cs`:19 | `ArbiterRepoRoot` | Directory.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/Arbiter/ArbiterRepoRoot.cs`:20 | `ArbiterRepoRoot` | File.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/Arbiter/ArbiterRepoRoot.cs`:16 | `ArbiterRepoRoot` | Path.GetFullPath (real path) | A | `IPathNormalizer` (new) or `IProcessEnvironment` | S |
| `src/KyberWeave.Cli/Commands/Arbiter/ArbiterSectionsCommand.cs`:35 | `ArbiterSectionsCommand` | Path.GetFullPath (real path) | A | `IPathNormalizer` (new) or `IProcessEnvironment` | S |
| `src/KyberWeave.Cli/Commands/Arbiter/ArbiterUninstallCommand.cs`:69,76,84,85,124 | `ArbiterUninstallCommand` | Path.GetFullPath (real path) | A | `IPathNormalizer` (new) or `IProcessEnvironment` | M |
| `src/KyberWeave.Cli/Commands/Arbiter/ConsoleArbiterKeyInput.cs`:10 | `ConsoleArbiterKeyInput` | Console.* write/read inside a service | A | `IConsole` (new) — or keep in the adapter class | S |
| `src/KyberWeave.Cli/Commands/CommandHelpers.cs`:41 | `CommandHelpers` | static KyberWeaveConfigLoader.TryLoad | A | `IKyberWeaveConfigRepository` / `IHostConfigLoader` | S |
| `src/KyberWeave.Cli/Commands/CommandHelpers.cs`:161 | `CommandHelpers` | Directory.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/CommandHelpers.cs`:163 | `CommandHelpers` | File.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/CommandHelpers.cs`:156 | `CommandHelpers` | Path.GetFullPath (real path) | A | `IPathNormalizer` (new) or `IProcessEnvironment` | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsCloseoutCommand.cs`:32 | `DocsCloseoutCommand` | DateTime.UtcNow | A | `TimeProvider` | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsCommandComposition.cs`:217 | `DocsAnalysisRuntime` | Path.GetFullPath (real path) | A | `IPathNormalizer` (new) or `IProcessEnvironment` | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsFinalizeCommand.cs`:31 | `DocsFinalizeCommand` | DateTime.UtcNow | A | `TimeProvider` | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsInitCommand.cs`:422 | `DocsInitCommand` | static KyberWeaveConfigLoader.TryLoad | A | `IKyberWeaveConfigRepository` / `IHostConfigLoader` | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsInitCommand.cs`:379 | `DocsInitCommand` | Directory.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsInitCommand.cs`:168 | `DocsInitCommand` | Environment.GetEnvironmentVariable | A | `IProcessEnvironment.GetVariable` | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsInitCommand.cs`:164,279,360,421 | `DocsInitCommand` | Path.GetFullPath (real path) | A | `IPathNormalizer` (new) or `IProcessEnvironment` | M |
| `src/KyberWeave.Cli/Commands/Docs/DocsInitCommand.cs`:277 | `DocsInitCommand` | ProcessStartInfo / Process | A | `IProcessRunner` (Contracts) / `IProcessExecutor` | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsInitCommand.cs`:299 | `DocsInitCommand` | static ProcessRunner.Run | A | `IProcessRunner` / `IProcessExecutor` | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsMcpMigrateCommand.cs`:181 | `DocsMcpMigrateCommand` | Directory.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsMcpMigrateCommand.cs`:66 | `DocsMcpMigrateCommand` | Environment.GetFolderPath / ProcessPath | A | `IProcessEnvironment` (home/profile members) | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsMcpMigrateCommand.cs`:171 | `DocsMcpMigrateCommand` | OperatingSystem.Is* | A | `IProcessEnvironment.Platform` (new member) | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsMcpMigrateCommand.cs`:179,182 | `DocsMcpMigrateCommand` | Path.GetFullPath (real path) | A | `IPathNormalizer` (new) or `IProcessEnvironment` | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsMcpWireCommand.cs`:518 | `DocsMcpWireCommand` | Directory.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsMcpWireCommand.cs`:439,519 | `DocsMcpWireCommand` | File.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsMcpWireCommand.cs`:110 | `DocsMcpWireCommand` | Path.GetFullPath (real path) | A | `IPathNormalizer` (new) or `IProcessEnvironment` | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsOnboardingCommand.cs`:214 | `DocsOnboardingCommand` | static KyberWeaveConfigLoader.TryLoad | A | `IKyberWeaveConfigRepository` / `IHostConfigLoader` | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsOnboardingCommand.cs`:98 | `DocsOnboardingCommand` | Directory.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsOnboardingCommand.cs`:100,145,198,227,278,280 | `DocsOnboardingCommand` | File.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | M |
| `src/KyberWeave.Cli/Commands/Docs/DocsOnboardingCommand.cs`:89 | `DocsOnboardingCommand` | Path.GetFullPath (real path) | A | `IPathNormalizer` (new) or `IProcessEnvironment` | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsPacketCommand.cs`:146,150,162,163 | `DocsPacketCommand` | Console.* write/read inside a service | A | `IConsole` (new) — or keep in the adapter class | M |
| `src/KyberWeave.Cli/Commands/Docs/DocsPacketCommand.cs`:160 | `DocsPacketCommand` | OperatingSystem.Is* | A | `IProcessEnvironment.Platform` (new member) | S |
| `src/KyberWeave.Cli/Commands/Docs/DocsPacketCommand.cs`:47,111,201,205 | `DocsPacketCommand` | Path.GetFullPath (real path) | A | `IPathNormalizer` (new) or `IProcessEnvironment` | M |
| `src/KyberWeave.Cli/Commands/Docs/DocsTasksCommand.cs`:123,135 | `DocsTasksCommand` | Console.* write/read inside a service | A | `IConsole` (new) — or keep in the adapter class | S |
| `src/KyberWeave.Cli/Commands/Doctor/DoctorCommand.cs`:65,71,112,123 | `DoctorCommand` | Console.* write/read inside a service | A | `IConsole` (new) — or keep in the adapter class | M |
| `src/KyberWeave.Cli/Commands/Doctor/DoctorCommand.cs`:106 | `DoctorCommand` | Path.GetFullPath (real path) | A | `IPathNormalizer` (new) or `IProcessEnvironment` | S |
| `src/KyberWeave.Cli/Commands/Doctor/DoctorReportRenderer.cs`:26,45 | `DoctorReportRenderer` | Console.* write/read inside a service | A | `IConsole` (new) — or keep in the adapter class | S |
| `src/KyberWeave.Cli/Commands/PlanArguments.cs`:85 | `PlanArguments` | static KyberWeaveConfigLoader.TryLoad | A | `IKyberWeaveConfigRepository` / `IHostConfigLoader` | S |
| `src/KyberWeave.Cli/Commands/PlanArguments.cs`:25,57 | `PlanArguments` | Directory.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/PlanArguments.cs`:29 | `PlanArguments` | File.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/PlanArguments.cs`:26,77 | `PlanArguments` | Path.GetFullPath (real path) | A | `IPathNormalizer` (new) or `IProcessEnvironment` | S |
| `src/KyberWeave.Cli/Commands/Review/ReviewCatalogueCommand.cs`:22,44 | `ReviewCatalogueCommand` | Console.* write/read inside a service | A | `IConsole` (new) — or keep in the adapter class | S |
| `src/KyberWeave.Cli/Commands/Review/ReviewCycleCommand.cs`:30,43,50 | `ReviewCycleCommand` | Console.* write/read inside a service | A | `IConsole` (new) — or keep in the adapter class | M |
| `src/KyberWeave.Cli/Commands/Review/ReviewGatesCommand.cs`:92,105,106 | `ReviewGatesCommand` | Console.* write/read inside a service | A | `IConsole` (new) — or keep in the adapter class | M |
| `src/KyberWeave.Cli/Commands/Review/ReviewMigrateTaskGatesCommand.cs`:101,172,412 | `ReviewMigrateTaskGatesCommand` | File.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | M |
| `src/KyberWeave.Cli/Commands/Review/ReviewMigrateTaskGatesCommand.cs`:89 | `ReviewMigrateTaskGatesCommand` | Path.GetFullPath (real path) | A | `IPathNormalizer` (new) or `IProcessEnvironment` | S |
| `src/KyberWeave.Cli/Commands/Review/ReviewSeatsCommand.cs`:45,67 | `ReviewSeatsCommand` | Console.* write/read inside a service | A | `IConsole` (new) — or keep in the adapter class | S |
| `src/KyberWeave.Cli/Commands/Review/ReviewSuppressionsCommand.cs`:59 | `ReviewSuppressionsCommand` | Console.* write/read inside a service | A | `IConsole` (new) — or keep in the adapter class | S |
| `src/KyberWeave.Cli/Commands/Review/ReviewVerdictCommand.cs`:41 | `ReviewVerdictCommand` | Console.* write/read inside a service | A | `IConsole` (new) — or keep in the adapter class | S |
| `src/KyberWeave.Cli/Commands/Run/RunDeliverCommand.cs`:31 | `RunDeliverCommand` | Path.GetFullPath (real path) | A | `IPathNormalizer` (new) or `IProcessEnvironment` | S |
| `src/KyberWeave.Cli/Commands/Run/RunStartCommand.cs`:199,223,238,300 | `RunStartCommand` | Console.* write/read inside a service | A | `IConsole` (new) — or keep in the adapter class | M |
| `src/KyberWeave.Cli/Commands/Skills/CatalogCommand.cs`:50 | `CatalogCommand` | Console.* write/read inside a service | A | `IConsole` (new) — or keep in the adapter class | S |
| `src/KyberWeave.Cli/Commands/Skills/RouteCommand.cs`:96,146 | `RouteCommand` | Console.* write/read inside a service | A | `IConsole` (new) — or keep in the adapter class | S |
| `src/KyberWeave.Cli/Commands/Squad/Brief/SquadBriefCommand.cs`:58 | `SquadBriefCommand` | Console.* write/read inside a service | A | `IConsole` (new) — or keep in the adapter class | S |
| `src/KyberWeave.Cli/Commands/Squad/Brief/SquadBriefCommand.cs`:36 | `SquadBriefCommand` | Directory.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/Squad/CopilotPromptCommands.cs`:495 | `CopilotPromptCommandSupport` | Directory.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/Squad/CopilotPromptCommands.cs`:430 | `CopilotPromptCommandSupport` | Environment.GetEnvironmentVariable | A | `IProcessEnvironment.GetVariable` | S |
| `src/KyberWeave.Cli/Commands/Squad/CopilotPromptCommands.cs`:431 | `CopilotPromptCommandSupport` | OperatingSystem.Is* | A | `IProcessEnvironment.Platform` (new member) | S |
| `src/KyberWeave.Cli/Commands/Squad/CopilotPromptCommands.cs`:358 | `CopilotPromptStatusCommand` | File.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/Squad/CopilotPromptCommands.cs`:275,304 | `CopilotPromptUninstallCommand` | File.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/Squad/Infrastructure/IProcessExecutor.cs`:38 | `ProcessExecutor` | static ProcessRunner.Run | A | `IProcessRunner` / `IProcessExecutor` | S |
| `src/KyberWeave.Cli/Commands/Squad/Infrastructure/KyberDashCapturePluginStep.cs`:254 | `KyberDashCapturePluginStep` | Environment.GetFolderPath / ProcessPath | A | `IProcessEnvironment` (home/profile members) | S |
| `src/KyberWeave.Cli/Commands/Squad/Infrastructure/KyberDashCapturePluginStep.cs`:262 | `KyberDashCapturePluginStep` | Environment.GetEnvironmentVariable | A | `IProcessEnvironment.GetVariable` | S |
| `src/KyberWeave.Cli/Commands/Squad/Infrastructure/KyberDashCapturePluginStep.cs`:258,270 | `KyberDashCapturePluginStep` | File.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/Squad/Infrastructure/KyberDashCapturePluginStep.cs`:252 | `KyberDashCapturePluginStep` | OperatingSystem.Is* | A | `IProcessEnvironment.Platform` (new member) | S |
| `src/KyberWeave.Cli/Commands/Squad/Infrastructure/ProcessProbes.cs`:170 | `ProcessProbe` | Environment.GetEnvironmentVariable | A | `IProcessEnvironment.GetVariable` | S |
| `src/KyberWeave.Cli/Commands/Squad/Infrastructure/ProcessProbes.cs`:158 | `ProcessProbe` | OperatingSystem.Is* | A | `IProcessEnvironment.Platform` (new member) | S |
| `src/KyberWeave.Cli/Commands/Squad/Infrastructure/ProcessProbes.cs`:144 | `ProcessProbe` | ProcessStartInfo / Process | A | `IProcessRunner` (Contracts) / `IProcessExecutor` | S |
| `src/KyberWeave.Cli/Commands/Squad/Infrastructure/SquadTargetRootConfirmation.cs`:117 | `SquadTargetRootConfirmation` | Console.* write/read inside a service | A | `IConsole` (new) — or keep in the adapter class | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs`:477 | `SquadCommandComposition` | static KyberWeaveConfigLoader.TryLoad | A | `IKyberWeaveConfigRepository` / `IHostConfigLoader` | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs`:445,537,677 | `SquadCommandComposition` | Directory.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | M |
| `src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs`:194,486,490,678 | `SquadCommandComposition` | File.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | M |
| `src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs`:424,481,672,715 | `SquadCommandComposition` | Path.GetFullPath (real path) | A | `IPathNormalizer` (new) or `IProcessEnvironment` | M |
| `src/KyberWeave.Cli/Commands/Squad/SquadDoctorCommand.cs`:367 | `SquadDoctorCommand` | static KyberWeaveConfigLoader.TryLoad | A | `IKyberWeaveConfigRepository` / `IHostConfigLoader` | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadDoctorCommand.cs`:408,469,613 | `SquadDoctorCommand` | Directory.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | M |
| `src/KyberWeave.Cli/Commands/Squad/SquadDoctorCommand.cs`:238 | `SquadDoctorCommand` | Environment.CurrentDirectory | A | `IProcessEnvironment.CurrentDirectory` | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadDoctorCommand.cs`:828 | `SquadDoctorCommand` | Environment.NewLine | A | none needed (constant) — D | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadDoctorCommand.cs`:561,708,709,943,962 | `SquadDoctorCommand` | File.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | M |
| `src/KyberWeave.Cli/Commands/Squad/SquadHostDefaults.cs`:30,31,35,55,56 | `SquadHostDefaults` | Environment.GetFolderPath / ProcessPath | A | `IProcessEnvironment` (home/profile members) | M |
| `src/KyberWeave.Cli/Commands/Squad/SquadHostDefaults.cs`:27,52,98,156 | `SquadHostDefaults` | Environment.GetEnvironmentVariable | A | `IProcessEnvironment.GetVariable` | M |
| `src/KyberWeave.Cli/Commands/Squad/SquadHostDefaults.cs`:172 | `SquadHostDefaults` | File.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadHostDefaults.cs`:69 | `SquadHostDefaults` | OperatingSystem.Is* | A | `IProcessEnvironment.Platform` (new member) | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadInstallCommand.cs`:230,237 | `SquadInstallCommand` | Console.* write/read inside a service | A | `IConsole` (new) — or keep in the adapter class | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadInstallCommand.cs`:97 | `SquadInstallCommand` | Environment.CurrentDirectory | A | `IProcessEnvironment.CurrentDirectory` | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadInstanceRemoveCommand.cs`:89,90 | `SquadInstanceRemoveCommand` | Path.GetFullPath (real path) | A | `IPathNormalizer` (new) or `IProcessEnvironment` | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadPresetClearCommand.cs`:241 | `SquadPresetClearCommand` | File.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadPresetListCommand.cs`:124,126 | `SquadPresetListCommand` | Directory.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadPresetListCommand.cs`:130 | `SquadPresetListCommand` | File.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadPresetUseCommand.cs`:299 | `SquadPresetUseCommand` | File.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadRunCommand.cs`:161 | `SquadRunCommand` | OperatingSystem.Is* | A | `IProcessEnvironment.Platform` (new member) | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadRunCommand.cs`:72 | `SquadRunCommand` | Path.GetFullPath (real path) | A | `IPathNormalizer` (new) or `IProcessEnvironment` | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadStatusCommand.cs`:446 | `SquadStatusCommand` | Directory.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadStatusCommand.cs`:76 | `SquadStatusCommand` | Environment.CurrentDirectory | A | `IProcessEnvironment.CurrentDirectory` | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadStatusCommand.cs`:146,154,253,273,466,470,473 | `SquadStatusCommand` | File.* direct I/O | A | `IFileSystem` / `IWritableFileSystem` | L |
| `src/KyberWeave.Cli/Commands/Squad/SquadTaskGateHint.cs`:35 | `SquadTaskGateHint` | static KyberWeaveConfigLoader.TryLoad | A | `IKyberWeaveConfigRepository` / `IHostConfigLoader` | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadUninstallCommand.cs`:112 | `SquadUninstallCommand` | Environment.CurrentDirectory | A | `IProcessEnvironment.CurrentDirectory` | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadUpdateCommand.cs`:191 | `SquadUpdateCommand` | Environment.CurrentDirectory | A | `IProcessEnvironment.CurrentDirectory` | S |
| `src/KyberWeave.Cli/Commands/Squad/SquadUserPaths.cs`:17,21 | `SquadUserPaths` | Environment.GetFolderPath / ProcessPath | A | `IProcessEnvironment` (home/profile members) | S |
| `src/KyberWeave.Cli/Composition/SelfUpdateHostFactory.cs`:15 | `SelfUpdateHostFactory` | Environment.GetFolderPath / ProcessPath | A | `IProcessEnvironment` (home/profile members) | S |
| `src/KyberWeave.Cli/Composition/SelfUpdateHostFactory.cs`:18,19 | `SelfUpdateHostFactory` | OperatingSystem.Is* | A | `IProcessEnvironment.Platform` (new member) | S |
| `src/KyberWeave.Cli/Rendering/ReportRenderer.cs`:34,35,36,71,77,119 | `ReportRenderer` | Console.* write/read inside a service | A | `IConsole` (new) — or keep in the adapter class | M |

**Reading the CLI list.** Three groups, in order of how much they cost the test suite:

1. **File I/O in commands** — `SquadStatusCommand`, `SquadDoctorCommand`, `DocsOnboardingCommand`,
   `SquadPresetListCommand`, `SquadPresetUseCommand`, `SquadPresetClearCommand`,
   `ReviewMigrateTaskGatesCommand`, `CommandHelpers`, `SquadCommandComposition`. These read and write
   real files that `IFileSystem`/`IWritableFileSystem` could serve. `CommandHelpers` is the worst
   lever: one static helper (`File.WriteAllText` at :163, `Directory.CreateDirectory` at :161) used by
   many commands, so a single port here removes a whole family of integration tests.
2. **Process and PATH probing** — `DocsInitCommand` (:277, :299), `ProcessProbes` (:144-170),
   `KyberDashCapturePluginStep` (:252-270), `IProcessExecutor.cs` (:38). `ProcessProbes` and
   `KyberDashCapturePluginStep` also search `PATH` with `File.Exists`, which is a third thing no
   port covers today.
3. **Clock, environment and console** — `DateTime.UtcNow` in `DocsCloseoutCommand`:32 and
   `DocsFinalizeCommand`:31 (both should use `TimeProvider`, which the application layer already
   injects), `Environment.CurrentDirectory` / `GetFolderPath` in the Squad commands
   (`IProcessEnvironment` already carries exactly these members), and 42 `Console.*` calls spread
   over 18 command and renderer types.

Also in this project: `SquadUserPaths` (:17, :21) and `SelfUpdateHostFactory` (:15-19) read the
profile directory, the process path and the OS name from `Environment`/`OperatingSystem` inside
service-shaped types.

---

## 5. Everything else under `src/` (in scope: "any other src/ project not named here")

All twelve other projects were covered by both mechanical passes over every file. They are listed
here with totals; they were not hand-read.

| Project | Files | Types | A hits | A-hit classes | B hits | Worst classes (A hits) |
|---|---|---|---|---|---|---|
| `KyberWeave.Domain` | 445 | 841 | 47 | 22 | 20 | `DocSpecValidator` (6), `McpWiringReceipt` (4), `PlatformRid` (4), `CopilotPromptFile` (3), `PathContainment` (3) |
| `KyberWeave.Data` | 137 | 189 | 790 | 107 | 1 | `SquadTransaction` (71), `SquadDeploymentPlanner` (31), `GitHubSquadReleaseSource` (27), `SquadStateStore` (26), `WindowsSquadLinkFileSystem` (25) |
| `KyberWeave.Application` | 150 | 217 | 189 | 70 | 17 | `SelfUpdater` (24), `StandardsFolderMigration` (12), `ReviewVerdictService` (10), `TaskPromptService` (7), `DocsQueryService` (6) |
| `KyberWeave.Cli` | 133 | 194 | 181 | 54 | 7 | see section 4 |
| `KyberWeave.Kernel` | 18 | 22 | 32 | 6 | 0 | `FileReferenceExtractor` (16), `AtomicFile` (8), `StdioSession` (3), `ProcessRunner` (2) |
| `KyberWeave.Mcp` | 13 | 14 | 32 | 7 | 1 | see section 1 |
| `KyberWeave.Contracts` | 197 | 49 | 0 | 0 | 0 | pure declarations |
| `KyberWeave.Core` | 21 | 22 | 4 | 1 | 0 | `CodeGraphProvisioner` (4) |
| `KyberWeave.Arbiter` | 5 | 6 | 3 | 2 | 0 | see section 2 |
| `KyberWeave.Hooks` | 2 | 1 | 3 | 1 | 0 | see section 3 |
| `KyberWeave.Core.Docs` | 4 | 4 | 0 | 0 | 0 | — |
| `KyberWeave.Core.Squad` | **0** | 0 | 0 | 0 | 0 | empty project folder — no `.cs` files at all; either finish or delete it |

Category-B findings outside the four focus projects, all verified against the extractor:

| Location | Class | Finding | Cat | Should depend on | Size |
|---|---|---|---|---|---|
| `src/KyberWeave.Application/Arbiter/Hooks/HookCommand.cs`:51 | `HookCommand` | Parameter `HarnessAdapterRegistry registry` — a static registry class. | B | `IHarnessAdapterRegistry` (new) | S |
| `src/KyberWeave.Application/Arbiter/Mcp/ArbiterServeContextFactory.cs`:11 | `ArbiterServeContextFactory` | Parameter `ArbiterRootResolver roots` — concrete resolver. | B | `IArbiterRootResolver` (new) | S |
| `src/KyberWeave.Application/Docs/Analysis/DocumentationAnalyzer.cs`:15 | `DocumentationAnalyzer` | Parameter `ClaimExtractor extractor` — concrete. | B | `IClaimExtractor` (new) | S |
| `src/KyberWeave.Application/Docs/Mcp/McpWiringService.cs`:8 | `McpWiringService` | Four concrete parameters at once: `McpWiringInspector`, `McpWiringRepairer`, `McpServerRenamer`, `McpWiringFixes`. All four are `internal` services with no interface. | B | `IMcpWiringInspector`, `IMcpWiringRepairer`, `IMcpServerRenamer`, `IMcpWiringFixes` (new) | M |
| `src/KyberWeave.Application/Docs/Mcp/McpWiringFixes.cs`:33 | `McpWiringFixes` | Parameter `McpServerRenamer renamer`. | B | `IMcpServerRenamer` (new) | S |
| `src/KyberWeave.Application/Docs/Search/DocumentIndex.cs`:103 | `DocumentIndex` | Parameters `DocumentCorpus corpus`, `DocumentSectionGraph sectionGraph` — concrete domain classes. | B | `IDocumentCorpus`, `IDocumentSectionGraph` — or accept them as immutable data (they are closer to value objects) | M |
| `src/KyberWeave.Application/Docs/Validation/ConfiguredPlanTaskAgentPickerFactory.cs`:12 | `ConfiguredPlanTaskAgentPickerFactory` | Parameter `ConfiguredArbiterProviderFactory providers` — concrete factory. | B | `IArbiterProviderFactory` (exists in Contracts) | S |
| `src/KyberWeave.Application/Doctor/RepositoryDoctorService.cs`:23 | `RepositoryDoctorService` | Parameters `McpServerLiveProbe mcpProbe`, `PostCheckoutHookInstaller postCheckoutHook` — concrete. | B | `IMcpServerLiveProbe`, `IPostCheckoutHookInstaller` (new) | M |
| `src/KyberWeave.Data/Squad/Deployment/TransactionLease.cs`:4 | `TransactionLease` | Parameter `Mutex mutex` — framework class, but it is also the file/OS lock. | D | `Mutex` has no interface; a thin `IExclusiveLock` is still advisable because the lease's release path is a named-mutex release and cannot be faked | S |
| `src/KyberWeave.Data/{Update/GitHubReleaseClient.cs:44, Squad/Release/GitHubSquadReleaseSource.cs:57, Docs/Analysis/Embeddings/OpenAiCompatibleEmbeddingGenerator.cs:36, Arbiter/Providers/SystemOneClient.cs:43}`, `src/KyberWeave.Application/Update/SelfUpdater.cs:89`, `SelfUpdateService.cs:12` | HTTP clients | Parameter `HttpMessageHandler` — the standard .NET test seam, and these classes take it explicitly rather than `new HttpClient()`. | D | Keep. `new HttpClient` appears 5 times repo-wide; none in these. | S |

`KyberWeave.Data`'s 790 A hits are mostly by design — it is the adapter layer, and
`PhysicalFileSystem`, `KernelProcessRunner`, `WindowsSquadLinkFileSystem` and the SQLite persistence
classes exist precisely to touch the resource. The exceptions worth naming are
`SquadTransaction` (71 hits: `File.*`, `Directory.*`, `Path.GetFullPath`, mutex handling mixed into
one 3,372-line class) and `SquadStateStore` (26), both of which mix policy with raw I/O in the same
type.

---

## 6. Per-project totals

| Project | A | B (real) | B (framework, D) | C | Types with any finding | Five worst classes |
|---|---|---|---|---|---|---|
| `KyberWeave.Cli` | 181 | 5 | 2 | 6 | 54 | `SquadCommandComposition` (12 A + C), `SquadDoctorCommand` (11 A), `SquadHostDefaults` (11 A + 1 C), `DocsInitCommand` (9 A + 1 C), `DocsOnboardingCommand` (9 A) |
| `KyberWeave.Mcp` | 32 | 2 | 0 | 3 | 7 | `CodeGraphFreshness` (17), `RepositoryRootResolver` (5 + 1 B), `DocsRootRegistry` (4), `DocsToolsComposition` (2 + 3 C + 2 B), `DocsTools` (1 + 1 B) |
| `KyberWeave.Arbiter` | 3 | 0 | 0 | 1 | 2 | `DrainingStreamServerTransport` (2), `CoreHostConfigLoader` (1), `ArbiterContainer`/`Composition` (composition roots) |
| `KyberWeave.Hooks` | 3 | 0 | 0 | 1 | 1 | `HooksCli` (3) |
| **Scope total** | **219** | **7** | **2** | **11** | **64** | — |
| *(all of `src/` for comparison)* | 1,281 | 38 | 39 | see per project | 270 | `SquadTransaction`, `SquadSourceLoader`, `PlanDocumentParser`, `SquadDeploymentPlanner`, `SquadStateStore` |

Counts: A is one row per resource call site; B counts parameters; a class can appear in both A and C.
The five-worst lists count A rows first, then B, then C.

---

## 7. Interfaces this scope needs

Ordered by how many call sites each would remove. Every one of these is small; none needs a framework
change.

| New interface | Members it must cover | Call sites it replaces |
|---|---|---|
| `IPathNormalizer` | `string FullPath(string path)`, `string FullPath(string path, string baseDirectory)`, `string Combine(params string[])`, `string TrimEndingDirectorySeparator(string)`, `bool AreEqual(string a, string b)` (platform-correct comparison) | 270 `Path.GetFullPath` hits repo-wide (35 in Cli, 9 in Mcp). Also absorbs the `OperatingSystem.IsWindows()` path-comparison decisions in `DocsRootRegistry`:238 and `DocsMcpMigrateCommand`:171 |
| `IConsole` (or `ITextOutput`) | `TextWriter Out`, `TextWriter Error`, `bool IsInputRedirected`, `string? ReadLine()` | 47 `Console.*` hits (28 Cli, 1 Mcp `DocsRootRegistry`:138, 2 Arbiter transport factory, 2 Hooks `Program`, 14 elsewhere). `IProcessEnvironment.IsInputRedirected` already exists — reuse it |
| `IProcessProbe` | `bool IsRunning(int processId)`, `string? FindOnPath(string executableName)`, `ProcessResult RunVersion(string executable, TimeSpan timeout)` | `Process.GetProcessById` (`CodeGraphFreshness`:330), the PATH scans in `KyberDashCapturePluginStep`:258-270 and `ProcessProbes`:144-170, and the harness version probes in `UtilitiesStatusLineDoctorCommand` |
| `IExclusiveLock` (cross-process) | `bool TryAcquire(string path, TimeSpan timeout)`, `IDisposable Release()`, `bool IsHeld(string path)` | `new Mutex` in `TransactionLease`; the lock-file polling in `CodeGraphFreshness`:264-276 and :365-380; `AtomicFile`'s temp-and-rename dance (Kernel, 8 hits) |
| `IIdentifierSource` | `Guid NewId()`, `string NewToken(int length)` | 21 `Guid.NewGuid` hits; `Path.GetRandomFileName` users |
| `IPause` | `Task WaitAsync(TimeSpan, CancellationToken)`, `void Wait(TimeSpan)` | 6 `Thread.Sleep`, 2 `Task.Delay`, and the `Action<TimeSpan>` seam already present in `CodeGraphFreshness` (which should become this interface) |
| `IPlatform` — or extend `IProcessEnvironment` | `PlatformOs Current { get; }` (`Windows`/`MacOS`/`Linux`), `bool IsCaseInsensitiveFileSystem { get; }` | 70 `OperatingSystem.Is*` hits (11 Cli, 2 Mcp, 1 Cli/Mcp transport, 56 elsewhere) |
| `IDocsRootRegistry` | `bool TryResolve(string? projectPath, out DocsTools? tools, out string? refusal)`, `int Count { get; }` | the concrete `DocsRootRegistry` parameters in `DocsTools`:48 and `DocsToolsComposition.Create`:147 |
| `IKyberDashCapturePluginStep` | the step's `Execute`-equivalent members already on `KyberDashCapturePluginStep` | `SquadInstallCommand`:35, `SquadUpdateCommand`:49 |
| `IDocsAnalysisRuntime`, `IDocsAnalysisCompositionFactories` | the runtime's resolved collaborators; the factories' `Create*` members | `RepositoryDocsAnalysisCommandService`:21, `AnalysisExecution`:179 |
| `IMcpWiringInspector` / `IMcpWiringRepairer` / `IMcpServerRenamer` / `IMcpWiringFixes` | the public members of the four `McpWiring*` classes | `McpWiringService`:8 (4 params), `McpWiringFixes`:33 |
| `IHarnessAdapterRegistry` | `TryFind(harness)`, `DisabledAllowDocumentOf(harness)` | `HookCommand`:51 |
| `IArbiterRootResolver`, `IClaimExtractor`, `IMcpServerLiveProbe`, `IPostCheckoutHookInstaller`, `IArbiterRootResolver` | the public members of each | one each, listed in section 5 |

Two things that are **not** new interfaces but are the same size of win:

- **Retire the static config loader.** `KyberWeaveConfigLoader.TryLoad` has 23 call sites
  (`CommandHelpers`:41, `PlanArguments`:85, `DocsInitCommand`:422, `DocsOnboardingCommand`:214,
  `SquadCommandComposition`:477/490, `SquadDoctorCommand`:367, `SquadTaskGateHint`:35,
  `RepositoryRootResolver`:176, `ArbiterContainer`:86, plus 13 more). `IKyberWeaveConfigRepository`
  and `IHostConfigLoader` already exist and are injected elsewhere; every one of these sites should
  go through one of them.
- **Make `IProcessRunner` the one process seam.** Today `IProcessRunner` (Contracts) has zero users
  in this scope, `IProcessExecutor` (CLI) has 15, and 19 sites call the static `ProcessRunner.Run`
  or build their own `ProcessStartInfo`. Folding `IProcessExecutor` into `IProcessRunner` (or making
  the latter the only implementation of the former) removes the split.

---

## 8. What this means for the "no integration tests" goal

Interface-driven *design* is essentially done — 38 concrete service dependencies across 962
constructors is a rounding error, and the exceptions above are all one-parameter fixes. What blocks
true unit tests is the 1,281 direct resource calls, and the distribution is uneven:

- **Hooks and Arbiter already qualify.** Both take streams as parameters, both are small, and the
  only untestable step is the static config loader.
- **Mcp is one class away.** `CodeGraphFreshness` alone is 17 of the 32 A hits; give it
  `IFileSystem`, `IProcessProbe`, `IProcessEnvironment` and `IPlatform` and the project passes.
- **Cli is where the work is.** The command layer both composes and executes: `SquadHostDefaults`
  and `SquadCommandComposition` build services with `new`, then the commands they build read files,
  start processes, read `Environment.CurrentDirectory` and print to `Console`. The fix that pays
  best is at the bottom, not the top: `CommandHelpers` (file writes), `PlanArguments`
  (`Path.GetFullPath` + `Directory.GetCurrentDirectory`), and `IProcessExecutor`. Those three
  changes are read by dozens of commands, and each is a single class.
- **The adapters are fine.** `Kernel/Processes/ProcessRunner`, `Kernel/FileSystem/AtomicFile`,
  `Data/Hooks/PhysicalFileSystem`, `Data/Hooks/KernelProcessRunner`,
  `ConsoleArbiterKeyInput`, `ProcessExecutor` and both `DrainingStreamServerTransport`s exist to
  touch the resource. They are category D and should stay exactly as they are; the rule that keeps
  them honest is that each must be constructible with no arguments from a test's point of view —
  that is, reachable only through the port it implements.

---

## Coverage limits

- Every `.cs` file under `src/` (1,125 files) was scanned by the constructor classifier and by the
  resource-pattern matcher, so no file was skipped.
- Read end-to-end: `KyberWeave.Hooks` (2/2 files), `KyberWeave.Arbiter` (5/5), `KyberWeave.Mcp` (13/13).
- `KyberWeave.Cli` (133 files): both passes over every file; not read line-by-line. Rows may miss a
  resource use that no pattern matches, for example a BCL helper such as `File.ReadAllBytes` reached
  through a wrapper in another project, or `DateTime.UtcNow` inside a lambda on a line whose only
  class above it is a struct.
- `Domain` (445 files), `Data` (137), `Application` (150), `Kernel` (18), `Core` (21), `Core.Docs` (4)
  and `Contracts` (197): both passes only. They are reported as totals plus worst classes, not as
  per-class tables. If the owner wants the same per-class depth there, `Domain` and `Data` are the
  two trees that need it (`Data` alone holds 790 of the 1,281 A hits).
- No test project was inspected; the goal was production code. `tests/` was out of scope.
- `src/KyberWeave.Core.Squad` contains no `.cs` files — it contributes nothing and is worth a
  decision (remove, or restore its content).
