// Claude Code harness adapter (spec: docs/specs/kyberdash; R4.2, R6.2).
//
// Claude Code's telemetry was reaching the store attributed to no harness at
// all, with every token counter zeroed. Two independent reasons:
//
//   1. Its counters are un-namespaced — `input_tokens`, `output_tokens`,
//      `cache_read_tokens`, `cache_creation_tokens` — while the shared
//      counter reader looked only for the `gen_ai.usage.*` spellings. All
//      1,250 of its stored spans normalized to all-zero tokens, which reads
//      downstream as a session with no spend rather than as a session whose
//      counters were not understood.
//   2. No adapter claimed it, so the live collector's fallback stamped the
//      harness from `service.name` — the one signal R6.2 says is never
//      attribution evidence.
//
// The convention is the cache-EXCLUSIVE one, matching Anthropic's API, where
// `input_tokens` counts non-cached input only and the cache classes are
// reported separately. Measured over the corpus: of 257 spans carrying
// counters, 255 had cache activity, and every one of those yields a negative
// fresh input under the inclusive conversion — a median of -179,155, and
// spans reporting `input_tokens: 2` against `cache_read_tokens: 35,739`.
// A single cached turn is enough to tell the two conventions apart, and this
// corpus is unambiguous.

import type { HarnessAdapter } from './base.js'
import { resolveRootByParentage, traceGroup } from './base.js'
import type { ContentPart } from '../types.js'
import {
  INPUT_TOKEN_KEYS,
  OUTPUT_TOKEN_KEYS,
  USAGE_EVIDENCE,
  VENDOR_EVIDENCE,
  baseRecord,
  exclusiveConvention,
  hasNamespace,
  readUsageCounters,
  validateRecordTokens,
} from './copilot.js'

/**
 * Vendor namespaces Claude Code telemetry is emitted under. `claude.*`
 * carries deployment metadata and `claude_code.*` names its own operations;
 * neither appears on another harness in the corpus. The source name is never
 * consulted (R6.2), which is what makes this adapter necessary rather than
 * cosmetic — the collector was reading `service.name` instead.
 */
const CLAUDE_VENDOR_NAMESPACES = ['claude', 'claude_code']

/**
 * Extract the content Claude sends in its API request body.
 *
 * The raw body is deliberately treated as an opaque boundary: only the
 * observed `system`, `tools`, and tool `server` fields are interpreted.
 * Tool names are not split because their spelling is not server evidence.
 */
export function parseClaudeApiRequestBody(body: unknown): ContentPart[] {
  let value: unknown = body
  if (typeof value === 'string') {
    try {
      value = JSON.parse(value)
    } catch {
      return []
    }
  }
  if (value === null || typeof value !== 'object') return []

  const request = value as Record<string, unknown>
  const parts: ContentPart[] = []
  const system = request.system
  if (typeof system === 'string') {
    parts.push({ part: 'system_prompt', text: system })
  } else if (Array.isArray(system)) {
    for (const block of system) {
      if (block === null || typeof block !== 'object') continue
      const text = (block as Record<string, unknown>).text
      if (typeof text === 'string') parts.push({ part: 'system_prompt', text })
    }
  }

  if (Array.isArray(request.tools)) {
    for (const tool of request.tools) {
      if (tool === null || typeof tool !== 'object') continue
      const toolRecord = tool as Record<string, unknown>
      const part: ContentPart = {
        part: 'tool_definitions',
        text: JSON.stringify(tool),
      }
      if (typeof toolRecord.server === 'string') part.server = toolRecord.server
      parts.push(part)
    }
  }
  return parts
}

/**
 * The Claude Code adapter. Detection is vendor-namespace driven, scored the
 * same way the other adapters score: a vendor attribute carries the vote,
 * and the shared usage keys alone are not enough to claim a span.
 */
export const claudeCodeAdapter: HarnessAdapter = {
  name: 'claude-code',
  namespaces: ['gen_ai', ...CLAUDE_VENDOR_NAMESPACES],

  detect(span) {
    let score = 0
    if (hasNamespace(span.attributes, CLAUDE_VENDOR_NAMESPACES)) score += VENDOR_EVIDENCE
    if (INPUT_TOKEN_KEYS.some((key) => key in span.attributes)) score += USAGE_EVIDENCE
    return Math.min(1, score)
  },

  relevance(span) {
    if (INPUT_TOKEN_KEYS.some((key) => key in span.attributes)) return 1
    if (OUTPUT_TOKEN_KEYS.some((key) => key in span.attributes)) return 0.5
    if (hasNamespace(span.attributes, CLAUDE_VENDOR_NAMESPACES)) return 0.1
    return 0
  },

  /**
   * Convert the cache-exclusive counters into the disjoint classes (R4.2):
   * fresh input is taken as claimed and `reportedInput` is reassembled from
   * the classes. Nothing is clamped; the identity validation checks is the
   * one the classes must satisfy.
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
   * Claude Code's spans carry counters and metadata but no message content:
   * no system prompt, no tool definitions, no message structure. Measured
   * across the corpus, not one span carried a content attribute. Every
   * content bucket is therefore declared unexported rather than reported as
   * zero (R7.6, R10.1) — the composition chart must say "not measurable"
   * for this source, and the content it needs lives in the harness's
   * on-disk transcripts instead.
   */
  unexportedMetrics() {
    return [
      'system_prompt',
      'tool_definitions',
      'instruction_context',
      'conversation_history',
      'tool_result_content',
      'schema_cost',
    ]
  },
}
