import { describe, expect, it, expectTypeOf } from 'vitest'
import { DatabaseSync } from 'node:sqlite'
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { clearCursorWorkspaceMapCache, loadAgentStreams } from '../../providers/cursor.js'
import { openDatabase } from '../../ingest/sqlite.js'
import { cursorReader } from './cursor.js'
import type { ContentReader, ReaderTurn } from './types.js'

function fixturePath(name: string): string {
  return fileURLToPath(new URL(`../../refresh/fixtures/${name}`, import.meta.url))
}

describe('cursorReader', () => {
  it('is exposed through the public ContentReader contract', () => {
    expectTypeOf(cursorReader).toMatchTypeOf<ContentReader>()
  })

  it('reads cursor-partial-evidence.json fixture file directly', async () => {
    const filePath = fixturePath('cursor-partial-evidence.json')
    const turns: ReaderTurn[] = []
    for await (const turn of cursorReader.read(filePath)) {
      turns.push(turn)
    }

    expect(turns.length).toBeGreaterThan(0)
    const turn = turns.find((t) => t.nativeRecordId === 'cursor-request-1')
    expect(turn).toBeDefined()
    expect(turn!.sessionId).toBe('cursor-static-session')
    expect(turn!.contextWindow).toBe(128000)

    const instructionPart = turn!.parts.find((p) => p.part === 'instruction_context')
    expect(instructionPart).toBeDefined()
    expect(instructionPart!.text).toContain('Cursor tool context')

    const userPart = turn!.parts.find((p) => p.part === 'conversation_history')
    expect(userPart).toBeDefined()
    expect(userPart!.text).toContain('current Cursor request')

    const toolPart = turn!.parts.find((p) => p.part === 'tool_result_content')
    expect(toolPart).toBeDefined()
    expect(toolPart!.text).toContain('tool context that Cursor preserved')
  })

  it('reads cursor-virtual-turns.json fixture file directly', async () => {
    const filePath = fixturePath('cursor-virtual-turns.json')
    const turns: ReaderTurn[] = []
    for await (const turn of cursorReader.read(filePath)) {
      turns.push(turn)
    }

    expect(turns).toHaveLength(2)
    expect(turns[0]!.sessionId).toBe('cursor-virtual-1')
    expect(turns[0]!.nativeRecordId).toBe('cursor-turn-1')
    expect(turns[0]!.contextWindow).toBeUndefined()
    expect(turns[1]!.nativeRecordId).toBe('cursor-older')
  })

  it('reads SQLite state.vscdb with cursorDiskKV table', async () => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-cursor-reader-test-'))
    const dbPath = join(root, 'state.vscdb')
    const fixture = JSON.parse(readFileSync(fixturePath('cursor-partial-evidence.json'), 'utf8')) as {
      rows: Array<{ key: string; value: Record<string, unknown> }>
    }
    const db = new DatabaseSync(dbPath)
    try {
      db.exec('CREATE TABLE cursorDiskKV (key TEXT PRIMARY KEY, value TEXT NOT NULL)')
      const insert = db.prepare('INSERT INTO cursorDiskKV (key, value) VALUES (?, ?)')
      for (const row of fixture.rows) insert.run(row.key, JSON.stringify(row.value))
    } finally {
      db.close()
    }

    try {
      const turns: ReaderTurn[] = []
      for await (const turn of cursorReader.read(dbPath)) {
        turns.push(turn)
      }

      expect(turns).toHaveLength(1)
      const turn = turns[0]!
      expect(turn.sessionId).toBe('cursor-static-session')
      expect(turn.nativeRecordId).toBe('cursor-request-1')
      expect(turn.contextWindow).toBeUndefined()

      const instructionPart = turn.parts.find((p) => p.part === 'instruction_context')
      expect(instructionPart?.text).toBe('Cursor tool context retained for this request')

      const userPart = turn.parts.find((p) => p.part === 'conversation_history')
      expect(userPart?.text).toBe('current Cursor request')

      const toolPart = turn.parts.find((p) => p.part === 'tool_result_content')
      expect(toolPart?.text).toBe('tool context that Cursor preserved')
    } finally {
      rmSync(root, { recursive: true, force: true })
    }
  })

  it('filters SQLite reader by workspace and guarantees non-empty sessionId for unjoined requests', async () => {
    clearCursorWorkspaceMapCache()
    const userDir = mkdtempSync(join(tmpdir(), 'kyber-cursor-ws-reader-test-'))
    const globalDbDir = join(userDir, 'globalStorage')
    const wsStorageDir = join(userDir, 'workspaceStorage')
    mkdirSync(globalDbDir, { recursive: true })
    mkdirSync(wsStorageDir, { recursive: true })

    const dbPath = join(globalDbDir, 'state.vscdb')

    // Create ws1 mapping to comp-1 (folder: file:///test/proj1)
    const ws1Dir = join(wsStorageDir, 'ws1')
    mkdirSync(ws1Dir, { recursive: true })
    writeFileSync(join(ws1Dir, 'workspace.json'), JSON.stringify({ folder: 'file:///test/proj1' }))
    const ws1Db = new DatabaseSync(join(ws1Dir, 'state.vscdb'))
    ws1Db.exec('CREATE TABLE ItemTable (key TEXT UNIQUE, value BLOB)')
    ws1Db.prepare('INSERT INTO ItemTable (key, value) VALUES (?, ?)').run(
      'composer.composerData',
      JSON.stringify({ allComposers: [{ composerId: 'comp-1' }] }),
    )
    ws1Db.close()

    // Global DB with:
    // - comp-1 bubble & request 'req-1'
    // - comp-2 bubble & request 'req-2' (unmapped / orphan)
    // - unjoined agentKv request 'req-unjoined'
    const db = new DatabaseSync(dbPath)
    try {
      db.exec('CREATE TABLE cursorDiskKV (key TEXT PRIMARY KEY, value TEXT NOT NULL)')
      const insert = db.prepare('INSERT INTO cursorDiskKV (key, value) VALUES (?, ?)')

      insert.run(
        'bubbleId:comp-1:b1',
        JSON.stringify({
          type: 1,
          text: 'hello comp 1',
          requestId: 'req-1',
          createdAt: new Date().toISOString(),
        }),
      )

      insert.run(
        'bubbleId:comp-2:b2',
        JSON.stringify({
          type: 1,
          text: 'hello comp 2',
          requestId: 'req-2',
          createdAt: new Date().toISOString(),
        }),
      )

      insert.run(
        'agentKv:blob:req-1-user',
        JSON.stringify({
          role: 'user',
          content: 'user text 1',
          providerOptions: { cursor: { requestId: 'req-1' } },
        }),
      )

      insert.run(
        'agentKv:blob:req-2-user',
        JSON.stringify({
          role: 'user',
          content: 'user text 2',
          providerOptions: { cursor: { requestId: 'req-2' } },
        }),
      )

      insert.run(
        'agentKv:blob:req-unjoined-user',
        JSON.stringify({
          role: 'user',
          content: 'unjoined text',
          providerOptions: { cursor: { requestId: 'req-unjoined' } },
        }),
      )
    } finally {
      db.close()
    }

    try {
      // 1. Reading with workspace filter file:///test/proj1:
      // only req-1 with sessionId 'comp-1'.
      const ws1Turns: ReaderTurn[] = []
      for await (const turn of cursorReader.read(`${dbPath}#cursor-ws=file:///test/proj1`)) {
        ws1Turns.push(turn)
      }
      expect(ws1Turns).toHaveLength(1)
      expect(ws1Turns[0]!.nativeRecordId).toBe('req-1')
      expect(ws1Turns[0]!.sessionId).toBe('comp-1')

      // 2. Reading with orphan filter __orphan__:
      // returns comp-2 and req-unjoined.
      // req-unjoined MUST have sessionId 'req-unjoined'.
      clearCursorWorkspaceMapCache()
      const orphanTurns: ReaderTurn[] = []
      for await (const turn of cursorReader.read(`${dbPath}#cursor-ws=__orphan__`)) {
        orphanTurns.push(turn)
      }
      expect(orphanTurns.map((t) => t.nativeRecordId).sort()).toEqual(['req-2', 'req-unjoined'])
      const unjoinedTurn = orphanTurns.find((t) => t.nativeRecordId === 'req-unjoined')
      expect(unjoinedTurn).toBeDefined()
      expect(unjoinedTurn!.sessionId).toBe('req-unjoined')

      // 3. Reading with bare DB path (__all__):
      // returns all 3 requests, all with defined sessionId.
      clearCursorWorkspaceMapCache()
      const allTurns: ReaderTurn[] = []
      for await (const turn of cursorReader.read(dbPath)) {
        allTurns.push(turn)
      }
      expect(allTurns).toHaveLength(3)
      for (const turn of allTurns) {
        expect(turn.sessionId).toBeDefined()
        expect(typeof turn.sessionId).toBe('string')
        expect(turn.sessionId!.length).toBeGreaterThan(0)
      }
    } finally {
      clearCursorWorkspaceMapCache()
      rmSync(userDir, { recursive: true, force: true })
    }
  })

  it('safely decodes bubble text containing truncated invalid UTF-8 without aborting', async () => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-cursor-utf8-test-'))
    const dbPath = join(root, 'state.vscdb')
    const db = new DatabaseSync(dbPath)
    try {
      db.exec('CREATE TABLE cursorDiskKV (key TEXT PRIMARY KEY, value BLOB)')
      const insert = db.prepare('INSERT INTO cursorDiskKV (key, value) VALUES (?, ?)')

      // Craft a JSON buffer containing an invalid UTF-8 byte sequence in "text"
      const prefix = Buffer.from('{"type":1,"requestId":"req-bad-utf8","text":"hello ')
      const badByte = Buffer.from([0xff, 0xfe])
      const suffix = Buffer.from(' world"}')
      const value = Buffer.concat([prefix, badByte, suffix])

      insert.run('bubbleId:comp-bad:b1', value)
    } finally {
      db.close()
    }

    try {
      const turns: ReaderTurn[] = []
      for await (const turn of cursorReader.read(dbPath)) {
        turns.push(turn)
      }
      expect(turns).toHaveLength(1)
      expect(turns[0]!.nativeRecordId).toBe('req-bad-utf8')
      const historyPart = turns[0]!.parts.find((p) => p.part === 'conversation_history')
      expect(historyPart).toBeDefined()
      expect(historyPart!.text).toContain('hello')
      expect(historyPart!.text).toContain('world')
    } finally {
      rmSync(root, { recursive: true, force: true })
    }
  })

  it('does not cross context windows across different models in the same database', async () => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-cursor-multi-model-test-'))
    const dbPath = join(root, 'state.vscdb')
    const db = new DatabaseSync(dbPath)
    try {
      db.exec('CREATE TABLE cursorDiskKV (key TEXT PRIMARY KEY, value TEXT NOT NULL)')
      const insert = db.prepare('INSERT INTO cursorDiskKV (key, value) VALUES (?, ?)')

      // Claude bubble has explicit contextWindow 200000
      insert.run(
        'bubbleId:comp-multi:b1',
        JSON.stringify({
          type: 1,
          requestId: 'req-claude',
          text: 'claude query',
          modelInfo: { modelName: 'claude-3-5-sonnet' },
          contextWindow: 200000,
        }),
      )

      // GPT-4o bubble has no explicit contextWindow; a model name is not window evidence.
      insert.run(
        'bubbleId:comp-multi:b2',
        JSON.stringify({
          type: 1,
          requestId: 'req-gpt',
          text: 'gpt query',
          modelInfo: { modelName: 'gpt-4o' },
        }),
      )

      // Gemini bubble has no explicit contextWindow either.
      insert.run(
        'bubbleId:comp-multi:b3',
        JSON.stringify({
          type: 1,
          requestId: 'req-gemini',
          text: 'gemini query',
          modelInfo: { modelName: 'gemini-1.5-pro' },
        }),
      )

      // Another Claude request must not borrow the first request's window.
      insert.run(
        'bubbleId:comp-multi:b4',
        JSON.stringify({
          type: 1,
          requestId: 'req-claude-other',
          text: 'another claude query',
          modelInfo: { modelName: 'claude-3-5-sonnet' },
        }),
      )
    } finally {
      db.close()
    }

    try {
      const turns: ReaderTurn[] = []
      for await (const turn of cursorReader.read(dbPath)) {
        turns.push(turn)
      }
      expect(turns).toHaveLength(4)

      const claudeTurn = turns.find((t) => t.nativeRecordId === 'req-claude')
      expect(claudeTurn).toBeDefined()
      expect(claudeTurn!.contextWindow).toBe(200000)

      const gptTurn = turns.find((t) => t.nativeRecordId === 'req-gpt')
      expect(gptTurn).toBeDefined()
      expect(gptTurn!.contextWindow).toBeUndefined()

      const geminiTurn = turns.find((t) => t.nativeRecordId === 'req-gemini')
      expect(geminiTurn).toBeDefined()
      expect(geminiTurn!.contextWindow).toBeUndefined()

      const otherClaudeTurn = turns.find((t) => t.nativeRecordId === 'req-claude-other')
      expect(otherClaudeTurn?.contextWindow).toBeUndefined()
    } finally {
      rmSync(root, { recursive: true, force: true })
    }
  })
})

describe('Cursor agent stream retention', () => {
  it('keeps text only for an opted-in request bucket while retaining provider counts', () => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-cursor-stream-test-'))
    const dbPath = join(root, 'state.vscdb')
    const db = new DatabaseSync(dbPath)
    try {
      db.exec('CREATE TABLE cursorDiskKV (key TEXT PRIMARY KEY, value TEXT NOT NULL)')
      const insert = db.prepare('INSERT INTO cursorDiskKV (key, value) VALUES (?, ?)')
      insert.run('agentKv:blob:system', JSON.stringify({ role: 'system', content: 'instructions' }))
      insert.run('agentKv:blob:user', JSON.stringify({
        role: 'user',
        content: 'prompt',
        providerOptions: { cursor: { requestId: 'req-1' } },
      }))
      insert.run('agentKv:blob:tool', JSON.stringify({ role: 'tool', content: 'tool result' }))
      insert.run('agentKv:blob:assistant', JSON.stringify({
        role: 'assistant',
        content: [{ type: 'text', text: 'reply' }],
      }))
    } finally {
      db.close()
    }

    try {
      const readerDb = openDatabase(dbPath)
      try {
        const join = new Map([['req-1', 'comp-1']])
        const provider = loadAgentStreams(readerDb, join)
        const reader = loadAgentStreams(readerDb, join, { retainContent: true })

        expect(provider.byComposer.get('comp-1')).toMatchObject({
          userChars: 6,
          contextChars: 23,
          assistantChars: 5,
          userContent: [],
          instructionContent: [],
          toolResultContent: [],
        })
        expect(provider.byRequest.size).toBe(0)
        expect(reader.byComposer.get('comp-1')?.userContent).toEqual([])
        expect(reader.byRequest.get('req-1')).toMatchObject({
          userContent: ['prompt'],
          instructionContent: ['instructions'],
          toolResultContent: ['tool result'],
        })
      } finally {
        readerDb.close()
      }
    } finally {
      rmSync(root, { recursive: true, force: true })
    }
  })
})
