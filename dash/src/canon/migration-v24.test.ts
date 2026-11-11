// The v23 -> v24 migration: duplicate record repair.
//
// A refresh re-mints a hash-backed model-call record under a new id whenever its
// window start moves, so one call can sit in the store as several copies, one per
// import. Each copy's record_provenance row names the import that wrote it. The
// migration keeps every row of the latest import and deletes the hash-backed
// rows of older imports, with their tool children and provenance.
//
// The fixture writes rows straight to SQL at schema 23. Going through
// commitSourceUnit would let the new commit path repair the duplicates before
// the migration ever ran.

import { mkdtempSync, rmSync } from 'node:fs'
import { createRequire } from 'node:module'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore, MIGRATIONS, RECORDS_WRITE_MARKER_KEY, SCHEMA_VERSION, migrateCanonDatabase } from './store.js'
import { compressRaw } from './store-rows.js'

const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}

const dirs: string[] = []

afterEach(() => {
  while (dirs.length > 0) rmSync(dirs.pop()!, { recursive: true, force: true })
})

const IMPORT_OLD = '2026-09-10T08:00:00.000Z'
const IMPORT_MID = '2026-09-11T08:00:00.000Z'
const IMPORT_NEW = '2026-09-12T08:00:00.000Z'

const T_KILO = '2026-09-01T10:00:00.000Z'
const T_CURSOR_A = '2026-09-02T10:00:00.000Z'
const T_CURSOR_B = '2026-09-02T10:00:05.000Z'
const T_COPILOT = '2026-09-03T10:00:00.000Z'
const T_PI = '2026-09-04T10:00:00.000Z'

/** The marker value the fixture seeds; the migration must advance it. */
const MARKER_BEFORE = 7

type SeedRow = {
  spanId: string
  op: 'llm.invoke' | 'tool.invoke'
  harness: string
  session: string
  name: string
  timestamp: string
  imported: string
  /** Hash-backed rows carry a record digest; stable-id rows do not. */
  hashBacked: boolean
  parentSpanId: string | null
}

function call(
  spanId: string,
  fields: { harness: string; session: string; name: string; timestamp: string; imported: string; hashBacked?: boolean },
): SeedRow {
  return {
    spanId,
    op: 'llm.invoke',
    harness: fields.harness,
    session: fields.session,
    name: fields.name,
    timestamp: fields.timestamp,
    imported: fields.imported,
    hashBacked: fields.hashBacked ?? true,
    parentSpanId: null,
  }
}

function tool(
  spanId: string,
  parentSpanId: string,
  fields: { harness: string; session: string; timestamp: string; imported: string },
): SeedRow {
  return {
    spanId,
    op: 'tool.invoke',
    harness: fields.harness,
    session: fields.session,
    name: 'Read',
    timestamp: fields.timestamp,
    imported: fields.imported,
    hashBacked: false,
    parentSpanId,
  }
}

/**
 * (a) Kilo: one call, three hash-backed copies from three imports, each with a tool child.
 * (b) Cursor: two distinct calls, each copied in two imports.
 * (c) Copilot: two distinct same-instant calls from a single import.
 * (d) Pi: stable-id rows from two imports.
 */
const ROWS: SeedRow[] = [
  call('kilo-hash-old', { harness: 'kilo-shared-runtime', session: 'kilo-root', name: 'kilo-code:opus', timestamp: T_KILO, imported: IMPORT_OLD }),
  tool('kilo-tool-old', 'kilo-hash-old', { harness: 'kilo-shared-runtime', session: 'kilo-root', timestamp: T_KILO, imported: IMPORT_OLD }),
  call('kilo-hash-mid', { harness: 'kilo-shared-runtime', session: 'kilo-root', name: 'kilo-code:opus', timestamp: T_KILO, imported: IMPORT_MID }),
  tool('kilo-tool-mid', 'kilo-hash-mid', { harness: 'kilo-shared-runtime', session: 'kilo-root', timestamp: T_KILO, imported: IMPORT_MID }),
  call('kilo-hash-new', { harness: 'kilo-shared-runtime', session: 'kilo-root', name: 'kilo-code:opus', timestamp: T_KILO, imported: IMPORT_NEW }),
  tool('kilo-tool-new', 'kilo-hash-new', { harness: 'kilo-shared-runtime', session: 'kilo-root', timestamp: T_KILO, imported: IMPORT_NEW }),

  call('cur-old-a', { harness: 'cursor', session: 'cur-s1', name: 'cursor:model', timestamp: T_CURSOR_A, imported: IMPORT_OLD }),
  tool('cur-tool-old-a', 'cur-old-a', { harness: 'cursor', session: 'cur-s1', timestamp: T_CURSOR_A, imported: IMPORT_OLD }),
  call('cur-old-b', { harness: 'cursor', session: 'cur-s1', name: 'cursor:model', timestamp: T_CURSOR_B, imported: IMPORT_OLD }),
  call('cur-new-a', { harness: 'cursor', session: 'cur-s1', name: 'cursor:model', timestamp: T_CURSOR_A, imported: IMPORT_NEW }),
  call('cur-new-b', { harness: 'cursor', session: 'cur-s1', name: 'cursor:model', timestamp: T_CURSOR_B, imported: IMPORT_NEW }),

  call('cop-same-a', { harness: 'copilot', session: 'cop-s1', name: 'copilot:model', timestamp: T_COPILOT, imported: IMPORT_NEW }),
  call('cop-same-b', { harness: 'copilot', session: 'cop-s1', name: 'copilot:model', timestamp: T_COPILOT, imported: IMPORT_NEW }),

  call('pi-stable-old', { harness: 'pi', session: 'pi-s1', name: 'pi:model', timestamp: T_PI, imported: IMPORT_OLD, hashBacked: false }),
  call('pi-stable-new', { harness: 'pi', session: 'pi-s1', name: 'pi:model', timestamp: T_PI, imported: IMPORT_NEW, hashBacked: false }),
]

/** Every span the repair keeps, sorted as the SQL ORDER BY sorts them. */
const SURVIVORS = [
  'cop-same-a',
  'cop-same-b',
  'cur-new-a',
  'cur-new-b',
  'kilo-hash-new',
  'kilo-tool-new',
  'pi-stable-new',
  'pi-stable-old',
]

function insertRow(db: InstanceType<typeof DatabaseSync>, row: SeedRow): void {
  const digest = row.hashBacked ? `digest-${row.spanId}` : undefined
  const raw = { provenance: digest === undefined ? {} : { recordDigest: digest } }
  db.prepare(
    `INSERT INTO records (
       span_id, trace_id, parent_span_id, source, harness, session_id, name, op, kind,
       timestamp, duration_ms, status, tokens_json, content_json, cost_json,
       measurability_json, parts_json, raw
     ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'internal', ?, 0, 'ok', '{}', '{}', '{}', NULL, NULL, ?)`,
  ).run(
    row.spanId,
    `synth:${row.harness}:${row.session}`,
    row.parentSpanId,
    `codeburn/${row.harness}`,
    row.harness,
    row.session,
    row.name,
    row.op,
    row.timestamp,
    compressRaw(raw),
  )
  db.prepare(
    `INSERT INTO record_provenance (
       span_id, harness_id, source_key, native_session_id, native_record_id,
       source_revision, parser_version, imported_at_utc, location_token
     ) VALUES (?, ?, ?, ?, ?, 'rev-1', '1', ?, 'codeburn/fixture')`,
  ).run(
    row.spanId,
    row.harness,
    `${row.harness}:${row.session}`,
    row.session,
    digest ?? `turn-${row.spanId}`,
    row.imported,
  )
}

/** A store stamped at schema 23 and holding the duplicate fixture rows. */
function seedV23Store(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-migration-v24-'))
  dirs.push(dir)
  const path = join(dir, 'canon.db')

  new CanonStore(path).close()

  const db = new DatabaseSync(path)
  try {
    db.prepare("UPDATE metadata SET value = '23' WHERE key = 'schema_version'").run()
    db.prepare('INSERT OR REPLACE INTO metadata (key, value) VALUES (?, ?)').run(
      RECORDS_WRITE_MARKER_KEY,
      String(MARKER_BEFORE),
    )
    for (const row of ROWS) insertRow(db, row)
  } finally {
    db.close()
  }
  return path
}

type StoreState = {
  schemaVersion: string | undefined
  marker: string | undefined
  records: string[]
  provenance: string[]
}

function readState(path: string): StoreState {
  const db = new DatabaseSync(path)
  try {
    const meta = (key: string): string | undefined =>
      (db.prepare('SELECT value FROM metadata WHERE key = ?').get(key) as { value: string } | undefined)?.value
    const ids = (sql: string): string[] =>
      (db.prepare(sql).all() as { span_id: string }[]).map((row) => row.span_id)
    return {
      schemaVersion: meta('schema_version'),
      marker: meta(RECORDS_WRITE_MARKER_KEY),
      records: ids('SELECT span_id FROM records ORDER BY span_id'),
      provenance: ids('SELECT span_id FROM record_provenance ORDER BY span_id'),
    }
  } finally {
    db.close()
  }
}

describe('v23 -> v24 duplicate record repair', () => {
  it('pins SCHEMA_VERSION to 24 and defines migration 23 as its own step', () => {
    expect(SCHEMA_VERSION).toBe(24)
    expect(MIGRATIONS[23], 'migration 23 must be a function').toBeTypeOf('function')
  })

  it('keeps only the latest import copies of duplicated calls when the store reopens', () => {
    const path = seedV23Store()

    new CanonStore(path).close()

    const state = readState(path)
    expect(state.records, 'only the latest import survives among the duplicates').toEqual(SURVIVORS)
    expect(state.provenance, 'provenance of deleted rows is gone').toEqual(SURVIVORS)
    expect(Number(state.marker), 'the records write marker advances').toBeGreaterThan(MARKER_BEFORE)
    expect(state.schemaVersion).toBe('24')
  })

  it('changes nothing when migration 23 runs again', () => {
    const path = seedV23Store()
    const step = MIGRATIONS[23]
    expect(step).toBeTypeOf('function')

    const db = new DatabaseSync(path)
    try {
      db.exec('BEGIN')
      step!(db)
      db.exec('COMMIT')
      const afterFirst = readState(path)

      db.exec('BEGIN')
      step!(db)
      db.exec('COMMIT')
      const afterSecond = readState(path)

      expect(afterSecond.records).toEqual(afterFirst.records)
      expect(afterSecond.provenance).toEqual(afterFirst.provenance)
      expect(afterSecond.marker, 'a second pass must not advance the marker').toBe(afterFirst.marker)
    } finally {
      db.close()
    }
  })

  it('migrateCanonDatabase leaves the same surviving rows as reopening the store', async () => {
    const reopenPath = seedV23Store()
    new CanonStore(reopenPath).close()

    const migratePath = seedV23Store()
    await migrateCanonDatabase(migratePath)

    const migrated = readState(migratePath)
    expect(migrated.records).toEqual(SURVIVORS)
    expect(migrated.provenance).toEqual(SURVIVORS)
    expect(migrated.records).toEqual(readState(reopenPath).records)
    expect(migrated.schemaVersion).toBe('24')
  })
})
