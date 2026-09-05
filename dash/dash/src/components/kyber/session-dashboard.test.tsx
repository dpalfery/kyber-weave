import { describe, expect, it } from 'vitest'
import * as React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { AgentSessionRow, ContextExplorer } from '../ContextExplorer'
import type { KyberSessionSummary } from '../../lib/kyberApi'

let hookStates: unknown[] = []
let hookIndex = 0

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
      return [hookStates[index] as T, () => {}]
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

function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
}

const claudeSession: KyberSessionSummary = {
  session_id: 'sess-claude-canonical',
  harness: 'claude',
  label: 'Canonical Claude session',
  started: '2026-03-01T12:00:00.000Z',
  turn_count: 3,
  cost_usd: 0.185,
}

describe('ContextExplorer Claude canonical-session integration', () => {
  it('renders Claude from fetchKyberSessions data and expands AgentSessionDashboard', () => {
    const queryClient = createQueryClient()
    queryClient.setQueryData(['kyber-sessions', 'claude'], [claudeSession])
    queryClient.setQueryData(['kyber-session', claudeSession.session_id], {
      id: claudeSession.session_id,
      session_id: claudeSession.session_id,
      harness: claudeSession.harness,
      label: claudeSession.label,
      summary: { turn_count: 3, cost: { usd: 0.185, basis: 'published_rates', status: 'ok' } },
      turns: [],
      tools: [],
      timeline: [],
    })

    const explorerHtml = renderHtml(
      <QueryClientProvider client={queryClient}>
        <ContextExplorer activeHarness="claude" />
      </QueryClientProvider>,
    )
    expect(explorerHtml).toContain('data-testid="agent-session-row-sess-claude-canonical"')
    expect(explorerHtml).toContain('Canonical Claude session')

    const expandedHtml = renderHtml(
      <QueryClientProvider client={queryClient}>
        <AgentSessionRow s={claudeSession} open onToggle={() => {}} onSelectSession={() => {}} />
      </QueryClientProvider>,
    )
    expect(expandedHtml).toContain('data-testid="agent-session-dashboard"')
    expect(expandedHtml).not.toContain('data-testid="tree-table"')
  })
})
