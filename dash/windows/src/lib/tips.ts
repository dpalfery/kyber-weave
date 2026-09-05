import type { MenubarPayload } from './payload'
import type { CurrencyState } from './currency'
import { formatCompactCurrency, formatSmallCurrency } from './currency'
import { computeHistoryStats } from './history'

export type TipItem = { text: string; trailing: string | null }
export type TipGroup = { label: string; icon: string; items: TipItem[] }

const CACHE_HIT_GOOD = 80
const CACHE_HIT_LOW = 50
const ONESHOT_GOOD = 0.75
const ONESHOT_LOW = 0.5
const SPEND_DOWN_THRESHOLD = -10
const SPEND_UP_THRESHOLD = 25
const STREAK_MILESTONE = 5
const MONTH_GROWTH_WARNING = 1.3
const TOP_FINDINGS_COUNT = 3

export function computeTipGroups(payload: MenubarPayload, currency: CurrencyState): TipGroup[] {
  const stats = computeHistoryStats(payload.history.daily)
  const { cacheHitPercent, oneShotRate } = payload.current

  const wins: TipItem[] = []
  if (cacheHitPercent >= CACHE_HIT_GOOD) wins.push({ text: `Cache hit at ${Math.round(cacheHitPercent)}% - most prompts reuse cache`, trailing: null })
  if (oneShotRate != null && oneShotRate >= ONESHOT_GOOD) wins.push({ text: `${Math.round(oneShotRate * 100)}% one-shot - edits landing first try`, trailing: null })
  if (stats.weekDelta != null && stats.weekDelta < SPEND_DOWN_THRESHOLD) wins.push({ text: `Spend down ${Math.round(Math.abs(stats.weekDelta))}% vs last 7 days`, trailing: null })
  if (stats.currentStreak >= STREAK_MILESTONE) wins.push({ text: `${stats.currentStreak}-day usage streak`, trailing: null })

  const improvements: TipItem[] = payload.optimize.topFindings.slice(0, TOP_FINDINGS_COUNT).map(f => ({
    text: f.title,
    trailing: formatSmallCurrency(f.savingsUSD, currency),
  }))

  const risks: TipItem[] = []
  if (stats.weekDelta != null && stats.weekDelta > SPEND_UP_THRESHOLD) risks.push({ text: `Spend up ${Math.round(stats.weekDelta)}% vs prior 7 days`, trailing: null })
  if (cacheHitPercent > 0 && cacheHitPercent < CACHE_HIT_LOW) risks.push({ text: `Cache hit only ${Math.round(cacheHitPercent)}% - paying for cold prompts`, trailing: null })
  if (oneShotRate != null && oneShotRate < ONESHOT_LOW) risks.push({ text: `${Math.round(oneShotRate * 100)}% one-shot - lots of iteration`, trailing: null })
  if (stats.previousMonthTotal != null && stats.previousMonthTotal > 0 && stats.monthProjection > stats.previousMonthTotal * MONTH_GROWTH_WARNING) {
    const pct = Math.round(((stats.monthProjection - stats.previousMonthTotal) / stats.previousMonthTotal) * 100)
    risks.push({ text: `On pace for ${formatCompactCurrency(stats.monthProjection, currency)} this month (+${pct}% vs last)`, trailing: null })
  }

  return [
    { label: "What's working", icon: 'check', items: wins },
    { label: 'What to improve', icon: 'up', items: improvements },
    { label: 'Risks', icon: 'warn', items: risks },
  ]
}
