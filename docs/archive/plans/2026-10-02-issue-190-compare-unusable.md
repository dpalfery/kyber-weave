---
id: plans/2026-10-02-issue-190-compare-unusable
title: "KyberDash: Compare is unusable with real data (#190)"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-02
development-mode: test-first
code-refs:
  - recordsForRun
  - compareRuns
  - alignByPhase
  - SessionIdentities
  - recordsForShare
  - CompareRuns
  - toCandidate
  - alignTurnsByPhase
  - fetchRunComparison
  - fetchRuns
---

# KyberDash: Compare is unusable with real data (#190)

**Status: Complete, archived 2026-10-02.** Development mode: `test-first`. Branch:
`hal.hermes.cursor/issue-190-compare-unusable`. Decision-complete: Q1–Q4 locked
as D1–D4. Approve-and-execute 2026-10-02 ("Hal approves"). T1–T6 complete:
share-resolved `recordsForRun` (`shareOf` → `recordsForShare` + per-execution
`dedupeTwinTurns`), Compare picker empty defaults / labels / subagent·zero-turn
filters, architecture/runbook harvest in
[dash/architecture.md](../../dash/architecture.md) and
[dash/runbook.md](../../dash/runbook.md). End-of-run code-review council
**VERDICT: APPROVE**, with A3 disclosure of known host `ts-test` risk (migration
DROP COLUMN + cursor DATE-ROT; disclose-only, not fixed here; not reproduced on
the standalone green dash suite). No new ADR — D1 mirrors existing
SessionIdentities consumers. Archived here per `KW-DOC-LIFECYCLE-003` before
`docs validate . --merge-ready`.

This plan addresses [issue #190](https://github.com/dpalfery/kyber-weave/issues/190):
selecting two runs on Compare yields **Turns: 0**, **Token Delta: 0**,
**Completed Pairs: 0 / 5**, and **No phase-aligned turn pairs**; Run A silently
defaults to the first list entry with no placeholder; pickers show ~450 raw run
ids without date/harness/size, including twin duplicates and 100+ ZCode subagent
runs.

**Discovery provenance.** Kyber-Weave MCP `docs_*` tools were unavailable in this
harness; docs discovery started at [`docs/README.md`](../../README.md) and
[`docs/dash/architecture.md`](../../dash/architecture.md) (also
[`docs/dash/runbook.md`](../../dash/runbook.md) Compare / API notes). Code discovery
used shell `codegraph explore` against the local `.codegraph/` index, then Read
for line-accurate citations. `git rev-parse --show-toplevel` =
`/Users/hal/git/cursor/kyber-weave`.

## Problem and goal

**Problem.** Compare is the phase-aligned run comparison surface (ADR 0012 D11;
architecture API table `/api/kyber/compare/runs`). With a real store the page
looks populated (hundreds of run ids) but every selected pair reports zero turns
and an empty phase-aligned diff. Independently, picker UX makes selection
untrustworthy: Run A auto-selects `runs[0]` with no "Select a run" option; option
labels are `label ?? runId` plus harness only, dropping `started` / `turnCount` /
token totals that `/api/kyber/runs` already serves; the unfiltered list mixes
useful parent runs with twin noise and ZCode subagent runs.

**Goal.** After this plan: (1) comparing two runs that have measured turns in the
canon store yields non-zero turn counts, a non-empty phase-aligned pair list when
either side has turns, and honest token deltas from those turns; (2) neither
picker silently defaults; (3) each option is labeled with date, harness, and size
(turns and/or tokens) so a human can pick among hundreds; (4) picker noise from
subagents / empty runs is reduced per D2 — test-first, without widening the
ontology, and without "fixing" the disclosed host test failures below.

## Approved decisions

| Id | Decision | Provenance |
|---|---|---|
| A1 | Deliver via plan at `docs/plans/2026-10-02-issue-190-compare-unusable.md` on branch `hal.hermes.cursor/issue-190-compare-unusable`. | Conductor assignment for issue #190, 2026-10-02 |
| A2 | Development mode is `test-first` (default; no user opt-out). | Conductor assignment |
| A3 | Disclose, never fix or exclude-from-CI as a "fix", the known host failures: `dash/src/refresh/migration.test.ts` (DROP COLUMN) and `dash/src/providers/cursor.test.ts` six-month-cap (DATE-ROT). Review/closeout must name them when the suite is otherwise green. | Conductor assignment (disclose-only) |
| A4 | Branding gate: any new user-visible string says kyberdash; any new `codeburn/*` wire-format / stored key needs an `ALLOWED` entry with reason in `dash/src/branding/user-visible-name.test.ts`. | Conductor assignment + existing branding test |
| A5 | Closeout verification includes dash typecheck/lint/test/check:reachable (or the plan's focused vitest plus those gates), `docs validate .`, and `docs drift .`. | Conductor assignment |
| A6 | Archive this plan on the finishing PR before `--merge-ready` (`KW-DOC-LIFECYCLE-003`); open PR to `main` with body starting `Fixes #190`. | Conductor assignment + lifecycle rule |
| D1 | **Q1 → (a):** Fix `recordsForRun` to mirror findings/runs: `sessionIdentities().shareOf(id)` then `recordsForShare` / bare `recordsForSession`; apply the same twin-dedupe scope findings use (`dedupeTwinTurns` per execution). Keep `alignByPhase` on `CanonicalRecord`s (phase inference preserved). Options (b)/(c) declined for this PR. | Conductor-relayed answer Q1=a, 2026-10-02; approve-and-execute "Hal approves" |
| D2 | **Q2 → (a):** Client default-exclude runs whose linked session(s) are `is_subagent` and runs with zero measured `turnCount`; optional "Show subagents" toggle. Defer twin-run collapse until labels + filters prove insufficient. Options (b)/(c)/(d) declined for this PR. | Conductor-relayed answer Q2=a, 2026-10-02; approve-and-execute "Hal approves" |
| D3 | **Q3 → (a):** Both pickers start empty with "Select a run"; fetch comparison only when both ids are non-empty, distinct, and explicitly set. Treat deep-link / `initialRunAId` / `initialRunBId` as explicit selection (not silent list default). Options (b)/(c-as-auto-default) declined. | Conductor-relayed answer Q3=a (+ deep-link as explicit), 2026-10-02; approve-and-execute "Hal approves" |
| D4 | **Q4 → (a):** Option label `{localDate} · {harness} · {turnCount}t · {tokens} · {shortId}` with optional `label` prefix when present; `tokens` = measured input+output or "—" when absent. Add tooltip with full `runId` + `workingDirectory` only when truncation collides. Server-composed `displayLabel` (c) declined for this PR. | Conductor-relayed answer Q4=a (+ tooltip on truncation collision), 2026-10-02; approve-and-execute "Hal approves" |
| A7 | Approve-and-execute: finalize Draft → Ready; implementation may proceed on T1–T6 under D1–D4. | Explicit user approval relayed as "Hal approves", 2026-10-02 |

All ledger questions (Q1–Q4) are resolved into D1–D4; none remain open. The Draft
decision ledger is removed.

## Investigation findings

Docs MCP unavailable — fallback path used (see provenance above).

1. **Architecture contract** ([`docs/dash/architecture.md`](../../dash/architecture.md)
   ~L568, ~L724–725): run/turn comparison by task phase lives in
   `dash/src/analysis/compare.ts` (+ `pairing.ts`);
   `GET /api/kyber/compare/runs` returns phase-aligned comparison between two runs.

2. **Empty comparison UI** (`CompareRuns.tsx:329–339`, `:536`, `:630–635`;
   `TurnAlignedDiff.tsx:246–253`): live mode uses `comparison?.pairs` from
   `fetchRunComparison`; `toCandidate` always sets `turns: []`, so the Turns
   badge is entirely API-driven. Empty `pairs` renders "No phase-aligned turn
   pairs available to compare." Token delta and Completed Pairs read the
   verdict/totals (Completed Pairs is the **promotion sufficiency** `n ≥ 5`
   history count — honestly 0 when outcomes are not dual-success and no
   `completedPairCount` query param — not the count of phase-aligned turn
   pairs; still looks broken beside Turns: 0).

3. **Root join defect — `recordsForRun` ignores split-share identity**
   (`bridge.ts:1900–1920` vs `runs.ts:658–661` / `findings.ts:42–45`):
   executions store `sessionId = identities.claim(rawKey, harness)`. Compare
   loads `records WHERE COALESCE(session_id, trace_id) = claimedId`. For split
   (or late) shares the claimed id is **not** the raw key on `records`, so the
   load returns `[]`. `alignByPhase` then yields no pairs; `compareRuns`
   reports `turnCount: 0` and `tokenDelta: 0`. Session payloads / `getRunTurns`
   still work because they key the **session** table by claimed id — hence Run
   Detail can show turns while Compare shows zero for the same run id.

4. **Phase aligner itself is not the empty-state culprit when turns exist**
   (`compare.ts:1216–1266`, `inferTurnPhase:866–926`): every normalized turn
   receives a `TaskPhase`; empty pairs require empty (or phase-less) inputs.
   Client fallback `alignTurnsByPhase` (`CompareRuns.tsx:213–261`) only runs
   when no API comparison is injected and both candidates already carry turns —
   live path never fills `RunCandidate.turns`.

5. **Silent Run A default / asymmetric placeholder**
   (`CompareRuns.tsx:303–309`, `:520–531` vs `:557–569`): Run A has no empty
   option and defaults to `runs[0]`; Run B has "Select a run" but still
   auto-picks the first id ≠ A. `canFetchComparison` then fires immediately
   (`:311–322`).

6. **Picker labeling omits served size/date** (`CompareRuns.tsx:125–135`,
   `:529`; `kyberApi.ts:575–588`; `routes.ts:839–859`):
   `/api/kyber/runs` already enriches `turnCount` / `totalInput` / `totalOutput`
   / `started` via `sumSessionFigures`. Compare drops the size fields in
   `toCandidate` and does not render `started` in the `<option>` text.

7. **List noise**: `fetchRuns()` is unfiltered; session rows carry
   `is_subagent` (`store.ts` session schema) but Compare never reads it. Twin
   harness fold (#182) collapses `cursor-agent` → `cursor` at the derived
   layer; residual "twin duplicates" in the picker are likely distinct derived
   runs / complementary histories, not a second front-end id — filter + labels
   first (D2); twin collapse deferred.

8. **Branding**: `dash/src/branding/user-visible-name.test.ts` `ALLOWED` list
   gates `codeburn/*` wire keys; user-visible copy must say kyberdash (A4).

9. **Disclosed host failures (A3)**: `migration.test.ts` DROP COLUMN;
   `cursor.test.ts` six-month-cap DATE-ROT — do not fix in this plan.

## Test contract (`test-first`)

Locked to D1–D4 (Q1=a, Q2=a, Q3=a with deep-link as explicit selection, Q4=a
with tooltip on truncation collision). Changing this contract returns the plan
to Draft and requires reapproval.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 (RED) | `dash/src/server/kyber-bridge.test.ts` and/or `dash/src/server/kyber-api.test.ts` (compare/runs) | `npm --prefix dash exec vitest run src/server/kyber-bridge.test.ts src/server/kyber-api.test.ts` (narrow `-t` compare/split as needed) | Store with a **split-share** session key (two harnesses on one raw key) → executions carry claimed `sessionId` → `GET /api/kyber/compare/runs` (or `bridge.compareRuns`) returns `runA.turnCount` / `runB.turnCount` > 0 and `pairs.length` > 0 when records exist; bare-key (non-split) runs still compare; twin-dedupe scope matches findings when both apply | New assertions fail on current `recordsForRun` | — |
| T2 (GREEN) | same | same | Same assertions pass after D1 join fix (`shareOf` → `recordsForShare` + twin-dedupe; phase inference via `alignByPhase` preserved) | — | Focused file(s) green; no fabricated sample data on 404 paths |
| T3 (RED→GREEN) | `dash/web/src/components/analysis/kyber-views.test.tsx` and/or new `CompareRuns` test module | `npm --prefix dash exec vitest run src/components/analysis/kyber-views.test.tsx` (and new file if added) | Both pickers expose empty "Select a run"; no auto-select of `runs[0]`; fetch only when both ids explicitly set (deep-link/`initial*` count as explicit); options use `date · harness · Nt · tokens · shortId` (optional label prefix); tooltip with full runId + cwd when truncation collides; default list omits `is_subagent` / zero-`turnCount` runs with a "Show subagents" toggle | UI assertions fail on current markup/defaults | Pass after D2–D4 UI work |
| T4 (RED→GREEN) | `dash/src/analysis/compare-runs.test.ts` (non-regression) | `npm --prefix dash exec vitest run src/analysis/compare-runs.test.ts` | Existing phase-align / sufficiency / outcome-guard contracts remain green; add fixture only if D1 needs pure `alignByPhase` coverage beyond bridge | Any accidental break shows RED | Full file green |
| T5 | docs / plan inventory | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` and `docs drift .` | Architecture/runbook only if prose must name the share-resolved compare load; plan archived on finishing PR | N/A (docs gate) | Zero findings; `KW-DOC-LIFECYCLE-003` clear at merge |
| T6 | review / host disclose | `npm --prefix dash run typecheck && npm --prefix dash run lint && npm --prefix dash run test && npm --prefix dash run check:reachable` | Suite green aside from A3 disclosures | N/A | Council + disclose migration + cursor DATE-ROT |

## Dispatchable tasks

### T1 — RED: split-share compare load contract

- **Objective:** Encode failing API/bridge expectations for compare over claimed
  session ids (and a non-split control).
- **Files/symbols:** `dash/src/server/kyber-bridge.test.ts` and/or
  `kyber-api.test.ts`; behavior of `KyberBridge.compareRuns` /
  `recordsForRun`; fixture pattern from `split-identity.test.ts`.
- **Acceptance:** Focused vitest RED solely for empty turnCount/pairs on
  split-share runs that have records under the raw key.
- **Dependencies:** none (Ready; D1 locked).
- **Required skills:** `test-dev`.

### T2 — GREEN: resolve share identity in `recordsForRun`

- **Objective:** Implement D1: load compare turns the way findings/runs already
  resolve shares (`sessionIdentities` + `recordsForShare` / bare session;
  twin-dedupe per execution). Cover store path; if direct-DB fallback
  remains reachable without `CanonStore`, either route it through the same
  resolution or document why Compare requires the store path.
- **Files/symbols:** `dash/src/server/bridge.ts` (`recordsForRun`,
  `compareRuns`); possibly thin helpers shared with findings load.
- **Acceptance:** T1 GREEN; 404 still when run ids missing; no sample/demo pairs;
  phase inference via `alignByPhase` unchanged.
- **Dependencies:** T1.
- **Required skills:** TypeScript / KyberDash server (conductor maps specialist).

### T3 — RED→GREEN: picker defaults, labels, filters

- **Objective:** Implement D2–D4 on `CompareRuns` / `toCandidate` (and minimal
  API enrichment only if D2 requires `is_subagent` on the runs list).
- **Files/symbols:** `dash/web/src/pages/CompareRuns.tsx` (`toCandidate`,
  `selectedAId`/`selectedBId`, `<select>` options);
  `dash/web/src/lib/kyberApi.ts` types if enriched; tests under
  `kyber-views.test.tsx` or dedicated Compare test.
- **Acceptance:** Placeholders on A and B; no silent list default; deep-link /
  `initial*` treated as explicit; labels show date/harness/size/shortId; tooltip
  on truncation collision; default filter matches D2 with show-subagents toggle;
  branding test still green (A4).
- **Dependencies:** none beyond Ready (disjoint files from T1/T2).
- **Required skills:** `test-dev` then TypeScript UI.

### T4 — Non-regression: `compare-runs` unit suite

- **Objective:** Keep `alignByPhase` / `compareRuns` sufficiency and outcome
  guards green; extend only if pure analysis coverage is needed for D1.
- **Files/symbols:** `dash/src/analysis/compare-runs.test.ts`,
  `dash/src/analysis/compare.ts`.
- **Acceptance:** File green after T2.
- **Dependencies:** T2 (if analysis signatures change); else parallel after Ready.
- **Required skills:** `test-dev`.

### T5 — Docs harvest + plan archive

- **Objective:** If architecture/runbook must state that compare loads records
  via share-resolved session identity (honest with code), update those docs;
  archive this plan on the finishing PR (`KW-DOC-LIFECYCLE-003`); PR body
  starts with `Fixes #190`.
- **Files:** `docs/dash/architecture.md` and/or `docs/dash/runbook.md` only
  when prose is wrong; this plan + `docs/plans/README.md` inventory row.
- **Acceptance:** `docs validate` / `docs drift` zero findings at PR;
  Active → Archived.
- **Dependencies:** after T2 (and T3 if UI contract is documented).
- **Required skills:** `app-docs-standard` / docs-dev closeout.

### T6 — Gates, review, disclose A3

- **Objective:** Dash gates + code-review council; disclose migration DROP
  COLUMN + cursor DATE-ROT; do not "fix" them in this PR.
- **Dependencies:** T2–T5.
- **Required skills:** `code-review`; dash verify commands from A5.

## Dependency graph and MAX_CONCURRENCY

```
T1 (RED join) → T2 (GREEN join) → T4 (compare-runs non-regression)
T3 (picker UX)  — parallel with T1/T2 (disjoint web vs server)
T2 + T3 → T5 (docs / archive)
T2 + T3 + T4 + T5 → T6 (review / gates)
```

| Task | Depends on | File scope |
|---|---|---|
| T1 | — | server compare tests |
| T2 | T1 | `bridge.ts` (+ helpers) |
| T3 | — | `CompareRuns.tsx` / web tests (+ optional API list enrichment) |
| T4 | T2 if analysis changes; else — | `compare-runs.test.ts` / `compare.ts` |
| T5 | T2, T3 | docs + plan inventory |
| T6 | T2–T5 | PR / gates |

**MAX_CONCURRENCY: 2** after Ready (T1∥T3, then T2∥T3 if T3 not finished, then
T4∥T5 when scopes stay disjoint). Drop to **1** if T3 adds `is_subagent` fields
on the same `/api/kyber/runs` route T1/T2 touch.

## Risks

| Risk | Mitigation |
|---|---|
| Fixing share resolution floods Compare with tool/aux spans as "turns" | D1 uses findings-style load + twin dedupe; revisit llm.invoke filter only with RED evidence vs Run Detail |
| Filtering subagents hides the only comparable ZCode pair a user wants | D2 toggle "Show subagents"; never delete rows from the store |
| Label truncation collides two runs | D4 tooltip with full runId + cwd when truncation collides |
| Direct-DB bridge path bypasses `CanonStore.sessionIdentities` | T2 acceptance: same resolution or explicit store-required behavior with a test |
| Host `ts-test` failures trip review engine | A3 disclose-only; do not expand scope to migration/DATE-ROT |

## Out of scope

- Changing `alignByPhase` / `inferTurnPhase` heuristics except as required by D1.
- Changing promotion sufficiency (`completedPairCount` / n ≥ 5) semantics — only
  clarify UI copy if a task explicitly includes it.
- Fixing `migration.test.ts` DROP COLUMN or cursor six-month-cap DATE-ROT (A3).
- New harness folds or ingest/provider parser changes.
- Twin-run collapse in the picker (deferred per D2).
- KyberDash tray / SEA / refresh pipeline work.
- Live owner repair of `~/.kyberdash/canon.db` as a planning deliverable.

## Verification gates

```bash
npm --prefix dash exec vitest run src/server/kyber-bridge.test.ts src/server/kyber-api.test.ts
npm --prefix dash exec vitest run src/analysis/compare-runs.test.ts
npm --prefix dash exec vitest run src/components/analysis/kyber-views.test.tsx
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run check:reachable
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
```

(Restore/build CLI first if `--no-build` fails. Expect A3 disclosures in full
`npm --prefix dash run test`.)

## Review and docs-dev closeout

- End-of-run `code-review` council on the branch diff.
- Harvest D1 join rule into architecture/runbook only if those docs currently
  imply Compare already share-resolves like findings (T5).
- No new ADR unless implementation invents a second identity model — prefer
  plan decision record + architecture prose (house style of #182 / #231).
- Archive this plan in the finishing PR; move the Active Plans row to Archived.
- PR to `main`, body starts with `Fixes #190`.

## Closeout evidence

| Check | Result |
|---|---|
| T1 RED / T2 GREEN (share-resolved compare load) | Complete — `recordsForRun` via `shareOf` → `recordsForShare` / bare session + per-execution `dedupeTwinTurns`; split-share + twin-dedupe bridge contracts green |
| T3 picker defaults / labels / filters | Complete — empty "Select a run"; D4 labels; D2 subagent/zero-turn filter + "Show subagents"; keyed `isSubagent` enrichment; deep-link keeps selected options in the list |
| T4 compare-runs non-regression | Complete — `compare-runs.test.ts` full file green (no analysis signature change) |
| T5 docs / archive | Complete — D1 join + Compare UX harvested into [dash/architecture.md](../../dash/architecture.md) and [dash/runbook.md](../../dash/runbook.md); plan archived 2026-10-02 |
| T6 review / A3 disclose | Complete — council **APPROVE**; A3 disclose migration DROP COLUMN + cursor DATE-ROT (disclose-only; not fixed; not reproduced on standalone dash suite 4279/0); development complete |
| ADR | None — D1 mirrors existing SessionIdentities consumers (plan + architecture prose) |
