// Codex harness adapter (issue #196, T4b). Codex exports OTLP logs and spans
// when the owner points its exporters at the collector. Attribution is by
// attribute fingerprint only (R6.2): the `codex.*` vendor namespace plus the
// shared `gen_ai.usage.*` keys. `service.name` (codex_cli_rs, codex_exec,
// codex-app-server) is never consulted; the CLI/exec/app-server split is not
// attributable from span attributes, so every record is harness `codex`.
//
// UNVERIFIED until an owner captures a real payload (plan 2026-10-01-issue-196):
//   1. the exported span name of handle_responses,
//   2. whether log records carry trace_id/span_id,
//   3. the token convention. This adapter ASSUMES cache-inclusive input
//      (OpenAI style, like Copilot). An exclusive payload fails validation with
//      a negative fresh input rather than being silently miscounted.
//
// Privacy: only an allowlist of attributes reaches the canonical record, its
// parts and its raw map. Prompts, tool arguments/output, user.email and account
// ids are dropped before normalization.

import type { HarnessAdapter, RawSpan } from './base.js'
import { resolveRootByParentage, traceGroup } from './base.js'
import {
  baseRecord,
  hasNamespace,
  inclusiveConvention,
  readCounter,
  readUsageCounters,
  validateRecordTokens,
  INPUT_TOKEN_KEYS,
  OUTPUT_TOKEN_KEYS,
  USAGE_EVIDENCE,
  VENDOR_EVIDENCE,
} from './copilot.js'

const CODEX_VENDOR_NAMESPACES = ['codex']
const CACHE_WRITE_KEYS = ['gen_ai.usage.cache_write.input_tokens'] as const
const REASONING_OUTPUT_KEYS = ['codex.usage.reasoning_output_tokens', 'gen_ai.usage.reasoning_tokens'] as const

const ALLOWED_KEYS = new Set(['model', 'slug', 'conversation.id', 'event.timestamp'])
const ALLOWED_PREFIXES = ['gen_ai.usage.', 'codex.usage.']

function allowlisted(attributes: Record<string, unknown>): Record<string, unknown> {
  return Object.fromEntries(
    Object.entries(attributes).filter(
      ([key]) => ALLOWED_KEYS.has(key) || ALLOWED_PREFIXES.some((prefix) => key.startsWith(prefix)),
    ),
  )
}

export const codexAdapter: HarnessAdapter = {
  name: 'codex',
  namespaces: ['gen_ai', ...CODEX_VENDOR_NAMESPACES],

  detect(span) {
    let score = 0
    if (hasNamespace(span.attributes, CODEX_VENDOR_NAMESPACES)) score += VENDOR_EVIDENCE
    if (INPUT_TOKEN_KEYS.some((key) => key in span.attributes)) score += USAGE_EVIDENCE
    return Math.min(1, score)
  },

  relevance(span) {
    if (INPUT_TOKEN_KEYS.some((key) => key in span.attributes)) return 1
    if (OUTPUT_TOKEN_KEYS.some((key) => key in span.attributes)) return 0.5
    if (hasNamespace(span.attributes, CODEX_VENDOR_NAMESPACES)) return 0.1
    return 0
  },

  normalize(raw: RawSpan) {
    const safe: RawSpan = { ...raw, attributes: allowlisted(raw.attributes) }
    const record = baseRecord(this, safe)
    const conversation = safe.attributes['conversation.id']
    if (typeof conversation === 'string' && conversation !== '') record.sessionId = conversation
    const counters = readUsageCounters(safe.attributes)
    const reasoning = readCounter(safe.attributes, REASONING_OUTPUT_KEYS)
    record.tokens = inclusiveConvention({
      input: counters.input,
      cacheRead: counters.cacheRead,
      cacheCreation: readCounter(safe.attributes, CACHE_WRITE_KEYS),
      output: counters.output,
      ...(reasoning !== 0 ? { reasoning } : {}),
    })
    return record
  },

  group: traceGroup,
  resolveRoot: resolveRootByParentage,
  validate: validateRecordTokens,

  unexportedMetrics() {
    return ['tool_definitions']
  },
}
