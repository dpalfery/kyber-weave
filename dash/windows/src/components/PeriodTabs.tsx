export type Period = 'today' | 'week' | '30days' | 'month' | 'all'

export const PERIOD_LABELS: Record<Period, string> = {
  today: 'Today', week: '7 Days', '30days': '30 Days', month: 'Month', all: 'All',
}

/// Short phrase used in sentences ("Sessions (7 days)", "No Claude data for this month").
export const PERIOD_PHRASES: Record<Period, string> = {
  today: 'today',
  week: 'the last 7 days',
  '30days': 'the last 30 days',
  month: 'this month',
  all: 'all time',
}

const PERIODS = Object.keys(PERIOD_LABELS) as Period[]

type Props = {
  selected: Period
  onSelect: (p: Period) => void
}

export function PeriodTabs({ selected, onSelect }: Props) {
  return (
    <div className="period-wrap">
      <nav className="period-tabs" aria-label="Period">
        {PERIODS.map(p => (
          <button
            key={p}
            type="button"
            className={`period ${selected === p ? 'period-active' : ''}`}
            aria-pressed={selected === p}
            onClick={() => onSelect(p)}
          >
            {PERIOD_LABELS[p]}
          </button>
        ))}
      </nav>
    </div>
  )
}
