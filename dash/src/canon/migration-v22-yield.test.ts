import { mkdtempSync, rmSync } from 'node:fs'
import { createRequire } from 'node:module'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from './store.js'

const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}

const dirs: string[] = []

afterEach(() => {
  while (dirs.length > 0) rmSync(dirs.pop()!, { recursive: true, force: true })
})

/**
 * The v21 session table, frozen as it ships: the v20 sync-progress and
 * transcript-progress columns, the v21 materialized summary columns
 * (`summary_json`, `problems_count`), and no `context_limit_source`.
 */
const V21_SESSION_TABLE_SQL = `
CREATE TABLE session (
  session_id TEXT PRIMARY KEY,
  harness TEXT NOT NULL,
  label TEXT,
  is_subagent INTEGER NOT NULL DEFAULT 0,
  parent_session TEXT,
  agent_name TEXT,
  repo TEXT,
  branch TEXT,
  started TEXT,
  ended TEXT,
  synced_at TEXT,
  sync_status TEXT DEFAULT 'pending',
  settled_at TEXT,
  transcript_path TEXT,
  transcript_offset INTEGER DEFAULT 0,
  summary_json TEXT,
  problems_count INTEGER,
  payload TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS session_by_started ON session (started);
`

/** The covering index for the sessions list, exactly as v21 ships it. */
const V21_SESSION_DENORM_INDEX_SQL = `CREATE INDEX IF NOT EXISTS session_denorm_idx
  ON session (started DESC, session_id, harness, label, is_subagent,
              parent_session, agent_name, repo, branch, ended,
              summary_json, problems_count)`

/** One turn of a v21 session payload. */
function turn(index: number): Record<string, unknown> {
  return {
    index,
    spanId: `span-turn-${index}`,
    timestamp: `2026-10-14T10:0${index}:00.000Z`,
    model: 'opus-4-1',
    input: 1200 + index,
    output: 300 + index,
  }
}

/** A v21-shaped session payload: the whole session blob, turns included. */
function v21Payload(sessionId: string, turnCount: number): Record<string, unknown> {
  const payload: Record<string, unknown> = {
    id: sessionId,
    session_id: sessionId,
    harness: 'claude-code',
    label: `Session ${sessionId}`,
    agent_name: 'architect',
    repo: 'kyber-weave',
    branch: 'main',
    span_count: turnCount,
    summary: { turn_count: turnCount, total_input: 1200, total_output: 300 },
    context: { contextLimitSource: 'reported' },
    problems: [],
    schema: { measurable: true },
  }
  payload['turns'] = Array.from({ length: turnCount }, (_, index) => turn(index))
  return payload
}

/**
 * A store at schema version 21, holding `sessionCount` sessions.
 *
 * <remarks>
 * The session table is the v21 shape, the covering index is the v21 one, and
 * the stamp is wound back — a genuine v21 store, which is exactly what the
 * live corpus is: the build that opens it runs the v21 -> v22 step and
 * nothing else.
 */
function v21StoreAt(sessionCount: number): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-v22-yield-'))
  dirs.push(dir)
  const path = join(dir, 'canon.db')

  // The current build's schema, minus the session table: the v21 shape is
  // rebuilt below and the stamp wound back.
  new CanonStore(path).close()

  const db = new DatabaseSync(path)
  try {
    db.exec('DROP TABLE IF EXISTS session;')
    db.exec(V21_SESSION_TABLE_SQL)
    db.exec(V21_SESSION_DENORM_INDEX_SQL)
    db.prepare('UPDATE metadata SET value = ? WHERE key = ?').run('21', 'schema_version')

    const insert = db.prepare(
      `INSERT INTO session (
         session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, payload
       ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    )
    for (let index = 0; index < sessionCount; index += 1) {
      const sessionId = `sess-yield-${index}`
      insert.run(
        sessionId,
        'claude-code',
        `Session ${sessionId}`,
        0,
        null,
        'architect',
        'kyber-weave',
        'main',
        `2026-10-14T${String(10 + index).padStart(2, '0')}:00:00.000Z`,
        `2026-10-14T${String(10 + index).padStart(2, '0')}:30:00.000Z`,
        JSON.stringify(v21Payload(sessionId, 3)),
      )
    }
  } finally {
    db.close()
  }

  return path
}

/**
 * The asynchronous migration entry point.
 *
 * <remarks>
 * Loaded through a dynamic import so the RED failure is this file's
 * assertion and not a module that cannot link: the export lands with the
 * fix. A named import of a missing export fails the whole file at link time,
 * which says nothing about the contract.
 */
async function migrationRunner(): Promise<
  (path: string, options?: { yield?: () => Promise<void> }) => Promise<void>
> {
  const module = await import('./store.js')
  return module.migrateCanonDatabase
}

/** The session rows of a store, as the reader sees them. */
function sessionRows(path: string): Array<Record<string, unknown>> {
  const db = new DatabaseSync(path)
  try {
    return db
      .prepare(
        `SELECT session_id, payload, summary_json, problems_count, context_limit_source
           FROM session ORDER BY session_id`,
      )
      .all() as Array<Record<string, unknown>>
  } finally {
    db.close()
  }
}

/** The turn rows of a store. */
function turnRows(path: string): Array<Record<string, unknown>> {
  const db = new DatabaseSync(path)
  try {
    const table = db
      .prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'session_turn'")
      .get()
    if (table === undefined) return []
    return db
      .prepare('SELECT session_id, turn_index, turn_json FROM session_turn ORDER BY session_id, turn_index')
      .all() as Array<Record<string, unknown>>
  } finally {
    db.close()
  }
}

/** The schema version a store's metadata carries. */
function schemaVersionOf(path: string): string | undefined {
  const db = new DatabaseSync(path)
  try {
    const row = db.prepare("SELECT value FROM metadata WHERE key = 'schema_version'").get() as
      | { value: string }
      | undefined
    return row?.value
  } finally {
    db.close()
  }
}

/**
 * RED-phase contract for the v21 -> v22 backfill's event-loop behavior
 * (GREEN lands in store.ts: the step must hand the event loop back between
 * sessions, inside its own transaction).
 *
 * The backfill walks every session in the corpus, parsing and rewriting each
 * payload. On the live corpus that is tens of megabytes of JSON in one
 * synchronous pass, and the opening process is blocked for the whole of it:
 * no HTTP handler answers and no CLI caller sees progress until the last
 * session is written.
 *
 * The contract is structural, not a timing threshold: the migration yields
 * between sessions, the number of yields grows with the corpus, and the
 * yield does not cost the step its atomicity — a failure part-way leaves the
 * store at v21 with nothing split.
 */
describe('schema migration v21 -> v22 yields between sessions', () => {
  it('yields at least once during a multi-session migration, and more as the corpus grows', async () => {
    const migrate = await migrationRunner()
    expect(typeof migrate, 'the asynchronous migration entry point must exist').toBe('function')

    const small = v21StoreAt(2)
    const large = v21StoreAt(6)
    let smallYields = 0
    let largeYields = 0

    await migrate(small, {
      yield: async () => {
        smallYields += 1
      },
    })
    await migrate(large, {
      yield: async () => {
        largeYields += 1
      },
    })

    // Bounded below: a multi-session migration hands the event loop back at
    // least once, so an opening server can answer requests while it migrates.
    expect(smallYields).toBeGreaterThanOrEqual(1)
    expect(largeYields).toBeGreaterThanOrEqual(1)
    // And the count scales with the corpus: the yield is per session, not a
    // single hand-back at the end of the pass.
    expect(largeYields).toBeGreaterThan(smallYields)
  })

  it('hands the event loop back while the migration runs', async () => {
    // The behaviour, not the seam: the default yield is the real event-loop
    // turn, so work already queued runs before the migration finishes. A
    // queued immediate is therefore a witness that the migration gave the
    // loop a turn rather than blocking through the whole corpus.
    const migrate = await migrationRunner()
    const path = v21StoreAt(4)

    let eventLoopTurnTaken = false
    setImmediate(() => {
      eventLoopTurnTaken = true
    })

    await migrate(path)

    expect(eventLoopTurnTaken).toBe(true)
  })

  it('holds the write lock across the yield, so no other writer can get in', async () => {
    // The transaction is not given up at the yield: it belongs to the
    // SQLite connection, and the connection keeps the write lock for the
    // whole transaction. Another connection's write is therefore refused
    // while the migration is between sessions — which is what makes the
    // step one atomic unit rather than a sequence of little ones.
    const migrate = await migrationRunner()
    const path = v21StoreAt(3)

    let otherWriter: 'refused' | 'admitted' | 'not-reached' = 'not-reached'
    let refusal = ''
    await migrate(path, {
      yield: async () => {
        const other = new DatabaseSync(path)
        try {
          // A short busy timeout, so the refusal is quick rather than a
          // five-second wait.
          other.exec('PRAGMA busy_timeout = 50')
          other.exec(
            "INSERT INTO session (session_id, harness, payload) VALUES ('sess-intruder', 'claude-code', '{}')",
          )
          otherWriter = 'admitted'
        } catch (err) {
          otherWriter = 'refused'
          refusal = err instanceof Error ? err.message : String(err)
        } finally {
          other.close()
        }
      },
    })

    expect(otherWriter).toBe('refused')
    // The refusal is the lock, not a constraint: the row it tried to write is
    // a valid one.
    expect(refusal).toMatch(/lock/i)
  })

  it('is atomic: a failure part-way leaves the store at v21 with no partial split', async () => {
    // The step runs inside one transaction, and the yield does not give it
    // up: the transaction and its write lock live on the SQLite connection,
    // not on the JavaScript stack. So a failure after the first session rolls
    // the whole step back — the store stays at v21, with no turn table and no
    // payload rewritten.
    const migrate = await migrationRunner()
    const path = v21StoreAt(3)

    await expect(
      migrate(path, {
        yield: async () => {
          throw new Error('migration seam failed')
        },
      }),
    ).rejects.toThrow(/migration seam failed/)

    expect(schemaVersionOf(path)).toBe('21')

    const db = new DatabaseSync(path)
    try {
      const turnTable = db
        .prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'session_turn'")
        .get()
      expect(turnTable, 'the rolled-back step must not leave a turn table behind').toBeUndefined()
      const payloads = db.prepare('SELECT payload FROM session').all() as Array<{ payload: string }>
      expect(payloads).toHaveLength(3)
      for (const row of payloads) {
        const payload = JSON.parse(row.payload) as Record<string, unknown>
        expect(payload, 'a rolled-back step must not split any payload').toHaveProperty('turns')
      }
    } finally {
      db.close()
    }
  })

  it('splits the turns and materializes the columns exactly as the synchronous path does', async () => {
    // The yielding path is the same migration: the turn split, the
    // materialized summary columns and the window provenance land identically
    // to the synchronous open, so nothing about the migration's outcome
    // changes with the yield.
    const migrate = await migrationRunner()
    const yielding = v21StoreAt(3)
    const synchronous = v21StoreAt(3)

    await migrate(yielding)
    new CanonStore(synchronous).close()

    expect(turnRows(yielding)).toEqual(turnRows(synchronous))
    expect(sessionRows(yielding)).toEqual(sessionRows(synchronous))
    expect(turnRows(yielding)).toHaveLength(9)
    expect(schemaVersionOf(yielding)).toBe('22')
  })
})
