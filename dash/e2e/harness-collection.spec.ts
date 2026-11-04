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
import {
  FACTORY_CONTENT_OFF_SESSION_ID,
  FACTORY_CONTENT_ON_SESSION_ID,
  FACTORY_REPLAY_MODEL,
  FACTORY_REPLAY_PROMPT,
  FACTORY_REPLAY_TOOL,
} from './factory-replay-world.js'

/**
 * Harness-collection completeness predicate.
 *
 * Isolated browser acceptance. Starts its own dashboard against a temp DB from
 * the world. Does not use the live 4747 spine host.
 *
 * Capture proof comes from two replays: the OpenCode 2.2.1 replay and the
 * Factory (Droid) replay below. Each world runs its recorded payload through
 * the production plugin, receiver decode, canonical ingest and projection
 * before the dashboard starts, so the session the round-trip case reads was
 * derived, not seeded. The honest-empty `opencode` row and the measured
 * `claude-code` row are rendering-only seeds and prove no capture.
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
 *
 * `baseUrl` is the dashboard the harness is opened on. The harness matrix and the
 * Factory replay run on dashboards of their own, each against its own temp store.
 */
async function openHarness(
  page: Page,
  harnessId: string,
  baseUrl: string = app,
): Promise<{ status: number; body: string; runs: unknown[] }> {
  const responsePromise = page.waitForResponse(
    (response) => response.url().includes('/api/kyber/runs'),
    { timeout: 30_000 },
  )
  await page.goto(baseUrl)
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

/**
 * The Factory (Droid) capture replay.
 *
 * REPLAY EVIDENCE, NOT LIVE CAPTURE. The payloads under `dash/e2e/fixtures/factory/` are recorded
 * Factory hook events, replayed through the production hook runner, the production loopback
 * receiver, the droid canon adapter, the canonical projection and this dashboard. No Droid agent
 * runs, so nothing in this block is proof that a live Factory turn is captured; the live check is a
 * separate, owner-run procedure.
 *
 * It runs on a dashboard of its own, against a temp store of its own: the `droid` harness is the
 * zero-record harness of the scenarios above, and this replay gives that harness rows.
 */
test.describe('the Factory (Droid) replay', () => {
  let factoryChild: ChildProcess | undefined
  let factoryBootPid: number | undefined
  let factoryApp = ''
  let factoryDbPath = ''

  /** Process 'exit' fallback for the Factory boot, as the harness-matrix boot has. */
  const killFactoryBootGroupOnExit = (): void => {
    if (factoryBootPid === undefined || !bootGroupAlive(factoryBootPid)) return
    try {
      signalBootGroup(factoryBootPid, 'SIGKILL')
    } catch {
      // The process is exiting; there is nothing left that could act on the error.
    }
  }

  test.beforeAll(async () => {
    test.setTimeout(180_000)
    factoryChild = spawn('npx', ['tsx', 'e2e/factory-replay-boot.ts'], {
      cwd: dashRoot,
      env: {
        ...process.env,
        NODE_OPTIONS: '--no-deprecation',
      },
      // Leads its own process group so teardown can reach every process the boot forks.
      detached: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    })
    factoryBootPid = factoryChild.pid
    process.once('exit', killFactoryBootGroupOnExit)

    factoryApp = await new Promise<string>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('the Factory replay dashboard did not become ready')), 150_000)
      let stdout = ''
      const onData = (chunk: Buffer) => {
        stdout += chunk.toString()
        const ready = stdout.match(/FR_READY (http:\/\/127\.0\.0\.1:\d+)/)
        const db = stdout.match(/FR_DB (.+)/)
        if (db) factoryDbPath = db[1]!.trim()
        if (ready) {
          clearTimeout(timer)
          factoryChild?.stdout?.off('data', onData)
          resolve(ready[1]!)
        }
      }
      factoryChild!.stdout!.on('data', onData)
      factoryChild!.stderr!.on('data', (chunk: Buffer) => {
        stdout += chunk.toString()
      })
      factoryChild!.on('exit', (code) => {
        if (!factoryApp) reject(new Error(`the Factory replay boot exited ${code}: ${stdout}`))
      })
      // Without this, a spawn failure (no `npx` on PATH, EACCES) arrives as an
      // unhandled 'error' event on the child and crashes the worker instead of
      // failing this hook with the reason.
      factoryChild!.on('error', (error) => {
        clearTimeout(timer)
        reject(new Error(`the Factory replay boot could not start: ${String(error)}`))
      })
    })

    expect(factoryDbPath).not.toBe(USER_CANON)
    expect(factoryApp).not.toContain(':4747')
  })

  test.afterAll(async () => {
    // SIGTERM is a request, not a completion: stop the whole boot process group and
    // wait for it to empty before deleting the store it was serving from.
    if (factoryBootPid !== undefined) {
      await stopBootGroup(factoryBootPid)
      process.off('exit', killFactoryBootGroupOnExit)
    }
    if (factoryDbPath !== '') {
      const root = dirname(factoryDbPath)
      if (root.startsWith(tmpdir()) && !root.includes('.kyberdash')) {
        rmSync(root, { recursive: true, force: true })
      }
    }
  })

  test('Factory replay: the recorded session renders its model, its tool and an honest token state', async ({ page }) => {
    test.setTimeout(120_000)
    // REPLAY, not live capture: the payloads are recorded hook events, and no Droid agent runs.
    const runs = await openHarness(page, 'droid', factoryApp)
    expect(runs.status, runs.body).toBe(200)

    // The harness page states what it cannot measure rather than printing a zero:
    // every dimension of the Factory harness is unmeasurable, and every one of them
    // carries a reason.
    const unmeasurable = page.getByTestId('dimension-unmeasurable')
    expect(await unmeasurable.count(), 'the Factory harness measures nothing yet').toBeGreaterThan(0)
    const reason = page.getByTestId(/^dimension-reason-/).first()
    await expect(reason).toBeVisible()
    await expect(reason).not.toBeEmpty()

    // Sessions tab: the replayed session, found by its real session id.
    await page.getByTestId('nav-tab-sessions').click()
    await expect(page.getByTestId('page-sessions')).toBeVisible()
    const row = page.getByTestId(`agent-session-row-${FACTORY_CONTENT_OFF_SESSION_ID}`)
    await expect(row).toBeVisible()
    await row.click()

    // The tool the recorded tool payload named: one invocation of it.
    await expect(page.getByTestId('metric-tool-calls')).toContainText('1')
    await expect(page.getByTestId('schema-cost-ranking')).toContainText(FACTORY_REPLAY_TOOL)

    // The token state is honest: the input figure is the em dash, never a zero and
    // never a number borrowed from another session.
    //
    // This is the PLUGIN-ONLY case. The replay writes no companion settings file beside the
    // session, so nothing pairs with it and the session keeps the plugin's own declaration:
    // the token usage is not measurable, with a reason that names the harness. (The paired
    // case — a companion file naming the same session id — carries that file's counters
    // instead, and is pinned by the canon pairing rules, not here.)
    const totalInput = page
      .getByTestId('overview-strip-section')
      .getByText('Total Input')
      .locator('xpath=following-sibling::div[1]')
    await expect(totalInput).toHaveText('—')

    // The reason the figure is the em dash is stated, not implied: the dashboard's own
    // session row declares the token usage not measurable and names the harness.
    const declared = await sessionTokenState(page, factoryApp, FACTORY_CONTENT_OFF_SESSION_ID)
    expect(declared.availability, 'a plugin row carries no token counter').toBe('not_measurable')
    expect(declared.reason, 'the reason must name the harness').toMatch(/droid/i)
    expect(declared.reason, 'the reason must name the metric').toMatch(/token/i)

    // Content OFF: the prompt text is nowhere in the rendered session, and the captured
    // span's own attributes carry none either.
    await expect(page.getByTestId('page-sessions')).not.toContainText(FACTORY_REPLAY_PROMPT)

    // The model the recorded prompt payload named reaches the dashboard: the session
    // declares it, and the span inspector renders it.
    const declaredModels = await sessionModels(page, factoryApp, FACTORY_CONTENT_OFF_SESSION_ID)
    expect(declaredModels, 'the session must declare the model its prompt payload named').toContain(
      FACTORY_REPLAY_MODEL,
    )
    await openPromptTurnInspector(page)
    await expect(page.getByTestId('span-inspector')).toContainText(FACTORY_REPLAY_MODEL)
    await expect(page.getByTestId('span-inspector')).not.toContainText(FACTORY_REPLAY_PROMPT)
  })

  test('Factory replay: the consent-on session carries the prompt text the consent allowed', async ({ page }) => {
    test.setTimeout(120_000)
    // REPLAY, not live capture: the payloads are recorded hook events, and no Droid agent runs.
    await openHarness(page, 'droid', factoryApp)

    await page.getByTestId('nav-tab-sessions').click()
    await expect(page.getByTestId('page-sessions')).toBeVisible()
    const row = page.getByTestId(`agent-session-row-${FACTORY_CONTENT_ON_SESSION_ID}`)
    await expect(row).toBeVisible()
    await row.click()

    // The same span inspector as the consent-off session, on the session the consent file
    // allowed the prompt text for: the prompt the user submitted is among the attributes
    // the plugin emitted, because the consent said it may travel.
    await openPromptTurnInspector(page)
    await expect(page.getByTestId('span-inspector')).toContainText(FACTORY_REPLAY_MODEL)
    await expect(page.getByTestId('span-inspector')).toContainText(FACTORY_REPLAY_PROMPT)

    // And the prompt is the session's own content, not only an attribute on the
    // raw span: the session payload's request carries what was asked.
    const response = await page.request.get(`${factoryApp}/api/kyber/session/${FACTORY_CONTENT_ON_SESSION_ID}`)
    expect(response.status(), await response.text()).toBe(200)
    const payload = (await response.json()) as {
      requests?: Array<{ prompt?: unknown }>
    }
    const prompt = payload.requests?.[0]?.prompt
    expect(typeof prompt, 'the session payload must carry the prompt the consent allowed').toBe('string')
    expect(prompt as string).toContain(FACTORY_REPLAY_PROMPT)
  })
})

/**
 * Open the dashboard's span inspector for the session's prompt turn.
 *
 * The timeline lists one node per captured span, and the prompt turn is the first
 * `factory.turn` node: the recorded prompt was submitted before the turn finished. The
 * inspector is where the dashboard renders what the plugin actually emitted, so it is
 * where the model and the captured prompt text are read.
 */
async function openPromptTurnInspector(page: Page): Promise<void> {
  const node = page.getByTestId('timeline-node').filter({ hasText: 'factory.turn' }).first()
  await expect(node, 'the replayed session must list its prompt turn in the timeline').toBeVisible()
  await node.click()
  await expect(page.getByTestId('span-inspector')).toBeVisible()
}

/**
 * The token state the dashboard declares for one session, as its own sessions answer
 * reports it. The answer is read rather than the page: a not-measurable declaration is
 * a payload fact, and the page renders it as the em dash.
 */
async function sessionTokenState(
  page: Page,
  baseUrl: string,
  sessionId: string,
): Promise<{ availability: string; reason: string }> {
  const response = await page.request.get(`${baseUrl}/api/kyber/sessions`)
  expect(response.status(), 'the sessions answer must be the dashboard own').toBe(200)
  const body = (await response.json()) as {
    sessions?: Array<{ session_id?: string; total_input?: unknown }>
  }
  const session = body.sessions?.find((row) => row.session_id === sessionId)
  expect(session, `the sessions answer must carry ${sessionId}`).toBeDefined()
  const total = session?.total_input as { availability?: string; reason?: string } | undefined
  expect(total, `${sessionId} must declare a token state`).toBeDefined()
  return {
    availability: total?.availability ?? 'measured',
    reason: total?.reason ?? '',
  }
}

/**
 * The models the dashboard declares for one session, as its own sessions answer reports
 * them. The model the plugin captured travels as the session's own attribute.
 */
async function sessionModels(page: Page, baseUrl: string, sessionId: string): Promise<string[]> {
  const response = await page.request.get(`${baseUrl}/api/kyber/sessions`)
  expect(response.status(), 'the sessions answer must be the dashboard own').toBe(200)
  const body = (await response.json()) as {
    sessions?: Array<{ session_id?: string; models?: unknown }>
  }
  const session = body.sessions?.find((row) => row.session_id === sessionId)
  expect(session, `the sessions answer must carry ${sessionId}`).toBeDefined()
  const models = session?.models
  return Array.isArray(models) ? models.filter((model): model is string => typeof model === 'string') : []
}
