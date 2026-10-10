// @vitest-environment happy-dom
/**
 * The shell has ONE harness selector, and it is a scope rather than a link.
 *
 * Choosing a harness narrows Context Doctor and Sessions where they stand —
 * it never walks away from Sessions into the harness detail route — the choice
 * survives switching between those two tabs, and a session picked under one
 * harness is dropped rather than shown under the next.
 *
 * It also owns the responsive header: one navigation row, scrolled sideways
 * below the wide breakpoint rather than pushed off the screen.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as React from 'react'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import type { ContextDoctorProps } from '@/pages/ContextDoctor'
import type { KyberHarnessSummary, KyberSessionSummary } from '@/lib/kyberApi'
import { fetchHarnesses, fetchKyberSession, fetchKyberSessions } from '@/lib/kyberApi'

// No DashboardTools mock: PI's header tools are mounted for real, because the
// defect this suite guards is what a narrow row does to those two controls. A
// stubbed empty div would have shown the shell renders *a* group and nothing
// more. DashboardTools only issues its reads once a panel is opened, so no test
// here reaches the API.

vi.mock('@/lib/kyberApi', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/kyberApi')>()
  return { ...actual, fetchHarnesses: vi.fn(), fetchKyberSessions: vi.fn(), fetchKyberSession: vi.fn() }
})

/**
 * The props the shell hands Context Doctor, echoed into the DOM.
 *
 * ContextDoctor itself is PI's to change; these tests pin the contract the
 * shell must satisfy — scope in, selected session in, session change out — so a
 * regression in App is caught without depending on the doctor's internals.
 */
type ScopedDoctorProps = ContextDoctorProps & {
  activeHarness?: string
  sessionId?: string
  onSessionChange?: (sessionId: string | undefined) => void
}

vi.mock('@/pages/ContextDoctor', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/pages/ContextDoctor')>()
  return {
    ...actual,
    ContextDoctor: ({ activeHarness, sessionId, onSessionChange, onSelectHarness }: ScopedDoctorProps) =>
      React.createElement('div', {
        'data-testid': 'doctor-probe',
        'data-active-harness': activeHarness ?? '',
        'data-session-id': sessionId ?? '',
        'data-select-harness': onSelectHarness === undefined ? 'absent' : 'present',
      }, React.createElement('button', {
        type: 'button',
        'data-testid': 'doctor-pick-session',
        onClick: () => onSessionChange?.('sess-picked-in-doctor'),
      }, 'Pick a session')),
  }
})

import { App } from './App.js'

const harnessSummary = (harness: string, sampleCount: number, name?: string): KyberHarnessSummary => ({
  harness,
  sampleCount,
  measurability: {},
  ...(name ? { name } : {}),
})

const session = (harness: string, suffix = ''): KyberSessionSummary => ({
  session_id: `sess-${harness}${suffix}`,
  harness,
  label: `${harness} session${suffix}`,
  started: '2026-03-01T12:00:00.000Z',
  turn_count: 3,
  cost_usd: 0.185,
})

const harnesses = [harnessSummary('codex', 40), harnessSummary('claude-code', 12)]
const sessions = [session('codex'), session('claude-code'), session('codex', '-second')]

const mockedHarnesses = vi.mocked(fetchHarnesses)
const mockedSessions = vi.mocked(fetchKyberSessions)
const mockedSession = vi.mocked(fetchKyberSession)

function renderApp(props: Parameters<typeof App>[0] = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return render(
    <QueryClientProvider client={client}>
      <App {...props} />
    </QueryClientProvider>,
  )
}

/** The strip renders once the harness query resolves, so wait for it. */
async function harnessTab(harness: string) {
  return screen.findByTestId(`harness-tab-${harness}`)
}

function doctorProbe(): HTMLElement {
  return screen.getByTestId('doctor-probe')
}

beforeEach(() => {
  mockedHarnesses.mockResolvedValue(harnesses)
  mockedSessions.mockResolvedValue(sessions)
  mockedSession.mockResolvedValue({
    id: 'sess-detail',
    session_id: 'sess-detail',
    harness: 'codex',
    summary: { turn_count: 0, cost: { usd: 0, basis: 'published_rates', status: 'ok' } },
    turns: [],
    tools: [],
    timeline: [],
  } as Awaited<ReturnType<typeof fetchKyberSession>>)
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('one harness selector scopes the shell', () => {
  it('narrows Sessions in place instead of navigating into the harness detail route', async () => {
    renderApp({ initialPage: 'sessions' })
    await harnessTab('codex')
    expect(screen.getByTestId('agent-session-row-sess-claude-code')).toBeDefined()

    fireEvent.click(await harnessTab('codex'))

    // Still the Sessions surface — the scope is not a link.
    expect(screen.getByTestId('page-sessions')).toBeDefined()
    expect(screen.getByTestId('page-title').textContent).toBe('Sessions')
    expect(window.location.pathname).toBe('/')

    // …and narrowed to the chosen harness.
    expect(screen.queryByTestId('agent-session-row-sess-claude-code')).toBeNull()
    expect(screen.getByTestId('agent-session-row-sess-codex')).toBeDefined()
    expect(screen.getByTestId('agent-session-row-sess-codex-second')).toBeDefined()
  })

  it('scopes Context Doctor findings with the same selector', async () => {
    renderApp()
    await harnessTab('codex')
    expect(doctorProbe().dataset.activeHarness).toBe('all')

    fireEvent.click(await harnessTab('codex'))
    await waitFor(() => expect(doctorProbe().dataset.activeHarness).toBe('codex'))
    expect(window.location.pathname).toBe('/')

    fireEvent.click(await harnessTab('all'))
    await waitFor(() => expect(doctorProbe().dataset.activeHarness).toBe('all'))
  })

  it('keeps the scope while switching between Context Doctor and Sessions', async () => {
    renderApp({ initialPage: 'sessions' })
    await harnessTab('codex')
    fireEvent.click(await harnessTab('codex'))

    fireEvent.click(screen.getByTestId('nav-tab-context-doctor'))
    await waitFor(() => expect(doctorProbe().dataset.activeHarness).toBe('codex'))

    fireEvent.click(screen.getByTestId('nav-tab-sessions'))
    await screen.findByTestId('page-sessions')
    expect(screen.queryByTestId('agent-session-row-sess-claude-code')).toBeNull()
    expect(await screen.findByTestId('agent-session-row-sess-codex')).toBeDefined()

    // The strip still shows the scope it is holding, rather than resetting to All.
    expect((await harnessTab('codex')).getAttribute('aria-pressed')).toBe('true')
  })

  it('marks All Harnesses selected for the all scope', async () => {
    renderApp({ initialPage: 'sessions' })
    expect((await harnessTab('all')).getAttribute('aria-pressed')).toBe('true')
    fireEvent.click(await harnessTab('codex'))
    await waitFor(() => expect((screen.getByTestId('harness-tab-all')).getAttribute('aria-pressed')).toBe('false'))
    expect((screen.getByTestId('harness-tab-codex')).getAttribute('aria-pressed')).toBe('true')
  })

  it('preserves a canonical harness id the catalogue does not recognise', async () => {
    mockedHarnesses.mockResolvedValue([
      ...harnesses,
      harnessSummary('future-harness-v2', 7),
    ])
    mockedSessions.mockResolvedValue(sessions)
    renderApp({ initialPage: 'sessions' })

    const future = await harnessTab('future-harness-v2')
    // No label for a harness this build has never heard of: the raw id is shown
    // and kept, rather than dropped or renamed into something else.
    expect(future.textContent).toContain('future-harness-v2')

    fireEvent.click(future)
    expect(screen.queryByTestId('agent-session-row-sess-codex')).toBeNull()
    expect(screen.getByTestId('explorer-empty').textContent).toContain('future-harness-v2')
  })

  it('shows a scoped empty state for a harness with no sessions, never all sessions', async () => {
    mockedSessions.mockResolvedValue([session('claude-code')])
    renderApp({ initialPage: 'sessions' })

    fireEvent.click(await harnessTab('codex'))
    // `codex` is a harness the sessions vocabulary has no display label for, so
    // the scoped empty state names it by its own canonical id rather than
    // guessing — and never answers with another harness's sessions.
    expect(await screen.findByTestId('explorer-empty')).toHaveProperty('textContent', 'No sessions for codex.')
    expect(screen.queryByTestId('agent-session-row-sess-claude-code')).toBeNull()
  })

  it('offers exactly one harness selector and no second strip inside Sessions', async () => {
    const { container } = renderApp({ initialPage: 'sessions' })
    await harnessTab('codex')
    expect(container.querySelectorAll('[data-testid="harness-selector"]')).toHaveLength(1)
    expect(container.querySelectorAll('[data-testid^="provider-tab-"]')).toHaveLength(0)
  })

  it('drops a session selected under one harness when the scope moves to another', async () => {
    renderApp({ initialPage: 'sessions' })
    await harnessTab('codex')

    fireEvent.click(screen.getByTestId('agent-session-row-sess-claude-code'))
    fireEvent.click(screen.getByTestId('nav-tab-context-doctor'))
    await waitFor(() => expect(doctorProbe().dataset.sessionId).toBe('sess-claude-code'))

    fireEvent.click(await harnessTab('codex'))
    await waitFor(() => expect(doctorProbe().dataset.sessionId).toBe(''))
    expect(doctorProbe().dataset.activeHarness).toBe('codex')
  })

  it('keeps a session Context Doctor chose while the user moves between the two tabs', async () => {
    renderApp()
    await harnessTab('codex')

    // Context Doctor's own session selector reports up to the shell, and the
    // shell holds it across the tab switch rather than resetting per surface.
    fireEvent.click(screen.getByTestId('doctor-pick-session'))
    expect(doctorProbe().dataset.sessionId).toBe('sess-picked-in-doctor')

    fireEvent.click(screen.getByTestId('nav-tab-sessions'))
    await screen.findByTestId('page-sessions')
    fireEvent.click(screen.getByTestId('nav-tab-context-doctor'))
    await waitFor(() => expect(doctorProbe().dataset.sessionId).toBe('sess-picked-in-doctor'))
  })
})

describe('navigation surface', () => {
  it('keeps Context Doctor and Sessions in the top navigation and Compare in the rail', async () => {
    renderApp({ initialPage: 'sessions' })
    await harnessTab('codex')

    expect(screen.getByTestId('nav-tab-context-doctor')).toBeDefined()
    expect(screen.getByTestId('nav-tab-sessions')).toBeDefined()
    expect(screen.queryByTestId('nav-rail-context-doctor')).toBeNull()
    expect(screen.queryByTestId('nav-rail-sessions')).toBeNull()
    expect(screen.getByTestId('nav-rail-compare')).toBeDefined()
    expect(screen.getByTestId('nav-tab-quarantine')).toBeDefined()
    expect(screen.getByTestId('nav-tab-problems')).toBeDefined()
  })

  it('reaches Sessions from the top navigation rather than the collapsed drawer', async () => {
    renderApp()
    await harnessTab('codex')
    fireEvent.click(screen.getByTestId('nav-tab-sessions'))
    expect(await screen.findByTestId('page-sessions')).toBeDefined()
    expect(screen.queryByTestId('page-context-doctor')).toBeNull()
  })

  it('leaves the off-canvas drawer usable on a narrow viewport', async () => {
    renderApp({ initialPage: 'sessions' })
    await harnessTab('codex')

    // The drawer is the mobile-only surface, so it must still open and still
    // carry its one remaining destination.
    const menu = screen.getByLabelText('Open menu')
    expect(menu.getAttribute('aria-expanded')).toBe('false')
    fireEvent.click(menu)
    expect(screen.getByLabelText('Open menu').getAttribute('aria-expanded')).toBe('true')
    fireEvent.click(screen.getByTestId('nav-rail-compare'))
    expect(await screen.findByTestId('page-compare')).toBeDefined()
  })

  it('mounts both operator tools and the theme toggle in the header action group', async () => {
    renderApp({ initialPage: 'sessions' })
    await harnessTab('codex')
    const actions = screen.getByTestId('header-actions')

    // The real controls, in the group: neither tool may be dropped, collapsed
    // to a menu, or moved out of the header to make the narrow layout fit.
    const status = actions.querySelector('[data-testid="dashboard-tools-status-button"]')
    const maintenance = actions.querySelector('[data-testid="dashboard-tools-maintenance-button"]')
    expect(status, 'Status stays in the header action group').not.toBeNull()
    expect(maintenance, 'Maintenance stays in the header action group').not.toBeNull()
    expect(
      actions.querySelector('[aria-label="Switch to dark mode"], [aria-label="Switch to light mode"]'),
    ).not.toBeNull()

    for (const tool of [status, maintenance]) {
      expect(tool!.closest('[hidden]')).toBeNull()
      expect(tool!.getAttribute('aria-hidden')).not.toBe('true')
      expect(tool!.className).not.toContain('hidden')
      expect(tool!.textContent?.trim()).toBe(tool === status ? 'Status' : 'Maintenance')
    }
  })

  it('renders the navigation once, as a row that scrolls sideways below the breakpoint', async () => {
    const { container } = renderApp({ initialPage: 'sessions' })
    await harnessTab('codex')

    // One top navigation in the DOM whatever the viewport: the narrow layout
    // moves it to a second row, it does not add a second strip.
    expect(container.querySelectorAll('[data-testid="nav-tabs"]')).toHaveLength(1)
    expect(container.querySelectorAll('[data-testid="header-actions"]')).toHaveLength(1)

    const nav = screen.getByTestId('nav-tabs')
    expect(nav.className).toContain('max-lg:order-last')
    expect(nav.className).toContain('max-lg:basis-full')
    expect(nav.className).toContain('min-w-0')
    expect(nav.className).toContain('overflow-x-auto')
    expect(nav.className).toContain('lg:order-none')
    expect(nav.className).toContain('lg:basis-auto')

    // Tabs keep their own width inside that row; a squeezed tab wraps onto two
    // lines, which is what the fixed 48px header could not show.
    for (const key of ['context-doctor', 'sessions', 'quarantine', 'problems']) {
      const tab = screen.getByTestId(`nav-tab-${key}`)
      expect(tab.className, `${key} tab keeps its width`).toContain('shrink-0')
      expect(tab.className, `${key} tab does not wrap`).toContain('whitespace-nowrap')
    }
  })

  it('leaves the action group uncompressed and unscrolled', async () => {
    renderApp({ initialPage: 'sessions' })
    await harnessTab('codex')
    const actions = screen.getByTestId('header-actions')
    // A group that can shrink splits Status and Maintenance onto two lines; a
    // group that scrolls puts them past the edge of a 375px screen.
    expect(actions.className).toContain('shrink-0')
    expect(actions.className).not.toContain('overflow')
  })

  it('stops handing Context Doctor the scorecard matrix callback', async () => {
    renderApp()
    await harnessTab('codex')

    // The matrix left the page with the operator panels, so nothing there calls
    // this. The prop is ContextDoctor's to drop from its own type; what the
    // shell owns is the call site.
    expect(doctorProbe().dataset.selectHarness).toBe('absent')
  })
})