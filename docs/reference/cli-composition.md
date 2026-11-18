---
id: reference/cli-composition
title: How the CLI gets its dependencies
doc-type: reference
status: current
owner: dpalfery
last-reviewed: 2026-11-16
---

# How the CLI gets its dependencies

A CLI command takes what it needs from a dependency-injection container instead of reading process
state itself. This page says where that container comes from, which contracts a command asks for, and how
a test replaces them. Commands not yet converted still build themselves; they move as their area moves.

## The layers a command sits on

`src/KyberWeave.Cli` is an entry app. It references `src/KyberWeave.Application` and never
`src/KyberWeave.Data`, so a command can only name interfaces from `src/KyberWeave.Contracts`.
`AddWeaveApplication` registers the production adapters from Data and the services on top of them.
Every registration uses `TryAdd`, so a registration made earlier wins.

## The container

`CliApplication.Create(configure)` builds the command tree. It calls `configure` first, then
`AddWeaveApplication()`, and hands the collection to Spectre.Console.Cli through a type registrar
(`src/KyberWeave.Cli/Composition/ServiceCollectionTypeRegistrar.cs`). Spectre asks the registrar for
every command and settings type. A type the container holds comes from the container. A concrete type it
does not hold is constructed directly, with any constructor parameter the container can supply, so a
command with no dependencies needs no registration. An interface or abstract type the container does not
hold resolves to nothing.

A converted command has one public constructor, and its parameters are contracts.

## The contracts every command can ask for

| Contract | What it answers | Production adapter |
|---|---|---|
| `IProcessEnvironment` | Environment variables, the current, home and temporary directories | `Data/Host/ProcessEnvironment.cs` |
| `IProductRootLocator` | Where a product's artifact tree sits in a checkout | `Data/Host/CheckoutProductRootLocator.cs` |
| `IFileSystem`, `IWritableFileSystem` | Reads, and writes with existence and link probes | `Data/Hooks/PhysicalFileSystem.cs` |
| `IProcessRunner` | Runs a command with merged output, asynchronously | `Data/Hooks/KernelProcessRunner.cs` |
| `IGitCommandRunner` | Runs git, keeping standard output and standard error apart | `Data/RunStart/ProcessGitCommandRunner.cs` |
| `IRunStateStore` | The base and place each run recorded under the operator's home | `Data/RunStart/FileRunStateStore.cs` |
| `TimeProvider` | The clock | `TimeProvider.System` |

The full register — every seam, its adapter, the fake and contract suite that prove it, and the
classes still to convert — is in [Seams, adapters and the exceptions they keep](seams.md).

A command reads the current directory, the home directory and any environment variable only through
`IProcessEnvironment`. It never calls `Environment` or `Directory.GetCurrentDirectory` itself.

## Testing a command in this process

Run the command with `CliApplication.Create(services => ...)` inside `ProcessConsoleCapture.Run`, and
register fakes in the callback. The run-start and Utilities suites register an environment that reports a
temporary directory as the working and home directories, and keep one case each that starts the real host
process to prove the production wiring.

Two limits apply. A callee that reads process state without taking it as a parameter cannot be isolated
that way, and a case that depends on it keeps spawning the CLI. A child process started by a fake resolves
its program against the test host's `PATH`, so the fake names the program in full.
