// pi harness adapter, ported from the pipeline's pi parser (spec:
// docs/specs/kyberdash; R4.2, R4.5). pi's token convention is the opposite of
// Copilot's under the SAME attribute key: `gen_ai.usage.input_tokens`
// EXCLUDES cache read and cache creation — the input counter reports fresh
// input alone, with the cache classes carried by their own counters (the
// session parser reads the same shape as `usage.input` alongside
// `usage.cacheRead` / `usage.cacheWrite` in dash/src/providers/pi.ts).
//
// Converting on the way in therefore takes fresh input as claimed and
// reassembles the converted total from the classes. This is the direction a
// single "input" number cannot check: fed Copilot-shaped counters it
// double-counts input by up to 2× while the sum identity still holds, which
// is exactly why the disjoint classes plus R4.5 reconciliation exist (see
// `exclusiveConvention` in copilot.ts, where the shared task-5.3 core
// lives).

import type { HarnessAdapter } from './base.js'
import { resolveRootByParentage, traceGroup } from './base.js'
import {
  baseRecord,
  exclusiveConvention,
  hasNamespace,
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

/** Vendor namespaces pi telemetry is emitted under (its OTLP export stamps `pi.*`). */
const PI_VENDOR_NAMESPACES = ['pi']

/**
 * The pi adapter. Detection is vendor-namespace driven: `pi.*` attributes
 * (e.g. `pi.session.id`) carry the vote, while the shared `gen_ai.usage.*`
 * keys alone score 0.4 — below the registry threshold, leaving GenAI-only
 * tool spans to quarantine or same-source inheritance (R6.1, R6.2). The
 * source name is never consulted.
 */
export const piAdapter: HarnessAdapter = {
  name: 'pi',
  namespaces: ['gen_ai', ...PI_VENDOR_NAMESPACES],

  detect(span) {
    let score = 0
    if (hasNamespace(span.attributes, PI_VENDOR_NAMESPACES)) score += VENDOR_EVIDENCE
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
    if (hasNamespace(span.attributes, PI_VENDOR_NAMESPACES)) return 0.1
    return 0
  },

  /**
   * Convert pi's cache-exclusive counters into the disjoint classes (R4.2):
   * fresh input is taken as claimed and `reportedInput` is reassembled as
   * fresh + cacheRead + cacheCreation — the claim after conversion. Nothing
   * is clamped and no total is guessed; the invariant the classes must
   * satisfy is the one validation checks.
   */
  normalize(raw) {
    const record = baseRecord(this, raw)
    const counters = readUsageCounters(raw.attributes)
    record.tokens = exclusiveConvention({
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
   * pi invokes tools without exporting their definitions — measured at 14
   * tools across 368 calls with none exported (design.md, "Normalization
   * layer") — so `tool_definitions` is declared unexported and every record
   * is stamped `not_measurable` for it: absent is not zero (R7.6, R8.5,
   * R10.2).
   */
  unexportedMetrics() {
    return ['tool_definitions']
  },
}
