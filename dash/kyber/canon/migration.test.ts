import { mkdtempSync, rmSync } from 'node:fs'
import { createRequire } from 'node:module'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore, SCHEMA_VERSION } from './store.js'
import type { CanonicalRecord } from './types.js'

const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}

// A store's corpus is the expensive thing in this system — 20,445 records
// collected over months, some of it from harness sessions that no longer
// exist. So a schema bump migrates in place; it does not tell the operator to
// rebuild. These tests hold that line: the v1 rows must survive v2 intact.

/** The v1 schema, verbatim as it shipped — no `parts_json` column. */
const V1_RECORDS_SQL = `
CREATE TABLE records (
  span_id TEXT PRIMARY KEY,
  trace_id TEXT,
  parent_span_id TEXT,
  source TEXT NOT NULL,
  harness TEXT NOT NULL,
  name TEXT NOT NULL,
  op TEXT NOT NULL,
  kind TEXT NOT NULL,
  timestamp TEXT NOT NULL,
  duration_ms INTEGER NOT NULL,
  status TEXT NOT NULL,
  tokens_json TEXT NOT NULL,
  content_json TEXT NOT NULL,
  cost_json TEXT NOT NULL,
  measurability_json TEXT,
  raw BLOB
);
CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
`

const dirs: string[] = []

function v1StoreAt(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-migration-'))
  dirs.push(dir)
  const path = join(dir, 'canon.db')
  const db = new DatabaseSync(path)
  db.exec(V1_RECORDS_SQL)
  db.prepare('INSERT INTO metadata (key, value) VALUES (?, ?)').run('schema_version', '1')
  db.prepare(
    `INSERT INTO records (
      span_id, trace_id, parent_span_id, source, harness, name, op, kind,
      timestamp, duration_ms, status, tokens_json, content_json, cost_json,
      measurability_json, raw
    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
  ).run(
    'span-v1',
    'trace-v1',
    null,
    'antigravity',
    'gemini',
    'llm_request',
    'llm.invoke',
    'client',
    '2026-09-01T00:00:00.000Z',
    1200,
    'ok',
    JSON.stringify({
      freshInput: 10,
      cacheRead: 0,
      cacheCreation: 0,
      output: 5,
      reportedInput: 10,
      reportedOutput: 5,
    }),
    JSON.stringify({ system_prompt: 'carried over from v1' }),
    JSON.stringify({ basis: 'unknown', status: 'no_rate' }),
    null,
    null,
  )
  db.close()
  return path
}

afterEach(() => {
  while (dirs.length > 0) rmSync(dirs.pop()!, { recursive: true, force: true })
})

describe('schema migration v1 -> v2', () => {
  it('opens a v1 store and stamps it at the current version', () => {
    const store = new CanonStore(v1StoreAt())

    expect(store.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))
    store.close()
  })

  it('loses no v1 data', () => {
    const store = new CanonStore(v1StoreAt())
    const record = store.get('span-v1')

    expect(record?.content).toEqual({ system_prompt: 'carried over from v1' })
    expect(record?.tokens.reportedInput).toBe(10)
    expect(record?.harness).toBe('gemini')
    // v1 rows have no structured parts; absent is the honest answer, not [].
    expect(record?.parts).toBeUndefined()
    store.close()
  })

  it('is idempotent — reopening a migrated store is a no-op', () => {
    const path = v1StoreAt()
    new CanonStore(path).close()
    const store = new CanonStore(path)

    expect(store.count()).toBe(1)
    expect(store.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))
    store.close()
  })

  it('accepts parts written after the migration', () => {
    const path = v1StoreAt()
    const store = new CanonStore(path)
    const record: CanonicalRecord = {
      spanId: 'span-v2',
      traceId: 'trace-v1',
      parentSpanId: null,
      source: 'antigravity',
      harness: 'gemini',
      name: 'llm_request',
      op: 'llm.invoke',
      kind: 'client',
      timestamp: '2026-09-02T00:00:00.000Z',
      durationMs: 10,
      status: 'ok',
      tokens: {
        freshInput: 1,
        cacheRead: 0,
        cacheCreation: 0,
        output: 1,
        reportedInput: 1,
        reportedOutput: 1,
      },
      content: { tool_definitions: 'schema text' },
      parts: [{ part: 'tool_definitions', text: 'schema text', server: 'context7', tokens: 42 }],
      cost: { basis: 'unknown', status: 'no_rate' },
    }
    store.upsert(record)

    expect(store.get('span-v2')?.parts).toEqual([
      { part: 'tool_definitions', text: 'schema text', server: 'context7', tokens: 42 },
    ])
    store.close()
  })

  it('refuses a store from a newer build rather than misreading it', () => {
    const path = v1StoreAt()
    const db = new DatabaseSync(path)
    db.prepare('UPDATE metadata SET value = ? WHERE key = ?').run('99', 'schema_version')
    db.close()

    expect(() => new CanonStore(path)).toThrow(/upgrade KyberDash/)
  })
})

describe('R12.4 — content is stored once', () => {
  it('does not keep a second uncompressed copy alongside the parts', () => {
    // Storing both measured 166 MB of `content_json` against 40 MB for the
    // same text compressed as `parts_json` — a 4x store for one copy of the
    // data. Parts are the authority; content is derived on read.
    const path = v1StoreAt()
    const store = new CanonStore(path)
    const body = 'a system prompt long enough to matter '.repeat(500)
    store.upsert({
      spanId: 'span-big',
      traceId: null,
      parentSpanId: null,
      source: 'antigravity',
      harness: 'gemini',
      name: 'llm_request',
      op: 'llm.invoke',
      kind: 'client',
      timestamp: '2026-09-02T00:00:00.000Z',
      durationMs: 1,
      status: 'ok',
      tokens: {
        freshInput: 1,
        cacheRead: 0,
        cacheCreation: 0,
        output: 1,
        reportedInput: 1,
        reportedOutput: 1,
      },
      content: { system_prompt: body },
      parts: [{ part: 'system_prompt', text: body }],
      cost: { basis: 'unknown', status: 'no_rate' },
    })
    store.close()

    const db = new DatabaseSync(path)
    const row = db
      .prepare('SELECT content_json, length(parts_json) AS parts_bytes FROM records WHERE span_id = ?')
      .get('span-big') as { content_json: string; parts_bytes: number }
    db.close()

    expect(row.content_json).toBe('{}')
    expect(row.parts_bytes).toBeLessThan(body.length)

    // ...and the caller still reads the content back unchanged.
    const reopened = new CanonStore(path)
    expect(reopened.get('span-big')?.content).toEqual({ system_prompt: body })
    reopened.close()
  })

  it('keeps content_json authoritative when a source supplies no parts', () => {
    const store = new CanonStore(v1StoreAt())

    expect(store.get('span-v1')?.content).toEqual({ system_prompt: 'carried over from v1' })
    store.close()
  })
})
