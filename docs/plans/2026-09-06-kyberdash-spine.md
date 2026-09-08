---
id: plans/2026-09-06-kyberdash-spine
title: KyberDash Diagnostic Spine — State, Work, Dispatch
doc-type: plan
status: needs-review
owner: dpalfery
last-reviewed: 2026-09-06
component: KyberDash
---

# KyberDash Diagnostic Spine — State, Work, Dispatch

**Status:** Needs review — the Phase A browser spine gates are green; Phase B–D remain active work.
**Date:** 2026-09-06
**Goal:** Get the six-level diagnostic hierarchy into the running dashboard without burning another ten agent-hours on unreachable components.

**Execution checkpoint (2026-09-06):** A real local host passed G1–G7 (7/7): the
dashboard lands on Attention, drills through the spine, retains one harness selector,
and renders unavailable metrics and empty charts honestly. T7 resolved Q1 below. This
is not plan completion: T5's dedicated numerals gate and T6's required artifact report
are not evidenced here; Phase B–D are deliberately unassigned; and Q2–Q7 remain open.

**Harness-filter regression checkpoint (2026-09-06):** The Attention selector had
display labels that did not match the canonical storage identifiers, so a selected
harness could yield no runs despite appearing in the All Harnesses view. Follow-up
browser gate G4a now verifies the one selector exposes the canonical Claude Code,
GitHub Copilot, and Gemini choices, and proves a Claude Code selection receives a
successful `runs?harness=claude-code` response and drills into a run. This is a Phase A
repair, not plan completion.

Consolidates and replaces three earlier drafts (the 09-05 diagnostic-hierarchy plan, the 09-06 spine-structure plan, the 09-06 execution playbook). Interaction reference: `KyberDash.dc.html` in the design project — read its structure as a spec.

Task prompts: [`tasks/`](tasks/). Gate: [`tasks/spine.spec.ts`](tasks/spine.spec.ts).

---

## 1. Where we are

Twenty-plus components exist. Five pages have no importer outside a test file. The product a developer can open is a session browser with a metric grid — the CodeBurn shape with new labels.

Evidence is one screenshot of `127.0.0.1:4747`, Context tab, Antigravity, session `rd-pi`. Every claim below is visible in it.

1. **The spine is not in the product.** Nav reads `Usage · Context · Compare · Quarantine · Problems`. `Attention`, `HarnessDetail`, `RunDetail`, `FindingDetail`, `CompareRuns` exist in `pages/` and are imported only by `Scorecard.test.tsx`, `kyber-views.test.tsx`, `FindingDetail.test.tsx`. *(verified)*
2. **What ships is a two-level session browser.** A flat list of sessions (`da5d8015`, `rd-pi`) → expand → inline dashboard. The canonical entity on screen is the session, which is precisely the unit that is *not* what a developer recognises. `Run` and `AgentExecution` are unrepresented. A row reading "5 turns" does not open a turn. *(verified)*
3. **Navigation is duplicated.** The shell renders a `HARNESS:` strip of eleven choices; the Context page renders the same eleven again 100px below. Both driven by the same `provider` state. *(verified)*
4. **Absent telemetry renders as zero, in the largest type on the page.** `TOTAL INPUT 0`, `CACHE READ 0`, `CACHE CREATION 0`, `TOTAL OUTPUT 0`, `TOOL CALLS 0` — for a session simultaneously reporting `SPANS 5`, `TURNS 5`, `REQUESTS 5`, `DURATION 2.5s`. Five requests did not consume zero tokens. The honest treatment sits three cells away (`COST —`, `CACHE HIT RATIO —`, `TOOLS OFFERED — / "not exported by gemini"`), so the mechanism works and is simply not applied to the token counters. *(verified)*
5. **No diagnostic content at all.** Twelve counters, one chart. No finding, scorecard, evidence, recommendation, confidence or context composition. The four questions in the product thesis go unanswered by the only screen that ships. *(verified)*
6. **The largest element is an empty chart.** "Token Spend per Turn" plots a 0–1 axis, a flat zero line, x-labels `0 0 / 1 0 / 2 0`, five empty series, above a note admitting cache-creation input was not recorded. *(verified)*
7. **The rail is spend chrome on a diagnostic page** — `SHARE / Share this device` and ~900px of empty space. *(verified)*
8. **Rendering artifacts at the left edge**, x < 20px, full height, outside the shell. *(verified)*
9. **`Attention.tsx` fabricates data.** `DEFAULT_SURVEYED_HARNESSES` hardcodes six harnesses with invented `fieldCoverage` (0.83 / 0.33 / 0.67 / 0.5) and `sampleCount: 0`, rendered through the same cells as live rollups. The list also disagrees with `App.tsx`'s eleven-entry `HARNESS_TABS`. *(verified)*
10. **The UI cites its own spec at users** — "Independent Vectors (D3)", "Ranked by Decision D6 formula", "(Decision D16)", plus a compliance footer. *(verified)*

Two things that *did* land and are worth protecting: `ContextInspector` is wired into `SessionInspectorDrawer` and works — right content, wrong level. And the dash's token layer is complete (`index.css` defines both surfaces, `@theme inline` re-exports every colour, `--font-mono` exists), so the styling phase needs no new stylesheet.

## 2. Why the last two windows produced nothing

Not an agent-quality problem. A task-shape problem, and the shape was mine.

The 09-05 plan's tasks listed **files and symbols**, with acceptance criteria like *"the component renders six dimensions with dashes for unavailable ones"*. An agent optimising for "all criteria met" satisfies that literally: write `Scorecard.tsx`, write `Scorecard.test.tsx`, run it, green, done. Every criterion was met. Nothing was reachable.

Three specific defects:

- **The gate was a test renderer, not a browser.** `renderToString(<Attention/>)` passes whether or not `App.tsx` imports `Attention`.
- **Tasks were sliced by component, not by path.** "Build Scorecard", "build FindingList", "build RunDetail" are horizontal slices; none individually produces something a user can see, so none individually fails when the wiring is missing. The wiring was nobody's task.
- **Criteria described existence, not behaviour.** Checkable against a fixture, not against a running app.

The 09-05 plan's own risk register named this failure mode — "phantom completion" — and shipped into it anyway.

## 3. Decisions

- **D1 — A task is done when a headless browser, driving the real dev server against the real store, observes the change.** Not when a test passes. Not when a file exists. No exceptions.
- **D2 — Slice vertically.** The first slice is one harness → one run → one turn, *reachable*, using whatever components exist, however ugly. Every page may be `<pre>{JSON.stringify(data)}</pre>`.
- **D3 — Context is replaced, not extended.** A session list cannot be relabelled into `Run` and `AgentExecution`.
- **D4 — `Attention` is the landing surface.** Usage survives as a tab; it does not open the app. If the first thing a developer sees is a spend grid, the product is CodeBurn.
- **D5 — One harness selector, in the shell.** It becomes the harness level of the spine.
- **D6 — A missing counter renders as an em-dash with a reason, never `0`.** Enforced at the formatter so no future card can opt out. A *genuine* zero still renders `0`, and that distinction is asserted.
- **D7 — A chart with no data does not render.** One line naming what was not recorded.
- **D8 — Every level answers the four questions or does not ship.** Where context went, whether it was necessary, what caused it, what to change. A screen of counters answers none.
- **D9 — Fabricated data never renders as measurement.**
- **D10 — Decision ids never appear in product UI.** Traceability lives here, in `docs/`.
- **D11 — No composite efficiency score, ever.** Six independent dimensions; a dimension with no telemetry is a dash, never a zero and never a passing grade.
- **D12 — Recommend, do not act.** No write path into any harness configuration without its own ADR.
- **D13 — Recommendations prefer relocating context to deleting it.** Cache placement, progressive disclosure, on-demand tools — reversible, and they do not assume unobserved context was useless.

## 4. Open questions

| Q# | Question | Recommended | Blocks | Status |
|---|---|---|---|---|
| Q1 | Are Antigravity's token counts absent, emitted-and-dropped, or emitted under unrecognised names? | Resolved: affected short-session aggregate counters are producer-emitted zeroes that survive to `canon.db`; no reader mapping fix is indicated. See the [T7 inventory record](../dash/telemetry-inventory.md#antigravity-aggregate-token-investigation). | T2, T4 | RESOLVED |
| Q2 | What happens to the Context tab? | (a) delete — composition and inspector become the Turn level. (c) keep, rename "Sessions", demote, is the hedge if most of the corpus has no derivable run identity | T8 | OPEN |
| Q3 | Does the index store context **content**, or hashes plus offsets? | Content with a 14-day retention and an explicit purge. Content is the inspector; an unbounded plaintext corpus of every prompt is a standing liability. Needs an ADR | the inspector's future | OPEN |
| Q4 | How is a `Run` boundary derived when the harness emits no run identity? | Require explicit run id, with working-dir + time-gap clustering offered as a *labelled derived* grouping the user accepts per harness — never silently | E1 | OPEN |
| Q5 | Where do Compare / Quarantine / Problems live? | Compare folds into the spine; Quarantine and Problems become a Health area | T8 | OPEN |
| Q6 | Does the sidebar become the spine rail? | Yes. Share moves to Usage | T8 | OPEN |
| Q7 | Adopt codeburn-desktop for pixel fidelity, or map the prototype onto the dash's Tailwind tokens? | Map onto Tailwind. Two stylesheet idioms in one binary is worse than an imperfect palette match | Phase C | OPEN |

## 5. The gate

```bash
npm --prefix dash install -D @playwright/test
npx --prefix dash playwright install chromium
cp docs/plans/tasks/spine.spec.ts dash/e2e/spine.spec.ts
npm --prefix dash run dev &
npx --prefix dash playwright test e2e/spine.spec.ts
```

G1–G7 plus G4a run against the real dev server and the real `~/.kyberdash/canon.db`. Per the 2026-09-06 checkpoints above, a real local host passed G1–G7 (7/7) and the G4a harness-filter repair. **Current gate status is pass, not fail.** These gates specify Phase A live behaviour; they do not close the plan. T5's dedicated numerals gate and T6's required artifact report are not evidenced here; Phases B–D remain unassigned; Q2–Q7 remain open. That is why this plan stays Needs review and unarchived.

| Gate | Asserts |
|---|---|
| G1 | app opens on the attention level |
| G2 | a developer can drill all six levels by clicking, and back out |
| G3 | no unreported counter renders as zero |
| G4 | exactly one harness selector on screen |
| G4a | the selector uses canonical harness identifiers; selecting Claude Code loads its matching runs and drills into a run |
| G5 | no decision ids in product copy |
| G6 | empty datasets do not render as charts |
| G7 | nothing lays out outside the shell |

Every task prompt names its gate. An agent that reports success while its gate fails is wrong, provably, in one command.

## 6. Phase A — assigned now

Prompts are in [`tasks/`](tasks/). Order matters more than the prompts.

```
T7  investigate Q1        strong model, ~30 min, report-only, blocks T2 and T4
T1 T2 T3 T4               four Haiku agents IN PARALLEL        → G3 G5 G6 green
T6  artifact hunt         Haiku, report-only first             → G7 green
T8  walking skeleton      ONE strong agent, ONE window         → G1 G2 G4 green
T5  mono numerals         Haiku, AFTER T8 merges, runs alone
```

| Task | What | Model |
|---|---|---|
| T7 | Are Antigravity's token counts absent or dropped? Investigation only | strong |
| T1 | Delete decision-id narration from spine components | Haiku |
| T2 | Make `0` unrepresentable for an unreported counter | Haiku |
| T3 | Delete the fabricated harness catalogue + Attention's narration | Haiku |
| T4 | A chart with no data is not a chart | Haiku |
| T6 | Find what renders outside the shell | Haiku |
| T8 | The walking skeleton — make the hierarchy clickable | strong |
| T5 | Every number is mono and tabular | Haiku |

**T1–T4 run before T8, not after.** They only delete, so they shrink the surface T8 must reason about and cannot collide with a reducer that does not exist yet.

**Where a small fast model helps, and where it costs you.** T1–T6 are mechanical, bounded and independently verifiable — grep-and-delete, a formatter signature, a `data-empty` guard. Haiku is the right tool. T8 replaces `useState<KyberPage>` with a location reducer inside 1,030 lines of `App.tsx` where device, share, period, provider and query-invalidation state are tangled together; a wrong call there is re-done later. One strong agent, one window, no parallelism. T7 is an empirical question whose answer changes what T2 even is.

**File ownership is assigned per window, not per feature** — the ownership table is in [`tasks/README.md`](tasks/README.md). `Scorecard.tsx` is wanted by two tasks and `App.tsx` by three; that collision is how parallel agents produce merge garbage.

Steps T7 through T8 are roughly one day and produce the thing that has been missing for ten hours: a hierarchy a developer can click.

## 7. Phase B — the diagnostic content

Not assigned. The spine is navigable but empty until findings appear on it. Depends on Phase A's T8.

- **B1 — Findings on Attention and HarnessDetail.** The landing page's first content block is ranked findings, not counters. Each row states measurement class, confidence, outcome risk and estimated recoverable waste without expanding. With no findings, one line — no padding.
- **B2 — Cross-harness scorecard matrix.** One dense row per harness with six inline meters, replacing `Attention`'s bespoke four-column table. Eleven harnesses × six dimensions on one screen at 1280px. An unmeasurable dimension is a dashed track and an em-dash, never an empty bar reading as zero.
- **B3 — Rehome the context inspector under Turn.** It currently hangs off a session drawer. Moving it touches the content endpoint's budget and clipping contract — get that wrong and clipped text presents as complete.
- **B4 — Run comparison.** Two runs of one task family, aligned by phase rather than turn index, with an outcome guard. No change is promoted to a recommendation below n ≥ 5 completed pairs with no outcome regression.

## 8. Phase C — look and feel

Not assigned. Only after A and B. Palette fidelity is explicitly not the goal; **density is**.

- **C1 — Density and numeric pass.** `font-mono tabular-nums` on every figure (T5 starts this). Named size tokens instead of `text-[10.5px]` literals. Semantic state colours instead of `amber-500` / `emerald-500`. Six stable model-series tokens — `--chart-1..10` are ramp slots, not model identity, so model colour is currently inconsistent between any two charts. A `Panel` wrapper and a `Chip` component matching the prototype's anatomy.
- **C2 — Parity review.** Six levels captured from the running dash against a live store, each with a verdict: matches / accepted divergence with a reason / defect with a task id. Palette, shell chrome and font divergences from Q7 are *accepted*, not defects.

## 9. Phase D — the backend nobody has been assigned

This is the part most at risk of being forgotten, because Phase A makes screens look finished while these entities do not exist. Without D1 and D2, `RunDetail` and the execution level have nothing real to render.

- **D1 — Derive `Run` and `AgentExecution` as canonical entities.** Rebuildable derived tables over `records`, like `session`. Parent/child linkage where the harness emits it, explicitly `not_measurable` where it does not. Grouping basis recorded per row (Q4). `dash/kyber/canon/runs.ts`, `store.ts` migration, `sessions.ts`.
- **D2 — Harness rollups.** Run count, median and p95 peak context pressure, cache hit rate, tool yield, delegation overhead, outcome mix, field coverage — each carrying its own measurability. A harness with zero collectable runs appears with a reason and no computed dimensions.
- **D3 — Outcome as a guard signal.** Termination reason, test result delta, user-correction count, abandonment, repeated attempts. Sparsely populated by nature. **Measure the population rate across the first 200 real runs** — below ~60%, every recommendation needs a much louder unverified state.
- **D4 — Survey cache and prefix availability per harness.** The precondition for the two highest-yield signals, and unsurveyed. Do this early: if only Claude Code and Codex reach cache-and-prefix tier, the scorecard is nine columns of dashes and the honest response is to narrow scope publicly.
- **D5 — The signal engine.** Context reuse ratio, cache prefix stability, tool yield, duplicate call rate, oversized result share, compaction pressure, delegation overhead, skill utilisation. Each declares numerator, denominator, unobservability rule and failure mode. **No detector returns 0 for absent input.** Detector version stamped so a changed detector forces recompute.
- **D6 — The finding contract.** Mechanism, ≥2 evidence rows each resolving to a record id, measurement class, confidence with a written basis and a what-would-raise-it, recommendation, expected improvement with an error bar, outcome-risk caveat. Ranked by recoverable waste × outcome risk × confidence — an inferred finding never outranks a comparable deterministic one.
- **D7 — Prediction logging and calibration.** Log every prediction with its stated error bar; score it against an accepted run pair. Confidence is currently displayed to two decimals and is uncalibrated — that is theatre until D7 reports. If calibration error exceeds threshold, demote to three named tiers.
- **D8 — Enforce the CodeBurn merge zone mechanically.** A build failure if any module outside `canon/adapters/**` imports the vendored upstream surface, or if a cost-shaped type appears in a `Run`, `AgentExecution`, `Finding` or scorecard contract. Anti-corruption layers erode through a hundred convenient leaks, not one bad decision; a static test is the only mitigation that holds.

## 10. Risks

- **Phantom completion, twice now.** Every task names the browser click path that closes it. `Scorecard.test.tsx` passes today while the page it tests is unreachable. Ask a completion report *what did you open, and what did you click* — not *did the tests pass*.
- **Q1 may be an adapter bug.** If Antigravity emits token counts and ingest drops them, T2 is still correct but insufficient, and the missing-data story across all eleven harnesses needs re-surveying.
- **Deleting Context may delete the only working screen.** It is the one place real data renders. If most of the corpus has no derivable run identity, Q2(a) strands users; Q2(c) is the hedge.
- **The product may be diagnosing two harnesses.** D4 decides. Nine columns of dashes is the honest output, and softening the dashes is the wrong response.
- **Outcome measurement is the weakest guard.** Every recommendation leans on it, and it is noisy, repo-dependent and sometimes wholly absent. A guard that fails silently is worse than no guard.
- **Content retention is the standing liability.** Prefix reconstruction plus the inspector means a durable plaintext corpus of every prompt, tool result and source file the agent saw, in one place. Local-first mitigates exfiltration, not the copy. Q3 needs an ADR before the inspector ships broadly.
- **"Waste" is a proxy claim.** Classification rests on evidence of use, which is not value. The prototype labels blocks `necessary / questionable / avoidable`; `evidence of use: strong / weak / none` is the more honest framing and is cheap to change now, expensive later.
- **The landing page's honest state is sparse.** With rollups thin, Attention will mostly say "unconnected, here's why". Correct, and it will look empty. The temptation to fill it with counters is exactly how the current screen happened.
- **The nav stack will want to be a router.** The first request for a shareable link makes `SpineLocation` a URL. Building one now is premature; building a shape that cannot become one would be a mistake.

## 11. Out of scope

- Any write path into harness configuration — prompt editing, config rewriting, request interception.
- Team aggregation, sharing, or network egress beyond the opt-in LLM review seam.
- URL routing, deep links, shareable views.
- Running benchmarks or grading model quality. Outcome signals are read from what already exists.
- New harness adapters. This consumes existing collection breadth.
- Replacing OTel or Aspire as transports, or promoting either to a domain boundary.
- Electron shell, TUI and menubar parity. Web dashboard first.

## 12. Verification

```bash
npm --prefix dash run typecheck
npm --prefix dash run dev            # then drill all six levels by hand
npx --prefix dash playwright test e2e/spine.spec.ts
npx --prefix dash vitest run kyber
npm --prefix dash test               # confirm dash/dash/src/** is included
```

Whether component tests gate in CI is unconfirmed — `dash/package.json`'s `test` script historically excluded `dash/dash/src/**`. Check before trusting any unit-test gate.

- **Phase A**: all seven Playwright gates green against a live store. Usage, Quarantine and Problems still render.
- **Phase B**: landing page's first block is ranked findings; a large inferred finding ranks below a smaller deterministic one; copied context text diffs clean against source bytes.
- **Phase C**: every numeric cell mono and tabular; no raw Tailwind palette colour in the spine directory; contrast ≥ 4.5:1 body and ≥ 3:1 headline in both themes.
- **Phase D**: no detector returns 0 for absent input; every finding has ≥2 resolvable evidence rows; the boundary test fails on a deliberately introduced upstream import.
- **Review gates**: the repository's code-review skill over each completed diff. Security review for Phase D's content endpoints, retention/purge path and review-provider credential handling.
- **Documentation gates**: `docs validate .` and `docs drift .`.

## 13. Related

- [`tasks/`](tasks/) — dispatch prompts and the Playwright gate
- [2026-09-05 Diagnostic Hierarchy and Context Inspector](2026-09-05-kyberdash-diagnostic-hierarchy-and-context-inspector.md) — predecessor (Needs review); durable decisions harvested as ADRs 0012–0015. Remaining G2/G3 and Phase G live product wiring is this plan.
- [KyberDash architecture](../dash/architecture.md), [runbook](../dash/runbook.md), [telemetry inventory](../dash/telemetry-inventory.md)
- [ADR 0006](../adr/0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md) — soft fork and merge zone, enforced by D8
- [ADR 0011](../adr/0011-asad-only-context-view-and-payload-contract.md) — ASAD-only Context view, revisited by Q2
- `KyberDash.dc.html` in the design project — the interaction reference
