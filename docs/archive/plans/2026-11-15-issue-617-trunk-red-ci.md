---
id: plans/2026-11-15-issue-617-trunk-red-ci
title: "Harden CI-flaky pipe-broken stdin and engine-lease probes"
doc-type: plan
status: complete
component: DocGraph
owner: dpalfery
created: 2026-11-15
last-reviewed: 2026-11-15
development-mode: test-first
autonomy: autonomous
documentation-impact: no-design-change
impact-components: [DocGraph, KyberDash]
---

# Harden CI-flaky pipe-broken stdin and engine-lease probes

**Status: Complete.** Archived 2026-11-15 for GitHub issue #617. All tasks T1–T3 delivered under test-first development mode. All 38 `AnalysisPersistenceTests` passed; all 20 `ServerStateRepositoryTests` passed; all 27 `ProcessSpawnSitesTests` passed.

**Development mode:** `test-first`.
**Autonomy:** `autonomous`.
**Scale:** `no-design-change`, components `DocGraph` and `KyberDash`.

## Problem and goal

**Problem.** CI run 43769291073 on trunk head `fc80ddf9` (squash of PR #612) failed two CI jobs:

1. Job `Build and test`:
   ```text
   failed KyberWeave.Tests.AnalysisPersistenceTests.IsStandardInputOpen_WhenPipeBroken_ReturnsFalse (2ms)
     Assert.False() Failure
     Expected: False
     Actual:   True
       at KyberWeave.Tests.AnalysisPersistenceTests.IsStandardInputOpen_WhenPipeBroken_ReturnsFalse() in tests/KyberWeave.Tests/AnalysisPersistenceTests.cs:864
   ```
   Under parallel test execution on Linux (un-serialized after Arbiter test suite parallelization in `fc80ddf9`), concurrent child process spawning via `fork()` without immediate `closefrom`/`execve` in sibling test threads can briefly duplicate the anonymous read pipe handle. The exited child (`git version`) closes its descriptor, but the kernel pipe reader reference count remains > 0 while the concurrent fork has not yet completed `execve()`. A single 1-byte write into the 64 KB kernel pipe buffer succeeds during that transient window, causing `IsStandardInputOpen` to flush without throwing `IOException` and return `true`.

2. Job `Dash harness-collection e2e`:
   ```text
   failed KyberDash.Data.Tests.ServerStateRepositoryTests.EngineLease_OwnsTheSocketAndAnswersProbes (61ms)
     Assert.True() Failure
     Expected: True
     Actual:   False
       at KyberDash.Data.Tests.ServerStateRepositoryTests.EngineLease_OwnsTheSocketAndAnswersProbes() in dash/server.tests/KyberDash.Data.Tests/ServerStateRepositoryTests.cs:240
   ```
   `ServerStateRepository.RequestEngineShutdownAsync` sends `{"op":"shutdown"}\n` over the Unix domain socket and immediately returns once the socket descriptor response is received. `EngineOwner.cs` dispatches the shutdown callback asynchronously via `_ = Task.Run(onShutdownRequested, CancellationToken.None);`. The test immediately evaluated `Assert.True(shutdownRequested.Task.IsCompleted);` synchronously. On a busy 2-core CI runner, thread pool worker dispatch took longer than the socket reply (61ms), leading to an immediate assertion failure before the callback ran.

**Goal.**
1. Harden `ServerStateRepositoryTests.EngineLease_OwnsTheSocketAndAnswersProbes` to await the completion of `shutdownRequested.Task` with a deterministic bounded timeout (`await shutdownRequested.Task.WaitAsync(TimeSpan.FromSeconds(5))`), eliminating the thread pool race condition.
2. Harden `AnalysisPersistenceTests.IsStandardInputOpen_WhenPipeBroken_ReturnsFalse` to poll write across the broken pipe until transient inherited reader descriptors drain and `Flush()` throws `IOException`, ensuring deterministic failure detection across all operating systems.
3. Preserve all existing contracts and assertions without weakening tests into tautologies or reverting #612.

---

## Decisions

| ID | Question | Answer / Decision | Mitigating and Supporting Information | Status / Approval Provenance |
|---|---|---|---|---|
| D1 | How should `ServerStateRepositoryTests.EngineLease_OwnsTheSocketAndAnswersProbes` verify that shutdown was requested without a thread-pool race? | Await `shutdownRequested.Task.WaitAsync(TimeSpan.FromSeconds(5))` before `Assert.True(shutdownRequested.Task.IsCompleted)`. | `EngineOwner` intentionally dispatches `onShutdownRequested` via `Task.Run` to avoid deadlocking synchronous callers. The test should await this asynchronous completion deterministically rather than asserting synchronous completion immediately after the socket response. | Approved (AP1) and implemented |
| D2 | How should `AnalysisPersistenceTests.IsStandardInputOpen_WhenPipeBroken_ReturnsFalse` test that `IsStandardInputOpen` catches `IOException` when writing across a broken pipe? | Retry writing buffered bytes across the pipe with a short bounded deadline (5 seconds with 20ms sleep intervals). | As soon as concurrent forks complete `execve()`, transient inherited read descriptors are closed by `O_CLOEXEC`, the kernel pipe reports `EPIPE`, and `IsStandardInputOpen` catches `IOException` and returns `false`. This avoids flakiness from concurrent test process spawning on Linux without weakening assertions. | Approved (AP1) and implemented |
| AP1 | Approval to proceed with plan execution under test-first development mode? | Approved by user 2026-11-15. | Fixes #617 on branch `hal.hermes.antigravity/fix/issue-617-trunk-red-ci`. | Approved by user 2026-11-15 |

---

## Tasks

- [x] **T1: RED proof.** Verify root causes and document timing and environment race windows on both tests.
- [x] **T2: GREEN fix.** Update `dash/server.tests/KyberDash.Data.Tests/ServerStateRepositoryTests.cs` to await `shutdownRequested.Task.WaitAsync(TimeSpan.FromSeconds(5))` and `tests/KyberWeave.Tests/AnalysisPersistenceTests.cs` to poll write across the broken pipe until `Flush` throws `IOException`.
- [x] **T3: Verification.** Run targeted tests for both test assemblies, verify `ProcessSpawnSitesTests` allowlist parity, run `docs validate . --merge-ready` and `docs drift .`, archive plan to `docs/archive/plans/`, and update inventory rows.

---

## Design harvest

Documentation waiver: scale is `no-design-change`. Test fixture reliability hardening inside existing test assemblies (`tests/KyberWeave.Tests/AnalysisPersistenceTests.cs` and `dash/server.tests/KyberDash.Data.Tests/ServerStateRepositoryTests.cs`), introducing no new architectural boundary, storage schema, or CLI command. No ADR — ADR waiver: implementation details of test assertions and asynchronous synchronization.

---

## Closeout mapping

| ID | Destination |
|---|---|
| D1 | Documentation waiver: test fixture reliability hardening inside existing test assembly, introducing no new architectural boundary or API contract. |
| D2 | Documentation waiver: test fixture reliability hardening inside existing test assembly, introducing no new architectural boundary or API contract. |
| AP1 | Documentation waiver: plan lifecycle provenance recorded in `docs/archive/plans/README.md`. |
