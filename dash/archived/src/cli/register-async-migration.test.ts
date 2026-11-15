// The CLI's store-touching verbs migrate the canon store without blocking.
//
// WHY: the same v21 -> v22 backfill the engine now migrates asynchronously
// runs inside `new CanonStore(path)` -- one blocking pass over every session
// payload in the corpus. A `dash refresh` against a store that needed
// migrating froze for the whole of it, with the refresh lock held and no
// progress reported, and a SIGINT could not be handled until it finished.
//
// The CLI verbs therefore run the same asynchronous migration before they
// open the store, and the store's own migration is then a no-op.
//
// `dash vacuum` is deliberately NOT in this file: it does not use
// `CanonStore` at all -- it opens the file itself, so its dry run writes
// nothing -- and that behaviour is unchanged.

import { mkdtempSync, rmSync } from 'node:fs'
import { createRequire } from 'node:module'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { Command } from 'commander'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { CanonStore, SCHEMA_VERSION } from '../canon/store.js'
import { registerKyberCommands } from './register.js'

const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}

const dirs: string[] = []

beforeEach(() => {
  delete process.env['KYBER_CANON_DB']
})

afterEach(() => {
  delete process.env['KYBER_CANON_DB']
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

/** A v21-shaped session payload: the whole session blob, turns included. */
function v21Payload(sessionId: string): Record<string, unknown> {
  return {
    id: sessionId,
    session_id: sessionId,
    harness: 'claude-code',
    label: `Session ${sessionId}`,
    span_count: 1,
    summary: { turn_count: 1, total_input: 1200, total_output: 300 },
    context: { contextLimitSource: 'reported' },
    problems: [],
    turns: [
      {
        index: 0,
        spanId: `span-turn-${sessionId}`,
        timestamp: '2026-10-14T10:01:00.000Z',
        model: 'opus-4-1',
        input: 1200,
        output: 300,
      },
    ],
  }
}

/** A store at schema version 21, holding `sessionCount` sessions. */
function v21StoreAt(sessionCount: number): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-cli-migrate-'))
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
    for (let index = 0; index < sessionCount; index += 1) {
      const sessionId = `sess-cli-${index}`
      insert.run(
        sessionId,
        'claude-code',
        `Session ${sessionId}`,
        0,
        '2026-10-14T10:00:00.000Z',
        '2026-10-14T10:30:00.000Z',
        JSON.stringify(v21Payload(sessionId)),
      )
    }
  } finally {
    db.close()
  }

  return path
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

/** The number of turn rows a store carries. */
function turnCountOf(path: string): number {
  const db = new DatabaseSync(path)
  try {
    const table = db
      .prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'session_turn'")
      .get()
    if (table === undefined) return 0
    return (db.prepare('SELECT COUNT(*) AS n FROM session_turn').get() as { n: number }).n
  } finally {
    db.close()
  }
}

/** The yields the verb handed the event loop back through. */
type Yields = { count: number }

/**
 * The `dash refresh` verb, run against the store at `dbPath`.
 *
 * <remarks>
 * The store is NOT injected: the verb must open it itself, because opening
 * it IS the startup path under test. The refresh itself is injected, so the
 * verb never reads the machine's own harness history, and the lock is
 * injected, so no lock file is taken in the real state directory.
 */
async function runRefresh(dbPath: string, yields: Yields): Promise<void> {
  const program = new Command()
  program.exitOverride()
  registerKyberCommands(program, {
    write: () => {},
    writeError: () => {},
    migrationYield: async () => {
      yields.count += 1
    },
    acquireStoreRefreshLock: async () => ({
      outcome: 'acquired' as const,
      handle: {
        token: 't',
        release: async () => {},
        verifyStillOwner: async () => true,
      },
    }),
    refreshHarnessSources: async () => ({
      historyWeeks: 2,
      commandStartedAt: new Date().toISOString(),
      rows: [],
      derived: { sessions: 0, runs: 0, executions: 0, rollups: 0 },
      failedJobs: 0,
      derivationFailed: false,
      exitCode: 0,
    }),
  })
  const previousExitCode = process.exitCode
  process.exitCode = undefined
  try {
    await program.parseAsync(['node', 'kyberdash', 'dash', 'refresh', '--db', dbPath])
  } finally {
    process.exitCode = previousExitCode
  }
}

/**
 * The `dash import-history` verb, run against the store at `dbPath`.
 */
async function runImportHistory(dbPath: string, yields: Yields): Promise<void> {
  const program = new Command()
  program.exitOverride()
  registerKyberCommands(program, {
    write: () => {},
    writeError: () => {},
    migrationYield: async () => {
      yields.count += 1
    },
    acquireStoreRefreshLock: async () => ({
      outcome: 'acquired' as const,
      handle: {
        token: 't',
        release: async () => {},
        verifyStillOwner: async () => true,
      },
    }),
    importFolderHistory: async () => {},
  })
  const previousExitCode = process.exitCode
  process.exitCode = undefined
  try {
    await program.parseAsync(['node', 'kyberdash', 'dash', 'import-history', '--db', dbPath])
  } finally {
    process.exitCode = previousExitCode
  }
}

describe('the CLI migrates the canon store before it opens it', () => {
  it('migrates asynchronously in `dash refresh`, and leaves the store current', async () => {
    const path = v21StoreAt(3)
    const yields = { count: 0 }

    await runRefresh(path, yields)

    expect(yields.count, 'the refresh must hand the event loop back while it migrates')
      .toBeGreaterThanOrEqual(1)
    expect(schemaVersionOf(path)).toBe(String(SCHEMA_VERSION))
    expect(turnCountOf(path)).toBe(3)
  })

  it('migrates asynchronously in `dash import-history`, and leaves the store current', async () => {
    const path = v21StoreAt(2)
    const yields = { count: 0 }

    await runImportHistory(path, yields)

    expect(yields.count, 'the import must hand the event loop back while it migrates')
      .toBeGreaterThanOrEqual(1)
    expect(schemaVersionOf(path)).toBe(String(SCHEMA_VERSION))
    expect(turnCountOf(path)).toBe(2)
  })
})
