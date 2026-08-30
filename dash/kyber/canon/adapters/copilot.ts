// Copilot harness adapter, ported from the pipeline's Copilot parser (spec:
// docs/specs/kyberdash; R4.2, R4.5). Copilot's token convention is the
// cache-INCLUSIVE one: `gen_ai.usage.input_tokens` already totals fresh input
// plus cache read plus cache creation, as its own parser records
// (dash/src/providers/copilot.ts: "input_tokens is cache-INCLUSIVE (input +
// cache_read + cache_write)"). Converting on the way in therefore SUBTRACTS
// the cache classes to recover fresh input; clamping the subtraction to zero
// would hide exactly the inversion R4.2 exists to catch, so the adapter
// preserves a negative result and lets `validate` reject the record loudly.
//
// This module also hosts the small shared core the three task-5.3 adapters
// are built from — counter reading, the two convention conversions, and the
// per-request reconciliation of R4.5 — because the task's scope admits only
// the three adapter files. Copilot is its home as the inclusive-convention
// reference; pi.ts and gemini.ts import from here rather than drift into a
// third private copy of arithmetic whose whole point is that two conventions
// under one attribute key already cost a silent miscount.

import type { HarnessAdapter, RawSpan } from './base.js'
import { resolveRootByParentage, traceGroup } from './base.js'
import {
  validateTokens,
  type CanonicalRecord,
  type TokenUsage,
} from '../types.js'

// ---------------------------------------------------------------------------
// Shared core — counter keys and reading
// ---------------------------------------------------------------------------

/**
 * Counter key families per class. Two spellings exist in the wild for the
 * cache classes: Copilot's OTel store writes the dotted
 * `gen_ai.usage.cache_read.input_tokens` (see dash/src/providers/copilot.ts
 * and its OTel tests), while summary-shaped exports use the underscored
 * `gen_ai.usage.cache_read_input_tokens`. An adapter's job is to convert on
 * the way in, so both are read; `gen_ai.usage.cached_tokens` is the GenAI
 * semantic-conventions spelling of cache read and is accepted for the same
 * reason. Accepting a spelling is not attribution evidence — `detect` votes
 * on vendor namespaces, never on these shared keys.
 */
export const INPUT_TOKEN_KEYS = ['gen_ai.usage.input_tokens'] as const
export const OUTPUT_TOKEN_KEYS = ['gen_ai.usage.output_tokens'] as const
export const CACHE_READ_KEYS = [
  'gen_ai.usage.cache_read.input_tokens',
  'gen_ai.usage.cache_read_input_tokens',
  'gen_ai.usage.cached_tokens',
] as const
export const CACHE_CREATION_KEYS = [
  'gen_ai.usage.cache_creation.input_tokens',
  'gen_ai.usage.cache_creation_input_tokens',
] as const
export const REASONING_KEYS = ['gen_ai.usage.reasoning_tokens'] as const

/**
 * Read a token counter from attribute values that may be number-typed (OTLP
 * JSON) or string-typed (exporters that flatten attributes to text columns,
 * as Copilot's OTel SQLite store does). Non-numeric garbage counts as absent
 * rather than NaN — a counter that did not parse was not reported.
 */
export function readCounter(attributes: Record<string, unknown>, keys: readonly string[]): number {
  for (const key of keys) {
    const value = attributes[key]
    if (typeof value === 'number' && Number.isFinite(value)) return value
    if (typeof value === 'string' && value.trim() !== '') {
      const parsed = Number(value)
      if (Number.isFinite(parsed)) return parsed
    }
  }
  return 0
}

/** True when any attribute key starts with one of the given namespaces. */
export function hasNamespace(attributes: Record<string, unknown>, namespaces: readonly string[]): boolean {
  for (const key of Object.keys(attributes)) {
    for (const namespace of namespaces) {
      if (key.startsWith(`${namespace}.`)) return true
    }
  }
  return false
}

/**
 * Timestamp carried on a RawSpan that has none: the base contract (task 5.1)
 * fixes RawSpan's shape without timing, so adapters stamp the epoch and the
 * receiver paths that do carry timing (task 6) remain free to grow it. A
 * neutral constant beats a fabricated wall clock.
 */
export const UNKNOWN_TIMESTAMP = '1970-01-01T00:00:00.000Z'

/**
 * The canonical operation for a span, decided from what the span carries
 * rather than the harness's verb (design.md, canonical record): a tool span
 * invokes a tool, a span carrying usage made an LLM call, anything else is
 * structural.
 */
export function canonicalOp(attributes: Record<string, unknown>): string {
  if ('gen_ai.tool.name' in attributes) return 'tool.invoke'
  if (
    INPUT_TOKEN_KEYS.some((key) => key in attributes) ||
    OUTPUT_TOKEN_KEYS.some((key) => key in attributes)
  ) {
    return 'llm.invoke'
  }
  return 'unspecified'
}

/** Map harness content attributes onto canonical keys; nothing else is guessed. */
export function canonicalContent(attributes: Record<string, unknown>): CanonicalRecord['content'] {
  const content: CanonicalRecord['content'] = {}
  const prompt = attributes['gen_ai.prompt']
  if (typeof prompt === 'string') content.conversation_history = prompt
  return content
}

// ---------------------------------------------------------------------------
// Shared core — the two token conventions (R4.2)
// ---------------------------------------------------------------------------

/**
 * Build a `TokenUsage` whose classes satisfy the reported-input identity
 * (R4.1) for the cache-INCLUSIVE convention (Copilot, Gemini): the harness's
 * input counter already totals fresh + cache read + cache creation.
 *
 * The subtraction is deliberately NOT clamped. Fed pi-shaped counters — where
 * the input counter EXCLUDES cache — it yields a negative fresh input, which
 * is the loud failure mode R4.2 measured on 293 of 307 spans; clamping here
 * would convert that loud failure into the silent miscount the spec forbids.
 */
export function inclusiveConvention(counts: {
  input: number
  cacheRead: number
  cacheCreation: number
  output: number
  reasoning?: number
}): TokenUsage {
  const freshInput = counts.input - counts.cacheRead - counts.cacheCreation
  return {
    freshInput,
    cacheRead: counts.cacheRead,
    cacheCreation: counts.cacheCreation,
    output: counts.output,
    ...(counts.reasoning !== undefined ? { reasoning: counts.reasoning } : {}),
    reportedInput: counts.input,
    reportedOutput: counts.output,
  }
}

/**
 * Build a `TokenUsage` for the cache-EXCLUSIVE convention (pi): the
 * harness's input counter reports fresh input only, so fresh is taken as
 * claimed and the converted total input is reassembled from the classes —
 * `reportedInput` is defined as the claim *after* conversion (types.ts).
 *
 * Fed Copilot-shaped counters — where the input counter already INCLUDES the
 * cache classes — this reassembly double-counts input by up to 2×. The sum
 * identity still holds, so record validation cannot see it; the per-request
 * reconciliation of R4.5 is the check that does, which is why both halves of
 * this task ship together.
 */
export function exclusiveConvention(counts: {
  input: number
  cacheRead: number
  cacheCreation: number
  output: number
  reasoning?: number
}): TokenUsage {
  return {
    freshInput: counts.input,
    cacheRead: counts.cacheRead,
    cacheCreation: counts.cacheCreation,
    output: counts.output,
    ...(counts.reasoning !== undefined ? { reasoning: counts.reasoning } : {}),
    reportedInput: counts.input + counts.cacheRead + counts.cacheCreation,
    reportedOutput: counts.output,
  }
}

/**
 * Record validation shared by the adapters (R4.3, R4.4): `validateTokens` is
 * the whole check — non-negative disjoint classes, reasoning within output,
 * and the reported-input identity — and its problem, when any, is what the
 * caller stores instead of the record.
 */
export function validateRecordTokens(record: CanonicalRecord) {
  return validateTokens(record.tokens, record.spanId).problem
}

/** Read the raw counters every GenAI-emitting harness shares. */
export function readUsageCounters(attributes: Record<string, unknown>) {
  return {
    input: readCounter(attributes, INPUT_TOKEN_KEYS),
    output: readCounter(attributes, OUTPUT_TOKEN_KEYS),
    cacheRead: readCounter(attributes, CACHE_READ_KEYS),
    cacheCreation: readCounter(attributes, CACHE_CREATION_KEYS),
    reasoning: readCounter(attributes, REASONING_KEYS),
  }
}

// ---------------------------------------------------------------------------
// Shared core — per-request reconciliation (R4.5)
// ---------------------------------------------------------------------------

/** The per-request match indicator R4.5 exposes. */
export type RequestReconciliation = {
  /** True when the per-turn sums equal the harness-reported totals exactly. */
  match: boolean
  /** Σ per-turn converted input (fresh + cache read + cache creation). */
  expected: number
  /** The input total the harness reported on the request root. */
  actual: number
  /** Same pair for output, so a mismatch names which axis diverged. */
  outputExpected: number
  outputActual: number
}

/**
 * Reconcile one request (R4.5): sum the per-turn converted tokens of every
 * record in the group except the root, and compare against the totals the
 * harness itself reported on the root span. `match` is the exposed
 * indicator; the figures ride along so a mismatch is diagnosable rather
 * than a bare boolean.
 *
 * Input is the reconciled axis that matters for conventions — the cache
 * semantics of R4.2 live there — but output is held to the same standard,
 * and `match` is false if either disagrees. Returns undefined when the root
 * is not among the records: with no harness-reported total there is nothing
 * to reconcile against, and that fact is itself the answer (never invent a
 * comparison). Both sides are stored either way; a mismatch is exposed, not
 * dropped (design.md, "Error Handling").
 */
export function reconcileRequest(
  rootSpanId: string,
  records: CanonicalRecord[],
): RequestReconciliation | undefined {
  const root = records.find((record) => record.spanId === rootSpanId)
  if (root === undefined) return undefined

  let expected = 0
  let outputExpected = 0
  for (const record of records) {
    if (record.spanId === rootSpanId) continue
    expected += record.tokens.reportedInput
    outputExpected += record.tokens.reportedOutput
  }

  return {
    match:
      expected === root.tokens.reportedInput && outputExpected === root.tokens.reportedOutput,
    expected,
    actual: root.tokens.reportedInput,
    outputExpected,
    outputActual: root.tokens.reportedOutput,
  }
}

// ---------------------------------------------------------------------------
// Shared core — adapter skeleton
// ---------------------------------------------------------------------------

/**
 * The parts of `normalize` that do not depend on a convention: structure,
 * operation, content, cost placeholder and measurability. Each adapter adds
 * its token conversion on top. Cost stays unpriced here — bases and rates
 * belong to the cost engine (task 4), and an adapter guessing a figure would
 * blend bases silently (R5.1).
 */
export function baseRecord(adapter: HarnessAdapter, raw: RawSpan): CanonicalRecord {
  return {
    spanId: raw.spanId,
    traceId: raw.traceId,
    parentSpanId: raw.parentSpanId,
    source: raw.source,
    harness: adapter.name,
    name: raw.name,
    op: canonicalOp(raw.attributes),
    kind: raw.kind,
    timestamp: UNKNOWN_TIMESTAMP,
    durationMs: 0,
    status: 'unspecified',
    tokens: inclusiveConvention({ input: 0, cacheRead: 0, cacheCreation: 0, output: 0 }),
    content: canonicalContent(raw.attributes),
    cost: { basis: 'unknown', status: 'no_rate' },
    measurability: Object.fromEntries(
      adapter.unexportedMetrics().map((metric) => [metric, 'not_measurable' as const]),
    ),
    raw: raw.attributes,
  }
}

// ---------------------------------------------------------------------------
// The Copilot adapter
// ---------------------------------------------------------------------------

/** Vendor namespaces Copilot telemetry is emitted under. */
const COPILOT_VENDOR_NAMESPACES = ['github.copilot', 'copilot', 'copilot_chat', 'codeburn']

/** Fingerprint weight of the vendor namespace evidence versus the shared GenAI usage keys. */
export const VENDOR_EVIDENCE = 0.6
export const USAGE_EVIDENCE = 0.4

/**
 * The Copilot adapter. Detection is vendor-namespace driven: the shared
 * `gen_ai.usage.*` keys alone score every GenAI harness identically (0.4,
 * below the registry's threshold — the alone-in-its-trace case R6.1
 * quarantines), while a Copilot vendor attribute (`github.copilot.chat.turn.id`,
 * `copilot.*`, `codeburn.provider`) carries the vote. The source name is
 * never consulted (R6.2).
 */
export const copilotAdapter: HarnessAdapter = {
  name: 'copilot',
  namespaces: ['gen_ai', ...COPILOT_VENDOR_NAMESPACES],

  detect(span) {
    let score = 0
    if (hasNamespace(span.attributes, COPILOT_VENDOR_NAMESPACES)) score += VENDOR_EVIDENCE
    if (INPUT_TOKEN_KEYS.some((key) => key in span.attributes)) score += USAGE_EVIDENCE
    return Math.min(1, score)
  },

  /**
   * A span carrying input counts is the request span per-request accounting
   * reconciles over (R4.5); tool and structural spans rank below it.
   */
  relevance(span) {
    if (INPUT_TOKEN_KEYS.some((key) => key in span.attributes)) return 1
    if (OUTPUT_TOKEN_KEYS.some((key) => key in span.attributes)) return 0.5
    if (hasNamespace(span.attributes, COPILOT_VENDOR_NAMESPACES)) return 0.1
    return 0
  },

  /**
   * Convert Copilot's cache-inclusive counters into the disjoint classes
   * (R4.2): fresh = input − cacheRead − cacheCreation, unclamped, so the
   * inverted convention surfaces as a validation problem rather than a
   * silently mispriced record.
   */
  normalize(raw) {
    const record = baseRecord(this, raw)
    const counters = readUsageCounters(raw.attributes)
    record.tokens = inclusiveConvention({
      input: counters.input,
      cacheRead: counters.cacheRead,
      cacheCreation: counters.cacheCreation,
      output: counters.output,
      ...(counters.reasoning !== 0 ? { reasoning: counters.reasoning } : {}),
    })
    return record
  },

  group: traceGroup,
  resolveRoot: resolveRootByParentage,
  validate: validateRecordTokens,

  /**
   * Copilot's measured per-span telemetry carries no reasoning counter (its
   * own parser records `reasoningTokens: 0`, "no reasoning entry"); the
   * session.shutdown rollup and newer store variants are honored per span by
   * the reasoning reader, but the static declaration describes the measured
   * corpus: per-turn reasoning is unexported rather than reported as zero
   * (R7.6, R10.2).
   */
  unexportedMetrics() {
    return ['reasoning']
  },
}
