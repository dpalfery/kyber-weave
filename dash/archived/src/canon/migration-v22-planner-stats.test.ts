import { mkdtempSync, rmSync } from 'node:fs'
import { createRequire } from 'node:module'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { KyberBridge } from '../server/bridge.js'
import { CanonStore } from './store.js'

const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}

type DbHandle = InstanceType<typeof DatabaseSync>

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

/**
 * The sessions the fixture carries.
 *
 * Five rows, each with a `summary` and a `problems` array: after the v22
 * migration every row is fully materialized, so the bridge issues its primary
 * list statement alone and the plan this contract pins is the plan the
 * deployed reader gets.
 */
const SESSIONS = [
  { id: 'sess-stats-1', started: '2026-10-14T10:00:00.000Z' },
  { id: 'sess-stats-2', started: '2026-10-14T11:00:00.000Z' },
  { id: 'sess-stats-3', started: '2026-10-14T12:00:00.000Z' },
  { id: 'sess-stats-4', started: '2026-10-14T13:00:00.000Z' },
  { id: 'sess-stats-5', started: '2026-10-14T14:00:00.000Z' },
] as const

/** A v21-shaped session payload: the whole session blob in one object. */
function v21Payload(sessionId: string): Record<string, unknown> {
  return {
    id: sessionId,
    session_id: sessionId,
    harness: 'claude-code',
    label: `Session ${sessionId}`,
    agent_name: 'architect',
    repo: 'kyber-weave',
    branch: 'main',
    span_count: 2,
    summary: { turn_count: 2, total_input: 1200, total_output: 300 },
    context: { contextLimitSource: 'reported' },
    problems: [],
    schema: { measurable: true },
  }
}

/**
 * A store at schema version 21, in the state the deployed corpus is in.
 *
 * <remarks>
 * The session table is the v21 shape and the stamp is wound back, so this is
 * a genuine v21 store: the v21 summary columns are present, `session_turn`
 * is not, and the covering index is whatever the fixture asks for.
 *
 * The statistics are the deployed corpus's statistics, and they are the point
 * of this fixture. The table was analysed while it carried its own indexes
 * only — the close-time `PRAGMA optimize` of the build that last wrote the
 * corpus does exactly that — and the covering index was created afterwards,
 * by a later open. SQLite writes one statistics row per analyzed index, so an
 * index created after the analysis carries no row at all, and the planner has
 * no estimate for it that would make it worth choosing: it stops choosing the
 * covering index and plans the sessions list over `session_by_started`
 * instead, which reads every row's payload.
 *
 * That is the deployed plan. A store with no statistics at all plans as a
 * covering scan on its own, so the fixture reproduces the state that
 * actually degraded — the table analysed, the covering index not.
 */
function v21StoreAt(options: { coveringIndex: boolean }): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-v22-stats-'))
  dirs.push(dir)
  const path = join(dir, 'canon.db')

  // The current build's schema, minus the session table: the v21 shape is
  // rebuilt below and the stamp wound back.
  new CanonStore(path).close()

  const db = new DatabaseSync(path)
  try {
    db.exec('DROP TABLE IF EXISTS session;')
    db.exec(V21_SESSION_TABLE_SQL)
    db.prepare('UPDATE metadata SET value = ? WHERE key = ?').run('21', 'schema_version')

    const insert = db.prepare(
      `INSERT INTO session (
         session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, payload
       ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    )
    for (const session of SESSIONS) {
      insert.run(
        session.id,
        'claude-code',
        `Session ${session.id}`,
        0,
        null,
        'architect',
        'kyber-weave',
        'main',
        session.started,
        session.started,
        JSON.stringify(v21Payload(session.id)),
      )
    }

    // The table's own statistics, as the close-time `PRAGMA optimize` of the
    // build that last wrote the corpus left them.
    db.exec('ANALYZE session')

    // The covering index, created after that analysis — so it carries no
    // statistics row, exactly as it does on the deployed corpus.
    if (options.coveringIndex) db.exec(V21_SESSION_DENORM_INDEX_SQL)
  } finally {
    db.close()
  }

  return path
}

/**
 * The statement the bridge actually issues for the sessions list.
 *
 * <remarks>
 * Captured from the handle rather than retyped: the contract is about the
 * plan the deployed reader gets, and a retyped statement would pin a copy of
 * it rather than the thing itself.
 */
function bridgeListStatement(store: CanonStore): string {
  const db = store.getDatabase()
  const statements: string[] = []
  const originalPrepare = db.prepare.bind(db)
  db.prepare = ((sql: string) => {
    statements.push(sql)
    return (originalPrepare as (sql: string) => unknown)(sql)
  }) as typeof db.prepare

  const bridge = new KyberBridge({ canonPath: ':memory:', store })
  try {
    expect(bridge.listSessions()).toHaveLength(SESSIONS.length)
  } finally {
    bridge.close()
  }

  const lists = statements.filter((sql) => sql.includes('FROM session'))
  // One statement: every row is fully materialized after the migration, so
  // the bridge issues its primary read and never the legacy payload fallback.
  expect(lists, 'the bridge must issue exactly one session statement').toHaveLength(1)
  return lists[0]!
}

/** The plan SQLite chooses for a statement, as the query planner words it. */
function planOf(db: DbHandle, statement: string): string {
  const plan = db.prepare(`EXPLAIN QUERY PLAN ${statement}`).all() as Array<{ detail: string }>
  return plan.map((row) => row.detail).join('\n')
}

/** The statistics rows SQLite holds for the `session` table. */
function sessionStatRows(db: DbHandle): Array<{ tbl: string; idx: string | null }> {
  const statTable = db
    .prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'sqlite_stat1'")
    .get() as { name: string } | undefined
  expect(statTable, 'the fixture analysed the table, so sqlite_stat1 exists').toBeDefined()
  return db.prepare("SELECT tbl, idx FROM sqlite_stat1 WHERE tbl = 'session'").all() as Array<{
    tbl: string
    idx: string | null
  }>
}

/**
 * RED-phase contract for the v21 -> v22 migration's planner statistics
 * (GREEN lands in store.ts: MIGRATIONS[21] must create the covering index if
 * the store lacks it and write the statistics for `session` with
 * `ANALYZE session`, mirroring MIGRATIONS[20] exactly).
 *
 * A store that is already at v21 runs neither MIGRATIONS[20] — which the
 * v21 -> v22 path skips — nor the constructor's new-store branch, so before
 * the fix the covering index is left without the statistics the planner
 * needs, and the sessions list is planned over `session_by_started`: a
 * non-covering scan that reads every row's payload.
 *
 * The plan is checked with the store still open, because the deployed store
 * is held open by the engine: the close-time `PRAGMA optimize` that would
 * eventually write the statistics is exactly what never runs while the
 * dashboard serves.
 */
describe('schema migration v21 -> v22 leaves the planner statistics behind', () => {
  it('plans the bridge sessions list as a covering-index scan over session_denorm_idx', () => {
    const path = v21StoreAt({ coveringIndex: true })
    const store = new CanonStore(path)
    try {
      const db = store.getDatabase()
      const plan = planOf(db, bridgeListStatement(store))
      // The index name is pinned, so a silently different plan fails.
      expect(plan).toContain('USING COVERING INDEX session_denorm_idx')
      // A covering scan reads no payload at all.
      expect(plan).not.toContain('payload')
    } finally {
      store.close()
    }
  })

  it('plans the list as a covering-index scan for a store that lacks the covering index', () => {
    // The deployed corpus's other shape: the index does not exist yet, so the
    // constructor creates it on this open. Created after the migration's
    // statistics were written, an index has no statistics row — and the
    // planner stops choosing it. The migration must create the index and
    // write the statistics in that order, or the store lands in exactly the
    // state this contract forbids.
    const path = v21StoreAt({ coveringIndex: false })
    const store = new CanonStore(path)
    try {
      const db = store.getDatabase()
      const plan = planOf(db, bridgeListStatement(store))
      expect(plan).toContain('USING COVERING INDEX session_denorm_idx')
      expect(plan).not.toContain('payload')
    } finally {
      store.close()
    }
  })

  it('leaves statistics for session and for the covering index behind, with the store open', () => {
    const path = v21StoreAt({ coveringIndex: true })
    const store = new CanonStore(path)
    try {
      const rows = sessionStatRows(store.getDatabase())
      expect(rows.length).toBeGreaterThan(0)
      // The covering index specifically: these are the statistics the
      // index-only plan for the sessions list rests on. A row for the table
      // alone is not enough — the planner needs the index's own row.
      expect(rows.map((row) => row.idx)).toContain('session_denorm_idx')
    } finally {
      store.close()
    }
  })

  it('adds nothing to a migrated store: the index exists once and the statistics survive', () => {
    // The constructor creates the covering index on every open. For a store
    // that has just been migrated that creation must be a no-op, and the
    // statistics the migration wrote must survive the constructor's own
    // steps — the schema re-run and the index creation — because the plan is
    // chosen while the store is open.
    const path = v21StoreAt({ coveringIndex: true })
    const store = new CanonStore(path)
    try {
      const db = store.getDatabase()
      const indexes = db
        .prepare("SELECT name FROM sqlite_master WHERE type = 'index' AND name = 'session_denorm_idx'")
        .all()
      expect(indexes).toHaveLength(1)
      expect(sessionStatRows(db).map((row) => row.idx)).toContain('session_denorm_idx')
    } finally {
      store.close()
    }
  })

  it('gives a store that reaches the constructor without the covering index the same statistics', () => {
    // The constructor's own creation path: a store stamped 22 by a build that
    // never created the covering index. The index is created here, after the
    // last analysis, so it carries no statistics — and the planner then keeps
    // choosing `session_by_started` over it. The constructor writes them, so
    // the store lands in the same state as a migrated one.
    const dir = mkdtempSync(join(tmpdir(), 'kyber-v22-stats-constructor-'))
    dirs.push(dir)
    const path = join(dir, 'canon.db')

    const store = new CanonStore(path)
    try {
      for (const session of SESSIONS) {
        store.upsertSession({
          sessionId: session.id,
          harness: 'claude-code',
          label: `Session ${session.id}`,
          agentName: 'architect',
          repo: 'kyber-weave',
          branch: 'main',
          started: session.started,
          ended: session.started,
          payload: v21Payload(session.id),
        })
      }
      // The index goes, as a build that never created it would leave it.
      store.getDatabase().exec('DROP INDEX IF EXISTS session_denorm_idx')
    } finally {
      // The close writes the table's own statistics, the way the close-time
      // `PRAGMA optimize` writes them for the deployed corpus.
      store.close()
    }

    const reopened = new CanonStore(path)
    try {
      const db = reopened.getDatabase()
      const plan = planOf(db, bridgeListStatement(reopened))
      expect(plan).toContain('USING COVERING INDEX session_denorm_idx')
      expect(plan).not.toContain('payload')
      expect(sessionStatRows(db).map((row) => row.idx)).toContain('session_denorm_idx')
    } finally {
      reopened.close()
    }
  })
})
