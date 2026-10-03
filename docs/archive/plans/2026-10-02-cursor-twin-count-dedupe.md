---
id: plans/2026-10-02-cursor-twin-count-dedupe
title: "KyberDash: stop cursor twin collectors double-counting overlapping output (#231)"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-02
development-mode: test-first
code-refs:
  - dedupeTwinTurns
  - collapseCluster
  - counterKey
  - TWIN_TURN_MAX_SKEW_MS
  - buildSessions
  - normalizeHarnessName
decided-by:
  - adr/0009-multi-signal-ingestion-span-shaped-record
---

# KyberDash: stop cursor twin collectors double-counting overlapping output (#231)

**Status: Complete, archived 2026-10-02.** Development mode: `test-first`. Branch:
`cursor/issue-231-twin-count`. Approve-and-execute 2026-10-02 (orchestrator; Q1=A3
stands; ledger closed). T1–T5 complete: complementary/overlap join in
`dedupeTwinTurns` (file+file under folded `cursor`), session-totals contract,
architecture A3 harvest in [dash/architecture.md](../../dash/architecture.md).
End-of-run code-review council **VERDICT: APPROVE**, with disclosure of pre-existing
host `ts-test` failures (migration DROP COLUMN + cursor date-floor flake; not
introduced by #231). No new ADR — A3 recorded in this plan and architecture prose
(house style of #182). Archived here per `KW-DOC-LIFECYCLE-003` before
`docs validate . --merge-ready`.

This plan addresses [issue #231](https://github.com/dpalfery/kyber-weave/issues/231): after the
#182 twin-front-end fold and ADR 0009 exact-counter dedupe, Cursor's complementary
file-row halves still leave the same small `output` figure on both sides of a merged
share, so session totals sum it twice (live key `0f659701-8568-44e8-86d2-0cf768294ae9`:
~90 vs ~45).

**Discovery provenance.** Kyber-Weave MCP `docs_*` tools and CodeGraph MCP were
unavailable in this harness; docs discovery started at
[`docs/README.md`](../../README.md), [`docs/dash/architecture.md`](../../dash/architecture.md),
and [ADR 0009](../../adr/0009-multi-signal-ingestion-span-shaped-record.md). Code discovery
used shell `codegraph explore` against the local `.codegraph/` index, then Read/Grep for
line-accurate citations. `git rev-parse --show-toplevel` =
`/Users/hal/git/cursor/kyber-weave-43`.

## Problem and goal

**Problem.** `#182` folds `cursor-agent` onto `cursor` and applies
`dedupeTwinTurns` (`dash/src/canon/twin-dedupe.ts`) so same-turn observations with
**byte-identical** six-counter payloads collapse under ADR 0009 D4. Cursor twins are
not that shape: live evidence (archived plan
[`2026-09-30-issues-181-182-191`](2026-09-30-issues-181-182-191.md)
§Live evidence; issue #231 body) shows 3 `cursor` file rows (request/response halves
such as `17007/0`, `0/45`) plus 1 `cursor-agent` row (`125/45`). Exact-counter
matching never clusters them, so `buildSessions` sums `tokens.output` across the union
(`sessions.ts` totals reduce) and the overlapping `45` is counted twice.

A second structural gap: both collectors stamp `codeburn/…` sources, so
`isFileSource` is true for every row. Even if counters matched,
`collapseCluster` requires mixed OTel + file kinds and would no-op — the Claude
OTLP+file path cannot be reused as-is for Cursor file+file twins.

**Goal.** After rebuild of derived tables, a merged Cursor twin share that exhibits
the #231 overlap reports each turn's output **once** (~45, not ~90) on sessions,
runs, and findings inputs, without inventing under-counts for genuinely distinct
turns, and without widening the ontology. Raw ingest rows stay as provenance.

## Approved decisions

| Id | Decision | Provenance |
|---|---|---|
| A1 | Deliver via a Draft → Ready plan on branch `cursor/issue-231-twin-count` (PLAN path already chosen). | Conductor assignment for issue #231, 2026-10-02 |
| A2 | Development mode is `test-first` (default; no user opt-out). | Conductor assignment |
| A3 | Resolve overlapping Cursor twin counters by **cross-source turn-joining beyond exact counters** inside `dedupeTwinTurns`: complementary / overlap join within `TWIN_TURN_MAX_SKEW_MS`, then per-dimension max / prefer-fuller-row merge so joined counters are **never summed**; admit **file+file** twin pairs under the folded `cursor` share (not only OTel+file). Option **(b)** (per-turn output ceiling at aggregation without collapsing rows) is **rejected** — it leaves inflated turn cardinality and can under-count distinct turns that share an output figure. | Orchestrator ruling 2026-10-02, grounded in ADR 0009 D4 (no summing the same turn across sources); (b) rejected — leaves inflated turn cardinality and can under-count distinct turns sharing an output figure. Reaffirmed at approve-and-execute 2026-10-02 (Q1=A3 stands). |
| A4 | Approve-and-execute: finalize this plan Draft → Ready (frontmatter `status: current`; ontology has no `ready` value) with no further plan-content changes; implementation may proceed on T1–T5. | Orchestrator approve-and-execute 2026-10-02; Q1=A3 stands; ledger closed |

Implementation guidance locked by A3 (not open questions):

- Join predicate: time skew + complementary/subset counter relation + twin share.
- Keeper selection: prefer fuller counter vector / max per field; never sum joined counters.
- Content transplant for file+file follows today's donor rules where applicable.
- Claude exact-counter OTel+file path stays unchanged.

## Investigation findings

Docs MCP unavailable — fallback path used (see provenance above).

1. **ADR 0009 D4** ([`docs/adr/0009-…`](../../adr/0009-multi-signal-ingestion-span-shaped-record.md)): for one turn with OTel + file, counters from OTel, content from file when OTel has no parts; **values are never summed across sources for the same turn**. Alternatives explicitly reject summing. The ADR does **not** define how to identify "same turn" when counters differ or both rows are file-sourced — A3 supplies that identification for Cursor twins.

2. **Architecture contract** ([`docs/dash/architecture.md`](../../dash/architecture.md) ~L591–599): twin fold + exact-counter collapse are shipped; *"Same-turn observations whose counters differ are left alone (follow-up #231)."* T4 replaces that residual sentence with the A3 join rule.

3. **`dedupeTwinTurns`** (`dash/src/canon/twin-dedupe.ts:120+`): buckets by exact `counterKey` (session + six counters); clusters by `TWIN_TURN_MAX_SKEW_MS` (60s span); `collapseCluster` drops file rows only when **both** OTel and file kinds are present (`:201–203`). Header comments (`:34–39`) already name #231 as the exact-counter boundary.

4. **Call sites:** `dedupeTwinTurns` is applied from `buildSessions`, `buildRuns`, and `buildFindings` (CodeGraph blast radius). Session totals sum `record.tokens.output` after dedupe (`dash/src/canon/sessions.ts` ~L607–615).

5. **Cursor sources:** `createCursorProvider` / `createCursorAgentProvider` synthesize under the `codeburn/` namespace; `isFileSource` (`measurability.ts:72–73`) is true for both. Exact-counter Claude path cannot fire for Cursor twins even with identical counters — A3 therefore admits file+file collapse under the folded `cursor` share.

6. **Pinned regression that must flip under the A3 fix:**
   `twin-dedupe.test.ts` *"leaves disjoint turns from twin collectors untouched (cursor-style)"* (`:105–124`) currently expects length 2 for `17007/0` + `125/45` and claims no inflation — incomplete vs the issue's three-file + agent shape and the doubled `45`.

7. **#182 residual** (archived plan live evidence): Cursor fold was accepted as complementary union; the `45`-output overlap was explicitly deferred. Amounts are trivial; the rule incompleteness is the defect.

## Test contract (`test-first`)

Locked to **A3** (Q1=(a)). Changing this contract returns the plan to Draft and requires reapproval.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 (RED) | `dash/src/canon/twin-dedupe.test.ts` | `npm --prefix dash exec vitest run src/canon/twin-dedupe.test.ts` | Fixture mirroring #231: cursor file halves (`17007/0`, `0/45`) + `cursor-agent` (`125/45`) within skew → after dedupe, summed `output`/`reportedOutput` equals **45** (not 90); Claude exact-counter OTel+file cases still collapse as today; two same-kind identical OTLP rows still kept | New/rewritten assertions fail on current `dedupeTwinTurns` | — |
| T2 (GREEN) | same | same | Same assertions pass after join/merge implementation | — | Focused file green; no weakening of Claude / same-kind-keep contracts |
| T3 (RED→GREEN) | `dash/src/canon/sessions.test.ts` (or adjacent canon session total test) | `npm --prefix dash exec vitest run src/canon/sessions.test.ts` (narrow filter as needed) | `buildSessions` over a seeded twin-cursor share reports session `totals.output` once for the overlapped turn | Fails before T2 lands / before sessions see joined records | Passes after T2; rebuild-only (no migration) |
| T4 | docs only — no unit test | `/Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` and `docs drift .` | Architecture text replaces the "#231 left alone" residual with the A3 join rule | N/A (docs gate) | Zero findings |
| T5 | plan inventory | same docs gates | Active → Archived row on finishing PR; plan not left open under `--merge-ready` | N/A | `KW-DOC-LIFECYCLE-003` clear at merge |

## Dispatchable tasks

### T1 — RED: #231 overlap contract in `twin-dedupe.test.ts`

- **Objective:** Encode the live overlap shape and the non-regression Claude/same-kind contracts as failing tests.
- **Files/symbols:** `dash/src/canon/twin-dedupe.test.ts`; behavior of `dedupeTwinTurns`.
- **Acceptance:** Focused vitest run shows RED solely for the new overlap expectations; existing Claude collapse tests still pass.
- **Dependencies:** none (after Ready).
- **Required skills:** `test-dev`.

### T2 — GREEN: complementary / overlap join for file+file twins

- **Objective:** Extend `dedupeTwinTurns` / cluster collapse so overlapping Cursor twin observations within `TWIN_TURN_MAX_SKEW_MS` join and merge counters by **per-dimension max / prefer-fuller-row** (never sum joined counters); admit file+file pairs under the folded `cursor` share; preserve ADR 0009 OTel+file exact-counter path; keep same-kind identical OTLP retries. Join predicate and content transplant follow the A3 guidance above.
- **Files/symbols:** `dash/src/canon/twin-dedupe.ts` (`dedupeTwinTurns`, `collapseCluster`, `counterKey` / any new join key helper); header remarks naming the #231 rule.
- **Acceptance:** T1 suite GREEN; no change to ingest adapters; raw dual rows remain in the store until derived rebuild.
- **Dependencies:** T1.
- **Required skills:** (TypeScript / KyberDash canon — map via conductor; no C# skill).

### T3 — Session totals see the join

- **Objective:** Prove `buildSessions` totals for a twin-cursor share match the joined counters (output once).
- **Files/symbols:** `dash/src/canon/sessions.ts` (`buildSessions` totals reduce); test in `sessions.test.ts`.
- **Acceptance:** RED then GREEN around T2; no schema migration.
- **Dependencies:** T2 (implementation); T1 (contract precedent).
- **Required skills:** `test-dev` then implementation skill as for T2.

### T4 — Docs: retire the #231 residual sentence

- **Objective:** Update `docs/dash/architecture.md` honest-measurement paragraph to state the A3 same-turn join rule and remove "left alone (follow-up #231)".
- **Files:** `docs/dash/architecture.md` (~L591–599); this plan's index row stays Active until closeout.
- **Acceptance:** `docs validate` and `docs drift` zero findings.
- **Dependencies:** After T2 so prose matches code.
- **Required skills:** `app-docs-standard` (or docs-dev closeout skill as mapped by conductor).

### T5 — Review, gates, archive

- **Objective:** Dash gates + review council; archive this plan on the finishing PR per `KW-DOC-LIFECYCLE-003`.
- **Dependencies:** T2–T4.
- **Required skills:** `code-review`; docs closeout.

~~Alternate task spine if Q1=(b)~~ — **not chosen.** A3 rejected (b); do not implement a sum-site ceiling from this plan.

## Dependency graph and MAX_CONCURRENCY

```
T1 (RED) → T2 (GREEN) → T3 (session totals)
                      ↘ T4 (docs)
T2+T3+T4 → T5 (review / archive)
```

| Task | Depends on | File scope |
|---|---|---|
| T1 | — | `twin-dedupe.test.ts` |
| T2 | T1 | `twin-dedupe.ts` |
| T3 | T2 | `sessions.ts` / `sessions.test.ts` |
| T4 | T2 (content) | `docs/dash/architecture.md` |
| T5 | T2, T3, T4 | plan index + PR |

**MAX_CONCURRENCY: 1** while T1→T2 share the dedupe module; after T2, **MAX_CONCURRENCY: 2** (T3 ∥ T4) if file scopes stay disjoint.

## Risks

| Risk | Mitigation |
|---|---|
| Over-join collapses two real turns that share an output figure | Bound by session share + `TWIN_TURN_MAX_SKEW_MS`; require complementary/subset relation, not output equality alone; keep same-kind identical OTLP retries |
| File+file merge picks the wrong keeper | Prefer fuller counter vector / per-field max; transplant content like today's donor rules; pin with the live half-pair fixture |
| Derived tables stale after binary upgrade | Existing rebuild runbook; no migration — same as #182 |

## Out of scope

- Changing `normalizeHarnessName` fold pairs or adding new harness folds.
- Ingest / provider parser changes under `dash/src/providers/**` or `dash/src/synth/**` (counters are not wrong at source per #231 evidence).
- Claude exact-counter OTel+file behavior (must remain).
- Live store repair as a planning deliverable (operator `kyber build` / rebuild after ship).
- Pricing / cost double-count (only token counters in #231 evidence).
- Per-turn output ceiling at aggregation without collapsing rows (Q1=(b); rejected by A3).

## Verification gates

```bash
npm --prefix dash exec vitest run src/canon/twin-dedupe.test.ts
npm --prefix dash exec vitest run src/canon/sessions.test.ts   # or focused filter from T3
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run check:reachable
/Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
/Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
```

(Restore/build CLI first if `--no-build` fails.)

## Review and docs-dev closeout

- End-of-run `code-review` council on the branch diff.
- Harvest A3 into `docs/dash/architecture.md` (T4); no new ADR unless implementation materially amends ADR 0009's identification rule beyond "same turn" — prefer architecture prose + this plan's decision record (house style of #182).
- Archive this plan in the finishing PR; move the Active Plans row to Archived.

## Closeout evidence

| Check | Result |
|---|---|
| T1 RED / T2 GREEN (`twin-dedupe` #231 overlap + Claude/same-kind contracts) | Complete — complementary/overlap join in `dedupeTwinTurns`; file+file under folded `cursor` |
| T3 session totals (`buildSessions` output once) | Complete — `sessions.test.ts` pins `total_output` / token totals at 45 |
| T4 architecture A3 harvest | Complete — [dash/architecture.md](../../dash/architecture.md) honest-measurement paragraph states the join rule; "#231 left alone" residual removed |
| T5 review / archive | Complete — council **APPROVE**; pre-existing host `ts-test` (migration DROP COLUMN + cursor date-floor flake) disclosed, not introduced by #231; plan archived 2026-10-02 |
| ADR | None — A3 stays in this plan + architecture (no amendment to ADR 0009 beyond same-turn identification) |
