import { describe, it, expect, beforeEach, afterEach } from 'vitest'
import { mkdtemp, mkdir, writeFile, rm } from 'fs/promises'
import { join } from 'path'
import { tmpdir } from 'os'

import { kiloCode, createKiloCodeProvider } from './kilo-code.js'
import type { ParsedProviderCall } from './types.js'

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
    const { createRequire } = await import('node:module')
    const req = createRequire(import.meta.url)
    const { DatabaseSync } = req('node:sqlite') as {
      DatabaseSync: new (path: string) => {
        exec(sql: string): void
        prepare(sql: string): { run(...params: unknown[]): void }
        close(): void
      }
    }
    const db = new DatabaseSync(dbPath)
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
})
