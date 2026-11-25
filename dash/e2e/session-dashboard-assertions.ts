// Shared Agent Session Dashboard assertions for the isolated OTLP suites (T3), so the web
// dashboard spec here and the per-harness local-file suites (T5-T7) check the same sections
// the same way.
import { expect, type Locator, type Page } from '@playwright/test'

import { SESSION_PAGE_LIMIT } from './harness-collection-world.js'
import type { HarnessExpectationContract } from './otel-harness-payloads.js'

export async function navigateToHarnessDetail(page: Page, baseUrl: string, harnessId: string): Promise<void> {
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

export function overviewCardValue(page: Page, cardTitle: string): Locator {
  return page
    .getByTestId('overview-strip-section')
    .getByText(cardTitle, { exact: true })
    .locator('xpath=following-sibling::div[1]')
}

/**
 * Asserts one session's whole Agent Session Dashboard, section by section, against the harness
 * contract: notes gaps, the overview tiles, tool calls, cost basis, the per-turn chart state
 * (`perTurnTokensMeasurable`), context composition, tool schema, skills, timeline, stored fields,
 * and the models `/api/kyber/sessions?harness=<id>` lists for the session.
 */
export async function assertHarnessSessionDashboard(
  page: Page,
  contract: HarnessExpectationContract,
  baseUrl: string,
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

  // The sessions API lists this session's models for its harness.
  const sessionsResponse = await page.request.get(
    `${baseUrl}/api/kyber/sessions?harness=${encodeURIComponent(contract.harness)}&limit=${SESSION_PAGE_LIMIT}`,
  )
  expect(sessionsResponse.status(), await sessionsResponse.text()).toBe(200)
  const sessionsBody = (await sessionsResponse.json()) as { sessions?: Array<Record<string, unknown>> }
  const sessionRow = (sessionsBody.sessions ?? []).find(
    (candidate) => candidate['session_id'] === contract.sessionId || candidate['sessionId'] === contract.sessionId,
  )
  expect(sessionRow, `session ${contract.sessionId} must be listed under harness ${contract.harness}`).toBeDefined()
  expect(sessionRow?.['models'], `session ${contract.sessionId} must list its contract models`).toEqual(
    contract.expectedModels,
  )

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

  // Per-turn chart state: measurable harnesses draw one measured bar per turn; a harness whose
  // turns carry no counters states that instead, with no bars at all.
  if (contract.perTurnTokensMeasurable) {
    await expect(spendSection.getByTestId('turn-spend-chart')).toBeVisible()
    // Turn bars are keyed by the engine's turn index, whose base is the engine's to choose, so
    // the check counts the bars and the measured bars rather than naming each index.
    expect(await spendSection.locator('[data-testid^="turn-bar-"]').count()).toBe(contract.turns)
    expect(
      await spendSection.locator('[data-testid^="turn-bar-"][data-measured="true"]').count(),
    ).toBe(contract.turns)
    await expect(spendSection.getByTestId('turn-spend-not-measurable')).toHaveCount(0)
  } else {
    await expect(spendSection.getByTestId('turn-spend-not-measurable')).toBeVisible()
    expect(await spendSection.locator('[data-testid^="turn-bar-"]').count()).toBe(0)
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
  // The panel projected the `session` table itself, not just any text that mentions the word.
  await expect(storedFieldsSection.getByTestId('stored-fields-table-session')).toBeVisible()
  await expect(storedFieldsSection.getByTestId('stored-fields-no-tables')).toHaveCount(0)
}
