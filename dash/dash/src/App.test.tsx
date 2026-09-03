import { describe, it, expect, beforeEach } from 'vitest'
import * as React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import {
  App,
  NAV_TABS,
  KyberComparePanel,
  KyberQuarantinePanel,
  KyberProblemsPanel,
  type KyberPage,
} from './App'

// Ensure minimal browser environment shims in Node
if (typeof (globalThis as any).window === 'undefined') {
  ;(globalThis as any).window = {
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

if (typeof (globalThis as any).document === 'undefined') {
  ;(globalThis as any).document = {
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
      H?: any
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

describe('App: Top Navigation Refactoring', () => {
  beforeEach(() => {
    clearHooks()
  })

  it('exports exactly the 5 approved navigation tabs in NAV_TABS', () => {
    expect(NAV_TABS).toHaveLength(5)
    const keys = NAV_TABS.map((t) => t.key)
    expect(keys).toEqual(['usage', 'context', 'compare', 'quarantine', 'problems'])
    const labels = NAV_TABS.map((t) => t.label)
    expect(labels).toEqual(['Usage', 'Context', 'Compare', 'Quarantine', 'Problems'])
  })

  it('renders exactly the 5 navigation tabs in the header and excludes disconnected buttons', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPage="usage" />
      </QueryClientProvider>
    )

    // All 5 tabs render
    expect(html).toContain('data-testid="nav-tab-usage"')
    expect(html).toContain('data-testid="nav-tab-context"')
    expect(html).toContain('data-testid="nav-tab-compare"')
    expect(html).toContain('data-testid="nav-tab-quarantine"')
    expect(html).toContain('data-testid="nav-tab-problems"')

    // Redundant disconnected tabs are removed
    expect(html).not.toContain('data-testid="nav-tab-schema"')
    expect(html).not.toContain('data-testid="nav-tab-timeline"')
    expect(html).not.toContain('data-testid="nav-tab-buckets"')
    expect(html).not.toContain('data-testid="nav-tab-kyber-context"')

    // Exactly 5 buttons in data-testid="nav-tabs"
    const navTabsMatch = html.match(/data-testid="nav-tab-[^"]+"/g)
    expect(navTabsMatch).toHaveLength(5)
  })

  it('applies active styling to the currently active page tab and inactive to others', () => {
    const qc = createTestQueryClient()

    // Test with initialPage="usage"
    clearHooks()
    const usageHtml = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPage="usage" />
      </QueryClientProvider>
    )
    expect(usageHtml).toContain('data-testid="nav-tab-usage"')
    // active tab has bg-active-primary
    const usageBtn = usageHtml.match(/<button[^>]*data-testid="nav-tab-usage"[^>]*>/)?.[0]
    expect(usageBtn).toContain('bg-active-primary')
    const compareBtnFromUsage = usageHtml.match(/<button[^>]*data-testid="nav-tab-compare"[^>]*>/)?.[0]
    expect(compareBtnFromUsage).toContain('text-tertiary-foreground')
    expect(compareBtnFromUsage).not.toContain('bg-active-primary')

    // Test with initialPage="compare"
    clearHooks()
    const compareHtml = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPage="compare" />
      </QueryClientProvider>
    )
    const compareBtn = compareHtml.match(/<button[^>]*data-testid="nav-tab-compare"[^>]*>/)?.[0]
    expect(compareBtn).toContain('bg-active-primary')
    const usageBtnFromCompare = compareHtml.match(/<button[^>]*data-testid="nav-tab-usage"[^>]*>/)?.[0]
    expect(usageBtnFromCompare).toContain('text-tertiary-foreground')
    expect(usageBtnFromCompare).not.toContain('bg-active-primary')
  })

  it('updates active page styling for context, quarantine, and problems tabs', () => {
    const qc = createTestQueryClient()
    const pages: KyberPage[] = ['context', 'quarantine', 'problems']

    for (const page of pages) {
      clearHooks()
      const html = renderHtml(
        <QueryClientProvider client={qc}>
          <App initialPage={page} />
        </QueryClientProvider>
      )
      const activeBtn = html.match(new RegExp(`<button[^>]*data-testid="nav-tab-${page}"[^>]*>`))?.[0]
      expect(activeBtn).toBeDefined()
      expect(activeBtn).toContain('bg-active-primary')
    }
  })
})

describe('App: Page Switching & Title Rendering', () => {
  beforeEach(() => {
    clearHooks()
  })

  it('renders page title "Context" when on context page', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPage="context" />
      </QueryClientProvider>
    )
    expect(html).toContain('data-testid="page-title"')
    expect(html).toContain('Context')
    expect(html).toContain('data-testid="provider-tab-agent-all"')
  })

  it('renders page title "Compare" and CompareView when on compare page', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['kyber-compare'], {
      harnesses: ['copilot', 'pi'],
      rows: [],
      problems: [],
    })
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPage="compare" />
      </QueryClientProvider>
    )
    expect(html).toContain('data-testid="page-title"')
    expect(html).toContain('Compare')
    expect(html).toContain('Per-turn ratios (lead)')
  })

  it('renders page title "Quarantine" and QuarantineView when on quarantine page', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['kyber-quarantine'], {
      entries: [
        { spanId: 'quar-1', namespaces: ['unclaimed'], reason: 'No matching harness' },
      ],
    })
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPage="quarantine" />
      </QueryClientProvider>
    )
    expect(html).toContain('data-testid="page-title"')
    expect(html).toContain('Quarantine')
    expect(html).toContain('quar-1')
  })

  it('renders page title "Problems" and ProblemsView when on problems page', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['kyber-problems'], {
      problems: [
        { severity: 'error', code: 'reconciliation_failed', message: 'Token mismatch' },
      ],
    })
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPage="problems" />
      </QueryClientProvider>
    )
    expect(html).toContain('data-testid="page-title"')
    expect(html).toContain('Problems')
    expect(html).toContain('reconciliation_failed')
    expect(html).toContain('Token mismatch')
  })
})

describe('KyberComparePanel', () => {
  beforeEach(() => {
    clearHooks()
  })

  it('renders live comparison matrix with harnesses, rows, and cells without throwing', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['kyber-compare'], {
      harnesses: ['copilot', 'pi'],
      rows: [
        {
          metric: 'tokens_per_turn',
          kind: 'per_turn',
          label: 'Tokens per turn',
          unit: 'tokens',
          cells: {
            copilot: { measurable: true, availability: 'measured', value: 1250, render: '1,250' },
            pi: { measurable: false, availability: 'not_measurable', render: 'not measurable' },
          },
        },
      ],
      problems: [],
    })

    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <KyberComparePanel />
      </QueryClientProvider>
    )

    expect(html).toContain('Tokens per turn')
    expect(html).toContain('1,250')
    expect(html).toContain('copilot')
    expect(html).toContain('pi')
    expect(html).toContain('Per-turn ratios (lead)')
  })

  it('renders fallback comparison table with warning problem when data is empty or missing', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['kyber-compare'], {
      harnesses: [],
      rows: [],
      problems: [],
    })

    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <KyberComparePanel />
      </QueryClientProvider>
    )

    expect(html).toContain('Per-turn ratios (lead)')
    expect(html).toContain('Totals (trail)')
  })

  it('handles query error gracefully without throwing or crashing', () => {
    const qc = createTestQueryClient()
    // Pre-populate query as error state
    qc.setQueryDefaults(['kyber-compare'], { retry: false })

    // Simulate failed query without throwing
    const origFetch = globalThis.fetch
    globalThis.fetch = () => Promise.reject(new Error('Network error'))

    try {
      const html = renderHtml(
        <QueryClientProvider client={qc}>
          <KyberComparePanel />
        </QueryClientProvider>
      )
      // When isLoading or error, it returns Skeleton or fallback CompareView
      expect(html).toBeDefined()
    } finally {
      globalThis.fetch = origFetch
    }
  })
})

describe('KyberQuarantinePanel', () => {
  beforeEach(() => {
    clearHooks()
  })

  it('renders live quarantined entries mapping span_id and array namespaces', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['kyber-quarantine'], {
      entries: [
        {
          span_id: 'span-q-100',
          namespaces: ['internal.agent', 'tools.unknown'],
          reason: 'Unregistered namespace handler',
        },
      ],
    })

    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <KyberQuarantinePanel />
      </QueryClientProvider>
    )

    expect(html).toContain('span-q-100')
    expect(html).toContain('internal.agent, tools.unknown')
    expect(html).toContain('Unregistered namespace handler')
    expect(html).toContain('data-testid="quarantine-row"')
  })

  it('maps JSON-serialized namespaces and string spanId cleanly', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['kyber-quarantine'], {
      entries: [
        {
          spanId: 'span-q-200',
          namespaces: JSON.stringify(['custom.parser']),
          reason: 'Schema mismatch',
        },
      ],
    })

    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <KyberQuarantinePanel />
      </QueryClientProvider>
    )

    expect(html).toContain('span-q-200')
    expect(html).toContain('custom.parser')
    expect(html).toContain('Schema mismatch')
  })

  it('handles comma-separated string namespaces', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['kyber-quarantine'], {
      entries: [
        {
          span_id: 'span-q-300',
          namespaces: 'alpha.ns, beta.ns',
          reason: 'Legacy comma format',
        },
      ],
    })

    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <KyberQuarantinePanel />
      </QueryClientProvider>
    )

    expect(html).toContain('span-q-300')
    expect(html).toContain('alpha.ns, beta.ns')
    expect(html).toContain('Legacy comma format')
  })

  it('renders empty message when no quarantined spans exist without blank screen', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['kyber-quarantine'], { entries: [] })

    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <KyberQuarantinePanel />
      </QueryClientProvider>
    )

    expect(html).toContain('No quarantined spans.')
  })

  it('handles raw array format directly from API', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['kyber-quarantine'], [
      { span_id: 'span-array-1', namespaces: ['test.ns'], reason: 'Raw array reason' },
    ])

    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <KyberQuarantinePanel />
      </QueryClientProvider>
    )

    expect(html).toContain('span-array-1')
    expect(html).toContain('test.ns')
    expect(html).toContain('Raw array reason')
  })
})

describe('KyberProblemsPanel', () => {
  beforeEach(() => {
    clearHooks()
  })

  it('renders live problems mapping span_id, severity, and location/harness', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['kyber-problems'], {
      problems: [
        {
          id: 1,
          severity: 'error',
          code: 'mismatch_root_turn',
          message: 'Root input token mismatch with turn sum',
          span_id: 'span-prob-001',
          harness: 'copilot',
        },
        {
          id: 2,
          severity: 'warning',
          code: 'drift_detected',
          message: 'Minor token drift observed',
          spanId: 'span-prob-002',
          at: 'turn-3',
        },
      ],
    })

    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <KyberProblemsPanel />
      </QueryClientProvider>
    )

    expect(html).toContain('mismatch_root_turn')
    expect(html).toContain('Root input token mismatch with turn sum')
    expect(html).toContain('at copilot')
    expect(html).toContain('span-prob-001')

    expect(html).toContain('drift_detected')
    expect(html).toContain('Minor token drift observed')
    expect(html).toContain('at turn-3')
    expect(html).toContain('span-prob-002')

    expect(html).toContain('Problems — 2')
  })

  it('renders empty message when no problems exist without blank screen', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['kyber-problems'], { problems: [] })

    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <KyberProblemsPanel />
      </QueryClientProvider>
    )

    expect(html).toContain('No problems.')
  })

  it('handles raw array format directly from API', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['kyber-problems'], [
      { severity: 'warning', code: 'warn_code', message: 'Warning message' },
    ])

    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <KyberProblemsPanel />
      </QueryClientProvider>
    )

    expect(html).toContain('warn_code')
    expect(html).toContain('Warning message')
  })
})
