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
 * The v19 session table DDL, before the v20 sync progress and transcript progress columns.
 */
const V19_SESSION_TABLE_SQL = `
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
  payload TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS session_by_started ON session (started);
`

/**
 * Creates a v19 store fixture with a legacy session row.
 * Can optionally pre-populate the v20 columns to test presence guards.
 */
function v19StoreAt(options?: { withV20Columns?: boolean }): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-migration-v19-'))
  dirs.push(dir)
  const path = join(dir, 'canon.db')

  // Initialize a baseline store to get all non-session tables (records, metadata, etc.)
  new CanonStore(path).close()

  const db = new DatabaseSync(path)
  try {
    // Replace session with the exact v19 schema
    db.exec('DROP TABLE IF EXISTS session;')
    db.exec(V19_SESSION_TABLE_SQL)
    db.prepare('UPDATE metadata SET value = ? WHERE key = ?').run('19', 'schema_version')

    if (options?.withV20Columns) {
      db.exec(`
        ALTER TABLE session ADD COLUMN synced_at TEXT;
        ALTER TABLE session ADD COLUMN sync_status TEXT DEFAULT 'pending';
        ALTER TABLE session ADD COLUMN settled_at TEXT;
        ALTER TABLE session ADD COLUMN transcript_path TEXT;
        ALTER TABLE session ADD COLUMN transcript_offset INTEGER DEFAULT 0;
      `)
    }

    db.prepare(
      `INSERT INTO session (
        session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, payload
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    ).run(
      'sess-legacy-v19',
      'claude-code',
      'Legacy Session',
      0,
      null,
      'architect',
      'kyber-weave',
      'main',
      '2026-10-14T10:00:00.000Z',
      '2026-10-14T10:30:00.000Z',
      JSON.stringify({ note: 'legacy session row' }),
    )
  } finally {
    db.close()
  }

  return path
}

describe('schema migration v19 -> v20 (session sync progress and settling)', () => {
  it('pins SCHEMA_VERSION to 20 and defines migration 19', () => {
    expect(SCHEMA_VERSION).toBe(20)
    expect(MIGRATIONS[19]).toBeTypeOf('function')
  })

  it('initializes session table on fresh store construction with v20 columns and defaults', () => {
    const dir = mkdtempSync(join(tmpdir(), 'kyber-fresh-v20-'))
    dirs.push(dir)
    const path = join(dir, 'canon.db')
    const store = new CanonStore(path)
    try {
      expect(store.getMetadata('schema_version')).toBe('20')

      const cols = tableInfo(path, 'session')
      const colMap = new Map(cols.map((c) => [c.name, c]))

      expect(colMap.get('synced_at')).toMatchObject({
        type: 'TEXT',
        dflt_value: null,
      })
      expect(colMap.get('sync_status')).toMatchObject({
        type: 'TEXT',
        dflt_value: "'pending'",
      })
      expect(colMap.get('settled_at')).toMatchObject({
        type: 'TEXT',
        dflt_value: null,
      })
      expect(colMap.get('transcript_path')).toMatchObject({
        type: 'TEXT',
        dflt_value: null,
      })
      expect(colMap.get('transcript_offset')).toMatchObject({
        type: 'INTEGER',
        dflt_value: '0',
      })

      // Inserting a minimal session row honors default values for omitted v20 fields
      const db = new DatabaseSync(path)
      try {
        db.prepare(
          `INSERT INTO session (session_id, harness, payload) VALUES (?, ?, ?)`,
        ).run('fresh-s1', 'claude-code', '{}')

        const row = db
          .prepare(
            `SELECT synced_at, sync_status, settled_at, transcript_path, transcript_offset FROM session WHERE session_id = ?`,
          )
          .get('fresh-s1') as {
            synced_at: string | null
            sync_status: string
            settled_at: string | null
            transcript_path: string | null
            transcript_offset: number
          }
        expect(row.synced_at).toBeNull()
        expect(row.sync_status).toBe('pending')
        expect(row.settled_at).toBeNull()
        expect(row.transcript_path).toBeNull()
        expect(row.transcript_offset).toBe(0)
      } finally {
        db.close()
      }
    } finally {
      store.close()
    }
  })

  it('migrates an existing v19 store to v20 adding new columns without losing session data', () => {
    const path = v19StoreAt()
    const store = new CanonStore(path)
    try {
      expect(store.getMetadata('schema_version')).toBe('20')

      const cols = tableInfo(path, 'session')
      const colNames = cols.map((c) => c.name)
      expect(colNames).toEqual(
        expect.arrayContaining([
          'synced_at',
          'sync_status',
          'settled_at',
          'transcript_path',
          'transcript_offset',
        ]),
      )

      const colMap = new Map(cols.map((c) => [c.name, c]))
      expect(colMap.get('synced_at')?.type).toBe('TEXT')
      expect(colMap.get('sync_status')?.type).toBe('TEXT')
      expect(colMap.get('sync_status')?.dflt_value).toBe("'pending'")
      expect(colMap.get('settled_at')?.type).toBe('TEXT')
      expect(colMap.get('transcript_path')?.type).toBe('TEXT')
      expect(colMap.get('transcript_offset')?.type).toBe('INTEGER')
      expect(colMap.get('transcript_offset')?.dflt_value).toBe('0')

      // Legacy row survives intact with defaults for new columns
      const db = new DatabaseSync(path)
      try {
        const row = db
          .prepare(
            `SELECT session_id, harness, label, payload, synced_at, sync_status, settled_at, transcript_path, transcript_offset FROM session WHERE session_id = ?`,
          )
          .get('sess-legacy-v19') as {
            session_id: string
            harness: string
            label: string
            payload: string
            synced_at: string | null
            sync_status: string
            settled_at: string | null
            transcript_path: string | null
            transcript_offset: number
          }
        expect(row).toBeDefined()
        expect(row.session_id).toBe('sess-legacy-v19')
        expect(row.harness).toBe('claude-code')
        expect(row.label).toBe('Legacy Session')
        expect(row.payload).toBe(JSON.stringify({ note: 'legacy session row' }))
        expect(row.synced_at).toBeNull()
        expect(row.sync_status).toBe('pending')
        expect(row.settled_at).toBeNull()
        expect(row.transcript_path).toBeNull()
        expect(row.transcript_offset).toBe(0)
      } finally {
        db.close()
      }
    } finally {
      store.close()
    }

    // Migration is idempotent: reopening leaves metadata and columns intact
    const reopened = new CanonStore(path)
    try {
      expect(reopened.getMetadata('schema_version')).toBe('20')
    } finally {
      reopened.close()
    }
  })

  it('guards against duplicate column errors when migration 19 is re-run on existing columns', () => {
    // 1. A v19 store that already has the columns opens cleanly without "duplicate column name" error
    const path = v19StoreAt({ withV20Columns: true })
    const store = new CanonStore(path)
    try {
      expect(store.getMetadata('schema_version')).toBe('20')
      const colNames = tableInfo(path, 'session').map((c) => c.name)
      expect(colNames).toEqual(
        expect.arrayContaining([
          'synced_at',
          'sync_status',
          'settled_at',
          'transcript_path',
          'transcript_offset',
        ]),
      )
    } finally {
      store.close()
    }

    // 2. Direct execution of MIGRATIONS[19] is idempotent on a database handle
    const db = new DatabaseSync(':memory:')
    try {
      db.exec(V19_SESSION_TABLE_SQL)
      expect(MIGRATIONS[19]).toBeTypeOf('function')
      // First invocation adds the columns
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      MIGRATIONS[19](db as any)
      // Second invocation must presence-guard and not throw "duplicate column name"
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      expect(() => MIGRATIONS[19](db as any)).not.toThrow()
    } finally {
      db.close()
    }

    // 3. Migration step tolerates missing session table gracefully
    const dbNoSession = new DatabaseSync(':memory:')
    try {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      expect(() => MIGRATIONS[19](dbNoSession as any)).not.toThrow()
    } finally {
      dbNoSession.close()
    }
  })
})
