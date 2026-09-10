/// Claude subscription usage as returned by the Rust `plan_usage` command, plus the
/// projection math from the macOS PlanInsight so both apps draw the same marker.

export type PlanWindow = {
  key: 'five_hour' | 'seven_day' | 'seven_day_opus' | 'seven_day_sonnet' | string
  label: string
  percent: number
  resets_at: string | null
  previous_final: number | null
}

export type PlanUsage =
  | { state: 'ok'; tier: string; raw_tier: string | null; windows: PlanWindow[]; fetched_at: string }
  | { state: 'no_credentials' }
  | { state: 'failed'; message: string }

export type PlanProjection = {
  percent: number
  willOverflow: boolean
  hitsLimitAt: Date | null
  source: 'linear' | 'historical'
}

const FIVE_HOUR_SECONDS = 5 * 3600
const SEVEN_DAY_SECONDS = 7 * 86_400
/// Below this fraction of the window the linear extrapolation is noise; fall back to
/// last cycle's final reading instead.
const FRESH_WINDOW_THRESHOLD = 0.05
const FULL_PERCENT = 100

function windowSeconds(key: string): number {
  return key === 'five_hour' ? FIVE_HOUR_SECONDS : SEVEN_DAY_SECONDS
}

export function projectWindow(window: PlanWindow, now = new Date()): PlanProjection | null {
  if (!window.resets_at) return null
  const resetsAt = new Date(window.resets_at)
  if (Number.isNaN(resetsAt.getTime())) return null
  const seconds = windowSeconds(window.key)
  const windowStart = resetsAt.getTime() / 1000 - seconds
  const elapsed = now.getTime() / 1000 - windowStart
  const elapsedFraction = elapsed / seconds

  if (elapsedFraction > FRESH_WINDOW_THRESHOLD && window.percent > 0) {
    const projected = window.percent / elapsedFraction
    let hitsLimitAt: Date | null = null
    if (projected > FULL_PERCENT && window.percent < FULL_PERCENT) {
      const percentPerSecond = window.percent / elapsed
      if (percentPerSecond > 0) {
        hitsLimitAt = new Date(now.getTime() + ((FULL_PERCENT - window.percent) / percentPerSecond) * 1000)
      }
    }
    return { percent: projected, willOverflow: projected > FULL_PERCENT, hitsLimitAt, source: 'linear' }
  }

  if (window.previous_final != null) {
    return {
      percent: window.previous_final,
      willOverflow: window.previous_final > FULL_PERCENT,
      hitsLimitAt: null,
      source: 'historical',
    }
  }
  return null
}

export function earliestReset(windows: PlanWindow[]): Date | null {
  const dates = windows
    .map(w => (w.resets_at ? new Date(w.resets_at) : null))
    .filter((d): d is Date => d !== null && !Number.isNaN(d.getTime()))
  if (dates.length === 0) return null
  return dates.reduce((a, b) => (a < b ? a : b))
}
