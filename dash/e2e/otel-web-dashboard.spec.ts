import { expect, test, type Page } from '@playwright/test'
import { mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'

import {
  USER_CANON,
  postOtlpLogs,
  postOtlpTraces,
  postRefresh,
  startIsolatedServer,
  waitForOtlpIngest,
  waitForPairedDroidSession,
  type IsolatedServerHandle,
} from './harness-collection-world.js'
import {
  GEMINI_EXCLUDED_SESSION_ID,
  STORED_OTLP_HARNESS_CONTRACTS,
  buildAllHarnessOtlpFixtures,
  buildDroidSessionFiles,
  type DroidSessionFilePair,
  type HarnessExpectationContract,
} from './otel-harness-payloads.js'
import {
  assertHarnessSessionDashboard,
  navigateToHarnessDetail,
} from './session-dashboard-assertions.js'

/**
 * Isolated end-to-end Playwright suite verifying multi-turn OTLP ingestion — two sessions of at
 * least 10 turns per harness (T3) — and every Web Dashboard surface (Context Doctor, Harness
 * Detail, Sessions, and Agent Session Dashboard) across all 7 stored OTLP harnesses plus the
 * excluded `gemini` check.
 */
test.describe.configure({ mode: 'serial' })

let server: IsolatedServerHandle | undefined

/** The harness's session contracts, in fixture order (the `a` session first, `b` second). */
const CONTRACTS_BY_HARNESS = new Map<string, HarnessExpectationContract[]>()
for (const contract of STORED_OTLP_HARNESS_CONTRACTS) {
  const group = CONTRACTS_BY_HARNESS.get(contract.harness) ?? []
  group.push(contract)
  CONTRACTS_BY_HARNESS.set(contract.harness, group)
}

/** Writes a paired Droid session's transcript and companion under the server's isolated home. */
function writeDroidSessionFiles(homeDir: string, files: DroidSessionFilePair): void {
  const transcriptPath = join(homeDir, files.transcriptPath)
  mkdirSync(dirname(transcriptPath), { recursive: true })
  writeFileSync(transcriptPath, files.transcript)
  writeFileSync(join(homeDir, files.companionPath), files.companion)
}

test.beforeAll(async () => {
  test.setTimeout(180_000)
  server = await startIsolatedServer({ startupTimeoutMs: 120_000 })
  expect(server.dbPath).not.toBe(USER_CANON)
  expect(server.url).not.toContain(':4747')

  // Paired Droid sessions read their transcript and companion from the isolated home on the
  // refresh, so the files exist before anything is posted.
  for (const contract of STORED_OTLP_HARNESS_CONTRACTS) {
    if (contract.pairedWithSessionFiles) {
      writeDroidSessionFiles(server.homeDir, buildDroidSessionFiles(contract))
    }
  }

  const fixtures = buildAllHarnessOtlpFixtures()
  for (const fixture of fixtures) {
    await postOtlpTraces(server.url, fixture.tracePayload)
    if (fixture.logPayload !== null) {
      await postOtlpLogs(server.url, fixture.logPayload)
    }
  }

  const postedSessionIds = fixtures
    .filter((fixture) => fixture.harness !== 'gemini')
    .map((fixture) => fixture.sessionId)
  await waitForOtlpIngest(server.url, postedSessionIds, {
    timeoutMs: 45_000,
    pollIntervalMs: 250,
    expectedHarnesses: [...new Set(fixtures.filter((fixture) => fixture.harness !== 'gemini').map((fixture) => fixture.harness))],
  })

  // The refresh reads the paired Droid session files; the pairing must have landed before the
  // session dashboards are asserted.
  await postRefresh(server.url)
  for (const contract of STORED_OTLP_HARNESS_CONTRACTS) {
    if (contract.pairedWithSessionFiles) {
      await waitForPairedDroidSession(server.url, contract.sessionId)
    }
  }
})

test.afterAll(async () => {
  if (server) {
    await server.stop()
    server = undefined
  }
})

test('Context Doctor, All Harnesses view, and Gemini exclusion behave honestly', async ({ page }) => {
  test.setTimeout(180_000)
  const baseUrl = server!.url

  await page.goto(baseUrl)
  await expect(page.getByTestId('page-context-doctor')).toBeVisible()

  // All 7 stored OTLP harnesses appear in the tab strip; gemini is excluded.
  const selector = page.getByTestId('harness-selector')
  await expect(selector).toBeVisible()
  await expect(selector.getByTestId('harness-tab-all')).toBeVisible()
  for (const contract of STORED_OTLP_HARNESS_CONTRACTS) {
    await expect(selector.getByTestId(`harness-tab-${contract.harness}`)).toBeVisible()
  }
  await expect(selector.getByTestId('harness-tab-gemini')).toHaveCount(0)

  // Session picker includes every stored session and excludes gemini. The
  // picker is a search combobox over the store's paged answer (Task T3): its
  // options render while it is open.
  await expect(page.getByTestId('context-doctor-session-scope-note')).toContainText(/Findings cover/)
  const sessionCombobox = page.getByTestId('context-doctor-session-combobox')
  await expect(sessionCombobox).toBeVisible()
  await sessionCombobox.click()
  const sessionListbox = page.getByTestId('context-doctor-session-listbox')
  await expect(sessionListbox).toBeVisible()
  for (const contract of STORED_OTLP_HARNESS_CONTRACTS) {
    await expect(sessionListbox.getByTestId(`session-option-${contract.sessionId}`)).toBeVisible()
  }
  await expect(sessionListbox.getByTestId(`session-option-${GEMINI_EXCLUDED_SESSION_ID}`)).toHaveCount(0)

  // Filter Context Doctor by a specific stored harness and verify scope note updates.
  await selector.getByTestId('harness-tab-opencode').click()
  await expect(page.getByTestId('context-doctor-session-scope-note')).toContainText(/OpenCode/i)
  await expect(page.getByTestId('findings-scope-note')).toContainText(/OpenCode/i)

  // Unknown harness tab shows honest empty scope in Sessions without leaking other harnesses.
  await selector.getByTestId('harness-tab-unknown').click()
  await page.getByTestId('nav-tab-sessions').click()
  await expect(page.getByTestId('explorer-empty')).toBeVisible()

  // Unfiltered All Harnesses view in Sessions shows every stored session and excludes gemini.
  await selector.getByTestId('harness-tab-all').click()
  for (const contract of STORED_OTLP_HARNESS_CONTRACTS) {
    await expect(page.getByTestId(`agent-session-row-${contract.sessionId}`)).toBeVisible()
  }
  await expect(page.getByTestId(`agent-session-row-${GEMINI_EXCLUDED_SESSION_ID}`)).toHaveCount(0)

  // Direct navigation to /harness/gemini confirms no stored record exists.
  await page.evaluate(() => {
    const stack = [{ level: 'context-doctor' }, { level: 'harness', harnessId: 'gemini' }]
    window.history.pushState({ stack }, '', '/harness/gemini')
    window.dispatchEvent(new PopStateEvent('popstate'))
  })
  await expect(page.getByTestId('not-found-panel')).toBeVisible()
  await expect(page.getByTestId('not-found-panel')).toContainText('gemini')
})

/** Reads the run count the harness detail API reports for `harnessId`. */
async function fetchHarnessRunCount(page: Page, baseUrl: string, harnessId: string): Promise<number> {
  const response = await page.request.get(`${baseUrl}/api/kyber/harness/${encodeURIComponent(harnessId)}`)
  expect(response.status(), await response.text()).toBe(200)
  const body = (await response.json()) as { runCount?: unknown }
  expect(body.runCount, `harness ${harnessId} must report a run count`).toEqual(expect.any(Number))
  const runCount = body.runCount as number
  expect(runCount, `harness ${harnessId} must have recorded runs`).toBeGreaterThan(0)
  return runCount
}

for (const [harnessId, contracts] of CONTRACTS_BY_HARNESS) {
  test(`Web Dashboard renders both sessions of ${harnessId} and their HarnessDetail sections (${contracts.map((contract) => contract.turns).join(' + ')} turns)`, async ({ page }) => {
    test.setTimeout(180_000)
    const baseUrl = server!.url

    await navigateToHarnessDetail(page, baseUrl, harnessId)

    // Each harness's session dashboards, section by section, for both sessions.
    for (const contract of contracts) {
      await assertHarnessSessionDashboard(page, contract, baseUrl)
    }

    // Harness Detail: two sessions per harness (T3), and the run count the API reports.
    await navigateToHarnessDetail(page, baseUrl, harnessId)
    await expect(page.getByTestId('harness-session-count')).toContainText(String(contracts.length))
    const runCount = await fetchHarnessRunCount(page, baseUrl, harnessId)
    await expect(page.getByTestId('harness-run-count')).toContainText(String(runCount))
    await expect(page.getByTestId('harness-runs-table')).toBeVisible()
    await expect(page.getByTestId(/^drill-run-/).first()).toBeVisible()
    await expect(page.getByTestId('scorecard')).toBeVisible()

    // The harness measures tokens when any of its sessions measures them; otherwise every
    // dimension names why it is unmeasurable.
    if (contracts.some((contract) => contract.tokensMeasurable)) {
      expect(await page.locator('[data-testid="dimension-value"]').count()).toBeGreaterThan(0)
    } else {
      expect(await page.locator('[data-testid="dimension-unmeasurable"]').count()).toBeGreaterThan(0)
      await expect(page.getByTestId(/^dimension-reason-/).first()).toBeVisible()
    }

    // Ensure no unmeasurable cell displays a lying zero.
    const numericUnmeasurable = page
      .locator('[data-testid="dimension-unmeasurable"]')
      .filter({ hasText: /^\s*\$?0(\.0+)?\s*(%|tok|x)?\s*$/ })
    expect(await numericUnmeasurable.count()).toBe(0)
  })
}
