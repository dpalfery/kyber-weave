import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  SESSION_PAYLOAD_FRAGMENTS_SELECT,
  SESSION_TURNS_SELECT,
  sessionTurnsOf,
} from '../canon/session-read.js'
import { CanonStore } from '../canon/store.js'
import { KyberBridge } from './bridge.js'

// The contract between the two session readers.
//
// WHY: the store and the bridge each read a session's payload, and they must
// answer alike — the bridge serves the dashboard what the store serves the
// ASAD contract, and a session that reads one way in one place and another
// way in the other is a session nobody can reason about. The readers stay two
// (they read through different handles), so what keeps them honest is not
// convention but these tests: the same session must come back the same from
// both, and both must read it with the same statements.
//
// The statements and the parsers are one definition, in
// `dash/src/canon/session-read.ts`, and both readers import them. That is what
// makes the SQL comparison below a real pin: a reader that grew its own copy
// of a statement would issue different text, and the comparison would fail.
//
// The SQL-interception convention mirrors session-turn-read.test.ts:
// `vi.spyOn(store.getDatabase(), 'prepare')` taps the same handle both readers
// read through, so one tap sees both.

/** The turns a session's own rows hold. */
const SPLIT_TURNS: Array<Record<string, unknown>> = [
  {
    index: 0,
    spanId: 'span-split-0',
    timestamp: '2026-10-20T10:00:00.000Z',
    model: 'opus-4-1',
    input: 1200,
    output: 300,
  },
  {
    index: 1,
    spanId: 'span-split-1',
    timestamp: '2026-10-20T10:04:00.000Z',
    model: 'opus-4-1',
    input: 2400,
    output: 410,
  },
]

/**
 * The turns a payload carries inline.
 *
 * Deliberately not the rows' turns: a session whose payload carries turns and
 * whose rows hold turns too must serve the payload's own — they are the fresh
 * ones. If both sets were the same value, the test could not tell which one
 * the reader took.
 */
const INLINE_TURNS: Array<Record<string, unknown>> = [
  {
    index: 0,
    spanId: 'span-inline-0',
    timestamp: '2026-10-20T11:00:00.000Z',
    model: 'opus-4-1',
    input: 5100,
    output: 900,
  },
  {
    index: 1,
    spanId: 'span-inline-1',
    timestamp: '2026-10-20T11:04:00.000Z',
    model: 'opus-4-1',
    input: 6200,
    output: 1100,
  },
]

/**
 * A projection-shaped session payload.
 *
 * The values stay small and shallow on purpose: the bridge clips what it
 * serves (2,000 characters per string, eight levels deep) and the store does
 * not, so a fixture that crossed either bound would make the two answers
 * differ for a reason that has nothing to do with the reader contract.
 */
function sessionPayload(
  sessionId: string,
  turns?: Array<Record<string, unknown>>,
): Record<string, unknown> {
  const payload: Record<string, unknown> = {
    id: sessionId,
    session_id: sessionId,
    harness: 'claude-code',
    label: `Session ${sessionId}`,
    summary: { turn_count: turns?.length ?? 0, total_input: 3600, total_output: 710 },
    context: { contextLimitSource: 'reported' },
    problems: [],
  }
  if (turns !== undefined) payload['turns'] = turns
  return payload
}

function writeSession(store: CanonStore, sessionId: string, payload: Record<string, unknown>): void {
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

/** Overwrite a session row's stored payload, as a row written before the split holds it. */
function setStoredPayload(
  store: CanonStore,
  sessionId: string,
  payload: Record<string, unknown>,
): void {
  store
    .getDatabase()
    .prepare('UPDATE session SET payload = ? WHERE session_id = ?')
    .run(JSON.stringify(payload), sessionId)
}

function clearTurnRows(store: CanonStore, sessionId: string): void {
  store.getDatabase().prepare('DELETE FROM session_turn WHERE session_id = ?').run(sessionId)
}

/** How many turn rows the session owns. */
function turnRowCount(store: CanonStore, sessionId: string): number {
  const row = store
    .getDatabase()
    .prepare('SELECT COUNT(*) AS n FROM session_turn WHERE session_id = ?')
    .get(sessionId) as { n: number }
  return row.n
}

type ReaderCase = {
  /** What the case is, in words a failure can repeat. */
  name: string
  /** Put the session into the state the case is about. */
  build: (store: CanonStore, sessionId: string) => void
  /** The turn rows the state holds, so a fixture that drifted is caught. */
  turnRows: number
  /** The turns both readers must serve for the state. */
  turns: Array<Record<string, unknown>>
}

/**
 * The four states a session row can be read from.
 *
 * They are the four the split read has to tell apart: turns moved out to
 * their own rows, turns still inline in the payload, no turns anywhere, and
 * turns in both places at once — where the inline ones are the fresh ones and
 * must win.
 */
const READER_CASES: readonly ReaderCase[] = [
  {
    name: 'a split session: the payload without its turns, the rows holding them',
    build: (store, sessionId) => {
      writeSession(store, sessionId, sessionPayload(sessionId, SPLIT_TURNS))
    },
    turnRows: 2,
    turns: SPLIT_TURNS,
  },
  {
    name: 'an unsplit session: the payload still carrying its turns',
    build: (store, sessionId) => {
      writeSession(store, sessionId, sessionPayload(sessionId, SPLIT_TURNS))
      clearTurnRows(store, sessionId)
      setStoredPayload(store, sessionId, sessionPayload(sessionId, INLINE_TURNS))
    },
    turnRows: 0,
    turns: INLINE_TURNS,
  },
  {
    name: 'a session with no turn rows anywhere',
    build: (store, sessionId) => {
      writeSession(store, sessionId, sessionPayload(sessionId))
    },
    turnRows: 0,
    turns: [],
  },
  {
    name: "a session whose payload carries turns and whose rows hold them too",
    build: (store, sessionId) => {
      writeSession(store, sessionId, sessionPayload(sessionId, SPLIT_TURNS))
      setStoredPayload(store, sessionId, sessionPayload(sessionId, INLINE_TURNS))
    },
    turnRows: 2,
    turns: INLINE_TURNS,
  },
]

/** The statements a session read issues against the session tables. */
function sessionReadSql(calls: unknown[][]): string[] {
  return calls
    .map((args) => args[0])
    .filter((sql): sql is string => typeof sql === 'string')
    .filter((sql) => sql.includes('FROM session ') || sql.includes('FROM session_turn '))
}

describe('the shared shape decision', () => {
  it('takes the payload own turns, then the split rows, then an empty list', () => {
    // The rule both readers apply, in one place: the payload's own turns are
    // the fresh ones and win; a payload without them takes the split rows; a
    // session with no turns anywhere is served an empty list, because the
    // projection always writes the key.
    expect(sessionTurnsOf('array', JSON.stringify(INLINE_TURNS), SPLIT_TURNS)).toEqual(INLINE_TURNS)
    expect(sessionTurnsOf('array', JSON.stringify(INLINE_TURNS), undefined)).toEqual(INLINE_TURNS)
    expect(sessionTurnsOf(null, null, SPLIT_TURNS)).toEqual(SPLIT_TURNS)
    expect(sessionTurnsOf(null, null, undefined)).toEqual([])
    // A `turns` key that is not an array names no turn, so the split rows are
    // read instead.
    expect(sessionTurnsOf('object', JSON.stringify({ index: 0 }), SPLIT_TURNS)).toEqual(SPLIT_TURNS)
  })
})

describe('the two session readers answer alike', () => {
  let store: CanonStore
  let bridge: KyberBridge

  beforeEach(() => {
    store = new CanonStore(':memory:')
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

  for (const readerCase of READER_CASES) {
    it(`serves the same payload for ${readerCase.name}`, () => {
      // The both-copies rule, pinned: the store's reader and the bridge's
      // reader answer the same question about the same session, so they answer
      // alike. A reader that read a session differently from the other one is
      // the defect this catches.
      const sessionId = 'sess-compare'
      readerCase.build(store, sessionId)

      // The fixture is what the case says it is: a state that drifted would
      // make the comparison below vacuous.
      expect(turnRowCount(store, sessionId)).toBe(readerCase.turnRows)

      const fromStore = store.getSessionPayload(sessionId)
      const fromBridge = bridge.getSessionPayload(sessionId)

      expect(fromStore, `the store must serve ${readerCase.name}`).toBeDefined()
      expect(fromBridge, `the bridge must serve ${readerCase.name}`).not.toBeNull()
      expect(fromBridge).toEqual(fromStore)

      // And both serve the turns the state holds — the payload's own when it
      // carries them, the rows' when they were moved out, none when there are
      // none anywhere.
      expect((fromStore as { turns: unknown[] }).turns).toEqual(readerCase.turns)
      expect((fromBridge as { turns: unknown[] }).turns).toEqual(readerCase.turns)
    })
  }

  it('reads the same session with the same statements', () => {
    // The shared definitions, seen from the outside. Both readers must issue
    // the same text: a reader that kept its own copy of a statement would
    // issue its own, and the two copies could then drift apart — which is
    // exactly what a test that only checked "both work" would not catch.
    const sessionId = 'sess-sql'
    writeSession(store, sessionId, sessionPayload(sessionId, SPLIT_TURNS))

    const spy = vi.spyOn(store.getDatabase(), 'prepare')
    let storeSql: string[]
    let bridgeSql: string[]
    try {
      expect(store.getSessionPayload(sessionId)).toBeDefined()
      storeSql = sessionReadSql([...spy.mock.calls])

      spy.mockClear()

      expect(bridge.getSessionPayload(sessionId)).not.toBeNull()
      bridgeSql = sessionReadSql([...spy.mock.calls])
    } finally {
      spy.mockRestore()
    }

    // The tap must have observed both reads, or the pin is vacuous.
    expect(storeSql.length).toBeGreaterThan(0)
    expect(bridgeSql.length).toBeGreaterThan(0)
    expect(bridgeSql).toEqual(storeSql)
    // And the text they issue is the shared contract's own.
    expect(storeSql).toEqual([SESSION_PAYLOAD_FRAGMENTS_SELECT, SESSION_TURNS_SELECT])
  })
})
