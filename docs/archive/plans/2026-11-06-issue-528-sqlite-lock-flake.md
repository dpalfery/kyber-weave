---
id: plans/2026-11-06-issue-528-sqlite-lock-flake
title: "Stabilize AnalysisPersistenceTests sqlite lock concurrent-transaction probe and rollback teardown"
doc-type: plan
status: complete
component: DocGraph
owner: dpalfery
created: 2026-11-06
last-reviewed: 2026-11-06
development-mode: test-first
autonomy: autonomous
documentation-impact: no-design-change
impact-components: [DocGraph]
---

# Stabilize AnalysisPersistenceTests sqlite lock concurrent-transaction probe and rollback teardown

**Status: Complete.** Archived 2026-11-06. All tasks T1–T3 delivered under test-first development mode. All 36 `AnalysisPersistenceTests` passed; all 27 `ProcessSpawnSitesTests` passed.

**Development mode:** `test-first`.
**Autonomy:** `autonomous`.
**Scale:** `no-design-change`, component `DocGraph`.

## Problem and goal

**Problem.** CI run 42275677294 (job `Build and test` id 128091288410 on `main` head `124da352`) failed `AnalysisPersistenceTests.StartSqliteLockWhenRollbackCleanupFailsPreservesCleanupDiagnosticInAggregateException` (recurrence of sqlite lock fixture flakiness tracked in #528, following #273 and #518):

```text
failed KyberWeave.Tests.AnalysisPersistenceTests.StartSqliteLockWhenRollbackCleanupFailsPreservesCleanupDiagnosticInAggregateException (24ms)
  Assert.Throws() Failure: Exception type was not an exact match
  Expected: typeof(System.AggregateException)
  Actual:   System.TimeoutException : sqlite lock fixture verified acquire, but concurrent transaction probe did not detect lock.
    at KyberWeave.Tests.AnalysisPersistenceTests.StartSqliteLock(...) in tests/KyberWeave.Tests/AnalysisPersistenceTests.cs:710
    at KyberWeave.Tests.AnalysisPersistenceTests.StartSqliteLockWhenRollbackCleanupFailsPreservesCleanupDiagnosticInAggregateException() in tests/KyberWeave.Tests/AnalysisPersistenceTests.cs:501
```

The test intends to verify that when `StartSqliteLock` encounters a failure (forced by `probeTimeoutSeconds: 0`) and rollback teardown also fails (forced by `beforeProbe: process => process.StandardInput.Dispose()`), the cleanup diagnostic is wrapped alongside the primary `TimeoutException` in an `AggregateException`.

However, closing `StandardInput` delivers EOF to `sqlite3`, causing the child process to exit immediately. In `StartSqliteLock`'s exception handler, the rollback write was guarded by `if (!process.HasExited)`. If the OS terminated `sqlite3` before the exception handler checked `!process.HasExited` (which occurred within 24ms on CI), the rollback write was skipped, `cleanupFailures` remained empty, and `StartSqliteLock` rethrew the raw `TimeoutException` rather than wrapping it in an `AggregateException`.

**Goal.**
1. Eliminate the child exit timing race window in `StartSqliteLock` by tracking whether an immediate transaction was acquired (`bool acquired`, initialized to `false` and set to `true` after the `ACQUIRED` handshake). When cleanup runs for an acquired transaction, handle the two acquired substates: if `process.StandardInput` was closed or disposed (`!IsStandardInputOpen`), record the cleanup failure (`IOException`) into `cleanupFailures`; if the child has already exited with input open, safely skip rollback (process termination releases all SQLite file locks, so there is nothing to roll back and nothing to report); otherwise (`!process.HasExited`) issue `ROLLBACK;`, close input, and capture any `IOException` or `InvalidOperationException`.
2. Keep the fast-exit guard for unacquired transactions (e.g. `acquireAckQuery` mismatch where the child was already killed during acquire), preserving the `InvalidOperationException` contract of `StartSqliteLockWhenOutputIsNotAcquiredKillsChildAndFailsFastWithoutHanging`.
3. Preserve all test assertions and timeouts without weakening tests or masking failures.

---

## Decisions

| ID | Question | Answer / Decision | Mitigating and Supporting Information | Status / Approval Provenance |
|---|---|---|---|---|
| D1 | How should `StartSqliteLock` decide whether to execute `ROLLBACK;` teardown in the exception handler? | Initialize `bool acquired = false;` and set it to `true` once the `ACQUIRED` handshake completes. In `catch (Exception primaryException)`, gate rollback teardown on `if (acquired)`. Within that guard, if `!IsStandardInputOpen(process)` (e.g. caller disposed input), add an `IOException` to `cleanupFailures` because transaction release is unknown; else if `!process.HasExited`, issue `ROLLBACK;` and close input, catching `IOException` or `InvalidOperationException`. If the child has already exited with input open, skip rollback without recording an error. | If `acquired` is false (acquisition failed or child was killed), rollback is skipped so raw acquisition errors are not masked. When `acquired` is true, an explicit input disposal records `IOException` in `cleanupFailures`. If the child exited with input open, the OS terminated the process and released all SQLite file locks, so there is no lock leak, nothing to roll back, and nothing to report. | Approved (AP1) and implemented |
| AP1 | Approval to proceed with plan execution under test-first development mode? | Approved by user. | Fixes #528 on branch `hal.hermes.agy/issue-528-sqlite-lock-flake`. | Approved by user 2026-11-06 |

---

## Tasks

- [x] **T1: RED proof.** Write a test verifying that when `sqlite3` exits before the rollback cleanup handler checks process state on an acquired transaction, `StartSqliteLock` still preserves the cleanup diagnostic in `AggregateException` (reproducing the CI failure on unpatched code).
- [x] **T2: GREEN fix.** Update `StartSqliteLock` in `tests/KyberWeave.Tests/AnalysisPersistenceTests.cs` to track `acquired` state and execute rollback cleanup deterministically without the `!process.HasExited` race window.
- [x] **T3: Verification.** Run targeted test suite via `scripts/proc-guard.sh`, verify `ProcessSpawnSitesTests` allowlist parity, run `docs validate . --merge-ready`, and push the branch.

---

## Design harvest

Documentation waiver: scale is `no-design-change`. Test fixture reliability hardening inside existing test assembly (`tests/KyberWeave.Tests/AnalysisPersistenceTests.cs`), introducing no new architectural boundary, storage schema, or CLI command. No ADR — ADR waiver: implementation detail of test fixture teardown.

---

## Closeout mapping

| ID | Destination |
|---|---|
| D1 | Documentation waiver: test fixture reliability hardening inside existing test assembly, introducing no new architectural boundary, storage schema, or CLI command. |
| AP1 | Documentation waiver: test fixture bugfix plan lifecycle provenance recorded in docs/plans/README.md. |
