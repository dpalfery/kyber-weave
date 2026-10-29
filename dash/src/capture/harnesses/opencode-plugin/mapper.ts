// Opencode plugin span mapper (T3): T0 payloads onto the T1 envelope.
//
// The dash-CLI-managed plugin observes session/turn/span hooks with the
// shapes surveyed in T0 (`fixtures/span-payload-skeleton.json`: session id,
// turn index, agent, model provider/id, prompt text, token counters, tool
// call, error). Ingest only claims the T1 envelope
// (`canon/adapters/opencode-envelope.ts`): the version key plus
// `opencode.turn/model/tool/prompt/call/message/hook` markers, with the tool
// marker winning the operation vote and a marker-less span quarantining as
// non-model.
//
// The mapper is the bridge, and nothing else: it stamps the supported
// contract version, carries the session identity verbatim (never
// synthesized), writes the model/tool markers the adapter votes on, and
// converts the T0 counters onto the OpenInference `llm.token_count.*` keys
// the adapter already reads through the cache-EXCLUSIVE convention (pi's
// shape, not Copilot's inclusive one — the live `llm.token_count.prompt`
// 616 beside `prompt_details.cache_read` 159744 proves the prompt carries
// fresh input alone). Timing and error status ride the OTLP span itself,
// which `growWithReceiverTiming` stamps onto the record; no transport,
// outbox, receiver, web, tray, resolver, or API change lives here.

import {
  OPENCODE_CONTEXT_SKILLS_KEY,
  OPENCODE_CONTEXT_SYSTEM_KEY,
  OPENCODE_CONTEXT_TOOLS_KEY,
  OPENCODE_CONTRACT_VERSION,
  OPENCODE_CONTRACT_VERSION_KEY,
} from '../../../canon/adapters/opencode-envelope.js'
import type { OtlpSpan } from '../../../otel/receiver.js'

/** Optional reported context window (D6); additive inside contract '2'. */
export const OPENCODE_MODEL_CONTEXT_LIMIT_KEY = 'opencode.model.context_limit'

/** Token counters as the hook payload carries them (T0 `tokens` bucket). */
export type OpencodePluginSpanTokens = {
  input_tokens: number
  output_tokens: number
  cache_read_tokens?: number
  cache_creation_tokens?: number
  reasoning_tokens?: number
}

/** Error detail as the hook payload carries it (T0 `errors` bucket). */
export type OpencodePluginSpanError = {
  type?: string | null
  message?: string | null
  status?: string | number | null
  attempt?: number | null
  retry_decision?: string | null
}

/**
 * A real-shaped plugin span payload: the T0 skeleton with its empty strings
 * filled in. Every field the skeleton surveys is present so the mapper test
 * pins the whole round trip; optional fields stay optional so a marker-less
 * app span (no model, no tool) is still expressible and still quarantines as
 * non-model per the T1 rules.
 */
export type OpencodePluginSpanPayload = {
  traceId: string
  spanId: string
  parentSpanId?: string | null
  name?: string
  kind?: string
  sessionID: string
  messageID?: string
  callID?: string
  turnIndex?: number
  agent?: string
  model?: { providerID: string; modelID?: string }
  prompt_text?: string
  tool?: string
  /** Assembled system prompt (T4, T0 `SessionContext.system`); carried only when non-empty. */
  systemPrompt?: string
  /** JSON-serialized tool definitions (T4, T0 `SessionContext.tools`); carried only when non-empty. */
  toolsText?: string
  /** Skill names listed for the session (T4, T0 pin); serialized as a JSON array, carried only when non-empty. */
  skills?: string[] | string
  /** Plain model identity from a context event (T4); carried only when non-empty. */
  modelId?: string
  /** Model context window the plugin read from the host catalog (D6); stamped only when a positive finite number. */
  contextLimit?: number
  tokens?: OpencodePluginSpanTokens
  errors?: OpencodePluginSpanError
  startTimeUnixNano: string | number | bigint
  endTimeUnixNano: string | number | bigint
  hook?: string
}

function nonEmpty(value: unknown): value is string {
  return typeof value === 'string' && value !== ''
}

function toNanoString(value: string | number | bigint): string {
  return typeof value === 'bigint' ? value.toString() : String(value)
}

function finiteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value)
}

// Best-effort harness clock: a junk timestamp must never throw away the
// span+log evidence for the hook. Unparseable input falls back to now,
// matching the installed pipeline's hook-anchored fallback.
function nanoToMs(timestampNano: string | number | bigint): number {
  try {
    const ms = Number(BigInt(toNanoString(timestampNano)) / 1_000_000n)
    return Number.isFinite(ms) ? ms : Date.now()
  } catch {
    return Date.now()
  }
}

function isErrorPayload(errors: OpencodePluginSpanError | undefined): boolean {
  if (errors === undefined) return false
  if (typeof errors.status === 'string' && errors.status.toLowerCase() === 'error') return true
  if (typeof errors.status === 'number' && errors.status !== 0) return true
  return nonEmpty(errors.type) || nonEmpty(errors.message)
}

/**
 * True for the B1 tool lifecycle hooks (`tool.execute.before/after` per
 * fixtures/hook-catalog.json). Kept as literal kinds here — not imported
 * from the skeleton — because the mapper must not depend on the plugin host
 * surface it feeds (the plugin imports the mapper, never the reverse).
 */
function isToolLifecycleHook(hook: string | undefined): boolean {
  return hook === 'tool.execute.before' || hook === 'tool.execute.after'
}

/**
 * Carry one hook payload onto envelope attributes. The version key is always
 * stamped (an envelope span names its contract); model, prompt, tool, call,
 * message, and hook markers are carried only when the payload names them, so
 * a marker-less app span stays marker-less and the adapter keeps it
 * `unspecified` for the non-model quarantine.
 */
export function mapOpencodePayloadToAttributes(
  payload: OpencodePluginSpanPayload,
): Record<string, unknown> {
  const attributes: Record<string, unknown> = {
    [OPENCODE_CONTRACT_VERSION_KEY]: OPENCODE_CONTRACT_VERSION,
    'opencode.session.id': payload.sessionID,
  }
  if (typeof payload.turnIndex === 'number' && Number.isFinite(payload.turnIndex)) {
    attributes['opencode.turn.index'] = payload.turnIndex
  }
  if (nonEmpty(payload.agent)) attributes['opencode.agent'] = payload.agent
  if (payload.model !== undefined) {
    if (nonEmpty(payload.model.providerID)) {
      attributes['opencode.model.provider'] = payload.model.providerID
    }
    if (nonEmpty(payload.model.modelID)) attributes['opencode.model.id'] = payload.model.modelID
  }
  if (nonEmpty(payload.prompt_text)) attributes['opencode.prompt.text'] = payload.prompt_text
  // T4 context content (envelope v2): carried only when named, so absent
  // fields stay absent — never empty-string padding.
  if (nonEmpty(payload.systemPrompt)) attributes[OPENCODE_CONTEXT_SYSTEM_KEY] = payload.systemPrompt
  if (nonEmpty(payload.toolsText)) attributes[OPENCODE_CONTEXT_TOOLS_KEY] = payload.toolsText
  if (typeof payload.skills === 'string') {
    if (nonEmpty(payload.skills)) {
      // A serialized empty list measures the same none as an absent list:
      // drop it so `[]` and `'[]'` stay one measurability. Malformed text
      // stays stamped — the adapter reports it under its named reason.
      let parsed: unknown
      try {
        parsed = JSON.parse(payload.skills)
      } catch {
        parsed = undefined
      }
      if (!(Array.isArray(parsed) && parsed.length === 0)) {
        attributes[OPENCODE_CONTEXT_SKILLS_KEY] = payload.skills
      }
    }
  } else if (Array.isArray(payload.skills) && payload.skills.length > 0) {
    attributes[OPENCODE_CONTEXT_SKILLS_KEY] = JSON.stringify(payload.skills)
  }
  if (nonEmpty(payload.modelId)) attributes['opencode.model.id'] = payload.modelId
  // D6: additive attribute inside contract '2' (no bump). Absent stays absent; 0 or non-finite is never stamped.
  if (finiteNumber(payload.contextLimit) && payload.contextLimit > 0) {
    attributes[OPENCODE_MODEL_CONTEXT_LIMIT_KEY] = payload.contextLimit
  }
  if (nonEmpty(payload.tool)) attributes['opencode.tool.name'] = payload.tool
  if (nonEmpty(payload.messageID)) attributes['opencode.message.id'] = payload.messageID
  if (nonEmpty(payload.callID)) {
    attributes['opencode.call.id'] = payload.callID
    if (nonEmpty(payload.tool)) attributes['opencode.tool.call.id'] = payload.callID
  }
  if (nonEmpty(payload.hook)) attributes['opencode.hook'] = payload.hook
  // Double-count guard (B1, decided): a tool lifecycle span never carries
  // usage — native OTel owns the counters, the plugin emits identity,
  // markers, and outcomes only. The hook payload carries no counters by
  // type, so this drops them only for a direct caller that hands the mapper
  // a tokens bucket on a tool hook anyway. Non-tool spans are unaffected.
  if (payload.tokens !== undefined && !isToolLifecycleHook(payload.hook)) {
    // Non-finite counters are omitted, never stamped: NaN/Infinity would
    // otherwise land as a confident measured value downstream.
    if (finiteNumber(payload.tokens.input_tokens)) {
      attributes['llm.token_count.prompt'] = payload.tokens.input_tokens
    }
    if (finiteNumber(payload.tokens.output_tokens)) {
      attributes['llm.token_count.completion'] = payload.tokens.output_tokens
    }
    if (finiteNumber(payload.tokens.cache_read_tokens)) {
      attributes['llm.token_count.prompt_details.cache_read'] = payload.tokens.cache_read_tokens
    }
    if (finiteNumber(payload.tokens.cache_creation_tokens)) {
      attributes['llm.token_count.prompt_details.cache_write'] = payload.tokens.cache_creation_tokens
    }
    if (finiteNumber(payload.tokens.reasoning_tokens)) {
      attributes['llm.token_count.completion_details.reasoning'] = payload.tokens.reasoning_tokens
    }
  }
  if (payload.errors !== undefined) {
    if (nonEmpty(payload.errors.type)) attributes['opencode.error.type'] = payload.errors.type
    if (nonEmpty(payload.errors.message)) attributes['opencode.error.message'] = payload.errors.message
  }
  return attributes
}

/**
 * Carry one hook payload onto a decoded OTLP span ready for `ingestBatch`.
 * Timing is the harness clock verbatim (nanoseconds to ISO + duration); the
 * error status is the harness verdict verbatim (error stays error, anything
 * else stays ok). The adapter seam grows the record from exactly these
 * fields, so a mapper change here is what a round-trip test would catch.
 */
export function mapOpencodePayloadToOtlpSpan(payload: OpencodePluginSpanPayload): OtlpSpan {
  const attributes = mapOpencodePayloadToAttributes(payload)
  const startMs = nanoToMs(payload.startTimeUnixNano)
  const endMs = nanoToMs(payload.endTimeUnixNano)
  const error = isErrorPayload(payload.errors)
  const errorMessage =
    payload.errors !== undefined && nonEmpty(payload.errors.message)
      ? payload.errors.message
      : payload.errors !== undefined && nonEmpty(payload.errors.type)
        ? payload.errors.type
        : undefined
  return {
    traceId: payload.traceId,
    spanId: payload.spanId,
    parentSpanId: payload.parentSpanId ?? null,
    name: payload.name ?? 'opencode.turn',
    kind: payload.kind ?? 'internal',
    startTimeUnixNano: toNanoString(payload.startTimeUnixNano),
    endTimeUnixNano: toNanoString(payload.endTimeUnixNano),
    timestamp: new Date(startMs).toISOString(),
    durationMs: Math.max(0, endMs - startMs),
    status: error ? { code: 'error', ...(errorMessage !== undefined ? { message: errorMessage } : {}) } : { code: 'ok' },
    attributes,
    resource: { 'service.name': 'opencode' },
    scope: {},
  }
}
