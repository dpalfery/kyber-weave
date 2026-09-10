// Unified dashboard data path for KyberDash (spec: docs/specs/kyberdash,
// task 10.1; R11.1, R11.2; design.md "Surface layer").
//
// The terminal dashboard's period reports, breakdown tables and daily
// activity MUST be derived from one data path that covers both file-sourced
// and OTLP-sourced sessions. This module is that path: `getDashboardData`
// queries the canonical store for all records and aggregates the same
// figures the live-parse dashboard renders, so the terminal sees the
// unified corpus rather than only what the parser just read.
//
// The store is the seam. Session files become canonical records through
// `dash/kyber/synth` (the span synthesizer), OTLP payloads become canonical
// records through `dash/kyber/otel`, and both land in the same `records`
// table. This module never reaches into upstream's parser or any provider;
// it reads the store that already holds the unified corpus (R14.2, adapt at
// the boundary). The terminal imports `getDashboardData` from this shim
// rather than calling `parseAllSessions` directly, which is what makes
// file-sourced and OTLP-sourced sessions appear together.
//
// What "period reports, breakdown tables and daily activity" means for this
// seam (R11.2):
//
//   * period reports — a daily and a weekly slice, the two windows the
//     dashboard's tabs render, computed relative to the latest record so a
//     test with synthetic dates in the past is deterministic rather than
//     wall-clock dependent;
//   * breakdown tables — one per harness (the `harness` field, never the
//     source name, R6.2) and one per model (the `cost.byModel` keys that
//     both ingest paths stamp, falling back to the record name's model
//     suffix when no priced cost is present);
//   * daily activity — one row per calendar day, cost and call count.

import type { CanonStore } from '../canon/store.js'
import type { CanonicalRecord } from '../canon/types.js'

// ---------------------------------------------------------------------------
// Public data shape returned to the terminal
// ---------------------------------------------------------------------------

export type PeriodReport = {
  /** `daily` is the latest calendar day, `weekly` the 7 days ending on it. */
  period: 'daily' | 'weekly'
  label: string
  /** Inclusive ISO date bounds of the slice, YYYY-MM-DD. */
  start: string
  end: string
  cost: number
  calls: number
  tokens: { input: number; output: number; cacheRead: number; cacheWrite: number }
}

export type ProviderBreakdown = {
  provider: string
  harness: string
  calls: number
  cost: number
  inputTokens: number
  outputTokens: number
  cacheRead: number
  cacheWrite: number
}

export type ModelBreakdown = {
  model: string
  calls: number
  cost: number
  tokens: number
}

export type DailyActivity = {
  /** Calendar day, YYYY-MM-DD. */
  day: string
  cost: number
  calls: number
  tokens: number
  inputTokens: number
  outputTokens: number
}

export type DashboardTotals = {
  cost: number
  calls: number
  sessions: number
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
}

export type DashboardData = {
  totals: DashboardTotals
  periodReports: PeriodReport[]
  breakdownByProvider: ProviderBreakdown[]
  breakdownByModel: ModelBreakdown[]
  dailyActivity: DailyActivity[]
  /** How many canonical records the aggregates cover — the one count every total reconciles to. */
  recordCount: number
}

// ---------------------------------------------------------------------------
// Helpers: timestamp and day handling
// ---------------------------------------------------------------------------

function parseTimestamp(record: CanonicalRecord): Date {
  return record.timestamp instanceof Date ? record.timestamp : new Date(record.timestamp)
}

function dayString(date: Date): string {
  return date.toISOString().slice(0, 10)
}

function addDays(date: Date, delta: number): Date {
  const next = new Date(date)
  next.setUTCDate(next.getUTCDate() + delta)
  return next
}

function recordCost(record: CanonicalRecord): number {
  return record.cost.value ?? 0
}

function modelNames(record: CanonicalRecord): string[] {
  const keys = record.cost.byModel ? Object.keys(record.cost.byModel) : []
  if (keys.length > 0) return keys.sort()
  // Synth names are "harness:model", OTLP names are span names that still
  // carry the model in the same suffix position when no cost breakdown is
  // present; splitting on the first colon keeps the fallback deterministic.
  const suffix = record.name.includes(':') ? record.name.slice(record.name.indexOf(':') + 1) : record.name
  return suffix ? [suffix] : [record.harness]
}

// ---------------------------------------------------------------------------
// Aggregation helpers
// ---------------------------------------------------------------------------

function sumTokens(records: readonly CanonicalRecord[]): {
  input: number
  output: number
  cacheRead: number
  cacheWrite: number
} {
  let input = 0
  let output = 0
  let cacheRead = 0
  let cacheWrite = 0
  for (const record of records) {
    input += record.tokens.reportedInput
    output += record.tokens.reportedOutput
    cacheRead += record.tokens.cacheRead
    cacheWrite += record.tokens.cacheCreation
  }
  return { input, output, cacheRead, cacheWrite }
}

function totalsOf(records: readonly CanonicalRecord[]): { cost: number; calls: number; tokens: ReturnType<typeof sumTokens>; sessions: number } {
  const cost = records.reduce((sum, r) => sum + recordCost(r), 0)
  const tokens = sumTokens(records)
  const traceIds = new Set(records.map((r) => r.traceId).filter((id): id is string => id !== null))
  return { cost, calls: records.length, tokens, sessions: traceIds.size || records.length }
}

function latestDay(records: readonly CanonicalRecord[]): string | null {
  if (records.length === 0) return null
  let latest = parseTimestamp(records[0]!)
  for (const r of records) {
    const at = parseTimestamp(r)
    if (at > latest) latest = at
  }
  return dayString(latest)
}

function recordsForDay(records: readonly CanonicalRecord[], day: string): CanonicalRecord[] {
  return records.filter((r) => dayString(parseTimestamp(r)) === day)
}

function recordsInWindow(records: readonly CanonicalRecord[], endDay: string, days: number): CanonicalRecord[] {
  const end = new Date(`${endDay}T00:00:00.000Z`)
  const startDay = dayString(addDays(end, -(days - 1)))
  return records.filter((r) => {
    const d = dayString(parseTimestamp(r))
    return d >= startDay && d <= endDay
  })
}

function dailyActivityOf(records: readonly CanonicalRecord[]): DailyActivity[] {
  const byDay = new Map<string, CanonicalRecord[]>()
  for (const record of records) {
    const day = dayString(parseTimestamp(record))
    const bucket = byDay.get(day)
    if (bucket) bucket.push(record)
    else byDay.set(day, [record])
  }
  return [...byDay.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([day, bucket]) => {
      const cost = bucket.reduce((sum, r) => sum + recordCost(r), 0)
      const tokens = sumTokens(bucket)
      return {
        day,
        cost,
        calls: bucket.length,
        tokens: tokens.input + tokens.output,
        inputTokens: tokens.input,
        outputTokens: tokens.output,
      }
    })
}

function providerBreakdownOf(records: readonly CanonicalRecord[]): ProviderBreakdown[] {
  const byHarness = new Map<string, CanonicalRecord[]>()
  for (const record of records) {
    const key = record.harness
    const bucket = byHarness.get(key)
    if (bucket) bucket.push(record)
    else byHarness.set(key, [record])
  }
  return [...byHarness.entries()]
    .map(([harness, bucket]) => {
      const cost = bucket.reduce((sum, r) => sum + recordCost(r), 0)
      const tokens = sumTokens(bucket)
      return {
        provider: harness,
        harness,
        calls: bucket.length,
        cost,
        inputTokens: tokens.input,
        outputTokens: tokens.output,
        cacheRead: tokens.cacheRead,
        cacheWrite: tokens.cacheWrite,
      }
    })
    .sort((a, b) => b.cost - a.cost || b.calls - a.calls || a.provider.localeCompare(b.provider))
}

function modelBreakdownOf(records: readonly CanonicalRecord[]): ModelBreakdown[] {
  const byModel = new Map<string, { cost: number; calls: number; tokens: number }>()
  for (const record of records) {
    const names = modelNames(record)
    const cost = recordCost(record)
    const tokens = record.tokens.reportedInput + record.tokens.reportedOutput
    const shareCost = names.length > 0 ? cost / names.length : cost
    const shareTokens = names.length > 0 ? tokens / names.length : tokens
    const shareCalls = 1 / names.length
    for (const name of names) {
      const existing = byModel.get(name)
      if (existing) {
        existing.cost += shareCost
        existing.tokens += shareTokens
        existing.calls += shareCalls
      } else {
        byModel.set(name, { cost: shareCost, calls: shareCalls, tokens: shareTokens })
      }
    }
  }
  return [...byModel.entries()]
    .map(([model, entry]) => ({ model, ...entry }))
    .sort((a, b) => b.cost - a.cost || b.calls - a.calls || a.model.localeCompare(b.model))
}

function periodReportsOf(records: readonly CanonicalRecord[]): PeriodReport[] {
  if (records.length === 0) return []
  const endDay = latestDay(records)!
  const end = new Date(`${endDay}T00:00:00.000Z`)

  const dailyRecords = recordsForDay(records, endDay)
  const weeklyRecords = recordsInWindow(records, endDay, 7)

  const dailyTotals = totalsOf(dailyRecords)
  const weeklyTotals = totalsOf(weeklyRecords)
  const weeklyStartDay = dayString(addDays(end, -6))

  return [
    {
      period: 'daily',
      label: endDay,
      start: endDay,
      end: endDay,
      cost: dailyTotals.cost,
      calls: dailyTotals.calls,
      tokens: dailyTotals.tokens,
    },
    {
      period: 'weekly',
      label: `${weeklyStartDay} – ${endDay}`,
      start: weeklyStartDay,
      end: endDay,
      cost: weeklyTotals.cost,
      calls: weeklyTotals.calls,
      tokens: weeklyTotals.tokens,
    },
  ]
}

// ---------------------------------------------------------------------------
// Public entry point
// ---------------------------------------------------------------------------

/**
 * Build the unified terminal-dashboard view model from the canonical store
 * (R11.1, R11.2). Queries the store for all records — the one data path
 * that holds both file-sourced (synthesized) and OTLP-sourced sessions —
 * and aggregates period reports (daily, weekly), breakdown tables by
 * provider and model, and daily activity.
 *
 * The terminal renders from the returned `DashboardData` instead of
 * directly from `parseAllSessions`, so both ingest paths appear together.
 */
export function getDashboardData(store: CanonStore): DashboardData {
  const records = store.listAll()
  const totals = totalsOf(records)
  return {
    totals: {
      cost: totals.cost,
      calls: totals.calls,
      sessions: totals.sessions,
      inputTokens: totals.tokens.input,
      outputTokens: totals.tokens.output,
      cacheReadTokens: totals.tokens.cacheRead,
      cacheWriteTokens: totals.tokens.cacheWrite,
    },
    periodReports: periodReportsOf(records),
    breakdownByProvider: providerBreakdownOf(records),
    breakdownByModel: modelBreakdownOf(records),
    dailyActivity: dailyActivityOf(records),
    recordCount: records.length,
  }
}

/**
 * Render-friendly one-liner the terminal can log or assert in tests: the
 * unified totals as `cost=$X calls=N tokens=M` over the canonical store's
 * records, so a corpus containing both paths visibly sums both.
 */
export function formatDashboardSummary(data: DashboardData): string {
  return `cost=$${data.totals.cost.toFixed(2)} calls=${data.totals.calls} input=${data.totals.inputTokens} output=${data.totals.outputTokens} records=${data.recordCount}`
}
