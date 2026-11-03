import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { KyberBridge } from './bridge.js'

// RED-phase contract for the reader half of the session-turn split (GREEN
// lands in the path that serves a session's turns — the detail payload the
// bridge assembles from a session): reading a session's turns must read the
// `session_turn` rows for that one session — one range scan per session, not
// a per-turn query — and must not select the session's whole `payload` blob
// to do it.
//
// The SQL-interception convention mirrors kyber-session-turn-route.test.ts:
// `vi.spyOn(store.getDatabase(), 'prepare')` taps the same handle the bridge
// reads through. Until GREEN the bridge serves turns out of the stored
// payload blob, so the `session_turn` tap sees nothing (tests 7 and 8 fail)
// while the served turns still equal the projection's turns (test 6 passes by
// characterization). Only this test file changes.

const FEW_TURNS: Array<Record<string, unknown>> = [
  {
    index: 0,
    spanId: 'span-few-0',
    timestamp: '2026-10-14T10:01:00.000Z',
    model: 'opus-4-1',
    input: 1200,
    output: 300,
  },
  {
    index: 1,
    spanId: 'span-few-1',
    timestamp: '2026-10-14T10:04:00.000Z',
    model: 'opus-4-1',
    input: 2400,
    output: 410,
  },
]

const MANY_TURNS: Array<Record<string, unknown>> = Array.from({ length: 8 }, (_, position) => ({
  index: position,
  spanId: `span-many-${String(position)}`,
  timestamp: '2026-10-14T10:01:00.000Z',
  model: 'opus-4-1',
  input: 1000 + position * 100,
  output: 200 + position * 10,
}))

// A blob the bounded read must never return: notes beside the small turn
// rows, so a whole-payload select is observable both in the SQL tap and in
// the response shape.
const HOSTILE_NOTES = 'x'.repeat(50_000)

function sessionPayload(sessionId: string, turns: Array<Record<string, unknown>>): Record<string, unknown> {
  return {
    id: sessionId,
    session_id: sessionId,
    harness: 'copilot',
    label: `Session ${sessionId}`,
    summary: { turn_count: turns.length, total_input: 100, total_output: 50 },
    context: { contextLimitSource: 'reported' },
    turns,
    notes: HOSTILE_NOTES,
  }
}

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
    started: '2026-10-14T12:00:00.000Z',
    ended: '2026-10-14T12:10:00.000Z',
    payload: sessionPayload(sessionId, turns),
  })
}

function preparedStatements(calls: unknown[][]): string[] {
  return calls
    .map((args) => args[0])
    .filter((sql): sql is string => typeof sql === 'string')
}

/** Statements that read the child table — the reader's path, never the blob. */
function sessionTurnStatements(calls: unknown[][]): string[] {
  return preparedStatements(calls).filter((sql) => sql.includes('session_turn'))
}

/** Statements against the parent table only — `session_turn` reads excluded. */
function sessionStatements(calls: unknown[][]): string[] {
  return preparedStatements(calls).filter((sql) => sql.includes('FROM session') && !sql.includes('session_turn'))
}

/** A whole-payload select: the bare `payload` column or `*` off `session`. */
function selectsWholePayload(sql: string): boolean {
  return /SELECT\s+payload\b/i.test(sql) || /SELECT\s+\*/i.test(sql)
}

describe('session turn read comes from session_turn', () => {
  let store: CanonStore
  let bridge: KyberBridge

  beforeEach(() => {
    store = new CanonStore(':memory:')
    writeSession(store, 'sess-few', FEW_TURNS)
    writeSession(store, 'sess-many', MANY_TURNS)
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

  it('serves the session turns equal to the turns the projection built', () => {
    // Characterization: passes today (the turns ride inline in the stored
    // blob) and must keep passing after GREEN — the reassembly serves the
    // same turns, same count, same order, same content.
    const few = bridge.getSessionPayload<{ turns: Array<Record<string, unknown>> }>('sess-few')
    expect(few).not.toBeNull()
    expect(few!.turns).toEqual(FEW_TURNS)

    const many = bridge.getSessionPayload<{ turns: Array<Record<string, unknown>> }>('sess-many')
    expect(many).not.toBeNull()
    expect(many!.turns).toEqual(MANY_TURNS)
  })

  it('reads one session turns with a single session_turn range scan, however many turns it holds', () => {
    // RED: the reader serves turns out of the stored payload blob, so no
    // statement reads `session_turn` at all. After GREEN one range scan per
    // session — `SELECT turn_index, turn_json FROM session_turn WHERE
    // session_id = ? ORDER BY turn_index` — serves any turn count, so the
    // count is exactly one for both sessions and never scales with turns.
    const countTurnStatements = (sessionId: string): number => {
      const spy = vi.spyOn(store.getDatabase(), 'prepare')
      try {
        const payload = bridge.getSessionPayload<{ turns: unknown[] }>(sessionId)
        expect(payload, `session ${sessionId} must be served`).not.toBeNull()
      } finally {
        const calls = [...spy.mock.calls]
        spy.mockRestore()
        return sessionTurnStatements(calls).length
      }
    }

    const fewCount = countTurnStatements('sess-few')
    const manyCount = countTurnStatements('sess-many')
    expect(manyCount).toBe(fewCount)
    expect(fewCount).toBe(1)
    expect(manyCount).toBe(1)
  })

  it('reads the turns without selecting the session whole payload blob', () => {
    // RED: the reader serves `getSessionPayload`, which issues `SELECT
    // payload FROM session`. After GREEN no `FROM session` statement may
    // return the blob — the turns come from the child table, and the
    // metadata read never pages the blob in.
    const spy = vi.spyOn(store.getDatabase(), 'prepare')
    try {
      const payload = bridge.getSessionPayload<{ turns: unknown[] }>('sess-many')
      expect(payload, 'the many-turn session must be served').not.toBeNull()
    } finally {
      const calls = [...spy.mock.calls]
      spy.mockRestore()
      // The tap itself must have observed the read, or the pin is vacuous.
      expect(calls.length).toBeGreaterThan(0)
      expect(sessionStatements(calls).filter(selectsWholePayload)).toEqual([])
    }
  })
})
