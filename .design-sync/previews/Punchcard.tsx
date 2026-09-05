import { Panel, Punchcard } from 'codeburn-desktop'

type TimelinePoint = { timestamp: string; cost: number }

// Mon..Sun. Local timestamps (no trailing Z) so the hour a point lands in is
// the hour it was authored in, whatever zone the preview renders in.
const WEEK = ['2026-08-31', '2026-09-01', '2026-09-02', '2026-09-03', '2026-09-04', '2026-09-05', '2026-09-06']
const DAY_WEIGHT = [1, 1.18, 1.06, 1.22, 0.84, 0.22, 0.38]
const HOUR_WEIGHT = [
  0.04, 0, 0, 0, 0, 0, 0.06, 0.22, 0.55, 0.92, 1, 0.88,
  0.45, 0.8, 1, 0.96, 0.82, 0.6, 0.34, 0.22, 0.3, 0.36, 0.18, 0.07,
]

/** Deterministic 0..1 noise so every rebuild renders the identical matrix. */
function noise(n: number): number {
  const x = Math.sin(n * 91.7) * 21_817.293
  return x - Math.floor(x)
}

function at(date: string, hour: number, minute = 0): string {
  return `${date}T${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}:00`
}

/** An hourly week of agent runs: office-hours mass, a post-lunch peak, a
 *  thin weekend, and the odd bucket that opened a session but spent nothing. */
function hourlyWeek(): TimelinePoint[] {
  const points: TimelinePoint[] = []
  WEEK.forEach((date, weekday) => {
    for (let hour = 0; hour < 24; hour++) {
      const weight = HOUR_WEIGHT[hour] * DAY_WEIGHT[weekday]
      const wobble = noise(weekday * 37 + hour * 3)
      if (weight * (0.45 + wobble) < 0.12) continue
      const cost = 3.9 * weight * (0.4 + wobble * 1.2)
      points.push({ timestamp: at(date, hour), cost: cost < 0.05 ? 0 : Math.round(cost * 100) / 100 })
    }
  })
  // A Wednesday-night release push that runs past midnight.
  points.push({ timestamp: at('2026-09-02', 23), cost: 4.85 })
  points.push({ timestamp: at('2026-09-03', 0), cost: 3.4 })
  points.push({ timestamp: at('2026-09-03', 1), cost: 1.95 })
  return points
}

/** The Spend section's punchcard: a full week at hourly resolution. */
export function HourlyWeek() {
  return (
    <div style={{ maxWidth: 640 }}>
      <Panel title="Spend punchcard" right="hour of day × weekday">
        <Punchcard timeline={{ bucketMinutes: 60, points: hourlyWeek() }} />
      </Panel>
    </div>
  )
}

/** A two-day custom range comes back at 15-minute resolution: the same matrix,
 *  a finer bucket label, and only the days the range actually covers. */
export function QuarterHourBuckets() {
  const points: TimelinePoint[] = []
  for (const date of ['2026-09-03', '2026-09-04']) {
    for (let hour = 7; hour <= 23; hour++) {
      for (const minute of [0, 15, 30, 45]) {
        const wobble = noise(hour * 13 + minute + (date === '2026-09-04' ? 5 : 0))
        if (wobble * HOUR_WEIGHT[hour] < 0.09) continue
        points.push({ timestamp: at(date, hour, minute), cost: Math.round(1.9 * wobble * HOUR_WEIGHT[hour] * 100) / 100 })
      }
    }
  }
  return (
    <div style={{ maxWidth: 640 }}>
      <Panel title="Spend punchcard" right="Sep 3 – Sep 4 · custom range">
        <Punchcard timeline={{ bucketMinutes: 15, points }} />
      </Panel>
    </div>
  )
}

/** Ranges wider than a week come back in daily buckets. Rather than stack every
 *  run into a fake midnight column, the card names the limitation. */
export function DailyBucketsTooCoarse() {
  const points = ['2026-08-06', '2026-08-13', '2026-08-20', '2026-08-27', '2026-09-03'].map((date, index) => ({
    timestamp: at(date, 0),
    cost: [38.4, 52.1, 44.75, 61.3, 47.9][index],
  }))
  return (
    <div style={{ maxWidth: 640 }}>
      <Panel title="Spend punchcard" right="last 30 days">
        <Punchcard timeline={{ bucketMinutes: 1440, points }} />
      </Panel>
    </div>
  )
}

/** Nothing timestamped in the range — a provider filter that matched no runs. */
export function NoTimestampedUsage() {
  return (
    <div style={{ maxWidth: 640 }}>
      <Panel title="Spend punchcard" right="provider · Codex">
        <Punchcard timeline={{ bucketMinutes: 60, points: [] }} />
      </Panel>
    </div>
  )
}
