import { createRequire } from 'node:module'
import { mkdir, mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'

import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { createGooseProvider } from '../../src/providers/goose.js'
import { isSqliteAvailable } from '../../src/ingest/sqlite.js'

const requireForTest = createRequire(import.meta.url)

type TestDb = {
  exec(sql: string): void
  prepare(sql: string): { run(...params: unknown[]): void }
  close(): void
}

let root: string
const originalRoot = process.env.GOOSE_PATH_ROOT

beforeEach(async () => {
  root = await mkdtemp(join(tmpdir(), 'codeburn-goose-sync-'))
  process.env.GOOSE_PATH_ROOT = root
})

afterEach(async () => {
  if (originalRoot === undefined) delete process.env.GOOSE_PATH_ROOT
  else process.env.GOOSE_PATH_ROOT = originalRoot
  await rm(root, { recursive: true, force: true })
})

const sqliteDescribe = isSqliteAvailable() ? describe : describe.skip

sqliteDescribe('Goose project provenance', () => {
  it('carries the exact working_dir through the parser', async () => {
    const dbPath = join(root, 'data', 'sessions', 'sessions.db')
    await mkdir(dirname(dbPath), { recursive: true })
    const { DatabaseSync: Database } = requireForTest('node:sqlite') as {
      DatabaseSync: new (path: string) => TestDb
    }
    const db = new Database(dbPath)
    db.exec(`
      CREATE TABLE sessions (
        id TEXT PRIMARY KEY,
        name TEXT,
        working_dir TEXT,
        created_at TEXT,
        updated_at TEXT,
        accumulated_input_tokens INTEGER,
        accumulated_output_tokens INTEGER,
        provider_name TEXT,
        model_config_json TEXT
      );
      CREATE TABLE messages (
        session_id TEXT,
        message_id TEXT,
        role TEXT,
        content_json TEXT,
        created_timestamp INTEGER
      );
    `)
    const insertSession = db.prepare(`
      INSERT INTO sessions (
        id, name, working_dir, created_at, updated_at,
        accumulated_input_tokens, accumulated_output_tokens,
        provider_name, model_config_json
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
    `)
    insertSession.run(
      'goose-session-1',
      'LLM-authored session title',
      '/Users/alice/company/private-widget',
      '2026-08-23T10:00:00.000Z',
      '2026-08-23T10:01:00.000Z',
      100,
      20,
      'openai',
      JSON.stringify({ model_name: 'gpt-5.4' }),
    )
    insertSession.run(
      'goose-container-session',
      'Container session title must stay local',
      '/sessions/synthetic-customer-secret',
      '2026-08-23T11:00:00.000Z',
      '2026-08-23T11:01:00.000Z',
      50,
      10,
      'openai',
      JSON.stringify({ model_name: 'gpt-5.4' }),
    )
    db.close()

    const provider = createGooseProvider()
    const sources = await provider.discoverSessions()
    expect(sources).toHaveLength(2)
    const calls = []
    for (const source of sources) {
      for await (const providerCall of provider.createSessionParser(source, new Set()).parse()) calls.push(providerCall)
    }
    expect(calls).toHaveLength(2)
    const trusted = calls.find(call => call.sessionId === 'goose-session-1')!
    const container = calls.find(call => call.sessionId === 'goose-container-session')!
    expect(trusted.workingDirectory).toBe('/Users/alice/company/private-widget')
    expect(container.workingDirectory).toBe('/sessions/synthetic-customer-secret')

  })
})
