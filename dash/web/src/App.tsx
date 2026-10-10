import { useEffect, useMemo, useReducer, useState } from 'react'
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
 * samples for, newest-heaviest first, plus an "Unknown" tab for rows the
 * server could not attribute.
 *
 * The strip used to be a hardcoded three (claude-code, copilot, gemini), so a
 * machine collecting Codex, Cursor, OpenCode or Antigravity had that data
 * ingested and then no way to reach it. Harnesses with no samples are left out
 * rather than shown as empty tabs. Unknown is always present: quarantine and
 * problem rows with no stored harness would otherwise have no scope to be
 * read under.
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
  return [{ harness: 'all', name: 'All Harnesses' }, ...live, { harness: 'unknown', name: 'Unknown' }]
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

/** One guidance line beside the shell title for each top-level page; always visible, including loading and empty states. */
function pageDescription(section: KyberPage, location: SpineLocation): string | undefined {
  if (section === 'compare' || location.level === 'compare')
    return 'Choose two runs, from the same or different harnesses, to compare recorded results.'
  if (section === 'sessions') return 'Browse captured sessions; open one to inspect its context and activity.'
  if (section === 'quarantine' || location.level === 'quarantine')
    return 'Telemetry held out of analysis; inspect namespaces and reasons to understand why.'
  if (section === 'problems' || location.level === 'problems')
    return 'Ingestion and validation warnings or errors; inspect the code, message, and location to trace failures.'
  if (section === 'context-doctor' && location.level === 'context-doctor')
    return 'Find context advice for the selected harness; choose a session to narrow findings.'
  return undefined
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
 * The destinations in the header — the shell's one navigation.
 *
 * There was a second place to be: a left rail, empty on the desktop and an
 * off-canvas drawer below `md`, holding Compare. It is gone, and Compare came
 * up here rather than being copied. Two navigation surfaces for five
 * destinations meant the same place was offered twice, and the rail's copy was
 * the one hidden on a phone, so the one surface that changed with the viewport
 * was the one carrying a destination. A rail wide enough to be worth keeping
 * was not, and the content now uses the space it held.
 *
 * Context Doctor and Sessions sit here because the harness scope under this
 * header reaches both: they are one decision, so they are one tab row. Compare
 * joins them because the rail it lived in no longer exists, and a destination
 * with nowhere to live is one with no route to its page. It keeps its own A/B
 * harness filters — the shell scope does not reach it, and never did.
 */
export const NAV_TABS = [
  { key: 'context-doctor', label: 'Context Doctor' },
  { key: 'sessions', label: 'Sessions' },
  { key: 'compare', label: 'Compare' },
  { key: 'quarantine', label: 'Quarantine' },
  { key: 'problems', label: 'Problems' },
] as const

export type KyberPage = (typeof NAV_TABS)[number]['key']

export function KyberQuarantinePanel({ activeHarness }: { activeHarness?: string } = {}) {
  const scoped = activeHarness === undefined || activeHarness === 'all' ? undefined : activeHarness
  const { data, isLoading, isError } = useQuery({
    queryKey: scoped === undefined ? ['kyber-quarantine'] : ['kyber-quarantine', scoped],
    queryFn: async () => {
      const harness = scoped === undefined ? '' : `?harness=${encodeURIComponent(scoped)}`
      const r = await fetch(`/api/kyber/quarantine${harness}`)
      if (!r.ok) {
        const err = await r.json().catch(() => null)
        throw new Error(err?.error || `HTTP ${r.status}`)
      }
      return r.json()
    },
    retry: false,
  })

  if (isLoading) return <Skeleton className="h-20" />
  if (isError || !data) return <QuarantineView entries={[]} activeHarness={activeHarness} />

  const rawEntries = Array.isArray(data) ? data : (data as { entries?: unknown[] }).entries ?? []
  if (!Array.isArray(rawEntries)) {
    return <QuarantineView entries={[]} activeHarness={activeHarness} />
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
    const source = typeof e.source === 'string' && e.source.trim() !== '' ? e.source : null
    const harness = typeof e.harness === 'string' && (e.harness as string).trim() !== '' ? (e.harness as string) : null
    const name = typeof e.name === 'string' ? (e.name as string) : null
    const seenAt =
      typeof e.seen_at === 'string' ? (e.seen_at as string)
      : typeof e.timestamp === 'string' ? (e.timestamp as string)
      : null
    const timestamp = typeof e.timestamp === 'string' ? (e.timestamp as string) : null
    return {
      spanId: String(e.spanId || e.span_id || `quarantine-${idx}`),
      namespaces: ns,
      reason: String(e.reason || ''),
      ...(source !== null ? { source } : {}),
      ...(harness !== null ? { harness } : {}),
      ...(name !== null ? { name } : {}),
      ...(seenAt !== null ? { seenAt } : {}),
      ...(timestamp !== null ? { timestamp } : {}),
    }
  })

  return <QuarantineView entries={entries} activeHarness={activeHarness} />
}

export function KyberProblemsPanel({ activeHarness }: { activeHarness?: string } = {}) {
  const scoped = activeHarness === undefined || activeHarness === 'all' ? undefined : activeHarness
  const { data, isLoading, isError } = useQuery({
    queryKey: scoped === undefined ? ['kyber-problems'] : ['kyber-problems', scoped],
    queryFn: async () => {
      const harness = scoped === undefined ? '' : `?harness=${encodeURIComponent(scoped)}`
      const r = await fetch(`/api/kyber/problems${harness}`)
      if (!r.ok) {
        const err = await r.json().catch(() => null)
        throw new Error(err?.error || `HTTP ${r.status}`)
      }
      return r.json()
    },
    retry: false,
  })

  if (isLoading) return <Skeleton className="h-20" />
  if (isError || !data) return <ProblemsView problems={[]} activeHarness={activeHarness} />

  const rawProblems = Array.isArray(data) ? data : (data as { problems?: unknown[] }).problems ?? []
  if (!Array.isArray(rawProblems)) {
    return <ProblemsView problems={[]} activeHarness={activeHarness} />
  }

  const problems: ProblemEntry[] = rawProblems.map((raw, idx: number) => {
    const p = raw as Record<string, unknown>
    const harness = typeof p.harness === 'string' && (p.harness as string).trim() !== '' ? ((p.harness as string).trim() as string) : null
    const at =
      typeof p.at === 'string' ? (p.at as string)
      : typeof p.timestamp === 'string' ? (p.timestamp as string)
      : null
    const id = typeof p.id === 'string' || typeof p.id === 'number' ? (p.id as string | number) : `problem-${idx}`
    return {
      id,
      severity: (p.severity === 'error' ? 'error' : 'warning') as 'error' | 'warning',
      code: String(p.code || 'unknown'),
      message: String(p.message || ''),
      location: (p.location || p.at || p.harness || undefined) as string | undefined,
      spanId: (p.spanId || p.span_id || undefined) as string | undefined,
      ...(harness !== null ? { harness } : {}),
      ...(at !== null ? { at } : {}),
    }
  })

  return <ProblemsView problems={problems} activeHarness={activeHarness} />
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
  // Guidance line beside the shell title; undefined on drill-down detail pages.
  const shellDescription = pageDescription(section, location)
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
          Nothing is held back behind a hamburger for the narrow case either —
          the rail that had to be opened is deleted, so every destination is in
          this row at every width.
        */}
        <header
          data-testid="app-header"
          className="flex shrink-0 flex-wrap items-center gap-x-2 gap-y-2 rounded-md border border-border bg-card px-2 py-2 shadow-[0_2px_8px_rgba(0,0,0,0.03)] dark:shadow-[0_2px_8px_rgba(0,0,0,0.5)] sm:gap-x-4 sm:px-4 lg:h-12 lg:flex-nowrap lg:px-5 lg:py-0"
        >
          <div className="flex min-w-0 items-center gap-2">
            <div className="flex min-w-0 items-center gap-2">
              <LightsaberLogo className="h-6 w-6 shrink-0" />
              {/* Below the small breakpoint the mark carries the brand on its own.
                  Both operator tools and the mark together need more width than
                  a 375px viewport has, and the wordmark is the one piece of
                  this row that may give way: it is the part that is decoration
                  rather than a control. The logo keeps its accessible name,
                  and from `sm` up there is room for the word. */}
              <span className="max-sm:hidden truncate text-lg font-semibold tracking-[-0.02em] text-foreground">
                kyber<span className="text-primary">Dash</span>
              </span>
              <span className="ml-1 shrink-0 text-[11px] font-light uppercase tracking-[0.14em] text-tertiary-foreground max-sm:hidden">telemetry</span>
            </div>
          </div>

          {/* The navigation row: last on the wrapped layout, in place on the wide
              one. `basis-full` gives it a row of its own, `min-w-0` lets it
              scroll inside that row rather than widening the page, and each tab
              keeps its own width rather than wrapping to a second line. Every
              destination is here: the row scrolls rather than shedding a
              destination into a drawer, so nothing is reachable at one width
              and unreachable at another. */}
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
                  // Sessions is the one tab that is not a spine location, so it
                  // sets the section without a stack to reset. Compare is: it is
                  // a spine level, and a bare one — `pathFromLocation` has no
                  // URL for a comparison with no runs picked yet, so opening it
                  // leaves the address bar alone exactly as the rail did.
                  if (pg.key === 'sessions') {
                    setNotFoundId(null)
                    setSection('sessions')
                  } else {
                    goHeader({ level: pg.key })
                  }
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

        {/*
          The content column, directly under the harness scope.

          This used to be a row: a fixed-width rail beside it, and a hamburger
          that slid that rail off canvas below `md`. Both are gone, so this is
          now the one child that takes the column's remaining height, and the
          width the rail held is content width rather than chrome. There is no
          wrapper left to collapse, so nothing here depends on the viewport to
          be reachable.
        */}
        <main className="min-h-0 min-w-0 flex-1 overflow-y-auto pr-0.5">
          <div className="mb-3 flex flex-wrap items-baseline gap-x-3 gap-y-1">
            <h1 className="font-display text-xl tracking-tight text-foreground" data-testid="page-title">
              {pageHeading(section, location)}
            </h1>
            {shellDescription !== undefined && (
              <p data-testid="page-description" className="text-xs text-muted-foreground">
                {shellDescription}
              </p>
            )}
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
            <KyberQuarantinePanel activeHarness={activeHarness} />
          ) : (
            <KyberProblemsPanel activeHarness={activeHarness} />
          )}
        </main>
      </div>

    </div>
  )
}
