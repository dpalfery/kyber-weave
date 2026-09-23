// The six harness dimensions, derived server-side.
//
// This is `dimensionForRow` from `web/src/components/analysis/ScorecardMatrix.tsx`, moved
// into the engine. The dashboard used to derive these in the browser while the report
// would have derived them again in the CLI; R11.14 requires the report and the REST API to
// agree on dimensions, and one derivation is how that is guaranteed rather than asserted.
// The dashboard now renders the served value (task 5.2).
//
// The dimensions stay six separate values. Nothing here averages, weights or ranks them
// into a composite (R14.3): Cache Efficiency and Tool Yield answer different questions
// about different subsystems, and a single number formed from both answers neither while
// looking authoritative.

import type { HarnessRollupRow } from '../canon/types.js'
import { measured, unmeasurable, type DimensionKey, type Measured } from './report/types.js'

/** The declared order dimensions are reported in, on every surface. */
export const DIMENSION_KEYS = [
  'contextHygiene',
  'cacheEfficiency',
  'toolYield',
  'skillUtilisation',
  'delegationOverhead',
  'continuity',
] as const satisfies readonly DimensionKey[]

/** Display names, carried so each surface does not invent its own wording. */
export const DIMENSION_NAMES: Record<DimensionKey, string> = {
  contextHygiene: 'Context Hygiene',
  cacheEfficiency: 'Cache Efficiency',
  toolYield: 'Tool Yield',
  skillUtilisation: 'Skill Utilisation',
  delegationOverhead: 'Delegation Overhead',
  continuity: 'Continuity',
}

/**
 * Why a dimension is absent when the harness *has* a rollup but not this metric. Naming
 * the missing telemetry is what makes the gap actionable rather than mysterious.
 */
export const UNMEASURED_REASONS: Record<DimensionKey, string> = {
  contextHygiene: 'Telemetry missing: context composition not recorded',
  cacheEfficiency: 'Telemetry missing: harness does not export cache read/creation counters',
  toolYield: 'Telemetry missing: tool call residency and schema tokens unobserved',
  skillUtilisation: 'Telemetry missing: skill activation unobserved on this harness',
  delegationOverhead: 'Telemetry missing: no delegation events recorded or single-agent run',
  continuity: 'Telemetry missing: turn outcome or correction telemetry unobserved',
}

/**
 * Why every dimension is absent when the harness was seen on runs but never aggregated.
 * Distinct from the per-dimension reasons above: the reader's next action is to refresh,
 * not to conclude the harness cannot report cache counters.
 */
export const NO_ROLLUP_REASON =
  'No live rollup: harness observed on runs but not yet aggregated'

/** One dimension's value: a figure with its display string, or its absence with a reason. */
export type ScorecardDimension = Measured<number> & { display?: string }

/** The six dimensions of one harness, keyed in `DIMENSION_KEYS` order. */
export type Scorecard = Record<DimensionKey, ScorecardDimension>

const asPercent = (value: number): string => `${Math.round(value * 100)}%`
const asMultiple = (value: number): string => `${value.toFixed(1)}x`

/**
 * A rollup row exists when the harness has been aggregated at least once. `sampleCount` of
 * 0 means it was observed on runs but has no aggregate yet, which is a different answer
 * from "aggregated, and this metric was not in the telemetry".
 */
function hasRollup(row: HarnessRollupRow): boolean {
  return row.sampleCount > 0
}

function dimension(
  key: DimensionKey,
  value: number | null | undefined,
  format: (value: number) => string,
  fallbackReason: string,
): ScorecardDimension {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return unmeasurable<number>(fallbackReason)
  }
  return { ...measured(value), display: format(value) }
}

/**
 * Derive one harness's six dimensions from its rollup row.
 *
 * Pure: the same row always yields the same dimensions, and the row is not mutated. That
 * matters because both the report builder and the harness endpoints call it, and a shared
 * derivation that depended on call order would reintroduce exactly the divergence moving
 * it here was meant to remove.
 */
export function buildScorecard(row: HarnessRollupRow): Scorecard {
  // Without an aggregate, no dimension has anything behind it — say that once, for all six,
  // rather than six times in the vocabulary of missing telemetry.
  if (!hasRollup(row)) {
    return Object.fromEntries(
      DIMENSION_KEYS.map((key) => [key, unmeasurable<number>(NO_ROLLUP_REASON)]),
    ) as Scorecard
  }

  return {
    contextHygiene: dimension(
      'contextHygiene', row.contextPressureMedian, asPercent, UNMEASURED_REASONS.contextHygiene,
    ),
    cacheEfficiency: dimension(
      'cacheEfficiency', row.cacheHitRate, asPercent, UNMEASURED_REASONS.cacheEfficiency,
    ),
    toolYield: dimension(
      'toolYield', row.toolYield, asMultiple, UNMEASURED_REASONS.toolYield,
    ),
    // Nothing derives these two yet. They are reported as absent with the reason rather
    // than omitted, so a surface renders the gap instead of silently showing four of six.
    skillUtilisation: unmeasurable<number>(UNMEASURED_REASONS.skillUtilisation),
    delegationOverhead: dimension(
      'delegationOverhead', row.delegationOverhead, asPercent, UNMEASURED_REASONS.delegationOverhead,
    ),
    continuity: unmeasurable<number>(UNMEASURED_REASONS.continuity),
  }
}
