// @vitest-environment happy-dom
// The tools section: one page of the ranked tool rows with the aggregate the
// engine priced over every row. The ranking table and its server rollup come
// from the section's own data; a tool row click opens the inspector with the
// definitions span the aggregate named — or states honestly that the harness
// exported no definitions and issues no content request at all.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactElement } from 'react'

import { SessionToolsSection } from './SessionToolsSection.js'
import type { OpenInspectorRequest } from './types.js'
import type { SessionSummaryPayload } from './types.js'

const SESSION_ID = 'sess-abc-123'

function queryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
}

function jsonResponse(body: unknown, status = 200): Response {
  return { ok: status >= 200 && status < 300, status, json: async () => body } as unknown as Response
}

function stubFetch(handler: (url: string) => Response): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn((url: string) => Promise.resolve(handler(url)))
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

const SUMMARY_RECORD: SessionSummaryPayload = {
  tools_offered: ['read_file', 'write_file', 'mcp_git_status'],
  tools_invoked: ['read_file', 'write_file'],
  unused_schema_per_turn: 900,
  schema_tokens_per_turn: 2800,
  defs_turns: 5,
  schema_waste_cost: { usd_low: 0.03, usd_high: 0.12, credits_low: 0.15, credits_high: 0.6 },
}

function mount(
  openInspector: (request: OpenInspectorRequest) => void,
  overrides: { summaryRecord?: SessionSummaryPayload | null; harness?: string | null } = {},
): ReturnType<typeof render> {
  const element = (
    <QueryClientProvider client={queryClient()}>
      <SessionToolsSection
        sessionId={SESSION_ID}
        harness={overrides.harness ?? 'copilot'}
        summaryRecord={overrides.summaryRecord === undefined ? SUMMARY_RECORD : overrides.summaryRecord}
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

const READ_ROW = {
  name: 'read_file',
  server: null,
  invocations: 5,
  schema_tokens: 700,
  turns_resident: 5,
  total_schema_cost: 3500,
  invoked: true,
  in_definitions: true,
  is_mcp: false,
}

const WRITE_ROW = {
  name: 'write_file',
  server: null,
  invocations: 3,
  schema_tokens: 1200,
  turns_resident: 5,
  total_schema_cost: 6000,
  invoked: true,
  in_definitions: true,
  is_mcp: false,
}

const GIT_ROW = {
  name: 'mcp_git_status',
  server: 'git',
  invocations: 0,
  schema_tokens: 900,
  turns_resident: 5,
  total_schema_cost: 4500,
  invoked: false,
  in_definitions: true,
  is_mcp: true,
}

function toolsPage(rows: unknown[], aggregate: Record<string, unknown>) {
  return {
    tools: rows,
    data: rows,
    total: rows.length,
    limit: 25,
    offset: 0,
    page: 1,
    aggregate,
  }
}

const AGGREGATE = {
  maxCost: 6000,
  residentCostTotal: 14000,
  invocationCount: 8,
  unusedCount: 1,
  unusedRange: { tokenResidencies: 4500, floor: 0.0, ceiling: 0.45, currency: 'USD' },
  byServer: { git: 4500 },
  definitionsSpanId: 'turn-span-1',
  measurable: true,
  reason: null,
}

describe('SessionToolsSection states', () => {
  it('shows its loading state while the page is in flight', () => {
    stubFetch(() => jsonResponse(toolsPage([READ_ROW], AGGREGATE)))
    mount(() => {})
    const loading = screen.getByTestId('session-tools-loading')
    expect(loading.getAttribute('role')).toBe('status')
    expect(loading.textContent).toMatch(/loading/i)
  })

  it('states the failure plainly and retries on press', async () => {
    const fetchMock = stubFetch(() => jsonResponse({}, 500))
    mount(() => {})
    const error = await screen.findByTestId('section-error-tools')
    expect(error.textContent).toContain('Request failed (500)')
    fireEvent.click(within(error).getByRole('button', { name: 'Retry' }))
    expect(fetchMock.mock.calls.length).toBeGreaterThanOrEqual(2)
  })

  it('ranks the page rows and renders the server rollup from the aggregate', async () => {
    stubFetch(() => jsonResponse(toolsPage([WRITE_ROW, READ_ROW, GIT_ROW], AGGREGATE)))
    mount(() => {})
    const section = await screen.findByTestId('tool-schema-section')
    expect(within(section).getByTestId('tool-row-read_file')).toBeTruthy()
    expect(within(section).getByTestId('tool-row-write_file')).toBeTruthy()
    expect(within(section).getByTestId('tool-row-mcp_git_status').textContent).toContain('never called')
    const serverRow = within(section).getByTestId('schema-server-git')
    expect(serverRow.textContent).toContain('git')
    expect(serverRow.textContent).toContain('MCP')
    expect(within(section).getByTestId('schema-unused-range').textContent).toContain('Unused-schema cost range')
  })

  it('renders the waste banner and range from the summary record', async () => {
    stubFetch(() => jsonResponse(toolsPage([READ_ROW, WRITE_ROW, GIT_ROW], AGGREGATE)))
    mount(() => {})
    const section = await screen.findByTestId('tool-schema-section')
    expect(within(section).getByTestId('schema-waste-banner').textContent).toContain(
      '1 of 3 tools were never called.',
    )
    expect(within(section).getByTestId('schema-waste-banner').textContent).toContain('900 tokens/turn')
    expect(within(section).getByTestId('schema-waste-range').textContent).toContain(
      'Unused waste range: $0.030 – $0.120',
    )
  })

  it('shows both honest banners for an invocation-only session the engine marked not measurable', async () => {
    const invocationOnlyRow = { name: 'read', server: null, invocations: 4, invoked: true, in_definitions: false, is_mcp: false }
    stubFetch(() =>
      jsonResponse(
        toolsPage([invocationOnlyRow], {
          // The engine prices an invocation-only ranking at zero — not null — so the
          // section must read the verdict the aggregate carries, never derive one.
          maxCost: 0,
          residentCostTotal: 0,
          invocationCount: 4,
          unusedCount: 0,
          unusedRange: null,
          byServer: {},
          definitionsSpanId: null,
          measurable: false,
          reason: 'Schema residency is not measurable because tool definitions were not exported.',
        }),
      ),
    )
    mount(() => {})
    const section = await screen.findByTestId('tool-schema-section')
    expect(within(section).getByTestId('schemas-not-exported-banner').textContent).toContain(
      'copilot does not export tool definitions',
    )
    // The engine's own verdict renders even though invocation rows exist: an
    // unrankable ranking must not read as a ranking.
    expect(within(section).getByTestId('schema-cost-not-measurable').textContent).toContain('(4)')
  })

  it('states the engine verdict when the tools route answers no rows and no definitions span', async () => {
    stubFetch(() =>
      jsonResponse(
        toolsPage([], {
          maxCost: null,
          residentCostTotal: 0,
          invocationCount: 0,
          unusedCount: 0,
          unusedRange: null,
          byServer: {},
          definitionsSpanId: null,
          measurable: false,
          reason: 'Schema residency is not measurable because tool definitions were not exported.',
        }),
      ),
    )
    mount(() => {})
    const section = await screen.findByTestId('tool-schema-section')
    expect(within(section).getByTestId('schemas-not-exported-banner').textContent).toContain(
      'copilot does not export tool definitions',
    )
    expect(within(section).getByTestId('schema-cost-not-measurable')).toBeTruthy()
  })

  it('states the empty ranking honestly when definitions existed but no tool was recorded', async () => {
    stubFetch(() => jsonResponse(toolsPage([], { ...AGGREGATE, byServer: {}, measurable: true, reason: null })))
    mount(() => {})
    const section = await screen.findByTestId('tool-schema-section')
    expect(within(section).getByTestId('tools-ranking-table').textContent).toContain(
      'No tools recorded for this session.',
    )
    expect(within(section).queryByTestId('schema-cost-not-measurable')).toBeNull()
  })

  it('pages the ranking at 25 and fetches page two only on press', async () => {
    const fetchMock = stubFetch(() =>
      jsonResponse({ ...toolsPage([READ_ROW, WRITE_ROW, GIT_ROW], AGGREGATE), total: 30 }),
    )
    mount(() => {})
    const section = await screen.findByTestId('tool-schema-section')
    const pager = within(section).getByTestId('list-pager')
    fireEvent.click(within(pager).getByTestId('pager-next'))
    await waitFor(() =>
      expect(
        fetchMock.mock.calls.some((call) => String(call[0]).includes('tools?page=2&limit=25&order=desc')),
      ).toBe(true),
    )
  })

  it('renders page-size and sort controls on a single non-empty page and hides them when empty', async () => {
    stubFetch(() => jsonResponse(toolsPage([READ_ROW], AGGREGATE)))
    const { unmount } = mount(() => {})
    const section = await screen.findByTestId('tool-schema-section')
    const pager = within(section).getByTestId('list-pager')
    expect(within(pager).getByTestId('pager-limit')).toBeTruthy()
    expect(within(pager).getByTestId('pager-order').textContent).toBe('Newest first')
    expect(within(pager).queryByTestId('pager-prev')).toBeNull()
    expect(within(pager).queryByTestId('pager-next')).toBeNull()
    unmount()

    stubFetch(() => jsonResponse(toolsPage([], { ...AGGREGATE, byServer: {}, measurable: true, reason: null })))
    mount(() => {})
    const emptySection = await screen.findByTestId('tool-schema-section')
    expect(within(emptySection).queryByTestId('list-pager')).toBeNull()
  })

  it('changes page size (25 -> 50 -> 100), persists per section, toggles sort order, and resets to page 1', async () => {
    const fetchMock = stubFetch((url) => {
      const parsed = new URL(url, 'http://localhost')
      const page = Number(parsed.searchParams.get('page') ?? '1')
      const limit = Number(parsed.searchParams.get('limit') ?? '25')
      const order = parsed.searchParams.get('order') ?? 'desc'
      return jsonResponse({
        ...toolsPage([READ_ROW, WRITE_ROW, GIT_ROW], AGGREGATE),
        total: 120,
        page,
        limit,
        order,
      })
    })
    mount(() => {})
    const section = await screen.findByTestId('tool-schema-section')
    fireEvent.click(within(section).getByTestId('pager-next'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('tools?page=2&limit=25&order=desc'))).toBe(true),
    )

    fireEvent.change(within(section).getByTestId('pager-limit'), { target: { value: '50' } })
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('tools?page=1&limit=50&order=desc'))).toBe(true),
    )
    expect(window.sessionStorage.getItem('kyberdash:session-section:limit:tools')).toBe('50')

    fireEvent.change(within(section).getByTestId('pager-limit'), { target: { value: '100' } })
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('tools?page=1&limit=100&order=desc'))).toBe(true),
    )
    expect(window.sessionStorage.getItem('kyberdash:session-section:limit:tools')).toBe('100')

    fireEvent.click(within(section).getByTestId('pager-next'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('tools?page=2&limit=100&order=desc'))).toBe(true),
    )

    fireEvent.click(within(section).getByTestId('pager-order'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('tools?page=1&limit=100&order=asc'))).toBe(true),
    )
    expect(within(section).getByTestId('pager-order').textContent).toBe('Oldest first')

    fireEvent.click(within(section).getByTestId('pager-order'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.filter(([url]) => String(url).includes('tools?page=1&limit=100&order=desc')).length).toBeGreaterThanOrEqual(2),
    )
  })

  it('preserves server-returned tool order without client-side re-sorting', async () => {
    stubFetch(() => jsonResponse(toolsPage([READ_ROW, WRITE_ROW], AGGREGATE)))
    mount(() => {})
    const section = await screen.findByTestId('tool-schema-section')
    const rows = section.querySelectorAll('[data-testid^="tool-row-"]')
    expect(Array.from(rows).map((el) => el.getAttribute('data-testid'))).toEqual([
      'tool-row-read_file',
      'tool-row-write_file',
    ])
  })
})

describe('SessionToolsSection inspector wiring', () => {
  it('opens the definitions request on the aggregate span when a tool row is clicked', async () => {
    stubFetch(() => jsonResponse(toolsPage([READ_ROW, WRITE_ROW, GIT_ROW], AGGREGATE)))
    const openInspector = vi.fn()
    mount(openInspector)
    const section = await screen.findByTestId('tool-schema-section')
    fireEvent.click(within(section).getByTestId('tool-row-read_file'))
    expect(openInspector).toHaveBeenCalledTimes(1)
    const request = openInspector.mock.calls[0][0] as OpenInspectorRequest
    expect(request.title).toBe('Tool: read_file')
    expect(request.contentRequest).toEqual({
      sessionId: SESSION_ID,
      span: 'turn-span-1',
      part: 'tool_definitions',
      toolName: 'read_file',
    })
  })

  it('states the harness reason and issues no content request when no definitions exist', async () => {
    stubFetch(() =>
      jsonResponse(
        toolsPage([READ_ROW], {
          ...AGGREGATE,
          byServer: {},
          unusedRange: null,
          definitionsSpanId: null,
          measurable: true,
          reason: null,
        }),
      ),
    )
    const openInspector = vi.fn()
    mount(openInspector, { harness: 'antigravity-cli', summaryRecord: { tools_offered: undefined } })
    const section = await screen.findByTestId('tool-schema-section')
    fireEvent.click(within(section).getByTestId('tool-row-read_file'))
    const request = openInspector.mock.calls[0][0] as OpenInspectorRequest
    expect(request.contentRequest).toBeUndefined()
    const content = request.content as Record<string, unknown>
    expect(content.definitionUnavailableNotice).toContain(
      'antigravity-cli does not export tool definitions for this session',
    )
  })

  it('keeps the Copilot CLI reason verbatim', async () => {
    stubFetch(() =>
      jsonResponse(
        toolsPage([READ_ROW], {
          ...AGGREGATE,
          byServer: {},
          unusedRange: null,
          definitionsSpanId: null,
          measurable: true,
          reason: null,
        }),
      ),
    )
    const openInspector = vi.fn()
    mount(openInspector, {
      harness: 'copilot',
      summaryRecord: { tools_offered: undefined },
    })
    const section = await screen.findByTestId('tool-schema-section')
    fireEvent.click(within(section).getByTestId('tool-row-read_file'))
    const content = (openInspector.mock.calls[0][0] as OpenInspectorRequest).content as Record<string, unknown>
    expect(content.definitionUnavailableNotice).toBe(
      "Copilot CLI session journals carry no tool schemas; the system prompt's <tools> section is usage guidance, not the offered tool set.",
    )
  })
})
