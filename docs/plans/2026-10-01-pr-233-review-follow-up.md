---
id: plans/2026-10-01-pr-233-review-follow-up
title: "KyberDash: PR #233 review follow-up (F1–F5)"
doc-type: plan
status: current
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-01
development-mode: test-first
---

# KyberDash: PR #233 review follow-up (F1–F5)

**Status: open, approved for execution 2026-10-01.** Development mode: `test-first`.

**Delivery status 2026-10-01: T1–T7 complete, including T7's documentation step.**
`docs/dash/architecture.md` now states the read-side normalisation of the harness checkpoint
join ("Display families and source display names") and the findings browser's paging contract
("Web Dashboard"). The two remaining lifecycle steps belong to PR close and are deliberately not
done here: archiving this plan to `docs/archive/plans/` and moving its inventory row in
[`<plan-index>`](README.md). Until then `docs validate . --merge-ready` reports
`KW-DOC-LIFECYCLE-003` for this plan, which is expected mid-PR.

This plan plans the work for the five findings on
[PR #233](https://github.com/dpalfery/kyber-weave/pull/233) (branch
`fix/dash-181-182-191-findings-quality`) that were resolved in the review threads with **no fix
commit**, plus the fifth thread that was only partially fixed. The 25 kilo-code-bot threads are
otherwise resolved; the findings below are the residue, not a re-litigation of the review.

The branch documents two premises this plan depends on and does not reopen:

- [`dash/runbook.md`](../dash/runbook.md) "Derived Projection Rebuilding": upgrading the binary
  does not rebuild derived tables, so a store written before a change is served through the new
  code until an operator runs `kyber build`.
- [`dash/architecture.md`](../dash/architecture.md) "Display families and source display names":
  twin front-ends fold at the derived layer (issue #182) and derived rows persist canonical ids.

The comment text is third-party data. Every claim below was re-checked against the checkout at
`a5f9003` before it was planned, and the two behavioural findings that could be proved were proved
by running the component, not by reading it (see "Investigation findings").

## Problem and goal

Goal: land the five verified defects under test-first delivery, with F2, F3 and F4 decided together
because they are one state machine, and with the two decisions the reviewer's framing left open
answered from the code rather than deferred.

Out of scope: the 20 resolved threads, the findings the branch already fixes, and any change to
the findings API envelope or the harness normalisation vocabulary.

## Approved decisions

| Id | Decision | Provenance | Reversal cost |
|---|---|---|---|
| A1 | **Approve and execute.** This plan is execution authority for F1–F5 on the existing PR branch. | The user commissioned this plan on 2026-10-01 and asked for a test-first plan covering F1–F5. | None. |
| A2 | **Development mode `test-first`.** Every behavioural finding gets a RED task, then a GREEN task. | Default; no opt-out was supplied. | None. |
| A3 | **F2, F3 and F4 are ONE task (T3/T4).** They are one state machine in one file. | See D1. | High — they are already merged here; splitting them later means re-deriving totals. |
| A4 | **F1 fixes the lookup key only. `?? []` stays a measured zero; the "replace with null" half of the review's suggestion is DECLINED.** | See D3. | Low — one expression and one test. |
| A5 | **F5 ships as its own commit (T5/T6), comment and guard only.** | See D2. | Trivial. |
| A6 | **The retained envelope lives in the accumulated-page state, not in a new `useState`.** | See D4. | Low — it is a derivation over `appended`. |
| A7 | **A stored page is invalidated when the scope's `total` moves, and the offset resets to 0.** | See D5. | Medium — the reset-to-0 half is a one-line change. |

NO_QUESTIONS for the conductor: every decision above was checked against the code and, where the
reviewer's framing was incomplete, corrected by evidence. A4 and A7 are the two a human may want
to overrule; each carries its reversal cost above and its reasoning below.

## Decision ledger (keyed by finding)

| Finding | Site | Decision | Verified reason (one line) |
|---|---|---|---|
| F1 | `dash/src/server/routes.ts:723` | **FIX** (normalise the join key) + **DECLINE** (widen to `null`) | The map is keyed by `normalizeHarnessName` (`routes.ts:265`) and the lookup is not (`routes.ts:723`), so a legacy rollup row misses its own checkpoints and reports a measured zero where the detail route one line below reports the truth. After the key is normalised, a `get()` miss means "read successfully, no units for this harness", which is an observed zero under R2, not an unobserved one. |
| F2 | `dash/web/src/pages/ContextDoctor.tsx:216` | **FIX** | Proved by running the component: on a page-2 failure with 3 rows retained the heading reads "All Workspace Findings (0)", the unknown-window banner is gone, Load more is gone, and no retry renders. The error arm is unreachable whenever rows exist. |
| F3 | `dash/web/src/pages/ContextDoctor.tsx:453` | **FIX** (offset from the served extent) + **FIX** (docblock) | Proved: with pages at offsets 0 (3 rows) and 3 (3 rows, first row an overlap), `browserRows` renders 5 rows and Load more requests offset 5 while the server extent is 6. The docblock's stability claim is credited to the count; it comes from the click-disable and React batching. |
| F4 | `dash/web/src/pages/ContextDoctor.tsx:251` | **FIX** | `AccumulatedPage` carries no `total`, nothing re-keys `appended`, and the browser query is issued only for the current offset — so a page stored before a rebuild is never refetched for the life of the mount. |
| F5 | `dash/src/canon/twin-dedupe.ts:48-49` | **FIX** (docblock only) | `exceedsStep` was deleted in `a689157`; only the span check at `twin-dedupe.ts:146` remains, and the correct inline note at `:142-144` already says why a step bound could not fire alone. The docblock paragraph is the stale half of that change. |

## Decisions and their evidence

### D1 — F2, F3 and F4 are one task, not three

The review presents F3 (offset derived from painted rows) and F4 (pages not keyed on `total`) as
separate defects, and the user asked whether they are one. **They are one task**, and F2 joins it.

The three share a single source of truth: `AccumulatedPage` (the accumulated-page state) plus the
three values read off it at `ContextDoctor.tsx:643-645` and the one derived at `:704`. F3 needs to
know the extent the server served — a property of the stored pages. F4 needs to invalidate stored
pages when the envelope's `total` moves — a property of the same pages. F2 needs the last envelope
those rows were served under. Splitting them means either two competing notions of "how far the
server has been asked" (one from `rows.length`, one from `offset + rows.length`) or a
`lastKnownTotal` that is a third copy of a fact the pages already hold. A naive fix to either one
regresses the other: keying pages on `total` without switching the offset to the served extent
leaves a page whose rows were deduped *and* whose total changed being paged from a stale base;
moving the offset to the served extent without keying on `total` asks for an offset past a window
that no longer exists, which the server answers with a short or empty page.

**Unified state shape** (`ContextDoctor.tsx:250-251`):

```ts
/**
 * One stored page of the browser, tagged with the filter scope it belongs to
 * and the envelope it was served under.
 *
 * <remarks>
 * The envelope travels with the rows because the rows are only meaningful
 * under it: a mid-list failure clears `data`, and a rebuild moves `total`
 * under rows that are already on screen. A page that kept only its rows
 * would leave the heading, the suppression count and the next offset to be
 * re-derived from whatever the failed query returned.
 * </remarks>
 */
export type AccumulatedPage = {
  scope: string
  offset: number
  total: number
  detectorCounts: Readonly<Record<string, number>>
  unknownWindowSessions: number
  rows: KyberFinding[]
}
```

Four pure helpers, all exported and unit-tested (no effects in any of them):

| Helper | Replaces | Contract |
|---|---|---|
| `nextAccumulated(pages, scope, page)` | `nextAccumulated(pages, scope, offset, rows)` | Stores the page under the scope **and** its envelope. Replaces a stored page whose ids changed. Returns the **same array reference** when nothing changed, so the accumulate effect is idempotent under React's dependency array. |
| `retainedEnvelope(pages, scope)` | new | The newest stored page's `{ total, detectorCounts, unknownWindowSessions }`, or `undefined` when the scope has nothing stored. |
| `servedOffset(pages, scope)` | new | `max(offset + rows.length)` over the scope's stored pages; `0` when none. The extent the **server** has served, which survives the id dedupe. |
| `nextBrowserOffset(served, total)` | unchanged signature | `served < total ? served : undefined`. The existing test at `ContextDoctor.test.tsx:521` keeps its arithmetic; only the argument's provenance changes. |

### D2 — F5 is its own commit

F5 is a docblock that describes a check deleted in `a689157`. It shares no file, no state, and no
behaviour with F1–F4. Riding along with a behavioural change would make a comment fix
unreviewable inside a diff that a reviewer is already reading for correctness, and would put a
docs-only edit behind a behavioural RED/GREEN pair — so a revert of the behavioural work would take
the accurate comment with it. It is T5/T6, owning `dash/src/canon/twin-dedupe.ts` and
`dash/src/canon/twin-dedupe.test.ts` only, and it is the one task that can land in parallel with
everything else.

**F5's RED is a source guard**, not a behavioural test: `TWIN_TURN_MAX_SKEW_MS` has no test today
(`rg TWIN_TURN_MAX_SKEW_MS src/canon/twin-dedupe.test.ts` returns nothing), and a behavioural test
cannot fail on prose. T5 reads the module source as text — the same technique
`dash/src/pricing/data/pricing-fallback-data.test.ts` uses to guard a hand-maintained source — and
asserts the docblock no longer claims a per-step bound while still naming the span bound. The guard
is worth its 6 lines here precisely because the comment is load-bearing: it explains a check that
was deliberately removed, and a future reader's first question is "where did the step bound go".

### D3 — F1: normalise the key; do NOT widen `?? []` to `null`

The review asks for two things: normalise the lookup key, and "replace `?? []` with a `null` for an
unmatchable harness rather than a measured zero". The first is correct. The second is declined, and
T1 pins the decline with an assertion so it cannot be re-raised silently.

Evidence against the `null` half:

1. **The rule it would invoke does not apply.** The docblock at `routes.ts:150-153` and the
   comment at `routes.ts:711-712` draw the line already: *"A failed checkpoint read is unknown for
   every row (null); a successful read with no units for this harness is genuinely zero."* The
   honest-unobservability rule agrees — R2 in [`docs/rules/honest-unobservability.md`](../rules/honest-unobservability.md)
   requires distinguishing "0 tokens consumed" (an empirically observed zero) from "unreported". A
   `null` for a read that succeeded and found nothing is the inverse fabrication: a permanent
   unknown over a fact we measured.
2. **The signature already carries `null`.** `checkpointSummaryOf(statuses: readonly SourceCheckpoint[] | null)`
   returns `| null` (`routes.ts:156-162`), and both callers already pass the documented null
   (`:721` for a failed read, `:765` via `filterCheckpointsByHarness`, whose own docblock says
   "Null (unreadable table) stays null — never an empty list posing as none"). The `?? []` is not a
   null-swallowing site; it is the measured-zero arm of a function that already models unknown.
3. **It would change the response shape for every harness that has never recorded a checkpoint**,
   from `{ok: 0, partial: 0, failed: 0, unavailable: 0}` to `null`, and the web type
   (`dash/web/src/lib/kyberApi.ts:567-573`) carries `| null` for API consumers. No web surface
   renders the field, so the change would be invisible in the dashboard and visible only to a
   consumer — a contract change smuggled into a key fix.
4. **The existing test pins the current null semantics**: `kyber-api.test.ts:1109-1110` asserts
   `null` for an unreadable `source_checkpoint` table, and nothing asserts `null` for a readable
   table. Under the reviewer's proposal the two cases would become indistinguishable, which is the
   opposite of what the field is for.

**What T1 asserts instead**, on a readable table with a rollup row that recorded no checkpoints:
`checkpointSummary` is the measured zero and is not `null`. The unknown stays reachable exactly
where it is honest: the unreadable table.

The seam was swept while verifying this: `family` is safe because `harnessFamily` normalises
internally (`measurability.ts:180-182` → `surveyFamily` at `:164-168`); `noDataReason` is safe
because `getLatestSessionTimeByHarness` keys the map by the raw `session.harness`
(`bridge.ts:1300-1330`) and `inWindowNoDataReason` looks up the raw `row.harness`
(`routes.ts:200-213`), so both sides of that join are equally raw; and the detail route at `:765`
already normalises through `filterCheckpointsByHarness` (`:250-257`). `checkpointSummary` on the
list route is the only field of the four that resolves a legacy name on one side only.

### D4 — F2's retained envelope belongs in the accumulated-page state

**In the accumulated pages, not in a new `useState`.** Three reasons, in order of weight:

1. **The invariant makes a second home unnecessary and unsafe.** `browserRows` returns
   `firstPageUnstored ? [...current, ...accumulated] : accumulated` (`:312-313`), and `current` is
   `undefined` whenever `pageData` is `undefined` (`:638`). So *rows on screen imply a stored page
   exists* — precisely in the case F2 is about. A `lastKnownTotal` in component state would have to
   be set and cleared by hand in two places (`resetBrowse` and the accumulate effect) to track a
   fact the pages already carry, and the two could disagree — the exact bug F4 is about.
2. **One reset clears it.** `resetBrowse` already calls `setAppended([])` (`:520-524`); a
   scope change therefore drops the retained envelope for free, and a stale total cannot outlive
   the filter it belonged to.
3. **It is a per-scope fact, not a per-component one.** Two scopes never coexist, but the pages are
   already tagged per scope; a single un-tagged `lastKnownTotal` would be right by accident and
   wrong by construction if the mount ever held two.

`browserTotal`, `detectorCounts` and `unknownWindowSessions` (`:643-645`) become
`pageData?.x ?? retained?.x`. `unknownWindowSessions` is the one that cannot fall back to `0`: the
prop becomes `number | undefined` and the banner renders the word *unknown* while the envelope is
in an error state, per the reviewer's constraint. `detectorCounts` is retained too — it is the
third field of the same envelope, and dropping only two of three leaves a hole of the same kind
(chips vanishing reads as "no findings from any detector").

### D5 — A total change invalidates the scope and restarts the offset

F4's fix needs a rule for what a `total` change means. Keying the page on `total` is necessary; the
question is what happens to the pages already stored under the old one.

**Recommended:** when a page arrives whose `total` differs from the scope's stored `total`, the
scope's stored pages are dropped and `offset` resets to `0`, so the browser restarts at the current
top of the ranking. The alternative — drop the pages and keep the offset — renders the current page
alone and then pages forward from a base that no longer describes the top of the list.

Growth is the common case (one new finding from a live refresh) and a focus refetch can deliver it
while the user reads, which is why the plain "drop the pages" variant is rejected: it would silently
collapse 115 rows to 25 on a tab switch. Resetting to offset 0 refetches page one and the list is
whole again. Shrink or re-rank is the case F4 is about, and a restart handles it. The residual cost
is one extra request per rebuild; the residual risk is recorded below.

## Investigation findings

Verified 2026-10-01 against `a5f9003` by direct reads plus two throwaway probe tests
(`dash/web/src/pages/ContextDoctor.tsx` mounted under happy-dom, deleted after evidence
collection; the tree is unchanged).

- **F1 write side is verbatim.** `CanonStore.upsertHarnessRollup` writes `rollup.harness`
  unchanged (`src/canon/store.ts:2092-2113`) and `listHarnessRollups` reads it back unchanged
  (`src/server/bridge.ts:2747-2764`). A legacy row is therefore constructible in a test by writing
  `cursor-agent` directly, which is what T1 does; no raw SQL is needed.
- **F1 the map and the lookup disagree.** `groupCheckpointsByHarness` keys by
  `normalizeHarnessName(status.harnessId)` (`routes.ts:259-271`); line 723 looks up `row.harness`.
  `normalizeHarnessName` maps `claude-desktop → claude-code` and `cursor-agent → cursor`
  (`src/canon/measurability.ts`), so an unwritten legacy row never finds its own checkpoints.
- **F1 existing coverage cannot see it.** `kyber-api.test.ts:929` writes a canonical
  `claude-code` rollup row against a raw `claude-desktop` checkpoint id — the shape only the write
  side has to survive — and `:1018-1019` assert that no `cursor-agent` row exists, so the
  unrebuilt-store row is never constructed. The detail-route assertion at `:1041-1042` passes for
  the same reason: it fetches `claude-code`, which the normalising filter resolves.
- **F2 reproduced.** Mount with 3 rows / `total` 115 / `unknownWindowSessions` 3, click Load more,
  reject the offset request. Rendered state: heading "All Workspace Findings (0)", 3 rows still
  present, `findings-error` absent, `findings-retry` absent, `unknown-window-banner` absent,
  `findings-load-more` absent. `@tanstack/query-core@5.101.0` drops `placeholderData` once a query
  errors, so `pageData` is `undefined` and the `?? 0` at `:643-645` is what the user sees. The
  `error !== null && findings.length === 0` arm is unreachable for any populated list.
- **F3 reproduced.** Stored pages at offsets 0 (3 rows) and 3 (3 rows whose first row repeats
  page one's last row) render 5 rows after the id dedupe; the next Load more requests offset 5 while
  the server extent is 6. The `offset`-keyed `nextAccumulated` call sites and the
  `accumulated.length` offset therefore re-fetch one already-served row per shifted page.
- **F4.** `nextAccumulated` (`ContextDoctor.tsx:262-280`) replaces a stored page only when the same
  offset is re-served with different ids. Nothing invalidates on `total`, and the browser query
  (`:561-568`) is issued only for `offset`, so a stored page is never re-requested.
- **F5.** `TWIN_TURN_MAX_SKEW_MS` (`twin-dedupe.ts:53`) has no test. `dedupeTwinTurns` closes a
  cluster on the span check only (`:141-149`); the docblock's "each step between neighbours" claim
  describes `exceedsStep`, deleted in `a689157`, and contradicted by the correct note at `:142-144`.
- **Baseline at `a5f9003`.** The three files this plan touches hold 106 tests (43 in
  `kyber-api.test.ts`, 44 in `ContextDoctor.test.tsx`, 19 in `twin-dedupe.test.ts`), all green
  (`npx vitest run src/server/kyber-api.test.ts web/src/pages/ContextDoctor.test.tsx
  src/canon/twin-dedupe.test.ts` → "Test Files 3 passed (3) / Tests 106 passed (106)"). One
  unrelated test is red on the branch tip and is **not** in scope: `src/canon/migration.test.ts`
  "schema migration v14 -> v15" fails with `error in table refresh_run after drop column:
  incomplete input`. A full `npm --prefix dash run test` therefore reports `Tests 1 failed | 4176
  passed` (4,177 collected) at HEAD; each task's acceptance below names that failure as the known
  baseline rather than treating it as a regression.

## Design of the fixes

1. **F1 — canonical join key.** `routes.ts:723` looks the rollup row's own harness up under its
   canonical name, the same normalisation the write side and the detail route already apply. The
   `?? []` measured zero stays, and the comment above it names why a miss is a measured zero.
2. **F2/F3/F4 — one page state (D1).** `AccumulatedPage` carries the envelope; `nextAccumulated`
   takes the whole `FindingsPage` and returns the same reference when nothing changed;
   `retainedEnvelope` and `servedOffset` are the two new derivations; the accumulate effect
   restarts the scope on a `total` change (D5); the render reads `pageData ?? retained`; the error
   arm becomes an inline banner above the retained rows carrying the same `findings-retry` button
   as the empty-state panel; the suppression banner renders *unknown* rather than `0` while the
   envelope is unavailable.
3. **F5 — the docblock states the bound that exists.** The paragraph becomes the span rule, the
   named constant keeps its traceable-assumption framing, and a pointer to the inline note records
   why no step bound exists.

## Test contract (development-mode: test-first)

Focused runs use `npx vitest run <file>` from `dash/`. The `npm --prefix dash run test` script
hardcodes its paths, so `npm run test -- <filter>` silently runs the whole suite and cannot be used
to show a RED.

| Task | Test file | Runner command (from `dash/`) | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `src/server/kyber-api.test.ts` (add to `describe('GET /api/kyber/harnesses carries coverage facts (T8)')`) | `npx vitest run src/server/kyber-api.test.ts` | **Legacy join.** With a `cursor-agent` rollup row and two checkpoints recorded under `cursor-agent` (one `ok`, one `partial`), the list row's `checkpointSummary` is `{ ok: 1, partial: 1, failed: 0, unavailable: 0 }`, and the detail route `/api/kyber/harness/cursor-agent` returns the same object (R11.14 agreement). **Measured zero (D3).** A readable table with a `no-units` rollup row and no checkpoints gives `{ ok: 0, partial: 0, failed: 0, unavailable: 0 }` and is **not** `null`. | The legacy assertion fails: `toEqual({ ok: 1, partial: 1, … })` receives `{ ok: 0, partial: 0, failed: 0, unavailable: 0 }`. The measured-zero and detail-route assertions pass and must keep passing. | Both new assertions pass; the three existing `checkpointSummary` assertions at `:1029`, `:1030`, `:1042` unchanged; `:1109-1110` (`toBeNull()` on an unreadable table) unchanged. |
| T3 | `web/src/pages/ContextDoctor.test.tsx` (rewrite the `:639-661` suites, the `:521-529` case, and the `:629` case; add two rendered cases) | `npx vitest run web/src/pages/ContextDoctor.test.tsx` | **F4 (pure).** `nextAccumulated` called with a page whose `total` differs from the scope's stored total stores only the new page: `browserRows(...)` then returns the new page's rows and none of the stale page's ids. **F3 (pure).** `servedOffset` over pages at offsets 0 (3 rows) and 3 (3 rows, first an overlap) is `6`, so `nextBrowserOffset(servedOffset(pages, '\|'), 115)` is `6`, not `5`. **F2 (rendered).** Mount 3 rows / total 115 / `unknownWindowSessions` 3, click Load more, reject the offset request: the heading still reads "All Workspace Findings (115)", the 3 rows are still on screen, `findings-retry` is present, `unknown-window-banner` is present with the count (not the word unknown), and `findings-load-more` is present. **F2 (moved test).** The case at `:629` now asserts rows **and** `findings-retry` **and** `findings-inline-error` — `expect(html).not.toContain('findings-error')` is deleted, not satisfied alongside. **F3 (rendered).** The same overlap, driven through Load more, requests `offset=6` on the third findings request. | Import failure: `retainedEnvelope` and `servedOffset` do not exist. The F4 pure assertion fails (the stale page survives). The F2 rendered case fails on all four counts at once. The moved test fails on `findings-retry`. The F3 rendered case requests `offset=5`. | All cases in the file pass; the two existing rendered filter-change cases (`:769`, `:821`) unchanged and green, including `queryByTestId('findings-load-more')` being `null` at the end of each. |
| T5 | `src/canon/twin-dedupe.test.ts` (new `describe`) | `npx vitest run src/canon/twin-dedupe.test.ts` | The source of `twin-dedupe.ts`, read as text, does not claim a per-step bound for `TWIN_TURN_MAX_SKEW_MS` (`not.toMatch(/step between\s+neighbours/)`) and does state the span bound (`toMatch(/span/)`). The behavioural cases are untouched: two rows 50 s apart still collapse, three rows 90 s apart still split. | The `not.toMatch` guard fails: the docblock still reads "as well as each step between neighbours". | The guard passes; the existing 19 cases in that file unchanged, so the file reports 20 passed. |

T2, T4 and T6 are the GREEN halves of T1, T3 and T5. They add no contract of their own.

## Tasks

- **T1 — DONE (RED; skill: test-dev).** Write the T1 contract cases in `src/server/kyber-api.test.ts`.
  Reuse the local `checkpoint(harnessId, sourceKey, lastStatus)` helper already in that `describe`
  (`:977-994`) and the store-construction pattern at `:1001-1046`; do not add a raw-SQL fixture.
  Files: `src/server/kyber-api.test.ts` only. Depends on: nothing.
- **T2 — DONE (GREEN).** TypeScript specialist (no listed skill covers Node/TypeScript server code; the
  conductor maps it, as the archived #186 review-fixes plan did). `dash/src/server/routes.ts`, the
  one expression:

  old (`:720-723`):
  ```ts
      checkpointSummary:
        checkpointsByHarness === null
          ? null
          : checkpointSummaryOf(checkpointsByHarness.get(row.harness) ?? []),
  ```
  new:
  ```ts
      checkpointSummary:
        checkpointsByHarness === null
          ? null
          // The join is canonical on both sides. The map is keyed by
          // `normalizeHarnessName` and a rollup row written before the fold
          // (issue #182) still carries its raw front-end id until an operator
          // rebuilds derived tables, so the lookup key is normalised too. A
          // miss after that is a measured zero, not an unknown: the read
          // succeeded and this harness recorded no units (D3).
          : checkpointSummaryOf(checkpointsByHarness.get(normalizeHarnessName(row.harness)) ?? []),
  ```
  `normalizeHarnessName` is already imported in this file (used at `:255` and `:265`); no import
  change. Acceptance: T1 GREEN, and `npx vitest run src/server/kyber-api.test.ts
  src/server/kyber-bridge.test.ts` green. Depends on: T1.
- **T3 — DONE (RED; skill: test-dev).** Write the T3 contract cases in
  `web/src/pages/ContextDoctor.test.tsx`. Mechanical prerequisites, all in the same commit:
  - Rewrite the three `nextAccumulated` call sites at `:648`, `:656` and `:657-658` to the new
    `nextAccumulated(pages, scope, page)` signature, passing a full `FindingsPage` (`offset`,
    `findings`, `total`, `detectorCounts`, `unknownWindowSessions`).
  - Rename the case at `:521` to say the offset comes from the served extent, and keep its
    arithmetic assertions (`nextBrowserOffset(25, 115) === 25`, `(115, 115) === undefined`,
    `(0, 0) === undefined`) — they are `nextBrowserOffset`'s own contract and stay true.
  - **Move** the case at `:629`: delete `expect(html).not.toContain('findings-error')` and assert
    `toContain('finding-card-e-1')`, `toContain('findings-inline-error')` and
    `toContain('findings-retry')`. The review is explicit that the old assertion pins the defect.
  - Add the two rendered cases using the probe recipe: the fetch stub serves the first page for a
    request without `offset`, waits on a gate for `offset=3`, and then throws; drive them with
    `render`/`within(screen.getByTestId('all-workspace-findings'))` as the existing rendered suite
    at `:671` does, including its `afterEach(cleanup)` and `vi.unstubAllGlobals`.
  Depends on: nothing.
- **T4 — DONE (GREEN).** TypeScript specialist. `dash/web/src/pages/ContextDoctor.tsx`, six edits, in
  this order so the file stays type-consistent:
  1. `AccumulatedPage` (`:250-251`) and `nextAccumulated` (`:262-280`) → the D1 shape. `nextAccumulated`
     takes `page: Pick<FindingsPage, 'offset' | 'findings' | 'total' | 'detectorCounts' | 'unknownWindowSessions'>`,
     stores `rows: [...page.findings]`, and on the unchanged-ids path returns `pages` itself.
  2. New `retainedEnvelope` and `servedOffset`, inserted after `browserRows` (`:314`), each with a
     `<remarks>` block saying what question it answers and why the row count cannot.
  3. The accumulate effect (`:590-593`) → the D5 restart, with `appended` added to the dependency
     array (safe because of the same-reference no-op in 1):

     ```ts
     useEffect(() => {
       if (!pageData || isPlaceholderData) return
       const stored = retainedEnvelope(appended, browseScope)
       if (stored !== undefined && stored.total !== pageData.total) {
         // The server's answer no longer describes these rows (D5): drop the
         // scope and restart at the top rather than paging from a base that
         // describes a ranking that has moved.
         setAppended(nextAccumulated([], browseScope, pageData))
         setOffset(0)
         return
       }
       setAppended((prev) => nextAccumulated(prev, browseScope, pageData))
     }, [pageData, isPlaceholderData, browseScope, appended])
     ```
  4. The retained reads (`:643-645`) → `pageData?.x ?? retained?.x` per D4, with
     `unknownWindowSessions` left `undefined` when nothing is retained.
  5. `onLoadMore` (`:701-706`) → `nextBrowserOffset(servedOffset(appended, browseScope), browserTotal)`,
     and the `nextBrowserOffset` docblock (`:447-452`) → the corrected claim in D1/F3.
  6. `FindingsBrowserView`: `unknownWindowSessions` default `0` → no default (`:130`), the render
     branch (`:214-245`) → hoist one `retry` button and add the inline `findings-inline-error`
     banner above `FindingList`, and the suppression banner (`:175-181`) → render when the count
     is unknown **and** `error !== null`, in words, never as `0`.
  Acceptance: T3 GREEN; `npm --prefix dash run typecheck` exits 0; `npm --prefix dash run lint`
  exits 0. Depends on: T3.
- **T5 — DONE (RED; skill: test-dev).** Add the source guard to `src/canon/twin-dedupe.test.ts`, reading
  the module with `readFileSync(new URL('./twin-dedupe.ts', import.meta.url), 'utf8')` — the
  pattern `src/pricing/data/pricing-fallback-data.test.ts` already uses. Depends on: nothing.
- **T6 — DONE (GREEN).** TypeScript specialist, comment only. `dash/src/canon/twin-dedupe.ts:47-52`:

  old:
  ```
   * The gap bounds a cluster's total span as well as each step between
   * neighbours. Bounding only the step would be single-linkage chaining: rows
   * 50 s apart form an unbounded run that a session re-reporting identical
   * counters every 50 s would collapse into one turn.
  ```
  new:
  ```
   * The gap bounds a cluster's total span, measured from its first row. Rows
   * arrive time-ordered, so that span always covers the neighbour gap and a
   * separate step bound could never fire on its own; bounding only the step
   * would in any case be single-linkage chaining, since rows 50 s apart form
   * an unbounded run that a session re-reporting identical counters every
   * 50 s would collapse into one turn. See the check in `dedupeTwinTurns`.
  ```
  Acceptance: T5 GREEN and the whole `twin-dedupe.test.ts` green. Depends on: T5.
- **T7 (docs; skill: app-docs-standard; no-test) — DONE except the two PR-close steps below,
  which are the orchestrator's.**
  - **DONE** — `docs/dash/architecture.md`, "Display families and source display names": the
    per-harness checkpoint join is canonical on both sides, so a rollup row written before the
    fold still reports its own coverage counts until a rebuild rewrites it. The edit also states
    the reason a miss after normalisation is a measured zero (R2) rather than an unknown, which
    is reserved for an unreadable `source_checkpoint` table, and links the runbook's
    derived-projection section for the "upgrade does not rebuild" premise.
  - **DONE** — `docs/dash/architecture.md`, "Web Dashboard": the browser's paging contract — the
    next offset is the extent the server served (not the rows painted), a `total` that moves
    invalidates the stored window and restarts the offset, and a failed page keeps the last
    successful envelope so the heading, chips and suppression banner are never rewritten to zero,
    with the suppression count stated in words as unknown while the envelope is unavailable
    (honest unobservability in the UI).
  - **DONE (no change)** — `docs/dash/architecture.md` for F5. No governed document describes the
    twin-dedupe clustering bound; the decision lives in the code, and this plan records that in
    its inventory row.
  - **PENDING, PR close (orchestrator)** — `docs/plans/README.md`: the Active Plans row moves to
    Archived Plans, with "None — the twin-dedupe bound is code-local; the D1–D5 decisions are
    recorded in the plan".
  - **PENDING, PR close (orchestrator)** — archive this plan to `docs/archive/plans/` and move the
    row, which is what clears `KW-DOC-LIFECYCLE-003` for the `--merge-ready` run.
  - Depends on: T2, T4, T6.

## Dependency graph and MAX_CONCURRENCY

```
T1 → T2 ─┐
T3 → T4 ─┼→ T7
T5 → T6 ─┘
```

| Task | Depends on | File scope |
|---|---|---|
| T1 | — | `dash/src/server/kyber-api.test.ts` |
| T2 | T1 | `dash/src/server/routes.ts` |
| T3 | — | `dash/web/src/pages/ContextDoctor.test.tsx` |
| T4 | T3 | `dash/web/src/pages/ContextDoctor.tsx` |
| T5 | — | `dash/src/canon/twin-dedupe.test.ts` |
| T6 | T5 | `dash/src/canon/twin-dedupe.ts` |
| T7 | T2, T4, T6 | `docs/dash/architecture.md`, this plan, `<plan-index>` |

Every task's file scope is disjoint from every other task's. **MAX_CONCURRENCY: 3** — the three
RED tasks, then the three GREEN tasks as their tests land, then T7. T5/T6 never touch the
ContextDoctor state, so the F5 comment fix is independent of every behavioural decision here.

## Risks

- **A rebuild collapses the list (D5).** A `total` change restarts the browser at offset 0, so a
  focus refetch that lands after a rebuild costs one request and shows the list again from the
  top. Accepted: the alternative renders stale rows for the life of the mount, which is the defect
  F4 names. If the collapse proves disruptive in review, the one-line reversal is to drop the
  `setOffset(0)` and keep the offset.
- **Suppression counts are now tri-state.** `unknownWindowSessions: number | undefined` is a prop
  signature change on an exported component. `FindingsBrowserViewProps` has no other consumer
  (`rg FindingsBrowserView dash/web/src` → this file and its test), and every existing call site
  either passes a number or omits the prop with `error = null`, where the unknown text does not
  render. T4's acceptance runs the whole file to prove it.
- **The same-reference no-op is load-bearing.** Returning `pages` instead of `[...pages]` is what
  makes the accumulate effect safe with `appended` in its dependency array. Reverting it to a copy
  causes an infinite render loop, not a test failure. It is called out in the docblock and in T4
  step 1 so the next editor does not "tidy" it.
- **`detectorCounts` retention is scope the review did not ask for** (D4). Cost: one field in one
  type. Benefit: the third field of the same envelope cannot vanish while the other two are
  retained. Reversible by dropping the field from `AccumulatedPage` and from the `??` chain.
- **F1 leaves the documented operator remedy as the real fix for the underlying state.** The
  runbook already tells an operator to run `kyber build`; the code fix only stops the dashboard
  fabricating a zero in the window before they do. It does not migrate legacy rows.
- **Baseline red.** `src/canon/migration.test.ts` (v14 → v15) fails on the branch tip and is out
  of scope here. A full `npm --prefix dash run test` is therefore not green at any point in this
  plan, and its tail must be read against that known failure rather than as a regression from T1–T6.
- **Plan lifecycle.** Any change to A3–A7 returns this plan to Draft and reopens the affected test
  contract rows.

## Out of scope

- The 20 review threads already resolved by fix commits on this branch, and the fifth partially
  fixed thread.
- F1's "widen `?? []` to `null`" (A4/D3), and any change to the `checkpointSummary` response shape
  or to `honest-unobservability` R1's "never `null`" wording, which the branch's convention
  contradicts and which no finding here asks for.
- Migrating or rewriting legacy `harness_rollup` rows, or changing `normalizeHarnessName`.
- Re-fetching every stored offset when `total` moves (the design D5 rejects), and `keepPreviousData`
  refinements beyond the F2 error path.
- `src/canon/migration.test.ts`'s v14 → v15 failure.

## Verification gates

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run check:reachable
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
```

`npm --prefix dash run test` is expected to report the known `migration.test.ts` failure until that
is fixed separately. At PR close, archive this plan to `docs/archive/plans/` and move its inventory
row before `docs validate . --merge-ready`, which rejects any plan still in `docs/plans/`
(`KW-DOC-LIFECYCLE-003`).

## Review and closeout

- Code review per the repository council flow over the follow-up diff, with F1–F5 named so each
  thread's resolution is checkable against a commit.
- Delivery: push to the existing branch `fix/dash-181-182-191-findings-quality`, so the fixes land
  in PR #233. T1–T4 and T5–T6 are two commits; T7 is a third.
- Closeout (T7, `docs-dev`): harvest into `docs/dash/architecture.md` as listed above.
- ADR: none. D3 applies an existing rule rather than choosing a new one, and D1/D4/D5 are
  implementation decisions inside the issue #191 paging contract, not constraints on future work.
