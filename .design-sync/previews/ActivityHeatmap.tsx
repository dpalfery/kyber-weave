import { ActivityHeatmap } from 'codeburn-desktop'

type DailyEntry = {
  date: string
  cost: number
  savingsUSD: number
  calls: number
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  topModels: Array<{ name: string; cost: number; savingsUSD: number; calls: number; inputTokens: number; outputTokens: number }>
}

/** Local-noon date `daysAgo` days back. The 26-week grid always ends on the
 *  render date, so fixed dates would land outside the window and draw nothing. */
function dayAt(daysAgo: number): Date {
  const date = new Date()
  date.setHours(12, 0, 0, 0)
  date.setDate(date.getDate() - daysAgo)
  return date
}

function dateKey(date: Date): string {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
}

/** Deterministic 0..1 noise so every rebuild renders the identical grid. */
function noise(n: number): number {
  const x = Math.sin(n * 127.1) * 43758.5453
  return x - Math.floor(x)
}

// Sun..Sat. A working codebase leans on Tue-Thu and goes quiet at the weekend.
const WEEKDAY_WEIGHT = [0.14, 0.92, 1.18, 1.04, 1.22, 0.76, 0.2]

function entry(date: string, cost: number): DailyEntry {
  return {
    date,
    cost,
    savingsUSD: 0,
    calls: cost > 0 ? Math.round(cost * 31) + 4 : 0,
    inputTokens: Math.round(cost * 9_400),
    outputTokens: Math.round(cost * 1_250),
    cacheReadTokens: Math.round(cost * 24_000),
    cacheWriteTokens: Math.round(cost * 2_100),
    topModels: [],
  }
}

function history(
  days: number,
  { peak = 46, idleRate = 0.08, ramp = true, sprints = false }: { peak?: number; idleRate?: number; ramp?: boolean; sprints?: boolean } = {},
): DailyEntry[] {
  const out: DailyEntry[] = []
  for (let back = days - 1; back >= 0; back--) {
    const date = dayAt(back)
    const wobble = noise(back + 11)
    const elapsed = (days - 1 - back) / Math.max(1, days - 1)
    const growth = ramp ? 0.32 + 0.68 * elapsed : 1
    const working = !sprints || noise(Math.floor((days - 1 - back) / 7) + 5) > 0.42
    const spend = peak * WEEKDAY_WEIGHT[date.getDay()] * growth * (0.5 + wobble)
    const idle = wobble < idleRate || !working
    out.push(entry(dateKey(date), idle ? 0 : Math.round(spend * 100) / 100))
  }
  return out
}

/** The default card: half a year of recorded days, weekday rhythm visible. */
export function SixMonths() {
  return (
    <div style={{ maxWidth: 470 }}>
      <ActivityHeatmap daily={history(190)} />
    </div>
  )
}

/** `bare` — the tile the Overview hero renders beside the headline spend. */
export function HeroTile() {
  return (
    <div className="ov-card ov-hero-split" style={{ maxWidth: 470 }}>
      <div className="ov-hero-main">
        <div className="ov-hero-top">
          <span className="ov-label">Last 30 days</span>
          <span className="ov-streak"><b>14</b>-day streak</span>
        </div>
        <div className="ov-hero-num">$612.48</div>
        <div className="ov-hero-sub">1,220 calls · 88 sessions</div>
      </div>
      <ActivityHeatmap daily={history(126, { peak: 38 })} bare />
    </div>
  )
}

/** Sprint weeks against idle weeks: genuine $0.00 days inside recorded history. */
export function BurstyWeeks() {
  return (
    <div style={{ maxWidth: 470 }}>
      <ActivityHeatmap daily={history(190, { peak: 38, sprints: true })} />
    </div>
  )
}

/** A fresh install: nine recorded days. Everything earlier is "no data" —
 *  hollow cells — rather than a currency zero the app never measured. */
export function FirstDaysRecorded() {
  return (
    <div style={{ maxWidth: 470 }}>
      <ActivityHeatmap daily={history(9, { peak: 24, ramp: false, idleRate: 0.12 })} />
    </div>
  )
}
