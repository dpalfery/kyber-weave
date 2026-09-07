import { test, expect } from '@playwright/test'

/**
 * The gate. Every dispatched task points at one of these tests.
 *
 * Runs against the real dev server (`npm --prefix dash run dev`, 127.0.0.1:4747)
 * and the developer's real ~/.kyberdash/canon.db. No fixtures. No mocks.
 *
 * ALL FIVE FAIL TODAY. That failing output is the specification.
 *
 *   npm --prefix dash run dev &
 *   npx --prefix dash playwright test e2e/spine.spec.ts
 */

const APP = 'http://127.0.0.1:4747'

test('G1 — app opens on the attention level', async ({ page }) => {
  await page.goto(APP)
  await expect(page.getByTestId('page-attention')).toBeVisible()
})

test('G2 — a developer can drill all six levels by clicking', async ({ page }) => {
  await page.goto(APP)

  await page.getByTestId(/^drill-harness-/).first().click()
  await expect(page.getByTestId('page-harness')).toBeVisible()

  await page.getByTestId(/^drill-run-/).first().click()
  await expect(page.getByTestId('page-run')).toBeVisible()

  await page.getByTestId(/^drill-execution-/).first().click()
  await expect(page.getByTestId('page-execution')).toBeVisible()

  await page.getByTestId(/^drill-turn-/).first().click()
  await expect(page.getByTestId('page-turn')).toBeVisible()

  await page.getByTestId(/^context-band-/).first().click()
  await expect(page.getByTestId('context-content')).not.toBeEmpty()

  // and back out again
  await page.getByTestId('breadcrumb-attention').click()
  await expect(page.getByTestId('page-attention')).toBeVisible()
})

test('G3 — no unreported counter renders as zero', async ({ page }) => {
  await page.goto(APP)
  const lying = page
    .locator('[data-testid^="metric-"][data-measured="false"]')
    .filter({ hasText: /^\s*(0|0%|0\.0%|\$0\.00|0 tok)\s*$/ })
  expect(await lying.count()).toBe(0)
})

test('G4 — exactly one harness selector on screen', async ({ page }) => {
  await page.goto(APP)
  await expect(page.getByTestId('harness-selector')).toHaveCount(1)
})

test('G5 — no decision ids in product copy', async ({ page }) => {
  await page.goto(APP)
  const body = await page.locator('body').innerText()
  expect(body).not.toMatch(/\bD\d{1,2}\b|Decision \d|per Decision|\bADR\b/)
})

/**
 * G6 — no chart renders axes for an all-empty dataset.
 * A chart that has nothing to plot must be replaced by one line of text
 * naming what was not recorded.
 */
test('G6 — empty datasets do not render as charts', async ({ page }) => {
  await page.goto(APP)
  const empties = page.locator('[data-testid^="chart-"][data-empty="true"]')
  for (let i = 0; i < (await empties.count()); i++) {
    await expect(empties.nth(i).locator('svg')).toHaveCount(0)
  }
})

/**
 * G7 — nothing lays out outside the shell.
 * The current build renders clipped glyphs down the full height at x < 20px.
 */
test('G7 — no element renders outside the shell', async ({ page }) => {
  await page.goto(APP)
  const strays = await page.evaluate(() =>
    [...document.querySelectorAll('body *')]
      .filter((el) => {
        const r = el.getBoundingClientRect()
        return r.width > 0 && r.height > 0 && r.right < 20 && r.left < 0
      })
      .map((el) => `${el.tagName.toLowerCase()}.${(el.className || '').toString().slice(0, 60)}`),
  )
  expect(strays).toEqual([])
})
