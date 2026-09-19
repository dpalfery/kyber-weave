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
}

export interface ScorecardMatrixProps {
  rows: readonly ScorecardMatrixRow[]
  loading?: boolean
  onSelectHarness?: (harnessId: string) => void
  className?: string
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
}: ScorecardMatrixProps) {
  return (
    <Card className={cn('p-chrome', className)} data-testid="scorecard-matrix">
      <div className="flex items-center justify-between border-b border-border/60 pb-chrome-sm mb-chrome-sm">
        <div>
          <h3 className="text-density-xs font-semibold uppercase tracking-density text-heading">
            Harness diagnostic matrix ({rows.length})
          </h3>
          <p className="text-density-xs text-muted-foreground mt-density-hair leading-density">
            Six independent dimensions per live harness. Unmeasured cells are dashes, never zeros.
          </p>
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

            {rows.map((row) => {
              const select = () => onSelectHarness?.(row.harness)
              return (
                <div key={row.harness} className="contents">
                  <button
                    type="button"
                    onClick={select}
                    data-testid={`drill-harness-${row.harness}`}
                    className="flex min-w-0 flex-col items-start rounded-chrome px-chrome-xs py-chrome-sm text-left hover:bg-interactive-secondary/30"
                  >
                    <span className="truncate font-semibold text-density-xs text-foreground">
                      {row.name || row.harness}
                    </span>
                    <span className="truncate font-mono text-density-2xs text-tertiary-foreground">
                      {row.harness}
                    </span>
                  </button>
                  {ORDERED_DIMENSION_KEYS.map((key) => (
                    <button
                      key={key}
                      type="button"
                      onClick={select}
                      className="rounded-chrome px-chrome-xs py-chrome-sm text-left hover:bg-interactive-secondary/30"
                      aria-label={`${row.name || row.harness} ${DIMENSION_METADATA[key].name}`}
                    >
                      <DimensionMeter dim={dimensionForRow(row, key)} />
                    </button>
                  ))}
                </div>
              )
            })}
          </div>
        </div>
      )}
    </Card>
  )
}
