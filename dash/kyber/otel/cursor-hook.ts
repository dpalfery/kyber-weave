import { createHash } from 'node:crypto'
import type { OtlpSpan } from './receiver.js'

/** The local OTLP/HTTP receiver started by `codeburn kyber otel`. */
export const DEFAULT_CURSOR_HOOK_OTLP_ENDPOINT = 'http://127.0.0.1:4318/v1/traces'

type CursorHookEventBase = {
  sessionId: string
  turnId: string
  timestamp: string
}

export type CursorHookEvent =
  | (CursorHookEventBase & {
      type: 'agent_turn.started'
      prompt?: string
      promptTokens?: number
      schemaTokens?: number
    })
  | (CursorHookEventBase & {
      type: 'agent_turn.completed'
      outputTokens?: number
      reasoningTokens?: number
    })
  | (CursorHookEventBase & {
      type: 'tool.started'
      toolCallId: string
      toolName: string
    })
  | (CursorHookEventBase & {
      type: 'tool.completed'
      toolCallId: string
    })

export type CursorHookTool = {
  toolCallId: string
  name: string
  order: number
  startedAt: string
  completedAt?: string
}

export type CursorHookTurn = {
  sessionId: string
  turnId: string
  startedAt: string
  completedAt: string
  promptTokens?: number
  schemaTokens?: number
  outputTokens?: number
  reasoningTokens?: number
  tools: CursorHookTool[]
}

type PendingTurn = {
  sessionId: string
  turnId: string
  startedAt: string
  promptTokens?: number
  schemaTokens?: number
  tools: CursorHookTool[]
}

/**
 * Converts the line-oriented Cursor hook stream into completed agent turns.
 * Completion delivery is at-least-once, so a completed identity is retained to
 * keep a redelivered stop hook from creating a second trace.
 */
export class CursorHookTurnAssembler {
  private readonly pending = new Map<string, PendingTurn>()
  private readonly completed = new Set<string>()

  accept(event: CursorHookEvent): CursorHookTurn[] {
    const key = turnKey(event.sessionId, event.turnId)
    if (event.type === 'agent_turn.started') {
      if (this.completed.has(key) || this.pending.has(key)) return []
      this.pending.set(key, {
        sessionId: event.sessionId,
        turnId: event.turnId,
        startedAt: event.timestamp,
        ...(validCount(event.promptTokens) ? { promptTokens: event.promptTokens } : {}),
        ...(validCount(event.schemaTokens) ? { schemaTokens: event.schemaTokens } : {}),
        tools: [],
      })
      return []
    }

    const turn = this.pending.get(key)
    if (event.type === 'tool.started') {
      if (turn === undefined || this.completed.has(key)) return []
      if (!turn.tools.some((tool) => tool.toolCallId === event.toolCallId)) {
        turn.tools.push({
          toolCallId: event.toolCallId,
          name: event.toolName,
          order: turn.tools.length,
          startedAt: event.timestamp,
        })
      }
      return []
    }

    if (event.type === 'tool.completed') {
      const tool = turn?.tools.find((candidate) => candidate.toolCallId === event.toolCallId)
      if (tool !== undefined && tool.completedAt === undefined) tool.completedAt = event.timestamp
      return []
    }

    if (this.completed.has(key) || turn === undefined) return []
    this.pending.delete(key)
    this.completed.add(key)
    return [{
      ...turn,
      completedAt: event.timestamp,
      ...(validCount(event.outputTokens) ? { outputTokens: event.outputTokens } : {}),
      ...(validCount(event.reasoningTokens) ? { reasoningTokens: event.reasoningTokens } : {}),
      tools: turn.tools,
    }]
  }
}

/** Turn a completed hook turn into the normalized span shape used by KyberDash. */
export function toCursorHookOtlpTrace(turn: CursorHookTurn): OtlpSpan[] {
  const traceId = stableId(`cursor:trace:${turn.sessionId}:${turn.turnId}`, 32)
  const rootSpanId = stableId(`cursor:turn:${turn.sessionId}:${turn.turnId}`, 16)
  const rootAttributes: Record<string, unknown> = {
    'gen_ai.system': 'cursor',
    'codeburn.provider': 'cursor',
    'cursor.session.id': turn.sessionId,
    'cursor.turn.id': turn.turnId,
    ...measuredOrUnavailable('prompt_tokens', 'gen_ai.usage.input_tokens', turn.promptTokens),
    ...measuredOrUnavailable('schema_tokens', 'codeburn.schema_tokens', turn.schemaTokens),
    ...(validCount(turn.outputTokens) ? { 'gen_ai.usage.output_tokens': turn.outputTokens } : {}),
    ...(validCount(turn.reasoningTokens) ? { 'gen_ai.usage.reasoning_tokens': turn.reasoningTokens } : {}),
  }
  const spans: OtlpSpan[] = [{
    traceId,
    spanId: rootSpanId,
    parentSpanId: null,
    name: 'cursor.agent_turn',
    kind: 'internal',
    ...timing(turn.startedAt, turn.completedAt),
    status: { code: 'ok' },
    attributes: rootAttributes,
    resource: { 'service.name': 'cursor' },
    scope: { name: 'codeburn.cursor-hook' },
  }]

  for (const tool of turn.tools) {
    const completedAt = tool.completedAt ?? turn.completedAt
    spans.push({
      traceId,
      spanId: stableId(`cursor:tool:${turn.sessionId}:${turn.turnId}:${tool.toolCallId}`, 16),
      parentSpanId: rootSpanId,
      name: 'cursor.tool',
      kind: 'internal',
      ...timing(tool.startedAt, completedAt),
      status: { code: 'ok' },
      attributes: {
        'gen_ai.system': 'cursor',
        'codeburn.provider': 'cursor',
        'cursor.session.id': turn.sessionId,
        'cursor.turn.id': turn.turnId,
        'cursor.tool.call_id': tool.toolCallId,
        'cursor.tool.name': tool.name,
        'cursor.tool.order': tool.order,
      },
      resource: { 'service.name': 'cursor' },
      scope: { name: 'codeburn.cursor-hook' },
    })
  }
  return spans
}

export type CursorHookStdinOptions = {
  stdin: string
  write: (line: string) => void
  /** Injectable delivery port; production posts to the local OTLP receiver. */
  post?: (payload: Record<string, unknown>) => Promise<void>
}

/**
 * Parse hook JSONL, deliver every completed turn to the local OTLP receiver,
 * and write the submitted request for hook diagnostics.
 */
export async function runCursorHookStdin({ stdin, write, post = postCursorHookOtlpJson }: CursorHookStdinOptions): Promise<void> {
  const assembler = new CursorHookTurnAssembler()
  for (const line of stdin.split(/\r?\n/)) {
    if (line.trim() === '') continue
    const event = JSON.parse(line) as CursorHookEvent
    for (const turn of assembler.accept(event)) {
      const payload = toOtlpJson(toCursorHookOtlpTrace(turn))
      await post(payload)
      write(JSON.stringify(payload))
    }
  }
}

/** Deliver one OTLP/JSON request to the in-repository local trace receiver. */
export async function postCursorHookOtlpJson(payload: Record<string, unknown>): Promise<void> {
  const response = await fetch(DEFAULT_CURSOR_HOOK_OTLP_ENDPOINT, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(payload),
  })
  if (!response.ok) {
    throw new Error(`Cursor hook OTLP delivery failed: ${response.status} ${response.statusText}`)
  }
}

function toOtlpJson(spans: readonly OtlpSpan[]): Record<string, unknown> {
  return {
    resourceSpans: [{
      resource: { attributes: [{ key: 'service.name', value: { stringValue: 'cursor' } }] },
      scopeSpans: [{
        scope: { name: 'codeburn.cursor-hook' },
        spans: spans.map((span) => ({
          traceId: span.traceId,
          spanId: span.spanId,
          ...(span.parentSpanId === null ? {} : { parentSpanId: span.parentSpanId }),
          name: span.name,
          kind: 'SPAN_KIND_INTERNAL',
          startTimeUnixNano: span.startTimeUnixNano,
          endTimeUnixNano: span.endTimeUnixNano,
          attributes: Object.entries(span.attributes).map(([key, value]) => ({
            key,
            value: anyValue(value),
          })),
        })),
      }],
    }],
  }
}

function anyValue(value: unknown): Record<string, unknown> {
  if (typeof value === 'string') return { stringValue: value }
  if (typeof value === 'boolean') return { boolValue: value }
  if (typeof value === 'number' && Number.isInteger(value)) return { intValue: String(value) }
  if (typeof value === 'number') return { doubleValue: value }
  return { stringValue: JSON.stringify(value) }
}

function measuredOrUnavailable(metric: string, attribute: string, value: number | undefined): Record<string, unknown> {
  if (validCount(value)) return { [attribute]: value }
  return {
    [`codeburn.measurability.${metric}`]: {
      availability: 'not_measurable',
      reason: `Cursor hook did not export ${metric}; unavailable values are not represented as zero.`,
    },
  }
}

function timing(startedAt: string, completedAt: string): Pick<OtlpSpan, 'startTimeUnixNano' | 'endTimeUnixNano' | 'timestamp' | 'durationMs'> {
  const startMs = Date.parse(startedAt)
  const endMs = Date.parse(completedAt)
  const safeStartMs = Number.isFinite(startMs) ? startMs : 0
  const safeEndMs = Number.isFinite(endMs) ? Math.max(safeStartMs, endMs) : safeStartMs
  return {
    startTimeUnixNano: String(BigInt(safeStartMs) * 1_000_000n),
    endTimeUnixNano: String(BigInt(safeEndMs) * 1_000_000n),
    timestamp: new Date(safeStartMs).toISOString(),
    durationMs: safeEndMs - safeStartMs,
  }
}

function validCount(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0
}

function turnKey(sessionId: string, turnId: string): string {
  return `${sessionId}\u0000${turnId}`
}

function stableId(value: string, length: number): string {
  return createHash('sha256').update(value).digest('hex').slice(0, length)
}
