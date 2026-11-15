// Unchanged-write avoidance (F1): every projection re-upserts every derived
// session, turn, run and execution, so the upserts themselves must prove a
// re-derived row identical before touching it. These tests pin the write
// behavior directly — an unchanged 100-turn re-upsert (the investigation's
// 201-row repro) must write zero rows, a change to one session must not
// rewrite any other session's rows, and deletion must still remove derived
// data. Write observation uses triggers, the same technique
// `projection.test.ts` uses to pin records-table idempotence.

import type { DatabaseSync } from 'node:sqlite'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from './store.js'
import { deriveOutcome } from './outcome.js'
import { projectCanonicalStore } from './projection.js'
import type { CanonicalRecord, TokenUsage } from './types.js'
import type { ExecutionRow, RunRow, SessionRow } from './types.js'

const stores: CanonStore[] = []

function openStore(): CanonStore {
  const store = new CanonStore(':memory:')
  stores.push(store)
  return store
}

function dbOf(store: CanonStore): DatabaseSync {
  return (store as unknown as { db: DatabaseSync }).db
}

afterEach(() => {
  for (const store of stores.splice(0)) store.close()
})

type WriteEntry = { target: string; id: string }

/**
 * Record every derived-table write with the row's owner, so a test can say
 * not just "something wrote" but "only this session wrote". Installed after
 * seeding: the turn table is materialized on demand, so its triggers can
 * only be created once a session has been written.
 */
function watchDerivedWrites(store: CanonStore): { entries: () => WriteEntry[]; clear: () => void } {
  const db = dbOf(store)
  db.exec('CREATE TABLE write_log (target TEXT, id TEXT)')
  db.exec("CREATE TRIGGER log_session_write AFTER INSERT ON session BEGIN INSERT INTO write_log VALUES ('session', NEW.session_id); END")
  db.exec("CREATE TRIGGER log_session_update AFTER UPDATE ON session BEGIN INSERT INTO write_log VALUES ('session', NEW.session_id); END")
  db.exec("CREATE TRIGGER log_session_delete AFTER DELETE ON session BEGIN INSERT INTO write_log VALUES ('session', OLD.session_id); END")
  // The turn table is materialized on demand by the session write path, so
  // a store that never held a turn row has no table to trigger on yet.
  const hasTurnTable = db.prepare("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'session_turn'").get() !== undefined
  if (hasTurnTable) {
    db.exec("CREATE TRIGGER log_turn_write AFTER INSERT ON session_turn BEGIN INSERT INTO write_log VALUES ('turn', NEW.session_id); END")
    db.exec("CREATE TRIGGER log_turn_update AFTER UPDATE ON session_turn BEGIN INSERT INTO write_log VALUES ('turn', NEW.session_id); END")
    db.exec("CREATE TRIGGER log_turn_delete AFTER DELETE ON session_turn BEGIN INSERT INTO write_log VALUES ('turn', OLD.session_id); END")
  }
  db.exec("CREATE TRIGGER log_run_write AFTER INSERT ON run BEGIN INSERT INTO write_log VALUES ('run', NEW.run_id); END")
  db.exec("CREATE TRIGGER log_run_update AFTER UPDATE ON run BEGIN INSERT INTO write_log VALUES ('run', NEW.run_id); END")
  db.exec("CREATE TRIGGER log_run_delete AFTER DELETE ON run BEGIN INSERT INTO write_log VALUES ('run', OLD.run_id); END")
  db.exec("CREATE TRIGGER log_exec_write AFTER INSERT ON execution BEGIN INSERT INTO write_log VALUES ('execution', NEW.execution_id); END")
  db.exec("CREATE TRIGGER log_exec_update AFTER UPDATE ON execution BEGIN INSERT INTO write_log VALUES ('execution', NEW.execution_id); END")
  db.exec("CREATE TRIGGER log_exec_delete AFTER DELETE ON execution BEGIN INSERT INTO write_log VALUES ('execution', OLD.execution_id); END")
  return {
    entries: () => db.prepare('SELECT target, id FROM write_log').all() as WriteEntry[],
    clear: () => {
      db.exec('DELETE FROM write_log')
    },
  }
}

function turnsFor(sessionId: string, count: number): Array<Record<string, unknown>> {
  return Array.from({ length: count }, (_, index) => ({ index, text: `turn ${index} of ${sessionId}` }))
}

function sessionRow(sessionId: string, turns: Array<Record<string, unknown>>): SessionRow {
  return {
    sessionId,
    harness: 'test-harness',
    label: `session ${sessionId}`,
    started: '2026-10-20T00:00:00.000Z',
    ended: '2026-10-20T01:00:00.000Z',
    payload: { session: sessionId, turns },
  }
}

describe('upsertSession unchanged-write avoidance', () => {
  it('writes zero rows when re-upserting an unchanged 100-turn session', () => {
    // The investigation's repro: re-upserting one unchanged 100-turn session
    // caused 201 SQL row changes (the session row plus a delete and 100
    // inserts of its turns). The guard must make the second upsert a pure
    // read.
    const store = openStore()
    store.upsertSession(sessionRow('unchanged-100', turnsFor('unchanged-100', 100)))
    const watch = watchDerivedWrites(store)

    store.upsertSession(sessionRow('unchanged-100', turnsFor('unchanged-100', 100)))

    expect(watch.entries()).toEqual([])
  })

  it('rewrites only the changed session when one session gains a turn', () => {
    const store = openStore()
    store.upsertSession(sessionRow('session-a', turnsFor('session-a', 100)))
    store.upsertSession(sessionRow('session-b', turnsFor('session-b', 100)))
    const before = store.getSessionPayload('session-b')
    const watch = watchDerivedWrites(store)

    const grown = turnsFor('session-a', 100)
    grown.push({ index: 100, text: 'one new turn' })
    store.upsertSession(sessionRow('session-a', grown))
    store.upsertSession(sessionRow('session-b', turnsFor('session-b', 100)))

    // Every row written names the changed session; the untouched session's
    // rows were read, never replaced.
    const entries = watch.entries()
    expect(entries.length).toBeGreaterThan(0)
    expect(new Set(entries.map((entry) => entry.id))).toEqual(new Set(['session-a']))
    expect(store.getSessionPayload('session-b')).toEqual(before)
  })

  it('rewrites a changed turn in place and then converges to zero writes', () => {
    const store = openStore()
    store.upsertSession(sessionRow('edited', turnsFor('edited', 10)))
    const watch = watchDerivedWrites(store)

    const edited = turnsFor('edited', 10)
    edited[4] = { index: 4, text: 'rewritten turn' }
    store.upsertSession(sessionRow('edited', edited))
    expect(watch.entries().length).toBeGreaterThan(0)

    watch.clear()
    store.upsertSession(sessionRow('edited', edited))
    expect(watch.entries()).toEqual([])
  })

  it('preserves the sync job columns across a skipped re-upsert', () => {
    // The upsert never names the transcript-sync columns, so skipping the
    // write trivially preserves them — but a regression that rewrote the row
    // with a plain REPLACE would reset them, so the skip is pinned through
    // the columns' survival across both a write and a skip.
    const store = openStore()
    store.upsertSession(sessionRow('synced', turnsFor('synced', 3)))
    dbOf(store).exec(
      "UPDATE session SET transcript_path = '/tmp/transcript.jsonl', transcript_offset = 42 WHERE session_id = 'synced'",
    )
    const watch = watchDerivedWrites(store)

    store.upsertSession(sessionRow('synced', turnsFor('synced', 3)))

    expect(watch.entries()).toEqual([])
    const row = dbOf(store).prepare('SELECT transcript_path, transcript_offset FROM session WHERE session_id = ?').get('synced') as {
      transcript_path: unknown
      transcript_offset: unknown
    }
    expect(row).toMatchObject({ transcript_path: '/tmp/transcript.jsonl', transcript_offset: 42 })
  })

  it('migrates a legacy inline-turns row once, then leaves it alone', () => {
    // A row written before the turn split carries its turns inline and owns
    // no turn rows: the first re-upsert still rewrites it into the split
    // shape, and only then does the guard converge to zero writes.
    const store = openStore()
    const legacy = { session: 'legacy', turns: turnsFor('legacy', 5) }
    dbOf(store).prepare('INSERT INTO session (session_id, harness, payload) VALUES (?, ?, ?)').run(
      'legacy',
      'test-harness',
      JSON.stringify(legacy),
    )
    // The turn table must exist before its trigger can be created; the
    // migration write below is what materializes it.
    store.upsertSession(sessionRow('other', turnsFor('other', 1)))
    const watch = watchDerivedWrites(store)

    store.upsertSession(sessionRow('legacy', turnsFor('legacy', 5)))
    expect(watch.entries().length).toBeGreaterThan(0)

    watch.clear()
    store.upsertSession(sessionRow('legacy', turnsFor('legacy', 5)))
    expect(watch.entries()).toEqual([])
    // The migrated session still serves its turns.
    expect(store.getSessionPayload('legacy')).toMatchObject({ session: 'legacy' })
  })
})

describe('upsertRun and upsertExecution unchanged-write avoidance', () => {
  function runRow(runId: string): RunRow {
    return {
      runId,
      harness: 'test-harness',
      label: `run ${runId}`,
      groupingBasis: 'derived',
      groupingRule: 'session_fallback',
      workingDirectory: null,
      started: '2026-10-20T00:00:00.000Z',
      ended: '2026-10-20T01:00:00.000Z',
      executionCount: 1,
    }
  }

  function executionRow(executionId: string, runId: string): ExecutionRow {
    return {
      executionId,
      runId,
      sessionId: 'session-a',
      parentExecutionId: null,
      harness: 'test-harness',
      agentName: 'agent',
      isRoot: true,
      started: '2026-10-20T00:00:00.000Z',
      ended: '2026-10-20T01:00:00.000Z',
      parentLinkage: 'measured',
    }
  }

  it('writes zero rows when re-upserting unchanged runs and executions', () => {
    const store = openStore()
    store.upsertRun(runRow('run-1'))
    store.upsertExecution(executionRow('exec-1', 'run-1'))
    const watch = watchDerivedWrites(store)

    store.upsertRun(runRow('run-1'))
    store.upsertExecution(executionRow('exec-1', 'run-1'))

    expect(watch.entries()).toEqual([])
  })

  it('rewrites a run whose outcome changed and then converges', () => {
    const store = openStore()
    const changedOutcome = { ...deriveOutcome([]), status: 'success' as const }
    store.upsertRun(runRow('run-2'))
    const watch = watchDerivedWrites(store)

    store.upsertRun({ ...runRow('run-2'), outcome: changedOutcome })
    expect(watch.entries()).toEqual([{ target: 'run', id: 'run-2' }])

    watch.clear()
    store.upsertRun({ ...runRow('run-2'), outcome: changedOutcome })
    expect(watch.entries()).toEqual([])
  })
})

describe('derived deletion still removes derived data', () => {
  it('deleteSession removes the session row and its turn rows', () => {
    const store = openStore()
    store.upsertSession(sessionRow('doomed', turnsFor('doomed', 4)))
    store.upsertSession(sessionRow('spared', turnsFor('spared', 4)))
    expect(store.getSessionPayload('doomed')).toBeDefined()

    store.deleteSession('doomed')

    expect(store.getSessionPayload('doomed')).toBeUndefined()
    expect(
      (dbOf(store).prepare('SELECT COUNT(*) AS n FROM session_turn WHERE session_id = ?').get('doomed') as { n: number }).n,
    ).toBe(0)
    expect(store.getSessionPayload('spared')).toBeDefined()
  })
})

describe('full re-projection unchanged-write avoidance', () => {
  function usage(): TokenUsage {
    return { freshInput: 10, cacheRead: 0, cacheCreation: 0, output: 4, reportedInput: 10, reportedOutput: 4 }
  }

  function record(spanId: string, sessionId: string): CanonicalRecord {
    return {
      spanId,
      traceId: `trace-${sessionId}`,
      parentSpanId: null,
      sessionId,
      source: 'pi:agent-7f3',
      harness: 'pi',
      name: 'chat turn',
      op: 'llm.invoke',
      kind: 'internal',
      timestamp: '2026-10-19T12:00:00.000Z',
      durationMs: 100,
      status: 'ok',
      tokens: usage(),
      content: { system_prompt: `content of ${spanId}` },
      cost: { basis: 'published', status: 'priced', value: 0.01, currency: 'USD' },
      raw: { attributes: { keep: spanId } },
    }
  }

  it('writes zero session, turn, run and execution rows when re-projecting an unchanged corpus', async () => {
    // The end-to-end version of the investigation's repros: the derivation
    // itself must be deterministic, or the upsert guards below it would
    // never engage and every pass would keep rewriting every session.
    const store = openStore()
    store.upsertMany([record('span-a1', 'session-a'), record('span-a2', 'session-a'), record('span-b1', 'session-b')])
    await projectCanonicalStore(store)
    const watch = watchDerivedWrites(store)

    await projectCanonicalStore(store)

    expect(watch.entries()).toEqual([])
  })

  it('rewrites only the changed session when one session gains a span', async () => {
    // The maintenance repro's shape: two sessions derived, then one span
    // added to one session. The next full projection must not rewrite the
    // unrelated session's rows (the repro showed both sessions rewritten).
    const store = openStore()
    store.upsertMany([record('span-a1', 'session-a'), record('span-b1', 'session-b')])
    await projectCanonicalStore(store)
    // Derived ids are the native session ids here: pin that first, or the
    // skip-unrelated assertion below would be vacuous.
    expect(store.getSessionPayload('session-a')).toBeDefined()
    const before = store.getSessionPayload('session-b')
    expect(before).toBeDefined()
    const watch = watchDerivedWrites(store)

    store.upsertMany([record('span-a2', 'session-a')])
    await projectCanonicalStore(store)

    const entries = watch.entries()
    expect(entries.length).toBeGreaterThan(0)
    for (const entry of entries) {
      if (entry.target === 'session' || entry.target === 'turn') expect(entry.id).not.toBe('session-b')
    }
    expect(store.getSessionPayload('session-b')).toEqual(before)
    // And the changed session did pick up the new span — skipping
    // unrelated rows must never mean skipping the changed one.
    expect(JSON.stringify(store.getSessionPayload('session-a'))).toContain('span-a2')
  })
})
