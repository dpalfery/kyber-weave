// The six dimensions, derived once in the engine (R11.6, R11.14, R14.3).
//
// These cases are ported from `dimensionForRow` in
// `web/src/components/analysis/ScorecardMatrix.tsx`, which derived them in the browser.
// That is the defect being fixed: the CLI report and the dashboard would each have had
// their own copy of this logic, and R11.14 requires the report and the REST API to agree.
// Moving it server-side makes disagreement impossible rather than tested-for.
//
// Two rules are load-bearing and have their own cases below. A dimension a harness cannot
// report is absent with a reason and never `0` (R14.1) — a zero-width bar reads as "we
// measured almost nothing", which is a different claim from "we could not measure". And no
// output may combine dimensions into a score, index or grade (R14.3), because the six
// answer different questions and their average answers none of them.

import { describe, expect, it } from 'vitest'

import type { HarnessRollupRow } from '../canon/types.js'
import { isUnmeasurable } from './report/types.js'
import {
  DIMENSION_KEYS,
  NO_ROLLUP_REASON,
  UNMEASURED_REASONS,
  buildScorecard,
} from './scorecard.js'

function row(overrides: Partial<HarnessRollupRow> = {}): HarnessRollupRow {
  return {
    harness: 'claude-code',
    sampleCount: 12,
    measurability: {},
    ...overrides,
  }
}

describe('buildScorecard', () => {
  it('returns exactly the six dimensions, in the declared order', () => {
    const dims = buildScorecard(row())
    expect(Object.keys(dims)).toEqual([...DIMENSION_KEYS])
    expect(DIMENSION_KEYS).toHaveLength(6)
  })

  it('carries context pressure as a measured fraction with a percent display', () => {
    const dims = buildScorecard(row({ contextPressureMedian: 0.7234 }))
    expect(dims.contextHygiene).toMatchObject({ value: 0.7234, display: '72%' })
  })

  it('carries cache hit rate as a measured fraction with a percent display', () => {
    const dims = buildScorecard(row({ cacheHitRate: 0.5 }))
    expect(dims.cacheEfficiency).toMatchObject({ value: 0.5, display: '50%' })
  })

  it('carries tool yield as a multiple, not a percentage', () => {
    const dims = buildScorecard(row({ toolYield: 3.25 }))
    expect(dims.toolYield).toMatchObject({ value: 3.25, display: '3.3x' })
  })

  it('carries delegation overhead as a measured fraction with a percent display', () => {
    const dims = buildScorecard(row({ delegationOverhead: 0.12 }))
    expect(dims.delegationOverhead).toMatchObject({ value: 0.12, display: '12%' })
  })

  it('reports skill utilisation and continuity as unmeasurable: nothing derives them yet', () => {
    const dims = buildScorecard(row({ contextPressureMedian: 0.5 }))
    expect(isUnmeasurable(dims.skillUtilisation)).toBe(true)
    expect(isUnmeasurable(dims.continuity)).toBe(true)
  })

  it('keeps a measured zero as a measurement rather than an absence', () => {
    const dims = buildScorecard(row({ cacheHitRate: 0, toolYield: 0, delegationOverhead: 0 }))
    for (const key of ['cacheEfficiency', 'toolYield', 'delegationOverhead'] as const) {
      expect(isUnmeasurable(dims[key])).toBe(false)
      expect(dims[key]).toMatchObject({ value: 0 })
    }
  })

  it('gives an absent metric the dimension reason, not a zero', () => {
    const dims = buildScorecard(row({ contextPressureMedian: null }))
    expect(dims.contextHygiene).toEqual({
      value: null,
      reason: UNMEASURED_REASONS.contextHygiene,
    })
  })

  it('treats an undefined metric the same as an explicit null', () => {
    expect(buildScorecard(row({ cacheHitRate: undefined })).cacheEfficiency).toEqual(
      buildScorecard(row({ cacheHitRate: null })).cacheEfficiency,
    )
  })

  it('distinguishes a harness with no rollup at all from one whose metric is missing', () => {
    // Observed on runs but never aggregated: every dimension says so, because the reason
    // a reader needs is "no rollup yet", not "this harness cannot report cache counters".
    const noRollup = buildScorecard(row({ sampleCount: 0 }))
    for (const key of DIMENSION_KEYS) {
      expect(noRollup[key]).toEqual({ value: null, reason: NO_ROLLUP_REASON })
    }
  })

  it('never emits 0 for any dimension of a harness with no data', () => {
    const dims = buildScorecard(row({ sampleCount: 0 }))
    for (const key of DIMENSION_KEYS) {
      expect(dims[key].value).toBeNull()
      expect(dims[key].value).not.toBe(0)
    }
  })

  it('emits no key that combines dimensions into a score, index or grade (R14.3)', () => {
    const dims = buildScorecard(row({
      contextPressureMedian: 0.4,
      cacheHitRate: 0.9,
      toolYield: 2,
      delegationOverhead: 0.1,
    }))
    const keys = Object.keys(dims)
    expect(keys).toEqual([...DIMENSION_KEYS])
    for (const forbidden of ['score', 'overall', 'total', 'index', 'grade', 'rating', 'composite']) {
      expect(keys.some((k) => k.toLowerCase().includes(forbidden))).toBe(false)
    }
    // And nothing nested inside a dimension carries one either.
    const serialised = JSON.stringify(dims).toLowerCase()
    for (const forbidden of ['"score"', '"overall"', '"grade"', '"composite"']) {
      expect(serialised).not.toContain(forbidden)
    }
  })

  it('is a pure function of the row', () => {
    const input = row({ toolYield: 1.5 })
    const before = JSON.stringify(input)
    buildScorecard(input)
    expect(JSON.stringify(input)).toBe(before)
    expect(buildScorecard(input)).toEqual(buildScorecard(input))
  })
})
