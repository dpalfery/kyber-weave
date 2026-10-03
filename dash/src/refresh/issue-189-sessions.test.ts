// T1/T3 RED — issue #189: per-harness sessions must reach the canonical store.
//
// KiloCode (#227): refresh discovers 70 kilo-shared-runtime units against a
// live ~/.local/share/kilo/kilo.db yet zero `records` rows land. Codex (#196):
// 647 folder sessions are counted but only a handful of records arrive, with
// no codex-cli rows ever persisted. These fixture-backed tests pin the
// end-to-end contract with REAL providers (createCodexProvider /
// createKiloCodeProvider) against a temp store — never ~/.kyberdash/canon.db.

import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createRequire } from 'node:module'

import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { createCodexProvider } from '../providers/codex.js'
import { createKiloCodeProvider } from '../providers/kilo-code.js'

import { refreshHarnessSources } from './orchestrator.js'
import { descriptorFor } from './registry.js'
import { COMMAND_STARTED_AT } from './fixtures/integration-harness.js'

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
  root = mkdtempSync(join(tmpdir(), 'issue-189-'))
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

// T1 — RED: a shared-runtime kilo.db must yield canonical records.
describe('issue #189 T1 (kilo-code)', () => {
  it('persists kilo-shared-runtime sessions into records', async () => {
    const kiloDir = join(root, 'xdg', 'kilo')
    mkdirSync(kiloDir, { recursive: true })
    const dbPath = join(kiloDir, 'kilo.db')
    const db = openSqlite(dbPath)
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
      CREATE TABLE part (
        id TEXT PRIMARY KEY, message_id TEXT NOT NULL,
        session_id TEXT NOT NULL, time_created INTEGER,
        time_updated INTEGER, data TEXT NOT NULL
      );
    `)
    const now = Date.parse('2026-09-10T10:00:00.000Z')
    db.prepare(`INSERT INTO session (id, project_id, parent_id, slug, directory, title, version, time_created, time_updated, time_archived)
                VALUES ('sess-kilo-1', 'proj-1', NULL, 'slug-1', '/home/user/myproject', 'Kilo session', '1.0', ?, ?, NULL)`).run(now, now)
    db.prepare(`INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ('msg-1', 'sess-kilo-1', ?, ?, ?)`)
      .run(now, now, JSON.stringify({ role: 'user' }))
    db.prepare(`INSERT INTO part (id, message_id, session_id, time_created, time_updated, data) VALUES ('part-1', 'msg-1', 'sess-kilo-1', ?, ?, ?)`)
      .run(now, now, JSON.stringify({ type: 'text', text: 'hello kilo' }))
    db.prepare(`INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ('msg-2', 'sess-kilo-1', ?, ?, ?)`)
      .run(now + 1000, now + 1000, JSON.stringify({ role: 'assistant', modelID: 'claude-opus-4-6', cost: 0.01, tokens: { input: 100, output: 50, reasoning: 0, cache: { read: 0, write: 0 } } }))
    db.prepare(`INSERT INTO part (id, message_id, session_id, time_created, time_updated, data) VALUES ('part-2', 'msg-2', 'sess-kilo-1', ?, ?, ?)`)
      .run(now + 1000, now + 1000, JSON.stringify({ type: 'text', text: 'hi there' }))
    db.close()

    const store = new CanonStore(join(root, 'canon.db'))
    try {
      await refreshHarnessSources(
        store,
        {
          getAllProviders: async () => [createKiloCodeProvider()],
          descriptors: descriptors('kilo-shared-runtime', 'kilo-vscode-legacy'),
          jobConcurrency: 1,
          writerCapacity: 1,
          commandStartedAt: COMMAND_STARTED_AT,
          parseAllSessions: async () => undefined,
        },
        { historyWeeks: 5200 },
      )

      const kiloRecords = store
        .listAll()
        .filter((record) => String(record.source).includes('kilo') || String(record.harness).includes('kilo'))
      expect(kiloRecords.length).toBeGreaterThan(0)
    } finally {
      store.close()
    }
  })
})

// T1b — RED: a divergent kilo.db shape must surface a record or a named reason.
//
// Hypothesis grounded in the code: `buildAssistantCall`
// (dash/src/providers/session-message.ts) only recognises token keys under
// `tokens` / `usage`, returns null when the turn has no cost and no
// substantive part, and `tryQuerySessionTokens` needs `cost`/`tokens_*`
// columns on `session`. A shared-runtime DB whose assistant `message.data`
// carries flat `tokens_input`-style keys with part types outside the
// substantive list yields exactly 0 records, silently. The live 104 MB
// kilo.db behaved this way (70 units, 69 with 0 records, none named why).
describe('issue #189 T1b (kilo-code divergent shape)', () => {
  it('either persists kilo records or records a named no-data reason', async () => {
    const kiloDir = join(root, 'xdg', 'kilo')
    mkdirSync(kiloDir, { recursive: true })
    const dbPath = join(kiloDir, 'kilo.db')
    const db = openSqlite(dbPath)
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
      CREATE TABLE part (
        id TEXT PRIMARY KEY, message_id TEXT NOT NULL,
        session_id TEXT NOT NULL, time_created INTEGER,
        time_updated INTEGER, data TEXT NOT NULL
      );
    `)
    const now = Date.parse('2026-09-10T10:00:00.000Z')
    db.prepare(`INSERT INTO session (id, project_id, parent_id, slug, directory, title, version, time_created, time_updated, time_archived)
                VALUES ('sess-kilo-2', 'proj-1', NULL, 'slug-2', '/home/user/myproject', 'Kilo session', '1.0', ?, ?, NULL)`).run(now, now)
    db.prepare(`INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ('msg-1', 'sess-kilo-2', ?, ?, ?)`)
      .run(now, now, JSON.stringify({ role: 'user' }))
    db.prepare(`INSERT INTO part (id, message_id, session_id, time_created, time_updated, data) VALUES ('part-1', 'msg-1', 'sess-kilo-2', ?, ?, ?)`)
      .run(now, now, JSON.stringify({ type: 'markdown', text: 'do a thing' }))
    // Divergent: flat token keys, no cost, no recoverable session-level totals.
    db.prepare(`INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ('msg-2', 'sess-kilo-2', ?, ?, ?)`)
      .run(now + 1000, now + 1000, JSON.stringify({
        role: 'assistant',
        modelID: 'claude-opus-4-6',
        tokens_input: 100,
        tokens_output: 50,
      }))
    db.prepare(`INSERT INTO part (id, message_id, session_id, time_created, time_updated, data) VALUES ('part-2', 'msg-2', 'sess-kilo-2', ?, ?, ?)`)
      .run(now + 1000, now + 1000, JSON.stringify({ type: 'markdown', text: 'answer' }))
    db.close()

    const store = new CanonStore(join(root, 'canon-t1b.db'))
    try {
      await refreshHarnessSources(
        store,
        {
          getAllProviders: async () => [createKiloCodeProvider()],
          descriptors: descriptors('kilo-shared-runtime', 'kilo-vscode-legacy'),
          jobConcurrency: 1,
          writerCapacity: 1,
          commandStartedAt: COMMAND_STARTED_AT,
          parseAllSessions: async () => undefined,
        },
        { historyWeeks: 5200 },
      )

      const kiloRecords = store
        .listAll()
        .filter((record) => String(record.source).includes('kilo') || String(record.harness).includes('kilo'))
      const kiloCheckpoints = store
        .listSourceCheckpoints()
        .filter((checkpoint) => checkpoint.harnessId.includes('kilo'))
      const anyNamedReason = kiloCheckpoints.some(
        (checkpoint) => checkpoint.recordCount > 0 || (checkpoint.lastErrorCode !== null && checkpoint.lastErrorCode !== ''),
      )
      expect(
        kiloRecords.length > 0 || anyNamedReason,
        `silent zero: ${kiloCheckpoints.length} kilo unit(s), 0 records, no reason recorded`,
      ).toBe(true)
    } finally {
      store.close()
    }
  })
})

// T3b — RED: a rollout from a third-party frontend named in codex.ts's own
// comment ('codex-tui') must not vanish. classifyCodex in registry.ts only
// maps 'codex-cli'/'codex_cli_rs' to cli and 'codex desktop' to desktop;
// everything else falls to codex-unclassified, which is still a record —
// but if the envelope path drops it, nothing lands at all.
// T3b — variant 1 result: PASSES on current code. A rollout with
// originator 'codex-tui' lands under harness 'codex-unclassified' — the
// session is NOT dropped, and the harness id is observable. Hypothesis
// "third-party originators are silently dropped" is wrong; this test pins
// the observed behaviour. See T3b' for the remaining RED variant.
describe('issue #189 T3b (codex-tui originator)', () => {
  it('lands a record under some codex harness rather than dropping the session', async () => {
    const codexHome = join(root, '.codex')
    const dayDir = join(codexHome, 'sessions', '2026', '09', '10')
    mkdirSync(dayDir, { recursive: true })
    writeFileSync(
      join(dayDir, 'rollout-tui-1.jsonl'),
      [
        JSON.stringify({
          type: 'session_meta',
          timestamp: '2026-09-10T10:00:00Z',
          payload: {
            cwd: '/Users/test/myproject',
            originator: 'codex-tui',
            session_id: 'sess-tui-1',
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

    const store = new CanonStore(join(root, 'canon-t3b.db'))
    try {
      await refreshHarnessSources(
        store,
        {
          getAllProviders: async () => [createCodexProvider(codexHome)],
          descriptors: descriptors('codex-cli', 'codex-desktop', 'codex-unclassified'),
          jobConcurrency: 1,
          writerCapacity: 1,
          commandStartedAt: COMMAND_STARTED_AT,
          parseAllSessions: async () => undefined,
        },
        { historyWeeks: 5200 },
      )

      const codexRecords = store
        .listAll()
        .filter((record) => String(record.harness).startsWith('codex'))
      expect(codexRecords.length).toBeGreaterThan(0)
    } finally {
      store.close()
    }
  })
})

// T3b' — RED variant 2: originator 'codex-tui' plus token_count usage under
// unrecognised keys. If nothing lands and no reason is recorded, that is the
// same silent-zero class as T1b, attributed to Codex.
describe('issue #189 T3b (codex divergent token shape)', () => {
  it('either persists codex records or records a named no-data reason', async () => {
    const codexHome = join(root, '.codex')
    const dayDir = join(codexHome, 'sessions', '2026', '09', '10')
    mkdirSync(dayDir, { recursive: true })
    writeFileSync(
      join(dayDir, 'rollout-camel-1.jsonl'),
      [
        JSON.stringify({
          type: 'session_meta',
          timestamp: '2026-09-10T10:00:00Z',
          payload: {
            cwd: '/Users/test/myproject',
            originator: 'codex-tui',
            session_id: 'sess-camel-1',
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
              lastTokenUsage: { inputTokens: 100, outputTokens: 50 },
              totalTokenUsage: { inputTokens: 100, outputTokens: 50 },
            },
          },
        }),
      ].join('\n') + '\n',
    )

    const store = new CanonStore(join(root, 'canon-t3b2.db'))
    try {
      await refreshHarnessSources(
        store,
        {
          getAllProviders: async () => [createCodexProvider(codexHome)],
          descriptors: descriptors('codex-cli', 'codex-desktop', 'codex-unclassified'),
          jobConcurrency: 1,
          writerCapacity: 1,
          commandStartedAt: COMMAND_STARTED_AT,
          parseAllSessions: async () => undefined,
        },
        { historyWeeks: 5200 },
      )

      const codexRecords = store
        .listAll()
        .filter((record) => String(record.harness).startsWith('codex'))
      const codexCheckpoints = store
        .listSourceCheckpoints()
        .filter((checkpoint) => checkpoint.harnessId.startsWith('codex'))
      const anyNamedReason = codexCheckpoints.some(
        (checkpoint) => checkpoint.recordCount > 0 || (checkpoint.lastErrorCode !== null && checkpoint.lastErrorCode !== ''),
      )
      expect(
        codexRecords.length > 0 || anyNamedReason,
        `silent zero: ${codexCheckpoints.length} codex unit(s), 0 records, no reason recorded`,
      ).toBe(true)
    } finally {
      store.close()
    }
  })
})

// T3 — RED: Codex CLI sessions must produce codex-cli records, not just duplicates under one desktop harness.
describe('issue #189 T3 (codex)', () => {
  it('persists codex-cli harness records from session_meta originator', async () => {
    const codexHome = join(root, '.codex')
    const dayDir = join(codexHome, 'sessions', '2026', '09', '10')
    mkdirSync(dayDir, { recursive: true })
    writeFileSync(
      join(dayDir, 'rollout-cli-1.jsonl'),
      [
        JSON.stringify({
          type: 'session_meta',
          timestamp: '2026-09-10T10:00:00Z',
          payload: {
            cwd: '/Users/test/myproject',
            originator: 'codex_cli_rs',
            session_id: 'sess-cli-1',
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

    const store = new CanonStore(join(root, 'canon.db'))
    try {
      await refreshHarnessSources(
        store,
        {
          getAllProviders: async () => [createCodexProvider(codexHome)],
          descriptors: descriptors('codex-cli', 'codex-desktop', 'codex-unclassified'),
          jobConcurrency: 1,
          writerCapacity: 1,
          commandStartedAt: COMMAND_STARTED_AT,
          parseAllSessions: async () => undefined,
        },
        { historyWeeks: 5200 },
      )

      const codexCli = store.listAll().filter((record) => record.harness === 'codex-cli')
      expect(codexCli.length).toBeGreaterThan(0)
    } finally {
      store.close()
    }
  })
})
