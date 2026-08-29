import type { DailyEntry } from './payload'
import {
  formatDateKey, addDays, startOfDay, firstOfMonth, daysInMonth, dayOfMonth,
  previousMonthRange, parseDateKey, MS_PER_DAY,
} from './dates'

/// Derived numbers over `history.daily` that several insights share (Trend, Forecast,
/// Stats, Tips). One implementation so the streak in Tips and the streak in Stats agree.

const MAX_STREAK_LOOKBACK_DAYS = 400
const WEEK_DAYS = 7

export type HistoryStats = {
  weekTotal: number
  priorWeekTotal: number
  weekDelta: number | null
  yesterday: number
  monthToDate: number
  monthProjection: number
  previousMonthTotal: number | null
  activeDaysThisMonth: number
  currentStreak: number
  longestStreak: number
  peak: DailyEntry | null
  trackedTotal: number
  trackedDays: number
}

export function computeHistoryStats(history: DailyEntry[], now = new Date()): HistoryStats {
  const today = startOfDay(now)
  const costByDate = new Map(history.map(d => [d.date, d.cost]))
  const sum = (from: string, to: string) =>
    history.filter(d => d.date >= from && d.date <= to).reduce((s, d) => s + d.cost, 0)

  const todayKey = formatDateKey(today)
  const weekStart = formatDateKey(addDays(today, -(WEEK_DAYS - 1)))
  const priorWeekStart = formatDateKey(addDays(today, -(2 * WEEK_DAYS - 1)))
  const priorWeekEnd = formatDateKey(addDays(today, -WEEK_DAYS))
  const weekTotal = sum(weekStart, todayKey)
  const priorWeekTotal = sum(priorWeekStart, priorWeekEnd)
  const weekDelta = priorWeekTotal > 0 ? ((weekTotal - priorWeekTotal) / priorWeekTotal) * 100 : null

  const yesterday = costByDate.get(formatDateKey(addDays(today, -1))) ?? 0

  const fomKey = formatDateKey(firstOfMonth(now))
  const monthToDate = sum(fomKey, todayKey)
  const dom = dayOfMonth(now)
  const monthProjection = dom > 0 ? (monthToDate / dom) * daysInMonth(now) : 0
  const prev = previousMonthRange(now)
  const prevEntries = history.filter(d => d.date >= prev.first && d.date <= prev.last)
  const previousMonthTotal = prevEntries.length > 0 ? prevEntries.reduce((s, d) => s + d.cost, 0) : null

  const activeDaysThisMonth = history.filter(d => d.date >= fomKey && d.cost > 0).length

  let currentStreak = 0
  for (let i = 0; i < MAX_STREAK_LOOKBACK_DAYS; i++) {
    if ((costByDate.get(formatDateKey(addDays(today, -i))) ?? 0) > 0) currentStreak++
    else break
  }

  let longestStreak = 0
  if (history.length > 0) {
    const first = parseDateKey([...history].sort((a, b) => a.date.localeCompare(b.date))[0].date)
    const totalDays = Math.min(
      MAX_STREAK_LOOKBACK_DAYS,
      Math.round((today.getTime() - first.getTime()) / MS_PER_DAY) + 1,
    )
    const start = addDays(today, -(totalDays - 1))
    let running = 0
    for (let i = 0; i < totalDays; i++) {
      if ((costByDate.get(formatDateKey(addDays(start, i))) ?? 0) > 0) {
        running++
        longestStreak = Math.max(longestStreak, running)
      } else {
        running = 0
      }
    }
  }

  const peak = history.reduce<DailyEntry | null>(
    (best, d) => (!best || d.cost > best.cost) ? d : best, null,
  )

  return {
    weekTotal,
    priorWeekTotal,
    weekDelta,
    yesterday,
    monthToDate,
    monthProjection,
    previousMonthTotal,
    activeDaysThisMonth,
    currentStreak,
    longestStreak,
    peak: peak && peak.cost > 0 ? peak : null,
    trackedTotal: history.reduce((s, d) => s + d.cost, 0),
    trackedDays: history.length,
  }
}
