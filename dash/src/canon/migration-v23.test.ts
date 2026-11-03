// The v22 -> v23 migration: the metric point's interval start.
//
// A metric point's identity is its content digest, and the digest did not carry
// the interval the point's measurement covers. Two points that differed ONLY in
// that interval -- the same name, kind, unit, temporality, observation time,
// value and attributes, over a different window -- so collided onto one
// point_key, and the second upserted onto the first row.
//
// The interval therefore becomes part of the point's identity: it travels in the
// digest, and it lands on the row.
//
// A COLUMN cannot be added by the store's schema: every statement in
// SCHEMA_SQL is IF NOT EXISTS, so a store that already carries the metric_point
// table never gains the column from the CREATE TABLE. The column is what the
// migration is for, and the migration is what moves the schema version. This
// file proves the step: a genuine v22 store -- the table as v22 shipped it,
// without the column -- gains the column and the new stamp when it is opened.

import { mkdtempSync, rmSync } from 'node:fs'
import { createRequire } from 'node:module'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore, MIGRATIONS, SCHEMA_VERSION } from './store.js'
import type { OtlpMetric } from '../otel/receiver.js'

const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}

interface ColumnInfo {
  cid: number
  name: string
  type: string
  notnull: number
  dflt_value: string | null
  pk: number
}

const dirs: string[] = []

afterEach(() => {
  while (dirs.length > 0) rmSync(dirs.pop()!, { recursive: true, force: true })
})

/**
 * The v22 metric point table DDL, frozen as it ships -- the table without the
 * interval column.
 */
const V22_METRIC_POINT_TABLE_SQL = `
CREATE TABLE metric_point (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  point_key TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL,
  kind TEXT NOT NULL,
  unit TEXT,
  aggregation_temporality INTEGER,
  time_unix_nano TEXT,
  timestamp TEXT,
  value REAL,
  point_count INTEGER,
  point_sum REAL,
  attributes_json TEXT NOT NULL,
  resource_json TEXT NOT NULL,
  scope_json TEXT NOT NULL,
  source TEXT
);
CREATE INDEX IF NOT EXISTS metric_point_by_name ON metric_point (name);
`

/** The interval the point of the migration case covers. */
const FIRST_INTERVAL_START_NANOS = '1756478300000000000'

/** The point's observation time: the same for every point of the case. */
const POINT_TIME_NANOS = '1756478400123456789'

function tableInfo(path: string, table: string): ColumnInfo[] {
  const db = new DatabaseSync(path)
  try {
    return db.prepare(`PRAGMA table_info(${table})`).all() as unknown as ColumnInfo[]
  } finally {
    db.close()
  }
}

/**
 * A store at schema version 22: the metric point table as v22 shipped it,
 * without the interval column, and the stamp wound back to 22.
 *
 * The current build stamps 23, so the table it created is dropped and
 * recreated in the v22 shape -- the state every installed copy holds before
 * this upgrade.
 */
function v22StoreAt(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-migration-v22-'))
  dirs.push(dir)
  const path = join(dir, 'canon.db')

  new CanonStore(path).close()

  const db = new DatabaseSync(path)
  try {
    db.exec('DROP TABLE IF EXISTS metric_point;')
    db.exec(V22_METRIC_POINT_TABLE_SQL)
    db.prepare('UPDATE metadata SET value = ? WHERE key = ?').run('22', 'schema_version')
    // A point the corpus already held: the migration adds a column to the
    // table, and the rows that were there before it keep their values.
    db.prepare(
      `INSERT INTO metric_point (
        point_key, name, kind, unit, aggregation_temporality, time_unix_nano, timestamp,
        value, point_count, point_sum, attributes_json, resource_json, scope_json, source
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    ).run(
      'v22-point-key',
      'droid.code.files_modified',
      'gauge',
      null,
      null,
      POINT_TIME_NANOS,
      '2026-08-29T00:00:00.123Z',
      4200,
      null,
      null,
      '{}',
      '{"service.name":"droid"}',
      '{}',
      'droid',
    )
  } finally {
    db.close()
  }

  return path
}

/** One metric carrying one point, over the given interval. */
function metricOver(intervalStartNanos: string): OtlpMetric {
  return {
    name: 'droid.code.files_modified',
    kind: 'gauge',
    dataPoints: [
      {
        attributes: { 'session.id': 'session-a' },
        timeUnixNano: POINT_TIME_NANOS,
        startTimeUnixNano: intervalStartNanos,
        value: 4200,
        count: null,
        sum: null,
      },
    ],
    resource: { 'service.name': 'droid' },
    scope: { name: 'droid.telemetry', version: '1.0.0' },
  }
}

describe('schema migration v22 -> v23 (the metric point interval start)', () => {
  it('pins SCHEMA_VERSION to 23 and defines migration 22 as its own step', () => {
    expect(SCHEMA_VERSION).toBe(23)
    expect(MIGRATIONS[22]).toBeTypeOf('function')
  })

  it('builds fresh stores at version 23 with the interval column', () => {
    const dir = mkdtempSync(join(tmpdir(), 'kyber-fresh-v23-'))
    dirs.push(dir)
    const path = join(dir, 'canon.db')
    const store = new CanonStore(path)
    try {
      expect(store.getMetadata('schema_version')).toBe('23')

      const cols = tableInfo(path, 'metric_point')
      const colMap = new Map(cols.map((c) => [c.name, c]))
      expect(
        colMap.get('start_time_unix_nano'),
        'a fresh store must carry the interval column',
      ).toMatchObject({ type: 'TEXT', notnull: 0 })
    } finally {
      store.close()
    }
  })

  it('upgrades a v22 store to 23 and adds the interval column to it', () => {
    const path = v22StoreAt()

    // Before the upgrade the store is genuinely v22: the table is there, the
    // interval column is not, and the stamp says 22.
    expect(tableInfo(path, 'metric_point').map((c) => c.name)).not.toContain(
      'start_time_unix_nano',
    )

    const store = new CanonStore(path)
    try {
      expect(store.getMetadata('schema_version')).toBe('23')
      expect(
        tableInfo(path, 'metric_point').map((c) => c.name),
        'the migration must add the interval column to an existing store',
      ).toContain('start_time_unix_nano')
    } finally {
      store.close()
    }
  })

  it('keeps the rows the corpus already held, and gives them no interval', () => {
    const path = v22StoreAt()
    const store = new CanonStore(path)
    try {
      const rows = store.getMetricPoints()
      expect(rows).toHaveLength(1)
      expect(rows[0]?.name).toBe('droid.code.files_modified')
      // The column is new, so a row that predates it has no interval: absent,
      // never a fabricated zero.
      expect(rows[0]?.startTimeUnixNano).toBeNull()
    } finally {
      store.close()
    }
  })

  it('stores the interval of a point written after the migration', () => {
    const path = v22StoreAt()
    const store = new CanonStore(path)
    try {
      store.upsertMetrics([metricOver(FIRST_INTERVAL_START_NANOS)])
      const rows = store.getMetricPoints()
      expect(rows).toHaveLength(2)
      const written = rows.find(
        (row) => row.name === 'droid.code.files_modified' && row.startTimeUnixNano !== null,
      )
      expect(written?.startTimeUnixNano).toBe(FIRST_INTERVAL_START_NANOS)
    } finally {
      store.close()
    }
  })

  it('is idempotent: reopening the upgraded store adds nothing and stays at 23', () => {
    const path = v22StoreAt()
    new CanonStore(path).close()

    const store = new CanonStore(path)
    try {
      expect(store.getMetadata('schema_version')).toBe('23')
      const cols = tableInfo(path, 'metric_point').map((c) => c.name)
      expect(cols.filter((name) => name === 'start_time_unix_nano')).toHaveLength(1)
    } finally {
      store.close()
    }
  })
})
