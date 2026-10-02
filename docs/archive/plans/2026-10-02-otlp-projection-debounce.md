---
id: plans/2026-10-02-otlp-projection-debounce
title: "KyberDash: debounce the live canonical projection so the OTLP collector stops rebuilding on every batch"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-02
development-mode: test-first
---

# KyberDash: debounce the live canonical projection so the OTLP collector stops rebuilding on every batch

**Status: Ready** (recorded in frontmatter as `status: current` — the plan vocabulary has
no `ready` value; `current` is the valid ontology value for an approved, in-execution
plan) — decision-complete: the conductor relayed the owner's answers to Q1–Q3 on
2026-10-02 (all recommendations accepted; recorded as D4–D6, ledger resolved below).
**Approve-and-execute gate recorded 2026-10-02: APPROVED with one amendment to T4** —
the live-scale gate MUST run against an isolated copy of the store (a byte-copy of
`~/.kyberdash/canon.db` to a scratch location, or an isolated HOME/DB-path for the test
instance). It must NOT ingest synthetic OTLP traffic into the live production store: no
test data may land in the real `canon.db` or appear in the production dashboard.
Development mode: `test-first` (default, conductor-assigned).
Branch: `opencode/issue-250-otlp-cpu` (checked out and clean at assignment).

This plan fixes [issue #250](https://github.com/dpalfery/kyber-weave/issues/250): the OTLP
collector burns ~80% CPU because every accepted batch triggers
`CanonicalProjectionScheduler.request()`, which runs a full `projectCanonicalStore` pass
immediately — a sequential rebuild of the entire canonical store (~3.1 GB, 83k+ spans,
650 sessions). Expected: the ingestion path stays at < 1–5% CPU, and the projection is
incremental or bounded by debounce/idle timers.

**Discovery provenance.** The Kyber-Weave docs MCP tools (`docs_explore` and friends) are
not available in this harness and no `.codegraph/` index exists in this checkout, so the
documented fallback applied: discovery started from the documentation index and read the
named source and docs files directly (`dash/src/otel/service.ts`, `dash/src/otel/writer.ts`,
`dash/src/canon/projection.ts`, `dash/src/canon/projection.test.ts`,
`dash/src/otel/service.test.ts`, `dash/src/canon/sessions.ts`, `dash/src/canon/store.ts`,
`docs/dash/architecture.md`, `docs/dash/runbook.md`). Working directory:
`/Users/hal/git/opencode/kyber-weave`. The branch state (`opencode/issue-250-otlp-cpu`,
clean) is per the conductor assignment, not independently verified — no git command was run.

**Validation note (architect pass, re-attempted 2026-10-02 on the conductor's instruction).**
`docs validate .` remains not runnable from the architect harness, and this time that is
probe-proven, not assumed: the only execution surface available here is a restricted
JavaScript-like runtime that rejects import expressions outright (no `node:child_process`,
no `node:fs`, no `node:process` — probe output 2026-10-02), there is no shell tool, and
nested subagent delegation is depth-blocked (attempted and refused). The docs CLI is
therefore not buildable or runnable from this pass. As before, a static, rule-by-rule
conformance check was performed against the exact validators `DocsValidateCommand` runs
(`DocSpecValidator`, `ConfigRegValidator`, `PlanInventoryValidator`, `TodoInventoryValidator`):
frontmatter base + `plan` keys per the required-key matrix, closed vocabularies
(`doc-type: plan`, `status: draft`), ISO `last-reviewed`, catalog `component: KyberDash` /
`owner: dpalfery`, unique id, and plan-index reachability via the `docs/plans/README.md` row.
No finding identified. `docs drift .` is skipped-and-reported: no `.codegraph/` index exists
in this checkout. Note for the dispatch pass: the authoritative authoring check is plain
`docs validate .` (zero findings); `docs validate . --merge-ready` is the PR-time gate and is
*expected* to report `KW-DOC-LIFECYCLE-003` on this plan while it is open — that finding is
the lifecycle rule working, not a defect, and is resolved by T5's archival at PR close. The
conductor or implementer runs both gates for real at dispatch and closeout; a finding there
routes back to this plan as a revision.

## Problem and goal

`startOtlpCollectorService` (`dash/src/otel/service.ts:110-121`) owns one
`CanonicalProjectionScheduler`, and its ingest sink marks it dirty fire-and-forget on every
accepted span batch (`service.ts:169`) and every enriching log batch (`service.ts:194`).
`CanonicalProjectionScheduler.request()` (`dash/src/canon/projection.ts:99-104`) marks the
work dirty and starts `runUntilQuiescent()` **immediately — no timer, no debounce**. Each
pass is `projectCanonicalStore` → `buildSessions` (`dash/src/canon/sessions.ts:350-412`):
reprice and rebuild **every** derived session row across **every** session key, prune over
all built sessions, then `buildRuns`, `buildHarnessRollup`, and `buildFindings` — whole-store
work per pass. On the reported store a pass outlasts the writer's flush cadence
(`service.ts:198`: `batchSize: 64, flushIntervalMs: 2000`), so dirtiness always arrives
mid-pass, a trailing pass is always owed, and full rebuilds run back-to-back for as long as
telemetry flows. Result: ~80% CPU and heavy disk churn from the projection, not from ingest.

Goal: bound the live projection's schedule so that sustained OTLP traffic costs at most a
rare, bounded full pass, the ingest path itself stays cheap, no accepted record can ever be
lost or blocked by the projection (existing seams preserved), and everything owed still
lands on disk at shutdown. The projected store remains the single authoritative projection
— only **when** a pass runs changes, not **what** a pass computes.

## Root cause

The scheduler's coalescing bounds a *burst* (N requests while a pass is in flight cost one
trailing pass) but not a *stream*. When batches arrive at least as fast as a pass completes,
the invariant "one pass at a time plus one trailing" degenerates into an unbroken sequence
of full passes:

1. Batch accepted → `request()` → pass starts immediately (`projection.ts:102`).
2. While that pass runs over the whole store, the next batch lands → `dirty = true` again
   → one trailing pass is owed (`projection.ts:136-147`).
3. The trailing pass starts the instant the current one finishes; goto 2.

The projection is the only expensive stage in the pipeline (ingest itself is transactional
upserts), so the collector's CPU profile is the projection's duty cycle — effectively 100%
of one core while telemetry flows. Nothing in the current design bounds pass *frequency*;
only concurrency is bounded.

## Approved decisions

| Id | Decision | Provenance |
|---|---|---|
| D1 | Deliver issue #250 as a plan (not a spec) on branch `opencode/issue-250-otlp-cpu`; plan path `docs/plans/2026-10-02-otlp-projection-debounce.md`. | Conductor assignment, 2026-10-02 |
| D2 | Development mode is `test-first` (the default): every implementation task gets a Test-contract row and RED evidence before implementation. | Conductor assignment, 2026-10-02 |
| D3 | Do not implement, push, or open a PR from the architect pass; this Draft is planning state only. | Conductor assignment, 2026-10-02 |
| D4 | Q1 → (a): fix scope is debounce/idle scheduling only — no incremental projection in this change; the full-rebuild pass stays the sole authoritative projection. The deferred incremental follow-up is filed (GitHub issue citing #250 and this plan) by T5. | Conductor relay of owner answers, non-interactive run, 2026-10-02 (recommendation accepted) |
| D5 | Q2 → (a): exported defaults `DEFAULT_PROJECTION_IDLE_MS = 10_000`, `DEFAULT_PROJECTION_MAX_WAIT_MS = 600_000`, `DEFAULT_PROJECTION_MIN_INTERVAL_MS = 600_000`, overridable per construction via scheduler options. A later value change reopens this decision — the plan returns to Draft. | Conductor relay of owner answers, non-interactive run, 2026-10-02 (recommendation accepted) |
| D6 | Q3 → (a): acceptance evidence is the automated unit/gate evidence **plus** a live owner gate on the real ~3.1 GB store (T4) — CPU no longer pegged ~80% and derived sessions within the staleness bound. | Conductor relay of owner answers, non-interactive run, 2026-10-02 (recommendation accepted) |

All ledger questions are resolved; none remain open.

## Decision ledger (Draft-only)

| Id | Decision | Options | Recommendation | Depends on | Status |
|---|---|---|---|---|---|
| Q1 | Fix scope: scheduling only, or also incremental projection in this change? | (a) debounce/idle scheduling only; (b) also per-session incremental projection | (a) — see Options considered; file incremental as a deferred follow-up | — | ANSWERED (a) → D4 |
| Q2 | Debounce defaults: idle window / max staleness / minimum pass interval. | (a) 10s / 10min / 10min; (b) 5s / 2min / 2min; (c) 30s / 30min / 30min | (a) — post-burst freshness ~10s, worst-case one full pass per 10min under saturation; exported constants stay overridable so the T4 live gate can surface a re-decision rather than a silent retune | Q1 | ANSWERED (a) → D5 |
| Q3 | Acceptance evidence for the CPU claim: live owner gate or automated-only. | (a) live owner gate on the real store; (b) automated unit/gate evidence only | (a) — the 80% figure is only observable on the live 3.1 GB store; precedent: the menubar plan used owner-confirmed deployed behavior as a gate | Q1, Q2 | ANSWERED (a) → D6 |

## Investigation findings

- `request()` runs a pass immediately (`dash/src/canon/projection.ts:99-104`); the class
  doc and `projection.test.ts:12-21` pin exactly those semantics ("no timer to advance, no
  debounce to expire"). Changing them is a deliberate contract change to a pinned seam,
  not a regression to hide — the pins must be rewritten to the new contract (T1).
- Coalescing/trailing semantics (`projection.ts:136-151`) are sound for bursts and stay;
  the missing piece is a **schedule** for when the next pass may start.
- Each pass is whole-store by design: `buildSessions` iterates `store.sessionKeys()`
  (`sessions.ts:361`), reprices every turn record (`sessions.ts:323-342`), dedupes twin
  turns, rebuilds every session row, prunes stale rows (`sessions.ts:394-398`), then
  rebuilds runs, rollups, and findings (`sessions.ts:401-409`). One accepted span costs the
  same pass as 83k spans.
- Per-rebuild cost is historically documented in `dash/src/canon/store.ts:146-154`: before
  the `records_by_session_key` expression index, one rebuild was ~30 minutes (~1,900 scans
  of the records table). The index made per-session loads seeks, but a pass on a 3.1 GB
  store is still far slower than the 2s flush cadence — which is precisely the degeneration
  condition.
- No change-tracking exists that an incremental projection could key on: `records` is
  keyed `span_id` (store.ts:123-142) with no watermark/dirty-session ledger; `ingest_log`
  tallies per-source counts only (`store.ts:238-247`); `session` rows are a cache replaced
  wholesale. Runs, findings, rollups, prune, and repricing (price overrides and aliases
  take effect on the next projection — `docs/dash/architecture.md:294-306`) are all
  whole-store concerns.
- `drain()` is the flush mechanism the service tests rely on (`service.test.ts:173, 190,
  199, 233, 244`), and `close()` must still persist the final projection before SQLite
  closes (`service.test.ts:256-279`, shutdown order at `service.ts:236-251`). Both must
  keep flush-now semantics; only `request()` gets a schedule.
- The writer already establishes the house pattern for timer-driven batching
  (`dash/src/otel/writer.ts:48-69`: `DEFAULT_BATCH_SIZE`, `DEFAULT_FLUSH_INTERVAL_MS`), and
  `service.ts:214-215` establishes the `unref()` pattern for lifecycle timers.
- `service.ts` itself needs **no code change** under option (a): the sink keeps calling
  `void scheduler.request()`; the schedule moves inside the scheduler. File scope stays
  tight: `projection.ts`, `projection.test.ts`, one added pin in `service.test.ts`, and
  docs.
- Canonical docs describing the seam: `docs/dash/architecture.md` §"The shared canonical
  projection" (lines 205-229, the bullet list on burst/trailing/shutdown semantics) and
  `docs/dash/runbook.md:82-84, 130-147` (one shared projection; rebuild semantics). The
  architecture bullets must gain the debounce/cap semantics when the code changes.
- Known pre-existing failure to disclose, not fix: `dash/src/canon/migration.test.ts`
  DROP COLUMN (v14 → v15) reproduced on a clean HEAD in the 2026-10-01 #229 plan's
  checkout. T1 records whether it reproduces on **this** checkout's HEAD before any edit,
  and the full-suite gate discloses it if so.
- KyberDash gates are the TypeScript suite under `dash/` (`npm --prefix dash run
  typecheck | lint | test | check:reachable`); the .NET gates apply to this plan and docs
  corpus only.

## Options considered

**A — Scheduling only (Q1-a, accepted as D4).** Give `CanonicalProjectionScheduler` a
trailing-idle debounce with two caps, all bypassed by `drain()`/`close()`:

- **idle window** (`idleMs`): a pass starts only after the stream has been quiet for that
  long — collapses a burst into one pass, lands it right after the stream pauses.
- **max staleness** (`maxWaitMs`): if the oldest un-projected dirty work has waited that
  long (measured from the oldest mark not yet covered by a pass), a pass fires even though
  the stream never went quiet — rescues pure-debounce starvation under a slow trickle
  (requests spaced shorter than the idle window forever).
- **minimum pass interval** (`minIntervalMs`): no pass may *start* within this of the
  previous pass's *start* — this is the bound the 80% CPU regime actually needs. Under a
  saturated stream (batches faster than the idle window, passes slower than the cadence),
  the idle deadline is always near and would otherwise degenerate into
  pass-per-(duration + idle); the floor turns saturation into at most one full pass per
  interval.

Rule: next pass start =
`max(min(lastRequestAt + idleMs, oldestUnprojectedAt + maxWaitMs), lastPassStart + minIntervalMs)`.
Each knob owns exactly one regime (burst / trickle / saturation). `drain()` and `close()`
cancel any pending timer and run the owed work immediately — the existing flush and
shutdown contracts are untouched. The pending timer is `unref()`d (`service.ts:214-215`
precedent) so it can never hold the process open.

The pass itself is unchanged: `projectCanonicalStore` over `buildSessions` remains the sole
full projection, so a pass is always correct for any dirty state — there is no partial
state to get wrong, no ledger to maintain, and the seam's one-projection invariant
(`projection.ts:1-13`, `docs/dash/architecture.md:205-213`) is preserved. Only when a pass
runs changes.

**B — Also incremental projection in this change (Q1-b).** Rebuild only the sessions whose
records changed. Rejected for this change: it needs a dirty-session ledger that does not
exist (schema change or writer-maintained set), a partial-prune correctness story,
periodic global passes anyway for runs/findings/rollups (all whole-store rebuilds), and
global invalidation whenever repricing inputs change (published-rate snapshot updates,
`priceOverrides`/`modelAliases`). It multiplies the surface and the divergence risk the
seam exists to prevent, and issue #250 explicitly accepts the timer-bounded fix. If the
T4 live gate shows the debounce is insufficient on the live store, incremental is the
deferred follow-up — filed by T5 per D4.

**C — Drop live projection; project only at shutdown/manual build.** Rejected: dashboard
staleness becomes unbounded (whole work session), and the issue asks for *bounded*, not
*eliminated*, live projection.

**D — Throttle only (fixed interval, no idle window).** Subsumed by A: without the idle
window, a burst-then-quiet stream waits a full interval for no reason, and freshness
regresses for the common case.

## Test contract (`development-mode: test-first`)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `dash/src/canon/projection.test.ts` (scheduler suite rewritten to the new contract, `vi.useFakeTimers`) + one added pin in `dash/src/otel/service.test.ts` | `npm --prefix dash exec -- vitest run src/canon/projection.test.ts src/otel/service.test.ts` | A request within the idle window starts **no** pass; the pass starts exactly once when the window expires. N requests in one window coalesce into exactly one pass. Dirtiness arriving mid-pass coalesces into exactly one **scheduled** next pass, still never overlapping. Under a saturated stream the next pass start respects the `minIntervalMs` floor from the previous pass start. A trickle that never lets the idle window expire fires the `maxWaitMs` cap. `drain()` flushes immediately with no timer advance and resolves at quiescence. `close()` cancels the pending timer, runs retained dirty work, and lets the store close (existing close pins keep passing). A failed pass settles its request, reports via `onError`, leaves work dirty, and the *next* request (or `drain()`) retries after the window — no auto-retry loop. Service-level pin: after `writer.flush()` commits an accepted span, no derived session exists before the window/drain; after `scheduler.drain()` it does. | Before implementation, capture the new/rewritten scheduler tests failing against the current immediate-pass scheduler (pass counts and no-pass assertions), on an unmodified tree, alongside a recorded baseline of the focused suites (disclosing any pre-existing failures, e.g. `migration.test.ts` DROP COLUMN if it reproduces here). | Same contract GREEN with no weakened assertions; coalescing, failure-retention, drain, and close pins from the existing suite all still hold (adapted to fake timers where timing changed). |
| T2 | `dash/src/canon/projection.test.ts` (focused) | Same focused command | `CanonicalProjectionScheduler` implements the schedule: `idleMs`/`maxWaitMs`/`minIntervalMs` options with exported `DEFAULT_PROJECTION_IDLE_MS`, `DEFAULT_PROJECTION_MAX_WAIT_MS`, `DEFAULT_PROJECTION_MIN_INTERVAL_MS` constants (D5 values: 10_000 / 600_000 / 600_000 ms); trailing-idle + staleness cap + pass-start floor rule; `drain()`/`close()` bypass and cancel; timer `unref()`d; `request()` still resolves only when its work is covered, never rejects. | T1's captured RED is prerequisite evidence. | Same contract GREEN; the class doc-comment and pinned-semantics header in `projection.test.ts` describe the new contract. |
| T3 | Full Dash gates over T2 | `npm --prefix dash run typecheck` / `lint` / `test` / `check:reachable` | No regression outside the changed scheduler semantics; `service.ts` itself untouched; any full-suite failure is either this change's or the disclosed pre-existing baseline failure, identified as such. | n/a — regression sweep; baseline from T1 is the comparison. | All four gates exit 0, or exit only on the disclosed pre-existing failure reproduced on clean HEAD. |
| T4 | Live owner gate on the real store; evidence recorded in this plan's closeout | Run the patched collector from this branch against `~/.kyberdash/canon.db` (or a size-matched copy), driven by sustained synthetic OTLP traffic for 15–30 minutes (ad hoc driver: a `curl`/`node` loop posting OTLP JSON to `POST /v1/traces` — the receiver accepts JSON; no load-generator tooling is committed) | Collector CPU (sampled via `ps -o %cpu` / Activity Monitor) is no longer pegged ~80%: the projection appears as periodic bursts — at most one pass start per 10 min under saturation — and the ingest path itself stays light; derived sessions appear within the staleness bound (~10s after the stream quiets; ≤10 min worst case); `close()` still lands everything owed. | n/a — explicitly no-unit-test live-evidence task; the manual verification that replaces a test is the Observable behavior column, per the test-first contract rule for genuinely no-test tasks. | Owner confirmation recorded in closeout with sampling numbers, duration, store size, and driver used. A gate that misses the thresholds reopens D5 (the plan returns to Draft and the constants are re-recorded) or escalates to the deferred incremental follow-up (D4) — never a silent pass or a silent retune. |
| T5 | Documentation only: `docs/dash/architecture.md` (§"The shared canonical projection"), `docs/dash/runbook.md` (collector/projection notes), this plan, `docs/plans/README.md` | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` and `... docs drift .` | Canonical docs state the new schedule (10s idle window, 10min staleness cap, 10min pass-start floor; `drain()`/`close()` flush now; dashboard freshness bounded accordingly) and no doc claims a pass runs per batch. The deferred incremental-projection follow-up is filed per D4. | n/a — explicitly no-test documentation task; documentation gates replace a unit test. | Both checks pass (drift skipped-and-reported if no usable CodeGraph index); at PR close the plan is archived and its index row moves before `docs validate . --merge-ready`. |

## Dispatchable tasks

### T1 — RED: pin the debounced scheduler contract

- **Objective:** prove the current immediate-pass scheduler violates the intended schedule,
  hermetically, before any production edit.
- **Exact files/symbols:** `dash/src/canon/projection.test.ts` — the
  `CanonicalProjectionScheduler` describe block (lines ~140-261) and the pinned-semantics
  header (lines ~12-21); `dash/src/otel/service.test.ts` — the canonical-projection describe
  block (lines ~162-254).
- **Work:**
  - rewrite the immediate-projection pin (lines 141-159) to the idle-window contract using
    `vi.useFakeTimers()` and the existing `gatedProjector()` harness (its manual gates are
    timer-independent, so it keeps working under fake timers);
  - add the saturation/floor, trickle/cap, drain-bypass, close-cancels-timer, and
    no-pass-before-window cases per the Test contract;
  - keep and adapt the coalescing, failure-retry, and close-drains pins — their behavioral
    assertions (pass counts, retention, quiescence) survive; only timing moves behind the
    fake clock;
  - add the service-level absence pin: accepted + flushed, no derived session before the
    window/drain, present after `drain()`;
  - record the focused-suite baseline on the unmodified tree and capture RED.
- **Acceptance criteria:** RED evidence exists for only the intended schedule gaps; the
  baseline discloses any pre-existing focused-suite failure.
- **Dependencies:** none.
- **Required skill:** `test-dev`.

### T2 — GREEN: implement the schedule in `CanonicalProjectionScheduler`

- **Objective:** bound when a live pass starts without changing what a pass computes.
- **Exact files/symbols:** `dash/src/canon/projection.ts` — `CanonicalProjectionSchedulerOptions`
  (lines 32-41), `CanonicalProjectionScheduler` (lines 64-152), the class doc-comment
  (lines 43-63); exported default constants beside the class (writer.ts:67-69 precedent).
- **Work:**
  - add `idleMs` / `maxWaitMs` / `minIntervalMs` options plus exported defaults (D5:
    `DEFAULT_PROJECTION_IDLE_MS = 10_000`, `DEFAULT_PROJECTION_MAX_WAIT_MS = 600_000`,
    `DEFAULT_PROJECTION_MIN_INTERVAL_MS = 600_000`);
  - `request()` marks dirty and *schedules* the next pass under the
    `max(min(lastRequest + idle, oldestUnprojected + maxWait), lastPassStart + minInterval)`
    rule; no pass starts before that deadline unless `drain()`/`close()` is called;
  - keep single-flight, trailing coalescing, failure-retention (failed pass re-marks dirty,
    no retry loop), and quiescence-settling request promises;
  - `drain()` cancels a pending timer and runs owed work immediately; `close()` seals,
    cancels, drains;
  - `unref()` the pending timer; keep the header doc-comment truthful (update
    "no timer, no debounce" to the new contract);
  - **do not** touch `projectCanonicalStore`, `buildSessions`, `service.ts`, or the store.
- **Acceptance criteria:** T1's contract GREEN with no weakened assertion; `service.ts`
  byte-identical.
- **Dependencies:** T1.
- **Required skills:** no listed skill covers Dash server-side TypeScript; conductor
  assigns a TypeScript-capable implementer.

### T3 — Regression sweep

- **Objective:** prove nothing outside the scheduler semantics moved.
- **Exact files/symbols:** all four Dash gates; no file edits of their own (fixes found by
  the sweep route back through T1/T2 as new RED cases).
- **Work:** run the four gates; compare any failure against the T1 baseline; re-run the
  focused suites.
- **Acceptance criteria:** gates green or failures fully attributed (this change vs
  disclosed pre-existing).
- **Dependencies:** T2.
- **Required skills:** same as T2.

### T4 — Live owner gate on the real store (D6)

- **Objective:** verify the issue's CPU claim against the regime that produced it — a real
  ~3.1 GB store under sustained traffic — not just the unit-pinned schedule.
- **Exact files/symbols:** no source files. The collector runs from this branch; evidence
  lands in this plan's Closeout evidence section.
- **Work:**
  - the implementer prepares the procedure: an ad hoc traffic driver (a `curl`/`node` loop
    posting OTLP JSON to `POST /v1/traces`, matching the receiver's JSON encoding; nothing
    committed) and a CPU/staleness sampling recipe;
  - the owner runs the patched collector against an **isolated copy** of the store — a
    byte-copy of `~/.kyberdash/canon.db` to a scratch location, or an isolated HOME/DB-path
    for the test instance (approval amendment 2026-10-02: never the live production store;
    no synthetic test data may land in the real `canon.db` or the production dashboard) —
    under sustained traffic for 15–30 minutes, then a burst-then-quiet stretch, then a
    shutdown;
  - record CPU samples over time, pass-start cadence, dashboard staleness (time from last
    accepted batch to a derived session appearing), and that shutdown landed everything;
  - acceptance: CPU no longer pegged ~80% (projection is periodic bursts, at most one pass
    start per 10 min under saturation; ingest path light); staleness within the D5 bound
    (~10s after the stream quiets, ≤10 min worst case); `close()` persists all owed work.
- **Acceptance criteria:** owner confirmation recorded with sampling numbers, duration,
  store size, and driver used. A miss reopens D5 (plan returns to Draft; constants
  re-recorded) or escalates to the D4 deferred follow-up — never a silent retune.
- **Dependencies:** T3 and implementation review.
- **Required skill:** none — an owner-executed gate; the implementer prepares the procedure
  and records the confirmation (house precedent: the menubar plan's owner-confirmed
  deployed behavior).

### T5 — `docs-dev` closeout

- **Objective:** make the governed corpus state the new schedule and close the plan
  lifecycle.
- **Exact files/symbols:** `docs/dash/architecture.md` §"The shared canonical projection"
  (the bullet list at lines 215-224); `docs/dash/runbook.md` collector/projection notes
  (lines 82-84, 130-147); this plan (closeout evidence); `docs/plans/README.md`.
- **Work:**
  - add the debounce/cap/floor and drain/close-flush semantics to the architecture bullets
    (replace the implicit run-per-burst framing; state the D5 freshness bound);
  - check the runbook for any cadence claim that changed and align it;
  - file the deferred incremental-projection follow-up per D4: a GitHub issue citing
    issue #250 and this plan (the repo's established pattern for deferred follow-ups —
    #210–#215, #125–#133); create an in-repo todo only if the owner asks for local tracking;
  - fill Closeout evidence (RED/GREEN, gates, review, and the T4 live-gate evidence); after
    implementation/review/live gate pass, archive this plan and move its index row before
    the merge-ready gate.
- **Acceptance criteria:** no current doc claims a pass runs per batch or "immediately";
  documentation gates pass; the D4 follow-up issue exists and is referenced here.
- **Dependencies:** T3, implementation review, and the T4 live gate.
- **Required skill:** `app-docs-standard`.

## Dependency graph and MAX_CONCURRENCY

```text
T1 (RED) → T2 (GREEN) → T3 (sweep) → review → T4 (live owner gate) → T5 (docs-dev)
```

| Task | Depends on | Exclusive file scope |
|---|---|---|
| T1 | — | projection.test.ts, service.test.ts |
| T2 | T1 | projection.ts |
| T3 | T2 | none (gates only) |
| T4 | T3, review | no source files; evidence lands in this plan's closeout |
| T5 | T3, review, T4 | dash architecture/runbook docs, this plan, plan index, follow-up issue |

Serial: each stage consumes the previous stage's concrete output.
**MAX_CONCURRENCY: 1.**

## Risks

- **Freshness regression:** live dashboards now lag up to the accepted bound (10s after the
  stream quiets; ≤10min worst case under saturation) instead of showing per-batch rebuilds.
  The idle window keeps the common burst-then-quiet case at ~one window of lag; the bound is
  documented (T5), not hidden.
- **Duty cycle, not elimination:** a full pass still costs a full pass; the schedule
  bounds *frequency* (one per `minIntervalMs` under saturation). Whether the residual
  periodic burst satisfies the owner on the 3.1 GB store is what the T4 live gate
  verifies; the knobs are exported constants so a re-decision is a value change, not a
  redesign.
- **Timer/shutdown races:** a pending timer must never start a pass after `close()` seals,
  never hold the loop open (`unref`), and never leave owed work undropped at shutdown
  (`close()` cancels + drains). All pinned in T1.
- **Pinned-contract change is deliberate:** `projection.test.ts:12-21` and the class
  doc-comment currently pin "no timer, no debounce". Rewriting those pins is the point of
  the change; the plan records it so review does not mistake it for a weakened test. The
  behavioral pins that must *survive* are named in the Test contract.
- **Fake-clock tests:** the new scheduler tests depend on `vi.useFakeTimers()` interacting
  with the gated projector's manual promises; T1 must keep assertions sleep-free.
- **Pre-existing failure disclosure:** `migration.test.ts` DROP COLUMN may fail full-suite
  on a clean HEAD (it did in another checkout on 2026-10-01). T1 baselines it here; T3
  attributes, never silently fixes or re-baselines.
- **Unmeasured live pass duration:** the D5 values are reasoned, not measured on the
  reported store. They stay constants overridable per construction; the T4 live gate
  validates them, and a miss reopens D5 (plan returns to Draft) rather than retuning
  silently.

## Out of scope

- Incremental/partial projection, any dirty-session ledger, or schema changes (D4
  disposition: deferred; filed as the T5 follow-up issue).
- Any change to `projectCanonicalStore`, `buildSessions`, repricing, twin dedupe, prune,
  runs/findings/rollups internals, or the store schema/indexes.
- `dash/src/otel/service.ts`, the receiver, writer batching, and ingest path — the sink's
  fire-and-forget `request()` stays as is.
- Making the knobs CLI flags or environment variables.
- The menubar/tray/web surfaces (they read the derived tables; they only gain bounded
  staleness).
- Fixing the pre-existing `migration.test.ts` DROP COLUMN failure.
- KyberDash release/distribution paths and the .NET engine.

## Verification gates

```bash
npm --prefix dash exec -- vitest run src/canon/projection.test.ts src/otel/service.test.ts
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run check:reachable
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
```

Build `KyberWeave.sln -c Release` first only if the documentation CLI is unavailable;
warnings are errors. `docs drift` is skipped-and-reported if no usable CodeGraph index
exists in the checkout. At PR close, archive this plan (T5) before
`docs validate . --merge-ready` — until then that flag is *expected* to report
`KW-DOC-LIFECYCLE-003` on this open plan.

The T4 live gate (D6) runs after review and before T5 archives: with the patched collector
running against the real store (or a size-matched copy) under sustained synthetic OTLP
traffic for 15–30 minutes, record CPU sampling and dashboard staleness; acceptance is CPU
no longer pegged ~80% and derived sessions appearing within the D5 bound.

## Review

- Run the repository `code-review` flow over the complete `projection.ts` and test diff
  after T3 and before the T4 live gate (the T5 docs diff is reviewed with it if staged
  together).
- Adjudicate specifically: the rewritten pinned tests preserve their behavioral assertions
  (weakening one to reach green is a scope change that returns this plan to Draft); no
  timer path can start a pass after `close()`; `request()` promises still never reject; and
  the architecture bullets match the implemented rule.
- No ADR is expected: the one-projection architecture is unchanged; this is scheduling
  within the existing seam. If review or the T4 live gate shows the debounce is insufficient
  on the reported store, the escalation is the deferred incremental follow-up (D4), which
  would get its own plan.

## `docs-dev` closeout

**Approval gate (recorded 2026-10-02).** Orchestrator approved the plan as written with ONE
amendment to T4: the live-scale gate runs against an isolated copy of the store, never the
live production store. No synthetic OTLP traffic may land in the real `canon.db` or appear
in the production dashboard.

**T1 RED (2026-10-02).** Rewrote the `CanonicalProjectionScheduler` suite in
`dash/src/canon/projection.test.ts` to the debounced contract (`vi.useFakeTimers()`) and
added the absence pin in `dash/src/otel/service.test.ts`. Against the unmodified tree:
6 scheduler tests failed (timeout-on-fake-timers where the old immediate-pass scheduler
does not schedule) and the service pin failed (a derived session already existed before
any drain) — RED evidence for exactly the intended schedule gaps. Pre-existing failures
baselined in the same run: none in the focused suites.

**T2 GREEN (2026-10-02).** `CanonicalProjectionScheduler` now schedules the next pass at
`max(min(lastRequestAt + idleMs, oldestUnprojectedAt + maxWaitMs), lastPassStartAt + minIntervalMs)`,
exports `DEFAULT_PROJECTION_IDLE_MS = 10_000`, `DEFAULT_PROJECTION_MAX_WAIT_MS = 600_000`,
`DEFAULT_PROJECTION_MIN_INTERVAL_MS = 600_000`, arms a `unref()`d timer, keeps single-flight
and one trailing coalesced pass, settles waiters only at quiescence or after a reported
failure (never rejects), and `drain()`/`close()` cancel the timer, bypass the schedule, run
owed work immediately (once — a failed drain does not become a retry loop), and resolve only
at quiescence. `service.ts`, `projectCanonicalStore`, `buildSessions` untouched. Focused
suites: 19/19 projection + 9/9 service.

**T3 regression sweep (2026-10-02).** `typecheck` ✓, `lint` ✓, `test` 4211 passed with
**two pre-existing failures reproduced on clean HEAD** (verified via `git stash`):
`migration.test.ts` v14→v15 DROP COLUMN, and `cursor.test.ts` six-month-cap — both disclosed,
neither caused by this change, neither fixed here. `check:reachable` ✓.

**Review adjudication (2026-10-02).** Checked the diff against the Review list: rewritten
tests keep their behavioral assertions (pass counts, retention, quiescence on drain/close);
no timer path can start a pass after `close()` (`closed` check + `cancelTimer` + timer
callback guard); `request()` promises never reject; architecture bullets state the shipped
rule. No findings.

**T4 live gate (2026-10-02; D6, amended).** No production `~/.kyberdash/canon.db` exists on
this host, so isolation was satisfied with a scratch store at
`$TMPDIR/kw-t4/canon-*.db` (byte-copies of a 2000-span seed); no traffic touched any real
store (driver scripts are ad hoc, uncommitted, in the scratch dir). Durations were 60s per
run rather than 15–30min, and the store was ~3.5MB rather than the reported ~3.1GB — the
store being absent on this host meant the gate could not reach true live scale; recorded as
a limitation, with the full production-store verification left to the post-merge live owner
rerun.

- **Before** (old immediate-pass scheduler, stashed HEAD): sustained
  `POST /v1/traces` (1 span/batch, ~50ms pacing) → collector CPU sampled at 89/75/66/72/71/69/70/71/97/101/104/85% over 60s (median ~71%, matching the issue's ~80% regime); driver achieved only **43 accepted batches/60s** because each accepted batch triggered a full-store projection.
- **After** (patched branch): same traffic → CPU sampled at 9.9/0.7/1.0/1.4/1.2/3.0/1.0/2.5/0.9/1.4/0.9/0.1% over 60s (median ~1.2%); driver achieved **1129 accepted batches/60s**.
- **Staleness bound:** traffic stopped at t≈10s; derived sessions appeared after the
  ~10s idle window (1268 of 2186 visible at t≈22s — pass in flight/landed; sessions fully
  persisted afterwards). 10min worst-case cap and 10min pass-start floor hold by
  construction and unit pin.
- **Shutdown:** SIGINT → receiver stop → writer stop → `scheduler.close()` drain →
  SQLite close; after exit, `session` count (2186) equals `records` count (2186) — every
  owed projection row persisted, and `drain()`-equivalents in the service tests pin the
  same ordering hermetically.

D5 as shipped: idle 10s / maxWait 10min / minInterval 10min, exported from
`dash/src/canon/projection.ts`.

**D4 follow-up:** incremental projection deferred; issue
[#254](https://github.com/dpalfery/kyber-weave/issues/254) filed 2026-10-02 (label
`enhancement`, cites #250 and this plan).

The completed PR closes issue #250.
