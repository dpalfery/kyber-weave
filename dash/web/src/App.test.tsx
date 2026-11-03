import { describe, it, expect, beforeEach } from 'vitest'
import * as React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

// No DashboardTools mock: PI's panels are mounted for real, because this suite
// asserts where the operator tools sit in the shell. It renders its two
// triggers and only queries once a panel is opened, so mounting it costs no
// data fetching — and a stubbed empty div could never have shown a tool that a
// responsive header squeezed out of the group.

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

describe('App: Top Navigation Refactoring', () => {
  beforeEach(() => {
    clearHooks()
  })

  it('exports exactly the header navigation tabs in NAV_TABS (requirement 2.9)', () => {
    expect(NAV_TABS).toHaveLength(5)
    const keys = NAV_TABS.map((t) => t.key)
    expect(keys).toEqual(['context-doctor', 'sessions', 'compare', 'quarantine', 'problems'])
    const labels = NAV_TABS.map((t) => t.label)
    expect(labels).toEqual(['Context Doctor', 'Sessions', 'Compare', 'Quarantine', 'Problems'])
  })

  it('renders every destination as a header tab, with the rail gone rather than emptied', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPage="context-doctor" />
      </QueryClientProvider>
    )

    expect(html).toContain('data-testid="nav-tab-context-doctor"')
    expect(html).not.toContain('data-testid="nav-tab-usage"')
    expect(html).toContain('data-testid="nav-tab-sessions"')
    expect(html).toContain('data-testid="nav-tab-compare"')
    expect(html).toContain('data-testid="nav-tab-quarantine"')
    expect(html).toContain('data-testid="nav-tab-problems"')
    expect(html).not.toContain('data-testid="nav-tab-context"')
    expect(html).not.toContain('data-testid="nav-tab-schema"')
    expect(html).not.toContain('data-testid="nav-tab-timeline"')
    expect(html).not.toContain('data-testid="nav-tab-buckets"')
    expect(html).not.toContain('data-testid="nav-tab-kyber-context"')

    const navTabsMatch = html.match(/data-testid="nav-tab-[^"]+"/g)
    expect(navTabsMatch).toHaveLength(5)

    // Compare moved up into the header rather than being copied into it, so the
    // rail is deleted whole: no rail destination, no off-canvas drawer, no
    // hamburger that opened one and no backdrop behind it. An empty rail is
    // still a rail, and a second copy of a destination is a duplicate.
    expect(html).not.toMatch(/data-testid="nav-rail-[^"]+"/)
    expect(html).not.toContain('dashboard-sidebar')
    expect(html).not.toContain('aria-label="Open menu"')
    expect(html).not.toContain('aria-label="Close menu"')
    expect((html.match(/data-testid="harness-selector"/g) ?? [])).toHaveLength(1)
  })

  it('mounts one header action group and one navigation row, in either layout', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPage="context-doctor" />
      </QueryClientProvider>
    )

    // Below the wide breakpoint the navigation is a second header row rather
    // than a second set of tabs, so neither group may be duplicated to make
    // room: one action group, one navigation row, one harness scope.
    expect((html.match(/data-testid="header-actions"/g) ?? [])).toHaveLength(1)
    expect((html.match(/data-testid="nav-tabs"/g) ?? [])).toHaveLength(1)

    // Both operator tools and the theme toggle are in the shell. The trigger
    // labels are asserted because a responsive header that drops or collapses
    // one of them is the defect this guards, not a detail.
    expect(html).toContain('data-testid="dashboard-tools-status-button"')
    expect(html).toContain('data-testid="dashboard-tools-maintenance-button"')
    expect(html).toContain('aria-label="Switch to')
  })

  it('gives the header two rows below the wide breakpoint and one row at or above it', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPage="context-doctor" />
      </QueryClientProvider>
    )
    const header = html.match(/<header[^>]*data-testid="app-header"[^>]*>/)?.[0]
    expect(header, 'the shell renders one header').toBeDefined()

    // Below the breakpoint the row wraps, so the navigation moves to a row of
    // its own; the header is padded rather than fixed-height, because a fixed
    // 48px is what clipped the two rows into each other at 768.
    expect(header).toContain('flex-wrap')
    expect(header).toContain('py-2')

    // At and above the wide breakpoint the same children are one 48px row.
    expect(header).toContain('lg:flex-nowrap')
    expect(header).toContain('lg:h-12')
    expect(header).toContain('lg:py-0')
  })

  it('scrolls the navigation row sideways instead of widening the page', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPage="context-doctor" />
      </QueryClientProvider>
    )
    const nav = html.match(/<div[^>]*data-testid="nav-tabs"[^>]*>/)?.[0]
    expect(nav, 'the shell renders one navigation row').toBeDefined()

    // The whole second row below the breakpoint (its own basis, and last in the
    // wrapped row, so it sits under the action group rather than beside it),
    // scrolling inside it, with a zero minimum width so the row scrolls rather
    // than forcing the row — and the page — wider.
    expect(nav).toContain('max-lg:order-last')
    expect(nav).toContain('max-lg:basis-full')
    expect(nav).toContain('min-w-0')
    expect(nav).toContain('overflow-x-auto')

    // Back to a share of the one desktop row at the breakpoint and above.
    expect(nav).toContain('lg:order-none')
    expect(nav).toContain('lg:basis-auto')

    // Every tab keeps its own width: a shrunken label wraps to two lines inside
    // a row with no height for them.
    for (const tab of ['context-doctor', 'sessions', 'compare', 'quarantine', 'problems']) {
      const button = html.match(new RegExp(`<button[^>]*data-testid="nav-tab-${tab}"[^>]*>`))?.[0]
      expect(button).toContain('shrink-0')
      expect(button).toContain('whitespace-nowrap')
    }
  })

  it('never compresses or scrolls the action group out of reach', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPage="context-doctor" />
      </QueryClientProvider>
    )
    const actions = html.match(/<div[^>]*data-testid="header-actions"[^>]*>/)?.[0]
    expect(actions, 'the shell renders one header action group').toBeDefined()

    // Status and Maintenance wrap inside their own group, so a group that can be
    // squeezed is a group that splits them onto two lines — and one that scrolls
    // is a group that puts them past the edge of the screen.
    expect(actions).toContain('shrink-0')
    expect(actions).not.toContain('overflow')
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

  it('updates active page styling for Context Doctor, sessions, quarantine, and problems tabs', () => {
    const qc = createTestQueryClient()
    const pages: KyberPage[] = ['context-doctor', 'sessions', 'quarantine', 'problems']

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

describe('App: Settings header button', () => {
  beforeEach(() => {
    clearHooks()
  })

  const PAGES: KyberPage[] = ['context-doctor', 'sessions', 'compare', 'quarantine', 'problems']

  it('renders the Settings button on every page with dialog semantics and a collapsed state', () => {
    const qc = createTestQueryClient()
    for (const page of PAGES) {
      clearHooks()
      const html = renderHtml(
        <QueryClientProvider client={qc}>
          <App initialPage={page} />
        </QueryClientProvider>
      )
      const button = html.match(/<button[^>]*data-testid="header-settings-button"[^>]*>/)?.[0]
      expect(button, page).toBeDefined()
      expect(button).toContain('aria-haspopup="dialog"')
      expect(button).toContain('aria-expanded="false"')
      expect(html).toContain('Settings</button>')
    }
  })

  it('places the Settings button immediately left of the theme toggle, in the header right-hand cluster', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPage="context-doctor" />
      </QueryClientProvider>
    )
    const settingsAt = html.indexOf('data-testid="header-settings-button"')
    const themeAt = html.search(/aria-label="Switch to (dark|light) mode"/)
    expect(settingsAt).toBeGreaterThan(-1)
    expect(themeAt).toBeGreaterThan(settingsAt)
    // Nothing but the Settings button's own markup sits between the two.
    const between = html.slice(settingsAt, themeAt)
    expect(between.match(/<button/g) ?? []).toHaveLength(1)
    // It lives after the nav tabs, in the same header.
    expect(html.indexOf('data-testid="nav-tabs"')).toBeLessThan(settingsAt)
  })

  it('does not render the drawer, and adds no header tab, while closed', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPage="context-doctor" />
      </QueryClientProvider>
    )
    expect(html).not.toContain('role="dialog"')
    expect(html).not.toContain('settings-drawer-panel')
    expect(html.match(/data-testid="nav-tab-[^"]+"/g)).toHaveLength(NAV_TABS.length)
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
      { harness: 'unknown', name: 'Unknown' },
    ])
  })

  it('leaves out surveyed harnesses that recorded nothing', () => {
    const tabs = harnessTabsFrom([summary('copilot', 56), summary('windsurf', 0)])
    expect(tabs.map((t) => t.harness)).toEqual(['all', 'copilot', 'unknown'])
  })

  it('uses each live canonical harness ID exactly once', () => {
    const tabs = harnessTabsFrom([summary('copilot', 56), summary('copilot', 56), summary('gemini', 2)])
    expect(new Set(tabs.map((tab) => tab.harness)).size).toBe(tabs.length)
  })

  it('falls back to All Harnesses alone when nothing has been collected', () => {
    expect(harnessTabsFrom([])).toEqual([
      { harness: 'all', name: 'All Harnesses' },
      { harness: 'unknown', name: 'Unknown' },
    ])
    expect(harnessTabsFrom(undefined)).toEqual([
      { harness: 'all', name: 'All Harnesses' },
      { harness: 'unknown', name: 'Unknown' },
    ])
  })
})

describe('App: Page Switching & Title Rendering', () => {
  beforeEach(() => {
    clearHooks()
  })

  it('does not repeat Context Doctor as the page title on a harness view (issue #194)', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialPath="/harness/claude-desktop" />
      </QueryClientProvider>
    )
    const title = html.match(/data-testid="page-title"[^>]*>([^<]*)</)?.[1]
    expect(title).toBe('Harness · claude-desktop')
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
    // Compare is a header tab now, and this page marks that tab active — the one
    // place its destination is offered, since the rail that used to carry it is gone.
    expect(html).not.toContain('data-testid="nav-rail-compare"')
    const compareTab = html.match(/<button[^>]*data-testid="nav-tab-compare"[^>]*>/)?.[0]
    expect(compareTab).toBeDefined()
    expect(compareTab).toContain('bg-active-primary')
  })

  it('renders the Sessions page from the header navigation', () => {
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
    const sessionsTab = html.match(/<button[^>]*data-testid="nav-tab-sessions"[^>]*>/)?.[0]
    expect(sessionsTab).toContain('bg-active-primary')
    // One destination, one place: the rail that used to hold a second copy of
    // Sessions is gone rather than emptied.
    expect(html).not.toContain('data-testid="nav-rail-sessions"')
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

  it('renders NotFoundPanel inside the shell for a missing id, naming it and linking home (R5.4)', () => {
    const qc = createTestQueryClient()
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <App initialNotFoundId="fid-missing" />
      </QueryClientProvider>
    )
    expect(html).toContain('data-testid="nav-tabs"')
    expect(html).toContain('data-testid="not-found-panel"')
    expect(html).toContain('fid-missing')
    expect(html).toMatch(/href="\/"/)
    expect(html).not.toContain('data-testid="page-finding"')
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
