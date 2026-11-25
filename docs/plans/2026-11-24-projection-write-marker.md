---
id: plans/2026-11-24-projection-write-marker
title: Record writes move the projection write marker again
doc-type: plan
status: current
component: KyberDash
owner: dpalfery
last-reviewed: 2026-11-24
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components:
  - KyberDash
---

# Record writes move the projection write marker again

**Status: Ready, 2026-11-25. Implementation complete T1-T4; closeout harvested.**

## Request

Owner's messages, verbatim: "do you see recent records in the database? i am showing no new sessions" / "everything in the sessions view is 2days old" / (after the diagnosis) "No need to back up the database. It's temporary telemetry on a developer computer. Go ahead and wipe the database and recreate it the right way."

Conductor's plan requirements, as dispatched: restore the contract on every record-changing write path in the SQLite store and the in-memory fake (bump the write marker and mark affected session keys / ranges dirty in the same transaction), with an exhaustive list of write paths found via codegraph; a contract test that fails on main (write a batch → projection rebuilds the session) run against BOTH the in-memory store and SQLite; remove hand-set SetWriteMarker calls from tests where the store now does it. Also include: why `clean --all` leaves quarantined spans and why vacuum did not shrink the file (diagnose; fix if in the same layer, else record as an open item). This fix ships as its own small PR from main (the large Droid PR on branch claude/fix/droid-chart-and-per-harness-e2e also edits SqliteRelationalStore.Reader.cs and InMemoryRelationalStore.cs — note merge risk).

## Problem and goal

Since the relational-store rewrite (#652, `ca221a9c4`), `SqliteRelationalStore.WriteBatch` and `DeleteSource` write records without moving `store_write_marker` or recording dirty session keys. `CanonProjectionService.ProjectIncremental` asks `CanonSessionProjectorEngine.IsProjectionCurrent`, which compares `projection_state.records_marker` with the write marker, finds them equal, and returns without rebuilding. New telemetry lands in `record` tables but never reaches `session_projection`, so the sessions view stays frozen at the last `clean` (which is the only code that moves the marker).

Goal: every record-changing write moves the marker and records the sessions it touched, in the same transaction, in both the SQLite store and the in-memory fake; the projector then rebuilds exactly those sessions, and a write that lands while a pass runs is never lost. `kyberdash kyber vacuum` actually compacts the file.

## Request trace

| Request instruction | Covered by | Evidence that it is met |
|---|---|---|
| No new sessions / sessions view two days old | T1, T2 | `ProjectionWriteMarkerContractTests` green on both stores: a written batch is rebuilt by the next `ProjectIncremental`, including one written mid-pass |
| Wipe the database and recreate it the right way | Out-of-scope boundaries (PR note), conductor operation | Conductor already ran `clean --all`; the PR note gives the full-reset step (stop engine, delete `canon.db`) for after merge |
| Restore the contract on every record-changing write path, SQLite and fake, same transaction | T1, D2, D3, D4 | `StoreWriteMarkerParityTests` asserts marker and dirty state after each write path, identical on both stores |
| Exhaustive list of write paths found via codegraph | Investigation findings (write-path inventory), D2 | Inventory table below |
| Contract test that fails on main, write a batch → projection rebuilds, against both stores | T1, D7 | T1 RED on main: second pass `Built == 0` on both stores; GREEN after T1's store change |
| Remove hand-set `SetWriteMarker` calls where the store now does it | T3 (`ProjectionReadPathTests`, `SessionProjectionTests`, `CanonAnalysisTests`) | Each named call replaced by the real write; assertions kept |
| Why `clean --all` leaves quarantined spans | Investigation findings, D9, PR note | Deliberate and test-pinned; recommendation in PR note |
| Why vacuum did not shrink the file; fix if same layer | Investigation findings, D10, T4 | `RunVacuum` was an empty method; T4 makes it run `VACUUM` and a Data test shows the file shrink |
| Own small PR from main; note merge risk with the Droid branch | Risks, PR note | Branch `claude/fix/projection-write-marker` from `origin/main` `2f661f462` |

## Scale

`documentation-impact: extends-component` on KyberDash: no schema change; it restores a write-path contract and changes how a projection pass ends.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Where is the contract restored: in the store or in each caller? | In the store (`SqliteRelationalStore` and `InMemoryRelationalStore`), inside the same transaction / lock as the record write. | Matches pre-#652 `CanonStoreRepository` (`AppendBumpRecordsWriteMarker` + `AppendDirtySessionUpdate` in every write script); caller-side bumps can be forgotten (this bug) and are not atomic. | autonomous: recommendation accepted (prior design in `git show ca221a9c4^`) |
| D2 | Which write paths are record-changing? | `IRelationalStoreWriter.WriteBatch` (when it carries at least one session or record) and `IRelationalStoreWriter.DeleteSource`. No other write moves the marker. | Inventory in findings. `WriteProblem`, quarantine, provenance, metrics, checkpoints, token cache, aliases and every `IProjectionStore` write do not change record or session rows; bumping on projector output would make the projector invalidate itself. Same split as the old repository (`RecordProblem` never bumped). | autonomous: recommendation accepted |
| D3 | Which session keys does a write mark dirty? | `WriteBatch`: the union of `batch.Sessions[].SessionKey` and non-null `batch.Records[].SessionKey`; any record with a null `SessionKey` makes the write a wildcard. `DeleteSource`: always wildcard (the cascade removes records of sessions the call does not know). | Same as old `PutRecords` (`hasUnkeyed ? null : touchedKeys`) and old `WipeHarnesses` (wildcard). | autonomous: recommendation accepted |
| D4 | How is a new write merged into an existing dirty range? | Rules table in Investigation findings ("Dirty-range merge rules"). Range continues when its `to_seq` equals the marker before the write; otherwise it restarts at that marker. Wildcard is "range row with no session rows", so a keyed write never adds keys to a continuing wildcard range. | Mirrors `AppendDirtySessionUpdate`; the projector's incremental test (`FromSeq == RecordsMarker && ToSeq == marker`) depends on exactly this. A wrong merge silently skips sessions, hence T1 is `hard`. | autonomous: low-confidence, no authoritative source; cost: stale projections, caught by T1 parity tests |
| D5 | What does a pass do when a write lands while it runs (`finalMarker > currentMarker`)? | Persist `records_marker = currentMarker` (what the pass actually read) and rewrite the dirty range as (`currentMarker`, `finalMarker`) with keys = stored keys ∪ deferred keys when the stored range is continuing (`ToSeq == finalMarker`); keep it wildcard when stored is wildcard; clear it otherwise. | Today the pass stamps `finalMarker` and clears dirty state, so a span written mid-pass for an already-rebuilt session is silently dropped; that was unreachable while writes never moved the marker and becomes live with T1. Every race in this path degrades to a full pass (range `ToSeq` ≠ marker), never to a skipped session. | autonomous: low-confidence, no authoritative source; cost: missed rows in the sessions view |
| D6 | Should a stored dirty state make `IsProjectionCurrent` false, so deferred keys are retried on a quiet store? | No change in this PR; PR note. | `KyberBridgeEngine.EnsureProjected` calls `IsProjectionCurrent` on every dashboard read; with deferred oversized keys stored, every read would re-enter a read pass that defers them again (the churn runbook entry 11 describes). With T1, the next write continues the range and the maintenance pass then rebuilds them, so live capture self-heals; only a fully quiet store waits. Not needed for the request. | autonomous: recommendation accepted |
| D7 | Where does the SQLite variant of the end-to-end contract test live, given the C# rule that a real-resource (integration) test never runs in a task gate? | `KyberDash.Application.Tests.Projection.ProjectionWriteMarkerContractTests`, one xUnit theory over `InMemoryRelationalStore` and a `SqliteRelationalStore` built as `SettingsCleanDoctorReportTests.RunVacuumLeavesTheRelationalStoreAnswering` builds it (`TempDirectory`, `SqliteCircuitBreaker`). | Conflict recorded: the csharp rule "integration test … never in a developer's task gate" versus the owner's explicit "run against BOTH the in-memory store and SQLite". The explicit instruction wins; dash has no Integration.Tests project, and Data.Tests and Application.Tests already run real-SQLite tests in their task gates, so this adds no new fixture or project. PR note raises the missing dash integration project. | autonomous: recommendation accepted (explicit instruction) |
| D8 | Does `CleanService.Wipe` keep its own marker bump? | Keep `SetWriteMarker(+1)` and `ClearProjectionDirtyState()`; correct the comment that says deleting sources does not move the marker. | The wipe also deletes projection rows directly and may delete no source at all, so an explicit full-pass invalidation stays right. | autonomous: recommendation accepted |
| D9 | Should `clean --all` delete quarantined spans and ingest problems? | No change in this PR; raise to the owner as a PR note with a recommendation. | Deliberate: `Wipe` reports `QuarantineDeleted: 0` and `SettingsCleanDoctorReportTests.CleanRetainsIngestProblemsAndQuarantinedSpansAsDiagnostics` pins the retention. Changing it reverses a tested design the request did not ask to change. | autonomous: recommendation accepted |
| D10 | Fix vacuum here? | Yes (T4): `SqliteRelationalStore` gains a public `Vacuum()` (WAL checkpoint truncate, then `VACUUM`, outside a transaction); `DoctorService.RunVacuum` calls it when the store is SQLite, through the same `operationalStore as SqliteRelationalStore` cast `RunDiagnostics` already uses. | Same layer (Data store plus its Application caller); no interface member is added, so no fake or `StoreInterfaceShapeTests` change. Today `RunVacuum` is empty and the CLI still prints "checkpoint and compaction complete". | autonomous: recommendation accepted |
| D11 | Existing tests whose assertions change | Only `SettingsCleanDoctorReportTests.AllScopeWipesCollectedDataAndReportsEveryDeletedFigure`: `Assert.Equal(1, store.GetWriteMarker())` becomes "marker after the clean is greater than the marker read just before it" (seed writes and each `DeleteSource` now move it). `ProjectionReadPathTests.AReadPathPassKeepsMidPassIngestedKeysDirtyInTheDeferredWriteBack` keeps its assertions once `MidPassIngestReader` drops its `inner.SetWriteMarker` (the real write moves the marker and continues the range). | Every other existing test keeps its assertions; a worker that must change another assertion escalates. | autonomous: recommendation accepted |
| AP1 | Approve and execute | Approved for execution as planned. | Recorded by the finalize step, which changed no task, file scope, test row or command. | ANSWERED: autonomous: recommendation accepted, AUTONOMOUS_APPROVAL: approve-and-execute (owner reported stale sessions; live-data defect, separate plan review skipped for urgency — the final council covers it), 2026-11-25 |

## Design harvest

| Target component | Target doc type | Section | Content to harvest |
|---|---|---|---|
| KyberDash | architecture | `docs/dash/architecture.md`, incremental projection paragraph | Replace `projection_dirty_sessions` / `projection_session_key_map` with `store_write_marker`, `projection_dirty_range`, `projection_dirty_session`; state D1–D4 (which writes move the marker, which keys they mark, merge rules) and D5 (a pass that saw a mid-pass write stamps the marker it read and keeps the new writes dirty) |
| KyberDash | runbook | `docs/dash/runbook.md`, clean and vacuum entries | Clean keeps quarantine and problems (D9); `kyber vacuum` now compacts (D10) |

## Investigation findings

- Read at `claude/fix/projection-write-marker` `2f661f462` in the worktree (codegraph `projectPath` provenance matched).
- **Write-path inventory** (every public write on `SqliteRelationalStore`, from codegraph and `rg 'public void '`):

| Write | Changes record/session rows? | Moves marker after this plan |
|---|---|---|
| `WriteBatch` (`SqliteRelationalStore.cs`) | yes (source, session, record + children, problems) | yes, when sessions or records non-empty (D2, D3) |
| `DeleteSource` (`SqliteRelationalStore.cs`) | yes (cascade to records) | yes, wildcard |
| `WriteProblem` | no (`ingest_problem*`) | no |
| `PutQuarantinedSpan`, `PutRecordProvenance`, `PutCachedTokenCounts`, `PutMetricPoints`, `RecordIngest`, `UpsertSourceCheckpoint`, `UpsertRefreshRun`, `ReplaceSessionKeyAliases`, `ReplaceModelContextWindowCatalog`, `SaveSettings`, `Set*` singletons (`SqliteRelationalStore.Operational.cs`) | no | no |
| `ReplaceSessionProjection(s)`, `DeleteSessionProjection`, `ReplaceSessionTurns`, `ReplaceRuns`, `DeleteRun`, `ReplaceExecutions`, `DeleteExecutionsForRun`, `ReplaceHarnessRollups`, `DeleteHarnessRollup`, `ReplaceFindings`, `DeleteFindingsForSession`, `ReplacePrediction`, `DeletePrediction` | projector output only | no (would self-invalidate) |

- **Data model**: no new table or column. Existing tables the contract uses:

| Table | Key | Columns | Meaning |
|---|---|---|---|
| `store_write_marker` | `singleton_id = 1` | `records_write_seq` (int64, not null, ≥ 0) | Increments by exactly 1 per record-changing write; absent row reads as 0 |
| `projection_dirty_range` | `singleton_id = 1` | `from_seq`, `to_seq` (int64, not null) | Writes since `from_seq`, up to `to_seq` |
| `projection_dirty_session` | `session_id` (unique) | — | Dirty keys; range with no rows = wildcard |
| `projection_state` | singleton | `records_marker` | Marker the last pass projected |

- **Dirty-range merge rules** (per record-changing write; `prev` = marker before the write, `next = prev + 1`; all in the write's transaction):

| Stored range before the write | Write is keyed (keys K) | Write is wildcard |
|---|---|---|
| none, or `to_seq ≠ prev` (stale) | delete all session rows; range = (`prev`, `next`); insert K | delete all session rows; range = (`prev`, `next`) |
| `to_seq = prev`, has session rows | keep `from_seq`; `to_seq = next`; insert K, ignoring duplicates | keep `from_seq`; `to_seq = next`; delete all session rows |
| `to_seq = prev`, no session rows (wildcard) | keep `from_seq`; `to_seq = next`; insert nothing | keep `from_seq`; `to_seq = next` |

  Then marker = `next`. Evaluate "continuing" and "has session rows" before the range row is updated.
- The fake (`InMemoryRelationalStore.WriteBatch`, `DeleteSource`) applies the same rules to `working` inside its lock, before `state = working`.
- `ProjectIncrementalRelational` ends by reading `finalMarker`, stamping it, then clearing or rewriting dirty state; D5 changes only the `finalMarker > currentMarker` branch.
- **Tests that read the marker or dirty state** (`rg 'GetWriteMarker\(\)|GetProjectionDirtyState\(\)|IsProjectionCurrent\(\)'`): `SettingsCleanDoctorReportTests` line asserting marker `1` after clean (changes, D11); `OperationalStoreTests` and `SqliteRelationalStoreParityTests` assert marker `0` on an empty store and round-trip `SetWriteMarker`/`SetProjectionDirtyState` without record writes (unchanged); `InMemoryRelationalStoreTests` round-trips setters only (unchanged); `ProjectionReadPathTests`, `SessionProjectionTests`, `CanonAnalysisTests` set the marker by hand after writes (T3). The interface stubs in `SettingsRepositoryTests` / `PricingRepositoryTests` throw and are untouched.
- `KyberBridgeEngine` reads the marker as a cache `storeGeneration` and lists non-wildcard dirty keys as `deferredSessions` in projection health; both only read. With T1 the generation moves on every write (intended: the cache invalidates on new data), and keyed dirty keys from fresh writes appear in `deferredSessions` until the next pass clears them.
- Application.Tests already builds a real `SqliteRelationalStore` (`SettingsCleanDoctorReportTests.RunVacuumLeavesTheRelationalStoreAnswering`, with `TempDirectory` and `SqliteCircuitBreaker`).
- **Clean and quarantine**: `CleanService.Wipe` never touches `quarantined_span*` or `ingest_problem*` (returns `QuarantineDeleted: 0`, `ProblemsDeleted: 0`) by design, pinned by `CleanRetainsIngestProblemsAndQuarantinedSpansAsDiagnostics`. 271,806 quarantined spans stay after `clean --all`; that volume also suggests an attribution gap worth its own look.
- **Vacuum**: `DoctorService.RunVacuum` has an empty body (its remark says WAL `journal_size_limit` makes it unnecessary), so `kyber vacuum` never ran `VACUUM`; SQLite keeps freed pages in the file, hence 1.28 GB after deleting 74,833 records. The quarantine rows also still occupy pages.
- **Standards divergence**: the C# standard requires in-process ADO.NET for application database access; `SqliteRelationalStore` drives the `sqlite3` CLI (documented in `docs/dash/architecture.md` as deliberate). New code here follows the existing store pattern; PR note below.
- Standards fetched with `docs_get_coding_standard_for_technology` (csharp).
- Discovery: codegraph 5, docs 2 (standards tool twice), direct reads and searches 23. Reasons: `git show ca221a9c4^` (pre-#652 repository, not in the index); `rg`/`grep` for the public-write inventory, marker/dirty-state call sites, test namespaces, csproj references and the plans-index row format (exhaustive lists codegraph caps); Read of `DoctorService.cs`, the projector pass end, `KyberBridgeEngine.EnsureProjected` and projection health, `ProjectionReadPathTests` mid-pass test and wrapper, `SettingsCleanDoctorReportTests` clean, seed and vacuum tests (single details outside earlier codegraph output); `dash/AGENTS.md`, `docs/dash/architecture.md`, `docs/dash/runbook.md` entry 11, `docs/plans/README.md` (docs read for rules and harvest targets).

## Test contract (development-mode: test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `dash/server.tests/KyberDash.Data.Tests/Repositories/StoreWriteMarkerParityTests.cs` (new; copy the two-store setup in `KyberDash.Data.Tests.SqliteRelationalStoreParityTests`) | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~KyberDash.Data.Tests.Repositories.StoreWriteMarkerParityTests"` | Each case runs on both stores and asserts the same marker and `DirtyProjectionState`: keyed batch on empty store → marker 1, range (0,1), keys = session ∪ record keys; second keyed batch → (0,2), keys merged, no duplicates; batch with a null-key record → wildcard; batch with only problems → marker unchanged; `DeleteSource` → wildcard; keyed write after a wildcard → stays wildcard; write after a stale range (range `to_seq` ≠ marker) → restarts at `prev`; `WriteProblem`, `PutQuarantinedSpan`, `PutRecordProvenance` → marker unchanged; a rejected batch (duplicate `session_id`, `DashValidationException`) leaves marker and range unchanged | marker stays 0 / dirty state null on both stores | all cases pass on both stores |
| T1 | `dash/server.tests/KyberDash.Application.Tests/Projection/ProjectionWriteMarkerContractTests.cs` (new; copy the write helpers of `KyberDash.Application.Tests.Projection.SessionProjectionTests` and the SQLite setup of `SettingsCleanDoctorReportTests.RunVacuumLeavesTheRelationalStoreAnswering`) | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~KyberDash.Application.Tests.Projection.ProjectionWriteMarkerContractTests"` | Theory over {in-memory, SQLite temp dir}, through `CanonProjectionService.ProjectIncremental`: batch for `sess-a` → first pass builds it; second record for `sess-a` → next pass `Built == 1`, `RebuiltKeys == [sess-a]`, projection `SpanCount` grows; batch for `sess-b` → only `sess-b` rebuilt, `sess-a` in `SkippedKeys`; no write between passes → second pass short-circuits (`Built == 0`); `DeleteSource` of `sess-a`'s source → next pass prunes it (`Pruned == 1`) | on main the pass after the second batch reports `Built == 0` and the old `SpanCount`, on both stores | all cases pass on both stores |
| T1 | `KyberDash.Application.Tests.SettingsCleanDoctorReportTests` (existing) | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~KyberDash.Application.Tests.SettingsCleanDoctorReportTests"` | The D11 assertion: marker after an all-scope clean is greater than the marker just before it; every other assertion unchanged | none (existing test, adjusted) | class green |
| T2 | `KyberDash.Application.Tests.Projection.ProjectionWriteMarkerContractTests` (add cases) | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~KyberDash.Application.Tests.Projection.ProjectionWriteMarkerContractTests"` | Theory over both stores: a maintenance pass (`ProjectIncremental`, no deferral) during which a record for an already-read session is written through the real `WriteBatch` (a `DelegatingReader`-style wrapper, as `ProjectionReadPathTests.MidPassIngestReader` does, minus its hand-set marker) → after the pass `GetProjectionState().RecordsMarker` equals the marker read at pass start, `IsProjectionCurrent()` is false, and the next pass rebuilds that session with the mid-pass record counted; the same with a read-path pass that also defers an oversized key → dirty keys after the pass are the deferred key ∪ the mid-pass key, `FromSeq` = pass-start marker | after the pass `IsProjectionCurrent()` is true and the next pass leaves the mid-pass record out | all cases pass on both stores |
| T3 | `KyberDash.Application.Tests.Projection.ProjectionReadPathTests` | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~KyberDash.Application.Tests.Projection.ProjectionReadPathTests"` | No-new-test task: hand-set `SetWriteMarker` calls that mimic a write removed (including `MidPassIngestReader`'s); `SetProjectionDirtyState` setups kept, using the store's real marker; assertions unchanged | none: setup refactor; the same class green with a non-zero test count | class green, no assertion changed |
| T3 | `KyberDash.Application.Tests.Projection.SessionProjectionTests` | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~KyberDash.Application.Tests.Projection.SessionProjectionTests"` | Same, for `ProjectIncremental_ShortCircuitsWhileCurrentAndInvalidatesOnWriteMarkerAdvance` | none | class green, no assertion changed |
| T3 | `KyberDash.Domain.Tests.CanonAnalysisTests` | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~KyberDash.Domain.Tests.CanonAnalysisTests"` | Same, for the `SetWriteMarker` calls after store writes | none | class green, no assertion changed |
| T4 | `KyberDash.Data.Tests.Repositories.OperationalStoreTests` (extend) | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~KyberDash.Data.Tests.Repositories.OperationalStoreTests"` | Write many batches, delete their sources, call `Vacuum()` → database file length strictly smaller than before the call, and the store still reads (`CountRecords() == 0`, `GetWriteMarker()` unchanged by `Vacuum`); `Vacuum()` on an empty store succeeds | `Vacuum` missing (compile failure in the test file) | test passes |
| T4 | `KyberDash.Application.Tests.SettingsCleanDoctorReportTests` (existing, unedited) | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~KyberDash.Application.Tests.SettingsCleanDoctorReportTests"` | `RunVacuumLeavesTheRelationalStoreAnswering` still passes with `RunVacuum` now running `VACUUM` | none (existing test) | class green |

## Tasks

### T1: Store write paths move the marker and record dirty sessions

- **Objective:** `WriteBatch` and `DeleteSource` move `store_write_marker` and merge the dirty range in the same transaction, identically in the SQLite store and the in-memory fake.
- **Capabilities:** C# data access over the store's SQLite script builder, with xUnit tests
- **Difficulty:** hard
- **Technology:** csharp
- **Session:** `store-marker`
- **Depends on:** none
- **Failure mode:** a wrong merge silently leaves sessions out of the dirty set or turns a wildcard into a keyed range, so the sessions view goes stale with no error.
- **Mitigation (decided):**
  - Append the marker and dirty-range statements to the same script `WriteBatch` / `DeleteSource` pass to `ExecuteTransaction`, so a failed write moves nothing.
  - Implement exactly the "Dirty-range merge rules" table in Investigation findings; compute "continuing" and "wildcard" in SQL from the stored rows, not from a prior read in C#, so concurrent writers cannot interleave a stale read.
  - Bump only when the batch has at least one session or record (D2); keys per D3.
  - Mirror the rules in `InMemoryRelationalStore` on `working` inside the existing lock.
  - Keep the new helper in `SqliteRelationalStore.cs` (T4 edits `SqliteRelationalStore.Operational.cs`).
- **Files:**
  - `dash/server/KyberDash.Data/Repositories/SqliteRelationalStore.cs`: `WriteBatch`, `DeleteSource`, a new private helper appending the marker and dirty-range statements
  - `dash/server.tests/KyberDash.Application.Tests/Fakes/InMemoryRelationalStore.cs`: `WriteBatch`, `DeleteSource`, matching private helper
  - `dash/server.tests/KyberDash.Data.Tests/Repositories/StoreWriteMarkerParityTests.cs`: new class `KyberDash.Data.Tests.Repositories.StoreWriteMarkerParityTests`
  - `dash/server.tests/KyberDash.Application.Tests/Projection/ProjectionWriteMarkerContractTests.cs`: new class `KyberDash.Application.Tests.Projection.ProjectionWriteMarkerContractTests` (write → rebuild cases)
  - `dash/server.tests/KyberDash.Application.Tests/SettingsCleanDoctorReportTests.cs`: `AllScopeWipesCollectedDataAndReportsEveryDeletedFigure`, the D11 marker assertion only
- **Commands:**
  - `kyber-weave review gates . --agent <you> --task T1 --red`
  - `kyber-weave review gates . --agent <you> --task T1`
- **Acceptance:**
  - Every T1 Test-contract case passes on both stores; RED recorded as the second pass reporting `Built == 0`.
  - `KyberDash.Data.Tests.Repositories.SqliteRelationalStoreWriteTests`, `KyberDash.Data.Tests.SqliteRelationalStoreParityTests` and `KyberDash.Data.Tests.Repositories.OperationalStoreTests` keep their assertions and pass.

### T2: A projection pass never drops a write that lands while it runs

- **Objective:** when the marker moves during a pass, the pass stamps the marker it read and leaves the new writes dirty, so the next pass rebuilds them.
- **Capabilities:** C# application service logic with xUnit tests against an in-memory fake and a real SQLite store
- **Difficulty:** hard
- **Technology:** csharp
- **Session:** `projector`
- **Depends on:** T1 (the stores now move the marker and record dirty keys)
- **Failure mode:** a pass stamps a marker newer than the records it read, so a span ingested mid-pass never reaches its session.
- **Mitigation (decided):**
  - In `ProjectIncrementalRelational`, when `finalMarker > currentMarker`, stamp `ProjectionStateView(SchemaVersion, currentMarker)` and rewrite dirty state per D5 (stored continuing keyed range → (`currentMarker`, `finalMarker`, stored keys ∪ deferred keys); stored wildcard → (`currentMarker`, `finalMarker`, wildcard); anything else → `ClearProjectionDirtyState`).
  - Leave the `finalMarker == currentMarker` branch exactly as it is.
  - Any race left (a write between reading and rewriting dirty state) leaves range `ToSeq` ≠ marker, which forces a full pass; the tests assert the session appears, not the pass kind.
- **Files:**
  - `dash/server/KyberDash.Application/CanonSessionProjectorEngine.cs`: `ProjectIncrementalRelational` (pass end, the `finalMarker > currentMarker` branch)
  - `dash/server.tests/KyberDash.Application.Tests/Projection/ProjectionWriteMarkerContractTests.cs`: `KyberDash.Application.Tests.Projection.ProjectionWriteMarkerContractTests`, add the mid-pass cases and their wrapper reader
- **Commands:**
  - `kyber-weave review gates . --agent <you> --task T2 --red`
  - `kyber-weave review gates . --agent <you> --task T2`
- **Acceptance:**
  - T2 Test-contract cases pass on both stores; RED recorded as the mid-pass record missing after the next pass.
  - T1's cases in the same class still pass.

### T3: Drop hand-set write markers from tests; correct the clean comment

- **Objective:** tests that faked a write by calling `SetWriteMarker` use the real write instead, and `CleanService.Wipe` no longer claims source deletion leaves the marker alone.
- **Capabilities:** C# test maintenance with xUnit
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `test-cleanup`
- **Depends on:** T2 (the `ProjectionReadPathTests` mid-pass test relies on T2's pass end once its hand-set marker is gone)
- **Files:**
  - `dash/server.tests/KyberDash.Application.Tests/Projection/ProjectionReadPathTests.cs`: every `store.SetWriteMarker(n)` that follows a write, and `MidPassIngestReader.SessionRecordsForRebuild`'s `inner.SetWriteMarker` call; keep the `SetProjectionDirtyState` setups (rewrite their `FromSeq`/`ToSeq` from `store.GetProjectionState()` and `store.GetWriteMarker()` where needed)
  - `dash/server.tests/KyberDash.Application.Tests/Projection/SessionProjectionTests.cs`: `ProjectIncremental_ShortCircuitsWhileCurrentAndInvalidatesOnWriteMarkerAdvance` (replace `store.SetWriteMarker(store.GetWriteMarker() + 1)` with a real `WriteBatch`)
  - `dash/server.tests/KyberDash.Domain.Tests/CanonAnalysisTests.cs`: the `SetWriteMarker` calls that follow a store write
  - `dash/server/KyberDash.Application/Services/CleanService.cs`: `Wipe`, comment above `SetWriteMarker` (D8: keep the bump, say it forces a full pass after projection rows are deleted directly)
- **Commands:**
  - `kyber-weave review gates . --agent <you> --task T3`
- **Acceptance:**
  - The three T3 classes pass with a non-zero test count and unchanged assertions.
  - No `SetWriteMarker` call left in those three test files except one that sets up a non-write scenario, each such call named in the report.

### T4: `kyber vacuum` compacts the SQLite file

- **Objective:** `DoctorService.RunVacuum` checkpoints the WAL and runs `VACUUM` on the SQLite store.
- **Capabilities:** C# data access over the store's SQLite runner, with xUnit tests
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `vacuum`
- **Depends on:** none
- **Files:**
  - `dash/server/KyberDash.Data/Repositories/SqliteRelationalStore.Operational.cs`: new public `Vacuum()` with XML docs (WAL checkpoint `TRUNCATE`, then `VACUUM`, outside `ExecuteTransaction` because `VACUUM` cannot run in a transaction)
  - `dash/server/KyberDash.Application/Services/DoctorService.cs`: `RunVacuum` calls `(operationalStore as SqliteRelationalStore)?.Vacuum()`; update the class remark that says vacuum has no work
  - `dash/server.tests/KyberDash.Data.Tests/Repositories/OperationalStoreTests.cs`: vacuum cases from the T4 row
- **Commands:**
  - `kyber-weave review gates . --agent <you> --task T4 --red`
  - `kyber-weave review gates . --agent <you> --task T4`
- **Acceptance:**
  - T4 Test-contract cases pass.
  - `KyberDash.Application.Tests.SettingsCleanDoctorReportTests` doctor cases still pass (the in-memory store makes `RunVacuum` a no-op).

## Dependency graph

```text
T1 ──> T2 ──> T3
T4 (independent)
```

## Risks

- Merge conflict with `claude/fix/droid-chart-and-per-harness-e2e`, which edits `InMemoryRelationalStore.cs` and `SqliteRelationalStore.Reader.cs`; this plan touches only `WriteBatch` / `DeleteSource` in the fake and no Reader code. Mitigation: whichever merges second rebases its fake edits; PR note.
- Writes now move the marker every collector batch (every second or two), so passes stop short-circuiting under live capture. Mitigation: incremental passes rebuild only dirty keys; this is the pre-#652 behaviour the runbook already describes.
- `VACUUM` on a 1.28 GB file may approach the store's 120 s batch timeout. Mitigation: T4 runs it through the existing bounded runner; a timeout surfaces as the store's normal error, and the owner's store shrinks after `clean`.
- `KyberBridgeEngine` projection health lists keyed dirty keys as `deferredSessions`, so freshly written sessions show there until the next pass. Mitigation: same as pre-#652 behaviour; the maintenance tick clears them within one cadence.
- T1 changes the shared fake, so Application tests outside the named classes may see a moving marker. Mitigation: the findings list every test that reads the marker or dirty state; the council's full suite catches the rest.

## Out-of-scope boundaries

- PR note: full reset the way the owner asked — after this merges, stop the engine and delete `canon.db` (plus `-wal` / `-shm`); the store recreates schema 25 on next start and the first write triggers a full projection. `clean --all` keeps quarantine and problems by design.
- PR note: `CleanService.Wipe` — `clean --all` keeps 271,806 quarantined spans and all ingest problems by design (`CleanRetainsIngestProblemsAndQuarantinedSpansAsDiagnostics`); recommendation: add an explicit `--include-diagnostics` option in a follow-up, and look at why so many spans are quarantined.
- PR note: `dash/server/KyberDash.Data/Repositories/SqliteRelationalStore*.cs` — drives the `sqlite3` CLI, against the C# standard's in-process ADO.NET rule; documented as deliberate in `docs/dash/architecture.md`; recommendation: decide in an ADR whether the standard or the store changes.
- PR note: merge risk with `claude/fix/droid-chart-and-per-harness-e2e` (both edit `InMemoryRelationalStore.cs`).
- PR note: `CanonSessionProjectorEngine.IsRelationalProjectionCurrent` — keys a read-path pass deferred are not retried on a fully quiet store until the next write, because the maintenance pass short-circuits (D6); making a stored dirty state "not current" would re-run a read pass on every dashboard read; left for a human to choose a fix.
- PR note: `dash/server.tests` — the SQLite contract cases run in the unit test projects, against the C# rule that real-resource tests run in CI only, because dash has no Integration.Tests project and the owner asked for both stores (D7); recommendation: decide whether dash gets one.
- No schema change, no new `IOperationalStore` member.

## Verification gates

- Each task's task gate (above).
- Review council full suite over `dash/server/KyberDash.sln` and the docs gates, run by the council.

## Review

End-of-run council under the `code-review` skill over the whole branch; it derives its own command.

## docs-dev closeout

`docs-dev` harvests the Design harvest rows into `docs/dash/architecture.md` and `docs/dash/runbook.md`, fills the Closeout mapping (ADR waiver expected: restores the pre-#652 contract), runs `kyber-weave docs standards migrate` if the standards warning period still reports, and archives the plan.

## Closeout mapping

| ID | Destination | Documentation-impact |
|---|---|---|
| D1, D2, D3, D4, D5 | [dash/architecture.md](../dash/architecture.md#the-shared-canonical-projection) | extends-component; impact-components: KyberDash; last-reviewed 2026-11-24; status archived. Verified: the incremental projection text names `store_write_marker`, `projection_dirty_range` and `projection_dirty_session`, which writes move the marker, which keys they mark, the merge rules, the same-transaction rule and the mid-pass rule; the old `projection_dirty_sessions` / `projection_session_key_map` names are gone |
| D9, D10 | [dash/runbook.md](../dash/runbook.md#vacuum-and-full-reset) | extends-component; impact-components: KyberDash; last-reviewed 2026-11-24; status archived. Verified: 2b states that clean keeps quarantine and problems (correcting a line that said wipe-all clears `quarantine`), that `kyber vacuum` checkpoints, vacuums and checkpoints again, and the full-reset procedure (stop the engine, delete `canon.db` plus `-wal`/`-shm`) |
| D6, D7, D8, D11, AP1 | Documentation waiver: D6 (deferred keys on a quiet store), D7 (where the SQLite contract test lives), D8 (a comment kept) and D11 (test assertion changes) are implementation and test detail whose surviving invariants are in the PR notes; AP1 is the approval gate |
| KyberDash | [dash/architecture.md](../dash/architecture.md), [dash/runbook.md](../dash/runbook.md) | The impact component's canonical documents, updated as above |

no ADR - ADR waiver (D1 to D11): restores the pre-#652 write contract and applies the established store pattern; no new constraint on future work.

## Completion summary

- T1 (0b014ae55, bb335d994, 86e293bff): `WriteBatch` and `DeleteSource` move the marker and merge the dirty range in the write transaction on both stores; empty record session keys are refused.
- T2 (235820ec5, 75cab68fc): a pass keeps mid-pass writes dirty and stamps the marker it read.
- T3 (29366a9ee): tests use real writes, a shared `DelegatingRelationalStoreReader` fake, and the `CleanService` comment is corrected.
- T4 (93deef3a2): `SqliteRelationalStore.Vacuum` (checkpoint `TRUNCATE`, `VACUUM`, checkpoint) and `DoctorService.RunVacuum` calls it.
- NOT VERIFIED: sessions view refreshing against the owner's live store, and vacuum time on a 1.28 GB file — needs the live environment.
