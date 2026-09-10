import { useState } from 'react'
import type { DailyEntry, DailyModel } from '../lib/payload'
import type { CurrencyState } from '../lib/currency'
import { formatCompactCurrency, formatCurrency, formatTokens } from '../lib/currency'
import { todayKey, formatDateKey, addDays, startOfDay, prettyDate, shortDate } from '../lib/dates'
import { ArrowUpRight, ArrowDownRight } from './Icons'

/// 19 columns of 13px bars with 4px gaps = 319px, the widest chart that fits the 332px
/// content width of a 360px popover (mirrors mac trendDays / trendBarWidth / trendBarGap).
export const TREND_DAYS = 19
const MAX_TOOLTIP_MODELS = 4
const MIN_BAR_PCT = 2

type TrendBar = {
  date: string
  cost: number
  tokens: number
  isToday: boolean
  topModels: DailyModel[]
}

function buildBars(days: DailyEntry[]): TrendBar[] {
  const byDate = new Map(days.map(d => [d.date, d]))
  const today = startOfDay(new Date())
  const tk = todayKey()
  const bars: TrendBar[] = []
  for (let i = TREND_DAYS - 1; i >= 0; i--) {
    const key = formatDateKey(addDays(today, -i))
    const entry = byDate.get(key)
    bars.push({
      date: key,
      cost: entry?.cost ?? 0,
      tokens: (entry?.inputTokens ?? 0) + (entry?.outputTokens ?? 0),
      isToday: key === tk,
      topModels: entry?.topModels ?? [],
    })
  }
  return bars
}

function computeDelta(bars: TrendBar[], allDays: DailyEntry[]): number | null {
  const thisTotal = bars.reduce((s, b) => s + b.cost, 0)
  const today = startOfDay(new Date())
  const priorStart = formatDateKey(addDays(today, -(2 * TREND_DAYS - 1)))
  const thisStart = formatDateKey(addDays(today, -(TREND_DAYS - 1)))
  const priorTotal = allDays
    .filter(d => d.date >= priorStart && d.date < thisStart)
    .reduce((s, d) => s + d.cost, 0)
  if (priorTotal <= 0) return null
  return ((thisTotal - priorTotal) / priorTotal) * 100
}

type Props = {
  days: DailyEntry[]
  currency: CurrencyState
}

export function TrendInsight({ days, currency }: Props) {
  const [hoveredIdx, setHoveredIdx] = useState<number | null>(null)
  const bars = buildBars(days)
  const totalTokens = bars.reduce((s, b) => s + b.tokens, 0)
  const useTokens = totalTokens > 0
  const metric = (b: TrendBar) => useTokens ? b.tokens : b.cost
  const maxVal = Math.max(...bars.map(metric), 0.01)
  const avgVal = bars.reduce((s, b) => s + metric(b), 0) / bars.length
  const totalCost = bars.reduce((s, b) => s + b.cost, 0)
  const peak = bars.filter(b => metric(b) > 0).sort((a, b) => metric(b) - metric(a))[0]
  const yd = formatDateKey(addDays(startOfDay(new Date()), -1))
  const yesterday = bars.find(b => b.date === yd)
  const delta = computeDelta(bars, days)

  const fmtVal = (v: number) => useTokens ? `${formatTokens(v)} tok` : formatCompactCurrency(v, currency)
  const heroText = useTokens ? `${formatTokens(totalTokens)} tokens` : formatCurrency(totalCost, currency)
  const hovered = hoveredIdx !== null ? bars[hoveredIdx] : null

  return (
    <div className="trend-insight">
      <div className="insight-header">
        <div>
          <div className="insight-sublabel">Last {TREND_DAYS} days</div>
          <div className="insight-hero">{heroText}</div>
        </div>
        {delta !== null && (
          <div className="delta-badge">
            {delta >= 0 ? <ArrowUpRight size={9} /> : <ArrowDownRight size={9} />}
            <span>{delta >= 0 ? '+' : ''}{Math.round(delta)}% vs prior {TREND_DAYS}d</span>
          </div>
        )}
      </div>

      <div className="trend-chart" onMouseLeave={() => setHoveredIdx(null)}>
        <div className="trend-bars">
          {bars.map((bar, i) => {
            const val = metric(bar)
            const pct = (val / maxVal) * 100
            const cls = [
              'trend-bar',
              bar.isToday ? 'trend-bar-today' : '',
              val <= 0 ? 'trend-bar-empty' : '',
              hoveredIdx === i ? 'trend-bar-hovered' : '',
            ].join(' ')
            return (
              <div
                key={bar.date}
                className="trend-bar-col"
                onMouseEnter={() => setHoveredIdx(i)}
              >
                <div className={cls} style={{ height: `${Math.max(MIN_BAR_PCT, pct)}%` }} />
              </div>
            )
          })}
        </div>
        <div
          className="trend-avg-line"
          style={{ bottom: `${Math.min((avgVal / maxVal) * 100, 100)}%` }}
        />
        {hovered && (
          <div className="bar-tooltip" role="tooltip">
            <div className="bar-tooltip-header">
              <span>{prettyDate(hovered.date)}</span>
              <span className="bar-tooltip-value">{fmtVal(metric(hovered))}</span>
            </div>
            {hovered.topModels.slice(0, MAX_TOOLTIP_MODELS).map(m => (
              <div key={m.name} className="bar-tooltip-model">
                <span className="bar-tooltip-dot" />
                <span className="bar-tooltip-name">{m.name}</span>
                <span className="bar-tooltip-tokens">{formatTokens(m.inputTokens + m.outputTokens)} tok</span>
                <span className="bar-tooltip-split">({formatTokens(m.inputTokens)}/{formatTokens(m.outputTokens)})</span>
              </div>
            ))}
          </div>
        )}
      </div>

      <div className="mini-stats">
        <div className="mini-stat">
          <div className="mini-stat-label">Avg/day</div>
          <div className="mini-stat-value">{fmtVal(avgVal)}</div>
        </div>
        <div className="mini-stat">
          <div className="mini-stat-label">Peak</div>
          <div className="mini-stat-value">
            {peak ? `${fmtVal(metric(peak))} on ${shortDate(peak.date)}` : '-'}
          </div>
        </div>
        <div className="mini-stat">
          <div className="mini-stat-label">Yesterday</div>
          <div className="mini-stat-value">{yesterday ? fmtVal(metric(yesterday)) : '-'}</div>
        </div>
      </div>
    </div>
  )
}
