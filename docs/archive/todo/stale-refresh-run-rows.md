---
id: archive/todo/stale-refresh-run-rows
title: refresh_run rows stay running forever after their refresh process dies
doc-type: todo
component: KyberDash
owner: dpalfery
last-reviewed: 2026-09-25
status: superseded
---

# refresh_run rows stay running forever after their refresh process dies

> [!NOTE]
> **Closed by PR #117.** Dead-PID and timed-out runs are reconciled before the next refresh.

This is **context for planning the work, not a plan** — what's known, what needs deciding,
and where the seam is. It does not sequence tasks or commit to an implementation.

## Why this exists

On the deployed KyberDash build, `refresh_run` bookkeeping rows remain in the `running`
state after the refresh process that created them has died. Observed on 2026-09-21/22: rows
started that morning at 03:34, 03:38, and 03:43 still read `running` although no refresh
process from those times was alive. Nothing ever writes their terminal transition, so the
rows are stuck.

## What is known

- The symptom is on the deployed build, not a test fixture, and reproduces across two days
  of observation (2026-09-21 and 2026-09-22).
- It is **not on the report data path**: reports read canonical derived sessions, not
  `refresh_run` rows. The canonical-projection correction executed by the
  [menu-bar runtime wiring plan](../plans/2026-09-20-kyberdash-menu-bar-runtime-wiring.md)
  neither caused this nor fixed it, and it must not be counted as evidence for or against
  that plan.
- It is distinct from the zero-wait `refresh.lock` dead-owner takeover (T10–T11 in the same
  plan): that fix concerns `~/.kyberdash/refresh.lock`, not the `refresh_run` rows the
  refresh pipeline records.

## What needs deciding

- Diagnose the writer that leaves a `refresh_run` row in `running`, and the terminal
  transition it fails to make when the process dies.
- Choose between marking the terminal state at exit and reclaiming a dead owner on read —
  `dash/src/refresh/lock.ts` already implements the dead-owner observation-and-takeover
  precedent, so reclamation on read has prior art in this codebase.
- Pin whichever transition is chosen with a test, so a killed refresh cannot silently strand
  a row again.

## The code seam

- `dash/src/refresh/orchestrator.ts` and the refresh pipeline under `dash/src/refresh/**` —
  wherever `refresh_run` rows are recorded and where the terminal transition belongs.
- `dash/src/refresh/lock.ts` — the dead-owner takeover precedent a read-side reclamation
  would follow.

## Resolution

Resolved by PR #117: `store.reconcileDeadRefreshRuns(importedAtUtc)` runs automatically
before each refresh cycle begins in `refreshHarnessSources`
(`dash/src/refresh/orchestrator.ts`). It inspects every `running` row in
`refresh_run`, probes whether the recorded PID is still alive, treats runs older than 15
minutes as timed out, and transitions orphaned runs to `failure` with an audit summary.
