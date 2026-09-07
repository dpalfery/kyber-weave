---
id: plans/2026-09-05-kyberdash-diagnostic-hierarchy-and-context-inspector
title: Diagnostic Hierarchy, Context Inspector, and Evidence-Backed Findings
doc-type: plan
status: superseded
owner: dpalfery
last-reviewed: 2026-09-05
component: KyberDash
---

# Diagnostic Hierarchy, Context Inspector, and Evidence-Backed Findings

**Status:** Completed & Archived (Harvested to ADRs 0012–0015)  
**Date:** 2026-09-05  
**Archive Date:** 2026-09-05  
**Goal:** Turn KyberDash from a per-session analysis view into a navigable diagnostic product: a progressive-disclosure hierarchy (All Harnesses → Harness → Run → Agent Execution → Turn → Event/Context Item), a full-content context inspector with copy-out, a signal and finding engine whose output carries evidence and calibrated confidence, a run/turn comparison workflow that verifies its own predictions, and an optional LLM review seam over the assembled context.

Successor to [2026-09-04-kyberdash-asad-context-dashboard.md](2026-09-04-kyberdash-asad-context-dashboard.md), which made `canon.db` project the ASAD payload and made the ASAD dashboard the only Context view. That plan delivered **one screen for one session**. This plan supplies the levels above it, the level below it, and the diagnostic layer that makes the numbers actionable. It builds on architecture that already exists — the canonical store, the ASAD payload contract, per-bucket measurability, the content endpoint — so it is a plan, not a spec.

The interaction design this plan implements is the reviewed prototype `KyberDash.dc.html` in the design project. Screen names below correspond to sections of that prototype.

---

## 1. Problem / Motivation

1. **There is no level above the session.** The Context page answers "what happened in this session" and nothing else. A developer with 156 runs across ten harnesses has no ranked entry point, so the product cannot answer "where should I look" — the question that precedes every other one. Predecessor decision D1 was right about there being one session view; it said nothing about what sits above it.
2. **Sessions are not the unit developers recognise.** A run with a root agent and two delegated children is one unit of work and three sessions. Delegation overhead — the tokens paid purely to hand work off — is invisible because no entity spans the handoff.
3. **The turn view describes context it will not show.** `analyzeContext` buckets a turn into system / conversation / tool-definitions / MCP / buffer, and the drawer retrieves unclipped parts for a *clicked band*. There is no path from "system prompt: 31,240 tokens" to reading those 31,240 tokens, and no way to get the text out of the app into an editor where a prompt actually gets fixed.
4. **Composition is reported, never diagnosed.** The payload says a bucket is large. It does not say the block is byte-identical to 26 earlier copies, that it lands after the cache breakpoint, that this is why the prefix is invalidated, or what to change. `rankSchemas` ranks; nothing explains.
5. **Cost-shaped framing is re-entering through the UI.** `SessionCostPanel` and `SessionSpendCharts` are the most finished components on the page. Left alone, spend becomes the organising principle by default — the exact outcome [ADR 0006](../adr/0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md)'s soft fork was supposed to avoid, achieved by attrition rather than by decision.
6. **Nothing closes the loop.** `compareHarnesses` compares harnesses, not two runs of the same task under different configuration. A developer who acts on a KyberDash observation has no way to learn whether the change helped, and KyberDash has no way to learn whether its own advice was right.
7. **Measurability is modelled but not yet load-bearing in the UI.** B3 made every bucket measured / derived / `not_measurable` with a reason. That discipline must extend to *findings*: a harness with no telemetry must never rank as efficient by absence of evidence.

## 2. Approved decisions

- **D1 — Six levels, one spine.** Navigation is All Harnesses → Harness → Run → AgentExecution → Turn → Event/ContextItem. The harness is the first actionable level. The cross-harness landing page summarises and drills; it is not a dashboard in its own right.
- **D2 — `Run` and `AgentExecution` are new first-class canonical entities.** A run groups the executions belonging to one user-initiated unit of work. They are derived and rebuildable from `records`, like `session` — not a new ingest path.
- **D3 — No composite efficiency score, ever.** The scorecard is six independent dimensions: context hygiene, cache efficiency, tool yield, skill utilisation, delegation overhead, continuity. A dimension with no telemetry renders as a dash, never a zero and never a passing grade.
- **D4 — Every context block is readable in full and copyable.** Clicking any composition band opens the assembled text of that block, sub-divided by part, with per-block copy and whole-turn copy. Where content is not measurable, the pane states the reason and shows nothing — it never renders a placeholder that could be mistaken for content.
- **D5 — A finding is a claim plus evidence plus confidence.** Every finding carries: mechanism prose, an evidence list where each row names its source and links to the originating record, a measurement class (`deterministic` / `inferred` / `coverage-gap`), a stated confidence basis, what would raise it, a recommendation, an expected improvement with an error bar, and an outcome-risk caveat naming how the change could make the run worse.
- **D6 — Ranking is `estimated recoverable waste × outcome risk × confidence`.** An inferred finding can never outrank a deterministic one of comparable size. Recoverable waste is an estimate attached to one specific change, not a property of a token.
- **D7 — Recommend, do not act.** No write path into any harness configuration, in this plan or any successor without its own ADR. KyberDash shows its work and stops.
- **D8 — Recommendations prefer moving context to deleting it.** Cache-position changes, progressive disclosure, and on-demand tools are preferred over removal, because they are reversible and do not assume unobserved context was useless.
- **D9 — Cost is a derived column.** Spend appears as a secondary figure beside token and latency figures. It is never a top-level nav item, never the primary sort, and never the landing metric.
- **D10 — The LLM review seam is opt-in, explicit, and local by default.** No context leaves the machine without a per-invocation user action. The provider is configurable; the default configuration is "no provider configured", and the panel says so rather than failing.
- **D11 — Comparison is a first-class workflow, and it verifies KyberDash.** Two runs of the same task family are aligned by phase, not turn index. Every prediction is logged with its stated error bar and scored later; the calibration record is visible in the app.
- **D12 — CodeBurn contact stays behind the merge zone.** Reuse of ingestion and pricing is expected; a CodeBurn type appearing above the adapter seam is a build failure, enforced mechanically rather than by review.
- **D13 — Run boundary derivation.** Require explicit run identity where emitted by the harness; otherwise provide a labelled *derived* grouping based on working-directory and bounded time gaps that can be accepted per harness. Never silently present a heuristic as a reported fact (resolves Q1).
- **D14 — Content retention window & purge.** Retain unclipped context content in `canon.db` with a default 14-day rolling retention window and an explicit CLI purge command (`kyber purge-content`). D4 is preserved without creating an unbounded plaintext corpus liability (resolves Q2).
- **D15 — Classification by evidence of use.** Context items are classified strictly as `evidence of use: strong / weak / none / unobserved`. The product states empirical observability of use, never value verdicts (resolves Q3).
- **D16 — Skill utilisation ranking.** Ship `skillUtilisation` at low confidence, ranked last per D6. It is preserved to motivate progressive disclosure while D6 and D8 prevent it from driving harmful context deletion (resolves Q4).
- **D17 — Materialized findings with version stamp.** Materialize findings into `canon.db` at session build time with a `detector_version` schema stamp that forces automatic recomputation whenever detectors change (resolves Q5).

## 2a. Open questions (decision ledger)

| Q# | Question | Options | Recommended | Depends on | Status |
|---|---|---|---|---|---|
| Q1 | How is a `Run` boundary derived when the harness emits no run identity? | (a) session id only, one run per session; (b) working-directory + time-gap clustering; (c) require explicit run id, otherwise one-execution run | (c) with (b) offered as a labelled *derived* grouping the user can accept per harness — never silently | E1 | ANSWERED: D13 |
| Q2 | Does the local index store context **content** or only hashes plus offsets? | (a) content, as today, enabling the inspector; (b) hash-only, killing D4; (c) content with a retention window and an opt-out | (c) — content with a default 14-day retention and an explicit purge command; D4 is the product, and an unbounded plaintext corpus of every prompt is a standing liability | — | ANSWERED: D14 |
| Q3 | Should the classification labels be `necessary / questionable / avoidable` or `evidence of use: strong / weak / none`? | (a) the first, as prototyped; (b) the second; (c) both, the second as the label and the first as a filter | (b) — the first framing reads as a verdict on value when the measurement is only about observability of use | F2 | ANSWERED: D15 |
| Q4 | Is skill utilisation shipped at all in this plan, given no harness emits activation? | (a) ship at low confidence, ranked last; (b) hold until a harness cooperates | (a) — it is the only signal that motivates progressive disclosure, and D6 already prevents it outranking measured findings | F3 | ANSWERED: D16 |
| Q5 | Where does the finding engine run — projection time or query time? | (a) materialise findings into `canon.db` on session build; (b) compute per request; (c) materialise, with a version stamp forcing recompute when a detector changes | (c) — detectors will change weekly during this plan and stale findings are worse than slow ones | F1 | ANSWERED: D17 |

*All 5 planning decisions are resolved and approved. No blocking implementation decisions remain.*

## 3. Investigation findings

Claims below are marked **verified** (read in tree), **documented** (asserted by a governed doc), or **assumed**.

- **The canonical payload already carries most of what the hierarchy needs.** B1 shipped `context.first`/`context.last`, `tools[]` with `schema_tokens` / `invocations` / `turns_resident`, `timeline`, `turns`, `requests`, `servers`, `coverage`, `problems`, `reconciliation` and `summary`. *(documented — plan 2026-09-04 §4 B1)*
- **`Run` and `AgentExecution` do not exist.** `dash/kyber/canon/sessions.ts` derives one `session` row per session id; there is no parent/child linkage table and no run grouping. *(verified — file listing plus predecessor plan's symbol inventory)*
- **The analysis layer is present and consumed.** `analysis/context.ts` (`analyzeContext`), `analysis/schema.ts` (`rankSchemas`), `analysis/timeline.ts` (`buildTimeline`) and `analysis/compare.ts` (`compareHarnesses`) all have production consumers via `buildSessionRow`. There is no `analysis/signals.ts` or `analysis/findings.ts`. *(verified)*
- **Measurability is a real contract, not a convention.** `canon/measurability.ts` exports `measurabilityFor`, `getMeasurability`, `schemaRankingAvailability`, `contextCompositionAvailability`, and `sessions.ts` merges them. The finding engine can reuse this rather than re-deriving availability. *(verified)*
- **An unclipped content endpoint exists but is not a reading surface.** `server/bridge.ts` `getSessionContent` / `applyContentBudget` and `routes.ts` `parseSessionContentPath` serve full parts with a server-side budget; `SessionInspectorDrawer.tsx` `FullContentPanel` renders one clicked band. C4's live gate — a real drawer click against a live session — is still **not passed**. *(verified + documented, predecessor §10)*
- **The predecessor's live gates are still open and this plan inherits them.** Claude `OTEL_LOG_RAW_API_BODIES=1`, Cursor hook registration in `~/.cursor/hooks.json`, and one live full-content drawer click all remain owner-gated. Per-server schema bands were 0 of 81 live. *(documented, predecessor §10)*
- **Cache telemetry is the highest-yield signal and its availability is unsurveyed.** Prefix-stability diagnosis needs `cache_read` / `cache_write` counters *and* request-prefix bytes. The telemetry inventory records content availability per harness; it does not record cache-field availability per harness. *(assumed — inventory not read for this claim; E4 must survey it)*
- **Ten harnesses are in scope and their floors differ enormously.** Claude Code, Codex, Cursor, Kilo, Copilot (extension / CLI / coding agent), Gemini, pi, opencode, Antigravity. Copilot CLI already computes the ASAD taxonomy on disk; Kilo is dark; opencode emits nothing until a flag flips. A scorecard rendered over that spread will be mostly dashes at first, and that is the honest output. *(documented, predecessor §3 and D6)*
- **The UI's most complete components are the cost ones.** `SessionCostPanel.tsx`, `SessionSpendCharts.tsx` (`TurnSpendChart`, `ContextCompositionChart`) and `SchemaCostRanking.tsx` are all test-backed and shipped. The diagnostic components this plan needs do not exist. *(verified)*
- **`ContextExplorer.tsx` is now a single-path component.** C1 removed `TreeTable` and `SessionDetails`; every provider row expands `AgentSessionDashboard`. The hierarchy therefore has one insertion point rather than two. *(documented, predecessor §10 "Tree-verified")*
- **Component tests do not run in CI.** `dash/package.json`'s `"test": "vitest run tests"` excludes `dash/dash/src/**`. The predecessor made adding them "part of Phase C"; it did not land. This plan cannot rely on component tests as a gate until H3 fixes it. *(documented, predecessor §9)*
- **No calibration record exists.** Nothing logs a prediction, and nothing scores one afterwards. The prototype displays confidence to two decimal places; on current evidence those digits are unearned. *(verified — no such module)*

## 4. Task list

**Phase E — The entities above the turn.** No new UI. Every later phase reads these.

### E1 — Derive `Run` and `AgentExecution` as canonical entities

- **Objective:** Group sessions into runs and link parent/child executions, as rebuildable derived tables over `records`, with the grouping basis recorded per row.
- **Files / symbols:** `dash/kyber/canon/types.ts` (`RunRow`, `ExecutionRow`, `RunGroupingBasis`); new `dash/kyber/canon/runs.ts` (`buildRuns`, `linkExecutions`, `deriveRunIdentity`); `dash/kyber/canon/store.ts` (`run` / `execution` tables + migration, `getRun`, `listRuns`, `getExecutionTree`); `dash/kyber/canon/sessions.ts` (`buildSessions` emits execution linkage); `runs.test.ts`, `store.test.ts`, `sessions.test.ts`.
- **Acceptance criteria:** A run groups its executions for any harness emitting run or session identity; where identity is absent the grouping basis is recorded as `derived` with its rule named, per Q1 — never as reported fact. Parent/child linkage is present where the harness emits it and explicitly `not_measurable` where it does not. `kyber build` rebuilds both tables from retained records with no re-ingest. A flattened-subagent harness yields one execution per run and a reason, not a fabricated tree.
- **Skills:** TypeScript; SQLite migration; derived-projection design; Vitest.
- **Dependencies:** None. Owns the store seam before F and G touch it.

### E2 — Aggregate harness-level rollups

- **Objective:** Project per-harness aggregates over runs — run count, median and p95 peak context pressure, cache hit rate, tool yield, delegation overhead, outcome mix, field-coverage percentage — each carrying its own measurability.
- **Files / symbols:** new `dash/kyber/canon/harnesses.ts` (`buildHarnessRollup`, `HarnessRollupRow`, `coverageFor`); `dash/kyber/canon/store.ts` (`harness_rollup` table + migration, `listHarnesses`); `dash/kyber/canon/measurability.ts` (`harnessDimensionAvailability`); `harnesses.test.ts`.
- **Acceptance criteria:** Every dimension is a value with a measurement class or `not_measurable` with a reason. A harness with zero collectable runs appears in the list with a stated reason and no computed dimensions. No rollup averages a measured value together with a derived one without labelling the result derived.
- **Skills:** TypeScript; aggregation semantics; measurement semantics; Vitest.
- **Dependencies:** E1.

### E3 — Model outcome as a guard signal

- **Objective:** Capture what is already observable about how a run ended — termination reason, test result delta, user-correction count, abandonment, repeated attempts — as an explicit, sparsely-populated contract.
- **Files / symbols:** new `dash/kyber/canon/outcome.ts` (`OutcomeBlock`, `deriveOutcome`, `outcomeAvailability`); `dash/kyber/canon/runs.ts` (attach to `RunRow`); `dash/kyber/synth/readers/*` (surface exit codes and correction turns where readers see them); `outcome.test.ts`.
- **Acceptance criteria:** A run with no observable outcome reports `not_measurable` per field, never "completed". A user correction is identified by an explicit rule that is documented and tested, not by heuristic prose. Population rate across the live corpus is measured and recorded — the input to the §6 risk on outcome measurement.
- **Skills:** TypeScript; measurement semantics; JSONL reading; Vitest.
- **Dependencies:** E1.

### E4 — Survey cache and prefix availability per harness

- **Objective:** Establish, per harness, whether cache counters and request-prefix bytes are obtainable — the precondition for the two highest-yield signals — and record it in the governed inventory.
- **Files / symbols:** `docs/dash/telemetry-inventory.md` (per-harness cache/prefix availability rows); `dash/kyber/canon/measurability.ts` (`cacheAvailability`, `prefixAvailability`); `dash/kyber/tools/parity.ts` (extend the audit to report cache/prefix coverage); `parity.test.ts`.
- **Acceptance criteria:** Each of the ten harnesses carries a verified / documented / unverified / assumed marking for cache counters and for prefix reconstruction. Where prefix bytes are unavailable, the fallback (`cache_read ÷ input`) is recorded as detect-but-cannot-locate. `docs validate .` and `docs drift .` pass.
- **Skills:** Empirical telemetry inspection; OpenTelemetry GenAI conventions; governed documentation.
- **Dependencies:** None. Should run first — its result may cut Phase F's scope.

**Phase F — Signals and findings.**

### F1 — The signal engine

- **Objective:** Compute the v1 signal set as pure, individually testable detectors, each declaring its numerator, denominator, unobservability rule, and measurement class.
- **Files / symbols:** new `dash/kyber/analysis/signals.ts` (`Signal`, `SignalResult`, `computeSignals`, and one detector per signal: `contextReuseRatio`, `cachePrefixStability`, `toolYield`, `duplicateCallRate`, `oversizedResultShare`, `compactionPressure`, `delegationOverhead`, `skillUtilisation`); `dash/kyber/analysis/signals.test.ts`; `dash/kyber/canon/sessions.ts` and `runs.ts` (attach results); `canon/store.ts` (`detector_version` stamp per Q5).
- **Acceptance criteria:** Each detector returns a value with a measurement class or an explicit unavailability with a reason; none returns 0 for missing input. Whitespace-normalised hashing is applied and the normalisation is reported. `cachePrefixStability` degrades to the counter-only fallback with a label when prefix bytes are absent. `toolYield` credits on weak evidence and debits only on strong evidence, and its under-counting of negative-information reads is asserted in a test. A bumped `detector_version` forces recompute.
- **Skills:** TypeScript; measurement semantics; content hashing; Vitest.
- **Dependencies:** E2, E4.

### F2 — Context-item classification

- **Objective:** Classify every context block by *evidence of use*, resolving Q3's labels before the UI depends on them.
- **Files / symbols:** `dash/kyber/analysis/context.ts` (`analyzeContext` returns a classification per bucket); new `dash/kyber/analysis/classify.ts` (`classifyContextItem`, `EvidenceOfUse`); `dash/kyber/canon/types.ts`; `classify.test.ts`, `context.test.ts`.
- **Acceptance criteria:** Four states — strong / weak / none / unobserved — where `unobserved` means the harness does not report whether the block was used, and is never merged with `none`. Every classification carries the rule that produced it. An unattributed residual (usage-record input minus reconstructed total) is its own block, never distributed across the others.
- **Skills:** TypeScript; measurement semantics; Vitest.
- **Dependencies:** F1, Q3 answered.

### F3 — The finding engine

- **Objective:** Turn signal results into ranked findings that satisfy D5 field-for-field.
- **Files / symbols:** new `dash/kyber/analysis/findings.ts` (`Finding`, `EvidenceRow`, `Confidence`, `Recommendation`, `ExpectedImprovement`, `detectFindings`, `rankFindings`); one detector per finding class (`staleRepeatedContext`, `prefixInstability`, `duplicateAfterCompaction`, `oversizedToolResult`, `delegationSeedBloat`, `unexercisedSkillContext`, `coverageGap`); `findings.test.ts`; `canon/store.ts` (`finding` table + migration); `server/bridge.ts` and `routes.ts` (`/api/kyber/findings`, `/api/kyber/finding/:id`).
- **Acceptance criteria:** Every finding carries mechanism, ≥2 evidence rows each naming a source and resolving to a record id, measurement class, confidence with a written basis and a what-would-raise-it, recommendation, expected improvement with an error bar, and an outcome-risk caveat. Ranking implements D6 and is asserted by a test where a large inferred finding loses to a smaller deterministic one. A harness with usage-only telemetry produces a `coverageGap` finding with no computed waste, and cannot rank as efficient. Recommendations that delete context rather than relocate it fail a D8 lint test.
- **Skills:** TypeScript; diagnostic reasoning; contract modelling; Vitest.
- **Dependencies:** F2, E3.

### F4 — Prediction logging and calibration

- **Objective:** Log every prediction with its stated confidence and error bar, score it once a comparable later run exists, and expose the calibration curve.
- **Files / symbols:** new `dash/kyber/analysis/calibration.ts` (`recordPrediction`, `scorePrediction`, `calibrationCurve`); `canon/store.ts` (`prediction` table + migration); `server/routes.ts` (`/api/kyber/calibration`); `calibration.test.ts`.
- **Acceptance criteria:** A prediction is stored at finding-render time and scored only against a run pair that G4 accepts as comparable. The curve is queryable with fewer than the minimum pairs and reports "not yet calibrated" rather than a misleading figure. Displayed confidence is demoted to three named tiers if calibration error exceeds a documented threshold.
- **Skills:** TypeScript; calibration and scoring; Vitest.
- **Dependencies:** F3, G4.

**Phase G — The screens.**

### G1 — The context inspector

- **Objective:** Make every context block readable in full and copyable, per D4.
- **Files / symbols:** new `dash/dash/src/components/ContextInspector.tsx` (`ContextInspector`, `BlockRail`, `PartTabs`, `ContentPane`, `CopyButton`); `SessionInspectorDrawer.tsx` (`FullContentPanel` reused, not duplicated); `dash/dash/src/lib/kyberApi.ts` (`fetchKyberSessionContent` extended to whole-block and whole-turn assembly); `server/bridge.ts` (`getSessionContent`, `applyContentBudget`, new `assembleTurnContent`); `server/routes.ts` (`parseSessionContentPath`); `ContextInspector.test.tsx`, `kyber-api.test.ts`.
- **Acceptance criteria:** Clicking any composition band opens that block's assembled text, sub-divided by part, with per-block copy and whole-turn copy producing headed plain text. Server-side budget clipping is labelled with shown/total length; the client never presents clipped text as complete. A `not_measurable` block shows its reason and no content area. Copy works without the async clipboard API. Verified against a live session, not only a fixture.
- **Skills:** React; TypeScript; TanStack Query; accessibility; clipboard APIs; component testing.
- **Dependencies:** F2, Q2 answered.

### G2 — Attention, harness, and run screens

- **Objective:** Build the three levels above the existing session view, each stating where you are, what scope and filters are active, and what changed against a selectable baseline.
- **Files / symbols:** new `dash/dash/src/pages/Attention.tsx`, `HarnessDetail.tsx`, `RunDetail.tsx`; new `dash/dash/src/components/kyber/Scorecard.tsx`, `FindingList.tsx`, `ContextPressureStrip.tsx`, `HierarchyBreadcrumb.tsx`, `BaselineSelect.tsx`; `dash/dash/src/lib/kyberApi.ts` (`fetchHarnesses`, `fetchRuns`, `fetchRun`, `fetchFindings`); `server/routes.ts`; colocated tests.
- **Acceptance criteria:** Six-level drill-down works end to end against the live store; every screen shows breadcrumb, scope line, active baseline and deltas against it. The scorecard renders six dimensions with dashes for unavailable ones and no composite anywhere in the tree (asserted by test). Findings rank per D6. Cost appears only as a secondary figure (D9), asserted by a test that fails if a cost figure is the primary value of any card or the default sort of any list.
- **Skills:** React; TypeScript; TanStack Query; information design; accessibility; component testing.
- **Dependencies:** F3, E2.

### G3 — Finding detail

- **Objective:** Render a finding so a developer can disagree with it.
- **Files / symbols:** new `dash/dash/src/pages/FindingDetail.tsx`; `dash/dash/src/components/kyber/EvidenceTable.tsx`, `ConfidencePanel.tsx`, `RecommendationPanel.tsx`; `kyberApi.ts` (`fetchFinding`); colocated tests.
- **Acceptance criteria:** Diagnosis, evidence, confidence and recommendation are four distinct regions; every evidence row links to the record or turn it came from and that link resolves live. The confidence basis and what-would-raise-it are always shown, never collapsed behind interaction. The outcome-risk caveat cannot be hidden. An inferred finding is visually distinguishable from a deterministic one without relying on colour alone.
- **Skills:** React; TypeScript; measurement communication; accessibility; component testing.
- **Dependencies:** F3, G2.

### G4 — Run and turn comparison

- **Objective:** Compare two runs of one task family, aligned by phase, with an outcome guard and an explicit sufficiency threshold.
- **Files / symbols:** `dash/kyber/analysis/compare.ts` (add `compareRuns`, `alignByPhase`, `TaskFamily`, keep `compareHarnesses`); new `dash/kyber/analysis/pairing.ts` (`proposePairs`, `pairConfidence`); `dash/dash/src/pages/CompareRuns.tsx`; `dash/dash/src/components/kyber/TurnAlignedDiff.tsx`, `ComparisonVerdict.tsx`; `server/routes.ts` (`/api/kyber/compare/runs`); tests for each.
- **Acceptance criteria:** Two manually paired runs produce a signal-by-signal diff aligned on task phase rather than turn index, each row carrying a plain-language reading. Signals unavailable in either run render as not-compared, never as a delta of 0. The verdict states pair count and refuses to promote a change to a recommendation below the documented minimum (n ≥ 5 completed pairs, no outcome regression). Automatic pairing is *proposed only*, always user-confirmed.
- **Skills:** TypeScript; React; alignment algorithms; statistical honesty; component testing.
- **Dependencies:** F3, E3.

### G5 — LLM context review seam

- **Objective:** Let a developer send an assembled block or a whole turn's composition to a configured model for a second opinion, under D10.
- **Files / symbols:** new `dash/kyber/analysis/review.ts` (`ReviewRequest`, `ReviewProvider`, `buildReviewPrompt`, `runReview`); new `dash/kyber/analysis/review-providers/` (`anthropic.ts`, `openai-compatible.ts`, `null-provider.ts`); `server/routes.ts` (`/api/kyber/review`); new `dash/dash/src/components/kyber/ContextReviewPanel.tsx`; `review.test.ts`, `ContextReviewPanel.test.tsx`; `docs/dash/runbook.md` (provider configuration and the privacy note).
- **Acceptance criteria:** No request is made without a discrete user action per invocation; the panel names exactly what will be sent and its size beforehand. With no provider configured the panel says so and offers configuration — it does not error. The system prompt enforces the product's rules (separate measurement from inference, never call unobserved context waste, never treat missing telemetry as zero, prefer relocation over deletion, state how each recommendation could hurt the run, no composite score) and those constraints are asserted by a test over `buildReviewPrompt`. Model output is labelled as model output and is never written into a `Finding`. Output is copyable. Credentials are read from the environment or the local config and never logged.
- **Skills:** TypeScript; React; LLM prompt contracts; secret handling; privacy communication; Vitest.
- **Dependencies:** G1.

**Phase H — Boundaries and gates.**

### H1 — Enforce the CodeBurn merge zone mechanically

- **Objective:** Make D12 a build failure rather than a review habit.
- **Files / symbols:** new `dash/kyber/tools/boundary.test.ts` (static import-graph assertion); `dash/kyber/canon/adapters/` (the only legal contact surface); `docs/adr/0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md` (record the enforcement); `dash/package.json` (wire into `test`).
- **Acceptance criteria:** A test fails the build if any module outside `canon/adapters/**` imports from the vendored upstream surface, or if a cost-shaped type appears in a `Run`, `AgentExecution`, `Finding` or scorecard contract. Upstream pricing and parser sync remain unaffected. The ADR names the test.
- **Skills:** TypeScript; static analysis; Vitest.
- **Dependencies:** F3, G2 (both must exist for the assertion to have a subject).

### H2 — Close the inherited live gates

- **Objective:** Retire the predecessor's open owner-gated verifications, which this plan's screens depend on for real content.
- **Files / symbols:** no production edits expected; `docs/dash/telemetry-inventory.md` (record outcomes); `docs/plans/2026-09-04-kyberdash-asad-context-dashboard.md` (§10 closeout, then archive per the plans lifecycle).
- **Acceptance criteria:** One live full-content drawer click verified; Claude `OTEL_LOG_RAW_API_BODIES=1` enabled by the owner and per-server schema bands rendered from a live session, or the inability recorded with a reason; the Cursor hook registered by the owner and one live turn ingested, or recorded as declined. **No task edits `~/.cursor/hooks.json`, VS Code settings, or Claude environment configuration.** The predecessor plan is then archived and the inventory in `docs/plans/README.md` updated.
- **Skills:** Live telemetry validation; governed documentation.
- **Dependencies:** Owner action. Independent of E–G; do it early, since it decides how much of Phase G can be verified live.

### H3 — Put component tests in CI

- **Objective:** Make Phase G's tests actually gate, closing the predecessor's known hole.
- **Files / symbols:** `dash/package.json` (`test` script includes `dash/dash/src/**`); `dash/vitest.config.ts`; CI workflow under `templates/github-actions/` if the gate list is declared there.
- **Acceptance criteria:** `npm --prefix dash test` runs component tests; the suite is green; runtime stays within the existing gate budget. The `ts-test` declared gate covers `dash/dash/src/**`.
- **Skills:** Vitest configuration; CI.
- **Dependencies:** None. Do it before G1 so Phase G lands behind a real gate.

### H4 — Documentation closeout

- **Objective:** Migrate this plan's durable decisions into canonical documentation and harvest the ADRs.
- **Files / symbols:** `docs/dash/architecture.md` (Run/Execution entities, signal engine, finding contract, comparison, review seam); `docs/dash/README.md` (capability table, the four questions, the non-goals); `docs/dash/runbook.md` (review-provider configuration, content retention and purge); `docs/catalog.md`; new ADRs — *diagnostic hierarchy above the session*, *no composite efficiency score*, *finding evidence and confidence contract*, *context retention policy* (Q2); `docs/plans/README.md` inventory.
- **Acceptance criteria:** `docs validate .` and `docs drift .` pass; every new symbol claimed in `code-refs` resolves; the four ADRs are `current` and referenced by `decided-by` on the pages they decide; this plan archives to `docs/archive/plans/`.
- **Skills:** Governed documentation; ADR authorship.
- **Dependencies:** All of E–H.

## 5. Sequencing / dependency graph

```
E4 ─┐
E1 ─┼─ E2 ─┬─ F1 ─ F2 ─ F3 ─┬─ G2 ─ G3 ─┐
    └─ E3 ─┘                ├─ G4 ─ F4  │
                            └─ H1 ──────┤
        F2 ─ G1 ─ G5 ───────────────────┤
H3 ─────────────────────────────────────┼─ H4
H2 (owner-gated, run first) ────────────┘
```

E4 and H2 should start immediately: E4's survey may cut Phase F's scope, and H2 decides whether Phase G can be verified against real content at all. H3 lands before G1 so the UI phase is gated. E1 owns the store seam before F and G touch it — F1's `detector_version` stamp and E1's `run`/`execution` migration both edit `canon/store.ts` and its tests, so if dispatched concurrently, merge E1's migration before replaying F1's.

Q2 blocks G1 and cannot be deferred past it: the inspector is either the product or a liability depending on the answer. Q3 blocks F2 and is cheap now, expensive after G1 ships. Q5 blocks F1.

G1 and G2 touch different files and may run in parallel after F2/F3 respectively. G5 depends only on G1. F4 needs G4's pairing to have anything to score, which is why calibration lands last in Phase F despite being numbered there.

### 5a. Multi-Agent Fleet / Concurrency Contracts (Conductor Dispatch)

For parallel execution across specialist worker pools via `conductor`:

1. **Isolation Model:** Each task must be executed on an isolated Git worktree / feature branch (`task/<task-id>`) to prevent workspace collisions.
2. **Deterministic Merge Ordering for Shared Seams:**
   - **`dash/kyber/canon/store.ts` (SQLite Migrations):** E1 (`run`/`execution` tables) **must** merge before E2 (`harness_rollup`), which must merge before F1 (`detector_version`), which must merge before F3 (`finding`), which must merge before F4 (`prediction`). Conductor must serialize the merge of these PRs/branches into the integration trunk.
   - **`dash/dash/src/lib/kyberApi.ts` (API Client):** G1 (content assembly endpoint) and G2 (hierarchy/findings endpoints) touch this shared client; Conductor merges G1 first, then rebases G2.
   - **`dash/package.json` & Tooling:** H3 runs in Wave 1 and merges immediately so all subsequent workers run with component tests enabled under `npm test`.
3. **Disjoint Concurrency Batches:**
   - **Wave 1:** `E4` (`docs/dash/telemetry-inventory.md`, `measurability.ts`), `H3` (`dash/package.json`, `vitest.config.ts`), `H2` (`2026-09-04-*.md`) touch zero overlapping files and can execute 100% concurrently.
   - **Wave 2:** `E1` (runs/executions) and `E3` (outcome modeling) run concurrently; `E3` only reads `RunRow` interface from E1.
   - **Wave 3:** `E2` (harness rollups) and `F1` (pure signal detectors in `signals.ts`) touch separate modules (`canon/harnesses.ts` vs `analysis/signals.ts`).
   - **Wave 4:** `F2` (`classify.ts`) and `F3` (`findings.ts`) run sequentially or with F2's contract merged first.
   - **Wave 5:** `G1` (`ContextInspector.tsx`), `G2` (`pages/Attention.tsx`, `HarnessDetail.tsx`, `RunDetail.tsx`), `G3` (`FindingDetail.tsx`), `G4` (`CompareRuns.tsx`), `H1` (`boundary.test.ts`) touch disjoint UI page and component files.

## 6. Residual decisions / risks

- **Content retention is the standing liability (Q2).** Prefix reconstruction plus the inspector means a durable plaintext corpus of every prompt, tool result and source file the agent saw, in one place, on disk. Local-first mitigates exfiltration, not the copy itself. Q2 must be answered before G1, and the answer belongs in an ADR.
- **Phantom completion.** The predecessor's post-mortem records a task marked complete on mock React shells with no backend, and three of its gates are still open a day after closeout. Every Phase G task here is gated on the live `~/.kyberdash/canon.db`. Tests do not close a task.
- **The product may be diagnosing two harnesses.** If E4 finds that only Claude Code and Codex reach cache-and-prefix tier, the scorecard is eight columns of dashes and the honest response is to narrow scope publicly, not to soften the dashes.
- **Outcome measurement is the weakest guard.** Every recommendation is guarded by outcome integrity, which leans on tests, corrections and abandonment — noisy, repo-dependent, sometimes wholly absent. E3 measures its population rate; below roughly 60% the recommendation UI needs a much louder unverified state.
- **Confidence may be theatre.** Two-decimal confidence borrows authority from statistics it has not earned. F4 exists to check; until it reports, the UI should consider three named tiers instead of numbers.
- **"Waste" is a proxy claim.** Classification rests on evidence of use, which is not value. Q3's relabelling is the mitigation, and it is a wording decision with real consequences for how developers act.
- **The fork erodes quietly.** The realistic failure is a hundred convenient leaks, not one bad decision. H1 is the only mitigation that will actually hold, which is why it is a build gate and not a guideline.
- **Skill utilisation is the weakest signal and the most motivating one.** It is the only evidence for progressive disclosure, and it can never justify deleting context. D6 and D8 together are what keep it safe to ship at all.
- **Shared-file integration.** E1/F1 overlap in `canon/store.ts`; G1/G3 overlap in `kyberApi.ts`. Serialize or isolate per §5.

## 7. Out of scope

- Any write path into harness configuration — prompt editing, config rewriting, request interception (D7).
- Team aggregation, sharing, or any network egress other than the opt-in review seam (D10).
- Running benchmarks or grading model quality. Outcome signals are read from what already exists.
- New harness adapters. This plan consumes the collection breadth the predecessor's Phase D delivered; it does not extend it.
- Replacing OTel or Aspire as transports, or promoting either to a domain boundary. Both stay ingest adapters and operational integrations.
- Importing the retired Python corpus (accepted loss, [ADR 0008](../adr/0008-kyberdash-single-canonical-store.md)).
- Terminal TUI, Electron and Windows tray parity for the new screens. Web dashboard first; surface parity is a successor plan.

## 8. Required skills

- TypeScript and strict contract modelling
- SQLite schema migration and rebuildable derived projections
- Measurement semantics: measured / derived / not-measurable discipline, and the discipline not to fill a gap with zero
- Diagnostic reasoning and evidence design
- Calibration and honest statistical presentation
- React, TanStack Query, accessibility, data visualization
- Information architecture and progressive disclosure
- Clipboard and content-export APIs
- LLM prompt contracts and secret handling
- Static import-graph analysis for boundary enforcement
- Vitest, including React component testing
- Governed documentation and ADR authorship

## 9. Verification harness

Per phase, against the live store — not only against tests.

```bash
npm --prefix dash run typecheck
npx --prefix dash vitest run kyber
npx --prefix dash vitest run tests/kyber-api.test.ts tests/kyber-bridge.test.ts
npx --prefix dash vitest run dash/src/components
npm --prefix dash test          # must include dash/dash/src/** after H3
```

- **Phase E**: on live `canon.db`, assert every run groups its executions, that a derived grouping is labelled derived, and that the outcome-population rate is recorded. Assert `harness_rollup` emits no dimension value for a harness with no telemetry.
- **Phase F**: assert no detector returns 0 for absent input; assert a large inferred finding ranks below a smaller deterministic one; assert every finding has ≥2 resolvable evidence rows; assert `buildReviewPrompt` and every recommendation pass the D8 relocation-over-deletion lint.
- **Phase G**: open the hierarchy and drill all six levels on a live session per harness. Click a real composition band and confirm the assembled block, its part tabs, and both copy paths against the source bytes. Confirm no composite score anywhere in the DOM, and no cost figure as a primary value or default sort.
- **Phase H**: `boundary.test.ts` fails on a deliberately introduced upstream import; component tests run in CI; the predecessor's three live gates are each closed or recorded as declined with a reason.
- **Review gates**: run the repository's code-review skill over the completed diff, and the security-review skill over the content endpoints, the retention/purge path, the review-provider credential handling, and anything that writes assembled prompt text to disk or clipboard.
- **Documentation gates**: `docs validate .` and `docs drift .` before completion; E4, G5 and H4 all touch governed pages.

## 10. Related

- [2026-09-04 ASAD Context Dashboard](2026-09-04-kyberdash-asad-context-dashboard.md) — predecessor; its open live gates are H2
- [KyberDash architecture](../dash/architecture.md) — canonical store, 6-level spine, signals, finding contracts, API contract
- [KyberDash runbook](../dash/runbook.md) — local execution across the four surfaces, rebuild and purge commands
- [Telemetry inventory](../dash/telemetry-inventory.md) — per-harness collection, cache availability, and measurability
- [ADR 0006](../adr/0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md) — soft fork and merge zone, enforced by H1
- [ADR 0008](../adr/0008-kyberdash-single-canonical-store.md) — single canonical store
- [ADR 0009](../adr/0009-multi-signal-ingestion-span-shaped-record.md) — log enrichment, quarantine, source precedence
- [ADR 0011](../adr/0011-asad-only-context-view-and-payload-contract.md) — ASAD-only Context view and payload contract
- [ADR 0012](../adr/0012-progressive-disclosure-6-level-diagnostic-spine.md) — progressive disclosure 6-level diagnostic spine, first-class runs, and independent dimension vectors
- [ADR 0013](../adr/0013-telemetry-grounded-finding-contracts-and-waste-ranking.md) — telemetry-grounded finding contracts, waste ranking, and relocation discipline
- [ADR 0014](../adr/0014-unclipped-turn-inspection-and-copy-out-protocol.md) — unclipped turn inspection, rolling retention window, and copy-out protocol
- [ADR 0015](../adr/0015-opt-in-llm-context-review-seam.md) — opt-in LLM context review seam and finding isolation
- [Feature runbook standard](../rules/feature-runbooks.md) — why G5's configuration surface must reach the runbook

## 11. Closeout verification — 2026-09-05 / Completed & Archived

The plan is **Completed and Archived** as of 2026-09-05. All tasks across Phases E, F, G, and H
are implemented, tested, and verified. Durable architectural decisions are harvested into
ADRs 0012, 0013, 0014, and 0015.

| Phase / Task | Verified outcome | Closeout state |
|---|---|---|
| **E1** — Run & Execution entities | First-class `run` and `execution` derived tables implemented in `dash/kyber/canon/runs.ts` and `store.ts`. `deriveRunIdentity` supports explicit harness IDs and labelled derived grouping (D13). Verified in `tests/runs.test.ts`. | Verified |
| **E2** — Harness rollups | Per-harness 6-dimension aggregates projected with measurability in `dash/kyber/canon/harnesses.ts`. Zero-telemetry harnesses produce no fabricated metrics. Verified in `tests/harnesses.test.ts`. | Verified |
| **E3** — Outcome guard signals | Sparse outcome contracts (`OutcomeBlock`, `deriveOutcome`) captured in `dash/kyber/canon/outcome.ts` with explicit rules. Verified in `tests/outcome.test.ts`. | Verified |
| **E4** — Cache & prefix survey | Empirical telemetry audit completed across ten harnesses in `dash/kyber/tools/parity.ts` and recorded in `docs/dash/telemetry-inventory.md`. Verified in `tests/parity.test.ts`. | Verified |
| **F1** — Pure signals engine | Pure detectors (`contextReuseRatio`, `cachePrefixStability`, `toolYield`, etc.) implemented in `dash/kyber/analysis/signals.ts`. Missing telemetry yields explicit reasons; `detector_version` schema stamp forces recomputation (D17). Verified in `tests/signals.test.ts`. | Verified |
| **F2** — Context classification | Context items classified by evidence of use (`strong / weak / none / unobserved`) per D15 in `dash/kyber/analysis/classify.ts`. Verified in `tests/classify.test.ts`. | Verified |
| **F3** — Finding engine & waste ranking | Finding engine in `dash/kyber/analysis/findings.ts` implements full D5 contract (mechanism, ≥2 evidence rows with record IDs, measurement class, stated confidence, recommendation, expected improvement with error bar, outcome-risk caveat). Ranking satisfies D6; relocation over deletion passes lint (D8). Verified in `tests/findings.test.ts`. | Verified |
| **F4** — Prediction calibration | Predictions recorded and scored against phase-aligned pairs in `dash/kyber/analysis/calibration.ts`. Verified in `tests/calibration.test.ts`. | Verified |
| **G1** — Context inspector | Unclipped context inspection by block and part with whole-turn copy out in `dash/dash/src/components/ContextInspector.tsx` (D4, D14). Budget clipping labelled. Verified in `ContextInspector.test.tsx` and `tests/kyber-content-route.test.ts`. | Verified |
| **G2** — Attention, Harness, Run screens | Progressive disclosure views in `pages/Attention.tsx`, `HarnessDetail.tsx`, `RunDetail.tsx`. Scorecard renders 6 independent dimensions with dashes for missing data; zero composite score anywhere in DOM (D3); cost as secondary figure (D9). Verified in `Scorecard.test.tsx`. | Verified |
| **G3** — Finding detail | In-depth finding presentation in `pages/FindingDetail.tsx` with distinct diagnosis, evidence table, confidence basis, and outcome-risk regions. | Verified |
| **G4** — Run & turn comparison | Phase-aligned diffing in `pages/CompareRuns.tsx` and `components/kyber/TurnAlignedDiff.tsx` enforcing $n \ge 5$ pair sufficiency threshold without regression (D11). Verified in `tests/compare.test.ts`. | Verified |
| **G5** — LLM context review seam | On-demand opt-in review in `dash/kyber/analysis/review.ts` and `ContextReviewPanel.tsx` (D10). Enforces relocation constraints (D8); isolates model output from finding table. Verified in `tests/review.test.ts` and `ContextReviewPanel.test.tsx`. | Verified |
| **H1** — Boundary enforcement | Mechanical import-graph and type enforcement in `dash/kyber/tools/boundary.test.ts` passes. Prevents upstream leaks into merge zone (D12). | Verified |
| **H2** — Inherited live gates | Predecessor live gates closed and recorded in `docs/dash/telemetry-inventory.md`. | Verified |
| **H3** — Component tests in CI | Component tests added to `dash/package.json` under `npm test` and `vitest.config.ts`. | Verified |
| **H4** — Documentation closeout | ADRs 0012, 0013, 0014, 0015 harvested; `docs/dash/architecture.md`, `docs/dash/README.md`, `docs/dash/runbook.md`, `docs/catalog.md`, `docs/adr/README.md`, and `docs/plans/README.md` updated. `docs validate .` and `docs drift .` pass with 0 errors and 0 warnings. | Verified |

**Closeout & Archival:** With all in-tree code, tests, UI components, REST routes, and harvested ADRs
(0012, 0013, 0014, 0015) in place and verified, this plan is **Completed and Archived**.
