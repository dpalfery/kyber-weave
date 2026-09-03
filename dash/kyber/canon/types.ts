// Canonical record model for KyberDash (spec: docs/specs/kyberdash, design.md
// "Data Models"). Every record entering the store — synthesized from a session
// file or decoded from an OTLP payload — normalizes into this shape. Token
// classes are stored disjointly so the reported-input identity is checkable
// (R4.1), and `validateTokens` runs on every record, orphans included (R4.3).

/** Where a cost figure came from (R5.1: bases are never blended silently). */
export type CostBasis = 'published' | 'harness' | 'unknown'

/**
 * Why a cost block carries (or lacks) a value. `no_rate` and `not_billed` are
 * distinct answers, and neither may be rendered as $0.00 (R5.4, R5.5).
 */
export type CostStatus = 'priced' | 'partial' | 'no_rate' | 'out_of_scope' | 'not_billed'

/** A cost figure together with the basis it was derived on. */
export type CostBlock = {
  basis: CostBasis
  status: CostStatus
  /** The figure; absent when status is not `priced`. */
  value?: number
  /** ISO currency code for `value`, e.g. `USD`. */
  currency?: string
  /** Per-model breakdown of `value`. */
  byModel?: Record<string, number>
}

/**
 * Disjoint token classes plus output, exactly as the normalized record claims
 * them. `freshInput + cacheRead + cacheCreation` is the true input (R4.1);
 * `reasoning` is a subset of `output`, never an addition to it.
 */
export type TokenUsage = {
  /** Input neither read from nor written to cache. */
  freshInput: number
  /** Input served from cache. */
  cacheRead: number
  /** Input written to cache. */
  cacheCreation: number
  /** Generated tokens. */
  output: number
  /** Tokens spent on reasoning; a subset of `output`. */
  reasoning?: number
  /** Total input the harness claimed, after its convention was converted (R4.2). */
  reportedInput: number
  /** Total output the harness claimed. */
  reportedOutput: number
}

/**
 * Availability of a metric for a source, independent of its value (R10.1,
 * R10.2): absent is not zero, and the reason matters. `derived` marks counts
 * produced by tokenizing content rather than read from a counter (R4.6).
 */
export type MetricAvailability = 'measured' | 'derived' | 'not_measurable'

/** Per-metric availability declared by a source or synthesizer. */
export type Measurability = Record<string, MetricAvailability>

/** Severity of a recorded problem. Validation failures are `error` (R4.4). */
export type ProblemSeverity = 'error' | 'warning'

/** A surfaced failure the system declines to guess about (design.md, "Error Handling"). */
export type Problem = {
  severity: ProblemSeverity
  code: string
  message: string
  /** Identifier of the record the problem belongs to, when one exists. */
  location?: string
}

/**
 * Canonical content keys — the analysis layer's context buckets (R7.1).
 * Downstream code addresses content only through these keys; nothing may name
 * a harness attribute.
 */
export const CANONICAL_CONTENT_KEYS = [
  'system_prompt',
  'tool_definitions',
  'instruction_context',
  'conversation_history',
  'tool_result_content',
] as const

export type CanonicalContentKey = (typeof CANONICAL_CONTENT_KEYS)[number]

/** Content addressed only through canonical keys; any key may be absent. */
export type CanonicalContent = Partial<Record<CanonicalContentKey, string>>

/** The central entity: one normalized span or synthesized session turn. */
export type CanonicalRecord = {
  /** Primary key; makes re-ingest idempotent (R2.5). */
  spanId: string
  /** Null when the record is an orphan with no resolvable parent (R4.3). */
  traceId: string | null
  parentSpanId: string | null
  /** Telemetry source name; never used as harness evidence (R6.2). */
  source: string
  /** Voted harness attribution (R6.2). */
  harness: string
  name: string
  /** Canonical operation, not the harness's verb. */
  op: string
  kind: string
  timestamp: Date | string
  durationMs: number
  status: string
  tokens: TokenUsage
  content: CanonicalContent
  cost: CostBlock
  /** What this source cannot supply, per metric (R7.6, R8.5, R10.1). */
  measurability?: Measurability
  /** Original payload, compressed in the store (R12.4). */
  raw?: unknown
}

/** Problem codes emitted by `validateTokens`. */
export const TOKEN_NEGATIVE_FRESH = 'TOKEN_NEGATIVE_FRESH'
export const TOKEN_NEGATIVE_CLASS = 'TOKEN_NEGATIVE_CLASS'
export const TOKEN_SUM_MISMATCH = 'TOKEN_SUM_MISMATCH'
export const TOKEN_REASONING_EXCEEDS_OUTPUT = 'TOKEN_REASONING_EXCEEDS_OUTPUT'

export type TokenValidation =
  | { valid: true; problem?: undefined }
  | { valid: false; problem: Problem }

/**
 * Validate a record's token decomposition (R4.1, R4.3, R4.4). The classes must
 * be non-negative, `reasoning` must stay a subset of `output`, and
 * `freshInput + cacheRead + cacheCreation` must equal `reportedInput` exactly.
 * Storing the classes disjointly is what makes the identity checkable at all —
 * a single "input" number could not detect the pi/Copilot inversion of R4.2,
 * which surfaces here as negative fresh input or as a sum that double-counts.
 *
 * Validation is a property of the record, not of its position in a trace: it
 * applies identically to orphans with no resolvable parent. The caller rejects
 * the record and writes the returned problem rather than storing it.
 */
export function validateTokens(tokens: TokenUsage, location?: string): TokenValidation {
  if (tokens.freshInput < 0) {
    return {
      valid: false,
      problem: {
        severity: 'error',
        code: TOKEN_NEGATIVE_FRESH,
        message: `fresh input is negative (${tokens.freshInput}); the harness's input convention was likely inverted on the way in`,
        location,
      },
    }
  }

  if (tokens.cacheRead < 0 || tokens.cacheCreation < 0 || tokens.output < 0) {
    return {
      valid: false,
      problem: {
        severity: 'error',
        code: TOKEN_NEGATIVE_CLASS,
        message: `token class is negative (cacheRead=${tokens.cacheRead}, cacheCreation=${tokens.cacheCreation}, output=${tokens.output})`,
        location,
      },
    }
  }

  if (tokens.reasoning !== undefined && (tokens.reasoning < 0 || tokens.reasoning > tokens.output)) {
    return {
      valid: false,
      problem: {
        severity: 'error',
        code: TOKEN_REASONING_EXCEEDS_OUTPUT,
        message: `reasoning (${tokens.reasoning}) is not a subset of output (${tokens.output})`,
        location,
      },
    }
  }

  const inputSum = tokens.freshInput + tokens.cacheRead + tokens.cacheCreation
  if (inputSum !== tokens.reportedInput) {
    return {
      valid: false,
      problem: {
        severity: 'error',
        code: TOKEN_SUM_MISMATCH,
        message: `fresh_input + cache_read + cache_creation (${inputSum}) does not reconcile with reported_input (${tokens.reportedInput})`,
        location,
      },
    }
  }

  return { valid: true }
}
