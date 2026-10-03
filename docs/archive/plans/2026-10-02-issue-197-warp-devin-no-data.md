---
id: plans/2026-10-02-issue-197-warp-devin-no-data
title: "KyberDash: surface and fix Warp/Devin producing no data (#197)"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-03
development-mode: test-first
code-refs:
  - createDevinProvider
  - DevinSessionParser
  - getCostFactor
  - createWarpProvider
  - discoverFromDb
  - openDatabase
  - emptyVerdict
---

# KyberDash: surface and fix Warp/Devin producing no data (#197)

**Status: Complete, archived 2026-10-02, harvested 2026-10-03.** Approve-and-execute
gate recorded 2026-10-02 by the orchestrator under the no-human-tonight protocol: all
questions were grounded in repo docs or carried as open questions with
conservative defaults; none required a human answer. Development mode:
`test-first` (default). Branch: `hal.hermes.opencode/issue-197-warp-devin-no-data` (created from
`origin/main`). T1–T5 complete. Review round 2: the branch merged main so
`copyFileBestEffort` (#257) is in scope — a copyfile(2) EPERM/EACCES now falls
back to read/write, so Warp produces records whenever the bytes are readable,
and the surfaced permission-denied state covers only true TCC denials. Both
halves are required by the issue title: produce data where possible, surface
the reason where not. Harvested into
[dash/architecture.md](../../dash/architecture.md) (Devin rate-independent ingest
and unknown cost; Warp source-unreadable vs cache-write denial) and
[dash/runbook.md](../../dash/runbook.md) (doctor missing-rate and permission-denied).
No new ADR — A3/A4 apply [honest unobservability](../../rules/honest-unobservability.md).
OQ1/OQ2 closed as the conservative defaults taken. Archived per KW-DOC-LIFECYCLE-003.

This plan addresses [issue #197](https://github.com/dpalfery/kyber-weave/issues/197):
Warp reports 4 checkpoints, last success 2026-09-24, 0 records because the sqlite
copy fails with EPERM (Group Containers not readable) and refresh silently skips it;
Devin has a `sessions.db` on disk but doctor says it "holds no sessions", with 0
checkpoints, 0 records, and no collector spans.

## Problem and goal

**Problem (code-verified root causes).**

1. **Devin — the missing price rate disables the whole provider.**
   `createDevinProvider.discoverSessions()` returns `[]` whenever
   `getCostFactor()` is `null` (`dash/src/providers/devin.ts:573`), and that
   happens exactly when `~/.kyberdash/config.json` has no `devin.acuUsdRate`
   (`devin.ts:456-459`). `DevinSessionParser.parse()` likewise yields nothing
   when the rate is null (`devin.ts:482`). The transcripts under
   `~/.local/share/devin/cli/transcripts/` are never read, so ingest, doctor,
   and checkpoints all report zero — doctor's verdict reads as
   "sessions.db exists but holds no sessions" even though sessions exist. The
   live host has no `~/.kyberdash/config.json` at all, matching the issue.
2. **Warp — the EPERM is swallowed twice.** The provider's database open path
   copies the source sqlite through `openDatabase`/the read-only cache
   (`dash/src/ingest/sqlite.ts:262`), which throws when macOS TCC denies the
   Group Containers path. `discoverFromDb` catches and returns `[]`
   (`dash/src/providers/warp.ts:429-431`), and `parse()` writes one stderr line
   and returns (`warp.ts:327-332`). Refresh then records successful checkpoints
   with 0 records and doctor reports the probe roots as missing/no sessions —
   the permission failure is invisible in every surfaced state.

**Goal.** Warp and Devin honestly produce the data they have, and where the
environment makes data unobtainable (Warp's Group Containers TCC denial) the
reason is surfaced in doctor and provider state instead of being flattened
into a zero. Honest measurement: absence is never coerced to a measured zero;
unknown cost is stated as unknown. Nothing is written to any live store.

## Approved decisions

| Id | Decision | Provenance |
|---|---|---|
| A1 | PLAN path (bounded fix, established product); artifact lives under `docs/plans/`. | Orchestrator entry directive |
| A2 | Development mode `test-first` (default; no opt-out). | Orchestrator entry directive |
| A3 | Devin: discovery and parsing must not depend on `devin.acuUsdRate`. Missing rate ⇒ sessions still ingest; `costUSD` reflects the absence honestly (tokenizer/unknown handling pinned by tests), and doctor names the missing rate rather than reporting the db as holding no sessions. | Grounded in `docs/rules/honest-unobservability.md`; code facts above |
| A4 | Warp: an EPERM/EACCES from the sqlite copy is surfaced to doctor (probe path "exists but not readable — permission denied (macOS TCC)") and to the provider/doctor verdict; no TCC workaround, no retrying as another user, no fabricated rows. Checkpoints continue to record 0 records — that remains a truthful measurement of *parsed* records. | Orchestrator directive: EPERM is an environment permission fact |
| A5 | No changes to live stores: all local inspection during this run is `sqlite3 -readonly` with `.timeout 5000`; no migration, no repair, no synth writes against `~/.kyberdash/canon.db` or harness stores. | Orchestrator entry directive |
| A6 | Known-failing tests are disclosed, never fixed: `dash/src/refresh/migration.test.ts` (DROP COLUMN) and the `dash/src/providers/cursor.test.ts` six-month-cap DATE-ROT test; verify against a fresh main run before attributing. | Orchestrator entry directive |

## Tasks (test-first)

| Id | Task | Test contract | File scope |
|---|---|---|---|
| T1 | Devin provider ingests transcripts when `acuUsdRate` is absent; `costUSD` is the honest unknown (0 + unknown cost marker per existing conventions, pinned by test), tokens/tools/session still parse. | RED: extend `dash/src/providers/devin.test.ts` — a transcript parsed without `configureDevinRate()` yields calls with token fields and documented unknown-cost behavior (currently yields nothing). GREEN: provider returns calls; no fabricated cost. | `dash/src/providers/devin.ts`, `dash/src/providers/devin.test.ts` |
| T2 | Doctor reports Devin's missing rate explicitly: when `sessions.db`/transcripts exist but `devin.acuUsdRate` is unset, verdict names the missing rate (not "holds no sessions"). | RED: extend `dash/src/cli/doctor.test.ts` (and devin discovery test) for the no-config case. GREEN: verdict string asserts the missing-rate reason. | `dash/src/cli/doctor.ts`, `dash/src/cli/doctor.test.ts`, `dash/src/providers/devin.test.ts` |
| T3 | Doctor surfaces Warp EPERM/EACCES: a probe root that exists but is unreadable reports permission-denied (macOS TCC) instead of "does not exist"/"no sessions"; `discoverFromDb` no longer swallows permission errors without a surfaced signal. | RED: test with an unreadable/EPERM path injection (EACCES chmod or stubbed openDatabase). GREEN: verdict/probePaths disclose the denial; discoverSessions still returns [] (honest), but the doctor hint names permission. | `dash/src/cli/doctor.ts`, `dash/src/cli/doctor.test.ts`, `dash/src/providers/warp.ts`, `dash/src/providers/warp.test.ts` |
| T4 | Honest-cost marker: when the Devin rate is missing, downstream cost is labeled unknown/estimated per the repo's `costIsEstimated`/unknown conventions rather than presented as measured $0. | Folded into T1's assertions; this row names the contract for review. | `dash/src/providers/devin.ts`, tests |
| T5 | Closeout: harvest A3/A4 into canonical dash docs, close OQ1/OQ2, docs validate --merge-ready + docs drift, archive plan to `docs/archive/plans/`, index sync. | Verification contract below | docs index files |

## Open questions (resolved)

- **OQ1 — Devin unknown-cost representation.** Resolved as the default: keep `costUSD: 0` with `costIsEstimated: true` (unknown, never presented as measured) and name a missing or non-finite `devin.acuUsdRate` in doctor. Harvested into [dash/architecture.md](../../dash/architecture.md) and [dash/runbook.md](../../dash/runbook.md).
- **OQ2 — Warp surfaced-state channel.** Resolved as both: the probe path stays `exists: true` with `accessError: permission-denied`, and `emptyVerdict` names the denial (Full Disk Access on macOS; owner/permissions otherwise). A kyberdash cache-directory `EACCES` is not a source denial. Harvested into the same two documents.

## Verification contract

- `/Users/hal/.dotnet/dotnet build KyberWeave.sln -c Release` and `/Users/hal/.dotnet/dotnet test` green.
- `npm --prefix dash run typecheck`, `lint`, `test`, `check:reachable` green; disclose the two known-failing tests above rather than fixing them.
- `kyber-weave docs validate .` zero findings; `kyber-weave docs drift .` (CodeGraph index permitting) clean or reported skipped with the blocker stated.
- No writes to any live sqlite store during the run (read-only `sqlite3 -readonly` with `.timeout 5000` only).
- User-visible text says `kyberdash`.
