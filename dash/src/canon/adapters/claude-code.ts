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
 * observed `system`, `tools`, `messages`, and tool `server` fields are
 * interpreted. Tool names are not split because their spelling is not
 * server evidence. Message blocks bucket on block TYPE, never role —
 * the same rule copilot.ts `messageParts` applies, so one bucketing
 * covers both harnesses (B5) — and no `instruction_context` is invented
 * for them: the sender does not mark one.
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

  if (Array.isArray(request.messages)) {
    for (const message of request.messages) {
      if (message === null || typeof message !== 'object') continue
      const content = (message as Record<string, unknown>).content
      const role = messageRole((message as Record<string, unknown>).role)
      const withRole = (part: ContentPart): ContentPart => (role === undefined ? part : { ...part, role })
      if (typeof content === 'string') {
        parts.push(withRole({ part: 'conversation_history', text: content }))
        continue
      }
      if (!Array.isArray(content)) {
        // A message with no readable content list is still turn evidence;
        // mirror copilot.ts and keep the whole message verbatim.
        parts.push(withRole({ part: 'conversation_history', text: JSON.stringify(message) }))
        continue
      }
      for (const block of content) {
        if (block === null || typeof block !== 'object') continue
        const blockRecord = block as Record<string, unknown>
        const text = contentBlockText(blockRecord)
        if (text === undefined) continue
        parts.push(
          withRole({
            part: blockRecord.type === 'tool_result' ? 'tool_result_content' : 'conversation_history',
            text,
          }),
        )
      }
    }
  }
  return parts
}

/** The message role when it is one of the three the API defines; anything else is no role. */
function messageRole(value: unknown): ContentPart['role'] {
  return value === 'system' || value === 'user' || value === 'assistant' ? value : undefined
}

/**
 * Why a request body yielded no parts, when the failure is the sender's.
 *
 * A string body that will not parse earns a reason, and which reason depends
 * on what the body still looks like. Claude Code cuts inline bodies at
 * `CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH` (61,440 UTF-16 units by default), and
 * a body cut that way is a broken-off JSON document: closing the strings and
 * brackets the cut left open turns it back into something that parses. Only
 * such a body points at the length knob — a balanced document that fails
 * anyway (`{oops}`) or one with an extra closer was never valid JSON at any
 * length, and no knob would fix it: those get the malformed reason. A body
 * that parsed — or never needed parsing — reports nothing: an empty part
 * list from a well-formed body is a real answer, not a failure.
 */
export function claudeApiRequestBodyFailureReason(body: unknown): string | undefined {
  if (typeof body !== 'string') return undefined
  try {
    JSON.parse(body)
    return undefined
  } catch {
    const trimmed = body.trim()
    if (
      (trimmed.startsWith('{') || trimmed.startsWith('[')) &&
      parsesWithClosersRestored(trimmed)
    ) {
      return 'The Claude Code request body did not parse — it was likely truncated at the sender limit. Set CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH to raise it.'
    }
    return 'The Claude Code request body did not parse as JSON — the sender emitted something that was never a request document, so there is nothing to reassemble.'
  }
}

/**
 * Would the body parse if the cut's open strings and brackets were closed?
 * A sender-side truncation ends mid-structure by construction, so restoring
 * the missing closers reassembles a document; garbage shaped like one does
 * not. The scan tracks string state so bracket characters inside strings
 * never count, and an unmatched closer (pop on an empty stack) or a body
 * that stays unparseable after completion answers no.
 */
function parsesWithClosersRestored(body: string): boolean {
  const closers: string[] = []
  let inString = false
  let escaped = false
  for (const ch of body) {
    if (inString) {
      if (escaped) escaped = false
      else if (ch === '\\') escaped = true
      else if (ch === '"') inString = false
      continue
    }
    if (ch === '"') inString = true
    else if (ch === '{' || ch === '[') closers.push(ch === '{' ? '}' : ']')
    else if (ch === '}' || ch === ']') {
      if (closers.pop() === undefined) return false
    }
  }
  if (closers.length === 0 && !inString) return false
  let completed = body
  if (inString) completed += '"'
  for (let i = closers.length - 1; i >= 0; i--) completed += closers[i]
  try {
    JSON.parse(completed)
    return true
  } catch {
    return false
  }
}

/**
 * The text one message content block contributes, or undefined when it
 * contributes none. Thinking blocks contribute nothing — the sender
 * redacts them, and a `redacted_thinking` block's `data` is the opaque
 * redaction payload rather than reasoning text. An `image` or `document`
 * block collapses to its `[<type> block]` placeholder: the bytes are
 * payload, never content.
 *
 * A block may nest its own `content` list — a `tool_result` can carry
 * image sub-blocks — so nested blocks run through this same mapping and
 * the placeholder and skip rules hold at every depth. Anything else keeps
 * its `text`/`content` string; an unanticipated shape carrying a `data`
 * field — or a `source` carrying one, at any JSON type, string or not —
 * still collapses to a placeholder, because bytes are payload on any block
 * shape, not only on the ones the mapping anticipated. Only a block with
 * neither collapses to the whole block as JSON; a `tool_use` takes that
 * last path (B5).
 */
function contentBlockText(block: Record<string, unknown>): string | undefined {
  const type = block.type
  if (type === 'thinking' || type === 'redacted_thinking') return undefined
  if (type === 'image' || type === 'document') return `[${type} block]`
  if (typeof block.text === 'string') return block.text
  const content = block.content
  if (typeof content === 'string') return content
  if (content !== null && typeof content === 'object') {
    const nested = (Array.isArray(content) ? content : [content])
      .filter((sub): sub is Record<string, unknown> => sub !== null && typeof sub === 'object')
      .map(contentBlockText)
      .filter((text): text is string => text !== undefined)
    return nested.length > 0 ? nested.join('\n') : undefined
  }
  const source = block.source
  const sourceData = source !== null && typeof source === 'object' ? (source as Record<string, unknown>).data : undefined
  if ((block.data !== undefined && block.data !== null) || (sourceData !== undefined && sourceData !== null)) {
    return `[${typeof type === 'string' ? type : 'binary'} block]`
  }
  return JSON.stringify(block)
}

/**
 * B2's interaction-span recognition, offered as span knowledge for the
 * ingest path: the `span.type` marker or the emitted name, either one alone
 * sufficient — the live packet carries both, and a single rename must not
 * blind the receiver. The claude-code attribution gate stays with the
 * caller; this predicate says nothing about a span another harness emitted.
 */
export function isClaudeInteractionSpan(span: {
  name: string
  attributes: Record<string, unknown>
}): boolean {
  return (
    span.attributes['span.type'] === 'interaction' ||
    span.name === 'claude_code.interaction'
  )
}

/**
 * The literal the sender substitutes for `user_prompt` when it exports
 * without `OTEL_LOG_USER_PROMPTS` — redaction arrives as text, not as a
 * missing key (F1).
 */
const REDACTED_USER_PROMPT = '<REDACTED>'

/**
 * What an interaction span's `user_prompt` carried (Q4): text to attach, or
 * a redaction to declare. An absent or non-string value answers undefined —
 * the span named nothing, which is not evidence either way.
 */
export type ClaudeUserPrompt = { redacted: true } | { redacted: false; text: string }

export function claudeUserPrompt(value: unknown): ClaudeUserPrompt | undefined {
  if (typeof value !== 'string' || value === '') return undefined
  return value === REDACTED_USER_PROMPT ? { redacted: true } : { redacted: false, text: value }
}

/**
 * Why a redacted `user_prompt` yields no `conversation_history` (Q2). The
 * sender has the text and withholds it, so the declaration names the exact
 * opt-in that would release it — a more honest answer than the generic
 * "does not export" the adapter stamps at normalize time.
 */
export const REDACTED_USER_PROMPT_REASON =
  'Claude Code sent the user prompt redacted. Set OTEL_LOG_USER_PROMPTS=1 on the sender to export it.'

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
    // Claude Code names a tool call `claude_code.tool` with `span.type=tool` and
    // `tool_name`, not the GenAI `gen_ai.tool.name` canonicalOp keys on. Without
    // this the call is `unspecified`, the session reports zero tool calls, and
    // every offered tool reads as never called. The child `tool.execution`
    // span is the same call and is left alone so it is not counted twice.
    if (
      record.op === 'unspecified' &&
      raw.attributes['span.type'] === 'tool' &&
      typeof raw.attributes.tool_name === 'string' &&
      raw.attributes.tool_name !== ''
    ) {
      record.op = 'tool.invoke'
    }
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
   * zero (R7.6, R10.1) — the span itself reads "not measurable" whether or
   * not content reaches the record by another route. That route is the
   * `api_request_body` log event parsed above when the sender opts in
   * (B3/B5); the on-disk transcripts fill conversation and tool results
   * too, but never carry a system prompt or tool definitions.
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
