import type { MenubarPayload } from '../lib/payload'
import type { CurrencyState } from '../lib/currency'
import { formatCurrency, formatCompactCurrency, plural } from '../lib/currency'
import { daysInMonth, monthDay } from '../lib/dates'
import { computeHistoryStats } from '../lib/history'
import type { Period } from './PeriodTabs'

type Props = {
  payload: MenubarPayload
  currency: CurrencyState
  period: Period
}

const PERIOD_SUFFIX: Record<Period, string> = {
  today: 'today',
  week: '(7 days)',
  '30days': '(30 days)',
  month: '(month)',
  all: '(all time)',
}

export function StatsInsight({ payload, currency, period }: Props) {
  const s = computeHistoryStats(payload.history.daily)
  const suffix = PERIOD_SUFFIX[period]

  return (
    <div className="stats-insight">
      <div className="stats-grid">
        <div className="stats-col">
          <StatRow label="Favorite model" value={payload.current.topModels[0]?.name ?? '-'} />
          <StatRow label="Active days (month)" value={`${s.activeDaysThisMonth}/${daysInMonth(new Date())}`} />
          <StatRow label="Most active day" value={s.peak ? monthDay(s.peak.date) : '-'} />
          <StatRow label="Peak day spend" value={s.peak ? formatCompactCurrency(s.peak.cost, currency) : '-'} />
        </div>
        <div className="stats-col">
          <StatRow label={`Sessions ${suffix}`} value={payload.current.sessions.toLocaleString()} />
          <StatRow label={`Calls ${suffix}`} value={payload.current.calls.toLocaleString()} />
          <StatRow label="Current streak" value={s.currentStreak > 0 ? plural(s.currentStreak, 'day') : '-'} />
          <StatRow label="Longest streak" value={s.longestStreak > 0 ? plural(s.longestStreak, 'day') : '-'} />
        </div>
      </div>
      {s.trackedDays > 0 && (
        <div className="stats-lifetime">
          <span className="stats-lifetime-label">
            Tracked spend (last {plural(s.trackedDays, 'day')})
          </span>
          <span className="stats-lifetime-value">
            {formatCurrency(s.trackedTotal, currency)}
          </span>
        </div>
      )}
    </div>
  )
}

function StatRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="stat-row">
      <div className="stat-row-label">{label}</div>
      <div className="stat-row-value">{value}</div>
    </div>
  )
}
