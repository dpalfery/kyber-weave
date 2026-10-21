import { afterAll, describe, expect, it } from 'vitest'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'

import { CanonStore, decompressRaw } from './store.js'
import { CONTENT_RETENTION_DAYS, contentRetentionFloorMs, purgeExpiredContent } from './retention.js'
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
    expect(contentRetentionFloorMs(now)).toBe(now.getTime() - CONTENT_RETENTION_DAYS * 24 * 60 * 60 * 1000)

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

describe('purgeExpiredContent runs in one transaction', () => {
  it('issues a single BEGIN/COMMIT pair with every content UPDATE inside it', () => {
    // WHY: the purge walked `spanIds` and called `setContent` per expired row -
    // one autocommit UPDATE per row (9,740 expired ids measured in production).
    // The fix is structural, not a duration: the statements must run inside one
    // transaction. A stopwatch would pin the machine, not the behaviour; the
    // statement stream pins the behaviour on any machine.
    // RED today: the purge issues no transaction statements at all, so the
    // single-BEGIN/COMMIT assertion fails.
    const store = new CanonStore(':memory:')
    try {
      store.upsertMany([
        record({ spanId: 'txn-fresh', timestamp: '2026-09-10T12:00:00.000Z', content: { system_prompt: 'fresh' } }),
        record({ spanId: 'txn-stale-1', timestamp: '2026-08-20T12:00:00.000Z', content: { system_prompt: 'one' } }),
        record({ spanId: 'txn-stale-2', timestamp: '2026-08-20T12:00:00.000Z', content: { system_prompt: 'two' } }),
        record({
          spanId: 'txn-stale-3',
          timestamp: '2026-08-20T12:00:00.000Z',
          content: { system_prompt: 'three' },
          parts: [{ part: 'system_prompt', text: 'three', order: 0 }],
        }),
      ])

      // The raw handle, as `projection.test.ts` reaches it: the store owns no
      // transaction API of its own, so the BEGIN/COMMIT must be visible here.
      const db = (store as unknown as { db: DatabaseSync }).db
      const events: Array<{ kind: 'exec' | 'prepare'; sql: string }> = []
      const originalPrepare = db.prepare.bind(db)
      const originalExec = db.exec.bind(db)
      db.prepare = ((sql: string) => {
        events.push({ kind: 'prepare', sql })
        return originalPrepare(sql)
      }) as typeof db.prepare
      db.exec = ((sql: string) => {
        events.push({ kind: 'exec', sql })
        return originalExec(sql)
      }) as typeof db.exec
      try {
        const result = purgeExpiredContent(store, new Date('2026-09-12T12:00:00.000Z'))
        expect(result.purged).toBe(3)
      } finally {
        db.prepare = originalPrepare
        db.exec = originalExec
      }

      const isBegin = (sql: string): boolean => sql.trim().toUpperCase() === 'BEGIN'
      const isCommit = (sql: string): boolean => sql.trim().toUpperCase() === 'COMMIT'
      const isRollback = (sql: string): boolean => sql.trim().toUpperCase() === 'ROLLBACK'
      const execs = events.filter((event) => event.kind === 'exec')
      expect(execs.filter((event) => isBegin(event.sql))).toHaveLength(1)
      expect(execs.filter((event) => isCommit(event.sql))).toHaveLength(1)
      expect(execs.filter((event) => isRollback(event.sql))).toHaveLength(0)

      const beginAt = events.findIndex((event) => event.kind === 'exec' && isBegin(event.sql))
      const commitAt = events.findIndex((event) => event.kind === 'exec' && isCommit(event.sql))
      expect(beginAt).toBeGreaterThanOrEqual(0)
      expect(commitAt).toBeGreaterThan(beginAt)
      const updates = events.filter((event) => /^UPDATE\s+records\b/i.test(event.sql))
      // Three expired rows collapse into the set-based statement(s); whatever
      // their number, none may run as an autocommit write outside the pair.
      expect(updates.length).toBeGreaterThan(0)
      for (const update of updates) {
        const at = events.indexOf(update)
        expect(at).toBeGreaterThan(beginAt)
        expect(at).toBeLessThan(commitAt)
      }
    } finally {
      store.close()
    }
  })
})
