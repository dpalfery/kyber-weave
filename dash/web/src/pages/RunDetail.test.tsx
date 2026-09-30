import { describe, it, expect, beforeEach } from 'vitest'
import * as React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { RunDetail } from './RunDetail.js'
import type { KyberRunDetail } from '../lib/kyberApi.js'

// Ensure minimal browser environment shims in Node
if (typeof (globalThis as { window?: unknown }).window === 'undefined') {
  ;(globalThis as { window?: unknown }).window = {
    matchMedia: () => ({
      matches: false,
      addEventListener: () => {},
      removeEventListener: () => {},
    }),
    localStorage: {
      getItem: () => null,
      setItem: () => {},
      removeItem: () => {},
    },
  }
}

if (typeof (globalThis as { document?: unknown }).document === 'undefined') {
  ;(globalThis as { document?: unknown }).document = {
    documentElement: {
      classList: {
        contains: () => false,
        toggle: () => false,
        add: () => {},
        remove: () => {},
      },
    },
  }
}

// React 19 Test Dispatcher
let hookStates: unknown[] = []
let hookIndex = 0

function clearHooks() {
  hookStates = []
  hookIndex = 0
}

const reactInternals = (
  React as unknown as {
    __CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE?: {
      H?: Record<string, unknown>
    }
  }
).__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE

if (reactInternals) {
  reactInternals.H = {
    useState: <T,>(v: T | (() => T)): [T, (val: T | ((prev: T) => T)) => void] => {
      const idx = hookIndex++
      if (idx >= hookStates.length) {
        hookStates.push(typeof v === 'function' ? (v as () => T)() : v)
      }
      const setter = (next: T | ((prev: T) => T)) => {
        hookStates[idx] = typeof next === 'function' ? (next as (prev: T) => T)(hookStates[idx] as T) : next
      }
      return [hookStates[idx] as T, setter]
    },
    useMemo: <T,>(fn: () => T) => fn(),
    useCallback: <T,>(fn: T) => fn,
    useRef: <T,>(v: T) => ({ current: v }),
    useEffect: () => {},
    useLayoutEffect: () => {},
    useId: () => 'test-id',
  }
}

function renderHtml(element: React.ReactElement | null | undefined): string {
  if (element == null) return ''
  hookIndex = 0
  return renderToStaticMarkup(element)
}

function createTestQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
        gcTime: 0,
      },
    },
  })
}

// Issue #184: transport is 0-based `turnIndex` (URL, router, content route) while
// every human-facing turn label is 1-based (`Turn #N`).
describe('RunDetail turns table numbering', () => {
  beforeEach(() => {
    clearHooks()
  })

  const baseRun: KyberRunDetail = {
    runId: 'run-184',
    harness: 'copilot',
    groupingBasis: 'derived',
    executionTree: [],
    executions: [
      {
        executionId: 'exec-1',
        runId: 'run-184',
        harness: 'copilot',
        isRoot: true,
      },
    ],
    findings: [],
  }

  it('renders a 1-based label for a measured turn row', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <RunDetail
          runId="run-184"
          initialRun={{ ...baseRun, turns: [{ turnIndex: 5, model: 'm', tokens: 1200 }] }}
        />
      </QueryClientProvider>,
    )

    expect(html).toContain('Turn #6')
    expect(html).not.toContain('>#5<')
  })

  it('keeps the 0-based transport identity on the row drill control', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <RunDetail
          runId="run-184"
          initialRun={{ ...baseRun, turns: [{ turnIndex: 5, model: 'm', tokens: 1200 }] }}
        />
      </QueryClientProvider>,
    )

    // Navigation still carries the raw 0-based index to `/turn/5`.
    expect(html).toContain('data-testid="turn-row-5"')
    expect(html).toContain('data-testid="drill-turn-5"')
  })

  it('renders 1-based labels for synthesized execution turns', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['kyber-execution-session', 'sess-1'], { turnCount: 3 })
    const runWithSession: KyberRunDetail = {
      ...baseRun,
      executions: [
        {
          executionId: 'exec-1',
          runId: 'run-184',
          sessionId: 'sess-1',
          harness: 'copilot',
          isRoot: true,
        },
      ],
    }
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <RunDetail runId="run-184" initialRun={runWithSession} />
      </QueryClientProvider>,
    )

    expect(html).toContain('Turn #1')
    expect(html).toContain('Turn #2')
    expect(html).toContain('Turn #3')
    expect(html).toContain('data-testid="drill-turn-0"')
    expect(html).toContain('data-testid="drill-turn-2"')
  })
})

// Issue #183: the turn table must render served measured figures (not dashes
// beside measured data), and the scorecard must render the served run-scoped
// dimensions — with honest absence when the server sent none.
describe('RunDetail measured figures (issue #183)', () => {
  beforeEach(() => {
    clearHooks()
  })

  const measuredRun: KyberRunDetail = {
    runId: 'run-183',
    harness: 'copilot',
    groupingBasis: 'explicit',
    turnCount: 2,
    totalInput: 3000,
    costUsd: 0.015,
    executionTree: [],
    executions: [
      {
        executionId: 'exec-1',
        runId: 'run-183',
        harness: 'copilot',
        isRoot: true,
      },
    ],
    findings: [],
    turns: [
      {
        turnIndex: 0,
        executionId: 'exec-1',
        model: 'gpt-4o',
        tokens: 1100,
        inputTokens: 1000,
        outputTokens: 100,
        contextPressure: 0.05,
        cacheHitRatio: 0.2,
        costUsd: 0.01,
      },
      { turnIndex: 1, executionId: 'exec-1' },
    ],
    scorecard: {
      cacheEfficiency: { value: 0.3333, display: '33%' },
      contextHygiene: { value: 0.05, display: '5%' },
    },
  }

  it('renders measured turn figures where the server sent them', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <RunDetail runId="run-183" initialRun={measuredRun} />
      </QueryClientProvider>,
    )

    expect(html).toContain('gpt-4o')
    expect(html).toContain('5%')
    expect(html).toContain('20%')
    expect(html).toContain('$0.010')
    // The unmeasured second row keeps its dashes — honesty both ways.
    expect(html).toContain('Turn #2')
  })

  it('renders the served run-scoped scorecard as measured', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <RunDetail runId="run-183" initialRun={measuredRun} />
      </QueryClientProvider>,
    )

    expect(html).toContain('data-testid="dimension-value"')
    expect(html).toContain('33%')
  })

  it('states honest absence without the harness-telemetry claim when unscored', () => {
    const qc = createTestQueryClient()
    const { scorecard: _omitted, ...unscored } = measuredRun
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <RunDetail runId="run-183" initialRun={{ ...unscored, turns: undefined }} />
      </QueryClientProvider>,
    )

    expect(html).toContain('projection has not scored it yet')
    expect(html).not.toContain('does not export cache')
  })
})

// Review follow-up (Copilot C7, Kilo K3): a priced zero cost is a
// measurement, not a missing figure — and a partial run total says so.
describe('RunDetail cost honesty', () => {
  beforeEach(() => {
    clearHooks()
  })

  const pricedZeroRun: KyberRunDetail = {
    runId: 'run-zero',
    harness: 'copilot',
    groupingBasis: 'explicit',
    costUsd: 0,
    executionTree: [],
    executions: [
      {
        executionId: 'exec-1',
        runId: 'run-zero',
        harness: 'copilot',
        isRoot: true,
      },
    ],
    findings: [],
    turns: [{ turnIndex: 0, model: 'gpt-4o', tokens: 100, costUsd: 0 }],
  }

  it('renders a priced zero turn cost as $0.00, not a dash', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <RunDetail runId="run-zero" initialRun={pricedZeroRun} />
      </QueryClientProvider>,
    )

    expect(html).toContain('$0.00')
  })

  it('marks a partial run total instead of presenting it as complete', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <RunDetail
          runId="run-zero"
          initialRun={{ ...pricedZeroRun, costUsd: 0.015, costStatus: 'partial' }}
        />
      </QueryClientProvider>,
    )

    expect(html).toContain('partial')
  })

  // Re-review #2 (Kilo 4): `partial: true` alone — the gap is turns or
  // tokens, not cost — still shows the visible marker.
  it('shows the visible marker for a non-cost partial', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <RunDetail
          runId="run-zero"
          initialRun={{
            ...pricedZeroRun,
            costUsd: 0.015,
            partial: true,
            partialFields: ['turnCount'],
          }}
        />
      </QueryClientProvider>,
    )

    expect(html).toContain('data-testid="secondary-cost-partial"')
  })
})

// Review re-review (Kilo 5): the partial marker must be visible text, not a
// hover-only title.
describe('RunDetail partial visibility', () => {
  beforeEach(() => {
    clearHooks()
  })

  it('shows a visible partial marker for a partial run total', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <RunDetail
          runId="run-zero"
          initialRun={{
            runId: 'run-zero',
            harness: 'copilot',
            groupingBasis: 'explicit',
            costUsd: 0.015,
            costStatus: 'partial',
            executionTree: [],
            executions: [],
            findings: [],
          }}
        />
      </QueryClientProvider>,
    )

    expect(html).toContain('data-testid="secondary-cost-partial"')
  })
})
