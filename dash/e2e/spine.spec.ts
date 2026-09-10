import { expect, test } from '@playwright/test'

/**
 * The KyberDash diagnostic-spine acceptance gate.
 *
 * Runs against the local product host and its real ~/.kyberdash/canon.db. No
 * fixtures or mocks belong here: this contract prevents an unreachable page
 * component from being mistaken for a shipped feature.
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

test('G4a — harness selector uses canonical filters and exposes its matching runs', async ({ page }) => {
  await page.goto(APP)

  await expect(page.getByRole('button', { name: 'Claude Code', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'GitHub Copilot', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Gemini', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: /Copilot \(VS Code\)|Copilot Agent|Copilot CLI/ })).toHaveCount(0)

  const filteredRuns = page.waitForResponse(
    (response) => response.url().includes('/api/kyber/runs?harness=claude-code') && response.ok(),
  )
  await page.getByRole('button', { name: 'Claude Code', exact: true }).click()
  await filteredRuns
  await expect(page.getByTestId('page-harness')).toBeVisible()
  await page.getByTestId(/^drill-run-/).first().click()
  await expect(page.getByTestId('page-run')).toBeVisible()
})

test('G5 — no decision ids in product copy', async ({ page }) => {
  await page.goto(APP)
  const body = await page.locator('body').innerText()
  expect(body).not.toMatch(/\bD\d{1,2}\b|Decision \d|per Decision|\bADR\b/)
})

test('G6 — empty datasets do not render as charts', async ({ page }) => {
  await page.goto(APP)
  const empties = page.locator('[data-testid^="chart-"][data-empty="true"]')
  for (let i = 0; i < (await empties.count()); i++) {
    await expect(empties.nth(i).locator('svg')).toHaveCount(0)
  }
})

test('G7 — no element renders outside the shell', async ({ page }) => {
  await page.goto(APP)
  const strays = await page.evaluate(() =>
    [...document.querySelectorAll('body *')]
      .filter((element) => {
        const bounds = element.getBoundingClientRect()
        return bounds.width > 0 && bounds.height > 0 && bounds.right < 20 && bounds.left < 0
      })
      .map((element) => `${element.tagName.toLowerCase()}.${(element.className || '').toString().slice(0, 60)}`),
  )
  expect(strays).toEqual([])
})
