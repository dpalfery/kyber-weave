import { expect, test, type Page } from '@playwright/test'
import { spawn, spawnSync, type ChildProcess } from 'node:child_process'
import { existsSync, mkdirSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import {
  HONEST_EMPTY_HARNESS,
  MEASURED_HARNESS,
  OPENCODE_REPLAY_SESSION_ID,
  USER_CANON,
  ZERO_RECORD_HARNESS,
} from './harness-collection-world.js'

/**
 * Harness-collection completeness predicate.
 *
 * Isolated browser acceptance. Starts its own dashboard against a temp DB from
 * the world. Does not use the live 4747 spine host.
 *
 * Capture proof comes from one scenario only: the OpenCode 2.2.1 replay. The
 * world runs it through the production plugin, receiver decode, canonical
 * ingest and projection before the dashboard starts, so the session the
 * round-trip case reads was derived, not seeded. The honest-empty `opencode`
 * row and the measured `claude-code` row are rendering-only seeds and prove
 * no capture.
 */
const specDir = fileURLToPath(new URL('.', import.meta.url))
const dashRoot = dirname(specDir)
const scratch = join(dashRoot, '..', '.agent_scratchpad', 'harness-collection-T1')
const dashDist = join(dashRoot, 'dist', 'dash', 'index.html')

let child: ChildProcess | undefined
let bootPid: number | undefined
let app = ''
let dbPath = ''

test.describe.configure({ mode: 'serial' })

/** True while the process group led by `pid` still has a live member. */
function bootGroupAlive(pid: number): boolean {
  try {
    process.kill(-pid, 0)
    return true
  } catch (error) {
    return (error as NodeJS.ErrnoException).code !== 'ESRCH'
  }
}

/**
 * Signals the boot's whole process group. The boot is spawned `detached`, so it leads its own
 * group and every process it forks shares that group id. Signalling only the leader let a forked
 * child outlive the run, reparented to PID 1. A group that is already gone (ESRCH) is success.
 */
function signalBootGroup(pid: number, signal: NodeJS.Signals): void {
  try {
    process.kill(-pid, signal)
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== 'ESRCH') throw error
  }
}

/** SIGTERM the group, allow a bounded grace to exit, then SIGKILL whatever is still in it. */
async function stopBootGroup(pid: number): Promise<void> {
  signalBootGroup(pid, 'SIGTERM')
  const deadline = Date.now() + 5_000
  while (bootGroupAlive(pid) && Date.now() < deadline) {
    await new Promise<void>((resolve) => setTimeout(resolve, 100))
  }
  if (bootGroupAlive(pid)) signalBootGroup(pid, 'SIGKILL')
}

/** Process 'exit' fallback: runs when the test process exits without reaching afterAll. */
const killBootGroupOnExit = (): void => {
  if (bootPid === undefined || !bootGroupAlive(bootPid)) return
  try {
    signalBootGroup(bootPid, 'SIGKILL')
  } catch {
    // The process is exiting; there is nothing left that could act on the error.
  }
}

/**
 * Open one harness from the dashboard root, the app's own client-side route.
 *
 * Returns the `/api/kyber/runs` answer as well as navigating: the rows the tests
 * assert on come from that payload, so a failure names what the server actually
 * replied instead of only "element not found". The payload is also the isolation
 * proof — it must carry the fixture run, never whatever the operator's own
 * `~/.kyberdash/canon.db` happens to hold.
 */
async function openHarness(
  page: Page,
  harnessId: string,
): Promise<{ status: number; body: string; runs: unknown[] }> {
  const responsePromise = page.waitForResponse(
    (response) => response.url().includes('/api/kyber/runs'),
    { timeout: 30_000 },
  )
  await page.goto(app)
  await expect(page.getByTestId('page-context-doctor')).toBeVisible()
  await page.evaluate((id) => {
    const stack = [{ level: 'context-doctor' }, { level: 'harness', harnessId: id }]
    window.history.pushState({ stack }, '', `/harness/${id}`)
    window.dispatchEvent(new PopStateEvent('popstate'))
  }, harnessId)
  await expect(page.getByTestId('page-harness')).toBeVisible()
  await expect(page.getByTestId('page-harness')).toContainText(harnessId)
  const response = await responsePromise
  const body = await response.text()
  const contentType = response.headers()['content-type'] ?? ''
  // The zero-record test asserts an EMPTY list, so an empty list has to mean
  // "the server reported no runs" and never "the body was not JSON". A 200 HTML
  // body is exactly what this server sends for an unknown path (the SPA
  // fallback serves index.html), so a swallowed parse error would let this
  // suite stay green while the dashboard served fiction. Both cases fail here,
  // naming what actually arrived.
  expect(
    contentType,
    `the runs answer for ${harnessId} was not JSON (content-type ${contentType}): ${body.slice(0, 300)}`,
  ).toContain('application/json')
  let parsed: { runs?: unknown }
  try {
    parsed = JSON.parse(body) as { runs?: unknown }
  } catch {
    throw new Error(
      `the runs answer for ${harnessId} did not parse as JSON: ${body.slice(0, 300)}`,
    )
  }
  expect(
    Array.isArray(parsed.runs),
    `the runs answer for ${harnessId} carried no runs array: ${body.slice(0, 300)}`,
  ).toBe(true)
  return { status: response.status(), body, runs: parsed.runs as unknown[] }
}

test.beforeAll(async () => {
  test.setTimeout(180_000)
  mkdirSync(scratch, { recursive: true })
  if (!existsSync(dashDist)) {
    // A synchronous call is not interrupted by test.setTimeout, so the build carries its own bound.
    const built = spawnSync('npm', ['run', 'build:web'], {
      cwd: dashRoot,
      encoding: 'utf8',
      timeout: 300_000,
      killSignal: 'SIGKILL',
    })
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
    // Leads its own process group so teardown can reach every process the boot forks.
    detached: true,
    stdio: ['ignore', 'pipe', 'pipe'],
  })
  bootPid = child.pid
  process.once('exit', killBootGroupOnExit)

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
    // Without this, a spawn failure (no `npx` on PATH, EACCES) arrives as an
    // unhandled 'error' event on the child and crashes the worker instead of
    // failing this hook with the reason.
    child!.on('error', (error) => {
      clearTimeout(timer)
      reject(new Error(`harness-collection boot could not start: ${String(error)}`))
    })
  })

  expect(dbPath).not.toBe(USER_CANON)
  expect(app).not.toContain(':4747')
})

test.afterAll(async () => {
  // SIGTERM is a request, not a completion: stop the whole boot process group and
  // wait for it to empty before deleting the store it was serving from.
  if (bootPid !== undefined) {
    await stopBootGroup(bootPid)
    process.off('exit', killBootGroupOnExit)
  }
  // The boot script seeds a `mkdtemp` canon store; nothing else removes it, so
  // a run would otherwise leave one temp root per invocation behind.
  if (dbPath !== '') {
    const root = dirname(dbPath)
    if (root.startsWith(tmpdir()) && !root.includes('.kyberdash')) {
      rmSync(root, { recursive: true, force: true })
    }
  }
})

test('measured harness renders measured dimensions and no lying zero', async ({ page }) => {
  test.setTimeout(120_000)
  const runs = await openHarness(page, MEASURED_HARNESS)
  expect(runs.status, runs.body).toBe(200)
  expect(runs.body, 'the runs payload must come from the fixture store').toContain('hc-measured-run-1')
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
  const runs = await openHarness(page, HONEST_EMPTY_HARNESS)
  expect(runs.status, runs.body).toBe(200)
  expect(runs.body, 'the runs payload must come from the fixture store').toContain('hc-honest-run-1')
  // The `opencode` harness also carries the replayed OpenCode run, so this case
  // pins the coexistence: the honest run stays visible and unmeasurable beside
  // its replayed sibling. Two runs must be listed, so a replay that replaced
  // the honest run (or the reverse) fails here.
  expect(runs.runs.length, 'the replayed sibling must not replace the honest run').toBeGreaterThanOrEqual(2)
  await expect(page.getByTestId('harness-runs-table')).toBeVisible()
  await expect(page.getByTestId(/^drill-run-/).first()).toBeVisible()
  await expect(page.getByTestId(/^drill-run-/).nth(1)).toBeVisible()

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
  const runs = await openHarness(page, ZERO_RECORD_HARNESS)
  expect(runs.status, runs.body).toBe(200)
  // A zero-record harness answers with an empty list from the fixture store; a
  // non-empty one here means the run leaked into another store, or out of it.
  expect(runs.runs).toEqual([])
  await expect(page.getByTestId('harness-runs-table')).toContainText('No runs recorded')

  // Nothing measured, so the completeness predicate cannot be satisfied here.
  await expect(page.getByTestId('dimension-value')).toHaveCount(0)
  await expect(page.getByTestId('page-harness')).not.toContainText(/complete/i)
})
test('OpenCode 2.2.1 replay through the production pipeline renders the consent-on session', async ({ page }) => {
  test.setTimeout(120_000)
  const runs = await openHarness(page, HONEST_EMPTY_HARNESS)
  expect(runs.status, runs.body).toBe(200)
  // Both `opencode` runs are listed: the replayed run from the production
  // pipeline and the honest-empty rendering seed beside it.
  expect(runs.runs.length, 'the replayed run and the honest-empty sibling must both be listed').toBeGreaterThanOrEqual(2)
  await expect(page.getByTestId('harness-runs-table')).toBeVisible()

  // Sessions tab: the replayed session, found by its real session id, shows the
  // consent-on figures the replay derived from runA.
  await page.getByTestId('nav-tab-sessions').click()
  await expect(page.getByTestId('page-sessions')).toBeVisible()
  const row = page.getByTestId(`agent-session-row-${OPENCODE_REPLAY_SESSION_ID}`)
  await expect(row).toBeVisible()
  // Soft, so a missing label does not hide the figure checks below from the same run.
  await expect.soft(row).toContainText('kw-probe runA shared-service')
  await row.click()

  // The figure checks are soft: one run reports every mismatch, not only the first.
  // Two tool calls: the skill load and the read.
  await expect.soft(page.getByTestId('metric-tool-calls')).toContainText('2')
  // Cost: priced at zero on the harness basis. A zero the harness reported is a
  // real figure; a missing rate would read "no published rate" instead.
  await expect.soft(page.getByTestId('metric-cost')).toContainText('$0')
  await expect.soft(page.getByTestId('metric-cost')).toContainText(/harness|reported/i)
  // Token figures: 40108 input (40.1K) and 1529 output (1.5K, or the raw count).
  await expect.soft(page.getByTestId('page-sessions')).toContainText('40.1K')
  await expect.soft(page.getByTestId('page-sessions')).toContainText(/1\.5K|1,?529/)
})
