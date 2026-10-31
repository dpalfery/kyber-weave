// Wire mapping for the OpenCode V2 capture plugin (contract 3): finished request, step and tool
// records onto OTLP spans.
//
// This module is pure. The plugin (`plugin.ts`) decides WHAT happened and WHEN; this module only
// says how a finished record looks on the wire. Every key, span name and id comes from
// `contract.ts`, the one home of contract-3 strings. An absent measurement stays absent: a field
// the record does not carry is never written as 0 or ''. Content (prompt text, session label,
// system prompt, tool definitions, skills listing) is attached only when the caller hands it over,
// and the caller decides consent.

import {
  OPENCODE_CONTRACT_VERSION,
  OPENCODE_KEYS,
  SPAN_NAMES,
  spanIdFor,
  traceIdFor,
  type SpanKind,
} from './contract.js'
import { MAX_FIELD_BYTES, capUtf8 } from './skills-block.js'
import type { OtlpWireAttribute, OtlpWireSpan, OtlpWireStatus } from './transport/otlp-sender.js'

/** Prompt text on the request span is capped at this many UTF-8 bytes. */
export const MAX_PROMPT_BYTES = 64 * 1024

/**
 * Cap on each other content field (system prompt, tools JSON), in characters (UTF-16 units, at
 * most 3 bytes each in UTF-8). A host prompt or tool catalogue can be megabytes; content past the
 * cap is cut and ends with `CONTENT_TRUNCATION_MARKER`, never sent whole.
 */
export const MAX_CONTENT_CHARS = 262_144

/** Appended to a field that hit the cap, so a reader can tell it was cut. */
export const CONTENT_TRUNCATION_MARKER = '\n[kyberdash: truncated]'

/** Latest event time accepted, in epoch milliseconds: 9999-12-31T23:59:59.999Z. */
export const MAX_EPOCH_MS = 253_402_300_799_999

// ---------------------------------------------------------------------------------------------
// Records

/** How a request ended. `unknown` means no terminal execution event was seen. */
export type RequestOutcome = 'succeeded' | 'failed' | 'interrupted' | 'unknown'

export type RequestRecord = {
  sessionId: string
  /** The execution-started event id: the request's span anchor. */
  anchorId: string
  startMs: number
  endMs: number
  outcome: RequestOutcome
  /** The interrupt reason; only an interrupted request carries one. */
  reason?: string
  agent?: string
  version?: string
  /** Prompts seen in the execution; absent when none was observed. */
  promptCount?: number
  /** Content. Present only when consent allowed it. */
  promptText?: string
  label?: string
}

export type StepUsage = {
  input?: number
  output?: number
  reasoning?: number
  cacheRead?: number
  cacheWrite?: number
}

export type StepContent = {
  system?: string
  /** JSON array text of `[{name, description, input}]`. */
  tools?: string
  /** Skill ids listed in the system prompt's `<available_skills>` block. */
  skills?: readonly string[]
}

export type StepRecord = {
  sessionId: string
  /** The step's assistant message id: its span anchor, and the key its tools point at. */
  messageId: string
  /** The execution-started event id of the request this step belongs to, when that was seen. */
  requestAnchorId?: string
  startMs: number
  endMs: number
  agent?: string
  modelId?: string
  modelProvider?: string
  requestKind: string
  finish?: string
  usage: StepUsage
  /** Reported cost; 0 is a reported value and absent means the host reported none. */
  cost?: number
  skills: readonly string[]
  /** Content. Present only when consent allowed it. */
  content?: StepContent
}

export type ToolRecord = {
  sessionId: string
  callId: string
  /** The step's assistant message id; the tool's parent step is derived from it. */
  messageId?: string
  startMs: number
  endMs: number
  name?: string
  status: 'completed' | 'error'
  errorType?: string
  inputBytes?: number
  resultBytes?: number
  /** `hook` when start and end are the plugin's own hook clock rather than the host's events. */
  timing?: 'hook'
  skillId?: string
}

// ---------------------------------------------------------------------------------------------
// Attribute helpers

function str(key: string, value: string): OtlpWireAttribute {
  return { key, value: { stringValue: value } }
}

function count(key: string, value: number): OtlpWireAttribute {
  return { key, value: { intValue: String(Math.trunc(value)) } }
}

function strings(key: string, values: readonly string[]): OtlpWireAttribute {
  return { key, value: { arrayValue: { values: values.map((stringValue) => ({ stringValue })) } } }
}

// One home for the guards on host values: the plugin narrows what the host sends with them and this
// module re-checks what it writes, so a bound changed here changes both.

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

/** A string that is not empty. */
export function nonEmpty(value: unknown): value is string {
  return typeof value === 'string' && value !== ''
}

/** A whole number from 0 up: a count or a byte size. */
export function wholeNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0
}

/** A reported cost: finite and not negative. Zero is a measurement. */
export function nonNegativeNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0
}

/** An event time: epoch milliseconds from 1970 to the year 9999. */
export function epochMs(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 && value <= MAX_EPOCH_MS
}

/** The value when it is a non-empty string; otherwise absent. */
export function textOf(value: unknown): string | undefined {
  return nonEmpty(value) ? value : undefined
}

/** `textOf` cut to `MAX_FIELD_BYTES`: for a host string that is retained or sent as a label. */
export function labelOf(value: unknown): string | undefined {
  return nonEmpty(value) ? capUtf8(value, MAX_FIELD_BYTES) : undefined
}

/** The value when it is a whole number from 0 up; otherwise absent. */
export function wholeOf(value: unknown): number | undefined {
  return wholeNumber(value) ? value : undefined
}

/** The value when it is a valid cost; otherwise absent. */
export function costOf(value: unknown): number | undefined {
  return nonNegativeNumber(value) ? value : undefined
}

function nanos(ms: number): string {
  return (BigInt(Math.trunc(ms)) * 1_000_000n).toString()
}

function identity(sessionId: string, kind: SpanKind): OtlpWireAttribute[] {
  return [
    str(OPENCODE_KEYS.contractVersion, OPENCODE_CONTRACT_VERSION),
    str(OPENCODE_KEYS.sessionId, sessionId),
    str(OPENCODE_KEYS.spanKind, kind),
  ]
}

function span(
  sessionId: string,
  kind: SpanKind,
  anchor: string,
  startMs: number,
  endMs: number,
  attributes: OtlpWireAttribute[],
  extra: { parent?: string; status?: OtlpWireStatus } = {},
): OtlpWireSpan {
  return {
    traceId: traceIdFor(sessionId),
    spanId: spanIdFor(kind, sessionId, anchor),
    ...(extra.parent !== undefined ? { parentSpanId: extra.parent } : {}),
    name: SPAN_NAMES[kind],
    kind: 'SPAN_KIND_INTERNAL',
    startTimeUnixNano: nanos(startMs),
    // A span never ends before it starts, whatever the two clocks said.
    endTimeUnixNano: nanos(Math.max(startMs, endMs)),
    attributes,
    ...(extra.status !== undefined ? { status: extra.status } : {}),
  }
}

// ---------------------------------------------------------------------------------------------
// Spans

/**
 * The request span's status. `unknown` means no terminal event was seen, which is not a failure,
 * so it carries no status (OTLP unset) instead of an error.
 */
function requestStatus(outcome: RequestOutcome): OtlpWireStatus | undefined {
  if (outcome === 'unknown') return undefined
  return { code: outcome === 'succeeded' ? 'STATUS_CODE_OK' : 'STATUS_CODE_ERROR' }
}

/** The request span: parentless, covering one execution. */
export function mapRequestSpan(record: RequestRecord): OtlpWireSpan {
  const attributes = identity(record.sessionId, 'request')
  if (nonEmpty(record.agent)) attributes.push(str(OPENCODE_KEYS.agent, record.agent))
  if (nonEmpty(record.version)) attributes.push(str(OPENCODE_KEYS.version, record.version))
  if (wholeNumber(record.promptCount)) attributes.push(count(OPENCODE_KEYS.promptCount, record.promptCount))
  if (record.outcome !== 'succeeded') attributes.push(str(OPENCODE_KEYS.executionOutcome, record.outcome))
  if (record.outcome === 'interrupted' && nonEmpty(record.reason)) {
    attributes.push(str(OPENCODE_KEYS.executionReason, record.reason))
  }
  if (nonEmpty(record.promptText)) attributes.push(str(OPENCODE_KEYS.promptText, record.promptText))
  if (nonEmpty(record.label)) attributes.push(str(OPENCODE_KEYS.sessionLabel, record.label))
  const status = requestStatus(record.outcome)
  return span(record.sessionId, 'request', record.anchorId, record.startMs, record.endMs, attributes, {
    ...(status !== undefined ? { status } : {}),
  })
}

/** The step span: one model call, child of its request when the request was seen. */
export function mapStepSpan(record: StepRecord): OtlpWireSpan {
  const attributes = identity(record.sessionId, 'step')
  attributes.push(str(OPENCODE_KEYS.messageId, record.messageId))
  if (nonEmpty(record.agent)) attributes.push(str(OPENCODE_KEYS.agent, record.agent))
  if (nonEmpty(record.modelId)) attributes.push(str(OPENCODE_KEYS.modelId, record.modelId))
  if (nonEmpty(record.modelProvider)) attributes.push(str(OPENCODE_KEYS.modelProvider, record.modelProvider))
  attributes.push(str(OPENCODE_KEYS.requestKind, record.requestKind))
  if (nonEmpty(record.finish)) attributes.push(str(OPENCODE_KEYS.stepFinish, record.finish))
  const { usage } = record
  if (wholeNumber(usage.input)) attributes.push(count(OPENCODE_KEYS.usageInput, usage.input))
  if (wholeNumber(usage.output)) attributes.push(count(OPENCODE_KEYS.usageOutput, usage.output))
  if (wholeNumber(usage.reasoning)) attributes.push(count(OPENCODE_KEYS.usageReasoning, usage.reasoning))
  if (wholeNumber(usage.cacheRead)) attributes.push(count(OPENCODE_KEYS.usageCacheRead, usage.cacheRead))
  if (wholeNumber(usage.cacheWrite)) attributes.push(count(OPENCODE_KEYS.usageCacheWrite, usage.cacheWrite))
  // A reported zero cost is a measurement; an absent cost is not one.
  if (nonNegativeNumber(record.cost)) {
    attributes.push({ key: OPENCODE_KEYS.cost, value: { doubleValue: record.cost } })
  }
  if (record.skills.length > 0) attributes.push(strings(OPENCODE_KEYS.skillsInvoked, record.skills))
  const content = record.content
  if (content !== undefined) {
    if (nonEmpty(content.system)) attributes.push(str(OPENCODE_KEYS.contextSystem, content.system))
    if (nonEmpty(content.tools)) attributes.push(str(OPENCODE_KEYS.contextTools, content.tools))
    if (content.skills !== undefined && content.skills.length > 0) {
      attributes.push(str(OPENCODE_KEYS.contextSkills, JSON.stringify(content.skills)))
    }
  }
  return span(record.sessionId, 'step', record.messageId, record.startMs, record.endMs, attributes, {
    ...(record.requestAnchorId !== undefined
      ? { parent: spanIdFor('request', record.sessionId, record.requestAnchorId) }
      : {}),
  })
}

/** The tool span: one tool call, child of the step named by its message id. */
export function mapToolSpan(record: ToolRecord): OtlpWireSpan {
  const attributes = identity(record.sessionId, 'tool')
  if (nonEmpty(record.name)) attributes.push(str(OPENCODE_KEYS.toolName, record.name))
  attributes.push(str(OPENCODE_KEYS.toolCallId, record.callId))
  if (nonEmpty(record.messageId)) attributes.push(str(OPENCODE_KEYS.messageId, record.messageId))
  attributes.push(str(OPENCODE_KEYS.toolStatus, record.status))
  if (nonEmpty(record.errorType)) attributes.push(str(OPENCODE_KEYS.toolErrorType, record.errorType))
  if (wholeNumber(record.inputBytes)) attributes.push(count(OPENCODE_KEYS.toolInputBytes, record.inputBytes))
  if (wholeNumber(record.resultBytes)) attributes.push(count(OPENCODE_KEYS.toolResultBytes, record.resultBytes))
  if (record.timing !== undefined) attributes.push(str(OPENCODE_KEYS.toolTiming, record.timing))
  if (nonEmpty(record.skillId)) attributes.push(str(OPENCODE_KEYS.skillId, record.skillId))
  return span(record.sessionId, 'tool', record.callId, record.startMs, record.endMs, attributes, {
    ...(nonEmpty(record.messageId) ? { parent: spanIdFor('step', record.sessionId, record.messageId) } : {}),
  })
}

// ---------------------------------------------------------------------------------------------
// Content shaping

/**
 * Copy a string into independent storage. `slice` on a long string keeps the parent's backing
 * allocation alive (a V8 SlicedString), so a retained capped value can pin megabytes behind a short
 * length. A JSON round-trip allocates a fresh string with identical UTF-16 code units, including
 * lone surrogates, which a UTF-8 round-trip would replace.
 */
export function detachedCopy(text: string): string {
  return JSON.parse(JSON.stringify(text)) as string
}

/** `text` limited to `MAX_CONTENT_CHARS`, marked when cut. Always an independent copy. */
export function capContent(text: string): string {
  if (text.length <= MAX_CONTENT_CHARS) return detachedCopy(text)
  return `${detachedCopy(text.slice(0, MAX_CONTENT_CHARS))}${CONTENT_TRUNCATION_MARKER}`
}

/** Joined `system[n].text` (blank line between parts); undefined when the host sent none. */
export function systemTextOf(system: unknown): string | undefined {
  if (!Array.isArray(system)) return undefined
  const parts = (system as unknown[])
    .map((part) => (isRecord(part) && typeof part['text'] === 'string' ? part['text'] : ''))
    .filter((part) => part !== '')
  return parts.length > 0 ? parts.join('\n\n') : undefined
}

/**
 * The tools object (keyed by tool name) as JSON text of `[{name, description, input}]`; undefined
 * when empty or not serializable. Entries are serialized one at a time and the walk stops once the
 * cap is passed, so a huge catalogue is never stringified whole; a cut result is marked, not valid
 * JSON.
 */
export function toolsJsonOf(tools: unknown): string | undefined {
  if (!isRecord(tools) || Array.isArray(tools)) return undefined
  const names = Object.keys(tools)
  if (names.length === 0) return undefined
  try {
    const members: string[] = []
    let size = 2
    let cut = false
    for (const name of names) {
      const definition = tools[name]
      const body = isRecord(definition) ? definition : {}
      const member = JSON.stringify({ name, description: body['description'], input: body['input'] })
      members.push(member)
      size += member.length + 1
      if (size > MAX_CONTENT_CHARS) {
        cut = true
        break
      }
    }
    return capContent(`[${members.join(',')}${cut ? '' : ']'}`)
  } catch {
    // A tools value JSON cannot serialize (a cycle, a BigInt) has no wire form: absent, not partial.
    return undefined
  }
}

/** UTF-8 size of `JSON.stringify(value)`; undefined when the value has no JSON form. */
export function jsonBytes(value: unknown): number | undefined {
  try {
    const text = JSON.stringify(value)
    return text === undefined ? undefined : Buffer.byteLength(text, 'utf8')
  } catch {
    return undefined
  }
}

/**
 * UTF-8 size of the concatenated `text` parts of a tool result's content: the result the model
 * receives. Undefined when the content is not a parts array.
 */
export function textPartsBytes(content: unknown): number | undefined {
  if (!Array.isArray(content)) return undefined
  let bytes = 0
  for (const part of content as unknown[]) {
    if (isRecord(part) && typeof part['text'] === 'string') bytes += Buffer.byteLength(part['text'], 'utf8')
  }
  return bytes
}
