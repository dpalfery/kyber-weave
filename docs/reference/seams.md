---
id: reference/seams
title: Seams, adapters and the exceptions they keep
doc-type: reference
status: current
owner: dpalfery
last-reviewed: 2026-11-18
component: CI Pipelines
---

# Seams, adapters and the exceptions they keep

Every outside resource the product touches — the disk, a child process, the environment, the
console, the clock, identifiers, a lease, an embedded resource, configuration, the network, the operating
system's credential store and the shipped content a test reads — is reached through one
interface, called a **seam**. This page is the
register the interface-driven unit tests specification requires: the seams, the adapters that
implement them, the fake and contract suite that prove each adapter, every class still calling a
resource directly and the task that converts it, and the sealed framework types allowed through
with a reason.

Read from this branch on 2026-11-18, at the end of the interface-driven unit tests work, and
reconciled then: the counts and the rows below are the same set of classes. The seams and adapters
come from the code, not from the design. The counts come from
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
| File system | `IFileSystem`, `IWritableFileSystem` (`Contracts/Repositories`) | Text, bytes, lines, streams, length, last-write time, attributes; enumerate with a no-follow option; create directory; write; atomic write; atomic move with and without overwrite; exclusive create; open a new file for writing; open a file exclusively (a lock file); open a file for a shared append; move a directory; delete file and tree without following links; create link; one-hop and final link target; flush a stream it opened to stable storage; temporary directory | `PhysicalFileSystem` (`KyberWeave.Data`, `Hooks/PhysicalFileSystem.cs`) | `InMemoryFileSystem` (`FileSystem/InMemoryFileSystem.cs`) | `FileSystemContractTests` and `FileSystemFaultContractTests`: fake in `KyberWeave.Tests/FileSystem/InMemoryFileSystemTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/PhysicalFileSystemContractTests.cs` |
| Paths | `IPathResolver` (`Contracts/Paths`) | Full path against an explicit base; are-same; is-within; canonical spelling (case and links); physical root. No member reads the process working directory | `PhysicalPathResolver` (`KyberWeave.Data`, `Paths/PhysicalPathResolver.cs`) over `FileSystemPathResolver` | `FakePathResolver` (`Fakes/FakePathResolver.cs`) | `PathResolverContractTests`: fake in `KyberWeave.Tests/Paths/InMemoryPathResolverTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/PhysicalPathResolverContractTests.cs` |
| Process | `IProcessRunner`; `IProcessExecutor`, `IProgramRunner` and `IBoundedProcessExecutor` stay as role views | Run with arguments, working directory, environment, timeout and input; captured output; is-alive; find on path | `KernelProcessRunner` (`KyberWeave.Data`, `Hooks/KernelProcessRunner.cs`) over `KyberWeave.Kernel/Processes/ProcessRunner.cs` | `ScriptedProcessRunner` (`Fakes/ScriptedProcessRunner.cs`); `ScriptedProcessExecutor` (`Fakes/ScriptedProcessExecutor.cs`) for the role view | `ProcessRunnerContractTests`: fake in `KyberWeave.Tests/Processes/ScriptedProcessRunnerContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/KernelProcessRunnerContractTests.cs` |
| Environment and platform | `IProcessEnvironment` (`Contracts/Repositories`) | Environment variables; current, home and temporary directories; user and machine name; operating-system kind; case-insensitive file system; input redirected | `ProcessEnvironment` (`KyberWeave.Data`, `Host/ProcessEnvironment.cs`) | `FakeProcessEnvironment` (`Fakes/FakeProcessEnvironment.cs`) | `ProcessEnvironmentContractTests`: fake in `KyberWeave.Tests/Host/FakeProcessEnvironmentContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/PhysicalProcessEnvironmentContractTests.cs` |
| Time | `TimeProvider` (framework) | Wall clock; monotonic timestamp and elapsed time; timers; `Task.Delay(TimeSpan, TimeProvider)` | `TimeProvider.System`, registered in `Application/ServiceCollectionExtensions.cs` and `Application/Arbiter/ArbiterApplicationServiceCollectionExtensions.cs` | The framework's `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`), used in `KyberWeave.Tests/Composition/TimeProviderRegistrationTests.cs` and `KyberWeave.Tests/FileSystem/InMemoryFileSystemTests.cs` | None, and none is required: the fake is the framework package's, not a product fake, so requirement 5.4's fake-versus-adapter run has no product fake to run. Decision D5 chose the framework type and its tested fake over a new interface and a hand-written fake |
| Identifiers | `IIdGenerator` (`Contracts/Repositories`) | New GUID; random token; random file name; sortable record id for an instant | `SystemIdGenerator` (`KyberWeave.Data`, `Host/SystemIdGenerator.cs`) | `FakeIdGenerator` (`Fakes/FakeIdGenerator.cs`) | `IdGeneratorContractTests`: fake in `KyberWeave.Tests/Host/FakeProcessEnvironmentContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/PhysicalProcessEnvironmentContractTests.cs` |
| Console | `IConsole` (`Contracts/Host`) | Output and error writers; read line; input redirected | `SystemConsole` (`KyberWeave.Data`, `Host/SystemConsole.cs`); the standard-stream transport `DrainingStreamServerTransport` in `KyberWeave.Mcp` and `KyberWeave.Arbiter` | `FakeConsole` (`Fakes/FakeConsole.cs`) | `ConsoleContractTests`: fake in `KyberWeave.Tests/Console/FakeConsoleContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/SystemConsoleContractTests.cs` |
| Lease | `ILease` (`Contracts/Host`) | Acquire a named cross-process lease with a timeout; release from any thread | `MutexLease` (`KyberWeave.Data`, `Host/MutexLease.cs`); the lock-file variant is `FileCodeGraphIndexWorkspace` (`Data/Doctor/FileCodeGraphIndexWorkspace.cs`) | `InProcessLease` (`Fakes/InProcessLease.cs`) | `LeaseContractTests`: fake in `KyberWeave.Tests/Lease/InProcessLeaseContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/MutexLeaseContractTests.cs` |
| Embedded resource | `IEmbeddedResources` (`Contracts/Host`) | Read a named manifest resource as a stream or UTF-8 text | `AssemblyEmbeddedResources` (`KyberWeave.Data`, `Host/AssemblyEmbeddedResources.cs`) | `FakeEmbeddedResources` (`Fakes/FakeEmbeddedResources.cs`) | `EmbeddedResourceContractTests`: fake in `KyberWeave.Tests/EmbeddedResources/FakeEmbeddedResourcesContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/AssemblyEmbeddedResourcesContractTests.cs` |
| Configuration | `IKyberWeaveConfigRepository` (`Contracts/Repositories`) | Load and try-load the repository's and the user's host configuration; find the config path | `FileKyberWeaveConfigRepository` (`KyberWeave.Application`, `Configuration/FileKyberWeaveConfigRepository.cs`) over `IRepositoryTreeProbe` (`KyberWeave.Application`, `Review/FileRepositoryTreeProbe.cs`), `IFileSystem`, `IPathResolver` and `IProcessEnvironment` | `FakeKyberWeaveConfigRepository` (`Fakes/FakeKyberWeaveConfigRepository.cs`) | `KyberWeaveConfigRepositoryContractTests`: fake in `KyberWeave.Tests/Configuration/FakeKyberWeaveConfigRepositoryContractTests.cs`, adapter over the in-memory file system in `KyberWeave.Tests/Configuration/InMemoryFileKyberWeaveConfigRepositoryContractTests.cs`, and over the real disk in `KyberWeave.Integration.Tests/Contracts/FileKyberWeaveConfigRepositoryContractTests.cs` |
| Credentials | `ICredentialStore` (`KyberWeave.Contracts`, `Arbiter/ICredentialStore.cs`) | Read, write and delete one named secret per endpoint origin; a missing entry reads as null; deleting an absent entry succeeds; a store may refuse a key it cannot carry, and every key it accepts reads back unchanged; an origin or key with a line break is refused on every store | `OperatingSystemCredentialStore` picks `WindowsCredentialStore`, `MacKeychainCredentialStore` or `SecretServiceCredentialStore` (`KyberWeave.Data`, `Arbiter/Credentials/`) | `FakeCredentialStore` (`Fakes/FakeCredentialStore.cs`) | `CredentialStoreContractTests`: fake in `KyberWeave.Tests/Credentials/FakeCredentialStoreContractTests.cs`, adapter in `KyberWeave.Integration.Tests/Contracts/OperatingSystemCredentialStoreContractTests.cs`, which skips where the host has no usable store |
| Network | `HttpMessageHandler` (framework); `IEmbeddingTransport` for the loopback-pinned embedding endpoint (`Contracts/Repositories`) | A client takes a handler by constructor and wraps it in a private `HttpClient`; request shape, headers, status handling and cancellation are tested through the handler. The embedding transport adds the handler and name resolution the loopback check needs | `HttpClientHandler` built by `SocketArbiterHttpHandlers` and `SocketSquadReleaseHttpHandlers`; `LoopbackEmbeddingTransport` (`KyberWeave.Data`, `Docs/Analysis/Embeddings/LoopbackEmbeddingTransport.cs`) | `FakeHttpMessageHandler` (`Fakes/FakeHttpMessageHandler.cs`): scripted responses, a hanging or failing server, requests recorded with their bodies | None, and none is required: the handler is the framework's, and the only product adapter that adds behaviour (the loopback pin) is proven against a real loopback socket in `KyberWeave.Integration.Tests/LoopbackEmbeddingTransportTests.cs` |
| Shipped content (tests) | `ICorpusSource` (`tests/KyberWeave.Testing/Corpus/ICorpusSource.cs`) | Read the shipped agents, skills, standards, documents and workflows by relative path, contained in the corpus; host name-case rule | `CheckoutCorpusSource` (`tests/KyberWeave.Testing/Corpus/CheckoutCorpusSource.cs`), a test adapter over this checkout | `InMemoryCorpusSource` (`tests/KyberWeave.Testing/Corpus/InMemoryCorpusSource.cs`) | `CorpusSourceContractTests`: both against the adapter and the fake in `KyberWeave.RepoPolicy.Tests/CorpusSourceContractTests.cs` |

Notes.

- The narrow file interfaces (`IRepositoryFileStore`, `IDoctorFileSystem`, `ISquadFileAccess`,
  `ISquadLinkFileSystem`, `IFileStatReader`, `IExclusiveFileWriter`, `IContainedFileReader`) are role
  views over the file-system seam, so a class can ask for the write it needs and nothing more.
- The corpus seam is test infrastructure, not a product seam. It lives in the shared testing library
  so a content test says what it reads instead of walking the tree for the repository root.
- The configuration seam has a shared fake and a contract suite (task 2.3): the fake runs in a unit
  project and the adapter over the in-memory file system there too, and over the real disk in the
  integration project.
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
| `ProcessSquadHarnessLauncher` | Process | `KyberWeave.Data` | `Squad/Instances/ProcessSquadHarnessLauncher.cs` | Starts the interactive harness; builds its start information through the `SquadHarnessLauncher` instance it is given |
| `ProcessExecutor` | Process | `KyberWeave.Cli` | `Commands/Squad/Infrastructure/IProcessExecutor.cs` | The CLI's `IProcessExecutor` role view |
| `MacKeychainCredentialStore` | Credentials | `KyberWeave.Data` | `Arbiter/Credentials/MacKeychainCredentialStore.cs` | Invokes `security` through `ICredentialProcessRunner`, so it reaches the process seam too. `Write` refuses a non-ASCII key: `security` prints such a password as hex, so it would not read back |
| `SecretServiceCredentialStore` | Credentials | `KyberWeave.Data` | `Arbiter/Credentials/SecretServiceCredentialStore.cs` | Invokes `secret-tool` through `ICredentialProcessRunner`, so it reaches the process seam too |
| `WindowsCredentialStore` | Credentials | `KyberWeave.Data` | `Arbiter/Credentials/WindowsCredentialStore.cs` | P/Invoke `advapi32`, gated on Windows; the only code that calls the native credential API |
| `OperatingSystemCredentialStore` | Credentials | `KyberWeave.Data` | `Arbiter/Credentials/OperatingSystemCredentialStore.cs` | Picks the store the platform offers from `IProcessEnvironment.OperatingSystem`; calls no credential resource itself |
| `ProcessEnvironment` | Environment and platform | `KyberWeave.Data` | `Host/ProcessEnvironment.cs` | The environment and platform adapter |
| `SystemIdGenerator` | Identifiers | `KyberWeave.Data` | `Host/SystemIdGenerator.cs` | GUIDs, random tokens and temp names |
| `SystemConsole` | Console | `KyberWeave.Data` | `Host/SystemConsole.cs` | Console output, error and input |
| `ProcessConsoleLauncher` | Process | `KyberWeave.Data` | `Update/ProcessConsoleLauncher.cs` | Starts an update child that shares this console, bounded; the only update code that calls `Process.Start` |
| `UnixUpdateFileModes` | File permissions | `KyberWeave.Data` | `Update/UnixUpdateFileModes.cs` | Reads and sets the Unix permission bits of an installed binary |
| `DrainingStreamServerTransport` | Console (standard streams) | `KyberWeave.Mcp` | `DrainingStreamServerTransport.cs` | Opens the standard streams for the MCP server |
| `DrainingStreamServerTransport` | Console (standard streams) | `KyberWeave.Arbiter` | `Mcp/DrainingStreamServerTransport.cs` | Opens the standard streams for the Arbiter host |
| `MutexLease` | Lease | `KyberWeave.Data` | `Host/MutexLease.cs` | The named-mutex lease |
| `ConsoleArbiterKeyInput` | Console | `KyberWeave.Cli` | `Commands/Arbiter/ConsoleArbiterKeyInput.cs` | The `IArbiterKeyInput` adapter: the only code that reads a key from standard input or from a masked Spectre prompt, and it reaches both through the `IConsole` and `IAnsiConsole` seams it is given |
| `AssemblyEmbeddedResources` | Embedded resource | `KyberWeave.Data` | `Host/AssemblyEmbeddedResources.cs` | Manifest resource reads |
| `FileKyberWeaveConfigRepository` | Configuration | `KyberWeave.Application` | `Configuration/FileKyberWeaveConfigRepository.cs` | Configuration load and try-load over the tree, file-system, path and environment seams |
| `SocketArbiterHttpHandlers` | Network | `KyberWeave.Data` | `Arbiter/SocketArbiterHttpHandlers.cs` | The Arbiter's real HTTP handler; the only place that builds it |
| `LoopbackEmbeddingTransport` | Network | `KyberWeave.Data` | `Docs/Analysis/Embeddings/LoopbackEmbeddingTransport.cs` | Sockets that connect only to loopback (the check repeats at connect time) and system name resolution for the embedding endpoint |
| `CheckoutCorpusSource` | Shipped content (tests) | `KyberWeave.Testing` | `Corpus/CheckoutCorpusSource.cs` | The test-side adapter over this checkout |
| `SquadDeploymentAdapters` | File system, paths, identifiers | `KyberWeave.Data` | `Squad/Deployment/SquadDeploymentAdapters.cs` | Small delegates the deployment code takes where it needs one operation: existence, read, write, temp name, candidate roots |
| `LocalCheckoutSquadReleaseSource` | Shipped release (tests and `squad pack` from a checkout) | `KyberWeave.Data` | `Squad/Release/LocalCheckoutSquadReleaseSource.cs` | Copies a release from a local checkout into the requested destination; the only release source that reads the disk directly |

The credential seam's interface lives in `KyberWeave.Contracts/Arbiter/ICredentialStore.cs`, under the
namespace `KyberWeave.Core.Arbiter.Credentials`, which it kept when it left Domain.
The two process-backed stores reach the process seam through `ICredentialProcessRunner`, and the
Windows store is the only code that calls the native credential API. Every caller takes
`ICredentialStore`, or a `Func<ICredentialStore>` where building the store at call time is
deliberate: a host with no keychain absorbs the construction failure there instead of failing
startup.

The process seam has one sanctioned escape that is not an adapter: a class may build a
`ProcessStartInfo` and hand it to `IProcessRunner`, `IProcessExecutor`, `IProgramRunner` or a
delegate. `ProcessStartInfo` is data there, and the exceptions register below covers it. Production
process starts are in `Kernel/Processes/ProcessRunner.cs`, `StdioSession` and the update's
`ProcessConsoleLauncher`, the one child that shares the user's console.

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

Task 1.8 does not convert any of these; it lists them so a reviewer can tell an adapter from a
violation. The phases converted the list; what remains is one small group of path and name-resolution
calls the conversion left, and each row names the todo that owns it. The register does not invent
coverage: a new gap is recorded here until a todo or a task names it.

The rows count only classes that are neither an adapter nor a composition root, and they exclude
platform checks, which have their own table below.

| Project | Classes calling a resource | Direct-call sites | Tasks |
|---|---|---|---|
| `KyberWeave.Application` | 10 | 15 | [seam-conversion todo](../todo/seam-conversion-leftovers.md) |
| `KyberWeave.Cli` | 0 | 0 | — (roots and `ProcessStartInfo` data only) |
| `KyberWeave.Data` | 0 | 0 | — (adapters only) |
| `KyberWeave.Domain` | 0 | 0 | — (data only) |
| `KyberWeave.Hooks` | 0 | 0 | — (its `Program` root) |
| `KyberWeave.Kernel` | 0 | 0 | — (its three adapters) |
| `KyberWeave.Mcp` | 0 | 0 | — (roots and the console-stream adapter) |
| `KyberWeave.Arbiter` | 0 | 0 | — (its `Program` root and the console-stream adapter) |
| **Total** | **10** | **15** | |

Reconciled on 2026-11-18. Adapters, composition roots, `ProcessStartInfo` used as data,
`Task.Delay` and other waits that take an injected `TimeProvider`, and pure `System.IO.Path` string
work are excluded, as above. The remaining work is 10 classes and 15 sites, all in
`KyberWeave.Application`, and is tracked by
[`docs/todo/seam-conversion-leftovers.md`](../todo/seam-conversion-leftovers.md); no other project
has a class outside its adapter register. `KyberWeave.Contracts` holds no implementation and no
counted site. `Environment.NewLine` sites are excluded.

#### Platform checks still read directly (`OperatingSystem.Is*`, `RuntimeInformation`)

These are not file, process, console or network calls, but they still read the operating system
instead of `IProcessEnvironment`, so they are listed rather than hidden. Every one picks a
`StringComparer`, a path rule or an OS label; none opens a resource. They are part of the same
todo.

| Project | Classes | Sites | Classes |
|---|---|---|---|
| `KyberWeave.Application` | 16 | 17 | `CopilotPromptFile`, `CopilotPromptHome`, `ManagedGlossary`, `CodeGraphIndexProvisioner`, `PostCheckoutHookInstaller`, `RepositoryDoctorService` (2), `DocsRootPath`, `PathContainment`, `FileReferenceExtractor`, `FileCoverageReportSource`, `RunStartRunner`, `SquadGlobalRoots`, `SquadTransaction`, `SquadWorkerBaselineFile`, `PlatformRid`, `StatusLineHostService` |
| `KyberWeave.Data` | 4 | 7 | `PlanPathResolver` (2), `AnalysisCacheSafety` (2), `LaunchCommandResolver` (2), `ReleaseArchiveExtractor` |
| `KyberWeave.Kernel` | 1 | 1 | `ShellQuote` |

The rest are adapters (`ProcessEnvironment`, `PhysicalFileSystem`, `PhysicalPathResolver`,
`WindowsSquadLinkFileSystem`, `FileGitHookFiles`, `DocsRootWalk`, `FileStatusLineFiles`,
`ExclusiveFileWriter`, `UnixUpdateFileModes`, `WindowsCredentialStore`, Kernel `ProcessRunner`) or
the Data composition root (`SquadServiceCollectionExtensions`).

#### KyberWeave.Application (10 classes)

| Class (file) | Calls directly | Task |
|---|---|---|
| `TaskPromptService` (`Docs/Validation/TaskPromptService.cs`) | paths (`Path.GetFullPath` four times) | Tracked in the seam todo |
| `SquadPathPolicy` (`Squad/Deployment/SquadPathPolicy.cs`) | paths (`Path.GetFullPath` twice) | Tracked in the seam todo |
| `SquadGlobalRoots` (`Squad/Deployment/SquadGlobalRoots.cs`) | paths (`Path.GetFullPath` twice) | Tracked in the seam todo |
| `SquadResponseGuidanceDeployer` (`Squad/Deployment/SquadResponseGuidanceDeployer.cs`) | paths (`Path.GetFullPath`) | Tracked in the seam todo |
| `WorkerGuard` (`Arbiter/Hooks/WorkerGuard.cs`) | paths (`Path.GetFullPath` with an explicit base) | Tracked in the seam todo |
| `ConductorGuardRules` (`Arbiter/Hooks/ConductorGuardRules.cs`) | paths (`Path.GetFullPath` with an explicit base) | Tracked in the seam todo |
| `ConductorGuard` (`Arbiter/Hooks/ConductorGuard.cs`) | paths (`Path.GetFullPath` with an explicit base) | Tracked in the seam todo |
| `ArbiterProvenance` (`Arbiter/Hooks/ArbiterProvenance.cs`) | paths (`Path.GetFullPath` with an explicit base) | Tracked in the seam todo |
| `ArbiterHookDecisionEngine` (`Arbiter/Hooks/ArbiterHookDecisionEngine.cs`) | paths (`Path.GetFullPath` with an explicit base) | Tracked in the seam todo |
| `DocsAnalysisConfigLoader` (`Configuration/DocsAnalysisConfigLoader.cs`) | network (`Dns.GetHostAddresses`) | Tracked in the seam todo |

#### KyberWeave.Cli (0 classes)

None left outside a composition root. `Program`, `DocsCommandComposition`, `SquadCliServices` and
`SelfUpdateHostFactory` may read process state while wiring adapters (the roots table above). A
command that builds a `ProcessStartInfo` hands it to `IProcessRunner` or `IProcessExecutor`; the
start information is data, as the exceptions register says.

#### KyberWeave.Data (0 classes)

None left outside an adapter. The two classes the phase 3 register called converters are adapters
and are listed in the adapter register above: `SquadDeploymentAdapters` and
`LocalCheckoutSquadReleaseSource`. `PresentFile` and `DocsRootWalk` are static helpers of the
`PhysicalRepositoryFileStore` adapter; `ZipSkillArchiveWriter` is the `ISkillArchiveWriter` adapter;
`SquadMigrationLedger`, `FileSquadPackager` and `SquadCanonicalProfileStore` now take the file-system,
path and identifier seams by constructor.

Task 4.4 leaves these in `Data` outside `Squad/`, each for a named reason:

- `FileHookSnapshotStore` and `FileWorkerBudgetStore` are converted: the file-system seam gained
  `OpenExclusive` (a read-write open that keeps every other opener out until disposed) and
  `OpenAppendShared` (an append that other appenders and readers share), proven on the fake and on
  `PhysicalFileSystem` by the file-system contract suite.
- Task 3.4 moved `SquadTransaction` and `TransactionLease` onto the seams. The file-system seam gained
  `DeleteEmptyDirectory` (removes a directory only when nothing is in it) and `OpenGuarded` (a read-only open
  that keeps others from changing or deleting a file), both proven by the same contract suite, and later
  `FlushDirectory` (flushes a directory's entries, so a rename survives a power loss; a no-op on Windows and in
  memory).
- `SelfUpdateSystem` is converted. It takes the file-system, path and bounded-process seams by constructor, and
  splits the work: `ArchiveEntryPlanner` decides where an archive entry may go and refuses an absolute, climbing or
  link-escaping one, `ReleaseArchiveExtractor` reads the archive as a stream and writes each entry through the file
  system (a link entry is refused), and `BinaryReplacer` stages, moves and (on Windows) rolls back one binary. Two
  adapters hold what those seams cannot express: `ProcessConsoleLauncher` starts a child that shares the console
  (a role view captures output), and also runs the hand-off rehearsal so it is the hand-off's own start path;
  `UnixUpdateFileModes` reads and sets permission bits (the file-system seam has no member for them). It replaces
  the running binary, so requirement 10.2 applies: `SelfUpdateCrashSafetyContractTests` kills the update and its
  rollback at every file operation and runs on the in-memory file system and on the real disk. One gap stays and is
  listed in that suite: on Windows a stop between moving the old image aside and moving the new one in leaves no
  image at the installed path.
- `AnalysisCacheSafety`, `SqliteAnalysisPersistence` and `CodeGraphResolverAdapter` are converted: each takes the
  file-system, path, environment and bounded-process seams by constructor, and SQLite itself stays the system
  `sqlite3` executable behind the process seam. The callers open them through `IAnalysisPersistenceFactory` and
  `ICodeGraphPortsFactory`, so no caller builds one with `new`.
- `SystemOneClient`, `GitHubReleaseClient` and `OpenAiCompatibleEmbeddingGenerator` are converted: each takes an
  `HttpMessageHandler` (the embedding client takes `IEmbeddingTransport`, which supplies one) and wraps it in a
  private `HttpClient`, so a test answers through `FakeHttpMessageHandler`. `SystemOneProviderFactory` only passes
  the handler on.
- `SocketArbiterHttpHandlers` is an adapter, not a class to convert: it is the one place that builds the Arbiter's
  real handler.
- `PresentFile` and `DocsRootWalk` are static helpers of the adapter `PhysicalRepositoryFileStore`
  (the bounded one-step read and the link-refusing walk). Only that adapter calls them: application code
  reaches the walk through `IRepositoryFileStore.EnumerateContainedFiles` and `IsContainedFile`.
- `ZipSkillArchiveWriter` is the adapter of `ISkillArchiveWriter`. `ZipFile.CreateEntryFromFile` records the
  source file's Unix permission bits in the archive; the file-system seam has no member that reads them, so
  reading entries through `OpenRead` would change the archive's contents.

#### KyberWeave.Domain (0 classes)

None left. Task 2.5 moved the last three to `KyberWeave.Application` (`Squad/Deployment/`), and the Data classes that named them moved with them.

#### KyberWeave.Hooks (0 classes)

None left outside its `Program` composition root.


#### KyberWeave.Kernel (3 classes)

None left. `FileReferenceExtractor` moved to `KyberWeave.Application` (`Parsing/FileReferenceExtractor.cs`) and `CodeGraphIndexStaleness` to `KyberWeave.Data` (`CodeGraph/CodeGraphIndexStaleness.cs`), both over the file-system seam (and the path and process seams); `StdioSession` is the interactive process adapter and runs on `TimeProvider`.

#### KyberWeave.Mcp (0 classes)

None left outside its `Program` and `DocsToolsComposition` roots and the `DrainingStreamServerTransport`
adapter.


## Gaps in the task list

The gaps this register found on 2026-11-17 were closed by the task list: the 12 `Application`
classes without a folder task became task 4.8, the two `Kernel` classes and the clock inside
`FileCodeGraphIndexWorkspace` and `StdioSession` became task 4.9, the native credential store
became the adapter of a new credential seam in task 4.10, and the shared configuration fake and
its contract suite became part of task 2.3. The one group phase 4 left — path and name resolution
in `Application` — is in
[`docs/todo/seam-conversion-leftovers.md`](../todo/seam-conversion-leftovers.md). A new gap is
recorded here until a todo names it.

## The exceptions register

A framework type that is sealed and has no interface may be used directly (requirement 3.3). The
type is only an exception from the seam, never from the rule that a test must be able to control
the behaviour: where a test observes the value, the seam still wraps it.

| Type | Why it stays | Where the code uses it | What a test controls |
|---|---|---|---|
| `JsonDocument` | Parses a string or byte payload the caller already holds. It opens no resource and holds no state a test needs to replace | 31 files, 44 sites; most in `Application/Docs/Mcp` and `Data/Squad` | The input text or bytes; the parse result is deterministic |
| `ZipArchive` | A sealed reader and writer over a stream. The stream is the resource, not the archive | 5 files, 6 sites, including `Application/Squad/Release/GitHubSquadReleaseSource.cs`, `Application/Squad/Packaging/SquadPacker.cs` and `Data/Skills/ZipSkillArchiveWriter.cs` | The stream: a `MemoryStream` holding a fixture archive |
| `SHA256` | A pure hash over bytes. Two calls on the same bytes return the same digest, so a test never needs to replace it | 39 files, 45 sites, from `Arbiter` through `Squad` to `SelfUpdater` | The bytes hashed; the digest is asserted directly |
| `ProcessStartInfo` | A sealed data transfer object handed to `IProcessRunner`, `IProcessExecutor`, `IProgramRunner` or a delegate. It starts nothing by itself; the runner does | 41 files, 64 sites; production process starts remain in `Kernel/Processes/ProcessRunner.cs` and `StdioSession` | A scripted fake asserts the program, arguments, working directory and environment it receives |
| `HttpClient` | A sealed network client. The transport is the seam: an injected `HttpMessageHandler` or an `IArbiterHttpHandlers` adapter | 5 files, 9 sites: the four network adapters (`SystemOneClient`, `GitHubReleaseClient`, `GitHubSquadReleaseSource`, `OpenAiCompatibleEmbeddingGenerator`) and their two factories that pass the handler on | The handler or the network adapter; no test opens a socket |
| `FileStream` | The stream type at the bottom of the file-system adapter. It is an exception only **inside** an adapter, where it is the adapter's whole job | 8 files, 17 sites: `PhysicalFileSystem`, `ArbiterLogFiles`, `PhysicalDoctorFileSystem`, `ExclusiveFileWriter`, `FileBoundedFileReader`, `FileCodeGraphIndexWorkspace` (lease) and Kernel `BoundedFileReader` and `AtomicFile`; `SquadTransaction` holds its journal through the seam's `OpenGuarded` | The file-system seam: `InMemoryFileSystem`, proven by the file-system contract suite |

No non-adapter class names `FileStream`. The durable flush that used to test for it is the file-system
seam's `FlushToStableStorage`, called by `SquadTransaction` and proven by the file-system contract suite
on the fake and on `PhysicalFileSystem`.

Two more framework members are used directly but are not in the exception list because a seam
already covers them:

- `Mutex` appears only in `MutexLease`, the lease adapter. `TransactionLease` now takes `ILease`.
- `OperatingSystem.Is*` and `Environment.*` belong to `IProcessEnvironment`; a class outside the
  environment adapter that calls them is listed above. `Environment.NewLine` is a platform
  constant, not process state, and is not a seam violation.

## Keeping this register current

A change that adds or changes an adapter, a fake or a contract suite edits this page in the same
commit (requirement 3.2). A new sealed framework type is added to the exceptions register with its
reason before it is used directly (requirement 3.3). No build check enforces either rule (decision
D11); a reviewer raises a missing register entry as a finding.
