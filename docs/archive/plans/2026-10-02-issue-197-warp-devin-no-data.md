---
id: plans/2026-10-02-issue-197-warp-devin-no-data
title: "KyberDash: fix Warp and Devin ingestion gaps and surface refresh skip reasons (#197)"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-02
development-mode: test-first
code-refs:
  - createWarpProvider
  - createDevinProvider
  - openDatabase
  - runHarnessJob
  - iterateNativeUnits
  - readNativeUnit
decided-by:
  - adr/0009-multi-signal-ingestion-span-shaped-record
  - adr/0016-kyberdash-harness-source-refresh
---

# KyberDash: fix Warp and Devin ingestion gaps and surface refresh skip reasons (#197)

**Status: Complete, archived 2026-10-02.** Development mode: `test-first`. Branch:
`hal.hermes.agy/issue-197-warp-devin-no-data`. Pre-authorized approve-and-execute
2026-10-02 (Hal approves gate; Q1=A1, Q2=A3, Q3=A1 locked; ledger closed). T1–T5 complete:
Devin discovery & fallback model pricing unblocked, Warp SQLite immutable fallback & error propagation,
refresh skip/failure diagnostics surfaced in job row and problems table.
Archived here per `KW-DOC-LIFECYCLE-003` before `docs validate . --merge-ready`.

This plan fixes [issue #197](https://github.com/dpalfery/kyber-weave/issues/197):
1. **Warp:** 4 checkpoints, last success 2026-09-24, 0 records; sqlite copy fails
   with EPERM (Group Containers not readable), and refresh silently skips it or
   treats it as an empty/unchanged success.
2. **Devin:** sessions.db exists, doctor says it "holds no sessions"; 0 checkpoints,
   0 records, no collector spans because session discovery and parse are hard-blocked
   when `devin.acuUsdRate` is unconfigured.
3. **Refresh:** Make refresh surface the skip/failure reason in job diagnostics and
   problem records instead of silently skipping or producing vacuous ok checkpoints.

## Problem and goal

**Problem.**
- In `dash/src/providers/devin.ts`, `discoverSessions()` and `parse()` hard-gate on
  `await getCostFactor() === null`. If the user has not configured `devin.acuUsdRate`
  in `~/.kyberdash/config.json`, Devin discovers 0 session sources and parses 0 steps.
  `doctor` checks probe roots (`transcripts` and `sessions.db`), finds `candidatesFound === 0`,
  and reports that `sessions.db exists but holds no sessions; no history yet`. Refresh
  creates 0 units and 0 checkpoints.
- In `dash/src/providers/warp.ts`, `discoverFromDb` and `createParser` catch `openDatabase`
  errors with bare `catch` blocks. When `openDatabase` fails (e.g. SQLite readonly cache
  fails to copy `warp.sqlite` due to `EPERM` in macOS Group Containers), `createParser`
  quietly writes to stderr and yields 0 records. Refresh commits a checkpoint with
  `status: 'ok'` and `recordCount: 0`. On subsequent runs, `orchestrator.ts` sees the
  reusable checkpoint, skips the unit as `unchanged`, and produces 0 records silently.
- In `dash/src/refresh/source-reader.ts` and `orchestrator.ts`, if a unit fails during
  parsing or is unreadable, or when a harness has 0 discovered units despite present
  probe roots, refresh lacks descriptive diagnostic reporting.

**Goal.**
1. Unblock Devin session discovery: discover transcripts regardless of whether
   `devin.acuUsdRate` is configured in config.
2. Unblock Devin parsing: when `devin.acuUsdRate` is unconfigured, fall back to standard
   token pricing (`calculateCost`) with `costIsEstimated: true` instead of dropping all
   steps into the void. Support `DEVIN_CLI_DIR` environment override.
3. Fix Warp SQLite and error handling: in `sqlite.ts`, when copying fails with EPERM,
   attempt `immutable=1` URI read before failing. In `warp.ts`, rethrow / propagate
   database read errors on existing files so failure is not masked as 0-record success.
4. In refresh (`source-reader.ts`, `orchestrator.ts`), surface unreadable units and
   discovery failures on present probe paths as problems / diagnostics rather than
   silently skipping or recording vacuous ok checkpoints.

## Approved decisions

| Id | Decision | Provenance |
|---|---|---|
| A1 | **Devin unblocked discovery & fallback pricing**: Devin session discovery in `discoverSessions()` must never gate on `getCostFactor()`. When `devin.acuUsdRate` is configured, price ACUs via `committedAcuCost * costFactor`. When unconfigured, calculate token cost via `calculateCost(model, ...)` with `costIsEstimated: true` (or 0 if unpriced), preserving tokens, tools, and telemetry. | Principle of honest observability (ADR 0009 / R1.3) |
| A2 | **Devin CLI directory override**: Support `process.env['DEVIN_CLI_DIR']` in `resolveDevinCliDir`. | Consistency with other CLI providers |
| A3 | **Warp SQLite read-only fallback & honest error propagation**: In `sqlite.ts`, if `readOnlyCachePath` fails (e.g. EPERM copy), try immutable in-place URI read (`?immutable=1`) if URI filenames are supported before giving up. In `warp.ts`, `createParser` must propagate database errors instead of quietly returning 0 calls. | Robustness against sandbox EPERM |
| A4 | **Surface refresh skip/failure reasons**: When a unit fails to parse, record `PROVIDER_PARSE_ERROR`, attach the diagnostic to `row.diagnostic`, mark the job `partial` or `failed`, and do NOT commit an `ok` checkpoint with 0 records. When `units.length === 0` but known probe paths exist on disk, surface the skip/empty diagnostic. | Issue #197 core requirement |

## Investigation findings

1. `dash/src/providers/devin.ts`:
   - Line 573: `if ((await getCostFactor()) === null) return [];` in `discoverSessions()`.
   - Line 482: `if (costFactor === null) return;` in `DevinSessionParser.parse()`.
   These two lines completely suppress Devin from both discovery and ingestion on any
   system without explicit `devin.acuUsdRate` in `~/.kyberdash/config.json`.
2. `dash/src/providers/warp.ts`:
   - Line 329: `try { db = openDatabase(dbPath) } catch (err) { ... return; }` swallows
     `openDatabase` errors.
   - Line 429: `try { db = openDatabase(dbPath) } catch { return [] }` swallows discovery errors.
3. `dash/src/ingest/sqlite.ts`:
   - Line 398: `readOnlyCachePath(path, fingerprint)` attempts `copyFileSync`. On macOS
     sandboxed directories or restricted Group Containers, `copyFileSync` throws `EPERM`.
     If `immutable=1` fallback is attempted when copy fails, readable files can still be opened.
4. `dash/src/refresh/orchestrator.ts`:
   - Line 257: `if (units.length === 0) return row;` returns `unavailable` with no diagnostic.
   - Line 314: Checkpoint is stamped `status: 'ok'` even when 0 records are imported due to
     suppressed parse failures.
   - Line 268: `row.skipped += 1` skips unchanged units without disclosing if previous
     checkpoints were vacuous.

## Test contract (`test-first`)

| Task | Test file | Runner command | Behavior | RED assertion | GREEN acceptance |
|---|---|---|---|---|---|
| T1 (RED) | `dash/src/providers/devin.test.ts` | `./node_modules/.bin/vitest run src/providers/devin.test.ts` | Devin discovers and parses sessions without `devin.acuUsdRate` configured in `config.json` | Fails because `discoverSessions` returns `[]` and `parse` yields 0 calls | Passes with estimated/fallback cost |
| T2 (RED) | `dash/src/providers/warp.test.ts` & `dash/src/ingest/sqlite-readonly-parent.test.ts` | `./node_modules/.bin/vitest run src/providers/warp.test.ts` | Warp parser propagates errors when `openDatabase` fails; SQLite attempts immutable open if copy fails | Fails on error swallowing | Passes with error propagation and immutable fallback |
| T3 (RED) | `dash/src/refresh/pipeline.test.ts` | `./node_modules/.bin/vitest run src/refresh/pipeline.test.ts` | Refresh surfaces parse error diagnostics instead of silent ok/skipped row with 0 records | Fails before error handling in reader/orchestrator | Passes with diagnostic surfaced in row and problems table |
| T4 (DOCS) | `docs validate .` & `docs drift .` | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` | Governed documentation and plan lifecycle clean | N/A | 0 findings |
| T5 | Archival | KW-DOC-LIFECYCLE-003 | Move plan to `docs/archive/plans/` before opening PR | N/A | `docs validate . --merge-ready` passes |

## Dispatchable tasks

- **T1: Devin ingestion unblocking (test-first)**: Add test in `devin.test.ts` verifying discovery and parse without configured `acuUsdRate`. Update `devin.ts` to discover sessions and compute fallback estimated costs via `calculateCost`.
- **T2: Warp SQLite resilience and honest error propagation (test-first)**: Add test in `warp.test.ts` verifying that parser propagates openDatabase errors. Update `sqlite.ts` to attempt immutable URI fallback when copy fails. Update `warp.ts` to propagate errors instead of silent empty generator.
- **T3: Refresh surface skip / error diagnostics (test-first)**: Add test in `pipeline.test.ts` asserting that an unreadable source unit or parse failure surfaces diagnostic in `HarnessJobRow` and records a problem, avoiding vacuous ok checkpoints.
- **T4: Verification**: Full dash typecheck, lint, tests, build, and .NET docs validate/drift.
- **T5: Closeout & PR**: Archive plan per KW-DOC-LIFECYCLE-003, push branch, open PR.
