import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as React from 'react'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import * as ContextExplorerModule from './ContextExplorer'
import type { KyberSessionSummary } from '../lib/kyberApi'

const { ContextExplorer, AgentSessionRow, getAgentHarnessFilter, PROVIDERS } = ContextExplorerModule

let hookStates: unknown[] = []
let hookIndex = 0

function clearHooks() {
  hookStates = []
  hookIndex = 0
}

const reactInternals = (
  React as unknown as {
    __CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE?: { H?: any }
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
}

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
      summary: { turn_count: 10, cost: { usd: 0.185, basis: 'published_rates', status: 'ok' } },
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

    let parentLink: any = null
    const walk = (node: any) => {
      if (!node || parentLink) return
      if (node.props?.['data-testid'] === 'parent-session-link') {
        parentLink = node
        return
      }
      React.Children.forEach(node.props?.children, walk)
    }
    walk(tree)

    expect(parentLink?.props.title).toContain('Parent session: sess-parent-001')
    const stopPropagation = vi.fn()
    parentLink.props.onClick({ stopPropagation })
    expect(stopPropagation).toHaveBeenCalledOnce()
    expect(onSelectSession).toHaveBeenCalledWith('sess-parent-001')
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
