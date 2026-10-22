import { describe, it, expect, beforeEach, afterEach, onTestFinished } from 'vitest'
import { mkdtemp, mkdir, writeFile, rm } from 'fs/promises'
import { createRequire } from 'node:module'
import { join } from 'path'
import { tmpdir } from 'os'

import { kiloCode, createKiloCodeProvider } from './kilo-code.js'
import type { ParsedProviderCall } from './types.js'

/// `node:sqlite` is loaded through `createRequire` rather than a static import so
/// the suite still loads on a Node build without it. The cast is the slice of
/// DatabaseSync these tests use.
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

let tmpDir: string

describe('kilo-code provider - discovery path differentiation', () => {
  beforeEach(async () => {
    tmpDir = await mkdtemp(join(tmpdir(), 'kilo-code-test-'))
  })

  afterEach(async () => {
    await rm(tmpDir, { recursive: true, force: true })
  })

  it('discovers tasks using kilo-code extension path', async () => {
    const task = join(tmpDir, 'tasks', 'task-kilo-1')
    await mkdir(task, { recursive: true })
    await writeFile(join(task, 'ui_messages.json'), JSON.stringify([
      { type: 'say', say: 'api_req_started', text: JSON.stringify({ tokensIn: 100, tokensOut: 50 }), ts: 1700000000000 },
    ]))

    const provider = createKiloCodeProvider(tmpDir)
    const sessions = await provider.discoverSessions()
    const fromOverride = sessions.filter(s => s.path.startsWith(tmpDir))

    expect(fromOverride).toHaveLength(1)
    expect(fromOverride[0]!.provider).toBe('kilo-code')
  })

  it('parses with kilo-code provider name in dedup key', async () => {
    const task = join(tmpDir, 'tasks', 'task-kilo-2')
    await mkdir(task, { recursive: true })
    await writeFile(join(task, 'ui_messages.json'), JSON.stringify([
      { type: 'say', say: 'api_req_started', text: JSON.stringify({ tokensIn: 200, tokensOut: 100 }), ts: 1700000000000 },
    ]))

    const source = { path: task, project: 'task-kilo-2', provider: 'kilo-code' }
    const calls: ParsedProviderCall[] = []
    for await (const call of kiloCode.createSessionParser(source, new Set()).parse()) calls.push(call)

    expect(calls).toHaveLength(1)
    expect(calls[0]!.provider).toBe('kilo-code')
    expect(calls[0]!.deduplicationKey).toMatch(/^kilo-code:/)
    // pairingId is an opt-in of the SQLite store: the legacy task tree has no
    // per-message id to pair a reader turn with, so it must not stamp one.
    expect(calls[0]!.pairingId).toBeUndefined()
  })
})

describe('kilo-code provider - metadata', () => {
  it('has correct name and displayName', () => {
    expect(kiloCode.name).toBe('kilo-code')
    expect(kiloCode.displayName).toBe('KiloCode')
  })

  it('uses different extension ID than roo-code', () => {
    expect(kiloCode.name).toBe('kilo-code')
    expect(kiloCode.name).not.toBe('roo-code')
  })
})

describe('kilo-code provider - shared-runtime sqlite store', () => {
  let prevXdg: string | undefined

  beforeEach(() => {
    prevXdg = process.env['XDG_DATA_HOME']
  })

  afterEach(() => {
    if (prevXdg === undefined) delete process.env['XDG_DATA_HOME']
    else process.env['XDG_DATA_HOME'] = prevXdg
  })

  it('discovers and parses sessions from kilo.db with flat tokens and markdown parts', async () => {
    const kiloDir = join(tmpDir, 'xdg', 'kilo')
    await mkdir(kiloDir, { recursive: true })
    process.env['XDG_DATA_HOME'] = join(tmpDir, 'xdg')

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

    const now = 1727500000000 // 2026-09-28
    db.prepare(`
      INSERT INTO session (id, project_id, parent_id, slug, directory, title, version, time_created, time_updated, time_archived)
      VALUES ('sess-kilo-live-1', 'proj-1', NULL, 'slug-1', '/Users/hal/myproject', 'Kilo session 1', '1.0', ?, ?, NULL)
    `).run(now, now)

    db.prepare(`
      INSERT INTO message (id, session_id, time_created, time_updated, data)
      VALUES ('msg-u1', 'sess-kilo-live-1', ?, ?, ?)
    `).run(now, now, JSON.stringify({ role: 'user' }))

    db.prepare(`
      INSERT INTO part (id, message_id, session_id, time_created, time_updated, data)
      VALUES ('part-u1', 'msg-u1', 'sess-kilo-live-1', ?, ?, ?)
    `).run(now, now, JSON.stringify({ type: 'markdown', text: 'Please inspect the bug' }))

    db.prepare(`
      INSERT INTO message (id, session_id, time_created, time_updated, data)
      VALUES ('msg-a1', 'sess-kilo-live-1', ?, ?, ?)
    `).run(now + 1000, now + 1000, JSON.stringify({
      role: 'assistant',
      modelID: 'claude-3-5-sonnet',
      tokens_input: 120,
      tokens_output: 60,
      tokens_reasoning: 15,
      tokens_cache_read: 25,
      tokens_cache_write: 5,
    }))

    db.prepare(`
      INSERT INTO part (id, message_id, session_id, time_created, time_updated, data)
      VALUES ('part-a1', 'msg-a1', 'sess-kilo-live-1', ?, ?, ?)
    `).run(now + 1000, now + 1000, JSON.stringify({ type: 'markdown', text: 'Found the root cause' }))

    db.close()

    const provider = createKiloCodeProvider()
    const sessions = await provider.discoverSessions()
    const sqliteSessions = sessions.filter(s => s.path.includes('kilo.db:'))
    expect(sqliteSessions).toHaveLength(1)
    expect(sqliteSessions[0]!.path).toBe(`${dbPath}:sess-kilo-live-1`)

    const parser = provider.createSessionParser(sqliteSessions[0]!, new Set())
    const calls: ParsedProviderCall[] = []
    for await (const call of parser.parse()) calls.push(call)

    expect(calls).toHaveLength(1)
    const call = calls[0]!
    expect(call.provider).toBe('kilo-code')
    expect(call.sessionId).toBe('sess-kilo-live-1')
    expect(call.model).toBe('claude-3-5-sonnet')
    expect(call.inputTokens).toBe(120)
    expect(call.outputTokens).toBe(60)
    expect(call.reasoningTokens).toBe(15)
    expect(call.cacheReadInputTokens).toBe(25)
    expect(call.cacheCreationInputTokens).toBe(5)
    expect(call.userMessage).toBe('Please inspect the bug')
  })

  // T2b — the reader and the parser see the same message list differently:
  // `buildAssistantCall` also yields a call for a zero-token assistant message
  // that still carries text, while the reader yields one turn per TOKEN-BEARING
  // message only. Index pairing would therefore attach each message's content to
  // its neighbour's call. `pairingId` is the message id both sides already know.
  it('stamps pairingId with the Kilo message id and leaves turnId unset', async () => {
    // A store of this test's own: the suite's other kilo.db fixture lives at a
    // path this test must not inherit, and a fixture that only passes when a
    // sibling ran first is not isolation.
    const root = await mkdtemp(join(tmpdir(), 'kilo-code-pairing-'))
    onTestFinished(async () => {
      await rm(root, { recursive: true, force: true })
    })
    const kiloDir = join(root, 'xdg', 'kilo')
    await mkdir(kiloDir, { recursive: true })
    process.env['XDG_DATA_HOME'] = join(root, 'xdg')

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
    const sessionId = 'sess-kilo-pairing'
    db.prepare(`
      INSERT INTO session (id, project_id, parent_id, slug, directory, title, version, time_created, time_updated, time_archived)
      VALUES (?, 'proj-1', NULL, 'slug-pairing', '/Users/hal/myproject', 'Kilo pairing', '1.4.2', ?, ?, NULL)
    `).run(sessionId, now, now)

    const message = (id: string, offsetMs: number, data: Record<string, unknown>) => {
      db.prepare(`INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES (?, ?, ?, ?, ?)`)
        .run(id, sessionId, now + offsetMs, now + offsetMs, JSON.stringify(data))
    }
    const part = (id: string, messageId: string, offsetMs: number, data: Record<string, unknown>) => {
      db.prepare(`INSERT INTO part (id, message_id, session_id, time_created, time_updated, data) VALUES (?, ?, ?, ?, ?, ?)`)
        .run(id, messageId, sessionId, now + offsetMs, now + offsetMs, JSON.stringify(data))
    }

    message('msg-u1', 0, { role: 'user' })
    part('part-u1', 'msg-u1', 0, { type: 'text', text: 'first question' })
    message('msg-a1', 1000, { role: 'assistant', modelID: 'claude-opus-4-6', tokens_input: 100, tokens_output: 20 })
    part('part-a1', 'msg-a1', 1000, { type: 'text', text: 'first answer' })
    // Zero token counters, but a text part: a call, not a reader turn.
    message('msg-router', 2000, { role: 'assistant', modelID: 'edenai/router-model' })
    part('part-router', 'msg-router', 2000, { type: 'text', text: 'router filler answer' })
    message('msg-a2', 3000, { role: 'assistant', modelID: 'claude-opus-4-6', tokens_input: 300, tokens_output: 60 })
    part('part-a2', 'msg-a2', 3000, { type: 'text', text: 'second answer' })
    db.close()

    const provider = createKiloCodeProvider()
    const source = { path: `${dbPath}:${sessionId}`, project: 'myproject', provider: 'kilo-code' }
    const calls: ParsedProviderCall[] = []
    for await (const call of provider.createSessionParser(source, new Set()).parse()) calls.push(call)

    expect(calls.map((call) => call.deduplicationKey)).toEqual([
      `kilo-code:${sessionId}:msg-a1`,
      `kilo-code:${sessionId}:msg-router`,
      `kilo-code:${sessionId}:msg-a2`,
    ])
    expect(
      calls.map((call) => call.pairingId),
      'pairingId must be the Kilo message id the reader stamps as nativeRecordId',
    ).toEqual(['msg-a1', 'msg-router', 'msg-a2'])
    // turnId is span identity. Reusing it as the pairing key would move the
    // span the record is keyed on, which is why pairing has its own field.
    expect(calls.map((call) => call.turnId)).toEqual([undefined, undefined, undefined])
  })
})
