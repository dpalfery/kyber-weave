import { deflateSync, inflateSync } from 'node:zlib'
import { describe, expect, it } from 'vitest'

import { CanonStore } from './store.js'
import { contentFromParts } from './types.js'
import type { CanonicalRecord, ContentPart } from './types.js'

// The compressed-column contract.
//
// Two columns hold JSON that a consumer fetches whole: `records.parts_json`
// (a record's parts — the detail and digest path's bulk) and
// `session_turn.turn_json` (one turn — the turn store's bulk). Both are
// stored compressed and read back decompressed, and both tolerate a row
// written before the compression landed: the column holds the plain JSON
// then, and a reader that cannot read it loses the content it was asked for.
//
// The pins below go through the store's public write and read paths, so they
// fail on the missing behaviour rather than on a name that does not exist yet.

/** Parts that exercise the JSON corners a round trip must not lose. */
const EXOTIC_PARTS: ContentPart[] = [
  { part: 'system_prompt', text: 'héllo — 日本語 🎉 "quoted" and \\ backslash', order: 0 },
  { part: 'conversation_history', text: '', order: 1 },
  { part: 'tool_result_content', text: 'x'.repeat(120_000), order: 2 },
  // A null field: the column holds JSON, and JSON's null is a value.
  { part: 'tool_definitions', text: 'schema', server: null, tokens: null } as unknown as ContentPart,
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

/** The value the `parts_json` column holds for one span. */
function storedParts(store: CanonStore, spanId: string): unknown {
  const row = store
    .getDatabase()
    .prepare('SELECT parts_json FROM records WHERE span_id = ?')
    .get(spanId) as { parts_json: unknown } | undefined
  expect(row, `record ${spanId} must be stored`).toBeDefined()
  return row!.parts_json
}

/**
 * The value the deflated bytes hold.
 *
 * Inflate-and-parse is the proof of exactly one deflate layer: a column that
 * had been deflated twice would inflate to deflated bytes, and the parse
 * would fail.
 */
function deflatedValue(stored: unknown): unknown {
  expect(stored, 'the column must hold deflated bytes').toBeInstanceOf(Uint8Array)
  return JSON.parse(inflateSync(Buffer.from(stored as Uint8Array)).toString('utf8'))
}

type TurnRow = { session_id: string; turn_index: number; turn_json: unknown }

function turnRows(store: CanonStore, sessionId: string): TurnRow[] {
  return store
    .getDatabase()
    .prepare('SELECT session_id, turn_index, turn_json FROM session_turn WHERE session_id = ? ORDER BY turn_index')
    .all(sessionId) as TurnRow[]
}

/** The bytes a compressed turn row holds, in the form the writer must produce. */
function compressedTurn(turn: Record<string, unknown>): Uint8Array {
  return deflateSync(Buffer.from(JSON.stringify(turn), 'utf8'))
}

/** The plain JSON a turn row held before the compression landed. */
function plainTurn(turn: Record<string, unknown>): string {
  return JSON.stringify(turn)
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

describe('records.parts_json is stored compressed and read back decompressed', () => {
  it('stores the parts as deflated bytes, not as the JSON text', () => {
    // Characterization: the column is the compressed form today, and the
    // compression is the point — the column is a fraction of the JSON it
    // holds, which is what the detail and digest paths stop transferring.
    const store = new CanonStore(':memory:')
    try {
      store.upsertMany([record({ parts: EXOTIC_PARTS })])

      const stored = storedParts(store, 'span-1')
      expect(stored).toBeInstanceOf(Uint8Array)
      expect(deflatedValue(stored)).toEqual(EXOTIC_PARTS)
      expect(Buffer.from(stored as Uint8Array).length).toBeLessThan(
        JSON.stringify(EXOTIC_PARTS).length,
      )
    } finally {
      store.close()
    }
  })

  it('reads the parts back byte-identical: unicode, quotes, a long string, an empty string, a null', () => {
    // The round trip is the whole contract: a round trip that loses a
    // character, a quote, an empty string or a null is a lossy store.
    const store = new CanonStore(':memory:')
    try {
      store.upsertMany([record({ parts: EXOTIC_PARTS })])

      const read = store.get('span-1')
      expect(read?.parts).toEqual(EXOTIC_PARTS)
      expect(JSON.stringify(read?.parts)).toBe(JSON.stringify(EXOTIC_PARTS))
      // The content derived from the parts is the same content the parts
      // name: the collapsed form is what every pre-parts consumer reads.
      expect(read?.content).toEqual(contentFromParts(EXOTIC_PARTS))
    } finally {
      store.close()
    }
  })

  it('stores content that does not benefit as-is, and reads it back correctly', () => {
    // An empty parts array is 2 bytes of JSON and 10 bytes deflated:
    // compressing it costs more than it saves, so the column keeps the plain
    // form and the reader reads that form back.
    const store = new CanonStore(':memory:')
    try {
      store.upsertMany([record({ parts: [], content: { system_prompt: 'kept' } })])

      const stored = storedParts(store, 'span-1')
      expect(typeof stored, 'content that does not benefit is stored as-is').toBe('string')
      expect(stored).toBe('[]')
      expect(store.get('span-1')?.parts).toEqual([])
      expect(store.get('span-1')?.content).toEqual({ system_prompt: 'kept' })
    } finally {
      store.close()
    }
  })

  it('reads a row written before the compression: a parts column holding plain JSON', () => {
    // The migration tolerance: a row written before the compression landed
    // holds the plain JSON in the column. The row's `content_json` is '{}'
    // (parts are the authority), so a reader that falls back to it loses the
    // parts entirely — the reader must read the plain form itself.
    const store = new CanonStore(':memory:')
    try {
      store.upsertMany([record({ parts: EXOTIC_PARTS })])
      store
        .getDatabase()
        .prepare('UPDATE records SET parts_json = ? WHERE span_id = ?')
        .run(JSON.stringify(EXOTIC_PARTS), 'span-1')

      const read = store.get('span-1')
      expect(read?.parts).toEqual(EXOTIC_PARTS)
      expect(JSON.stringify(read?.parts)).toBe(JSON.stringify(EXOTIC_PARTS))
      expect(read?.content).toEqual(contentFromParts(EXOTIC_PARTS))
    } finally {
      store.close()
    }
  })

  it('does not compress a column that already holds compressed data a second time', () => {
    // The column holds the deflated bytes; rewriting the same parts into it
    // must not deflate the deflated bytes again — the stored bytes stay one
    // deflate layer, and the value they hold is still the parts.
    const store = new CanonStore(':memory:')
    try {
      store.upsertMany([record({ parts: EXOTIC_PARTS })])
      const first = storedParts(store, 'span-1')

      store.setContent('span-1', {}, EXOTIC_PARTS)
      const second = storedParts(store, 'span-1')

      expect(Buffer.from(second as Uint8Array)).toEqual(Buffer.from(first as Uint8Array))
      expect(deflatedValue(second)).toEqual(EXOTIC_PARTS)
    } finally {
      store.close()
    }
  })
})

describe('session_turn.turn_json is stored compressed and read back decompressed', () => {
  it('stores each turn compressed, not as plain JSON', () => {
    // The turn store's whole point is that the split does not give its
    // storage win back: a turn row holds the deflated turn, so the 40,621
    // turn rows of the corpus cost what the compressed form costs.
    const store = new CanonStore(':memory:')
    try {
      writeSession(store, 'sess-turns', TURNS)

      const rows = turnRows(store, 'sess-turns')
      expect(rows).toHaveLength(3)
      for (const row of rows) {
        const turn = TURNS.find((candidate) => candidate['index'] === row.turn_index)
        expect(turn, `a row for turn index ${String(row.turn_index)} must exist`).toBeDefined()
        expect(row.turn_json, 'a turn row holds the deflated turn').toBeInstanceOf(Uint8Array)
        expect(deflatedValue(row.turn_json)).toEqual(turn)
      }
    } finally {
      store.close()
    }
  })

  it('reads a compressed turn row back through the session payload', () => {
    // The reader half: a turn row written by the compressed writer must come
    // back as the turn it holds. A reader that parses the column as text
    // drops the turn, and a session's turns come back as nothing.
    const store = new CanonStore(':memory:')
    try {
      writeSession(store, 'sess-compressed', TURNS)
      rewriteTurnRows(store, 'sess-compressed', TURNS, compressedTurn)

      const payload = store.getSessionPayload('sess-compressed') as {
        turns: Array<Record<string, unknown>>
      }
      expect(payload.turns).toEqual(TURNS)
    } finally {
      store.close()
    }
  })

  it('reads a turn row written before the compression: plain JSON in the column', () => {
    // The migration tolerance, on the turn store: every turn row written
    // before the compression landed holds the plain JSON, and the reader must
    // read it — not drop the turn and serve an empty session.
    const store = new CanonStore(':memory:')
    try {
      writeSession(store, 'sess-plain', TURNS)
      rewriteTurnRows(store, 'sess-plain', TURNS, plainTurn)

      const payload = store.getSessionPayload('sess-plain') as {
        turns: Array<Record<string, unknown>>
      }
      expect(payload.turns).toEqual(TURNS)
    } finally {
      store.close()
    }
  })

  it('serves the same payload for a compressed turn row and a plain one', () => {
    // The ASAD payload contract: the compression is invisible to the reader.
    // The same session, stored compressed and stored plain, answers with the
    // same bytes.
    const compressed = new CanonStore(':memory:')
    const plain = new CanonStore(':memory:')
    try {
      writeSession(compressed, 'sess-x', TURNS)
      writeSession(plain, 'sess-x', TURNS)
      rewriteTurnRows(plain, 'sess-x', TURNS, plainTurn)

      expect(JSON.stringify(compressed.getSessionPayload('sess-x'))).toBe(
        JSON.stringify(plain.getSessionPayload('sess-x')),
      )
    } finally {
      compressed.close()
      plain.close()
    }
  })
})
