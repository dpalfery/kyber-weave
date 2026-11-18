---
id: todo/seam-conversion-leftovers
title: Finish the resource calls the interface-driven conversion left in Application
doc-type: todo
component: CI Pipelines
status: draft
owner: dpalfery
last-reviewed: 2026-11-18
---

# Finish the resource calls the conversion left

## The finding

[The seam register](../reference/seams.md#classes-still-calling-a-resource-directly) lists eleven
`KyberWeave.Application` classes and eighteen sites that still reach a resource without its seam, and
a second table of platform checks: 21 classes and 25 sites across `Application`, `Data` and `Kernel`
that call `OperatingSystem.Is*` or `RuntimeInformation` instead of `IProcessEnvironment`. They are
the residue of phase 4, not a new design question. Each is small:

| Class | Call | What to do |
|---|---|---|
| `SquadTransaction` | `Path.GetFullPath` three times | Take `IPathResolver`. One call has an explicit base, two do not; the containment check must not depend on the process working directory |
| `TaskPromptService` | `Path.GetFullPath` four times | Take `IPathResolver` and pass the base it already holds |
| `SquadPathPolicy` | `Path.GetFullPath` twice | Take `IPathResolver` |
| `SquadGlobalRoots` | `Path.GetFullPath` twice | Take `IPathResolver` |
| `SquadResponseGuidanceDeployer` | `Path.GetFullPath` once | Take `IPathResolver` |
| `WorkerGuard` | `Path.GetFullPath` once | Take `IPathResolver`; the base comes from `IProcessEnvironment.CurrentDirectory` |
| `ConductorGuardRules` | `Path.GetFullPath` once | Take `IPathResolver` |
| `ConductorGuard` | `Path.GetFullPath` once | Take `IPathResolver` |
| `ArbiterProvenance` | `Path.GetFullPath` once | Take `IPathResolver` |
| `ArbiterHookDecisionEngine` | `Path.GetFullPath` once | Take `IPathResolver` |
| `DocsAnalysisConfigLoader` | `Dns.GetHostAddresses` | Add a name-resolution seam (the network seam already has a fake for the handler) and resolve through it |

Each change lands with the unit test that would fail without it; a path call with an explicit base is
still a seam violation (requirement 2.7), because only the seam knows the platform's canonical
spelling.

The platform checks are the same conversion one member at a time: take `IProcessEnvironment` (its
operating-system kind, case rule and, once added, its architecture) and read the answer from it. Most
of them only choose a `StringComparer`, so the change is mechanical and the test asserts both
branches through a fake environment. The register's second table names every class.

## Other seam work the delivery deferred

- **`IProcessEnvironment.ProcessPath`.** `SelfUpdateHostFactory` and `SquadCliServices` still read
  `Environment.ProcessPath` directly. `AppContext.BaseDirectory` is not the same value for a
  dotnet-tool install, so the member must return `Environment.ProcessPath`. The composition roots may
  build adapters, but this call is inside a root, so the member still removes the last direct read.
- **Write-through-link on the atomic write seam.** `CopilotPromptCommands` still uses `AtomicFile`;
  the file-system seam has no "write through a link" member. Add it, with the contract case on the
  fake and `PhysicalFileSystem`.
- **Architecture from `RuntimeInformation`.** `PlatformRid` reads the process architecture directly.
  Add an architecture member to `IProcessEnvironment` and answer it from the adapter.
- **`FileCodeGraphIndexWorkspace` holds decisions.** The adapter is also where doctor and
  CodeGraph-refresh logic decides what the lock means, so those tests stay on the real disk. Split
  the decisions out and test them in memory; keep only the file-lock translation in the adapter.
- **`PlanPathResolver` and a trailing slash.** Every plan under a root given with a trailing
  separator is rejected. The behaviour is kept for requirement 10.1; fix it separately, with a test.
- **The CLI path seam drops a trailing separator.** `--root /repo/` reaches downstream code as
  `/repo`. Judged harmless; confirm that in the next behaviour sweep and document it or fix it.
- **`squad run`'s platform flag has no unit test.** It is covered only by an integration smoke test.
- **`RegisterProject` writes the registry outside the lease.** This is a pre-existing race between
  two concurrent installs; reconcile it under the transaction lease.
- **The ledger builds event ids from the clock.** Two events in the same instant collide; use
  `IIdGenerator`. Tracked separately in
  [arbiter-ledger-event-id-collision.md](arbiter-ledger-event-id-collision.md).
- **`SquadDoctorCommand` retired-key scan is optional.** Its MCP wiring service is an optional
  injection; make it required so a caller cannot silently lose the scan.
- **`FakeSquadReleaseSource` defaults to the physical file system for integration callers.** Make
  the file system explicit at every call site.
- **`ProcessSquadHarnessLauncher.RunInteractive` calls the static Kernel `ProcessRunner`.**
  `IProcessRunner` has no console-attached start. Add one, or record the launcher as an adapter
  beside the process seam. The Semgrep finding on this site is a false positive and needs no fix.

## Why this was not fixed here

Closeout writes down what the delivery left; it does not change product behaviour. Each item is a
small, independent conversion, and several need a seam member added first. Doing them in closeout
would mix a documentation change with behaviour changes that requirement 10.1 asked to ship
separately.

## Related

- [Seams, adapters and the exceptions they keep](../reference/seams.md)
- [ADR 0089](../adr/0089-every-behaviour-is-testable-through-a-seam.md) — the decisions these
  leftovers belong to
- [Interface-driven test follow-ups](interface-driven-test-follow-ups.md) — the test-side leftovers
