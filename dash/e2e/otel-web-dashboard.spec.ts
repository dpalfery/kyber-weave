import { expect, test, type Locator, type Page } from '@playwright/test'

import {
  USER_CANON,
  postOtlpLogs,
  postOtlpTraces,
  startIsolatedServer,
  waitForOtlpIngest,
  type IsolatedServerHandle,
} from './harness-collection-world.js'
import {
  GEMINI_EXCLUDED_SESSION_ID,
  STORED_OTLP_HARNESS_CONTRACTS,
  buildAllHarnessOtlpFixtures,
  type HarnessExpectationContract,
} from './otel-harness-payloads.js'

/**
 * Isolated end-to-end Playwright suite verifying multi-turn OTLP ingestion (up to 12 turns per
 * harness) and every Web Dashboard surface (Context Doctor, Harness Detail, Sessions, and
 * Agent Session Dashboard) across all 7 stored OTLP harnesses plus the excluded `gemini` check.
 */
test.describe.configure({ mode: 'serial' })

let server: IsolatedServerHandle | undefined

async function navigateToHarnessDetail(page: Page, baseUrl: string, harnessId: string): Promise<void> {
  await page.goto(baseUrl)
  await expect(page.getByTestId('page-context-doctor')).toBeVisible()
  await page.evaluate((id) => {
    const stack = [{ level: 'context-doctor' }, { level: 'harness', harnessId: id }]
    window.history.pushState({ stack }, '', `/harness/${id}`)
    window.dispatchEvent(new PopStateEvent('popstate'))
  }, harnessId)
  await expect(page.getByTestId('page-harness')).toBeVisible()
  await expect(page.getByTestId('page-harness')).toContainText(harnessId)
}

function overviewCardValue(page: Page, cardTitle: string): Locator {
  return page
    .getByTestId('overview-strip-section')
    .getByText(cardTitle, { exact: true })
    .locator('xpath=following-sibling::div[1]')
}

async function assertHarnessSessionDashboard(
  page: Page,
  contract: HarnessExpectationContract,
): Promise<void> {
  await page.getByTestId('nav-tab-sessions').click()
  await expect(page.getByTestId('page-sessions')).toBeVisible()

  await page.getByTestId(`harness-tab-${contract.harness}`).click()
  await expect(page.getByTestId(`harness-tab-${contract.harness}`)).toHaveAttribute('aria-pressed', 'true')

  const row = page.getByTestId(`agent-session-row-${contract.sessionId}`)
  await expect(row).toBeVisible()
  await expect(row).toContainText(`${contract.turns} turns`)
  await row.click()

  const dashboard = page.getByTestId('agent-session-dashboard')
  await expect(dashboard).toBeVisible()
  await expect(dashboard.getByTestId('session-harness-badge')).toHaveText(contract.harness)

  // Notes banner explicitly lists gaps / unexported fields for this harness.
  const notesBanner = dashboard.getByTestId('harness-notes')
  await expect(notesBanner).toBeVisible()
  for (const snippet of contract.expectedGapSubstrings) {
    await expect.soft(notesBanner).toContainText(snippet)
  }

  // Overview Strip: Turns, Spans, Tool Calls, Tokens, Cost, Tools Offered
  await expect(overviewCardValue(page, 'Turns')).toHaveText(String(contract.turns))

  if (contract.tokensMeasurable) {
    const totalInputText = (await overviewCardValue(page, 'Total Input').innerText()).trim()
    expect(totalInputText, `${contract.harness} Total Input must be measured`).not.toBe('—')
    expect(totalInputText).toMatch(/\d/)

    const totalOutputText = (await overviewCardValue(page, 'Total Output').innerText()).trim()
    expect(totalOutputText, `${contract.harness} Total Output must be measured`).not.toBe('—')
    expect(totalOutputText).toMatch(/\d/)

    if (contract.cacheCreationMeasurable) {
      const cacheCreationText = (await overviewCardValue(page, 'Cache Creation').innerText()).trim()
      expect(cacheCreationText, `${contract.harness} Cache Creation must be measured`).not.toBe('—')
    } else {
      await expect(overviewCardValue(page, 'Cache Creation')).toHaveText('—')
    }
  } else {
    await expect(overviewCardValue(page, 'Total Input')).toHaveText('—')
    await expect(overviewCardValue(page, 'Total Output')).toHaveText('—')
    await expect(overviewCardValue(page, 'Cache Read')).toHaveText('—')
    await expect(overviewCardValue(page, 'Cache Creation')).toHaveText('—')
  }

  const toolCallsCard = dashboard.getByTestId('metric-tool-calls')
  if (contract.expectedToolCalls !== null) {
    await expect(toolCallsCard).toHaveAttribute('data-measured', 'true')
    await expect(toolCallsCard).toContainText(String(contract.expectedToolCalls))
  } else {
    await expect(toolCallsCard).toHaveAttribute('data-measured', 'false')
    await expect(toolCallsCard).toContainText('—')
    await expect(toolCallsCard).toContainText(contract.harness)
  }

  // Cost & Basis card + Session Cost Panel
  const costTile = dashboard.getByTestId('metric-cost')
  await expect(costTile).toBeVisible()
  const costPanel = dashboard.getByTestId('session-cost-panel')
  await expect(costPanel).toBeVisible()
  if (contract.expectedCostStatus === 'harness') {
    await expect(costTile).toContainText(/\$/)
    await expect(costTile).toContainText(/harness/i)
    await expect(costPanel).toContainText(/harness/i)
  } else if (contract.expectedCostStatus === 'published_priced') {
    await expect(costTile).toContainText(/\$/)
    await expect(costPanel.getByTestId('cost-basis-card-published')).toBeVisible()
  } else if (contract.expectedCostStatus === 'out_of_scope') {
    await expect(costTile).toContainText('—')
    await expect(costPanel).toContainText(/out of scope/i)
  } else {
    await expect(costTile).toContainText('—')
    await expect(costPanel).toContainText(/no published rate/i)
  }

  // Spend & Context Composition section
  const spendSection = dashboard.getByTestId('spend-composition-section')
  await expect(spendSection).toBeVisible()
  if (contract.tokensMeasurable) {
    await expect(spendSection.getByTestId('turn-spend-chart')).toBeVisible()
    expect(await spendSection.locator('[data-testid^="turn-bar-"]').count()).toBe(contract.turns)
  }

  if (contract.contextMeasurable) {
    await expect(spendSection.getByTestId('context-composition-chart')).toBeVisible()
    expect(await spendSection.locator('[data-testid^="context-turn-"]').count()).toBe(contract.turns)
  } else {
    await expect(spendSection.getByTestId('context-composition-not-measurable')).toBeVisible()
  }

  // Tool & Schema Cost section
  const toolSchemaSection = dashboard.getByTestId('tool-schema-section')
  await expect(toolSchemaSection).toBeVisible()
  if (contract.schemaMeasurable) {
    await expect(toolSchemaSection.getByTestId('schema-cost-ranking')).toBeVisible()
    await expect(toolSchemaSection.getByTestId('tools-ranking-table')).toBeVisible()
    expect(await overviewCardValue(page, 'Tools Offered').innerText()).not.toBe('—')
  } else {
    await expect(toolSchemaSection.getByTestId('schemas-not-exported-banner')).toBeVisible()
    await expect(toolSchemaSection.getByTestId('schema-cost-not-measurable')).toBeVisible()
    await expect(overviewCardValue(page, 'Tools Offered')).toHaveText('—')
  }

  // Skills section
  const skillsSection = dashboard.getByTestId('skills-section')
  await expect(skillsSection).toBeVisible()
  if (contract.skillsMeasurable) {
    await expect(skillsSection.getByTestId('skills-panel')).toBeVisible()
    for (const skill of contract.expectedSkills) {
      await expect(skillsSection.getByTestId('skills-panel')).toContainText(skill)
    }
  } else {
    await expect(skillsSection.getByTestId('skills-not-measurable')).toBeVisible()
  }

  // Execution Timeline & Call Tree (Tree + Duration Bars)
  const timelineSection = dashboard.getByTestId('execution-timeline-section')
  await expect(timelineSection).toBeVisible()
  await expect(timelineSection.getByTestId('timeline-tree-view')).toBeVisible()
  expect(await timelineSection.getByTestId('timeline-node').count()).toBeGreaterThan(0)
  await timelineSection.getByTestId('timeline-tab-bars').click()
  await expect(timelineSection.getByTestId('timeline-bars-view')).toBeVisible()
  expect(await timelineSection.getByTestId('timeline-bar-row').count()).toBeGreaterThan(0)

  // Stored Fields section
  const storedFieldsSection = dashboard.getByTestId('stored-fields-section')
  await expect(storedFieldsSection).toBeVisible()
  await expect(storedFieldsSection.getByTestId('stored-fields-panel')).toBeVisible()
  await expect(storedFieldsSection).toContainText('session')
}

test.beforeAll(async () => {
  test.setTimeout(180_000)
  server = await startIsolatedServer({ startupTimeoutMs: 120_000 })
  expect(server.dbPath).not.toBe(USER_CANON)
  expect(server.url).not.toContain(':4747')

  const fixtures = buildAllHarnessOtlpFixtures()
  for (const fixture of fixtures) {
    await postOtlpTraces(server.url, fixture.tracePayload)
    if (fixture.logPayload !== null) {
      await postOtlpLogs(server.url, fixture.logPayload)
    }
  }

  await waitForOtlpIngest(
    server.url,
    STORED_OTLP_HARNESS_CONTRACTS.map((c) => c.sessionId),
    {
      timeoutMs: 45_000,
      pollIntervalMs: 250,
      expectedHarnesses: STORED_OTLP_HARNESS_CONTRACTS.map((c) => c.harness),
    },
  )
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

  // Session selector includes all 7 stored sessions and excludes gemini.
  await expect(page.getByTestId('context-doctor-session-scope-note')).toContainText(/Findings cover/)
  const sessionSelect = page.getByTestId('context-doctor-session-select')
  await expect(sessionSelect).toBeVisible()
  const optionValues = await sessionSelect.locator('option').evaluateAll((opts) =>
    opts.map((opt) => (opt as HTMLOptionElement).value),
  )
  for (const contract of STORED_OTLP_HARNESS_CONTRACTS) {
    expect(optionValues).toContain(contract.sessionId)
  }
  expect(optionValues).not.toContain(GEMINI_EXCLUDED_SESSION_ID)

  // Filter Context Doctor by a specific stored harness and verify scope note updates.
  await selector.getByTestId('harness-tab-opencode').click()
  await expect(page.getByTestId('context-doctor-session-scope-note')).toContainText(/OpenCode/i)
  await expect(page.getByTestId('findings-scope-note')).toContainText(/OpenCode/i)

  // Unknown harness tab shows honest empty scope in Sessions without leaking other harnesses.
  await selector.getByTestId('harness-tab-unknown').click()
  await page.getByTestId('nav-tab-sessions').click()
  await expect(page.getByTestId('explorer-empty')).toBeVisible()

  // Unfiltered All Harnesses view in Sessions shows all 7 stored sessions and excludes gemini.
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

for (const contract of STORED_OTLP_HARNESS_CONTRACTS) {
  test(`Web Dashboard renders HarnessDetail and AgentSessionDashboard for ${contract.harness} (${contract.turns} turns)`, async ({ page }) => {
    test.setTimeout(180_000)
    const baseUrl = server!.url

    // 1. Verify HarnessDetail page for this harness
    await navigateToHarnessDetail(page, baseUrl, contract.harness)
    await expect(page.getByTestId('harness-session-count')).toContainText('1')
    await expect(page.getByTestId('harness-run-count')).toContainText('1')
    await expect(page.getByTestId('harness-runs-table')).toBeVisible()
    await expect(page.getByTestId(/^drill-run-/).first()).toBeVisible()
    await expect(page.getByTestId('scorecard')).toBeVisible()

    if (contract.tokensMeasurable) {
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

    // 2. Verify Sessions + AgentSessionDashboard sections and explicit gaps
    await assertHarnessSessionDashboard(page, contract)
  })
}
