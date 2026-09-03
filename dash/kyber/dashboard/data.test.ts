// United data path behind the terminal dashboard (spec: docs/specs/kyberdash,
// task 10.1; R11.1, R11.2). The terminal's period reports, breakdown tables
// and daily activity MUST be derived from one data path that covers both
// file-sourced and OTLP-sourced sessions. This test builds a corpus
// containing both — a handful of file-sourced (synth) records and a handful
// of OTLP-sourced records — stores them in the canonical store, and asserts
// the terminal output (the `getDashboardData` view model plus its formatted
// summary) counts both sides together: totals, token sums, breakdowns and
// daily activity all include the file and OTLP records, with no source
// dropped.

import { describe, it, expect } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { FILE_SOURCE_PREFIX } from '../canon/measurability.js'
import type { CanonicalRecord } from '../canon/types.js'
import { getDashboardData, formatDashboardSummary } from './data.js'

function record(overrides: Partial<CanonicalRecord> & { spanId: string }): CanonicalRecord {
  const { timestamp: overrideTimestamp, ...rest } = overrides
  return {
    traceId: `trace-${overrides.spanId}`,
    parentSpanId: null,
    source: `${FILE_SOURCE_PREFIX}claude`,
    harness: 'claude',
    name: 'claude:claude-sonnet-4-5',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: overrideTimestamp ?? '2026-08-10T12:00:00.000Z',
    durationMs: 1000,
    status: 'ok',
    tokens: {
      freshInput: 100,
      cacheRead: 50,
      cacheCreation: 10,
      output: 40,
      reportedInput: 160,
      reportedOutput: 40,
    },
    content: {},
    cost: { basis: 'harness', status: 'priced', value: 0.02, currency: 'USD', byModel: { 'claude-sonnet-4-5': 0.02 } },
    ...rest,
  } as CanonicalRecord
}

describe('getDashboardData unified path (R11.1, R11.2)', () => {
  it('routes period reports, breakdown tables and daily activity through the canonical store so both sources appear together', () => {
    const store = new CanonStore(':memory:')

    // File-sourced sessions: the synthesizer's `codeburn/<provider>` source
    // namespace (R14.2: adapt at the boundary, don't modify upstream).
    const fileRecords: CanonicalRecord[] = [
      record({
        spanId: 'synth:claude:sess-a:msg-1',
        traceId: 'synth:claude:sess-a',
        source: 'codeburn/claude',
        harness: 'claude',
        name: 'claude:claude-sonnet-4-5',
        timestamp: '2026-08-10T10:00:00.000Z',
        cost: { basis: 'harness', status: 'priced', value: 1.5, currency: 'USD', byModel: { 'claude-sonnet-4-5': 1.5 } },
        tokens: { freshInput: 1000, cacheRead: 200, cacheCreation: 50, output: 300, reportedInput: 1250, reportedOutput: 300 },
      }),
      record({
        spanId: 'synth:claude:sess-a:msg-2',
        traceId: 'synth:claude:sess-a',
        source: 'codeburn/claude',
        harness: 'claude',
        name: 'claude:claude-sonnet-4-5',
        timestamp: '2026-08-10T14:00:00.000Z',
        cost: { basis: 'harness', status: 'priced', value: 2.0, currency: 'USD', byModel: { 'claude-sonnet-4-5': 2.0 } },
        tokens: { freshInput: 800, cacheRead: 100, cacheCreation: 0, output: 200, reportedInput: 900, reportedOutput: 200 },
      }),
    ]

    // OTLP-sourced sessions: a harness the file path never saw (pi via
    // telemetry), source name outside the `codeburn/` namespace, distinct
    // model and cost basis — the other half of the unified corpus.
    const otlpRecords: CanonicalRecord[] = [
      record({
        spanId: '01a2b3c4d5e6f001',
        traceId: '0af7651916cd43dd8448eb211c80319c',
        parentSpanId: null,
        source: 'otel/pi-agent-7f3',
        harness: 'pi',
        name: 'pi:gemini-2.5-pro',
        timestamp: '2026-08-11T09:00:00.000Z',
        cost: { basis: 'published', status: 'priced', value: 0.75, currency: 'USD', byModel: { 'gemini-2.5-pro': 0.75 } },
        tokens: { freshInput: 500, cacheRead: 0, cacheCreation: 0, output: 150, reportedInput: 500, reportedOutput: 150 },
      }),
      record({
        spanId: '01a2b3c4d5e6f002',
        traceId: '0af7651916cd43dd8448eb211c80319c',
        parentSpanId: '01a2b3c4d5e6f001',
        source: 'otel/pi-agent-7f3',
        harness: 'pi',
        name: 'pi:gemini-2.5-pro',
        timestamp: '2026-08-11T10:30:00.000Z',
        cost: { basis: 'published', status: 'priced', value: 1.25, currency: 'USD', byModel: { 'gemini-2.5-pro': 1.25 } },
        tokens: { freshInput: 600, cacheRead: 100, cacheCreation: 20, output: 250, reportedInput: 720, reportedOutput: 250 },
      }),
    ]

    store.upsertMany([...fileRecords, ...otlpRecords])
    expect(store.count()).toBe(4)
    expect(store.listAll()).toHaveLength(4)

    const data = getDashboardData(store)

    // Totals: the file half and the OTLP half are both present (R11.1 -- one
    // data path, not two). Every sum must include both sides together.
    const expectedCost = 1.5 + 2.0 + 0.75 + 1.25 // 5.50
    const expectedCalls = 4
    const expectedInput = 1250 + 900 + 500 + 720 // 3370
    const expectedOutput = 300 + 200 + 150 + 250 // 900

    expect(data.recordCount).toBe(4)
    expect(data.totals.calls).toBe(expectedCalls)
    expect(data.totals.cost).toBeCloseTo(expectedCost)
    expect(data.totals.inputTokens).toBe(expectedInput)
    expect(data.totals.outputTokens).toBe(expectedOutput)

    // Period reports (R11.2): daily is the latest calendar day (2026-08-11,
    // the OTLP day), weekly is the 7-day window ending on it and therefore
    // spans both file and OTLP days. Weekly must sum both; daily must sum
    // only the latest day but still come from the store (not from a live
    // parse that would have dropped the OTLP half).
    expect(data.periodReports).toHaveLength(2)
    const daily = data.periodReports.find((r) => r.period === 'daily')!
    const weekly = data.periodReports.find((r) => r.period === 'weekly')!
    expect(daily).toBeDefined()
    expect(weekly).toBeDefined()

    // Daily (2026-08-11) is the OTLP-only day.
    expect(daily.start).toBe('2026-08-11')
    expect(daily.end).toBe('2026-08-11')
    expect(daily.calls).toBe(2)
    expect(daily.cost).toBeCloseTo(0.75 + 1.25)

    // Weekly window covers both source days, so it must equal the grand total.
    expect(weekly.cost).toBeCloseTo(expectedCost)
    expect(weekly.calls).toBe(expectedCalls)
    expect(weekly.tokens.input).toBe(expectedInput)

    // Breakdown tables (R11.2): the provider table and the model table each
    // name both harnesses/models, so neither source was filtered out.
    const providerNames = data.breakdownByProvider.map((b) => b.provider)
    expect(providerNames).toContain('claude')
    expect(providerNames).toContain('pi')
    expect(data.breakdownByProvider).toHaveLength(2)
    const claudeRow = data.breakdownByProvider.find((b) => b.provider === 'claude')!
    const piRow = data.breakdownByProvider.find((b) => b.provider === 'pi')!
    expect(claudeRow.calls).toBe(2)
    expect(claudeRow.cost).toBeCloseTo(1.5 + 2.0)
    expect(piRow.calls).toBe(2)
    expect(piRow.cost).toBeCloseTo(0.75 + 1.25)

    const modelNames = data.breakdownByModel.map((b) => b.model)
    expect(modelNames).toContain('claude-sonnet-4-5')
    expect(modelNames).toContain('gemini-2.5-pro')
    expect(data.breakdownByModel).toHaveLength(2)

    // Daily activity (R11.2): one row per calendar day, newest-first by
    // construction in the terminal's Daily Activity panel, but this shim
    // sorts oldest-first (the view reverses it); both days are present and
    // their costs sum to the grand total.
    expect(data.dailyActivity).toHaveLength(2)
    const day10 = data.dailyActivity.find((d) => d.day === '2026-08-10')!
    const day11 = data.dailyActivity.find((d) => d.day === '2026-08-11')!
    expect(day10).toBeDefined()
    expect(day11).toBeDefined()
    expect(day10.calls).toBe(2)
    expect(day10.cost).toBeCloseTo(1.5 + 2.0)
    expect(day11.calls).toBe(2)
    expect(day11.cost).toBeCloseTo(0.75 + 1.25)
    expect(data.dailyActivity[0]!.day < data.dailyActivity[1]!.day).toBe(true)
    expect(day10.cost + day11.cost).toBeCloseTo(expectedCost)

    // Terminal summary line: the same figures the dashboard renders as its
    // headline appear in the one-liner, so the check survives a render
    // refactor -- the corpus covering both sources is what is pinned.
    const summary = formatDashboardSummary(data)
    expect(summary).toContain(`cost=$${expectedCost.toFixed(2)}`)
    expect(summary).toContain(`calls=${expectedCalls}`)
    expect(summary).toContain(`input=${expectedInput}`)
    expect(summary).toContain(`output=${expectedOutput}`)
    expect(summary).toContain('records=4')

    store.close()
  })

  it('returns empty period reports, breakdowns and daily activity for an empty store', () => {
    const store = new CanonStore(':memory:')
    const data = getDashboardData(store)

    expect(data.recordCount).toBe(0)
    expect(data.totals.cost).toBe(0)
    expect(data.totals.calls).toBe(0)
    expect(data.periodReports).toHaveLength(0)
    expect(data.breakdownByProvider).toHaveLength(0)
    expect(data.breakdownByModel).toHaveLength(0)
    expect(data.dailyActivity).toHaveLength(0)
    expect(formatDashboardSummary(data)).toBe('cost=$0.00 calls=0 input=0 output=0 records=0')

    store.close()
  })

  it('derives every figure from the canonical store rather than a live parse', () => {
    // The store is the only input: two records with identical harness/model
    // but distinct source namespaces must still aggregate together, proving
    // the path does not filter by source.
    const store = new CanonStore(':memory:')
    store.upsertMany([
      record({
        spanId: 'synth:claude:sess-b:1',
        traceId: 'synth:claude:sess-b',
        source: 'codeburn/claude',
        harness: 'claude',
        name: 'claude:claude-sonnet-4-5',
        timestamp: '2026-08-09T08:00:00.000Z',
        cost: { basis: 'harness', status: 'priced', value: 3.0, currency: 'USD', byModel: { 'claude-sonnet-4-5': 3.0 } },
        tokens: { freshInput: 100, cacheRead: 0, cacheCreation: 0, output: 10, reportedInput: 100, reportedOutput: 10 },
      }),
      record({
        spanId: 'otel-span-1',
        traceId: 'trace-otel',
        source: 'otel/collector-1',
        harness: 'claude',
        name: 'claude:claude-sonnet-4-5',
        timestamp: '2026-08-09T09:00:00.000Z',
        cost: { basis: 'published', status: 'priced', value: 4.0, currency: 'USD', byModel: { 'claude-sonnet-4-5': 4.0 } },
        tokens: { freshInput: 200, cacheRead: 0, cacheCreation: 0, output: 20, reportedInput: 200, reportedOutput: 20 },
      }),
    ])

    const data = getDashboardData(store)
    // Both records share harness and model, so the breakdown collapses to one
    // row but that row's totals include both (not just the `codeburn/` half).
    expect(data.breakdownByProvider).toHaveLength(1)
    expect(data.breakdownByProvider[0]!.calls).toBe(2)
    expect(data.breakdownByProvider[0]!.cost).toBeCloseTo(7.0)
    expect(data.breakdownByModel).toHaveLength(1)
    expect(data.breakdownByModel[0]!.calls).toBeCloseTo(2)
    expect(data.totals.cost).toBeCloseTo(7.0)

    store.close()
  })
})
