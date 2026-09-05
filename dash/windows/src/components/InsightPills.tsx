export type InsightMode = 'plan' | 'trend' | 'forecast' | 'pulse' | 'stats'

export const INSIGHT_LABELS: Record<InsightMode, string> = {
  plan: 'Plan',
  trend: 'Trend',
  forecast: 'Forecast',
  pulse: 'Pulse',
  stats: 'Stats',
}

/// Same order as the macOS InsightMode enum: Plan first when it is visible.
export const INSIGHT_ORDER: InsightMode[] = ['plan', 'trend', 'forecast', 'pulse', 'stats']

export function isInsightMode(value: string | null): value is InsightMode {
  return value !== null && value in INSIGHT_LABELS
}

type Props = {
  selected: InsightMode
  onSelect: (m: InsightMode) => void
  modes: InsightMode[]
}

export function InsightPills({ selected, onSelect, modes }: Props) {
  return (
    <div className="insight-pills" role="tablist">
      {modes.map(m => (
        <button
          key={m}
          type="button"
          role="tab"
          aria-selected={selected === m}
          className={`insight-pill ${selected === m ? 'insight-pill-active' : ''}`}
          onClick={() => onSelect(m)}
        >
          {INSIGHT_LABELS[m]}
        </button>
      ))}
    </div>
  )
}
