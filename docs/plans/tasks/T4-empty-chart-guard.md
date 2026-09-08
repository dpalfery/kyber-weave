---
id: plans/tasks/T4-empty-chart-guard
title: T4 — A chart with no data is not a chart
doc-type: plan
status: needs-review
owner: dpalfery
last-reviewed: 2026-09-06
component: KyberDash
---

# T4 — A chart with no data is not a chart

**Model:** Haiku. **Parallel with:** T1, T2, T3.

---

TASK: "Token Spend per Turn" is the largest element on the current Context screen. It plots a 0–1 axis with a flat line at zero, x-labels reading `0 0 / 1 0 / 2 0 / 3 0 / 4 0`, a legend of five series with no data, and a note underneath admitting cache-creation input was not recorded. It carries no information and occupies the most space. Replace empty charts with a sentence naming what was not recorded.

DONE WHEN THIS PASSES (it fails right now — run it first and read the failure):

```bash
npm --prefix dash run dev &
npx --prefix dash playwright test e2e/spine.spec.ts -g "G6"
```

FILES YOU OWN (touch nothing else):

```
dash/dash/src/components/SessionSpendCharts.tsx
dash/dash/src/components/UsageChart.tsx
dash/dash/src/components/kyber/ContextPressureStrip.tsx
```

WHAT TO CHANGE:

1. Each chart container gets `data-testid="chart-<key>"` and `data-empty="true|false"`.
2. When **every** series is empty or all-zero-because-unreported, render no SVG. Render one line: what was not recorded, and by whom where known — e.g. *"Token counts were not recorded for this session."*
3. When **some** series have data, still chart. Name the absent series in one line beneath, rather than drawing them flat at zero.
4. Do not render a legend entry for a series with no data.

THE DISTINCTION THAT MATTERS: a series that is genuinely zero across every turn (real measurement, real zero) still charts. A series that is zero because nothing was reported does not. If the component cannot currently tell these apart, say so in your report rather than guessing — that is a `measurability` question and T7 may already have the answer.

CONSTRAINTS:

- Do not add fixtures or mocks. The gate runs against the real dev server and the developer's real `~/.kyberdash/canon.db`.
- Do not write a new unit test as evidence of completion. The gate above is the only evidence that counts.
- If the gate cannot pass for a reason outside your owned files, STOP and report what blocks it. Do not widen your file list.
- Do not change chart colours, axes, tooltips or layout for charts that **do** have data. This task only handles the empty case.
- `UsageChart.tsx` is shared with the Usage pages. Check those for regressions.

REFERENCE: `docs/plans/2026-09-06-kyberdash-spine.md` decision D7.
