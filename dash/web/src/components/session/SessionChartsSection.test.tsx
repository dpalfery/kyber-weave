// @vitest-environment happy-dom
// The charts section: the session's turns and context composition, read from
// the bounded charts route and rendered through the existing spend charts. A
// turn band opens the whole-turn inspector; a context bucket opens its bucket
// drill-down. Both content requests are the section's own to compute.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactElement } from 'react'

import { SessionChartsSection } from './SessionChartsSection.js'
import type { OpenInspectorRequest } from './types.js'

const SESSION_ID = 'sess-abc-123'

function queryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
}

function jsonResponse(body: unknown, status = 200): Response {
  return { ok: status >= 200 && status < 300, status, json: async () => body } as unknown as Response
}

function stubFetch(handler: (url: string) => Response): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn((input: unknown) => Promise.resolve(handler(String(input))))
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

function mount(openInspector: (request: OpenInspectorRequest) => void): ReturnType<typeof render> {
  const element = (
    <QueryClientProvider client={queryClient()}>
      <SessionChartsSection sessionId={SESSION_ID} openInspector={openInspector} />
    </QueryClientProvider>
  ) as ReactElement
  return render(element)
}

afterEach(() => {
  cleanup()
  window.sessionStorage.clear()
  vi.unstubAllGlobals()
})

const CHARTS_BODY = {
  sessionId: SESSION_ID,
  total: 2,
  limit: 25,
  offset: 0,
  page: 1,
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
      has_tool_defs: true,
      content: {
        tool_definitions: [{ name: 'read_file', description: 'Read file contents', parameters: { path: { type: 'string' } } }],
      },
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
        buckets: {
          system_prompt: 4000,
          tool_definitions: 2800,
          instruction_context: 7200,
          conversation_history: 12000,
          tool_result_content: 18000,
          residual: 1500,
        },
      },
    ],
    first: { turn: 1, buckets: { system_prompt: 4000 }, reported_input: 45500 },
    last: { turn: 2, buckets: { system_prompt: 4000 }, reported_input: 91000 },
    unmeasuredTurns: 0,
  },
}

describe('SessionChartsSection states', () => {
  it('shows its loading state while the section is in flight', () => {
    stubFetch(() => jsonResponse(CHARTS_BODY))
    mount(() => {})
    const loading = screen.getByTestId('session-charts-loading')
    expect(loading.getAttribute('role')).toBe('status')
    expect(loading.textContent).toMatch(/loading/i)
  })

  it('states the failure plainly and retries on press', async () => {
    const fetchMock = stubFetch(() => jsonResponse({}, 500))
    mount(() => {})
    const error = await screen.findByTestId('section-error-charts')
    expect(error.textContent).toContain('Request failed (500)')
    fireEvent.click(within(error).getByRole('button', { name: 'Retry' }))
    expect(fetchMock.mock.calls.length).toBeGreaterThanOrEqual(2)
  })

  it('states a faulted projection as the section error, never as charts', async () => {
    stubFetch(() => jsonResponse({ ...CHARTS_BODY, projection_status: 'PROJECTION_FAILED' }))
    mount(() => {})
    const error = await screen.findByTestId('section-error-charts')
    expect(error.textContent).toContain('PROJECTION_FAILED')
    expect(screen.queryByTestId('spend-composition-section')).toBeNull()
  })

  it('renders the spend charts from the section turns and context', async () => {
    stubFetch(() => jsonResponse(CHARTS_BODY))
    mount(() => {})
    const section = await screen.findByTestId('spend-composition-section')
    expect(within(section).getByTestId('session-spend-charts')).toBeTruthy()
    expect(within(section).getByTestId('turn-spend-chart')).toBeTruthy()
    expect(within(section).getByTestId('turn-bar-0')).toBeTruthy()
    expect(within(section).getByTestId('turn-bar-1')).toBeTruthy()
    expect(within(section).getByTestId('context-composition-chart')).toBeTruthy()
  })

  it('renders the honest not-measurable context banner when the engine sent one', async () => {
    const reason = 'The harness records tool invocations, not a measurable context composition.'
    stubFetch(() =>
      jsonResponse({ ...CHARTS_BODY, context: { measurable: false, reason } }),
    )
    mount(() => {})
    const section = await screen.findByTestId('spend-composition-section')
    const banner = within(section).getByTestId('context-composition-not-measurable')
    expect(banner.textContent).toContain(reason)
  })

  it('renders the not-measurable turn state, with no turn bars, for a charts response with no token fields', async () => {
    stubFetch(() =>
      jsonResponse({
        ...CHARTS_BODY,
        turns: [
          { index: 0, spanId: 'turn-span-1', model: 'droid', durationMs: 4200 },
          { index: 1, spanId: 'turn-span-2', model: 'droid', durationMs: 5100 },
        ],
      }),
    )
    mount(() => {})
    const section = await screen.findByTestId('spend-composition-section')
    expect(within(section).getByTestId('turn-spend-not-measurable')).toBeTruthy()
    expect(within(section).queryByTestId('turn-bar-0')).toBeNull()
    expect(within(section).queryByTestId('turn-bar-1')).toBeNull()
  })

  it('shows limit and order controls on a single non-empty page in both Stacked Bars and Heatmap Matrix and hides page buttons', async () => {
    stubFetch(() => jsonResponse(CHARTS_BODY))
    mount(() => {})
    const section = await screen.findByTestId('spend-composition-section')
    const pager = within(section).getByTestId('list-pager')
    expect(within(pager).getByTestId('pager-limit')).toBeTruthy()
    expect(within(pager).getByTestId('pager-order').textContent).toBe('Newest first')
    expect(within(pager).queryByTestId('pager-prev')).toBeNull()
    expect(within(pager).queryByTestId('pager-next')).toBeNull()

    fireEvent.click(within(section).getByTestId('tab-heatmap'))
    const heatmap = within(section).getByTestId('context-heatmap')
    expect(heatmap).toBeTruthy()
    const heatmapPager = within(section).getByTestId('list-pager')
    expect(within(heatmapPager).getByTestId('pager-limit')).toBeTruthy()
    expect(within(heatmapPager).getByTestId('pager-order').textContent).toBe('Newest first')
  })

  it('hides the pager when total is 0 and context is empty', async () => {
    stubFetch(() =>
      jsonResponse({
        sessionId: SESSION_ID,
        total: 0,
        limit: 25,
        offset: 0,
        page: 1,
        turns: [],
        context: {
          measurable: true,
          contextLimit: 200000,
          turns: [],
          residualTotal: 0,
          derivedCounts: false,
          freshJumpFactor: 1.25,
          flaggedTurns: [],
          sessionAccumulationRate: 0,
          unmeasuredTurns: 0,
        },
      }),
    )
    mount(() => {})
    const section = await screen.findByTestId('spend-composition-section')
    expect(within(section).getByTestId('context-composition-empty')).toBeTruthy()
    expect(within(section).queryByTestId('list-pager')).toBeNull()
  })
})

describe('SessionChartsSection paging and sorting', () => {
  function pagedChartsResponse(url: string) {
    const parsed = new URL(url, 'http://localhost')
    const page = Number(parsed.searchParams.get('page') ?? '1')
    const limit = Number(parsed.searchParams.get('limit') ?? '25')
    const order = parsed.searchParams.get('order') ?? 'desc'
    const turnNumber = page === 1 ? (order === 'asc' ? 1 : 26) : 2
    return {
      sessionId: SESSION_ID,
      total: 120,
      limit,
      offset: (page - 1) * limit,
      page,
      order,
      turns: [
        {
          index: turnNumber - 1,
          spanId: `turn-span-${turnNumber}`,
          model: 'claude-3-5-sonnet',
          durationMs: 4200,
          fresh: 27500,
          cache_read: 0,
          cache_creation: 18000,
          output: 1200,
        },
      ],
      context: {
        measurable: true,
        contextLimit: 200000,
        derivedCounts: true,
        tokenizer: 'o200k_base',
        tokenizers: [{ name: 'o200k_base', isModelOwn: false }],
        tokenizerIsModelOwn: false,
        measuredInputTotal: 100000,
        residualTotal: 40000,
        maxBucketTokens: 50000,
        observedBuckets: ['system_prompt', 'conversation_history', 'tool_definitions'],
        observedServers: ['context7'],
        missingBuckets: ['system_prompt', 'tool_definitions'],
        residualReasons: ['Claude Code transcripts omit the runtime system prompt and tool schemas.'],
        turns: [
          {
            turn: turnNumber,
            reported_input: 50000,
            buckets: page === 1 ? { conversation_history: 10000 } : { system_prompt: 4000 },
            residual: page === 1 ? 40000 : 1000,
          },
        ],
        first: { turn: 1, buckets: { conversation_history: 10000 }, reported_input: 50000 },
        last: { turn: 2, buckets: { conversation_history: 20000 }, reported_input: 50000 },
        unmeasuredTurns: 0,
      },
    }
  }

  it('pages and sorts Context Composition in Stacked Bars and Heatmap Matrix views, resetting to page 1 on limit/order changes', async () => {
    const fetchMock = stubFetch((url) => jsonResponse(pagedChartsResponse(url)))
    mount(() => {})
    let section = await screen.findByTestId('spend-composition-section')
    expect(within(section).getByTestId('context-stacked-bars')).toBeTruthy()
    expect(within(section).getByTestId('context-turn-26')).toBeTruthy()
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/charts?page=1&limit=25&order=desc'))).toBe(true)

    // Page change in Stacked Bars
    fireEvent.click(within(section).getByTestId('pager-next'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/charts?page=2&limit=25&order=desc'))).toBe(true),
    )
    await within(section).findByTestId('context-turn-2')

    // Page-size changes (25 -> 50 -> 100) reset to page 1 and persist
    fireEvent.change(within(section).getByTestId('pager-limit'), { target: { value: '50' } })
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/charts?page=1&limit=50&order=desc'))).toBe(true),
    )
    await screen.findByTestId('pager-limit')
    section = screen.getByTestId('spend-composition-section')
    expect((within(section).getByTestId('pager-limit') as HTMLSelectElement).value).toBe('50')
    expect(window.sessionStorage.getItem('kyberdash:session-section:limit:charts')).toBe('50')

    fireEvent.change(within(section).getByTestId('pager-limit'), { target: { value: '100' } })
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/charts?page=1&limit=100&order=desc'))).toBe(true),
    )
    await screen.findByTestId('pager-limit')
    section = screen.getByTestId('spend-composition-section')
    expect((within(section).getByTestId('pager-limit') as HTMLSelectElement).value).toBe('100')
    expect(window.sessionStorage.getItem('kyberdash:session-section:limit:charts')).toBe('100')

    // Switch to Heatmap Matrix and verify paging + order toggle reset to page 1
    fireEvent.click(within(section).getByTestId('tab-heatmap'))
    await within(section).findByTestId('context-heatmap')

    fireEvent.click(within(section).getByTestId('pager-next'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/charts?page=2&limit=100&order=desc'))).toBe(true),
    )
    await waitFor(() =>
      expect((within(section).getByTestId('pager-order') as HTMLButtonElement).disabled).toBe(false),
    )

    fireEvent.click(within(section).getByTestId('pager-order'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/charts?page=1&limit=100&order=asc'))).toBe(true),
    )
    await screen.findByTestId('pager-order')
    section = screen.getByTestId('spend-composition-section')
    expect(within(section).getByTestId('pager-order').textContent).toBe('Oldest first')
    // The scope-change loading state remounts the chart; select the heatmap
    // again on the freshly fetched page before checking its rendered data.
    fireEvent.click(within(section).getByTestId('tab-heatmap'))
    await within(section).findByTestId('context-heatmap')
    expect(within(section).getByTestId('context-heatmap').textContent).toContain('Turn #1')
  })

  it('preserves whole-session legend, residual %, and honest omission copy across pages without heavyDrift warning', async () => {
    stubFetch((url) => jsonResponse(pagedChartsResponse(url)))
    mount(() => {})
    const section = await screen.findByTestId('spend-composition-section')

    // Page 1: whole-session legend includes mcp:context7 even though page 1 row has no servers
    expect(within(section).getByTestId('context-legend-mcp:context7')).toBeTruthy()
    expect(within(section).getByTestId('context-legend-system_prompt')).toBeTruthy()
    expect(within(section).getByTestId('context-caveat').textContent).toContain('40.0%')
    expect(within(section).queryByTestId('context-caveat-no-servers')).toBeNull()
    expect(within(section).getByTestId('context-caveat').textContent).not.toContain('proxy is a poor fit')
    expect(within(section).getByTestId('context-caveat').textContent).toContain('system prompt')
    expect(within(section).getByTestId('context-composition-footer').textContent).toContain('tool schemas')

    // Page 2: residual % and legend remain invariant
    fireEvent.click(within(section).getByTestId('pager-next'))
    await within(section).findByTestId('context-turn-2')
    expect(within(section).getByTestId('context-legend-mcp:context7')).toBeTruthy()
    expect(within(section).getByTestId('context-caveat').textContent).toContain('40.0%')
  })
})

describe('SessionChartsSection inspector wiring', () => {
  it('opens the whole-turn lossless request when a turn band is clicked', async () => {
    stubFetch(() => jsonResponse(CHARTS_BODY))
    const openInspector = vi.fn()
    mount(openInspector)
    const section = await screen.findByTestId('spend-composition-section')
    fireEvent.click(within(section).getByTestId('turn-bar-0'))
    expect(openInspector).toHaveBeenCalledTimes(1)
    const request = openInspector.mock.calls[0][0] as OpenInspectorRequest
    expect(request.title).toBe('Turn 1 · Full context')
    expect(request.contentRequest).toEqual({ sessionId: SESSION_ID, turnIndex: 0, full: true })
    // D9: the opener passes the turn's key only — no row rides the payload.
    expect(request.content).toEqual({ turnIndex: 0 })
  })

  it('opens the keyed bucket request when a context segment is clicked', async () => {
    stubFetch(() => jsonResponse(CHARTS_BODY))
    const openInspector = vi.fn()
    mount(openInspector)
    const section = await screen.findByTestId('spend-composition-section')
    fireEvent.click(within(section).getByTestId('context-segment-tool_definitions'))
    expect(openInspector).toHaveBeenCalledTimes(1)
    const request = openInspector.mock.calls[0][0] as OpenInspectorRequest
    expect(request.title).toBe('Turn 1 · tool_definitions')
    // D9: the drill-down carries the key pair; the drawer fetches the turn
    // (widened with its context) and derives the span-scoped content request.
    expect(request.contentRequest).toEqual({
      sessionId: SESSION_ID,
      turnIndex: 0,
      bucket: 'tool_definitions',
    })
    expect(request.content).toEqual({ turnIndex: 0, bucket: 'tool_definitions' })
  })

  it('keys the system-prompt bucket to its turn, for the drawer to keep off any span', async () => {
    stubFetch(() => jsonResponse(CHARTS_BODY))
    const openInspector = vi.fn()
    mount(openInspector)
    const section = await screen.findByTestId('spend-composition-section')
    fireEvent.click(within(section).getByTestId('context-segment-system_prompt'))
    const request = openInspector.mock.calls[0][0] as OpenInspectorRequest
    expect(request.title).toBe('Turn 1 · system_prompt')
    expect(request.contentRequest).toEqual({ sessionId: SESSION_ID, turnIndex: 0, bucket: 'system_prompt' })
  })
})
