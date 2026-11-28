import { expect, test, type Page } from '@playwright/test'
import { mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'

import {
  COPILOT_BASELINE_CONTRACT,
  GEMINI_EXCLUDED_SESSION_ID,
  STORED_OTLP_HARNESS_CONTRACTS,
  buildAllHarnessOtlpFixtures,
  buildCopilotBaselineFixture,
  buildDroidSessionFiles,
  type DroidSessionFilePair,
  type HarnessExpectationContract,
  type OtlpExportLogsServiceRequest,
  type OtlpExportTraceServiceRequest,
} from './otel-harness-payloads.js'
import {
  FILE_HARNESS_FIXTURES,
  type FileHarnessContract,
  type FileHarnessFixture,
} from './file-harness-contracts.js'
import { buildClaudeParityFixture, buildCopilotCliParityFixture, buildDroidParityFixture } from './file-harness-jsonl.js'
import { buildUnreadableSqliteFixtures } from './file-harness-sqlite.js'
import { postImportHistory, writeHarnessFiles } from './file-harness-world.js'
import {
  SESSION_PAGE_LIMIT,
  SECTION_PAGE_LIMIT,
  USER_CANON,
  postOtlpLogs,
  postOtlpTraces,
  postRefresh,
  startIsolatedServer,
  waitForOtlpIngest,
  type IsolatedServerHandle,
} from './harness-collection-world.js'
import { getParityExpectation, normalizeParityHarnessId } from './copilot-parity-baseline.js'
import {
  assertHarnessFullContentParity,
  type ContentParityResult,
} from './session-dashboard-assertions.js'

/**
 * Isolated cross-harness content parity suite.
 *
 * Compares every HTTP-testable harness against the sanitized dual Copilot baseline (the `copilot`
 * OTLP shape plus the `copilot-cli` file-transcript shape) across the four layers where content can
 * be lost: collection, projection, the API payload, and the rendered dashboard. Each harness runs
 * through `assertHarnessFullContentParity`, which records a structured gap annotation for every
 * honest empty state and fails on any content drop the harness parity matrix does not already name.
 *
 * Two servers are booted: server #1 receives the healthy fixtures (21 ingestible harnesses plus the
 * excluded `gemini`), and server #2 — with its own root, home, state and database — receives the
 * corrupt SQLite sources and the unclaimed/unattributed OTLP signals, so neither can overwrite the
 * other's fixed OS SQLite paths. Both assert they are not serving the operator's own `canon.db`.
 */
test.describe.configure({ mode: 'serial' })

let healthy: IsolatedServerHandle | undefined
let broken: IsolatedServerHandle | undefined

const BASE_TIME_MS = Math.floor(Date.now() / 60_000) * 60_000 - 3_600_000

/** A file fixture's contract, named as the dashboard stores it. */
function asAssertionContract(contract: FileHarnessContract): HarnessExpectationContract {
  return contract as unknown as HarnessExpectationContract
}

/** The harnesses server #1 ingests: every evidenced reader plus the OTLP-only harnesses. */
const HEALTHY_FILE_FIXTURES: readonly FileHarnessFixture[] = FILE_HARNESS_FIXTURES
/** The three parity fixtures added for the baseline comparison, one session each. */
const PARITY_FIXTURES: readonly FileHarnessFixture[] = [
  buildCopilotCliParityFixture(),
  buildDroidParityFixture(),
  buildClaudeParityFixture(),
]

/** Every file fixture server #1 writes, parity fixtures included. */
const ALL_FILE_FIXTURES: readonly FileHarnessFixture[] = [...HEALTHY_FILE_FIXTURES, ...PARITY_FIXTURES]

/** Writes a paired Droid session's transcript and companion under the server's isolated home. */
function writeDroidSessionFiles(homeDir: string, files: DroidSessionFilePair): void {
  const transcriptPath = join(homeDir, files.transcriptPath)
  mkdirSync(dirname(transcriptPath), { recursive: true })
  writeFileSync(transcriptPath, files.transcript)
  writeFileSync(join(homeDir, files.companionPath), files.companion)
}

/** One unclaimed OTLP span: no supported namespace votes for it, so it lands in quarantine. */
function unclaimedSpanPayload(): OtlpExportTraceServiceRequest {
  const nowNs = String(BigInt(Date.now()) * 1_000_000n)
  return {
    resourceSpans: [
      {
        resource: { attributes: [{ key: 'service.name', value: { stringValue: 'parity-unknown-vendor' } }] },
        scopeSpans: [
          {
            scope: { name: 'parity.unknown', version: '1.0.0' },
            spans: [
              {
                traceId: 'feedface000000000000000000000001',
                spanId: 'feedface00000001',
                name: 'parity.mystery.operation',
                kind: 'SPAN_KIND_INTERNAL',
                startTimeUnixNano: nowNs,
                endTimeUnixNano: nowNs,
                status: { code: 'STATUS_CODE_OK' },
                attributes: [{ key: 'parity.mystery.attribute', value: { stringValue: 'synthetic' } }],
              },
            ],
          },
        ],
      },
    ],
  }
}

/** One unattributed OTLP log: no supported namespace claims it, so it lands as a problem row. */
function unattributedLogPayload(): OtlpExportLogsServiceRequest {
  return {
    resourceLogs: [
      {
        resource: { attributes: [{ key: 'service.name', value: { stringValue: 'parity-unknown-vendor' } }] },
        scopeLogs: [
          {
            scope: { name: 'parity.unknown', version: '1.0.0' },
            logRecords: [
              {
                timeUnixNano: String(BigInt(Date.now()) * 1_000_000n),
                body: { stringValue: 'synthetic unattributed log line' },
                attributes: [{ key: 'parity.mystery.attribute', value: { stringValue: 'synthetic' } }],
              },
            ],
          },
        ],
      },
    ],
  }
}

/** Records every gap annotation a parity result produced on the running test. */
function annotateGaps(result: ContentParityResult): void {
  for (const gap of result.gaps) {
    test.info().annotations.push({
      type: `parity-${gap.code}`,
      description: `${gap.harness}/${gap.sessionId} [${gap.dimension}] ${gap.detail}`,
    })
  }
}

/** Opens the dashboard's Sessions view for `harnessId`, failing if the tab is absent. */
async function openHarnessSessions(page: Page, baseUrl: string, harnessId: string): Promise<void> {
  await page.goto(baseUrl)
  await expect(page.getByTestId('page-context-doctor')).toBeVisible()
  await page.getByTestId('nav-tab-sessions').click()
  await expect(page.getByTestId('page-sessions')).toBeVisible()
  await expect(page.getByTestId(`harness-tab-${harnessId}`)).toBeVisible()
  await page.getByTestId(`harness-tab-${harnessId}`).click()
  await expect(page.getByTestId(`harness-tab-${harnessId}`)).toHaveAttribute('aria-pressed', 'true')
}

/** Reads one page of a problem page envelope, filtered by `query` when supplied. */
async function readProblems(
  page: Page,
  baseUrl: string,
  query: string,
): Promise<{ total: number; codes: ReadonlyArray<{ code: string; harness: string | null }> }> {
  const response = await page.request.get(`${baseUrl}/api/kyber/problems?limit=200${query}`)
  expect(response.status(), await response.text()).toBe(200)
  const body = (await response.json()) as {
    problems?: Array<Record<string, unknown>>
    total?: unknown
  }
  const rows = Array.isArray(body.problems) ? body.problems : []
  return {
    total: typeof body.total === 'number' ? body.total : rows.length,
    codes: rows.map((row) => ({
      code: String(row['code'] ?? ''),
      harness: typeof row['harness'] === 'string' ? row['harness'] : null,
    })),
  }
}

test.beforeAll(async () => {
  test.setTimeout(480_000)

  // Server #1: the healthy corpus, the dual Copilot baseline and every other ingestible harness.
  healthy = await startIsolatedServer({ startupTimeoutMs: 120_000 })
  expect(healthy.dbPath).not.toBe(USER_CANON)
  expect(healthy.url).not.toContain(':4747')

  for (const fixture of ALL_FILE_FIXTURES) {
    if (fixture.files) {
      writeHarnessFiles(healthy.homeDir, fixture.files(BASE_TIME_MS))
    }
    fixture.build?.(healthy.homeDir, BASE_TIME_MS)
  }

  // The paired Droid sessions read their transcript and companion from the isolated home on the
  // refresh, so the files exist before anything is posted.
  for (const contract of STORED_OTLP_HARNESS_CONTRACTS) {
    if (contract.pairedWithSessionFiles) {
      writeDroidSessionFiles(healthy.homeDir, buildDroidSessionFiles(contract))
    }
  }

  const otlpFixtures = [
    ...buildAllHarnessOtlpFixtures(),
    buildCopilotBaselineFixture(),
  ]
  for (const fixture of otlpFixtures) {
    await postOtlpTraces(healthy.url, fixture.tracePayload)
    if (fixture.logPayload !== null) {
      await postOtlpLogs(healthy.url, fixture.logPayload)
    }
  }

  const expectedOtlpFixtures = otlpFixtures.filter((fixture) => fixture.harness !== 'gemini')
  await waitForOtlpIngest(
    healthy.url,
    expectedOtlpFixtures.map((fixture) => fixture.sessionId),
    {
      timeoutMs: 60_000,
      pollIntervalMs: 250,
      expectedHarnesses: [...new Set(expectedOtlpFixtures.map((fixture) => fixture.harness))],
    },
  )
  await postRefresh(healthy.url)
  await postImportHistory(healthy.url)

  // Wait until every file fixture's session is listed, so no dashboard read races the import.
  const expectedByHarness = new Map<string, string[]>()
  for (const fixture of ALL_FILE_FIXTURES) {
    const group = expectedByHarness.get(fixture.contract.harness) ?? []
    group.push(fixture.contract.sessionId)
    expectedByHarness.set(fixture.contract.harness, group)
  }
  const deadline = Date.now() + 120_000
  const missing = new Set(ALL_FILE_FIXTURES.map((fixture) => `${fixture.contract.harness}/${fixture.contract.sessionId}`))
  while (missing.size > 0 && Date.now() < deadline) {
    for (const [harness, sessionIds] of expectedByHarness) {
      const response = await fetch(
        `${healthy.url}/api/kyber/sessions?harness=${encodeURIComponent(harness)}&limit=${SESSION_PAGE_LIMIT}`,
      )
      if (!response.ok) continue
      const body = (await response.json()) as { sessions?: Array<Record<string, unknown>> }
      const listed = new Set(
        (body.sessions ?? []).map((row) => String(row['session_id'] ?? row['sessionId'] ?? '')),
      )
      for (const sessionId of sessionIds) {
        if (listed.has(sessionId)) missing.delete(`${harness}/${sessionId}`)
      }
    }
    if (missing.size > 0) {
      await new Promise<void>((resolveWait) => setTimeout(resolveWait, 500))
    }
  }
  expect([...missing], 'every file fixture must be imported and listed before the parity checks').toEqual([])

  // Server #2: the corrupt SQLite sources and the unclaimed/unattributed OTLP signals, on its own
  // root, home, state and database, so it cannot touch server #1's healthy SQLite paths.
  broken = await startIsolatedServer({ startupTimeoutMs: 120_000 })
  expect(broken.dbPath).not.toBe(USER_CANON)
  expect(broken.dbPath).not.toBe(healthy.dbPath)
  expect(broken.url).not.toContain(':4747')
  expect(broken.url).not.toBe(healthy.url)

  // The unreadable sources reuse the registry's fixed OS paths, so they must live on server #2.
  // The builder writes a readable Cursor global database beside a corrupt workspace database, so
  // discovery reaches the workspace map and records CURSOR_WORKSPACE_UNREADABLE rather than
  // SQLITE_SOURCE_UNREADABLE.
  const unreadable = buildUnreadableSqliteFixtures(broken.homeDir)
  expect(unreadable.map((source) => source.harness).sort()).toEqual(['cursor', 'devin', 'warp', 'zcode'])
})

test.afterAll(async () => {
  if (broken) {
    await broken.stop()
    broken = undefined
  }
  if (healthy) {
    await healthy.stop()
    healthy = undefined
  }
})

test('the parity matrix covers every ingestible harness the corpus serves', () => {
  test.setTimeout(180_000)

  const servedHarnesses = new Set<string>()
  for (const fixture of ALL_FILE_FIXTURES) {
    servedHarnesses.add(normalizeParityHarnessId(fixture.contract.harness))
  }
  for (const contract of STORED_OTLP_HARNESS_CONTRACTS) {
    servedHarnesses.add(normalizeParityHarnessId(contract.harness))
  }
  servedHarnesses.add('copilot')
  servedHarnesses.add('copilot-cli')

  for (const harness of servedHarnesses) {
    const expectation = getParityExpectation(harness)
    expect(expectation.testable, `${harness} must be a testable harness in the parity matrix`).toBe(true)
    expect(expectation.expectedDimensions.length, `${harness} must name the dimensions it is compared on`).toBeGreaterThan(0)
  }

  test.info().annotations.push({
    type: 'parity-harness-count',
    description: `${servedHarnesses.size} harnesses served by the healthy corpus`,
  })
})

test('the dual Copilot baseline renders its full content end to end (OTLP, CLI, VSCode)', async ({ page }) => {
  test.setTimeout(180_000)
  const baseUrl = healthy!.url

  for (const harness of ['copilot', 'copilot-cli', 'copilot-vscode']) {
    const fixtures = ALL_FILE_FIXTURES.filter((fixture) => fixture.contract.harness === harness)
    const contracts: HarnessExpectationContract[] =
      harness === 'copilot'
        ? [COPILOT_BASELINE_CONTRACT]
        : fixtures.map((fixture) => asAssertionContract(fixture.contract))

    expect(contracts.length, `${harness} must have at least one baseline session`).toBeGreaterThan(0)
    for (const contract of contracts) {
      await test.step(`${harness} session ${contract.sessionId}`, async () => {
        const result = await assertHarnessFullContentParity(page, contract, baseUrl)
        annotateGaps(result)
        expect(result.sessionId).toBe(contract.sessionId)
      })
    }
  }
})

/** Every file-import harness in the healthy corpus, grouped by harness id. */
const FILE_CONTRACTS_BY_HARNESS = new Map<string, FileHarnessContract[]>()
for (const fixture of ALL_FILE_FIXTURES) {
  const group = FILE_CONTRACTS_BY_HARNESS.get(fixture.contract.harness) ?? []
  group.push(fixture.contract)
  FILE_CONTRACTS_BY_HARNESS.set(fixture.contract.harness, group)
}

for (const [harness, contracts] of FILE_CONTRACTS_BY_HARNESS) {
  test(`${harness}: every file-import session renders its full content against the baseline`, async ({ page }) => {
    test.setTimeout(180_000)
    const baseUrl = healthy!.url

    // The harness must be reachable from the dashboard before its sessions are inspected.
    await openHarnessSessions(page, baseUrl, harness)
    for (const contract of contracts) {
      await expect(page.getByTestId(`agent-session-row-${contract.sessionId}`)).toBeVisible()
    }

    for (const contract of contracts) {
      await test.step(`${harness} session ${contract.sessionId}`, async () => {
        const result = await assertHarnessFullContentParity(page, asAssertionContract(contract), baseUrl)
        annotateGaps(result)
        // A served summary/requests/tools/stored-fields read is a collected session: the parity
        // check never succeeds by finding nothing.
        expect(result.sessionId).toBe(contract.sessionId)
      })
    }
  })
}

/** Every OTLP harness in the healthy corpus, grouped by harness id. */
const OTLP_CONTRACTS_BY_HARNESS = new Map<string, HarnessExpectationContract[]>()
for (const contract of STORED_OTLP_HARNESS_CONTRACTS) {
  const group = OTLP_CONTRACTS_BY_HARNESS.get(contract.harness) ?? []
  group.push(contract)
  OTLP_CONTRACTS_BY_HARNESS.set(contract.harness, group)
}

for (const [harness, contracts] of OTLP_CONTRACTS_BY_HARNESS) {
  test(`${harness}: every OTLP session renders its full content against the baseline`, async ({ page }) => {
    test.setTimeout(180_000)
    const baseUrl = healthy!.url

    await openHarnessSessions(page, baseUrl, harness)
    for (const contract of contracts) {
      await expect(page.getByTestId(`agent-session-row-${contract.sessionId}`)).toBeVisible()
    }

    for (const contract of contracts) {
      await test.step(`${harness} session ${contract.sessionId}`, async () => {
        const result = await assertHarnessFullContentParity(page, contract, baseUrl)
        annotateGaps(result)
        expect(result.sessionId).toBe(contract.sessionId)
      })
    }
  })
}

test('the excluded gemini session is never collected or displayed as a coding harness', async ({ page }) => {
  test.setTimeout(180_000)
  const baseUrl = healthy!.url

  await page.goto(baseUrl)
  await expect(page.getByTestId('page-context-doctor')).toBeVisible()
  await expect(page.getByTestId('harness-selector').getByTestId('harness-tab-gemini')).toHaveCount(0)

  await page.getByTestId('nav-tab-sessions').click()
  await expect(page.getByTestId('page-sessions')).toBeVisible()
  await expect(page.getByTestId(`agent-session-row-${GEMINI_EXCLUDED_SESSION_ID}`)).toHaveCount(0)

  const sessions = await (
    await page.request.get(`${baseUrl}/api/kyber/sessions?harness=gemini&limit=${SESSION_PAGE_LIMIT}`)
  ).json() as { sessions?: unknown[] }
  expect(sessions.sessions ?? []).toEqual([])
})

test('server #2: corrupt SQLite sources and unclaimed OTLP produce honest diagnostics and zero sessions', async ({ page }) => {
  test.setTimeout(180_000)
  const baseUrl = broken!.url

  // The unclaimed span and unattributed log are posted before the import, then we wait for the
  // collector's drain loop to flush /healthz pendingCount to 0 before reading problems/quarantine.
  await postOtlpTraces(baseUrl, unclaimedSpanPayload())
  await postOtlpLogs(baseUrl, unattributedLogPayload())
  const drainDeadline = Date.now() + 30_000
  while (Date.now() < drainDeadline) {
    const health = await page.request.get(`${baseUrl}/healthz`)
    if (health.ok()) {
      const body = (await health.json()) as { pendingCount?: number }
      if ((body.pendingCount ?? 0) === 0) break
    }
    await new Promise<void>((resolveWait) => setTimeout(resolveWait, 200))
  }
  await postImportHistory(baseUrl)

  const unfiltered = await readProblems(page, baseUrl, '')
  const allCodes = new Set(unfiltered.codes.map((row) => row.code))
  expect(unfiltered.total, 'server #2 must record ingest problems').toBeGreaterThan(0)
  expect(allCodes.has('SQLITE_SOURCE_UNREADABLE'), 'a corrupt warp.sqlite records SQLITE_SOURCE_UNREADABLE').toBe(true)
  expect(allCodes.has('CURSOR_WORKSPACE_UNREADABLE'), 'a corrupt Cursor workspace db records CURSOR_WORKSPACE_UNREADABLE').toBe(true)
  expect(allCodes.has('DEVIN_SESSION_UNREADABLE'), 'an unparseable last_activity_at records DEVIN_SESSION_UNREADABLE').toBe(true)
  expect(allCodes.has('SOURCE_FORMAT_UNEVIDENCED'), 'a zcode db records SOURCE_FORMAT_UNEVIDENCED').toBe(true)
  expect(allCodes.has('OTLP_LOG_UNATTRIBUTED'), 'an unattributed OTLP log records OTLP_LOG_UNATTRIBUTED').toBe(true)

  // The harness filter is honoured: a filtered read returns only that harness's rows.
  const devinProblems = await readProblems(page, baseUrl, '&harness=devin')
  expect(devinProblems.codes.length).toBeGreaterThan(0)
  expect(devinProblems.codes.every((row) => row.harness === 'devin')).toBe(true)
  expect(devinProblems.codes.map((row) => row.code)).toContain('DEVIN_SESSION_UNREADABLE')

  // The unclaimed span is quarantined with the honest reason, and fabricates no session.
  const quarantine = await page.request.get(`${baseUrl}/api/kyber/quarantine?limit=200`)
  expect(quarantine.status(), await quarantine.text()).toBe(200)
  const quarantineBody = (await quarantine.json()) as { entries?: Array<Record<string, unknown>>; total?: unknown }
  const quarantined = quarantineBody.entries ?? []
  expect(
    quarantined.some((entry) => String(entry['reason'] ?? '') === 'unclaimed'),
    'the unclaimed OTLP span must be quarantined as unclaimed',
  ).toBe(true)

  const coverage = await (
    await page.request.get(`${baseUrl}/api/kyber/coverage`)
  ).json() as { quarantineByReason?: Array<{ reason?: string; count?: number }> }
  const unclaimed = (coverage.quarantineByReason ?? []).find((row) => row.reason === 'unclaimed')
  expect(unclaimed?.count ?? 0, 'coverage must report the unclaimed quarantines').toBeGreaterThan(0)

  // None of the corrupt sources produced a session: every affected harness answers with an empty list.
  for (const harness of ['cursor', 'devin', 'warp', 'zcode']) {
    const response = await page.request.get(
      `${baseUrl}/api/kyber/sessions?harness=${encodeURIComponent(harness)}&limit=${SESSION_PAGE_LIMIT}`,
    )
    expect(response.status(), await response.text()).toBe(200)
    const body = (await response.json()) as { sessions?: unknown[] }
    expect(body.sessions ?? [], `${harness} must have no fabricated session`).toEqual([])
  }

  // The Problems view renders the recorded problems for the operator.
  await page.goto(baseUrl)
  await expect(page.getByTestId('page-context-doctor')).toBeVisible()
  await page.getByTestId('nav-tab-problems').click()
  await expect(page.getByTestId('problem-row').first()).toBeVisible()
})

test('server #2: a session section route refuses an unsupported limit instead of clamping it', async ({ page }) => {
  test.setTimeout(180_000)
  const baseUrl = broken!.url

  // A session section route serves only 25, 50 or 100 and refuses any other limit with 400; the
  // parity suite reads the payload's own `limit` rather than assuming a clamp. This pins the
  // refusal end to end. The problem page routes clamp, so a session section is the one to read.
  const refusal = await page.request.get(
    `${baseUrl}/api/kyber/session/parity-absent-session/charts?limit=${SECTION_PAGE_LIMIT + 1}`,
  )
  expect(refusal.status()).toBe(400)
})
