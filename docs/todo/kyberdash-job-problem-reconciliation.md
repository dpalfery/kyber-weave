---
id: todo/kyberdash-job-problem-reconciliation
title: KyberDash job-problem row reconciliation after a store heals
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-21
---

# KyberDash job-problem row reconciliation after a store heals

Deferred from the [Kilo Code collection plan](../archive/plans/2026-10-21-kyberdash-kilo-otel-collection.md) (finding F2). The behaviour is recorded in [dash/architecture.md](../dash/architecture.md#local-harness-source-refresh) under "The job row is not reconciled after a repair", and the workaround in [runbook §10](../dash/runbook.md#10-kilo-shows-no-data). This todo records a gap, not a decision: the repair is deliberately deferred.

## Behaviour today

When a harness-source job throws — for example on a present-but-unreadable kilo.db — the job-level catch in `refreshHarnessSources` (`dash/src/refresh/orchestrator.ts`) records exactly one `PROVIDER_PARSE_ERROR` on `harness:<harnessId>:job`. `reconcileSourceProblems` (`dash/src/canon/store.ts`) owns `harness:<harnessId>:<sourceKey>:<code>` rows only (#243), which is its documented scope, so the job row survives the store healing and stays in the Problems list until that harness is cleaned.

## Why it was not fixed in the plan

Reconciling the job row is a cross-harness contract change — the gap is pre-existing for every harness, not Kilo-specific — and was outside the approved scope. D8's "exactly one problem" acceptance holds with the row present: the new Kilo marker yields one problem row, attributed to the harness and naming the database.

## Candidate repairs

- **(a)** Delete the job row when a job completes without throwing.
- **(b)** Record the job problem on a source-keyed span the existing reconcile call owns.
- **(c)** Add an explicit job-unit arm to `reconcileSourceProblems` rather than widening its prefix rule.

Constraint: a run which skips a harness must not clear that harness's job row.

## Workaround

A per-harness `dash clean` removes the harness's problem rows — it also wipes its records, provenance and checkpoints, so the next refresh re-reads them.

## Acceptance criteria

- A healed store's stale `harness:<harnessId>:job` `PROVIDER_PARSE_ERROR` row is removed by a normal refresh, without `dash clean`.
- A run which skips a harness leaves that harness's existing job row untouched.
- Source-keyed reconciliation is unchanged: `reconcileSourceProblems` still owns the `harness:<harnessId>:<sourceKey>:<code>` rows documented in [architecture](../dash/architecture.md#local-harness-source-refresh).
- The architecture and runbook notes are updated to match whichever repair lands.
