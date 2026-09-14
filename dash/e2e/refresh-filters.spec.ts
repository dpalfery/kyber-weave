import { expect, test } from '@playwright/test'
import { spawn, spawnSync, type ChildProcess } from 'node:child_process'
import { existsSync, mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { POPULATED_HARNESSES, USER_CANON } from './refresh-filters-world.js'

/**
 * Isolated browser acceptance for refresh filters. Starts its own dashboard
 * against a temp refreshed DB. Does not use the live 4747 spine host.
 */
const specDir = fileURLToPath(new URL('.', import.meta.url))
const dashRoot = dirname(specDir)
const scratch = join(dashRoot, '..', '.agents-scratchpad', 'refresh-T8')
const dashDist = join(dashRoot, 'dash', 'dist', 'index.html')

let child: ChildProcess | undefined
let app = ''
let dbPath = ''

test.describe.configure({ mode: 'serial' })

test.beforeAll(async () => {
  test.setTimeout(180_000)
  mkdirSync(scratch, { recursive: true })
  if (!existsSync(dashDist)) {
    const built = spawnSync('npm', ['run', 'build:dash'], { cwd: dashRoot, encoding: 'utf8' })
    if (built.status !== 0) {
      throw new Error(`build:dash failed: ${built.stdout}\n${built.stderr}`)
    }
  }
  child = spawn('npx', ['tsx', 'e2e/refresh-filters-boot.ts'], {
    cwd: dashRoot,
    env: {
      ...process.env,
      NODE_OPTIONS: '--no-deprecation',
      CODEBURN_DASH_DIR: join(dashRoot, 'dash', 'dist'),
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  })

  app = await new Promise<string>((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('T8 dashboard did not become ready')), 150_000)
    let stdout = ''
    const onData = (chunk: Buffer) => {
      stdout += chunk.toString()
      const ready = stdout.match(/T8_READY (http:\/\/127\.0\.0\.1:\d+)/)
      const db = stdout.match(/T8_DB (.+)/)
      if (db) dbPath = db[1]!.trim()
      if (ready) {
        clearTimeout(timer)
        child?.stdout?.off('data', onData)
        resolve(ready[1]!)
      }
    }
    child!.stdout!.on('data', onData)
    child!.stderr!.on('data', (chunk: Buffer) => {
      stdout += chunk.toString()
    })
    child!.on('exit', (code) => {
      if (!app) reject(new Error(`T8 boot exited ${code}: ${stdout}`))
    })
  })

  expect(dbPath).not.toBe(USER_CANON)
  expect(app).not.toContain(':4747')
})

test.afterAll(async () => {
  child?.kill('SIGTERM')
})

test('T8 Attention filters split identities from the temp refreshed DB', async ({ page }) => {
  test.setTimeout(120_000)
  await page.goto(app)
  await expect(page.getByTestId('page-attention')).toBeVisible()
  await expect(page.getByRole('button', { name: /refresh/i })).toHaveCount(0)

  const harnesses = await page.request.get(`${app}/api/kyber/harnesses`)
  expect(harnesses.ok()).toBe(true)
  const payload = (await harnesses.json()) as {
    harnesses: Array<{
      harness: string
      sampleCount: number
      cacheHitRate: number | null
      measurability: Record<string, { availability?: string; reason?: string } | string>
    }>
  }
  writeFileSync(join(scratch, 'harnesses.json'), JSON.stringify(payload, null, 2))

  for (const harness of POPULATED_HARNESSES) {
    const row = payload.harnesses.find((entry) => entry.harness === harness)
    expect(row, `${harness} missing from rollups`).toBeTruthy()
    expect(row!.sampleCount).toBeGreaterThan(0)

    const drill = page.getByTestId(`drill-harness-${harness}`)
    await expect(drill).toBeVisible()
    await drill.click()
    await expect(page.getByTestId('page-harness')).toBeVisible()
    await expect(page.getByTestId('page-harness')).toContainText(harness)
    await expect(page.getByTestId('harness-runs-table')).toBeVisible()
    await expect(page.getByTestId(/^drill-run-/).first()).toBeVisible()

    const runs = await page.request.get(`${app}/api/kyber/runs?harness=${encodeURIComponent(harness)}`)
    const runBody = (await runs.json()) as { runs: Array<{ harness: string; runId: string }> }
    expect(runBody.runs.every((run) => run.harness === harness)).toBe(true)

    const unmeasured = page.locator('[data-testid="dimension-unmeasurable"]')
    const measured = page.locator('[data-testid="dimension-value"]')
    expect((await unmeasured.count()) + (await measured.count())).toBeGreaterThan(0)
    const lyingZero = page
      .locator('[data-testid^="dimension-"][data-measured="false"]')
      .filter({ hasText: /^\s*(0|0%|0\.0%|\$0\.00|0 tok)\s*$/ })
    expect(await lyingZero.count()).toBe(0)

    const scorecard = page.getByTestId('scorecard')
    await expect(scorecard).toBeVisible()
    const dashed = page.getByTestId('dimension-unmeasurable')
    if ((await dashed.count()) > 0) {
      const title = await dashed.first().locator('xpath=ancestor::*[@title][1]').getAttribute('title')
      expect(title ?? '').not.toMatch(/^\s*$/)
      expect(title ?? '').toMatch(/\(.+\)/)
    }

    await page.getByTestId('breadcrumb-attention').click()
    await expect(page.getByTestId('page-attention')).toBeVisible()
  }

  await page.screenshot({ path: join(scratch, 'attention.png'), fullPage: true })

  const emptyDrill = page.getByTestId('drill-harness-cline')
  if (await emptyDrill.count()) {
    await emptyDrill.click()
    await expect(page.getByTestId('page-harness')).toBeVisible()
    await expect(page.getByTestId('harness-runs-table')).toContainText('No runs recorded')
    await expect(page.getByTestId('dimension-unmeasurable').first()).toBeVisible()
    await page.screenshot({ path: join(scratch, 'empty-cline.png'), fullPage: true })
  }
})
