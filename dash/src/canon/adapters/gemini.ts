// Gemini harness adapter, ported from the pipeline's Gemini parser (spec:
// docs/specs/kyberdash; R4.2, R4.5). Gemini's convention is documented here
// because — unlike Copilot and pi, whose conventions are pinned by measured
// spans — Gemini's rests on the session parser's recorded reading of its
// counters (dash/src/providers/gemini.ts):
//
//   1. `gen_ai.usage.input_tokens` INCLUDES cached input ("Gemini's `input`
//      count includes `cached` tokens as a subset"), the same inclusive
//      semantics as Copilot — cache read is recovered by subtraction.
//   2. Gemini has NO cache-creation counter: explicit caching carries no
//      write charge, so cache creation is always stored as 0 and declared
//      unexported rather than guessed from an absent attribute.
//   3. Thought tokens (reasoning) are a SUBSET of output — Gemini bills them
//      at the output rate — so they map onto `reasoning`, which validation
//      holds within `output`.
//
// Should a Gemini telemetry corpus contradict an assumption, the fix is to
// revise the assumption here and its row in the test table, not to clamp or
// infer in the conversion (R4.2's rule survives every convention).

import type { HarnessAdapter } from './base.js'
import { resolveRootByParentage, traceGroup } from './base.js'
import {
  baseRecord,
  hasNamespace,
  inclusiveConvention,
  readCounter,
  readUsageCounters,
  reconcileRequest,
  validateRecordTokens,
  INPUT_TOKEN_KEYS,
  OUTPUT_TOKEN_KEYS,
  VENDOR_EVIDENCE,
  USAGE_EVIDENCE,
} from './copilot.js'

export { reconcileRequest }
export type { RequestReconciliation } from './copilot.js'

/** Vendor namespaces Gemini telemetry is emitted under. */
const GEMINI_VENDOR_NAMESPACES = ['gemini']
const GEMINI_SYSTEM_KEY = 'gen_ai.system'

/** Where Gemini's thought tokens come from, beyond the shared reasoning key. */
const THOUGHT_KEYS = ['gemini.usage.thoughts_tokens'] as const

/**
 * The Gemini adapter. Detection uses Gemini's vendor namespace or its exact
 * `gen_ai.system` value; the shared `gen_ai.usage.*` keys alone score 0.4,
 * below the registry threshold. The source name is never consulted (R6.2).
 */
export const geminiAdapter: HarnessAdapter = {
  name: 'gemini',
  namespaces: ['gen_ai', ...GEMINI_VENDOR_NAMESPACES],

  detect(span) {
    let score = 0
    if (
      hasNamespace(span.attributes, GEMINI_VENDOR_NAMESPACES) ||
      span.attributes[GEMINI_SYSTEM_KEY] === 'gemini'
    ) {
      score += VENDOR_EVIDENCE
    }
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
    if (
      hasNamespace(span.attributes, GEMINI_VENDOR_NAMESPACES) ||
      span.attributes[GEMINI_SYSTEM_KEY] === 'gemini'
    ) {
      return 0.1
    }
    return 0
  },

  /**
   * Convert Gemini's cached-inclusive counters into the disjoint classes
   * (R4.2): fresh = input − cacheRead (no creation term — there is no
   * counter for it), unclamped, so an inverted reading of the convention
   * surfaces as negative fresh input in validation rather than a silent
   * undercount.
   */
  normalize(raw) {
    const record = baseRecord(this, raw)
    const counters = readUsageCounters(raw.attributes)
    const thoughts =
      counters.reasoning !== 0 ? counters.reasoning : readCounter(raw.attributes, THOUGHT_KEYS)
    record.tokens = inclusiveConvention({
      input: counters.input,
      cacheRead: counters.cacheRead,
      cacheCreation: 0,
      output: counters.output,
      ...(thoughts !== 0 ? { reasoning: thoughts } : {}),
    })
    return record
  },

  group: traceGroup,
  resolveRoot: resolveRootByParentage,
  validate: validateRecordTokens,

  /**
   * Gemini's explicit caching has no cache-creation counter, so
   * `cache_creation` is declared unexported and stamped `not_measurable` on
   * every record: the absence of a write charge is a stated limitation, not
   * a zero (R7.6, R10.2).
   */
  unexportedMetrics() {
    return ['cache_creation']
  },
}
