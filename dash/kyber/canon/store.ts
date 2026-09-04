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
import { createRequire } from 'node:module'
const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}
type StatementSync = import('node:sqlite').StatementSync
type Database = import('node:sqlite').DatabaseSync
import { deflateSync, inflateSync } from 'node:zlib'

import { contentFromParts } from './types.js'
import type {
  CanonicalRecord,
  ContentPart,
  CostBlock,
  Measurability,
  Problem,
  TokenUsage,
} from './types.js'

/**
 * Bump when SCHEMA_SQL changes shape. A store built under a version this
 * build does not understand refuses to open; a store built under an older
 * one is migrated in place by `MIGRATIONS` rather than rebuilt, because the
 * corpus is the expensive thing here and re-collecting it is not always
 * possible.
 */
export const SCHEMA_VERSION = 3

/**
 * The whole schema, as code. `CREATE ... IF NOT EXISTS` throughout so
 * constructing against an existing store is a no-op and a fresh clone builds
 * the empty store on first use. The tokenization cache table is created here
 * because the schema is versioned as one unit; its accessors arrive with the
 * tokenizer (task 3.3).
 */
/**
 * The one table the constructor needs before it can ask what version the
 * store is. Repeated inside SCHEMA_SQL, which is harmless and keeps that
 * constant a complete description of the schema.
 */
export const METADATA_SQL = `
CREATE TABLE IF NOT EXISTS metadata (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
`

export const SCHEMA_SQL = `
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
-- Derived sessions: one row per conversation, payload built by the analysis
-- layer over the records table. This is what the dashboard reads. It is a
-- cache, not a source -- dropping every row and rebuilding loses nothing.
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
CREATE INDEX IF NOT EXISTS session_by_started ON session (started);
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

/**
 * In-place upgrades, keyed by the version they upgrade FROM. Each runs inside
 * one transaction and leaves the store at `key + 1`. `SCHEMA_SQL` cannot do
 * this work: every statement in it is `IF NOT EXISTS`, so an existing table
 * never gains a column.
 */
export const MIGRATIONS: Record<number, (db: Database) => void> = {
  // v1 -> v2: structured content parts. v1 stored content as a flat string
  // per bucket, which has nowhere to put the ground-truth MCP server a tool
  // definition arrived under, nor a harness-reported per-part token count.
  // Existing rows get a NULL column and keep working — `content_json` is
  // still their content; a backfill fills `parts_json` where the raw payload
  // supports it.
  1: (db) => {
    const columns = db.prepare('PRAGMA table_info(records)').all() as { name: string }[]
    if (!columns.some((column) => column.name === 'parts_json')) {
      db.exec('ALTER TABLE records ADD COLUMN parts_json BLOB')
    }
  },
  // v2 -> v3: the harness's own conversation id, promoted to a column so
  // sessions are a GROUP BY rather than a decompress-every-raw-payload scan.
  // Existing rows get NULL and fall back to their trace, which is what the
  // session builder does for any record whose source named no session.
  2: (db) => {
    const columns = db.prepare('PRAGMA table_info(records)').all() as { name: string }[]
    if (!columns.some((column) => column.name === 'session_id')) {
      db.exec('ALTER TABLE records ADD COLUMN session_id TEXT')
    }
    db.exec('CREATE INDEX IF NOT EXISTS records_by_session ON records (session_id)')
  },
}

/** One derived session, as the `session` table stores it. */
export type SessionRow = {
  sessionId: string
  harness: string
  label?: string | null
  isSubagent?: boolean
  parentSession?: string | null
  agentName?: string | null
  repo?: string | null
  branch?: string | null
  started?: string | null
  ended?: string | null
  /** The analysis output the dashboard reads. */
  payload: unknown
}

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
  session_id: unknown
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
  parts_json: unknown
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
    // `content` is overwritten below when parts are present; see toRecord.
    cost: JSON.parse(text(row.cost_json)) as CostBlock,
    raw: row.raw === null ? undefined : decompressRaw(row.raw as Uint8Array),
  }
  if (row.session_id !== null && row.session_id !== undefined) {
    record.sessionId = text(row.session_id)
  }
  if (row.measurability_json !== null) {
    record.measurability = JSON.parse(text(row.measurability_json)) as Measurability
  }
  if (row.parts_json !== null && row.parts_json !== undefined) {
    // Parts are the authority when present, and `content` is derived from
    // them on the way out rather than stored a second time. Storing both
    // measured 166 MB of uncompressed `content_json` against 40 MB for the
    // same text compressed as parts — a 4x store for one copy of the data,
    // which is the shape of the 2.9 GB problem R12.4 exists to prevent.
    record.parts = decompressRaw(row.parts_json as Uint8Array) as ContentPart[]
    record.content = contentFromParts(record.parts)
  }
  return record
}

const UPSERT_RECORD_SQL = `
INSERT OR REPLACE INTO records (
  span_id, trace_id, parent_span_id, source, harness, session_id, name, op, kind,
  timestamp, duration_ms, status, tokens_json, content_json, cost_json,
  measurability_json, parts_json, raw
) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
`

export class CanonStore {
  private readonly db: Database
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

    // Order matters. `SCHEMA_SQL` is every statement in its current shape,
    // and some of them — an index over a column a later version added —
    // cannot run against an older store. So: establish `metadata`, read the
    // version, migrate the old shapes forward, and only then apply the full
    // schema, by which point every `IF NOT EXISTS` is a genuine no-op.
    this.db.exec(METADATA_SQL)
    const existing = this.getMetadata('schema_version')
    if (existing !== undefined && Number(existing) !== SCHEMA_VERSION) {
      this.migrate(path, Number(existing))
    }

    this.db.exec(SCHEMA_SQL)

    if (existing === undefined) {
      this.db
        .prepare('INSERT INTO metadata (key, value) VALUES (?, ?)')
        .run('schema_version', String(SCHEMA_VERSION))
    }

    this.upsertStatement = this.db.prepare(UPSERT_RECORD_SQL)
  }

  /**
   * Walk `MIGRATIONS` from the store's recorded version up to this build's.
   * Each step commits with the version it produced, so an interrupted upgrade
   * resumes rather than replaying a step that already ran. A store from a
   * newer build, or one with no path forward, still refuses to open — silently
   * misreading a schema is the failure this guards.
   */
  private migrate(path: string, from: number): void {
    if (from > SCHEMA_VERSION) {
      this.db.close()
      throw new Error(
        `canon store at ${path} was built with schema version ${from}, ` +
          `but this build understands version ${SCHEMA_VERSION}; upgrade KyberDash`,
      )
    }
    for (let version = from; version < SCHEMA_VERSION; version += 1) {
      const step = MIGRATIONS[version]
      if (step === undefined) {
        this.db.close()
        throw new Error(
          `canon store at ${path} is at schema version ${version} with no migration ` +
            `to ${version + 1}; rebuild the store`,
        )
      }
      this.db.exec('BEGIN')
      try {
        step(this.db)
        this.db
          .prepare('INSERT OR REPLACE INTO metadata (key, value) VALUES (?, ?)')
          .run('schema_version', String(version + 1))
        this.db.exec('COMMIT')
      } catch (err) {
        this.db.exec('ROLLBACK')
        this.db.close()
        throw err
      }
    }
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
          record.sessionId ?? null,
          record.name,
          record.op,
          record.kind,
          timestamp,
          record.durationMs,
          record.status,
          JSON.stringify(record.tokens),
          // Derivable from parts, so it is not stored alongside them (R12.4).
          record.parts === undefined || record.parts.length === 0
            ? JSON.stringify(record.content)
            : '{}',
          JSON.stringify(record.cost),
          record.measurability === undefined ? null : JSON.stringify(record.measurability),
          // Parts repeat the content text, so they are compressed like `raw`
          // rather than stored verbatim (R12.4).
          record.parts === undefined ? null : compressRaw(record.parts),
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

  /**
   * Span ids in storage order. Exposed for passes that must walk the whole
   * corpus without holding it in memory — `listAll` decompresses every raw
   * payload at once, which on a real store is gigabytes.
   */
  spanIds(): string[] {
    const rows = this.db.prepare('SELECT span_id FROM records ORDER BY span_id').all() as {
      span_id: string
    }[]
    return rows.map((row) => row.span_id)
  }

  /**
   * Replace one record's content without rewriting the rest of the row.
   * The backfill uses this: content is re-derived from the raw payload the
   * store already holds, and nothing else about the span changes.
   */
  setContent(spanId: string, content: CanonicalRecord['content'], parts?: readonly ContentPart[]): void {
    const hasParts = parts !== undefined && parts.length > 0
    this.db
      .prepare('UPDATE records SET content_json = ?, parts_json = ? WHERE span_id = ?')
      .run(
        hasParts ? '{}' : JSON.stringify(content),
        hasParts ? compressRaw(parts) : null,
        spanId,
      )
  }

  /** Distinct trace ids, the unit attribution votes over. */
  traceIds(): string[] {
    return (
      this.db
        .prepare('SELECT DISTINCT trace_id FROM records WHERE trace_id IS NOT NULL')
        .all() as { trace_id: string }[]
    ).map((row) => row.trace_id)
  }

  /** Every record in one trace, in timestamp order. */
  recordsForTrace(traceId: string): CanonicalRecord[] {
    const rows = this.db
      .prepare('SELECT * FROM records WHERE trace_id = ? ORDER BY timestamp')
      .all(traceId) as RecordRow[]
    return rows.map(toRecord)
  }

  /** Rewrite the fields re-normalization decides, leaving content untouched. */
  setAttribution(
    spanId: string,
    fields: { harness: string; source: string; op: string; tokens: TokenUsage },
  ): void {
    this.db
      .prepare('UPDATE records SET harness = ?, source = ?, op = ?, tokens_json = ? WHERE span_id = ?')
      .run(fields.harness, fields.source, fields.op, JSON.stringify(fields.tokens), spanId)
  }

  /** Attach the harness's conversation id to a stored record. */
  setSessionId(spanId: string, sessionId: string | null): void {
    this.db.prepare('UPDATE records SET session_id = ? WHERE span_id = ?').run(sessionId, spanId)
  }

  /**
   * Distinct session keys across the corpus, newest first. The key is the
   * harness's own conversation id where it named one and the trace id
   * otherwise — a fallback, not a claim that a trace is a session.
   */
  sessionKeys(): { key: string; harness: string; started: string; ended: string }[] {
    return this.db
      .prepare(
        `SELECT COALESCE(session_id, trace_id) AS key, harness,
                MIN(timestamp) AS started, MAX(timestamp) AS ended
         FROM records
         WHERE COALESCE(session_id, trace_id) IS NOT NULL
         GROUP BY key, harness
         ORDER BY started DESC`,
      )
      .all() as { key: string; harness: string; started: string; ended: string }[]
  }

  /** Every record belonging to one session key, in timestamp order. */
  recordsForSession(key: string): CanonicalRecord[] {
    const rows = this.db
      .prepare(
        'SELECT * FROM records WHERE COALESCE(session_id, trace_id) = ? ORDER BY timestamp',
      )
      .all(key) as RecordRow[]
    return rows.map(toRecord)
  }

  /**
   * Store a derived session. These rows are a cache over `records`: dropping
   * them all and rebuilding loses nothing, which is why the payload is
   * replaced wholesale rather than merged.
   */
  upsertSession(row: SessionRow): void {
    this.db
      .prepare(
        `INSERT OR REPLACE INTO session (
           session_id, harness, label, is_subagent, parent_session,
           agent_name, repo, branch, started, ended, payload
         ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      )
      .run(
        row.sessionId,
        row.harness,
        row.label ?? null,
        row.isSubagent ? 1 : 0,
        row.parentSession ?? null,
        row.agentName ?? null,
        row.repo ?? null,
        row.branch ?? null,
        row.started ?? null,
        row.ended ?? null,
        JSON.stringify(row.payload),
      )
  }

  /** One derived session's payload, or undefined when it has not been built. */
  getSessionPayload(sessionId: string): unknown | undefined {
    const row = this.db
      .prepare('SELECT payload FROM session WHERE session_id = ?')
      .get(sessionId) as { payload: string } | undefined
    return row === undefined ? undefined : JSON.parse(row.payload)
  }

  /** Session ids currently built, for pruning a rebuild's leftovers. */
  builtSessionIds(): string[] {
    return (this.db.prepare('SELECT session_id FROM session').all() as { session_id: string }[]).map(
      (row) => row.session_id,
    )
  }

  /** Drop a derived session. Safe by construction: the row is a cache. */
  deleteSession(sessionId: string): void {
    this.db.prepare('DELETE FROM session WHERE session_id = ?').run(sessionId)
  }

  /** Number of derived sessions currently built. */
  sessionCount(): number {
    return (this.db.prepare('SELECT COUNT(*) AS n FROM session').get() as { n: number }).n
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
