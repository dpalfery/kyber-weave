// The engine's startup path migrates the canon store without blocking the
// event loop.
//
// WHY: the v21 -> v22 backfill parses and rewrites every session payload --
// 339 sessions and 80 MB of payloads on the live corpus. The store's
// constructor migrates in ONE blocking pass, so an engine opened against a
// store that needed migrating froze for the whole of it: no handler answered,
// no progress was reported, and the process read as hung.
//
// The asynchronous migration already exists -- `migrateCanonDatabase` hands
// the event loop back between sessions, and its own contract pins that --
// but nothing called it. This file pins that the ENGINE calls it: the
// startup path migrates BEFORE it opens the store, and it does so without
// holding the event loop.
//
// The contract is structural, not a timing threshold. The yield count is the
// claim, counted the way the migration's own contract counts it: one yield
// per session, so the count grows with the corpus.

import { mkdtempSync, rmSync } from 'node:fs'
import { request, type Server } from 'node:http'
import { createRequire } from 'node:module'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import type { AddressInfo } from 'node:net'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { CanonStore, migrateCanonDatabase, SCHEMA_VERSION } from '../canon/store.js'
import type { ChildResult, JobSpawner } from '../jobs/host.js'
import type { ReceiverProbe } from '../jobs/receiver-host.js'
import { runWebDashboard } from './web.js'

const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}

const dirs: string[] = []

/**
 * The store path the engine resolves, restored after every test.
 *
 * WHY it is saved and restored: `resolveCanonDbPath` reads the environment
 * at call time, so the engine is pointed at a fixture by setting the
 * variable -- and a variable left behind would point the NEXT test at a
 * store that is already deleted.
 */
const previousCanonDb = process.env['KYBER_CANON_DB']

beforeEach(() => {
  delete process.env['KYBER_CANON_DB']
})

afterEach(() => {
  if (previousCanonDb === undefined) delete process.env['KYBER_CANON_DB']
  else process.env['KYBER_CANON_DB'] = previousCanonDb
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
 * the stamp is wound back: a genuine v21 store, which is exactly what the
 * live corpus is. The build that opens it runs the v21 -> v22 step and
 * nothing else.
 */
function v21StoreAt(sessionCount: number): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-web-migrate-'))
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
      const sessionId = `sess-web-${index}`
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

/** A store already at this build's schema version, holding `sessionCount` sessions. */
async function currentStoreAt(sessionCount: number): Promise<string> {
  const path = v21StoreAt(sessionCount)
  await migrateCanonDatabase(path)
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
      .prepare('SELECT session_id, turn_index FROM session_turn ORDER BY session_id, turn_index')
      .all() as Array<Record<string, unknown>>
  } finally {
    db.close()
  }
}

class IdleChild {
  readonly exited: Promise<ChildResult> = new Promise(() => {})
  kill(): void {}
}
const idleSpawner: JobSpawner = { spawn: () => new IdleChild() }

/**
 * The yields the startup path handed the event loop back through.
 *
 * <remarks>
 * `count` is the number of hand-backs. `witnessRanBeforeSecondYield` is the
 * behavioural half: work queued DURING the migration ran before the
 * migration finished, which is what "the event loop was handed back" means.
 */
type Yields = {
  count: number
  witnessRanBeforeSecondYield: boolean
}

/**
 * The engine, started against the store at `dbPath`.
 *
 * <remarks>
 * Nothing is injected for the store: the engine must resolve and open the
 * store itself, because that resolution IS the startup path under test. The
 * yield seam is the only injection -- it is how the contract counts the
 * hand-backs the migration makes.
 */
async function startEngine(dbPath: string, yields: Yields): Promise<{ server: Server; base: string }> {
  process.env['KYBER_CANON_DB'] = dbPath
  const stateDir = mkdtempSync(join(tmpdir(), 'kyber-web-migrate-state-'))
  dirs.push(stateDir)
  let witnessQueued = false
  let witnessRan = false
  const options = {
    port: 0,
    open: false,
    stateDir,
    writeStdout: () => {},
    program: 'kyberdash-test',
    jobSpawner: idleSpawner,
    receiverSpawner: idleSpawner,
    receiverProber: { probe: async (): Promise<ReceiverProbe> => 'refused' },
    // The maintenance pass is engine work over the whole corpus; the store's
    // migration is what this file is about, so the pass is held out of the way.
    maintenance: async () => {},
    migrationYield: async () => {
      yields.count += 1
      if (yields.count === 2) {
        // The witness queued during the FIRST hand-back must have run by now.
        // The migration gave the loop a turn, so work queued during the
        // migration ran before the migration finished -- which is the claim,
        // and not a timing threshold.
        yields.witnessRanBeforeSecondYield = witnessRan
      }
      if (!witnessQueued) {
        witnessQueued = true
        setImmediate(() => {
          witnessRan = true
        })
      }
      // A real hand-back, not just a counter: the seam gives the loop a turn
      // of its own, so the migration's own progress is what the count means.
      await new Promise<void>((resolve) => {
        setImmediate(resolve)
      })
    },
  }
  const server = await runWebDashboard(options)
  return { server, base: `http://127.0.0.1:${(server.address() as AddressInfo).port}` }
}

/** Close the engine, so the next one starts from a clean process singleton. */
function stopEngine(server: Server): Promise<void> {
  return new Promise<void>((resolve) => {
    if (!server.listening) {
      resolve()
      return
    }
    server.close(() => resolve())
  })
}

/** One GET against the running engine. */
function get(base: string, path: string): Promise<{ status: number; text: string }> {
  return new Promise((resolve, reject) => {
    const req = request(`${base}${path}`, { method: 'GET' }, (res) => {
      let text = ''
      res.setEncoding('utf8')
      res.on('data', (chunk: string) => {
        text += chunk
      })
      res.on('end', () => resolve({ status: res.statusCode ?? 0, text }))
    })
    req.on('error', reject)
    req.end()
  })
}

describe('the engine migrates the canon store at startup, without blocking', () => {
  it('hands the event loop back between sessions, and more of them as the corpus grows', async () => {
    // The structural claim: the startup migration yields per session, so the
    // event loop is handed back while the backfill runs. A count that grew
    // with the corpus is the difference between a per-session hand-back and
    // a single hand-back at the end of the pass.
    const small = { count: 0, witnessRanBeforeSecondYield: false }
    const large = { count: 0, witnessRanBeforeSecondYield: false }

    const smallPath = v21StoreAt(2)
    const smallEngine = await startEngine(smallPath, small)
    await stopEngine(smallEngine.server)

    // The second fixture is a store of its own: the engine resolved and
    // opened the store itself, so the second startup must not be answering
    // with the first startup's handle.
    const largePath = v21StoreAt(6)
    expect(schemaVersionOf(largePath), 'the second fixture must start at v21').toBe('21')

    const largeEngine = await startEngine(largePath, large)
    await stopEngine(largeEngine.server)

    expect(small.count, 'a multi-session startup must hand the event loop back').toBeGreaterThanOrEqual(1)
    expect(large.count, 'a multi-session startup must hand the event loop back').toBeGreaterThanOrEqual(1)
    expect(large.count, 'the hand-back is per session, not once at the end').toBeGreaterThan(small.count)
    expect(
      small.witnessRanBeforeSecondYield,
      'work queued during the migration must run before the migration finishes',
    ).toBe(true)
    expect(
      large.witnessRanBeforeSecondYield,
      'work queued during the migration must run before the migration finishes',
    ).toBe(true)
  })

  it('has the store fully migrated by the time the engine serves', async () => {
    // The properties the migration's own contract pins, read back after the
    // engine is up: the store is at this build's version, the turn table
    // exists, the materialized columns are populated, and the turns have
    // left the payload.
    const path = v21StoreAt(3)
    const engine = await startEngine(path, { count: 0, witnessRanBeforeSecondYield: false })

    const served = await get(engine.base, '/api/kyber/sessions')
    expect(served.status).toBe(200)

    expect(schemaVersionOf(path)).toBe(String(SCHEMA_VERSION))

    const table = new DatabaseSync(path)
    try {
      const turnTable = table
        .prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'session_turn'")
        .get()
      expect(turnTable, 'the migrated store must carry the turn table').toBeDefined()
    } finally {
      table.close()
    }

    const rows = sessionRows(path)
    try {
      expect(rows).toHaveLength(3)
      for (const row of rows) {
        expect(row['summary_json'], 'the summary column must be materialized').not.toBeNull()
        expect(row['problems_count'], 'the problems count must be materialized').not.toBeNull()
        expect(row['context_limit_source'], 'the window provenance must be materialized').not.toBeNull()
        const payload = JSON.parse(row['payload'] as string) as Record<string, unknown>
        expect(payload, 'the turns must have left the payload').not.toHaveProperty('turns')
      }
      expect(turnRows(path)).toHaveLength(9)
    } finally {
      await stopEngine(engine.server)
    }
  })

  it('leaves a store that needs no migration alone', async () => {
    // A store already at this build's version has nothing to migrate, so
    // the startup path must not walk it: no yields, and the data is exactly
    // as it was.
    const path = await currentStoreAt(4)
    const yields = { count: 0, witnessRanBeforeSecondYield: false }

    const engine = await startEngine(path, yields)

    try {
      expect(yields.count, 'a current store must not be walked').toBe(0)
      expect(schemaVersionOf(path)).toBe(String(SCHEMA_VERSION))
      expect(sessionRows(path)).toHaveLength(4)
    } finally {
      await stopEngine(engine.server)
    }
  })

  it('keeps the synchronous constructor path working', async () => {
    // Hundreds of call sites construct `new CanonStore(path)` and expect the
    // store to be current the moment it is open. That path is untouched by
    // the asynchronous one, and it stays the contract for a caller that
    // cannot await.
    const path = v21StoreAt(2)

    new CanonStore(path).close()

    expect(schemaVersionOf(path)).toBe(String(SCHEMA_VERSION))
    expect(turnRows(path)).toHaveLength(6)
    for (const row of sessionRows(path)) {
      expect(row['summary_json']).not.toBeNull()
      expect(row['context_limit_source']).not.toBeNull()
    }
  })
})
