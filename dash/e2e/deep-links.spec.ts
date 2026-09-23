import { expect, test, type APIRequestContext, type Page } from '@playwright/test'

/**
 * Deep-link acceptance for the view-paths table (R5.1–R5.4).
 *
 * Runs against the same local product host as the other e2e specs. Entity ids
 * come from that store rather than being hardcoded, so the spec follows
 * whatever seeded corpus the host was started with.
 */
const APP = 'http://127.0.0.1:4747'

type Seed = {
  harnessId?: string
  runId?: string
  sessionId?: string
  turnIndex?: number
  findingId?: string
  compareA?: string
  compareB?: string
}

async function seedFromStore(request: APIRequestContext): Promise<Seed> {
  const seed: Seed = {}
  const harnesses = (await (await request.get(`${APP}/api/kyber/harnesses`)).json()) as {
    harnesses?: Array<{ harness: string; sampleCount?: number }>
  }
  seed.harnessId = harnesses.harnesses?.find((row) => (row.sampleCount ?? 0) > 0)?.harness

  const runs = (await (await request.get(`${APP}/api/kyber/runs`)).json()) as {
    runs?: Array<{ runId: string }>
  }
  seed.runId = runs.runs?.[0]?.runId
  if ((runs.runs?.length ?? 0) >= 2) {
    seed.compareA = runs.runs![0]!.runId
    seed.compareB = runs.runs![1]!.runId
  }

  const sessions = (await (await request.get(`${APP}/api/kyber/sessions`)).json()) as {
    sessions?: Array<{ session_id?: string; sessionId?: string; turn_count?: number; turnCount?: number }>
  }
  const withTurns = sessions.sessions?.find((row) => (row.turn_count ?? row.turnCount ?? 0) > 0)
  const session = withTurns ?? sessions.sessions?.[0]
  seed.sessionId = session?.session_id ?? session?.sessionId
  const turns = session?.turn_count ?? session?.turnCount
  if (typeof turns === 'number' && turns > 0) seed.turnIndex = 0

  const findings = (await (await request.get(`${APP}/api/kyber/findings?limit=1`)).json()) as {
    findings?: Array<{ id: string }>
  }
  seed.findingId = findings.findings?.[0]?.id
  return seed
}

async function openAndReload(page: Page, path: string, testId: string): Promise<void> {
  await page.goto(`${APP}${path}`)
  await expect(page.getByTestId(testId)).toBeVisible()
  await page.reload()
  await expect(page.getByTestId(testId)).toBeVisible()
}

test.describe('deep links (R5.1, R5.2, R5.3, R5.4)', () => {
  test('opens each view-path route directly and still shows it after reload', async ({ page, request }) => {
    test.setTimeout(120_000)
    const seed = await seedFromStore(request)

    await openAndReload(page, '/', 'page-context-doctor')
    await openAndReload(page, '/quarantine', 'page-title')
    await expect(page.getByTestId('page-title')).toContainText('Quarantine')
    await openAndReload(page, '/problems', 'page-title')
    await expect(page.getByTestId('page-title')).toContainText('Problems')

    if (seed.harnessId) {
      await openAndReload(page, `/harness/${encodeURIComponent(seed.harnessId)}`, 'page-harness')
    }
    if (seed.runId) {
      await openAndReload(page, `/run/${encodeURIComponent(seed.runId)}`, 'page-run')
    }
    if (seed.sessionId) {
      await openAndReload(page, `/session/${encodeURIComponent(seed.sessionId)}`, 'page-execution')
    }
    if (seed.sessionId && seed.turnIndex !== undefined) {
      await openAndReload(
        page,
        `/session/${encodeURIComponent(seed.sessionId)}/turn/${seed.turnIndex}`,
        'page-turn',
      )
    }
    if (seed.findingId) {
      await openAndReload(page, `/finding/${encodeURIComponent(seed.findingId)}`, 'page-finding')
    }
    if (seed.compareA && seed.compareB) {
      await openAndReload(
        page,
        `/compare?a=${encodeURIComponent(seed.compareA)}&b=${encodeURIComponent(seed.compareB)}`,
        'page-compare',
      )
    }
  })

  test('Back and Forward walk the spine history', async ({ page }) => {
    test.setTimeout(60_000)
    await page.goto(`${APP}/`)
    await expect(page.getByTestId('page-context-doctor')).toBeVisible()

    await page.goto(`${APP}/quarantine`)
    await expect(page.getByTestId('page-title')).toContainText('Quarantine')

    await page.goto(`${APP}/problems`)
    await expect(page.getByTestId('page-title')).toContainText('Problems')

    await page.goBack()
    await expect(page.getByTestId('page-title')).toContainText('Quarantine')
    await page.goBack()
    await expect(page.getByTestId('page-context-doctor')).toBeVisible()
    await page.goForward()
    await expect(page.getByTestId('page-title')).toContainText('Quarantine')
    await page.goForward()
    await expect(page.getByTestId('page-title')).toContainText('Problems')
  })

  test('an unknown id renders NotFoundPanel inside the shell, naming the id and linking home', async ({
    page,
  }) => {
    const missing = 'kw-no-such-finding'
    await page.goto(`${APP}/finding/${missing}`)
    await expect(page.getByTestId('nav-tabs')).toBeVisible()
    await expect(page.getByTestId('not-found-panel')).toBeVisible()
    await expect(page.getByTestId('not-found-panel')).toContainText(missing)
    await expect(page.getByTestId('not-found-home')).toHaveAttribute('href', '/')

    await page.getByTestId('not-found-home').click()
    await expect(page.getByTestId('page-context-doctor')).toBeVisible()
  })
})
