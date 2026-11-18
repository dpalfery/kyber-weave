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
console, the clock, identifiers, a lease, an embedded resource, configuration, the operating
system's credential store and the shipped content a test reads — is reached through one
interface, called a **seam**. This page is the
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
| File system | `IFileSystem`, `IWritableFileSystem` (`Contracts/Repositories`) | Text, bytes, lines, streams, length, last-write time, attributes; enumerate with a no-follow option; create directory; write; atomic write; atomic move with and without overwrite; exclusive create; open a new file for writing; open a file exclusively (a lock file); open a file for a shared append; move a directory; delete file and tree without following links; create link; one-hop and final link target; temporary directory | `PhysicalFileSystem` (`KyberWeave.Data`, `Hooks/PhysicalFileSystem.cs`) | `InMemoryFileSystem` (`FileSystem/InMemoryFileSystem.cs`) | `FileSystemContractTests` and `FileSystemFaultContractTests`: fake in `KyberWeave.Tests/FileSystem/InMemoryFileSystemTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/PhysicalFileSystemContractTests.cs` |
| Paths | `IPathResolver` (`Contracts/Paths`) | Full path against an explicit base; are-same; is-within; canonical spelling (case and links); physical root. No member reads the process working directory | `PhysicalPathResolver` (`KyberWeave.Data`, `Paths/PhysicalPathResolver.cs`) over `FileSystemPathResolver` | `FakePathResolver` (`Fakes/FakePathResolver.cs`) | `PathResolverContractTests`: fake in `KyberWeave.Tests/Paths/InMemoryPathResolverTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/PhysicalPathResolverContractTests.cs` |
| Process | `IProcessRunner`; `IProcessExecutor`, `IProgramRunner` and `IBoundedProcessExecutor` stay as role views | Run with arguments, working directory, environment, timeout and input; captured output; is-alive; find on path | `KernelProcessRunner` (`KyberWeave.Data`, `Hooks/KernelProcessRunner.cs`) over `KyberWeave.Kernel/Processes/ProcessRunner.cs` | `ScriptedProcessRunner` (`Fakes/ScriptedProcessRunner.cs`); `ScriptedProcessExecutor` (`Fakes/ScriptedProcessExecutor.cs`) for the role view | `ProcessRunnerContractTests`: fake in `KyberWeave.Tests/Processes/ScriptedProcessRunnerContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/KernelProcessRunnerContractTests.cs` |
| Environment and platform | `IProcessEnvironment` (`Contracts/Repositories`) | Environment variables; current, home and temporary directories; user and machine name; operating-system kind; case-insensitive file system; input redirected | `ProcessEnvironment` (`KyberWeave.Data`, `Host/ProcessEnvironment.cs`) | `FakeProcessEnvironment` (`Fakes/FakeProcessEnvironment.cs`) | `ProcessEnvironmentContractTests`: fake in `KyberWeave.Tests/Host/FakeProcessEnvironmentContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/PhysicalProcessEnvironmentContractTests.cs` |
| Time | `TimeProvider` (framework) | Wall clock; monotonic timestamp and elapsed time; timers; `Task.Delay(TimeSpan, TimeProvider)` | `TimeProvider.System`, registered in `Application/ServiceCollectionExtensions.cs` and `Application/Arbiter/ArbiterApplicationServiceCollectionExtensions.cs` | The framework's `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`), used in `KyberWeave.Tests/Composition/TimeProviderRegistrationTests.cs` and `KyberWeave.Tests/FileSystem/InMemoryFileSystemTests.cs` | None, and none is required: the fake is the framework package's, not a product fake, so requirement 5.4's fake-versus-adapter run has no product fake to run. Decision D5 chose the framework type and its tested fake over a new interface and a hand-written fake |
| Identifiers | `IIdGenerator` (`Contracts/Repositories`) | New GUID; random token; random file name; sortable record id for an instant | `SystemIdGenerator` (`KyberWeave.Data`, `Host/SystemIdGenerator.cs`) | `FakeIdGenerator` (`Fakes/FakeIdGenerator.cs`) | `IdGeneratorContractTests`: fake in `KyberWeave.Tests/Host/FakeProcessEnvironmentContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/PhysicalProcessEnvironmentContractTests.cs` |
| Console | `IConsole` (`Contracts/Host`) | Output and error writers; read line; input redirected | `SystemConsole` (`KyberWeave.Data`, `Host/SystemConsole.cs`); the standard-stream transport `DrainingStreamServerTransport` in `KyberWeave.Mcp` and `KyberWeave.Arbiter` | `FakeConsole` (`Fakes/FakeConsole.cs`) | `ConsoleContractTests`: fake in `KyberWeave.Tests/Console/FakeConsoleContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/SystemConsoleContractTests.cs` |
| Lease | `ILease` (`Contracts/Host`) | Acquire a named cross-process lease with a timeout; release from any thread | `MutexLease` (`KyberWeave.Data`, `Host/MutexLease.cs`); the lock-file variant is `FileCodeGraphIndexWorkspace` (`Data/Doctor/FileCodeGraphIndexWorkspace.cs`) | `InProcessLease` (`Fakes/InProcessLease.cs`) | `LeaseContractTests`: fake in `KyberWeave.Tests/Lease/InProcessLeaseContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/MutexLeaseContractTests.cs` |
| Embedded resource | `IEmbeddedResources` (`Contracts/Host`) | Read a named manifest resource as a stream or UTF-8 text | `AssemblyEmbeddedResources` (`KyberWeave.Data`, `Host/AssemblyEmbeddedResources.cs`) | `FakeEmbeddedResources` (`Fakes/FakeEmbeddedResources.cs`) | `EmbeddedResourceContractTests`: fake in `KyberWeave.Tests/EmbeddedResources/FakeEmbeddedResourcesContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/AssemblyEmbeddedResourcesContractTests.cs` |
| Configuration | `IKyberWeaveConfigRepository` (`Contracts/Repositories`) | Load and try-load the repository's and the user's host configuration; find the config path | `FileKyberWeaveConfigRepository` (`KyberWeave.Application`, `Configuration/FileKyberWeaveConfigRepository.cs`) over `IRepositoryTreeProbe` (`KyberWeave.Application`, `Review/FileRepositoryTreeProbe.cs`), `IFileSystem`, `IPathResolver` and `IProcessEnvironment` | `FakeKyberWeaveConfigRepository` (`Fakes/FakeKyberWeaveConfigRepository.cs`) | `KyberWeaveConfigRepositoryContractTests`: fake in `KyberWeave.Tests/Configuration/FakeKyberWeaveConfigRepositoryContractTests.cs`, adapter over the in-memory file system in `KyberWeave.Tests/Configuration/InMemoryFileKyberWeaveConfigRepositoryContractTests.cs`, and over the real disk in `KyberWeave.Integration.Tests/Contracts/FileKyberWeaveConfigRepositoryContractTests.cs` |
| Credentials | `ICredentialStore` (`KyberWeave.Domain`, `Arbiter/Credentials/ICredentialStore.cs`) | Read, write and delete one named secret per endpoint origin; a missing entry reads as null; deleting an absent entry succeeds; a store may refuse a key it cannot carry, and every key it accepts reads back unchanged; an origin or key with a line break is refused on every store | `OperatingSystemCredentialStore` picks `WindowsCredentialStore`, `MacKeychainCredentialStore` or `SecretServiceCredentialStore` (`KyberWeave.Data`, `Arbiter/Credentials/`) | `FakeCredentialStore` (`Fakes/FakeCredentialStore.cs`) | `CredentialStoreContractTests`: fake in `KyberWeave.Tests/Credentials/FakeCredentialStoreContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/OperatingSystemCredentialStoreContractTests.cs`, which skips where the host has no usable store |
| Shipped content (tests) | `ICorpusSource` (`tests/KyberWeave.Testing/Corpus/ICorpusSource.cs`) | Read the shipped agents, skills, standards, documents and workflows by relative path, contained in the corpus; host name-case rule | `CheckoutCorpusSource` (`tests/KyberWeave.Testing/Corpus/CheckoutCorpusSource.cs`), a test adapter over this checkout | `InMemoryCorpusSource` (`tests/KyberWeave.Testing/Corpus/InMemoryCorpusSource.cs`) | `CorpusSourceContractTests`: both against the adapter and the fake in `KyberWeave.RepoPolicy.Tests/CorpusSourceContractTests.cs` |

Notes.

- The narrow file interfaces (`IRepositoryFileStore`, `IDoctorFileSystem`, `ISquadFileAccess`,
  `ISquadLinkFileSystem`, `IFileStatReader`, `IExclusiveFileWriter`, `IContainedFileReader`) are role
  views over the file-system seam, so a class can ask for the write it needs and nothing more.
- The corpus seam is test infrastructure, not a product seam. It lives in the shared testing library
  so a content test says what it reads instead of walking the tree for the repository root.
- The configuration row is the one seam without a shared fake and contract suite. Task 2.3 converts
  its 23 callers and is where both belong.
- The credential store's integration run skips only when the host has no usable store. A read-only
  probe turns the typed unavailable failures into a skip, and every other failure fails the run. A
  probe that wrote would hide an adapter that writes but reads back wrong, because it would look
  unavailable and every contract test would skip.
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
| `UnixSquadLinkFileSystem` | File system | `KyberWeave.Data` | `Squad/Instances/UnixSquadLinkFileSystem.cs` | Symbolic links on Unix |
| `WindowsSquadLinkFileSystem` | File system | `KyberWeave.Data` | `Squad/Instances/WindowsSquadLinkFileSystem.cs` | Links on Windows, including the P/Invoke probe |
| `PhysicalMcpConfigFiles` | File system | `KyberWeave.Data` | `Docs/Mcp/PhysicalMcpConfigFiles.cs` | Reads, attributes and deletes MCP configuration files |
| `FileGitHookFiles` | File system | `KyberWeave.Data` | `Doctor/FileGitHookFiles.cs` | Hook files and executable bits |
| `FileStatusLineFiles` | File system | `KyberWeave.Data` | `Utilities/FileStatusLineFiles.cs` | Status-line artifact files |
| `FileCodeGraphIndexWorkspace` | Lease (file-lock variant) | `KyberWeave.Data` | `Doctor/FileCodeGraphIndexWorkspace.cs` | Holds the index lock as an exclusively opened file; its wait and poll run on the injected `TimeProvider` |
| `ExclusiveFileWriter` | File system | `KyberWeave.Data` | `Docs/Closeout/ExclusiveFileWriter.cs` | Exclusive create |
| `FileBoundedFileReader` | File system | `KyberWeave.Data` | `Review/FileBoundedFileReader.cs` | Reads up to a byte limit and refuses a file that grew |
| `FileHookFileWindowReader` | File system | `KyberWeave.Data` | `Hooks/Arbiter/FileHookFileWindowReader.cs` | Reads a line window from a file |
| `ArbiterLogFiles` | File system (`IArbiterLogStore`) | `KyberWeave.Data` | `Arbiter/Ledger/ArbiterLogFiles.cs` | The Arbiter's append-only log: one line per append under an exclusively opened lock file, plus the lock-free windowed read every ledger, decision log and run store shares |
| `FileStatReader` | File system | `KyberWeave.Data` | `Arbiter/FileStatReader.cs` | Length and last-write time |
| `FileLinkProbe` | File system | `KyberWeave.Data` | `Hooks/Arbiter/FileLinkProbe.cs` | Link target and link kind |
| `AtomicFile` | File system | `KyberWeave.Kernel` | `FileSystem/AtomicFile.cs` | The kernel-side atomic write, rename and link resolution the file-system adapter builds on |
| `BoundedFileReader` | File system | `KyberWeave.Kernel` | `FileSystem/BoundedFileReader.cs` | The kernel-side bounded stream read |
| `PhysicalPathResolver` | Paths | `KyberWeave.Data` | `Paths/PhysicalPathResolver.cs` | The path adapter |
| `FileSystemPathResolver` | Paths | `KyberWeave.Data` | `Paths/FileSystemPathResolver.cs` | The base implementation over the file-system seam |
| `SquadFileSystemPathSemantics` | Paths | `KyberWeave.Data` | `Squad/Deployment/SquadFileSystemPathSemantics.cs` | Instance singleton the deployment code shares; it implements `IPathResolver` and `IFileSystemPathSemantics` |
| `KernelProcessRunner` | Process | `KyberWeave.Data` | `Hooks/KernelProcessRunner.cs` | The product's one `IProcessRunner` |
| `ProcessRunner` | Process | `KyberWeave.Kernel` | `Processes/ProcessRunner.cs` | The one place that calls `Process.Start`; `KernelProcessRunner` and the role-view adapters build on it |
| `StdioSession` | Process | `KyberWeave.Kernel` | `Processes/StdioSession.cs` | Interactive stdin/stdout session; its budget and read wait run on the injected `TimeProvider` |
| `ProcessProgramRunner` | Process | `KyberWeave.Data` | `Review/ProcessProgramRunner.cs` | The `IProgramRunner` role view |
| `KernelBoundedProcessExecutor` | Process | `KyberWeave.Data` | `Docs/FileSystem/KernelBoundedProcessExecutor.cs` | The `IBoundedProcessExecutor` role view |
| `ProcessGitCommandRunner` | Process | `KyberWeave.Data` | `RunStart/ProcessGitCommandRunner.cs` | Git with standard output and error kept apart |
| `ProcessRunnerCredentialProcessRunner` | Process | `KyberWeave.Data` | `Arbiter/Credentials/ProcessRunnerCredentialProcessRunner.cs` | The credential process runner over `IProcessRunner` |
| `ProcessSquadHarnessLauncher` | Process | `KyberWeave.Data` | `Squad/Instances/ProcessSquadHarnessLauncher.cs` | Starts the interactive harness; builds its start information through the static `SquadHarnessLauncher` |
| `ProcessExecutor` | Process | `KyberWeave.Cli` | `Commands/Squad/Infrastructure/IProcessExecutor.cs` | The CLI's `IProcessExecutor` role view |
| `MacKeychainCredentialStore` | Credentials | `KyberWeave.Data` | `Arbiter/Credentials/MacKeychainCredentialStore.cs` | Invokes `security` through `ICredentialProcessRunner`, so it reaches the process seam too. `Write` refuses a non-ASCII key: `security` prints such a password as hex, so it would not read back |
| `SecretServiceCredentialStore` | Credentials | `KyberWeave.Data` | `Arbiter/Credentials/SecretServiceCredentialStore.cs` | Invokes `secret-tool` through `ICredentialProcessRunner`, so it reaches the process seam too |
| `WindowsCredentialStore` | Credentials | `KyberWeave.Data` | `Arbiter/Credentials/WindowsCredentialStore.cs` | P/Invoke `advapi32`, gated on Windows; the only code that calls the native credential API |
| `OperatingSystemCredentialStore` | Credentials | `KyberWeave.Data` | `Arbiter/Credentials/OperatingSystemCredentialStore.cs` | Picks the store the platform offers from `IProcessEnvironment.OperatingSystem`; calls no credential resource itself |
| `ProcessEnvironment` | Environment and platform | `KyberWeave.Data` | `Host/ProcessEnvironment.cs` | The environment and platform adapter |
| `SystemIdGenerator` | Identifiers | `KyberWeave.Data` | `Host/SystemIdGenerator.cs` | GUIDs, random tokens and temp names |
| `SystemConsole` | Console | `KyberWeave.Data` | `Host/SystemConsole.cs` | Console output, error and input |
| `DrainingStreamServerTransport` | Console (standard streams) | `KyberWeave.Mcp` | `DrainingStreamServerTransport.cs` | Opens the standard streams for the MCP server |
| `DrainingStreamServerTransport` | Console (standard streams) | `KyberWeave.Arbiter` | `Mcp/DrainingStreamServerTransport.cs` | Opens the standard streams for the Arbiter host |
| `MutexLease` | Lease | `KyberWeave.Data` | `Host/MutexLease.cs` | The named-mutex lease |
| `ConsoleArbiterKeyInput` | Console | `KyberWeave.Cli` | `Commands/Arbiter/ConsoleArbiterKeyInput.cs` | The `IArbiterKeyInput` adapter: the only code that reads a key from standard input or from a masked Spectre prompt, and it reaches both through the `IConsole` and `IAnsiConsole` seams it is given |
| `AssemblyEmbeddedResources` | Embedded resource | `KyberWeave.Data` | `Host/AssemblyEmbeddedResources.cs` | Manifest resource reads |
| `FileKyberWeaveConfigRepository` | Configuration | `KyberWeave.Application` | `Configuration/FileKyberWeaveConfigRepository.cs` | Configuration load and try-load over the tree, file-system, path and environment seams |
| `CheckoutCorpusSource` | Shipped content (tests) | `KyberWeave.Testing` | `Corpus/CheckoutCorpusSource.cs` | The test-side adapter over this checkout |

The credential seam's interface is the one seam still outside `Contracts`: it lives in
`KyberWeave.Domain/Arbiter/Credentials/ICredentialStore.cs`, under the namespace
`KyberWeave.Core.Arbiter.Credentials`. The domain-move inventory schedules that file for
`Contracts` with the phase-2 layer work; until then this register names where it actually is.
The two process-backed stores reach the process seam through `ICredentialProcessRunner`, and the
Windows store is the only code that calls the native credential API. Every caller takes
`ICredentialStore`, or a `Func<ICredentialStore>` where building the store at call time is
deliberate: a host with no keychain absorbs the construction failure there instead of failing
startup.

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
| `SquadCliServices` | `KyberWeave.Cli` | `Composition/SquadCliServices.cs` |
| `Program` | `KyberWeave.Mcp` | `Program.cs` |
| `DocsToolsComposition` | `KyberWeave.Mcp` | `DocsToolsComposition.cs` |
| `Program` | `KyberWeave.Hooks` | `Program.cs` |
| `Program` | `KyberWeave.Arbiter` | `Program.cs` |

### Classes still calling a resource directly

Task 1.8 does not convert any of these; it lists them so phase 2, phase 3 and phase 4 have a
worklist and so a reviewer can tell an adapter from a violation. A task number is the task whose
file list or folder covers the class. **`unassigned` would mean the approved task list names no task
for that class.** The register does not invent coverage: the gaps are listed after the tables and
task 1.8's acceptance criterion is not met for them until `tasks.md` assigns an owner.

Two rows are adapters for another seam whose remaining direct call is the clock. They are counted
here, with the task that converts it (4.9), because otherwise a test through the adapter still reads the real
clock and no task would track the fix.

| Project | Classes calling a resource | Direct-call sites | Tasks |
|---|---|---|---|
| `KyberWeave.Application` | 68 | 208 | 4.1, 4.2, 4.3, 4.8 |
| `KyberWeave.Cli` | 49 | 180 | 4.5 |
| `KyberWeave.Data` | 73 | 663 | 3.3, 3.4, 3.5, 4.4, 4.9 |
| `KyberWeave.Domain` | 24 | 46 | 2.5 |
| `KyberWeave.Hooks` | 1 | 1 | 4.7 |
| `KyberWeave.Kernel` | 3 | 22 | 4.9 |
| `KyberWeave.Mcp` | 4 | 16 | 4.6 |
| `KyberWeave.Arbiter` | 0 | 0 | — (its two files are the Program root and the console-stream adapter) |
| **Total** | **220** | **1,136** | |

The totals before adapters and roots are taken out are 68 classes and 212 sites in Application,
52 and 183 in Cli, 111 and 955 in Data, 28 and 49 in Domain, 2 and 3 in Hooks, 6 and 47 in Kernel,
7 and 28 in Mcp, and 2 and 6 in Arbiter. `KyberWeave.Contracts` reports six files and eight sites,
all of them `ProcessStartInfo` in an interface or a request record; it holds no implementation.
`Environment.NewLine` sites are excluded from both sets of numbers.

#### KyberWeave.Application (68 classes)

| Class (file) | Calls directly | Task |
|---|---|---|
| `ArbiterPlanService` (`Arbiter/ArbiterPlanService.cs`) | paths | 4.3 |
| `ArbiterSectionSelectionService` (`Arbiter/ArbiterSectionSelectionService.cs`) | file system | 4.3 |
| `ArbiterHookDecisionEngine` (`Arbiter/Hooks/ArbiterHookDecisionEngine.cs`) | paths | 4.3 |
| `ArbiterProvenance` (`Arbiter/Hooks/ArbiterProvenance.cs`) | paths | 2.5 |
| `ConductorGuard` (`Arbiter/Hooks/ConductorGuard.cs`) | paths | 4.3 |
| `ConductorGuardRules` (`Arbiter/Hooks/ConductorGuardRules.cs`) | paths | 2.5 |
| `WorkerGuard` (`Arbiter/Hooks/WorkerGuard.cs`) | file system, paths | 4.3 |
| `OllamaProbe` (`Arbiter/OllamaProbe.cs`) | network | 4.3 |
| `KyberStandardsTemplates` (`Docs/Scaffolding/KyberStandardsTemplates.cs`) | file system | 4.2 |
| `TaskPromptService` (`Docs/Validation/TaskPromptService.cs`) | file system, paths | 4.2 |
| `CodeGraphIndexRules` (`Doctor/CodeGraphIndexRules.cs`) | paths | 2.5 |
| `GateCatalogueRenderer` (`Review/Catalogue/GateCatalogueRenderer.cs`) | file system | 2.5 |
| `ProjectRootDetection` (`Review/Catalogue/ProjectRootDetection.cs`) | file system | 2.5 |
| `PlanTaskTargetService` (`Review/PlanTaskTargetService.cs`) | file system, paths | 4.3 |
| `ReviewCycleService` (`Review/ReviewCycleService.cs`) | paths | 4.3 |
| `ReviewSeatsService` (`Review/ReviewSeatsService.cs`) | paths | 4.3 |
| `ReviewWorkTreeService` (`Review/ReviewWorkTreeService.cs`) | file system, paths | 4.3 |
| `TaskGateResolver` (`Review/TaskGateResolver.cs`) | file system | 4.3 |
| `RunBaseStateFormat` (`RunStart/RunBaseStateFormat.cs`) | paths | 2.5 |
| `GitHubReleaseClient` (`Update/GitHubReleaseClient.cs`) | network | 4.4 |
| `ArbiterConfigLoader` (`Arbiter/ArbiterConfigLoader.cs`) | file system | 2.5 |
| `ArbiterSquadCatalog` (`Arbiter/Squad/ArbiterSquadCatalog.cs`) | file system | 2.5 |
| `DocsAnalysisConfigLoader` (`Configuration/DocsAnalysisConfigLoader.cs`) | network | 2.5 |

#### KyberWeave.Cli (14 classes)

| Class (file) | Calls directly | Task |
|---|---|---|
| `ArbiterAuditCommand` (`Commands/Arbiter/ArbiterAuditCommand.cs`) | paths | 4.5 |
| `ArbiterCommentCommand` (`Commands/Arbiter/ArbiterCommentCommand.cs`) | file system, paths | 4.5 |
| `ArbiterSectionsCommand` (`Commands/Arbiter/ArbiterSectionsCommand.cs`) | file system, paths | 4.5 |
| `CommandHelpers` (`Commands/CommandHelpers.cs`) | file system, paths | 4.5 |
| `RunDeliverCommand` (`Commands/Run/RunDeliverCommand.cs`) | paths | 4.5 |
| `CopilotPromptCommands` (`Commands/Squad/CopilotPromptCommands.cs`) | file system | 4.5 |
| `SelfUpdateHostFactory` (`Composition/SelfUpdateHostFactory.cs`) | environment | 4.5 |
| `ReportRenderer` (`Rendering/ReportRenderer.cs`) | console | 4.5 |

#### KyberWeave.Data (72 classes)

| Class (file) | Calls directly | Task |
|---|---|---|
| `SystemOneClient` (`Arbiter/Providers/SystemOneClient.cs`) | clock, network | 4.4 |
| `SocketArbiterHttpHandlers` (`Arbiter/SocketArbiterHttpHandlers.cs`) | network | 4.4 |
| `SystemOneProviderFactory` (`Arbiter/SystemOneProviderFactory.cs`) | network | 4.4 |
| `CodeGraphResolverAdapter` (`CodeGraph/CodeGraphResolverAdapter.cs`) | file system, paths | 4.4 |
| `OpenAiCompatibleEmbeddingGenerator` (`Docs/Analysis/Embeddings/OpenAiCompatibleEmbeddingGenerator.cs`) | environment, network | 4.4 |
| `AnalysisCacheSafety` (`Docs/Analysis/Persistence/AnalysisCacheSafety.cs`) | file system, paths | 4.4 |
| `SqliteAnalysisPersistence` (`Docs/Analysis/Persistence/SqliteAnalysisPersistence.cs`) | file system, paths | 4.4 |
| `PresentFile` (`Docs/Closeout/PresentFile.cs`) | file system | 4.4 |
| `DocsRootWalk` (`FileSystem/DocsRootWalk.cs`) | file system | 4.4 |
| `FileCoverageReportSource` (`Review/FileCoverageReportSource.cs`) | file system | 4.4 |
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
| `SquadSourceLoader` (`Squad/Parsing/SquadSourceLoader.cs`) | file system, paths | 3.5 |
| `SquadCanonicalProfileStore` (`Squad/Presets/SquadCanonicalProfileStore.cs`) | file system, identifiers | 3.5 |
| `SquadModelPresetStore` (`Squad/Presets/SquadModelPresetStore.cs`) | file system | 3.5 |
| `SquadModelPresetValidator` (`Squad/Presets/SquadModelPresetValidator.cs`) | file system | 3.5 |
| `LocalCheckoutSquadReleaseSource` (`Squad/Release/LocalCheckoutSquadReleaseSource.cs`) | file system, paths | 3.5 |
| `CopilotMcpServerProbe` (`Squad/Rendering/CopilotMcpServerProbe.cs`) | file system | 3.5 |
| `FileSquadRenderInputReader` (`Squad/Rendering/FileSquadRenderInputReader.cs`) | file system, environment | 3.5 |
| `SquadOperatorChannelOverlayFile` (`Squad/Rendering/SquadOperatorChannelOverlayFile.cs`) | file system | 3.5 |
| `SelfUpdateSystem` (`Update/SelfUpdateSystem.cs`) | file system, process, identifiers | 4.4 |

Task 4.4 leaves these in `Data` outside `Squad/`, each for a named reason:

- `FileHookSnapshotStore` and `FileWorkerBudgetStore` are converted: the file-system seam gained
  `OpenExclusive` (a read-write open that keeps every other opener out until disposed) and
  `OpenAppendShared` (an append that other appenders and readers share), proven on the fake and on
  `PhysicalFileSystem` by the file-system contract suite.
- `SelfUpdateSystem` replaces the running binary and extracts archives, so requirement 10.2 applies: it
  moves with crash-safety contract tests and a second reviewer, with task 4.1's `SelfUpdater`.
- `AnalysisCacheSafety`, `SqliteAnalysisPersistence` and `CodeGraphResolverAdapter` are built with `new` or
  held as a static delegate by `KyberWeave.Cli` and `KyberWeave.Application` callers, so converting them
  edits those projects' composition code (tasks 4.5 and 4.2).
- `SystemOneClient`, `SocketArbiterHttpHandlers`, `SystemOneProviderFactory` and
  `OpenAiCompatibleEmbeddingGenerator` reach the network; no network seam exists yet.
- `PresentFile` and `DocsRootWalk` are static helpers of the adapter `PhysicalRepositoryFileStore`
  (the bounded one-step read and the link-refusing walk). Only that adapter calls them: application code
  reaches the walk through `IRepositoryFileStore.EnumerateContainedFiles` and `IsContainedFile`.
- `FileCoverageReportSource` lives in `Application/Review`, which another task owns.
- `ZipSkillArchiveWriter` is the adapter of `ISkillArchiveWriter`. `ZipFile.CreateEntryFromFile` records the
  source file's Unix permission bits in the archive; the file-system seam has no member that reads them, so
  reading entries through `OpenRead` would change the archive's contents.

#### KyberWeave.Domain (24 classes)

| Class (file) | Calls directly | Task |
|---|---|---|
| `SquadGlobalRoots` (`Squad/Deployment/SquadGlobalRoots.cs`) | paths | 2.5 |
| `SquadPathPolicy` (`Squad/Deployment/SquadPathPolicy.cs`) | paths | 2.5 |
| `SquadResponseGuidanceSource` (`Squad/Deployment/SquadResponseGuidanceSource.cs`) | file system | 2.5 |

#### KyberWeave.Hooks (1 class)

| Class (file) | Calls directly | Task |
|---|---|---|

#### KyberWeave.Kernel (3 classes)

None left. `FileReferenceExtractor` moved to `KyberWeave.Application` (`Parsing/FileReferenceExtractor.cs`) and `CodeGraphIndexStaleness` to `KyberWeave.Data` (`CodeGraph/CodeGraphIndexStaleness.cs`), both over the file-system seam (and the path and process seams); `StdioSession` is the interactive process adapter and runs on `TimeProvider`.

#### KyberWeave.Mcp (4 classes)

| Class (file) | Calls directly | Task |
|---|---|---|

## Gaps in the task list

The gaps this register found on 2026-11-17 were closed by amending the task list: the 12
`Application` classes without a folder task are task 4.8; the two `Kernel` classes and the clock
inside `FileCodeGraphIndexWorkspace` and `StdioSession` are task 4.9; the native credential store
becomes the adapter of a new credential seam in task 4.10; and the shared configuration fake and
its contract suite are part of task 2.3. A new gap is recorded here until the task list names it.

## The exceptions register

A framework type that is sealed and has no interface may be used directly (requirement 3.3). The
type is only an exception from the seam, never from the rule that a test must be able to control
the behaviour: where a test observes the value, the seam still wraps it.

| Type | Why it stays | Where the code uses it | What a test controls |
|---|---|---|---|
| `JsonDocument` | Parses a string or byte payload the caller already holds. It opens no resource and holds no state a test needs to replace | 32 files, 53 sites; most in `Application/Docs/Mcp` and `Data/Squad` | The input text or bytes; the parse result is deterministic |
| `ZipArchive` | A sealed reader and writer over a stream. The stream is the resource, not the archive | 3 files, 8 sites: `Application/Squad/Release/GitHubSquadReleaseSource.cs`, `Application/Squad/Packaging/SquadPacker.cs`, `Data/Skills/ZipSkillArchiveWriter.cs` | The stream: a `MemoryStream` holding a fixture archive |
| `SHA256` | A pure hash over bytes. Two calls on the same bytes return the same digest, so a test never needs to replace it | 40 files, 51 sites, from `Arbiter` through `Squad` to `SelfUpdater` | The bytes hashed; the digest is asserted directly |
| `ProcessStartInfo` | A sealed data transfer object handed to `IProcessRunner`, `IProcessExecutor`, `IProgramRunner` or a delegate. It starts nothing by itself; the runner does | 39 files, 66 sites; production process starts remain in `Kernel/Processes/ProcessRunner.cs` and `StdioSession` | A scripted fake asserts the program, arguments, working directory and environment it receives |
| `HttpClient` | A sealed network client. The transport is the seam: an injected `HttpMessageHandler` or `IArbiterHttpHandlers`, or a named network adapter | 9 files, 16 sites: the four network adapters (`SystemOneClient`, `GitHubReleaseClient`, `GitHubSquadReleaseSource`, `OpenAiCompatibleEmbeddingGenerator`), `OllamaProbe` over `IArbiterHttpHandlers`, `SelfUpdater` over an injected handler, and two factories that pass the handler on | The handler or the network adapter; no test opens a socket |
| `FileStream` | The stream type at the bottom of the file-system adapter. It is an exception only **inside** an adapter, where it is the adapter's whole job | 27 files, 37 sites. Adapter uses: `PhysicalFileSystem`, `PhysicalDoctorFileSystem`, `ExclusiveFileWriter`, `FileBoundedFileReader`, `FileCodeGraphIndexWorkspace` (lease) and Kernel `BoundedFileReader`; `TransactionLease` is adapter-shaped but task 3.4 moves it onto `ILease` | The file-system seam: `InMemoryFileSystem`, proven by the file-system contract suite |

`FileStream` needs a caveat. The design allows it only inside the file-system adapter, and the code
does not yet meet that: 15 non-adapter classes open a `FileStream` directly. They are
`Application/Update/SelfUpdater.cs`,
`Data/Squad/Deployment/SquadTransaction.cs`, `Data/Squad/Instances/SquadInstanceLinkSync.cs`,
`Data/Squad/Presets/SquadCanonicalProfileStore.cs` and
`SquadModelPresetSelectionStore.cs`,
`Data/Squad/Rendering/CopilotMcpServerProbe.cs`, `Data/Update/SelfUpdateSystem.cs`, and `Mcp/CodeGraphFreshness.cs`. Each is on its task's list in the table
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
