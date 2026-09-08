---
id: plans/tasks/T5-mono-numerals
title: T5 — Every number is mono and tabular
doc-type: plan
status: needs-review
owner: dpalfery
last-reviewed: 2026-09-06
component: KyberDash
---

# T5 — Every number is mono and tabular

**Model:** Haiku. **Run after:** T8 merges. Do not run in parallel with T8 — it owns the same files.

---

TASK: Numbers are inconsistently monospaced across the spine. The prototype puts every count, token figure, percentage, delta and cost in `--font-mono` with `tabular-nums`; it is the strongest single signal of this design language, and columns of figures do not align without it. The current build mixes mono cells, proportional cells, and one-off `text-[10.5px]` literals.

DONE WHEN: this returns nothing, and the pages still look right in a browser.

```bash
# every numeric-looking cell in the spine carries both classes
npx --prefix dash playwright test e2e/numerals.spec.ts
```

Write `dash/e2e/numerals.spec.ts` as part of this task — it is the gate, not the evidence:

```ts
import { test, expect } from '@playwright/test'

test('every numeric cell is mono and tabular', async ({ page }) => {
  await page.goto('http://127.0.0.1:4747')
  const bad = await page.evaluate(() =>
    [...document.querySelectorAll('[data-testid^="metric-"], td, .kpi, [data-numeric]')]
      .filter((el) => /^[\s$]*[\d.,]+\s*(%|tok|M|k|s|ms)?\s*$/.test(el.textContent || ''))
      .filter((el) => {
        const s = getComputedStyle(el)
        return !s.fontFamily.toLowerCase().includes('mono') ||
               !s.fontVariantNumeric.includes('tabular-nums')
      })
      .map((el) => `${el.getAttribute('data-testid') || el.tagName}: ${el.textContent?.trim()}`),
  )
  expect(bad).toEqual([])
})
```

FILES YOU OWN: every file under `dash/dash/src/pages/` and `dash/dash/src/components/kyber/`, plus `dash/e2e/numerals.spec.ts`.

WHAT TO CHANGE:

1. `font-mono tabular-nums` on every count, token figure, percentage, delta, duration and cost. No exceptions.
2. Replace ad-hoc sizes (`text-[10.5px]`, `text-[11px]`, `text-[9.5px]`) with named tokens. Add `--text-micro: 10px` and `--text-meta: 11px` to `index.css` and `@theme inline` if they do not exist — that one file edit is in scope.
3. Replace raw Tailwind palette colours used for state (`amber-500`, `emerald-500`, `text-amber-400`) with the app's semantic tokens (`--positive`, `--negative`, and a new `--caution` if missing).

WHAT NOT TO CHANGE:

- Layout, spacing, component structure, copy.
- Anything outside `pages/` and `components/kyber/`. The Usage-page components have their own conventions and are not in scope.
- Non-numeric text. Labels stay in the sans face.

CONSTRAINTS:

- Do not add fixtures or mocks. The gate runs against the real dev server and the developer's real `~/.kyberdash/canon.db`.
- Do not write a unit test as evidence of completion. The Playwright gate above is the only evidence that counts.
- If the gate cannot pass for a reason outside your owned files, STOP and report what blocks it. Do not widen your file list.
- Confirm contrast after any colour swap: body text ≥ 4.5:1, headline figures ≥ 3:1, in **both** light and dark.
- The gate's regex will catch some non-numbers (a bare "5" in prose, a year). If a flagged element is legitimately not a figure, add `data-numeric="false"` and exclude it — do not loosen the regex.

REFERENCE: `docs/plans/2026-09-06-kyberdash-spine.md` Phase C1, decision D6.
