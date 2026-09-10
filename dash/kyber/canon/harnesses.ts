// Aggregate performance and measurability rollups per harness (Task E2, ADR 0009, ADR 0011).
//
// The core discipline of KyberDash (D3, ADR 0008, ADR 0011):
// Absent is never zero, and the reason matters. Where telemetry is absent (e.g. cache counters
// on Cursor or Aider, or tool schemas on unconfigured harnesses), the rollup emits an explicit
// `not_measurable` with reason, NEVER fabrication or zero. Averaging a measured value with a
// derived one marks the result derived. A harness with zero collectable sessions appears in
// the catalogued list with a stated reason and no computed dimensions.

import {
  harnessDimensionAvailability,
  SURVEYED_HARNESSES,
} from './measurability.js'
import { CanonStore } from './store.js'
import {
  isNotMeasurable,
  notMeasurable,
  type HarnessRollupRow,
  type MetricAvailability,
} from './types.js'
import type { AsadSessionPayload } from './sessions.js'

export type { HarnessRollupRow }

/**
 * Core dimensions evaluated for harness field coverage.
 */
export const COVERAGE_DIMENSIONS = [
  'token_usage',
  'cache_hit_rate',
  'prefix_stability',
  'tool_yield',
  'delegation_overhead',
  'context_pressure',
] as const

export type CoverageDimension = (typeof COVERAGE_DIMENSIONS)[number]

/**
 * Compute the empirical field-coverage ratio (0.0 to 1.0) for an agent harness.
 *
 * Grounded in the Task E4 inventory and repository ADRs: harnesses that do not export
 * essential telemetry (such as cache counters on Cursor/Aider, prefix reconstruction,
 * or tool definitions) have their coverage degraded proportionately, preventing
 * uninstrumented harnesses from appearing efficient by silence.
 */
export function coverageFor(harness: string): number {
  let covered = 0
  for (const dim of COVERAGE_DIMENSIONS) {
    const avail = harnessDimensionAvailability(harness, dim)
    if (!isNotMeasurable(avail)) {
      covered += 1
    }
  }
  return Number((covered / COVERAGE_DIMENSIONS.length).toFixed(4))
}

/**
 * Calculate the median of a sorted numerical array.
 */
export function computeMedian(sorted: readonly number[]): number {
  const n = sorted.length
  if (n === 0) return 0
  const mid = Math.floor(n / 2)
  return n % 2 === 1 ? sorted[mid]! : (sorted[mid - 1]! + sorted[mid]!) / 2
}

/**
 * Calculate the 95th percentile (nearest-rank) of a sorted numerical array.
 */
export function computeP95(sorted: readonly number[]): number {
  const n = sorted.length
  if (n === 0) return 0
  const index = Math.min(Math.max(Math.ceil(0.95 * n) - 1, 0), n - 1)
  return sorted[index]!
}

/**
 * Everything the rollup dimensions need from a harness's sessions, reduced to
 * scalars in one streaming pass.
 *
 * The dimensions below each used to walk a materialized array of every session
 * payload for the harness. The reductions are unchanged — this only decides
 * when a payload is live, not what is computed from it.
 */
type SessionDigest = {
  count: number
  /** Peak context pressure per session, for the median and p95. */
  peakPressures: number[]
  /** Whether any session's pressure came from derived rather than measured counts. */
  anyDerived: boolean
  totalCacheRead: number
  totalInput: number
  totalDefinedTools: number
  totalInvokedTools: number
  subagentSessions: number
}

function digestSessions(store: CanonStore, harness: string): SessionDigest {
  const digest: SessionDigest = {
    count: 0,
    peakPressures: [],
    anyDerived: false,
    totalCacheRead: 0,
    totalInput: 0,
    totalDefinedTools: 0,
    totalInvokedTools: 0,
    subagentSessions: 0,
  }

  for (const row of store.iterateSessions(harness)) {
    const payload = (row.payload !== null && typeof row.payload === 'object'
      ? row.payload
      : {}) as AsadSessionPayload
    digest.count += 1

    // Context pressure: the peak of a session's per-turn pressures.
    const context = payload.context
    if (context && context.measurable === true && Array.isArray(context.turns) && context.turns.length > 0) {
      const pressures = context.turns
        .map((t: unknown) => (t as { pressure?: number })?.pressure)
        .filter((pressure: unknown): pressure is number => typeof pressure === 'number' && Number.isFinite(pressure))
      if (pressures.length > 0) digest.peakPressures.push(Math.max(...pressures))
      if (context.derivedCounts) digest.anyDerived = true
    } else if (Array.isArray(payload.turns) && payload.turns.length > 0) {
      const limit = Number(payload.context?.contextLimit ?? 200_000)
      const pressures = payload.turns
        .map((t: unknown) => {
          const input = (t as { input?: number })?.input
          return typeof input === 'number' ? input / limit : undefined
        })
        .filter((pressure: unknown): pressure is number => typeof pressure === 'number' && Number.isFinite(pressure))
      if (pressures.length > 0) {
        digest.peakPressures.push(Math.max(...pressures))
        digest.anyDerived = true
      }
    }

    // Cache hit rate: measured totals only; a not_measurable total is an
    // object, not a number, and must not be coerced into the denominator.
    const summary = payload.summary
    if (summary) {
      if (typeof summary.total_cache_read === 'number') digest.totalCacheRead += summary.total_cache_read
      if (typeof summary.total_input === 'number') digest.totalInput += summary.total_input
    }

    // Tool yield: per-tool rows where the session has them, per-server counts
    // where it only has the server bands.
    if (Array.isArray(payload.tools) && payload.tools.length > 0) {
      digest.totalDefinedTools += payload.tools.length
      digest.totalInvokedTools += payload.tools.filter(
        (t: { invocations?: number }) => (t.invocations ?? 0) > 0,
      ).length
    } else if (Array.isArray(payload.servers) && payload.servers.length > 0) {
      for (const server of payload.servers) {
        digest.totalDefinedTools += server.tools ?? 0
        digest.totalInvokedTools += Math.max(0, (server.tools ?? 0) - (server.unused_tools ?? 0))
      }
    }

    // Delegation overhead falls back to this when no executions were recorded.
    if (payload.isSubagent || (Array.isArray(payload.subagents) && payload.subagents.length > 0)) {
      digest.subagentSessions += 1
    }
  }

  return digest
}

/**
 * Build a single harness rollup row over sessions and executions in the store.
 */
export function buildRollupForHarness(store: CanonStore, harness: string): HarnessRollupRow {
  // Reduce each session to the scalars the dimensions below need, one payload
  // at a time. Every use of a session here is a sum, a count or one number per
  // session, so nothing is gained by holding 995 MB of payloads in an array —
  // and doing so is what took this phase's peak to 4.4 GB.
  const digest = digestSessions(store, harness)
  const sessions = { length: digest.count }

  // Query all executions and runs for this harness
  const executions = store.listExecutionsByHarness(harness)
  const runs = store.listRuns(harness)

  const fieldCoverage = coverageFor(harness)
  const measurability: Record<string, MetricAvailability> = {}

  // Case 1: Zero recorded runs/sessions.
  // Rule: appears in the list with a stated reason and no computed dimensions.
  if (sessions.length === 0 && executions.length === 0 && runs.length === 0) {
    for (const dim of [
      'context_pressure',
      'context_pressure_median',
      'context_pressure_p95',
      'cache_hit_rate',
      'tool_yield',
      'delegation_overhead',
    ]) {
      const baseAvail = harnessDimensionAvailability(harness, dim)
      if (isNotMeasurable(baseAvail)) {
        measurability[dim] = baseAvail
      } else {
        measurability[dim] = notMeasurable(
          `No collectable runs or sessions recorded for harness "${harness}".`,
        )
      }
    }
    measurability['field_coverage'] = 'measured'

    return {
      harness,
      sampleCount: 0,
      contextPressureMedian: null,
      contextPressureP95: null,
      cacheHitRate: null,
      toolYield: null,
      delegationOverhead: null,
      fieldCoverage,
      measurability,
      payload: {
        sessionCount: 0,
        runCount: 0,
        executionCount: 0,
        reason: `No collectable runs or sessions recorded for harness "${harness}".`,
      },
    }
  }

  const sampleCount = Math.max(sessions.length, runs.length, executions.length)

  // 1. Context Pressure (Median & P95)
  let contextPressureMedian: number | null = null
  let contextPressureP95: number | null = null
  const pressureAvail = harnessDimensionAvailability(harness, 'context_pressure')

  if (isNotMeasurable(pressureAvail)) {
    contextPressureMedian = null
    contextPressureP95 = null
    measurability['context_pressure'] = pressureAvail
    measurability['context_pressure_median'] = pressureAvail
    measurability['context_pressure_p95'] = pressureAvail
  } else {
    const peakPressures = digest.peakPressures
    const anyDerived = digest.anyDerived

    if (peakPressures.length > 0) {
      peakPressures.sort((a, b) => a - b)
      contextPressureMedian = Number(computeMedian(peakPressures).toFixed(4))
      contextPressureP95 = Number(computeP95(peakPressures).toFixed(4))
      const classTag: MetricAvailability = anyDerived ? 'derived' : 'measured'
      measurability['context_pressure'] = classTag
      measurability['context_pressure_median'] = classTag
      measurability['context_pressure_p95'] = classTag
    } else {
      const notMeas = notMeasurable(
        `No measurable turns found in recorded sessions for harness "${harness}".`,
      )
      measurability['context_pressure'] = notMeas
      measurability['context_pressure_median'] = notMeas
      measurability['context_pressure_p95'] = notMeas
    }
  }

  // 2. Cache Hit Rate
  let cacheHitRate: number | null = null
  const cacheAvail = harnessDimensionAvailability(harness, 'cache_hit_rate')

  if (isNotMeasurable(cacheAvail)) {
    cacheHitRate = null
    measurability['cache_hit_rate'] = cacheAvail
  } else {
    const totalCacheRead = digest.totalCacheRead
    const totalInput = digest.totalInput

    if (totalInput > 0) {
      cacheHitRate = Number((totalCacheRead / totalInput).toFixed(4))
      measurability['cache_hit_rate'] = 'measured'
    } else {
      measurability['cache_hit_rate'] = notMeasurable(
        `No input tokens recorded in sessions for harness "${harness}".`,
      )
    }
  }

  // 3. Tool Yield
  let toolYield: number | null = null
  const toolAvail = harnessDimensionAvailability(harness, 'tool_yield')

  if (isNotMeasurable(toolAvail)) {
    toolYield = null
    measurability['tool_yield'] = toolAvail
  } else {
    const totalDefinedTools = digest.totalDefinedTools
    const totalInvokedTools = digest.totalInvokedTools

    if (totalDefinedTools > 0) {
      toolYield = Number((totalInvokedTools / totalDefinedTools).toFixed(4))
      measurability['tool_yield'] = 'measured'
    } else {
      measurability['tool_yield'] = notMeasurable(
        `No tool definitions recorded in sessions for harness "${harness}".`,
      )
    }
  }

  // 4. Delegation Overhead
  let delegationOverhead: number | null = null
  const delAvail = harnessDimensionAvailability(harness, 'delegation_overhead')

  if (isNotMeasurable(delAvail)) {
    delegationOverhead = null
    measurability['delegation_overhead'] = delAvail
  } else {
    if (executions.length === 0) {
      const subagentSessions = digest.subagentSessions
      if (sessions.length > 0) {
        delegationOverhead = Number((subagentSessions / sessions.length).toFixed(4))
        measurability['delegation_overhead'] = 'measured'
      } else {
        measurability['delegation_overhead'] = notMeasurable(
          `No agent executions recorded for harness "${harness}".`,
        )
      }
    } else {
      let childTokens = 0
      let rootTokens = 0
      let childCount = 0

      for (const exec of executions) {
        const isChild =
          !exec.isRoot ||
          (exec.parentExecutionId !== null && exec.parentExecutionId !== undefined)
        let tokens = 0
        if (exec.sessionId) {
          // Two numbers read out of the stored JSON, rather than parsing a
          // payload that can run to hundreds of megabytes for each execution.
          const totals = store.sessionTokenTotals(exec.sessionId)
          if (totals !== undefined) tokens = totals.input + totals.output
        }

        if (isChild) {
          childCount += 1
          childTokens += tokens
        } else {
          rootTokens += tokens
        }
      }

      if (childTokens + rootTokens > 0) {
        delegationOverhead = Number((childTokens / (childTokens + rootTokens)).toFixed(4))
        measurability['delegation_overhead'] = 'measured'
      } else if (executions.length > 0) {
        delegationOverhead = Number((childCount / executions.length).toFixed(4))
        measurability['delegation_overhead'] = 'measured'
      } else {
        measurability['delegation_overhead'] = notMeasurable(
          `No agent executions recorded for harness "${harness}".`,
        )
      }
    }
  }

  // 5. Field Coverage
  measurability['field_coverage'] = 'measured'

  return {
    harness,
    sampleCount,
    contextPressureMedian,
    contextPressureP95,
    cacheHitRate,
    toolYield,
    delegationOverhead,
    fieldCoverage,
    measurability,
    payload: {
      sessionCount: sessions.length,
      runCount: runs.length,
      executionCount: executions.length,
      dimensions: {
        contextPressureMedian: {
          value: contextPressureMedian,
          availability: measurability['context_pressure_median'],
        },
        contextPressureP95: {
          value: contextPressureP95,
          availability: measurability['context_pressure_p95'],
        },
        cacheHitRate: {
          value: cacheHitRate,
          availability: measurability['cache_hit_rate'],
        },
        toolYield: {
          value: toolYield,
          availability: measurability['tool_yield'],
        },
        delegationOverhead: {
          value: delegationOverhead,
          availability: measurability['delegation_overhead'],
        },
        fieldCoverage: {
          value: fieldCoverage,
          availability: measurability['field_coverage'],
        },
      },
    },
  }
}

/**
 * Compute aggregate harness rollups and store them in the canonical store (Task E2).
 *
 * Overload 1: Build and persist rollup for a single specified harness.
 * Overload 2: Build and persist rollups for all surveyed and observed harnesses in the store.
 */
export function buildHarnessRollup(store: CanonStore, harnessId: string): HarnessRollupRow
export function buildHarnessRollup(store: CanonStore): HarnessRollupRow[]
export function buildHarnessRollup(
  store: CanonStore,
  harnessId?: string,
): HarnessRollupRow | HarnessRollupRow[] {
  if (harnessId !== undefined) {
    const row = buildRollupForHarness(store, harnessId)
    store.upsertHarnessRollup(row)
    return row
  }

  const allHarnesses = Array.from(
    new Set([...SURVEYED_HARNESSES, ...store.listHarnesses()]),
  ).sort()

  const results: HarnessRollupRow[] = []
  const built = new Set<string>()
  for (const h of allHarnesses) {
    const row = buildRollupForHarness(store, h)
    store.upsertHarnessRollup(row)
    built.add(h)
    results.push(row)
  }

  // A rebuild is authoritative, as it is for `run`, `session` and `finding`.
  // Without this a harness that stops being produced — a front-end name that
  // now normalizes onto its canonical harness, say — keeps its last rollup and
  // shows up beside the harness it was merged into.
  for (const existing of store.listHarnessRollups()) {
    if (built.has(existing.harness)) continue
    store.deleteHarnessRollup(existing.harness)
  }

  return results
}
