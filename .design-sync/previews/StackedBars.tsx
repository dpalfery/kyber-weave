import { Panel, StackedBars } from 'codeburn-desktop'

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

const OPUS = 'claude-opus-5'
const SONNET = 'claude-sonnet-5'
const HAIKU = 'claude-haiku-4.5'
const CODEX = 'gpt-5.5-codex'

function round2(n: number): number {
  return Math.round(n * 100) / 100
}

function day(date: string, models: Array<[string, number]>): DailyEntry {
  const cost = round2(models.reduce((sum, [, modelCost]) => sum + modelCost, 0))
  return {
    date,
    cost,
    savingsUSD: 0,
    calls: Math.round(cost * 21),
    inputTokens: Math.round(cost * 9_400),
    outputTokens: Math.round(cost * 1_250),
    cacheReadTokens: Math.round(cost * 24_000),
    cacheWriteTokens: Math.round(cost * 2_100),
    topModels: models.map(([name, modelCost]) => ({
      name,
      cost: modelCost,
      savingsUSD: 0,
      calls: Math.round(modelCost * 18),
      inputTokens: Math.round(modelCost * 9_400),
      outputTokens: Math.round(modelCost * 1_250),
    })),
  }
}

/** A fortnight of real spend: weekday peaks, a quiet weekend, one idle Sunday. */
const FORTNIGHT: DailyEntry[] = [
  day('2026-08-21', [[OPUS, 18.4], [SONNET, 9.1], [HAIKU, 1.2]]),
  day('2026-08-22', [[SONNET, 2.3], [HAIKU, 0.4]]),
  day('2026-08-23', []),
  day('2026-08-24', [[OPUS, 24.1], [SONNET, 11.8], [HAIKU, 1.9], [CODEX, 4.6]]),
  day('2026-08-25', [[OPUS, 31.6], [SONNET, 13.2], [HAIKU, 2.4], [CODEX, 6.15]]),
  day('2026-08-26', [[OPUS, 27.9], [SONNET, 15.05], [HAIKU, 2.1], [CODEX, 3.8]]),
  day('2026-08-27', [[OPUS, 33.4], [SONNET, 12.6], [HAIKU, 3.05], [CODEX, 8.9]]),
  day('2026-08-28', [[OPUS, 21.75], [SONNET, 10.4], [HAIKU, 1.65], [CODEX, 5.2]]),
  day('2026-08-29', [[SONNET, 3.1], [HAIKU, 0.85]]),
  day('2026-08-30', [[OPUS, 6.2], [SONNET, 2.05]]),
  day('2026-08-31', [[OPUS, 29.85], [SONNET, 14.3], [HAIKU, 2.75], [CODEX, 7.4]]),
  day('2026-09-01', [[OPUS, 35.2], [SONNET, 16.1], [HAIKU, 3.4], [CODEX, 9.55]]),
  day('2026-09-02', [[OPUS, 30.1], [SONNET, 13.75], [HAIKU, 2.2], [CODEX, 6.8]]),
  day('2026-09-03', [[OPUS, 26.45], [SONNET, 18.9], [HAIKU, 2.95], [CODEX, 5.35]]),
  day('2026-09-04', [[OPUS, 22.3], [SONNET, 11.15], [HAIKU, 1.8], [CODEX, 4.05]]),
]

/** The Spend section's chart card: every model series stacked per day. */
export function DailySpendByModel() {
  return (
    <div style={{ maxWidth: 520 }}>
      <Panel title="Daily spend by model" right="Last 15 days" className="spend-chart-panel">
        <StackedBars daily={FORTNIGHT} />
      </Panel>
    </div>
  )
}

/** `dataStart` — the window opens before the first scanned day. Those days get
 *  a dashed no-data rule, never a $0.00 column the app cannot vouch for. */
export function BeforeRecordedHistory() {
  const window = [
    day('2026-08-25', []),
    day('2026-08-26', []),
    day('2026-08-27', []),
    ...FORTNIGHT.slice(7),
  ]
  return (
    <div style={{ maxWidth: 520 }}>
      <Panel title="Daily spend by model" right="Scanning since Aug 28" className="spend-chart-panel">
        <StackedBars daily={window} dataStart="2026-08-28" />
      </Panel>
    </div>
  )
}

/** `fallbackLabel` — a provider-filtered range carries daily totals but no
 *  per-model split, so each day draws one segment under the provider's name. */
export function ProviderFiltered() {
  const daily = [
    day('2026-08-29', []),
    { ...day('2026-08-30', []), cost: 4.85 },
    { ...day('2026-08-31', []), cost: 22.4 },
    { ...day('2026-09-01', []), cost: 31.15 },
    { ...day('2026-09-02', []), cost: 26.7 },
    { ...day('2026-09-03', []), cost: 29.05 },
    { ...day('2026-09-04', []), cost: 18.6 },
  ]
  return (
    <div style={{ maxWidth: 520 }}>
      <Panel title="Daily spend by model" right="Provider · Claude Code" className="spend-chart-panel">
        <StackedBars daily={daily} fallbackLabel="Claude Code" />
      </Panel>
    </div>
  )
}

/** A low-spend week on cheap models — two idle days, a two-entry legend. */
export function QuietWeek() {
  const daily = [
    day('2026-08-29', [[HAIKU, 0.32]]),
    day('2026-08-30', []),
    day('2026-08-31', [[SONNET, 2.4], [HAIKU, 0.55]]),
    day('2026-09-01', [[SONNET, 3.85], [HAIKU, 0.7]]),
    day('2026-09-02', [[SONNET, 1.6], [HAIKU, 0.28]]),
    day('2026-09-03', []),
    day('2026-09-04', [[SONNET, 2.95], [HAIKU, 0.48]]),
  ]
  return (
    <div style={{ maxWidth: 520 }}>
      <Panel title="Daily spend by model" right="Last 7 days" className="spend-chart-panel">
        <StackedBars daily={daily} />
      </Panel>
    </div>
  )
}
