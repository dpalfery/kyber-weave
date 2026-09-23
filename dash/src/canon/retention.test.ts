import { afterAll, describe, expect, it } from 'vitest'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'

import { CanonStore, decompressRaw } from './store.js'
import { CONTENT_RETENTION_DAYS, purgeExpiredContent } from './retention.js'
import type { CanonicalRecord, TokenUsage } from './types.js'

const tempRoots: string[] = []

function tempStorePath(): string {
  const root = mkdtempSync(join(tmpdir(), 'kyber-canon-retention-'))
  tempRoots.push(root)
  return join(root, 'canon.db')
}

afterAll(() => {
  for (const root of tempRoots) rmSync(root, { recursive: true, force: true })
})

function usage(): TokenUsage {
  return {
    freshInput: 10,
    cacheRead: 0,
    cacheCreation: 0,
    output: 4,
    reportedInput: 10,
    reportedOutput: 4,
  }
}

function record(overrides: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return {
    spanId: 'span-1',
    traceId: 'trace-1',
    parentSpanId: null,
    source: 'pi:agent-7f3',
    harness: 'pi',
    name: 'chat turn',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: '2026-09-12T00:00:00.000Z',
    durationMs: 100,
    status: 'ok',
    tokens: usage(),
    content: { system_prompt: 'You are a coding agent.' },
    cost: { basis: 'published', status: 'priced', value: 0.01, currency: 'USD' },
    raw: { attributes: { 'gen_ai.prompt': 'keep me' } },
    ...overrides,
  }
}

type RecordColumns = {
  span_id: string
  content_json: string | null
  parts_json: Uint8Array | null
  raw: Uint8Array | null
}

function loadColumns(path: string): Map<string, RecordColumns> {
  const db = new DatabaseSync(path)
  try {
    const rows = db
      .prepare('SELECT span_id, content_json, parts_json, raw FROM records')
      .all() as RecordColumns[]
    return new Map(rows.map((row) => [row.span_id, row]))
  } finally {
    db.close()
  }
}

describe('purgeExpiredContent', () => {
  it('keeps content newer than 14 days, purges older content_json, and leaves records.raw', () => {
    expect(CONTENT_RETENTION_DAYS).toBe(14)

    const path = tempStorePath()
    const store = new CanonStore(path)
    const now = new Date('2026-09-12T12:00:00.000Z')

    store.upsertMany([
      record({
        spanId: 'fresh-span',
        timestamp: '2026-09-10T12:00:00.000Z',
        content: { system_prompt: 'fresh prompt' },
        raw: { keep: 'fresh-raw' },
      }),
      record({
        spanId: 'stale-span',
        timestamp: '2026-08-20T12:00:00.000Z',
        content: { system_prompt: 'stale prompt' },
        raw: { keep: 'stale-raw' },
      }),
      record({
        spanId: 'stale-parts',
        timestamp: '2026-08-20T12:00:00.000Z',
        content: { system_prompt: 'stale parts' },
        parts: [{ part: 'system_prompt', text: 'stale parts', order: 0 }],
        raw: { keep: 'stale-parts-raw' },
      }),
    ])

    const result = purgeExpiredContent(store, now)
    expect(result.purged).toBe(2)
    store.close()

    const columns = loadColumns(path)
    const fresh = columns.get('fresh-span')
    const stale = columns.get('stale-span')
    const staleParts = columns.get('stale-parts')
    expect(fresh).toBeDefined()
    expect(stale).toBeDefined()
    expect(staleParts).toBeDefined()

    expect(fresh!.content_json).toBe(JSON.stringify({ system_prompt: 'fresh prompt' }))
    expect(fresh!.raw).not.toBeNull()
    expect(decompressRaw(fresh!.raw as Uint8Array)).toEqual({ keep: 'fresh-raw' })

    expect(stale!.content_json).toBe('{}')
    expect(stale!.parts_json).toBeNull()
    expect(stale!.raw).not.toBeNull()
    expect(decompressRaw(stale!.raw as Uint8Array)).toEqual({ keep: 'stale-raw' })

    expect(staleParts!.content_json).toBe('{}')
    expect(staleParts!.parts_json).toBeNull()
    expect(staleParts!.raw).not.toBeNull()
    expect(decompressRaw(staleParts!.raw as Uint8Array)).toEqual({ keep: 'stale-parts-raw' })
  })
})
