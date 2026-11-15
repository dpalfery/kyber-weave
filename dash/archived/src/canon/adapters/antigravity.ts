// Antigravity harness adapter (issue #195). Antigravity speaks Gemini's
// telemetry vocabulary — live spans carry `gen_ai.system: 'gemini'` — but it
// is a distinct surveyed harness with its own agent identity
// (`gen_ai.agent.name: 'antigravity'`) and its own conversation store. Before
// this adapter existed the fingerprint vote filed those spans under the
// excluded `gemini` identity and the projection dropped them, hiding live
// `agy` history on every screen.
//
// Detection is keyed on the agent identity, never the vendor label alone:
// the shared `gen_ai.usage.*` keys score 0.4, below the registry threshold,
// so an agent-name-less span stays undecided (R6.1 quarantine) rather than
// guessed. The source name is never consulted (R6.2).
//
// Token semantics are Gemini's — cache-INCLUSIVE input, no cache-creation
// counter, thoughts a subset of output — the same conversion these rows
// already received under the gemini label, so only the label changes, never
// the figures. This agrees with the file side (`PROVIDER_UNMEASURABLE`
// antigravity family): OTLP and file sessions state the same limitation.

import type { HarnessAdapter } from './base.js'
import { resolveRootByParentage, traceGroup } from './base.js'
import {
  baseRecord,
  convertInclusiveCounts,
  readCounter,
  readUsageCounters,
  validateRecordTokens,
  INPUT_TOKEN_KEYS,
  OUTPUT_TOKEN_KEYS,
  VENDOR_EVIDENCE,
  USAGE_EVIDENCE,
} from './copilot.js'
import { THOUGHT_KEYS } from './gemini.js'

export { reconcileRequest } from './gemini.js'
export type { RequestReconciliation } from './copilot.js'

/** The agent identity Antigravity telemetry asserts about itself. */
const ANTIGRAVITY_AGENT_KEY = 'gen_ai.agent.name'
const ANTIGRAVITY_AGENT_VALUE = 'antigravity'

/** True when the span carries Antigravity's own agent identity. */
function isAntigravityAgent(attributes: Record<string, unknown>): boolean {
  return attributes[ANTIGRAVITY_AGENT_KEY] === ANTIGRAVITY_AGENT_VALUE
}

export const antigravityAdapter: HarnessAdapter = {
  name: 'antigravity',
  namespaces: ['gen_ai', 'gemini', 'antigravity'],

  detect(span) {
    let score = 0
    if (isAntigravityAgent(span.attributes)) score += VENDOR_EVIDENCE
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
    if (isAntigravityAgent(span.attributes)) return 0.1
    return 0
  },

  /**
   * Convert the cached-inclusive counters into the disjoint classes (R4.2):
   * fresh = input − cacheRead (no creation term — there is no counter for
   * it). When cache exceeds input the counters cannot be inclusive — they
   * convert exclusively rather than being stored as negative fresh or
   * clamped to zero (issue #193).
   */
  normalize(raw) {
    const record = baseRecord(this, raw)
    const counters = readUsageCounters(raw.attributes)
    const thoughts =
      counters.reasoning !== 0 ? counters.reasoning : readCounter(raw.attributes, THOUGHT_KEYS)
    record.tokens = convertInclusiveCounts({
      input: counters.input,
      cacheRead: counters.cacheRead,
      cacheCreation: 0,
      output: counters.output,
      inputPresent: counters.inputPresent,
      ...(thoughts !== 0 ? { reasoning: thoughts } : {}),
    })
    return record
  },

  group: traceGroup,
  resolveRoot: resolveRootByParentage,
  validate: validateRecordTokens,

  /**
   * Antigravity's explicit caching has no cache-creation counter, so
   * `cache_creation` is declared unexported and stamped `not_measurable` on
   * every record: the absence of a write charge is a stated limitation, not
   * a zero (R7.6, R10.2). This matches the file-side declaration, so OTLP
   * and file sessions agree.
   */
  unexportedMetrics() {
    return ['cache_creation']
  },
}
