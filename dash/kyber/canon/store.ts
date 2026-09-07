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
  ExecutionRow,
  ExecutionTreeNode,
  HarnessRollupRow,
  Measurability,
  MetricAvailability,
  Problem,
  RunGroupingBasis,
  RunRow,
  SessionRow,
  TokenUsage,
} from './types.js'
import type {
  DetectorId,
  Finding,
  FindingConfidence,
  FindingEvidenceLink,
} from '../analysis/findings.js'
import {
  calculateCalibrationCurve,
  type CalibrationCurveResult,
  type PredictionRecord,
} from '../analysis/calibration.js'

/**
 * Bump when SCHEMA_SQL changes shape. A store built under a version this
 * build does not understand refuses to open; a store built under an older
 * one is migrated in place by `MIGRATIONS` rather than rebuilt, because the
 * corpus is the expensive thing here and re-collecting it is not always
 * possible.
 */
export const SCHEMA_VERSION = 9

/**
 * Version of the diagnostic signal and finding detector suite (Decision D17).
 * When detectors change, this version stamp is bumped to force automatic
 * recomputation of derived findings and signals over stored canonical records.
 */
export const DETECTOR_VERSION = 1

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
-- Derived runs: group executions into user-initiated units of work.
-- Grouping basis (explicit or derived) and rule are recorded per row (D13).
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
CREATE INDEX IF NOT EXISTS run_by_harness ON run (harness);
CREATE INDEX IF NOT EXISTS run_by_started ON run (started);
-- Derived agent executions: individual agent sessions or subagents within a run.
-- Parent/child linkage is preserved where emitted and explicitly not_measurable otherwise.
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
CREATE INDEX IF NOT EXISTS execution_by_run ON execution (run_id);
CREATE INDEX IF NOT EXISTS execution_by_parent ON execution (parent_execution_id);
CREATE INDEX IF NOT EXISTS execution_by_session ON execution (session_id);
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
CREATE INDEX IF NOT EXISTS finding_by_run ON finding (run_id);
CREATE INDEX IF NOT EXISTS finding_by_session ON finding (session_id);
CREATE INDEX IF NOT EXISTS finding_by_rank_score ON finding (rank_score DESC);
-- Prediction table for logging and scoring finding waste predictions (Task F4 / Decision D11).
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
CREATE INDEX IF NOT EXISTS prediction_by_finding ON prediction (finding_id);
CREATE INDEX IF NOT EXISTS prediction_by_run ON prediction (run_id);
CREATE INDEX IF NOT EXISTS prediction_by_created_at ON prediction (created_at);
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
  3: (db) => {
    db.exec(`CREATE TABLE IF NOT EXISTS pending_logs (
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
    );`)
  },
  4: (db) => {
    db.exec(`CREATE TABLE IF NOT EXISTS run (
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
    CREATE INDEX IF NOT EXISTS run_by_harness ON run (harness);
    CREATE INDEX IF NOT EXISTS run_by_started ON run (started);

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
    CREATE INDEX IF NOT EXISTS execution_by_run ON execution (run_id);
    CREATE INDEX IF NOT EXISTS execution_by_parent ON execution (parent_execution_id);
    CREATE INDEX IF NOT EXISTS execution_by_session ON execution (session_id);`)
  },
  5: (db) => {
    db.exec(`CREATE TABLE IF NOT EXISTS harness_rollup (
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
    );`)
  },
  // v6 -> v7: Decision D17 - detector_version schema stamp
  // Records the version of the pure signal and finding detectors.
  // When detector_version is bumped, automatic recomputation is triggered.
  6: (db) => {
    db.prepare('INSERT OR REPLACE INTO metadata (key, value) VALUES (?, ?)').run(
      'detector_version',
      String(DETECTOR_VERSION),
    )
  },
  // v7 -> v8: Task F3 / Decision D5 finding table and indexes
  7: (db) => {
    db.exec(`CREATE TABLE IF NOT EXISTS finding (
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
    CREATE INDEX IF NOT EXISTS finding_by_run ON finding (run_id);
    CREATE INDEX IF NOT EXISTS finding_by_session ON finding (session_id);
    CREATE INDEX IF NOT EXISTS finding_by_rank_score ON finding (rank_score DESC);`)
  },
  // v8 -> v9: Task F4 prediction logging and calibration table with indexes
  8: (db) => {
    db.exec(`CREATE TABLE IF NOT EXISTS prediction (
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
    CREATE INDEX IF NOT EXISTS prediction_by_finding ON prediction (finding_id);
    CREATE INDEX IF NOT EXISTS prediction_by_run ON prediction (run_id);
    CREATE INDEX IF NOT EXISTS prediction_by_created_at ON prediction (created_at);`)
  },
}

export type {
  DetectorId,
  Finding,
  FindingConfidence,
  FindingEvidenceLink,
  PredictionRecord,
  CalibrationCurveResult,
  SessionRow,
  ExecutionTreeNode,
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

export type QuarantinedLog = {
  logId: string
  reason: string
  log: unknown
}

export type RecordRow = {
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

export function toRecord(row: RecordRow): CanonicalRecord {
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
    record.content = {
      ...contentFromParts(record.parts),
      ...(JSON.parse(text(row.content_json)) as CanonicalRecord['content']),
    }
  }
  return record
}

export type SessionDbRow = {
  session_id: unknown
  harness: unknown
  label: unknown
  is_subagent: unknown
  parent_session: unknown
  agent_name: unknown
  repo: unknown
  branch: unknown
  started: unknown
  ended: unknown
  payload: unknown
}

export function toSessionRow(row: SessionDbRow): SessionRow {
  let payload: unknown = undefined
  if (row.payload !== null && row.payload !== undefined) {
    try {
      payload = JSON.parse(text(row.payload))
    } catch {
      payload = row.payload
    }
  }
  return {
    sessionId: text(row.session_id),
    harness: text(row.harness),
    label: nullableText(row.label),
    isSubagent: Boolean(row.is_subagent),
    parentSession: nullableText(row.parent_session),
    agentName: nullableText(row.agent_name),
    repo: nullableText(row.repo),
    branch: nullableText(row.branch),
    started: nullableText(row.started),
    ended: nullableText(row.ended),
    payload,
  }
}

export type RunDbRow = {
  run_id: unknown
  harness: unknown
  label: unknown
  grouping_basis: unknown
  grouping_rule: unknown
  working_directory: unknown
  started: unknown
  ended: unknown
  execution_count: unknown
  payload: unknown
}

export function toRunRow(row: RunDbRow): RunRow {
  const payload = row.payload === null || row.payload === undefined ? undefined : JSON.parse(text(row.payload))
  const outcome =
    payload !== null && typeof payload === 'object' && !Array.isArray(payload) && 'outcome' in payload
      ? (payload as Record<string, unknown>)['outcome']
      : undefined
  return {
    runId: text(row.run_id),
    harness: text(row.harness),
    label: nullableText(row.label),
    groupingBasis: text(row.grouping_basis) as RunGroupingBasis,
    groupingRule: nullableText(row.grouping_rule),
    workingDirectory: nullableText(row.working_directory),
    started: nullableText(row.started),
    ended: nullableText(row.ended),
    executionCount: Number(row.execution_count ?? 0),
    ...(outcome !== undefined ? { outcome: outcome as RunRow['outcome'] } : {}),
    payload,
  }
}

export type ExecutionDbRow = {
  execution_id: unknown
  run_id: unknown
  session_id: unknown
  parent_execution_id: unknown
  harness: unknown
  agent_name: unknown
  is_root: unknown
  started: unknown
  ended: unknown
  parent_linkage_json: unknown
  payload: unknown
}

export function toExecutionRow(row: ExecutionDbRow): ExecutionRow {
  return {
    executionId: text(row.execution_id),
    runId: text(row.run_id),
    sessionId: nullableText(row.session_id),
    parentExecutionId: nullableText(row.parent_execution_id),
    harness: text(row.harness),
    agentName: nullableText(row.agent_name),
    isRoot: Boolean(row.is_root),
    started: nullableText(row.started),
    ended: nullableText(row.ended),
    parentLinkage: JSON.parse(text(row.parent_linkage_json)) as MetricAvailability,
    payload: row.payload === null || row.payload === undefined ? undefined : JSON.parse(text(row.payload)),
  }
}

export type HarnessRollupDbRow = {
  harness: unknown
  sample_count: unknown
  context_pressure_median: unknown
  context_pressure_p95: unknown
  cache_hit_rate: unknown
  tool_yield: unknown
  delegation_overhead: unknown
  field_coverage: unknown
  measurability_json: unknown
  payload: unknown
}

function nullableNumber(value: unknown): number | null {
  if (value === null || value === undefined) return null
  const num = Number(value)
  return Number.isFinite(num) ? num : null
}

export function toHarnessRollupRow(row: HarnessRollupDbRow): HarnessRollupRow {
  return {
    harness: text(row.harness),
    sampleCount: Number(row.sample_count ?? 0),
    contextPressureMedian: nullableNumber(row.context_pressure_median),
    contextPressureP95: nullableNumber(row.context_pressure_p95),
    cacheHitRate: nullableNumber(row.cache_hit_rate),
    toolYield: nullableNumber(row.tool_yield),
    delegationOverhead: nullableNumber(row.delegation_overhead),
    fieldCoverage: nullableNumber(row.field_coverage),
    measurability: JSON.parse(text(row.measurability_json)) as Record<string, MetricAvailability>,
    payload: row.payload === null || row.payload === undefined ? undefined : JSON.parse(text(row.payload)),
  }
}

export type FindingDbRow = {
  id: unknown
  detector_id: unknown
  title: unknown
  mechanism: unknown
  confidence: unknown
  estimated_waste_tokens: unknown
  recommendation: unknown
  error_bar_json: unknown
  evidence_links_json: unknown
  outcome_risk_caveat: unknown
  run_id: unknown
  session_id: unknown
  rank_score: unknown
  payload: unknown
}

export function toFinding(row: FindingDbRow): Finding {
  const errorBar = JSON.parse(text(row.error_bar_json)) as { lower: number; upper: number }
  const evidenceLinks = JSON.parse(text(row.evidence_links_json)) as FindingEvidenceLink[]
  const finding: Finding = {
    id: text(row.id),
    detectorId: text(row.detector_id) as DetectorId,
    title: text(row.title),
    mechanism: text(row.mechanism),
    evidenceLinks,
    confidence: text(row.confidence) as FindingConfidence,
    estimatedWasteTokens: Number(row.estimated_waste_tokens ?? 0),
    recommendation: text(row.recommendation),
    errorBar,
    outcomeRiskCaveat: text(row.outcome_risk_caveat),
    runId: nullableText(row.run_id) ?? undefined,
    sessionId: nullableText(row.session_id) ?? undefined,
    rankScore: Number(row.rank_score ?? 0),
  }
  if (row.payload !== null && row.payload !== undefined) {
    try {
      const extra = JSON.parse(text(row.payload)) as Record<string, unknown>
      Object.assign(finding, extra)
    } catch {}
  }
  return finding
}

export type PredictionDbRow = {
  id: unknown
  finding_id: unknown
  run_id: unknown
  predicted_waste_tokens: unknown
  confidence: unknown
  confidence_tier: unknown
  error_bar_json: unknown
  created_at: unknown
  comparison_run_id: unknown
  observed_delta_tokens: unknown
  calibration_score: unknown
  payload: unknown
}

export function toPrediction(row: PredictionDbRow): PredictionRecord {
  const errorBar = row.error_bar_json
    ? (JSON.parse(text(row.error_bar_json)) as { lower: number; upper: number })
    : undefined
  const createdAt = text(row.created_at)
  const prediction: PredictionRecord = {
    id: text(row.id),
    findingId: text(row.finding_id),
    runId: text(row.run_id),
    predictedWasteTokens: Number(row.predicted_waste_tokens ?? 0),
    confidence: Number(row.confidence ?? 0),
    confidenceTier: row.confidence_tier ? (text(row.confidence_tier) as FindingConfidence) : undefined,
    errorBar,
    createdAt,
    timestamp: createdAt,
    comparisonRunId: nullableText(row.comparison_run_id) ?? undefined,
    observedDeltaTokens:
      row.observed_delta_tokens !== null && row.observed_delta_tokens !== undefined
        ? Number(row.observed_delta_tokens)
        : undefined,
    calibrationScore:
      row.calibration_score !== null && row.calibration_score !== undefined
        ? Number(row.calibration_score)
        : undefined,
    status:
      row.comparison_run_id !== null && row.comparison_run_id !== undefined
        ? 'scored'
        : 'pending',
  }
  if (row.payload !== null && row.payload !== undefined) {
    try {
      prediction.payload = JSON.parse(text(row.payload)) as Record<string, unknown>
    } catch {}
  }
  return prediction
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
      this.db
        .prepare('INSERT INTO metadata (key, value) VALUES (?, ?)')
        .run('detector_version', String(DETECTOR_VERSION))
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

  /**
   * Move a record out of the canonical corpus and into quarantine atomically.
   * Reclassification uses this for rows retained by an older ingest pass:
   * the audit entry and the removal cannot disagree after a partial write.
   */
  quarantineAndDelete(spanId: string, namespaces: readonly string[], reason: string): void {
    this.db.exec('BEGIN')
    try {
      this.db
        .prepare('INSERT OR REPLACE INTO quarantine (span_id, namespaces, reason) VALUES (?, ?, ?)')
        .run(spanId, JSON.stringify(namespaces), reason)
      this.db.prepare('DELETE FROM records WHERE span_id = ?').run(spanId)
      this.db.exec('COMMIT')
    } catch (err) {
      this.db.exec('ROLLBACK')
      throw err
    }
  }

  /** Attach the harness's conversation id to a stored record. */
  setSessionId(spanId: string, sessionId: string | null): void {
    this.db.prepare('UPDATE records SET session_id = ? WHERE span_id = ?').run(sessionId, spanId)
  }

  /** Find one span by the exact OTLP correlation identity. */
  findByTraceSpan(traceId: string, spanId: string): CanonicalRecord | undefined {
    const row = this.db
      .prepare('SELECT * FROM records WHERE trace_id = ? AND span_id = ?')
      .get(traceId, spanId) as RecordRow | undefined
    return row === undefined ? undefined : toRecord(row)
  }

  /** Find the nearest record in a session, bounded to avoid accidental joins. */
  findBySessionTime(sessionId: string, timestamp: string, windowMs = 5_000): CanonicalRecord | undefined {
    const rows = this.db
      .prepare('SELECT * FROM records WHERE session_id = ?')
      .all(sessionId) as RecordRow[]
    const target = Date.parse(timestamp)
    return rows
      .map(toRecord)
      .filter((record) => Math.abs(Date.parse(String(record.timestamp)) - target) <= windowMs)
      .sort((a, b) => Math.abs(Date.parse(String(a.timestamp)) - target) - Math.abs(Date.parse(String(b.timestamp)) - target))[0]
  }

  /** Merge log evidence into a record without touching model counters. */
  enrich(spanId: string, log: { body?: unknown; attributes?: Record<string, unknown>; sessionId?: string | null }): boolean {
    const existing = this.get(spanId)
    if (existing === undefined) return false
    const content = { ...existing.content } as Record<string, unknown>
    if (log.body !== undefined && log.body !== null) content.log_body = log.body
    if (log.attributes !== undefined) Object.assign(content, log.attributes)
    this.db.prepare('UPDATE records SET content_json = ?, session_id = COALESCE(session_id, ?) WHERE span_id = ?')
      .run(JSON.stringify(content), log.sessionId ?? null, spanId)
    return true
  }

  addPendingLog(log: { logId: string; pendingSince?: number }): void {
    this.db.prepare('INSERT OR REPLACE INTO pending_logs (log_id, payload) VALUES (?, ?)')
      .run(log.logId, JSON.stringify(log))
  }

  deletePendingLog(logId: string): void {
    this.db.prepare('DELETE FROM pending_logs WHERE log_id = ?').run(logId)
  }

  getPendingLogs(): unknown[] {
    return (this.db.prepare('SELECT payload FROM pending_logs ORDER BY log_id').all() as { payload: string }[])
      .map((row) => JSON.parse(row.payload))
  }

  consumePendingLogs(traceId: string, spanId: string, sessionId?: string | null): unknown[] {
    const pending = this.getPendingLogs() as Array<{ traceId?: string | null; spanId?: string | null; sessionId?: string | null }>
    const matched = pending.filter((log) =>
      ((log.traceId === traceId && log.spanId === spanId) || log.spanId === spanId) ||
      (sessionId !== null && sessionId !== undefined && log.sessionId === sessionId),
    )
    for (const log of matched) {
      this.db.prepare('DELETE FROM pending_logs WHERE log_id = ?').run((log as { logId: string }).logId)
    }
    return matched
  }

  quarantineLog(log: { logId: string }, reason: string): void {
    this.db.prepare('INSERT OR REPLACE INTO quarantined_logs (log_id, payload, reason) VALUES (?, ?, ?)')
      .run(log.logId, JSON.stringify(log), reason)
  }

  isLogEnriched(logId: string): boolean {
    return this.db.prepare('SELECT 1 FROM enriched_logs WHERE log_id = ?').get(logId) !== undefined
  }

  markLogEnriched(logId: string): void {
    this.db.prepare('INSERT OR IGNORE INTO enriched_logs (log_id) VALUES (?)').run(logId)
  }

  getQuarantinedLog(logId: string): QuarantinedLog | undefined {
    const row = this.db.prepare('SELECT payload, reason FROM quarantined_logs WHERE log_id = ?')
      .get(logId) as { payload: string; reason: string } | undefined
    return row === undefined ? undefined : { logId, reason: row.reason, log: JSON.parse(row.payload) }
  }

  quarantinedLogCount(): number {
    return (this.db.prepare('SELECT COUNT(*) AS n FROM quarantined_logs').get() as { n: number }).n
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

  /** Fetch one session by id; absent id gives undefined. */
  getSession(sessionId: string): SessionRow | undefined {
    const row = this.db
      .prepare('SELECT * FROM session WHERE session_id = ?')
      .get(sessionId) as SessionDbRow | undefined
    return row === undefined ? undefined : toSessionRow(row)
  }

  /** List derived sessions, optionally narrowed to one harness; newest first. */
  listSessions(harnessId?: string): SessionRow[] {
    const rows = (
      harnessId === undefined
        ? this.db.prepare('SELECT * FROM session ORDER BY started DESC').all()
        : this.db.prepare('SELECT * FROM session WHERE harness = ? ORDER BY started DESC').all(harnessId)
    ) as SessionDbRow[]
    return rows.map(toSessionRow)
  }

  /**
   * Store a derived or explicit run. Dropping and rebuilding runs loses nothing;
   * it is rebuildable over records.
   */
  upsertRun(run: RunRow): void {
    const payloadObj =
      run.outcome !== undefined
        ? {
            ...(run.payload !== null && typeof run.payload === 'object' && !Array.isArray(run.payload)
              ? (run.payload as Record<string, unknown>)
              : {}),
            outcome: run.outcome,
          }
        : run.payload

    this.db
      .prepare(
        `INSERT OR REPLACE INTO run (
           run_id, harness, label, grouping_basis, grouping_rule,
           working_directory, started, ended, execution_count, payload
         ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      )
      .run(
        run.runId,
        run.harness,
        run.label ?? null,
        run.groupingBasis,
        run.groupingRule ?? null,
        run.workingDirectory ?? null,
        run.started ?? null,
        run.ended ?? null,
        run.executionCount ?? 0,
        payloadObj === undefined || payloadObj === null ? null : JSON.stringify(payloadObj),
      )
  }

  upsertRuns(runs: readonly RunRow[]): void {
    if (runs.length === 0) return
    this.db.exec('BEGIN')
    try {
      for (const run of runs) this.upsertRun(run)
      this.db.exec('COMMIT')
    } catch (err) {
      this.db.exec('ROLLBACK')
      throw err
    }
  }

  /** Fetch one run by id; absent id gives undefined. */
  getRun(id: string): RunRow | undefined {
    const row = this.db
      .prepare('SELECT * FROM run WHERE run_id = ?')
      .get(id) as RunDbRow | undefined
    return row === undefined ? undefined : toRunRow(row)
  }

  /** List runs, optionally narrowed to one harness; newest first. */
  listRuns(harnessId?: string): RunRow[] {
    const rows = (
      harnessId === undefined
        ? this.db.prepare('SELECT * FROM run ORDER BY started DESC').all()
        : this.db.prepare('SELECT * FROM run WHERE harness = ? ORDER BY started DESC').all(harnessId)
    ) as RunDbRow[]
    return rows.map(toRunRow)
  }

  deleteRun(id: string): void {
    this.db.prepare('DELETE FROM run WHERE run_id = ?').run(id)
  }

  runCount(): number {
    return (this.db.prepare('SELECT COUNT(*) AS n FROM run').get() as { n: number }).n
  }

  builtRunIds(): string[] {
    return (this.db.prepare('SELECT run_id FROM run').all() as { run_id: string }[]).map((r) => r.run_id)
  }

  /**
   * Store one agent execution.
   */
  upsertExecution(execution: ExecutionRow): void {
    this.db
      .prepare(
        `INSERT OR REPLACE INTO execution (
           execution_id, run_id, session_id, parent_execution_id,
           harness, agent_name, is_root, started, ended, parent_linkage_json, payload
         ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      )
      .run(
        execution.executionId,
        execution.runId,
        execution.sessionId ?? null,
        execution.parentExecutionId ?? null,
        execution.harness,
        execution.agentName ?? null,
        execution.isRoot ? 1 : 0,
        execution.started ?? null,
        execution.ended ?? null,
        JSON.stringify(execution.parentLinkage),
        execution.payload === undefined ? null : JSON.stringify(execution.payload),
      )
  }

  upsertExecutions(executions: readonly ExecutionRow[]): void {
    if (executions.length === 0) return
    this.db.exec('BEGIN')
    try {
      for (const execution of executions) this.upsertExecution(execution)
      this.db.exec('COMMIT')
    } catch (err) {
      this.db.exec('ROLLBACK')
      throw err
    }
  }

  /** Fetch one execution by id; absent id gives undefined. */
  getExecution(id: string): ExecutionRow | undefined {
    const row = this.db
      .prepare('SELECT * FROM execution WHERE execution_id = ?')
      .get(id) as ExecutionDbRow | undefined
    return row === undefined ? undefined : toExecutionRow(row)
  }

  /** List executions, optionally filtered by run id; ordered by started. */
  listExecutions(runId?: string): ExecutionRow[] {
    const rows = (
      runId === undefined
        ? this.db.prepare('SELECT * FROM execution ORDER BY started, execution_id').all()
        : this.db.prepare('SELECT * FROM execution WHERE run_id = ? ORDER BY started, execution_id').all(runId)
    ) as ExecutionDbRow[]
    return rows.map(toExecutionRow)
  }

  deleteExecution(id: string): void {
    this.db.prepare('DELETE FROM execution WHERE execution_id = ?').run(id)
  }

  executionCount(): number {
    return (this.db.prepare('SELECT COUNT(*) AS n FROM execution').get() as { n: number }).n
  }

  builtExecutionIds(): string[] {
    return (this.db.prepare('SELECT execution_id FROM execution').all() as { execution_id: string }[]).map(
      (r) => r.execution_id,
    )
  }

  /**
   * Return the hierarchical execution tree for a given run id.
   * Root nodes are executions without a parent within the run; child executions nest
   * recursively under their parent's `children` array.
   */
  getExecutionTree(runId: string): ExecutionTreeNode[] {
    const executions = this.listExecutions(runId)
    if (executions.length === 0) return []

    const nodesById = new Map<string, ExecutionTreeNode>()
    for (const exec of executions) {
      nodesById.set(exec.executionId, { ...exec, children: [] })
    }

    const roots: ExecutionTreeNode[] = []
    for (const node of nodesById.values()) {
      if (node.parentExecutionId !== null && node.parentExecutionId !== undefined) {
        const parent = nodesById.get(node.parentExecutionId)
        if (parent !== undefined && parent !== node) {
          parent.children.push(node)
          continue
        }
      }
      roots.push(node)
    }

    return roots
  }

  /**
   * Distinct harnesses observed across records, sessions, runs, and executions.
   */
  listHarnesses(): string[] {
    const rows = this.db
      .prepare(
        `SELECT DISTINCT harness FROM (
           SELECT harness FROM records
           UNION
           SELECT harness FROM session
           UNION
           SELECT harness FROM run
           UNION
           SELECT harness FROM execution
         ) WHERE harness IS NOT NULL AND harness != ''
         ORDER BY harness ASC`,
      )
      .all() as { harness: string }[]
    return rows.map((r) => r.harness)
  }

  /**
   * Store a derived harness rollup row.
   */
  upsertHarnessRollup(rollup: HarnessRollupRow): void {
    this.db
      .prepare(
        `INSERT OR REPLACE INTO harness_rollup (
           harness, sample_count, context_pressure_median, context_pressure_p95,
           cache_hit_rate, tool_yield, delegation_overhead, field_coverage,
           measurability_json, payload
         ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      )
      .run(
        rollup.harness,
        rollup.sampleCount,
        rollup.contextPressureMedian ?? null,
        rollup.contextPressureP95 ?? null,
        rollup.cacheHitRate ?? null,
        rollup.toolYield ?? null,
        rollup.delegationOverhead ?? null,
        rollup.fieldCoverage ?? null,
        JSON.stringify(rollup.measurability),
        rollup.payload === undefined ? null : JSON.stringify(rollup.payload),
      )
  }

  upsertHarnessRollups(rollups: readonly HarnessRollupRow[]): void {
    if (rollups.length === 0) return
    this.db.exec('BEGIN')
    try {
      for (const rollup of rollups) this.upsertHarnessRollup(rollup)
      this.db.exec('COMMIT')
    } catch (err) {
      this.db.exec('ROLLBACK')
      throw err
    }
  }

  /** Fetch one harness rollup; absent harness gives undefined. */
  getHarnessRollup(harness: string): HarnessRollupRow | undefined {
    const row = this.db
      .prepare('SELECT * FROM harness_rollup WHERE harness = ?')
      .get(harness) as HarnessRollupDbRow | undefined
    return row === undefined ? undefined : toHarnessRollupRow(row)
  }

  /** List all harness rollups in ascending harness name order. */
  listHarnessRollups(): HarnessRollupRow[] {
    const rows = this.db
      .prepare('SELECT * FROM harness_rollup ORDER BY harness ASC')
      .all() as HarnessRollupDbRow[]
    return rows.map(toHarnessRollupRow)
  }

  deleteHarnessRollup(harness: string): void {
    this.db.prepare('DELETE FROM harness_rollup WHERE harness = ?').run(harness)
  }

  harnessRollupCount(): number {
    return (this.db.prepare('SELECT COUNT(*) AS n FROM harness_rollup').get() as { n: number }).n
  }

  /** Store or replace a diagnostic finding row (Decision D5). */
  upsertFinding(finding: Finding): void {
    const payload = finding.payload ?? {
      ...(finding.measurementClass ? { measurementClass: finding.measurementClass } : {}),
      ...(finding.confidenceBasis ? { confidenceBasis: finding.confidenceBasis } : {}),
      ...(finding.whatWouldRaiseIt ? { whatWouldRaiseIt: finding.whatWouldRaiseIt } : {}),
    }
    this.db
      .prepare(
        `INSERT OR REPLACE INTO finding (
           id, detector_id, title, mechanism, confidence,
           estimated_waste_tokens, recommendation, error_bar_json,
           evidence_links_json, outcome_risk_caveat, run_id, session_id,
           rank_score, payload
         ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      )
      .run(
        finding.id,
        finding.detectorId,
        finding.title,
        finding.mechanism,
        finding.confidence,
        finding.estimatedWasteTokens,
        finding.recommendation,
        JSON.stringify(finding.errorBar),
        JSON.stringify(finding.evidenceLinks),
        finding.outcomeRiskCaveat,
        finding.runId ?? null,
        finding.sessionId ?? null,
        finding.rankScore ?? 0.0,
        Object.keys(payload).length > 0 ? JSON.stringify(payload) : null,
      )
  }

  /** Batch upsert findings inside a single transaction. */
  upsertFindings(findings: readonly Finding[]): void {
    if (findings.length === 0) return
    this.db.exec('BEGIN')
    try {
      for (const finding of findings) this.upsertFinding(finding)
      this.db.exec('COMMIT')
    } catch (err) {
      this.db.exec('ROLLBACK')
      throw err
    }
  }

  /** Fetch one finding by id; absent id gives undefined. */
  getFinding(id: string): Finding | undefined {
    const row = this.db
      .prepare('SELECT * FROM finding WHERE id = ?')
      .get(id) as FindingDbRow | undefined
    return row === undefined ? undefined : toFinding(row)
  }

  /** List findings, optionally filtered by runId or sessionId; ordered by rank_score DESC. */
  listFindings(runId?: string, sessionId?: string): Finding[] {
    let query = 'SELECT * FROM finding'
    const params: string[] = []
    const conditions: string[] = []

    if (runId !== undefined && runId !== '') {
      conditions.push('run_id = ?')
      params.push(runId)
    }
    if (sessionId !== undefined && sessionId !== '') {
      conditions.push('session_id = ?')
      params.push(sessionId)
    }

    if (conditions.length > 0) {
      query += ` WHERE ${conditions.join(' AND ')}`
    }
    query += ' ORDER BY rank_score DESC, id ASC'

    const rows = this.db.prepare(query).all(...params) as FindingDbRow[]
    return rows.map(toFinding)
  }

  /** Delete one finding by id. */
  deleteFinding(id: string): void {
    this.db.prepare('DELETE FROM finding WHERE id = ?').run(id)
  }

  /** Count total findings in the store. */
  findingCount(): number {
    return (this.db.prepare('SELECT COUNT(*) AS n FROM finding').get() as { n: number }).n
  }

  /** Store or replace a prediction record (Task F4 / Decision D11). */
  upsertPrediction(prediction: PredictionRecord): void {
    const errorBar = prediction.errorBar ? JSON.stringify(prediction.errorBar) : null
    const payload = prediction.payload ? JSON.stringify(prediction.payload) : null
    const createdAt = prediction.createdAt || prediction.timestamp || new Date().toISOString()
    const id = prediction.id || `pred-${prediction.findingId}-${prediction.runId}`

    this.db
      .prepare(
        `INSERT OR REPLACE INTO prediction (
          id,
          finding_id,
          run_id,
          predicted_waste_tokens,
          confidence,
          confidence_tier,
          error_bar_json,
          created_at,
          comparison_run_id,
          observed_delta_tokens,
          calibration_score,
          payload
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
      )
      .run(
        id,
        prediction.findingId,
        prediction.runId,
        prediction.predictedWasteTokens,
        prediction.confidence,
        prediction.confidenceTier ?? null,
        errorBar,
        createdAt,
        prediction.comparisonRunId ?? null,
        prediction.observedDeltaTokens ?? null,
        prediction.calibrationScore ?? null,
        payload
      )
  }

  /** Batch upsert predictions inside a single transaction. */
  upsertPredictions(predictions: readonly PredictionRecord[]): void {
    if (predictions.length === 0) return
    this.db.exec('BEGIN')
    try {
      for (const prediction of predictions) this.upsertPrediction(prediction)
      this.db.exec('COMMIT')
    } catch (err) {
      this.db.exec('ROLLBACK')
      throw err
    }
  }

  /** Fetch one prediction by id; absent id gives undefined. */
  getPrediction(id: string): PredictionRecord | undefined {
    const row = this.db
      .prepare('SELECT * FROM prediction WHERE id = ?')
      .get(id) as unknown as PredictionDbRow | undefined
    return row === undefined ? undefined : toPrediction(row)
  }

  /** List predictions, optionally filtered by runId, findingId, or scoredOnly; ordered by created_at DESC. */
  listPredictions(options?: {
    runId?: string
    findingId?: string
    scoredOnly?: boolean
    limit?: number
  }): PredictionRecord[] {
    let query = 'SELECT * FROM prediction'
    const conditions: string[] = []
    const params: (string | number)[] = []

    if (options?.runId) {
      conditions.push('run_id = ?')
      params.push(options.runId)
    }
    if (options?.findingId) {
      conditions.push('finding_id = ?')
      params.push(options.findingId)
    }
    if (options?.scoredOnly) {
      conditions.push('comparison_run_id IS NOT NULL')
    }
    if (conditions.length > 0) {
      query += ` WHERE ${conditions.join(' AND ')}`
    }
    query += ' ORDER BY created_at DESC, id ASC'
    if (typeof options?.limit === 'number' && options.limit > 0) {
      query += ' LIMIT ?'
      params.push(Math.floor(options.limit))
    }

    const rows = this.db.prepare(query).all(...params) as unknown as PredictionDbRow[]
    return rows.map(toPrediction)
  }

  /** Delete one prediction by id. */
  deletePrediction(id: string): void {
    this.db.prepare('DELETE FROM prediction WHERE id = ?').run(id)
  }

  /** Count total predictions in the store. */
  predictionCount(): number {
    return (this.db.prepare('SELECT COUNT(*) AS n FROM prediction').get() as { n: number }).n
  }

  /**
   * Calculates aggregate calibration curve and summary metrics across predictions.
   */
  getCalibrationSummary(options?: { runId?: string }): CalibrationCurveResult {
    const predictions = this.listPredictions(options ? { runId: options.runId } : undefined)
    return calculateCalibrationCurve(predictions)
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

  /** Write or replace a metadata value. */
  setMetadata(key: string, value: string): void {
    this.db
      .prepare('INSERT OR REPLACE INTO metadata (key, value) VALUES (?, ?)')
      .run(key, value)
  }

  /** Read the currently stamped detector version in metadata (Decision D17). */
  getDetectorVersion(): number | undefined {
    const val = this.getMetadata('detector_version')
    return val === undefined ? undefined : Number(val)
  }

  /** Stamp a detector version into metadata (Decision D17). */
  setDetectorVersion(version: number = DETECTOR_VERSION): void {
    this.setMetadata('detector_version', String(version))
  }

  /**
   * Check whether the store's stamped detector version is outdated (Decision D17).
   * Returns true if missing or less than targetVersion, forcing automatic recomputation.
   */
  isDetectorOutdated(targetVersion: number = DETECTOR_VERSION): boolean {
    const stored = this.getDetectorVersion()
    return stored === undefined || stored < targetVersion
  }

  /** Returns true if the store's stamped detector version matches this build's DETECTOR_VERSION. */
  hasCurrentDetectorVersion(): boolean {
    return this.getDetectorVersion() === DETECTOR_VERSION
  }

  close(): void {
    this.db.close()
  }
}
