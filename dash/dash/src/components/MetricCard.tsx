import { Card } from './ui/card'
import { cn } from '@/lib/utils'

export function MetricCard({
  label,
  value,
  sub,
  accent,
  metricKey,
  measured,
  reason,
}: {
  label: string
  value: string
  sub?: string
  accent?: boolean
  metricKey?: string
  measured?: boolean
  reason?: string
}) {
  const key = metricKey ?? label.toLowerCase().replace(/[^a-z0-9]+/g, '-')
  const isMeasured = measured ?? value !== '—'
  const missingReason = reason ?? 'This metric was not reported by the source.'

  return (
    <Card
      className="px-4 py-3.5"
      data-testid={`metric-${key}`}
      data-measured={String(isMeasured)}
      title={isMeasured ? undefined : missingReason}
    >
      <div className="text-[11px] uppercase tracking-wider text-tertiary-foreground">{label}</div>
      <div className={cn('mt-1.5 text-2xl font-semibold tabular-nums tracking-tight', accent && 'text-primary')}>
        {value}
      </div>
      {sub ? <div className="mt-1 text-xs text-tertiary-foreground">{sub}</div> : null}
    </Card>
  )
}
