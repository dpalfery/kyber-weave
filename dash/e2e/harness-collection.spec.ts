import { expect, test } from '@playwright/test'
import { spawn, spawnSync, type ChildProcess } from 'node:child_process'
import { existsSync, mkdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import {
  HONEST_EMPTY_HARNESS,
  MEASURED_HARNESS,
  USER_CANON,
  ZERO_RECORD_HARNESS,
} from './harness-collection-world.js'

/**
 * Harness-collection completeness predicate (GREEN: the fixture world ships
 * alongside this spec).
 *
 * RED was captured against the missing `harness-collection-world.ts`; the
 * world and its boot script now exist, so this spec pins the contract they
 * satisfy: a measured harness is claimable as complete, an honest-empty
 * harness shows its session as unmeasurable with a reason, and a zero-record
 * harness stays honest-empty and is never claimable as complete.
 *
 * Isolated browser acceptance. Starts its own dashboard against a temp DB from
 * the fixture. Does not use the live 4747 spine host.
 */
const specDir = fileURLToPath(new URL('.', import.meta.url))
const dashRoot = dirname(specDir)
const scratch = join(dashRoot, '..', '.agent_scratchpad', 'harness-collection-T1')
const dashDist = join(dashRoot, 'dist', 'dash', 'index.html')

let child: ChildProcess | undefined
let app = ''
let dbPath = ''

test.describe.configure({ mode: 'serial' })

test.beforeAll(async () => {
  test.setTimeout(180_000)
  mkdirSync(scratch, { recursive: true })
  if (!existsSync(dashDist)) {
    const built = spawnSync('npm', ['run', 'build:web'], { cwd: dashRoot, encoding: 'utf8' })
    if (built.status !== 0) {
      throw new Error(`build:web failed: ${built.stdout}\n${built.stderr}`)
    }
  }
  child = spawn('npx', ['tsx', 'e2e/harness-collection-boot.ts'], {
    cwd: dashRoot,
    env: {
      ...process.env,
      NODE_OPTIONS: '--no-deprecation',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  })

  app = await new Promise<string>((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('harness-collection dashboard did not become ready')), 150_000)
    let stdout = ''
    const onData = (chunk: Buffer) => {
      stdout += chunk.toString()
      const ready = stdout.match(/HC_READY (http:\/\/127\.0\.0\.1:\d+)/)
      const db = stdout.match(/HC_DB (.+)/)
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
      if (!app) reject(new Error(`harness-collection boot exited ${code}: ${stdout}`))
    })
  })

  expect(dbPath).not.toBe(USER_CANON)
  expect(app).not.toContain(':4747')
})

test.afterAll(async () => {
  child?.kill('SIGTERM')
})

test('measured harness renders measured dimensions and no lying zero', async ({ page }) => {
  test.setTimeout(120_000)
  await page.goto(app)
  await expect(page.getByTestId('page-context-doctor')).toBeVisible()
  await page.evaluate((harnessId) => {
    const stack = [
      { level: 'context-doctor' },
      { level: 'harness', harnessId },
    ]
    window.history.pushState({ stack }, '', `/harness/${harnessId}`)
    window.dispatchEvent(new PopStateEvent('popstate'))
  }, MEASURED_HARNESS)
  await expect(page.getByTestId('page-harness')).toBeVisible()
  await expect(page.getByTestId('page-harness')).toContainText(MEASURED_HARNESS)
  await expect(page.getByTestId('harness-runs-table')).toBeVisible()
  await expect(page.getByTestId(/^drill-run-/).first()).toBeVisible()

  const measured = page.locator('[data-testid="dimension-value"]')
  expect(await measured.count()).toBeGreaterThan(0)

  // Scorecard emits no data-measured attribute, so "never a lying zero" is
  // expressed against what it renders: an unmeasurable cell never carries a
  // numeric figure, and every measured cell carries a real one.
  const numericUnmeasurable = page
    .locator('[data-testid="dimension-unmeasurable"]')
    .filter({ hasText: /^\s*\$?0(\.0+)?\s*(%|tok|x)?\s*$/ })
  expect(await numericUnmeasurable.count()).toBe(0)

  const figures = await measured.allInnerTexts()
  for (const figure of figures) {
    expect(figure.trim()).not.toBe('—')
    expect(figure).toMatch(/\d/)
  }
})

test('honest-empty harness renders a visible session as unmeasurable with reason', async ({ page }) => {
  test.setTimeout(120_000)
  await page.goto(app)
  await expect(page.getByTestId('page-context-doctor')).toBeVisible()
  await page.evaluate((harnessId) => {
    const stack = [
      { level: 'context-doctor' },
      { level: 'harness', harnessId },
    ]
    window.history.pushState({ stack }, '', `/harness/${harnessId}`)
    window.dispatchEvent(new PopStateEvent('popstate'))
  }, HONEST_EMPTY_HARNESS)
  await expect(page.getByTestId('page-harness')).toBeVisible()
  await expect(page.getByTestId('page-harness')).toContainText(HONEST_EMPTY_HARNESS)
  await expect(page.getByTestId('harness-runs-table')).toBeVisible()
  await expect(page.getByTestId(/^drill-run-/).first()).toBeVisible()

  const unmeasurable = page.getByTestId('dimension-unmeasurable')
  expect(await unmeasurable.count()).toBeGreaterThan(0)

  const reason = page.getByTestId(/^dimension-reason-/).first()
  await expect(reason).toBeVisible()
  await expect(reason).not.toBeEmpty()

  const title = await unmeasurable.first().locator('xpath=ancestor::*[@title][1]').getAttribute('title')
  expect(title ?? '').not.toMatch(/^\s*$/)
  expect(title ?? '').toMatch(/\(.+\)/)
})

test('zero-record harness renders honest-empty and is never complete', async ({ page }) => {
  test.setTimeout(120_000)
  await page.goto(app)
  await expect(page.getByTestId('page-context-doctor')).toBeVisible()
  await page.evaluate((harnessId) => {
    const stack = [
      { level: 'context-doctor' },
      { level: 'harness', harnessId },
    ]
    window.history.pushState({ stack }, '', `/harness/${harnessId}`)
    window.dispatchEvent(new PopStateEvent('popstate'))
  }, ZERO_RECORD_HARNESS)
  await expect(page.getByTestId('page-harness')).toBeVisible()
  await expect(page.getByTestId('page-harness')).toContainText(ZERO_RECORD_HARNESS)
  await expect(page.getByTestId('harness-runs-table')).toContainText('No runs recorded')

  // Nothing measured, so the completeness predicate cannot be satisfied here.
  await expect(page.getByTestId('dimension-value')).toHaveCount(0)
  await expect(page.getByTestId('page-harness')).not.toContainText(/complete/i)
})
