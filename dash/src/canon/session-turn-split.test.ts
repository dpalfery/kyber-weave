import { inflateSync } from 'node:zlib'
import { describe, expect, it } from 'vitest'

import type { CanonicalRecord } from './types.js'
import { CanonStore } from './store.js'

// RED-phase contract for the writer half of the session-turn split (GREEN lands
// in store.ts `upsertSession`): the projection writer must split each session
// as it writes it — the `session` row carries the metadata payload (no
// `turns`), and each turn becomes a `session_turn` row.
//
// The fixture technique mirrors migration-v22.test.ts: hand-built payloads with
// the projection's key order (`turns` appended last), so the byte-identical
// assertions below mean what the migration's own `withoutTurns` check means.
// Until GREEN, every split assertion below fails the same way the migration
// describes the old behavior — the whole payload (turns included) is stored in
// `session`, and `session_turn` stays empty — which is exactly the missing
// behavior the sibling worker implements. Only this test file changes.

const THREE_TURNS: Array<Record<string, unknown>> = [
  {
    index: 0,
    spanId: 'span-turn-0',
    timestamp: '2026-10-14T10:01:00.000Z',
    model: 'opus-4-1',
    input: 1200,
    output: 300,
  },
  {
    index: 1,
    spanId: 'span-turn-1',
    timestamp: '2026-10-14T10:04:00.000Z',
    model: 'opus-4-1',
    input: 2400,
    output: 410,
  },
  {
    index: 2,
    spanId: 'span-turn-2',
    timestamp: '2026-10-14T10:09:00.000Z',
    model: 'opus-4-1',
    input: 1800,
    output: 220,
  },
]

/**
 * A projection-shaped session payload: the whole session in one object, turns
 * inline under the top-level `turns` key, the window provenance under
 * `context.contextLimitSource`. `turns` is appended after every other key so
 * the key order matches the projection's emission order.
 */
function sessionPayload(
  sessionId: string,
  turns?: Array<Record<string, unknown>>,
  contextLimitSource = 'reported',
): Record<string, unknown> {
  const payload: Record<string, unknown> = {
    id: sessionId,
    session_id: sessionId,
    harness: 'claude-code',
    label: `Session ${sessionId}`,
    agent_name: 'architect',
    repo: 'kyber-weave',
    branch: 'main',
    span_count: 3,
    summary: { turn_count: turns?.length ?? 0, total_input: 100, total_output: 50 },
    context: { contextLimitSource, first: { pressure: 0.1 }, last: { pressure: 0.4 } },
    tools: [{ name: 'Bash', invocations: 2 }],
    schema: { measurable: true },
    timeline: [{ spanId: 'span-root', name: 'root' }],
    requests: [{ request: 'span-root', timestamp: '2026-10-14T10:00:00.000Z' }],
    servers: [],
    coverage: { schema: 1, context: 1 },
    problems: [],
    cost: { basis: 'unknown', status: 'no_rate' },
    reconciliation: [],
    subagents: [],
    auxiliary: {},
    skills: {},
    measurability: {},
  }
  if (turns !== undefined) payload.turns = turns
  return payload
}

function writeSession(
  store: CanonStore,
  sessionId: string,
  payload: Record<string, unknown>,
  harness = 'claude-code',
): void {
  store.upsertSession({
    sessionId,
    harness,
    label: `Session ${sessionId}`,
    isSubagent: false,
    parentSession: null,
    agentName: 'architect',
    repo: 'kyber-weave',
    branch: 'main',
    started: '2026-10-14T10:00:00.000Z',
    ended: '2026-10-14T10:30:00.000Z',
    payload,
  })
}

/** The original payload with only the `turns` key removed, key order otherwise preserved. */
function withoutTurns(original: string): string {
  const parsed = JSON.parse(original) as Record<string, unknown>
  delete parsed.turns
  return JSON.stringify(parsed)
}

function storedPayloadText(store: CanonStore, sessionId: string): string {
  const row = store
    .getDatabase()
    .prepare('SELECT payload FROM session WHERE session_id = ?')
    .get(sessionId) as { payload: string } | undefined
  expect(row, `session ${sessionId} must be stored`).toBeDefined()
  return row!.payload
}

type TurnRow = { session_id: string; turn_index: number; turn_json: unknown }

function turnRows(store: CanonStore, sessionId: string): TurnRow[] {
  return store
    .getDatabase()
    .prepare('SELECT session_id, turn_index, turn_json FROM session_turn WHERE session_id = ? ORDER BY turn_index')
    .all(sessionId) as TurnRow[]
}

/**
 * The turn a stored row holds, read back from the deflated bytes.
 *
 * The row holds the turn's JSON deflated, so inflating it is the read the
 * store's own reader makes. A row that does not inflate is not a turn, and
 * the assertion fails rather than reading an empty turn.
 */
function inflatedTurn(stored: unknown): unknown {
  if (typeof stored === 'string') return JSON.parse(stored)
  return JSON.parse(inflateSync(Buffer.from(stored as Uint8Array)).toString('utf8'))
}

function contextLimitSourceOf(store: CanonStore, sessionId: string): string | null {
  const row = store
    .getDatabase()
    .prepare('SELECT context_limit_source FROM session WHERE session_id = ?')
    .get(sessionId) as { context_limit_source: string | null } | undefined
  expect(row, `session ${sessionId} must be stored`).toBeDefined()
  return row!.context_limit_source
}

function recordForWipe(sessionId: string, harness: string): CanonicalRecord {
  return {
    spanId: `span-wipe-${sessionId}`,
    traceId: `trace-wipe-${sessionId}`,
    parentSpanId: null,
    source: harness,
    harness,
    sessionId,
    name: 'llm_request',
    op: 'llm.invoke',
    kind: 'client',
    timestamp: '2026-10-14T10:00:00.000Z',
    durationMs: 100,
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
  }
}

describe('session writer splits turns into session_turn', () => {
  it('splits a written session: metadata payload without turns, one row per turn', () => {
    // RED: the writer stores the whole payload (turns included) in `session`
    // and writes no `session_turn` rows. After GREEN the payload keeps every
    // other top-level key byte-identical and each turn lands in its own row.
    const store = new CanonStore(':memory:')
    try {
      const payload = sessionPayload('sess-split', THREE_TURNS)
      writeSession(store, 'sess-split', payload)

      // The session row's payload has no `turns` key; every other top-level
      // key is byte-identical to what the projection built, in key order.
      expect(storedPayloadText(store, 'sess-split')).toBe(withoutTurns(JSON.stringify(payload)))

      // Exactly one row per turn, each with the turn's own index and the
      // turn's JSON — stored deflated, so the split's storage win is not
      // given back by uncompressed turn JSON. The stored bytes are one
      // deflate layer: inflating them yields the turn the projection built.
      const rows = turnRows(store, 'sess-split')
      expect(rows).toHaveLength(3)
      for (const turn of THREE_TURNS) {
        const match = rows.find((row) => row.turn_index === turn.index)
        expect(match, `session_turn must hold a row for turn index ${String(turn.index)}`).toBeDefined()
        expect(match!.session_id).toBe('sess-split')
        expect(match!.turn_json).toBeInstanceOf(Uint8Array)
        expect(inflatedTurn(match!.turn_json)).toEqual(turn)
      }
    } finally {
      store.close()
    }
  })

  it('leaves the non-turn keys of a stored payload unchanged', () => {
    // Characterization: passes today (the writer stores the payload verbatim)
    // and must keep passing after GREEN — the split removes `turns` and moves
    // nothing else.
    const store = new CanonStore(':memory:')
    try {
      const payload = sessionPayload('sess-keys', THREE_TURNS)
      writeSession(store, 'sess-keys', payload)

      const stored = JSON.parse(storedPayloadText(store, 'sess-keys')) as Record<string, unknown>
      const { turns: _dropped, ...storedRest } = stored
      const { turns: _alsoDropped, ...payloadRest } = payload
      expect(storedRest).toEqual(payloadRest)
    } finally {
      store.close()
    }
  })

  it('falls back to the array position for a turn carrying no numeric index', () => {
    // RED for the same missing split: today no row exists at all. After GREEN
    // the index-less turn at position 1 is stored under `turn_index` 1.
    const store = new CanonStore(':memory:')
    try {
      const turns: Array<Record<string, unknown>> = [
        { index: 0, spanId: 'span-fallback-0', input: 100 },
        { spanId: 'span-fallback-noidx', input: 200 },
      ]
      writeSession(store, 'sess-fallback', sessionPayload('sess-fallback', turns))

      const rows = turnRows(store, 'sess-fallback')
      expect(rows).toHaveLength(2)
      expect(rows[1]!.turn_index).toBe(1)
      expect(rows[1]!.turn_json).toBe(JSON.stringify(turns[1]))
    } finally {
      store.close()
    }
  })

  it('replaces a session turn rows wholesale on re-upsert', () => {
    // RED: today no turn rows exist, so the rewrite holds none of the new
    // turns. After GREEN exactly the new turns remain — no stale rows from
    // the first write, no duplicates.
    const store = new CanonStore(':memory:')
    try {
      writeSession(store, 'sess-rewrite', sessionPayload('sess-rewrite', THREE_TURNS))

      const replacement = [{ index: 0, spanId: 'span-new-0', input: 50, output: 5 }]
      writeSession(store, 'sess-rewrite', sessionPayload('sess-rewrite', replacement))

      const rows = turnRows(store, 'sess-rewrite')
      expect(rows).toHaveLength(1)
      expect(rows[0]).toMatchObject({ session_id: 'sess-rewrite', turn_index: 0 })
      // The round-trip value, not the stored form: this turn's JSON is 55
      // bytes, exactly on compressColumn's tie — macOS zlib squeezes it to 53
      // (deflated wins) while linux zlib ties at 55 (plain wins) — and the
      // readers accept both forms, so the form is not a contract.
      expect(inflatedTurn(rows[0]!.turn_json)).toEqual(replacement[0])
    } finally {
      store.close()
    }
  })

  it('writes context_limit_source from the payload context, NULL when the payload has none', () => {
    // RED on the populated half: the migration backfills existing rows only,
    // so a newly written session reads NULL for the column today. After GREEN
    // the writer materializes it from `$.context.contextLimitSource`.
    const store = new CanonStore(':memory:')
    try {
      writeSession(store, 'sess-reported', sessionPayload('sess-reported', THREE_TURNS, 'reported'))
      expect(contextLimitSourceOf(store, 'sess-reported')).toBe('reported')

      // Characterization: a payload with no window provenance stores NULL,
      // never a fabricated value. Passes today and must keep passing.
      const { context: _context, ...withoutContext } = sessionPayload('sess-nocontext', THREE_TURNS)
      writeSession(store, 'sess-nocontext', withoutContext)
      expect(contextLimitSourceOf(store, 'sess-nocontext')).toBeNull()
    } finally {
      store.close()
    }
  })

  it('deletes the turn rows with deleteSession and with the harness-scoped delete', () => {
    // The writer provides the rows, so the deletes are exercised against the
    // true integrated state: both deletion paths remove the session's turn
    // rows while leaving other sessions' rows alone.
    const store = new CanonStore(':memory:')
    try {
      writeSession(store, 'sess-gone', sessionPayload('sess-gone', THREE_TURNS), 'copilot')
      writeSession(store, 'sess-kept', sessionPayload('sess-kept', THREE_TURNS.slice(0, 1)), 'other')
      writeSession(store, 'sess-harness-gone', sessionPayload('sess-harness-gone', THREE_TURNS), 'copilot')
      expect(turnRows(store, 'sess-gone')).toHaveLength(3)

      store.deleteSession('sess-gone')
      expect(turnRows(store, 'sess-gone')).toHaveLength(0)

      store.deleteSessionsByHarness('copilot')
      expect(turnRows(store, 'sess-harness-gone')).toHaveLength(0)

      expect(turnRows(store, 'sess-kept')).toHaveLength(1)
    } finally {
      store.close()
    }
  })

  it('deletes the turn rows with wipeAll', () => {
    // The writer provides the rows: `wipeAll` must clear the `session` rows
    // and every `session_turn` row with them.
    const store = new CanonStore(':memory:')
    try {
      writeSession(store, 'sess-wiped', sessionPayload('sess-wiped', THREE_TURNS))
      expect(turnRows(store, 'sess-wiped')).toHaveLength(3)

      store.wipeAll()
      const remaining = store
        .getDatabase()
        .prepare('SELECT COUNT(*) AS n FROM session_turn')
        .get() as { n: number }
      expect(remaining.n).toBe(0)
    } finally {
      store.close()
    }
  })

  it('deletes the turn rows with wipeHarnesses', () => {
    // The writer provides the rows: the harness wipe removes the wiped
    // harness's turn rows with its session rows while other harnesses' rows
    // survive. The `records` row is required — the wipe path early-returns
    // for a harness with no records.
    const store = new CanonStore(':memory:')
    try {
      store.upsertMany([recordForWipe('sess-wh', 'copilot')])
      writeSession(store, 'sess-wh', sessionPayload('sess-wh', THREE_TURNS), 'copilot')
      writeSession(store, 'sess-wh-kept', sessionPayload('sess-wh-kept', THREE_TURNS.slice(0, 1)), 'other')
      expect(turnRows(store, 'sess-wh')).toHaveLength(3)

      store.wipeHarnesses(['copilot'])
      expect(turnRows(store, 'sess-wh')).toHaveLength(0)
      expect(turnRows(store, 'sess-wh-kept')).toHaveLength(1)
    } finally {
      store.close()
    }
  })

  it('drops an empty turns array: no turn rows and no turns key', () => {
    // RED: today the empty array is stored verbatim in the payload. After
    // GREEN the key is removed even when its value was an empty array, and
    // no turn rows exist.
    const store = new CanonStore(':memory:')
    try {
      writeSession(store, 'sess-empty', sessionPayload('sess-empty', []))

      expect(turnRows(store, 'sess-empty')).toHaveLength(0)
      const stored = JSON.parse(storedPayloadText(store, 'sess-empty')) as Record<string, unknown>
      expect(stored).not.toHaveProperty('turns')
    } finally {
      store.close()
    }
  })

  it('writes a payload with no turns key with no turn rows and the payload byte-identical', () => {
    // Characterization: a session that carries no `turns` key at all has
    // nothing to split. Passes today and must keep passing after GREEN.
    const store = new CanonStore(':memory:')
    try {
      const payload = sessionPayload('sess-noturns')
      delete payload.turns
      const original = JSON.stringify(payload)
      writeSession(store, 'sess-noturns', payload)

      expect(turnRows(store, 'sess-noturns')).toHaveLength(0)
      expect(storedPayloadText(store, 'sess-noturns')).toBe(original)
    } finally {
      store.close()
    }
  })
})

describe('session turn read failure handling', () => {
  it('rethrows a turn-table read error instead of serving the session with no turns', async () => {
    const { CanonStore: Store } = await import('./store.js')
    const store = new Store(':memory:')
    try {
      const payload = sessionPayload('sess-err', THREE_TURNS)
      writeSession(store as unknown as CanonStore, 'sess-err', payload)
      const db = (store as unknown as { getDatabase: () => { prepare: (sql: string) => unknown } }).getDatabase()
      const originalPrepare = db.prepare.bind(db)
      db.prepare = ((sql: string) => {
        if (typeof sql === 'string' && sql.includes('FROM session_turn')) {
          throw new Error('database is locked')
        }
        return originalPrepare(sql)
      }) as typeof db.prepare
      try {
        expect(() => (store as unknown as { getSessionPayload: (id: string) => unknown }).getSessionPayload('sess-err')).toThrow(
          /database is locked/,
        )
      } finally {
        db.prepare = originalPrepare
      }
    } finally {
      store.close()
    }
  })
})
