import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import * as ContextExplorerModule from './ContextExplorer.js'
import { AgentSessionRow, ContextExplorer } from './ContextExplorer.js'
import type { KyberSessionSummary } from '../lib/kyberApi.js'
import { formatCostFigure, normalizeCostBlock } from './SessionCostPanel.js'

type InventoryQuery = {
  queryKey: readonly unknown[]
  queryFn: () => Promise<KyberSessionSummary[]>
  staleTime?: number
}
let queryClient: QueryClient
let inventoryQuery: InventoryQuery

// SSR does not run React Query's fetching effect. Keep the real QueryClient and
// query function, and advance that effect explicitly through the mocked HTTP boundary.
vi.mock('@tanstack/react-query', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@tanstack/react-query')>()
  return {
    ...actual,
    useQuery: (options: InventoryQuery) => {
      if (options.queryKey[0] !== 'kyber-sessions') return actual.useQuery(options)
      inventoryQuery = options
      const state = queryClient.getQueryState(options.queryKey)
      return {
        data: state?.data,
        isLoading: !state || state.status === 'pending',
        isError: state?.status === 'error',
        error: state?.error,
      }
    },
  }
})

let hookStates: unknown[] = []
let hookIndex = 0
const reactInternals = (
  React as unknown as {
    __CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE?: { H?: Record<string, unknown> }
  }
).__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE

function installHooks() {
  if (!reactInternals) throw new Error('React hook dispatcher unavailable')
  hookIndex = 0
  reactInternals.H = {
    useState: <T,>(value: T | (() => T)): [T, (next: T | ((previous: T) => T)) => void] => {
      const index = hookIndex++
      if (index >= hookStates.length) hookStates.push(typeof value === 'function' ? (value as () => T)() : value)
      return [hookStates[index] as T, (next) => {
        hookStates[index] = typeof next === 'function' ? (next as (previous: T) => T)(hookStates[index] as T) : next
      }]
    },
    useMemo: <T,>(factory: () => T) => factory(),
    useCallback: <T,>(callback: T) => callback,
    useRef: <T,>(value: T) => ({ current: value }),
    useEffect: () => {},
    useLayoutEffect: () => {},
    useId: () => 'test-id',
  }
}

const canonicalHarnesses = [
  'claude-code', 'claude-desktop', 'claude-cli', 'codex-desktop', 'codex-cli',
  'antigravity-cli', 'antigravity-ide', 'zcode', 'cursor-agent',
  'copilot-cli', 'copilot-vscode', 'copilot-agent', 'future-harness-v2',
]
function session(harness: string, suffix = ''): KyberSessionSummary {
  return {
    session_id: `sess-${harness}${suffix}`, harness, label: `Canonical ${harness}${suffix} session`,
    started: '2026-03-01T12:00:00.000Z', turn_count: 3, cost_usd: 0.185,
  }
}
const inventory = [...canonicalHarnesses.map((harness) => session(harness)), session('claude-code', '-second')]

function mockSessions(sessions = inventory) {
  const fetchMock = vi.fn(async (_url: string) => new Response(JSON.stringify({ sessions }), {
    status: 200, headers: { 'Content-Type': 'application/json' },
  }))
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

type ExplorerProps = Parameters<typeof ContextExplorer>[0]

function createTestQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
}

const sampleSession: KyberSessionSummary = {
  ...session('claude-code'),
  turn_count: 10,
  cost: { basis: 'published', status: 'priced', value: 0.185, currency: 'USD' },
}

/**
 * The props these assertions reach for on a rendered node.
 *
 * React 19 types `ReactElement['props']` as `unknown`, so a node found by test id has
 * nothing callable on it. Naming the handful of props the suite actually touches keeps
 * the walker typed without casting each call site past the checker.
 */
type TestNodeProps = {
  'data-testid'?: string
  children?: React.ReactNode
  onClick?: (event?: unknown) => unknown
  title?: string
}
type TestNode = React.ReactElement<TestNodeProps>

function renderExplorer(props: ExplorerProps = {}) {
  installHooks()
  return ContextExplorer(props)
}
async function loadExplorer(props: ExplorerProps = {}) {
  renderExplorer(props)
  await queryClient.ensureQueryData(inventoryQuery)
  return renderExplorer(props)
}
function html(tree: React.ReactElement, client: QueryClient = queryClient) {
  return renderToStaticMarkup(<QueryClientProvider client={client}>{tree}</QueryClientProvider>)
}
function nodes(tree: React.ReactNode, predicate: (node: TestNode) => boolean): TestNode[] {
  const found: TestNode[] = []
  const walk = (node: React.ReactNode): void => {
    if (!React.isValidElement<TestNodeProps>(node)) return
    if (predicate(node)) found.push(node)
    React.Children.forEach(node.props.children, walk)
  }
  walk(tree)
  return found
}
function clickTab(tree: React.ReactElement, harness: string) {
  const tabs = nodes(tree, (node) => node.props['data-testid'] === `provider-tab-${harness}`)
  expect(tabs, `Expected exactly one selectable tab for ${harness}`).toHaveLength(1)
  tabs[0].props.onClick!()
}
function rowIds(tree: React.ReactElement) {
  return [...html(tree).matchAll(/data-testid="agent-session-row-([^"]+)"/g)].map((match) => match[1]).sort()
}
function tabIds(tree: React.ReactElement) {
  return [...tabIdSequence(tree)].sort()
}
/** Tab ids in rendered order, so ordering is assertable rather than incidental. */
function tabIdSequence(tree: React.ReactElement) {
  return nodes(tree, (node) => node.props['data-testid']?.startsWith('provider-tab-') ?? false)
    .map((node) => node.props['data-testid']!.slice('provider-tab-'.length))
}
function openRow(tree: React.ReactElement, sessionId: string) {
  const rows = nodes(tree, (node) => node.type === AgentSessionRow)
  const row = rows.find((node) => (node.props as { s?: KyberSessionSummary }).s?.session_id === sessionId)
  expect(row, `Expected visible canonical session ${sessionId}`).toBeDefined()
  const onToggle = (row!.props as { onToggle: () => void }).onToggle
  onToggle()
}

beforeEach(() => {
  hookStates = []
  queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } })
  mockSessions()
})
afterEach(() => {
  queryClient.clear()
  vi.unstubAllGlobals()
})

describe('ContextExplorer canonical harness inventory', () => {
  it('defaults to All and shows every canonical session', async () => {
    expect(rowIds(await loadExplorer())).toEqual(inventory.map((s) => s.session_id).sort())
  })
  it('offers one tab per observed canonical ID and excludes absent static providers', async () => {
    expect(tabIds(await loadExplorer())).toEqual(['agent-all', ...canonicalHarnesses].sort())
  })
  it.each(canonicalHarnesses)('selects only exact %s rows and reports that canonical ID', async (harness) => {
    const onHarnessChange = vi.fn()
    clickTab(await loadExplorer({ onHarnessChange }), harness)
    expect(rowIds(await loadExplorer({ onHarnessChange }))).toEqual(
      inventory.filter((s) => s.harness === harness).map((s) => s.session_id).sort(),
    )
    expect(onHarnessChange).toHaveBeenCalledExactlyOnceWith(harness)
  })
  it('restores every row and reports the agent-all sentinel when All is clicked', async () => {
    const onHarnessChange = vi.fn()
    clickTab(await loadExplorer({ onHarnessChange }), 'claude-code')
    clickTab(await loadExplorer({ onHarnessChange }), 'agent-all')
    expect(rowIds(await loadExplorer({ onHarnessChange }))).toEqual(inventory.map((s) => s.session_id).sort())
    expect(onHarnessChange.mock.calls).toEqual([['claude-code'], ['agent-all']])
  })
  it('discovers through one unfiltered HTTP request and never requests provider aliases on tab switches', async () => {
    const fetchMock = mockSessions()
    let tree = await loadExplorer()
    expect(fetchMock.mock.calls).toEqual([['/api/kyber/sessions']])
    for (const harness of [...canonicalHarnesses, 'agent-all']) {
      clickTab(tree, harness)
      tree = await loadExplorer()
    }
    expect(fetchMock.mock.calls).toEqual([['/api/kyber/sessions']])
  })
  it('keeps future harness IDs selectable with their raw ID as the display fallback', async () => {
    const tree = await loadExplorer()
    const tab = nodes(tree, (node) => node.props['data-testid'] === 'provider-tab-future-harness-v2')
    expect(tab).toHaveLength(1)
    expect(renderToStaticMarkup(tab[0])).toContain('future-harness-v2')
    clickTab(tree, 'future-harness-v2')
    expect(rowIds(await loadExplorer())).toEqual(['sess-future-harness-v2'])
  })
  it('uses labels for display while keeping the canonical identity selectable', async () => {
    const tree = await loadExplorer()
    const tab = nodes(tree, (node) => node.props['data-testid'] === 'provider-tab-claude-code')
    expect(tab).toHaveLength(1)
    expect(renderToStaticMarkup(tab[0])).toContain('Claude Code')
    clickTab(tree, 'claude-code')
    expect(rowIds(await loadExplorer())).toEqual(['sess-claude-code', 'sess-claude-code-second'])
  })
  it.each(['claude-desktop', 'codex-desktop', 'antigravity-ide', 'future-harness-v2'])(
    'honors available controlled canonical selection %s', async (harness) => {
      const tree = await loadExplorer({ activeHarness: harness })
      expect(rowIds(tree)).toEqual([`sess-${harness}`])
      expect(tabIds(tree)).toEqual(['agent-all', ...canonicalHarnesses].sort())
    },
  )
  it('explicit controlled all replaces an earlier local selection', async () => {
    clickTab(await loadExplorer(), 'copilot-cli')
    expect(rowIds(await loadExplorer())).toEqual(['sess-copilot-cli'])
    expect(rowIds(await loadExplorer({ activeHarness: 'agent-all' }))).toEqual(inventory.map((s) => s.session_id).sort())
  })
  it('controlled agent-all selects the full canonical inventory', async () => {
    expect(rowIds(await loadExplorer({ activeHarness: 'agent-all' }))).toEqual(inventory.map((s) => s.session_id).sort())
  })
  it('falls back to All when a controlled harness is unavailable', async () => {
    expect(rowIds(await loadExplorer({ activeHarness: 'missing-harness' }))).toEqual(inventory.map((s) => s.session_id).sort())
  })
  it('falls back to All when a controlled harness disappears from refreshed inventory', async () => {
    expect(rowIds(await loadExplorer({ activeHarness: 'copilot-cli' }))).toEqual(['sess-copilot-cli'])
    const remaining = inventory.filter((s) => s.harness !== 'copilot-cli')
    queryClient.setQueryData(inventoryQuery.queryKey, remaining)
    const tree = renderExplorer({ activeHarness: 'copilot-cli' })
    expect(rowIds(tree)).toEqual(remaining.map((s) => s.session_id).sort())
    expect(tabIds(tree)).not.toContain('copilot-cli')
  })
  it('falls back to All when a local harness disappears from refreshed inventory', async () => {
    clickTab(await loadExplorer(), 'copilot-cli')
    expect(rowIds(await loadExplorer())).toEqual(['sess-copilot-cli'])
    const remaining = inventory.filter((s) => s.harness !== 'copilot-cli')
    queryClient.setQueryData(inventoryQuery.queryKey, remaining)
    expect(rowIds(renderExplorer())).toEqual(remaining.map((s) => s.session_id).sort())
  })
  it('keeps All and the empty message when the canonical inventory is empty', async () => {
    mockSessions([])
    const tree = await loadExplorer()
    expect(tabIds(tree)).toEqual(['agent-all'])
    expect(html(tree)).toContain('data-testid="explorer-empty"')
    expect(html(tree)).toContain('No sessions found.')
  })
  it('preserves the loading state until the canonical response arrives', () => {
    expect(html(renderExplorer())).toContain('data-testid="explorer-loading"')
  })
  it('preserves the error state when the canonical HTTP request fails', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 503 })))
    renderExplorer()
    await expect(queryClient.fetchQuery(inventoryQuery)).rejects.toThrow('Request failed (503)')
    expect(html(renderExplorer())).toContain('data-testid="explorer-error"')
    expect(html(renderExplorer())).toContain('Request failed (503)')
  })
  it('orders the tab strip deterministically regardless of store ordering', async () => {
    // The store hands back started DESC; a new session must not reshuffle the strip.
    const byAge = [...canonicalHarnesses].sort((a, b) => a.localeCompare(b)).reverse()
    mockSessions([...byAge.map((harness) => session(harness)), ...byAge.map((harness) => session(harness, '-old'))])
    const tree = await loadExplorer()
    // All is pinned first and only the harness IDs are sorted; a harness sorting
    // ahead of "agent-all" must not move All out of position.
    expect([...tabIdSequence(tree)]).toEqual(['agent-all', ...[...canonicalHarnesses].sort()])
  })
  it('never lets a stored agent-all harness mint a second All tab', async () => {
    mockSessions([...inventory, { ...session('agent-all') }])
    const tree = await loadExplorer()
    const tabs = nodes(tree, (node) => node.props['data-testid'] === 'provider-tab-agent-all')
    expect(tabs).toHaveLength(1)
    expect(tabIds(tree)).toEqual(['agent-all', ...canonicalHarnesses].sort())
  })
  it('treats the legacy all spelling as a selection that resolves to All', async () => {
    expect(rowIds(await loadExplorer({ activeHarness: 'all' }))).toEqual(inventory.map((s) => s.session_id).sort())
    expect(tabIds(await loadExplorer({ activeHarness: 'all' }))).toEqual(['agent-all', ...canonicalHarnesses].sort())
  })
  it('opens the canonical Claude Code dashboard and clears expansion when switching tabs', async () => {
    queryClient.setQueryData(['kyber-session', 'sess-claude-code'], {
      id: 'sess-claude-code', session_id: 'sess-claude-code', harness: 'claude-code',
      summary: { turn_count: 3, cost: { usd: 0.185, basis: 'published_rates', status: 'ok' } },
      turns: [], tools: [], timeline: [],
    })
    openRow(await loadExplorer(), 'sess-claude-code')
    expect(html(renderExplorer())).toContain('data-testid="agent-session-dashboard"')
    clickTab(renderExplorer(), 'agent-all')
    expect(html(await loadExplorer())).not.toContain('data-testid="agent-session-dashboard"')
    clickTab(await loadExplorer(), 'claude-code')
    openRow(await loadExplorer(), 'sess-claude-code')
    expect(html(renderExplorer())).toContain('data-testid="agent-session-dashboard"')
    clickTab(renderExplorer(), 'codex-desktop')
    expect(html(await loadExplorer())).not.toContain('data-testid="agent-session-dashboard"')
    clickTab(await loadExplorer(), 'claude-code')
    expect(html(await loadExplorer())).not.toContain('data-testid="agent-session-dashboard"')
  })
  it('expanding a canonical row mounts AgentSessionDashboard', () => {
    const detailClient = createTestQueryClient()
    detailClient.setQueryData(['kyber-session', sampleSession.session_id], {
      id: sampleSession.session_id,
      session_id: sampleSession.session_id,
      harness: sampleSession.harness,
      label: sampleSession.label,
      summary: {
        turn_count: 10,
        cost: { basis: 'published', status: 'priced', value: 0.185, currency: 'USD' },
      },
      turns: [],
      tools: [],
      timeline: [],
    })

    const markup = html(
      <AgentSessionRow s={sampleSession} open onToggle={() => {}} onSelectSession={() => {}} />,
      detailClient,
    )

    expect(markup).toContain('data-testid="agent-session-dashboard"')
    expect(markup).toContain('data-testid="overview-strip-section"')
  })
})

describe('AgentSessionRow parent navigation', () => {
  it('navigates to the canonical parent without toggling the child', () => {
    const onSelectSession = vi.fn()
    const onToggle = vi.fn()
    const tree = AgentSessionRow({
      s: { ...session('claude-code'), is_subagent: true, parent_session: 'sess-parent-001' },
      open: false, onToggle, onSelectSession,
    })
    const links = nodes(tree, (node) => node.props['data-testid'] === 'parent-session-link')
    expect(links).toHaveLength(1)
    expect(links[0].props.title).toBe('Parent session: sess-parent-001')
    const stopPropagation = vi.fn()
    links[0].props.onClick!({ stopPropagation })
    expect(stopPropagation).toHaveBeenCalledOnce()
    expect(onSelectSession).toHaveBeenCalledExactlyOnceWith('sess-parent-001')
    expect(onToggle).not.toHaveBeenCalled()
  })
})

describe('AgentSessionRow row cost cell (issue #186)', () => {
  // The cell is addressed by test id; T4 adds `data-testid="agent-session-cost"`.
  function renderCostCell(cost: KyberSessionSummary['cost'] | undefined): {
    text: string
    title: string
    className: string
  } {
    const target: KyberSessionSummary = { ...sampleSession, cost_usd: null }
    if (cost !== undefined) target.cost = cost
    else delete target.cost
    const markup = html(
      <AgentSessionRow s={target} open={false} onToggle={() => {}} onSelectSession={() => {}} />,
      createTestQueryClient(),
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
    // "no published rate" measures ~97px at text-xs; w-20 (80px) wraps. The cell
    // stays on formatCostFigure (U10) and uses w-28 (112px) plus nowrap.
    const { text, className } = renderCostCell({ basis: 'published', status: 'no_rate' })
    expect(text).toBe(formatCostFigure(normalizeCostBlock({ basis: 'published', status: 'no_rate' })))
    expect(className.split(/\s+/)).toEqual(expect.arrayContaining(['w-28', 'whitespace-nowrap', 'shrink-0']))
    expect(className.split(/\s+/)).not.toContain('w-20')
  })
})

describe('ContextExplorer removed surface', () => {
  it('does not export a tree-detail or context-window-toggle component', () => {
    expect(ContextExplorerModule).not.toHaveProperty('TreeTable')
    expect(ContextExplorerModule).not.toHaveProperty('SessionDetails')
    expect(ContextExplorerModule).not.toHaveProperty('SessionDetailsBoundary')
  })
  it('never requests the context-tree endpoint', async () => {
    // Behavioural guard through the mocked HTTP boundary. Asserting that five literal
    // strings are absent from the source file would pass on a renamed helper or a
    // moved fetch while the feature it polices came straight back.
    const fetchMock = mockSessions()
    await loadExplorer()
    expect(fetchMock.mock.calls.map(([url]) => String(url))).not.toContain('/api/context/tree')
  })
})
