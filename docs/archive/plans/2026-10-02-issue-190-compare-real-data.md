---
id: plans/2026-10-02-issue-190-compare-real-data
title: "KyberDash: make Compare usable with real run data (#190)"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-02
development-mode: test-first
code-refs:
  - CompareRuns
  - fetchRunComparison
  - KyberBridge
  - alignByPhase
  - SessionIdentities
  - MINIMUM_COMPLETED_PAIRS
decided-by:
  - adr/0008-kyberdash-single-canonical-store
  - adr/0009-multi-signal-ingestion-span-shaped-record
  - adr/0012-progressive-disclosure-6-level-diagnostic-spine
---

# KyberDash: make Compare usable with real run data (#190)

**Status: Complete, archived 2026-10-02.** Development mode: `test-first`. Hal approved
execution on 2026-10-02 through the orchestrator overnight approve-and-execute gate; Q1–Q4
stand as answered and are recorded as A3–A6 below. T1–T6 complete: canonical-share turn
loading and honest unavailable states in the comparison backend, explicit A/B selection with
independent harness filters and descriptive inventory labels, architecture/runbook harvest in
[dash/architecture.md](../../dash/architecture.md) and [dash/runbook.md](../../dash/runbook.md).
No new ADR — the work conforms to ADR 0008, ADR 0009, and ADR 0012 D11. Archived here per
`KW-DOC-LIFECYCLE-003` before `docs validate . --merge-ready`.

This plan addresses
[issue #190](https://github.com/dpalfery/kyber-weave/issues/190), an S2 audit finding from
kyberdash 0.1.7-rc.15: the Compare rail silently chose two runs, reported zero turns and
tokens, showed `0 / 5` completed pairs, and offered a long raw-id inventory with insufficient
context to choose useful runs.

**Discovery provenance.** Kyber-Weave MCP `docs_*` tools were unavailable in this harness, so
documentation discovery used the authorized fallback beginning at
[`docs/README.md`](../../README.md), then
[`docs/dash/architecture.md`](../../dash/architecture.md),
[`docs/dash/runbook.md`](../../dash/runbook.md),
[`docs/rules/honest-unobservability.md`](../../rules/honest-unobservability.md), and
[ADR 0012](../../adr/0012-progressive-disclosure-6-level-diagnostic-spine.md). Code discovery
used shell `codegraph explore` against the local `.codegraph/` index before line-accurate
Read/Grep checks. The issue thread had no comments beyond the supplied intake.
`git rev-parse --show-toplevel` returned
`/Users/hal/git/cursor/kyber-weave-43`. The audit's live `~/.kyberdash/canon.db` is not
present in this checkout's host account, so the plan relies on the issue's recorded live
evidence and requires a representative persisted-store regression fixture; it makes no new
live measurement claim.

## Problem and goal

**Problem.** Compare has three independent failure modes:

1. The backend joins a derived execution's persisted `sessionId` directly to
   `records.session_id` / `trace_id`. For a split native key, `SessionIdentities` deliberately
   persists a qualified id such as `cursor:<native-key>` while the raw rows retain
   `<native-key>`. `KyberBridge.recordsForRun` does not reverse that identity to its canonical
   share, although `buildRuns` and `buildFindings` already do. Both selected runs can therefore
   exist while Compare resolves no records and honestly has nothing to align.
2. Empty arrays are then reduced from zero accumulators, so missing records become `Turns: 0`
   and `Token Delta: 0`. Separately, absence of persisted comparison history becomes a
   computed `completedPairCount` of zero or one. Those are absence defaults, not measurements.
3. `CompareRuns` silently defaults Run A to the first run and Run B to the first different run.
   Its option text is only label/raw id plus harness. Although `/api/kyber/runs` already serves
   `started` and measured `turnCount`, the picker omits them and provides no narrowing control.

**Goal.** A user intentionally selects two identifiable runs and receives phase-aligned,
measured turn/token comparisons from their canonical shares. If records, token coverage, or
recommendation history are unavailable, the API and UI state that limitation and show no
fabricated zero. The inventory is newest-first, independently filterable by harness for A and
B, and labels each option with date, harness, measured size, and a distinguishing label/id.

## Approved decisions

| Id | Decision | Provenance |
|---|---|---|
| A1 | Deliver issue #190 through the PLAN path. | User-selected route in conductor assignment, 2026-10-02 |
| A2 | Use `development-mode: test-first`; there is no opt-out. | Conductor assignment and architect default |
| A3 | Do not infer ZCode subagent role from id, label, or size. Make each picker independently filterable by measured harness and give every option descriptive metadata; add role filtering only when the canonical contract carries that evidence. | Q1 auto-resolved from docs using the conservative honest-measurement choice; approved by Hal through orchestrator overnight approve-and-execute, 2026-10-02 |
| A4 | When no persisted comparison history exists, omit the completed-pair count, mark recommendation history unavailable, and keep the selected pair manual-only. Preserve the n ≥ 5 promotion guard only for an explicitly measured count. | Q2 auto-resolved from docs using the conservative honest-measurement choice; approved by Hal through orchestrator overnight approve-and-execute, 2026-10-02 |
| A5 | When a selected run resolves no comparable canonical turns, return a first-class unavailable state with a reason and no token delta; do not substitute zero or a run-list aggregate for phase-aligned evidence. | Q3 auto-resolved from docs using the conservative honest-measurement choice; approved by Hal through orchestrator overnight approve-and-execute, 2026-10-02 |
| A6 | Rely on the shipped canonical twin folds and turn dedupe, test Compare against canonical shares, and do not add picker-only dedupe that could merge real runs. Verify against a rebuilt derived store; stale pre-fold projections use the documented `kyber build` step. | Q4 auto-resolved from docs using the conservative honest-measurement choice; approved by Hal through orchestrator overnight approve-and-execute, 2026-10-02 |

## Investigation findings

1. **The empty comparison is a real join defect.** `buildRuns` assigns execution/session ids
   through `SessionIdentities.claim` and, when it later needs raw records, reverses the id with
   `shareOf` before calling `recordsForShare`
   (`dash/src/canon/runs.ts:421-455,655-663`). `buildFindings` follows the same rule
   (`dash/src/canon/findings.ts:37-50`). `KyberBridge.recordsForRun` instead maps execution ids
   directly through `recordsForSessionKey` (`dash/src/server/bridge.ts:1896-1920`). A qualified
   split-share id therefore misses raw rows kept under their native key.
2. **Compare currently counts records, not canonical model turns.**
   `recordsForRun` returns every matching canonical record; `alignByPhase` converts every
   supplied `CanonicalRecord` into a `RunTurn`. The established session/run-detail contract
   treats `op === 'llm.invoke'` as turns (`dash/src/canon/sessions.ts:607-617`;
   `KyberBridge.assembleTurnContent` and `getRunTurns`). The comparison loader must resolve each
   execution's share, apply the existing `dedupeTwinTurns` rule within that share, and pass only
   model turns to phase alignment.
3. **Zero is the current empty-array identity, not evidence.** `compareRuns` initializes token
   totals at zero and subtracts them even when both input arrays are empty
   (`dash/src/analysis/compare.ts:1361-1383`). `getTurnTokens` also defaults absent token
   components to zero (`:1019-1024`). The issue's zero displays are therefore not proof of
   measured zero.
4. **`0 / 5` is not persisted history.** When the route omits `completedPairCount`,
   `compareRuns` substitutes one only if both outcomes are successful and zero otherwise
   (`dash/src/analysis/compare.ts:1397-1402`). The public route also accepts an arbitrary query
   parameter as if it were history (`dash/src/server/routes.ts:475-480`). Neither path is a
   store-backed measurement. `MINIMUM_COMPLETED_PAIRS = 5` remains valid for promoting
   recommendations, but no count should be shown until a source actually measures it.
5. **Silent selection is in the component.** `selectedAId` defaults to `runs[0]`; Run B defaults
   to the first different row (`dash/web/src/pages/CompareRuns.tsx:303-309`). Only B renders a
   `Select a run` option (`:514-570`). A deep link's explicit `a`/`b` values remain valid user
   selections and should still load.
6. **Picker metadata already exists.** `KyberRunSummary` and `/api/kyber/runs` carry `started`,
   `harness`, and measured `turnCount`; `CompareRuns.toCandidate` drops `turnCount` and options
   render only `label ?? runId` plus harness. The minimal inventory fix is primarily client-side.
7. **The inventory is already newest-first.** `KyberBridge.listRuns` orders by `started DESC`
   (`dash/src/server/bridge.ts:2801-2822`). Preserve that order through filtering.
8. **Twin duplicates are not a new issue-190 algorithm.** Current architecture records the
   `claude-desktop` → `claude-code` and `cursor-agent` → `cursor` fold plus exact/overlap/file
   turn dedupe. The audit predates those delivered fixes. This plan tests the identity seam that
   Compare still bypasses; it does not introduce a second dedupe ontology.
9. **Branding applies to every new visible string.** New UI copy uses `kyberdash` if it names
   the product. This work should not add a `codeburn/*` wire key; if implementation proves one
   unavoidable, `dash/src/branding/user-visible-name.test.ts` `ALLOWED` must gain the exact key
   and migration reason in the same task.

## Test contract (`test-first`)

Changing or weakening this contract returns the plan to Draft for reapproval.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 (RED, backend) | `dash/src/server/kyber-api.test.ts`; `dash/src/analysis/compare-runs.test.ts` | `npm --prefix dash exec -- vitest run src/server/kyber-api.test.ts src/analysis/compare-runs.test.ts` | A real server over a seeded store with split native keys compares two derived runs with non-empty, deduped `llm.invoke` turns and measured token delta; an intentionally unresolvable run returns unavailable comparison/totals with a reason, not numeric zero; omitted history has no completed-pair count and cannot promote | New split-identity/API and unavailable-history assertions fail on current direct-key lookup and zero defaults | Focused files pass without weakening existing phase alignment, cost-basis, outcome-regression, or explicit-history tests |
| T2 (GREEN, backend) | same | same | Backend satisfies T1 using the canonical share identity and honest availability contract | T1 evidence attached before implementation | Same focused command green; no schema migration and no raw-row mutation |
| T3 (RED, web) | `dash/web/src/pages/CompareRuns.test.tsx` (new or existing nearest component suite) | `npm --prefix dash exec -- vitest run web/src/pages/CompareRuns.test.tsx web/src/components/analysis/kyber-views.test.tsx` | Both selectors begin at `Select a run`; no compare request occurs until two distinct explicit selections (or explicit deep-link ids) exist; A/B harness filters narrow independently; options include ISO date, harness, measured turn count or an explicit unknown marker, and distinguishing label/id; unavailable totals/history render `—` plus reason and never `0`/`0 / 5` | Observable component assertions fail on current auto-selection, raw option labels, and zero history treatment | Focused web files green; same-run selection remains non-fetching; cross-harness selection remains possible |
| T4 (GREEN, web) | same | same | Compare UI satisfies T3 and consumes the additive availability fields from T2 | T3 evidence attached before implementation | Same focused command green; new visible product text uses `kyberdash`; branding test remains green |
| T5 (docs) | `docs/dash/architecture.md`; `docs/dash/runbook.md` | `/Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli -- docs validate .` and `docs drift .` | Canonical docs state the canonical-share comparison loader, explicit selector behavior, descriptive filtering, and unavailable-history/metric treatment | N/A — governed docs checks replace a unit test | Both docs gates report zero findings |
| T6 (review/closeout) | repository gates and plan inventory | Commands under Verification gates | Full diff passes required gates except only the two disclosed pre-existing tests; review finds no blocking issue; finishing PR archives this plan | N/A | Evidence recorded, plan archived, Active row moved, merge-ready docs validation passes |

## Dispatchable tasks

### T1 — RED: persisted-store Compare contract

- **Objective:** Pin the real failure shape before changing production code.
- **Files/symbols:** `dash/src/server/kyber-api.test.ts`
  (`GET /api/kyber/compare/runs`); `dash/src/analysis/compare-runs.test.ts`
  (`compareRuns` availability/history behavior).
- **Fixture:** Build sessions and runs from at least two native keys whose records are split
  across canonical harness shares, so their executions persist qualified session ids while raw
  rows retain native ids. Include a non-`llm.invoke` record and a duplicate twin/file turn to
  prove the comparison counts deduped model turns only. Add an explicit empty/unresolvable run
  fixture for the unavailable branch.
- **Acceptance:** The focused command is RED only on the new contracts; captured output names
  the direct-key miss, fabricated zero, and/or inferred-history mismatch.
- **Dependencies:** none after Ready.
- **Required skills:** `test-dev`.

### T2 — GREEN: canonical-share turn loading and honest comparison result

- **Objective:** Make the run comparison backend read the same canonical share that created
  each execution, and stop representing absent turns/tokens/history as zero.
- **Files/symbols:** `dash/src/server/bridge.ts`
  (`KyberBridge.recordsForRun`, `compareRuns`); `dash/src/analysis/compare.ts`
  (`ComparisonSummary`, `compareRuns`, `getTurnTokens`); `dash/src/server/routes.ts`
  (`/api/kyber/compare/runs`).
- **Implementation contract:**
  - Resolve each execution's persisted session id through the current store's
    `SessionIdentities.shareOf`; read `recordsForShare` when it resolves, otherwise retain the
    legitimate unsplit-session lookup.
  - Apply `dedupeTwinTurns` per execution/share and compare only `llm.invoke` turns. Deduplicate
    by span id when a run contains repeated execution keys. Do not mutate raw rows or derive a
    second run boundary.
  - Add a discriminated measured/unavailable comparison state and reasons. Token totals/delta
    exist only when both selected runs have comparable measured turns; missing token coverage
    remains unavailable rather than flowing through a `?? 0`.
  - The public route does not accept a caller-supplied historical count as measured fact.
    Preserve the n ≥ 5 guard for an explicitly supplied trusted/internal count, but when the
    route has no store-backed history, return history availability with no count and
    `canPromote: false`.
- **Acceptance:** T1 is GREEN; existing explicit `completedPairCount` unit tests continue to
  prove the threshold without making the public route claim history.
- **Dependencies:** T1.
- **Required skills:** TypeScript/KyberDash implementation skill; no C# skill.

### T3 — RED: intentional, identifiable run selection

- **Objective:** Define the user-visible selection and honest-empty behavior before UI changes.
- **Files/symbols:** `dash/web/src/pages/CompareRuns.test.tsx` (new if no focused suite exists);
  `dash/web/src/components/analysis/kyber-views.test.tsx`; behavior of `CompareRuns`.
- **Acceptance:** Tests cover both blank placeholders, request gating, explicit deep-link
  selection, independent A/B harness filters, descriptive option text, preserved newest-first
  order, same-run refusal, and unavailable metrics/history. The tests must not classify ZCode
  runs as subagents from id/label/size.
- **Dependencies:** none after Ready; may run in parallel with T1.
- **Required skills:** `test-dev`.

### T4 — GREEN: picker UX and unavailable rendering

- **Objective:** Make a large real inventory navigable and prevent silent or fabricated
  comparison output.
- **Files/symbols:** `dash/web/src/pages/CompareRuns.tsx`
  (`RunCandidate`, `toCandidate`, selector state and rendering);
  `dash/web/src/lib/kyberApi.ts` (`KyberRunSummary`, `KyberRunComparison`,
  `KyberComparisonVerdict`, `fetchRunComparison`).
- **Implementation contract:**
  - A and B both start blank unless their route/deep-link ids were explicitly provided. Do not
    default either side to an inventory row.
  - Give A and B independent harness filters (`All harnesses` plus observed canonical ids);
    preserve server order within each filtered list. This permits cross-harness comparisons and
    lets the user exclude ZCode without an unsupported subagent inference.
  - Render each option as stable ISO date (or `date unknown`), harness, measured turn count
    (or `turns unknown`), then human label plus a shortened distinguishing id when needed. Keep
    the full id as the option value.
  - Fetch only after two distinct explicit ids exist. Render unavailable turn/token/history
    values as `—` with the API reason. Do not show the recommendation threshold as `0 / 5`;
    state that recommendation history is not measured and keep the current pair manual-only.
  - New user-visible product copy, if any, says `kyberdash`. No new `codeburn/*` key is expected.
- **Acceptance:** T3 is GREEN; a 450-row fixture remains selectable without raw-id-only options;
  a ZCode-heavy fixture can be narrowed independently on either side.
- **Dependencies:** T3 and T2's locked response shape. With that shape fixed by this plan, T4
  may proceed in parallel with T2 after T3, reconciling only `kyberApi.ts` at integration.
- **Required skills:** TypeScript/React implementation skill.

### T5 — Canonical documentation update

- **Objective:** Harvest the fixed contract into current documentation.
- **Files:** `docs/dash/architecture.md` (Run Comparison and Phase Alignment, REST schema, web
  surface); `docs/dash/runbook.md` (Compare navigation/selection behavior).
- **Acceptance:** Docs distinguish current-pair measurements from recommendation-history
  sufficiency, state canonical-share resolution and explicit unavailable states, and document
  the picker filters without claiming inferred subagent identity.
- **Dependencies:** T2 and T4.
- **Required skills:** `app-docs-standard`.

### T6 — Gates, review, and docs-dev closeout

- **Objective:** Verify the complete branch, run the review council, and archive the plan in
  the finishing PR.
- **Files:** branch diff; this plan and `docs/plans/README.md` during closeout.
- **Acceptance:** Focused and deterministic gates pass. The full test run may disclose only the
  two known pre-existing failures named below; any other failure blocks. `code-review` produces
  an approve-quality result. Closeout records evidence, archives the plan, moves its inventory
  row, and runs `docs validate . --merge-ready`.
- **Dependencies:** T2, T4, T5.
- **Required skills:** `code-review`; docs-dev closeout.

## Dependency graph and MAX_CONCURRENCY

```text
T1 (backend RED) → T2 (backend GREEN) ─┐
                                      ├→ T5 (docs) → T6 (review/archive)
T3 (web RED) ─────→ T4 (web GREEN) ───┘
```

| Task | Depends on | File scope |
|---|---|---|
| T1 | — | server/analysis tests |
| T2 | T1 | analysis + bridge + route |
| T3 | — | web tests |
| T4 | T3; T2 response contract | Compare page + web API types |
| T5 | T2, T4 | canonical dash docs |
| T6 | T2, T4, T5 | gates, review, plan inventory |

**MAX_CONCURRENCY: 2.** T1 and T3 are disjoint and may run together. After both RED contracts
exist, T2 and T4 may run together because this plan fixes the additive API shape; integration
must serialize the final `KyberRunComparison` type check before T5.

## Risks

| Risk | Mitigation |
|---|---|
| Resolving a qualified id to the wrong native share blends two harnesses | Use `SessionIdentities.shareOf` plus `recordsForShare`, the same mapping already used by `buildRuns`/`buildFindings`; seed a split-key regression with two harness shares |
| Comparing tool/auxiliary records inflates turn counts | Filter to `llm.invoke` after per-share twin/file dedupe; pin an auxiliary-record fixture |
| Optional measurement fields cause client fallback back to zero | Add explicit availability unions and UI assertions that unavailable output contains a reason and no numeric zero/delta |
| Removing silent defaults breaks deep links | Treat explicit route `a`/`b` ids as selections; test both direct navigation and blank rail entry |
| Harness filtering prevents cross-harness A/B | Filters are independent per selector, not one shared filter |
| "Subagent" filtering hides genuine work based on a name or small size | Do not infer role. Q1 deliberately uses measured harness/date/size only |
| Stale pre-#182 derived rows still show twin ids | Verify after canonical rebuild, as current architecture requires; do not add risky UI dedupe over stale rows |
| Existing callers rely on the public `completedPairCount` query parameter | Keep trusted explicit-count analysis tests, but remove the unauthenticated route claim; document the API change and search call sites before removal |

## Out of scope

- Adding ZCode parent/subagent telemetry or changing its provider/database parser.
- Inventing a minimum-turn threshold or classifying runs from id, label, harness reputation, or
  size.
- Reworking run grouping, `SessionIdentities`, the #182 canonical harness folds, or the
  #231/#232 turn dedupe algorithms.
- Persisting historical comparison pairs or building a calibration-history subsystem. Until
  such evidence exists, history is unavailable and promotion remains disabled.
- Changing Context Doctor, Run Detail, Sessions, pricing, findings, or ingest behavior.
- Repairing or rewriting the user's live `canon.db`; verification uses a scratch/fixture store,
  and an owner may separately rebuild derived tables with the documented command.
- Fixing the known unrelated test failures below.

## Verification gates

Focused contract gates:

```bash
npm --prefix dash exec -- vitest run src/server/kyber-api.test.ts src/analysis/compare-runs.test.ts
npm --prefix dash exec -- vitest run web/src/pages/CompareRuns.test.tsx web/src/components/analysis/kyber-views.test.tsx
npm --prefix dash exec -- vitest run src/branding/user-visible-name.test.ts
```

KyberDash gates:

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run check:reachable
```

Documentation gates:

```bash
/Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli -- docs validate .
/Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli -- docs drift .
```

Known pre-existing full-suite failures are **disclosed, never fixed in this issue**:

- `dash/src/refresh/migration.test.ts` — SQLite `DROP COLUMN` failure.
- `dash/src/providers/cursor.test.ts` — six-month-cap `DATE-ROT` failure.

The finishing review records those exact failures as pre-existing if they reproduce; any new
failure or any change to those tests is blocking. If implementation introduces a new
`codeburn/*` wire-format source key despite the expectation above, the branding gate requires
an exact `ALLOWED` entry with the reason the persisted/wire value cannot use the user-visible
`kyberdash` name.

## Review and docs-dev closeout

- Run the `code-review` council over the complete backend, web, tests, and docs diff.
- Confirm the review specifically checks honest unobservability, split-share isolation,
  same-run request gating, and that no stale-projection UI heuristic merges distinct runs.
- Harvest the resulting comparison contract into `docs/dash/architecture.md` and operator UX
  into `docs/dash/runbook.md`. No ADR is expected: the work conforms to ADR 0008's single
  canonical store, ADR 0009's no-double-counting rule, and ADR 0012 D11's phase alignment and
  promotion guard.
- In the finishing PR, append closeout evidence, archive this plan under
  `docs/archive/plans/`, move its Active Plans row to Archived, and run
  `docs validate . --merge-ready` plus `docs drift .`.

## Closeout evidence

| Check | Result |
|---|---|
| T1 RED / T2 GREEN (split-identity canonical-share loader, honest unavailable turns/tokens/history) | Complete — `KyberBridge.recordsForRun` resolves via `SessionIdentities.shareOf` + `recordsForShare`; `compareRuns` and `/api/kyber/compare/runs` return measured/unavailable states; public route no longer treats query params as store-backed history |
| T3 RED / T4 GREEN (explicit A/B selection, independent harness filters, descriptive inventory, unavailable UI) | Complete — `CompareRuns` blank-by-default selectors, request gating, `—` rendering for unavailable metrics/history; branding test green |
| T5 architecture/runbook harvest | Complete — [dash/architecture.md](../../dash/architecture.md) Run Comparison section states canonical-share resolution, current-pair vs recommendation-history separation, and unavailable states; [dash/runbook.md](../../dash/runbook.md) documents Compare picker behavior |
| T6 review / archive | Complete — plan archived 2026-10-02 per `KW-DOC-LIFECYCLE-003`; pre-existing host `ts-test` failures (`migration.test.ts` DROP COLUMN, `cursor.test.ts` DATE-ROT) disclosed, not introduced by #190 |
| ADR | None — A3–A6 and the comparison contract stay in this plan + canonical dash docs (conforms to ADR 0008, 0009, 0012 D11) |
