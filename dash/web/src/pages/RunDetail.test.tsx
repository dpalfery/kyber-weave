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
