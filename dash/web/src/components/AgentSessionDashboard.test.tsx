// @vitest-environment happy-dom
// The session view as a whole. Opening a session issues one request per
// section, in page order, before any response resolves — and no content route
// fires until something is clicked. Each section renders its own states while
// every other route is held back, so one slow route never blanks the page.
// Every section body below is built to the API client's validator, from the
// server's own serializers (see kyberApi.sections.test.ts).
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactElement } from 'react'
import { readdirSync, readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

import { AgentSessionDashboard } from './AgentSessionDashboard.js'
import { findContextTurn, findTurnByTransport, fetchKyberSessionSummary } from '../lib/kyberApi.js'
import { formatDuration } from '../lib/labels.js'

const SESSION_ID = 'sess-abc-123'

const SUMMARY_ROUTE = `/api/kyber/session/${SESSION_ID}/summary`
const CHARTS_ROUTE = `/api/kyber/session/${SESSION_ID}/charts?page=1&limit=25&order=desc`
const TOOLS_ROUTE = `/api/kyber/session/${SESSION_ID}/tools?page=1&limit=25&order=desc`
const SERVERS_ROUTE = `/api/kyber/session/${SESSION_ID}/servers?page=1&limit=25&order=desc`
const SKILLS_ROUTE = `/api/kyber/session/${SESSION_ID}/skills?page=1&limit=25&order=desc`
const KW_SKILLS_ROUTE = `/api/kyber/session/${SESSION_ID}/kw-skills?page=1&limit=25&order=desc`
const TIMELINE_ROUTE = `/api/kyber/session/${SESSION_ID}/timeline?page=1&limit=25&order=desc`
const REQUESTS_ROUTE = `/api/kyber/session/${SESSION_ID}/requests?page=1&limit=25&order=desc`
const RECONCILIATION_ROUTE = `/api/kyber/session/${SESSION_ID}/reconciliation?page=1&limit=25&order=desc`
const PROBLEMS_ROUTE = `/api/kyber/session/${SESSION_ID}/problems?page=1&limit=25&order=desc`
const STORED_FIELDS_ROUTE = `/api/kyber/session/${SESSION_ID}/stored-fields`

const PAGE_ORDER = [
  SUMMARY_ROUTE,
  CHARTS_ROUTE,
  TOOLS_ROUTE,
  SERVERS_ROUTE,
  SKILLS_ROUTE,
  KW_SKILLS_ROUTE,
  TIMELINE_ROUTE,
  REQUESTS_ROUTE,
  RECONCILIATION_ROUTE,
  PROBLEMS_ROUTE,
  STORED_FIELDS_ROUTE,
]

function queryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
}

function jsonResponse(body: unknown, status = 200): Response {
  return { ok: status >= 200 && status < 300, status, json: async () => body } as unknown as Response
}

type Held = { resolve: (response: Response) => void }

/**
 * One fetch stub for the whole page: every route is held back until the test
 * answers it by URL, and every call is recorded in issue order.
 */
function stubSectionRoutes(): { calls: string[]; respond(route: string, body: unknown): void; fail(route: string): void } {
  const calls: string[] = []
  const held = new Map<string, Held[]>()
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string) => {
      calls.push(url)
      return new Promise<Response>((resolve) => {
        const queue = held.get(url) ?? []
        queue.push({ resolve })
        held.set(url, queue)
      })
    }),
  )
  const last = (route: string): Held | undefined => {
    const queue = held.get(route)
    return queue && queue.length > 0 ? queue[queue.length - 1] : undefined
  }
  return {
    calls,
    respond(route, body) {
      last(route)?.resolve(jsonResponse(body))
    },
    fail(route) {
      last(route)?.resolve(jsonResponse({}, 500))
    },
  }
}

function mountDashboard(onSelectSession?: (sessionId: string) => void): ReturnType<typeof render> {
  const element = (
    <QueryClientProvider client={queryClient()}>
      <AgentSessionDashboard sessionId={SESSION_ID} onSelectSession={onSelectSession} />
    </QueryClientProvider>
  ) as ReactElement
  return render(element)
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

// ---------------------------------------------------------------------------
// Section bodies, each valid for the client's validator
// ---------------------------------------------------------------------------

const SUMMARY_BODY = {
  session_id: SESSION_ID,
  harness: 'copilot',
  label: 'Fix parser AST recursion',
  is_subagent: false,
  parent_session: null,
  agent_name: 'coder-bot',
  repo: 'github.com/my-org/kyber-repo',
  branch: 'feature/ast-fix',
  started: '2026-11-19T10:00:00.000Z',
  ended: '2026-11-19T11:00:00.000Z',
  span_count: 48,
  problems: 0,
  problems_count: 0,
  request_count: 2,
  costBasisMismatch: null,
  summary: {
    turn_count: 5,
    reported_turn_count: 5,
    request_count: 2,
    total_input: 120500,
    total_output: 6400,
    total_cache_read: 75000,
    total_cache_creation: 18000,
    cache_creation_coverage: 2,
    cache_hit_ratio: 0.622,
    total_reasoning: 1500,
    duration_ms: 38400,
    models: ['claude-3-5-sonnet'],
    tool_calls: 8,
    tools_invoked: 2,
    tools_offered: 3,
    median_ttft_ms: 540,
    aux_chat_calls: 2,
    aux_models: ['gpt-4o-mini'],
    aux_input: 1200,
    aux_output: 80,
    unused_schema_per_turn: 900,
    schema_tokens_per_turn: 2800,
    defs_turns: 5,
    schema_waste_cost: { usd_low: 0.03, usd_high: 0.12, credits_low: 0.15, credits_high: 0.6 },
    cost: { usd: 1.25, credits: 6.25, basis: 'published_rates', status: 'ok' },
  },
  notes: ['Harness does not export raw workspace info files.', 'Tokens computed with o200k_base tokenizer.'],
  reconciliation: { total: 2, mismatched: 0 },
  tools_offered_count: 3,
  tools_invoked_count: 2,
  unused_tools_count: 1,
  skills_count: 2,
}

const CHARTS_BODY = {
  sessionId: SESSION_ID,
  total: 2,
  page: 1,
  limit: 25,
  offset: 0,
  turns: [
    {
      index: 0,
      spanId: 'turn-span-1',
      model: 'claude-3-5-sonnet',
      durationMs: 4200,
      fresh: 27500,
      cache_read: 0,
      cache_creation: 18000,
      output: 1200,
    },
    {
      index: 1,
      spanId: 'turn-span-2',
      model: 'claude-3-5-sonnet',
      durationMs: 5100,
      fresh: 5000,
      cache_read: 42000,
      cache_creation: 0,
      output: 1800,
    },
  ],
  context: {
    measurable: true,
    contextLimit: 200000,
    turns: [
      {
        turn: 1,
        reported_input: 45500,
        buckets: { system_prompt: 4000, tool_definitions: 2800, residual: 1500 },
      },
    ],
    first: { turn: 1, buckets: { system_prompt: 4000 }, reported_input: 45500 },
    last: { turn: 2, buckets: { system_prompt: 4000 }, reported_input: 91000 },
    unmeasuredTurns: 0,
  },
}

const TOOL_ROWS = [
  { name: 'read_file', server: null, invocations: 5, schema_tokens: 700, turns_resident: 5, total_schema_cost: 3500, invoked: true, in_definitions: true, is_mcp: false },
  { name: 'write_file', server: null, invocations: 3, schema_tokens: 1200, turns_resident: 5, total_schema_cost: 6000, invoked: true, in_definitions: true, is_mcp: false },
  { name: 'mcp_git_status', server: 'git', invocations: 0, schema_tokens: 900, turns_resident: 5, total_schema_cost: 4500, invoked: false, in_definitions: true, is_mcp: true },
]

const TOOLS_BODY = {
  tools: TOOL_ROWS,
  data: TOOL_ROWS,
  total: 3,
  limit: 25,
  offset: 0,
  page: 1,
  aggregate: {
    maxCost: 6000,
    residentCostTotal: 14000,
    invocationCount: 8,
    unusedCount: 1,
    unusedRange: { tokenResidencies: 4500, floor: 0.0, ceiling: 0.45, currency: 'USD' },
    byServer: { git: 4500 },
    definitionsSpanId: 'turn-span-1',
    measurable: true,
    reason: null,
  },
}

const SERVER_ROWS = [
  { server: 'builtin', is_mcp: false, tools: 2, schema_tokens: 1900, total_schema_cost: 9500, invocations: 8, unused_tools: 0, unused_cost: 0 },
  { server: 'git', is_mcp: true, tools: 1, schema_tokens: 900, total_schema_cost: 4500, invocations: 0, unused_tools: 1, unused_cost: 4500 },
]

const SERVERS_BODY = { servers: SERVER_ROWS, data: SERVER_ROWS, total: 2, limit: 25, offset: 0, page: 1 }

const SKILL_ROW = { name: 'docs', listed: true, invocations: 2, turns_listed: [0, 1], derived_tokens: 38, description: 'Write the docs' }

const SKILLS_BODY = { measurable: true, rows: [SKILL_ROW], total: 1, limit: 25, offset: 0, page: 1 }

const KW_SKILLS_BODY = { rows: [], total: 0, limit: 25, offset: 0, page: 1 }

const TIMELINE_BODY = {
  sessionId: SESSION_ID,
  parent: null,
  nodes: [
    {
      spanId: 'root-span-1',
      parentId: null,
      startMs: 0,
      durationMs: 38400,
      kind: 'agent',
      name: 'invoke_agent',
      status: 'Ok',
      attributes: { 'agent.name': 'coder-bot' },
      isSubagent: false,
      isAuxiliary: false,
      cost: { basis: 'published', status: 'priced', value: 1.25, currency: 'USD' },
      childCount: 3,
    },
  ],
  total: 1,
  limit: 25,
  offset: 0,
  page: 1,
  bounds: { startMs: 0, endMs: 38400 },
}

const REQUEST_ROWS = [
  { request: 'span-r1', timestamp: '2026-11-19T10:00:00.000Z', turns: 3, model: 'claude-3-5-sonnet', prompt: 'Analyze the recursion bug in ast.ts' },
  { request: 'span-r2', timestamp: '2026-11-19T10:05:00.000Z', turns: 2, model: 'claude-3-5-sonnet', prompt: 'Apply the patch and verify tests pass' },
]

const REQUESTS_BODY = { requests: REQUEST_ROWS, data: REQUEST_ROWS, total: 128, limit: 25, offset: 0, page: 1 }

const REQUESTS_PAGE_2 = {
  requests: [{ request: 'span-r26', timestamp: null, turns: 1, model: null, prompt: 'Rewrite the parser entry point' }],
  data: [{ request: 'span-r26', timestamp: null, turns: 1, model: null, prompt: 'Rewrite the parser entry point' }],
  total: 128,
  limit: 25,
  offset: 25,
  page: 2,
}

const RECONCILIATION_ROWS = [
  { request: 'span-r1', root_input: 70000, sum_chat_input: 70000, input_match: true, root_output: 3200, sum_chat_output: 3200, output_match: true },
  { request: 'span-r2', root_input: 50500, sum_chat_input: 42000, input_match: false, root_output: 3200, sum_chat_output: 3200, output_match: true },
]

const RECONCILIATION_BODY = {
  reconciliation: RECONCILIATION_ROWS,
  data: RECONCILIATION_ROWS,
  total: 40,
  limit: 25,
  offset: 0,
  page: 1,
}

const PROBLEM_ROW = {
  id: 7,
  spanId: 'span-1',
  severity: 'warning',
  code: 'PARSE_SKIPPED',
  message: 'A record was skipped',
  location: null,
  sessionId: SESSION_ID,
  harness: 'copilot',
  timestamp: '2026-11-19T10:00:00.000Z',
}

const PROBLEMS_BODY = { problems: [PROBLEM_ROW], data: [PROBLEM_ROW], total: 1, limit: 25, offset: 0, page: 1 }

const STORED_FIELDS_BODY = {
  sessionKey: SESSION_ID,
  tables: [
    {
      tableName: 'session',
      columnNames: ['session_key', 'harness'],
      rowCount: 1,
      missingParentKeys: [],
    },
  ],
}

/** Answers every held route with its data body, or the override's body where one is given. */
function respondAll(
  router: ReturnType<typeof stubSectionRoutes>,
  overrides: Record<string, unknown> = {},
): void {
  router.respond(SUMMARY_ROUTE, (overrides[SUMMARY_ROUTE] as typeof SUMMARY_BODY) ?? SUMMARY_BODY)
  router.respond(CHARTS_ROUTE, (overrides[CHARTS_ROUTE] as typeof CHARTS_BODY) ?? CHARTS_BODY)
  router.respond(TOOLS_ROUTE, (overrides[TOOLS_ROUTE] as typeof TOOLS_BODY) ?? TOOLS_BODY)
  router.respond(SERVERS_ROUTE, (overrides[SERVERS_ROUTE] as typeof SERVERS_BODY) ?? SERVERS_BODY)
  router.respond(SKILLS_ROUTE, (overrides[SKILLS_ROUTE] as typeof SKILLS_BODY) ?? SKILLS_BODY)
  router.respond(KW_SKILLS_ROUTE, (overrides[KW_SKILLS_ROUTE] as typeof KW_SKILLS_BODY) ?? KW_SKILLS_BODY)
  router.respond(TIMELINE_ROUTE, (overrides[TIMELINE_ROUTE] as typeof TIMELINE_BODY) ?? TIMELINE_BODY)
  router.respond(REQUESTS_ROUTE, (overrides[REQUESTS_ROUTE] as typeof REQUESTS_BODY) ?? REQUESTS_BODY)
  router.respond(RECONCILIATION_ROUTE, (overrides[RECONCILIATION_ROUTE] as typeof RECONCILIATION_BODY) ?? RECONCILIATION_BODY)
  router.respond(PROBLEMS_ROUTE, (overrides[PROBLEMS_ROUTE] as typeof PROBLEMS_BODY) ?? PROBLEMS_BODY)
  router.respond(STORED_FIELDS_ROUTE, (overrides[STORED_FIELDS_ROUTE] as typeof STORED_FIELDS_BODY) ?? STORED_FIELDS_BODY)
}

// ---------------------------------------------------------------------------
// Opening a session
// ---------------------------------------------------------------------------

describe('AgentSessionDashboard: opening a session', () => {
  it('issues the eleven section requests in page order before any response resolves', async () => {
    const router = stubSectionRoutes()
    mountDashboard()
    await waitFor(() => expect(router.calls).toHaveLength(11))
    expect(router.calls).toEqual(PAGE_ORDER)
  })

  it('issues no content-route or stored-fields-rows request on open', async () => {
    const router = stubSectionRoutes()
    mountDashboard()
    await waitFor(() => expect(router.calls).toHaveLength(11))
    const lateRoutes = router.calls.filter(
      (url) => /\/content(\?|$)/.test(url) || /\/turn\/\d+\/content/.test(url) || /\/stored-fields\/[^/?]+/.test(url),
    )
    expect(lateRoutes).toEqual([])

    // Nothing new fires once every route answers, either: the drawer stays shut.
    respondAll(router)
    await screen.findByTestId('session-harness-badge')
    expect(router.calls).toHaveLength(11)
  })

  it('renders the header, banners and overview from /summary alone', async () => {
    const router = stubSectionRoutes()
    mountDashboard()
    router.respond(SUMMARY_ROUTE, SUMMARY_BODY)

    expect(await screen.findByTestId('session-harness-badge').then((el) => el)).toBeTruthy()
    expect(screen.getByTestId('session-harness-badge').textContent).toBe('copilot')
    expect(screen.getByTestId('session-label').textContent).toBe('Fix parser AST recursion')
    expect(screen.getByTestId('harness-notes').textContent).toContain('What copilot does not export:')
    expect(screen.getByTestId('aux-calls-banner').textContent).toContain('2 auxiliary gpt-4o-mini call(s)')
    expect(screen.getByTestId('reconciliation-ok').textContent).toContain('across 2 requests')
    const overview = screen.getByTestId('overview-strip-section').textContent ?? ''
    expect(overview).toContain('120.5K')
    expect(overview).toContain('$1.25')
    expect(overview).toContain('48 spans'.replace(' spans', ''))

    // Every other section is still held back and says so; nothing of theirs renders.
    expect(screen.getByTestId('session-charts-loading')).toBeTruthy()
    expect(screen.getByTestId('session-tools-loading')).toBeTruthy()
  })

  it('shows the summary loading state first, with the section skeletons beside it', () => {
    stubSectionRoutes()
    mountDashboard()
    expect(screen.getByTestId('session-summary-loading').getAttribute('role')).toBe('status')
    expect(screen.getByTestId('agent-session-dashboard')).toBeTruthy()
  })

  it('states the summary failure with a retry while the other sections carry on', async () => {
    const router = stubSectionRoutes()
    mountDashboard()
    router.fail(SUMMARY_ROUTE)
    const error = await screen.findByTestId('session-summary-error')
    expect(error.textContent).toContain('Request failed (500)')

    // The sections do not wait on the summary.
    router.respond(CHARTS_ROUTE, CHARTS_BODY)
    expect(await screen.findByTestId('turn-bar-0')).toBeTruthy()

    fireEvent.click(within(error).getByRole('button', { name: 'Retry' }))
    await waitFor(() => expect(router.calls.filter((url) => url === SUMMARY_ROUTE).length).toBe(2))
  })
})

// ---------------------------------------------------------------------------
// One section at a time
// ---------------------------------------------------------------------------

describe('AgentSessionDashboard: each section stands alone', () => {
  const SECTION_SURFACES: Array<[string, unknown, string]> = [
    [CHARTS_ROUTE, CHARTS_BODY, 'turn-bar-0'],
    [TOOLS_ROUTE, TOOLS_BODY, 'tool-row-read_file'],
    [SERVERS_ROUTE, SERVERS_BODY, 'server-row-builtin'],
    [SKILLS_ROUTE, SKILLS_BODY, 'skill-row-docs'],
    [TIMELINE_ROUTE, TIMELINE_BODY, 'timeline-node'],
    [REQUESTS_ROUTE, REQUESTS_BODY, 'request-row-1'],
    [RECONCILIATION_ROUTE, RECONCILIATION_BODY, 'reconciliation-row-1'],
    [PROBLEMS_ROUTE, PROBLEMS_BODY, 'problem-row-7'],
    [STORED_FIELDS_ROUTE, STORED_FIELDS_BODY, 'stored-fields-table-session'],
  ]

  it.each(SECTION_SURFACES)(
    'renders its own data from %s while every other route is held back',
    async (route, body, surface) => {
      const router = stubSectionRoutes()
      mountDashboard()
      router.respond(route, body)
      expect(await screen.findByTestId(surface)).toBeTruthy()
    },
  )

  it('renders one section after another fails, and states the failure in place', async () => {
    const router = stubSectionRoutes()
    mountDashboard()
    router.respond(SUMMARY_ROUTE, SUMMARY_BODY)
    router.fail(CHARTS_ROUTE)
    const chartsError = await screen.findByTestId('section-error-charts')
    expect(chartsError.textContent).toContain('Request failed (500)')
    expect(screen.getByTestId('session-harness-badge').textContent).toBe('copilot')

    router.respond(TOOLS_ROUTE, TOOLS_BODY)
    expect(await screen.findByTestId('tool-row-read_file')).toBeTruthy()
    expect(screen.getByTestId('section-error-charts')).toBeTruthy()
  })
})

// ---------------------------------------------------------------------------
// Paged lists through the real tree
// ---------------------------------------------------------------------------

describe('AgentSessionDashboard: paged lists', () => {
  it('shows the requests pager past 25 and requests page two only on press', async () => {
    const router = stubSectionRoutes()
    mountDashboard()
    router.respond(REQUESTS_ROUTE, REQUESTS_BODY)
    await screen.findByTestId('user-requests')
    const pager = screen.getByTestId('list-pager')

    fireEvent.click(within(pager).getByTestId('pager-next'))
    await waitFor(() =>
      expect(router.calls).toContain(`/api/kyber/session/${SESSION_ID}/requests?page=2&limit=25&order=desc`),
    )
    router.respond(`/api/kyber/session/${SESSION_ID}/requests?page=2&limit=25&order=desc`, REQUESTS_PAGE_2)
    expect(await screen.findByTestId('request-row-26')).toBeTruthy()
  })

  it('shows the reconciliation pager past 25', async () => {
    const router = stubSectionRoutes()
    mountDashboard()
    router.respond(RECONCILIATION_ROUTE, RECONCILIATION_BODY)
    await screen.findByTestId('reconciliation-section')
    expect(screen.getByTestId('list-pager')).toBeTruthy()
  })
})

// ---------------------------------------------------------------------------
// The inspector drawer, wired through the real tree
// ---------------------------------------------------------------------------

describe('AgentSessionDashboard: inspector drawer', () => {
  it('opens the definitions request on the aggregate span when a tool row is clicked', async () => {
    const router = stubSectionRoutes()
    mountDashboard()
    respondAll(router)
    await screen.findByTestId('tool-row-read_file')

    fireEvent.click(screen.getByTestId('tool-row-read_file'))
    expect((await screen.findByTestId('drawer-title')).textContent).toBe('Tool: read_file')
    await waitFor(() =>
      expect(router.calls).toContain(
        `/api/kyber/session/${SESSION_ID}/content?span=turn-span-1&part=tool_definitions&full=true`,
      ),
    )
  })

  it('opens the whole-turn lossless request when a turn band is clicked', async () => {
    const router = stubSectionRoutes()
    mountDashboard()
    respondAll(router)
    await screen.findByTestId('turn-bar-1')

    fireEvent.click(screen.getByTestId('turn-bar-1'))
    expect((await screen.findByTestId('drawer-title')).textContent).toBe('Turn 2 · Full context')
    // D9: the drawer resolves the turn by key first, and requests its content
    // only once that row answers.
    await waitFor(() => expect(router.calls).toContain(`/api/kyber/session/${SESSION_ID}/turn/1`))
    expect(router.calls.filter((url) => url.includes('/turn/1/content'))).toEqual([])

    router.respond(`/api/kyber/session/${SESSION_ID}/turn/1`, {
      sessionId: SESSION_ID,
      turnIndex: 1,
      turn: { index: 1, spanId: 'turn-span-2', fresh: 5000, cache_read: 42000 },
      context: null,
    })
    await waitFor(() =>
      expect(router.calls).toContain(`/api/kyber/session/${SESSION_ID}/turn/1/content?full=true`),
    )
    router.respond(`/api/kyber/session/${SESSION_ID}/turn/1/content?full=true`, {
      sessionId: SESSION_ID,
      turnIndex: 1,
      blocks: [],
      parts: [],
      assembledText: '',
    })
    expect(await screen.findByTestId('turn-content-full')).toBeTruthy()
    // The measured totals come from the fetched row.
    expect(screen.getByTestId('turn-total-cache-read').textContent).toContain('42.0K')
  })

  it('opens the span inspector for a clicked timeline node', async () => {
    const router = stubSectionRoutes()
    mountDashboard()
    respondAll(router)
    await screen.findByTestId('timeline-node')

    fireEvent.click(screen.getByTestId('timeline-node'))
    expect((await screen.findByTestId('drawer-title')).textContent).toBe('Span: invoke_agent')
    await waitFor(() =>
      expect(router.calls).toContain(
        `/api/kyber/session/${SESSION_ID}/content?span=root-span-1&full=true`,
      ),
    )
    // The loaded node still names some attributes inline, but the attributes
    // route answers for it too: the span-scoped read carries keys the
    // content-free timeline omits (the consent-allowed prompt text among
    // them), so the inspector fetches the node it opens.
    expect(router.calls).toContain('/api/kyber/span/root-span-1/attributes')
  })

  it('fetches the span attributes for a node that also carries some inline, and renders the fetched keys', async () => {
    const router = stubSectionRoutes()
    mountDashboard()
    // A content-free timeline node still names a few typed attributes inline
    // (the model the span declared, say), but the consent-allowed prompt text
    // the engine attaches only on the span-scoped read lives behind the
    // attributes route. The inspector fetches the node it opens even when the
    // node carries something inline; the inline map stays on screen until the
    // fetch answers, and the fetched map — a superset — replaces it.
    respondAll(router)
    await screen.findByTestId('timeline-node')

    fireEvent.click(screen.getByTestId('timeline-node'))
    expect((await screen.findByTestId('drawer-title')).textContent).toBe('Span: invoke_agent')
    await waitFor(() => expect(router.calls).toContain('/api/kyber/span/root-span-1/attributes'))
    // The fetch is still in flight: the inline map renders meanwhile.
    expect(screen.getByTestId('span-inspector').textContent).toContain('coder-bot')

    router.respond('/api/kyber/span/root-span-1/attributes', {
      spanId: 'root-span-1',
      attributes: {
        'agent.name': 'coder-bot',
        'factory.turn.prompt.text': 'the consent-allowed prompt',
      },
    })
    expect(
      await screen.findByTestId('span-inspector').then((el) => el.textContent),
    ).toContain('the consent-allowed prompt')
  })

  it('fetches a span node attributes from the attributes route when the node carries none', async () => {
    const router = stubSectionRoutes()
    mountDashboard()
    // The served node may carry no inline attribute map — the raw payload is
    // the span's attributes, and the inspector fetches the node it shows.
    respondAll(router, {
      [TIMELINE_ROUTE]: { ...TIMELINE_BODY, nodes: [{ ...TIMELINE_BODY.nodes[0], attributes: {} }] },
    })
    await screen.findByTestId('timeline-node')

    fireEvent.click(screen.getByTestId('timeline-node'))
    expect((await screen.findByTestId('drawer-title')).textContent).toBe('Span: invoke_agent')
    await waitFor(() => expect(router.calls).toContain('/api/kyber/span/root-span-1/attributes'))
    router.respond('/api/kyber/span/root-span-1/attributes', {
      spanId: 'root-span-1',
      attributes: { 'agent.name': 'coder-bot' },
    })
    expect(await screen.findByTestId('span-inspector').then((el) => el.textContent)).toContain('coder-bot')
  })

  it('opens the first listed turn for a clicked skill row', async () => {
    const router = stubSectionRoutes()
    mountDashboard()
    respondAll(router)
    await screen.findByTestId('skill-row-docs')

    fireEvent.click(screen.getByTestId('skill-row-docs'))
    expect((await screen.findByTestId('drawer-title')).textContent).toBe('Skill: docs')
    await waitFor(() =>
      expect(router.calls).toContain(`/api/kyber/session/${SESSION_ID}/turn/0/content?full=true`),
    )
  })

  it('opens a bucket drill-down from the fetched turn context when a context segment is clicked', async () => {
    const router = stubSectionRoutes()
    mountDashboard()
    respondAll(router)
    await screen.findByTestId('context-composition-chart')

    fireEvent.click(screen.getByTestId('context-segment-tool_definitions'))
    expect((await screen.findByTestId('drawer-title')).textContent).toBe('Turn 1 · tool_definitions')
    // D9: the drill-down carries the key pair; the drawer fetches the turn and
    // reads that turn's own context — no content request before it answers.
    await waitFor(() => expect(router.calls).toContain(`/api/kyber/session/${SESSION_ID}/turn/0`))
    expect(router.calls.filter((url) => url.includes('/content'))).toEqual([])

    // The per-turn route answers with figures of its own, distinct from the
    // session-wide composition the charts rendered.
    router.respond(`/api/kyber/session/${SESSION_ID}/turn/0`, {
      sessionId: SESSION_ID,
      turnIndex: 0,
      turn: { index: 0, spanId: 'turn-span-1' },
      context: { index: 1, buckets: { tool_definitions: 321, residual: 44 }, reported_input: 1234 },
    })
    const inspector = await screen.findByTestId('context-bucket-inspector')
    expect(inspector.textContent).toContain('321')
    expect(inspector.textContent).toContain('1.2K')
    await waitFor(() =>
      expect(router.calls).toContain(
        `/api/kyber/session/${SESSION_ID}/content?span=turn-span-1&part=tool_definitions&full=true`,
      ),
    )
    router.respond(`/api/kyber/session/${SESSION_ID}/content?span=turn-span-1&part=tool_definitions&full=true`, {
      sessionId: SESSION_ID,
      spanId: 'turn-span-1',
      parts: [{ spanId: 'turn-span-1', part: 'tool_definitions', text: 'BUCKET_DEFINITIONS_FULL_TEXT' }],
    })
    expect(await screen.findByText('BUCKET_DEFINITIONS_FULL_TEXT')).toBeTruthy()
  })

  it('states the harness notice and issues no content request when no definitions exist', async () => {
    const router = stubSectionRoutes()
    const noDefinitions = {
      ...SUMMARY_BODY,
      harness: 'antigravity-cli',
      summary: { ...SUMMARY_BODY.summary, tools_offered: undefined },
    }
    const bareTools = {
      ...TOOLS_BODY,
      tools: [{ name: 'read_file', server: null, invocations: 4, invoked: true, in_definitions: false, is_mcp: false }],
      data: [{ name: 'read_file', server: null, invocations: 4, invoked: true, in_definitions: false, is_mcp: false }],
      aggregate: { ...TOOLS_BODY.aggregate, byServer: {}, unusedRange: null, definitionsSpanId: null },
    }
    mountDashboard()
    router.respond(SUMMARY_ROUTE, noDefinitions)
    router.respond(TOOLS_ROUTE, bareTools)
    await screen.findByTestId('tool-row-read_file')

    fireEvent.click(screen.getByTestId('tool-row-read_file'))
    const notice = await screen.findByTestId('tool-definition-unavailable')
    expect(notice.textContent).toContain(
      'antigravity-cli does not export tool definitions for this session',
    )
    expect(router.calls.filter((url) => /\/content(\?|$)/.test(url))).toEqual([])
  })
})

// ---------------------------------------------------------------------------
// Page order and prop-driven states
// ---------------------------------------------------------------------------

describe('AgentSessionDashboard: layout order', () => {
  it('renders the sections in page order', async () => {
    const router = stubSectionRoutes()
    const { container } = mountDashboard()
    respondAll(router)
    await screen.findByTestId('stored-fields-table-session')

    const html = container.innerHTML
    const order = [
      'data-testid="overview-strip-section"',
      'data-testid="spend-composition-section"',
      'data-testid="tool-schema-section"',
      'data-testid="servers-section"',
      'data-testid="skills-section"',
      'data-testid="execution-timeline-section"',
      'data-testid="session-cost-panel"',
      'data-testid="user-requests"',
      'data-testid="reconciliation-section"',
      'data-testid="problems-section"',
      'data-testid="stored-fields-section"',
    ]
    let previous = -1
    for (const marker of order) {
      const index = html.indexOf(marker)
      expect(index, `${marker} is rendered`).toBeGreaterThan(-1)
      expect(index, `${marker} comes after the section before it`).toBeGreaterThan(previous)
      previous = index
    }
  })
})

describe('AgentSessionDashboard: a charts response with no per-turn counters', () => {
  it('renders the turn not-measurable state and no turn bars', async () => {
    const router = stubSectionRoutes()
    mountDashboard()
    respondAll(router, {
      [CHARTS_ROUTE]: {
        ...CHARTS_BODY,
        turns: [
          { index: 0, spanId: 'turn-span-1', model: 'droid', durationMs: 4200 },
          { index: 1, spanId: 'turn-span-2', model: 'droid', durationMs: 5100 },
        ],
      },
    })
    const state = await screen.findByTestId('turn-spend-not-measurable')
    expect(state.textContent).toContain('This harness does not report token counts per turn.')
    expect(screen.queryByTestId('turn-bar-0')).toBeNull()
    expect(screen.queryByTestId('turn-bar-1')).toBeNull()
  })
})

describe('AgentSessionDashboard: prop-driven states', () => {
  it('renders the error state from the error prop', () => {
    const element = (
      <AgentSessionDashboard error={new Error('Network connection failed')} />
    ) as ReactElement
    const { container } = render(element)
    expect(container.textContent).toContain('Failed to load session')
    expect(container.textContent).toContain('Network connection failed')
    expect(container.querySelector('[data-testid="agent-dashboard-error"]')).toBeTruthy()
  })

  it('renders the loading skeleton from the isLoading prop', () => {
    const element = <AgentSessionDashboard isLoading={true} /> as ReactElement
    const { container } = render(element)
    expect(container.querySelector('[data-testid="agent-dashboard-loading"]')).toBeTruthy()
  })

  it('renders the empty state when no session is selected', () => {
    const element = (
      <QueryClientProvider client={queryClient()}>
        <AgentSessionDashboard />
      </QueryClientProvider>
    ) as ReactElement
    const { container } = render(element)
    expect(container.querySelector('[data-testid="agent-dashboard-empty"]')).toBeTruthy()
    expect(container.textContent).toContain('No agent session selected')
  })
})

// ---------------------------------------------------------------------------
// The client is the only network caller
// ---------------------------------------------------------------------------

describe('the session view never calls fetch itself', () => {
  const sessionDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), 'session')

  function productionFiles(): string[] {
    const files = readdirSync(sessionDir, { withFileTypes: true })
      .filter((entry) => /\.(ts|tsx)$/.test(entry.name) && !/\.test\.(ts|tsx)$/.test(entry.name))
      .map((entry) => path.join(sessionDir, entry.name))
    return [path.resolve(path.dirname(fileURLToPath(import.meta.url)), 'AgentSessionDashboard.tsx'), ...files]
  }

  it('holds no raw fetch call outside lib/kyberApi.ts', () => {
    for (const file of productionFiles()) {
      expect(readFileSync(file, 'utf8'), `${file} must not call fetch directly`).not.toMatch(/\bfetch\s*\(/)
    }
  })

  it('reads the summary through the client fetcher, not a raw payload route', async () => {
    const fetchMock = vi.fn(async (_url: string) => jsonResponse(SUMMARY_BODY))
    vi.stubGlobal('fetch', fetchMock)
    const element = (
      <QueryClientProvider client={queryClient()}>
        <AgentSessionDashboard sessionId={SESSION_ID} />
      </QueryClientProvider>
    ) as ReactElement
    render(element)
    await waitFor(() => expect(fetchMock).toHaveBeenCalled())
    expect(fetchMock.mock.calls[0][0]).toBe(SUMMARY_ROUTE)
    expect(await fetchKyberSessionSummary(SESSION_ID)).toMatchObject({ session_id: SESSION_ID, harness: 'copilot' })
  })
})

// ---------------------------------------------------------------------------
// Shared lookups the drawer still uses (issue #184)
// ---------------------------------------------------------------------------

describe('findTurnByTransport (shared drawer lookup)', () => {
  it('matches a 0-based index row and the positional fallback', () => {
    expect(findTurnByTransport([{ index: 4 }], 4)).toEqual({ index: 4 })
    expect(findTurnByTransport([{}, {}], 1)).toEqual({})
    expect(findTurnByTransport([{ index: 3 }], 4)).toBeUndefined()
  })

  it('matches a legacy 1-based turn row via turn - 1 only', () => {
    expect(findTurnByTransport([{ turn: 5 }], 4)).toEqual({ turn: 5 })
    expect(findTurnByTransport([{ turn: 5 }], 5)).toBeUndefined()
  })

  it('prefers explicit identity over an earlier positional match', () => {
    const rows = [{ turn: 2, spanId: 'span-a' }, { turn: 1, spanId: 'span-b' }]
    expect(findTurnByTransport(rows, 0)).toEqual({ turn: 1, spanId: 'span-b' })
    expect(findTurnByTransport(rows, 1)).toEqual({ turn: 2, spanId: 'span-a' })
  })

  it('never serves a neighboring identified row positionally', () => {
    expect(findTurnByTransport([{ index: 7 }, { index: 8 }], 0)).toBeUndefined()
  })
})

describe('findContextTurn (shared 1-based context lookup)', () => {
  it('matches engine 1-based index and legacy turn, then position', () => {
    expect(findContextTurn([{ index: 2 }], 2)).toEqual({ index: 2 })
    expect(findContextTurn([{ turn: 2 }], 2)).toEqual({ turn: 2 })
    expect(findContextTurn([{}, {}], 2)).toEqual({})
  })

  it('prefers explicit identity over an earlier positional match', () => {
    const rows = [{ turn: 2 }, { turn: 1 }]
    expect(findContextTurn(rows, 1)).toEqual({ turn: 1 })
    expect(findContextTurn(rows, 2)).toEqual({ turn: 2 })
  })
})

describe('formatDuration', () => {
  it('formats durations properly', () => {
    expect(formatDuration(null)).toBe('—')
    expect(formatDuration(undefined)).toBe('—')
    expect(formatDuration(0)).toBe('—')
    expect(formatDuration(450)).toBe('450ms')
    expect(formatDuration(2500)).toBe('2.5s')
    expect(formatDuration(65000)).toBe('1m 5s')
  })
})
