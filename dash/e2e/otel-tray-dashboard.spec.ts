import { expect, test, type Page } from '@playwright/test'

import {
  SESSION_PAGE_LIMIT,
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
} from './otel-harness-payloads.js'
import {
  installTrayTauriBridge,
  startTrayBridge,
  translateCleanDatabaseScope,
  type TrayBridgeHandle,
} from './otel-tray-bridge.js'

/**
 * Isolated end-to-end Playwright suite verifying the Tray popover UI against live multi-turn
 * OTLP telemetry (up to 12 turns per harness) via a same-origin Node HTTP proxy (/tray/invoke
 * without an Origin header) and a full @tauri-apps/api v2 window.__TAURI_INTERNALS__ bridge.
 */
test.describe.configure({ mode: 'serial' })
test.setTimeout(180_000)

let server: IsolatedServerHandle | undefined
let bridge: TrayBridgeHandle | undefined

test.beforeAll(async () => {
  test.setTimeout(180_000)
  server = await startIsolatedServer({ startupTimeoutMs: 120_000 })
  try {
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
      STORED_OTLP_HARNESS_CONTRACTS.map((contract) => contract.sessionId),
      {
        timeoutMs: 45_000,
        expectedHarnesses: STORED_OTLP_HARNESS_CONTRACTS.map((contract) => contract.harness),
      },
    )

    bridge = await startTrayBridge({ serverUrl: server.url })
    expect(bridge.url).toMatch(/^http:\/\/127\.0\.0\.1:\d+$/)
  } catch (error) {
    await bridge?.stop().catch(() => undefined)
    bridge = undefined
    await server?.stop().catch(() => undefined)
    server = undefined
    throw error
  }
})

test.afterAll(async () => {
  try {
    await bridge?.stop()
  } finally {
    bridge = undefined
    try {
      await server?.stop()
    } finally {
      server = undefined
    }
  }
})

test('ingests all 7 stored harnesses, excludes gemini, and renders initial Tray state', async ({ page }) => {
  for (const contract of STORED_OTLP_HARNESS_CONTRACTS) {
    expect(
      await sessionsPageCarriesSession(page, server!.url, contract.sessionId),
      `the sessions page must carry ${contract.sessionId}`,
    ).toBe(true)
  }
  expect(
    await sessionsPageCarriesSession(page, server!.url, GEMINI_EXCLUDED_SESSION_ID),
    'the gemini session is excluded from the store',
  ).toBe(false)

  const bridgeState = await bridge!.getState()
  expect(bridgeState.viewState.phase).toBe('ready')
  expect(bridgeState.viewState.report).not.toBeNull()

  const pageErrors: string[] = []
  const failedRequests: string[] = []
  page.on('pageerror', (error) => pageErrors.push(error.message))
  page.on('response', (response) => {
    if (response.status() >= 400) {
      failedRequests.push(`${response.status()} ${response.url()}`)
    }
  })

  await installTrayTauriBridge(page, bridge!.url)
  await page.goto(bridge!.url)

  await expect(page.getByTestId('popover')).toBeVisible()
  await expect(page.getByTestId('popover')).toHaveAttribute('data-phase', 'ready')
  await expect(page.getByTestId('ipc-action-error')).toHaveCount(0)

  const select = page.getByTestId('harness-select')
  await expect(page.getByTestId('harness-selector')).toBeVisible()
  await expect(select).toHaveValue('all')

  const optionsText = await select.locator('option').allInnerTexts()
  const optionValues = await select.locator('option').evaluateAll((nodes) =>
    nodes.map((node) => (node as HTMLOptionElement).value),
  )
  for (const contract of STORED_OTLP_HARNESS_CONTRACTS) {
    expect(optionValues).toContain(contract.harness)
    expect(optionsText.some((text) => text.includes(contract.harness) && text.includes('(1)'))).toBe(true)
  }
  expect(optionValues).not.toContain('gemini')

  const footer = page.getByTestId('health-footer')
  await expect(footer).toBeVisible()
  await expect(footer.getByTestId('refresh-age')).toBeVisible()
  await expect(footer.getByTestId('receiver-status')).toContainText('receiver off')
  await expect(footer.getByTestId('collector-status')).toBeVisible()
  await expect(footer.getByTestId('store-counts')).toContainText('quarantined')
  await expect(footer.getByTestId('cost-line')).toBeVisible()

  expect(pageErrors).toEqual([])
  expect(failedRequests).toEqual([])
})

test('switches every harness in HarnessSelector and validates SessionPanel, FindingsList, and HealthFooter', async ({ page }) => {
  await installTrayTauriBridge(page, bridge!.url)
  await page.goto(bridge!.url)

  const select = page.getByTestId('harness-select')
  await expect(select).toBeVisible()

  let sawFindings = false

  for (const contract of STORED_OTLP_HARNESS_CONTRACTS) {
    await select.selectOption(contract.harness)
    await expect(select).toHaveValue(contract.harness)

    const sessionPanel = page.getByTestId('session-panel')
    await expect(sessionPanel).toBeVisible()
    await expect(sessionPanel.getByTestId('open-session')).toContainText(`· ${contract.harness}`)

    const pressure = sessionPanel.getByTestId('session-pressure')
    const bucketList = sessionPanel.getByTestId('bucket-list')
    const residual = bucketList.locator('dd[data-bucket="residual"]')
    await expect(pressure).toBeVisible()
    await expect(bucketList).toBeVisible()
    await expect(residual).toBeVisible()

    if (contract.contextMeasurable) {
      await expect(sessionPanel).toContainText(`turn ${contract.turns}`)
      // opencode and pi fixtures do not report a model context limit, so pressure & window stay explicitly unmeasured while buckets and residual are measured.
      await expect(pressure).toContainText('—')
      await expect(pressure).toContainText('harness did not report model context window')

      const compositionBar = sessionPanel.getByTestId('composition-bar')
      await expect(compositionBar).toBeVisible()
      expect(await compositionBar.locator('[data-bucket]').count()).toBeGreaterThan(0)
      await expect(residual).not.toContainText('—')
      await expect(residual).toContainText(/\d/)
    } else {
      await expect(sessionPanel.getByTestId('composition-bar')).toHaveCount(0)
      await expect(pressure).toContainText('—')
      await expect(residual).toContainText('—')
    }

    const findings = page.getByTestId('findings')
    await expect(findings).toBeVisible()
    const findingCount = await findings.getByTestId('finding').count()
    if (findingCount > 0) {
      sawFindings = true
      await expect(findings.getByTestId('open-finding').first()).toBeVisible()
      await expect(findings.getByTestId('finding-recovery').first()).toBeVisible()
      await expect(findings.getByTestId('finding-recommendation').first()).toBeVisible()
    } else {
      await expect(findings.getByTestId('findings-empty')).toBeVisible()
    }

    const footer = page.getByTestId('health-footer')
    await expect(footer.getByTestId('refresh-age')).toBeVisible()
    await expect(footer.getByTestId('receiver-status')).toBeVisible()
    await expect(footer.getByTestId('collector-status')).toBeVisible()
    await expect(footer.getByTestId('store-counts')).toBeVisible()
    await expect(footer.getByTestId('cost-line')).toBeVisible()
  }

  expect(sawFindings, 'at least one stored OTLP harness must emit Tray findings').toBe(true)

  await select.selectOption('all')
  await expect(select).toHaveValue('all')
  await expect(page.getByTestId('session-panel')).toBeVisible()
  await expect(page.getByTestId('ipc-action-error')).toHaveCount(0)
})

test('dispatches open_view and hide_popover IPC commands', async ({ page }) => {
  await installTrayTauriBridge(page, bridge!.url)
  await page.goto(bridge!.url)
  await expect(page.getByTestId('popover')).toBeVisible()

  await page.getByTestId('open-dashboard').click()
  await page.getByTestId('open-session').click()

  const openFinding = page.getByTestId('open-finding').first()
  if ((await openFinding.count()) > 0) {
    await openFinding.click()
  } else {
    await page.getByTestId('harness-select').selectOption('opencode')
    await expect(page.getByTestId('open-finding').first()).toBeVisible()
    await page.getByTestId('open-finding').first().click()
    await page.getByTestId('harness-select').selectOption('all')
  }

  await page.keyboard.press('Escape')

  await expect.poll(async () => {
    const snapshot = await bridge!.getState()
    return {
      openedViews: snapshot.openedViews.map((entry) => entry.view),
      hidePopoverCount: snapshot.hidePopoverCount,
    }
  }).toEqual({
    openedViews: expect.arrayContaining(['/']),
    hidePopoverCount: expect.any(Number),
  })

  const snapshot = await bridge!.getState()
  expect(snapshot.openedViews.some((entry) => entry.view === '/')).toBe(true)
  expect(snapshot.openedViews.some((entry) => entry.view.startsWith('session/'))).toBe(true)
  expect(snapshot.openedViews.some((entry) => entry.view.startsWith('finding/'))).toBe(true)
  expect(snapshot.hidePopoverCount).toBeGreaterThanOrEqual(1)
})

test('updates SettingsView ?days= window and shared server settings over same-origin proxy', async ({ page }) => {
  await installTrayTauriBridge(page, bridge!.url)
  await page.goto(bridge!.url)
  await expect(page.getByTestId('popover')).toBeVisible()

  await page.getByTestId('open-settings').click()
  await expect(page.getByTestId('settings-view')).toBeVisible()
  await expect(page.getByTestId('settings-kyberdash-version')).toContainText(/KyberDash version \d+\.\d+/)

  const receiverToggle = page.getByTestId('shared-receiver-hosted')
  await expect(receiverToggle).not.toBeChecked()
  await receiverToggle.click()
  await expect(receiverToggle).toBeChecked()
  await expect(page.getByTestId('ipc-action-error')).toHaveCount(0)
  await expect.poll(async () => (await bridge!.getState()).viewState.sharedSettings?.receiverHosted).toBe(true)

  await receiverToggle.click()
  await expect(receiverToggle).not.toBeChecked()
  await expect(page.getByTestId('ipc-action-error')).toHaveCount(0)

  const proxyStatuses = await page.evaluate(async () => {
    const proxiedReport = await fetch('/api/kyber/report?days=7')
    const proxiedPut = await fetch('/api/kyber/settings', {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ refreshCadenceMinutes: 15 }),
    })
    return {
      proxiedReportStatus: proxiedReport.status,
      proxiedPutStatus: proxiedPut.status,
    }
  })
  expect(proxyStatuses.proxiedReportStatus).toBe(200)
  expect(proxyStatuses.proxiedPutStatus).toBe(200)

  const crossOriginRes = await page.request.put(`${server!.url}/api/kyber/settings`, {
    headers: { origin: bridge!.url },
    data: { refreshCadenceMinutes: 20 },
  })
  expect(crossOriginRes.status()).toBe(403)

  const cadenceInput = page.getByTestId('shared-refresh-cadence')
  await cadenceInput.fill('30')
  await page.getByTestId('shared-refresh-cadence-apply').click()
  await expect.poll(async () => (await bridge!.getState()).viewState.sharedSettings?.refreshCadenceMinutes).toBe(30)

  const windowDaysInput = page.getByTestId('window-days')
  await windowDaysInput.fill('1')
  await page.getByTestId('settings-back').click()

  await expect(page.getByTestId('empty-state')).toBeVisible()
  const narrowed = await bridge!.getState()
  expect(narrowed.viewState.settings.windowDays).toBe(1)
  expect((narrowed.viewState.report as { scope?: { days?: number } } | null)?.scope?.days).toBe(1)

  await page.getByTestId('open-settings').click()
  await page.getByTestId('window-days').fill('30')
  await page.getByTestId('settings-back').click()

  await expect(page.getByTestId('session-panel')).toBeVisible()
  const widened = await bridge!.getState()
  expect(widened.viewState.settings.windowDays).toBe(30)
  expect((widened.viewState.report as { scope?: { days?: number } } | null)?.scope?.days).toBe(30)
  await expect(page.getByTestId('ipc-action-error')).toHaveCount(0)
})

test('exercises Maintenance pause/resume, refresh_now, and import_folder_history without 403', async ({ page }) => {
  await installTrayTauriBridge(page, bridge!.url)
  await page.goto(bridge!.url)
  await expect(page.getByTestId('maintenance')).toBeVisible()

  const jobsState = page.getByTestId('jobs-state')
  await expect(jobsState).toHaveAttribute('data-paused', 'false')
  await expect(jobsState).toContainText('Scheduled jobs: running')

  await page.getByTestId('pause-toggle').click()
  await expect(jobsState).toHaveAttribute('data-paused', 'true')
  await expect(jobsState).toContainText('Scheduled jobs: paused')
  await expect(page.getByTestId('jobs-paused-state')).toBeVisible()

  await page.getByTestId('pause-toggle').click()
  await expect(jobsState).toHaveAttribute('data-paused', 'false')
  await expect(page.getByTestId('jobs-paused-state')).toHaveCount(0)

  const beforeRefresh = (await bridge!.getState()).viewState.reportFetchedAt
  await page.getByTestId('refresh-now').click()
  await expect.poll(async () => (await bridge!.getState()).viewState.reportFetchedAt).not.toBe(beforeRefresh)
  await expect(page.getByTestId('ipc-action-error')).toHaveCount(0)

  await page.getByTestId('import-weeks').fill('2')
  await page.getByTestId('import-folder-history').click()
  await expect.poll(async () =>
    (await bridge!.getState()).invocations.some((entry) => entry.cmd === 'import_folder_history' && entry.ok),
  ).toBe(true)
  await expect(page.getByTestId('ipc-action-error')).toHaveCount(0)
})

test('exercises CleanDatabase single-harness and all-harness scopes, plus quit IPC', async ({ page }) => {
  expect(
    translateCleanDatabaseScope({
      kind: 'harnesses',
      harnesses: ['codex'],
      reingestWeeks: 2,
    }),
  ).toEqual({
    scope: { harness: 'codex' },
    harnesses: ['codex'],
    confirm: true,
    reingestWeeks: 2,
  })
  expect(translateCleanDatabaseScope({ kind: 'all' })).toEqual({
    scope: 'all',
    all: true,
    confirm: true,
  })

  await installTrayTauriBridge(page, bridge!.url)
  await page.goto(bridge!.url)

  const select = page.getByTestId('harness-select')
  await select.selectOption('antigravity')
  await expect(page.getByTestId('session-panel')).toBeVisible()

  await page.getByTestId('clean-database-arm').click()
  await expect(page.getByTestId('clean-database-disclosure')).toContainText('Wiping the antigravity harness')
  await page.getByTestId('clean-database-confirm').click()

  await expect(page.getByTestId('empty-state')).toBeVisible()
  await expect(page.getByTestId('ipc-action-error')).toHaveCount(0)

  await select.selectOption('opencode')
  await expect(page.getByTestId('session-panel')).toBeVisible()

  await select.selectOption('all')
  await page.getByTestId('clean-database-arm').click()
  await expect(page.getByTestId('clean-database-disclosure')).toContainText('Wiping all harnesses')
  await page.getByTestId('clean-database-confirm').click()

  await expect(page.getByTestId('empty-state')).toBeVisible()
  await expect(page.getByTestId('ipc-action-error')).toHaveCount(0)

  const sessionsAfterClean = await page.request.get(`${server!.url}/api/kyber/sessions?limit=${SESSION_PAGE_LIMIT}`)
  expect(sessionsAfterClean.status()).toBe(200)
  const sessionsAfterJson = (await sessionsAfterClean.json()) as {
    sessions?: unknown[]
    total?: number
    page?: number
    limit?: number
  }
  expect(sessionsAfterJson.sessions ?? []).toEqual([])
  expect(sessionsAfterJson.total, 'the clean leaves the store with no session to page').toBe(0)

  await page.getByTestId('quit').click()
  await expect.poll(async () => (await bridge!.getState()).quitCount).toBeGreaterThanOrEqual(1)
})

/**
 * Whether the sessions page carries the session `sessionId`, read through the page's own search.
 *
 * The list is paged, so asking for the id is the read that cannot miss a row the corpus ordered
 * past the first window.
 */
async function sessionsPageCarriesSession(page: Page, baseUrl: string, sessionId: string): Promise<boolean> {
  const response = await page.request.get(
    `${baseUrl}/api/kyber/sessions?q=${encodeURIComponent(sessionId)}&limit=${SESSION_PAGE_LIMIT}`,
  )
  expect(response.status(), await response.text()).toBe(200)
  const body = (await response.json()) as {
    sessions?: Array<{ session_id?: string; sessionId?: string }>
    total?: number
    page?: number
    limit?: number
  }
  return (body.sessions ?? []).some((row) => (row.session_id ?? row.sessionId) === sessionId)
}
