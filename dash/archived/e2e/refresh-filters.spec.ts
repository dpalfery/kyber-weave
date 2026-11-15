import { expect, test } from '@playwright/test'
import { spawn, spawnSync, type ChildProcess } from 'node:child_process'
import { existsSync, mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { DERIVED_HARNESSES, USER_CANON } from './refresh-filters-world.js'

/**
 * Isolated browser acceptance for refresh filters. Starts its own dashboard
 * against a temp refreshed DB. Does not use the live 4747 spine host.
 */
const specDir = fileURLToPath(new URL('.', import.meta.url))
const dashRoot = dirname(specDir)
const scratch = join(dashRoot, '..', '.agent_scratchpad', 'refresh-T8')
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
      throw new Error(`build:dash failed: ${built.stdout}\n${built.stderr}`)
    }
  }
  child = spawn('npx', ['tsx', 'e2e/refresh-filters-boot.ts'], {
    cwd: dashRoot,
    env: {
      ...process.env,
      NODE_OPTIONS: '--no-deprecation',
      KYBERDASH_DASH_DIR: join(dashRoot, 'dist', 'dash'),
    },
    // Leads its own process group so teardown can reach every process the boot forks.
    detached: true,
    stdio: ['ignore', 'pipe', 'pipe'],
  })
  bootPid = child.pid
  process.once('exit', killBootGroupOnExit)

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
  // Stop the whole boot process group, not only the leader, so no forked child outlives the run.
  if (bootPid !== undefined) {
    await stopBootGroup(bootPid)
    process.off('exit', killBootGroupOnExit)
  }
})

test('T8 Context Doctor filters split identities from the temp refreshed DB', async ({ page }) => {
  test.setTimeout(120_000)
  await page.goto(app)
  await expect(page.getByTestId('page-context-doctor')).toBeVisible()
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

  // Derived harness ids (issue #182): refresh jobs still collect under
  // front-end names, but rollups and runs carry the folded owner. The fold
  // must unite rows, not lose the twin: the cursor-agent job's fixture
  // session (`cursor-agent-1`) ingests under the folded owner.
  for (const harness of DERIVED_HARNESSES) {
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

    await page.getByTestId('breadcrumb-context-doctor').click()
    await expect(page.getByTestId('page-context-doctor')).toBeVisible()
  }

  // Issue #182: folded front-ends appear nowhere as derived ids, and the
  // twin job's records are not lost in the fold.
  const foldedOut = (payload.harnesses as Array<{ harness: string }>).map((entry) => entry.harness)
  expect(foldedOut).not.toContain('cursor-agent')
  expect(foldedOut).not.toContain('claude-desktop')
  const cursorSessions = await page.request.get(`${app}/api/kyber/sessions?harness=cursor`)
  const cursorBody = (await cursorSessions.json()) as {
    sessions: Array<{ session_id?: string; sessionId?: string }>
  }
  expect(cursorBody.sessions.map((s) => s.session_id ?? s.sessionId)).toContain('cursor-agent-1')

  await page.screenshot({ path: join(scratch, 'context-doctor.png'), fullPage: true })

  const emptyDrill = page.getByTestId('drill-harness-cline')
  if (await emptyDrill.count()) {
    await emptyDrill.click()
    await expect(page.getByTestId('page-harness')).toBeVisible()
    await expect(page.getByTestId('harness-runs-table')).toContainText('No runs recorded')
    await expect(page.getByTestId('dimension-unmeasurable').first()).toBeVisible()
    await page.screenshot({ path: join(scratch, 'empty-cline.png'), fullPage: true })
  }
})
