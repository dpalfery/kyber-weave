import { describe, expect, it, expectTypeOf } from 'vitest'
import { DatabaseSync } from 'node:sqlite'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

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
    expect(turns[0]!.contextWindow).toBe(128000)
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
      expect(turn.contextWindow).toBe(128000)

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
})
