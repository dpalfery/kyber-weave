// Canonical store for KyberDash (spec: docs/specs/kyberdash, design.md
// "Data Models"). SQLite is reached through Node's built-in `node:sqlite`
// module — upstream already depends on it for the Cursor and OpenCode
// providers, so the store adds no dependency. The schema lives here as a
// version-controlled constant and is executed on construction, following the
// Python pipeline's rule: any clone builds an empty store on first use and the
// database file stays pure local data.
//
// `span_id` is the primary key, which is what makes re-ingest idempotent
// (R2.5): the same corpus applied twice lands on the same rows. `upsertMany`
// wraps a batch in one transaction so spans arriving faster than they can be
// persisted are written as a unit and never dropped. The raw column is
// deflate-compressed rather than stored verbatim (R12.4) — the measured cost
// of not doing so is 2.9 GB for 37,623 records, roughly 78 KB per span.

import { mkdirSync } from 'node:fs'
import { dirname } from 'node:path'
import { DatabaseSync, type StatementSync } from 'node:sqlite'
import { deflateSync, inflateSync } from 'node:zlib'

import type { CanonicalRecord, CostBlock, Measurability, Problem, TokenUsage } from './types.js'

/** Bump when SCHEMA_SQL changes shape; a store built under another version refuses to open. */
export const SCHEMA_VERSION = 1

/**
 * The whole schema, as code. `CREATE ... IF NOT EXISTS` throughout so
 * constructing against an existing store is a no-op and a fresh clone builds
 * the empty store on first use. The tokenization cache table is created here
 * because the schema is versioned as one unit; its accessors arrive with the
 * tokenizer (task 3.3).
 */
export const SCHEMA_SQL = `
CREATE TABLE IF NOT EXISTS records (
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
CREATE INDEX IF NOT EXISTS records_by_trace ON records (trace_id);
CREATE INDEX IF NOT EXISTS records_by_timestamp ON records (timestamp);
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
`

/** A problem pinned to the record it belongs to (the `problems` table row). */
export type SpanProblem = Problem & { spanId: string }

/** One entry of the ingest audit log. */
export type IngestLogEntry = {
  source: string
  count: number
  timestamp: string
}

/** One quarantined span: held back from the corpus, with the reason why. */
export type QuarantineEntry = {
  spanId: string
  namespaces: string[]
  reason: string
}

type RecordRow = {
  span_id: unknown
  trace_id: unknown
  parent_span_id: unknown
  source: unknown
  harness: unknown
  name: unknown
  op: unknown
  kind: unknown
  timestamp: unknown
  duration_ms: unknown
  status: unknown
  tokens_json: unknown
  content_json: unknown
  cost_json: unknown
  measurability_json: unknown
  raw: unknown
}

/**
 * Deflate the raw payload into the bytes the store keeps (R12.4). JSON
 * telemetry is highly repetitive — attribute names, prompt scaffolding — so
 * this is where the 78 KB/span floor collapses.
 */
export function compressRaw(raw: unknown): Uint8Array {
  return deflateSync(Buffer.from(JSON.stringify(raw), 'utf8'))
}

/** Inverse of `compressRaw`; the caller parses the JSON. */
export function decompressRaw(blob: Uint8Array): unknown {
  return JSON.parse(inflateSync(Buffer.from(blob)).toString('utf8'))
}

function text(value: unknown): string {
  return value as string
}

function nullableText(value: unknown): string | null {
  return (value as string | null) ?? null
}

function toRecord(row: RecordRow): CanonicalRecord {
  const record: CanonicalRecord = {
    spanId: text(row.span_id),
    traceId: nullableText(row.trace_id),
    parentSpanId: nullableText(row.parent_span_id),
    source: text(row.source),
    harness: text(row.harness),
    name: text(row.name),
    op: text(row.op),
    kind: text(row.kind),
    // Timestamps normalize to ISO strings on the way in; a Date input and a
    // string input are indistinguishable after a round trip.
    timestamp: text(row.timestamp),
    durationMs: row.duration_ms as number,
    status: text(row.status),
    tokens: JSON.parse(text(row.tokens_json)) as TokenUsage,
    content: JSON.parse(text(row.content_json)) as CanonicalRecord['content'],
    cost: JSON.parse(text(row.cost_json)) as CostBlock,
    raw: row.raw === null ? undefined : decompressRaw(row.raw as Uint8Array),
  }
  if (row.measurability_json !== null) {
    record.measurability = JSON.parse(text(row.measurability_json)) as Measurability
  }
  return record
}

const UPSERT_RECORD_SQL = `
INSERT OR REPLACE INTO records (
  span_id, trace_id, parent_span_id, source, harness, name, op, kind,
  timestamp, duration_ms, status, tokens_json, content_json, cost_json,
  measurability_json, raw
) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
`

export class CanonStore {
  private readonly db: DatabaseSync
  private readonly upsertStatement: StatementSync

  /**
   * Opens (or creates) the store at `path` and brings its schema up to
   * `SCHEMA_VERSION`. `:memory:` gives a throwaway store for tests. A store
   * whose recorded schema version does not match this build refuses to open —
   * that is how a database built by an older version is detected rather than
   * silently misread.
   */
  constructor(path: string) {
    if (path !== ':memory:') {
      mkdirSync(dirname(path), { recursive: true })
    }
    this.db = new DatabaseSync(path)
    // R2.5: under an ingest burst a second writer waits instead of failing,
    // and WAL keeps batch commits cheap.
    this.db.exec('PRAGMA busy_timeout = 5000')
    this.db.exec('PRAGMA journal_mode = WAL')
    this.db.exec(SCHEMA_SQL)

    const existing = this.getMetadata('schema_version')
    if (existing === undefined) {
      this.db
        .prepare('INSERT INTO metadata (key, value) VALUES (?, ?)')
        .run('schema_version', String(SCHEMA_VERSION))
    } else if (Number(existing) !== SCHEMA_VERSION) {
      this.db.close()
      throw new Error(
        `canon store at ${path} was built with schema version ${existing}, ` +
          `but this build understands version ${SCHEMA_VERSION}; rebuild the store`,
      )
    }

    this.upsertStatement = this.db.prepare(UPSERT_RECORD_SQL)
  }

  /** Store one record; re-ingesting the same span leaves the same row (R2.5). */
  upsert(record: CanonicalRecord): void {
    this.upsertMany([record])
  }

  /**
   * Store a batch of records in one transaction (R2.5): spans arriving faster
   * than they can be persisted are written as a unit and never dropped. The
   * statement is keyed on `span_id`, so a span already present is replaced by
   * its current form rather than duplicated.
   */
  upsertMany(records: readonly CanonicalRecord[]): void {
    if (records.length === 0) return
    this.db.exec('BEGIN')
    try {
      for (const record of records) {
        const timestamp =
          record.timestamp instanceof Date ? record.timestamp.toISOString() : record.timestamp
        this.upsertStatement.run(
          record.spanId,
          record.traceId,
          record.parentSpanId,
          record.source,
          record.harness,
          record.name,
          record.op,
          record.kind,
          timestamp,
          record.durationMs,
          record.status,
          JSON.stringify(record.tokens),
          JSON.stringify(record.content),
          JSON.stringify(record.cost),
          record.measurability === undefined ? null : JSON.stringify(record.measurability),
          record.raw === undefined ? null : compressRaw(record.raw),
        )
      }
      this.db.exec('COMMIT')
    } catch (err) {
      this.db.exec('ROLLBACK')
      throw err
    }
  }

  /** Fetch a record by span id, decompressing the raw payload; absent id gives undefined. */
  get(spanId: string): CanonicalRecord | undefined {
    const row = this.db
      .prepare('SELECT * FROM records WHERE span_id = ?')
      .get(spanId) as RecordRow | undefined
    return row === undefined ? undefined : toRecord(row)
  }

  /** Number of stored records — the assertion behind store idempotency. */
  count(): number {
    return (this.db.prepare('SELECT COUNT(*) AS n FROM records').get() as { n: number }).n
  }

  /**
   * All canonical records ordered by timestamp — the single data path every
   * surface reads (R11.1). The terminal dashboard's period reports, breakdown
   * tables and daily activity are derived from this rather than from a live
   * parse, so file-sourced and OTLP-sourced sessions appear together.
   */
  listAll(): import('./types.js').CanonicalRecord[] {
    const rows = this.db.prepare('SELECT * FROM records ORDER BY timestamp').all() as RecordRow[]
    return rows.map(toRecord)
  }

  /**
   * Bytes the store keeps for one record's raw payload, compressed. Exposed so
   * the storage budget is testable per record (R12.4).
   */
  storedRawBytes(spanId: string): number | null {
    const row = this.db
      .prepare('SELECT length(raw) AS bytes FROM records WHERE span_id = ?')
      .get(spanId) as { bytes: number | null } | undefined
    return row === undefined ? null : row.bytes
  }

  /** Hold a span out of the corpus; re-quarantining the same span replaces the entry. */
  quarantine(spanId: string, namespaces: readonly string[], reason: string): void {
    this.db
      .prepare('INSERT OR REPLACE INTO quarantine (span_id, namespaces, reason) VALUES (?, ?, ?)')
      .run(spanId, JSON.stringify(namespaces), reason)
  }

  /** The quarantine entry for a span, with its parsed namespaces; absent id gives undefined. */
  getQuarantine(spanId: string): QuarantineEntry | undefined {
    const row = this.db
      .prepare('SELECT span_id, namespaces, reason FROM quarantine WHERE span_id = ?')
      .get(spanId) as { span_id: unknown; namespaces: unknown; reason: unknown } | undefined
    if (row === undefined) return undefined
    return {
      spanId: text(row.span_id),
      namespaces: JSON.parse(text(row.namespaces)) as string[],
      reason: text(row.reason),
    }
  }

  /** Every quarantined span ordered by span id — the R6.3 view's row list. */
  listQuarantine(): QuarantineEntry[] {
    const rows = this.db
      .prepare('SELECT span_id, namespaces, reason FROM quarantine ORDER BY span_id')
      .all() as { span_id: unknown; namespaces: unknown; reason: unknown }[]
    return rows.map((row) => ({
      spanId: text(row.span_id),
      namespaces: JSON.parse(text(row.namespaces)) as string[],
      reason: text(row.reason),
    }))
  }

  /** Record a surfaced failure the system declines to guess about. */
  recordProblem(problem: SpanProblem): void {
    this.db
      .prepare(
        'INSERT INTO problems (span_id, severity, code, message, location) VALUES (?, ?, ?, ?, ?)',
      )
      .run(problem.spanId, problem.severity, problem.code, problem.message, problem.location ?? null)
  }

  /** Recorded problems, optionally narrowed to one span; ordered as written. */
  getProblems(spanId?: string): SpanProblem[] {
    const rows = (
      spanId === undefined
        ? this.db.prepare('SELECT span_id, severity, code, message, location FROM problems').all()
        : this
            .db
            .prepare('SELECT span_id, severity, code, message, location FROM problems WHERE span_id = ?')
            .all(spanId)
    ) as { span_id: unknown; severity: unknown; code: unknown; message: unknown; location: unknown }[]
    return rows.map((row) => ({
      spanId: text(row.span_id),
      severity: text(row.severity) as Problem['severity'],
      code: text(row.code),
      message: text(row.message),
      location: row.location === null ? undefined : text(row.location),
    }))
  }

  /** Append one ingest run to the audit log. */
  logIngest(source: string, count: number): void {
    this.db
      .prepare('INSERT INTO ingest_log (source, count, timestamp) VALUES (?, ?, ?)')
      .run(source, count, new Date().toISOString())
  }

  /** The ingest audit log, oldest first. */
  getIngestLog(): IngestLogEntry[] {
    const rows = this.db
      .prepare('SELECT source, count, timestamp FROM ingest_log ORDER BY id')
      .all() as { source: unknown; count: unknown; timestamp: unknown }[]
    return rows.map((row) => ({
      source: text(row.source),
      count: row.count as number,
      timestamp: text(row.timestamp),
    }))
  }

  /** Read a metadata value; absent key gives undefined. */
  getMetadata(key: string): string | undefined {
    const row = this.db
      .prepare('SELECT value FROM metadata WHERE key = ?')
      .get(key) as { value: unknown } | undefined
    return row === undefined ? undefined : text(row.value)
  }

  close(): void {
    this.db.close()
  }
}
