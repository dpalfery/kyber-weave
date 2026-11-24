import { useState, useMemo, useEffect } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Skeleton } from '../components/ui/skeleton.js'
import {
  fetchFindings,
  fetchKyberSession,
  fetchKyberSessionPage,
  isNotFoundError,
  type FindingsPage,
  type KyberFinding,
  type KyberSessionSummary,
} from '../lib/kyberApi.js'
import { Pager } from '../components/ui/Pager.js'
import { HierarchyBreadcrumb, FindingList, BaselineSelect } from '../components/analysis/index.js'

export interface ContextDoctorProps {
  /**
   * Initial findings page, or a bare row list (the coverage suites pass
   * `[]`: it normalizes to an empty envelope). Prefer the envelope — a bare
   * list cannot carry totals, counts, or the unknown-window figure.
   */
  initialFindings?: FindingsPage | KyberFinding[]
  /**
   * The harness the owner has selected; `undefined` or `'all'` means every
   * harness. The scope belongs to the header above this page, so the findings
   * queries carry it rather than a second harness selector here — two
   * selectors are two answers to "which harness am I looking at".
   */
  activeHarness?: string
  /** One session, or `undefined` for every session in scope. */
  sessionId?: string
  /**
   * Reports a session chosen in this page's selector to the owner, which
   * holds the shared selection. Absent, the page keeps the choice in internal
   * state and stands alone. Its presence is what makes the page controlled:
   * the owner's `sessionId` is then the whole scope, including its absence.
   */
  onSessionChange?: (sessionId: string | undefined) => void
  onSelectRun?: (runId: string, harnessId?: string) => void
  onSelectFinding?: (findingId: string) => void
  onSelectTurn?: (turnIndex: number, executionId?: string, runId?: string) => void
}

/**
 * The session scope a findings query runs under, or `undefined` for every
 * harness. `'all'` is the tab the header shows for the whole workspace, and
 * the API has no such harness — sending it would filter to nothing.
 */
export function harnessScopeOf(activeHarness: string | undefined): string | undefined {
  return activeHarness === undefined || activeHarness === '' || activeHarness === 'all'
    ? undefined
    : activeHarness
}

/**
 * The session scope's one control: a search combobox over the store's `q`,
 * paged like every list (Task T3).
 *
 * The options are the page the store answered — narrowed by harness above
 * this page and by the search text here — so a store the size of a year of
 * work answers a page, never its whole inventory. The input doubles as the
 * display of the current scope while it is untouched: the validated
 * session's label, or its id when the label is blank. Typing replaces that
 * display, and the committed search trails the keystrokes by a debounce, so
 * a burst of characters asks the store once.
 *
 * Keyboard: the listbox opens on focus or ArrowDown, ArrowDown/ArrowUp move
 * the active option, Enter chooses it, Escape closes without choosing. Every
 * option is a real element a click or an Enter reaches.
 */
export function SessionPicker({
  effectiveSessionId,
  selectedLabel,
  options,
  loading,
  fetching = false,
  error,
  onSelect,
  onSearch,
  page,
  limit,
  total,
  onPageChange,
}: {
  /** The session the scope stands on after validation, or undefined for all. */
  effectiveSessionId?: string
  /** The validated session's stored label, shown while the input is untouched. */
  selectedLabel?: string | null
  /** The page's session rows, already narrowed by the store. */
  options: readonly KyberSessionSummary[]
  /** True while the rows are in flight and nothing is on screen yet. */
  loading: boolean
  /** True while a page is in flight after rows are on screen. */
  fetching?: boolean
  /** The rows' read failure, when the list could not be read at all. */
  error: string | null
  /** Reports the session chosen here; undefined is the All-sessions scope. */
  onSelect: (sessionId: string | undefined) => void
  /** Reports the committed search text; '' is no filter. */
  onSearch: (q: string) => void
  /** The pager's window, as the sessions page answered it. */
  page: number
  limit: number
  total: number | undefined
  onPageChange: (page: number) => void
}) {
  const [open, setOpen] = useState(false)
  // Null while the input is untouched: it then displays the current scope
  // rather than a stale search the user cleared by choosing an option.
  const [edited, setEdited] = useState<string | null>(null)
  const [activeIndex, setActiveIndex] = useState(-1)
  const pendingSearch = useMemo(() => ({ timer: undefined as ReturnType<typeof setTimeout> | undefined }), [])

  const displayValue = edited ?? (selectedLabel != null && selectedLabel.length > 0 ? selectedLabel : effectiveSessionId ?? '')
  const optionIds = ['all', ...options.map((session) => sessionIdOf(session))]

  const cancelPendingSearch = () => {
    if (pendingSearch.timer !== undefined) {
      clearTimeout(pendingSearch.timer)
      pendingSearch.timer = undefined
    }
  }

  const commitSearch = (text: string) => {
    cancelPendingSearch()
    pendingSearch.timer = setTimeout(() => onSearch(text), SESSION_PICKER_SEARCH_DEBOUNCE_MS)
  }

  const choose = (id: string | undefined) => {
    cancelPendingSearch()
    setEdited(null)
    onSearch('')
    setOpen(false)
    setActiveIndex(-1)
    onSelect(id)
  }

  const moveActive = (next: number) => {
    setActiveIndex(Math.min(Math.max(next, 0), optionIds.length - 1))
  }

  return (
    <div className="relative">
      <input
        type="text"
        id="context-doctor-session"
        role="combobox"
        aria-expanded={open}
        aria-controls="context-doctor-session-listbox"
        aria-autocomplete="list"
        aria-activedescendant={open && activeIndex >= 0 ? `session-option-${optionIds[activeIndex]}` : undefined}
        data-testid="context-doctor-session-combobox"
        className="text-density-xs text-foreground"
        placeholder="All sessions — search to narrow"
        value={displayValue}
        onChange={(event) => {
          setEdited(event.target.value)
          setOpen(true)
          setActiveIndex(-1)
          commitSearch(event.target.value)
        }}
        onFocus={() => {
          setOpen(true)
          setActiveIndex(-1)
        }}
        onBlur={(event) => {
          // Focus moving into the listbox is not a close: the click that
          // chooses an option passes through blur first.
          if (event.relatedTarget instanceof Element && event.relatedTarget.closest('[role="listbox"]')) return
          setOpen(false)
          setActiveIndex(-1)
        }}
        onKeyDown={(event) => {
          if (event.key === 'ArrowDown') {
            event.preventDefault()
            if (!open) {
              setOpen(true)
              setActiveIndex(0)
              return
            }
            moveActive(activeIndex + 1)
          } else if (event.key === 'ArrowUp') {
            event.preventDefault()
            if (open) moveActive(activeIndex - 1)
          } else if (event.key === 'Enter') {
            if (open && activeIndex >= 0) {
              event.preventDefault()
              const chosen = optionIds[activeIndex]
              choose(chosen === 'all' ? undefined : chosen)
            }
          } else if (event.key === 'Escape') {
            setOpen(false)
            setActiveIndex(-1)
          }
        }}
      />
      {open && (
        <ul
          id="context-doctor-session-listbox"
          role="listbox"
          aria-label="Sessions"
          data-testid="context-doctor-session-listbox"
          className="absolute z-10 mt-1 max-h-72 w-full overflow-y-auto rounded-md border border-border bg-card shadow-lg"
        >
          <li
            role="option"
            id="session-option-all"
            data-testid="session-option-all"
            aria-selected={effectiveSessionId === undefined}
            onMouseDown={(event) => {
              event.preventDefault()
              choose(undefined)
            }}
            className="cursor-pointer px-3 py-1.5 text-density-xs text-foreground hover:bg-interactive-secondary"
          >
            All sessions
          </li>
          {loading && (
            <li className="px-3 py-1.5 text-density-xs text-muted-foreground" data-testid="context-doctor-session-loading">
              Reading sessions…
            </li>
          )}
          {error !== null && !loading && (
            <li className="px-3 py-1.5 text-density-xs text-muted-foreground" data-testid="context-doctor-session-list-error">
              {error}
            </li>
          )}
          {options.map((session) => {
            const id = sessionIdOf(session)
            return (
              <li
                key={id}
                role="option"
                id={`session-option-${id}`}
                data-testid={`session-option-${id}`}
                aria-selected={effectiveSessionId === id}
                onMouseDown={(event) => {
                  event.preventDefault()
                  choose(id)
                }}
                className="cursor-pointer px-3 py-1.5 text-density-xs text-foreground hover:bg-interactive-secondary"
                title={id}
              >
                {sessionOptionLabel(session)}
              </li>
            )
          })}
          {total !== undefined && total > limit && (
            <li className="px-2 py-1">
              <Pager page={page} limit={limit} total={total} onPageChange={onPageChange} loading={fetching} ariaLabel="Session picker pages" />
            </li>
          )}
        </ul>
      )}
    </div>
  )
}

export type HarnessCatalogEntry = {
  /** Canonical KyberDash storage identifier, used verbatim in API filters. */
  harness: 'all' | 'claude-code' | 'copilot' | 'gemini'
  name: string
}

/**
 * Display names for harnesses the survey knows about. This is a labelling
 * table, NOT the list of tabs: the tab strip is built from the harnesses the
 * store actually holds (see `useHarnessTabs`), so a harness collected on this
 * machine is never hidden because it was missing from a hardcoded list.
 *
 * Folded front-ends have no entry (issue #182, Q5): `cursor-agent` is
 * `cursor` at the derived layer, so a label here would imply a tab that no
 * longer exists.
 */
export const HARNESS_DISPLAY_NAMES: Readonly<Record<string, string>> = {
  all: 'All Harnesses',
  claude: 'Claude Code',
  'claude-code': 'Claude Code',
  copilot: 'GitHub Copilot',
  'copilot-cli': 'GitHub Copilot CLI',
  gemini: 'Gemini',
  antigravity: 'Antigravity',
  codex: 'Codex',
  cursor: 'Cursor',
  opencode: 'OpenCode',
  'kilo-code': 'Kilo Code',
  'roo-code': 'Roo Code',
  cline: 'Cline',
  windsurf: 'Windsurf',
  aider: 'Aider',
  droid: 'Droid',
  pi: 'Pi',
}

/** The harness's display name, falling back to its own id. */
export function harnessDisplayName(harness: string): string {
  return HARNESS_DISPLAY_NAMES[harness] ?? harness
}

export const HARNESS_CATALOG: readonly HarnessCatalogEntry[] = [
  { harness: 'all', name: 'All Harnesses' },
]

/** Page size for the workspace findings browser (issue #191). */
export const FINDINGS_PAGE_SIZE = 25

/** Rows per page the session picker asks the store for (Task T3). */
export const SESSION_PICKER_PAGE_SIZE = 100

/** How long the picker's search box waits before it narrows the options. */
export const SESSION_PICKER_SEARCH_DEBOUNCE_MS = 400

/** The canonical session id, whichever spelling the payload used. */
export function sessionIdOf(session: KyberSessionSummary): string {
  return session.sessionId ?? session.session_id
}

/** What the session picker lists for one option. */
export function sessionOptionLabel(session: KyberSessionSummary): string {
  const id = sessionIdOf(session)
  const label = session.label ?? null
  return label !== null && label.length > 0 ? `${label} — ${id}` : id
}

export interface FindingsBrowserViewProps {
  findings: readonly KyberFinding[]
  total: number
  detectorCounts: Readonly<Record<string, number>>
  unknownWindowSessions?: number
  detectorFilter?: string
  /** Plain-language statement of the scope above this list. */
  scopeNote?: string
  /** True while a page is in flight. The skeleton shows only when there is
   * nothing stored yet (review M1): pages already on screen stay up. */
  loading: boolean
  /** Bounded fetch failure. Raw server text never renders (council review). */
  error?: string | null
  onDetectorChange?: (detector: string | undefined) => void
  onLoadMore?: () => void
  onRetry?: () => void
  onSelectFinding?: (findingId: string) => void
  onSelectTurn?: (turnIndex: number, executionId?: string) => void
  onSelectExecution?: (executionId: string) => void
}

/**
 * The workspace findings browser as pure presentation (issue #191, review
 * M1): every prop is renderable without effects, so the paged states —
 * including a fully accumulated second page — are directly testable.
 */
export function FindingsBrowserView({
  findings,
  total,
  detectorCounts,
  unknownWindowSessions,
  detectorFilter,
  scopeNote,
  loading,
  error = null,
  onDetectorChange = () => {},
  onLoadMore = () => {},
  onRetry = () => {},
  onSelectFinding,
  onSelectTurn,
  onSelectExecution,
}: FindingsBrowserViewProps) {
  const detectorChips = useMemo(
    () =>
      Object.entries(detectorCounts)
        .filter(([, count]) => count > 0)
        .sort(([, a], [, b]) => b - a),
    [detectorCounts],
  )
  // One retry control, rendered in whichever error surface applies.
  const retry = (
    <button type="button" data-testid="findings-retry" onClick={onRetry}>
      Retry
    </button>
  )

  return (
    <section aria-label="All workspace findings" data-testid="all-workspace-findings">
      <div className="flex flex-wrap items-baseline justify-between gap-density-cluster">
        <h3 className="font-display text-density-base font-semibold text-foreground">
          All Workspace Findings ({total})
        </h3>
        {scopeNote !== undefined && (
          <p data-testid="findings-scope-note" className="text-density-xs text-muted-foreground">
            {scopeNote}
          </p>
        )}
      </div>

      {/* Suppression is stated only when it is measured. While the envelope
      is unavailable — a mid-list failure cleared it — the count is unknown,
      and a banner reading `0` would claim every suppressed session is
      measurable, which is the opposite of what the failure means (F2). */}
      {unknownWindowSessions !== undefined && unknownWindowSessions > 0 && (
        <p data-testid="unknown-window-banner" className="text-density-xs text-muted-foreground mt-density-hair">
          {unknownWindowSessions} session{unknownWindowSessions === 1 ? '' : 's'} with unknown context window
          {' '}— pressure unmeasurable, not zero. Findings are suppressed for these sessions until a reported,
          declared, or catalog window covers the model.
        </p>
      )}
      {unknownWindowSessions === undefined && error !== null && (
        <p data-testid="unknown-window-banner" className="text-density-xs text-muted-foreground mt-density-hair">
          Sessions with unknown context window — pressure unmeasurable, not zero. How many are
          suppressed is unknown: the findings request failed, so this is not a measured zero. A window
          can be reported, declared, or taken from the catalog.
        </p>
      )}

      <div className="flex flex-wrap gap-density-cluster mt-density-hair" data-testid="findings-detector-chips">
        {detectorFilter !== undefined && (
          <button
            type="button"
            data-testid="findings-clear-filters"
            title="Clear detector filter"
            onClick={() => {
              onDetectorChange(undefined)
            }}
          >
            Clear ×
          </button>
        )}
        {detectorChips.map(([detector, count]) => {
          const active = detectorFilter === detector
          return (
            <button
              key={detector}
              type="button"
              data-testid={`findings-detector-chip-${detector}`}
              aria-pressed={active}
              title={active ? `Clear ${detector} filter` : `Show only ${detector} findings`}
              onClick={() => onDetectorChange(active ? undefined : detector)}
            >
              {detector} ×{count}
            </button>
          )
        })}
      </div>

      {loading && findings.length === 0 && error === null ? (
        <Skeleton className="h-44 w-full" />
      ) : error !== null && findings.length === 0 ? (
        <div data-testid="findings-error" role="alert">
          <p>Findings failed to load.</p>
          {retry}
        </div>
      ) : (
        <>
          {/* A failure with rows on screen is a retryable wedge, not a reason
              to hide what the user already read: the banner rides above the
              retained rows, and the panel above stays the no-rows state only
              (F2). Without it a page-2 failure is indistinguishable from
              "you have seen everything". */}
          {error !== null && (
            <div data-testid="findings-inline-error" role="status">
              <p>More findings failed to load. The rows below are the ones already loaded.</p>
              {retry}
            </div>
          )}
          <FindingList
            findings={[...findings]}
            title=""
            description="Server-ranked across the workspace; filters narrow the set before paging."
            onSelectFinding={onSelectFinding}
            onSelectTurn={onSelectTurn}
            onSelectExecution={onSelectExecution}
          />
          {findings.length < total && (
            <button
              type="button"
              data-testid="findings-load-more"
              disabled={loading}
              title={loading ? 'Loading the next page…' : 'Load the next page of findings'}
              onClick={onLoadMore}
            >
              Load more ({findings.length} of {total})
            </button>
          )}
        </>
      )}
    </section>
  )
}

/**
 * One stored page of the browser, tagged with the filter scope it belongs to
 * and the envelope it was served under.
 *
 * <remarks>
 * The envelope travels with the rows because the rows are only meaningful
 * under it: a mid-list failure clears `data`, and a rebuild moves `total`
 * under rows that are already on screen. A page that kept only its rows would
 * leave the heading, the suppression count and the next offset to be
 * re-derived from whatever the failed query returned — which is how a
 * page-2 failure rendered "All Workspace Findings (0)" above 25 retained
 * rows, and dropped the banner that says some of those sessions are
 * unmeasurable.
 * </remarks>
 */
export type AccumulatedPage = {
  scope: string
  offset: number
  total: number
  detectorCounts: Readonly<Record<string, number>>
  unknownWindowSessions: number
  rows: KyberFinding[]
}

/**
 * The next accumulated-page list after a page arrives.
 *
 * The first page is stored like any other. Skipping offset zero is what made
 * "Load more" replace the list rather than extend it: page one never entered
 * `appended`, so advancing the offset left nothing to extend and the rendered
 * rows collapsed to the newly fetched page alone. Re-storing the same page is
 * a no-op, which is what keeps a refetch from duplicating it.
 *
 * The no-op returns `pages` itself, not a copy: the accumulate effect depends
 * on the stored list, so a fresh array per render would loop forever.
 *
 * <remarks>
 * A `total` that differs from the scope's stored one means the server's answer
 * no longer describes the rows underneath it — a rebuild deleted or re-ranked
 * findings inside a window already on screen. Those pages are dropped rather
 * than left on screen for the life of the mount (F4). The caller restarts the
 * offset at zero; storing only the new page here is what makes that restart
 * render the new page alone.
 * </remarks>
 */
export function nextAccumulated(
  pages: readonly AccumulatedPage[],
  scope: string,
  page: Pick<FindingsPage, 'offset' | 'findings' | 'total' | 'detectorCounts' | 'unknownWindowSessions'>,
): AccumulatedPage[] {
  const { offset, findings, total, detectorCounts, unknownWindowSessions } = page
  const sameIds = (a: readonly KyberFinding[], b: readonly KyberFinding[]): boolean =>
    a.length === b.length && a.every((row, i) => row.id === b[i]!.id)
  const storedForScope = pages.filter((entry) => entry.scope === scope)
  const sameTotal = storedForScope.every((entry) => entry.total === total)
  const entry: AccumulatedPage = {
    scope,
    offset,
    total,
    detectorCounts,
    unknownWindowSessions,
    rows: [...findings],
  }
  // The moved total supersedes the scope: nothing stored under the old one can
  // be re-ranked into place.
  if (!sameTotal) return [entry]
  const ix = pages.findIndex((other) => other.scope === scope && other.offset === offset)
  // A rebuild can shift or replace a stored page's rows (review): an
  // identical re-serve is a no-op, but changed rows replace the stale page
  // instead of haunting the list for the life of the mount.
  if (ix !== -1 && sameIds(pages[ix]!.rows, findings)) return pages as AccumulatedPage[]
  const next = [...pages]
  if (ix === -1) next.push(entry)
  else next[ix] = entry
  return next
}

/**
 * The rows the browser renders: every page already stored for `scope`, in
 * offset order.
 *
 * The first page is stored like any other. Treating it specially is what made
 * "Load more" replace the list rather than extend it — page 1 was never
 * accumulated, so the moment the offset left zero the rendered rows collapsed
 * to the newly fetched page alone.
 *
 * Tagging each page with its scope stops a page fetched under a previous
 * harness, session or detector from being re-appended after the filter
 * changes. `current` covers the window before the effect has stored the first
 * page, which is also the only thing that exists during static render.
 */
export function browserRows(
  pages: readonly AccumulatedPage[],
  scope: string,
  offset: number,
  current: readonly KyberFinding[] | undefined,
): KyberFinding[] {
  const stored = pages.filter((page) => page.scope === scope).sort((a, b) => a.offset - b.offset)
  // A rebuild or refresh can shift the server window by one: the same
  // finding may open page 2 after closing page 1. Dedupe by id (first
  // occurrence wins) so the list never renders a row twice (review).
  const seen = new Set<string>()
  const accumulated = stored.flatMap((page) => page.rows).filter((row) => {
    if (seen.has(row.id)) return false
    seen.add(row.id)
    return true
  })
  const firstPageUnstored = offset === 0 && current !== undefined && !stored.some((page) => page.offset === 0)
  return firstPageUnstored ? [...current, ...accumulated] : accumulated
}

/**
 * The envelope the scope's retained rows were served under, or `undefined`
 * when nothing is stored for it.
 *
 * <remarks>
 * This answers "what did the server last say about this list?" for the fields
 * the row count cannot answer: the total, the per-detector counts, and the
 * unknown-window figure. A mid-list failure clears the query's `data`, and
 * reading those fields off the failed envelope rewrote a 115 total to 0 and
 * dropped the banner saying three sessions are unmeasurable — "all clear"
 * invented from a fetch error.
 * </remarks>
 */
export function retainedEnvelope(
  pages: readonly AccumulatedPage[],
  scope: string,
): { total: number; detectorCounts: Readonly<Record<string, number>>; unknownWindowSessions: number } | undefined {
  const stored = pages.filter((page) => page.scope === scope).sort((a, b) => a.offset - b.offset)
  const newest = stored[stored.length - 1]
  if (newest === undefined) return undefined
  return {
    total: newest.total,
    detectorCounts: newest.detectorCounts,
    unknownWindowSessions: newest.unknownWindowSessions,
  }
}

/**
 * How far the **server** has served this scope: the extent of the contiguous
 * run of stored pages starting at offset 0, or 0 when there is none.
 *
 * A page stored beyond a gap is not counted until the gap is refetched, and a
 * page that serves zero rows ends the run — neither advances the offset, so
 * paging on from them would skip rows the server never served.
 *
 * <remarks>
 * Not `browserRows(...).length`. A rebuild can shift a page window by one, so
 * the same finding opens page two after closing page one; `browserRows` dedupes
 * the overlap by id, and paging from the painted count then re-requests a row
 * the server already served. The stored pages carry the served extent
 * directly, and it survives the dedupe.
 *
 * Sorted by offset first, like `browserRows` and `retainedEnvelope`: the
 * stored list is in arrival order, not offset order, and the F4 restart makes
 * that visible — it stores the page the rebuild was caught at and then re-fetches
 * from 0, so the list reads `[p25, p0]`. Walking arrival order skips `p25`,
 * never returns for it, and reports the extent of `p0` alone; the next offset
 * handed to `onLoadMore` is then the one already loaded and the browser
 * re-fetches the same page forever. `.filter()` copies, so the sort cannot
 * mutate the caller's list.
 * </remarks>
 */
export function servedOffset(pages: readonly AccumulatedPage[], scope: string): number {
  return pages
    .filter((page) => page.scope === scope)
    .sort((a, b) => a.offset - b.offset)
    .reduce((extent, page) => (page.offset === extent ? page.offset + page.rows.length : extent), 0)
}

/**
 * The next page offset from the extent the server has served this scope
 * (`servedOffset`), or `undefined` when everything is loaded and the button
 * hides.
 *
 * <remarks>
 * Two quick clicks on Load more resolving to the same offset is the
 * click-disable plus React batching, not a property of the count: a fetch in
 * flight does not freeze it, and the argument is deliberately the served
 * extent rather than the painted row count so a deduped overlap does not make
 * the browser re-request a row it already has.
 * </remarks>
 */
export function nextBrowserOffset(served: number, total: number): number | undefined {
  return served < total ? served : undefined
}

/**
 * The current page eligible for display (review M1): placeholder rows from
 * a previous key are never the current page of a new scope — they belong
 * to no scope until the server answers.
 */
export function currentBrowserPage(
  findings: readonly KyberFinding[] | undefined,
  isPlaceholderData: boolean,
): readonly KyberFinding[] | undefined {
  return isPlaceholderData ? undefined : findings
}

/**
 * Whether the browser query may keep the previous page on screen while the
 * next one loads. Offset-only moves stay within the same filter scope, so the
 * stored rows are still valid; a detector, harness or session change
 * re-scopes the list and the old scope's rows must not pose as the new one's.
 * All three scope slots are compared — checking only some would keep stale rows
 * when another changes.
 */
export function shouldKeepPreviousData(
  prevKey: readonly unknown[] | undefined,
  nextKey: readonly unknown[],
): boolean {
  return (
    prevKey?.[1] === nextKey[1] &&
    prevKey?.[2] === nextKey[2] &&
    prevKey?.[3] === nextKey[3]
  )
}

/**
 * The scope the browser list is showing, in words. A narrowed list says so:
 * a reader who lands on one harness's findings has to be able to tell that
 * from the whole workspace.
 */
export function scopeNoteFor(
  harness: string | undefined,
  session: string | undefined,
  sessionLabel?: string,
): string {
  const harnessPart = harness === undefined ? 'All harnesses' : harnessDisplayName(harness)
  const sessionPart =
    session === undefined ? 'all sessions' : `session ${sessionLabel ?? session}`
  return `${harnessPart} · ${sessionPart}`
}

/**
 * Renders the Context Doctor landing page: findings and the scope they were
 * drawn in. The ingest coverage readout and the maintenance controls are not
 * here — the header's Status and Maintenance panels own them
 * (`DashboardTools`) — and the scorecard matrix was removed from this page
 * outright. This is the diagnosis, not the operator's console.
 */
export function ContextDoctor({
  initialFindings,
  activeHarness,
  sessionId: selectedSession,
  onSessionChange,
  onSelectRun,
  onSelectFinding,
  onSelectTurn,
}: ContextDoctorProps) {
  const [baseline, setBaseline] = useState('none')
  const [detectorFilter, setDetectorFilter] = useState<string | undefined>(undefined)
  // Standalone session state, for the page mounted without an owner. When an
  // owner supplies `sessionId`, that value is the scope and this state is not
  // consulted.
  const [localSession, setLocalSession] = useState<string | undefined>(undefined)
  // Offset paging (issue #191): Load more advances `offset` and appends the
  // next page instead of refetching a growing prefix. The offset is tagged
  // with the scope it belongs to, so a scope change restarts at page one
  // without an effect having to run first.
  const [paging, setPaging] = useState<{ scope: string; offset: number }>({ scope: '', offset: 0 })
  const [appended, setAppended] = useState<AccumulatedPage[]>([])

  const harnessFilter = harnessScopeOf(activeHarness)
  // Controlled or standalone, decided once by whether an owner is listening.
  // A nullish fallback would not do: when a controlled owner clears
  // `sessionId` (All sessions, or a harness change), the session this page
  // last chose itself would take over again and keep querying a session
  // nobody selected.
  const controlled = onSessionChange !== undefined
  const sessionId = controlled ? selectedSession : localSession

  // A bare row list normalizes to an empty-total envelope (see props).
  const initialPage: FindingsPage | undefined = Array.isArray(initialFindings)
    ? {
        findings: initialFindings,
        total: initialFindings.length,
        offset: 0,
        detectorCounts: {},
        unknownWindowSessions: 0,
      }
    : initialFindings

  // The picker's one search-and-page scope. The store narrows the options —
  // by the harness scope above this page, then by the search text here — so
  // the picker never downloads the inventory whole.
  const [pickerQuery, setPickerQuery] = useState<{ q: string; page: number }>({ q: '', page: 1 })
  // A harness change restarts the picker at its first page: page N of the
  // harness just left is not where a viewer narrowing to another harness is,
  // and it may be past the end of the new list. Adjusted during render (the
  // React state-when-props-change pattern, as `useSectionPage` does for a
  // session change), so the out-of-range page request never leaves.
  const [prevHarnessFilter, setPrevHarnessFilter] = useState(harnessFilter)
  if (prevHarnessFilter !== harnessFilter) {
    setPrevHarnessFilter(harnessFilter)
    setPickerQuery((previous) => ({ ...previous, page: 1 }))
  }
  const {
    data: sessionPage,
    isLoading: loadingSessions,
    isFetching: fetchingSessions,
    isError: sessionsError,
    error: sessionsErrorValue,
    refetch: refetchSessions,
  } = useQuery({
    queryKey: ['kyber-sessions', harnessFilter ?? 'all', pickerQuery.q, pickerQuery.page],
    queryFn: () =>
      fetchKyberSessionPage({
        page: pickerQuery.page,
        limit: SESSION_PICKER_PAGE_SIZE,
        harness: harnessFilter,
        q: pickerQuery.q === '' ? undefined : pickerQuery.q,
      }),
  })
  const pickerRows = useMemo(() => sessionPage?.entries ?? [], [sessionPage])

  // A selected or deep-linked session validates through the bounded summary
  // route — one bounded read per id — never through list membership: a
  // session past page one of the picker would otherwise read as unavailable.
  // While the record is in flight the session stands, so a metadata read
  // never widens the findings scope on its own.
  const {
    data: selectedSummary,
    isLoading: selectedLoading,
    isError: selectedError,
    error: selectedErrorValue,
    refetch: refetchSelected,
  } = useQuery({
    queryKey: ['kyber-session-summary', sessionId],
    queryFn: () => fetchKyberSession(sessionId!),
    enabled: sessionId !== undefined,
    // A 404 is the answer, not a flake: nothing retries it. The one retry a
    // transient failure gets runs immediately — a bounded identity read has
    // no backoff to wait out.
    retry: (failureCount, failure) => !isNotFoundError(failure) && failureCount < 1,
    retryDelay: 0,
  })

  // Resolution is render-time where the record is already known: a session
  // whose record answers for another harness, or answers nothing at all
  // (404), cannot scope anything, and one request must not go out under the
  // stale pairing before the clearing lands.
  const selectedKnown = selectedSummary !== undefined
  const harnessMismatch =
    selectedKnown &&
    harnessFilter !== undefined &&
    (selectedSummary.harness ?? '').toLowerCase() !== harnessFilter.toLowerCase()
  const selectedMissing = sessionId !== undefined && selectedError && isNotFoundError(selectedErrorValue)
  const selectedUnreadable = sessionId !== undefined && selectedError && !selectedMissing
  const sessionStands = sessionId !== undefined && !harnessMismatch && !selectedMissing
  const effectiveSessionId = sessionStands ? sessionId : undefined

  // A scope that lost its session is reported, not applied: the owner
  // decides. The page clears its own standalone choice at the same moment.
  useEffect(() => {
    if (sessionId === undefined || sessionStands) return
    setLocalSession(undefined)
    onSessionChange?.(undefined)
  }, [sessionId, sessionStands, onSessionChange])

  // Every page is stored under the scope it was fetched for, including the
  // first, so advancing the offset extends the list instead of replacing it.
  // The scope names the harness, the session and the detector: a page fetched
  // under one of them must never be re-appended under another.
  const browseScope = `${detectorFilter ?? ''}|${harnessFilter ?? ''}|${effectiveSessionId ?? ''}`
  const offset = paging.scope === browseScope ? paging.offset : 0
  // Seeded initial rows describe the whole workspace, so they are only ever
  // the page one of an unscoped list.
  const unscoped = harnessFilter === undefined && effectiveSessionId === undefined

  const setOffset = (next: number) => {
    setPaging({ scope: browseScope, offset: next })
  }

  const resetBrowse = (update: () => void) => {
    update()
    setOffset(0)
    setAppended([])
  }

  // Headline findings, under the same scope as the browser below. The query
  // fetches a full page; the card keeps four on screen and scrolls the rest.
  const { data: headlineData, isLoading: loadingFindings } = useQuery({
    queryKey: ['kyber-findings-workspace', harnessFilter ?? '', effectiveSessionId ?? ''],
    queryFn: () =>
      fetchFindings({
        ...(harnessFilter !== undefined ? { harness: harnessFilter } : {}),
        ...(effectiveSessionId !== undefined ? { sessionId: effectiveSessionId } : {}),
        limit: FINDINGS_PAGE_SIZE,
      }),
    initialData: unscoped ? initialPage : undefined,
  })

  // Full workspace browser (issue #191): server-side scope and detector
  // filters with per-detector counts over the narrowed set, paged by offset.
  // Counts ignore paging, so chips stay comparable across pages.
  const {
    data: pageData,
    isLoading: loadingBrowser,
    isPlaceholderData,
    error: browserError,
    refetch: refetchBrowser,
  } = useQuery({
    queryKey: [
      'kyber-findings-browser',
      detectorFilter ?? '',
      harnessFilter ?? '',
      effectiveSessionId ?? '',
      offset,
    ],
    queryFn: () =>
      fetchFindings({
        ...(detectorFilter !== undefined ? { detector: detectorFilter } : {}),
        ...(harnessFilter !== undefined ? { harness: harnessFilter } : {}),
        ...(effectiveSessionId !== undefined ? { sessionId: effectiveSessionId } : {}),
        limit: FINDINGS_PAGE_SIZE,
        offset,
      }),
    initialData: offset === 0 && detectorFilter === undefined && unscoped ? initialPage : undefined,
    // Keep the current list on screen while the next page loads (review
    // M1): without this every offset change flashes a skeleton and the
    // heading reads "(0)" mid-flight. Scoped to unchanged filters: after
    // resetBrowse the old scope's page must not pose as the new scope's.
    placeholderData: (previousData, previousQuery) => {
      const prevKey = previousQuery?.queryKey as readonly unknown[] | undefined
      const nextKey: readonly unknown[] = [
        'kyber-findings-browser',
        detectorFilter ?? '',
        harnessFilter ?? '',
        effectiveSessionId ?? '',
        offset,
      ]
      return shouldKeepPreviousData(prevKey, nextKey) ? previousData : undefined
    },
  })

  // A scope change empties the retained pages. Rendering is already safe
  // without it (pages are tagged), but keeping rows the server will never
  // serve again under this scope is memory the operator cannot see the use of.
  useEffect(() => {
    setAppended([])
  }, [browseScope])

  // Accumulate each page as it arrives, first page included, keyed by the
  // offset the server actually served (review M1): storing placeholder data
  // under the requested offset would file page 0 away as page 25 and show
  // it twice.
  useEffect(() => {
    if (!pageData || isPlaceholderData) return
    const stored = retainedEnvelope(appended, browseScope)
    if (stored !== undefined && stored.total !== pageData.total) {
      // The server's answer no longer describes these rows (F4): drop the
      // scope and restart at the top rather than paging from a base that
      // describes a ranking that has moved. Paging on instead would keep a
      // deleted row on screen for the life of the mount.
      setAppended(nextAccumulated([], browseScope, pageData))
      setOffset(0)
      return
    }
    setAppended((prev) => nextAccumulated(prev, browseScope, pageData))
  }, [pageData, isPlaceholderData, browseScope, appended])

  function chooseSession(next: string | undefined) {
    // Only the standalone page owns a selection; a controlled page reports the
    // choice and waits for the owner to move the scope.
    if (!controlled) setLocalSession(next)
    onSessionChange?.(next)
  }

  const headline: KyberFinding[] = headlineData?.findings ?? []
  // Every page fetched so far for this filter scope, oldest first.
  // The eligible current page: never placeholder rows from a previous key
  // (review M1). During a filter change this is undefined until the server
  // answers, so the old filter's rows cannot pose as the new filter's.
  const current = currentBrowserPage(pageData?.findings, isPlaceholderData)
  const browserFindings: KyberFinding[] = useMemo(
    () => browserRows(appended, browseScope, offset, current),
    [appended, browseScope, offset, current],
  )
  // The envelope the rows on screen were served under: the live one when the
  // query has it, otherwise the last one the stored pages arrived under. A
  // mid-list failure clears `data`, and reading these off the failed envelope
  // reported "All Workspace Findings (0)" above 25 rows (F2).
  const retained = retainedEnvelope(appended, browseScope)
  const browserTotal = pageData?.total ?? retained?.total ?? 0
  const detectorCounts = pageData?.detectorCounts ?? retained?.detectorCounts ?? {}
  // Never a fabricated 0: while the envelope is unavailable the banner says
  // the count is unknown rather than that nothing is suppressed.
  const unknownWindowSessions = pageData?.unknownWindowSessions ?? retained?.unknownWindowSessions

  return (
    <div className="flex flex-col gap-density-stack" data-testid="page-context-doctor">
      {/* Top Header & Spine Navigation */}
      <div className="flex flex-wrap items-center justify-between gap-density-cluster">
        <HierarchyBreadcrumb />
        <BaselineSelect value={baseline} onChange={setBaseline} />
      </div>

      <div className="flex flex-wrap items-baseline justify-between gap-density-cluster">
        <div>
          <h2 className="font-display text-density-display font-bold tracking-density text-foreground">
            Context Doctor
          </h2>
        </div>
      </div>

      {/* Session scope. The harness is chosen above this page, so the one
          control here is the one dimension this page narrows itself: a search
          combobox over the store's paged answer (Task T3), all sessions in
          scope by default, one session on request. A chosen or deep-linked id
          validates through the bounded summary route, not through list
          membership, so a session past page one still stands. */}
      <div className="flex flex-wrap items-center gap-density-cluster">
        <label className="text-density-xs text-muted-foreground" htmlFor="context-doctor-session">
          Session
        </label>
        <SessionPicker
          effectiveSessionId={effectiveSessionId}
          selectedLabel={selectedSummary?.label ?? null}
          options={pickerRows}
          loading={loadingSessions}
          fetching={fetchingSessions}
          error={sessionsError ? `Sessions could not be read: ${String((sessionsErrorValue as Error)?.message)}` : null}
          onSelect={chooseSession}
          onSearch={(q) => setPickerQuery({ q, page: 1 })}
          page={pickerQuery.page}
          limit={SESSION_PICKER_PAGE_SIZE}
          total={sessionPage?.total}
          onPageChange={(next) => setPickerQuery((previous) => ({ ...previous, page: next }))}
        />
        <p
          data-testid="context-doctor-session-scope-note"
          className="text-density-xs text-muted-foreground"
        >
          {sessionsError
            ? // Two situations, and only one of them is unscoped. While the
              // list is unreadable the selection still stands (see
              // `sessionStands`), so claiming "all sessions" here would
              // describe a list these queries never asked for.
              effectiveSessionId === undefined
              ? 'Sessions could not be read, so the scope stays at all sessions.'
              : 'Sessions could not be read, so findings stay scoped to the selected session.'
            : loadingSessions
              ? 'Reading sessions…'
              : selectedLoading
                ? 'Reading the selected session…'
                : selectedUnreadable
                  ? 'The selected session could not be read, so findings stay scoped to the selected session.'
                  : effectiveSessionId === undefined
                    ? `Findings cover all sessions in ${harnessFilter === undefined ? 'every harness' : harnessDisplayName(harnessFilter)}.`
                    : `Findings cover one session in ${harnessFilter === undefined ? 'every harness' : harnessDisplayName(harnessFilter)}.`}
        </p>
        {(sessionsError || selectedUnreadable) && (
          <button
            type="button"
            data-testid="context-doctor-session-retry"
            onClick={() => {
              void refetchSessions()
              if (sessionId !== undefined) void refetchSelected()
            }}
          >
            Retry
          </button>
        )}
      </div>

      {loadingFindings ? (
        <Skeleton className="h-44 w-full" />
      ) : (
        <FindingList
          findings={headline}
          title="Highest-Leverage Workspace Findings"
          description="Ranked by estimated waste, outcome risk, and confidence. Deterministic evidence beats inferred claims."
          onSelectFinding={onSelectFinding}
          onSelectTurn={(turnIdx, execId) => onSelectTurn?.(turnIdx, execId)}
          onSelectExecution={(execId) => onSelectRun?.(execId)}
        />
      )}

      {/* Full workspace findings browser (issue #191): every finding is one */}
      {/* filter or page away, with per-detector counts — the 49 */}
      {/* duplicate-tool-call findings are no longer invisible. */}
      <FindingsBrowserView
        findings={browserFindings}
        total={browserTotal}
        detectorCounts={detectorCounts}
        unknownWindowSessions={unknownWindowSessions}
        detectorFilter={detectorFilter}
        scopeNote={scopeNoteFor(
          harnessFilter,
          effectiveSessionId,
          effectiveSessionId === undefined ? undefined : (selectedSummary?.label ?? undefined),
        )}
        loading={loadingBrowser}
        error={browserError ? 'findings-load-failed' : null}
        onRetry={() => {
          void refetchBrowser()
        }}
        onDetectorChange={(detector) => resetBrowse(() => setDetectorFilter(detector))}
        onLoadMore={() => {
          // The offset follows the extent the server served, not the rows
          // painted: `browserRows` dedupes an overlap by id, and paging from
          // the painted count re-requests a served row (F3).
          const next = nextBrowserOffset(servedOffset(appended, browseScope), browserTotal)
          if (next !== undefined) setOffset(next)
        }}
        onSelectFinding={onSelectFinding}
        onSelectTurn={(turnIdx, execId) => onSelectTurn?.(turnIdx, execId)}
        onSelectExecution={(execId) => onSelectRun?.(execId)}
      />
    </div>
  )
}