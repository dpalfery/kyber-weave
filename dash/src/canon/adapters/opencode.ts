// OpenCode harness adapter (Seam 3, P2.4b).
//
// OpenCode has been seen in three telemetry vocabularies. An older build
// emitted the Vercel AI SDK's; `gen_ai.*` content is already mapped by
// `canonicalParts`, so for that vocabulary this adapter only adds the legacy
// `ai.*` buckets and the counters. The deployed OpenCode (2.0.0) emits
// neither: it emits OpenInference (`openinference.span.kind`, `llm.*`,
// `input.value`), which this adapter now detects and normalizes too.
// Attribution never reads `service.name` (R6.2) — that value arrives as
// `RawSpan.source` and is not consulted.
//
// The OpenInference token convention is cache-EXCLUSIVE, pi's shape rather
// than Copilot's: the live span reports `llm.token_count.prompt` 616 beside
// `prompt_details.cache_read` 159744 and `llm.token_count.total` 160747, and
// 616 + 159744 + 0 (cache_write) + 242 (completion) + 145 (reasoning) is
// exactly that total. The prompt therefore carries fresh input alone and the
// cache classes are its disjoint siblings; see `applyOpenInferenceUsage`.
//
// App spans are not model calls. `sql.execute` and `http.server GET` are
// already structural in the ingest seam, so they stay unclaimed. The
// OpenCode Effect spans are not: the seam only calls a claimed span non-model
// when its operation is unspecified. Detecting those spans here, and leaving
// `op` unspecified, is what quarantines them as non-model without another
// file.

import type { HarnessAdapter, RawSpan } from './base.js'
import { resolveRootByParentage, traceGroup } from './base.js'
import {
  CACHE_CREATION_KEYS,
  CACHE_READ_KEYS,
  OUTPUT_TOKEN_KEYS,
  baseRecord,
  convertInclusiveCounts,
  exclusiveConvention,
  hasNamespace,
  parseStructured,
  readOptionalCounter,
  readUsageCounters,
  validateRecordTokens,
} from './copilot.js'
import {
  contentFromParts,
  notMeasurable,
  type ContentPart,
  type Measurability,
} from '../types.js'
import {
  isOpencodeEnvelopeModel,
  isOpencodeEnvelopeSpan,
  isOpencodeEnvelopeTool,
  isSupportedOpencodeEnvelope,
  opencodeEnvelopeContextSystem,
  opencodeEnvelopeContextTools,
  opencodeEnvelopePromptText,
  opencodeEnvelopeSessionId,
} from './opencode-envelope.js'

/** The harness identity stamped on records this adapter wins. */
const OPENCODE_HARNESS = 'opencode'

/**
 * Vendor fingerprints that already decide another harness. A shared
 * `gen_ai.*` semconv span must not outvote them: OpenCode is registered
 * last, and a higher score here would take a span those adapters only
 * partially match.
 */
const FOREIGN_VENDOR_NAMESPACES = [
  'pi',
  'copilot_chat',
  'github.copilot',
  'copilot',
  'codeburn',
  'gemini',
  'claude',
  'claude_code',
  'codex',
] as const

/** AI SDK legacy usage spellings. Current spans use `gen_ai.usage.*`. */
const LEGACY_INPUT_KEYS = ['ai.usage.promptTokens'] as const
const LEGACY_OUTPUT_KEYS = ['ai.usage.completionTokens'] as const

const LEGACY_MESSAGES_KEY = 'ai.prompt.messages'
const LEGACY_TOOLS_KEY = 'ai.prompt.tools'
const LEGACY_TOOL_RESULT_KEY = 'ai.toolCall.result'

/**
 * The OpenInference vocabulary the deployed OpenCode emits. Every span in it
 * carries `openinference.span.kind` (`LLM`, `TOOL`, `CHAIN`, …) — the vendor
 * namespace R6.2 admits as fingerprint evidence — and its kind is what
 * separates a model call from the tool calls and structural app spans around
 * it.
 */
const OPENINFERENCE_NAMESPACE = 'openinference'
const OPENINFERENCE_KIND_KEY = 'openinference.span.kind'
const OPENINFERENCE_INPUT_MESSAGES_KEY = 'llm.input_messages'

/** OpenInference usage counters. See `applyOpenInferenceUsage` for the convention. */
const OPENINFERENCE_INPUT_KEYS = ['llm.token_count.prompt'] as const
const OPENINFERENCE_OUTPUT_KEYS = ['llm.token_count.completion'] as const
const OPENINFERENCE_CACHE_READ_KEYS = ['llm.token_count.prompt_details.cache_read'] as const
const OPENINFERENCE_CACHE_CREATION_KEYS = ['llm.token_count.prompt_details.cache_write'] as const
const OPENINFERENCE_REASONING_KEYS = ['llm.token_count.completion_details.reasoning'] as const

/**
 * Native OpenInference model identity. The deployed OpenCode emits the model
 * under `llm.model_name`; downstream cost basis reads only the `gen_ai.*`
 * and `model` keys, so a native span without bridging resolves no model and
 * prices as `no_rate`. Bridged onto `gen_ai.request.model` in `normalize`
 * (never synthesized when absent).
 */
const NATIVE_MODEL_KEYS = ['llm.model_name'] as const

/**
 * Envelope model identity. `opencode.model.id` already feeds the context-window
 * identity but not the session `MODEL_KEYS`, so it is bridged the same way.
 * No new envelope marker: the existing `opencode.model.*` markers suffice.
 */
const ENVELOPE_MODEL_ID_KEYS = ['opencode.model.id'] as const

/**
 * Keys downstream already reads for the model (`sessions.ts` `MODEL_KEYS`,
 * `context-window.ts` `MODEL_IDENTITY_KEYS`, `store.ts`
 * `PROJECTED_ATTRIBUTE_KEYS`). Bridging writes only here so no downstream
 * reader needs a new key.
 */
const CANONICAL_MODEL_KEYS = ['gen_ai.response.model', 'gen_ai.request.model', 'model'] as const

/** First non-empty model string under the given keys, or undefined. Never synthesized. */
function readModelString(
  attributes: Record<string, unknown>,
  keys: readonly string[],
): string | undefined {
  // Strings only. A numeric attribute is not a model identity: coercing it
  // with `String(value)` turned the number `0` into the model name `"0"`,
  // which is precisely the fabricated identity this bridge never invents.
  for (const key of keys) {
    const value = attributes[key]
    if (typeof value === 'string' && value !== '') return value
  }
  return undefined
}

/**
 * The model id to bridge onto the record, or undefined when none is needed.
 * An already-canonical model wins by absence (nothing to bridge); otherwise
 * the native `llm.model_name` wins over the envelope `opencode.model.id`.
 * Absent stays absent: no fallback, no empty string, no zero.
 */
function bridgedModelId(attributes: Record<string, unknown>): string | undefined {
  if (readModelString(attributes, CANONICAL_MODEL_KEYS) !== undefined) return undefined
  return (
    readModelString(attributes, NATIVE_MODEL_KEYS) ??
    readModelString(attributes, ENVELOPE_MODEL_ID_KEYS)
  )
}

/** True when the span carries the OpenInference `openinference.*` namespace. */
function hasOpenInference(attributes: Record<string, unknown>): boolean {
  return hasNamespace(attributes, [OPENINFERENCE_NAMESPACE])
}

/** The OpenInference span kind, or undefined when the span named none. */
function openInferenceKind(attributes: Record<string, unknown>): string | undefined {
  const kind = attributes[OPENINFERENCE_KIND_KEY]
  return typeof kind === 'string' && kind !== '' ? kind : undefined
}

/**
 * The OpenInference model shape. The kind is authoritative when present, but
 * the model counters are accepted on their own too so a span whose kind was
 * dropped in transit is still read as a model call rather than quarantined.
 */
function isOpenInferenceModel(attributes: Record<string, unknown>): boolean {
  return (
    openInferenceKind(attributes) === 'LLM' ||
    'llm.model_name' in attributes ||
    OPENINFERENCE_INPUT_KEYS.some((key) => key in attributes)
  )
}

/** The OpenInference tool shape: the `TOOL` kind, or a `tool.name` on its own. */
function isOpenInferenceTool(attributes: Record<string, unknown>): boolean {
  return openInferenceKind(attributes) === 'TOOL' || 'tool.name' in attributes
}

/** True when the span carries OpenInference usage counters. */
function hasOpenInferenceUsage(attributes: Record<string, unknown>): boolean {
  return (
    OPENINFERENCE_INPUT_KEYS.some((key) => key in attributes) ||
    OPENINFERENCE_OUTPUT_KEYS.some((key) => key in attributes)
  )
}

/** True when the span carries the AI SDK's legacy `ai.*` namespace. */
function hasLegacyAi(attributes: Record<string, unknown>): boolean {
  return hasNamespace(attributes, ['ai'])
}

/**
 * The current AI SDK shape: a model operation plus the requested model.
 * Shared usage keys alone are every GenAI harness's 0.4 and are not this.
 */
function isGenAiSdkModel(attributes: Record<string, unknown>): boolean {
  return 'gen_ai.operation.name' in attributes && 'gen_ai.request.model' in attributes
}

function isModelSpan(span: RawSpan): boolean {
  return (
    hasLegacyAi(span.attributes) ||
    isGenAiSdkModel(span.attributes) ||
    isOpenInferenceModel(span.attributes)
  )
}

/**
 * OpenCode's Effect app spans. `Session.load`/`Session.create` are the
 * lifecycle calls, and `SessionRunner.*`, `SessionStep.*` and
 * `SessionProjector.*` are the agent loop's own steps. They carry no counter
 * and no OpenInference kind, so the emitted name is the only thing that
 * identifies them; claiming them here and leaving `op` unspecified is what
 * makes the ingest seam quarantine them as non-model instead of filing them
 * as unclaimed. The earlier `Session.` test missed the Runner/Step/Projector
 * spellings — those names begin `SessionRunner`, not `Session` followed by a
 * dot.
 */
const APP_SPAN_PREFIXES = [
  'Session.',
  'SessionRunner.',
  'SessionStep.',
  'SessionProjector.',
] as const

function isSessionAppSpan(span: RawSpan): boolean {
  return APP_SPAN_PREFIXES.some((prefix) => span.name.startsWith(prefix))
}

function yieldsToAnotherHarness(attributes: Record<string, unknown>): boolean {
  if (hasNamespace(attributes, FOREIGN_VENDOR_NAMESPACES)) return true
  if (attributes['gen_ai.agent.name'] === 'antigravity') return true
  if (attributes['gen_ai.system'] === 'gemini') return true
  return false
}

function attributeText(value: unknown): string | undefined {
  if (typeof value === 'string' && value !== '') return value
  if (value !== null && value !== undefined && typeof value === 'object') return JSON.stringify(value)
  return undefined
}

/**
 * Text of one `{role, content}` message — the shape both the legacy
 * `ai.prompt.messages` entry and an OpenInference `llm.input_messages` entry
 * carry. `content` is a string or a part list.
 */
function messageText(message: unknown): { role: string | undefined; text: string } | undefined {
  if (message === null || typeof message !== 'object') return undefined
  const record = message as Record<string, unknown>
  const role = typeof record.role === 'string' ? record.role : undefined
  const content = record.content
  if (typeof content === 'string') return { role, text: content }
  if (Array.isArray(content)) {
    const texts: string[] = []
    for (const piece of content) {
      if (typeof piece === 'string' && piece !== '') {
        texts.push(piece)
        continue
      }
      if (piece === null || typeof piece !== 'object') continue
      const part = piece as Record<string, unknown>
      if (typeof part.text === 'string' && part.text !== '') texts.push(part.text)
      else if (typeof part.content === 'string' && part.content !== '') texts.push(part.content)
    }
    return { role, text: texts.join('\n') }
  }
  // `content` is a string or a part list. A missing or other value is not
  // conversation text; stringifying the message would file role and metadata
  // as if the user had sent them.
  return undefined
}

/**
 * Legacy AI SDK buckets. System-role messages are the system prompt; every
 * other role stays conversation. Tools and `ai.toolCall.result` are their
 * own buckets. `gen_ai.*` parts, when any, stay ahead of these.
 */
function legacyAiParts(attributes: Record<string, unknown>, startOrder: number): ContentPart[] {
  const parts: ContentPart[] = []
  let order = startOrder
  const nextOrder = () => order++

  const messagesRaw = attributes[LEGACY_MESSAGES_KEY]
  const messagesText = attributeText(messagesRaw)
  const parsedMessages = parseStructured(messagesRaw)
  if (Array.isArray(parsedMessages)) {
    for (const message of parsedMessages) {
      const extracted = messageText(message)
      if (extracted === undefined || extracted.text === '') continue
      parts.push({
        part: extracted.role === 'system' ? 'system_prompt' : 'conversation_history',
        text: extracted.text,
        order: nextOrder(),
      })
    }
  } else if (messagesText !== undefined) {
    parts.push({ part: 'conversation_history', text: messagesText, order: nextOrder() })
  }

  const tools = attributeText(attributes[LEGACY_TOOLS_KEY])
  if (tools !== undefined) {
    parts.push({ part: 'tool_definitions', text: tools, order: nextOrder() })
  }

  const toolResult = attributeText(attributes[LEGACY_TOOL_RESULT_KEY])
  if (toolResult !== undefined) {
    parts.push({ part: 'tool_result_content', text: toolResult, order: nextOrder() })
  }

  return parts
}

/**
 * The OpenInference message list. `llm.input_messages` is the same
 * `{role, content}` JSON array the legacy `ai.prompt.messages` carried, so
 * the same extraction applies and system-role entries become the system
 * prompt. `input.value` is deliberately not bucketed: it is the span's raw
 * input, which for a model span repeats the message text and for the app
 * spans around it is a serialized payload rather than conversation — the
 * same rule that keeps pi's `pi.llm.request.payload` off content buckets.
 */
function openInferenceParts(attributes: Record<string, unknown>, startOrder: number): ContentPart[] {
  const parts: ContentPart[] = []
  let order = startOrder
  const parsed = parseStructured(attributes[OPENINFERENCE_INPUT_MESSAGES_KEY])
  if (!Array.isArray(parsed)) return parts
  for (const message of parsed) {
    const extracted = messageText(message)
    if (extracted === undefined || extracted.text === '') continue
    parts.push({
      part: extracted.role === 'system' ? 'system_prompt' : 'conversation_history',
      text: extracted.text,
      order: order++,
    })
  }
  return parts
}

function cacheClass(
  attributes: Record<string, unknown>,
  keys: readonly string[],
  absentReason: string,
): 'measured' | ReturnType<typeof notMeasurable> {
  // A present key is not a measurement: `readCounter` folds an unreadable value
  // ('n/a', null, '') to 0, so an `in`-check here declared a garbage counter
  // measured and published a fabricated zero. Presence has to come from the
  // same parse the counter itself uses; a reported `0` still parses.
  return readOptionalCounter(attributes, keys) !== undefined ? 'measured' : notMeasurable(absentReason)
}

/**
 * Whether the span reported the counters a dimension is built from. A value
 * is only absent when the vocabulary carries no such key: a reported `0` is
 * a measured zero and must stay one.
 *
 * The plugin observes request assembly, not completion, so an envelope span
 * names a model and carries no usage at all. The token identity still stores
 * 0 for that shape, so the absence has to travel as its own declaration —
 * the session projection reads it to publish the totals as `not_measurable`
 * instead of a fabricated zero.
 */
function counterAvailability(inputReported: boolean, outputReported: boolean): Measurability {
  return {
    token_usage: inputReported
      ? 'measured'
      : notMeasurable('OpenCode telemetry did not report input token usage for this span.'),
    output: outputReported
      ? 'measured'
      : notMeasurable('OpenCode telemetry did not report output token usage for this span.'),
  }
}

/**
 * Counters from the vocabulary the span actually speaks: OpenInference
 * `llm.token_count.*`, or the AI SDK's `ai.usage.*` / `gen_ai.usage.*`. Cache
 * classes stay `not_measurable` unless the span actually carries them: a
 * missing counter is not a measured zero, even though the token identity
 * still stores 0.
 */
function applyUsage(record: ReturnType<typeof baseRecord>, attributes: Record<string, unknown>): void {
  if (hasOpenInferenceUsage(attributes)) {
    applyOpenInferenceUsage(record, attributes)
    return
  }
  const gen = readUsageCounters(attributes)
  const legacyInput = readOptionalCounter(attributes, LEGACY_INPUT_KEYS)
  const legacyOutput = readOptionalCounter(attributes, LEGACY_OUTPUT_KEYS)
  // The output value is read with the same parser as its presence: `readCounter`
  // inside `readUsageCounters` decides the NUMBER, so an `in`-check here would
  // declare an unreadable value `measured` and stamp the folded 0 as a figure.
  const genOutput = readOptionalCounter(attributes, OUTPUT_TOKEN_KEYS)
  const inputPresent = gen.inputPresent || legacyInput !== undefined
  // Cache classes are input: a span that reported only a cache counter still
  // measured the input it reads.
  const cacheReadPresent = readOptionalCounter(attributes, CACHE_READ_KEYS) !== undefined
  const cacheCreationPresent = readOptionalCounter(attributes, CACHE_CREATION_KEYS) !== undefined
  const outputPresent = genOutput !== undefined || legacyOutput !== undefined

  record.tokens = convertInclusiveCounts({
    input: gen.inputPresent ? gen.input : (legacyInput ?? 0),
    cacheRead: gen.cacheRead,
    cacheCreation: gen.cacheCreation,
    output: genOutput ?? legacyOutput ?? 0,
    inputPresent,
    ...(gen.reasoning !== 0 ? { reasoning: gen.reasoning } : {}),
  })
  record.measurability = {
    ...record.measurability,
    ...counterAvailability(inputPresent || cacheReadPresent || cacheCreationPresent, outputPresent),
    cache_read: cacheClass(attributes, CACHE_READ_KEYS, 'OpenCode did not export a cache read counter.'),
    cache_creation: cacheClass(
      attributes,
      CACHE_CREATION_KEYS,
      'OpenCode did not export a cache creation counter.',
    ),
  }
}

/**
 * OpenInference counters, converted through the cache-EXCLUSIVE convention —
 * pi's shape, not the inclusive one the AI-SDK vocabularies use. The live
 * span reports `llm.token_count.prompt` 616 beside
 * `prompt_details.cache_read` 159744 and `llm.token_count.total` 160747, and
 * 616 + 159744 + 0 (`cache_write`) + 242 (`completion`) + 145
 * (`completion_details.reasoning`) is exactly that total. The prompt counter
 * therefore carries fresh input alone and the cache classes are its disjoint
 * siblings, so the exclusive conversion takes the prompt as fresh and
 * reassembles `reportedInput`. Copilot's inclusive conversion would subtract
 * the cache read from the prompt and land on a negative fresh input, which
 * `validateTokens` rejects outright (issue #193's failure mode).
 */
function applyOpenInferenceUsage(
  record: ReturnType<typeof baseRecord>,
  attributes: Record<string, unknown>,
): void {
  const input = readOptionalCounter(attributes, OPENINFERENCE_INPUT_KEYS)
  const cacheRead = readOptionalCounter(attributes, OPENINFERENCE_CACHE_READ_KEYS)
  const cacheCreation = readOptionalCounter(attributes, OPENINFERENCE_CACHE_CREATION_KEYS)
  const output = readOptionalCounter(attributes, OPENINFERENCE_OUTPUT_KEYS)
  const reasoning = readOptionalCounter(attributes, OPENINFERENCE_REASONING_KEYS)

  record.tokens = exclusiveConvention({
    input: input ?? 0,
    cacheRead: cacheRead ?? 0,
    cacheCreation: cacheCreation ?? 0,
    output: output ?? 0,
    ...(reasoning !== undefined && reasoning !== 0 ? { reasoning } : {}),
  })
  record.measurability = {
    ...record.measurability,
    ...counterAvailability(
      input !== undefined || cacheRead !== undefined || cacheCreation !== undefined,
      output !== undefined,
    ),
    cache_read: cacheClass(
      attributes,
      OPENINFERENCE_CACHE_READ_KEYS,
      'OpenCode did not export a cache read counter.',
    ),
    cache_creation: cacheClass(
      attributes,
      OPENINFERENCE_CACHE_CREATION_KEYS,
      'OpenCode did not export a cache creation counter.',
    ),
  }
}

/**
 * The plugin envelope mapping (T1). A supported version stamps the envelope
 * session and maps the turn onto its operation: the tool marker wins over
 * the model marker, as `gen_ai.tool.name` does over a model vote. The prompt
 * text buckets as conversation history. Any other version leaves the record
 * untouched — `detect` scores those spans 0, so this path only runs for the
 * supported contract, and anything else keeps `unspecified` for the
 * non-model quarantine.
 */
function applyEnvelope(record: ReturnType<typeof baseRecord>, raw: RawSpan): void {
  if (!isSupportedOpencodeEnvelope(raw)) return
  const sessionId = opencodeEnvelopeSessionId(raw.attributes)
  if (sessionId !== undefined) record.sessionId = sessionId
  if (isOpencodeEnvelopeTool(raw.attributes)) {
    record.op = 'tool.invoke'
  } else if (isOpencodeEnvelopeModel(raw.attributes) && record.op !== 'tool.invoke') {
    record.op = 'llm.invoke'
  }
  const prompt = opencodeEnvelopePromptText(raw.attributes)
  if (prompt !== undefined) {
    appendPart(record, { part: 'conversation_history', text: prompt })
  }
  // Envelope v2 context content (plan 2026-10-23 §5-T5, D4): the assembled
  // system prompt buckets as `system_prompt` and the serialized tool
  // definitions as `tool_definitions`, the `legacyAiParts` pattern, ordered
  // after every existing part. Counters are never fabricated here — a context
  // span with no usage keys keeps zero tokens with not-measurable cache classes.
  const system = opencodeEnvelopeContextSystem(raw.attributes)
  if (system !== undefined) {
    appendPart(record, { part: 'system_prompt', text: system })
  }
  const tools = opencodeEnvelopeContextTools(raw.attributes)
  if (tools !== undefined) {
    appendPart(record, { part: 'tool_definitions', text: tools })
  }
}

/** Appends a content part after every existing part, recomputing order and content. */
function appendPart(record: ReturnType<typeof baseRecord>, part: Omit<ContentPart, 'order'>): void {
  const existing = record.parts ?? []
  const order = existing.reduce((max, entry) => Math.max(max, (entry.order ?? 0) + 1), 0)
  const parts: ContentPart[] = [...existing, { ...part, order }]
  record.parts = parts
  record.content = contentFromParts(parts)
}

export const opencodeAdapter: HarnessAdapter = {
  name: OPENCODE_HARNESS,
  namespaces: ['ai', 'gen_ai', 'openinference', 'opencode'],

  detect(span) {
    // Another harness's fingerprint wins outright. `service.name` is not
    // evidence and is not read.
    if (yieldsToAnotherHarness(span.attributes)) return 0
    // The plugin envelope is claimed only under the supported contract (T1):
    // an unknown or missing version scores 0 so no vote can claim it, and
    // the ingest seam quarantines it with the unknown-version reason instead.
    // A supported envelope scores 1 whatever its markers: `normalize` maps
    // model and tool markers onto their operations, and a marker-less span
    // keeps `unspecified` so ingest quarantines it as non-model.
    if (isOpencodeEnvelopeSpan(span)) return isSupportedOpencodeEnvelope(span) ? 1 : 0
    if (isModelSpan(span)) return 1
    // Any other OpenInference span is claimed so its group's confidence holds
    // and ingest quarantines it as non-model rather than filing an unclaimed
    // span of unknown origin. Its operation stays unspecified.
    if (hasOpenInference(span.attributes)) return 1
    // Claimed so ingest quarantines an unspecified operation as non-model.
    if (isSessionAppSpan(span)) return 1
    return 0
  },

  relevance(span) {
    if (yieldsToAnotherHarness(span.attributes)) return 0
    if (isModelSpan(span)) return 1
    if (isSupportedOpencodeEnvelope(span) && isOpencodeEnvelopeModel(span.attributes)) return 1
    return 0
  },

  normalize(raw) {
    const record = baseRecord(this, raw)
    if (
      LEGACY_MESSAGES_KEY in raw.attributes ||
      LEGACY_TOOLS_KEY in raw.attributes ||
      LEGACY_TOOL_RESULT_KEY in raw.attributes
    ) {
      const existing = record.parts ?? []
      const start = existing.reduce((max, part) => Math.max(max, (part.order ?? 0) + 1), 0)
      const parts = [...existing, ...legacyAiParts(raw.attributes, start)]
      record.parts = parts
      record.content = contentFromParts(parts)
    }
    if (isOpenInferenceModel(raw.attributes)) {
      const existing = record.parts ?? []
      const start = existing.reduce((max, part) => Math.max(max, (part.order ?? 0) + 1), 0)
      const parts = [...existing, ...openInferenceParts(raw.attributes, start)]
      record.parts = parts
      record.content = contentFromParts(parts)
    }
    applyUsage(record, raw.attributes)
    applyEnvelope(record, raw)
    // Bridge the model id onto a key downstream already reads
    // (`gen_ai.request.model` is in `MODEL_KEYS`, `MODEL_IDENTITY_KEYS` and
    // `PROJECTED_ATTRIBUTE_KEYS`). Native `llm.model_name` wins over envelope
    // `opencode.model.id`; an already-canonical model or an absent model
    // bridges nothing, so absence stays absence and never becomes a zero.
    const bridged = bridgedModelId(raw.attributes)
    if (bridged !== undefined) {
      const rawAttributes =
        record.raw !== null && typeof record.raw === 'object' && !Array.isArray(record.raw)
          ? (record.raw as Record<string, unknown>)
          : {}
      record.raw = { ...rawAttributes, 'gen_ai.request.model': bridged }
    }
    // The app spans keep `unspecified`, which the ingest seam quarantines as
    // non-model. `llm.invoke` is the model call; an OpenInference `TOOL` span
    // is the tool call. A `gen_ai.tool.name` still wins over a model vote.
    if (isModelSpan(raw) && record.op !== 'tool.invoke') record.op = 'llm.invoke'
    else if (record.op === 'unspecified' && isOpenInferenceTool(raw.attributes)) {
      record.op = 'tool.invoke'
    }
    return record
  },

  group: traceGroup,
  resolveRoot: resolveRootByParentage,
  validate: validateRecordTokens,

  /**
   * Cache read and cache creation are per-span: present counters are
   * measured, absent ones are `not_measurable` on the record. They are not
   * a standing export gap, so they are not listed here.
   */
  unexportedMetrics() {
    return []
  },
}
