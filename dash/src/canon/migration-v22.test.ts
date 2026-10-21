import { mkdtempSync, rmSync } from 'node:fs'
import { createRequire } from 'node:module'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore, MIGRATIONS, SCHEMA_VERSION } from './store.js'

const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}

type DbHandle = InstanceType<typeof DatabaseSync>

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
 * The v21 session table DDL, frozen as it ships — the v20 sync progress and
 * transcript progress columns plus the v21 materialized summary columns
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

/** Every column the v21 session table carries, before the v22 migration. */
const V21_SESSION_COLUMNS = [
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
  'summary_json',
  'problems_count',
  'payload',
]

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

const ONE_TURN: Array<Record<string, unknown>> = [
  {
    index: 0,
    spanId: 'span-solo-0',
    timestamp: '2026-10-14T11:01:00.000Z',
    model: 'opus-4-1',
    input: 900,
    output: 120,
  },
]

/**
 * A v21-shaped session payload: the whole session blob in one object, turns
 * inline under the top-level `turns` key, the window provenance under
 * `context.contextLimitSource`. `turns` is appended after `schema` so the key
 * order matches the projection's emission order.
 */
function v21Payload(
  sessionId: string,
  contextLimitSource: string,
  turns?: Array<Record<string, unknown>>,
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

const WITH_THREE_TURNS_PAYLOAD = JSON.stringify(v21Payload('sess-turns-3', 'reported', THREE_TURNS))
const WITH_ONE_TURN_PAYLOAD = JSON.stringify(v21Payload('sess-turns-1', 'default', ONE_TURN))
const WITHOUT_TURNS_PAYLOAD = JSON.stringify(v21Payload('sess-no-turns', 'declared'))

/**
 * A store at schema version 21. The current build stamps 21, so rebuild the
 * v21 session shape and wind the stamp back: a real v21 store, not a v20 one
 * (the v21 summary columns and the covering index are present) and not a v22
 * one (no `session_turn` table, no `context_limit_source` column).
 */
function v21StoreAt(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-migration-v21-'))
  dirs.push(dir)
  const path = join(dir, 'canon.db')

  // The current build stamps 21, which is the post-change shape. Drop the
  // session table it created, recreate the exact v21 shape with its covering
  // index, and wind the stamp back — the state every installed copy holds
  // before this upgrade.
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
    insert.run(
      'sess-turns-3',
      'claude-code',
      'Session sess-turns-3',
      0,
      null,
      'architect',
      'kyber-weave',
      'main',
      '2026-10-14T10:00:00.000Z',
      '2026-10-14T10:30:00.000Z',
      WITH_THREE_TURNS_PAYLOAD,
    )
    insert.run(
      'sess-turns-1',
      'claude-code',
      'Session sess-turns-1',
      0,
      null,
      'architect',
      'kyber-weave',
      'main',
      '2026-10-14T11:00:00.000Z',
      '2026-10-14T11:30:00.000Z',
      WITH_ONE_TURN_PAYLOAD,
    )
    insert.run(
      'sess-no-turns',
      'claude-code',
      'Session sess-no-turns',
      0,
      null,
      'architect',
      'kyber-weave',
      'main',
      '2026-10-14T12:00:00.000Z',
      '2026-10-14T12:30:00.000Z',
      WITHOUT_TURNS_PAYLOAD,
    )
  } finally {
    db.close()
  }

  return path
}

/** The v21 stamp and shape a fixture must carry to be a genuine v21 store. */
function expectGenuineV21(path: string): void {
  const db = new DatabaseSync(path)
  try {
    const stamp = db.prepare("SELECT value FROM metadata WHERE key = 'schema_version'").get() as
      | { value: string }
      | undefined
    expect(stamp?.value).toBe('21')
    const cols = (db.prepare('PRAGMA table_info(session)').all() as { name: string }[]).map(
      (c) => c.name,
    )
    expect(cols).toEqual(expect.arrayContaining(['summary_json', 'problems_count']))
    expect(cols).not.toContain('context_limit_source')
    const denorm = db
      .prepare("SELECT name FROM sqlite_master WHERE type = 'index' AND name = 'session_denorm_idx'")
      .get()
    expect(denorm, 'a genuine v21 store carries the covering index').toBeDefined()
    const turnTable = db
      .prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'session_turn'")
      .get()
    expect(turnTable, 'a genuine v21 store has no session_turn table yet').toBeUndefined()
  } finally {
    db.close()
  }
}

function sessionTurnTableColumns(db: DbHandle): string[] {
  return (db.prepare('PRAGMA table_info(session_turn)').all() as { name: string }[]).map((c) => c.name)
}

function allSessionTurnRows(db: DbHandle): Array<Record<string, unknown>> {
  return db.prepare('SELECT * FROM session_turn').all() as Array<Record<string, unknown>>
}

/** Rows of `session_turn` belonging to one session, whatever the link column is named. */
function rowsForSession(
  rows: Array<Record<string, unknown>>,
  sessionId: string,
): Array<Record<string, unknown>> {
  return rows.filter((row) => Object.values(row).includes(sessionId))
}

function tryParseJsonObject(value: unknown): Record<string, unknown> | undefined {
  if (typeof value !== 'string') return undefined
  try {
    const parsed: unknown = JSON.parse(value)
    if (parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)) {
      return parsed as Record<string, unknown>
    }
    return undefined
  } catch {
    // Not a JSON document cell — the probe moves on to the next value.
    return undefined
  }
}

/** The turn documents a set of `session_turn` rows carries, whatever the JSON column is named. */
function turnDocumentsOf(rows: Array<Record<string, unknown>>): Array<Record<string, unknown>> {
  const docs: Array<Record<string, unknown>> = []
  for (const row of rows) {
    for (const value of Object.values(row)) {
      const parsed = tryParseJsonObject(value)
      if (parsed !== undefined && typeof parsed.index === 'number') docs.push(parsed)
    }
  }
  return docs
}

/** The original payload with only the `turns` key removed, key order otherwise preserved. */
function withoutTurns(original: string): string {
  const parsed = JSON.parse(original) as Record<string, unknown>
  delete parsed.turns
  return JSON.stringify(parsed)
}

function sessionPayloadOf(store: CanonStore, sessionId: string): string {
  const row = store
    .getDatabase()
    .prepare('SELECT payload FROM session WHERE session_id = ?')
    .get(sessionId) as { payload: string } | undefined
  expect(row, `session ${sessionId} must survive the upgrade`).toBeDefined()
  return row!.payload
}

/**
 * RED-phase contract for the v21 -> v22 payload split (GREEN lands in
 * store.ts): MIGRATIONS[21] creates the `session_turn` table, adds the
 * `context_limit_source` column, extracts every session's turns into one row
 * per turn, and rewrites each `session` payload without the `turns` key.
 *
 * Until then every version-22 assertion below fails — SCHEMA_VERSION is still
 * 21, `session_turn` does not exist, and no payload is rewritten — which is
 * exactly the missing behavior the sibling worker implements. None of these
 * tests touches application code; only this test file changes.
 */
describe('schema migration v21 -> v22 (session turns child table)', () => {
  it('pins SCHEMA_VERSION to 22 and defines migration 21 as its own step', () => {
    expect(SCHEMA_VERSION).toBe(22)
    expect(MIGRATIONS[21]).toBeTypeOf('function')
  })

  it('builds fresh stores at version 22 with the session_turn table and the context limit column', () => {
    const dir = mkdtempSync(join(tmpdir(), 'kyber-fresh-v22-'))
    dirs.push(dir)
    const path = join(dir, 'canon.db')
    const store = new CanonStore(path)
    try {
      expect(store.getMetadata('schema_version')).toBe('22')

      // The child table exists and carries at least the three contracted
      // roles: the owning session, the turn index, and the turn JSON.
      const turnTable = store
        .getDatabase()
        .prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'session_turn'")
        .get()
      expect(turnTable, 'a fresh store must carry session_turn').toBeDefined()
      expect(sessionTurnTableColumns(store.getDatabase()).length).toBeGreaterThanOrEqual(3)

      // Exactly the v21 columns plus the one new one — no more.
      const cols = tableInfo(path, 'session')
      expect(cols.map((c) => c.name).sort()).toEqual(
        [...V21_SESSION_COLUMNS, 'context_limit_source'].sort(),
      )

      // Nullable with no DEFAULT: NULL means "no window provenance recorded",
      // never a real source value.
      const colMap = new Map(cols.map((c) => [c.name, c]))
      expect(colMap.get('context_limit_source')).toMatchObject({
        type: 'TEXT',
        notnull: 0,
      })
    } finally {
      store.close()
    }
  })

  it('upgrades a v21 store to 22 exactly once, backfilling turn rows and rewriting payloads', () => {
    const path = v21StoreAt()
    expectGenuineV21(path)

    const store = new CanonStore(path)
    try {
      expect(store.getMetadata('schema_version')).toBe('22')

      // Every turn of every session landed in the child table: 3 + 1.
      const rows = allSessionTurnRows(store.getDatabase())
      expect(rows).toHaveLength(4)
      expect(rowsForSession(rows, 'sess-turns-3')).toHaveLength(3)
      expect(rowsForSession(rows, 'sess-turns-1')).toHaveLength(1)
      expect(rowsForSession(rows, 'sess-no-turns')).toHaveLength(0)

      // The migrated payloads no longer carry the turns array.
      for (const sessionId of ['sess-turns-3', 'sess-turns-1']) {
        const rewritten = JSON.parse(sessionPayloadOf(store, sessionId)) as Record<string, unknown>
        expect(rewritten).not.toHaveProperty('turns')
      }
    } finally {
      store.close()
    }
  })

  it('splits each session turns into one row per turn with the right index and the right turn JSON', () => {
    const path = v21StoreAt()
    const store = new CanonStore(path)
    try {
      expect(store.getMetadata('schema_version')).toBe('22')
      const rows = allSessionTurnRows(store.getDatabase())

      // The three-turn session: every row names its session, every turn
      // index is represented, and every turn document round-trips.
      const three = rowsForSession(rows, 'sess-turns-3')
      expect(three).toHaveLength(3)
      for (const row of three) {
        expect(Object.values(row)).toContain('sess-turns-3')
      }
      const threeDocs = turnDocumentsOf(three)
      expect(threeDocs).toHaveLength(3)
      for (const turn of THREE_TURNS) {
        const match = three.find((row) => {
          const values = Object.values(row)
          return (
            values.includes(turn.index) &&
            values.some((value) => {
              const parsed = tryParseJsonObject(value)
              return parsed !== undefined && JSON.stringify(parsed) === JSON.stringify(turn)
            })
          )
        })
        expect(match, `session_turn must hold a row for turn index ${String(turn.index)}`).toBeDefined()
        expect(threeDocs).toContainEqual(turn)
      }

      // The one-turn session splits the same way.
      const one = rowsForSession(rows, 'sess-turns-1')
      expect(one).toHaveLength(1)
      expect(turnDocumentsOf(one)).toEqual([ONE_TURN[0]])

      // The rewritten payloads keep every other top-level key byte-identical:
      // the original with only `turns` removed is the rewritten blob.
      expect(sessionPayloadOf(store, 'sess-turns-3')).toBe(withoutTurns(WITH_THREE_TURNS_PAYLOAD))
      expect(sessionPayloadOf(store, 'sess-turns-1')).toBe(withoutTurns(WITH_ONE_TURN_PAYLOAD))
    } finally {
      store.close()
    }
  })

  it('leaves a row with no turns key alone: no turn rows, payload unchanged', () => {
    const path = v21StoreAt()
    const store = new CanonStore(path)
    try {
      expect(store.getMetadata('schema_version')).toBe('22')

      const rows = allSessionTurnRows(store.getDatabase())
      expect(rowsForSession(rows, 'sess-no-turns')).toHaveLength(0)
      expect(sessionPayloadOf(store, 'sess-no-turns')).toBe(WITHOUT_TURNS_PAYLOAD)
    } finally {
      store.close()
    }
  })

  it('is idempotent: reopening the upgraded store adds nothing and stays at 22', () => {
    const path = v21StoreAt()
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
        db.prepare('SELECT COUNT(*) AS n FROM session_turn').get() as { n: number }
      ).n
      expect(count).toBe(4)
    } finally {
      db.close()
    }
  })

  it('adds no duplicate turn rows when migration 21 runs twice on one handle', () => {
    // The step's own re-run contract, the way the v20 duplicate-column guard
    // asserts it: the first invocation backfills, the second presence-guards
    // instead of throwing or duplicating.
    const db = new DatabaseSync(':memory:')
    try {
      db.exec(V21_SESSION_TABLE_SQL)
      db.exec(V21_SESSION_DENORM_INDEX_SQL)
      db.prepare(
        'INSERT INTO session (session_id, harness, payload) VALUES (?, ?, ?)',
      ).run('sess-turns-3', 'claude-code', WITH_THREE_TURNS_PAYLOAD)

      const step = MIGRATIONS[21]
      expect(step).toBeTypeOf('function')
      // First invocation backfills the three turn rows.
      step!(db)
      expect(
        (db.prepare('SELECT COUNT(*) AS n FROM session_turn').get() as { n: number }).n,
      ).toBe(3)
      // Second invocation must presence-guard and not throw "duplicate
      // column", "table already exists", or add duplicate turn rows.
      expect(() => step!(db)).not.toThrow()
      expect(
        (db.prepare('SELECT COUNT(*) AS n FROM session_turn').get() as { n: number }).n,
      ).toBe(3)
      // The new column is present exactly once.
      const names = (db.prepare('PRAGMA table_info(session)').all() as { name: string }[]).map(
        (c) => c.name,
      )
      expect(names.filter((name) => name === 'context_limit_source')).toHaveLength(1)
    } finally {
      db.close()
    }
  })

  it('populates context_limit_source from the payload context on upgrade', () => {
    const path = v21StoreAt()
    const store = new CanonStore(path)
    try {
      expect(store.getMetadata('schema_version')).toBe('22')

      const rows = store
        .getDatabase()
        .prepare('SELECT session_id, context_limit_source FROM session ORDER BY session_id')
        .all() as Array<{ session_id: string; context_limit_source: string | null }>
      expect(rows).toEqual([
        { session_id: 'sess-no-turns', context_limit_source: 'declared' },
        { session_id: 'sess-turns-1', context_limit_source: 'default' },
        { session_id: 'sess-turns-3', context_limit_source: 'reported' },
      ])
    } finally {
      store.close()
    }
  })

  it('commits the 21 -> 22 bump as its own step, stamping exactly the new version', () => {
    // migrate() runs each step inside one transaction and stamps the version
    // it produced, so an interrupted upgrade resumes rather than replaying.
    // The 21 -> 22 bump must land as that committed stamp: exactly 22, the
    // dedicated step's product — not left at 21, not folded elsewhere.
    const path = v21StoreAt()
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
    // version — the exact downgrade shape — so the v22 bump cannot silently
    // weaken the refusal.
    const dir = mkdtempSync(join(tmpdir(), 'kyber-migration-v22-newer-'))
    dirs.push(dir)
    const path = join(dir, 'canon.db')
    new CanonStore(path).close()
    const db = new DatabaseSync(path)
    db.prepare('UPDATE metadata SET value = ? WHERE key = ?').run(String(SCHEMA_VERSION + 1), 'schema_version')
    db.close()

    expect(() => new CanonStore(path)).toThrow(/upgrade KyberDash/)
  })
})
