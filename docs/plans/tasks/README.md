---
id: plans/tasks/index
title: KyberDash spine dispatch pack
doc-type: plan
status: needs-review
owner: dpalfery
last-reviewed: 2026-09-06
component: KyberDash
---

# Dispatch pack — prototype to running app

Copy `spine.spec.ts` to `dash/e2e/spine.spec.ts`, then hand out the task files below.
Current state, decisions, and the phases after this one: [../2026-09-06-kyberdash-spine.md](../2026-09-06-kyberdash-spine.md).

## Setup (you, once, ~1 hour)

```bash
npm --prefix dash install -D @playwright/test
npx --prefix dash playwright install chromium
cp docs/plans/tasks/spine.spec.ts dash/e2e/spine.spec.ts
npm --prefix dash run dev &
npx --prefix dash playwright test e2e/spine.spec.ts
```

All seven gates fail. Read the output. That is the work.

## Order — this matters more than the prompts

```
T7  investigate Q9        strong model, 30 min, blocks T2 and T4
T1 T2 T3 T4               four Haiku agents IN PARALLEL      → G3 G5 G6 green
T6  artifact hunt         Haiku, report-only first           → G7 green
T8  walking skeleton      ONE strong agent, ONE window       → G1 G2 G4 green
T5  mono numerals         Haiku, AFTER T8 merges
```

**T1–T4 run before T8, not after.** They only delete, so they shrink the surface T8 has to reason about, and they cannot collide with a reducer that does not exist yet.

## File ownership — one owner per file per window

| Files | Owner |
|---|---|
| `components/kyber/Scorecard.tsx`, `FindingList.tsx`, `EvidenceTable.tsx`, `ConfidencePanel.tsx`, `RecommendationPanel.tsx`, `TurnAlignedDiff.tsx`, `pages/CompareRuns.tsx` | T1 |
| `lib/utils.ts`, `components/MetricCard.tsx` | T2 |
| `pages/Attention.tsx` | T3 |
| `components/SessionSpendCharts.tsx`, `UsageChart.tsx`, `components/kyber/ContextPressureStrip.tsx` | T4 |
| `App.tsx`, `pages/HarnessDetail.tsx`, `RunDetail.tsx`, `FindingDetail.tsx`, `pages/TurnDetail.tsx` (new), `HierarchyBreadcrumb.tsx` | T8 |
| everything under `pages/` and `components/kyber/` | T5 — which is why it runs alone, last |

`Attention.tsx` belongs to T3 alone — T3 does both the fabricated-data deletion and the narration sweep inside that one file, so T1 must not touch it. T6 must not touch `App.tsx`; if the artifact originates there, T6 reports and stops.

## The `data-testid` contract

Every task implements against these names. Do not rename them.

| Testid | On |
|---|---|
| `page-attention` / `page-harness` / `page-run` / `page-execution` / `page-turn` / `page-finding` | each level's root element |
| `drill-harness-<id>` / `drill-run-<id>` / `drill-execution-<id>` / `drill-turn-<n>` / `drill-finding-<id>` | the clickable element that descends a level |
| `breadcrumb-attention` … `breadcrumb-turn` | breadcrumb segments |
| `harness-selector` | the single harness selector, in the shell |
| `metric-<key>` + `data-measured="true\|false"` | every metric card |
| `chart-<key>` + `data-empty="true\|false"` | every chart container |
| `context-band-<key>` / `context-content` | a turn composition band, and the pane its text lands in |

## The two lines that stop the last failure repeating

Every task file carries them. Do not edit them out:

- *Do not write a new unit test as evidence of completion.*
- *If the gate cannot pass for a reason outside your owned files, STOP and report. Do not widen your file list.*

The previous two windows failed by substituting a satisfiable proxy for the real gate, then widening scope to keep making progress. `Scorecard.test.tsx` passes today while the page it tests is unreachable.

## What to ask a completion report

Not "did the tests pass". Ask: **what did you open in a browser, and what did you click?** If the answer is nothing, the task is not done.
