import { expect, test, type Page } from '@playwright/test'
import { mkdirSync, readFileSync } from 'node:fs'
import path from 'node:path'

/**
 * OpenCode live acceptance gate, browser side.
 *
 * Runs against the live kyberDash on 127.0.0.1:4747, for the OpenCode session the gate just
 * produced. Skipped unless KW_OPENCODE_LIVE_SESSION_ID is set, so the suite stays green in CI and
 * on any machine without a live run.
 *
 * Environment:
 *   KW_OPENCODE_LIVE_SESSION_ID    the OpenCode session id the gate ran, e.g. ses_...
 *   KW_OPENCODE_LIVE_EXPECTED      path to the expected-values JSON the gate runner writes
 *   KW_OPENCODE_LIVE_EVIDENCE_DIR  directory for the full-page screenshot (created if missing)
 *
 * Expected-values JSON (LiveGateExpected), one key per value the page must show:
 *   {
 *     "title": "kw-probe runA shared-service",
 *     "skill": "kw-probe-skill",
 *     "prompts": ["<prompt 1 text>", "<prompt 2 text>"],
 *     "requestCount": 2,
 *     "turnCount": 6,
 *     "totalInput": 40108,
 *     "totalOutput": 1529,
 *     "toolCalls": 4,
 *     "toolsInvoked": ["skill", "read"],
 *     "skillsListed": 37
 *   }
 *
 * The gate runs two prompts in one session. The session page renders the user-requests list only
 * for more than one request, so a one-prompt session cannot satisfy this spec by design.
 */

const LIVE_BASE_URL = 'http://127.0.0.1:4747'

/** The API caps a request's prompt preview at this many characters. */
const PROMPT_PREVIEW_LIMIT = 200

interface LiveGateExpected {
  title: string
  skill: string
  prompts: string[]
  requestCount: number
  turnCount: number
  totalInput: number
  totalOutput: number
  toolCalls: number
  toolsInvoked: string[]
  skillsListed: number
}

function requireEnv(name: string): string {
  const value = process.env[name]
  if (value === undefined || value.trim() === '') {
    throw new Error(`${name} must be set for the OpenCode live gate`)
  }
  return value
}

function readExpected(file: string): LiveGateExpected {
  return JSON.parse(readFileSync(file, 'utf8')) as LiveGateExpected
}

/**
 * The dashboard's token tiles format with fmtTokens (web/src/lib/utils.ts): one decimal, K, M or B.
 * Copied here so the spec does not import the web bundle; keep the two in step.
 */
function formatTokens(value: number): string {
  if (value >= 1e9) return (value / 1e9).toFixed(2) + 'B'
  if (value >= 1e6) return (value / 1e6).toFixed(1) + 'M'
  if (value >= 1e3) return (value / 1e3).toFixed(1) + 'K'
  return String(Math.round(value))
}

test.describe('OpenCode live gate: the session page shows the measured values', () => {
  test.skip(
    !process.env.KW_OPENCODE_LIVE_SESSION_ID,
    'set KW_OPENCODE_LIVE_SESSION_ID to the OpenCode session the live gate produced',
  )

  test('the session page shows the overview, requests, tool table, composition and skills of the live run', async ({
    page,
  }: {
    page: Page
  }) => {
    const sessionId = requireEnv('KW_OPENCODE_LIVE_SESSION_ID')
    const expected = readExpected(requireEnv('KW_OPENCODE_LIVE_EXPECTED'))
    const evidenceDir = requireEnv('KW_OPENCODE_LIVE_EVIDENCE_DIR')

    await page.goto(`${LIVE_BASE_URL}/session/${encodeURIComponent(sessionId)}`)

    // Session header.
    await expect(page.getByTestId('agent-session-dashboard')).toBeVisible()
    await expect(page.getByTestId('session-label')).toContainText(expected.title)

    // Overview tiles: each label is present and the token figures carry the measured values.
    const overview = page.getByTestId('overview-strip-section')
    await expect(overview).toBeVisible()
    for (const label of ['Total Input', 'Total Output', 'Turns', 'Requests', 'Tool Calls']) {
      await expect(overview).toContainText(label)
    }
    await expect(overview).toContainText(formatTokens(expected.totalInput))
    await expect(overview).toContainText(formatTokens(expected.totalOutput))
    await expect(page.getByTestId('metric-tool-calls')).toContainText(String(expected.toolCalls))

    // User requests: one entry per prompt, each showing the prompt that was sent.
    const requests = page.getByTestId('user-requests')
    await expect(requests).toBeVisible()
    await expect(requests).toContainText(`${expected.requestCount} user requests`)
    for (const prompt of expected.prompts) {
      await expect(requests).toContainText(prompt.slice(0, PROMPT_PREVIEW_LIMIT))
    }

    // Tool table: a row for every tool the live run invoked.
    await expect(page.getByTestId('tools-ranking-table')).toBeVisible()
    for (const tool of expected.toolsInvoked) {
      await expect(page.getByTestId(`tool-row-${tool}`)).toBeVisible()
    }

    // Composition chart: rendered, and not replaced by a not-measurable or empty notice.
    await expect(page.getByTestId('spend-composition-section')).toBeVisible()
    await expect(page.getByTestId('context-composition-chart')).toBeVisible()
    await expect(page.getByTestId('context-composition-not-measurable')).toHaveCount(0)
    await expect(page.getByTestId('context-composition-empty')).toHaveCount(0)

    // Skills: the listed count matches the gate's listing, the invoked tile is measured, and the gate
    // skill has its own row.
    await expect(page.getByTestId('skills-section')).toBeVisible()
    await expect(page.getByTestId('skills-tile-listed')).toContainText(String(expected.skillsListed))
    await expect(page.getByTestId('skills-tile-invoked')).not.toContainText('not measurable')
    await expect(page.getByTestId(`skill-row-${expected.skill}`)).toBeVisible()

    // Evidence for the gate report.
    mkdirSync(evidenceDir, { recursive: true })
    await page.screenshot({ path: path.join(evidenceDir, 'session-page.png'), fullPage: true })
  })
})
