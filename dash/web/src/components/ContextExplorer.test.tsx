import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as React from 'react'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import * as ContextExplorerModule from './ContextExplorer.js'
import type { KyberSessionSummary } from '../lib/kyberApi.js'
import { formatCostFigure, normalizeCostBlock } from './SessionCostPanel.js'

const { ContextExplorer, AgentSessionRow, getAgentHarnessFilter, PROVIDERS } = ContextExplorerModule

let hookStates: unknown[] = []
let hookIndex = 0

function clearHooks() {
  hookStates = []
  hookIndex = 0
}

const reactInternals = (
  React as unknown as {
    __CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE?: { H?: Record<string, unknown> }
  }
).__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE

if (reactInternals) {
  reactInternals.H = {
    useState: <T,>(value: T | (() => T)): [T, (next: T | ((previous: T) => T)) => void] => {
      const index = hookIndex++
      if (index >= hookStates.length) {
        hookStates.push(typeof value === 'function' ? (value as () => T)() : value)
      }
      return [
        hookStates[index] as T,
        (next) => {
          hookStates[index] =
            typeof next === 'function' ? (next as (previous: T) => T)(hookStates[index] as T) : next
        },
      ]
    },
    useMemo: <T,>(factory: () => T) => factory(),
    useCallback: <T,>(callback: T) => callback,
    useRef: <T,>(value: T) => ({ current: value }),
    useEffect: () => {},
    useLayoutEffect: () => {},
    useId: () => 'test-id',
  }
}

function renderHtml(element: React.ReactElement): string {
  hookIndex = 0
  return renderToStaticMarkup(element)
}

function createTestQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
}

const sampleSession: KyberSessionSummary = {
  session_id: 'sess-canonical-001',
  harness: 'claude',
  label: 'Canonical Claude session',
  is_subagent: false,
  parent_session: null,
  started: '2026-03-01T12:00:00.000Z',
  turn_count: 10,
  cost_usd: 0.185,
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
  [key: string]: unknown
}

type TestNode = React.ReactElement<TestNodeProps>

describe('ContextExplorer: canonical session-list contract', () => {
  const source = readFileSync(fileURLToPath(new URL('./ContextExplorer.tsx', import.meta.url)), 'utf8')

  beforeEach(clearHooks)

  it('routes every provider through fetchKyberSessions and AgentSessionRow', () => {
    expect(source).toContain("import { fetchKyberSessions")
    expect(source).toContain('queryFn: () => fetchKyberSessions(agentFilter)')
    expect(source).toContain('<AgentSessionRow')
    expect(PROVIDERS.map((provider) => provider.key)).toEqual([
      'agent-all',
      'claude',
      'codex',
      'antigravity',
      'copilot-cli',
      'copilot-vscode',
      'copilot-agent',
      'pi',
      'opencode',
      'kilo-code',
      'cursor',
    ])
    expect(PROVIDERS.map((provider) => getAgentHarnessFilter(provider.key))).toEqual([
      null,
      'claude',
      'codex',
      'gemini',
      'copilot-cli',
      'copilot',
      'copilot',
      'pi',
      'opencode',
      'kilo-code',
      'cursor',
    ])
  })

  it('renders the Claude provider as a canonical agent session row', () => {
    const queryClient = createTestQueryClient()
    queryClient.setQueryData(['kyber-sessions', 'claude'], [sampleSession])

    const html = renderHtml(
      <QueryClientProvider client={queryClient}>
        <ContextExplorer activeHarness="claude" />
      </QueryClientProvider>,
    )

    expect(html).toContain('data-testid="agent-session-row-sess-canonical-001"')
    expect(html).toContain('Canonical Claude session')
    expect(html).toContain('claude')
  })

  it('expanding a canonical row mounts AgentSessionDashboard', () => {
    const queryClient = createTestQueryClient()
    queryClient.setQueryData(['kyber-session', sampleSession.session_id], {
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

    const html = renderHtml(
      <QueryClientProvider client={queryClient}>
        <AgentSessionRow s={sampleSession} open onToggle={() => {}} onSelectSession={() => {}} />
      </QueryClientProvider>,
    )

    expect(html).toContain('data-testid="agent-session-dashboard"')
    expect(html).toContain('data-testid="overview-strip-section"')
  })

  it('navigates to a parent canonical session from the row link', () => {
    const onSelectSession = vi.fn()
    const tree = AgentSessionRow({
      s: { ...sampleSession, is_subagent: true, parent_session: 'sess-parent-001' },
      open: false,
      onToggle: () => {},
      onSelectSession,
    })

    let found: TestNode | null = null
    const walk = (node: unknown): void => {
      if (!React.isValidElement(node) || found) return
      const props = node.props as TestNodeProps | undefined
      if (props?.['data-testid'] === 'parent-session-link') {
        found = node as TestNode
        return
      }
      React.Children.forEach(props?.children, walk)
    }
    walk(tree)

    // Read through a fresh binding: the assignment above happens inside a callback, which
    // control-flow analysis cannot follow, so `found` stays narrowed to `null` at its own
    // declaration.
    const parentLink: TestNode | null = found
    expect(parentLink).not.toBeNull()
    expect(parentLink!.props.title).toContain('Parent session: sess-parent-001')
    const stopPropagation = vi.fn()
    parentLink!.props.onClick!({ stopPropagation })
    expect(stopPropagation).toHaveBeenCalledOnce()
    expect(onSelectSession).toHaveBeenCalledWith('sess-parent-001')
  })

  describe('row cost cell (issue #186)', () => {
    // The cell is addressed by test id; T4 adds `data-testid="agent-session-cost"`.
    function renderCostCell(cost: KyberSessionSummary['cost'] | undefined): { text: string; title: string } {
      const session: KyberSessionSummary = { ...sampleSession, cost_usd: null }
      if (cost !== undefined) session.cost = cost
      else delete session.cost
      const html = renderHtml(
        <QueryClientProvider client={createTestQueryClient()}>
          <AgentSessionRow s={session} open={false} onToggle={() => {}} onSelectSession={() => {}} />
        </QueryClientProvider>,
      )
      const match = /<span([^>]*data-testid="agent-session-cost"[^>]*)>([\s\S]*?)<\/span>/.exec(html)
      expect(match, 'row must render an element with data-testid="agent-session-cost"').not.toBeNull()
      const title = /title="([^"]*)"/.exec(match![1]!)?.[1] ?? ''
      return { text: match![2]!, title }
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
  })

  it('does not retain a tree-detail or context-window-toggle path', () => {
    expect(ContextExplorerModule).not.toHaveProperty('TreeTable')
    expect(ContextExplorerModule).not.toHaveProperty('SessionDetails')
    expect(ContextExplorerModule).not.toHaveProperty('SessionDetailsBoundary')
    expect(source).not.toContain('fetchContextTree')
    expect(source).not.toContain('/api/context/tree')
    expect(source).not.toContain("'context-tree'")
    expect(source).not.toContain('Live window')
    expect(source).not.toContain('Full history')
  })
})
