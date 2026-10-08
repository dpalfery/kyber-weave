// OpenCode harness adapter (Seam 3, P2.4b).
//
// OpenCode's model spans are the Vercel AI SDK's telemetry, in two
// vocabularies: legacy `ai.*` and the current `gen_ai.*` semconv. `gen_ai.*`
// content is already mapped by `canonicalParts`; this adapter only adds the
// legacy buckets and the counters. Attribution never reads `service.name`
// (R6.2) — that value arrives as `RawSpan.source` and is not consulted.
//
// App spans are not model calls. `sql.execute` and `http.server GET` are
// already structural in the ingest seam, so they stay unclaimed. `Session.*`
// is not: the seam only calls a claimed span non-model when its operation is
// unspecified. Detecting those spans here, and leaving `op` unspecified, is
// what quarantines them as non-model without another file.

import type { HarnessAdapter, RawSpan } from './base.js'
import { resolveRootByParentage, traceGroup } from './base.js'
import {
  CACHE_CREATION_KEYS,
  CACHE_READ_KEYS,
  OUTPUT_TOKEN_KEYS,
  baseRecord,
  convertInclusiveCounts,
  hasNamespace,
  readOptionalCounter,
  readUsageCounters,
  validateRecordTokens,
} from './copilot.js'
import { contentFromParts, notMeasurable, type ContentPart } from '../types.js'

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
  return hasLegacyAi(span.attributes) || isGenAiSdkModel(span.attributes)
}

/** OpenCode's Effect app spans (`Session.load`, `Session.create`, …). */
function isSessionAppSpan(span: RawSpan): boolean {
  return span.name.startsWith('Session.')
}

function yieldsToAnotherHarness(attributes: Record<string, unknown>): boolean {
  if (hasNamespace(attributes, FOREIGN_VENDOR_NAMESPACES)) return true
  if (attributes['gen_ai.agent.name'] === 'antigravity') return true
  if (attributes['gen_ai.system'] === 'gemini') return true
  return false
}

function parseJson(value: unknown): unknown {
  if (typeof value !== 'string') return value
  try {
    return JSON.parse(value)
  } catch {
    return undefined
  }
}

function attributeText(value: unknown): string | undefined {
  if (typeof value === 'string' && value !== '') return value
  if (value !== null && value !== undefined && typeof value === 'object') return JSON.stringify(value)
  return undefined
}

/** Text of one legacy `ai.prompt.messages` entry. `content` is a string or a part list. */
function legacyMessageText(message: unknown): { role: string | undefined; text: string } | undefined {
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
  return { role, text: JSON.stringify(message) }
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
  const parsedMessages = parseJson(messagesRaw)
  if (Array.isArray(parsedMessages)) {
    for (const message of parsedMessages) {
      const extracted = legacyMessageText(message)
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

function cacheClass(
  attributes: Record<string, unknown>,
  keys: readonly string[],
  absentReason: string,
): 'measured' | ReturnType<typeof notMeasurable> {
  return keys.some((key) => key in attributes) ? 'measured' : notMeasurable(absentReason)
}

/**
 * Counters from `ai.usage.*` or `gen_ai.usage.*`. Cache classes stay
 * `not_measurable` unless the span actually carries them: a missing counter
 * is not a measured zero, even though the token identity still stores 0.
 */
function applyUsage(record: ReturnType<typeof baseRecord>, attributes: Record<string, unknown>): void {
  const gen = readUsageCounters(attributes)
  const legacyInput = readOptionalCounter(attributes, LEGACY_INPUT_KEYS)
  const legacyOutput = readOptionalCounter(attributes, LEGACY_OUTPUT_KEYS)
  const genOutputPresent = OUTPUT_TOKEN_KEYS.some((key) => key in attributes)
  const inputPresent = gen.inputPresent || legacyInput !== undefined

  record.tokens = convertInclusiveCounts({
    input: gen.inputPresent ? gen.input : (legacyInput ?? 0),
    cacheRead: gen.cacheRead,
    cacheCreation: gen.cacheCreation,
    output: genOutputPresent ? gen.output : (legacyOutput ?? 0),
    inputPresent,
    ...(gen.reasoning !== 0 ? { reasoning: gen.reasoning } : {}),
  })
  record.measurability = {
    ...record.measurability,
    cache_read: cacheClass(attributes, CACHE_READ_KEYS, 'OpenCode did not export a cache read counter.'),
    cache_creation: cacheClass(
      attributes,
      CACHE_CREATION_KEYS,
      'OpenCode did not export a cache creation counter.',
    ),
  }
}

export const opencodeAdapter: HarnessAdapter = {
  name: OPENCODE_HARNESS,
  namespaces: ['ai', 'gen_ai', 'opencode'],

  detect(span) {
    // Another harness's fingerprint wins outright. `service.name` is not
    // evidence and is not read.
    if (yieldsToAnotherHarness(span.attributes)) return 0
    if (isModelSpan(span)) return 1
    // Claimed so ingest quarantines an unspecified operation as non-model.
    if (isSessionAppSpan(span)) return 1
    return 0
  },

  relevance(span) {
    if (yieldsToAnotherHarness(span.attributes)) return 0
    if (isModelSpan(span)) return 1
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
    applyUsage(record, raw.attributes)
    // Session.* and other non-model spans keep `unspecified`, which the
    // ingest seam quarantines as non-model. A tool name still wins.
    if (isModelSpan(raw) && record.op !== 'tool.invoke') record.op = 'llm.invoke'
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
