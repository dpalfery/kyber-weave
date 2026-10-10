import { useEffect, useMemo, useReducer, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'

import { cn } from '@/lib/utils'
import { Skeleton } from '@/components/ui/skeleton'
import { QuarantineView, type QuarantineEntry } from '@/components/analysis/QuarantineView'
import { ProblemsView, type ProblemEntry } from '@/components/analysis/ProblemsView'
import { LightsaberLogo } from '@/components/LightsaberLogo'
import { NotFoundPanel } from '@/components/analysis/NotFoundPanel'
import { DashboardTools } from './components/maintenance/DashboardTools'
import { fetchHarnesses, type KyberHarnessSummary } from '@/lib/kyberApi'
import {
  applySpineAction,
  createAncestryApi,
  locationFromPath,
  pathFromLocation,
  pushView,
  replaceView,
  resolveAncestry,
  restoreFromPopState,
  type SpineLocation,
} from '@/lib/router'
import { ContextDoctor, HARNESS_CATALOG, harnessDisplayName } from '@/pages/ContextDoctor'
import { CompareRuns } from '@/pages/CompareRuns'
import { Sessions } from '@/pages/Sessions'
import { HarnessDetail } from '@/pages/HarnessDetail'
import { RunDetail } from '@/pages/RunDetail'
import { FindingDetail } from '@/pages/FindingDetail'
import { TurnDetail } from '@/pages/TurnDetail'

export const HARNESS_TABS = HARNESS_CATALOG

/**
 * The harness tabs to show: "All Harnesses" plus every harness the store has
 * samples for, newest-heaviest first.
 *
 * The strip used to be a hardcoded three (claude-code, copilot, gemini), so a
 * machine collecting Codex, Cursor, OpenCode or Antigravity had that data
 * ingested and then no way to reach it. Harnesses with no samples are left out
 * rather than shown as empty tabs.
 */
export function harnessTabsFrom(
  summaries: readonly KyberHarnessSummary[] | undefined,
): Array<{ harness: string; name: string }> {
  const seen = new Set<string>(['all'])
  const live: Array<{ harness: string; name: string }> = []
  for (const summary of [...(summaries ?? [])].sort(
    (a, b) => (b.sampleCount ?? 0) - (a.sampleCount ?? 0),
  )) {
    // A harness with no samples has nothing to show; a duplicate id would
    // render two tabs that open the same page.
    if ((summary.sampleCount ?? 0) <= 0 || seen.has(summary.harness)) continue
    seen.add(summary.harness)
    live.push({ harness: summary.harness, name: summary.name ?? harnessDisplayName(summary.harness) })
  }
  return [{ harness: 'all', name: 'All Harnesses' }, ...live]
}

function useHarnessTabs(): Array<{ harness: string; name: string }> {
  const { data } = useQuery({
    queryKey: ['kyber-harnesses'],
    queryFn: fetchHarnesses,
    staleTime: 30_000,
  })
  return useMemo(() => harnessTabsFrom(data), [data])
}

export type { SpineLocation }

function hrefFromWindow(): string {
  if (typeof window === 'undefined' || typeof window.location?.pathname !== 'string') return '/'
  return `${window.location.pathname}${window.location.search}`
}

function pageHeading(section: KyberPage, location: SpineLocation): string {
  if (section === 'compare' || location.level === 'compare') return 'Compare'
  if (section === 'sessions') return 'Sessions'
  if (section === 'quarantine' || location.level === 'quarantine') return 'Quarantine'
  if (section === 'problems' || location.level === 'problems') return 'Problems'
  switch (location.level) {
    case 'harness':
      return location.harnessId ? `Harness · ${location.harnessId}` : 'Harness'
    case 'run':
      return 'Run'
    case 'execution':
      return 'Execution'
    case 'turn':
      return 'Turn'
    case 'finding':
      return 'Finding'
    default:
      return 'Context Doctor'
  }
}

function pageFor(location: SpineLocation): KyberPage {
  if (location.level === 'quarantine') return 'quarantine'
  if (location.level === 'problems') return 'problems'
  if (location.level === 'compare') return 'compare'
  return 'context-doctor'
}

function viewFromProps(initialPage: KyberPage, initialPath?: string): SpineLocation {
  const href = initialPath ?? hrefFromWindow()
  const fromUrl = locationFromPath(href)
  // An explicit initialPath always wins. Otherwise a bare `/` must not override
  // `initialPage` — tests (and the Sessions rail) land on a tab without a URL.
  if (fromUrl && (initialPath !== undefined || (href !== '/' && href !== ''))) return fromUrl
  if (initialPage === 'quarantine') return { level: 'quarantine' }
  if (initialPage === 'problems') return { level: 'problems' }
  if (initialPage === 'compare') return { level: 'compare' }
  return { level: 'context-doctor' }
}

function SideLink({ active, onClick, children, testId }: { active: boolean; onClick: () => void; children: ReactNode; testId?: string }) {
  return (
    <button
      type="button"
      data-testid={testId}
      onClick={onClick}
      className={cn(
        'flex items-center gap-2 rounded-md px-2.5 py-1.5 text-left text-[13.5px] transition-colors max-md:min-h-9',
        active ? 'bg-interactive-secondary font-medium text-foreground' : 'font-light text-muted-foreground hover:text-foreground',
      )}
    >
      <span className={cn('h-1.5 w-1.5 shrink-0 rounded-full', active ? 'bg-primary' : 'bg-transparent')} />
      <span className="truncate">{children}</span>
    </button>
  )
}

// Theme toggle: mirrors the .dark class set by the index.html pre-paint script
// and persists the choice to the same localStorage key (`kyberdash-theme`,
// falling back to the pre-severance key on read until a release ships past it).
function ThemeToggle() {
  const [dark, setDark] = useState(() =>
    typeof document !== 'undefined' ? document.documentElement.classList.contains('dark') : false,
  )
  const toggle = () => {
    const next = !dark
    setDark(next)
    if (typeof document !== 'undefined') {
      document.documentElement.classList.toggle('dark', next)
    }
    try {
      localStorage.setItem('kyberdash-theme', next ? 'dark' : 'light')
      localStorage.removeItem('codeburn-theme')
    } catch {
      // storage disabled (some embeds/webviews): persist nothing, OS theme wins next load
    }
  }
  return (
    <button
      type="button"
      onClick={toggle}
      aria-label={dark ? 'Switch to light mode' : 'Switch to dark mode'}
      title={dark ? 'Switch to light mode' : 'Switch to dark mode'}
      className="flex h-7 w-7 shrink-0 items-center justify-center rounded-md border border-border bg-card text-tertiary-foreground transition-colors hover:bg-interactive-secondary hover:text-foreground max-md:h-9 max-md:w-9 max-md:shrink-0"
    >
      {dark ? (
        <svg viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round">
          <path d="M14 8.53A6 6 0 1 1 7.47 2 4.67 4.67 0 0 0 14 8.53Z" />
        </svg>
      ) : (
        <svg viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round">
          <circle cx="8" cy="8" r="3.1" />
          <path d="M8 1.8v1.7M8 12.5v1.7M1.8 8h1.7M12.5 8h1.7M3.5 3.5l1.2 1.2M11.3 11.3l1.2 1.2M12.5 3.5l-1.2 1.2M4.7 11.3l-1.2 1.2" />
        </svg>
      )}
    </button>
  )
}

/**
 * The destinations in the header.
 *
 * Context Doctor and Sessions live here because the harness scope under this
 * header reaches both: they are one decision, so they are one tab row. The
 * rail keeps what the scope does not cover.
 */
export const NAV_TABS = [
  { key: 'context-doctor', label: 'Context Doctor' },
  { key: 'sessions', label: 'Sessions' },
  { key: 'quarantine', label: 'Quarantine' },
  { key: 'problems', label: 'Problems' },
] as const

export type KyberPage = (typeof NAV_TABS)[number]['key'] | 'compare'

export function KyberQuarantinePanel() {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['kyber-quarantine'],
    queryFn: async () => {
      const r = await fetch('/api/kyber/quarantine')
      if (!r.ok) {
        const err = await r.json().catch(() => null)
        throw new Error(err?.error || `HTTP ${r.status}`)
      }
      return r.json()
    },
    retry: false,
  })

  if (isLoading) return <Skeleton className="h-20" />
  if (isError || !data) return <QuarantineView entries={[]} />

  const rawEntries = Array.isArray(data) ? data : (data as { entries?: unknown[] }).entries ?? []
  if (!Array.isArray(rawEntries)) {
    return <QuarantineView entries={[]} />
  }

  const entries: QuarantineEntry[] = rawEntries.map((raw, idx: number) => {
    const e = raw as Record<string, unknown>
    let ns: string[] = []
    if (Array.isArray(e.namespaces)) {
      ns = e.namespaces.map(String)
    } else if (typeof e.namespaces === 'string' && e.namespaces.trim()) {
      try {
        const parsed = JSON.parse(e.namespaces)
        ns = Array.isArray(parsed) ? parsed.map(String) : [String(parsed)]
      } catch {
        ns = e.namespaces.includes(',') ? e.namespaces.split(',').map((s: string) => s.trim()) : [e.namespaces.trim()]
      }
    }
    return {
      spanId: String(e.spanId || e.span_id || `quarantine-${idx}`),
      namespaces: ns,
      reason: String(e.reason || ''),
    }
  })

  return <QuarantineView entries={entries} />
}

export function KyberProblemsPanel() {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['kyber-problems'],
    queryFn: async () => {
      const r = await fetch('/api/kyber/problems')
      if (!r.ok) {
        const err = await r.json().catch(() => null)
        throw new Error(err?.error || `HTTP ${r.status}`)
      }
      return r.json()
    },
    retry: false,
  })

  if (isLoading) return <Skeleton className="h-20" />
  if (isError || !data) return <ProblemsView problems={[]} />

  const rawProblems = Array.isArray(data) ? data : (data as { problems?: unknown[] }).problems ?? []
  if (!Array.isArray(rawProblems)) {
    return <ProblemsView problems={[]} />
  }

  const problems: ProblemEntry[] = rawProblems.map((raw) => {
    const p = raw as Record<string, unknown>
    return {
      severity: (p.severity === 'error' ? 'error' : 'warning') as 'error' | 'warning',
      code: String(p.code || 'unknown'),
      message: String(p.message || ''),
      location: (p.location || p.at || p.harness || undefined) as string | undefined,
      spanId: (p.spanId || p.span_id || undefined) as string | undefined,
    }
  })

  return <ProblemsView problems={problems} />
}

export interface AppProps {
  initialPage?: KyberPage
  /** Path to parse on first paint; defaults to `window.location` so a deep link lands. */
  initialPath?: string
  /** A missing-id deep link that already 404'd, used by tests that cannot run effects. */
  initialNotFoundId?: string
}

/**
 * Renders the KyberDash shell and coordinates its top-level navigation and
 * diagnostic spine.
 */
export function App({ initialPage = 'context-doctor', initialPath, initialNotFoundId }: AppProps = {}) {
  const [section, setSection] = useState<KyberPage>(() =>
    initialPage === 'sessions' ? 'sessions' : pageFor(viewFromProps(initialPage, initialPath)),
  )
  const [spine, dispatchSpine] = useReducer(applySpineAction, undefined, (): SpineLocation[] => [
    viewFromProps(initialPage, initialPath),
  ])
  const location = spine.at(-1)!
  const harnessTabs = useHarnessTabs()
  // Mobile only: the sidebar collapses to an off-canvas drawer below md.
  // On desktop this flag is inert (the max-md: transform classes don't apply).
  const [sidebarOpen, setSidebarOpen] = useState(false)
  const [notFoundId, setNotFoundId] = useState<string | null>(initialNotFoundId ?? null)
  // The shell's one harness scope, shared by Context Doctor and Sessions. It
  // outlives a tab switch between the two, so the same choice is still in
  // force when the user comes back.
  const [activeHarness, setActiveHarness] = useState<string>('all')
  // The session those two surfaces agree on: opened in Sessions, or chosen in
  // Context Doctor's own selector.
  const [sessionId, setSessionId] = useState<string | undefined>(undefined)

  // Follow the OS theme live while the user has no explicit preference, so
  // flipping the system theme updates the dashboard without a reload.
  useEffect(() => {
    if (typeof window === 'undefined' || !window.matchMedia) return
    const mql = window.matchMedia('(prefers-color-scheme: dark)')
    const apply = () => {
      let saved: string | null = null
      try {
        saved = localStorage.getItem('kyberdash-theme') ?? localStorage.getItem('codeburn-theme')
      } catch {
        // storage disabled: OS theme only
      }
      if (saved !== 'dark' && saved !== 'light') {
        document.documentElement.classList.toggle('dark', mql.matches)
      }
    }
    mql.addEventListener('change', apply)
    return () => mql.removeEventListener('change', apply)
  }, [])

  // Direct opens have a URL but no history.state stack. Rebuild the ancestry
  // navigating there would have pushed, then replaceState so Back leaves the app.
  useEffect(() => {
    let cancelled = false
    const href = initialPath ?? hrefFromWindow()
    const loc = locationFromPath(href)
    if (
      !loc ||
      loc.level === 'context-doctor' ||
      loc.level === 'quarantine' ||
      loc.level === 'problems' ||
      (loc.level === 'compare' && !loc.compareA)
    ) {
      return
    }
    void createAncestryApi().then((api) =>
      resolveAncestry(loc, api).then((result) => {
        if (cancelled) return
        if (result.status === 'not-found') {
          setNotFoundId(result.id)
          return
        }
        setNotFoundId(null)
        dispatchSpine({ type: 'reset', stack: result.stack })
        setSection(pageFor(result.stack.at(-1)!))
        const path = pathFromLocation(result.stack.at(-1)!)
        if (path) replaceView(path, result.stack)
      }),
    )
    return () => {
      cancelled = true
    }
  }, [initialPath])

  useEffect(() => {
    if (typeof window === 'undefined' || !window.addEventListener) return
    const onPop = (event: PopStateEvent) => {
      const restored = restoreFromPopState(event.state, hrefFromWindow())
      if (Array.isArray(restored)) {
        setNotFoundId(null)
        dispatchSpine({ type: 'reset', stack: restored })
        setSection(pageFor(restored.at(-1) ?? { level: 'context-doctor' }))
        return
      }
      void createAncestryApi().then((api) =>
        resolveAncestry(restored.needsAncestry, api).then((result) => {
          if (result.status === 'not-found') {
            setNotFoundId(result.id)
            return
          }
          setNotFoundId(null)
          dispatchSpine({ type: 'reset', stack: result.stack })
          setSection(pageFor(result.stack.at(-1)!))
        }),
      )
    }
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
  }, [])

  const showSpine = section === 'context-doctor' || section === 'compare'
  /** Opens a diagnostic spine location, records it in history, and makes Context Doctor active. */
  const openSpine = (next: SpineLocation, action: 'push' | 'replace' | 'goTo' = 'push') => {
    setNotFoundId(null)
    setSection(pageFor(next))
    const nextStack = applySpineAction(spine, { type: action, location: next })
    dispatchSpine({ type: action, location: next })
    const path = pathFromLocation(nextStack.at(-1)!)
    if (path) pushView(path, nextStack)
  }

  const goHeader = (next: SpineLocation) => {
    setNotFoundId(null)
    setSection(pageFor(next))
    dispatchSpine({ type: 'reset', stack: [next] })
    const path = pathFromLocation(next)
    if (path) pushView(path, [next])
  }

  /**
   * Narrow the shell to one harness.
   *
   * The strip is a scope, not a link: it filters whichever of Context Doctor
   * and Sessions is on screen and leaves the page there, so the drill-down
   * routes stay where the findings put them rather than under a selector. The
   * session selected so far is dropped with the old scope — it was collected
   * from the harness being left behind, and showing it under the new one would
   * attribute another harness's session to this one.
   */
  const selectHarnessScope = (harness: string) => {
    if (harness === activeHarness) return
    setActiveHarness(harness)
    setSessionId(undefined)
  }

  return (
    <div className="min-h-screen bg-outer-background p-2.5 max-md:min-h-[100dvh]">
      <div className="flex h-[calc(100vh-20px)] flex-col gap-2.5 max-md:h-[calc(100dvh-20px)]">
        {/*
          One top navigation, laid out for the width in front of it.

          The header was one fixed 48px row, and the operator tools do not fit
          into 48px of a phone-width viewport: the action group was a scrollable
          strip that put Status and Maintenance past the right edge, and squeezed
          to a tablet width it split those two onto separate lines inside a row
          with no height left to give them. Below the wide breakpoint the row
          wraps instead — the mark and the action group share the first row, and
          the navigation becomes a full-width row beneath them that scrolls
          sideways rather than widening the page. At and above the breakpoint the
          same three children sit on the one 48px row as before.

          It is one header and one navigation in both layouts: only the placement
          changes, so nothing is duplicated to make room, and the header is padded
          rather than fixed-height so neither row can be clipped by the other.
        */}
        <header
          data-testid="app-header"
          className="flex shrink-0 flex-wrap items-center gap-x-2 gap-y-2 rounded-md border border-border bg-card px-2 py-2 shadow-[0_2px_8px_rgba(0,0,0,0.03)] dark:shadow-[0_2px_8px_rgba(0,0,0,0.5)] sm:gap-x-4 sm:px-4 lg:h-12 lg:flex-nowrap lg:px-5 lg:py-0"
        >
          <div className="flex min-w-0 items-center gap-2">
            <button
              type="button"
              onClick={() => setSidebarOpen(true)}
              aria-label="Open menu"
              aria-expanded={sidebarOpen}
              aria-controls="dashboard-sidebar"
              className="-ml-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-md text-foreground transition-colors hover:bg-interactive-secondary md:hidden"
            >
              <svg viewBox="0 0 16 16" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round">
                <path d="M2.5 4.5h11M2.5 8h11M2.5 11.5h11" />
              </svg>
            </button>
            <div className="flex min-w-0 items-center gap-2">
              <LightsaberLogo className="h-6 w-6 shrink-0" />
              {/* Below the small breakpoint the mark carries the brand on its own.
                  Both operator tools, the menu and the mark together need more
                  width than a 375px viewport has, and the wordmark is the one
                  piece of this row that may give way: it is the part that is
                  decoration rather than a control. The logo keeps its
                  accessible name, and from `sm` up there is room for the word. */}
              <span className="max-sm:hidden truncate text-lg font-semibold tracking-[-0.02em] text-foreground">
                kyber<span className="text-primary">Dash</span>
              </span>
              <span className="ml-1 shrink-0 text-[11px] font-light uppercase tracking-[0.14em] text-tertiary-foreground max-sm:hidden">telemetry</span>
            </div>
          </div>

          {/* The navigation row: last on the wrapped layout, in place on the wide
              one. `basis-full` gives it a row of its own, `min-w-0` lets it
              scroll inside that row rather than widening the page, and each tab
              keeps its own width rather than wrapping to a second line. */}
          <div
            className="flex min-w-0 items-center overflow-x-auto rounded-md border border-border bg-interactive-secondary p-0.5 [scrollbar-width:none] [&::-webkit-scrollbar]:hidden max-lg:order-last max-lg:basis-full lg:order-none lg:ml-6 lg:basis-auto"
            data-testid="nav-tabs"
          >
            {NAV_TABS.map((pg) => (
              <button
                key={pg.key}
                type="button"
                data-testid={`nav-tab-${pg.key}`}
                onClick={() => {
                  if (pg.key === 'context-doctor') goHeader({ level: 'context-doctor' })
                  else if (pg.key === 'sessions') {
                    setNotFoundId(null)
                    setSection('sessions')
                  } else if (pg.key === 'quarantine') goHeader({ level: 'quarantine' })
                  else goHeader({ level: 'problems' })
                }}
                className={cn(
                  'shrink-0 whitespace-nowrap rounded-[5px] px-3 py-1 text-xs font-medium transition-colors',
                  section === pg.key ? 'bg-active-primary text-foreground shadow-sm' : 'text-tertiary-foreground hover:text-foreground',
                )}
              >
                {pg.label}
              </button>
            ))}
          </div>

          {/* Right-aligned action group: the status/maintenance panels, then the
              theme toggle, so the panels never displace the navigation. It is
              never compressed (a squeezed group wraps Status and Maintenance onto
              two lines) and never scrolled (a scrolled group puts them past the
              edge of the screen) — the navigation row is what gives way instead. */}
          <div className="ml-auto flex shrink-0 items-center gap-2" data-testid="header-actions">
            <DashboardTools />
            <ThemeToggle />
          </div>
        </header>

        <nav
          aria-label="Harness tabs"
          data-testid="harness-selector"
          className="flex shrink-0 items-center gap-1.5 overflow-x-auto rounded-md border border-border bg-card px-3 py-1.5 shadow-[0_1px_3px_rgba(0,0,0,0.02)] [scrollbar-width:none] [&::-webkit-scrollbar]:hidden"
        >
          <span className="mr-1 text-[11px] font-semibold uppercase tracking-wider text-tertiary-foreground">Harness:</span>
          {harnessTabs.map((tab) => {
            const active = activeHarness === tab.harness
            return (
              <button
                key={tab.harness}
                type="button"
                data-testid={`harness-tab-${tab.harness}`}
                aria-pressed={active}
                onClick={() => selectHarnessScope(tab.harness)}
                className={cn(
                  'flex items-center gap-1.5 whitespace-nowrap rounded-md px-2.5 py-1 text-xs font-medium transition-all',
                  active
                    ? 'border border-primary/50 bg-primary/15 text-primary font-semibold shadow-xs'
                    : 'border border-transparent text-tertiary-foreground hover:bg-interactive-secondary hover:text-foreground'
                )}
              >
                <span>{tab.name}</span>
              </button>
            )
          })}
        </nav>

        <div className="flex min-h-0 flex-1 gap-2.5">
          {sidebarOpen && (
            <button
              type="button"
              aria-label="Close menu"
              onClick={() => setSidebarOpen(false)}
              className="fixed inset-0 z-30 bg-black/40 md:hidden"
            />
          )}
          <aside
            id="dashboard-sidebar"
            className={cn(
              'flex w-60 shrink-0 flex-col gap-5 overflow-y-auto rounded-md border border-border bg-card p-5',
              'max-md:fixed max-md:inset-y-0 max-md:left-0 max-md:z-40 max-md:rounded-none max-md:shadow-2xl max-md:transition-[transform,visibility] max-md:duration-200 max-md:ease-out',
              // Closed below md: slide off-canvas AND go visibility:hidden so its
              // links leave the tab order / a11y tree (not just visually hidden).
              sidebarOpen ? 'max-md:visible max-md:translate-x-0' : 'max-md:invisible max-md:-translate-x-full',
            )}
          >
            <button
              type="button"
              aria-label="Close menu"
              onClick={() => setSidebarOpen(false)}
              className="absolute right-3 top-3 flex h-9 w-9 items-center justify-center rounded-md text-tertiary-foreground transition-colors hover:bg-interactive-secondary hover:text-foreground md:hidden"
            >
              <svg viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round">
                <path d="M4 4l8 8M12 4l-8 8" />
              </svg>
            </button>
            {/* Context Doctor and Sessions are in the header; repeating them here put
                one destination on screen twice, once per surface. Compare stays:
                the harness scope does not reach it, and it is the only
                destination the rail still carries. */}
            <div className="flex flex-col gap-1">
              <SideLink
                testId="nav-rail-compare"
                active={section === 'compare'}
                onClick={() => {
                  setNotFoundId(null)
                  setSection('compare')
                  dispatchSpine({ type: 'reset', stack: [{ level: 'compare' }] })
                  setSidebarOpen(false)
                }}
              >
                Compare
              </SideLink>
            </div>
          </aside>

          <main className="min-w-0 flex-1 overflow-y-auto pr-0.5">
            <div className="mb-3 flex items-baseline justify-between">
              <h1 className="font-display text-xl tracking-tight text-foreground" data-testid="page-title">
                {pageHeading(section, location)}
              </h1>
            </div>

            {notFoundId ? (
              <NotFoundPanel id={notFoundId} onHome={() => goHeader({ level: 'context-doctor' })} />
            ) : showSpine ? (
              location.level === 'context-doctor' ? (
                <ContextDoctor
                  key="context-doctor"
                  activeHarness={activeHarness}
                  sessionId={sessionId}
                  onSessionChange={setSessionId}
                  onSelectRun={(runId, harnessId) => openSpine({ level: 'run', runId, harnessId })}
                  onSelectFinding={(findingId) => openSpine({ level: 'finding', findingId })}
                  onSelectTurn={(turnIndex, executionId, runId) => openSpine({ level: 'turn', turnIndex, executionId, runId })}
                />
              ) : location.level === 'harness' ? (
                <HarnessDetail
                  key={`harness:${location.harnessId}`}
                  harnessId={location.harnessId!}
                  onSelectAll={() => openSpine({ level: 'context-doctor' }, 'goTo')}
                  onSelectRun={(runId) => openSpine({ level: 'run', harnessId: location.harnessId, runId })}
                  onSelectFinding={(findingId) => openSpine({ level: 'finding', harnessId: location.harnessId, findingId })}
                  onSelectTurn={(turnIndex, executionId, runId) => openSpine({ level: 'turn', harnessId: location.harnessId, runId, executionId, turnIndex })}
                />
              ) : location.level === 'run' || location.level === 'execution' ? (
                <RunDetail
                  key={`run:${location.runId}:${location.executionId ?? ''}`}
                  runId={location.runId!}
                  executionId={location.level === 'execution' ? location.executionId : undefined}
                  onSelectAll={() => openSpine({ level: 'context-doctor' }, 'goTo')}
                  onSelectHarness={(harnessId) => openSpine({ level: 'harness', harnessId }, 'goTo')}
                  onSelectExecution={(executionId) => openSpine({ level: 'execution', harnessId: location.harnessId, runId: location.runId, executionId })}
                  onSelectTurn={(turnIndex, executionId) => openSpine({ level: 'turn', harnessId: location.harnessId, runId: location.runId, executionId, turnIndex })}
                  onSelectFinding={(findingId) => openSpine({ level: 'finding', harnessId: location.harnessId, runId: location.runId, findingId })}
                />
              ) : location.level === 'turn' ? (
                <TurnDetail
                  key={`turn:${location.runId}:${location.executionId ?? ''}:${location.turnIndex}`}
                  runId={location.runId!}
                  executionId={location.executionId}
                  turnIndex={location.turnIndex!}
                  onSelectAll={() => openSpine({ level: 'context-doctor' }, 'goTo')}
                  onSelectHarness={(harnessId) => openSpine({ level: 'harness', harnessId }, 'goTo')}
                  onSelectRun={(runId) => openSpine({ level: 'run', harnessId: location.harnessId, runId }, 'goTo')}
                  onSelectExecution={(executionId) => openSpine({ level: 'execution', harnessId: location.harnessId, runId: location.runId, executionId }, 'goTo')}
                />
              ) : location.level === 'compare' ? (
                <CompareRuns initialRunAId={location.compareA} initialRunBId={location.compareB} />
              ) : (
                <FindingDetail
                  key={`finding:${location.findingId}`}
                  findingId={location.findingId}
                  onSelectAll={() => openSpine({ level: 'context-doctor' }, 'goTo')}
                  onSelectHarness={(harnessId) => openSpine({ level: 'harness', harnessId }, 'goTo')}
                  onSelectRun={(runId) => openSpine({ level: 'run', runId }, 'goTo')}
                  onSelectExecution={(executionId) => openSpine({ level: 'execution', executionId })}
                  onSelectTurn={(turnIndex, executionId, runId) => openSpine({ level: 'turn', harnessId: location.harnessId, runId: runId ?? location.runId, executionId, turnIndex })}
                  onBack={() => {
                    const nextStack = applySpineAction(spine, { type: 'pop' })
                    dispatchSpine({ type: 'pop' })
                    const path = pathFromLocation(nextStack.at(-1)!)
                    if (path) pushView(path, nextStack)
                  }}
                />
              )
            ) : section === 'sessions' ? (
              <Sessions activeHarness={activeHarness} onSelectSession={setSessionId} />
            ) : section === 'quarantine' ? (
              <KyberQuarantinePanel />
            ) : (
              <KyberProblemsPanel />
            )}
          </main>
        </div>
      </div>

    </div>
  )
}
