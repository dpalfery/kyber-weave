---
id: plans/tasks/T3-fabricated-harnesses
title: T3 — Delete the fabricated harness catalogue and Attention's narration
doc-type: plan
status: needs-review
owner: dpalfery
last-reviewed: 2026-09-06
component: KyberDash
---

# T3 — Delete the fabricated harness catalogue and Attention's narration

**Model:** Haiku. **Parallel with:** T1, T2, T4.

---

TASK: `Attention.tsx` hardcodes six harnesses with invented `fieldCoverage` values (`0.83`, `0.33`, `0.67`, `0.5`) and `sampleCount: 0`, then renders them through the same table cells as live rollups. A screen whose entire purpose is honest measurement is currently shipping made-up coverage percentages. The list also disagrees with `App.tsx`'s eleven-entry `HARNESS_TABS`.

You own this file exclusively, so also do the decision-id sweep within it (T1 does the other components).

DONE WHEN BOTH PASS (both fail right now — run them first):

```bash
npm --prefix dash run dev &
npx --prefix dash playwright test e2e/spine.spec.ts -g "G3|G5"
```

Plus this check by hand, which no gate covers: point the dev server at an **empty** store and confirm the landing page shows no coverage percentage, no pressure figure and no cache figure for any harness.

FILES YOU OWN (touch nothing else):

```
dash/dash/src/pages/Attention.tsx
```

WHAT TO CHANGE:

1. Replace `DEFAULT_SURVEYED_HARNESSES` with a name catalogue — `{harness, name}` only. **No metric fields at all.** Not zeroed, not null-filled: absent from the type.
2. A harness with no live rollup renders as unconnected with a reason, reusing `components/kyber/NotMeasurable.tsx`. It must not render a numeric cell.
3. Export the catalogue, or import `HARNESS_TABS` from `App.tsx`, so the two lists cannot diverge again. One source.
4. Remove the decision-id narration in this file: both `<p>` descriptions ("Six-level spine landing page: All Harnesses…", "Ranked by Decision D6 formula (estimated waste × outcome risk × confidence)…") and the `(Decision D16)` inside the `skillUtilisation` reason. Replace each with one plain sentence, or nothing.
5. Add `data-testid="page-attention"` to the root if it is missing, and `data-testid="drill-harness-<id>"` to each row's clickable element.

CONSTRAINTS:

- Do not add fixtures or mocks. The gate runs against the real dev server and the developer's real `~/.kyberdash/canon.db`.
- Do not write a new unit test as evidence of completion. The gate above is the only evidence that counts.
- If the gate cannot pass for a reason outside your owned files, STOP and report what blocks it. Do not widen your file list. You will be tempted to edit `Scorecard.tsx` — it belongs to T1.
- Do not restyle or restructure the page. The bespoke four-column table stays for now; L2 replaces it later.
- The empty state will look sparse. That is correct. Do not add counters to fill it.

REFERENCE: `docs/plans/2026-09-06-kyberdash-spine.md` decisions D9 and D10.
