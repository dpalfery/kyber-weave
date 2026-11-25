// @vitest-environment happy-dom
// ContextExplorer pages through the server (Task T3): one paged request per
// scope carrying the shell's harness and the search text, a shared pager, and
// subagent children paged past the root's inline preview.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { AgentSessionRow, ContextExplorer, SESSION_LIST_COLUMNS } from './ContextExplorer.js'
import type { KyberSessionSummary } from '../lib/kyberApi.js'
import { formatCostFigure, normalizeCostBlock, STATUS_EXPLANATIONS } from './SessionCostPanel.js'

function createTestQueryClient() {
  // staleTime and gcTime pinned: data a test seeds (an expanded row's
  // dashboard payload) must survive until its observer mounts — a gcTime of
  // zero collects an unobserved seed immediately — and must render without
  // the mount refetch firing an unstubbed request.
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity, staleTime: Infinity } } })
}

let queryClient: QueryClient

function session(harness: string, suffix = ''): KyberSessionSummary {
  return {
    session_id: `sess-${harness}${suffix}`,
    harness,
    label: `Canonical ${harness}${suffix} session`,
    started: '2026-03-01T12:00:00.000Z',
    turn_count: 3,
    cost_usd: 0.185,
  }
}

const canonicalHarnesses = [
  'claude-code', 'claude-desktop', 'claude-cli', 'codex-desktop', 'codex-cli',
  'antigravity-cli', 'antigravity-ide', 'zcode', 'cursor-agent',
  'copilot-cli', 'copilot-vscode', 'copilot-agent', 'future-harness-v2',
]
const inventory = [...canonicalHarnesses.map((harness) => session(harness)), session('claude-code', '-second')]

function jsonResponse(payload: unknown, status = 200): Response {
  return { ok: status >= 200 && status < 300, status, json: async () => payload } as unknown as Response
}

/**
 * A stub that answers the way the engine's paging routes do: harness and
 * search filter server-side, the window is chosen by page and limit, and a
 * root's subagent children page at their own route. Requests are recorded so
 * tests assert what was asked for.
 */
function stubServer(
  roots: KyberSessionSummary[],
  childrenOf: Record<string, KyberSessionSummary[]> = {},
  opts: { hold?: boolean; failSubagents?: boolean } = {},
) {
  const requests: string[] = []
  const waiters: Array<() => void> = []
  let holding = opts.hold === true
  const fetchMock = vi.fn(async (input: string) => {
    const url = new URL(String(input), 'http://localhost')
    requests.push(`${url.pathname}${url.search}`)
    if (url.pathname.endsWith('/subagents') && opts.failSubagents === true) return jsonResponse({}, 500)
    let rows: KyberSessionSummary[] = []
    let limit = 100
    if (url.pathname === '/api/kyber/sessions') {
      const harness = url.searchParams.get('harness')
      const q = url.searchParams.get('q')
      rows = roots
      if (harness) rows = rows.filter((s) => (s.harness ?? '').toLowerCase() === harness.toLowerCase())
      if (q) {
        const wanted = q.toLowerCase()
        rows = rows.filter(
          (s) => (s.label ?? '').toLowerCase().includes(wanted) || s.session_id.toLowerCase().includes(wanted),
        )
      }
    } else {
      const subagents = /^\/api\/kyber\/session\/([^/]+)\/subagents$/.exec(url.pathname)
      if (!subagents) throw new Error(`unstubbed fetch: ${input}`)
      rows = childrenOf[decodeURIComponent(subagents[1]!)] ?? []
      limit = 25
    }
    const page = Number(url.searchParams.get('page') ?? 1)
    const windowed = rows.slice((page - 1) * limit, page * limit)
    if (holding) await new Promise<void>((resolve) => waiters.push(resolve))
    const itemsKey = url.pathname.endsWith('/subagents') ? 'subagents' : 'sessions'
    return jsonResponse({ [itemsKey]: windowed, data: windowed, total: rows.length, limit, offset: (page - 1) * limit, page })
  })
  vi.stubGlobal('fetch', fetchMock)
  return {
    requests,
    fetchMock,
    /** Releases every answer a `hold: true` stub is holding. */
    release: () => {
      holding = false
      waiters.splice(0).forEach((release) => release())
    },
  }
}

/** A root carrying its inline subagent preview and the true total. */
function rootWithChildren(id: string, children: KyberSessionSummary[], harness = 'claude-code'): KyberSessionSummary {
  // The assertion keeps the engine's `subagent_total` key, which the summary
  // type does not spell yet.
  return {
    ...session(harness),
    session_id: id,
    subagents: children.slice(0, 25),
    subagent_total: children.length,
  } as KyberSessionSummary
}

function renderExplorer(props: Parameters<typeof ContextExplorer>[0] = {}) {
  return render(
    <QueryClientProvider client={queryClient}>
      <ContextExplorer {...props} />
    </QueryClientProvider>,
  )
}

function searchBox(): HTMLInputElement {
  return screen.getByTestId('explorer-search') as HTMLInputElement
}

/** The detail payload AgentSessionDashboard reads when a row is expanded. */
function seedSessionDetail(id: string, harness = 'claude-code') {
  queryClient.setQueryData(['kyber-session', id], {
    id, session_id: id, harness,
    summary: { turn_count: 3, cost: { usd: 0.185, basis: 'published_rates', status: 'ok' } },
    turns: [], tools: [], timeline: [],
  })
}

const sampleSession: KyberSessionSummary = {
  ...session('claude-code'),
  turn_count: 10,
  cost: { basis: 'published', status: 'priced', value: 0.185, currency: 'USD' },
}

beforeEach(() => {
  queryClient = createTestQueryClient()
  stubServer(inventory)
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('ContextExplorer pages through the server (Task T3)', () => {
  it('requests the first page with the sessions page limit and no harness when the scope is all', async () => {
    const server = stubServer(inventory)
    renderExplorer()
    await screen.findByTestId('agent-session-row-sess-claude-code')
    // The engine serves roots and their inline subagents; the explorer shows
    // every root the page carried.
    expect(server.requests).toEqual(['/api/kyber/sessions?page=1&limit=100'])
    expect(screen.getAllByTestId(/^agent-session-row-/)).toHaveLength(inventory.length)
  })

  it("sends the shell's harness to the server and shows exactly the page the server served", async () => {
    const server = stubServer(inventory)
    renderExplorer({ activeHarness: 'codex-cli' })
    await screen.findByTestId('agent-session-row-sess-codex-cli')
    expect(server.requests).toEqual(['/api/kyber/sessions?page=1&limit=100&harness=codex-cli'])
  })

  it('keeps an unrecognized canonical harness scoped through the server, showing its raw id', async () => {
    renderExplorer({ activeHarness: 'future-harness-v2' })
    await screen.findByTestId('agent-session-row-sess-future-harness-v2')
    expect(screen.queryByTestId('agent-session-row-sess-claude-code')).toBeNull()
  })

  it('treats the agent-all spelling as the unscoped inventory', async () => {
    const server = stubServer(inventory)
    renderExplorer({ activeHarness: 'agent-all' })
    await screen.findByTestId('agent-session-row-sess-claude-code')
    expect(server.requests[0]).not.toContain('harness=')
  })

  it('holds the loading state until the paged answer arrives', async () => {
    const server = stubServer(inventory, {}, { hold: true })
    renderExplorer()
    expect(screen.getByTestId('explorer-loading')).not.toBeNull()
    expect(screen.queryByTestId('agent-session-row-sess-claude-code')).toBeNull()
    server.release()
    await screen.findByTestId('agent-session-row-sess-claude-code')
  })

  it('renders the error state when the request fails, with the message the user also sees', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse({}, 503)))
    renderExplorer()
    const error = await screen.findByTestId('explorer-error')
    expect(error.textContent).toContain('Request failed (503)')
    expect(screen.queryByTestId('agent-session-row-sess-claude-code')).toBeNull()
  })

  it('renders the error state when the served envelope is malformed, never a silent empty list', async () => {
    // An unpaged body — the shape the old list route served — is not the page
    // envelope; it is a broken server, and must read as an error.
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse({ sessions: inventory })))
    renderExplorer()
    const error = await screen.findByTestId('explorer-error')
    expect(error.textContent).toMatch(/malformed/i)
    expect(screen.queryByTestId('explorer-empty')).toBeNull()
  })

  it('renders the scoped empty state when the server answers an empty page', async () => {
    stubServer([session('claude-code')])
    renderExplorer({ activeHarness: 'codex-cli' })
    const empty = await screen.findByTestId('explorer-empty')
    expect(empty.textContent).toBe('No sessions for Codex CLI.')
  })

  it('shows the unrecognized scope verbatim in the scoped empty state', async () => {
    stubServer([session('claude-code')])
    renderExplorer({ activeHarness: 'future-harness-v2' })
    expect((await screen.findByTestId('explorer-empty')).textContent).toBe('No sessions for future-harness-v2.')
  })

  it('shows the unscoped empty state when the store holds no sessions at all', async () => {
    stubServer([])
    renderExplorer()
    expect((await screen.findByTestId('explorer-empty')).textContent).toBe('No sessions found.')
  })

  it('searches server-side: the query string carries q and the rows are the narrowed page', async () => {
    const server = stubServer(inventory)
    renderExplorer()
    await screen.findByTestId('agent-session-row-sess-claude-code')

    fireEvent.change(searchBox(), { target: { value: 'codex-cli' } })
    await waitFor(() => expect(server.requests.some((url) => url.includes('q=codex-cli'))).toBe(true))
    await waitFor(() => expect(screen.queryByTestId('agent-session-row-sess-claude-code')).toBeNull())
    expect(screen.getByTestId('agent-session-row-sess-codex-cli')).not.toBeNull()
  })

  it('commits one search for a burst of keystrokes, not one per character', async () => {
    const server = stubServer(inventory)
    renderExplorer()
    await screen.findByTestId('agent-session-row-sess-claude-code')

    fireEvent.change(searchBox(), { target: { value: 'c' } })
    fireEvent.change(searchBox(), { target: { value: 'co' } })
    fireEvent.change(searchBox(), { target: { value: 'codex-cli' } })

    await waitFor(() => expect(server.requests.some((url) => url.includes('q=codex-cli'))).toBe(true))
    expect(server.requests.filter((url) => url.includes('q='))).toHaveLength(1)
  })

  it('restarts at page one when the search changes', async () => {
    const many = Array.from({ length: 250 }, (_, i) => session('claude-code', `-${i}`))
    const server = stubServer(many)
    renderExplorer()
    await screen.findByTestId('agent-session-row-sess-claude-code-0')

    // Walk to page two, then search: the new search must ask for page one.
    fireEvent.click(screen.getByRole('button', { name: 'Next page' }))
    await waitFor(() => expect(server.requests.some((url) => url.includes('page=2'))).toBe(true))

    fireEvent.change(searchBox(), { target: { value: '-249' } })
    await waitFor(() => expect(server.requests.some((url) => url.includes('q=-249') && url.includes('page=1'))).toBe(true))
    expect(screen.getByTestId('agent-session-row-sess-claude-code-249')).not.toBeNull()
  })

  it('pages the root list through the shared pager, requesting page two only on press', async () => {
    const many = Array.from({ length: 250 }, (_, i) => session('claude-code', `-${i}`))
    const server = stubServer(many)
    renderExplorer()
    await screen.findByTestId('agent-session-row-sess-claude-code-0')
    expect(screen.getAllByTestId(/^agent-session-row-/)).toHaveLength(100)

    expect(server.requests.filter((url) => url.includes('page=2'))).toHaveLength(0)
    fireEvent.click(screen.getByRole('button', { name: 'Next page' }))
    await waitFor(() => expect(server.requests.some((url) => url.includes('page=2'))).toBe(true))
    expect(screen.getAllByTestId(/^agent-session-row-/)).toHaveLength(100)
  })

  it('marks the page it is showing on the shared pager', async () => {
    const many = Array.from({ length: 250 }, (_, i) => session('claude-code', `-${i}`))
    stubServer(many)
    renderExplorer()
    await screen.findByTestId('agent-session-row-sess-claude-code-0')

    fireEvent.click(screen.getByRole('button', { name: 'Next page' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Page 2' }).getAttribute('aria-current')).toBe('page'))
    expect(screen.getByRole('button', { name: 'Page 1' }).getAttribute('aria-current')).toBeNull()
  })
})

describe('subagent children page under the root (Task T3)', () => {
  const thirty = Array.from({ length: 30 }, (_, i) => session('claude-code', `-child-${i}`))

  function childRoot(): KyberSessionSummary {
    return rootWithChildren('sess-parent-001', thirty)
  }

  it('shows the inline first 25 and pages the rest at the subagent route', async () => {
    const server = stubServer([childRoot()], { 'sess-parent-001': thirty })
    renderExplorer()
    await screen.findByTestId('agent-session-row-sess-parent-001')

    // Collapsed: no children on screen, no subagent request.
    expect(screen.queryByTestId('subagent-children')).toBeNull()
    expect(server.requests.filter((url) => url.includes('/subagents'))).toHaveLength(0)

    fireEvent.click(screen.getByTestId('subagent-toggle-sess-parent-001'))
    const children = await screen.findByTestId('subagent-children')
    expect(within(children).getAllByTestId(/^agent-session-row-/)).toHaveLength(25)

    fireEvent.click(within(children).getByRole('button', { name: 'Next page' }))
    await waitFor(() => expect(server.requests.some((url) => url.includes('/subagents?page=2'))).toBe(true))
    await waitFor(() => expect(within(children).getAllByTestId(/^agent-session-row-/)).toHaveLength(5))
  })

  it('pages back to the inline preview', async () => {
    stubServer([childRoot()], { 'sess-parent-001': thirty })
    renderExplorer()
    await screen.findByTestId('agent-session-row-sess-parent-001')
    fireEvent.click(screen.getByTestId('subagent-toggle-sess-parent-001'))
    const children = await screen.findByTestId('subagent-children')

    fireEvent.click(within(children).getByRole('button', { name: 'Next page' }))
    await waitFor(() => expect(within(children).getAllByTestId(/^agent-session-row-/)).toHaveLength(5))

    fireEvent.click(within(children).getByRole('button', { name: 'Previous page' }))
    await waitFor(() => expect(within(children).getAllByTestId(/^agent-session-row-/)).toHaveLength(25))
    expect(within(children).queryByTestId('subagent-children-error')).toBeNull()
  })

  it('renders a failed subagent read as an error, never as no more subagents', async () => {
    stubServer([childRoot()], { 'sess-parent-001': thirty }, { failSubagents: true })
    renderExplorer()
    await screen.findByTestId('agent-session-row-sess-parent-001')
    fireEvent.click(screen.getByTestId('subagent-toggle-sess-parent-001'))
    const children = await screen.findByTestId('subagent-children')

    fireEvent.click(within(children).getByRole('button', { name: 'Next page' }))
    const failure = await within(children).findByTestId('subagent-children-error')
    expect(failure.textContent).toContain('Request failed (500)')
    // The inline preview is still on screen: a deeper-page failure does not
    // claim the root has no subagents.
    expect(within(children).getAllByTestId(/^agent-session-row-/)).toHaveLength(25)
  })
})

describe('ContextExplorer reports the session it opens', () => {
  it('reports the expanded session and clears it on collapse', async () => {
    const onSelectSession = vi.fn()
    seedSessionDetail('sess-claude-code')
    renderExplorer({ onSelectSession })
    fireEvent.click(await screen.findByTestId('agent-session-row-sess-claude-code'))
    expect(onSelectSession).toHaveBeenCalledExactlyOnceWith('sess-claude-code')
    expect(screen.getByTestId('agent-session-dashboard')).not.toBeNull()

    fireEvent.click(screen.getByTestId('agent-session-row-sess-claude-code'))
    await waitFor(() => expect(onSelectSession).toHaveBeenLastCalledWith(undefined))
    expect(screen.queryByTestId('agent-session-dashboard')).toBeNull()
  })

  it('reports the parent session a subagent row points at', async () => {
    const onSelectSession = vi.fn()
    stubServer([
      { ...session('claude-code'), is_subagent: true, parent_session: 'sess-parent-001' },
      { ...session('claude-code'), session_id: 'sess-parent-001' },
    ])
    seedSessionDetail('sess-parent-001')
    renderExplorer({ onSelectSession })
    fireEvent.click(await screen.findByTestId('parent-session-link'))
    expect(onSelectSession).toHaveBeenCalledExactlyOnceWith('sess-parent-001')
  })

  it('closes the expanded session when the scope moves to another harness', async () => {
    seedSessionDetail('sess-claude-code')
    const onSelectSession = vi.fn()
    const { rerender } = renderExplorer({ activeHarness: 'claude-code', onSelectSession })
    fireEvent.click(await screen.findByTestId('agent-session-row-sess-claude-code'))
    expect(screen.getByTestId('agent-session-dashboard')).not.toBeNull()

    rerender(
      <QueryClientProvider client={queryClient}>
        <ContextExplorer activeHarness="codex-cli" onSelectSession={onSelectSession} />
      </QueryClientProvider>,
    )
    expect(screen.queryByTestId('agent-session-dashboard')).toBeNull()
    await screen.findByTestId('agent-session-row-sess-codex-cli')

    // Returning to the original scope does not resurrect the old expansion either.
    rerender(
      <QueryClientProvider client={queryClient}>
        <ContextExplorer activeHarness="claude-code" onSelectSession={onSelectSession} />
      </QueryClientProvider>,
    )
    expect(screen.queryByTestId('agent-session-dashboard')).toBeNull()
  })

  it('keeps the expanded session while the scope is unchanged', async () => {
    seedSessionDetail('sess-claude-code')
    renderExplorer({ activeHarness: 'claude-code' })
    fireEvent.click(await screen.findByTestId('agent-session-row-sess-claude-code'))
    expect(screen.getByTestId('agent-session-dashboard')).not.toBeNull()
  })
})

describe('AgentSessionRow parent navigation', () => {
  it('navigates to the canonical parent without toggling the child', () => {
    const onSelectSession = vi.fn()
    const onToggle = vi.fn()
    const { container } = render(
      <AgentSessionRow
        s={{ ...session('claude-code'), is_subagent: true, parent_session: 'sess-parent-001' }}
        open={false}
        onToggle={onToggle}
        onSelectSession={onSelectSession}
      />,
    )
    const link = container.querySelector('[data-testid="parent-session-link"]')!
    expect(link.getAttribute('title')).toBe('Parent session: sess-parent-001')
    // The link is a sibling of the row button, not inside it, so a click cannot reach onToggle.
    expect(link.closest('button')?.getAttribute('data-testid')).toBe('parent-session-link')
    expect(link.querySelector('button')).toBeNull()
    fireEvent.click(link)
    expect(onSelectSession).toHaveBeenCalledExactlyOnceWith('sess-parent-001')
    expect(onToggle).not.toHaveBeenCalled()
  })
})

describe('AgentSessionRow row cost cell (issue #186)', () => {
  // Static markup keeps these assertions on the cell's own classes: the row's
  // dashboard child is only opened (and only queried) when a row is expanded.
  function renderCostCell(cost: KyberSessionSummary['cost'] | undefined): {
    text: string
    title: string
    className: string
  } {
    const target: KyberSessionSummary = { ...sampleSession, cost_usd: null }
    if (cost !== undefined) target.cost = cost
    else delete target.cost
    const markup = renderToStaticMarkup(
      <AgentSessionRow s={target} open={false} onToggle={() => {}} onSelectSession={() => {}} />,
    )
    const match = /<span([^>]*data-testid="agent-session-cost"[^>]*)>([\s\S]*?)<\/span>/.exec(markup)
    expect(match, 'row must render an element with data-testid="agent-session-cost"').not.toBeNull()
    const title = /title="([^"]*)"/.exec(match![1]!)?.[1] ?? ''
    const className = /class="([^"]*)"/.exec(match![1]!)?.[1] ?? ''
    return { text: match![2]!, title, className }
  }

  it('renders the formatted figure (same formatter as the cost tile) for a priced block', () => {
    const block = { basis: 'published', status: 'priced', value: 0.185, currency: 'USD' } as const
    const { text } = renderCostCell(block)
    expect(text).toBe(formatCostFigure(normalizeCostBlock(block)))
    expect(text).toMatch(/^\$0\.1[89]\d*$/)
    expect(text).not.toBe('—')
  })

  it.each([
    ['no_rate', 'no published rate'],
    ['not_billed', 'not billed'],
    ['out_of_scope', 'out of scope'],
  ] as const)('renders the %s reason in words, never a bare dash or $0.00', (status, words) => {
    const { text } = renderCostCell({ basis: 'published', status })
    expect(text).toBe(words)
    expect(text).toBe(formatCostFigure(normalizeCostBlock({ basis: 'published', status })))
    expect(text).not.toContain('—')
    expect(text).not.toContain('$0.00')
  })

  it('renders "partially priced" with no figure for a partial block (U10)', () => {
    const { text } = renderCostCell({ basis: 'published', status: 'partial', value: 1.23, currency: 'USD' })
    expect(text).toBe('partially priced')
    expect(text).not.toContain('$')
    expect(text).not.toContain('—')
  })

  it('renders a dash only when the server sent no cost at all', () => {
    expect(renderCostCell(undefined).text).toBe('—')
  })

  it('names the cost basis in the cell title', () => {
    expect(renderCostCell({ basis: 'published', status: 'no_rate' }).title).toMatch(/published/i)
    expect(
      renderCostCell({ basis: 'harness', status: 'priced', value: 0.5, currency: 'USD' }).title,
    ).toMatch(/harness/i)
  })

  it('keeps word statuses on one line without shrinking below the measured phrase width (issue #228)', () => {
    const { text, className } = renderCostCell({ basis: 'published', status: 'no_rate' })
    expect(text).toBe(formatCostFigure(normalizeCostBlock({ basis: 'published', status: 'no_rate' })))
    expect(className.split(/\s+/)).toEqual(expect.arrayContaining(['w-28', 'whitespace-nowrap', 'shrink-0']))
    expect(className.split(/\s+/)).not.toContain('w-20')
  })

  it.each([
    ['no_rate'],
    ['not_billed'],
    ['out_of_scope'],
    ['partial'],
  ] as const)('explains the %s status in the cell title', (status) => {
    const { title } = renderCostCell({ basis: 'published', status })
    expect(title).toMatch(/published/i)
    expect(title).toContain(STATUS_EXPLANATIONS[status])
  })

  it('titles a priced cell with its cost basis only', () => {
    const { title } = renderCostCell({ basis: 'published', status: 'priced', value: 0.185, currency: 'USD' })
    expect(title).toBe('Cost basis: published')
  })

  it('titles a rolled-up figure as including subagents', () => {
    const markup = renderToStaticMarkup(
      <AgentSessionRow
        s={{ ...sampleSession, cost_usd: null, rollup: { cost_usd: 1.5 } } as KyberSessionSummary}
        open={false}
        onToggle={() => {}}
        onSelectSession={() => {}}
      />,
    )
    expect(markup).toContain('title="Cost including subagents"')
  })
})

describe('ContextExplorer column headers', () => {
  const headerLabels = ['Session', 'Title', 'Harness', 'Turns', 'Cost', 'Started']

  /** The text of each named column label in a header, in order, without the blank spacer slots. */
  function headerCellTexts(header: HTMLElement): string[] {
    return Array.from(header.querySelectorAll('span'))
      .map((el) => el.textContent ?? '')
      .filter((text) => text !== '')
  }

  it('names each column once, in order, above the first row', () => {
    renderExplorer({ sessions: [sampleSession, session('codex-cli')] })
    const headers = screen.getAllByTestId('explorer-column-headers')
    expect(headers).toHaveLength(1)
    expect(headerCellTexts(headers[0]!)).toEqual(headerLabels)
    const firstRow = screen.getByTestId(`agent-session-row-${sampleSession.session_id}`)
    expect(headers[0]!.compareDocumentPosition(firstRow) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('renders no header when the embedded list is empty', () => {
    renderExplorer({ sessions: [] })
    expect(screen.queryByTestId('explorer-column-headers')).toBeNull()
  })

  it('renders no header when the server answers an empty page', async () => {
    stubServer([])
    renderExplorer()
    expect(await screen.findByTestId('explorer-empty')).toBeTruthy()
    expect(screen.queryByTestId('explorer-column-headers')).toBeNull()
  })

  it('renders no header while the list is loading', async () => {
    const server = stubServer(inventory, {}, { hold: true })
    renderExplorer()
    expect(await screen.findByTestId('explorer-loading')).toBeTruthy()
    expect(screen.queryByTestId('explorer-column-headers')).toBeNull()
    server.release()
  })

  it('renders no header when the list request fails', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse({}, 500)))
    renderExplorer()
    expect(await screen.findByTestId('explorer-error')).toBeTruthy()
    expect(screen.queryByTestId('explorer-column-headers')).toBeNull()
  })

  it('renders the header for the data state the server serves', async () => {
    stubServer(inventory)
    renderExplorer()
    expect(await screen.findByTestId('explorer-column-headers')).toBeTruthy()
  })

  it.each([
    ['Session', 'id', 0],
    ['Harness', 'harness', 2],
    ['Turns', 'turns', 3],
    ['Cost', 'cost', 4],
    ['Started', 'started', 5],
  ] as const)('gives the %s header the width class of its row cell', (label, key, cellIndex) => {
    renderExplorer({ sessions: [sampleSession] })
    const header = screen.getByTestId('explorer-column-headers')
    const row = screen.getByTestId(`agent-session-row-${sampleSession.session_id}`)
    // Row cells follow the chevron: id, title, harness, turns, cost, started.
    const rowCells = Array.from(row.children).filter((el) => el.tagName.toLowerCase() !== 'svg')
    const tokens = SESSION_LIST_COLUMNS[key].split(/\s+/)
    expect(within(header).getByText(label).className.split(/\s+/)).toEqual(expect.arrayContaining(tokens))
    expect(rowCells[cellIndex]!.className.split(/\s+/)).toEqual(expect.arrayContaining(tokens))
  })

  it('leaves a spacer the toggle width on a root row without subagents', () => {
    const markup = renderToStaticMarkup(
      <AgentSessionRow s={sampleSession} open={false} onToggle={() => {}} onSelectSession={() => {}} />,
    )
    expect(markup).toContain('data-testid="subagent-toggle-spacer"')
    expect(markup).not.toContain(`data-testid="subagent-toggle-${sampleSession.session_id}"`)
  })

  it('renders the toggle, and no spacer, on a root row with subagents', () => {
    const root = rootWithChildren('root-1', [session('claude-code', '-child')])
    const markup = renderToStaticMarkup(
      <AgentSessionRow s={root} open={false} onToggle={() => {}} onSelectSession={() => {}} />,
    )
    expect(markup).toContain('data-testid="subagent-toggle-root-1"')
    expect(markup).not.toContain('data-testid="subagent-toggle-spacer"')
  })
})

// ---------------------------------------------------------------------------
// Hierarchical subagent tree and dual spend contracts (Task T3.1)
// ---------------------------------------------------------------------------

const childOne: KyberSessionSummary = {
  session_id: 'sess-child-001',
  harness: 'claude-code',
  label: 'explore workspace architecture',
  agent_name: 'explorer',
  is_subagent: true,
  parent_session: 'sess-parent-001',
  turn_count: 5,
  total_input: 12000,
  total_output: 3000,
  cost_usd: 0.05,
  cost: { basis: 'published', status: 'priced', value: 0.05, currency: 'USD' },
  started: '2026-03-01T12:05:00.000Z',
}

const childTwo: KyberSessionSummary = {
  session_id: 'sess-child-002',
  harness: 'claude-code',
  label: 'run test suite verification',
  agent_name: 'test-runner',
  is_subagent: true,
  parent_session: 'sess-parent-001',
  turn_count: 8,
  total_input: 25000,
  total_output: 5000,
  cost_usd: 0.12,
  cost: { basis: 'published', status: 'priced', value: 0.12, currency: 'USD' },
  started: '2026-03-01T12:10:00.000Z',
}

const parentWithSubagents: KyberSessionSummary = {
  session_id: 'sess-parent-001',
  harness: 'claude-code',
  label: 'orchestrate multi-agent feature delivery',
  agent_name: 'lead-orchestrator',
  is_subagent: false,
  turn_count: 12,
  total_input: 40000,
  total_output: 8000,
  cost_usd: 0.20,
  cost: { basis: 'published', status: 'priced', value: 0.20, currency: 'USD' },
  started: '2026-03-01T12:00:00.000Z',
  rollup: {
    turn_count: 25,
    total_input: 77000,
    total_output: 16000,
    tokens: 93000,
    cost_usd: 0.37,
  },
  subagents: [childOne, childTwo],
}

describe('hierarchical subagent tree and dual spend (Task T3.1)', () => {
  it('renders an expand/collapse chevron toggle button on parent sessions with subagents', async () => {
    renderExplorer({ sessions: [parentWithSubagents] })
    await screen.findByTestId('agent-session-row-sess-parent-001')
    expect(screen.getByTestId('subagent-toggle-sess-parent-001').getAttribute('aria-expanded')).toBe('false')
  })

  it('toggles child subagent visibility when clicking the chevron button', async () => {
    renderExplorer({ sessions: [parentWithSubagents] })
    await screen.findByTestId('agent-session-row-sess-parent-001')

    expect(screen.queryByTestId('subagent-children')).toBeNull()
    fireEvent.click(screen.getByTestId('subagent-toggle-sess-parent-001'))
    expect(screen.getByTestId('subagent-children')).not.toBeNull()

    fireEvent.click(screen.getByTestId('subagent-toggle-sess-parent-001'))
    expect(screen.queryByTestId('subagent-children')).toBeNull()
  })

  it('renders child subagents nested and indented under parent with agent names and subagent indicators when expanded', async () => {
    renderExplorer({ sessions: [parentWithSubagents] })
    await screen.findByTestId('agent-session-row-sess-parent-001')
    fireEvent.click(screen.getByTestId('subagent-toggle-sess-parent-001'))

    const children = screen.getByTestId('subagent-children')
    expect(within(children).getByTestId('agent-session-row-sess-child-001')).not.toBeNull()
    expect(within(children).getByTestId('agent-session-row-sess-child-002')).not.toBeNull()
    expect(within(children).getAllByTestId('subagent-indicator')).toHaveLength(2)
    expect(within(children).getByText('explorer · explore workspace architecture')).not.toBeNull()
    expect(within(children).getByText('test-runner · run test suite verification')).not.toBeNull()
  })

  it('displays rolled-up cost from session.rollup on the parent row and granular cost on child rows', async () => {
    renderExplorer({ sessions: [parentWithSubagents] })
    await screen.findByTestId('agent-session-row-sess-parent-001')

    // The parent's wire rollup carries the summed cost; children carry their own.
    expect(screen.getByTestId('agent-session-cost').textContent).toContain('$0.37')

    fireEvent.click(screen.getByTestId('subagent-toggle-sess-parent-001'))
    const children = await screen.findByTestId('subagent-children')
    const childCosts = within(children).getAllByTestId('agent-session-cost').map((node) => node.textContent)
    expect(childCosts).toHaveLength(2)
    expect(childCosts[0]).toContain('$0.05')
    expect(childCosts[1]).toContain('$0.12')
  })

  it('renders the parent cost from the rollup wire shape (cost_usd) beside the rolled-up turns', async () => {
    renderExplorer({ sessions: [parentWithSubagents] })
    await screen.findByTestId('agent-session-row-sess-parent-001')
    expect(screen.getByText('25 turns')).not.toBeNull()
    expect(screen.getByTestId('agent-session-cost').textContent).toContain('$0.37')
  })

  it('never nests a button inside a button, and the subagent toggle reports its state', async () => {
    const { container } = renderExplorer({ sessions: [parentWithSubagents] })
    await screen.findByTestId('agent-session-row-sess-parent-001')
    expect(container.querySelector('button button')).toBeNull()
    expect(screen.getByTestId('subagent-toggle-sess-parent-001').getAttribute('aria-expanded')).toBe('false')

    fireEvent.click(screen.getByTestId('subagent-toggle-sess-parent-001'))
    expect(screen.getByTestId('subagent-toggle-sess-parent-001').getAttribute('aria-expanded')).toBe('true')
  })

  it('renders title as "<agent name> · <task summary>" with a tooltip displaying the raw sessionId', async () => {
    renderExplorer({ sessions: [parentWithSubagents] })
    await screen.findByTestId('agent-session-row-sess-parent-001')

    const parentTitle = screen
      .getAllByTitle('sess-parent-001')
      .find((node) => node.textContent?.includes('lead-orchestrator · orchestrate multi-agent feature delivery'))
    expect(parentTitle).not.toBeNull()

    fireEvent.click(screen.getByTestId('subagent-toggle-sess-parent-001'))
    await waitFor(() => {
      const childTitle = screen
        .getAllByTitle('sess-child-001')
        .find((node) => node.textContent?.includes('explorer · explore workspace architecture'))
      const childTwoTitle = screen
        .getAllByTitle('sess-child-002')
        .find((node) => node.textContent?.includes('test-runner · run test suite verification'))
      expect(childTitle).not.toBeNull()
      expect(childTwoTitle).not.toBeNull()
    })
  })
})
