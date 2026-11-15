// Hook-to-span mapping for the Factory (Droid) capture plugin (contract 1): the eight documented
// hook events onto the three `factory.*` span names.
//
// This module is pure. It says how a hook payload looks on the wire and nothing else: the hook
// runner (a later task) decides which hook fired and when, the consent gate decides whether
// content may travel, and the transport posts the span. Every key, span name and id comes from
// `contract.ts`, the one home of the contract-1 strings.
//
// Absence is absence. A field the payload does not carry is never written as 0, '' or null: the
// attribute is left off the wire entirely. A tool outcome is only ever what the payload states,
// and a byte count is only ever the size of the value the payload carries.
//
// An event the payload cannot identify yields no span at all. `SubagentStop` is one: Factory's
// documented payload for it names no child session id, and the common `session_id` is the session
// the hook fires in — the parent's, for a subagent's stop. A span there would name the parent as
// the child, so none is written.

import {
  FACTORY_CONTRACT_VERSION,
  FACTORY_KEYS,
  SPAN_NAMES,
  spanIdFor,
  traceIdFor,
  type FactorySpanKind,
  type FactoryTurnRole,
} from './contract.js'

/**
 * An OTLP/JSON attribute value. Integers ride as decimal strings, as the OTLP JSON mapping
 * requires.
 */
export type FactoryWireValue = { stringValue: string } | { intValue: string } | { doubleValue: number }

/** One attribute of a mapped span. */
export type FactoryWireAttribute = { key: string; value: FactoryWireValue }

/**
 * One mapped span. The transport (a later task) wraps these into the OTLP request body; this
 * module does not build the body, and it stamps no time the hook payload does not carry.
 */
export type FactoryWireSpan = {
  traceId: string
  spanId: string
  name: string
  kind: 'SPAN_KIND_INTERNAL'
  attributes: FactoryWireAttribute[]
}

/** What the caller allows the mapper to write. */
export type FactoryMapOptions = {
  /**
   * Whether prompt text may travel on the wire. The consent gate owns this flag and the mapper
   * only obeys it: the prompt text is written when it is true and left off the wire when it is
   * not.
   */
  contentAllowed: boolean
}

/**
 * The eight hook events Factory documents. Two of them yield no span in contract 1: `PreCompact`,
 * because the contract names no span for a compaction, and `SubagentStop`, because the documented
 * payload carries no child session id to anchor one on.
 */
export const FACTORY_HOOK_EVENTS = [
  'SessionStart',
  'UserPromptSubmit',
  'PreToolUse',
  'PostToolUse',
  'Stop',
  'SubagentStop',
  'SessionEnd',
  'PreCompact',
] as const

/** Prompt text on a turn span is capped at this many UTF-8 bytes. */
export const MAX_PROMPT_BYTES = 64 * 1024

/** Appended to a prompt that hit the cap, so a reader can tell it was cut. */
const PROMPT_TRUNCATION_MARKER = '\n[kyberdash: truncated]'

// ---------------------------------------------------------------------------------------------
// Attribute helpers

function stringAttribute(key: string, value: string): FactoryWireAttribute {
  return { key, value: { stringValue: value } }
}

function countAttribute(key: string, value: number): FactoryWireAttribute {
  return { key, value: { intValue: String(Math.trunc(value)) } }
}

function contractAttribute(): FactoryWireAttribute {
  return stringAttribute(FACTORY_KEYS.contractVersion, FACTORY_CONTRACT_VERSION)
}

/** The value when it is a string with something in it; otherwise absent. */
function textOf(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined
}

/**
 * The session id of a payload. An absent session id still names a span, so the ids are derived
 * from the empty string rather than invented: the row then carries no session id attribute.
 */
function sessionIdOf(payload: Record<string, unknown>): string {
  return textOf(payload['session_id']) ?? ''
}

/**
 * The UTF-8 size of `JSON.stringify(value)`, the size the value takes on the wire. Absent when
 * the value has no JSON form: a value the payload does not carry, a null, or a value JSON cannot
 * serialize (a cycle, a BigInt) has no size to report.
 */
function jsonBytes(value: unknown): number | undefined {
  if (value === undefined || value === null) return undefined
  try {
    const text = JSON.stringify(value)
    return text === undefined ? undefined : Buffer.byteLength(text, 'utf8')
  } catch {
    return undefined
  }
}

/**
 * The prompt text as it travels on the wire, capped at `MAX_PROMPT_BYTES` UTF-8 bytes. A longer
 * prompt is cut on a character boundary and marked, never sent whole.
 */
function capPrompt(text: string): string {
  if (Buffer.byteLength(text, 'utf8') <= MAX_PROMPT_BYTES) return text
  const budget = MAX_PROMPT_BYTES - Buffer.byteLength(PROMPT_TRUNCATION_MARKER, 'utf8')
  // Every UTF-16 unit takes at least one byte, so the first `budget` units already cover every
  // byte that can be kept and the rest of a huge prompt is never encoded.
  let head = text.slice(0, budget)
  // A high surrogate left at the end, whose pair the cut would have split, is dropped.
  const last = head.charCodeAt(head.length - 1)
  if (last >= 0xd800 && last <= 0xdbff) head = head.slice(0, -1)
  const bytes = Buffer.from(head, 'utf8')
  let end = Math.min(budget, bytes.length)
  // A byte of the form 10xxxxxx continues a character; step back to the byte that starts it.
  while (end > 0 && end < bytes.length && ((bytes[end] ?? 0) & 0xc0) === 0x80) end -= 1
  return `${bytes.toString('utf8', 0, end)}${PROMPT_TRUNCATION_MARKER}`
}

/**
 * One mapped span. The trace and the span id are derived from the session id and the anchor of
 * the unit of work the span covers, so the two hooks of one unit share both ids. A role
 * qualifies the span inside its kind, and only the span id takes it: the two spans of one turn
 * stay in one trace and land on two rows.
 */
function span(
  kind: FactorySpanKind,
  sessionId: string,
  anchor: string,
  attributes: FactoryWireAttribute[],
  role: FactoryTurnRole | '' = '',
): FactoryWireSpan {
  return {
    traceId: traceIdFor(sessionId, anchor),
    spanId: spanIdFor(kind, sessionId, anchor, role),
    name: SPAN_NAMES[kind],
    kind: 'SPAN_KIND_INTERNAL',
    attributes,
  }
}

// ---------------------------------------------------------------------------------------------
// Events

/**
 * A session span: the session itself, anchored at the session id. `SessionStart` and `SessionEnd`
 * are the two ends of one session, so both carry the session's ids.
 */
function sessionSpan(payload: Record<string, unknown>): FactoryWireSpan {
  const sessionId = sessionIdOf(payload)
  const attributes: FactoryWireAttribute[] = [contractAttribute()]
  const session = textOf(payload['session_id'])
  if (session !== undefined) attributes.push(stringAttribute(FACTORY_KEYS.sessionId, session))
  const cwd = textOf(payload['cwd'])
  if (cwd !== undefined) attributes.push(stringAttribute(FACTORY_KEYS.cwd, cwd))
  return span('session', sessionId, sessionId, attributes)
}

/**
 * The turn span of a submitted prompt: the model invocation, anchored at the message id. The
 * operation is the contract's word for that invocation, not a value the payload states.
 *
 * The prompt carries the prompt role, so it is a span of its own: the prompt and the completion
 * of one turn share that turn's trace id, and a shared span id would let the completion replace
 * the prompt's row and take the prompt text and the model with it.
 */
function promptTurnSpan(
  payload: Record<string, unknown>,
  options: FactoryMapOptions,
): FactoryWireSpan {
  const sessionId = sessionIdOf(payload)
  const messageId = textOf(payload['message_id'])
  const attributes: FactoryWireAttribute[] = [contractAttribute()]
  const session = textOf(payload['session_id'])
  if (session !== undefined) attributes.push(stringAttribute(FACTORY_KEYS.sessionId, session))
  if (messageId !== undefined) attributes.push(stringAttribute(FACTORY_KEYS.messageId, messageId))
  attributes.push(stringAttribute(FACTORY_KEYS.turnOperation, 'llm.invoke'))
  if (options.contentAllowed) {
    const prompt = textOf(payload['prompt'])
    if (prompt !== undefined) {
      attributes.push(stringAttribute(FACTORY_KEYS.promptText, capPrompt(prompt)))
    }
  }
  const model = textOf(payload['model'])
  if (model !== undefined) attributes.push(stringAttribute(FACTORY_KEYS.modelId, model))
  return span('turn', sessionId, messageId ?? sessionId, attributes, 'prompt')
}

/**
 * The turn span of a finished turn, anchored at the message id. The contract names no operation
 * for the end of a turn, so none is written.
 *
 * The completion carries the completion role, so it is a span of its own: the prompt and the
 * completion of one turn share that turn's trace id, and a shared span id would let the
 * completion replace the prompt's row and take the prompt text and the model with it.
 */
function stopTurnSpan(payload: Record<string, unknown>): FactoryWireSpan {
  const sessionId = sessionIdOf(payload)
  const messageId = textOf(payload['message_id'])
  const attributes: FactoryWireAttribute[] = [contractAttribute()]
  const session = textOf(payload['session_id'])
  if (session !== undefined) attributes.push(stringAttribute(FACTORY_KEYS.sessionId, session))
  if (messageId !== undefined) attributes.push(stringAttribute(FACTORY_KEYS.messageId, messageId))
  return span('turn', sessionId, messageId ?? sessionId, attributes, 'completion')
}

/**
 * The tool span of one tool call, anchored at the call id, so the `PreToolUse` and the
 * `PostToolUse` of that call share a span id and a trace id. The outcome is written only for the
 * hook that carries it: a payload that states no outcome yields no outcome attribute.
 */
function toolSpan(payload: Record<string, unknown>, outcome: boolean): FactoryWireSpan {
  const sessionId = sessionIdOf(payload)
  const callId = textOf(payload['tool_call_id'])
  const attributes: FactoryWireAttribute[] = [contractAttribute()]
  const session = textOf(payload['session_id'])
  if (session !== undefined) attributes.push(stringAttribute(FACTORY_KEYS.sessionId, session))
  const toolName = textOf(payload['tool_name'])
  if (toolName !== undefined) attributes.push(stringAttribute(FACTORY_KEYS.toolName, toolName))
  if (callId !== undefined) attributes.push(stringAttribute(FACTORY_KEYS.toolCallId, callId))
  const inputBytes = jsonBytes(payload['tool_input'])
  if (inputBytes !== undefined) {
    attributes.push(countAttribute(FACTORY_KEYS.toolInputBytes, inputBytes))
  }
  if (outcome) {
    const status = textOf(payload['tool_status'])
    if (status !== undefined) attributes.push(stringAttribute(FACTORY_KEYS.toolStatus, status))
    const errorType = textOf(payload['tool_error_type'])
    if (errorType !== undefined) {
      attributes.push(stringAttribute(FACTORY_KEYS.toolErrorType, errorType))
    }
    const resultBytes = jsonBytes(payload['tool_response'])
    if (resultBytes !== undefined) {
      attributes.push(countAttribute(FACTORY_KEYS.toolResultBytes, resultBytes))
    }
  }
  return span('tool', sessionId, callId ?? sessionId, attributes)
}

// ---------------------------------------------------------------------------------------------
// Mapping

/**
 * Map one hook payload onto the spans it produces: one span for every documented event that names
 * one, and no span for an event the contract does not name — nor for one whose payload carries no
 * identity to anchor a span on.
 */
export function mapHookEvent(
  payload: Record<string, unknown>,
  options: FactoryMapOptions = { contentAllowed: false },
): FactoryWireSpan[] {
  const event = textOf(payload['hook_event_name'])
  if (event === undefined) return []
  switch (event) {
    case 'SessionStart':
      return [sessionSpan(payload)]
    case 'UserPromptSubmit':
      return [promptTurnSpan(payload, options)]
    case 'PreToolUse':
      return [toolSpan(payload, false)]
    case 'PostToolUse':
      return [toolSpan(payload, true)]
    case 'Stop':
      return [stopTurnSpan(payload)]
    case 'SubagentStop':
      // The documented payload names the task and its outcome, and no child session id: the
      // common `session_id` is the session the hook fires in, which for a subagent's stop is the
      // parent's. A span here would publish the parent as the child, so none is written.
      return []
    case 'SessionEnd':
      return [sessionSpan(payload)]
    case 'PreCompact':
      // Contract 1 names no span for a compaction, so none is written.
      return []
    default:
      // An event the contract does not know is not mapped; the caller decides whether to report it.
      return []
  }
}
