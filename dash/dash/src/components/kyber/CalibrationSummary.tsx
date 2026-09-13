import { cn } from '../../lib/utils'
import { Card } from '../ui/card'
import type { KyberCalibrationBin, KyberCalibrationSummary } from '../../lib/kyberApi'

export interface CalibrationSummaryProps {
  data?: KyberCalibrationSummary | null
  isLoading?: boolean
  isError?: boolean
  className?: string
}

const UNMEASURED = '—'

function formatPct(value: number): string {
  return `${Math.round(value * 100)}%`
}

function measurablePct(value: number | undefined, measurable: boolean): string {
  if (!measurable || value === undefined || Number.isNaN(value)) return UNMEASURED
  return formatPct(value)
}

function measurableNum(value: number | undefined, measurable: boolean, digits = 3): string {
  if (!measurable || value === undefined || Number.isNaN(value)) return UNMEASURED
  return value.toFixed(digits)
}

function hasEnoughScoredPairs(data: KyberCalibrationSummary | null | undefined): boolean {
  if (!data) return false
  return data.isCalibrated && data.status === 'calibrated' && data.scoredPredictions >= 5
}

function binHasObservations(bin: KyberCalibrationBin): boolean {
  return bin.predictionCount > 0
}

/**
 * Live calibration curve from `GET /api/kyber/calibration`.
 * Fewer than five scored pairs is an empty chart plus `statusMessage`, never a fabricated accuracy curve.
 */
export function CalibrationSummary({ data, isLoading, isError, className }: CalibrationSummaryProps) {
  const calibrated = hasEnoughScoredPairs(data)
  const status = data?.status ?? 'not_yet_calibrated'
  const statusLabel = status === 'calibrated' && calibrated ? 'Calibrated' : 'Not yet calibrated'
  const statusMessage =
    data?.statusMessage ??
    (isError
      ? 'Calibration summary could not be loaded.'
      : isLoading
        ? 'Loading calibration from scored prediction pairs…'
        : 'Not yet calibrated: no scored prediction pairs are available.')

  const populatedBins = calibrated ? (data?.bins ?? []).filter(binHasObservations) : []
  const chartEmpty = !calibrated || populatedBins.length === 0

  return (
    <Card className={cn('p-5 space-y-4', className)} data-testid="calibration-summary">
      <div className="flex flex-wrap items-start justify-between gap-3 border-b border-border/60 pb-3">
        <div className="min-w-0 space-y-1.5">
          <div className="flex items-center gap-2 flex-wrap">
            <h3 className="text-xs font-semibold uppercase tracking-wider text-heading">
              Prediction Calibration
            </h3>
            <span
              data-testid="calibration-status"
              className={cn(
                'rounded border px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wider',
                calibrated
                  ? 'border-emerald-500/30 bg-emerald-500/10 text-emerald-700 dark:text-emerald-400'
                  : 'border-border bg-interactive-secondary text-tertiary-foreground',
              )}
            >
              {statusLabel}
            </span>
          </div>
          <p className="text-xs text-muted-foreground leading-relaxed" data-testid="calibration-status-message">
            {statusMessage}
          </p>
        </div>

        <div className="flex gap-4 text-right shrink-0">
          <Metric
            label="Scored pairs"
            value={
              data
                ? `${data.scoredPredictions} / ${data.totalPredictions}`
                : UNMEASURED
            }
            testId="calibration-scored-pairs"
          />
          <Metric
            label="ECE"
            value={measurableNum(data?.expectedCalibrationError, calibrated)}
            testId="calibration-ece"
          />
          <Metric
            label="Brier"
            value={measurableNum(data?.brierScore, calibrated)}
            testId="calibration-brier"
          />
        </div>
      </div>

      <div
        data-testid="chart-calibration"
        data-empty={chartEmpty ? 'true' : 'false'}
        className="rounded-md border border-border/80 bg-card p-3 min-h-[5.5rem]"
      >
        {chartEmpty ? (
          <p className="text-xs text-muted-foreground leading-relaxed">
            {isLoading
              ? 'Calibration chart is empty until scored prediction pairs load.'
              : 'No calibration curve to plot. Observed accuracy is unmeasured until at least five scored pairs exist; this panel does not invent a percentage.'}
          </p>
        ) : (
          <div className="space-y-2.5" role="img" aria-label="Stated confidence versus observed accuracy by bin">
            {populatedBins.map((bin) => {
              const confPct = Math.round(bin.meanConfidence * 100)
              const accPct = Math.round(bin.observedAccuracy * 100)
              return (
                <div key={bin.bin} className="space-y-1">
                  <div className="flex items-center justify-between gap-2 text-[10px] text-tertiary-foreground">
                    <span className="font-mono">{bin.bin}</span>
                    <span>
                      {bin.predictionCount} pair{bin.predictionCount === 1 ? '' : 's'} · stated{' '}
                      {measurablePct(bin.meanConfidence, true)} · observed{' '}
                      {measurablePct(bin.observedAccuracy, true)}
                    </span>
                  </div>
                  <div className="grid grid-cols-2 gap-2">
                    <BarTrack pct={confPct} label="stated" />
                    <BarTrack pct={accPct} label="observed" />
                  </div>
                </div>
              )
            })}
          </div>
        )}
      </div>
    </Card>
  )
}

function Metric({
  label,
  value,
  testId,
}: {
  label: string
  value: string
  testId: string
}) {
  return (
    <div>
      <div className="text-[10px] uppercase tracking-wider text-tertiary-foreground">{label}</div>
      <div className="font-mono text-sm font-semibold tabular-nums text-foreground" data-testid={testId}>
        {value}
      </div>
    </div>
  )
}

function BarTrack({ pct, label }: { pct: number; label: string }) {
  const width = Math.max(0, Math.min(100, pct))
  return (
    <div className="space-y-0.5">
      <div className="h-1.5 rounded-full bg-interactive-secondary overflow-hidden" aria-hidden="true">
        <div className="h-full rounded-full bg-primary/70" style={{ width: `${width}%` }} />
      </div>
      <div className="text-[9px] uppercase tracking-wider text-tertiary-foreground">{label}</div>
    </div>
  )
}
