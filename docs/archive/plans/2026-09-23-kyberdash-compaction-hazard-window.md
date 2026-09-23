---
id: archive/plans/2026-09-23-kyberdash-compaction-hazard-window
title: Seed the compaction-hazard detector with the real context window
doc-type: plan
status: archived
owner: dpalfery
last-reviewed: 2026-09-23
component: KyberDash
development-mode: test-first
---

# Seed the compaction-hazard detector with the real context window

**Status:** Archived  
**Archive Date:** 2026-09-23  
**Completion:** Complete — the code-review council returned APPROVE over the accumulated
change with all 15 declared gates green on 2026-09-23; see the Closeout (T5, 2026-09-23)
section at the end of this plan.  
**Approved:** 2026-09-23 — user chose "Approve and execute" at the conductor's
approve-and-execute gate.
**Date:** 2026-09-23
**Development mode:** test-first
**Goal:** Measure the compaction-hazard finding against each session's real context window
(the same window the session analysis already derives from telemetry) instead of the fixed
200,000-token default, with explicit fallback semantics when no source reports a window.

---

## 1. Problem / Motivation

The tray's FINDINGS surface showed "Compaction Hazard: Context consumption reached 261% of
window ... 351,520 tokens recoverable". The 2026-09-23 intake assessment confirmed the
string is computed, not hardcoded — but the denominator is not real:

- `detectFindings` (`dash/src/analysis/findings.ts:1253`) never passes
  `CompactionHazardInput.contextLimit`, so `detectCompactionHazard`
  (`dash/src/analysis/findings.ts:912`) falls back to
  `DEFAULT_COMPACTION_CONTEXT_LIMIT = 200_000` (`findings.ts:906`, used at `:919`) for every
  harness. The 261% reading is a peak turn of ~521,520 tokens against that fixed default.
- The canon layer already derives a real window per session from record attributes —
  `CONTEXT_LIMIT_KEYS` (`dash/src/canon/sessions.ts:35-39`:
  `gen_ai.request.max_context_tokens`, `gen_ai.request.context_window`,
  `model_context_window`), first-reported rule at `sessions.ts:375-378`, persisted in the
  session payload (`ContextAnalysis.contextLimit`, `dash/src/analysis/context.ts:230`) and
  surfaced in reports (`dash/src/analysis/report/build.ts:363`). The detector ignores it.
- Every other consumer prefers the reported window: the harness rollup reads
  `payload.context?.contextLimit` before its 200k fallback
  (`dash/src/canon/harnesses.ts:127`), and the scorecard's `compactionPressure` receives
  that value (`dash/src/analysis/signals.ts:1043-1052`). The finding detector is the one
  unseeded path, so its percentages and recoverable-token estimates are wrong for any
  harness whose model window is not 200k (larger windows over-report hazards and inflate
  `estimatedWasteTokens`, which also skews D6 ranking).

---

## 2. Investigation findings

| Fact | Evidence |
|---|---|
| Detector computes ratio/waste from an optional limit with a 200k default | `dash/src/analysis/findings.ts:906`, `:919`, `:960-963` |
| `DetectFindingsInput.contextLimit` exists and is forwarded to the detector | `dash/src/analysis/findings.ts:1239`, `:1293` |
| The store-driven build stage passes no limit | `dash/src/canon/findings.ts:46-50` |
| Session-level real-window derivation (attribute keys + first-reported rule + fallback) | `dash/src/canon/sessions.ts:32`, `:35-39`, `:375-381` |
| Derivation reads `record.raw` for the keys; proven across sources by the session path | `dash/src/canon/sessions.ts:47-57` |
| Codex reader extracts `model_context_window` from rollout payloads | `dash/src/synth/readers/codex.ts:86-93`, `:225-226` |
| Projection order: sessions (with limits) built, then runs, then findings | `dash/src/canon/sessions.ts:311`, `:331`, `:339` |
| `finding` table is a rebuildable cache; rebuild prunes stale rows | `dash/src/canon/findings.ts:9-10`, `:70-75` |
| Detector has covering tests incl. compaction-hazard | `dash/src/analysis/findings.test.ts:378-418`; mock records accept `raw` overrides (`:57`) |
| No governed doc pins the 200k detector default (no drift risk) | searched `docs/dash/` 2026-09-23 |
| Import direction: analysis modules import from `canon/*` (e.g. `analysis/context.ts:46-48`); `canon/sessions.ts` must not be imported by `analysis/findings.ts` (cycle via `canon/findings.ts`) | module graph |

---

## 3. Decision ledger (answered 2026-09-23; approved results in §4)

### D1 — Window granularity for the detector — ANSWERED (b)

Answered 2026-09-23: option (b), user decision relayed by the conductor; the approved
result is recorded as D1 in §4.

- **(a) Run-level, first-reported.** `buildFindings` derives one limit per run (same rule as
  `buildSessionRow`) and passes it. Smallest diff; wrong when a run's sessions use
  different models.
- **(b) Per-session derivation inside the detector.** `detectCompactionHazard` groups its
  turn list by record `sessionId`, derives each session's window with the shared rule
  unless `input.contextLimit` is explicitly given (override preserved for tests and the
  parity tool), and emits one finding per session over threshold. No caller changes; the
  other six detectors keep their run-level scope. RECOMMENDED — matches the accepted
  "real per-session window" intent; the finding contract is session-scoped (id carries
  `sessionId`, evidence links, deep link `finding/<id>`); also makes the finding's
  `sessionId` truthful (today it is taken from `records[0]` regardless of where the peak
  occurred, `findings.ts:916`).
- **(c) Peak-turn session window, single finding per run.** Keep one finding per run; use
  the window of the session containing the global peak and tag that session. Minimal
  behavioral delta; still misses non-peak sessions that individually cross 85%.

Dependency: D1 shapes T1/T3 test and implementation detail.

### D2 — Fallback semantics when no window is reported — ANSWERED (b)

Answered 2026-09-23: option (b), user decision relayed by the conductor; the approved
result is recorded as D2 in §4.

- **(a) Silent 200k default** (status quo behavior, now per-session).
- **(b) Keep the 200k default, mark provenance, unify the constant.** The finding payload
  records the window it was measured against and its source
  (`contextLimit`, `contextLimitSource: 'reported' | 'default'`), and the two 200k
  constants (`DEFAULT_COMPACTION_CONTEXT_LIMIT`, `DEFAULT_CONTEXT_LIMIT`) collapse into
  one named default shared by the session analysis and the detector. RECOMMENDED —
  aligns with `docs/rules/honest-unobservability.md` and the "named rather than inlined"
  convention at `sessions.ts:28-31`; additive payload only, no report contract change.
- **(c) Suppress the finding when the window is unknown.** Most honest numerically but
  discards real signal for harnesses that never export a window.

Dependency: none (independent of D1).

---

## 4. Approved decisions

- **A1 — Calibrate the compaction-hazard detector to seed from a real context window.**
  Provenance: user accepted the calibration note from the 2026-09-23 intake assessment
  (GAPS), relayed by the conductor. Sibling fallback sites (scorecard `compactionPressure`
  default at `signals.ts:686`, harness rollup fallback at `harnesses.ts:127`, parity
  `PARITY_CONTEXT_LIMIT`) are excluded — they are already real-value-first.
- **D1 — Per-session derivation inside the detector (option b).**
  `detectCompactionHazard` groups its turn list by record `sessionId`, derives each
  session's window with the shared rule unless `input.contextLimit` is explicitly given
  (override preserved for tests and the parity tool), and emits one finding per session
  over threshold; the other six detectors keep their run-level scope. Provenance: user
  decision relayed by the conductor, 2026-09-23 (chose the recommended option).
- **D2 — Keep the 200k default, mark provenance, unify the constant (option b).** The
  finding payload records the window it was measured against and its source
  (`contextLimit`, `contextLimitSource: 'reported' | 'default'`), and the two 200k
  constants (`DEFAULT_COMPACTION_CONTEXT_LIMIT`, `DEFAULT_CONTEXT_LIMIT`) collapse into
  one named default shared by the session analysis and the detector. Provenance: user
  decision relayed by the conductor, 2026-09-23 (chose the recommended option).

---

## 5. Test contract (development-mode: test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `dash/src/analysis/findings.test.ts` (extend "Detector 5" describe) | `npm --prefix dash run test -- src/analysis/findings.test.ts` | (i) records whose `raw` carries `gen_ai.request.max_context_tokens: 1_000_000` with peak 900_000 produce a 90% finding with `estimatedWasteTokens` 50_000 and a mechanism naming the 1,000,000 window; (ii) two sessions in one invocation, window 200k/peak 190k fires, window 1M/peak 190k does not, finding `sessionId` matches the firing session; (iii) explicit `input.contextLimit` still overrides derivation; (iv) payload carries `contextLimit` and `contextLimitSource` `'reported'`/`'default'` | New tests fail on the current detector (it ignores record attributes; no payload fields) — capture the vitest failure output | Same tests pass without weakening assertions; existing detector tests (incl. `findings.test.ts:378-418`) unchanged and green |
| T2 | `dash/src/canon/findings.test.ts` (new; follow the store fixture pattern of `dash/src/canon/sessions.test.ts`) | `npm --prefix dash run test -- src/canon/findings.test.ts` | `buildFindings` over a store whose records report a window persists a finding measured against it (window via `listFindings` payload/mechanism); records without a window keep default behavior | Fails today: `buildFindings` emits findings measured against 200k regardless of attributes | Passes; persisted finding carries the reported window (and provenance) |
| T3 | — implementation task; covered by T1/T2 | — | — | — | — |
| T4 | — gates only | see §9 | — | — | — |

No-test tasks are T3 (implementation, covered by T1/T2) and T4 (verification gates); both
name their replacement verification explicitly.

---

## 6. Task list

| # | Phase | Component | Description | Skills |
|---|---|---|---|---|
| T1 | RED | dash/src | Extend the Detector 5 tests per §5 row T1. | `test-dev` |
| T2 | RED | dash/src | New canon-level integration test per §5 row T2. | `test-dev` |
| T3 | GREEN | dash/src | Shared window module + per-session detector change with provenance-marked fallback. | TypeScript engine implementation (no repository skill pins `dash/src`; conductor maps to the live specialist inventory) |
| T4 | Verification | gates | Run the §9 verification contract. | conductor-mapped |
| T5 | Closeout | docs | Re-check `docs validate` / `docs drift` only if any `docs/` file changed during implementation; update this plan's status. | `app-docs-standard` |

### Task details

**T3 implementation shape:**
- New module `dash/src/canon/context-window.ts` (neutral home: analysis already imports
  from `canon/*`; `canon/sessions.ts` must not be imported by `analysis/findings.ts`):
  exports `CONTEXT_LIMIT_KEYS`, the single `DEFAULT_CONTEXT_LIMIT = 200_000` (moved from
  `sessions.ts:32`, re-exported there for existing importers), and
  `contextLimitOf(records)` returning the first `llm.invoke` record's reported window and
  its source. `buildSessionRow` switches to it with no behavior change (same rule,
  `sessions.ts:375-378`).
- `detectCompactionHazard` (`findings.ts:912`): group the turn list by record
  `sessionId`; derive each group's window via `contextLimitOf` unless `input.contextLimit`
  is set; per-group peak, threshold `floor(limit * 0.85)`, waste `peak − threshold`; one
  finding per group over threshold; id keeps the
  `finding-compaction-hazard-<sessionId>-<spanId>` shape; payload gains
  `contextLimit` and `contextLimitSource`; mechanism text (`findings.ts:994`) keeps naming
  the limit used. Retire `DEFAULT_COMPACTION_CONTEXT_LIMIT` (`findings.ts:906`) in favor
  of the shared default.

---

## 7. Dependency graph and MAX_CONCURRENCY

```text
T1 (RED, analysis tests) ─┐
T2 (RED, canon test) ─────┴─► T3 (GREEN implementation) ─► T4 (gates) ─► T5 (closeout)
```

T1 and T2 touch disjoint files and may run concurrently; everything else is sequential.
**MAX_CONCURRENCY: 2** (audited: only T1/T2 are independent; T3 consumes both RED suites;
T4 consumes T3; T5 consumes T4).

---

## 8. Backward compatibility

- The `finding` table is a rebuildable cache (`dash/src/canon/findings.ts:9-10`, `:70-75`);
  a projection rebuild regenerates findings against real windows and prunes stale rows. No
  migration.
- Report surface unchanged: `FindingReport` (`dash/src/analysis/report/types.ts:134-150`)
  keeps its fields; `recoverableTokens` semantics unchanged; the tray renders title and
  recommendation verbatim (`dash/tray/ui/src/components/FindingsList.tsx:51-57`) and needs
  no change.
- Explicit `contextLimit` callers (existing tests, parity tool) keep override behavior.
- Multi-session runs may emit more than one compaction finding; ranking
  (`rankFindings`, `findings.ts:188`) still orders globally, and the tray shows the top 3.

---

## 9. Verification contract

1. `npm --prefix dash run typecheck`
2. `npm --prefix dash run lint`
3. `npm --prefix dash run test`
4. `npm --prefix dash run check:reachable` (a new engine module is added, so the
   reachable-code surface is touched)
5. Only if `docs/` changed during implementation: `dotnet run --project src/KyberWeave.Cli
   -c Release -- docs validate .` and `docs drift .` (build first if needed)

## 10. Risks

| Risk | Mitigation |
|---|---|
| Model switch mid-session (window changes; first-reported rule mismatches later turns) | Same rule as the session analysis — consistent by design; noted in the module remarks |
| Harnesses that never report a window | Default behavior preserved and provenance-marked |
| Multi-finding cardinality changes tray top-3 composition | Ranked by score; bounded by sessions over 85% (typically one) |
| `record.raw` shape varies by source | The keys are already read successfully by the session path over the same records |

## 11. Out of scope

- Scorecard `compactionPressure` default (`signals.ts:686`), harness rollup fallback
  (`harnesses.ts:127`), parity `PARITY_CONTEXT_LIMIT` — all already real-value-first.
- Any change to the report/tray contracts, the other six detectors' run-level scope, or
  the 85% threshold itself.

## 12. Review and docs-dev closeout

- Review: `code-review` lenses over the diff once T3 lands; the Test contract may not be
  weakened to reach green (scope change returns the plan to Draft).
- Closeout: no governed document states the detector's 200k default (verified 2026-09-23),
  so no doc harvest is required unless implementation touches `docs/`; on completion,
  update this plan's status and the plan index.

---

## Closeout (T5, 2026-09-23)

**Review verdict.** The code-review council returned **APPROVE** over the accumulated change
on 2026-09-23, with all 15 declared gates green.

**Shipped model.** `dash/src/canon/context-window.ts` carries `CONTEXT_LIMIT_KEYS`, the single
`DEFAULT_CONTEXT_LIMIT = 200_000`, and `contextLimitOf(records)` returning the first-reported
window and its source; `buildSessionRow` consumes it with no behavior change.
`detectCompactionHazard` groups its turn list per record `sessionId`, derives each group's
window through that shared rule unless an explicit `input.contextLimit` override is given,
emits one finding per session over threshold, and stamps payload provenance
(`contextLimit`, `contextLimitSource: 'reported' | 'default'`) on every finding;
`DEFAULT_COMPACTION_CONTEXT_LIMIT` is retired. Tests: 34 in `dash/src/analysis/findings.test.ts`,
3 in the new `dash/src/canon/findings.test.ts`, sessions regression 28/28; the full dash suite
is 255 files / 3555 tests green and `check:reachable` is green.

**Documentation closeout.** No doc harvest was required, independently confirming §12. A
retrieval over the governed corpus plus targeted searches (2026-09-23) found no current
document describing the findings/compaction-hazard window derivation: nothing claims findings
are measured against a fixed 200,000-token window, and nothing claims a run yields at most one
compaction finding. The nearest statements are out of scope by §11 — the `compactionPressure`
scorecard signal in `docs/dash/architecture.md` (already real-value-first) and the
`/api/kyber/finding/:id` "single finding detail" endpoint row — and implementation touched no
file under `docs/`. Decisions D1/D2 therefore remain recorded by this plan alone; the plan
index moves this plan's row to the Archived table.

**Mechanical archival step.** This closeout session ran without a shell tool, so two physical
steps remain for the orchestrator:

1. Move this file to `docs/archive/plans/`, set its frontmatter to
   `id: archive/plans/2026-09-23-kyberdash-compaction-hazard-window` and `status: archived`
   (`archived` is outside the closed status vocabulary, so it is only legal once the file
   sits in the excluded archive), and switch this plan's row in the plan index to the
   `../archive/plans/…` link, dropping the row's "Pending the mechanical move" clause, in the
   same edit. Until the move the row must keep pointing at
   this file's current path, or the plan is an unlisted plan in `docs/plans/`
   (`KW-DOC-LIFECYCLE-001`). The body carries no relative links, so nothing else needs
   repointing; the body header above already uses the archived representation of the other
   archived plans.
2. Run the documentation gates from the repository root and confirm zero findings:
   `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` and
   `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .`.
