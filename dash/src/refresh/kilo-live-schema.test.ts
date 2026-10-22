// T1 RED — the Kilo kilo.db contract for `kilo-shared-runtime`.
//
// `kilo-shared-runtime` reads ~/.local/share/kilo/kilo.db, a store Kilo migrates
// on its own release schedule. Two halves of one contract are pinned here.
//
// T1a (characterization) — the LIVE column set: `session` carries `path`,
// `agent`, `model`, `cost`, `tokens_*` and `metadata`, and the store also holds
// `session_message`, `session_input` and `event` beside `message`/`part`. A
// store shaped like the live build must still land one canonical record per
// assistant message, with that message's own counters. This one is a
// characterization, not a bug report: it is expected to pass before any source
// change, and a pass is the evidence that the live shape is already read.
//
// T1b — a store that is PRESENT but unreadable is a problem to report, never a
// silent zero. `discoverSqliteSessions`
// (dash/src/providers/sqlite-session-parser.ts) `continue`s past both shapes
// today, so the harness reports `unavailable` with no problem row at all: the
// operator sees an empty Kilo panel and no reason. Only an ABSENT directory is
// a negative existence check and stays silent.
//
// Both drive the real `refreshHarnessSources` entry point against a temp
// XDG_DATA_HOME, never ~/.kyberdash/canon.db — the same pattern as the #227
// fixture in issue-189-sessions.test.ts.

import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createRequire } from 'node:module'

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// Passthrough mock: the busy-at-discovery regression stubs `openDatabase`
// once; every other call (and every other export) falls through to the real
// module, so fixtures and unrelated parses behave exactly as unmocked.
vi.mock('../ingest/sqlite.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../ingest/sqlite.js')>()
  return { ...actual, openDatabase: vi.fn(actual.openDatabase) }
})

import type { SpanProblem } from '../canon/store.js'
import { CanonStore } from '../canon/store.js'
import { createCodexProvider } from '../providers/codex.js'
import { createKiloCodeProvider } from '../providers/kilo-code.js'
import type { Provider } from '../providers/types.js'
import { PROVIDER_PARSE_ERROR } from '../synth/provider.js'

import { COMMAND_STARTED_AT } from './fixtures/integration-harness.js'
import { refreshHarnessSources } from './orchestrator.js'
import { descriptorFor } from './registry.js'

const requireForTest = createRequire(import.meta.url)
type SqliteDb = {
  exec(sql: string): void
  prepare(sql: string): { run(...params: unknown[]): void }
  close(): void
}
const openSqlite = (path: string): SqliteDb => {
  const { DatabaseSync } = requireForTest('node:sqlite') as {
    DatabaseSync: new (path: string) => SqliteDb
  }
  return new DatabaseSync(path)
}

let root: string
let previousXdg: string | undefined

beforeEach(() => {
  root = mkdtempSync(join(tmpdir(), 'kilo-live-schema-'))
  previousXdg = process.env['XDG_DATA_HOME']
  process.env['XDG_DATA_HOME'] = join(root, 'xdg')
})

afterEach(() => {
  rmSync(root, { recursive: true, force: true })
  if (previousXdg === undefined) delete process.env['XDG_DATA_HOME']
  else process.env['XDG_DATA_HOME'] = previousXdg
})

function descriptors(...ids: string[]) {
  return ids.map((id) => {
    const descriptor = descriptorFor(id)
    if (descriptor === undefined) throw new Error(`missing descriptor ${id}`)
    return descriptor
  })
}

const KILO_HARNESSES = ['kilo-shared-runtime', 'kilo-vscode-legacy'] as const
const CODEX_HARNESSES = ['codex-cli', 'codex-desktop', 'codex-unclassified'] as const

async function refresh(
  store: CanonStore,
  providers: readonly Provider[],
  harnessIds: readonly string[],
): Promise<void> {
  await refreshHarnessSources(
    store,
    {
      getAllProviders: async () => [...providers],
      descriptors: descriptors(...harnessIds),
      jobConcurrency: 1,
      writerCapacity: 1,
      commandStartedAt: COMMAND_STARTED_AT,
      parseAllSessions: async () => undefined,
    },
    { historyWeeks: 5200 },
  )
}

// A companion harness in the same run: it proves the kilo failure neither
// aborts the run nor takes another harness's records with it.
function writeCodexCompanionRollout(codexHome: string): void {
  const dayDir = join(codexHome, 'sessions', '2026', '09', '10')
  mkdirSync(dayDir, { recursive: true })
  writeFileSync(
    join(dayDir, 'rollout-kilo-companion.jsonl'),
    [
      JSON.stringify({
        type: 'session_meta',
        timestamp: '2026-09-10T10:00:00Z',
        payload: {
          cwd: '/Users/test/myproject',
          originator: 'codex_cli_rs',
          session_id: 'sess-codex-companion',
          model: 'gpt-5.3-codex',
        },
      }),
      JSON.stringify({
        type: 'event_msg',
        timestamp: '2026-09-10T10:01:00Z',
        payload: {
          type: 'token_count',
          info: {
            model: 'gpt-5.3-codex',
            last_token_usage: { input_tokens: 100, cached_input_tokens: 0, output_tokens: 50, reasoning_output_tokens: 0, total_tokens: 150 },
            total_token_usage: { input_tokens: 100, cached_input_tokens: 0, output_tokens: 50, reasoning_output_tokens: 0, total_tokens: 150 },
          },
        },
      }),
    ].join('\n') + '\n',
  )
}

function kiloParseProblems(store: CanonStore): SpanProblem[] {
  return store.getProblems().filter((problem) => problem.code === PROVIDER_PARSE_ERROR)
}

// The live store: every column the current Kilo build writes onto `session`,
// plus the auxiliary tables it maintains beside `message`/`part`.
function writeLiveSchemaKiloDb(dbPath: string): void {
  const db = openSqlite(dbPath)
  db.exec(`
    CREATE TABLE session (
      id TEXT PRIMARY KEY, project_id TEXT NOT NULL, parent_id TEXT,
      slug TEXT NOT NULL, directory TEXT NOT NULL, title TEXT NOT NULL,
      version TEXT NOT NULL, time_created INTEGER, time_updated INTEGER,
      time_archived INTEGER,
      path TEXT, agent TEXT, model TEXT, cost REAL,
      tokens_input INTEGER, tokens_output INTEGER, tokens_reasoning INTEGER,
      tokens_cache_read INTEGER, tokens_cache_write INTEGER, metadata TEXT
    );
    CREATE TABLE message (
      id TEXT PRIMARY KEY, session_id TEXT NOT NULL,
      time_created INTEGER, time_updated INTEGER, data TEXT NOT NULL
    );
    CREATE TABLE part (
      id TEXT PRIMARY KEY, message_id TEXT NOT NULL,
      session_id TEXT NOT NULL, time_created INTEGER,
      time_updated INTEGER, data TEXT NOT NULL
    );
    CREATE TABLE session_message (
      session_id TEXT NOT NULL, message_id TEXT NOT NULL
    );
    CREATE TABLE session_input (
      session_id TEXT NOT NULL, input TEXT
    );
    CREATE TABLE event (
      id TEXT PRIMARY KEY, session_id TEXT NOT NULL, data TEXT NOT NULL
    );
  `)

  const now = Date.parse('2026-09-10T10:00:00.000Z')
  db.prepare(`INSERT INTO session (id, project_id, parent_id, slug, directory, title, version, time_created, time_updated, time_archived,
                                   path, agent, model, cost, tokens_input, tokens_output, tokens_reasoning,
                                   tokens_cache_read, tokens_cache_write, metadata)
              VALUES ('sess-kilo-live', 'proj-1', NULL, 'slug-live', '/Users/hal/myproject', 'Kilo live session', '1.4.2', ?, ?, NULL,
                      '/Users/hal/myproject', 'build', ?, 0.42, 3000, 800, 0, 0, 0, ?)`)
    .run(
      now,
      now,
      JSON.stringify({ id: 'claude-opus-4-6', providerID: 'anthropic' }),
      JSON.stringify({ source: 'kilo-shared-runtime' }),
    )

  db.prepare(`INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ('msg-u1', 'sess-kilo-live', ?, ?, ?)`)
    .run(now, now, JSON.stringify({ role: 'user' }))
  db.prepare(`INSERT INTO part (id, message_id, session_id, time_created, time_updated, data) VALUES ('part-u1', 'msg-u1', 'sess-kilo-live', ?, ?, ?)`)
    .run(now, now, JSON.stringify({ type: 'text', text: 'inspect the live schema' }))

  // Assistant turn 1 — normalized `tokens` object plus a tool part.
  db.prepare(`INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ('msg-a1', 'sess-kilo-live', ?, ?, ?)`)
    .run(now + 1000, now + 1000, JSON.stringify({
      role: 'assistant',
      modelID: 'claude-opus-4-6',
      cost: 0.02,
      tokens: { input: 1000, output: 200, reasoning: 0, cache: { read: 0, write: 0 } },
    }))
  db.prepare(`INSERT INTO part (id, message_id, session_id, time_created, time_updated, data) VALUES ('part-a1-text', 'msg-a1', 'sess-kilo-live', ?, ?, ?)`)
    .run(now + 1000, now + 1000, JSON.stringify({ type: 'text', text: 'reading the store' }))
  db.prepare(`INSERT INTO part (id, message_id, session_id, time_created, time_updated, data) VALUES ('part-a1-tool', 'msg-a1', 'sess-kilo-live', ?, ?, ?)`)
    .run(now + 1001, now + 1001, JSON.stringify({ type: 'tool', tool: 'bash', state: { input: { command: 'ls' } } }))

  // Assistant turn 2 — flat token keys, the spelling the live store also writes.
  db.prepare(`INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ('msg-a2', 'sess-kilo-live', ?, ?, ?)`)
    .run(now + 2000, now + 2000, JSON.stringify({
      role: 'assistant',
      modelID: 'claude-opus-4-6',
      tokens_input: 2000,
      tokens_output: 600,
    }))
  db.prepare(`INSERT INTO part (id, message_id, session_id, time_created, time_updated, data) VALUES ('part-a2-text', 'msg-a2', 'sess-kilo-live', ?, ?, ?)`)
    .run(now + 2000, now + 2000, JSON.stringify({ type: 'text', text: 'done' }))

  db.prepare(`INSERT INTO event (id, session_id, data) VALUES ('evt-1', 'sess-kilo-live', ?)`)
    .run(JSON.stringify({ type: 'session.updated' }))
  db.close()
}

// T1a — characterization: the live schema must already land records.
describe('kilo live schema T1a (live column set)', () => {
  it('lands one canonical record per assistant message with that message\'s own token counts', async () => {
    const kiloDir = join(root, 'xdg', 'kilo')
    mkdirSync(kiloDir, { recursive: true })
    writeLiveSchemaKiloDb(join(kiloDir, 'kilo.db'))

    const store = new CanonStore(join(root, 'canon-t1a.db'))
    try {
      await refresh(store, [createKiloCodeProvider()], KILO_HARNESSES)

      const allRecords = store.listAll()
      const kiloRecords = allRecords.filter((record) => record.harness === 'kilo-shared-runtime')
      expect(
        kiloRecords.length,
        `live-schema kilo fixture must land 2 records, one per assistant message; landed harnesses: ${JSON.stringify(allRecords.map((record) => record.harness))}`,
      ).toBe(2)

      // Ordered by input so the assertion names each turn's own counter rather
      // than a session total that a session-level fallback could also produce.
      const byInput = [...kiloRecords].sort((left, right) => left.tokens.reportedInput - right.tokens.reportedInput)
      expect(byInput.map((record) => record.tokens.reportedInput)).toEqual([1000, 2000])
      expect(byInput.map((record) => record.tokens.reportedOutput)).toEqual([200, 600])

      const checkpoints = store.listSourceCheckpoints('kilo-shared-runtime')
      expect(checkpoints).toHaveLength(1)
      expect(checkpoints[0]!.recordCount).toBe(2)
    } finally {
      store.close()
    }
  })
})

// T1b — RED: a present-but-unreadable store must surface a problem.
describe('kilo live schema T1b (present-but-unreadable store)', () => {
  it('records exactly one PROVIDER_PARSE_ERROR naming the source for a kilo.db holding garbage bytes', async () => {
    const kiloDir = join(root, 'xdg', 'kilo')
    mkdirSync(kiloDir, { recursive: true })
    writeFileSync(join(kiloDir, 'kilo.db'), Buffer.from('this is not a sqlite database\n'.repeat(8), 'utf8'))
    const codexHome = join(root, '.codex')
    writeCodexCompanionRollout(codexHome)

    const store = new CanonStore(join(root, 'canon-t1b-garbage.db'))
    try {
      await refresh(
        store,
        [createKiloCodeProvider(), createCodexProvider(codexHome)],
        [...KILO_HARNESSES, ...CODEX_HARNESSES],
      )

      const problems = kiloParseProblems(store)
      expect(
        problems.map((problem) => `${problem.harness}: ${problem.message}`),
        'a kilo.db that exists but cannot be read is a problem to report, not a silent zero',
      ).toHaveLength(1)
      const problem = problems[0]!
      expect(problem.harness).toBe('kilo-shared-runtime')
      expect(`${problem.message} ${problem.location ?? ''}`).toContain('kilo.db')

      // The run continues: the companion harness still lands its own records.
      const codexRecords = store.listAll().filter((record) => record.harness.startsWith('codex'))
      expect(codexRecords.length).toBeGreaterThan(0)
    } finally {
      store.close()
    }
  })

  it('records exactly one PROVIDER_PARSE_ERROR naming the source when the part table is missing', async () => {
    const kiloDir = join(root, 'xdg', 'kilo')
    mkdirSync(kiloDir, { recursive: true })
    const db = openSqlite(join(kiloDir, 'kilo.db'))
    db.exec(`
      CREATE TABLE session (
        id TEXT PRIMARY KEY, project_id TEXT NOT NULL, parent_id TEXT,
        slug TEXT NOT NULL, directory TEXT NOT NULL, title TEXT NOT NULL,
        version TEXT NOT NULL, time_created INTEGER, time_updated INTEGER,
        time_archived INTEGER
      );
      CREATE TABLE message (
        id TEXT PRIMARY KEY, session_id TEXT NOT NULL,
        time_created INTEGER, time_updated INTEGER, data TEXT NOT NULL
      );
    `)
    const now = Date.parse('2026-09-10T10:00:00.000Z')
    db.prepare(`INSERT INTO session (id, project_id, parent_id, slug, directory, title, version, time_created, time_updated, time_archived)
                VALUES ('sess-kilo-nopart', 'proj-1', NULL, 'slug-nopart', '/Users/hal/myproject', 'Kilo session', '1.4.2', ?, ?, NULL)`)
      .run(now, now)
    db.prepare(`INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ('msg-a1', 'sess-kilo-nopart', ?, ?, ?)`)
      .run(now + 1000, now + 1000, JSON.stringify({ role: 'assistant', modelID: 'claude-opus-4-6', tokens_input: 100, tokens_output: 50 }))
    db.close()
    const codexHome = join(root, '.codex')
    writeCodexCompanionRollout(codexHome)

    const store = new CanonStore(join(root, 'canon-t1b-nopart.db'))
    try {
      await refresh(
        store,
        [createKiloCodeProvider(), createCodexProvider(codexHome)],
        [...KILO_HARNESSES, ...CODEX_HARNESSES],
      )

      const problems = kiloParseProblems(store)
      expect(
        problems.map((problem) => `${problem.harness}: ${problem.message}`),
        'a kilo.db missing the required part table is a schema problem to report, not a silent zero',
      ).toHaveLength(1)
      const problem = problems[0]!
      expect(problem.harness).toBe('kilo-shared-runtime')
      expect(`${problem.message} ${problem.location ?? ''}`).toContain('kilo.db')

      const codexRecords = store.listAll().filter((record) => record.harness.startsWith('codex'))
      expect(codexRecords.length).toBeGreaterThan(0)
    } finally {
      store.close()
    }
  })

  it('records exactly one PROVIDER_PARSE_ERROR for a store busy at discovery even when it opens cleanly at parse time', async () => {
    // The marker must carry its discovery-time failure: a SQLITE_BUSY store
    // that becomes readable before parse is still a present-but-unreadable
    // read this run, never a silent zero-unit pass. Here the first
    // `openDatabase` call (discovery) throws busy while the underlying db is
    // a fully healthy fixture — any later open would succeed.
    const kiloDir = join(root, 'xdg', 'kilo')
    mkdirSync(kiloDir, { recursive: true })
    writeLiveSchemaKiloDb(join(kiloDir, 'kilo.db'))
    const codexHome = join(root, '.codex')
    writeCodexCompanionRollout(codexHome)

    const sqlite = await import('../ingest/sqlite.js')
    vi.mocked(sqlite.openDatabase).mockImplementationOnce(() => {
      throw Object.assign(new Error('database is locked'), { code: 'SQLITE_BUSY', errcode: 5 })
    })

    const store = new CanonStore(join(root, 'canon-t1b-busy.db'))
    try {
      await refresh(
        store,
        [createKiloCodeProvider(), createCodexProvider(codexHome)],
        [...KILO_HARNESSES, ...CODEX_HARNESSES],
      )

      const problems = kiloParseProblems(store)
      expect(
        problems.map((problem) => `${problem.harness}: ${problem.message}`),
        'a kilo.db busy at discovery is a problem to report — a succeeding second open must not read as an empty store',
      ).toHaveLength(1)
      const problem = problems[0]!
      expect(problem.harness).toBe('kilo-shared-runtime')
      expect(`${problem.message} ${problem.location ?? ''}`).toContain('kilo.db')

      const codexRecords = store.listAll().filter((record) => record.harness.startsWith('codex'))
      expect(codexRecords.length).toBeGreaterThan(0)
    } finally {
      store.close()
    }
  })

  it('records no problem at all when the kilo directory is absent', async () => {
    // Nothing is written under xdg/kilo: absence is a negative existence check.
    const codexHome = join(root, '.codex')
    writeCodexCompanionRollout(codexHome)

    const store = new CanonStore(join(root, 'canon-t1b-absent.db'))
    try {
      await refresh(
        store,
        [createKiloCodeProvider(), createCodexProvider(codexHome)],
        [...KILO_HARNESSES, ...CODEX_HARNESSES],
      )

      expect(
        store.getProblems().map((problem) => `${problem.harness}: ${problem.code}: ${problem.message}`),
        'an absent kilo directory is not a problem — only a present, unreadable one is',
      ).toEqual([])

      // The run did happen: the companion harness landed its records.
      const codexRecords = store.listAll().filter((record) => record.harness.startsWith('codex'))
      expect(codexRecords.length).toBeGreaterThan(0)
    } finally {
      store.close()
    }
  })
})
