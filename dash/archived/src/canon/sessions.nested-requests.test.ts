// The session projection over nested records (ADR-era OpenCode V2 capture, design §3.3).
//
// An OpenCode execution is a parentless `agent.invoke` request root, its model calls are
// `llm.invoke` steps parented to it, and its tools are grandchildren. The projection used to
// count every parentless record as a request, credit a turn only to a root that was itself a
// turn, and sum the duration of every record. These tests pin the nested rules and the flat
// behaviour that must not move. The records are hand-built so the adapter is not needed.

import { describe, expect, it } from 'vitest'

import { tokens, turn } from './fixtures/records.js'
import { buildSessionRow } from './sessions.js'
import { approximateO200kBase } from './tokens.js'
import type { CanonicalRecord, ContentPart, SessionRow } from './types.js'

type RequestView = {
  request: string
  turns: number
  prompt?: string | null
}

type PayloadView = {
  label: string
  summary: {
    turn_count: number
    request_count: number
    duration_ms: number
    total_input: number
    total_cache_read: number
    cache_hit_ratio?: number
  }
  requests: RequestView[]
}

function payloadOf(row: SessionRow): PayloadView {
  return row.payload as unknown as PayloadView
}

/** The request list keyed by request span id, so the assertions do not depend on order. */
function turnsByRequest(payload: PayloadView): Record<string, number> {
  return Object.fromEntries(payload.requests.map((request) => [request.request, request.turns]))
}

const SESSION = 'ses_nested'
const T0 = Date.parse('2026-10-30T09:00:00.000Z')
const at = (offsetMs: number): string => new Date(T0 + offsetMs).toISOString()

/** The user's prompt, long enough that the 200-character preview is a real cut. */
const PROMPT_A = 'Refactor the auth module so the token refresh is shared by the CLI and the server. '.repeat(4)

/** An OpenCode execution: a parentless request root, timed by the run. */
function request(spanId: string, parts: ContentPart[], over: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return turn(spanId, parts, {
    op: 'agent.invoke',
    name: 'opencode.request',
    source: 'opencode',
    harness: 'opencode',
    sessionId: SESSION,
    kind: 'internal',
    timestamp: at(0),
    durationMs: 13367,
    tokens: tokens({ freshInput: 0, cacheRead: 0, cacheCreation: 0, output: 0, reportedInput: 0, reportedOutput: 0 }),
    cost: { basis: 'unknown', status: 'no_rate' },
    ...over,
  })
}

/** One model call. Output already includes reasoning, as the adapter folds it (D13). */
function step(
  spanId: string,
  parentSpanId: string,
  usage: { input: number; output: number; cacheRead: number; cacheWrite: number },
  over: Partial<CanonicalRecord> = {},
): CanonicalRecord {
  return turn(spanId, [], {
    parentSpanId,
    op: 'llm.invoke',
    name: 'opencode.step',
    source: 'opencode',
    harness: 'opencode',
    sessionId: SESSION,
    kind: 'internal',
    timestamp: at(100),
    durationMs: 4000,
    tokens: tokens({
      freshInput: usage.input,
      cacheRead: usage.cacheRead,
      cacheCreation: usage.cacheWrite,
      output: usage.output,
      reportedInput: usage.input + usage.cacheRead + usage.cacheWrite,
      reportedOutput: usage.output,
    }),
    cost: { basis: 'harness', status: 'priced', value: 0.01, currency: 'USD' },
    raw: { 'gen_ai.request.model': 'claude-opus-4-6' },
    ...over,
  })
}

/** A tool execution under a step: a grandchild of its request, never a turn. */
function tool(spanId: string, parentSpanId: string, durationMs: number): CanonicalRecord {
  return turn(spanId, [], {
    parentSpanId,
    op: 'tool.invoke',
    name: 'read',
    source: 'opencode',
    harness: 'opencode',
    sessionId: SESSION,
    kind: 'internal',
    timestamp: at(150),
    durationMs,
    tokens: tokens({ freshInput: 0, cacheRead: 0, cacheCreation: 0, output: 0, reportedInput: 0, reportedOutput: 0 }),
    cost: { basis: 'unknown', status: 'no_rate' },
  })
}

/**
 * Two OpenCode executions. Request A has three model calls (the runA shape: fresh input,
 * output with reasoning, cache read, cache write) and one tool. Request B has one model call.
 */
function nestedSession(): CanonicalRecord[] {
  return [
    request('req-a', [{ part: 'conversation_history', text: PROMPT_A }]),
    step('a-step-1', 'req-a', { input: 10935, output: 69 + 957, cacheRead: 1521, cacheWrite: 0 }, { durationMs: 4100 }),
    tool('a-tool-1', 'a-step-1', 300),
    step('a-step-2', 'req-a', { input: 1259, output: 65 + 223, cacheRead: 12401, cacheWrite: 0 }, {
      durationMs: 5200,
      timestamp: at(4400),
    }),
    step('a-step-3', 'req-a', { input: 439, output: 19 + 196, cacheRead: 13553, cacheWrite: 0 }, {
      durationMs: 3900,
      timestamp: at(9700),
    }),
    request('req-b', [], { timestamp: at(20000), durationMs: 4000 }),
    step('b-step-1', 'req-b', { input: 1200, output: 40, cacheRead: 0, cacheWrite: 0 }, {
      durationMs: 3900,
      timestamp: at(20100),
    }),
  ]
}

describe('nested OpenCode session: request and turn counts', () => {
  it('counts two requests and four turns for a three-step and a one-step execution', () => {
    // Passes on today's projection: these two counts already follow the parentless roots and the
    // llm.invoke turns. Kept so a regression in either is named, not inferred.
    const payload = payloadOf(buildSessionRow(SESSION, nestedSession(), approximateO200kBase))

    expect(payload.summary.request_count).toBe(2)
    expect(payload.summary.turn_count).toBe(4)
  })

  it('attributes each turn to the request root it descends from, so requests are [3, 1]', () => {
    // Today a request earns a turn only when it is itself a turn, so both roots show 0.
    // The grandchild tool under a-step-1 is not a turn and must not raise the count.
    const payload = payloadOf(buildSessionRow(SESSION, nestedSession(), approximateO200kBase))

    expect(turnsByRequest(payload)).toEqual({ 'req-a': 3, 'req-b': 1 })
  })
})

describe('nested OpenCode session: duration', () => {
  it('sums the request roots durations as the session duration, not every record', () => {
    // Request durations are 13367 + 4000. Summing every record would also add the steps
    // (4100 + 5200 + 3900 + 3900) and the tool (300).
    const payload = payloadOf(buildSessionRow(SESSION, nestedSession(), approximateO200kBase))

    expect(payload.summary.duration_ms).toBe(13367 + 4000)
  })
})

describe('flat session: counts that must not move', () => {
  it('sums every record and counts a root that is itself a turn as one request', () => {
    // A flat session has no record with a parent in the session, so duration is the sum of
    // all records, and a turn root carries one turn. The parentless tool root counts as a
    // request with no turns, as it always has.
    const records = [
      turn('flat-1', [{ part: 'system_prompt', text: 'You are a helpful assistant.', tokens: 10 }], {
        sessionId: 'sess-flat',
        timestamp: at(0),
        durationMs: 120,
      }),
      turn('flat-2', [], { sessionId: 'sess-flat', timestamp: at(500), durationMs: 80 }),
      turn('flat-tool', [], {
        sessionId: 'sess-flat',
        op: 'tool.invoke',
        name: 'read',
        timestamp: at(900),
        durationMs: 50,
      }),
    ]

    const payload = payloadOf(buildSessionRow('sess-flat', records, approximateO200kBase))

    expect(payload.summary.duration_ms).toBe(120 + 80 + 50)
    expect(turnsByRequest(payload)).toEqual({ 'flat-1': 1, 'flat-2': 1, 'flat-tool': 0 })
  })
})

describe('requests[].prompt: the user prompt preview', () => {
  it('previews the first 200 characters of the request root conversation_history part', () => {
    // Today a request carries no prompt field at all, so this is undefined, not the preview.
    const payload = payloadOf(buildSessionRow(SESSION, nestedSession(), approximateO200kBase))
    const requestA = payload.requests.find((request) => request.request === 'req-a')

    expect(PROMPT_A.length).toBeGreaterThan(200)
    expect(requestA?.prompt).toBe(PROMPT_A.slice(0, 200))
    expect(requestA?.prompt?.length).toBeLessThanOrEqual(200)
  })

  it('is null for a request root that carries no prompt part', () => {
    const payload = payloadOf(buildSessionRow(SESSION, nestedSession(), approximateO200kBase))
    const requestB = payload.requests.find((request) => request.request === 'req-b')

    expect(requestB?.prompt).toBeNull()
  })
})
