// Unit contracts for the reusable full-content parity assertions.
//
// The helpers in dash/e2e/session-dashboard-assertions.ts normally run inside Playwright
// against a live dashboard. These tests drive them over an in-memory page seam that answers the
// same locator and request calls a real Playwright `Page` does, so every branch is exercised
// without a browser: the chart-gated turn drawer and its unmeasurable-chart fallback, the direct
// /session/:id/turn/:index page, timeline span inspection, tool definitions, stored fields,
// and the refusal paths that must fail loudly instead of hiding a collection or display gap.
import { describe, expect, it } from 'vitest'

import type { HarnessExpectationContract } from '../../e2e/otel-harness-payloads.js'
import {
  assertHarnessFullContentParity,
  classifyParityGap,
  inspectTurnFullContent,
  type ParityApiResponseLike,
  type ParityGapEvidence,
  type ParityLocatorLike,
  type ParityPageLike,
} from '../../e2e/session-dashboard-assertions.js'

const SESSION_ID = 'ses-copilot-baseline'
const BASE_URL = 'http://127.0.0.1:4010'

const CANONICAL_BLOCK_KEYS = [
  'system_prompt',
  'tool_definitions',
  'instruction_context',
  'conversation_history',
  'tool_result_content',
] as const

type FakeElement = {
  readonly testId: string
  readonly text?: string
  readonly visible?: boolean
  readonly enabled?: boolean
  readonly attrs?: Record<string, string>
}

type ApiAnswer = { readonly status: number; readonly body: unknown }

type ApiRouter = (url: URL) => ApiAnswer | undefined

function json(status: number, body: unknown): ApiAnswer {
  return { status, body }
}

/**
 * The in-memory page world: a flat element registry keyed by test id, click handlers that mutate
 * that registry the way the dashboard's state does, and an API router that answers each route.
 */
class FakeWorld {
  readonly elements: FakeElement[] = []
  readonly handlers = new Map<string, () => void>()
  readonly visited: string[] = []
  private readonly router: ApiRouter
  private gotoHandler: (url: string) => void = () => {}

  constructor(router: ApiRouter) {
    this.router = router
  }

  add(...elements: FakeElement[]): void {
    this.elements.push(...elements)
  }

  removeWhere(predicate: (element: FakeElement) => boolean): void {
    for (let index = this.elements.length - 1; index >= 0; index -= 1) {
      const element = this.elements[index]
      if (element !== undefined && predicate(element)) this.elements.splice(index, 1)
    }
  }

  setEnabled(testId: string, enabled: boolean): void {
    for (let index = 0; index < this.elements.length; index += 1) {
      const element = this.elements[index]
      if (element !== undefined && element.testId === testId) {
        this.elements[index] = { ...element, enabled }
      }
    }
  }

  handle(testId: string, handler: () => void): void {
    this.handlers.set(testId, handler)
  }

  onGoto(handler: (url: string) => void): void {
    this.gotoHandler = handler
  }

  navigate(url: string): void {
    this.visited.push(url)
    this.gotoHandler(url)
  }

  answer(url: string): ApiAnswer {
    return this.router(new URL(url)) ?? json(404, { error: 'not found' })
  }

  locator(selector: string): FakeLocator {
    const prefix = selector.match(/^\[data-testid\^="(.+)"\]$/)
    if (prefix !== null) {
      const value = prefix[1] ?? ''
      return new FakeLocator(this, () => this.elements.filter((element) => element.testId.startsWith(value)))
    }
    const exact = selector.match(/^\[data-testid="(.+)"\]$/)
    if (exact !== null) {
      const value = exact[1] ?? ''
      return new FakeLocator(this, () => this.elements.filter((element) => element.testId === value))
    }
    throw new Error(`fake world cannot parse selector '${selector}'`)
  }

  click(element: FakeElement): void {
    const exact = this.handlers.get(element.testId)
    if (exact !== undefined) {
      exact()
      return
    }
    for (const [prefix, handler] of this.handlers) {
      if (element.testId.startsWith(prefix)) {
        handler()
        return
      }
    }
  }
}

class FakeLocator implements ParityLocatorLike {
  constructor(
    private readonly world: FakeWorld,
    private readonly resolve: () => FakeElement[],
  ) {}

  async count(): Promise<number> {
    return this.resolve().length
  }

  async isVisible(): Promise<boolean> {
    return this.resolve().some((element) => element.visible !== false)
  }

  async isEnabled(): Promise<boolean> {
    return this.resolve().some((element) => element.enabled !== false)
  }

  async innerText(): Promise<string> {
    return this.resolve()
      .map((element) => element.text ?? '')
      .join('\n')
  }

  async getAttribute(name: string): Promise<string | null> {
    const first = this.resolve()[0]
    if (first === undefined) return null
    if (name === 'data-testid') return first.testId
    return first.attrs?.[name] ?? null
  }

  async click(): Promise<void> {
    const first = this.resolve()[0]
    if (first === undefined) throw new Error('cannot click a missing element')
    this.world.click(first)
  }

  first(): ParityLocatorLike {
    return new FakeLocator(this.world, () => this.resolve().slice(0, 1))
  }

  locator(selector: string): ParityLocatorLike {
    return this.world.locator(selector)
  }

  getByTestId(testId: string): ParityLocatorLike {
    return this.world.locator(`[data-testid="${testId}"]`)
  }

  async all(): Promise<ParityLocatorLike[]> {
    return this.resolve().map(
      (element) => new FakeLocator(this.world, () => this.world.elements.filter((candidate) => candidate === element)),
    )
  }

  async waitFor(options?: { state?: 'attached' | 'detached' | 'visible' | 'hidden' }): Promise<void> {
    const state = options?.state ?? 'visible'
    if (state === 'visible' && !(await this.isVisible())) throw new Error('element is not visible')
    if (state === 'attached' && (await this.count()) === 0) throw new Error('element is not attached')
    if (state === 'hidden' && (await this.isVisible())) throw new Error('element is visible')
    if (state === 'detached' && (await this.count()) > 0) throw new Error('element is attached')
  }
}

class FakePage implements ParityPageLike {
  readonly request: { get: (url: string) => Promise<ParityApiResponseLike> }

  constructor(private readonly world: FakeWorld) {
    this.request = {
      get: async (url: string): Promise<ParityApiResponseLike> => {
        const answer = world.answer(url)
        return {
          status: () => answer.status,
          text: async () => JSON.stringify(answer.body),
        }
      },
    }
  }

  async goto(url: string): Promise<unknown> {
    this.world.navigate(url)
    return null
  }

  getByTestId(testId: string): ParityLocatorLike {
    return this.world.locator(`[data-testid="${testId}"]`)
  }

  locator(selector: string): ParityLocatorLike {
    return this.world.locator(selector)
  }
}

function contract(overrides: Partial<HarnessExpectationContract> = {}): HarnessExpectationContract {
  return {
    harness: 'copilot',
    sessionId: SESSION_ID,
    turns: 2,
    expectedToolCalls: 1,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: true,
    schemaMeasurable: true,
    skillsMeasurable: true,
    perTurnTokensMeasurable: true,
    pairedWithSessionFiles: false,
    expectedSkills: [],
    expectedCostStatus: 'harness',
    expectedModels: ['gpt-6.2-sol'],
    expectedGapSubstrings: [],
    sendsLogs: true,
    ...overrides,
  }
}

function sessionDashboardElements(): FakeElement[] {
  return [
    { testId: 'page-context-doctor' },
    { testId: 'nav-tab-sessions' },
    { testId: 'page-sessions' },
    { testId: 'harness-tab-copilot' },
    { testId: `agent-session-row-${SESSION_ID}` },
    { testId: 'agent-session-dashboard' },
    { testId: 'user-requests', text: '2 user requests in this session:' },
    { testId: 'request-row-1', text: '1. Do the thing' },
    { testId: 'request-row-2', text: '2. Request 2' },
    { testId: 'tool-schema-section' },
    { testId: 'tools-ranking-table' },
    { testId: 'tool-row-read_file' },
    { testId: 'stored-fields-panel' },
    { testId: 'stored-fields-table-content_part' },
    { testId: 'stored-fields-table-content_part_text' },
    { testId: 'stored-fields-table-content_part_withheld' },
    { testId: 'stored-fields-table-tool_definition' },
    { testId: 'stored-fields-table-tool_call' },
    { testId: 'stored-fields-table-toggle-tool_call' },
    { testId: 'spend-composition-section' },
    { testId: 'turn-bar-0' },
    { testId: 'context-turn-0' },
    { testId: 'execution-timeline-section' },
    { testId: 'timeline-tree-view' },
    { testId: 'timeline-node' },
    { testId: 'timeline-tab-bars' },
  ]
}

function wireDashboard(world: FakeWorld, options: { readonly toolCallRows?: number } = {}): void {
  world.add(...sessionDashboardElements())
  const rows = options.toolCallRows ?? 2
  if (rows > 0) {
    world.handle('stored-fields-table-toggle-tool_call', () => {
      for (let index = 0; index < rows; index += 1) world.add({ testId: `stored-field-row-tool_call-${index}` })
    })
  }
}

function turnDrawerElements(overrides: { readonly emptyBlocks?: readonly string[] } = {}): FakeElement[] {
  const empty = new Set(overrides.emptyBlocks ?? ['tool_definitions', 'instruction_context', 'tool_result_content'])
  const elements: FakeElement[] = [
    { testId: 'session-inspector-drawer' },
    { testId: 'drawer-close-button' },
    { testId: 'turn-content-full' },
    { testId: 'copy-turn-button' },
  ]
  for (const key of CANONICAL_BLOCK_KEYS) {
    elements.push({ testId: `turn-block-${key}` })
    if (empty.has(key)) {
      elements.push({ testId: `turn-block-${key}-empty` })
    } else {
      elements.push({ testId: `copy-turn-block-${key}` })
      elements.push({ testId: `turn-block-${key}-view-raw` })
    }
  }
  return elements
}

function turnPageElements(options: {
  readonly bands: readonly string[]
  readonly assembled: boolean
}): FakeElement[] {
  const elements: FakeElement[] = [
    { testId: 'page-turn', text: options.bands.length > 0 ? '' : 'No context bands were recorded for this turn.' },
    { testId: 'context-inspector' },
    { testId: 'copy-block-button', enabled: options.assembled },
    { testId: 'copy-turn-button', enabled: options.assembled },
    { testId: options.assembled ? 'context-content-pane' : 'empty-content-pane' },
  ]
  for (const key of options.bands) {
    elements.push({ testId: `context-band-${key}` })
  }
  return elements
}

function wireTurnDrawer(world: FakeWorld, emptyBlocks?: readonly string[]): void {
  world.handle('turn-bar-', () => {
    world.add(...turnDrawerElements({ emptyBlocks }))
    for (const key of CANONICAL_BLOCK_KEYS) {
      if ((emptyBlocks ?? []).includes(key)) continue
      world.handle(`turn-block-${key}-view-raw`, () => world.add({ testId: `turn-block-${key}-raw` }))
    }
  })
  world.handle('drawer-close-button', () => {
    world.removeWhere((element) => element.testId.startsWith('turn-block-') || element.testId === 'turn-content-full' || element.testId === 'copy-turn-button' || element.testId === 'session-inspector-drawer' || element.testId === 'drawer-close-button' || element.testId === 'span-inspector' || element.testId === 'drawer-content-scroll')
  })
}

function wireTimeline(world: FakeWorld, spanIds: { readonly tool: string; readonly llm: string }): void {
  world.handle('timeline-tab-bars', () => {
    world.add({ testId: 'timeline-bars-view' })
    world.add({ testId: `timeline-bar-${spanIds.tool}` })
    world.add({ testId: `timeline-bar-${spanIds.llm}` })
  })
  world.handle('timeline-bar-', () => {
    world.add({ testId: 'span-inspector' })
    world.add({ testId: 'drawer-content-scroll' })
    world.add({ testId: 'drawer-close-button' })
  })
}

function wireTurnPage(world: FakeWorld, options: { readonly bands: readonly string[]; readonly assembled: boolean }): void {
  world.onGoto((url) => {
    if (!url.includes('/turn/')) return
    world.add(...turnPageElements(options))
    for (const key of options.bands) {
      world.handle(`context-band-${key}`, () => {
        world.removeWhere((element) => element.testId === 'context-content-pane' || element.testId === 'empty-content-pane' || element.testId === 'not-measurable-pane')
        world.add({ testId: key === 'instruction_context' ? 'not-measurable-pane' : 'context-content-pane' })
        world.setEnabled('copy-block-button', key !== 'instruction_context')
      })
    }
  })
}

function apiRouter(options: {
  readonly requests?: unknown
  readonly tools?: unknown
  readonly storedFields?: unknown
  readonly storedToolCallRows?: unknown
  readonly turnParts?: readonly unknown[]
  readonly projectionFailed?: boolean
  readonly timelineNodes?: readonly unknown[]
  readonly missingSession?: boolean
  readonly contentStatus?: number
}): ApiRouter {
  const turnParts = options.turnParts ?? [{ part: 'system_prompt', text: 'You are a helpful agent.' }]
  const timelineNodes = options.timelineNodes ?? [
    { spanId: 'span-tool', kind: 'tool.invoke', name: 'execute_tool', childCount: 0 },
    { spanId: 'span-llm', kind: 'llm.invoke', name: 'invoke_agent', childCount: 0 },
  ]
  return (url) => {
    const path = url.pathname
    if (path === '/api/kyber/sessions') {
      return options.missingSession === true
        ? json(200, { sessions: [], total: 0, page: 1, limit: 100 })
        : json(200, { sessions: [{ session_id: SESSION_ID, models: ['gpt-6.2-sol'] }], total: 1, page: 1, limit: 100 })
    }
    if (path.endsWith('/summary')) {
      return json(200, { session_id: SESSION_ID, harness: 'copilot' })
    }
    if (path.endsWith('/requests')) {
      return json(
        200,
        options.requests ?? {
          entries: [
            { request: 'req-1', prompt: 'Do the thing', turns: 1, model: null, timestamp: null },
            { request: 'req-2', prompt: null, turns: 1, model: null, timestamp: null },
          ],
          total: 2,
          page: 1,
          limit: 25,
          offset: 0,
        },
      )
    }
    if (path.endsWith('/tools')) {
      return json(
        200,
        options.tools ?? {
          entries: [
            { name: 'read_file', server: null, invocations: 1, invoked: true, in_definitions: true, is_mcp: false },
          ],
          aggregate: {
            maxCost: 10,
            residentCostTotal: 10,
            invocationCount: 1,
            unusedCount: 0,
            unusedRange: null,
            byServer: {},
            definitionsSpanId: 'span-def',
            measurable: true,
            reason: null,
          },
          total: 1,
          page: 1,
          limit: 25,
          offset: 0,
        },
      )
    }
    if (path.endsWith('/stored-fields')) {
      return json(
        200,
        options.storedFields ?? {
          sessionKey: SESSION_ID,
          tables: [
            { tableName: 'content_part', columnNames: ['part'], rowCount: 3, missingParentKeys: [] },
            { tableName: 'content_part_text', columnNames: ['part_text'], rowCount: 2, missingParentKeys: [] },
            { tableName: 'content_part_withheld', columnNames: ['part_text'], rowCount: 1, missingParentKeys: [] },
            { tableName: 'tool_definition', columnNames: ['name'], rowCount: 1, missingParentKeys: [] },
            { tableName: 'tool_call', columnNames: ['name'], rowCount: 2, missingParentKeys: [] },
          ],
        },
      )
    }
    if (path.endsWith('/stored-fields/tool_call')) {
      return json(
        200,
        options.storedToolCallRows ?? {
          tableName: 'tool_call',
          columnNames: ['name'],
          totalRows: 2,
          page: 1,
          limit: 25,
          offset: 0,
          rows: [{ cells: [] }],
        },
      )
    }
    if (/\/turn\/\d+\/content$/.test(path)) {
      const status = options.contentStatus ?? 200
      const body = options.projectionFailed === true
        ? { sessionId: SESSION_ID, turnIndex: 0, projection_status: 'PROJECTION_FAILED' }
        : { sessionId: SESSION_ID, turnIndex: 0, parts: turnParts }
      return json(status, body)
    }
    if (path.endsWith('/timeline')) {
      return json(200, { nodes: timelineNodes, total: timelineNodes.length, page: 1, limit: 25, offset: 0 })
    }
    if (/\/span\/[^/]+\/attributes$/.test(path)) {
      return json(200, { spanId: 'span-tool', attributes: {} })
    }
    if (path.endsWith('/content')) {
      return json(200, { sessionId: SESSION_ID, spanId: 'span-tool', parts: [] })
    }
    return undefined
  }
}

function buildPage(router: ApiRouter): { page: FakePage; world: FakeWorld } {
  const world = new FakeWorld(router)
  return { page: new FakePage(world), world }
}

function baseGap(overrides: Partial<ParityGapEvidence> = {}): ParityGapEvidence {
  return {
    code: 'unreachable-drawer',
    harness: 'copilot',
    sessionId: SESSION_ID,
    dimension: 'context-content',
    detail: 'no clickable turn bar',
    ...overrides,
  }
}

describe('classifyParityGap', () => {
  it('annotates an intrinsic honest state with no documentation required', () => {
    const annotation = classifyParityGap(baseGap())
    expect(annotation.code).toBe('unreachable-drawer')
    expect(annotation.documented).toBe(true)
    expect(annotation.annotation).toContain('unreachable-drawer: no clickable turn bar')
  })

  it('carries the fallback into the annotation', () => {
    const annotation = classifyParityGap(baseGap({ fallback: 'timeline llm.invoke span inspection' }))
    expect(annotation.annotation).toContain('fallback: timeline llm.invoke span inspection')
  })

  it('annotates a content drop documented in the harness parity matrix', () => {
    const annotation = classifyParityGap(
      baseGap({ code: 'content-part-drop', dimension: 'context-content', documentedGaps: ['content-part-drop'] }),
    )
    expect(annotation.documented).toBe(true)
    expect(annotation.code).toBe('content-part-drop')
  })

  it('refuses an unannotated content drop', () => {
    expect(() =>
      classifyParityGap(baseGap({ code: 'content-part-drop', documentedGaps: ['content_part_withheld'] })),
    ).toThrow(/Unannotated content drop/)
  })

  it('refuses any gap in strict mode, documented or not', () => {
    expect(() => classifyParityGap(baseGap(), { mode: 'strict' })).toThrow(/Parity gap \[unreachable-drawer\]/)
    expect(() =>
      classifyParityGap(baseGap({ code: 'content-part-drop', documentedGaps: ['content-part-drop'] }), {
        mode: 'strict',
      }),
    ).toThrow(/Parity gap \[content-part-drop\]/)
  })
})

describe('inspectTurnFullContent', () => {
  it('refuses a blank session id before touching the page', async () => {
    const { page } = buildPage(apiRouter({}))
    await expect(inspectTurnFullContent(page, '   ', undefined, { baseUrl: BASE_URL })).rejects.toThrow(
      'sessionId must be non-empty',
    )
  })

  it('defaults an omitted turn index to 0', async () => {
    const { page, world } = buildPage(apiRouter({}))
    wireTurnPage(world, { bands: ['system_prompt'], assembled: true })
    const inspection = await inspectTurnFullContent(page, SESSION_ID, undefined, { baseUrl: BASE_URL })
    expect(inspection.turnIndex).toBe(0)
    expect(world.visited.at(-1)).toBe(`${BASE_URL}/session/${SESSION_ID}/turn/0`)
  })

  it('inspects bands, panes, and the enabled copy controls of a populated turn', async () => {
    const { page, world } = buildPage(
      apiRouter({
        turnParts: [
          { part: 'system_prompt', text: 'You are a helpful agent.' },
          { part: 'conversation_history', text: 'user: hi' },
          { part: 'instruction_context', text: 'not measurable here', },
        ],
      }),
    )
    // The API part above still carries text, but the turn page renders instruction_context as
    // not-measurable, which the helper must accept as a disabled copy-block state.
    wireTurnPage(world, { bands: ['system_prompt', 'conversation_history', 'instruction_context'], assembled: true })
    const inspection = await inspectTurnFullContent(page, SESSION_ID, 0, { baseUrl: BASE_URL })

    expect(inspection.api.partCount).toBe(3)
    expect(inspection.api.projectionFailed).toBe(false)
    expect(inspection.bandKeys).toEqual(['system_prompt', 'conversation_history', 'instruction_context'])
    expect(inspection.noBandsNote).toBe(false)
    expect(inspection.wholeTurnPane).toBe('content')
    expect(inspection.copyTurnEnabled).toBe(true)
    expect(inspection.blocks).toEqual([
      { key: 'system_prompt', pane: 'content', copyBlockEnabled: true },
      { key: 'conversation_history', pane: 'content', copyBlockEnabled: true },
      { key: 'instruction_context', pane: 'not-measurable', copyBlockEnabled: false },
    ])
  })

  it('reports the empty turn: no bands, the honest note, and disabled copy', async () => {
    const { page, world } = buildPage(apiRouter({ turnParts: [] }))
    wireTurnPage(world, { bands: [], assembled: false })
    const inspection = await inspectTurnFullContent(page, SESSION_ID, 0, { baseUrl: BASE_URL })

    expect(inspection.api.partCount).toBe(0)
    expect(inspection.bandKeys).toEqual([])
    expect(inspection.noBandsNote).toBe(true)
    expect(inspection.wholeTurnPane).toBe('empty')
    expect(inspection.copyTurnEnabled).toBe(false)
    expect(inspection.blocks).toEqual([])
  })

  it('refuses when the turn content route answers non-200', async () => {
    const { page } = buildPage(apiRouter({ contentStatus: 500 }))
    await expect(inspectTurnFullContent(page, SESSION_ID, 0, { baseUrl: BASE_URL })).rejects.toThrow(
      /turn content answered HTTP 500/,
    )
  })

  it('reports a projection fault instead of reading it as an empty turn', async () => {
    const { page, world } = buildPage(apiRouter({ projectionFailed: true }))
    world.onGoto((url) => {
      if (url.includes('/turn/')) world.add({ testId: 'page-turn' }, { testId: 'turn-projection-failed' })
    })
    const inspection = await inspectTurnFullContent(page, SESSION_ID, 0, { baseUrl: BASE_URL })
    expect(inspection.api.projectionFailed).toBe(true)
    expect(inspection.bandKeys).toEqual([])
    expect(inspection.blocks).toEqual([])
  })
})

describe('assertHarnessFullContentParity', () => {
  it('refuses a blank session id', async () => {
    const { page } = buildPage(apiRouter({}))
    await expect(assertHarnessFullContentParity(page, contract({ sessionId: '' }), BASE_URL)).rejects.toThrow(
      'sessionId must be non-empty',
    )
  })

  it('refuses a session the harness listing does not carry', async () => {
    const { page } = buildPage(apiRouter({ missingSession: true }))
    await expect(assertHarnessFullContentParity(page, contract(), BASE_URL)).rejects.toThrow(/is not listed under harness/)
  })

  it('asserts the full dashboard, drawer, timeline, and turn page of a complete session', async () => {
    const { page, world } = buildPage(apiRouter({}))
    wireDashboard(world)
    wireTurnDrawer(world)
    wireTimeline(world, { tool: 'span-tool', llm: 'span-llm' })
    wireTurnPage(world, { bands: ['system_prompt'], assembled: true })

    const result = await assertHarnessFullContentParity(page, contract(), BASE_URL)

    expect(result.requests).toEqual({ total: 2, rowsOnPage: 2 })
    expect(result.tools.measurable).toBe(true)
    expect(result.tools.rowsOnPage).toBe(1)
    expect(result.storedFields.toolCallRows).toBe(2)
    expect(result.storedFields.tables).toContain('content_part_withheld')
    expect(result.drawer.reachable).toBe(true)
    expect(result.drawer.blocks).toContainEqual({ key: 'system_prompt', empty: false, rawTogglePresent: true, copyPresent: true })
    expect(result.drawer.blocks).toContainEqual({ key: 'tool_result_content', empty: true, rawTogglePresent: false, copyPresent: false })
    expect(result.timeline.inspectedSpanId).toBe('span-tool')
    expect(result.timeline.drawerContent).toBe('content')
    expect(result.turn?.sessionId).toBe(SESSION_ID)
    expect(result.turn?.blocks.map((block) => block.key)).toEqual(['system_prompt'])
    expect(result.gaps).toEqual([])
  })

  it('normalizes a blank request prompt to its position label', async () => {
    const { page, world } = buildPage(
      apiRouter({
        requests: {
          entries: [
            { request: 'req-1', prompt: '', turns: 1, model: null, timestamp: null },
            { request: 'req-2', prompt: null, turns: 1, model: null, timestamp: null },
          ],
          total: 2,
          page: 1,
          limit: 25,
          offset: 0,
        },
      }),
    )
    world.add(
      ...sessionDashboardElements().filter((element) => element.testId !== 'request-row-1' && element.testId !== 'request-row-2'),
      { testId: 'request-row-1', text: '1. Request 1' },
      { testId: 'request-row-2', text: '2. Request 2' },
    )
    world.handle('stored-fields-table-toggle-tool_call', () => world.add({ testId: 'stored-field-row-tool_call-0' }))
    wireTurnDrawer(world)
    wireTimeline(world, { tool: 'span-tool', llm: 'span-llm' })
    wireTurnPage(world, { bands: ['system_prompt'], assembled: true })

    const result = await assertHarnessFullContentParity(page, contract(), BASE_URL)
    expect(result.requests.rowsOnPage).toBe(2)
  })

  it('records an unreachable drawer and falls back to the timeline when the charts are unmeasurable', async () => {
    const { page, world } = buildPage(apiRouter({ timelineNodes: [{ spanId: 'span-llm', kind: 'llm.invoke', name: 'invoke_agent', childCount: 0 }] }))
    world.add(
      ...sessionDashboardElements().filter(
        (element) => element.testId !== 'turn-bar-0' && element.testId !== 'context-turn-0',
      ),
      { testId: 'turn-spend-not-measurable' },
      { testId: 'context-composition-not-measurable' },
    )
    world.handle('stored-fields-table-toggle-tool_call', () => world.add({ testId: 'stored-field-row-tool_call-0' }))
    wireTimeline(world, { tool: 'span-tool', llm: 'span-llm' })

    const result = await assertHarnessFullContentParity(page, contract({ perTurnTokensMeasurable: false, contextMeasurable: false }), BASE_URL, {
      includeTurnDetail: false,
    })

    expect(result.drawer.reachable).toBe(false)
    expect(result.turn).toBeNull()
    expect(result.timeline.inspectedSpanId).toBe('span-llm')
    expect(result.gaps.map((gap) => gap.code)).toContain('unreachable-drawer')
  })

  it('records the tool-definition and stored-field empties as honest annotations', async () => {
    const { page, world } = buildPage(
      apiRouter({
        tools: {
          entries: [],
          aggregate: {
            maxCost: null,
            residentCostTotal: 0,
            invocationCount: 0,
            unusedCount: 0,
            unusedRange: null,
            byServer: {},
            definitionsSpanId: null,
            measurable: false,
            reason: 'declared_not_measurable',
          },
          total: 0,
          page: 1,
          limit: 25,
          offset: 0,
        },
        storedFields: {
          sessionKey: SESSION_ID,
          tables: [{ tableName: 'tool_call', columnNames: ['name'], rowCount: 0, missingParentKeys: [] }],
        },
      }),
    )
    world.add(
      ...sessionDashboardElements().filter(
        (element) => !element.testId.startsWith('stored-fields-table-') && element.testId !== 'tools-ranking-table',
      ),
      { testId: 'schemas-not-exported-banner' },
      { testId: 'stored-fields-table-tool_call' },
      { testId: 'stored-fields-empty-tool_call' },
    )
    wireTurnDrawer(world)
    wireTimeline(world, { tool: 'span-tool', llm: 'span-llm' })

    const result = await assertHarnessFullContentParity(page, contract(), BASE_URL, { includeTurnDetail: false })

    expect(result.tools.measurable).toBe(false)
    expect(result.storedFields.toolCallRows).toBe(0)
    expect(result.gaps.map((gap) => gap.code)).toEqual(
      expect.arrayContaining(['tool-definition-unavailable', 'stored-field-empty']),
    )
  })

  it('refuses an unannotated content drop when served parts render as empty blocks', async () => {
    const { page, world } = buildPage(apiRouter({}))
    wireDashboard(world)
    // Every block renders empty although the API served a non-empty system_prompt part.
    world.handle('turn-bar-', () => {
      world.add(...turnDrawerElements({ emptyBlocks: [...CANONICAL_BLOCK_KEYS] }))
    })
    world.handle('drawer-close-button', () => {})

    await expect(
      assertHarnessFullContentParity(page, contract(), BASE_URL, { includeTurnDetail: false }),
    ).rejects.toThrow(/Unannotated content drop \[content-part-drop\]/)
  })
})
