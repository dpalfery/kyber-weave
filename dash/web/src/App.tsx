import { useEffect, useMemo, useReducer, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'

import { cn } from '@/lib/utils'
import { Skeleton } from '@/components/ui/skeleton'
import { QuarantineView, type QuarantineEntry } from '@/components/analysis/QuarantineView'
import { ProblemsView, type ProblemEntry } from '@/components/analysis/ProblemsView'
import { LightsaberLogo } from '@/components/LightsaberLogo'
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
// and persists the choice to the same localStorage key.
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
      localStorage.setItem('codeburn-theme', next ? 'dark' : 'light')
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

export const NAV_TABS = [
  { key: 'context-doctor', label: 'Context Doctor' },
  { key: 'quarantine', label: 'Quarantine' },
  { key: 'problems', label: 'Problems' },
] as const

export type KyberPage = (typeof NAV_TABS)[number]['key'] | 'compare' | 'sessions'

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
}

/**
 * Renders the KyberDash shell and coordinates its top-level navigation and
 * diagnostic spine.
 */
export function App({ initialPage = 'context-doctor', initialPath }: AppProps = {}) {
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

  // Follow the OS theme live while the user has no explicit preference, so
  // flipping the system theme updates the dashboard without a reload.
  useEffect(() => {
    if (typeof window === 'undefined' || !window.matchMedia) return
    const mql = window.matchMedia('(prefers-color-scheme: dark)')
    const apply = () => {
      let saved: string | null = null
      try {
        saved = localStorage.getItem('codeburn-theme')
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
        if (cancelled || result.status !== 'ok') return
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
        dispatchSpine({ type: 'reset', stack: restored })
        setSection(pageFor(restored.at(-1) ?? { level: 'context-doctor' }))
        return
      }
      void createAncestryApi().then((api) =>
        resolveAncestry(restored.needsAncestry, api).then((result) => {
          if (result.status !== 'ok') return
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
    setSection(pageFor(next))
    const nextStack = applySpineAction(spine, { type: action, location: next })
    dispatchSpine({ type: action, location: next })
    const path = pathFromLocation(nextStack.at(-1)!)
    if (path) pushView(path, nextStack)
  }

  const goHeader = (next: SpineLocation) => {
    setSection(pageFor(next))
    dispatchSpine({ type: 'reset', stack: [next] })
    const path = pathFromLocation(next)
    if (path) pushView(path, [next])
  }

  return (
    <div className="min-h-screen bg-outer-background p-2.5 max-md:min-h-[100dvh]">
      <div className="flex h-[calc(100vh-20px)] flex-col gap-2.5 max-md:h-[calc(100dvh-20px)]">
        <header className="flex h-12 shrink-0 items-center gap-4 rounded-md border border-border bg-card px-5 shadow-[0_2px_8px_rgba(0,0,0,0.03)] dark:shadow-[0_2px_8px_rgba(0,0,0,0.5)] max-md:gap-3 max-md:px-3">
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
          <div className="flex items-center gap-2 max-md:shrink-0">
            <LightsaberLogo className="h-6 w-6" />
            <span className="text-lg font-semibold tracking-[-0.02em] text-foreground">
              kyber<span className="text-primary">Dash</span>
            </span>
            <span className="ml-1 text-[11px] font-light uppercase tracking-[0.14em] text-tertiary-foreground max-sm:hidden">telemetry</span>
          </div>

          <div className="ml-6 flex rounded-md border border-border bg-interactive-secondary p-0.5 max-md:ml-2 max-md:shrink-0" data-testid="nav-tabs">
            {NAV_TABS.map((pg) => (
              <button
                key={pg.key}
                type="button"
                data-testid={`nav-tab-${pg.key}`}
                onClick={() => {
                  if (pg.key === 'context-doctor') goHeader({ level: 'context-doctor' })
                  else if (pg.key === 'quarantine') goHeader({ level: 'quarantine' })
                  else goHeader({ level: 'problems' })
                }}
                className={cn(
                  'rounded-[5px] px-3 py-1 text-xs font-medium transition-colors',
                  section === pg.key ? 'bg-active-primary text-foreground shadow-sm' : 'text-tertiary-foreground hover:text-foreground',
                )}
              >
                {pg.label}
              </button>
            ))}
          </div>

          <div className="ml-auto flex items-center gap-2 max-md:min-w-0 max-md:overflow-x-auto max-md:[-ms-overflow-style:none] max-md:[scrollbar-width:none] max-md:[&::-webkit-scrollbar]:hidden">
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
            const active = location.level === 'harness' && location.harnessId === tab.harness
            return (
              <button
                key={tab.harness}
                type="button"
                onClick={() => {
                  if (tab.harness === 'all') {
                    openSpine({ level: 'context-doctor' }, 'goTo')
                  } else {
                    openSpine({ level: 'harness', harnessId: tab.harness })
                  }
                }}
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
            <div className="flex flex-col gap-1">
              <SideLink
                testId="nav-rail-context-doctor"
                active={section === 'context-doctor'}
                onClick={() => {
                  goHeader({ level: 'context-doctor' })
                  setSidebarOpen(false)
                }}
              >
                Context Doctor
              </SideLink>
              <SideLink
                testId="nav-rail-sessions"
                active={section === 'sessions'}
                onClick={() => {
                  setSection('sessions')
                  setSidebarOpen(false)
                }}
              >
                Sessions
              </SideLink>
              <SideLink
                testId="nav-rail-compare"
                active={section === 'compare'}
                onClick={() => {
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
                {section === 'compare'
                  ? 'Compare'
                  : section === 'sessions'
                    ? 'Sessions'
                    : showSpine
                      ? 'Context Doctor'
                      : section === 'quarantine'
                        ? 'Quarantine'
                        : 'Problems'}
              </h1>
            </div>

            {showSpine ? (
              location.level === 'context-doctor' ? (
                <ContextDoctor
                  onSelectHarness={(harnessId) => openSpine({ level: 'harness', harnessId })}
                  onSelectRun={(runId, harnessId) => openSpine({ level: 'run', runId, harnessId })}
                  onSelectFinding={(findingId) => openSpine({ level: 'finding', findingId })}
                  onSelectTurn={(turnIndex, executionId, runId) => openSpine({ level: 'turn', turnIndex, executionId, runId })}
                />
              ) : location.level === 'harness' ? (
                <HarnessDetail
                  harnessId={location.harnessId!}
                  onSelectAll={() => openSpine({ level: 'context-doctor' }, 'goTo')}
                  onSelectRun={(runId) => openSpine({ level: 'run', harnessId: location.harnessId, runId })}
                  onSelectFinding={(findingId) => openSpine({ level: 'finding', harnessId: location.harnessId, findingId })}
                  onSelectTurn={(turnIndex, executionId, runId) => openSpine({ level: 'turn', harnessId: location.harnessId, runId, executionId, turnIndex })}
                />
              ) : location.level === 'run' || location.level === 'execution' ? (
                <RunDetail
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
              <Sessions />
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
