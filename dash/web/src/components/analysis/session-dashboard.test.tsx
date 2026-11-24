import { beforeEach, describe, expect, it } from 'vitest'
import * as React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { AgentSessionRow, ContextExplorer, scopeOf } from '../ContextExplorer.js'
import { KYBER_SESSIONS_PAGE_LIMIT, type KyberPage, type KyberSessionSummary } from '../../lib/kyberApi.js'

const hookStates: unknown[] = []
let hookIndex = 0

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

/** One page as `GET /api/kyber/sessions` answers it: the rows plus their window. */
function sessionsPageEnvelope(rows: KyberSessionSummary[]): KyberPage<KyberSessionSummary> {
  return { entries: rows, data: rows, total: rows.length, page: 1, limit: KYBER_SESSIONS_PAGE_LIMIT }
}

const claudeSession: KyberSessionSummary = {
  session_id: 'sess-claude-canonical',
  harness: 'claude-code',
  label: 'Canonical Claude session',
  started: '2026-03-01T12:00:00.000Z',
  turn_count: 3,
  cost_usd: 0.185,
}

describe('ContextExplorer Claude Code canonical-session integration', () => {
  beforeEach(() => {
    hookStates.length = 0
    hookIndex = 0
  })

  // `all` is the shell's unscoped spelling; `agent-all` is the legacy one. Both
  // reach the same inventory, because the harness scope lives in the shell now.
  it.each(['all', 'agent-all', 'claude-code'])('renders Claude Code in %s and expands AgentSessionDashboard', (activeHarness) => {
    const queryClient = createQueryClient()
    // The explorer reads the paged route, keyed by the resolved harness scope
    // (`agent-all` and `all` both resolve to `all`), the committed search text
    // and the page.
    queryClient.setQueryData(
      ['kyber-sessions', scopeOf(activeHarness), '', 1],
      sessionsPageEnvelope([claudeSession]),
    )
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
        <ContextExplorer activeHarness={activeHarness} />
      </QueryClientProvider>,
    )
    expect(explorerHtml).toContain('data-testid="agent-session-row-sess-claude-canonical"')
    expect(explorerHtml).toContain('Canonical Claude session')
    // The one harness selector belongs to the shell, so the explorer must not
    // mint a second strip of its own over the same paged sessions cache.
    expect(explorerHtml).not.toMatch(/data-testid="provider-tab-/)

    const expandedHtml = renderHtml(
      <QueryClientProvider client={queryClient}>
        <AgentSessionRow s={claudeSession} open onToggle={() => {}} onSelectSession={() => {}} />
      </QueryClientProvider>,
    )
    expect(expandedHtml).toContain('data-testid="agent-session-dashboard"')
    expect(expandedHtml).not.toContain('data-testid="tree-table"')
  })
})
