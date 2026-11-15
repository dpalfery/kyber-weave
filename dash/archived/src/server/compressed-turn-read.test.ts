import { deflateSync } from 'node:zlib'
import { describe, expect, it } from 'vitest'

import { digestSessionPayloads } from '../canon/harnesses.js'
import { CanonStore } from '../canon/store.js'
import type { AsadSessionPayload } from '../canon/sessions.js'
import type { CanonicalRecord, ContentPart } from '../canon/types.js'
import { KyberBridge } from './bridge.js'
import { callRoute, makeDeps } from './testing.js'

// The reader half of the compressed-column contract, on the bridge's side.
//
// The bridge keeps its own copies of the store's reads (the both-copies
// rule), so a compression on the write side has to be matched in every copy
// that parses the column — a write-side change that lands without its
// matching reader turns a session's turns into nothing.
//
// The pins below are behavioural: they go through the bridge's public reads
// and through the detail route, so they fail on the missing behaviour rather
// than on a name that does not exist yet.

const EXOTIC_PARTS: ContentPart[] = [
  { part: 'system_prompt', text: 'héllo — 日本語 🎉 "quoted" and \\ backslash', order: 0 },
  { part: 'conversation_history', text: '', order: 1 },
  { part: 'tool_result_content', text: 'x'.repeat(120_000), order: 2 },
]

const TURNS: Array<Record<string, unknown>> = [
  { index: 0, spanId: 'span-turn-0', model: 'opus-4-1', input: 1200, output: 300, fresh: 900 },
  { index: 1, spanId: 'span-turn-1', model: 'opus-4-1', input: 2400, output: 410, fresh: 1800 },
  { index: 2, spanId: 'span-turn-2', model: 'opus-4-1', input: 1800, output: 220, fresh: 1500 },
]

function record(overrides: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return {
    spanId: 'span-1',
    traceId: 'trace-1',
    parentSpanId: null,
    source: 'claude-code',
    harness: 'claude-code',
    sessionId: 'sess-1',
    name: 'llm_request',
    op: 'llm.invoke',
    kind: 'client',
    timestamp: '2026-10-20T10:00:00.000Z',
    durationMs: 12,
    status: 'ok',
    tokens: {
      freshInput: 100,
      cacheRead: 0,
      cacheCreation: 0,
      output: 10,
      reportedInput: 100,
      reportedOutput: 10,
    },
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
    ...overrides,
  }
}

function writeSession(store: CanonStore, sessionId: string, turns: Array<Record<string, unknown>>): void {
  const payload: Record<string, unknown> = {
    id: sessionId,
    session_id: sessionId,
    harness: 'claude-code',
    label: `Session ${sessionId}`,
    agent_name: 'architect',
    repo: 'kyber-weave',
    branch: 'main',
    span_count: turns.length,
    summary: { turn_count: turns.length, total_input: 5400, total_output: 930 },
    context: {
      contextLimitSource: 'reported',
      contextLimit: 200_000,
      measurable: true,
      first: { buckets: { system_prompt: 1000 } },
      last: { buckets: { system_prompt: 2000 } },
      turns: [{ pressure: 0.3 }, { pressure: 0.5 }],
    },
    tools: [{ name: 'Bash', schema_tokens: 100 }],
    requests: [],
    servers: [],
    coverage: { schema: 1, context: 1 },
    problems: [],
    reconciliation: [],
    costBasisMismatch: null,
    turns,
  }
  store.upsertSession({
    sessionId,
    harness: 'claude-code',
    label: `Session ${sessionId}`,
    isSubagent: false,
    parentSession: null,
    agentName: 'architect',
    repo: 'kyber-weave',
    branch: 'main',
    started: '2026-10-20T10:00:00.000Z',
    ended: '2026-10-20T10:30:00.000Z',
    payload,
  })
}

/** The bytes a compressed turn row holds, in the form the writer must produce. */
function compressedTurn(turn: Record<string, unknown>): Uint8Array {
  return deflateSync(Buffer.from(JSON.stringify(turn), 'utf8'))
}

/** The plain JSON a turn row held before the compression landed. */
function plainTurn(turn: Record<string, unknown>): string {
  return JSON.stringify(turn)
}

/** Overwrite a session's turn rows with one stored form. */
function rewriteTurnRows(
  store: CanonStore,
  sessionId: string,
  turns: Array<Record<string, unknown>>,
  stored: (turn: Record<string, unknown>) => string | Uint8Array,
): void {
  const statement = store
    .getDatabase()
    .prepare('UPDATE session_turn SET turn_json = ? WHERE session_id = ? AND turn_index = ?')
  for (const turn of turns) statement.run(stored(turn), sessionId, turn['index'] as number)
}

describe('the bridge reads a compressed session_turn row', () => {
  it('serves the session turns from a compressed turn row', () => {
    // The bridge's own copy of the turn read. A reader that parses the column
    // as text drops every turn, and the session comes back with none.
    const store = new CanonStore(':memory:')
    const bridge = new KyberBridge({ store })
    try {
      writeSession(store, 'sess-c', TURNS)
      rewriteTurnRows(store, 'sess-c', TURNS, compressedTurn)

      const payload = bridge.getSessionPayload<{ turns: Array<Record<string, unknown>> }>('sess-c')
      expect(payload).not.toBeNull()
      expect(payload!.turns).toEqual(TURNS)
    } finally {
      bridge.close()
      store.close()
    }
  })

  it('serves one turn measured counters from a compressed turn row', () => {
    // The bounded turn read is a second copy of the same parse: it must
    // inflate the row too, or the turn figures of a compressed session are
    // absent.
    const store = new CanonStore(':memory:')
    const bridge = new KyberBridge({ store })
    try {
      writeSession(store, 'sess-c', TURNS)
      rewriteTurnRows(store, 'sess-c', TURNS, compressedTurn)

      expect(bridge.getSessionTurnFigures('sess-c', 1)).toEqual({
        index: 1,
        input: 2400,
        output: 410,
        fresh: 1800,
      })
    } finally {
      bridge.close()
      store.close()
    }
  })

  it('serves the session turns from a turn row written before the compression', () => {
    // The migration tolerance on the bridge's side: every turn row written
    // before the compression landed holds the plain JSON.
    const store = new CanonStore(':memory:')
    const bridge = new KyberBridge({ store })
    try {
      writeSession(store, 'sess-p', TURNS)
      rewriteTurnRows(store, 'sess-p', TURNS, plainTurn)

      const payload = bridge.getSessionPayload<{ turns: Array<Record<string, unknown>> }>('sess-p')
      expect(payload).not.toBeNull()
      expect(payload!.turns).toEqual(TURNS)
      expect(bridge.getSessionTurnFigures('sess-p', 1)).toEqual({
        index: 1,
        input: 2400,
        output: 410,
        fresh: 1800,
      })
    } finally {
      bridge.close()
      store.close()
    }
  })
})

describe('the consumers see the same bytes for a compressed column and a plain one', () => {
  it('serves the detail route response identically for both stored forms', async () => {
    // The detail route's response must be byte-identical to today's: the
    // compression is a storage concern and nothing a consumer can observe.
    const compressedStore = new CanonStore(':memory:')
    const plainStore = new CanonStore(':memory:')
    const compressedBridge = new KyberBridge({ store: compressedStore })
    const plainBridge = new KyberBridge({ store: plainStore })
    const deps = makeDeps()
    try {
      writeSession(compressedStore, 'sess-x', TURNS)
      writeSession(plainStore, 'sess-x', TURNS)
      rewriteTurnRows(plainStore, 'sess-x', TURNS, plainTurn)

      const compressed = await callRoute('GET', '/api/kyber/session/sess-x', {
        bridge: compressedBridge,
        deps,
      })
      const plain = await callRoute('GET', '/api/kyber/session/sess-x', {
        bridge: plainBridge,
        deps,
      })

      expect(compressed.status).toBe(200)
      expect(plain.status).toBe(200)
      expect(compressed.raw).toBe(plain.raw)
    } finally {
      compressedBridge.close()
      plainBridge.close()
      compressedStore.close()
      plainStore.close()
      deps.store.close()
    }
  })

  it('digests the session identically for both stored forms', () => {
    // The digest's figures: a session whose turns are stored compressed and
    // the same session whose turns are stored plain must reduce to the same
    // figures — same count, same peak pressure, same tool and cache totals.
    const compressedStore = new CanonStore(':memory:')
    const plainStore = new CanonStore(':memory:')
    const compressedBridge = new KyberBridge({ store: compressedStore })
    const plainBridge = new KyberBridge({ store: plainStore })
    try {
      writeSession(compressedStore, 'sess-x', TURNS)
      writeSession(plainStore, 'sess-x', TURNS)
      rewriteTurnRows(plainStore, 'sess-x', TURNS, plainTurn)

      const compressed = digestSessionPayloads([
        compressedBridge.getSessionPayload('sess-x') as unknown as AsadSessionPayload,
      ])
      const plain = digestSessionPayloads([
        plainBridge.getSessionPayload('sess-x') as unknown as AsadSessionPayload,
      ])

      expect(compressed).toEqual(plain)
      expect(compressed.count).toBe(1)
      expect(compressed.peakPressures).toEqual([0.5])
    } finally {
      compressedBridge.close()
      plainBridge.close()
      compressedStore.close()
      plainStore.close()
    }
  })
})

describe('the bridge reads a records.parts_json column in either stored form', () => {
  it('serves the content of a parts column holding plain JSON', () => {
    // The direct-DB copy of the parts read. A row written before the
    // compression landed holds the plain JSON, and the row's `content_json`
    // is '{}' — a reader that falls back to it serves no content at all.
    const store = new CanonStore(':memory:')
    try {
      store.upsertMany([record({ parts: EXOTIC_PARTS })])
      store
        .getDatabase()
        .prepare('UPDATE records SET parts_json = ? WHERE span_id = ?')
        .run(JSON.stringify(EXOTIC_PARTS), 'span-1')

      // The bridge reads through the raw handle here, so the direct-DB copies
      // are the ones under test. The handle belongs to the store, which
      // closes it; the bridge is not closed for that reason.
      const bridge = new KyberBridge({ canonDb: store.getDatabase() })
      const content = bridge.getSessionContent('sess-1', { full: true })

      expect(content).not.toBeNull()
      expect(content!.parts.map((part) => part.text)).toEqual(EXOTIC_PARTS.map((part) => part.text))
    } finally {
      store.close()
    }
  })

  it('serves the content of a parts column holding the compressed parts', () => {
    // The same read, on the compressed form: both stored forms answer with
    // the same content.
    const store = new CanonStore(':memory:')
    try {
      store.upsertMany([record({ parts: EXOTIC_PARTS })])
      const bridge = new KyberBridge({ canonDb: store.getDatabase() })
      const content = bridge.getSessionContent('sess-1', { full: true })

      expect(content).not.toBeNull()
      expect(content!.parts.map((part) => part.text)).toEqual(EXOTIC_PARTS.map((part) => part.text))
    } finally {
      store.close()
    }
  })
})
