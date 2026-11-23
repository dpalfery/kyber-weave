// @vitest-environment happy-dom
// The timeline section: the session's top-level spans, one page at a time,
// rendered through the existing call-tree and duration-bars views. T10 pins
// the top level; deeper levels page on expand in T11–T13.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
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
