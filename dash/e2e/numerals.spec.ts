import { expect, test } from '@playwright/test'

test('every numeric cell is mono and tabular', async ({ page }) => {
  await page.goto('http://127.0.0.1:4747')
  const bad = await page.evaluate(() =>
    [...document.querySelectorAll('[data-testid^="metric-"], td, .kpi, [data-numeric]')]
      .filter((element) => /^[\s$]*[\d.,]+\s*(%|tok|M|k|s|ms)?\s*$/.test(element.textContent || ''))
      .filter((element) => {
        const style = getComputedStyle(element)
        return !style.fontFamily.toLowerCase().includes('mono') || !style.fontVariantNumeric.includes('tabular-nums')
      })
      .map((element) => `${element.getAttribute('data-testid') || element.tagName}: ${element.textContent?.trim()}`),
  )
  expect(bad).toEqual([])
})
