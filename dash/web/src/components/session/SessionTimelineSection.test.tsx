// @vitest-environment happy-dom
// The timeline section: the session's top-level spans, one page at a time,
// rendered through the existing call-tree and duration-bars views. T10 pins
// the top level; deeper levels page on expand in T11–T13.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactElement } from 'react'

import { SessionTimelineSection, TIMELINE_OP_COLORS } from './SessionTimelineSection.js'
import type { OpenInspectorRequest } from './types.js'
import type { KyberSessionSummarySection } from '../../lib/kyberApi.js'

const SESSION_ID = 'sess-abc-123'

function queryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
}

function jsonResponse(body: unknown, status = 200): Response {
  return { ok: status >= 200 && status < 300, status, json: async () => body } as unknown as Response
}

function stubFetch(handler: () => Response): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(() => Promise.resolve(handler()))
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

const SUMMARY: KyberSessionSummarySection = {
  session_id: SESSION_ID,
  harness: 'copilot',
  label: 'Fix parser AST recursion',
  is_subagent: false,
  span_count: 48,
  problems: 0,
  problems_count: 0,
  request_count: 2,
  costBasisMismatch: null,
  summary: { cost: { basis: 'published', status: 'priced', value: 1.25, currency: 'USD' } },
  notes: [],
  reconciliation: { total: 2, mismatched: 0 },
  tools_offered_count: 3,
  tools_invoked_count: 2,
  unused_tools_count: 1,
  skills_count: 2,
}

function mount(
  openInspector: (request: OpenInspectorRequest) => void,
  overrides: { initialTab?: 'tree' | 'bars'; summary?: KyberSessionSummarySection | null } = {},
): ReturnType<typeof render> {
  const element = (
    <QueryClientProvider client={queryClient()}>
      <SessionTimelineSection
        sessionId={SESSION_ID}
        summary={overrides.summary === undefined ? SUMMARY : overrides.summary}
        summaryRecord={SUMMARY.summary as never}
        initialTab={overrides.initialTab}
        openInspector={openInspector}
      />
    </QueryClientProvider>
  ) as ReactElement
  return render(element)
}

afterEach(() => {
  cleanup()
  window.sessionStorage.clear()
  vi.unstubAllGlobals()
})

const ROOT_NODE = {
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
}

function timelinePage(nodes: unknown[], endMs: number) {
  return {
    sessionId: SESSION_ID,
    parent: null,
    nodes,
    total: nodes.length,
    limit: 25,
    offset: 0,
    page: 1,
    bounds: { startMs: 0, endMs },
  }
}

describe('SessionTimelineSection states', () => {
  it('shows its loading state while the level is in flight', () => {
    stubFetch(() => jsonResponse(timelinePage([ROOT_NODE], 38400)))
    mount(() => {})
    const loading = screen.getByTestId('session-timeline-loading')
    expect(loading.getAttribute('role')).toBe('status')
    expect(loading.textContent).toMatch(/loading/i)
  })

  it('states the failure plainly and retries on press', async () => {
    const fetchMock = stubFetch(() => jsonResponse({}, 500))
    mount(() => {})
    const error = await screen.findByTestId('section-error-timeline')
    expect(error.textContent).toContain('Request failed (500)')
    fireEvent.click(within(error).getByRole('button', { name: 'Retry' }))
    expect(fetchMock.mock.calls.length).toBeGreaterThanOrEqual(2)
  })

  it('states a faulted projection as the section error, never as an empty tree', async () => {
    stubFetch(() => jsonResponse({ ...timelinePage([ROOT_NODE], 38400), projection_status: 'PROJECTION_FAILED' }))
    mount(() => {})
    const error = await screen.findByTestId('section-error-timeline')
    expect(error.textContent).toContain('PROJECTION_FAILED')
    expect(screen.queryByTestId('timeline-empty')).toBeNull()
    expect(screen.queryByTestId('execution-timeline-section')).toBeNull()
  })

  it('renders the top-level nodes in the call tree', async () => {
    stubFetch(() => jsonResponse(timelinePage([ROOT_NODE], 38400)))
    mount(() => {})
    const section = await screen.findByTestId('execution-timeline-section')
    const tree = within(section).getByTestId('timeline-tree-view')
    expect(tree.textContent).toContain('invoke_agent')
    expect(within(tree).getAllByTestId('timeline-node').length).toBe(1)
  })

  it('renders the duration bars under the bars tab', async () => {
    stubFetch(() => jsonResponse(timelinePage([ROOT_NODE], 38400)))
    mount(() => {})
    const section = await screen.findByTestId('execution-timeline-section')
    fireEvent.click(within(section).getByTestId('timeline-tab-bars'))
    const bars = within(section).getByTestId('timeline-bars-view')
    expect(within(bars).getByTestId('timeline-bar-row').textContent).toContain('invoke_agent')
  })

  it('honours an initial bars tab without a press', async () => {
    stubFetch(() => jsonResponse(timelinePage([ROOT_NODE], 38400)))
    mount(() => {}, { initialTab: 'bars' })
    const section = await screen.findByTestId('execution-timeline-section')
    expect(within(section).getByTestId('timeline-bars-view')).toBeTruthy()
  })

  it('states the empty level honestly', async () => {
    stubFetch(() => jsonResponse(timelinePage([], 0)))
    mount(() => {})
    const empty = await screen.findByTestId('timeline-empty')
    expect(empty.textContent).toMatch(/no timeline spans recorded/i)
  })

  it('keeps pager when a later page is empty but total remains (dbId 4704550871)', async () => {
    const topRows = Array.from({ length: 25 }, (_, i) => ({
      ...ROOT_NODE,
      spanId: `top-${i}`,
      childCount: 0,
    }))
    const fetchMock = vi.fn((input: unknown) => {
      const url = String(input)
      const page = Number(new URL(url, 'http://localhost').searchParams.get('page') ?? '1')
      if (page === 2) {
        return Promise.resolve(jsonResponse({ ...timelinePage([], 38400), total: 30, page: 2, offset: 25 }))
      }
      return Promise.resolve(jsonResponse({ ...timelinePage(topRows, 38400), total: 30, page: 1, offset: 0 }))
    })
    vi.stubGlobal('fetch', fetchMock)
    mount(() => {})
    const section = await screen.findByTestId('execution-timeline-section')
    fireEvent.click(within(section).getByTestId('pager-next'))

    await waitFor(() => expect(within(section).queryByTestId('timeline-node')).toBeNull())
    expect(within(section).getByTestId('list-pager')).toBeTruthy()
    expect(within(section).queryByTestId('timeline-empty')).toBeNull()
    expect(screen.queryByText(/No timeline spans recorded/i)).toBeNull()
    expect(within(section).getByText(/No spans on this page/i)).toBeTruthy()
  })

  it('styles status badges by their failure and success classes', async () => {
    const failing = { ...ROOT_NODE, spanId: 'span-bad', name: 'Read', status: 'error' }
    const succeeding = { ...ROOT_NODE, spanId: 'span-good', name: 'Bash', status: 'ok' }
    stubFetch(() => jsonResponse(timelinePage([failing, succeeding], 38400)))
    mount(() => {})
    const section = await screen.findByTestId('execution-timeline-section')
    const badges = within(section).getAllByTestId('status-badge')
    const byText = new Map(badges.map((badge) => [badge.textContent, badge]))
    expect(byText.get('error')!.className).toMatch(/text-red-/)
    expect(byText.get('ok')!.className).toMatch(/text-emerald-/)
  })

  it('scales a bar by the level bounds the route answered', async () => {
    const lone = { ...ROOT_NODE, spanId: 'span-kind-only', kind: 'tool', name: 'kind_span', durationMs: 500, status: null }
    stubFetch(() => jsonResponse(timelinePage([lone], 500)))
    mount(() => {}, { initialTab: 'bars' })
    const section = await screen.findByTestId('execution-timeline-section')
    const bar = within(section).getByTestId('timeline-bar-span-kind-only')
    expect(bar.style.left).toBe('0%')
    expect(bar.style.width).toBe('100%')
  })

  it('maps the tool kind to the timeline operation colour', () => {
    expect(TIMELINE_OP_COLORS.tool).toBe('#10b981')
    expect(TIMELINE_OP_COLORS.chat).toBe('#f59e0b')
  })
})

describe('SessionTimelineSection inspector wiring', () => {
  it('opens the span inspector for the clicked bar row', async () => {
    stubFetch(() => jsonResponse(timelinePage([ROOT_NODE], 38400)))
    const openInspector = vi.fn()
    mount(openInspector, { initialTab: 'bars' })
    const section = await screen.findByTestId('execution-timeline-section')
    fireEvent.click(within(section).getByTestId('timeline-bar-row'))
    expect(openInspector).toHaveBeenCalledTimes(1)
    const request = openInspector.mock.calls[0][0] as OpenInspectorRequest
    expect(request.title).toBe('Span: invoke_agent')
    expect(request.contentRequest).toEqual({ sessionId: SESSION_ID, span: 'root-span-1' })
  })

  it('opens the span inspector for a clicked tree node', async () => {
    stubFetch(() => jsonResponse(timelinePage([ROOT_NODE], 38400)))
    const openInspector = vi.fn()
    mount(openInspector)
    const section = await screen.findByTestId('execution-timeline-section')
    fireEvent.click(within(section).getByTestId('timeline-node'))
    const request = openInspector.mock.calls[0][0] as OpenInspectorRequest
    expect(request.title).toBe('Span: invoke_agent')
    expect(request.contentRequest).toEqual({ sessionId: SESSION_ID, span: 'root-span-1' })
  })
})

// T13: deeper levels load on expand and every level pages past its first 25.
describe('SessionTimelineSection: levels load on expand and page', () => {
  const CHILD_NODE = {
    spanId: 'child-1',
    parentId: 'span/one',
    startMs: 10000,
    durationMs: 5000,
    kind: 'tool',
    name: 'Read',
    status: 'ok',
    attributes: {},
    isSubagent: false,
    isAuxiliary: false,
    cost: { basis: 'none', status: '—' },
    childCount: 0,
  }

  function levelPage(
    nodes: unknown[],
    opts: { total?: number; page?: number; limit?: number; order?: string; parent?: string | null; endMs?: number } = {},
  ) {
    const limit = opts.limit ?? 25
    return {
      sessionId: SESSION_ID,
      parent: opts.parent ?? null,
      nodes,
      total: opts.total ?? nodes.length,
      limit,
      offset: ((opts.page ?? 1) - 1) * limit,
      page: opts.page ?? 1,
      ...(opts.order ? { order: opts.order } : {}),
      bounds: { startMs: 0, endMs: opts.endMs ?? 38400 },
    }
  }

  function stubTimelineRoutes(routes: (url: string) => unknown): ReturnType<typeof vi.fn> {
    const fetchMock = vi.fn((input: unknown) => Promise.resolve(jsonResponse(routes(String(input)))))
    vi.stubGlobal('fetch', fetchMock)
    return fetchMock
  }

  it('expanding a node requests that node’s first page, its id URL-encoded', async () => {
    const nested = { ...ROOT_NODE, spanId: 'span/one', childCount: 2 }
    const fetchMock = stubTimelineRoutes((url) =>
      url.includes('parent=span%2Fone') ? levelPage([CHILD_NODE], { parent: 'span/one' }) : levelPage([nested]),
    )
    mount(() => {})
    const section = await screen.findByTestId('execution-timeline-section')
    expect(fetchMock.mock.calls.length).toBe(1)
    fireEvent.click(within(section).getByTestId('timeline-node-expand'))
    await within(section).findAllByTestId('timeline-node')
    const urls = fetchMock.mock.calls.map((call) => String(call[0]))
    expect(urls.some((url) => url.endsWith('/timeline?page=1&limit=25&order=desc&parent=span%2Fone'))).toBe(true)
  })

  it('pages the top level through the shared pager instead of a root load-more button', async () => {
    const topRows = Array.from({ length: 25 }, (_, i) => ({
      ...ROOT_NODE,
      spanId: `top-${i}`,
      childCount: 0,
    }))
    const laterRows = Array.from({ length: 5 }, (_, i) => ({
      ...ROOT_NODE,
      spanId: `top-${25 + i}`,
      childCount: 0,
    }))
    const fetchMock = stubTimelineRoutes((url) =>
      url.includes('page=2')
        ? levelPage(laterRows, { total: 30, page: 2 })
        : levelPage(topRows, { total: 30 }),
    )
    mount(() => {})
    const section = await screen.findByTestId('execution-timeline-section')
    expect(within(section).getAllByTestId('timeline-node').length).toBe(25)
    expect(within(section).queryByTestId('timeline-level-more')).toBeNull()
    expect(fetchMock.mock.calls.length).toBe(1)
    const pager = within(section).getByTestId('list-pager')
    fireEvent.click(within(pager).getByTestId('pager-next'))
    await waitFor(() => expect(within(section).getAllByTestId('timeline-node').length).toBe(5))
    const urls = fetchMock.mock.calls.map((call) => String(call[0]))
    expect(urls.some((url) => url.endsWith('/timeline?page=2&limit=25&order=desc'))).toBe(true)
  })

  it('preserves child timeline-level-more to page expanded child levels past 25', async () => {
    const nested = { ...ROOT_NODE, spanId: 'span/one', childCount: 30 }
    const childPage1 = Array.from({ length: 25 }, (_, i) => ({
      ...CHILD_NODE,
      spanId: `child-${i}`,
    }))
    const childPage2 = Array.from({ length: 5 }, (_, i) => ({
      ...CHILD_NODE,
      spanId: `child-${25 + i}`,
    }))
    const fetchMock = stubTimelineRoutes((url) => {
      if (url.includes('parent=span%2Fone')) {
        return url.includes('page=2')
          ? levelPage(childPage2, { total: 30, page: 2, parent: 'span/one' })
          : levelPage(childPage1, { total: 30, page: 1, parent: 'span/one' })
      }
      return levelPage([nested], { total: 1 })
    })
    mount(() => {})
    const section = await screen.findByTestId('execution-timeline-section')
    fireEvent.click(within(section).getByTestId('timeline-node-expand'))
    await waitFor(() => expect(within(section).getAllByTestId('timeline-node').length).toBe(26))

    fireEvent.click(within(section).getByTestId('timeline-level-more'))
    await waitFor(() => expect(within(section).getAllByTestId('timeline-node').length).toBe(31))
    const urls = fetchMock.mock.calls.map((call) => String(call[0]))
    expect(urls.some((url) => url.endsWith('/timeline?page=2&limit=25&order=desc&parent=span%2Fone'))).toBe(true)
  })

  it('lists every loaded node in the duration view, scaled to the bounds', async () => {
    const nested = { ...ROOT_NODE, spanId: 'span/one', childCount: 2 }
    stubTimelineRoutes((url) =>
      url.includes('parent=span%2Fone') ? levelPage([CHILD_NODE], { parent: 'span/one' }) : levelPage([nested]),
    )
    mount(() => {})
    const section = await screen.findByTestId('execution-timeline-section')
    fireEvent.click(within(section).getByTestId('timeline-node-expand'))
    await within(section).findAllByTestId('timeline-node')
    fireEvent.click(within(section).getByTestId('timeline-tab-bars'))
    const bars = within(section).getByTestId('timeline-bars-view')
    expect(within(bars).getAllByTestId('timeline-bar-row').length).toBe(2)
    const childBar = within(bars).getByTestId('timeline-bar-child-1')
    expect(childBar.style.left).toBe(`${(10000 / 38400) * 100}%`)
  })

  it('re-requests the new session top level, page 1, when the session changes past page 1', async () => {
    const SESSION_B = 'sess-b-456'
    const topRows = Array.from({ length: 25 }, (_, i) => ({
      ...ROOT_NODE,
      spanId: `top-${i}`,
      childCount: 0,
    }))
    const laterRows = Array.from({ length: 5 }, (_, i) => ({
      ...ROOT_NODE,
      spanId: `top-${25 + i}`,
      childCount: 0,
    }))
    const fetchMock = stubTimelineRoutes((url) => {
      if (url.includes(SESSION_B)) return levelPage(topRows)
      return url.includes('page=2')
        ? levelPage(laterRows, { total: 30, page: 2 })
        : levelPage(topRows, { total: 30 })
    })
    const client = queryClient()
    const view = render(
      <QueryClientProvider client={client}>
        <SessionTimelineSection
          sessionId={SESSION_ID}
          summary={SUMMARY}
          summaryRecord={SUMMARY.summary as never}
          openInspector={() => {}}
        />
      </QueryClientProvider>,
    )
    const section = await screen.findByTestId('execution-timeline-section')
    expect(within(section).getAllByTestId('timeline-node').length).toBe(25)
    fireEvent.click(within(section).getByTestId('pager-next'))
    await waitFor(() => expect(within(section).getAllByTestId('timeline-node').length).toBe(5))
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('page=2'))).toBe(true)
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes(SESSION_B))).toBe(false)

    view.rerender(
      <QueryClientProvider client={client}>
        <SessionTimelineSection
          sessionId={SESSION_B}
          summary={SUMMARY}
          summaryRecord={SUMMARY.summary as never}
          openInspector={() => {}}
        />
      </QueryClientProvider>,
    )
    // The new session restarts at its own first page: no page-2 request for it.
    await waitFor(() => {
      const urlsForB = fetchMock.mock.calls.map((call) => String(call[0])).filter((url) => url.includes(SESSION_B))
      expect(urlsForB.length).toBeGreaterThan(0)
      expect(urlsForB.every((url) => url.endsWith('/timeline?page=1&limit=25&order=desc'))).toBe(true)
    })
  })

  it('collapses expanded levels when the session changes', async () => {
    const SESSION_B = 'sess-b-456'
    const nested = { ...ROOT_NODE, spanId: 'span/one', childCount: 2 }
    const fetchMock = stubTimelineRoutes((url) => {
      if (url.includes(SESSION_B)) return levelPage([{ ...nested, spanId: 'b-root' }])
      return url.includes('parent=span%2Fone') ? levelPage([CHILD_NODE], { parent: 'span/one' }) : levelPage([nested])
    })
    const client = queryClient()
    const view = render(
      <QueryClientProvider client={client}>
        <SessionTimelineSection
          sessionId={SESSION_ID}
          summary={SUMMARY}
          summaryRecord={SUMMARY.summary as never}
          openInspector={() => {}}
        />
      </QueryClientProvider>,
    )
    let section = await screen.findByTestId('execution-timeline-section')
    fireEvent.click(within(section).getByTestId('timeline-node-expand'))
    await within(section).findAllByTestId('timeline-node')
    const urlsForB = () =>
      fetchMock.mock.calls.map((call) => String(call[0])).filter((url) => url.includes(SESSION_B))
    expect(urlsForB().length).toBe(0)

    view.rerender(
      <QueryClientProvider client={client}>
        <SessionTimelineSection
          sessionId={SESSION_B}
          summary={SUMMARY}
          summaryRecord={SUMMARY.summary as never}
          openInspector={() => {}}
        />
      </QueryClientProvider>,
    )
    // The new session's tree is unexpanded: only its own top level, page 1.
    await waitFor(() => expect(urlsForB().length).toBeGreaterThan(0))
    expect(urlsForB().every((url) => url.endsWith('/timeline?page=1&limit=25&order=desc'))).toBe(true)
    section = await screen.findByTestId('execution-timeline-section')
    expect(within(section).getAllByTestId('timeline-node').length).toBe(1)
  })
})

describe('SessionTimelineSection shared pager, child propagation, and state resets', () => {
  const CHILD_NODE = {
    spanId: 'child-1',
    parentId: 'root-span-1',
    startMs: 10000,
    durationMs: 5000,
    kind: 'tool',
    name: 'Read',
    status: 'ok',
    attributes: {},
    isSubagent: false,
    isAuxiliary: false,
    cost: { basis: 'none', status: '—' },
    childCount: 0,
  }

  function stubDynamicRoutes(): ReturnType<typeof vi.fn> {
    const fetchMock = vi.fn((input: unknown) => {
      const url = String(input)
      const parsed = new URL(url, 'http://localhost')
      const page = Number(parsed.searchParams.get('page') ?? '1')
      const limit = Number(parsed.searchParams.get('limit') ?? '25')
      const order = parsed.searchParams.get('order') ?? 'desc'
      const parent = parsed.searchParams.get('parent')

      if (parent === 'root-span-1') {
        const count = page === 1 ? limit : 5
        const nodes = Array.from({ length: count }, (_, i) => ({
          ...CHILD_NODE,
          spanId: `child-${order}-p${page}-${i}`,
          name: `Child-${order}-p${page}-${i}`,
        }))
        return Promise.resolve(
          jsonResponse({
            sessionId: SESSION_ID,
            parent,
            nodes,
            total: limit + 5,
            limit,
            offset: (page - 1) * limit,
            page,
            order,
            bounds: { startMs: 0, endMs: 38400 },
          }),
        )
      }

      const nodes = [
        {
          ...ROOT_NODE,
          spanId: 'root-span-1',
          name: `Root-${order}-p${page}`,
          childCount: limit + 5,
        },
      ]
      return Promise.resolve(
        jsonResponse({
          sessionId: SESSION_ID,
          parent: null,
          nodes,
          total: 120,
          limit,
          offset: (page - 1) * limit,
          page,
          order,
          bounds: { startMs: 0, endMs: 38400 },
        }),
      )
    })
    vi.stubGlobal('fetch', fetchMock)
    return fetchMock
  }

  it('shows limit and order controls on a single non-empty page and hides pager when empty', async () => {
    stubFetch(() => jsonResponse(timelinePage([ROOT_NODE], 38400)))
    const view = mount(() => {})
    const section = await screen.findByTestId('execution-timeline-section')
    const pager = within(section).getByTestId('list-pager')
    expect(within(pager).getByTestId('pager-limit')).toBeTruthy()
    expect(within(pager).getByTestId('pager-order').textContent).toBe('Newest first')
    expect(within(pager).queryByTestId('pager-prev')).toBeNull()

    fireEvent.click(within(section).getByTestId('timeline-tab-bars'))
    expect(within(section).getByTestId('list-pager')).toBeTruthy()

    view.unmount()
    stubFetch(() => jsonResponse(timelinePage([], 0)))
    mount(() => {})
    await screen.findByTestId('timeline-empty')
    expect(screen.queryByTestId('list-pager')).toBeNull()
  })

  it('shares <Pager> across Call Tree and Duration Timeline and supports 25/50/100 + order toggle resetting to page 1', async () => {
    const fetchMock = stubDynamicRoutes()
    mount(() => {})
    const section = await screen.findByTestId('execution-timeline-section')

    // Page size 25 -> 50 -> 100 resets to page 1 and persists
    fireEvent.click(within(section).getByTestId('pager-next'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/timeline?page=2&limit=25&order=desc'))).toBe(true),
    )

    fireEvent.change(within(section).getByTestId('pager-limit'), { target: { value: '50' } })
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/timeline?page=1&limit=50&order=desc'))).toBe(true),
    )
    expect(window.sessionStorage.getItem('kyberdash:session-section:limit:timeline')).toBe('50')

    fireEvent.change(within(section).getByTestId('pager-limit'), { target: { value: '100' } })
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/timeline?page=1&limit=100&order=desc'))).toBe(true),
    )
    expect(window.sessionStorage.getItem('kyberdash:session-section:limit:timeline')).toBe('100')

    // Switch to Duration Timeline and verify paging + order toggle reset to page 1
    fireEvent.click(within(section).getByTestId('timeline-tab-bars'))
    expect(within(section).getByTestId('timeline-bars-view')).toBeTruthy()
    fireEvent.click(within(section).getByTestId('pager-next'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/timeline?page=2&limit=100&order=desc'))).toBe(true),
    )
    await waitFor(() =>
      expect(within(section).getByTestId('timeline-bars-view').textContent).toContain('Root-desc-p2'),
    )

    fireEvent.click(within(section).getByTestId('pager-order'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/timeline?page=1&limit=100&order=asc'))).toBe(true),
    )
    expect(within(section).getByTestId('pager-order').textContent).toBe('Oldest first')
    await waitFor(() =>
      expect(within(section).getByTestId('timeline-bars-view').textContent).toContain('Root-asc-p1'),
    )
  })

  it('forwards active limit and order to child queries and resets expandedIds and levelPages on limit, order, and page changes', async () => {
    const fetchMock = stubDynamicRoutes()
    mount(() => {})
    const section = await screen.findByTestId('execution-timeline-section')

    // Change limit=50 and order=asc first
    fireEvent.change(within(section).getByTestId('pager-limit'), { target: { value: '50' } })
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/timeline?page=1&limit=50&order=desc'))).toBe(true),
    )
    await waitFor(() => expect((within(section).getByTestId('pager-order') as HTMLButtonElement).disabled).toBe(false))
    fireEvent.click(within(section).getByTestId('pager-order'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/timeline?page=1&limit=50&order=asc'))).toBe(true),
    )
    await waitFor(() => expect((within(section).getByTestId('pager-order') as HTMLButtonElement).disabled).toBe(false))

    // Expand root-span-1 and load child page 2
    fireEvent.click(within(section).getByTestId('timeline-node-expand'))
    await waitFor(() => expect(within(section).getAllByTestId('timeline-node').length).toBe(51))
    expect(
      fetchMock.mock.calls.some(([url]) => String(url).includes('/timeline?page=1&limit=50&order=asc&parent=root-span-1')),
    ).toBe(true)

    fireEvent.click(within(section).getByTestId('timeline-level-more'))
    await waitFor(() => expect(within(section).getAllByTestId('timeline-node').length).toBe(56))
    expect(
      fetchMock.mock.calls.some(([url]) => String(url).includes('/timeline?page=2&limit=50&order=asc&parent=root-span-1')),
    ).toBe(true)

    // Root page change resets BOTH expandedIds and levelPages
    fireEvent.click(within(section).getByTestId('pager-next'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/timeline?page=2&limit=50&order=asc'))).toBe(true),
    )
    await waitFor(() => expect(within(section).getAllByTestId('timeline-node').length).toBe(1))
    expect(within(section).getByTestId('timeline-node-expand').getAttribute('aria-expanded')).toBe('false')

    // Re-expand on page 2 -> only child page 1 is requested (levelPages was reset)
    const childPage2CallsBeforeReexpand = fetchMock.mock.calls.filter(([url]) =>
      String(url).includes('page=2&limit=50&order=asc&parent=root-span-1'),
    ).length
    fireEvent.click(within(section).getByTestId('timeline-node-expand'))
    await waitFor(() => expect(within(section).getAllByTestId('timeline-node').length).toBe(51))
    const childPage2CallsAfterReexpand = fetchMock.mock.calls.filter(([url]) =>
      String(url).includes('page=2&limit=50&order=asc&parent=root-span-1'),
    ).length
    expect(childPage2CallsAfterReexpand).toBe(childPage2CallsBeforeReexpand)

    // Limit change resets expandedIds
    fireEvent.change(within(section).getByTestId('pager-limit'), { target: { value: '25' } })
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/timeline?page=1&limit=25&order=asc'))).toBe(true),
    )
    await waitFor(() => expect(within(section).getAllByTestId('timeline-node').length).toBe(1))
    expect(within(section).getByTestId('timeline-node-expand').getAttribute('aria-expanded')).toBe('false')

    // Expand again, then order change resets expandedIds
    fireEvent.click(within(section).getByTestId('timeline-node-expand'))
    await waitFor(() => expect(within(section).getAllByTestId('timeline-node').length).toBe(26))
    fireEvent.click(within(section).getByTestId('pager-order'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/timeline?page=1&limit=25&order=desc'))).toBe(true),
    )
    await waitFor(() => expect(within(section).getAllByTestId('timeline-node').length).toBe(1))
    expect(within(section).getByTestId('timeline-node-expand').getAttribute('aria-expanded')).toBe('false')
  })
})
