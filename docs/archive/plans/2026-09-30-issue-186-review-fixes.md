---
id: plans/2026-09-30-issue-186-review-fixes
title: "KyberDash: PR #225 review follow-up for issue #186 cost pricing"
doc-type: plan
status: archived
component: KyberDash
owner: dpalfery
last-reviewed: 2026-09-30
development-mode: test-first
---

# KyberDash: PR #225 review follow-up for issue #186 cost pricing

**Status: Archived 2026-09-30.** Approved for execution 2026-09-30 (A1 below). Development mode: `test-first`.

This plan follows up [PR #225](https://github.com/dpalfery/kyber-weave/pull/225) on branch
`fix/issue-186-cost-pricing`, which implements the archived
[issue #186 plan](./2026-09-30-issue-186.md). That plan's decisions U1–U12 (Q1–Q4
all answered (a)) still apply. This plan adds no new product decision. It decides FIX, DECLINE
or DEFER for each of the 11 inline review comments that kilo-code-bot posted in review
`5371959051` (commit `4f8fe3c1`), and it plans the FIX work.

## Problem and goal

The bot review lists 7 warnings and 4 suggestions against PR #225. The comment text is
third-party data, so every claim was checked against the checked-out code before a decision was
made. Several claims hold. Some are wrong about the mechanism: the bundler does not write the
file the comment names, and the consumer the comment cites reads a different payload. Some ask
to reverse a decision the original plan approved.

Goal: fix the verified defects under test-first delivery, and keep every approved decision
(U8–U11). Record a checked reason for each DECLINE and DEFER. Give the conductor one reply per
comment and the text of each follow-up issue.

## Approved decisions

| Id | Decision | Provenance |
|---|---|---|
| A1 | **Approve and execute.** This plan is approved for execution as Ready. | The user told the conductor on 2026-09-30 to stop asking for permission and proceed. The conductor relayed this, and it is recorded here as the approve-and-execute gate. |
| A2 | **The original plan's decisions stand.** U8 (additive scope), U9 (projection-time repricing with `cost_json` write-back, no schema bump), U10 (partial sessions show the tile's formatter text and no figure) and U11 (targeted, source-cited additions) are not reopened. Where a comment asks to reverse one of them, the answer is DECLINE. | Archived plan, U8–U12 |
| A3 | **Development mode `test-first`.** Every FIX has a RED test task (`test-dev`) and then a GREEN task. | Default; the conductor packet supplied no opt-out |

NO_QUESTIONS: the conductor told the planner to decide each comment using the recommended option.
Every decision below either applies the original plan's approved contract or fixes a defect
verified in code without changing any user-visible contract. No choice remains that only the
user can make.

## Decision ledger (keyed by review comment_id)

| comment_id | File (bot's anchor) | Decision | Verified reason (one line) |
|---|---|---|---|
| 4149313577 | `canon/sessions.ts` repriceTurns | **FIX** (batch + design note); in-memory option DECLINED | Verified: `store.setCost` runs one autocommit `UPDATE` per record and `buildSessions` opens no transaction. Write-back itself is approved (U9) because the report path reads `cost_json`. So keep write-back, batch each session's changes in one transaction, and document the exception in the module doc. |
| 4149313583 | `canon/sessions.ts` isCopilotFamily | **FIX** | Verified: `isCopilotFamily` (sessions.ts) and `copilotFamily` (copilot-rates.ts) apply the same `normalizeHarnessName` rule in two files. Export one `isCopilotHarness` from `copilot-rates.ts` and use it in both places. |
| 4149313591 | `canon/sessions.ts` `model ?? ''` | **FIX** (explicit absent-model guard); the `unknown` basis is DECLINED | Verified: `''` reaches `getModelCosts('')` and returns `no_rate` only by falling through. Let `pricePublishedTurn` accept `undefined` and return `{published, no_rate}` explicitly. The basis stays `published`: an `unknown` block in a session with `published` turns makes `sumCosts` refuse the total (`COST_BASIS_MISMATCH`), which is the defect the original design step 6 fixed. |
| 4149313604 | `canon/cost.ts` selectTier miss | **DECLINE** | The documented R5.4/R5.6 contract (`priceWithTable` and `selectTier` doc comments) says an input beyond every tier is a missing rate. `cost.test.ts` ("returns undefined when the input exceeds every tier") and `cost.scoping.test.ts` ("measured input beyond every tier is no_rate") pin it. Falling back to the base rate would price a large turn at a rate the vendor does not publish for that size. The bot's concern is an unguarded table entry, so T3 adds a table-shape test that every tiered Copilot entry ends in an `Infinity` tier. |
| 4149313621 | `pricing/models.ts` luna high tier | **FIX** | Verified: `buildCosts(2e-7, 7.5e-7, null, …)` makes the cache-write rate `input × 1.25` ($0.25/1M), and the snapshot's base `gpt-6-luna` row has `null` too ($0.125/1M). OpenAI publishes no cache-write rate for this model. `pricePublishedTurn` ignores `cacheWriteCostIsExplicit`, which `providers/codex.ts` already honours. Fix: when the rate is not explicit, bill cache creation as input (the `codex.ts` precedent, and the Copilot table's rule), and correct the misleading comment. |
| 4149313625 | `pricing/models.ts` luna threshold | **FIX** | Verified: `calculateCost` picks the tier from `input + cacheRead`, which excludes cache creation, while `priceWithTable` uses `measuredInput`, which includes it. The same tokens can therefore land in different tiers. The 4149313621 fold puts cache creation into input whenever the write rate is not explicit, so both paths read the same sum for `gpt-6-luna`. Also export `GPT_6_LUNA_PROMPT_TOKEN_THRESHOLD` and use it in the Copilot table so the 272K boundary is defined once. |
| 4149313627 | `synth/synth.ts` Copilot relabel | **FIX** (reader half); the default inversion is DECLINED | Reader half verified: `readers/copilot.ts` sets `costHarnessReported: true` even when `cost_usd` is absent. Set it only for a finite `cost_usd`. Inversion declined: requiring `=== false` would carry a Copilot parser that forgets the flag as `harness` at LiteLLM API rates, and that is never repriced. That is the R5.3 failure the rationale records. The current default only reprices such a figure, openly labelled `published`, from the credits table (design step 5: "by provenance"). |
| 4149313635 | `server/bridge.ts` cost_usd for partial | **DECLINE** | Approved contract: design step 2 and the T1 row ("A `partial` session lists `cost_usd: null`") are pinned by `kyber-bridge.test.ts` ("lists a partial block with no figure"). U10 bars showing an incomplete total as a figure. `SessionSummary.cost_usd` is already documented as "non-null only for a priced USD block" (`bridge.ts`). The consumer the bot cites (`AgentSessionDashboard.adaptTimelineNode`) reads a **timeline node's** `cost_usd`, not the session list row. |
| 4149313643 | `canon/cost.ts` cache-write fallback | **FIX** | Verified: after per-class pricing, the fallback `cacheWriteRate ?? inputRate` is a pricing policy, not an arithmetic identity. Rewrite the comment to state it. Give the Copilot `gpt-6-luna` tiers explicit `cacheWriteRate` values equal to the input rate, with GitHub's "Not applicable" note, so the table shows the applied price. Note in `measuredInput` that tier selection reads the sum while class pricing reads the classes. |
| 4149313649 | `web/…/ContextExplorer.tsx` fallback arms | **DECLINE** (cell width DEFERRED, follow-up F1) | Approved contract: design step 2 and `kyber-bridge.test.ts` ("defaults a row with no summary cost to unknown/no_rate") pin the bridge default, and the default equals what the tile's `normalizeCostBlock` renders for an absent block. That keeps the list and the tile on one answer (the plan's goal). The `costUsd` and `—` arms serve legacy payload shapes (`s.costUsd`, fixture `ContextExplorer.test.tsx:204`). The narrow `w-20` cell for word statuses is real, but needs a visual check in the running dashboard, so it is deferred. |
| 4149313657 | `pricing/data/litellm-snapshot.json` hand rows | **FIX** (bundler path defect DEFERRED, follow-up F2) | Partly verified. The rows are not reproducible from `scripts/bundle-litellm.mjs`, but the bot's mechanism is wrong: the bundler writes `dash/src/data/` (its `dataDir` is `join(__dirname, '..', 'src', 'data')`), not `dash/src/pricing/data/`, which `models.ts` imports. So `npm run build` today leaves them in place. Fix: add both rows to `MANUAL_ENTRIES`, so a regeneration from the corrected path reproduces them. Correcting the bundler's output path would turn every build into a full re-snapshot, which is the U11 follow-up, so it is deferred. |

Tally: FIX 8 (4149313577, 583, 591, 621, 625, 627, 643, 657) and DECLINE 3 (604, 635, 649).
No comment is a primary DEFER. Two follow-ups are deferred and recorded as issue texts: F1 (from
649) and F2 (from 657).

## Investigation findings

Verified 2026-09-30 through CodeGraph (`codegraph_explore`, index present) and direct reads of
the pushed branch. Documentation came from `docs_explore` (root confirmed as this checkout, rev
`2a8c98d`, clean).

- **Write-back (577).** `repriceTurns` (`dash/src/canon/sessions.ts`) calls `store.setCost` for
  each changed record. `CanonStore.setCost` (`dash/src/canon/store.ts`) is a bare prepared
  `UPDATE`, outside any `BEGIN`/`COMMIT`. `upsertMany` and `commitSourceUnit` show the house
  pattern for a batch in one transaction. The original plan's risk note says to write back "in
  the projection's transaction", but `buildSessions` has no such transaction. The module docs of
  `buildSessions` and `projectCanonicalStore` still say every derived table is a cache over
  `records`, with no mention of the cost write-back.
- **Copilot predicate (583).** `isCopilotFamily(harness): boolean` (sessions.ts) and
  `copilotFamily(harness): string | undefined` (copilot-rates.ts, not exported) test the same
  `id === 'copilot' || id.startsWith('copilot-')` after `normalizeHarnessName`.
- **Absent model (591).** `pricePublishedTurn(tokens, model: string, …)` receives `model ?? ''`.
  `isFlatRateModel('')` returns false through its `!model` guard, and `getModelCosts('')` returns
  `null`, so the result is `{published, no_rate}`. That outcome is correct. It comes from falling
  through the lookup, not from a stated rule.
- **Tier miss (604).** `priceWithTable` returns `no_rate` when `selectTier` misses. That is the
  documented contract, and two existing tests pin it. The one per-model tiered entry,
  `COPILOT_RATES` `gpt-6-luna`, ends in `upTo: Infinity`. Nothing enforces that for future entries.
- **Cache-write fabrication (621).** `buildCosts` defaults the cache-write rate to `input × 1.25`
  and sets `cacheWriteCostIsExplicit: false`. The snapshot row
  `"gpt-6-luna":[1e-7,5e-7,null,1e-8,null]` and `GPT_6_LUNA_HIGH_PROMPT_COSTS` both take that
  default. `providers/codex.ts` moves tokens into the cache-write bucket only when
  `getModelCosts(model)?.cacheWriteCostIsExplicit`. `pricePublishedTurn` passes
  `tokens.cacheCreation` straight to `calculateCost`.
- **Tier measure (625).** `calculateCost` computes
  `promptTokens = safe(inputTokens) + safe(cacheReadTokens)` and applies `tieredCostsFor` with `>`
  272,000. `priceWithTable` uses `measuredInput = freshInput + cacheRead + cacheCreation` with an
  inclusive `upTo: 272_000`. The boundary semantics agree (exactly 272K stays on the base tier),
  but the measured quantity differs.
- **Provenance flag (627).** `costBlockFor` relabels Copilot figures as `published` unless
  `costHarnessReported === true`. `loadCopilotCliCalls` sets `costHarnessReported: true`
  unconditionally, and `costUSD: numberValue(row.cost_usd)` gives `0` for NULL.
- **Legacy field (635).** `listSessions` sets `cost_usd` only for `priced` + `USD`, as design
  step 2 specifies. `AgentSessionDashboard.adaptTimelineNode` reads `node.cost_usd` from a
  `SessionTimelineNode`, which is a different payload.
- **Fallback comment (643).** `priceWithTable` says "an absent cache rate falls back to the input
  rate, which reproduces the single-rate arithmetic". That was true before per-class pricing, but
  it is now a pricing policy.
- **List cell (649).** The bridge sends a `cost` for every row, and the default
  `{unknown, no_rate}` is pinned. `normalizeCostBlock(undefined)` in the tile returns the same
  block. The cell is `w-20 shrink-0` with no `whitespace-nowrap`, so "no published rate" wraps.
- **Bundler (657).** `dash/scripts/bundle-litellm.mjs` writes `dash/src/data/litellm-snapshot.json`,
  which is a stale path. The runtime and the tests import `dash/src/pricing/data/litellm-snapshot.json`.
  The bundler throws on a failed LiteLLM fetch rather than writing a smaller file. `MANUAL_ENTRIES`
  holds 4-tuples, and the snapshot rows are 5-tuples (`fast` is `null`).
- **Import guard.** `PURE_DIAGNOSTIC_MODULES` (`dash/src/tools/cost-isolation.ts`) does not list
  `copilot-rates.ts` or `published-pricing.ts`. `published-pricing.ts` already imports
  `../pricing/models.js`, so a `copilot-rates.ts → pricing/models.ts` import of one constant
  passes the guard.

## Design of the fixes

1. **Batched, documented write-back (577).**
   - Add `CanonStore.setCosts(changes: ReadonlyArray<{ spanId: string; cost: CostBlock }>)`. It
     writes every change in one `BEGIN`/`COMMIT` and rolls back all of them on error, the same
     pattern as `upsertMany`. An empty list is a no-op.
   - `repriceTurns` collects a session's changed blocks and calls `setCosts` once per session.
     Remove `setCost` if nothing else calls it.
   - Update the `buildSessions` and `projectCanonicalStore` doc comments. The derived tables
     remain caches over `records`. The one exception is turn `cost_json`, which is re-derived and
     written back. It is a pure function of tokens, model, harness and the current pricing
     config. `harness` blocks are never touched, and an unchanged block is never written.
2. **One Copilot predicate (583).** Export `isCopilotHarness(harness: string | undefined): boolean`
   from `copilot-rates.ts`. Rebuild `copilotFamily` on top of it, import it into `sessions.ts`,
   and delete `isCopilotFamily`.
3. **Explicit absent model (591).** Change the signature to
   `pricePublishedTurn(tokens, model: string | undefined, harness, existing?)`. The order of
   checks is:
   1. R5.2: a harness-reported block is returned untouched.
   2. R5.3: a harness outside the table's scope is `out_of_scope`.
   3. With no model, return `{published, no_rate}` without any lookup.
   4. Otherwise continue unchanged.

   `repriceTurns` passes `model` through, with no `?? ''`.
4. **Cache-write policy on the published path (621, 625).** Inside `pricePublishedTurn`, when
   `getModelCosts(model)?.cacheWriteCostIsExplicit !== true`, call `calculateCost` with
   `freshInput + cacheCreation` as input and `0` as cache creation. This is the
   `providers/codex.ts` rule, applied at the canonical pricer. Two consequences:
   - A model with no published cache-write rate is billed its input rate for cache writes,
     which is what the Copilot table does.
   - The tier check (`input + cacheRead`) now covers cache creation for such a model, so
     `gpt-6-luna` picks the same tier on both paths.

   Explicit-rate models, such as the Claude entries, are unchanged. Also:
   - Export `GPT_6_LUNA_PROMPT_TOKEN_THRESHOLD` from `pricing/models.ts` and use it as the
     Copilot table's first `upTo`.
   - Rewrite the `gpt-6-luna` comment in `models.ts`: `buildCosts`' 1.25× default is not an
     OpenAI price, and the canonical pricer bills cache creation as input when the rate is not
     explicit.
5. **Explicit Copilot cache-write rates and comment (643, and the 604 guard).**
   - Set `cacheWriteRate: 0.1` on the base `gpt-6-luna` tier and `0.2` on the long tier, with
     the note that GitHub lists the class as "Not applicable", so it is billed at the tier's
     input rate. Both values equal today's fallback.
   - Rewrite the `priceWithTable` comment so it states the policy rather than an identity.
   - Note in the `measuredInput` doc that tier selection reads the summed input while each class
     is priced separately.
   - The shape test in T3 guards the table: every class explicit, every tiered entry ending in
     an `Infinity` tier.
6. **Reader provenance (627).** In `loadCopilotCliCalls`, set `costHarnessReported: true` only
   when `row.cost_usd` is a finite number. The relabel default in `costBlockFor` is unchanged.
   Its comment names the R5.3 reason for the default and says a new genuine Copilot reader must
   set the flag.
7. **Reproducible hand rows (657).** Add `'claude-sonnet-5-5': [2e-6, 1e-5, 2.5e-6, 2e-7]` and
   `'gpt-6-luna': [1e-7, 5e-7, null, 1e-8]` to `MANUAL_ENTRIES`. Cite the vendor URLs and the
   2026-09-30 retrieval (`pricing-provenance.json` stays the machine-checkable citation). Note
   that upstream LiteLLM now carries both keys, so the bundler's "candidate to remove" note is
   expected until a re-snapshot confirms the upstream values. Do not regenerate the snapshot:
   that needs the network and is follow-up F2.

## Test contract (development-mode: test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `dash/src/canon/store.test.ts` (exists) + `dash/src/canon/projection.test.ts` (exists) | `npm --prefix dash run test -- store projection` and `npm --prefix dash run typecheck` | `setCosts` writes every given block in one call. `setCosts([])` changes nothing. If a block fails mid-batch (a value `JSON.stringify` rejects, such as a `BigInt`), no row of the batch changes. After one `projectCanonicalStore` over a session with three unpriced Claude Code turns, `setCosts` is called once for that session with three entries, and `setCost` is never called. A second projection writes nothing. A turn with no model attribute is stored as `{published, no_rate}`. | Typecheck and runtime failure: `setCosts` does not exist, and the batch assertions fail | Same tests pass; existing `projection.test.ts` and `store.test.ts` cases unchanged |
| T3 | `dash/src/canon/published-pricing.test.ts` (exists) + `dash/src/canon/copilot-rates.test.ts` (exists) | `npm --prefix dash run test -- published-pricing copilot-rates` and `npm --prefix dash run typecheck` | **591:** `pricePublishedTurn(tokens, undefined, 'claude-code')` returns `{published, no_rate}`; with `'copilot'` it returns `out_of_scope`; a harness block passes through. **621:** `codex` + `gpt-6-luna` with 1M cache creation costs $0.10, not $0.125. `claude-code` + `claude-sonnet-5-5` with 1M cache creation still costs $2.50 (explicit rate unchanged). **625:** `gpt-6-luna` at 265,000 fresh + 10,000 cache creation (275,000 measured) prices on the >272K tier on both paths, and `pricePublishedTurn(…'codex')` equals `priceCopilotTurn(…'copilot')` for the same tokens at 272,000 and 272,001 measured input. `COPILOT_CREDITS_TABLE`'s `gpt-6-luna` first tier `upTo` equals the exported `GPT_6_LUNA_PROMPT_TOKEN_THRESHOLD`. **583:** `isCopilotHarness` is exported: true for `copilot`, `copilot-cli`, `copilot-vscode`; false for `claude-code`, `codex`, `undefined`. **643/604 guard:** every priced `COPILOT_CREDITS_TABLE` entry and tier sets `cacheReadRate` and `cacheWriteRate`, and every `tiers` list has a last tier with `upTo: Infinity`. | Typecheck fails (`undefined` model, missing exports). The 621, 625 and shape assertions fail at runtime. | All pass; every earlier case in both files unchanged |
| T5 | `dash/src/synth/readers/copilot.test.ts` (exists; suite at `:86`) | `npm --prefix dash run test -- readers/copilot` | A `sessions` row whose `cost_usd` is NULL or missing gives a call with `costHarnessReported` not `true`, and its synthesized cost is not `basis:'harness'`. A row with `cost_usd` 0.42 still gives `costHarnessReported: true` and a `harness` block of 0.42 (existing case). | The NULL-`cost_usd` case fails: the flag is `true` | Both cases pass |
| T7 | `dash/src/pricing/data/pricing-fallback-data.test.ts` (exists) | `npm --prefix dash run test -- pricing-fallback-data` | The bundler source (`dash/scripts/bundle-litellm.mjs`, read as text) declares `claude-sonnet-5-5` and `gpt-6-luna` in `MANUAL_ENTRIES`. Their first four tuple elements equal the committed snapshot rows. This is a reproducibility guard: a regeneration would keep what is committed. | Both key assertions fail: the keys are absent from `MANUAL_ENTRIES` | Both pass; the existing provenance assertions unchanged |
| T9 | No test task (docs only) | `docs validate` / `docs drift` | The canonical docs describe the batched write-back and its design note, the cache-write policy on the published path, the shared tier measure, the reader provenance rule, and the `MANUAL_ENTRIES` home. | n/a. Explicitly no-test: the two documentation checks are the verification | Both checks report zero findings |

T2, T4, T6 and T8 are the GREEN halves of T1, T3, T5 and T7. They add no contract of their own.
No DECLINE produces a code change. 4149313635 needs none, because `SessionSummary.cost_usd`
already documents its rule.

## Tasks

- **T1 (RED; skill: test-dev).** Write the T1 contract cases in `store.test.ts` and
  `projection.test.ts`. Use `vi.spyOn(CanonStore.prototype, 'setCosts')` and `'setCost'` for the
  once-per-session assertion, and restore them in `afterEach`. Capture the RED typecheck and test
  output. Files: those two test files only. Depends on: nothing.
- **T2 (GREEN).** No listed skill covers Node/TypeScript server code; the conductor maps this
  task to its TypeScript-capable specialist, as the original plan did. Implement design steps 1,
  2 (consumer side) and 3 (caller side):
  - `dash/src/canon/store.ts`: `setCosts`; remove `setCost` if it has no remaining caller.
  - `dash/src/canon/sessions.ts`: `repriceTurns` batching; import `isCopilotHarness` and delete
    `isCopilotFamily`; pass `model` without `?? ''`; update the `buildSessions` doc.
  - `dash/src/canon/projection.ts`: the doc comment only.

  Acceptance: T1 GREEN, and T3 stays GREEN. Depends on: T1, T4 (consumes the `isCopilotHarness`
  export and the `string | undefined` signature).
- **T3 (RED; skill: test-dev).** Write the T3 contract cases in `published-pricing.test.ts` and
  `copilot-rates.test.ts`. Reset `setPriceOverrides({})` and `setModelAliases({})` in `afterEach`.
  Use only the cited rates. Files: those two test files only. Depends on: nothing.
- **T4 (GREEN).** TypeScript specialist, mapped by the conductor. Implement design steps 2
  (export), 3, 4 and 5:
  - `dash/src/canon/published-pricing.ts`
  - `dash/src/canon/copilot-rates.ts`
  - `dash/src/pricing/models.ts`: export the threshold constant and fix the `gpt-6-luna` comment
    only.
  - `dash/src/canon/cost.ts`: comments only (`priceWithTable` and `measuredInput`).

  Acceptance:
  - T3 GREEN.
  - `models.test.ts`, `cost.test.ts`, `cost.scoping.test.ts` and `data-handling.test.ts` are
    unchanged and GREEN.
  - `check:reachable` exits 0, and `cost-isolation.test.ts` stays GREEN.

  Depends on: T3.
- **T5 (RED; skill: test-dev).** Add the NULL-`cost_usd` case to `readers/copilot.test.ts`.
  Depends on: nothing.
- **T6 (GREEN).** TypeScript specialist. Implement design step 6 in
  `dash/src/synth/readers/copilot.ts` and the `costBlockFor` comment in `dash/src/synth/synth.ts`
  (comment only). Acceptance: T5 GREEN, and `synth.test.ts` is unchanged and GREEN. Depends on: T5.
- **T7 (RED; skill: test-dev).** Add the reproducibility guard to `pricing-fallback-data.test.ts`.
  Depends on: nothing.
- **T8 (GREEN).** TypeScript/Node specialist. Implement design step 7 in
  `dash/scripts/bundle-litellm.mjs`, touching `MANUAL_ENTRIES` and its comment only. Do not run
  the bundler, and do not touch `dash/src/pricing/data/*`. Acceptance: T7 GREEN. Depends on: T7.
- **T9 (docs; skill: app-docs-standard, docs-dev; no-test).**
  - `docs/dash/architecture.md`, "`CostBlock` and cost basis" → "Repricing at projection time":
    - the per-session transactional write-back, and why `records.cost_json` is the one
      derived-on-write field;
    - the published-path cache-write rule (cache creation is billed as input when the source
      publishes no cache-write rate);
    - the shared `gpt-6-luna` tier measure and constant;
    - the reader provenance rule;
    - that `claude-sonnet-5-5` and `gpt-6-luna` live in `MANUAL_ENTRIES`.
  - `docs/dash/runbook.md`: next to the existing `priceOverrides` troubleshooting item, add one
    sentence. An override without `cacheCreation` bills cache writes at its input rate on the
    Claude Code/Codex path.
  - `docs/reference/kyberdash-rationale.md`: add follow-ups F1 and F2 to its follow-up list.
  - At PR close, archive this plan to `docs/archive/plans/` and move its `<plan-index>` row.
  - Run the gates.
  - Depends on: T2, T4, T6, T8.

## Dependency graph and MAX_CONCURRENCY

```
T1 ──────────────┐
T3 → T4 ─────────┴→ T2 ─┐
T5 → T6 ────────────────┼→ T9
T7 → T8 ────────────────┘
```

| Task | Depends on | File scope |
|---|---|---|
| T1 | — | `dash/src/canon/store.test.ts`, `dash/src/canon/projection.test.ts` |
| T2 | T1, T4 | `dash/src/canon/store.ts`, `dash/src/canon/sessions.ts`, `dash/src/canon/projection.ts` (doc comment) |
| T3 | — | `dash/src/canon/published-pricing.test.ts`, `dash/src/canon/copilot-rates.test.ts` |
| T4 | T3 | `dash/src/canon/published-pricing.ts`, `dash/src/canon/copilot-rates.ts`, `dash/src/pricing/models.ts`, `dash/src/canon/cost.ts` (comments) |
| T5 | — | `dash/src/synth/readers/copilot.test.ts` |
| T6 | T5 | `dash/src/synth/readers/copilot.ts`, `dash/src/synth/synth.ts` (comment) |
| T7 | — | `dash/src/pricing/data/pricing-fallback-data.test.ts` |
| T8 | T7 | `dash/scripts/bundle-litellm.mjs` |
| T9 | T2, T4, T6, T8 | `docs/dash/architecture.md`, `docs/dash/runbook.md`, `docs/reference/kyberdash-rationale.md`, this plan, `<plan-index>` |

Every task's file scope is disjoint from every other task's, so no two tasks edit one file.
`sessions.ts` carries three comments' fixes (577, 583, 591) and belongs to T2 alone. The four
pricing files carry five comments' fixes (583 export, 591, 621, 625, 643) and belong to T4
alone. **MAX_CONCURRENCY: 4.**

- Wave 1 runs T1, T3, T5 and T7 together.
- T4, T6 and T8 each start as soon as their RED task lands, so at most four tasks run at once.
- T2 waits for both T1 and T4.

## Risks

- **Override semantics shift (621).** A `priceOverrides` entry without `cacheCreation` builds a
  non-explicit cache-write rate. On the Claude Code/Codex path, cache writes for such an override
  are now billed at its input rate, not 1.25×. That matches `providers/codex.ts` and the Copilot
  table, and a user can set `cacheCreation` to state a surcharge. T9 documents it in the runbook.
  No bundled Claude model is affected: every one carries an explicit rate.
- **Figures that change.** A `gpt-6-luna` turn with cache creation now costs less (1.0× rather
  than 1.25× input) and may change tier. Both are intended and pinned by T3. Existing stores
  reprice on the next projection (U9).
- **Transaction scope (577).** One transaction per session, not per pass, keeps a failed session
  from rolling back other sessions' repricing. A crash mid-pass leaves earlier sessions
  committed, and the next projection finishes the rest, because repricing is idempotent.
- **Text-based bundler guard (657).** T7 reads the `.mjs` source rather than executing it,
  because executing it fetches from the network at top level. The guard is only as precise as
  the regex T7 writes. The task evidence shows the parsed tuples.
- **Plan lifecycle.** Any change to A1–A3, to the original U8–U12, or to the development mode
  returns this plan to Draft and reopens approval of the affected Test contract rows.

## Out of scope

- Reversing U9 (in-memory repricing), U10 (partial `cost_usd`) or the approved bridge default
  (4149313577 option (a), 4149313635, 4149313649).
- Falling back to the base rate on a tier miss (4149313604).
- Inverting the Copilot relabel default (4149313627, second half).
- Correcting the bundler's output path, or running it (F2), and a full LiteLLM re-snapshot.
- Widening the session-list cost cell (F1).

## Deferred follow-ups (issue texts for the conductor)

**F1. KyberDash: session-list cost cell wraps word statuses.** The session-list cost cell in
`dash/web/src/components/ContextExplorer.tsx` (`AgentSessionRow`) is `w-20 shrink-0` with no
`whitespace-nowrap`. Since #186 it renders the tile's words ("no published rate", "partially
priced", "out of scope"), which are wider than 80px at `text-xs`, so the row wraps to two lines.
Check it in the running dashboard, then either widen the cell or render a short label with the
full reason in the `title`. Keep list and tile on one formatter (U10). Raised by kilo-code-bot on
PR #225, comment 4149313649.

**F2. KyberDash: `bundle-litellm.mjs` writes a path the runtime never reads.**
`dash/scripts/bundle-litellm.mjs` sets `dataDir = join(__dirname, '..', 'src', 'data')`, so
`npm run build` writes `dash/src/data/litellm-snapshot.json` and `pricing-fallback.json`.
`dash/src/pricing/models.ts` and the tests import `dash/src/pricing/data/`, so builds never
refresh the bundled prices and leave a stray untracked directory. Changing the path makes every
build a full LiteLLM re-snapshot that reprices every model. Do that as the source-cited
re-snapshot follow-up from issue #186 (U11). Found while verifying comment 4149313657 on PR #225.

## Per-comment replies (for the conductor to post)

- **4149313577:** "Agreed on the write path, not on moving repricing in memory. `records.cost_json`
  write-back is a deliberate decision from the issue #186 plan (U9): the report path
  (`costContributionsForSessions`) reads `cost_json`, and in-memory repricing would give the list
  and the report different answers. Fixed the rest: each session's changed blocks are now written
  in one transaction through a new `CanonStore.setCosts`, instead of one autocommit `UPDATE` per
  record. The `buildSessions`/`projectCanonicalStore` docs now state that turn `cost_json` is the
  one derived-on-write field, and why it is idempotent. Cost blocks are never user-configured.
  `priceOverrides` flow in through the pricer, and `harness` blocks are never rewritten."
- **4149313583:** "Fixed. `copilot-rates.ts` exports one `isCopilotHarness` predicate;
  `copilotFamily` and `repriceTurns` both use it, and `isCopilotFamily` is gone."
- **4149313591:** "Fixed the implicit `''`: `pricePublishedTurn` now accepts an absent model and
  returns `{published, no_rate}` explicitly after the R5.2/R5.3 checks, with no lookup. The basis
  stays `published` on purpose. An `unknown` block beside `published` turns makes `sumCosts`
  refuse the session total with `COST_BASIS_MISMATCH`, which is the defect #186 fixed. A
  published-table attempt that finds no rate is `published/no_rate` by design."
- **4149313604:** "Keeping this. The R5.4/R5.6 contract says an input beyond every tier is a
  missing rate. `cost.test.ts` and `cost.scoping.test.ts` pin it, and the `priceWithTable` and
  `selectTier` docs state it. Falling back to the flat rate would price an oversized turn at the
  base-tier rate, which the vendor does not publish for that size. To close the gap you point at,
  a table-shape test now requires every tiered Copilot entry to end in an `upTo: Infinity` tier."
- **4149313621:** "Fixed. The canonical pricer now honours `cacheWriteCostIsExplicit`, as
  `providers/codex.ts` already does. When the source publishes no cache-write rate, cache creation
  is billed as input, not at the fabricated 1.25×. That matches the Copilot table's
  'Not applicable → input rate' rule, so both tables agree for GPT-6 Luna on both tiers. The
  misleading comment is corrected."
- **4149313625:** "Fixed. With the cache-write fix, a model with no explicit write rate
  (`gpt-6-luna`) folds cache creation into input, so the LiteLLM path's tier check reads the same
  measured input as `priceWithTable`. Tests assert both paths give the same figure at
  272,000/272,001, including the 10K cache-creation case you describe. The 272K boundary is now
  one exported constant that the Copilot table uses."
- **4149313627:** "Fixed the reader half: `loadCopilotCliCalls` sets `costHarnessReported` only
  when `cost_usd` is a finite number. Keeping the default. Inverting it means a Copilot parser
  that forgets the flag is carried as `harness` at LiteLLM API rates and never repriced. That is
  exactly the R5.3 failure in `kyberdash-rationale.md`. The current default only reprices such a
  figure, openly labelled `published`, from the credits table. The `costBlockFor` comment now
  says a new genuine Copilot reader must set the flag."
- **4149313635:** "Intentional, and pinned. Under the #186 plan (U10 and the bridge contract),
  `cost_usd` is the priced USD figure only; a partial session never shows an incomplete total as
  a figure. `kyber-bridge.test.ts` ('lists a partial block with no figure') pins it, and
  `SessionSummary.cost_usd` documents it. The `AgentSessionDashboard` line you cite reads a
  timeline node's `cost_usd`, not the session list row. `cost.value` carries the partial figure
  for any consumer that wants it."
- **4149313643:** "Fixed. The `priceWithTable` comment now states the policy: an absent cache-write
  rate bills cache creation at the input rate, as for a class the provider lists as not
  applicable. The Copilot `gpt-6-luna` tiers now carry an explicit `cacheWriteRate` equal to
  their input rate, so the table shows the applied price. The `measuredInput` doc notes that tier
  selection reads the summed input while each class is priced separately."
- **4149313649:** "Keeping the contract. The bridge default `{unknown, no_rate}` is what the tile's
  `normalizeCostBlock` renders for an absent block, so the list and the tile give one answer. It
  is pinned in `kyber-bridge.test.ts`. The `costUsd` and `—` arms serve legacy payload shapes
  (`costUsd`, `cost_usd`-only fixtures). The cell width is a fair point: word statuses wrap in
  `w-20`. That needs a visual check, so it is tracked as a follow-up issue."
- **4149313657:** "Fixed where it matters: both rows are now in `MANUAL_ENTRIES` with their
  citations, and a test checks they match the committed snapshot. One correction: the bundler
  writes `dash/src/data/`, not `dash/src/pricing/data/`, so `npm run build` does not delete them
  today. It never refreshes the bundled prices at all. Fixing that path makes every build a full
  re-snapshot, so it is tracked as a separate follow-up issue."

## Verification gates

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run check:reachable
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
```

At PR close, archive this plan to `docs/archive/plans/` and move its inventory row before
`docs validate . --merge-ready`, which rejects active plans (`KW-DOC-LIFECYCLE-003`).

## Review and closeout

- Code review per the repository council flow over the follow-up diff.
- Delivery: push to the existing branch `fix/issue-186-cost-pricing`, so the fixes land in
  PR #225. The conductor posts the per-comment replies above and files F1 and F2 as GitHub issues.
- Closeout (T9, `docs-dev`): harvest into `docs/dash/architecture.md`,
  `docs/dash/runbook.md` and `docs/reference/kyberdash-rationale.md`.
- ADR: none. No decision here constrains future work beyond the approved #186 contract.
