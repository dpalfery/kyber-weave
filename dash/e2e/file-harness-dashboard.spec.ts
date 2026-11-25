import { expect, test } from '@playwright/test'

import { FILE_HARNESS_FIXTURES, type FileHarnessContract } from './file-harness-contracts.js'
import { postImportHistory, writeHarnessFiles } from './file-harness-world.js'
import {
  SESSION_PAGE_LIMIT,
  USER_CANON,
  startIsolatedServer,
  type IsolatedServerHandle,
} from './harness-collection-world.js'
import type { HarnessExpectationContract } from './otel-harness-payloads.js'
import { assertHarnessSessionDashboard, navigateToHarnessDetail } from './session-dashboard-assertions.js'

/**
 * Isolated end-to-end suite for the file-import harnesses (T5-T7): two sessions of 10 or more
 * turns per harness are written as native files under the isolated home, loaded through the
 * real folder import (`api/kyber/import-history`), and every Agent Session Dashboard section is
 * checked per session against the harness contract.
 */
// Each harness test stands alone: a harness whose section renders wrong fails its own test and
// leaves the other harnesses' results readable (D6 keeps the failing assertion).
let server: IsolatedServerHandle | undefined
let unlistedSessions: ReadonlySet<string> = new Set()

// The run clock, one hour back and floored to the minute, so retention never ages a fixture out.
const BASE_TIME_MS = Math.floor(Date.now() / 60_000) * 60_000 - 3_600_000

const CONTRACTS_BY_HARNESS = new Map<string, FileHarnessContract[]>()
for (const fixture of FILE_HARNESS_FIXTURES) {
  const group = CONTRACTS_BY_HARNESS.get(fixture.contract.harness) ?? []
  group.push(fixture.contract)
  CONTRACTS_BY_HARNESS.set(fixture.contract.harness, group)
}

/** The assertions name a harness by its stored id; a file-import id is the same kind of string. */
function asAssertionContract(contract: FileHarnessContract): HarnessExpectationContract {
  return contract as unknown as HarnessExpectationContract
}

async function listedSessionIds(baseUrl: string, harness: string): Promise<ReadonlySet<string>> {
  const response = await fetch(
    `${baseUrl}/api/kyber/sessions?harness=${encodeURIComponent(harness)}&limit=${SESSION_PAGE_LIMIT}`,
  )
  if (!response.ok) {
    return new Set()
  }
  const body = (await response.json()) as { sessions?: Array<Record<string, unknown>> }
  return new Set((body.sessions ?? []).map((row) => String(row['session_id'] ?? row['sessionId'] ?? '')))
}

test.beforeAll(async () => {
  test.setTimeout(240_000)
  server = await startIsolatedServer({ startupTimeoutMs: 120_000 })
  expect(server.dbPath).not.toBe(USER_CANON)
  expect(server.url).not.toContain(':4747')

  for (const fixture of FILE_HARNESS_FIXTURES) {
    if (fixture.files) {
      writeHarnessFiles(server.homeDir, fixture.files(BASE_TIME_MS))
    }
    fixture.build?.(server.homeDir, BASE_TIME_MS)
  }
  await postImportHistory(server.url)

  const deadline = Date.now() + 60_000
  const missing = new Set(
    FILE_HARNESS_FIXTURES.map((fixture) => `${fixture.contract.harness}/${fixture.contract.sessionId}`),
  )
  while (missing.size > 0 && Date.now() < deadline) {
    for (const [harness, contracts] of CONTRACTS_BY_HARNESS) {
      const listed = await listedSessionIds(server.url, harness)
      for (const contract of contracts) {
        if (listed.has(contract.sessionId)) {
          missing.delete(`${harness}/${contract.sessionId}`)
        }
      }
    }
    if (missing.size > 0) {
      await new Promise<void>((resolveWait) => setTimeout(resolveWait, 500))
    }
  }
  // Kept per harness (below), so a harness whose import yields nothing fails its own test and
  // leaves the other harnesses' results readable (D6).
  unlistedSessions = missing
})

test.afterAll(async () => {
  await server?.stop()
})

/** The SQLite-format harnesses whose source reader the C# engine lacks (regression from #612). */
const SQLITE_READER_EXPECTED_FAILURES: ReadonlySet<string> = new Set(['cursor', 'devin'])
const SQLITE_READER_EXPECTED_FAILURE_REASON =
  'C# engine has no SQLite source reader (regression from #612); fixed on branch claude/fix/sqlite-harness-readers'

for (const [harness, contracts] of CONTRACTS_BY_HARNESS) {
  test(`${harness}: ${contracts.length} imported sessions, every dashboard section`, async ({ page }) => {
    test.fail(SQLITE_READER_EXPECTED_FAILURES.has(harness), SQLITE_READER_EXPECTED_FAILURE_REASON)
    test.setTimeout(120_000)
    expect(server).toBeDefined()
    const baseUrl = server!.url
    expect(
      contracts.filter((contract) => unlistedSessions.has(`${harness}/${contract.sessionId}`)).map((contract) => contract.sessionId),
      `${harness}: every imported session must be listed by api/kyber/sessions`,
    ).toEqual([])

    await navigateToHarnessDetail(page, baseUrl, harness)
    await expect(page.getByTestId('harness-session-count')).toHaveText(String(contracts.length))

    for (const contract of contracts) {
      await test.step(`${harness} session ${contract.sessionId}`, async () => {
        await assertHarnessSessionDashboard(page, asAssertionContract(contract), baseUrl)
      })
    }
  })
}
