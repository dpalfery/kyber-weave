---
id: reference/seams
title: Seams, adapters and the exceptions they keep
doc-type: reference
status: current
owner: dpalfery
last-reviewed: 2026-11-17
component: CI Pipelines
---

# Seams, adapters and the exceptions they keep

Every outside resource the product touches — the disk, a child process, the environment, the
console, the clock, identifiers, a lease, an embedded resource, configuration and the shipped
content a test reads — is reached through one interface, called a **seam**. This page is the
register the interface-driven unit tests specification requires: the seams, the adapters that
implement them, the fake and contract suite that prove each adapter, every class still calling a
resource directly and the task that converts it, and the sealed framework types allowed through
with a reason.

Read from this branch (`opencode/idt/registers`, commit `de8057b1`) on 2026-11-17. The seams and
adapters come from the code on this branch, not from the design. The counts come from
`git grep -E` over `src/`: a **site** is one non-comment line that calls `System.IO`, a file or
directory member, `Process`/`ProcessStartInfo`, `Environment`, `Console`, the system clock,
`Stopwatch`, `Thread.Sleep`, `Task.Delay`, `Guid.NewGuid`, `Random`, `Mutex` or a network client.
Two kinds of line are not counted, because neither reads process state: pure string work in
`System.IO.Path` (`Combine`, `GetFileName`, `DirectorySeparatorChar`) and the platform constant
`Environment.NewLine`. `Path.GetFullPath`, `Path.GetTempPath` and `Path.GetRandomFileName` are
counted. Counts are lower bounds: one line can hold two calls.

## The seams

| Seam | Interface(s) | Covers | Adapter (project, file) | Fake (`tests/KyberWeave.Testing`) | Contract suite |
|---|---|---|---|---|---|
| File system | `IFileSystem`, `IWritableFileSystem` (`Contracts/Repositories`) | Text, bytes, lines, streams, length, last-write time, attributes; enumerate with a no-follow option; create directory; write; atomic write; atomic move with and without overwrite; exclusive create; delete file and tree without following links; create link; one-hop and final link target; temporary directory | `PhysicalFileSystem` (`KyberWeave.Data`, `Hooks/PhysicalFileSystem.cs`) | `InMemoryFileSystem` (`FileSystem/InMemoryFileSystem.cs`) | `FileSystemContractTests` and `FileSystemFaultContractTests`: fake in `KyberWeave.Tests/FileSystem/InMemoryFileSystemTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/PhysicalFileSystemContractTests.cs` |
| Paths | `IPathResolver` (`Contracts/Paths`) | Full path against an explicit base; are-same; is-within; canonical spelling (case and links); physical root. No member reads the process working directory | `PhysicalPathResolver` (`KyberWeave.Data`, `Paths/PhysicalPathResolver.cs`) over `FileSystemPathResolver` | `FakePathResolver` (`Fakes/FakePathResolver.cs`) | `PathResolverContractTests`: fake in `KyberWeave.Tests/Paths/InMemoryPathResolverTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/PhysicalPathResolverContractTests.cs` |
| Process | `IProcessRunner`; `IProcessExecutor`, `IProgramRunner` and `IBoundedProcessExecutor` stay as role views | Run with arguments, working directory, environment, timeout and input; captured output; is-alive; find on path | `KernelProcessRunner` (`KyberWeave.Data`, `Hooks/KernelProcessRunner.cs`) over `KyberWeave.Kernel/Processes/ProcessRunner.cs` | `ScriptedProcessRunner` (`Fakes/ScriptedProcessRunner.cs`); `ScriptedProcessExecutor` (`Fakes/ScriptedProcessExecutor.cs`) for the role view | `ProcessRunnerContractTests`: fake in `KyberWeave.Tests/Processes/ScriptedProcessRunnerContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/KernelProcessRunnerContractTests.cs` |
| Environment and platform | `IProcessEnvironment` (`Contracts/Repositories`) | Environment variables; current, home and temporary directories; user and machine name; operating-system kind; case-insensitive file system; input redirected | `ProcessEnvironment` (`KyberWeave.Data`, `Host/ProcessEnvironment.cs`) | `FakeProcessEnvironment` (`Fakes/FakeProcessEnvironment.cs`) | `ProcessEnvironmentContractTests`: fake in `KyberWeave.Tests/Host/FakeProcessEnvironmentContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/PhysicalProcessEnvironmentContractTests.cs` |
| Time | `TimeProvider` (framework) | Wall clock; monotonic timestamp and elapsed time; timers; `Task.Delay(TimeSpan, TimeProvider)` | `TimeProvider.System`, registered in `Application/ServiceCollectionExtensions.cs` and `Application/Arbiter/ArbiterApplicationServiceCollectionExtensions.cs` | `ManualTimeProvider` (`FileSystem/ManualTimeProvider.cs`); the framework's `FakeTimeProvider` is used in `KyberWeave.Tests/Composition/TimeProviderRegistrationTests.cs` | None: the framework owns the behaviour (decision D5). The registration test proves the composition roots resolve `TimeProvider.System` |
| Identifiers | `IIdGenerator` (`Contracts/Repositories`) | New GUID; random token; random file name | `SystemIdGenerator` (`KyberWeave.Data`, `Host/SystemIdGenerator.cs`) | `FakeIdGenerator` (`Fakes/FakeIdGenerator.cs`) | `IdGeneratorContractTests`: fake in `KyberWeave.Tests/Host/FakeProcessEnvironmentContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/PhysicalProcessEnvironmentContractTests.cs` |
| Console | `IConsole` (`Contracts/Host`) | Output and error writers; read line; input redirected | `SystemConsole` (`KyberWeave.Data`, `Host/SystemConsole.cs`); the standard-stream transport `DrainingStreamServerTransport` in `KyberWeave.Mcp` and `KyberWeave.Arbiter` | `FakeConsole` (`Fakes/FakeConsole.cs`) | `ConsoleContractTests`: fake in `KyberWeave.Tests/Console/FakeConsoleContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/SystemConsoleContractTests.cs` |
| Lease | `ILease` (`Contracts/Host`) | Acquire a named cross-process lease with a timeout; release from any thread | `MutexLease` (`KyberWeave.Data`, `Host/MutexLease.cs`); the lock-file variant is `FileCodeGraphIndexWorkspace` (`Data/Doctor/FileCodeGraphIndexWorkspace.cs`) | `InProcessLease` (`Fakes/InProcessLease.cs`) | `LeaseContractTests`: fake in `KyberWeave.Tests/Lease/InProcessLeaseContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/MutexLeaseContractTests.cs` |
| Embedded resource | `IEmbeddedResources` (`Contracts/Host`) | Read a named manifest resource as a stream or UTF-8 text | `AssemblyEmbeddedResources` (`KyberWeave.Data`, `Host/AssemblyEmbeddedResources.cs`) | `FakeEmbeddedResources` (`Fakes/FakeEmbeddedResources.cs`) | `EmbeddedResourceContractTests`: fake in `KyberWeave.Tests/EmbeddedResources/FakeEmbeddedResourcesContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/AssemblyEmbeddedResourcesContractTests.cs` |
| Configuration | `IKyberWeaveConfigRepository` (`Contracts/Repositories`) | Load and try-load the repository's and the user's host configuration; find the config path | `FileKyberWeaveConfigRepository` (`KyberWeave.Data`, `Configuration/FileKyberWeaveConfigRepository.cs`) over `FileRepositoryTreeProbe` | No shared fake yet: `FixedConfigs` is private in `KyberWeave.Application.Tests/Configuration/ConfigLoadServiceTests.cs` | None yet. Gap recorded for task 2.3, which retires the static loader and gives every caller a fake repository |
| Shipped content (tests) | `ICorpusSource` (`tests/KyberWeave.Testing/Corpus/ICorpusSource.cs`) | Read the shipped agents, skills, standards, documents and workflows by relative path, contained in the corpus; host name-case rule | `CheckoutCorpusSource` (`tests/KyberWeave.Testing/Corpus/CheckoutCorpusSource.cs`), a test adapter over this checkout | `InMemoryCorpusSource` (`tests/KyberWeave.Testing/Corpus/InMemoryCorpusSource.cs`) | `CorpusSourceContractTests`: both against the adapter and the fake in `KyberWeave.RepoPolicy.Tests/CorpusSourceContractTests.cs` |

Notes.

- The narrow file interfaces (`IRepositoryFileStore`, `IDoctorFileSystem`, `ISquadFileAccess`,
  `ISquadLinkFileSystem`, `IFileStatReader`, `IExclusiveFileWriter`, `IContainedFileReader`) are role
  views over the file-system seam, so a class can ask for the write it needs and nothing more.
- The corpus seam is test infrastructure, not a product seam. It lives in the shared testing library
  so a content test says what it reads instead of walking the tree for the repository root.
- The configuration row is the one seam without a shared fake and contract suite. Task 2.3 converts
  its 23 callers and is where both belong.
- An adapter is thin: it translates the call and holds no decision the logic should make. Where an
  adapter does hold policy (the file-system adapter's skip list, the process adapter's missing-
  executable mapping), that is noted below rather than hidden.

## The adapter register

An adapter implements one seam and is the only kind of class allowed to call the resource directly.
Everything else is either a composition root, which wires adapters at startup, or a class on a later
task's list.

### Adapters that stay

| Adapter | Seam | Project | File | Note |
|---|---|---|---|---|
| `PhysicalFileSystem` | File system | `KyberWeave.Data` | `Hooks/PhysicalFileSystem.cs` | The canonical adapter. It also owns the skip list a recursive enumerate hides, so the in-memory fake must agree with it |
| `PhysicalRepositoryFileStore` | File system | `KyberWeave.Data` | `Docs/FileSystem/PhysicalRepositoryFileStore.cs` | Repository reads and appends; the older `IRepositoryFileStore` role view |
| `PhysicalDoctorFileSystem` | File system | `KyberWeave.Data` | `Doctor/PhysicalDoctorFileSystem.cs` | Bytes and directory listings for doctor checks |
| `FileRepositoryTreeProbe` | File system | `KyberWeave.Data` | `Review/FileRepositoryTreeProbe.cs` | Existence, link chain and full-path probes; the config repository's tree probe |
| `UnixSquadLinkFileSystem` | File system | `KyberWeave.Data` | `Squad/Instances/UnixSquadLinkFileSystem.cs` | Symbolic links on Unix |
| `WindowsSquadLinkFileSystem` | File system | `KyberWeave.Data` | `Squad/Instances/WindowsSquadLinkFileSystem.cs` | Links on Windows, including the P/Invoke probe |
| `PhysicalMcpConfigFiles` | File system | `KyberWeave.Data` | `Docs/Mcp/PhysicalMcpConfigFiles.cs` | Reads, attributes and deletes MCP configuration files |
| `FileGitHookFiles` | File system | `KyberWeave.Data` | `Doctor/FileGitHookFiles.cs` | Hook files and executable bits |
| `FileStatusLineFiles` | File system | `KyberWeave.Data` | `Utilities/FileStatusLineFiles.cs` | Status-line artifact files |
| `FileCodeGraphIndexWorkspace` | Lease (file-lock variant) | `KyberWeave.Data` | `Doctor/FileCodeGraphIndexWorkspace.cs` | Holds the index lock as an exclusively opened file; its wait uses `DateTime.UtcNow` and `Thread.Sleep`, which task 4.4 puts on `TimeProvider` |
| `ExclusiveFileWriter` | File system | `KyberWeave.Data` | `Docs/Closeout/ExclusiveFileWriter.cs` | Exclusive create |
| `FileBoundedFileReader` | File system | `KyberWeave.Data` | `Review/FileBoundedFileReader.cs` | Reads up to a byte limit and refuses a file that grew |
| `FileHookFileWindowReader` | File system | `KyberWeave.Data` | `Hooks/Arbiter/FileHookFileWindowReader.cs` | Reads a line window from a file |
| `FileStatReader` | File system | `KyberWeave.Data` | `Arbiter/FileStatReader.cs` | Length and last-write time |
| `FileLinkProbe` | File system | `KyberWeave.Data` | `Hooks/Arbiter/FileLinkProbe.cs` | Link target and link kind |
| `AtomicFile` | File system | `KyberWeave.Kernel` | `FileSystem/AtomicFile.cs` | The kernel-side atomic write, rename and link resolution the file-system adapter builds on |
| `BoundedFileReader` | File system | `KyberWeave.Kernel` | `FileSystem/BoundedFileReader.cs` | The kernel-side bounded stream read |
| `PhysicalPathResolver` | Paths | `KyberWeave.Data` | `Paths/PhysicalPathResolver.cs` | The path adapter |
| `FileSystemPathResolver` | Paths | `KyberWeave.Data` | `Paths/FileSystemPathResolver.cs` | The base implementation over the file-system seam |
| `SquadFileSystemPathSemantics` | Paths | `KyberWeave.Data` | `Squad/Deployment/SquadFileSystemPathSemantics.cs` | Instance singleton the deployment code shares; it implements `IPathResolver` and `IFileSystemPathSemantics` |
| `KernelProcessRunner` | Process | `KyberWeave.Data` | `Hooks/KernelProcessRunner.cs` | The product's one `IProcessRunner` |
| `ProcessRunner` | Process | `KyberWeave.Kernel` | `Processes/ProcessRunner.cs` | The one place that calls `Process.Start`; `KernelProcessRunner` and the role-view adapters build on it |
| `StdioSession` | Process | `KyberWeave.Kernel` | `Processes/StdioSession.cs` | Interactive stdin/stdout session; its timeout still reads `DateTime.UtcNow`, so the clock conversion is outstanding |
| `ProcessProgramRunner` | Process | `KyberWeave.Data` | `Review/ProcessProgramRunner.cs` | The `IProgramRunner` role view |
| `KernelBoundedProcessExecutor` | Process | `KyberWeave.Data` | `Docs/FileSystem/KernelBoundedProcessExecutor.cs` | The `IBoundedProcessExecutor` role view |
| `ProcessGitCommandRunner` | Process | `KyberWeave.Data` | `RunStart/ProcessGitCommandRunner.cs` | Git with standard output and error kept apart |
| `ProcessRunnerCredentialProcessRunner` | Process | `KyberWeave.Data` | `Arbiter/Credentials/ProcessRunnerCredentialProcessRunner.cs` | The credential process runner over `IProcessRunner` |
| `ProcessSquadHarnessLauncher` | Process | `KyberWeave.Data` | `Squad/Instances/ProcessSquadHarnessLauncher.cs` | Starts the interactive harness; builds its start information through the static `SquadHarnessLauncher` |
| `ProcessExecutor` | Process | `KyberWeave.Cli` | `Commands/Squad/Infrastructure/IProcessExecutor.cs` | The CLI's `IProcessExecutor` role view |
| `MacKeychainCredentialStore` | Credentials (`ICredentialStore`, process-backed) | `KyberWeave.Data` | `Arbiter/Credentials/MacKeychainCredentialStore.cs` | Invokes `security`, through `ICredentialProcessRunner` |
| `SecretServiceCredentialStore` | Credentials (`ICredentialStore`, process-backed) | `KyberWeave.Data` | `Arbiter/Credentials/SecretServiceCredentialStore.cs` | Invokes `secret-tool`, through `ICredentialProcessRunner` |
| `WindowsCredentialStore` | Credentials (`ICredentialStore`, native) | `KyberWeave.Data` | `Arbiter/Credentials/WindowsCredentialStore.cs` | P/Invoke `advapi32`, gated on Windows |
| `ProcessEnvironment` | Environment and platform | `KyberWeave.Data` | `Host/ProcessEnvironment.cs` | The environment and platform adapter |
| `SystemIdGenerator` | Identifiers | `KyberWeave.Data` | `Host/SystemIdGenerator.cs` | GUIDs, random tokens and temp names |
| `SystemConsole` | Console | `KyberWeave.Data` | `Host/SystemConsole.cs` | Console output, error and input |
| `DrainingStreamServerTransport` | Console (standard streams) | `KyberWeave.Mcp` | `DrainingStreamServerTransport.cs` | Opens the standard streams for the MCP server |
| `DrainingStreamServerTransport` | Console (standard streams) | `KyberWeave.Arbiter` | `Mcp/DrainingStreamServerTransport.cs` | Opens the standard streams for the Arbiter host |
| `MutexLease` | Lease | `KyberWeave.Data` | `Host/MutexLease.cs` | The named-mutex lease |
| `AssemblyEmbeddedResources` | Embedded resource | `KyberWeave.Data` | `Host/AssemblyEmbeddedResources.cs` | Manifest resource reads |
| `FileKyberWeaveConfigRepository` | Configuration | `KyberWeave.Data` | `Configuration/FileKyberWeaveConfigRepository.cs` | Configuration load and save over the tree probe |
| `CheckoutCorpusSource` | Shipped content (tests) | `KyberWeave.Testing` | `Corpus/CheckoutCorpusSource.cs` | The test-side adapter over this checkout |

The process seam has one sanctioned escape that is not an adapter: a class may build a
`ProcessStartInfo` and hand it to `IProcessRunner`, `IProcessExecutor`, `IProgramRunner` or a
delegate. `ProcessStartInfo` is data there, and the exceptions register below covers it. Production
process starts are in `Kernel/Processes/ProcessRunner.cs` and `StdioSession`; `SelfUpdateSystem`
still calls `Process.Start` itself and is on task 4.4's list.

### Composition roots (allowed to read process state at startup)

These wire the adapters and may read the environment, the console or the working directory while
they do it. They are not seams and no test fakes them; a smoke test proves the wiring instead.

| Root | Project | File |
|---|---|---|
| `ServiceCollectionExtensions` | `KyberWeave.Application` | `ServiceCollectionExtensions.cs` |
| `ArbiterApplicationServiceCollectionExtensions` | `KyberWeave.Application` | `Arbiter/ArbiterApplicationServiceCollectionExtensions.cs` |
| `ServiceCollectionExtensions` | `KyberWeave.Data` | `ServiceCollectionExtensions.cs` |
| `SquadServiceCollectionExtensions` | `KyberWeave.Data` | `Squad/SquadServiceCollectionExtensions.cs` |
| `Program` | `KyberWeave.Cli` | `Program.cs` |
| `DocsCommandComposition` | `KyberWeave.Cli` | `Commands/Docs/DocsCommandComposition.cs` |
| `Program` | `KyberWeave.Mcp` | `Program.cs` |
| `DocsToolsComposition` | `KyberWeave.Mcp` | `DocsToolsComposition.cs` |
| `Program` | `KyberWeave.Hooks` | `Program.cs` |
| `Program` | `KyberWeave.Arbiter` | `Program.cs` |

### Classes still calling a resource directly

Task 1.8 does not convert any of these; it lists them so phase 2, phase 3 and phase 4 have a
worklist and so a reviewer can tell an adapter from a violation. A task number is the task whose
file list or folder covers the class. `4.3 (split)` marks an `Application` folder that the phase 4
task list does not name individually; it is converted by the per-folder split of task 4.3, which is
also where those splits are recorded.

| Project | Classes calling a resource | Direct-call sites | Tasks |
|---|---|---|---|
| `KyberWeave.Application` | 64 | 208 | 4.1, 4.2, 4.3, unassigned |
| `KyberWeave.Cli` | 49 | 180 | 4.5 |
| `KyberWeave.Data` | 80 | 705 | 3.2, 3.3, 3.4, 3.5, 4.4 |
| `KyberWeave.Domain` | 26 | 46 | 2.5 |
| `KyberWeave.Hooks` | 1 | 1 | 4.7 |
| `KyberWeave.Kernel` | 2 | 18 | 2.2, 4.6 |
| `KyberWeave.Mcp` | 4 | 16 | 4.6 |
| `KyberWeave.Arbiter` | 0 | 0 | — (its two files are the Program root and the console-stream adapter) |
| **Total** | **226** | **1,174** | |

The totals before adapters and roots are taken out are 68 classes and 212 sites in Application,
52 and 183 in Cli, 111 and 955 in Data, 28 and 49 in Domain, 2 and 3 in Hooks, 6 and 47 in Kernel,
7 and 28 in Mcp, and 2 and 6 in Arbiter. `KyberWeave.Contracts` reports six files and eight sites,
all of them `ProcessStartInfo` in an interface or a request record; it holds no implementation.
`Environment.NewLine` sites are excluded from both sets of numbers.

#### KyberWeave.Application (67 classes)

| Class (file) | Calls directly | Task |
|---|---|---|
| `AgentFileReader` (`Agents/AgentFileReader.cs`) | paths | 4.3 (split) |
| `ArbiterEvaluator` (`Arbiter/ArbiterEvaluator.cs`) | clock | 4.3 |
| `ArbiterHarnessGating` (`Arbiter/ArbiterHarnessGating.cs`) | paths | 4.3 |
| `ArbiterInstallService` (`Arbiter/ArbiterInstallService.cs`) | paths | 4.3 |
| `ArbiterPlanService` (`Arbiter/ArbiterPlanService.cs`) | paths | 4.3 |
| `ArbiterRepositoryLocator` (`Arbiter/ArbiterRepositoryLocator.cs`) | file system, paths | 4.3 |
| `ArbiterSectionSelectionService` (`Arbiter/ArbiterSectionSelectionService.cs`) | file system | 4.3 |
| `ArbiterUninstaller` (`Arbiter/ArbiterUninstaller.cs`) | paths | 4.3 |
| `ArbiterHookDecisionEngine` (`Arbiter/Hooks/ArbiterHookDecisionEngine.cs`) | paths, identifiers | 4.3 |
| `ConductorGuard` (`Arbiter/Hooks/ConductorGuard.cs`) | paths | 4.3 |
| `HookCommand` (`Arbiter/Hooks/HookCommand.cs`) | identifiers | 4.3 |
| `HookGitFacts` (`Arbiter/Hooks/HookGitFacts.cs`) | clock | 4.3 |
| `WorkerGuard` (`Arbiter/Hooks/WorkerGuard.cs`) | file system, paths | 4.3 |
| `ArbiterRootResolver` (`Arbiter/Mcp/ArbiterRootResolver.cs`) | paths | 4.3 |
| `OllamaProbe` (`Arbiter/OllamaProbe.cs`) | network | 4.3 |
| `ManagedGlossary` (`Docs/Analysis/Glossary/ManagedGlossary.cs`) | paths | 4.2 |
| `PlanCloseout` (`Docs/Closeout/PlanCloseout.cs`) | paths | 4.2 |
| `PlanFileResolver` (`Docs/Closeout/PlanFileResolver.cs`) | file system, paths | 4.2 |
| `PlanFinalize` (`Docs/Closeout/PlanFinalize.cs`) | paths | 4.2 |
| `PullRequestDescription` (`Docs/Delivery/PullRequestDescription.cs`) | file system | 4.2 |
| `RunDelivery` (`Docs/Delivery/RunDelivery.cs`) | paths | 4.2 |
| `CorpusProvenanceService` (`Docs/Mcp/CorpusProvenanceService.cs`) | paths | 4.2 |
| `McpServerRenamer` (`Docs/Mcp/McpServerRenamer.cs`) | paths, clock | 4.2 |
| `McpWiringFixes` (`Docs/Mcp/McpWiringFixes.cs`) | paths | 4.2 |
| `McpWiringInspector` (`Docs/Mcp/McpWiringInspector.cs`) | paths | 4.2 |
| `McpWiringRepairer` (`Docs/Mcp/McpWiringRepairer.cs`) | paths, clock | 4.2 |
| `RepositoryDocsAnalysisReader` (`Docs/Mcp/RepositoryDocsAnalysisReader.cs`) | paths | 4.2 |
| `DocsScaffolder` (`Docs/Scaffolding/DocsScaffolder.cs`) | file system, paths | 4.2 |
| `StandardsFolderMigration` (`Docs/Scaffolding/StandardsFolderMigration.cs`) | file system, paths, identifiers | 4.2 |
| `StandardsTemplateDiff` (`Docs/Scaffolding/StandardsTemplateDiff.cs`) | file system, paths | 4.2 |
| `DocumentIndexHost` (`Docs/Search/DocumentIndexHost.cs`) | file system, paths | 4.2 |
| `ArchiveCloseoutValidator` (`Docs/Validation/ArchiveCloseoutValidator.cs`) | paths | 4.2 |
| `ConfigRegValidator` (`Docs/Validation/ConfigRegValidator.cs`) | paths | 4.2 |
| `PlanTaskService` (`Docs/Validation/PlanTaskService.cs`) | paths | 4.2 |
| `TaskPromptService` (`Docs/Validation/TaskPromptService.cs`) | file system, paths | 4.2 |
| `TaskPromptServiceFactory` (`Docs/Validation/TaskPromptServiceFactory.cs`) | paths | 4.2 |
| `CodeGraphIndexProvisioner` (`Doctor/CodeGraphIndexProvisioner.cs`) | clock | 4.3 (split) |
| `McpServerLiveProbe` (`Doctor/McpServerLiveProbe.cs`) | paths | 4.3 (split) |
| `PostCheckoutHookInstaller` (`Doctor/PostCheckoutHookInstaller.cs`) | paths | 4.3 (split) |
| `EditHookService` (`Hooks/EditHookService.cs`) | file system, paths | 4.3 (split) |
| `GateRunService` (`Review/GateRunService.cs`) | paths, clock | 4.3 |
| `PlanTaskTargetService` (`Review/PlanTaskTargetService.cs`) | file system, paths | 4.3 |
| `ReviewCatalogueService` (`Review/ReviewCatalogueService.cs`) | paths | 4.3 |
| `ReviewCycleService` (`Review/ReviewCycleService.cs`) | paths | 4.3 |
| `ReviewCycleView` (`Review/ReviewCycleView.cs`) | paths | 4.3 |
| `ReviewDecisionSession` (`Review/ReviewDecisionSession.cs`) | clock | 4.3 |
| `ReviewDuplicatesService` (`Review/ReviewDuplicatesService.cs`) | paths | 4.3 |
| `ReviewEvidenceService` (`Review/ReviewEvidenceService.cs`) | paths | 4.3 |
| `ReviewGatesService` (`Review/ReviewGatesService.cs`) | file system, paths | 4.3 |
| `ReviewReportWriter` (`Review/ReviewReportWriter.cs`) | paths | 4.3 |
| `ReviewSeatsService` (`Review/ReviewSeatsService.cs`) | paths | 4.3 |
| `ReviewSuppressionsService` (`Review/ReviewSuppressionsService.cs`) | paths | 4.3 |
| `ReviewVerdictService` (`Review/ReviewVerdictService.cs`) | paths | 4.3 |
| `ReviewWorkTreeService` (`Review/ReviewWorkTreeService.cs`) | file system, paths | 4.3 |
| `SuppressionScanService` (`Review/SuppressionScanService.cs`) | paths | 4.3 |
| `TaskGateResolver` (`Review/TaskGateResolver.cs`) | file system | 4.3 |
| `RunStartRunner` (`RunStart/RunStartRunner.cs`) | paths | 4.3 (split) |
| `SkillFileReader` (`Skills/SkillFileReader.cs`) | paths | 4.3 (split) |
| `SquadBriefBuilder` (`Squad/Briefs/SquadBriefBuilder.cs`) | file system, paths | 4.3 (split) |
| `FactoryMcpConfiguration` (`Squad/Deployment/FactoryMcpConfiguration.cs`) | file system | 4.3 (split) |
| `SquadInstanceService` (`Squad/Instances/SquadInstanceService.cs`) | paths | 4.3 (split) |
| `SquadPackService` (`Squad/Packaging/SquadPackService.cs`) | paths | 4.3 (split) |
| `SelfUpdater` (`Update/SelfUpdater.cs`) | file system, paths, environment, network | 4.1 |
| `StatusLineProductArtifactSource` (`Utilities/StatusLineProductArtifactSource.cs`) | paths | 4.3 (split) |

#### KyberWeave.Cli (49 classes)

| Class (file) | Calls directly | Task |
|---|---|---|
| `ArbiterAuditCommand` (`Commands/Arbiter/ArbiterAuditCommand.cs`) | paths | 4.5 |
| `ArbiterCommentCommand` (`Commands/Arbiter/ArbiterCommentCommand.cs`) | file system, paths | 4.5 |
| `ArbiterRepoRoot` (`Commands/Arbiter/ArbiterRepoRoot.cs`) | file system, paths | 4.5 |
| `ArbiterSectionsCommand` (`Commands/Arbiter/ArbiterSectionsCommand.cs`) | file system, paths | 4.5 |
| `ArbiterUninstallCommand` (`Commands/Arbiter/ArbiterUninstallCommand.cs`) | paths | 4.5 |
| `ConsoleArbiterKeyInput` (`Commands/Arbiter/ConsoleArbiterKeyInput.cs`) | console | 4.5 |
| `CommandHelpers` (`Commands/CommandHelpers.cs`) | file system, paths | 4.5 |
| `DocsCloseoutCommand` (`Commands/Docs/DocsCloseoutCommand.cs`) | clock | 4.5 |
| `DocsFinalizeCommand` (`Commands/Docs/DocsFinalizeCommand.cs`) | clock | 4.5 |
| `DocsInitCommand` (`Commands/Docs/DocsInitCommand.cs`) | file system, paths, environment, console | 4.5 |
| `DocsMcpMigrateCommand` (`Commands/Docs/DocsMcpMigrateCommand.cs`) | file system, paths, environment | 4.5 |
| `DocsMcpWireCommand` (`Commands/Docs/DocsMcpWireCommand.cs`) | file system, paths | 4.5 |
| `DocsOnboardingCommand` (`Commands/Docs/DocsOnboardingCommand.cs`) | file system, paths | 4.5 |
| `DocsPacketCommand` (`Commands/Docs/DocsPacketCommand.cs`) | paths, console | 4.5 |
| `DocsTasksCommand` (`Commands/Docs/DocsTasksCommand.cs`) | console | 4.5 |
| `DoctorCommand` (`Commands/Doctor/DoctorCommand.cs`) | paths, console | 4.5 |
| `DoctorReportRenderer` (`Commands/Doctor/DoctorReportRenderer.cs`) | console | 4.5 |
| `PlanArguments` (`Commands/PlanArguments.cs`) | file system, paths | 4.5 |
| `ReviewCatalogueCommand` (`Commands/Review/ReviewCatalogueCommand.cs`) | console | 4.5 |
| `ReviewCycleCommand` (`Commands/Review/ReviewCycleCommand.cs`) | console | 4.5 |
| `ReviewGatesCommand` (`Commands/Review/ReviewGatesCommand.cs`) | console | 4.5 |
| `ReviewMigrateTaskGatesCommand` (`Commands/Review/ReviewMigrateTaskGatesCommand.cs`) | file system, paths | 4.5 |
| `ReviewSeatsCommand` (`Commands/Review/ReviewSeatsCommand.cs`) | console | 4.5 |
| `ReviewSuppressionsCommand` (`Commands/Review/ReviewSuppressionsCommand.cs`) | console | 4.5 |
| `ReviewVerdictCommand` (`Commands/Review/ReviewVerdictCommand.cs`) | console | 4.5 |
| `RunDeliverCommand` (`Commands/Run/RunDeliverCommand.cs`) | paths | 4.5 |
| `RunStartCommand` (`Commands/Run/RunStartCommand.cs`) | console | 4.5 |
| `CatalogCommand` (`Commands/Skills/CatalogCommand.cs`) | console | 4.5 |
| `RouteCommand` (`Commands/Skills/RouteCommand.cs`) | console | 4.5 |
| `SquadBriefCommand` (`Commands/Squad/Brief/SquadBriefCommand.cs`) | file system, console | 4.5 |
| `CopilotPromptCommands` (`Commands/Squad/CopilotPromptCommands.cs`) | file system, environment, console | 4.5 |
| `KyberDashCapturePluginStep` (`Commands/Squad/Infrastructure/KyberDashCapturePluginStep.cs`) | file system, environment | 4.5 |
| `ProcessProbes` (`Commands/Squad/Infrastructure/ProcessProbes.cs`) | environment | 4.5 |
| `SquadTargetRootConfirmation` (`Commands/Squad/Infrastructure/SquadTargetRootConfirmation.cs`) | console | 4.5 |
| `SquadCommandComposition` (`Commands/Squad/SquadCommandComposition.cs`) | file system, paths, console | 4.5 |
| `SquadDoctorCommand` (`Commands/Squad/SquadDoctorCommand.cs`) | file system, environment | 4.5 |
| `SquadHostDefaults` (`Commands/Squad/SquadHostDefaults.cs`) | file system, environment | 4.5 |
| `SquadInstallCommand` (`Commands/Squad/SquadInstallCommand.cs`) | environment, console | 4.5 |
| `SquadInstanceRemoveCommand` (`Commands/Squad/SquadInstanceRemoveCommand.cs`) | paths | 4.5 |
| `SquadPresetClearCommand` (`Commands/Squad/SquadPresetClearCommand.cs`) | file system | 4.5 |
| `SquadPresetListCommand` (`Commands/Squad/SquadPresetListCommand.cs`) | file system | 4.5 |
| `SquadPresetUseCommand` (`Commands/Squad/SquadPresetUseCommand.cs`) | file system | 4.5 |
| `SquadRunCommand` (`Commands/Squad/SquadRunCommand.cs`) | paths | 4.5 |
| `SquadStatusCommand` (`Commands/Squad/SquadStatusCommand.cs`) | file system, environment | 4.5 |
| `SquadUninstallCommand` (`Commands/Squad/SquadUninstallCommand.cs`) | environment | 4.5 |
| `SquadUpdateCommand` (`Commands/Squad/SquadUpdateCommand.cs`) | environment | 4.5 |
| `SquadUserPaths` (`Commands/Squad/SquadUserPaths.cs`) | environment | 4.5 |
| `SelfUpdateHostFactory` (`Composition/SelfUpdateHostFactory.cs`) | environment | 4.5 |
| `ReportRenderer` (`Rendering/ReportRenderer.cs`) | console | 4.5 |

#### KyberWeave.Data (80 classes)

| Class (file) | Calls directly | Task |
|---|---|---|
| `ClaudeHookCoverage` (`Arbiter/ClaudeHookCoverage.cs`) | file system | 4.4 |
| `ConfiguredArbiterProviderFactory` (`Arbiter/ConfiguredArbiterProviderFactory.cs`) | environment, network | 4.4 |
| `FileTextFacts` (`Arbiter/Facts/FileTextFacts.cs`) | file system, paths | 4.4 |
| `GateReportFacts` (`Arbiter/Facts/GateReportFacts.cs`) | file system, paths | 4.4 |
| `GitFacts` (`Arbiter/Facts/GitFacts.cs`) | file system | 4.4 |
| `InFlightLedger` (`Arbiter/Ledger/InFlightLedger.cs`) | file system, clock | 4.4 |
| `PlanFacts` (`Arbiter/Plans/PlanFacts.cs`) | file system | 4.4 |
| `PlanPathResolver` (`Arbiter/Plans/PlanPathResolver.cs`) | file system, paths | 4.4 |
| `PlanRootResolver` (`Arbiter/Plans/PlanRootResolver.cs`) | file system, paths | 4.4 |
| `SystemOneClient` (`Arbiter/Providers/SystemOneClient.cs`) | clock, network | 4.4 |
| `SocketArbiterHttpHandlers` (`Arbiter/SocketArbiterHttpHandlers.cs`) | network | 4.4 |
| `ArbiterRunStore` (`Arbiter/Stores/ArbiterRunStore.cs`) | file system, paths, clock | 4.4 |
| `SystemOneProviderFactory` (`Arbiter/SystemOneProviderFactory.cs`) | network | 4.4 |
| `CodeGraphResolverAdapter` (`CodeGraph/CodeGraphResolverAdapter.cs`) | file system, paths | 4.4 |
| `FileUserHostConfigStore` (`Configuration/FileUserHostConfigStore.cs`) | file system | 4.4 |
| `OpenAiCompatibleEmbeddingGenerator` (`Docs/Analysis/Embeddings/OpenAiCompatibleEmbeddingGenerator.cs`) | environment, network | 4.4 |
| `AnalysisCacheSafety` (`Docs/Analysis/Persistence/AnalysisCacheSafety.cs`) | file system, paths | 4.4 |
| `SqliteAnalysisPersistence` (`Docs/Analysis/Persistence/SqliteAnalysisPersistence.cs`) | file system, paths | 4.4 |
| `PresentFile` (`Docs/Closeout/PresentFile.cs`) | file system | 4.4 |
| `DocGraphExporter` (`Docs/Export/DocGraphExporter.cs`) | file system | 4.4 |
| `GitCheckoutProbe` (`Docs/Mcp/GitCheckoutProbe.cs`) | file system, paths | 4.4 |
| `GitWorktreeIdentity` (`Docs/Mcp/GitWorktreeIdentity.cs`) | paths | 4.4 |
| `LaunchCommandResolver` (`Docs/Mcp/LaunchCommandResolver.cs`) | file system, paths, environment | 4.4 |
| `DocumentLoader` (`Docs/Parsing/DocumentLoader.cs`) | file system, paths | 4.4 |
| `DocsRootPath` (`FileSystem/DocsRootPath.cs`) | file system | 4.4 |
| `FileBackgroundDispatchStore` (`Hooks/Arbiter/FileBackgroundDispatchStore.cs`) | file system, clock, identifiers | 4.4 |
| `FileDispatchClaimStore` (`Hooks/Arbiter/FileDispatchClaimStore.cs`) | file system, clock, identifiers | 4.4 |
| `FileFactoryTranscriptSource` (`Hooks/Arbiter/FileFactoryTranscriptSource.cs`) | file system | 4.4 |
| `FileHookSnapshotStore` (`Hooks/Arbiter/FileHookSnapshotStore.cs`) | file system, clock, identifiers | 4.4 |
| `FileWorkerBudgetStore` (`Hooks/Arbiter/FileWorkerBudgetStore.cs`) | file system | 4.4 |
| `CheckoutProductRootLocator` (`Host/CheckoutProductRootLocator.cs`) | paths | 4.4 |
| `FileCitedSourceFiles` (`Review/FileCitedSourceFiles.cs`) | file system, paths | 4.4 |
| `FileCoverageReportSource` (`Review/FileCoverageReportSource.cs`) | file system | 4.4 |
| `FileNotSeatedStore` (`Review/FileNotSeatedStore.cs`) | file system | 4.4 |
| `FileProjectFileLocator` (`Review/FileProjectFileLocator.cs`) | file system, paths | 4.4 |
| `FileRepositoryFileLister` (`Review/FileRepositoryFileLister.cs`) | file system | 4.4 |
| `FileReviewCycleStore` (`Review/FileReviewCycleStore.cs`) | file system, paths | 4.4 |
| `FileReviewDecisionCacheStore` (`Review/FileReviewDecisionCacheStore.cs`) | file system, paths | 4.4 |
| `FileReviewEvidenceStore` (`Review/FileReviewEvidenceStore.cs`) | file system | 4.4 |
| `FileSeatFolder` (`Review/FileSeatFolder.cs`) | file system | 4.4 |
| `FileSeverityOverrideSource` (`Review/FileSeverityOverrideSource.cs`) | file system, paths | 4.4 |
| `FileTestClassSources` (`Review/FileTestClassSources.cs`) | file system, paths | 4.4 |
| `RunCheckoutGuard` (`RunStart/RunCheckoutGuard.cs`) | paths | 4.4 |
| `ZipSkillArchiveWriter` (`Skills/ZipSkillArchiveWriter.cs`) | file system | 4.4 |
| `DevinImportOverlap` (`Squad/Deployment/DevinImportOverlap.cs`) | file system, paths | 3.5 |
| `FactoryHooksShadowing` (`Squad/Deployment/FactoryHooksShadowing.cs`) | file system | 3.5 |
| `SquadDeploymentAdapters` (`Squad/Deployment/SquadDeploymentAdapters.cs`) | file system, paths, identifiers | 3.5 |
| `SquadDeploymentPlanner` (`Squad/Deployment/SquadDeploymentPlanner.cs`) | file system, paths | 3.3 |
| `SquadHookJsonFile` (`Squad/Deployment/SquadHookJsonFile.cs`) | file system, paths | 3.5 |
| `SquadMigrationLedger` (`Squad/Deployment/SquadMigrationLedger.cs`) | file system | 3.5 |
| `SquadResponseGuidanceDeployer` (`Squad/Deployment/SquadResponseGuidanceDeployer.cs`) | file system, paths, identifiers | 3.5 |
| `SquadResponseGuidanceReader` (`Squad/Deployment/SquadResponseGuidanceReader.cs`) | file system | 3.5 |
| `SquadSkillShadowing` (`Squad/Deployment/SquadSkillShadowing.cs`) | file system, paths | 3.5 |
| `SquadStateMigration` (`Squad/Deployment/SquadStateMigration.cs`) | file system, clock, identifiers | 3.2 |
| `SquadStateStore` (`Squad/Deployment/SquadStateStore.cs`) | file system, paths, identifiers | 3.2 |
| `SquadTargetResolver` (`Squad/Deployment/SquadTargetResolver.cs`) | file system, paths | 3.5 |
| `SquadTransaction` (`Squad/Deployment/SquadTransaction.cs`) | file system, paths, identifiers, lease | 3.4 |
| `SquadWorkerBaselineFile` (`Squad/Deployment/SquadWorkerBaselineFile.cs`) | file system, paths | 3.5 |
| `SquadWorktreeMcp` (`Squad/Deployment/SquadWorktreeMcp.cs`) | file system, paths | 3.5 |
| `TransactionLease` (`Squad/Deployment/TransactionLease.cs`) | file system, lease | 3.4 |
| `ZCodeMcpConfiguration` (`Squad/Deployment/ZCodeMcpConfiguration.cs`) | paths | 3.5 |
| `SquadHarnessLauncher` (`Squad/Instances/SquadHarnessLauncher.cs`) | file system, environment | 3.5 |
| `SquadInstanceLinkSync` (`Squad/Instances/SquadInstanceLinkSync.cs`) | file system, paths, identifiers, lease | 3.5 |
| `FileSquadPackager` (`Squad/Packaging/FileSquadPackager.cs`) | file system, paths, identifiers | 3.5 |
| `SquadCompanionSkills` (`Squad/Packaging/SquadCompanionSkills.cs`) | file system, paths | 3.5 |
| `SquadPackSourceLocator` (`Squad/Packaging/SquadPackSourceLocator.cs`) | file system, paths | 3.5 |
| `SquadPacker` (`Squad/Packaging/SquadPacker.cs`) | file system, identifiers | 3.5 |
| `SquadSourceLoader` (`Squad/Parsing/SquadSourceLoader.cs`) | file system, paths | 3.5 |
| `SquadCanonicalProfileStore` (`Squad/Presets/SquadCanonicalProfileStore.cs`) | file system, identifiers | 3.5 |
| `SquadModelPresetSelectionStore` (`Squad/Presets/SquadModelPresetSelectionStore.cs`) | file system, identifiers | 3.5 |
| `SquadModelPresetStore` (`Squad/Presets/SquadModelPresetStore.cs`) | file system | 3.5 |
| `SquadModelPresetValidator` (`Squad/Presets/SquadModelPresetValidator.cs`) | file system | 3.5 |
| `GitHubSquadReleaseSource` (`Squad/Release/GitHubSquadReleaseSource.cs`) | file system, paths, environment, identifiers, network | 3.5 |
| `LocalCheckoutSquadReleaseSource` (`Squad/Release/LocalCheckoutSquadReleaseSource.cs`) | file system, paths | 3.5 |
| `CopilotMcpServerProbe` (`Squad/Rendering/CopilotMcpServerProbe.cs`) | file system | 3.5 |
| `FileSquadRenderInputReader` (`Squad/Rendering/FileSquadRenderInputReader.cs`) | file system, environment | 3.5 |
| `SquadOperatorChannelOverlayFile` (`Squad/Rendering/SquadOperatorChannelOverlayFile.cs`) | file system | 3.5 |
| `CopilotPromptRefresher` (`Update/CopilotPromptRefresher.cs`) | file system | 4.4 |
| `GitHubReleaseClient` (`Update/GitHubReleaseClient.cs`) | file system, network | 4.4 |
| `SelfUpdateSystem` (`Update/SelfUpdateSystem.cs`) | file system, process, identifiers | 4.4 |

#### KyberWeave.Domain (26 classes)

| Class (file) | Calls directly | Task |
|---|---|---|
| `MarkdownAgentParser` (`Agents/Parsing/MarkdownAgentParser.cs`) | paths | 2.5 |
| `TomlAgentParser` (`Agents/Parsing/TomlAgentParser.cs`) | paths | 2.5 |
| `AgentSpecValidator` (`Agents/Validation/AgentSpecValidator.cs`) | paths | 2.5 |
| `ArbiterConfigLoader` (`Arbiter/ArbiterConfigLoader.cs`) | file system | 2.5 |
| `ArbiterLedgerEvent` (`Arbiter/Facts/ArbiterLedgerEvent.cs`) | identifiers | 2.5 |
| `ArbiterProvenance` (`Arbiter/Hooks/ArbiterProvenance.cs`) | paths | 2.5 |
| `ConductorGuardRules` (`Arbiter/Hooks/ConductorGuardRules.cs`) | paths | 2.5 |
| `ArbiterSquadCatalog` (`Arbiter/Squad/ArbiterSquadCatalog.cs`) | file system | 2.5 |
| `DocsAnalysisConfigLoader` (`Configuration/DocsAnalysisConfigLoader.cs`) | network | 2.5 |
| `ReviewConfigParser` (`Configuration/ReviewConfigParser.cs`) | paths | 2.5 |
| `CopilotPromptFile` (`CopilotPrompt/CopilotPromptFile.cs`) | file system, paths | 2.5 |
| `McpRepositoryPath` (`Docs/Mcp/McpRepositoryPath.cs`) | paths | 2.5 |
| `McpWiringReceipt` (`Docs/Mcp/McpWiringReceipt.cs`) | file system | 2.5 |
| `KyberStandardsTemplates` (`Docs/Scaffolding/KyberStandardsTemplates.cs`) | file system | 2.5 |
| `DocSpecValidator` (`Docs/Validation/DocSpecValidator.cs`) | file system, paths | 2.5 |
| `CodeGraphIndexRules` (`Doctor/CodeGraphIndexRules.cs`) | paths | 2.5 |
| `PathContainment` (`Hooks/PathContainment.cs`) | paths | 2.5 |
| `GateCatalogueRenderer` (`Review/Catalogue/GateCatalogueRenderer.cs`) | file system | 2.5 |
| `ProjectRootDetection` (`Review/Catalogue/ProjectRootDetection.cs`) | file system | 2.5 |
| `TaskGatePlanning` (`Review/TaskGatePlanning.cs`) | paths | 2.5 |
| `RunBaseStateFormat` (`RunStart/RunBaseStateFormat.cs`) | paths | 2.5 |
| `SkillParser` (`Skills/Parsing/SkillParser.cs`) | paths | 2.5 |
| `BriefMap` (`Squad/Briefs/BriefMap.cs`) | file system | 2.5 |
| `SquadGlobalRoots` (`Squad/Deployment/SquadGlobalRoots.cs`) | paths | 2.5 |
| `SquadPathPolicy` (`Squad/Deployment/SquadPathPolicy.cs`) | paths | 2.5 |
| `SquadResponseGuidanceSource` (`Squad/Deployment/SquadResponseGuidanceSource.cs`) | file system | 2.5 |

#### KyberWeave.Hooks (1 class)

| Class (file) | Calls directly | Task |
|---|---|---|
| `HooksCli` (`HooksCli.cs`) | environment | 4.7 |

#### KyberWeave.Kernel (2 classes)

| Class (file) | Calls directly | Task |
|---|---|---|
| `FileReferenceExtractor` (`Parsing/FileReferenceExtractor.cs`) | file system, paths | 2.2 |
| `CodeGraphIndexStaleness` (`VersionControl/CodeGraphIndexStaleness.cs`) | file system | 4.6 |

#### KyberWeave.Mcp (4 classes)

| Class (file) | Calls directly | Task |
|---|---|---|
| `CodeGraphFreshness` (`CodeGraphFreshness.cs`) | file system, paths, environment, clock | 4.6 |
| `DocsRootRegistry` (`DocsRootRegistry.cs`) | paths, console | 4.6 |
| `DocsTools` (`DocsTools.cs`) | paths | 4.6 |
| `RepositoryRootResolver` (`RepositoryRootResolver.cs`) | paths | 4.6 |

## The exceptions register

A framework type that is sealed and has no interface may be used directly (requirement 3.3). The
type is only an exception from the seam, never from the rule that a test must be able to control
the behaviour: where a test observes the value, the seam still wraps it.

| Type | Why it stays | Where the code uses it | What a test controls |
|---|---|---|---|
| `JsonDocument` | Parses a string or byte payload the caller already holds. It opens no resource and holds no state a test needs to replace | 32 files, 53 sites; most in `Application/Docs/Mcp` and `Data/Squad` | The input text or bytes; the parse result is deterministic |
| `ZipArchive` | A sealed reader and writer over a stream. The stream is the resource, not the archive | 3 files, 8 sites: `Data/Squad/Release/GitHubSquadReleaseSource.cs`, `Data/Squad/Packaging/SquadPacker.cs`, `Data/Skills/ZipSkillArchiveWriter.cs` | The stream: a `MemoryStream` holding a fixture archive |
| `SHA256` | A pure hash over bytes. Two calls on the same bytes return the same digest, so a test never needs to replace it | 40 files, 51 sites, from `Arbiter` through `Squad` to `SelfUpdater` | The bytes hashed; the digest is asserted directly |
| `ProcessStartInfo` | A sealed data transfer object handed to `IProcessRunner`, `IProcessExecutor`, `IProgramRunner` or a delegate. It starts nothing by itself; the runner does | 39 files, 66 sites; production process starts remain in `Kernel/Processes/ProcessRunner.cs` and `StdioSession` | A scripted fake asserts the program, arguments, working directory and environment it receives |
| `HttpClient` | A sealed network client. The transport is the seam: an injected `HttpMessageHandler` or `IArbiterHttpHandlers`, or a named network adapter | 9 files, 16 sites: the four network adapters (`SystemOneClient`, `GitHubReleaseClient`, `GitHubSquadReleaseSource`, `OpenAiCompatibleEmbeddingGenerator`), `OllamaProbe` over `IArbiterHttpHandlers`, `SelfUpdater` over an injected handler, and two factories that pass the handler on | The handler or the network adapter; no test opens a socket |
| `FileStream` | The stream type at the bottom of the file-system adapter. It is an exception only **inside** an adapter, where it is the adapter's whole job | 27 files, 37 sites. Adapter uses: `PhysicalFileSystem`, `PhysicalDoctorFileSystem`, `ExclusiveFileWriter`, `FileBoundedFileReader`, `FileCodeGraphIndexWorkspace` (lease) and Kernel `BoundedFileReader`; `TransactionLease` is adapter-shaped but task 3.4 moves it onto `ILease` | The file-system seam: `InMemoryFileSystem`, proven by the file-system contract suite |

`FileStream` needs a caveat. The design allows it only inside the file-system adapter, and the code
does not yet meet that: 20 non-adapter classes open a `FileStream` directly. They are
`Application/Docs/Scaffolding/StandardsFolderMigration.cs`, `Application/Update/SelfUpdater.cs`,
`Data/Arbiter/Facts/FileTextFacts.cs`, `GateReportFacts.cs` and `GitFacts.cs`,
`Data/Arbiter/Ledger/InFlightLedger.cs`, `Data/Hooks/Arbiter/FileHookSnapshotStore.cs` and
`FileWorkerBudgetStore.cs`, `Data/Squad/Deployment/SquadStateMigration.cs`, `SquadStateStore.cs`,
`SquadTransaction.cs`, `Data/Squad/Instances/SquadInstanceLinkSync.cs`,
`Data/Squad/Packaging/SquadPacker.cs`, `Data/Squad/Presets/SquadCanonicalProfileStore.cs` and
`SquadModelPresetSelectionStore.cs`, `Data/Squad/Release/GitHubSquadReleaseSource.cs`,
`Data/Squad/Rendering/CopilotMcpServerProbe.cs`, `Data/Update/GitHubReleaseClient.cs` and
`SelfUpdateSystem.cs`, and `Mcp/CodeGraphFreshness.cs`. Each is on its task's list in the table
above; `FileStream` is not a licence to bypass the seam.

Two more framework members are used directly but are not in the exception list because a seam
already covers them and must replace them:

- `Mutex` appears in `MutexLease` (the lease adapter) and in `TransactionLease`, which task 3.4
  moves onto `ILease`.
- `OperatingSystem.Is*` and `Environment.*` belong to `IProcessEnvironment`; a class outside the
  environment adapter that calls them is on the task list above. `Environment.NewLine` is a
  platform constant, not process state, and is not a seam violation.

## Keeping this register current

A change that adds or changes an adapter, a fake or a contract suite edits this page in the same
commit (requirement 3.2). A new sealed framework type is added to the exceptions register with its
reason before it is used directly (requirement 3.3). No build check enforces either rule (decision
D11); a reviewer raises a missing register entry as a finding.
