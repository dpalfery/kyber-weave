import { cn } from '../../lib/utils.js'
import { Card } from '../ui/card.js'
import { Skeleton } from '../ui/skeleton.js'
import type {
  ScorecardDimensionKey,
  ScorecardDimensionValue,
  ServedHarnessScorecard,
} from '../../lib/kyberApi.js'
import {
  DIMENSION_METADATA,
  ORDERED_DIMENSION_KEYS,
  formatDimensionDisplay,
} from './Scorecard.js'

export interface ScorecardMatrixRow {
  harness: string
  name?: string
  sampleCount?: number
  scorecard?: ServedHarnessScorecard
  /**
   * Display-only family label (decision D3, issue #199): split client
   * surfaces stay distinct rows — grouping sums nothing across origins.
   */
  family?: string | null
  /** Verbatim zero-data reason off the rollup payload, or null when covered. */
  noDataReason?: string | null
  /**
   * Per-harness checkpoint unit counts by status. Accepted for type
   * compatibility with the served payload; rendered by the ingest panel
   * (T9), never summed here.
   */
  checkpointSummary?: {
    ok: number
    partial: number
    failed: number
    unavailable: number
  } | null
}

/**
 * The persisted refresh coverage window (issues #189/#199, T5). Every number
 * shown comes from a row that exists: a null/missing `historyWeeks` means
 * recorded before window tracking — stated as unknown, never defaulted to 2.
 * Fields stay optional to match the served `KyberCoverageRefresh` contract
 * (T9's canonical seam); absence reads as unknown.
 */
export interface ScorecardCoverageWindow {
  historyWeeks?: number | null
  coveredFrom?: string | null
  coveredThrough?: string | null
}

export interface ScorecardMatrixProps {
  rows: readonly ScorecardMatrixRow[]
  loading?: boolean
  onSelectHarness?: (harnessId: string) => void
  className?: string
  /**
   * Coverage window for the banner. `undefined` (not fetched) renders no
   * banner — absence is never invented. T9's ingest panel owns the rest of
   * the coverage payload; this prop carries the window only.
   */
  coverage?: ScorecardCoverageWindow | null
}

/** Stored file-source namespace — stays in data, never in visible text (#199). */
const FILE_SOURCE_PREFIX = 'codeburn/'

/** Strip the storage namespace for display; the canonical id rides along untouched. */
function displayId(harness: string): string {
  return harness.startsWith(FILE_SOURCE_PREFIX)
    ? harness.slice(FILE_SOURCE_PREFIX.length)
    : harness
}

/** A row's visible name: served label, else the de-namespaced id. */
function rowDisplayName(row: ScorecardMatrixRow): string {
  return row.name || displayId(row.harness)
}

/** A row carries no in-window data when the rollup said so verbatim. */
function noDataReasonOf(row: ScorecardMatrixRow): string | null {
  const reason = row.noDataReason
  return typeof reason === 'string' && reason.trim() !== '' ? reason : null
}

/** Display-only family: served label, else the row's own id (no merging). */
function familyOf(row: ScorecardMatrixRow): string {
  const family = row.family
  return typeof family === 'string' && family.trim() !== '' ? family : row.harness
}

/** Short calendar date off an ISO timestamp, or null when there is none. */
function shortDate(iso: string | null | undefined): string | null {
  if (iso === null || iso === undefined) return null
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return null
  return date.toISOString().slice(0, 10)
}

/**
 * Coverage banner (issue #189): the persisted window plus how many listed
 * harnesses hold no records in it. Counts rows that exist; the family sums
 * nothing, so no aggregate is fabricated here either.
 */
function CoverageBanner({
  coverage,
  noDataCount,
  totalCount,
}: {
  coverage: ScorecardCoverageWindow
  noDataCount: number
  totalCount: number
}) {
  const { historyWeeks, coveredFrom, coveredThrough } = coverage
  const from = shortDate(coveredFrom)
  const through = shortDate(coveredThrough)
  return (
    <p className="text-density-xs text-muted-foreground mt-density-hair leading-density" data-testid="coverage-banner">
      {typeof historyWeeks === 'number' && Number.isFinite(historyWeeks) ? (
        <>
          Coverage: last {historyWeeks} week{historyWeeks === 1 ? '' : 's'}
          {from !== null && through !== null ? ` (${from} → ${through})` : ''}
        </>
      ) : (
        <>Coverage window unknown (recorded before window tracking)</>
      )}
      {noDataCount > 0 && (
        <>
          {' — '}
          {noDataCount} of {totalCount} harness{totalCount === 1 ? '' : 'es'} with no records in coverage window
        </>
      )}
    </p>
  )
}

/** Shown when the row carries no scorecard at all, so the server said nothing about it. */
const NO_SCORECARD_REASON =
  'No live rollup: harness observed on runs but not yet aggregated'

/**
 * Adapt one served dimension to the shape this table renders.
 *
 * This used to derive the dimension from the row's raw metrics. It no longer does: the
 * engine derives all six (R11.14), so the browser cannot disagree with the CLI report
 * about what a harness scored. What is left here is presentation — picking the display
 * name and mapping absence to the `not_measurable` status the meter renders as a dash.
 */
function dimensionForRow(
  row: ScorecardMatrixRow,
  key: ScorecardDimensionKey,
): ScorecardDimensionValue {
  const meta = DIMENSION_METADATA[key]
  const served = row.scorecard?.[key]

  if (!served || served.value === null) {
    return {
      key,
      name: meta.name,
      value: null,
      status: 'not_measurable',
      reason: served?.reason ?? NO_SCORECARD_REASON,
    }
  }

  return {
    key,
    name: meta.name,
    value: served.value,
    formatted: served.display,
    status: 'measured',
    reason: meta.description,
  }
}

/** Fill ratio for a measured meter. Unmeasurable never gets a 0-width filled bar. */
function meterFill(dim: ScorecardDimensionValue, unmeasurable: boolean): number | null {
  if (unmeasurable) return null
  const value = dim.value
  if (typeof value !== 'number' || !Number.isFinite(value)) return null
  if (value <= 1) return Math.max(0, Math.min(1, value))
  return 1
}

function DimensionMeter({
  dim,
}: {
  dim: ScorecardDimensionValue
}) {
  const formatted = formatDimensionDisplay(dim)
  const fill = meterFill(dim, formatted.isUnmeasurable)

  return (
    <div
      data-testid={`dimension-${dim.key}`}
      data-measured={formatted.isUnmeasurable ? 'false' : 'true'}
      title={`${DIMENSION_METADATA[dim.key].name}: ${formatted.display} (${formatted.reason})`}
      className="min-w-0"
    >
      <div
        className={cn(
          'h-1.5 w-full rounded-full',
          formatted.isUnmeasurable
            ? 'border border-dashed border-border bg-transparent'
            : 'bg-interactive-secondary/60',
        )}
        aria-hidden="true"
      >
        {fill !== null && (
          <div
            className="h-full rounded-full bg-primary/70"
            style={{ width: `${Math.round(fill * 100)}%` }}
          />
        )}
      </div>
      <div
        className={cn(
          'mt-density-hair font-mono text-density-xs tabular-nums',
          formatted.isUnmeasurable ? 'text-tertiary-foreground' : 'text-foreground',
        )}
        data-testid={formatted.isUnmeasurable ? 'dimension-unmeasurable' : 'dimension-value'}
      >
        {formatted.display}
      </div>
    </div>
  )
}

export function ScorecardMatrix({
  rows,
  loading = false,
  onSelectHarness,
  className,
  coverage,
}: ScorecardMatrixProps) {
  // Zero-data rows keep their verbatim rollup reason in a section of their
  // own — never a dash-filled matrix row, never `0 sessions` where the truth
  // is none in the coverage window (honest-unobservability rule).
  const noDataRows = rows.filter((row) => noDataReasonOf(row) !== null)
  const dataRows = rows.filter((row) => noDataReasonOf(row) === null)

  // Display-level family grouping only (decision D3): split identities stay
  // distinct rows with per-origin counts; nothing is summed across origins.
  const familyGroups = new Map<string, ScorecardMatrixRow[]>()
  for (const row of dataRows) {
    const family = familyOf(row)
    const group = familyGroups.get(family) ?? []
    group.push(row)
    familyGroups.set(family, group)
  }

  return (
    <Card className={cn('p-chrome', className)} data-testid="scorecard-matrix">
      <div className="flex items-center justify-between border-b border-border/60 pb-chrome-sm mb-chrome-sm">
        <div>
          <h3 className="text-density-xs font-semibold uppercase tracking-density text-heading">
            Harness diagnostic matrix ({dataRows.length})
          </h3>
          <p className="text-density-xs text-muted-foreground mt-density-hair leading-density">
            Six independent dimensions per harness with in-window data. Unmeasured cells are dashes, never zeros.
          </p>
          {coverage !== undefined && coverage !== null && (
            <CoverageBanner coverage={coverage} noDataCount={noDataRows.length} totalCount={rows.length} />
          )}
          {coverage !== undefined && coverage === null && (
            <p className="text-density-xs text-muted-foreground mt-density-hair leading-density" data-testid="coverage-banner">
              Coverage window unknown (recorded before window tracking)
            </p>
          )}
        </div>
      </div>

      {loading ? (
        <div className="flex flex-col gap-density-cluster">
          {Array.from({ length: 3 }).map((_, i) => (
            <Skeleton key={i} className="h-12 w-full" />
          ))}
        </div>
      ) : rows.length === 0 ? (
        <p className="text-density-xs text-muted-foreground">
          No harnesses observed in rollups or runs.
        </p>
      ) : (
        <>
        <div className="overflow-x-auto">
          <div
            className="grid min-w-[960px] gap-x-density-cluster gap-y-density-cluster"
            style={{ gridTemplateColumns: 'minmax(160px, 1.3fr) repeat(6, minmax(92px, 1fr))' }}
          >
            <div className="px-chrome-xs pb-chrome-xs text-density-xs font-semibold uppercase tracking-density text-tertiary-foreground">
              Harness
            </div>
            {ORDERED_DIMENSION_KEYS.map((key) => (
              <div
                key={key}
                className="px-chrome-xs pb-chrome-xs text-density-xs font-semibold uppercase tracking-density text-tertiary-foreground"
              >
                {DIMENSION_METADATA[key].name}
              </div>
            ))}

            {[...familyGroups.entries()].map(([family, group]) => (
              <div key={family} className="contents">
                {group.length > 1 && (
                  <div
                    className="col-span-full px-chrome-xs pt-chrome-sm text-density-xs font-semibold uppercase tracking-density text-tertiary-foreground"
                    data-testid={`family-${family}`}
                  >
                    {displayId(family)} family · {group.length} origins, per-origin counts below
                  </div>
                )}
                {group.map((row) => (
                  <MatrixRow key={row.harness} row={row} onSelectHarness={onSelectHarness} />
                ))}
              </div>
            ))}
          </div>
        </div>
        {noDataRows.length > 0 && (
          <div className="mt-chrome-sm border-t border-border/60 pt-chrome-sm" data-testid="matrix-no-data">
            <h4 className="text-density-xs font-semibold uppercase tracking-density text-tertiary-foreground">
              No records in coverage window ({noDataRows.length})
            </h4>
            <ul className="mt-density-hair flex flex-col gap-density-hair">
              {noDataRows.map((row) => (
                <li key={row.harness} className="text-density-xs text-muted-foreground leading-density">
                  <span className="font-semibold text-foreground">{rowDisplayName(row)}</span>
                  {' — '}
                  {noDataReasonOf(row)}
                </li>
              ))}
            </ul>
          </div>
        )}
        </>
      )}
    </Card>
  )
}

/**
 * One per-origin matrix row. The drill hook and selection keep the canonical
 * id verbatim (API filters key on it); visible text uses the display name so
 * the storage namespace never leaks to users. The per-origin sample count
 * renders only when the row carries one — never invented, never summed.
 */
function MatrixRow({
  row,
  onSelectHarness,
}: {
  row: ScorecardMatrixRow
  onSelectHarness?: (harnessId: string) => void
}) {
  const select = () => onSelectHarness?.(row.harness)
  const displayName = rowDisplayName(row)
  return (
    <div key={row.harness} className="contents">
      <button
        type="button"
        onClick={select}
        data-testid={`drill-harness-${row.harness}`}
        className="flex min-w-0 flex-col items-start rounded-chrome px-chrome-xs py-chrome-sm text-left hover:bg-interactive-secondary/30"
      >
        <span className="truncate font-semibold text-density-xs text-foreground">
          {displayName}
        </span>
        <span className="truncate font-mono text-density-2xs text-tertiary-foreground">
          {displayId(row.harness)}
        </span>
        {typeof row.sampleCount === 'number' && Number.isFinite(row.sampleCount) && (
          <span
            className="truncate font-mono text-density-2xs text-tertiary-foreground"
            data-testid={`samples-${row.harness}`}
          >
            {row.sampleCount} samples
          </span>
        )}
      </button>
      {ORDERED_DIMENSION_KEYS.map((key) => (
        <button
          key={key}
          type="button"
          onClick={select}
          className="rounded-chrome px-chrome-xs py-chrome-sm text-left hover:bg-interactive-secondary/30"
          aria-label={`${displayName} ${DIMENSION_METADATA[key].name}`}
        >
          <DimensionMeter dim={dimensionForRow(row, key)} />
        </button>
      ))}
    </div>
  )
}
