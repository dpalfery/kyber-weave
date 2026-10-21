// The v21 -> v22 migration must materialize the summary columns it walks past.
//
// WHY: the v21 migration added `summary_json` and `problems_count` with no
// backfill -- the plan's D5 was a lazy reader fallback instead. The reader
// takes that fallback whenever the columns are NULL (the legacy path in
// `dash/src/server/bridge.ts`), so a corpus whose columns were never
// populated pays a payload parse on every sessions-list read. Measured
// against the live 3.3 GB corpus: a materialized copy reads the list in
// 4.61 ms warm and 29.5 ms cold, while the corpus as it actually stands --
// 0 of 338 rows materialized -- reads it in 55.79 ms warm and 473.7 ms cold.
//
// The migration is the one pass that already walks every session row, so
// materializing there costs no extra traversal: a store that migrates to
// v22 comes out the other side materialized, exactly as if every row had
// been written by this build.
//
// The values are the WRITER's values. `upsertSession` derives them through
// `materializeSessionSummary`, and the migration has to derive them the
// same way: a migrated corpus and a freshly written one must describe the
// same session identically. These tests therefore pin the AGREEMENT rather
// than the migration's own numbers -- the migrated columns must equal what
// the writer produces for the same payload -- and they pin the NULL cases
// by value, because a NULL in the wrong column reads as an empty summary or
// a measured zero rather than as "not materialized".

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

const dirs: string[] = []

afterEach(() => {
  while (dirs.length > 0) rmSync(dirs.pop()!, { recursive: true, force: true })
})

/**
 * The v21 session table DDL, frozen as it ships: the v20 sync and transcript
 * progress columns plus the v21 materialized summary columns
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

const THREE_TURNS: Array<Record<string, unknown>> = [
  {
    index: 0,
    spanId: 'span-full-0',
    timestamp: '2026-10-14T10:01:00.000Z',
    model: 'opus-4-1',
  },
  {
    index: 1,
    spanId: 'span-full-1',
    timestamp: '2026-10-14T10:04:00.000Z',
    model: 'opus-4-1',
  },
  {
    index: 2,
    spanId: 'span-full-2',
    timestamp: '2026-10-14T10:09:00.000Z',
    model: 'opus-4-1',
  },
]

const ONE_TURN: Array<Record<string, unknown>> = [
  {
    index: 0,
    spanId: 'span-plain-0',
    timestamp: '2026-10-14T11:01:00.000Z',
    model: 'opus-4-1',
  },
]

/**
 * A payload with a summary, a three-entry problems array and three turns --
 * the shape the corpus actually holds.
 */
const FULL_PAYLOAD: Record<string, unknown> = {
  id: 'sess-full',
  session_id: 'sess-full',
  harness: 'claude-code',
  label: 'Session sess-full',
  span_count: 3,
  summary: { turn_count: 3, total_cost: 1.5 },
  context: { contextLimitSource: 'reported', first: { pressure: 0.1 } },
  problems: [{ id: 'p1' }, { id: 'p2' }, { id: 'p3' }],
  turns: THREE_TURNS,
}

/**
 * A payload with no `summary` key and no `problems` key at all: neither
 * column has a value to materialize, so both are NULL.
 */
const PLAIN_PAYLOAD: Record<string, unknown> = {
  id: 'sess-plain',
  session_id: 'sess-plain',
  harness: 'claude-code',
  label: 'Session sess-plain',
  span_count: 1,
  context: { contextLimitSource: 'default' },
  turns: ONE_TURN,
}

/**
 * A payload whose `problems` is an object rather than an array: only an
 * array has a length to count, so the count is NULL and never a zero.
 */
const NON_ARRAY_PROBLEMS_PAYLOAD: Record<string, unknown> = {
  id: 'sess-object-problems',
  session_id: 'sess-object-problems',
  harness: 'claude-code',
  label: 'Session sess-object-problems',
  span_count: 1,
  summary: { turn_count: 1 },
  context: { contextLimitSource: 'declared' },
  problems: { count: 2 },
}

/**
 * A payload with a null summary and an empty problems array: a null summary
 * has no summary to materialize (NULL), and an empty array is an array, so
 * the count is a measured zero.
 */
const NULL_SUMMARY_PAYLOAD: Record<string, unknown> = {
  id: 'sess-null-summary',
  session_id: 'sess-null-summary',
  harness: 'claude-code',
  label: 'Session sess-null-summary',
  span_count: 0,
  summary: null,
  context: { contextLimitSource: 'declared' },
  problems: [],
}

/** A payload that does not parse at all: the row is left exactly as it is. */
const UNPARSEABLE_PAYLOAD = 'this is not a json document'

/** The v21 rows the fixture stores: the unmaterialized state the live corpus is in. */
const V21_ROWS: Array<{ id: string; payload: string }> = [
  { id: 'sess-full', payload: JSON.stringify(FULL_PAYLOAD) },
  { id: 'sess-plain', payload: JSON.stringify(PLAIN_PAYLOAD) },
  { id: 'sess-object-problems', payload: JSON.stringify(NON_ARRAY_PROBLEMS_PAYLOAD) },
  { id: 'sess-null-summary', payload: JSON.stringify(NULL_SUMMARY_PAYLOAD) },
  { id: 'sess-unparseable', payload: UNPARSEABLE_PAYLOAD },
]

/**
 * A store at schema version 21, holding sessions whose materialized columns
 * are all NULL -- the state the live corpus is in after being migrated to v21
 * by a build whose writer did not yet materialize them.
 *
 * The current build stamps 22, so the session table it created is dropped and
 * the exact v21 shape is recreated with its covering index, and the stamp is
 * wound back: a real v21 store, not a v20 one (the v21 summary columns and
 * the covering index are present) and not a v22 one (no `session_turn` table,
 * no `context_limit_source` column).
 */
function v21StoreAt(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-migration-v21-materialize-'))
  dirs.push(dir)
  const path = join(dir, 'canon.db')

  new CanonStore(path).close()

  const db = new DatabaseSync(path)
  try {
    db.exec('DROP TABLE IF EXISTS session;')
    db.exec(V21_SESSION_TABLE_SQL)
    db.exec(V21_SESSION_DENORM_INDEX_SQL)
    db.prepare('UPDATE metadata SET value = ? WHERE key = ?').run('21', 'schema_version')

    const insert = db.prepare(
      `INSERT INTO session (
        session_id, harness, label, is_subagent, started, ended, payload
      ) VALUES (?, ?, ?, ?, ?, ?, ?)`,
    )
    for (const row of V21_ROWS) {
      insert.run(
        row.id,
        'claude-code',
        `Session ${row.id}`,
        0,
        '2026-10-14T10:00:00.000Z',
        '2026-10-14T10:30:00.000Z',
        row.payload,
      )
    }
  } finally {
    db.close()
  }

  return path
}

/** The two materialized summary columns of one session row. */
type Materialized = { summary_json: string | null; problems_count: number | null }

function readColumns(store: CanonStore, sessionId: string): Materialized {
  const row = store
    .getDatabase()
    .prepare('SELECT summary_json, problems_count FROM session WHERE session_id = ?')
    .get(sessionId) as Materialized | undefined
  expect(row, `session ${sessionId} must survive the upgrade`).toBeDefined()
  return row!
}

/**
 * The columns the WRITE path produces for a payload: `upsertSession` derives
 * them from the payload it is handed, so this is the value a freshly written
 * corpus holds for the same session.
 */
function writtenColumns(payload: Record<string, unknown>): Materialized {
  const store = new CanonStore(':memory:')
  try {
    store.upsertSession({
      sessionId: String(payload['session_id']),
      harness: String(payload['harness']),
      label: String(payload['label']),
      payload,
    })
    return readColumns(store, String(payload['session_id']))
  } finally {
    store.close()
  }
}

function turnRowCount(store: CanonStore): number {
  return (store.getDatabase().prepare('SELECT COUNT(*) AS n FROM session_turn').get() as { n: number })
    .n
}

function sessionPayloadOf(store: CanonStore, sessionId: string): string {
  const row = store
    .getDatabase()
    .prepare('SELECT payload FROM session WHERE session_id = ?')
    .get(sessionId) as { payload: string } | undefined
  expect(row, `session ${sessionId} must survive the upgrade`).toBeDefined()
  return row!.payload
}

/** The turn rows of one session, in index order. */
function turnRowsOf(store: CanonStore, sessionId: string): Array<{ index: number; turn: unknown }> {
  const rows = store
    .getDatabase()
    .prepare(
      'SELECT turn_index, turn_json FROM session_turn WHERE session_id = ? ORDER BY turn_index',
    )
    .all(sessionId) as Array<{ turn_index: number; turn_json: string }>
  return rows.map((row) => ({ index: row.turn_index, turn: JSON.parse(row.turn_json) }))
}

/** The original payload with only the `turns` key removed, key order otherwise preserved. */
function withoutTurns(original: string): string {
  const parsed = JSON.parse(original) as Record<string, unknown>
  delete parsed.turns
  return JSON.stringify(parsed)
}

describe('schema migration v21 -> v22 (materialized summary columns)', () => {
  it('materializes summary_json and problems_count for every migrated row', () => {
    const path = v21StoreAt()
    const store = new CanonStore(path)
    try {
      expect(store.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))

      // The values, not merely the presence of the columns: a summary
      // materializes as the serialized `$.summary`, and a problems array
      // materializes as its length.
      expect(readColumns(store, 'sess-full')).toEqual({
        summary_json: JSON.stringify(FULL_PAYLOAD['summary']),
        problems_count: 3,
      })

      // No `summary` key and no `problems` key: nothing to materialize, so
      // both columns are NULL -- never an empty summary and never a zero.
      expect(readColumns(store, 'sess-plain')).toEqual({
        summary_json: null,
        problems_count: null,
      })

      // A `problems` that is not an array has no length to count: NULL,
      // never a measured zero.
      expect(readColumns(store, 'sess-object-problems')).toEqual({
        summary_json: JSON.stringify(NON_ARRAY_PROBLEMS_PAYLOAD['summary']),
        problems_count: null,
      })

      // A null summary has no summary to materialize (NULL), and an empty
      // problems array IS an array, so its count is a measured zero.
      expect(readColumns(store, 'sess-null-summary')).toEqual({
        summary_json: null,
        problems_count: 0,
      })

      // A payload that does not parse is left entirely alone: the row keeps
      // its payload and carries no materialized values.
      expect(readColumns(store, 'sess-unparseable')).toEqual({
        summary_json: null,
        problems_count: null,
      })
      expect(sessionPayloadOf(store, 'sess-unparseable')).toBe(UNPARSEABLE_PAYLOAD)
    } finally {
      store.close()
    }
  })

  it('materializes exactly what the writer materializes for the same payload', () => {
    // The property that makes a migrated corpus and a freshly written one
    // indistinguishable: the same payload, written through `upsertSession`,
    // must land on the same two column values the migration wrote.
    const path = v21StoreAt()
    const store = new CanonStore(path)
    try {
      for (const payload of [
        FULL_PAYLOAD,
        PLAIN_PAYLOAD,
        NON_ARRAY_PROBLEMS_PAYLOAD,
        NULL_SUMMARY_PAYLOAD,
      ]) {
        const sessionId = String(payload['session_id'])
        expect(readColumns(store, sessionId)).toEqual(writtenColumns(payload))
      }
    } finally {
      store.close()
    }
  })

  it('is idempotent: a second run of the step changes nothing', () => {
    const path = v21StoreAt()
    const store = new CanonStore(path)
    try {
      const before = {
        full: readColumns(store, 'sess-full'),
        plain: readColumns(store, 'sess-plain'),
        objectProblems: readColumns(store, 'sess-object-problems'),
        nullSummary: readColumns(store, 'sess-null-summary'),
        unparseable: readColumns(store, 'sess-unparseable'),
      }
      const turnsBefore = turnRowCount(store)
      const payloadBefore = sessionPayloadOf(store, 'sess-full')

      const step = MIGRATIONS[21]
      expect(step).toBeTypeOf('function')
      expect(() => step!(store.getDatabase())).not.toThrow()

      // The same values, not merely the same presence: a re-run must not
      // re-materialize a row differently.
      expect(readColumns(store, 'sess-full')).toEqual(before.full)
      expect(readColumns(store, 'sess-plain')).toEqual(before.plain)
      expect(readColumns(store, 'sess-object-problems')).toEqual(before.objectProblems)
      expect(readColumns(store, 'sess-null-summary')).toEqual(before.nullSummary)
      expect(readColumns(store, 'sess-unparseable')).toEqual(before.unparseable)

      // No duplicate turn rows, no rewritten payload, and the version stands.
      expect(turnRowCount(store)).toBe(turnsBefore)
      expect(sessionPayloadOf(store, 'sess-full')).toBe(payloadBefore)
      expect(store.getMetadata('schema_version')).toBe('22')
    } finally {
      store.close()
    }
  })

  it('re-opening the migrated store changes nothing either', () => {
    const path = v21StoreAt()
    const store = new CanonStore(path)
    const materialized = readColumns(store, 'sess-full')
    const turns = turnRowCount(store)
    store.close()

    const reopened = new CanonStore(path)
    try {
      expect(reopened.getMetadata('schema_version')).toBe('22')
      expect(readColumns(reopened, 'sess-full')).toEqual(materialized)
      expect(turnRowCount(reopened)).toBe(turns)
    } finally {
      reopened.close()
    }
  })

  it('leaves the turn split untouched: no turns key, same bytes, same turn rows', () => {
    // The materialization rides along in the pass that already splits the
    // turns; it must not change what that pass does to the payload or to the
    // turn rows.
    const path = v21StoreAt()
    const store = new CanonStore(path)
    try {
      expect(store.getMetadata('schema_version')).toBe('22')

      const rewritten = JSON.parse(sessionPayloadOf(store, 'sess-full')) as Record<string, unknown>
      expect(rewritten).not.toHaveProperty('turns')
      expect(sessionPayloadOf(store, 'sess-full')).toBe(withoutTurns(JSON.stringify(FULL_PAYLOAD)))

      expect(turnRowsOf(store, 'sess-full')).toEqual([
        { index: 0, turn: THREE_TURNS[0] },
        { index: 1, turn: THREE_TURNS[1] },
        { index: 2, turn: THREE_TURNS[2] },
      ])
      expect(turnRowsOf(store, 'sess-plain')).toEqual([{ index: 0, turn: ONE_TURN[0] }])
      expect(turnRowsOf(store, 'sess-object-problems')).toEqual([])
    } finally {
      store.close()
    }
  })
})
