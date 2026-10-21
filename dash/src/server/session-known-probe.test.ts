import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { KyberBridge } from './bridge.js'

// RED-phase contract for the session-known check: "does this session exist"
// must be answered by a probe of the session table's key, and by nothing else.
//
// The check used to ask the store for the session's payload. That read is the
// split reader: it takes the payload apart and hands over every turn of the
// session, reassembled out of `session_turn` -- 10,933 of them on the corpus's
// largest session -- to answer a fact the session row's primary key already
// carries. An existence check that reads the session's content costs as much
// as a full read of that session.
//
// The SQL-interception convention mirrors session-turn-read.test.ts:
// `vi.spyOn(store.getDatabase(), 'prepare')` taps the handle the bridge and the
// store both read through, so every statement either of them issues is seen.

/** The existence probe: a seek on the session table's primary key. */
const EXISTENCE_PROBE = /SELECT\s+1\s+FROM\s+session\s+WHERE\s+session_id/i

const TURNS: Array<Record<string, unknown>> = [
  {
    index: 0,
    spanId: 'span-turn-0',
    timestamp: '2026-10-20T10:00:00.000Z',
    model: 'opus-4-1',
    input: 1200,
    output: 300,
  },
  {
    index: 1,
    spanId: 'span-turn-1',
    timestamp: '2026-10-20T10:04:00.000Z',
    model: 'opus-4-1',
    input: 2400,
    output: 410,
  },
]

/**
 * Writes one derived session, with or without turns.
 *
 * <remarks>
 * A payload with a `turns` key is split by the writer: the turns move into
 * `session_turn` and the session row keeps the metadata. A payload with an
 * empty `turns` array leaves the session with no turn rows at all -- the case
 * a naive fix that only looked at the turn table would miss.
 */
function writeSession(store: CanonStore, sessionId: string, turns: Array<Record<string, unknown>>): void {
  store.upsertSession({
    sessionId,
    harness: 'copilot',
    label: `Session ${sessionId}`,
    isSubagent: false,
    parentSession: null,
    agentName: null,
    repo: null,
    branch: null,
    started: '2026-10-20T12:00:00.000Z',
    ended: '2026-10-20T12:10:00.000Z',
    payload: {
      id: sessionId,
      session_id: sessionId,
      harness: 'copilot',
      label: `Session ${sessionId}`,
      summary: { turn_count: turns.length, total_input: 100, total_output: 50 },
      timeline: [{ at: '2026-10-20T10:00:00.000Z', label: 'start' }],
      turns,
    },
  })
}

function preparedStatements(calls: unknown[][]): string[] {
  return calls
    .map((args) => args[0])
    .filter((sql): sql is string => typeof sql === 'string')
}

/** Statements that name the session's payload -- the blob the check must not read. */
function statementsNamingPayload(calls: unknown[][]): string[] {
  return preparedStatements(calls).filter((sql) => /payload/i.test(sql))
}

/** Statements that read the turn table -- the reassembly the check must not pay for. */
function statementsTouchingTurns(calls: unknown[][]): string[] {
  return preparedStatements(calls).filter((sql) => sql.includes('session_turn'))
}

/** Statements that probe the session table's key. */
function existenceProbes(calls: unknown[][]): string[] {
  return preparedStatements(calls).filter((sql) => EXISTENCE_PROBE.test(sql))
}

describe('the session-known check probes the session table', () => {
  let store: CanonStore
  let bridge: KyberBridge

  beforeEach(() => {
    store = new CanonStore(':memory:')
    writeSession(store, 'sess-with-turns', TURNS)
    writeSession(store, 'sess-no-turns', [])
    bridge = new KyberBridge({ store })
  })

  afterEach(() => {
    try {
      bridge.close()
    } finally {
      try {
        store.close()
      } catch {
        // The bridge already closed the injected handle.
      }
    }
  })

  it('answers the existence check without reading the session payload or its turns', () => {
    // The content route's own work reads records, never the payload, so every
    // statement of this call is either the records read or the existence check
    // -- and the pin is over the whole call.
    const spy = vi.spyOn(store.getDatabase(), 'prepare')
    let calls: unknown[][] = []
    let result: unknown = null
    try {
      result = bridge.getSessionContent('sess-with-turns')
    } finally {
      calls = [...spy.mock.calls]
      spy.mockRestore()
    }

    // The tap must have seen the read, or the pin is vacuous.
    expect(calls.length).toBeGreaterThan(0)
    expect(result, 'the known session must be served').not.toBeNull()
    expect(statementsNamingPayload(calls)).toEqual([])
    expect(statementsTouchingTurns(calls)).toEqual([])
    // The check must still be a check: the probe is what answers it.
    expect(existenceProbes(calls).length).toBeGreaterThan(0)
  })

  it('probes the session table before the per-turn route reads the payload', () => {
    // The per-turn route asks the same question before it reads the session,
    // so the probe has to come first there too: a payload read ahead of it
    // would be the existence check paying for the session again.
    const spy = vi.spyOn(store.getDatabase(), 'prepare')
    let calls: unknown[][] = []
    try {
      bridge.assembleTurnContent('sess-with-turns', 0)
    } finally {
      calls = [...spy.mock.calls]
      spy.mockRestore()
    }

    const statements = preparedStatements(calls)
    const probeIndex = statements.findIndex((sql) => EXISTENCE_PROBE.test(sql))
    expect(probeIndex, 'the existence check must issue the session-table probe').toBeGreaterThanOrEqual(0)
    const beforeProbe = statements.slice(0, probeIndex)
    expect(beforeProbe.filter((sql) => /payload/i.test(sql))).toEqual([])
    expect(beforeProbe.filter((sql) => sql.includes('session_turn'))).toEqual([])
  })

  it('finds a session that exists', () => {
    expect(bridge.getSessionContent('sess-with-turns')).not.toBeNull()
  })

  it('finds a session that exists but holds no turn rows', () => {
    // The fixture has to be the case it claims to be: a session row with no
    // turn rows beside it. A fix that looked at the turn table alone would
    // miss this session entirely.
    const turnRows = store
      .getDatabase()
      .prepare('SELECT COUNT(*) AS n FROM session_turn WHERE session_id = ?')
      .get('sess-no-turns') as { n: number }
    expect(Number(turnRows.n)).toBe(0)

    expect(bridge.getSessionContent('sess-no-turns')).not.toBeNull()
  })

  it('does not find a session that does not exist', () => {
    expect(bridge.getSessionContent('sess-missing')).toBeNull()
  })

  it('leaves the rest of the bridge reads as they were', () => {
    // The detail read still serves the session's turns.
    const payload = bridge.getSessionPayload<{ turns: Array<Record<string, unknown>> }>('sess-with-turns')
    expect(payload).not.toBeNull()
    expect(payload!.turns).toEqual(TURNS)

    // The summary read still answers from the materialized columns.
    const summary = bridge.getSessionSummary('sess-with-turns')
    expect(summary?.session_id).toBe('sess-with-turns')
    expect(summary?.summary).toMatchObject({ turn_count: TURNS.length })

    // The per-turn read still answers from the turn table.
    const figures = bridge.getSessionTurnFigures('sess-with-turns', 1)
    expect(figures).not.toBeNull()
    expect(figures?.index).toBe(1)
    expect(figures?.input).toBe(2400)
    expect(figures?.output).toBe(410)

    // The timeline read still answers from the payload's own timeline.
    const timeline = bridge.getSessionTimeline('sess-with-turns')
    expect(timeline?.timeline).toEqual([{ at: '2026-10-20T10:00:00.000Z', label: 'start' }])

    // The sessions list still lists the sessions.
    const listed = bridge.listSessions().map((session) => session.session_id)
    expect(listed).toContain('sess-with-turns')
    expect(listed).toContain('sess-no-turns')
  })
})
