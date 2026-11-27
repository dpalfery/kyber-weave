// Shared Agent Session Dashboard assertions for the isolated OTLP suites (T3), so the web
// dashboard spec here and the per-harness local-file suites (T5-T7) check the same sections
// the same way.
import { expect, type Locator, type Page } from '@playwright/test'

import { getParityExpectation, type CopilotParityDimension } from './copilot-parity-baseline.js'
import { SECTION_PAGE_LIMIT, SESSION_PAGE_LIMIT } from './harness-collection-world.js'
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

// ── Full-content parity assertions ──────────────────────────────────────────
//
// `assertHarnessSessionDashboard` stops at the section cards. These helpers go down to the content:
// the served API payloads and the text the dashboard renders for them, per harness. They are split
// into three parts — a gap classifier, one turn's direct-page inspection, and the whole-session
// orchestrator — so T3's isolated Playwright suite can run the contract per harness and so each
// part is exercisable over a page seam without a browser.
//
// Every helper accepts the structural subset of Playwright's `Page`/`Locator` it uses rather than
// the concrete types, so a real `Page` satisfies it and a Vitest fake can too.

/** The five canonical turn-content blocks `buildTurnContentBlocks` always renders, in rail order. */
export const PARITY_CONTENT_BLOCK_KEYS = [
  'system_prompt',
  'tool_definitions',
  'instruction_context',
  'conversation_history',
  'tool_result_content',
] as const

/** One canonical turn-content block key. */
export type ParityContentBlockKey = (typeof PARITY_CONTENT_BLOCK_KEYS)[number]

/**
 * The part keys the canonical blocks aggregate, mirroring `BLOCK_DEFS` in
 * `dash/web/src/lib/turnContentBlocks.ts`. A served part outside this set is an "additional part":
 * it opens no context band and adds no assembled text.
 */
const CANONICAL_TURN_PART_KEYS: ReadonlySet<string> = new Set([
  'system_prompt',
  'tool_definitions',
  'instruction_context',
  'tool_result_content',
  'conversation_history',
  'user_messages',
  'assistant_turns',
])

/** The paragraph `TurnDetail` renders when a turn recorded no context bands. */
const NO_CONTEXT_BANDS_NOTE = 'No context bands were recorded for this turn.'

/** How a parity gap is handled: annotated for a diagnostic pass, or fatal for a strict pass. */
export type ParityDiagnosticMode = 'annotate-gaps' | 'strict'

/**
 * The kinds of parity gap a full-content check can find. `content-part-drop` is the only one that
 * means collected content failed to render; the rest are honest empty or unobservable states the
 * dashboard states explicitly.
 */
export type ParityGapCode =
  | 'unreachable-drawer'
  | 'turn-content-empty'
  | 'requests-empty'
  | 'stored-field-empty'
  | 'tool-definition-unavailable'
  | 'tool-invocation-absent'
  | 'content-part-drop'

/**
 * Gap codes that are honest dashboard states rather than regressions, so they annotate without
 * needing a matching marker in the harness parity matrix.
 */
const INTRINSIC_GAP_CODES: ReadonlySet<ParityGapCode> = new Set<ParityGapCode>([
  'unreachable-drawer',
  'turn-content-empty',
  'requests-empty',
  'stored-field-empty',
  'tool-definition-unavailable',
  'tool-invocation-absent',
])

/** One piece of parity-gap evidence, before it is classified. */
export type ParityGapEvidence = {
  readonly code: ParityGapCode
  readonly harness: string
  readonly sessionId: string
  readonly dimension: CopilotParityDimension
  readonly detail: string
  /** The harness's documented gap markers; only a documented `content-part-drop` is tolerated. */
  readonly documentedGaps?: readonly string[]
  /** What the check did instead of the unreachable surface, for the annotation. */
  readonly fallback?: string
}

/** A classified, annotatable parity gap. */
export type ParityGapAnnotation = {
  readonly code: ParityGapCode
  readonly harness: string
  readonly sessionId: string
  readonly dimension: CopilotParityDimension
  readonly detail: string
  readonly documented: true
  readonly fallback?: string
  /** One-line label for `test.info().annotations`. */
  readonly annotation: string
}

/**
 * Classifies one parity gap.
 *
 * An intrinsic honest state — an unreachable drawer, an empty request list, an empty turn, an empty
 * stored table, a harness that exports no tool definitions or no tool spans — is annotated in the
 * default `annotate-gaps` mode. A `content-part-drop`, which means served content rendered nowhere,
 * is a regression: it annotates only when the harness's documented gaps already name it, and
 * otherwise throws. `strict` mode throws on every gap.
 *
 * @throws {Error} `Unannotated content drop [...]` for an undocumented content drop.
 * @throws {Error} `Parity gap [...]` for any gap in strict mode.
 */
export function classifyParityGap(
  evidence: ParityGapEvidence,
  options?: { readonly mode?: ParityDiagnosticMode },
): ParityGapAnnotation {
  const mode = options?.mode ?? 'annotate-gaps'
  if (mode === 'strict') {
    throw new Error(
      `Parity gap [${evidence.code}] for ${evidence.harness} session ${evidence.sessionId}: ${evidence.detail}`,
    )
  }
  const documented =
    INTRINSIC_GAP_CODES.has(evidence.code) || (evidence.documentedGaps ?? []).includes(evidence.code)
  if (!documented) {
    throw new Error(
      `Unannotated content drop [${evidence.code}] for ${evidence.harness} session ${evidence.sessionId}: ${evidence.detail} is not listed in the harness parity matrix`,
    )
  }
  const fallback = evidence.fallback === undefined ? '' : ` (fallback: ${evidence.fallback})`
  return {
    code: evidence.code,
    harness: evidence.harness,
    sessionId: evidence.sessionId,
    dimension: evidence.dimension,
    detail: evidence.detail,
    documented: true,
    ...(evidence.fallback === undefined ? {} : { fallback: evidence.fallback }),
    annotation: `${evidence.code}: ${evidence.detail}${fallback}`,
  }
}

/** The locator members these helpers use; a real Playwright `Locator` satisfies it. */
export interface ParityLocatorLike {
  count(): Promise<number>
  click(): Promise<void>
  innerText(): Promise<string>
  isVisible(): Promise<boolean>
  isEnabled(): Promise<boolean>
  getAttribute(name: string): Promise<string | null>
  first(): ParityLocatorLike
  locator(selector: string): ParityLocatorLike
  getByTestId(testId: string): ParityLocatorLike
  all(): Promise<ParityLocatorLike[]>
  waitFor?(options?: {
    state?: 'attached' | 'detached' | 'visible' | 'hidden'
    timeout?: number
  }): Promise<void>
}

/** Anything that can scope a test-id or CSS lookup; both a page and a locator qualify. */
export interface ParityLocatorScope {
  locator(selector: string): ParityLocatorLike
  getByTestId(testId: string): ParityLocatorLike
}

/** The API response members these helpers use; a real Playwright `APIResponse` satisfies it. */
export interface ParityApiResponseLike {
  status(): number
  text(): Promise<string>
}

/** The page members these helpers use; a real Playwright `Page` satisfies it. */
export interface ParityPageLike extends ParityLocatorScope {
  goto(url: string): Promise<unknown>
  readonly request: { get(url: string): Promise<ParityApiResponseLike> }
}

/** Options for `inspectTurnFullContent`. */
export type TurnFullContentOptions = {
  readonly baseUrl: string
}

/** How one context pane rendered for a selected block. */
export type ParityPaneState = 'content' | 'not-measurable' | 'empty'

/** What the direct turn page showed for one selected context band. */
export type TurnBlockInspection = {
  readonly key: string
  readonly pane: ParityPaneState
  readonly copyBlockEnabled: boolean
}

/** The served API facts about one turn's content. */
export type TurnContentApiSnapshot = {
  readonly sessionId: string
  readonly turnIndex: number
  readonly partCount: number
  readonly nonEmptyCanonicalPartCount: number
  readonly projectionFailed: boolean
  readonly truncated: boolean
}

/** What `inspectTurnFullContent` observed for one directly-addressed turn. */
export type TurnFullContentInspection = {
  readonly sessionId: string
  readonly turnIndex: number
  readonly api: TurnContentApiSnapshot
  readonly bandKeys: readonly string[]
  readonly noBandsNote: boolean
  readonly wholeTurnPane: ParityPaneState
  readonly copyTurnEnabled: boolean
  readonly blocks: readonly TurnBlockInspection[]
}

/** What one canonical block rendered in the chart-opened drawer. */
export type ParityBlockViewInspection = {
  readonly key: string
  readonly empty: boolean
  readonly rawTogglePresent: boolean
  readonly copyPresent: boolean
}

/** What the chart-gated turn drawer showed. */
export type ParityDrawerInspection = {
  readonly reachable: boolean
  readonly blocks: readonly ParityBlockViewInspection[]
  readonly turnContentEmpty: boolean
}

/** What the timeline span inspection found and rendered. */
export type ParityTimelineInspection = {
  readonly toolSpanIds: readonly string[]
  readonly inspectedSpanId: string | null
  readonly drawerContent: 'content' | 'fallback-note' | 'none'
}

/** The whole per-harness full-content parity result. */
export type ContentParityResult = {
  readonly harness: string
  readonly sessionId: string
  readonly requests: { readonly total: number; readonly rowsOnPage: number }
  readonly tools: {
    readonly measurable: boolean
    readonly rowsOnPage: number
    readonly definitionsSpanId: string | null
  }
  readonly storedFields: { readonly tables: readonly string[]; readonly toolCallRows: number }
  readonly drawer: ParityDrawerInspection
  readonly timeline: ParityTimelineInspection
  readonly turn: TurnFullContentInspection | null
  readonly gaps: readonly ParityGapAnnotation[]
}

/** Options for `assertHarnessFullContentParity`. */
export type FullContentParityOptions = {
  /** The 0-based turn to inspect directly; defaults to `0`. */
  readonly turnIndex?: number
  /** Whether to address `/session/:id/turn/:index` directly; defaults to `true`. */
  readonly includeTurnDetail?: boolean
  /** `annotate-gaps` (default) keeps the suite diagnostic; `strict` fails on any gap. */
  readonly diagnosticMode?: ParityDiagnosticMode
}

type JsonObject = Record<string, unknown>

type GapRecorder = (
  evidence: Omit<ParityGapEvidence, 'harness' | 'sessionId' | 'documentedGaps'>,
) => ParityGapAnnotation

function isRecord(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function stringOrEmpty(value: unknown): string {
  return typeof value === 'string' ? value : ''
}

function numberOr(value: unknown, fallback: number): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback
}

function assertNonEmptySessionId(sessionId: string): void {
  if (typeof sessionId !== 'string' || sessionId.trim() === '') {
    throw new Error('sessionId must be non-empty')
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

/**
 * Reads the row array out of a session-section page envelope. Every section route serves its rows
 * under both `data` (the one items key the web reader trusts) and the section's own key
 * (`requests`, `tools`); a unit fake may serve `entries` alone, so that is the last fallback.
 */
function sectionRows(page: JsonObject, itemsKey: string): JsonObject[] {
  for (const candidate of [page[itemsKey], page.data, page.entries]) {
    if (Array.isArray(candidate)) return candidate.filter(isRecord)
  }
  return []
}

async function readApiObject(page: ParityPageLike, url: string, label: string): Promise<JsonObject> {
  const response = await page.request.get(url)
  const status = response.status()
  const text = await response.text()
  if (status !== 200) {
    throw new Error(`${label} answered HTTP ${status}: ${text.slice(0, 300)}`)
  }
  let parsed: unknown
  try {
    parsed = JSON.parse(text)
  } catch {
    throw new Error(`${label} answered a body that is not JSON`)
  }
  if (!isRecord(parsed)) {
    throw new Error(`${label} answered a body that is not a JSON object`)
  }
  return parsed
}

async function mustBeVisible(locator: ParityLocatorLike, label: string): Promise<void> {
  if (typeof locator.waitFor === 'function') {
    try {
      await locator.waitFor({ state: 'visible' })
      return
    } catch (error) {
      const reason = error instanceof Error ? error.message : String(error)
      throw new Error(`${label} did not become visible: ${reason}`)
    }
  }
  if (await locator.isVisible()) return
  throw new Error(`${label} is not visible`)
}

async function countTestIdPrefix(scope: ParityLocatorScope, prefix: string): Promise<number> {
  return scope.locator(`[data-testid^="${prefix}"]`).count()
}

/** Waits for any of `testIds` to appear, returning the first that does or `null` on timeout. */
async function firstAppearingTestId(
  page: ParityLocatorScope,
  testIds: readonly string[],
  timeoutMs = 5_000,
): Promise<string | null> {
  const deadline = Date.now() + timeoutMs
  let first = true
  while (first || Date.now() <= deadline) {
    first = false
    for (const testId of testIds) {
      if ((await page.getByTestId(testId).count()) > 0) return testId
    }
    if (Date.now() > deadline) break
    await sleep(50)
  }
  return null
}

async function readPaneState(page: ParityPageLike): Promise<ParityPaneState> {
  if ((await page.getByTestId('not-measurable-pane').count()) > 0) return 'not-measurable'
  if ((await page.getByTestId('context-content-pane').count()) > 0) return 'content'
  if ((await page.getByTestId('empty-content-pane').count()) > 0) return 'empty'
  throw new Error('the context inspector rendered no content, not-measurable, or empty pane')
}

function canonicalNonEmptyPartCount(parts: readonly unknown[]): number {
  return parts.filter(
    (part) =>
      isRecord(part) &&
      CANONICAL_TURN_PART_KEYS.has(stringOrEmpty(part.part)) &&
      stringOrEmpty(part.text).trim() !== '',
  ).length
}

/**
 * Inspects one turn by navigating directly to `/session/:id/turn/:index` and checking the
 * `ContextInspector` it renders against the turn-content route: the context bands, each pane, and
 * the block/turn copy buttons' enabled state.
 *
 * @throws {Error} `sessionId must be non-empty` for a blank session id.
 * @throws {Error} `turn content answered HTTP <status>` for a non-200 answer.
 */
export async function inspectTurnFullContent(
  page: ParityPageLike,
  sessionId: string,
  turnIndex: number | undefined,
  options: TurnFullContentOptions,
): Promise<TurnFullContentInspection> {
  assertNonEmptySessionId(sessionId)
  const index = turnIndex ?? 0
  const baseUrl = options.baseUrl

  const content = await readApiObject(
    page,
    `${baseUrl}/api/kyber/session/${encodeURIComponent(sessionId)}/turn/${index}/content?full=true`,
    'turn content',
  )
  const parts = Array.isArray(content.parts) ? content.parts : []
  const projectionFailed = content.projection_status === 'PROJECTION_FAILED'
  const nonEmptyCanonical = canonicalNonEmptyPartCount(parts)
  const api: TurnContentApiSnapshot = {
    sessionId,
    turnIndex: index,
    partCount: parts.length,
    nonEmptyCanonicalPartCount: nonEmptyCanonical,
    projectionFailed,
    truncated: content.truncated === true,
  }

  await page.goto(`${baseUrl}/session/${encodeURIComponent(sessionId)}/turn/${index}`)
  const pageTurn = page.getByTestId('page-turn')
  await mustBeVisible(pageTurn, `turn page for ${sessionId} turn ${index}`)

  if (projectionFailed) {
    await mustBeVisible(page.getByTestId('turn-projection-failed'), 'turn projection failure note')
    return {
      sessionId,
      turnIndex: index,
      api,
      bandKeys: [],
      noBandsNote: false,
      wholeTurnPane: 'empty',
      copyTurnEnabled: false,
      blocks: [],
    }
  }

  await mustBeVisible(page.getByTestId('context-inspector'), 'context inspector')

  const bandLocators = await pageTurn.locator('[data-testid^="context-band-"]').all()
  const bandKeys: string[] = []
  const blocks: TurnBlockInspection[] = []
  const wholeTurnPane = await readPaneState(page)
  const copyTurn = page.getByTestId('copy-turn-button')
  const copyTurnEnabled = (await copyTurn.count()) > 0 && (await copyTurn.isEnabled())
  const pageTurnText = await pageTurn.innerText()
  const noBandsNote = pageTurnText.includes(NO_CONTEXT_BANDS_NOTE)

  for (const band of bandLocators) {
    const testId = await band.getAttribute('data-testid')
    const key = testId === null ? '' : testId.slice('context-band-'.length)
    if (key === '') continue
    bandKeys.push(key)
    if (!(PARITY_CONTENT_BLOCK_KEYS as readonly string[]).includes(key)) {
      throw new Error(`turn ${index} of ${sessionId} rendered an unknown context band '${key}'`)
    }
    await band.click()
    const pane = await readPaneState(page)
    const copyBlock = page.getByTestId('copy-block-button')
    const copyBlockEnabled = (await copyBlock.count()) > 0 && (await copyBlock.isEnabled())
    if (copyBlockEnabled !== (pane === 'content')) {
      throw new Error(
        `turn ${index} of ${sessionId} band '${key}' rendered pane '${pane}' but copy-block-button enabled=${String(copyBlockEnabled)}`,
      )
    }
    blocks.push({ key, pane, copyBlockEnabled })
  }

  if (nonEmptyCanonical > 0) {
    if (bandKeys.length === 0) {
      throw new Error(
        `turn ${index} of ${sessionId} served ${nonEmptyCanonical} non-empty canonical part(s) but rendered no context band`,
      )
    }
    if (noBandsNote) {
      throw new Error(
        `turn ${index} of ${sessionId} served content but also rendered "${NO_CONTEXT_BANDS_NOTE}"`,
      )
    }
  } else {
    if (bandKeys.length !== 0) {
      throw new Error(
        `turn ${index} of ${sessionId} served no non-empty content but rendered ${bandKeys.length} context band(s)`,
      )
    }
    if (!noBandsNote) {
      throw new Error(`turn ${index} of ${sessionId} rendered no context bands without the honest note`)
    }
  }

  if (copyTurnEnabled !== (nonEmptyCanonical > 0)) {
    throw new Error(
      `turn ${index} of ${sessionId} copy-turn-button enabled=${String(copyTurnEnabled)} but served ${nonEmptyCanonical} non-empty canonical part(s)`,
    )
  }

  return {
    sessionId,
    turnIndex: index,
    api,
    bandKeys,
    noBandsNote,
    wholeTurnPane,
    copyTurnEnabled,
    blocks,
  }
}

/** Reads one turn's content route and returns its served parts. */
async function readTurnParts(
  page: ParityPageLike,
  baseUrl: string,
  sessionId: string,
  turnIndex: number,
): Promise<{ readonly parts: readonly unknown[]; readonly projectionFailed: boolean }> {
  const content = await readApiObject(
    page,
    `${baseUrl}/api/kyber/session/${encodeURIComponent(sessionId)}/turn/${turnIndex}/content?full=true`,
    'turn content',
  )
  return {
    parts: Array.isArray(content.parts) ? content.parts : [],
    projectionFailed: content.projection_status === 'PROJECTION_FAILED',
  }
}

/**
 * Opens the chart-gated turn drawer and checks every canonical block it renders: a non-empty block
 * offers the raw view and its own copy button, an empty block states so and offers no copy button.
 * A served non-empty canonical part that renders as no content at all is a content drop. Returns
 * `null` when no chart click opened the drawer, so the caller can fall back honestly.
 */
async function inspectChartDrawer(
  page: ParityPageLike,
  baseUrl: string,
  sessionId: string,
  preferredTurnIndex: number,
  record: GapRecorder,
): Promise<ParityDrawerInspection | null> {
  const spend = page.getByTestId('spend-composition-section')
  await mustBeVisible(spend, 'spend composition section')

  let inspectedTurn = preferredTurnIndex
  let clicked = false
  const exactBar = spend.getByTestId(`turn-bar-${preferredTurnIndex}`)
  if ((await exactBar.count()) > 0) {
    await exactBar.first().click()
    clicked = true
  } else if ((await countTestIdPrefix(spend, 'turn-bar-')) > 0) {
    const firstBar = spend.locator('[data-testid^="turn-bar-"]').first()
    const parsed = Number((await firstBar.getAttribute('data-testid'))?.slice('turn-bar-'.length) ?? NaN)
    if (Number.isInteger(parsed) && parsed >= 0) inspectedTurn = parsed
    await firstBar.click()
    clicked = true
  } else if ((await countTestIdPrefix(spend, 'context-turn-')) > 0) {
    // The stacked-bar row's labelled header opens the full-context drawer; the row div itself is
    // the click target here, so the open is verified below rather than assumed.
    const exactRow = spend.getByTestId(`context-turn-${preferredTurnIndex}`)
    const row = (await exactRow.count()) > 0 ? exactRow.first() : spend.locator('[data-testid^="context-turn-"]').first()
    const parsed = Number((await row.getAttribute('data-testid'))?.slice('context-turn-'.length) ?? NaN)
    if (Number.isInteger(parsed) && parsed >= 0) inspectedTurn = parsed
    await row.click()
    clicked = true
  }
  if (!clicked) return null

  if ((await firstAppearingTestId(page, ['session-inspector-drawer'], 3_000)) === null) return null

  const drawer = page.getByTestId('session-inspector-drawer')
  await mustBeVisible(drawer, 'session inspector drawer')
  await mustBeVisible(page.getByTestId('turn-content-full'), 'turn full-content panel')

  const { parts, projectionFailed } = await readTurnParts(page, baseUrl, sessionId, inspectedTurn)
  const turnContentEmpty = (await page.getByTestId('turn-content-empty').count()) > 0
  if (projectionFailed) {
    if (!turnContentEmpty) {
      throw new Error(`turn ${inspectedTurn} of ${sessionId} faulted its projection without the empty-turn state`)
    }
    record({
      code: 'turn-content-empty',
      dimension: 'turn-projection',
      detail: `turn ${inspectedTurn} projection faulted; the drawer stated it instead of rendering content`,
    })
  } else if (turnContentEmpty) {
    throw new Error(`turn ${inspectedTurn} of ${sessionId} rendered the empty-turn state for a projected turn`)
  }

  const blocks: ParityBlockViewInspection[] = []
  for (const key of PARITY_CONTENT_BLOCK_KEYS) {
    await mustBeVisible(drawer.getByTestId(`turn-block-${key}`), `turn block '${key}'`)
    const empty = (await drawer.getByTestId(`turn-block-${key}-empty`).count()) > 0
    const copyPresent = (await drawer.getByTestId(`copy-turn-block-${key}`).count()) > 0
    const rawToggle = drawer.getByTestId(`turn-block-${key}-view-raw`)
    let rawTogglePresent = (await rawToggle.count()) > 0
    if (empty) {
      if (copyPresent) {
        throw new Error(`empty turn block '${key}' of ${sessionId} offered a copy button`)
      }
      if (rawTogglePresent) {
        throw new Error(`empty turn block '${key}' of ${sessionId} offered a raw view`)
      }
    } else {
      if (!copyPresent) {
        throw new Error(`turn block '${key}' of ${sessionId} rendered content without its copy button`)
      }
      await mustBeVisible(rawToggle, `raw toggle for turn block '${key}'`)
      await rawToggle.click()
      await mustBeVisible(drawer.getByTestId(`turn-block-${key}-raw`), `raw view for turn block '${key}'`)
      rawTogglePresent = true
    }
    blocks.push({ key, empty, rawTogglePresent, copyPresent })
  }

  if ((await page.getByTestId('copy-turn-button').count()) === 0) {
    throw new Error(`turn ${inspectedTurn} of ${sessionId} rendered its full content without a copy-turn button`)
  }

  if (canonicalNonEmptyPartCount(parts) > 0 && blocks.every((block) => block.empty)) {
    record({
      code: 'content-part-drop',
      dimension: 'context-content',
      detail: `turn ${inspectedTurn} served non-empty canonical parts but every block rendered empty`,
    })
  }

  const close = page.getByTestId('drawer-close-button')
  await mustBeVisible(close, 'drawer close button')
  await close.click()

  return { reachable: true, blocks, turnContentEmpty }
}

function isToolSpan(node: JsonObject): boolean {
  const kind = stringOrEmpty(node.kind)
  const name = stringOrEmpty(node.name)
  return kind === 'tool.invoke' || kind === 'execute_tool' || name.includes('execute_tool')
}

function isLlmSpan(node: JsonObject): boolean {
  return stringOrEmpty(node.kind) === 'llm.invoke'
}

/**
 * Inspects the timeline's tool spans over HTTP and the UI: the attributes route and the span-scoped
 * content route must answer, and opening the span must render drawer content or the explicit
 * fallback note. With no tool span it inspects an `llm.invoke` span instead (the unmeasurable-chart
 * fallback). A missing span is recorded as an honest absence.
 */
async function inspectTimelineSpans(
  page: ParityPageLike,
  baseUrl: string,
  sessionId: string,
  preferLlm: boolean,
  record: GapRecorder,
): Promise<ParityTimelineInspection> {
  const timeline = await readApiObject(
    page,
    `${baseUrl}/api/kyber/session/${encodeURIComponent(sessionId)}/timeline?page=1&limit=${SECTION_PAGE_LIMIT}&order=desc`,
    'timeline',
  )
  const nodes = (Array.isArray(timeline.nodes) ? timeline.nodes : []).filter(isRecord)
  const rootToolSpans = nodes.filter(isToolSpan)
  const llmSpans = nodes.filter(isLlmSpan)
  const nestedRoots = nodes.filter((node) => numberOr(node.childCount, 0) > 0)

  const nestedToolSpans: JsonObject[] = []
  for (const root of nestedRoots) {
    const parentId = stringOrEmpty(root.spanId)
    if (parentId === '') continue
    const children = await readApiObject(
      page,
      `${baseUrl}/api/kyber/session/${encodeURIComponent(sessionId)}/timeline?parent=${encodeURIComponent(parentId)}&page=1&limit=${SECTION_PAGE_LIMIT}&order=desc`,
      'timeline children',
    )
    const childNodes = (Array.isArray(children.nodes) ? children.nodes : []).filter(isRecord)
    nestedToolSpans.push(...childNodes.filter(isToolSpan))
  }

  const toolSpans = [...rootToolSpans, ...nestedToolSpans]
  const target = preferLlm ? (llmSpans[0] ?? toolSpans[0]) : (toolSpans[0] ?? llmSpans[0])

  const section = page.getByTestId('execution-timeline-section')
  await mustBeVisible(section, 'execution timeline section')
  if (nestedRoots.length > 0 && (await countTestIdPrefix(section, 'timeline-node-expand')) === 0) {
    throw new Error(`timeline of ${sessionId} holds nested nodes but rendered no timeline-node-expand control`)
  }

  if (target === undefined) {
    record({
      code: 'tool-invocation-absent',
      dimension: 'tool-invocations',
      detail: `session ${sessionId} recorded no tool.invoke or llm.invoke span to inspect`,
    })
    return { toolSpanIds: [], inspectedSpanId: null, drawerContent: 'none' }
  }

  const spanId = stringOrEmpty(target.spanId)
  if (spanId === '') throw new Error(`timeline of ${sessionId} named a span without a spanId`)
  await readApiObject(page, `${baseUrl}/api/kyber/span/${encodeURIComponent(spanId)}/attributes`, 'span attributes')
  await readApiObject(
    page,
    `${baseUrl}/api/kyber/session/${encodeURIComponent(sessionId)}/content?span=${encodeURIComponent(spanId)}&full=true`,
    'span content',
  )

  const barsTab = page.getByTestId('timeline-tab-bars')
  if ((await barsTab.count()) > 0) await barsTab.click()
  const bar = page.getByTestId(`timeline-bar-${spanId}`)
  await mustBeVisible(bar, `timeline bar for span ${spanId}`)
  await bar.click()
  await mustBeVisible(page.getByTestId('span-inspector'), `span inspector for span ${spanId}`)

  const appeared = await firstAppearingTestId(page, [
    'drawer-content-scroll',
    'drawer-content-fallback-note',
    'tool-call-view',
    'tool-result-view',
  ])
  let drawerContent: ParityTimelineInspection['drawerContent'] = 'none'
  if (appeared === 'drawer-content-fallback-note') drawerContent = 'fallback-note'
  else if (appeared !== null) drawerContent = 'content'

  if (drawerContent === 'none') {
    record({
      code: 'tool-invocation-absent',
      dimension: 'tool-invocations',
      detail: `span ${spanId} opened no full-content drawer view`,
    })
  }

  const close = page.getByTestId('drawer-close-button')
  if ((await close.count()) > 0) await close.click()

  return {
    toolSpanIds: toolSpans.map((span) => stringOrEmpty(span.spanId)).filter((id) => id !== ''),
    inspectedSpanId: spanId,
    drawerContent,
  }
}

async function assertSessionRequests(
  page: ParityPageLike,
  baseUrl: string,
  sessionId: string,
  record: GapRecorder,
): Promise<ContentParityResult['requests']> {
  await mustBeVisible(page.getByTestId('user-requests'), 'user requests section')
  const requests = await readApiObject(
    page,
    `${baseUrl}/api/kyber/session/${encodeURIComponent(sessionId)}/requests?page=1&limit=${SECTION_PAGE_LIMIT}&order=desc`,
    'requests',
  )
  const rows = sectionRows(requests, 'requests')
  const total = numberOr(requests.total, rows.length)
  const offset = numberOr(requests.offset, 0)

  if (total === 0 || rows.length === 0) {
    await mustBeVisible(page.getByTestId('requests-empty'), 'empty user-requests note')
    if ((await countTestIdPrefix(page, 'request-row-')) !== 0) {
      throw new Error(`session ${sessionId} rendered request rows while the route served none`)
    }
    record({
      code: 'requests-empty',
      dimension: 'user-requests',
      detail: `session ${sessionId} recorded no session_request rows`,
    })
    return { total, rowsOnPage: 0 }
  }

  for (let index = 0; index < rows.length; index += 1) {
    const position = offset + index + 1
    const row = page.getByTestId(`request-row-${position}`)
    await mustBeVisible(row, `request row ${position}`)
    const prompt = stringOrEmpty(rows[index]?.prompt)
    const expected = prompt !== '' ? prompt : `Request ${position}`
    const text = await row.innerText()
    if (!text.includes(expected)) {
      throw new Error(
        `session ${sessionId} request row ${position} rendered '${text}' instead of a label carrying '${expected}'`,
      )
    }
  }
  return { total, rowsOnPage: rows.length }
}

async function assertSessionTools(
  page: ParityPageLike,
  baseUrl: string,
  sessionId: string,
  record: GapRecorder,
): Promise<ContentParityResult['tools']> {
  const section = page.getByTestId('tool-schema-section')
  await mustBeVisible(section, 'tool schema section')
  const tools = await readApiObject(
    page,
    `${baseUrl}/api/kyber/session/${encodeURIComponent(sessionId)}/tools?page=1&limit=${SECTION_PAGE_LIMIT}&order=desc`,
    'tools',
  )
  const rows = sectionRows(tools, 'tools')
  const aggregate = isRecord(tools.aggregate) ? tools.aggregate : {}
  const measurable = aggregate.measurable === true
  const definitionsSpanId = stringOrEmpty(aggregate.definitionsSpanId)

  if (measurable) {
    await mustBeVisible(section.getByTestId('tools-ranking-table'), 'tools ranking table')
    const first = rows[0]
    if (first !== undefined) {
      await mustBeVisible(
        section.getByTestId(`tool-row-${stringOrEmpty(first.name)}`),
        `tool row '${stringOrEmpty(first.name)}'`,
      )
    }
  } else {
    await mustBeVisible(section.getByTestId('schemas-not-exported-banner'), 'schemas not-exported banner')
    record({
      code: 'tool-definition-unavailable',
      dimension: 'tool-definitions',
      detail: `session ${sessionId} reported tool definitions not measurable: ${stringOrEmpty(aggregate.reason) || 'declared_not_measurable'}`,
    })
  }

  return {
    measurable,
    rowsOnPage: rows.length,
    definitionsSpanId: definitionsSpanId === '' ? null : definitionsSpanId,
  }
}

async function assertStoredFields(
  page: ParityPageLike,
  baseUrl: string,
  sessionId: string,
  record: GapRecorder,
): Promise<ContentParityResult['storedFields']> {
  await mustBeVisible(page.getByTestId('stored-fields-panel'), 'stored fields panel')
  const stored = await readApiObject(
    page,
    `${baseUrl}/api/kyber/session/${encodeURIComponent(sessionId)}/stored-fields`,
    'stored fields',
  )
  const tables = (Array.isArray(stored.tables) ? stored.tables : []).filter(isRecord)
  const tableNames = tables.map((table) => stringOrEmpty(table.tableName))

  for (const name of ['content_part', 'content_part_text', 'content_part_withheld', 'tool_definition']) {
    if (!tableNames.includes(name)) continue
    await mustBeVisible(
      page.getByTestId(`stored-fields-table-${name}`),
      `stored fields table '${name}'`,
    )
  }

  const toolCall = tables.find((table) => stringOrEmpty(table.tableName) === 'tool_call')
  const toolCallRows = toolCall === undefined ? 0 : numberOr(toolCall.rowCount, 0)
  await readApiObject(
    page,
    `${baseUrl}/api/kyber/session/${encodeURIComponent(sessionId)}/stored-fields/tool_call?page=1&limit=${SECTION_PAGE_LIMIT}`,
    'stored tool_call rows',
  )

  if (toolCallRows > 0) {
    const toggle = page.getByTestId('stored-fields-table-toggle-tool_call')
    await mustBeVisible(toggle, 'stored tool_call row toggle')
    await toggle.click()
    // The rows load through their own route after the toggle opens them, so the first row is
    // waited for before the count is read; a count taken on the click's own tick reads zero.
    const tableRows = page.getByTestId('stored-fields-table-tool_call')
    await mustBeVisible(tableRows.getByTestId('stored-field-row-tool_call-0'), 'first stored tool_call row')
    const rows = await countTestIdPrefix(tableRows, 'stored-field-row-tool_call-')
    if (rows === 0) {
      throw new Error(`session ${sessionId} stored ${toolCallRows} tool_call row(s) but rendered none`)
    }
  } else {
    await mustBeVisible(page.getByTestId('stored-fields-empty-tool_call'), 'stored empty tool_call note')
    record({
      code: 'stored-field-empty',
      dimension: 'stored-fields',
      detail: `session ${sessionId} stored no tool_call rows`,
    })
  }

  return { tables: tableNames, toolCallRows }
}

/**
 * Asserts one session's full content, section by section, against the harness contract: the
 * summary/requests/tools/stored-fields API payloads, the rendered user requests and tool ranking,
 * the chart-gated turn drawer (falling back to a timeline `llm.invoke` span when the charts are
 * unmeasurable), timeline tool-span I/O, and the direct `/session/:id/turn/:index` page.
 *
 * Diagnostic gaps are returned in `result.gaps` (and are meant for `test.info().annotations`); a
 * `content-part-drop` throws unless the harness documents it, and `strict` mode throws on any gap.
 *
 * @throws {Error} `sessionId must be non-empty` for a blank session id.
 * @throws {Error} when the session is not listed, an API route answers non-200, or a rendered
 *   surface contradicts the served payload.
 */
export async function assertHarnessFullContentParity(
  page: ParityPageLike,
  contract: HarnessExpectationContract,
  baseUrl: string,
  options: FullContentParityOptions = {},
): Promise<ContentParityResult> {
  const harness = contract.harness
  const sessionId = contract.sessionId
  assertNonEmptySessionId(sessionId)

  const turnIndex = options.turnIndex ?? 0
  const mode = options.diagnosticMode ?? 'annotate-gaps'
  const documentedGaps = getParityExpectation(harness).knownGaps
  const gaps: ParityGapAnnotation[] = []
  const record: GapRecorder = (evidence) => {
    const annotated = classifyParityGap({ ...evidence, harness, sessionId, documentedGaps }, { mode })
    gaps.push(annotated)
    return annotated
  }

  const sessions = await readApiObject(
    page,
    `${baseUrl}/api/kyber/sessions?harness=${encodeURIComponent(harness)}&limit=${SESSION_PAGE_LIMIT}`,
    'sessions listing',
  )
  const sessionRows = (Array.isArray(sessions.sessions) ? sessions.sessions : []).filter(isRecord)
  const listed = sessionRows.some(
    (row) => row.session_id === sessionId || row.sessionId === sessionId,
  )
  if (!listed) {
    throw new Error(`session ${sessionId} is not listed under harness ${harness} in the sessions route`)
  }

  await readApiObject(page, `${baseUrl}/api/kyber/session/${encodeURIComponent(sessionId)}/summary`, 'session summary')

  await page.goto(baseUrl)
  await page.getByTestId('nav-tab-sessions').click()
  await mustBeVisible(page.getByTestId('page-sessions'), 'sessions page')
  await page.getByTestId(`harness-tab-${harness}`).click()
  await page.getByTestId(`agent-session-row-${sessionId}`).click()
  await mustBeVisible(page.getByTestId('agent-session-dashboard'), 'agent session dashboard')

  const requests = await assertSessionRequests(page, baseUrl, sessionId, record)
  const tools = await assertSessionTools(page, baseUrl, sessionId, record)
  const storedFields = await assertStoredFields(page, baseUrl, sessionId, record)

  const spend = page.getByTestId('spend-composition-section')
  await mustBeVisible(spend, 'spend composition section')
  const turnBars = await countTestIdPrefix(spend, 'turn-bar-')
  const contextTurns = await countTestIdPrefix(spend, 'context-turn-')

  const recordUnreachableDrawer = (detail: string): void => {
    record({
      code: 'unreachable-drawer',
      dimension: 'context-content',
      detail,
      fallback: 'timeline llm.invoke span inspection',
    })
  }

  let drawer: ParityDrawerInspection = { reachable: false, blocks: [], turnContentEmpty: false }
  let preferLlm = false
  if (turnBars > 0 || contextTurns > 0) {
    const inspected = await inspectChartDrawer(page, baseUrl, sessionId, turnIndex, record)
    if (inspected !== null) {
      drawer = inspected
    } else {
      preferLlm = true
      recordUnreachableDrawer('no chart click opened the turn-content drawer')
    }
  } else {
    preferLlm = true
    recordUnreachableDrawer('both per-turn charts are unmeasurable, so no chart opens the turn-content drawer')
  }

  const timeline = await inspectTimelineSpans(page, baseUrl, sessionId, preferLlm, record)

  let turn: TurnFullContentInspection | null = null
  if (options.includeTurnDetail !== false) {
    turn = await inspectTurnFullContent(page, sessionId, turnIndex, { baseUrl })
  }

  return { harness, sessionId, requests, tools, storedFields, drawer, timeline, turn, gaps }
}
