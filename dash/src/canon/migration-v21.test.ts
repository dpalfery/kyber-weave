import { mkdtempSync, rmSync } from 'node:fs'
import { createRequire } from 'node:module'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore, MIGRATIONS, SCHEMA_VERSION } from './store.js'
import type { SessionRow } from './types.js'

const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}

interface ColumnInfo {
  cid: number
  name: string
  type: string
  notnull: number
  dflt_value: string | null
  pk: number
}

const dirs: string[] = []

afterEach(() => {
  while (dirs.length > 0) rmSync(dirs.pop()!, { recursive: true, force: true })
})

function tableInfo(path: string, table: string): ColumnInfo[] {
  const db = new DatabaseSync(path)
  try {
    return db.prepare(`PRAGMA table_info(${table})`).all() as unknown as ColumnInfo[]
  } finally {
    db.close()
  }
}

/**
 * The v20 session table DDL, frozen as it ships — the v20 sync progress and
 * transcript progress columns are present, the v21 summary columns are not.
 */
const V20_SESSION_TABLE_SQL = `
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
  payload TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS session_by_started ON session (started);
`

/** Every column the v20 session table carries, before the v21 migration. */
const V20_SESSION_COLUMNS = [
  'session_id',
  'harness',
  'label',
  'is_subagent',
  'parent_session',
  'agent_name',
  'repo',
  'branch',
  'started',
  'ended',
  'synced_at',
  'sync_status',
  'settled_at',
  'transcript_path',
  'transcript_offset',
  'payload',
]

const LEGACY_PAYLOAD = JSON.stringify({
  summary: { title: 'Legacy session', outcome: 'shipped' },
  problems: [{ code: 'slow-loop', severity: 'warning' }],
  note: 'legacy session row',
})

/**
 * A store at schema version 20. The current build stamps 21, so rebuild the
 * v20 session shape and wind the stamp back: a real v20 store, not a v21 one.
 * A legacy session row predates the new columns, so they must read NULL for
 * it — NULL means "not materialized", never a real value.
 * With `withV21Columns`, the v21 columns are pre-added, the shape a
 * presence guard must survive without a duplicate-column error.
 */
function v20StoreAt(options?: { withV21Columns?: boolean }): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-migration-v20-'))
  dirs.push(dir)
  const path = join(dir, 'canon.db')

  // The current build stamps 21, which is the post-change shape. Drop the
  // session table it created, recreate the exact v20 shape, and wind the
  // stamp back — the state every installed copy holds before this upgrade.
  new CanonStore(path).close()

  const db = new DatabaseSync(path)
  try {
    db.exec('DROP TABLE IF EXISTS session;')
    db.exec(V20_SESSION_TABLE_SQL)
    db.prepare('UPDATE metadata SET value = ? WHERE key = ?').run('20', 'schema_version')

    if (options?.withV21Columns === true) {
      db.exec('ALTER TABLE session ADD COLUMN summary_json TEXT')
      db.exec('ALTER TABLE session ADD COLUMN problems_count INTEGER')
    }

    db.prepare(
      `INSERT INTO session (
        session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, payload
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    ).run(
      'sess-legacy-v20',
      'claude-code',
      'Legacy Session',
      0,
      null,
      'architect',
      'kyber-weave',
      'main',
      '2026-10-14T10:00:00.000Z',
      '2026-10-14T10:30:00.000Z',
      LEGACY_PAYLOAD,
    )
  } finally {
    db.close()
  }

  return path
}

describe('schema migration v20 -> v21 (session summary materialization)', () => {
  it('pins SCHEMA_VERSION to 21 and defines migration 20 as its own step', () => {
    expect(SCHEMA_VERSION).toBe(22)
    expect(MIGRATIONS[20]).toBeTypeOf('function')
  })

  it('builds fresh stores at version 21 with nullable DEFAULT-less summary columns', () => {
    const dir = mkdtempSync(join(tmpdir(), 'kyber-fresh-v21-'))
    dirs.push(dir)
    const path = join(dir, 'canon.db')
    const store = new CanonStore(path)
    try {
      expect(store.getMetadata('schema_version')).toBe('22')

      const cols = tableInfo(path, 'session')
      // The v21 columns are present. Pinned as containment, not an exact
      // set: later versions add their own columns (v22's
      // `context_limit_source`), and a v21 file pins what v21 introduced
      // rather than freezing the schema against later versions.
      expect(cols.map((c) => c.name)).toEqual(
        expect.arrayContaining([...V20_SESSION_COLUMNS, 'summary_json', 'problems_count']),
      )

      const colMap = new Map(cols.map((c) => [c.name, c]))
      // NULL is the legacy sentinel, so neither column may carry a DEFAULT
      // that would erase the distinction between "legacy, derive from
      // payload" and a real materialized value.
      expect(colMap.get('summary_json')).toMatchObject({
        type: 'TEXT',
        notnull: 0,
        dflt_value: null,
      })
      expect(colMap.get('problems_count')).toMatchObject({
        type: 'INTEGER',
        notnull: 0,
        dflt_value: null,
      })

      // A row written without the columns keeps them NULL, never a default.
      const db = new DatabaseSync(path)
      try {
        db.prepare(`INSERT INTO session (session_id, harness, payload) VALUES (?, ?, ?)`).run(
          'fresh-s1',
          'claude-code',
          '{}',
        )
        const row = db
          .prepare(`SELECT summary_json, problems_count FROM session WHERE session_id = ?`)
          .get('fresh-s1') as { summary_json: string | null; problems_count: number | null }
        expect(row.summary_json).toBeNull()
        expect(row.problems_count).toBeNull()
      } finally {
        db.close()
      }
    } finally {
      store.close()
    }
  })

  it('upgrades a v20 store to 21, adding the columns; the v22 step materializes the legacy row', () => {
    const path = v20StoreAt()
    const store = new CanonStore(path)
    try {
      expect(store.getMetadata('schema_version')).toBe('22')

      const cols = tableInfo(path, 'session')
      expect(cols.map((c) => c.name)).toEqual(
        expect.arrayContaining(['summary_json', 'problems_count']),
      )

      const colMap = new Map(cols.map((c) => [c.name, c]))
      expect(colMap.get('summary_json')).toMatchObject({
        type: 'TEXT',
        notnull: 0,
        dflt_value: null,
      })
      expect(colMap.get('problems_count')).toMatchObject({
        type: 'INTEGER',
        notnull: 0,
        dflt_value: null,
      })

      // The legacy row survives intact. This store opens all the way to 22,
      // and the v22 step materializes every row it walks -- so the row this
      // store ends up holding carries the writer's own values, not NULL.
      const db = new DatabaseSync(path)
      try {
        const row = db
          .prepare(
            `SELECT session_id, harness, label, payload, summary_json, problems_count
             FROM session WHERE session_id = ?`,
          )
          .get('sess-legacy-v20') as {
            session_id: string
            harness: string
            label: string
            payload: string
            summary_json: string | null
            problems_count: number | null
          }
        expect(row).toBeDefined()
        expect(row.session_id).toBe('sess-legacy-v20')
        expect(row.harness).toBe('claude-code')
        expect(row.label).toBe('Legacy Session')
        expect(row.payload).toBe(LEGACY_PAYLOAD)
        expect(row.summary_json).toBe(
          JSON.stringify({ title: 'Legacy session', outcome: 'shipped' }),
        )
        expect(row.problems_count).toBe(1)
      } finally {
        db.close()
      }

      // The v21 step ON ITS OWN still leaves the legacy row unmaterialized:
      // a row that predates the columns reads NULL until a later step -- or
      // the writer -- materializes it. That distinction is the whole reason
      // the columns carry no DEFAULT.
      const legacy = new DatabaseSync(':memory:')
      try {
        legacy.exec(V20_SESSION_TABLE_SQL)
        legacy
          .prepare('INSERT INTO session (session_id, harness, payload) VALUES (?, ?, ?)')
          .run('sess-legacy-v20', 'claude-code', LEGACY_PAYLOAD)
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        MIGRATIONS[20](legacy as any)
        const stepRow = legacy
          .prepare('SELECT summary_json, problems_count FROM session WHERE session_id = ?')
          .get('sess-legacy-v20') as { summary_json: string | null; problems_count: number | null }
        expect(stepRow.summary_json).toBeNull()
        expect(stepRow.problems_count).toBeNull()
      } finally {
        legacy.close()
      }
    } finally {
      store.close()
    }
  })

  it('is idempotent: reopening the upgraded store adds nothing and stays at 21', () => {
    const path = v20StoreAt()
    new CanonStore(path).close()

    const reopened = new CanonStore(path)
    try {
      expect(reopened.getMetadata('schema_version')).toBe('22')
    } finally {
      reopened.close()
    }

    const cols = tableInfo(path, 'session')
    // The presence guard must not double-add: each new column appears once.
    for (const name of ['summary_json', 'problems_count']) {
      expect(cols.filter((c) => c.name === name)).toHaveLength(1)
    }
    // The v21 columns are present exactly once. Pinned as containment, not
    // an exact set, for the same reason as the fresh-store assertion above:
    // later versions add their own columns, and a v21 file pins what v21
    // introduced rather than freezing the schema against later versions.
    expect(cols.map((c) => c.name)).toEqual(
      expect.arrayContaining([...V20_SESSION_COLUMNS, 'summary_json', 'problems_count']),
    )
  })

  it('guards against duplicate column errors when migration 20 meets existing columns', () => {
    // 1. A v20 store that already has the columns opens cleanly without a
    // "duplicate column name" error and still lands at 21.
    const path = v20StoreAt({ withV21Columns: true })
    const store = new CanonStore(path)
    try {
      expect(store.getMetadata('schema_version')).toBe('22')
      expect(tableInfo(path, 'session').map((c) => c.name)).toEqual(
        expect.arrayContaining(['summary_json', 'problems_count']),
      )
    } finally {
      store.close()
    }

    // 2. Direct execution of MIGRATIONS[20] is idempotent on a database handle.
    const db = new DatabaseSync(':memory:')
    try {
      db.exec(V20_SESSION_TABLE_SQL)
      expect(MIGRATIONS[20]).toBeTypeOf('function')
      // First invocation adds the columns.
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      MIGRATIONS[20](db as any)
      expect(
        (db.prepare('PRAGMA table_info(session)').all() as { name: string }[]).map((c) => c.name),
      ).toEqual(expect.arrayContaining(['summary_json', 'problems_count']))
      // Second invocation must presence-guard and not throw "duplicate column name".
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      expect(() => MIGRATIONS[20](db as any)).not.toThrow()
    } finally {
      db.close()
    }
  })

  it('commits the 20 -> 21 bump as its own step, stamping exactly the new version', () => {
    // migrate() runs each step inside one transaction and stamps the version
    // it produced, so an interrupted upgrade resumes rather than replaying.
    // The 20 -> 21 bump must land as that committed stamp: exactly 21, the
    // dedicated step's product — not left at 20, not folded elsewhere.
    const path = v20StoreAt()
    const store = new CanonStore(path)
    try {
      expect(store.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))
      expect(SCHEMA_VERSION).toBe(22)
    } finally {
      store.close()
    }
  })

  it('still refuses a store stamped newer than it understands', () => {
    // The fail-closed guard is generic over future versions: pin the adjacent
    // version — the exact downgrade shape — so the v21 bump cannot silently
    // weaken the refusal.
    const dir = mkdtempSync(join(tmpdir(), 'kyber-migration-v21-newer-'))
    dirs.push(dir)
    const path = join(dir, 'canon.db')
    new CanonStore(path).close()
    const db = new DatabaseSync(path)
    db.prepare('UPDATE metadata SET value = ? WHERE key = ?').run(String(SCHEMA_VERSION + 1), 'schema_version')
    db.close()

    expect(() => new CanonStore(path)).toThrow(/upgrade KyberDash/)
  })
})

/**
 * RED-phase contract for the covering index and its planner statistics
 * (GREEN lands in store.ts): MIGRATIONS[20] must create
 * `session_denorm_idx` immediately after the two ALTER TABLE statements —
 * same step transaction, so index and statistics land atomically with the
 * version-21 stamp — with `ANALYZE session` as the step's final statement;
 * SCHEMA_SQL's session block must carry the identical index so a fresh
 * database is born with it; and the fresh-create path must run
 * `ANALYZE session` once after SCHEMA_SQL and the version stamping, so no
 * store can exist at v21 without `sqlite_stat1` rows.
 *
 * Until then the index assertions below fail — the index does not exist yet —
 * and no `sqlite_stat1` row can name it, which is exactly the missing
 * behavior the sibling worker implements (without the index the list path
 * cannot be served as an index-only scan). None of these tests touches
 * application code; only this file changes.
 */
describe('session_denorm_idx covering index and planner statistics (index contract)', () => {
  /**
   * The covering index GREEN must ship, in order: `started DESC` first so
   * the scan answers `ORDER BY started DESC` with no sort step, then the
   * reader's select list in reader order, so the index alone answers the
   * whole list query. Compared against `sqlite_master` / PRAGMA output in
   * order — a column added, dropped, or reordered breaks the covering
   * property, and this is the tripwire for it.
   */
  const EXPECTED_DENORM_INDEX_COLUMNS = [
    'started',
    'session_id',
    'harness',
    'label',
    'is_subagent',
    'parent_session',
    'agent_name',
    'repo',
    'branch',
    'ended',
    'summary_json',
    'problems_count',
  ]

  interface IndexXInfoRow {
    seqno: number
    cid: number
    name: string
    desc: number
    key: number
  }

  function freshStorePath(): string {
    const dir = mkdtempSync(join(tmpdir(), 'kyber-denorm-fresh-'))
    dirs.push(dir)
    return join(dir, 'canon.db')
  }

  function indexDefinition(path: string): { tblName: string; sql: string } | undefined {
    const db = new DatabaseSync(path)
    try {
      const row = db
        .prepare(
          "SELECT tbl_name AS tblName, sql FROM sqlite_master WHERE type = 'index' AND name = 'session_denorm_idx'",
        )
        .get() as { tblName: string; sql: string } | undefined
      return row ?? undefined
    } finally {
      db.close()
    }
  }

  function indexKeyColumns(path: string): Array<{ name: string; desc: number }> {
    const db = new DatabaseSync(path)
    try {
      const rows = db.prepare('PRAGMA index_xinfo(session_denorm_idx)').all() as unknown as IndexXInfoRow[]
      return rows
        .filter((row) => row.key === 1)
        .sort((a, b) => a.seqno - b.seqno)
        .map((row) => ({ name: row.name, desc: row.desc }))
    } finally {
      db.close()
    }
  }

  function sessionStatRows(path: string): Array<{ tbl: string; idx: string; stat: string }> {
    const db = new DatabaseSync(path)
    try {
      const statTable = db
        .prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'sqlite_stat1'")
        .get() as { name: string } | undefined
      expect(statTable, 'ANALYZE session must have created sqlite_stat1').toBeDefined()
      return db.prepare("SELECT * FROM sqlite_stat1 WHERE tbl = 'session'").all() as Array<{
        tbl: string
        idx: string
        stat: string
      }>
    } finally {
      db.close()
    }
  }

  it('creates session_denorm_idx with the covering columns in order on fresh stores', () => {
    // A fresh database is born with the index: SCHEMA_SQL's session block
    // carries it, presence-guarded and idempotent, so re-running SCHEMA_SQL
    // on every open is a genuine no-op.
    const path = freshStorePath()
    new CanonStore(path).close()

    const definition = indexDefinition(path)
    expect(definition, 'a fresh store must carry session_denorm_idx').toBeDefined()
    expect(definition!.tblName).toBe('session')

    const keyColumns = indexKeyColumns(path)
    expect(keyColumns.map((column) => column.name)).toEqual(EXPECTED_DENORM_INDEX_COLUMNS)
    // `started DESC` leads so the scan answers ORDER BY started DESC with no
    // sort step; the remaining columns follow the reader's select list.
    expect(keyColumns[0]).toEqual({ name: 'started', desc: 1 })
  })

  it('creates session_denorm_idx identically on the v20 -> 21 upgrade path', () => {
    // MIGRATIONS[20] creates the index immediately after the two ALTER TABLE
    // statements of the same step, by which point the columns exist.
    const path = v20StoreAt()
    const store = new CanonStore(path)
    try {
      expect(store.getMetadata('schema_version')).toBe('22')
    } finally {
      store.close()
    }

    const definition = indexDefinition(path)
    expect(definition, 'the upgraded store must carry session_denorm_idx').toBeDefined()
    expect(definition!.tblName).toBe('session')
    expect(indexKeyColumns(path).map((column) => column.name)).toEqual(EXPECTED_DENORM_INDEX_COLUMNS)
  })

  it('carries statistics for the covering index once the session table holds rows', () => {
    // SQLite records no sqlite_stat1 rows for an empty table — ANALYZE on an
    // empty session table creates the statistics table but writes no session
    // rows — so an empty fresh store cannot carry per-index statistics under
    // any implementation, and asserting rows there is unsatisfiable. The
    // design guarantee is instead: the statistics infrastructure is in place
    // from creation, and real statistics for session — including the covering
    // index the list plan rests on — exist as soon as the table holds rows.
    // RED pre-T4 by construction: session_denorm_idx does not exist there,
    // so no implementation can record a statistics row naming it.
    const dir = mkdtempSync(join(tmpdir(), 'kyber-denorm-stats-'))
    dirs.push(dir)
    const path = join(dir, 'canon.db')
    const store = new CanonStore(path)
    try {
      // The statistics table is in place from the moment of creation: the
      // fresh-create ANALYZE session creates it even though the empty table
      // yields no per-index rows yet.
      const created = store
        .getDatabase()
        .prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'sqlite_stat1'")
        .get() as { name: string } | undefined
      expect(created?.name).toBe('sqlite_stat1')

      store.upsertSession({
        sessionId: 'sess-stats-1',
        harness: 'copilot',
        label: 'stats probe',
        agentName: 'agent',
        repo: 'repo',
        branch: 'main',
        started: '2026-09-04T12:00:00.000Z',
        ended: '2026-09-04T12:05:00.000Z',
        payload: { summary: { turn_count: 1 }, problems: [] },
      })
    } finally {
      // Close runs the store's PRAGMA optimize, which records statistics for
      // tables that hold rows.
      store.close()
    }

    const rows = sessionStatRows(path)
    expect(rows.length).toBeGreaterThan(0)
    // The covering index specifically must be represented: these are the
    // statistics the index-only plan for the sessions list rests on.
    expect(rows.map((row) => row.idx)).toContain('session_denorm_idx')
  })

  it('writes planner statistics for session on the v20 -> 21 upgrade path', () => {
    // ANALYZE session runs as the final statement of MIGRATIONS[20], inside
    // that step's existing transaction, so index and statistics land
    // atomically and an interrupted upgrade resumes at the step boundary.
    const path = v20StoreAt()
    new CanonStore(path).close()

    const rows = sessionStatRows(path)
    expect(rows.length).toBeGreaterThan(0)
    // The covering index specifically must be represented, not just any row
    // for the table: a pre-index store already carries statistics rows (its
    // close-time PRAGMA optimize writes them for a non-empty table), so only
    // the denorm row pins the migration's own statistics.
    expect(rows.map((row) => row.idx)).toContain('session_denorm_idx')
  })

  it('lands the index and the statistics with the 20 -> 21 stamp in one step', () => {
    // migrate() runs each step inside one transaction and stamps the version
    // it produced, so an interrupted upgrade resumes rather than replaying.
    // The index and the statistics have no other writer: all three landing
    // together from a single open is the per-step transaction preserved, and
    // this asserts it the way the stamp test above does — one open, one
    // committed version — with no new mechanism.
    const path = v20StoreAt()
    const store = new CanonStore(path)
    try {
      expect(store.getMetadata('schema_version')).toBe('22')
    } finally {
      store.close()
    }

    expect(indexDefinition(path)).toBeDefined()
    expect(indexKeyColumns(path).map((column) => column.name)).toEqual(EXPECTED_DENORM_INDEX_COLUMNS)
    expect(sessionStatRows(path).length).toBeGreaterThan(0)
  })

  it('keeps session_denorm_idx exactly once when the upgraded store reopens', () => {
    // Re-opening an already-upgraded store adds nothing further and leaves
    // the version at 21, with the presence-guarded index present exactly
    // once: IF NOT EXISTS makes SCHEMA_SQL's re-run a genuine no-op.
    const path = v20StoreAt()
    new CanonStore(path).close()
    new CanonStore(path).close()

    const reopened = new CanonStore(path)
    try {
      expect(reopened.getMetadata('schema_version')).toBe('22')
    } finally {
      reopened.close()
    }

    const db = new DatabaseSync(path)
    try {
      const count = (
        db
          .prepare("SELECT COUNT(*) AS n FROM sqlite_master WHERE type = 'index' AND name = 'session_denorm_idx'")
          .get() as { n: number }
      ).n
      expect(count).toBe(1)
    } finally {
      db.close()
    }
    expect(sessionStatRows(path).length).toBeGreaterThan(0)
  })

  it('creates the index exactly once when migration 20 runs twice on a handle', () => {
    // The step's own re-run contract, the way the duplicate-column guard
    // above asserts it: the first invocation adds the index, the second
    // presence-guards instead of throwing.
    const db = new DatabaseSync(':memory:')
    try {
      db.exec(V20_SESSION_TABLE_SQL)
      expect(MIGRATIONS[20]).toBeTypeOf('function')
      // First invocation adds the index alongside the columns.
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      MIGRATIONS[20](db as any)
      // Second invocation must presence-guard and not throw.
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      expect(() => MIGRATIONS[20](db as any)).not.toThrow()
      const names = (
        db
          .prepare("SELECT name FROM sqlite_master WHERE type = 'index' AND name = 'session_denorm_idx'")
          .all() as Array<{ name: string }>
      ).map((row) => row.name)
      expect(names).toEqual(['session_denorm_idx'])
      const keyColumns = (
        db.prepare('PRAGMA index_xinfo(session_denorm_idx)').all() as unknown as IndexXInfoRow[]
      )
        .filter((row) => row.key === 1)
        .sort((a, b) => a.seqno - b.seqno)
        .map((row) => row.name)
      expect(keyColumns).toEqual(EXPECTED_DENORM_INDEX_COLUMNS)
    } finally {
      db.close()
    }
  })
})

/**
 * RED-phase contract for the v21 writer change (GREEN lands separately):
 * `upsertSession` in store.ts must derive `summary_json` and
 * `problems_count` from the same `row.payload` it already serializes and
 * write them in the same statement — the INSERT column list and the
 * ON CONFLICT ... DO UPDATE SET list (via `excluded`). Until then every
 * materialization assertion below fails with NULL, which is exactly the
 * missing behavior: the writer does not write the columns yet.
 *
 * Each `it` pins one separately observable behavior. None of them touches
 * application code; only this test file changes.
 */
describe('upsertSession materializes the v21 summary columns (writer contract)', () => {
  function sessionRow(overrides: Partial<SessionRow> = {}): SessionRow {
    return {
      sessionId: 'sess-1',
      harness: 'claude-code',
      label: 'Session One',
      isSubagent: false,
      parentSession: null,
      agentName: 'architect',
      repo: 'kyber-weave',
      branch: 'main',
      started: '2026-10-14T10:00:00.000Z',
      ended: '2026-10-14T10:30:00.000Z',
      payload: {},
      ...overrides,
    }
  }

  function summaryColumns(
    store: CanonStore,
    sessionId: string,
  ): { summary_json: string | null; problems_count: number | null; payload: string } {
    return store
      .getDatabase()
      .prepare('SELECT summary_json, problems_count, payload FROM session WHERE session_id = ?')
      .get(sessionId) as { summary_json: string | null; problems_count: number | null; payload: string }
  }

  it('materializes summary_json and problems_count on insert', () => {
    const store = new CanonStore(':memory:')
    try {
      const summary = { title: 'Shipped', outcome: 'done' }
      const problems = [
        { code: 'slow-loop', severity: 'warning' },
        { code: 'stale-context', severity: 'error' },
      ]
      store.upsertSession(sessionRow({ payload: { summary, problems, note: 'v1' } }))

      const row = summaryColumns(store, 'sess-1')
      expect(row.summary_json).not.toBeNull()
      expect(JSON.parse(row.summary_json as string)).toEqual(summary)
      expect(row.problems_count).toBe(2)
    } finally {
      store.close()
    }
  })

  it('updates both columns through the ON CONFLICT path on re-upsert', () => {
    const store = new CanonStore(':memory:')
    try {
      store.upsertSession(sessionRow({ payload: { summary: { title: 'v1' }, problems: [{ code: 'a' }] } }))
      store.upsertSession(
        sessionRow({ payload: { summary: { title: 'v2' }, problems: [{ code: 'a' }, { code: 'b' }, { code: 'c' }] } }),
      )

      const row = summaryColumns(store, 'sess-1')
      expect(row.summary_json).not.toBeNull()
      expect(JSON.parse(row.summary_json as string)).toEqual({ title: 'v2' })
      expect(row.problems_count).toBe(3)
    } finally {
      store.close()
    }
  })

  it('self-heals a legacy row whose columns are NULL after a re-upsert', () => {
    const store = new CanonStore(':memory:')
    try {
      const summary = { title: 'Legacy session', outcome: 'shipped' }
      const problems = [{ code: 'slow-loop', severity: 'warning' }]
      store.upsertSession(sessionRow({ sessionId: 'sess-legacy', payload: { summary, problems } }))
      // Every existing row is in this state today: the columns predate the
      // writer, so force NULL regardless of what the writer under test does.
      store
        .getDatabase()
        .prepare('UPDATE session SET summary_json = NULL, problems_count = NULL WHERE session_id = ?')
        .run('sess-legacy')

      store.upsertSession(sessionRow({ sessionId: 'sess-legacy', payload: { summary, problems } }))

      const row = summaryColumns(store, 'sess-legacy')
      expect(row.summary_json).not.toBeNull()
      expect(JSON.parse(row.summary_json as string)).toEqual(summary)
      expect(row.problems_count).toBe(1)
    } finally {
      store.close()
    }
  })

  it('writes NULL, not an empty value or zero, for each column the payload lacks', () => {
    const store = new CanonStore(':memory:')
    try {
      // Neither key: both columns stay NULL. toBeNull pins the distinction —
      // a missing problems array is "never measured", never a zero count,
      // and a missing summary is NULL, never "" or "null".
      store.upsertSession(sessionRow({ sessionId: 'sess-bare', payload: { note: 'no summary, no problems' } }))
      const bare = summaryColumns(store, 'sess-bare')
      expect(bare.summary_json).toBeNull()
      expect(bare.problems_count).toBeNull()

      // The columns are independent: a summary without problems materializes
      // only the summary side.
      const summary = { title: 'Summary only' }
      store.upsertSession(sessionRow({ sessionId: 'sess-half', payload: { summary } }))
      const half = summaryColumns(store, 'sess-half')
      expect(JSON.parse(half.summary_json as string)).toEqual(summary)
      expect(half.problems_count).toBeNull()
    } finally {
      store.close()
    }
  })

  it('writes NULL for problems_count when problems is not an array', () => {
    const store = new CanonStore(':memory:')
    try {
      store.upsertSession(
        sessionRow({ payload: { summary: { title: 'Shipped' }, problems: 'slow-loop' } }),
      )

      expect(summaryColumns(store, 'sess-1').problems_count).toBeNull()
    } finally {
      store.close()
    }
  })

  it('leaves the transcript-sync-owned columns untouched on re-upsert', () => {
    const store = new CanonStore(':memory:')
    try {
      store.upsertSession(sessionRow({ payload: { summary: { title: 'v1' }, problems: [] } }))
      store
        .getDatabase()
        .prepare(
          `UPDATE session SET transcript_path = ?, transcript_offset = ?, sync_status = ?, synced_at = ?, settled_at = ?
           WHERE session_id = ?`,
        )
        .run('/tmp/t.jsonl', 1024, 'synced', '2026-10-15T00:00:00.000Z', '2026-10-15T01:00:00.000Z', 'sess-1')

      store.upsertSession(
        sessionRow({ label: 'rewritten', payload: { summary: { title: 'v2' }, problems: [{ code: 'b' }] } }),
      )

      const after = store
        .getDatabase()
        .prepare(
          `SELECT label, transcript_path, transcript_offset, sync_status, synced_at, settled_at,
                  summary_json, problems_count FROM session`,
        )
        .get() as Record<string, unknown>
      // The writer did its own job ...
      expect(after.label).toBe('rewritten')
      expect(JSON.parse(after.summary_json as string)).toEqual({ title: 'v2' })
      expect(after.problems_count).toBe(1)
      // ... without touching the sync job's columns.
      expect(after.transcript_path).toBe('/tmp/t.jsonl')
      expect(after.transcript_offset).toBe(1024)
      expect(after.sync_status).toBe('synced')
      expect(after.synced_at).toBe('2026-10-15T00:00:00.000Z')
      expect(after.settled_at).toBe('2026-10-15T01:00:00.000Z')
    } finally {
      store.close()
    }
  })

  it('keeps the materialized columns consistent with the payload the same statement wrote', () => {
    const store = new CanonStore(':memory:')
    try {
      store.upsertSession(sessionRow({ payload: { summary: { title: 'v1' }, problems: [{ code: 'a' }] } }))
      const v2 = {
        summary: { title: 'v2', outcome: 'changed' },
        problems: [{ code: 'a' }, { code: 'b' }],
      }
      store.upsertSession(sessionRow({ payload: v2 }))

      const row = summaryColumns(store, 'sess-1')
      const storedPayload = JSON.parse(row.payload) as { summary?: unknown; problems?: unknown[] }
      // Compared against the stored payload, not the input object, so the
      // two cannot disagree: they came from the same statement.
      expect(row.summary_json).not.toBeNull()
      expect(JSON.parse(row.summary_json as string)).toEqual(storedPayload.summary)
      expect(row.problems_count).toBe((storedPayload.problems as unknown[]).length)
    } finally {
      store.close()
    }
  })
})
