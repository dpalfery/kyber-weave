import { describe, it, expect, beforeEach } from 'vitest'
import * as React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import {
  App,
  harnessTabsFrom,
  NAV_TABS,
  KyberQuarantinePanel,
  KyberProblemsPanel,
  type KyberPage,
} from './App.js'
import type { KyberHarnessSummary } from '@/lib/kyberApi'

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

  it('exports exactly the 3 header navigation tabs in NAV_TABS (requirement 2.9)', () => {
    expect(NAV_TABS).toHaveLength(3)
    const keys = NAV_TABS.map((t) => t.key)
    expect(keys).toEqual(['context-doctor', 'quarantine', 'problems'])
    const labels = NAV_TABS.map((t) => t.label)
    expect(labels).toEqual(['Context Doctor', 'Quarantine', 'Problems'])
  })

  it('renders exactly the 3 header tabs and spine rail destinations, excluding Compare as a peer tab', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPage="context-doctor" />
      </QueryClientProvider>
    )

    expect(html).toContain('data-testid="nav-tab-context-doctor"')
    expect(html).not.toContain('data-testid="nav-tab-usage"')
    expect(html).toContain('data-testid="nav-tab-quarantine"')
    expect(html).toContain('data-testid="nav-tab-problems"')
    expect(html).not.toContain('data-testid="nav-tab-compare"')
    expect(html).not.toContain('data-testid="nav-tab-context"')
    expect(html).not.toContain('data-testid="nav-tab-schema"')
    expect(html).not.toContain('data-testid="nav-tab-timeline"')
    expect(html).not.toContain('data-testid="nav-tab-buckets"')
    expect(html).not.toContain('data-testid="nav-tab-kyber-context"')

    const navTabsMatch = html.match(/data-testid="nav-tab-[^"]+"/g)
    expect(navTabsMatch).toHaveLength(3)

    expect(html).toContain('data-testid="nav-rail-context-doctor"')
    expect(html).toContain('data-testid="nav-rail-sessions"')
    expect(html).toContain('data-testid="nav-rail-compare"')
    expect((html.match(/data-testid="harness-selector"/g) ?? [])).toHaveLength(1)
  })

  it('applies active styling to the currently active page tab and inactive to others', () => {
    const qc = createTestQueryClient()

    clearHooks()
    const quarantineHtml = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPage="quarantine" />
      </QueryClientProvider>
    )
    const quarantineBtn = quarantineHtml.match(/<button[^>]*data-testid="nav-tab-quarantine"[^>]*>/)?.[0]
    expect(quarantineBtn).toContain('bg-active-primary')
    const doctorBtnFromQuarantine = quarantineHtml.match(/<button[^>]*data-testid="nav-tab-context-doctor"[^>]*>/)?.[0]
    expect(doctorBtnFromQuarantine).toContain('text-tertiary-foreground')
    expect(doctorBtnFromQuarantine).not.toContain('bg-active-primary')
  })

  it('updates active page styling for Context Doctor, quarantine, and problems tabs', () => {
    const qc = createTestQueryClient()
    const pages: KyberPage[] = ['context-doctor', 'quarantine', 'problems']

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

describe('App: Harness selector storage filters', () => {
  // The strip used to be a hardcoded three, so a machine collecting Codex,
  // Cursor, OpenCode or Antigravity ingested that data and then offered no tab
  // to reach it. It is now derived from the harnesses the store actually holds.
  const summary = (harness: string, sampleCount: number): KyberHarnessSummary => ({
    harness,
    sampleCount,
    measurability: {},
  })

  it('offers a tab for every harness that has samples, heaviest first', () => {
    expect(
      harnessTabsFrom([summary('codex', 101), summary('cursor', 405), summary('claude-code', 12)]),
    ).toEqual([
      { harness: 'all', name: 'All Harnesses' },
      { harness: 'cursor', name: 'Cursor' },
      { harness: 'codex', name: 'Codex' },
      { harness: 'claude-code', name: 'Claude Code' },
    ])
  })

  it('leaves out surveyed harnesses that recorded nothing', () => {
    const tabs = harnessTabsFrom([summary('copilot', 56), summary('windsurf', 0)])
    expect(tabs.map((t) => t.harness)).toEqual(['all', 'copilot'])
  })

  it('uses each live canonical harness ID exactly once', () => {
    const tabs = harnessTabsFrom([summary('copilot', 56), summary('copilot', 56), summary('gemini', 2)])
    expect(new Set(tabs.map((tab) => tab.harness)).size).toBe(tabs.length)
  })

  it('falls back to All Harnesses alone when nothing has been collected', () => {
    expect(harnessTabsFrom([])).toEqual([{ harness: 'all', name: 'All Harnesses' }])
    expect(harnessTabsFrom(undefined)).toEqual([{ harness: 'all', name: 'All Harnesses' }])
  })
})

describe('App: Page Switching & Title Rendering', () => {
  beforeEach(() => {
    clearHooks()
  })

  it('renders the Context Doctor landing page by default', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPage="context-doctor" />
      </QueryClientProvider>
    )
    expect(html).toContain('data-testid="page-title"')
    expect(html).toContain('Context Doctor')
    expect(html).toContain('data-testid="page-context-doctor"')
  })

  it('renders page title "Compare" and the CompareRuns workspace when on compare page', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['kyber-runs'], [])
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPage="compare" />
      </QueryClientProvider>
    )
    expect(html).toContain('data-testid="page-title"')
    expect(html).toContain('Compare')
    expect(html).toContain('data-testid="page-compare"')
    expect(html).toContain('Run Comparison Workspace')
    expect(html).toContain('data-testid="compare-empty"')
    expect(html).not.toContain('data-testid="nav-tab-compare"')
    const compareRail = html.match(/<button[^>]*data-testid="nav-rail-compare"[^>]*>/)?.[0]
    expect(compareRail).toContain('bg-interactive-secondary')
  })

  it('renders the Sessions page from the spine rail destination', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPage="sessions" />
      </QueryClientProvider>
    )
    expect(html).toContain('data-testid="page-title"')
    expect(html).toContain('Sessions')
    expect(html).toContain('data-testid="page-sessions"')
    expect(html).not.toContain('data-testid="nav-tab-context"')
    const sessionsRail = html.match(/<button[^>]*data-testid="nav-rail-sessions"[^>]*>/)?.[0]
    expect(sessionsRail).toContain('bg-interactive-secondary')
  })

  it('shows no Share or device controls on any page (requirement 2.9)', () => {
    const qc = createTestQueryClient()
    for (const page of ['context-doctor', 'sessions', 'compare', 'quarantine', 'problems'] as KyberPage[]) {
      clearHooks()
      const html = renderHtml(
        <QueryClientProvider client={qc}>
          <App initialPage={page} />
        </QueryClientProvider>
      )
      expect(html).not.toContain('Share this device')
      expect(html).not.toContain('Search local devices')
      expect(html).not.toMatch(/\bD20\b|\bD21\b/)
    }
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
