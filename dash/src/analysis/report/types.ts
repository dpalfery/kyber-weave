// The `ContextReport` contract: one versioned document every surface reads.
//
// The CLI report, the `/api/kyber/report` endpoint and the tray popover all render this
// one model (Decision D2, R7.5). That is the point: a second derivation of the same
// figures would need a parity test forever, and the two would still drift between the
// runs of it. The tray in particular holds no analysis logic — if it needs a figure, the
// figure is added here and computed by the engine (R7.5).
//
// `schemaVersion` is on the document rather than negotiated per field, so a consumer that
// predates a change fails loudly on the version instead of silently reading a shape it
// does not understand.

/**
 * A figure together with whether it could be measured at all (R11.10, R14.1).
 *
 * The absent case carries `value: null` and a reason rather than being omitted, so a
 * consumer cannot mistake "we did not look" for "the key is missing" for "zero". JSON
 * consumers get `null` and a reason; text and Markdown get `—` and the reason through
 * `formatMeasured`. Neither ever gets `0` for an absent figure.
 *
 * This is the report's carried value, and is deliberately not `canon/types.ts`'s
 * `MetricAvailability`: that one declares what a *source* can report, per metric, before
 * any figure exists. This one pairs one figure with its own absence. Collapsing them
 * would make an unmeasurable bucket indistinguishable from a source-level gap.
 */
export type Measured<T> = { value: T; unit?: string } | { value: null; reason: string }

/** The mark an unmeasurable figure renders as. Never `0`, never blank (R14.1). */
export const NOT_MEASURABLE = '—'

/** A figure that was measured. `0` is a legitimate value here and renders as `0`. */
export function measured<T>(value: T, unit?: string): Measured<T> {
  return unit === undefined ? { value } : { value, unit }
}

/** A figure the source cannot report, with the reason a reader needs to act on it. */
export function unmeasurable<T = never>(reason: string): Measured<T> {
  return { value: null, reason }
}

/** True when the figure is absent, narrowing to the branch that carries `reason`. */
export function isUnmeasurable<T>(m: Measured<T>): m is { value: null; reason: string } {
  return m.value === null
}

/**
 * Render a figure for a human-readable surface (R14.1).
 *
 * `format` controls only the measured branch, so a renderer can choose precision or a
 * percent sign without reimplementing the absent branch — which is the branch a mistake
 * would turn into a false `0`. Keeping that branch in one place is the whole reason this
 * helper exists rather than each renderer formatting inline.
 */
export function formatMeasured<T>(m: Measured<T>, format: (value: T) => string = String): string {
  if (isUnmeasurable(m)) return `${NOT_MEASURABLE} (${m.reason})`
  return m.unit === undefined ? format(m.value) : `${format(m.value)} ${m.unit}`
}

/**
 * What the report was asked about. `days` is the findings and session window in whole
 * days (R11.11, default 7); the three id filters narrow it further and are absent for
 * "everything in the window".
 */
export type ReportScope = {
  /** Canonical harness id; absent means every harness. */
  harness?: string
  sessionId?: string
  runId?: string
  /** Positive integer, default 7. */
  days: number
}

/**
 * The sections a caller can ask for. `detection` walks the filesystem to discover
 * harnesses, so it is excluded by default: the CLI includes it, the tray does not
 * (design C1).
 */
export type ReportSection =
  | 'coverage'
  | 'detection'
  | 'findings'
  | 'harnesses'
  | 'latestSession'
  | 'cost'

/** The six harness dimensions, reported separately and never combined (R11.6, R14.3). */
export type DimensionKey =
  | 'contextHygiene'
  | 'cacheEfficiency'
  | 'toolYield'
  | 'skillUtilisation'
  | 'delegationOverhead'
  | 'continuity'

/** The five canonical context buckets, plus a residual reported alongside them (R8.2). */
export type BucketKey =
  | 'system_prompt'
  | 'tool_definitions'
  | 'instruction_context'
  | 'conversation_history'
  | 'tool_result_content'

/** How confident a finding's measurement is, as the Finding Contract records it. */
export type MeasurementClass = 'deterministic' | 'inferred' | 'coverage-gap'

export type ReportCoverage = {
  storePath: string
  refresh: {
    lastSuccessAt: string | null
    lastFailure: { at: string; summary: string } | null
    inProgress: { pid: number; since: string } | null
  }
  harnesses: Array<{
    harness: string
    name: string
    sessionsInWindow: number
    measurability: Record<string, 'measurable' | { reason: string }>
  }>
  quarantineCount: number | null
  problemCount: number | null
  /** Commands that remedy what the section reports, e.g. `kyberdash dash refresh` (R11.4). */
  hints: string[]
}

export type ReportDetection = Array<{
  harness: string
  detected: boolean
  probedPaths: string[]
}>

export type ReportFinding = {
  id: string
  title: string
  measurementClass: MeasurementClass
  confidence: string
  confidenceBasis: string | null
  mechanism: string
  /** At least two by the Finding Contract. */
  evidenceIds: string[]
  /** The engine's text, carried verbatim — never rephrased (R14.4). */
  recommendation: string
  recoverableTokens: Measured<number>
  errorBar: { low: number; high: number } | null
  outcomeRiskCaveat: string
  rankScore: number
  sessionId: string | null
  runId: string | null
  harness: string | null
  /** The view path that opens this finding, `finding/<id>`. */
  view: string
}

export type ReportHarness = {
  harness: string
  name: string
  dimensions: Record<DimensionKey, Measured<number> & { display?: string }>
}

export type ReportLatestSession = {
  sessionId: string
  harness: string
  project: string | null
  lastActivityAt: string
  turnCount: number
  view: string
  latestTurn: {
    index: number
    /** Share of the context window in use, 0..1. */
    pressure: Measured<number>
    contextWindow: Measured<number>
    buckets: Record<BucketKey, Measured<number>>
    residual: Measured<number>
    cacheInvalidation: boolean
  }
  toolDefinitionSources: Array<{ source: string; tokens: Measured<number> }>
  cacheInvalidationTurns: number[]
}

/**
 * Cost per basis, never blended across bases, and always emitted last so no surface can
 * lead with it or sort by it (R8.9, R14.2).
 */
export type ReportCost = Array<{ basis: string; amountUsd: Measured<number> }>

export type ContextReport = {
  schemaVersion: 1
  /** ISO-8601 UTC. */
  generatedAt: string
  kyberdashVersion: string
  scope: ReportScope
  coverage?: ReportCoverage
  detection?: ReportDetection
  findings?: ReportFinding[]
  harnesses?: ReportHarness[]
  latestSession?: ReportLatestSession | null
  cost?: ReportCost
}

/** The version every document this engine builds carries. */
export const REPORT_SCHEMA_VERSION = 1 as const

/** Every section except `detection`, which walks the filesystem (design C1). */
export const DEFAULT_SECTIONS: readonly ReportSection[] = [
  'coverage',
  'findings',
  'harnesses',
  'latestSession',
  'cost',
]

/** Findings shown by default, in the report and over REST (R11.5). */
export const DEFAULT_FINDING_LIMIT = 5

/** The findings and session window, in days (R11.11). */
export const DEFAULT_WINDOW_DAYS = 7
