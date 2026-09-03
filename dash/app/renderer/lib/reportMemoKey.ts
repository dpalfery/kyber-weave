import { localDateKey } from './period'
import type { DateRange, Period } from './types'

/** Identity for a durable report snapshot. Today and Month include the local
 * calendar boundary so yesterday's exact answer can never paint under today's
 * label after midnight; rolling/historical horizons remain reusable. */
export function reportMemoKey(
  section: string,
  period: Period,
  provider = 'all',
  range: DateRange | null = null,
  variant = '',
  now = new Date(),
): string {
  const boundary = period === 'today'
    ? localDateKey(now)
    : period === 'month'
      ? `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}`
      : ''
  return `${section}|${period}|${provider}|${range?.from ?? ''}-${range?.to ?? ''}|${variant}|${boundary}`
}
