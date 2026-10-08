import { useState, useMemo, useEffect } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Skeleton } from '../components/ui/skeleton.js'
import {
  fetchCoverage,
  fetchHarnesses,
  fetchRuns,
  fetchFindings,
  refreshModelWindows,
  type ModelCatalogRefreshResult,
  type FindingsPage,
  type KyberCoverage,
  type KyberCoverageCheckpoint,
  type KyberCoverageRefresh,
  type KyberHarnessSummary,
  type KyberFinding,
} from '../lib/kyberApi.js'
import {
  HierarchyBreadcrumb,
  FindingList,
  BaselineSelect,
  ScorecardMatrix,
  formatCoverageWindow,
} from '../components/analysis/index.js'
import type { ScorecardMatrixRow, ScorecardCoverageWindow } from '../components/analysis/ScorecardMatrix.js'

export interface ContextDoctorProps {
  initialHarnesses?: KyberHarnessSummary[]
  /**
   * Initial findings page, or a bare row list (the coverage suites pass
   * `[]`: it normalizes to an empty envelope). Prefer the envelope — a bare
   * list cannot carry totals, counts, or the unknown-window figure.
   */
  initialFindings?: FindingsPage | KyberFinding[]
  /**
   * Coverage facts for the banner (T10) and ingest panel (T9). Accepts the
   * full `GET /api/kyber/coverage` payload or the refresh slice alone —
   * the T10 suite feeds the refresh slice; the panel renders only when the
   * full payload (with ingest activity) is present, so a refresh-only value
   * never invents receiver facts.
   */
  initialCoverage?: KyberCoverage | KyberCoverageRefresh
  onSelectHarness?: (harnessId: string) => void
  onSelectRun?: (runId: string, harnessId?: string) => void
  onSelectFinding?: (findingId: string) => void
  onSelectTurn?: (turnIndex: number, executionId?: string, runId?: string) => void
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

/** Returns whether a harness id represents collected, harness-specific data. */
function isObservedHarness(harness: string): boolean {
  return harness.length > 0 && harness !== 'all'
}

/** Page size for the workspace findings browser (issue #191). */
export const FINDINGS_PAGE_SIZE = 25

export interface FindingsBrowserViewProps {
  findings: readonly KyberFinding[]
  total: number
  detectorCounts: Readonly<Record<string, number>>
  unknownWindowSessions?: number
  detectorFilter?: string
  harnessFilter?: string
  harnesses: readonly ScorecardMatrixRow[]
  /** True while a page is in flight. The skeleton shows only when there is
   * nothing stored yet (review M1): pages already on screen stay up. */
  loading: boolean
  /** Bounded fetch failure. Raw server text never renders (council review). */
  error?: string | null
  onDetectorChange?: (detector: string | undefined) => void
  onHarnessChange?: (harness: string | undefined) => void
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
  harnessFilter,
  harnesses,
  loading,
  error = null,
  onDetectorChange = () => {},
  onHarnessChange = () => {},
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
        <label className="text-density-xs text-muted-foreground">
          Harness:{' '}
          <select
            data-testid="findings-harness-filter"
            value={harnessFilter ?? ''}
            onChange={(event) => onHarnessChange(event.target.value === '' ? undefined : event.target.value)}
          >
            <option value="">All harnesses</option>
            {harnesses.map((row) => (
              <option key={row.harness} value={row.harness}>
                {row.name ?? harnessDisplayName(row.harness)}
              </option>
            ))}
          </select>
        </label>
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
        {(detectorFilter !== undefined || harnessFilter !== undefined) && (
          <button
            type="button"
            data-testid="findings-clear-filters"
            title="Clear detector and harness filters"
            onClick={() => {
              onDetectorChange(undefined)
              onHarnessChange(undefined)
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
 * rows collapsed to the newly fetched page alone. Re-storing the same page is a
 * no-op, which is what keeps a refetch from duplicating it.
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
 * harness or detector from being re-appended after the filter changes.
 * `current` covers the window before the effect has stored the first page,
 * which is also the only thing that exists during static render.
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
 * Relative age of an ISO timestamp for the ingest panel's last-received line.
 * A pure formatter over a recorded timestamp — never a claim about whether
 * the receiver process is still running.
 */
export function formatCoverageAgo(iso: string, nowMs: number = Date.now()): string {
  const then = Date.parse(iso)
  if (!Number.isFinite(then)) return 'unknown age'
  const deltaMs = nowMs - then
  // A timestamp in the future (clock skew, NTP correction) is data the
  // panel cannot place — folding it into `just now` would claim fresh
  // arrival for a time that has not happened yet.
  if (deltaMs < 0) return 'in the future (clock skew)'
  const minutes = Math.floor(deltaMs / 60000)
  if (minutes < 1) return 'just now'
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.floor(minutes / 60)
  if (hours < 48) return `${hours}h ago`
  return `${Math.floor(hours / 24)}d ago`
}

function formatModelCatalogRefreshStatus(result: ModelCatalogRefreshResult): string {
  const failed = result.vendorsFailed.map((entry) => entry.vendor)
  const when = result.lastRefreshAt ?? ''
  if (failed.length > 0 && result.vendorsUpdated.length > 0) {
    return `Partial refresh. Failed vendors: ${failed.join(', ')}. ${result.rowCount} rows at ${when}`
  }
  if (failed.length > 0) {
    return `Refresh failed for ${failed.join(', ')}.`
  }
  return `${result.rowCount} rows at ${when}`
}

/**
 * Coverage-panel control for the vendor-window catalog. The status line is
 * built from row count, timestamp, and vendor names — never from a refresh
 * error string, which can carry a remote body.
 */
function ModelCatalogRefreshControl() {
  const [inFlight, setInFlight] = useState(false)
  const [status, setStatus] = useState<string | null>(null)

  return (
    <div className="mt-density-cluster">
      <button
        type="button"
        data-testid="model-catalog-refresh-button"
        disabled={inFlight}
        onClick={() => {
          setInFlight(true)
          void refreshModelWindows()
            .then((result) => {
              setStatus(formatModelCatalogRefreshStatus(result))
            })
            .catch(() => {
              setStatus('Refresh failed. The model-window catalog was left unchanged.')
            })
            .finally(() => {
              setInFlight(false)
            })
        }}
      >
        Refresh model windows
      </button>
      {status !== null && (
        <p data-testid="model-catalog-refresh-status" className="text-density-xs text-muted-foreground mt-density-hair">
          {status}
        </p>
      )}
    </div>
  )
}

/**
 * Ingest coverage panel (T9: issues #189/#198/#199). Reads the single
 * `GET /api/kyber/coverage` payload: per-source counts under T7 display
 * names (the stored id rides along only as a `data-raw-source` audit
 * attribute, never as the shown label), the last-received line, verbatim
 * quarantine reasons, and partial-checkpoint state. Empty log ⇒ unknown;
 * null window ⇒ unknown; zero-data keeps its verbatim reason. This panel
 * owns ingest activity only — the matrix banner and row grouping below
 * belong to T10.
 */
export function CoverageIngestPanel({ coverage }: { coverage: KyberCoverage }) {
  const { refresh, ingest, quarantineByReason, checkpoints } = coverage
  const partialUnits =
    checkpoints === null ? [] : checkpoints.filter((unit) => unit.lastStatus === 'partial')
  const zeroRecordByReason = zeroRecordReasons(checkpoints)
  const zeroRecordTotal = zeroRecordByReason.reduce((sum, [, count]) => sum + count, 0)
  const receiverLine =
    ingest.status === 'unknown'
      ? 'no receiver activity recorded — receiver status is not observable from this page'
      : ingest.lastReceivedAt === null
        ? 'last received unknown (no receiver timestamp recorded)'
        : `last received ${ingest.lastReceivedAt} (${formatCoverageAgo(ingest.lastReceivedAt)})`

  return (
    <section
      className="rounded-lg border border-border bg-card p-chrome"
      data-testid="coverage-ingest-panel"
      aria-label="Ingest coverage"
    >
      <h3 className="text-density-xs font-semibold uppercase tracking-density text-heading">
        Ingest coverage
      </h3>
      <ModelCatalogRefreshControl />
      <p className="text-density-xs text-muted-foreground mt-density-hair leading-density" data-testid="coverage-window">
        {formatCoverageWindow(refresh)}
      </p>
      <p className="text-density-xs text-muted-foreground mt-density-hair leading-density" data-testid="coverage-last-received">
        {receiverLine}
      </p>
      {ingest.status === 'known' && (
        <ul className="mt-density-cluster flex flex-col gap-density-hair">
          {ingest.sources.map((entry) => (
            <li
              key={entry.source}
              data-testid="coverage-source-row"
              data-raw-source={entry.source}
              className="flex flex-wrap items-baseline justify-between gap-density-cluster text-density-xs"
            >
              <span data-testid="coverage-source-label">{entry.display || entry.source}</span>
              <span className="text-muted-foreground">
                {entry.recordCount} records · {entry.kind}
              </span>
            </li>
          ))}
        </ul>
      )}
      {quarantineByReason.length > 0 && (
        <ul className="mt-density-cluster flex flex-col gap-density-hair">
          {quarantineByReason.map((entry) => (
            <li
              key={entry.reason}
              data-testid="coverage-quarantine-row"
              className="flex flex-wrap items-baseline justify-between gap-density-cluster text-density-xs"
            >
              <span>{entry.reason}</span>
              <span className="text-muted-foreground">{entry.count}</span>
            </li>
          ))}
        </ul>
      )}
      {checkpoints === null ? (
        <p
          className="text-density-xs text-muted-foreground mt-density-hair leading-density"
          data-testid="coverage-checkpoints-unknown"
        >
          checkpoint status not observable from this page
        </p>
      ) : (
        partialUnits.length > 0 && (
          <div className="mt-density-cluster text-density-xs" data-testid="coverage-partial">
            <p>
              {partialUnits.length} source{partialUnits.length === 1 ? '' : 's'} partial (problems
              recorded, coverage incomplete)
            </p>
            <ul className="mt-density-hair flex flex-col gap-density-hair">
              {partialUnits.map((unit) => (
                <li
                  key={`${unit.harnessId}:${unit.sourceKey}`}
                  data-testid="coverage-partial-unit"
                  className="text-muted-foreground"
                >
                  {unit.harnessId}: {unit.recordCount} records
                </li>
              ))}
            </ul>
          </div>
        )
      )}
      {zeroRecordTotal > 0 && (
        <div className="mt-density-cluster text-density-xs" data-testid="coverage-zero-record">
          <p>
            {zeroRecordTotal} source{zeroRecordTotal === 1 ? '' : 's'} discovered but not ingested
          </p>
          <ul className="mt-density-hair flex flex-col gap-density-hair">
            {zeroRecordByReason.map(([reason, count]) => (
              <li
                key={reason}
                data-testid="coverage-zero-record-reason"
                className="flex flex-wrap items-baseline justify-between gap-density-cluster text-muted-foreground"
              >
                <span>{reason}</span>
                <span>{count}</span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </section>
  )
}

/**
 * Zero-record checkpoints grouped by their persisted reason (issue #196).
 * Only `ok`/`unchanged` units that carry a reason count: failed and partial
 * units already surface through their own status, and a unit with no reason
 * is never given an invented one.
 */
function zeroRecordReasons(
  checkpoints: readonly KyberCoverageCheckpoint[] | null,
): [string, number][] {
  if (checkpoints === null) return []
  const counts = new Map<string, number>()
  for (const unit of checkpoints) {
    if (unit.recordCount !== 0 || unit.lastErrorCode === null) continue
    if (unit.lastStatus !== 'ok' && unit.lastStatus !== 'unchanged') continue
    counts.set(unit.lastErrorCode, (counts.get(unit.lastErrorCode) ?? 0) + 1)
  }
  return [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
}

/**
 * Type guard for the coverage prop union: the full payload carries `ingest`,
 * the refresh slice does not. The ingest panel renders only for the full
 * payload; the matrix banner reads the refresh facts from either shape.
 */
export function isFullCoverage(
  coverage: KyberCoverage | KyberCoverageRefresh | undefined,
): coverage is KyberCoverage {
  return coverage !== undefined && 'ingest' in coverage
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
 * a previous key are never the current page of a new filter — they belong
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
 * next one loads. Offset-only moves stay within the same filter scope, so
 * the stored rows are still valid; a detector or harness change re-scopes
 * the list and the old filter's rows must not pose as the new filter's.
 * Both filter slots are compared — checking only one would keep stale rows
 * when the other filter changes.
 */
export function shouldKeepPreviousData(
  prevKey: readonly unknown[] | undefined,
  nextKey: readonly unknown[],
): boolean {
  return prevKey?.[1] === nextKey[1] && prevKey?.[2] === nextKey[2]
}

/**
 * Renders the Context Doctor landing page with workspace findings and a
 * per-harness diagnostic scorecard.
 */
export function ContextDoctor({
  initialHarnesses,
  initialFindings,
  initialCoverage,
  onSelectHarness,
  onSelectRun,
  onSelectFinding,
  onSelectTurn,
}: ContextDoctorProps) {
  const [baseline, setBaseline] = useState('none')
  const [detectorFilter, setDetectorFilter] = useState<string | undefined>(undefined)
  const [harnessFilter, setHarnessFilter] = useState<string | undefined>(undefined)
  // Offset paging (issue #191): Load more advances `offset` and appends the
  // next page instead of refetching a growing prefix.
  const [offset, setOffset] = useState(0)
  const [appended, setAppended] = useState<AccumulatedPage[]>([])

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

  // Every page is stored under the scope it was fetched for, including the
  // first, so advancing the offset extends the list instead of replacing it.
  const browseScope = `${detectorFilter ?? ''}|${harnessFilter ?? ''}`

  const resetBrowse = (update: () => void) => {
    update()
    setOffset(0)
    setAppended([])
  }

  // Query live harnesses
  const { data: harnessesData, isLoading: loadingHarnesses } = useQuery({
    queryKey: ['kyber-harnesses'],
    queryFn: fetchHarnesses,
    initialData: initialHarnesses,
  })

  // Harness rollups are deliberately optional. When their endpoint has not
  // been populated yet, the existing run projection still supplies real
  // harness identities for navigation. It supplies no metrics: those remain
  // not measurable until the rollup contract is available.
  const { data: runsData, isLoading: loadingRuns } = useQuery({
    queryKey: ['kyber-runs-navigation'],
    queryFn: () => fetchRuns(),
  })

  // Headline findings across the entire workspace. The query fetches a full
  // page; the card keeps four on screen and scrolls the rest.
  const { data: headlineData, isLoading: loadingFindings } = useQuery({
    queryKey: ['kyber-findings-workspace'],
    queryFn: () => fetchFindings({ limit: FINDINGS_PAGE_SIZE }),
    initialData: initialPage,
  })

  // Full workspace browser (issue #191): server-side detector/harness
  // filters with per-detector counts over the narrowed set, paged by
  // offset. Counts ignore paging, so chips stay comparable across pages.
  const filtersActive = detectorFilter !== undefined || harnessFilter !== undefined
  const {
    data: pageData,
    isLoading: loadingBrowser,
    isPlaceholderData,
    error: browserError,
    refetch: refetchBrowser,
  } = useQuery({
    queryKey: ['kyber-findings-browser', detectorFilter ?? '', harnessFilter ?? '', offset],
    queryFn: () =>
      fetchFindings({
        ...(detectorFilter !== undefined ? { detector: detectorFilter } : {}),
        ...(harnessFilter !== undefined ? { harness: harnessFilter } : {}),
        limit: FINDINGS_PAGE_SIZE,
        offset,
      }),
    initialData: offset === 0 && !filtersActive ? initialPage : undefined,
    // Keep the current list on screen while the next page loads (review
    // M1): without this every offset change flashes a skeleton and the
    // heading reads "(0)" mid-flight. Scoped to unchanged filters: after
    // resetBrowse the old filter's page must not pose as the new filter's.
    placeholderData: (previousData, previousQuery) => {
      const prevKey = previousQuery?.queryKey as readonly unknown[] | undefined
      const nextKey: readonly unknown[] = [
        'kyber-findings-browser',
        detectorFilter ?? '',
        harnessFilter ?? '',
        offset,
      ]
      return shouldKeepPreviousData(prevKey, nextKey) ? previousData : undefined
    },
  })

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
  // Coverage payload for the T9 ingest panel: window, receiver activity,
  // quarantine reasons, checkpoint statuses. Optional initial data keeps the
  // panel deterministic under test; otherwise the live endpoint supplies it.
  // A refresh-only initial value feeds the T10 banner below without
  // inventing ingest facts — the panel renders only for the full payload.
  const { data: coverageData } = useQuery({
    queryKey: ['kyber-coverage'],
    queryFn: fetchCoverage,
    initialData: isFullCoverage(initialCoverage) ? initialCoverage : undefined,
  })
  const refreshFacts: KyberCoverageRefresh | undefined =
    coverageData?.refresh ?? (isFullCoverage(initialCoverage) ? initialCoverage.refresh : initialCoverage)
  // T10's matrix banner owns the window wording (ScorecardMatrix `coverage`
  // prop); T9 only feeds it the refresh facts, which read identically from
  // the full payload's `refresh` slice or from a refresh-only value.
  // `undefined` (not fetched) renders no banner — absence is never invented.
  const matrixCoverage: ScorecardCoverageWindow | undefined =
    refreshFacts === undefined
      ? undefined
      : {
          historyWeeks: refreshFacts.historyWeeks,
          coveredFrom: refreshFacts.coveredFrom,
          coveredThrough: refreshFacts.coveredThrough,
          // A fresh store (null) must not read as a legacy tracked run in
          // the banner — the window formatter distinguishes the two.
          lastSuccessAt: refreshFacts.lastSuccessAt,
        }

  const harnesses = useMemo((): ScorecardMatrixRow[] => {
    const fromRollups = (harnessesData ?? []).filter((h) => isObservedHarness(h.harness))
    if (fromRollups.length > 0) {
      return fromRollups.map((row) => ({
        ...row,
        sessionCount: row.sessionCount,
        sampleCount: row.sessionCount ?? row.sampleCount,
      }))
    }

    const observed = [...new Set((runsData ?? []).map((run) => run.harness).filter(isObservedHarness))]
    return observed.map((harness) => ({
      harness,
      name: harnessDisplayName(harness),
    }))
  }, [harnessesData, runsData])

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
  const loadingMatrix = loadingHarnesses || (!harnessesData?.length && loadingRuns)

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
          <p className="text-density-xs text-muted-foreground mt-density-hair leading-density">
            What's bloating or breaking your agents' context, per harness, and what to change.
          </p>
        </div>
      </div>

      {coverageData && <CoverageIngestPanel coverage={coverageData} />}

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
        harnessFilter={harnessFilter}
        harnesses={harnesses}
        loading={loadingBrowser}
        error={browserError ? 'findings-load-failed' : null}
        onRetry={() => {
          void refetchBrowser()
        }}
        onDetectorChange={(detector) => resetBrowse(() => setDetectorFilter(detector))}
        onHarnessChange={(harness) => resetBrowse(() => setHarnessFilter(harness))}
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

      <ScorecardMatrix
        rows={harnesses}
        loading={loadingMatrix}
        coverage={matrixCoverage}
        onSelectHarness={onSelectHarness}
      />
    </div>
  )
}
