// Pi tool-record derivation for the OTLP path.
//
// Pi's collector emits one span per turn (`pi.llm.request`) and carries the
// whole conversation inline as `gen_ai.input.messages`; the tool calls and
// their results live inside that text, but no child span is emitted for them.
// Left alone, a Pi session therefore shows its `llm.invoke` turns and zero
// `tool.invoke` records — the tool usage the harness did report, dropped on
// the floor.
//
// Why this is a sibling of `pi.ts` and not a method on the adapter: the one
// mechanism that turns a reader tool call into a canonical tool record lives
// in `synth/synth.ts`, and `synth.ts` reaches `canon/measurability.ts`, which
// imports `pi.ts` at module scope to build `ADAPTERS_BY_HARNESS`. Importing
// `synth.ts` from `pi.ts` would close that cycle and evaluate `piAdapter`
// before it is initialized (TDZ). Deriving here, and calling from
// `canon/ingest.ts`, keeps the single `canon/adapters -> synth` edge clear of
// any back path into the measurability module.

import { createHash } from 'node:crypto'

import { synthesizeToolCall, type CanonicalToolRecord } from '../../synth/synth.js'
import type { ReaderToolCall, ReaderToolResult } from '../../synth/readers/types.js'
import { notMeasurable, type CanonicalRecord } from '../types.js'
import type { RawSpan } from './base.js'

/** Namespace for derived tool span ids; never collides with an OTLP hex id or `synth:`. */
export const PI_TOOL_SPAN_PREFIX = 'pi-tool:'

/** The metric the truncation caveat rides under, beside the tool-call count. */
export const PI_TOOL_CALLS_METRIC = 'tool_calls'

/**
 * Why a truncated Pi conversation cannot yield a complete tool-call count:
 * the collector caps `gen_ai.input.messages`, so calls past the cut are not
 * visible at all and the recovered list is a lower bound, never a total.
 */
export const PI_TOOL_TRUNCATION_REASON =
  'pi truncated the exported conversation at its size cap, so tool calls past the cut are not visible and this count is a lower bound.'

/** The tool call/result pieces one Pi conversation yielded, and whether it was complete. */
export type PiToolScan = {
  calls: ReaderToolCall[]
  results: ReaderToolResult[]
  /** The scan stopped at an object the cap cut short, so both lists are partial. */
  truncated: boolean
}

/**
 * Recover the `tool_call` and `tool_result` pieces from a Pi conversation.
 *
 * The extraction is marker-anchored rather than a whole-string parse: past the
 * collector's cap the text is no longer valid JSON, so a whole-string
 * `JSON.parse` could not be used to read it. Anchoring on the part marker and
 * brace-matching from there recovers every object written in full and stops
 * honestly at the first one that was not — and whitespace after a colon is
 * tolerated, so a payload that emits `{"type": "tool_call", …}` is not misread
 * as a conversation with no tool calls at all. Completeness is decided
 * separately, by the whole-payload parse below, because a cut that lands
 * *between* two objects leaves no half object behind to notice. Escaped quotes inside
 * result text, and the words `tool_call` / `tool_result` inside a result,
 * cannot be mistaken for a real part: inside a JSON string the quotes are
 * escaped, so the unescaped marker can only be a part boundary.
 */
export function extractPiToolMessages(messages: string): PiToolScan {
  const calls: ReaderToolCall[] = []
  const results: ReaderToolResult[] = []
  let truncated = false

  // Whitespace is tolerated after every colon, not only after `"raw":`. A
  // payload that emits `{"type": "tool_call", "raw": {…}}` is the same part,
  // and matching only the tight spelling dropped every tool call in the
  // conversation without ever saying so. The anchor is still the unescaped
  // marker, which inside a JSON string cannot appear — the quotes are escaped
  // there — so it can only be a part boundary.
  const marker = /\{\s*"type"\s*:\s*"(tool_call|tool_result)"\s*,\s*"raw"\s*:\s*\{/g
  let match: RegExpExecArray | null
  while ((match = marker.exec(messages)) !== null) {
    const kind = match[1]
    const objectStart = match.index + match[0].length - 1
    const object = balancedJsonObject(messages, objectStart)
    if (object === undefined) {
      // The cap cut this object — or everything after it — off. Nothing past
      // here is provably a call, so the lists end here and say so.
      truncated = true
      break
    }
    let raw: unknown
    try {
      raw = JSON.parse(object)
    } catch {
      // A balanced slice that still will not parse is not a call we can trust;
      // skip it without claiming an error and keep scanning past it.
      marker.lastIndex = objectStart + object.length
      continue
    }
    if (kind === 'tool_call') {
      const call = toReaderToolCall(raw)
      if (call !== undefined) calls.push(call)
    } else {
      const result = toReaderToolResult(raw)
      if (result !== undefined) results.push(result)
    }
    // Resume after the object so markers nested inside it are not rescanned.
    marker.lastIndex = objectStart + object.length
  }

  // The scan can only prove what it read. A cap that lands *between* two
  // objects leaves every object it covered intact, so the loop above finds
  // them all and never meets a cut one — indistinguishable from a complete
  // conversation. The payload is the only witness: if the whole thing is not
  // valid JSON, the emitter truncated it, and the list is a lower bound.
  return { calls, results, truncated: truncated || !isCompleteJson(messages) }
}

/** True when `text` parses as one whole JSON document. */
function isCompleteJson(text: string): boolean {
  try {
    JSON.parse(text)
    return true
  } catch {
    return false
  }
}

/** The balanced `{…}` object starting at `start`, or undefined when it never closes. */
function balancedJsonObject(text: string, start: number): string | undefined {
  let depth = 0
  let inString = false
  let escaped = false
  for (let i = start; i < text.length; i += 1) {
    const ch = text[i]
    if (inString) {
      if (escaped) escaped = false
      else if (ch === '\\') escaped = true
      else if (ch === '"') inString = false
      continue
    }
    if (ch === '"') inString = true
    else if (ch === '{') depth += 1
    else if (ch === '}') {
      depth -= 1
      if (depth === 0) return text.slice(start, i + 1)
    }
  }
  return undefined
}

/**
 * Build the `tool.invoke` records a Pi OTLP span's inline conversation
 * implies, reusing `synthesizeToolCall` — the synthesizer's one mechanism for
 * turning a reader tool call into a canonical tool record — so the OTLP path
 * and the session-file path produce the same record shape.
 *
 * Identity is keyed on (session, call id), not on the parent span: the
 * collector repeats the (capped) conversation prefix on every turn span, so a
 * parent-derived id would store one copy of each call per turn. A
 * deterministic, parent-independent id lets the store's idempotent upsert
 * collapse the copies (R2.5); `ingest.ts` keeps the first write so the stored
 * parent and timing are the earliest turn that carried the call, not an
 * arbitrary later one.
 */
export function derivePiToolRecords(parent: CanonicalRecord, raw: RawSpan): CanonicalToolRecord[] {
  const messages = raw.attributes['gen_ai.input.messages']
  if (typeof messages !== 'string' || messages === '') return []

  const { calls, results, truncated } = extractPiToolMessages(messages)
  if (calls.length === 0) return []

  const resultsById = new Map<string, ReaderToolResult>()
  for (const result of results) {
    if (!resultsById.has(result.toolCallId)) resultsById.set(result.toolCallId, result)
  }

  const sessionKey = parent.sessionId ?? parent.traceId ?? ''
  const seen = new Set<string>()
  const records: CanonicalToolRecord[] = []
  calls.forEach((call, index) => {
    // Identity must not depend on the parent. Every turn span of a session
    // repeats the same conversation prefix, so a parent-scoped identity filed
    // one copy of an id-less call per turn — the exact duplication this
    // derivation exists to prevent. What the call *is* (name, arguments) plus
    // its position in the prefix is stable, because the prefix is.
    const identity =
      call.id !== ''
        ? call.id
        : `${call.name}\u0000${JSON.stringify(call.arguments ?? {})}\u0000${index}`
    if (seen.has(identity)) return
    seen.add(identity)

    const record = synthesizeToolCall(
      parent,
      call,
      call.id !== '' ? resultsById.get(call.id) : undefined,
      index,
    )
    records.push({
      ...record,
      spanId: piToolSpanId(sessionKey, identity),
      ...(truncated
        ? {
            measurability: {
              ...record.measurability,
              [PI_TOOL_CALLS_METRIC]: notMeasurable(PI_TOOL_TRUNCATION_REASON),
            },
          }
        : {}),
    })
  })
  return records
}

/** The parent-independent span id one (session, call id) pair maps to. */
export function piToolSpanId(sessionKey: string, identity: string): string {
  const digest = createHash('sha256').update(`${sessionKey}\u0000${identity}`, 'utf8').digest('hex')
  return `${PI_TOOL_SPAN_PREFIX}${digest.slice(0, 24)}`
}

function toReaderToolCall(raw: unknown): ReaderToolCall | undefined {
  if (raw === null || typeof raw !== 'object') return undefined
  const piece = raw as Record<string, unknown>
  const name = nonEmptyString(piece['name'])
  if (name === undefined) return undefined
  const args = piece['arguments']
  return {
    id: nonEmptyString(piece['id']) ?? '',
    name,
    arguments:
      typeof args === 'string' || (args !== null && typeof args === 'object')
        ? (args as ReaderToolCall['arguments'])
        : {},
  }
}

function toReaderToolResult(raw: unknown): ReaderToolResult | undefined {
  if (raw === null || typeof raw !== 'object') return undefined
  const piece = raw as Record<string, unknown>
  const toolCallId = nonEmptyString(piece['toolCallId'])
  if (toolCallId === undefined) return undefined
  const name = nonEmptyString(piece['toolName'])
  return {
    toolCallId,
    ...(name !== undefined ? { name } : {}),
    content: toolResultText(piece['content']),
    ...(piece['isError'] === true ? { isError: true } : {}),
  }
}

/** The textual content of a Pi tool result: a string, or its text blocks joined. */
function toolResultText(content: unknown): string {
  if (typeof content === 'string') return content
  if (!Array.isArray(content)) return ''
  return content
    .map((piece) => {
      if (typeof piece === 'string') return piece
      if (piece !== null && typeof piece === 'object') {
        const text = (piece as Record<string, unknown>)['text']
        if (typeof text === 'string') return text
      }
      return ''
    })
    .join('')
}

function nonEmptyString(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined
}
