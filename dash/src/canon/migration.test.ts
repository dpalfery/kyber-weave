import { mkdtempSync, rmSync } from 'node:fs'
import { createRequire } from 'node:module'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore, MIGRATIONS, SCHEMA_VERSION, compressRaw } from './store.js'
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

/** Live schema 10, frozen here so v10→current cannot silently reuse SCHEMA_SQL. */
const V10_SCHEMA_SQL = `
CREATE TABLE IF NOT EXISTS records (
  span_id TEXT PRIMARY KEY,
  trace_id TEXT,
  parent_span_id TEXT,
  source TEXT NOT NULL,
  harness TEXT NOT NULL,
  session_id TEXT,
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
  parts_json BLOB,
  raw BLOB
);
CREATE INDEX IF NOT EXISTS records_by_trace ON records (trace_id);
CREATE INDEX IF NOT EXISTS records_by_timestamp ON records (timestamp);
CREATE INDEX IF NOT EXISTS records_by_session ON records (session_id);
CREATE INDEX IF NOT EXISTS records_by_session_key
  ON records (COALESCE(session_id, trace_id), timestamp);
CREATE TABLE IF NOT EXISTS session (
  session_id TEXT PRIMARY KEY,
  harness TEXT NOT NULL,
  label TEXT,
  is_subagent INTEGER NOT NULL DEFAULT 0,
  parent_session TEXT,
  agent_name TEXT,
  repo TEXT,
  branch TEXT,
  started TEXT,
  ended TEXT,
  payload TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS run (
  run_id TEXT PRIMARY KEY,
  harness TEXT NOT NULL,
  label TEXT,
  grouping_basis TEXT NOT NULL,
  grouping_rule TEXT,
  working_directory TEXT,
  started TEXT,
  ended TEXT,
  execution_count INTEGER NOT NULL DEFAULT 0,
  payload TEXT
);
CREATE TABLE IF NOT EXISTS execution (
  execution_id TEXT PRIMARY KEY,
  run_id TEXT NOT NULL,
  session_id TEXT,
  parent_execution_id TEXT,
  harness TEXT NOT NULL,
  agent_name TEXT,
  is_root INTEGER NOT NULL DEFAULT 0,
  started TEXT,
  ended TEXT,
  parent_linkage_json TEXT NOT NULL,
  payload TEXT
);
CREATE TABLE IF NOT EXISTS token_cache (
  hash TEXT PRIMARY KEY,
  count INTEGER NOT NULL,
  model TEXT
);
CREATE TABLE IF NOT EXISTS quarantine (
  span_id TEXT PRIMARY KEY,
  namespaces TEXT NOT NULL,
  reason TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS pending_logs (
  log_id TEXT PRIMARY KEY,
  payload TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS quarantined_logs (
  log_id TEXT PRIMARY KEY,
  payload TEXT NOT NULL,
  reason TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS enriched_logs (
  log_id TEXT PRIMARY KEY
);
CREATE TABLE IF NOT EXISTS problems (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  span_id TEXT,
  severity TEXT NOT NULL,
  code TEXT NOT NULL,
  message TEXT NOT NULL,
  location TEXT
);
CREATE TABLE IF NOT EXISTS ingest_log (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  source TEXT NOT NULL,
  count INTEGER NOT NULL,
  timestamp TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS metadata (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS harness_rollup (
  harness TEXT PRIMARY KEY,
  sample_count INTEGER NOT NULL DEFAULT 0,
  context_pressure_median REAL,
  context_pressure_p95 REAL,
  cache_hit_rate REAL,
  tool_yield REAL,
  delegation_overhead REAL,
  field_coverage REAL,
  measurability_json TEXT NOT NULL,
  payload TEXT
);
CREATE TABLE IF NOT EXISTS finding (
  id TEXT PRIMARY KEY,
  detector_id TEXT NOT NULL,
  title TEXT NOT NULL,
  mechanism TEXT NOT NULL,
  confidence TEXT NOT NULL,
  estimated_waste_tokens INTEGER NOT NULL DEFAULT 0,
  recommendation TEXT NOT NULL,
  error_bar_json TEXT NOT NULL,
  evidence_links_json TEXT NOT NULL,
  outcome_risk_caveat TEXT NOT NULL,
  run_id TEXT,
  session_id TEXT,
  rank_score REAL NOT NULL DEFAULT 0.0,
  payload TEXT
);
CREATE TABLE IF NOT EXISTS prediction (
  id TEXT PRIMARY KEY,
  finding_id TEXT NOT NULL,
  run_id TEXT NOT NULL,
  predicted_waste_tokens INTEGER NOT NULL,
  confidence REAL NOT NULL,
  confidence_tier TEXT,
  error_bar_json TEXT,
  created_at TEXT NOT NULL,
  comparison_run_id TEXT,
  observed_delta_tokens INTEGER,
  calibration_score REAL,
  payload TEXT
);
`

const RAW_V10 = { prompt: 'do not rewrite me', nested: [1, null, { ok: true }] }

function v10StoreAt(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-migration-v10-'))
  dirs.push(dir)
  const path = join(dir, 'canon.db')
  const db = new DatabaseSync(path)
  db.exec(V10_SCHEMA_SQL)
  db.prepare('INSERT INTO metadata (key, value) VALUES (?, ?)').run('schema_version', '10')
  db.prepare('INSERT INTO metadata (key, value) VALUES (?, ?)').run('detector_version', '1')
  db.prepare(
    `INSERT INTO records (
      span_id, trace_id, parent_span_id, source, harness, session_id, name, op, kind,
      timestamp, duration_ms, status, tokens_json, content_json, cost_json,
      measurability_json, parts_json, raw
    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
  ).run(
    'span-v10',
    'trace-v10',
    null,
    'pi:agent-7f3',
    'pi',
    'agent-7f3',
    'llm_request',
    'llm.invoke',
    'client',
    '2026-09-01T00:00:00.000Z',
    50,
    'ok',
    JSON.stringify({
      freshInput: 3,
      cacheRead: 0,
      cacheCreation: 0,
      output: 2,
      reportedInput: 3,
      reportedOutput: 2,
    }),
    JSON.stringify({ system_prompt: 'v10 content' }),
    JSON.stringify({ basis: 'unknown', status: 'no_rate' }),
    null,
    null,
    compressRaw(RAW_V10),
  )
  db.close()
  return path
}

function tableNames(path: string): string[] {
  const db = new DatabaseSync(path)
  const rows = db
    .prepare(`SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name`)
    .all() as { name: string }[]
  db.close()
  return rows.map((row) => row.name)
}

function rawBlob(path: string, spanId: string): Uint8Array {
  const db = new DatabaseSync(path)
  const row = db.prepare('SELECT raw FROM records WHERE span_id = ?').get(spanId) as { raw: Uint8Array }
  db.close()
  return row.raw
}

describe('schema migration v10 -> current (source checkpoint / provenance)', () => {
  it('bumps past live schema 10 through a dedicated step', () => {
    expect(SCHEMA_VERSION).toBeGreaterThan(10)
    expect(MIGRATIONS[10]).toBeTypeOf('function')
  })

  it('opens a v10 store, preserves the raw blob, and adds checkpoint tables', () => {
    const path = v10StoreAt()
    const before = Buffer.from(rawBlob(path, 'span-v10'))
    expect(tableNames(path)).not.toContain('source_checkpoint')
    expect(tableNames(path)).not.toContain('record_provenance')

    const store = new CanonStore(path)
    expect(store.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))
    expect(store.count()).toBe(1)
    expect(store.get('span-v10')?.raw).toEqual(RAW_V10)
    expect(store.get('span-v10')?.content).toEqual({ system_prompt: 'v10 content' })
    store.close()

    expect(Buffer.from(rawBlob(path, 'span-v10'))).toEqual(before)
    expect(tableNames(path)).toEqual(expect.arrayContaining(['source_checkpoint', 'record_provenance']))
  })

  it('is stable across reopen after the v10 migration', () => {
    const path = v10StoreAt()
    new CanonStore(path).close()
    const store = new CanonStore(path)
    expect(store.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))
    expect(store.get('span-v10')?.spanId).toBe('span-v10')
    expect(store.getSourceCheckpoint('pi', 'missing')).toBeUndefined()
    store.close()
  })
})

describe('schema migration v11 -> current (refresh run log)', () => {
  it('bumps past live schema 11 through a dedicated step', () => {
    expect(SCHEMA_VERSION).toBeGreaterThan(11)
    expect(MIGRATIONS[11]).toBeTypeOf('function')
  })

  it('adds refresh_run to a v11 store without disturbing its records', () => {
    // A v11 store is a v10 store the current code has already opened once, which is the
    // state every installed copy is in before this upgrade.
    const path = v10StoreAt()
    new CanonStore(path).close()
    const before = Buffer.from(rawBlob(path, 'span-v10'))

    const store = new CanonStore(path)
    expect(store.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))
    expect(store.count()).toBe(1)
    store.close()

    expect(Buffer.from(rawBlob(path, 'span-v10'))).toEqual(before)
    expect(tableNames(path)).toContain('refresh_run')
  })

  it('leaves the new table empty, so an upgrade never invents a refresh that did not happen', () => {
    const path = v10StoreAt()
    new CanonStore(path).close()
    const db = new DatabaseSync(path)
    const rows = db.prepare('SELECT COUNT(*) AS n FROM refresh_run').get() as { n: number }
    db.close()
    expect(rows.n).toBe(0)
  })

  it('is idempotent: reopening does not re-run the step or drop the table', () => {
    const path = v10StoreAt()
    new CanonStore(path).close()
    new CanonStore(path).close()
    const store = new CanonStore(path)
    expect(store.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))
    store.close()
    expect(tableNames(path)).toContain('refresh_run')
  })
})
