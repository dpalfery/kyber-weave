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

import { FILE_SOURCE_PREFIX, QUARANTINE_UNKNOWN_SOURCES, SessionIdentities, canonicalHarnessId, normalizeHarnessName, sourceSpellingsForHarness } from './measurability.js'
import { mkdirSync } from 'node:fs'
import { dirname } from 'node:path'
import { createRequire } from 'node:module'
const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}
import { deflateSync, inflateSync } from 'node:zlib'
import { setImmediate as scheduleImmediate } from 'node:timers'

import type { TokenCacheStore } from './tokens.js'
import {
  SOURCE_STATE_SQL,
  toRecordProvenance,
  toSourceCheckpoint,
  type RecordProvenance,
  type RecordProvenanceRow,
  type SourceCheckpoint,
  type SourceCheckpointRow,
} from './source-state.js'
import {
  REFRESH_MAX_AGE_MS,
  REFRESH_RUN_SQL,
  refreshProcessIsAlive,
  type RefreshRunRow,
  type RefreshRunStatus,
  type RefreshTrigger,
} from './refresh-run.js'
import { contentFromParts } from './types.js'
import { compressColumn, decompressColumn, type CompressedColumn } from './compressed-column.js'
import {
  SESSION_PAYLOAD_FRAGMENTS_SELECT,
  SESSION_PAYLOAD_SELECT,
  SESSION_TURNS_SELECT,
  parseJsonObject,
  sessionTurnsOf,
  turnObjectOf,
} from './session-read.js'

// The compressed-column codec moved to `./compressed-column.js` so the
// split-read contract (`./session-read.js`) can read a turn row without
// importing this module: the store imports the contract, so the contract
// cannot import the store without closing an import cycle.
//
// Re-exported here so the store's exported names do not change: the codec is
// still the store's to hand out, and an importer that resolved it from here
// keeps resolving it.
export { compressColumn, decompressColumn, type CompressedColumn } from './compressed-column.js'

/**
 * What a user-initiated wipe removed (issue #312). Counts only — the rows
 * are gone, so nothing more specific can be reported.
 */
export interface StoreWipeReport {
  /** Canonical harness ids the wipe covered (`['*']` for wipe-all). */
  harnesses: string[]
  /** Canonical records deleted. */
  records: number
  /** Provenance rows deleted. */
  provenance: number
  /** Checkpoint rows deleted. */
  checkpoints: number
}

/**
 * Delete every row a set of raw stored harness names owns — the canonical
 * harness's own spelling plus its folded front-end names (`claude-desktop`
 * under `claude-code`, `cursor-agent` under `cursor`). Covers records,
 * provenance, checkpoints, and the harness-scoped derived caches (`session`,
 * `run`, `execution`, `harness_rollup`, `problems`, and predictions via their
 * wiped run ids). `finding` has no harness column: the post-wipe projection
 * clears it authoritatively, so this function leaves it. `quarantine`,
 * `pending_logs`, and `quarantined_logs` carry the harness tag since schema
 * v19 (plan 2026-10-10, D-Q3) and go with the wipe; `enriched_logs` has no
 * attribution and stays for the complete wipe. Call inside the caller's
 * transaction, like `commitSourceUnit`'s inverse: a failure rolls back
 * everything, so records and checkpoints can never disagree about coverage.
 *
 * <remarks>
 * Why a free function and not methods: only `wipeHarnesses` and `wipeAll`
 * need these statements, and they are pure SQL over the handle the caller
 * owns — the class keeps the public names, this function keeps the deletes.
 * </remarks>
 */
function wipeHarnessRows(
  db: Database,
  rawNames: readonly string[],
): { records: number; provenance: number; checkpoints: number } {
  const placeholders = rawNames.map(() => '?').join(', ')
  const countChange = (sql: string): number => {
    const info = db.prepare(sql).run(...rawNames) as unknown as { changes?: unknown }
    return typeof info.changes === 'number' ? info.changes : Number(info.changes) || 0
  }
  const report = {
    records: countChange(`DELETE FROM records WHERE harness IN (${placeholders})`),
    provenance: countChange(
      `DELETE FROM record_provenance WHERE harness_id IN (${placeholders})`,
    ),
    checkpoints: countChange(
      `DELETE FROM source_checkpoint WHERE harness_id IN (${placeholders})`,
    ),
  }
  db.prepare(
    // The turn rows go with their sessions: the session rows are the only
    // thing that names them, so the subquery finds the turns before the
    // session rows go. The migration does not clean up after itself -- it
    // splits the rows it finds and leaves them -- so the wipe is where the
    // orphans of a split corpus are collected.
    `DELETE FROM session_turn WHERE session_id IN (SELECT session_id FROM session WHERE harness IN (${placeholders}))`,
  ).run(...rawNames)
  db.prepare(`DELETE FROM session WHERE harness IN (${placeholders})`).run(...rawNames)
  db.prepare(`DELETE FROM harness_rollup WHERE harness IN (${placeholders})`).run(...rawNames)
  db.prepare(`DELETE FROM problems WHERE harness IN (${placeholders})`).run(...rawNames)
  // Predictions join their run, so their run ids are collected BEFORE the
  // run rows go — after that the delete below would match nothing.
  const wipedRunIds = (
    db.prepare(`SELECT run_id FROM run WHERE harness IN (${placeholders})`).all(...rawNames) as {
      run_id: unknown
    }[]
  )
    .map((row) => row.run_id)
    .filter((id): id is string => typeof id === 'string' && id !== '')
  db.prepare(`DELETE FROM run WHERE harness IN (${placeholders})`).run(...rawNames)
  db.prepare(`DELETE FROM execution WHERE harness IN (${placeholders})`).run(...rawNames)
  if (wipedRunIds.length > 0) {
    const runPlaceholders = wipedRunIds.map(() => '?').join(', ')
    db.prepare(`DELETE FROM prediction WHERE run_id IN (${runPlaceholders})`).run(...wipedRunIds)
  }
  // The audit rows follow the harness tag T14 stamped on them — but that
  // scoping is only as good as the attribution on the row: NULL reads as
  // "unknown", never "matches", so unclaimed rows survive here and only the
  // complete `wipeAll` clears them.
  db.prepare(`DELETE FROM quarantine WHERE harness IN (${placeholders})`).run(...rawNames)
  db.prepare(`DELETE FROM pending_logs WHERE harness IN (${placeholders})`).run(...rawNames)
  db.prepare(`DELETE FROM quarantined_logs WHERE harness IN (${placeholders})`).run(...rawNames)
  return report
}

type StatementSync = import('node:sqlite').StatementSync
type Database = import('node:sqlite').DatabaseSync

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
export const SCHEMA_VERSION = 22

/**
 * The refusal a store written by a newer KyberDash earns.
 *
 * WHY it is one function and not a literal at each call site: the store
 * refuses to open such a store, and every tool that reads a store *without*
 * opening it for writing has to refuse it the same way — the vacuum's dry
 * run reads the store's page counters and cannot open it for writing. Two
 * literals would drift, and a drifted refusal is a refusal nobody recognises
 * when the engine's own refresh reports one.
 */
export function schemaVersionRefusal(path: string, from: number, to: number = SCHEMA_VERSION): string {
  return (
    `canon store at ${path} was built with schema version ${from}, ` +
    `but this build understands version ${to}; upgrade KyberDash`
  )
}

/**
 * How much of the write-ahead log the store keeps on disk once a checkpoint
 * has reset it. SQLite's default (-1) never shrinks the WAL file, so it stays
 * at whatever high-water mark a burst reached: after one folder import across
 * 41 providers the sidecar sat at 145 MB beside a 442 MB database and stayed
 * there. 32 MiB lets a burst ride without a checkpoint while bounding what a
 * quiet store keeps.
 */
export const WAL_SIZE_LIMIT_BYTES = 32 * 1024 * 1024

/**
 * Version of the diagnostic signal and finding detector suite (Decision D17).
 * An informational stamp identifying which detector semantics built a store's
 * derived rows, so tooling and operators can tell a stale finding table from
 * a fresh one. It does not itself trigger recomputation: every build
 * (`buildSessions`, `buildRuns`, `buildFindings`, `buildHarnessRollup`) is
 * authoritative and rewrites its derived rows, pruning what detectors no
 * longer emit.
 *
 * Generations: 2 stopped fabricating duplicate-tool-call waste for
 * underivable sizes (coverage-gap findings carry no estimate); 3 adds honest
 * compaction windows, folded twin-harness identity, and rebuilt findings
 * (issues #181/#182/#191). The stamp is written when the store is created;
 * a store file that predates a generation keeps its old stamp until its
 * derived tables are rebuilt. Bump it whenever detector semantics change
 * and say so in the PR, so the stamp stays a truthful witness instead of a
 * forgotten counter. 4 reads skill listings and invocations from canon/skills.ts
 * (Skill tool calls and the invoked key) instead of prompt-text regexes and tool names.
 * 5 derives unmeasurable context buckets (a part never observed is absent, not zero) and
 * carries observed-parts evidence (OpenCode capture T6/T8). Both change derived output
 * without moving the corpus, so a store stamped 4 must re-project (D13).
 */
export const DETECTOR_VERSION = 5

/**
 * Metadata key: the records-write marker.
 *
 * WHY it exists: the derived tables (`session`, `run`, `execution`, `finding`)
 * are a cache over `records`, and rebuilding them over a real corpus costs
 * ~26 s of CPU. A pass that rebuilds on a timer burns that CPU whether or not
 * anything changed. The marker is the corpus's change signal: every write to
 * `records` bumps it, so a pass can ask "did the corpus move since the last
 * rebuild?" in one metadata read instead of rebuilding to find out.
 *
 * It is a counter rather than a timestamp because the only question asked is
 * "same or different", and a counter cannot go backwards when two writers
 * race.
 */
export const RECORDS_WRITE_MARKER_KEY = 'records_write_seq'

/**
 * The whole schema, as code. `CREATE ... IF NOT EXISTS` throughout so
 * constructing against an existing store is a no-op and a fresh clone builds
 * the empty store on first use. The tokenization cache table is created here
 * because the schema is versioned as one unit; its accessors arrive with the
 * tokenizer (task 3.3).
 *
 * The turn table is the one exception, and it is not an oversight: it is
 * materialized where turns are written and read -- by the migration that
 * backfills them, by the session write path, and by the raw-handle accessor a
 * consumer queries directly. A store that has never held a turn row does not
 * carry the table, so a store that predates it stays the store it was instead
 * of growing a table from the build that opens it.
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

/**
 * The turns of a session, one row per turn.
 *
 * A session row used to carry its whole session in a single `payload` column
 * -- 15.3 MB for one session of 10,933 turns, 79.6 MB across the 336-session
 * corpus -- so reading one session's turns meant reading the whole blob. The
 * turns live here instead, one row each, and the session row keeps the rest
 * of the session.
 *
 * `turn_index` is the turn's own index, copied verbatim from the payload: the
 * projection writes a 0-based `index` on every turn, so copying that value
 * settles the base question without a convention of our own. The index on
 * `(session_id, turn_index)` is the reader's path -- one session's turns, in
 * order -- and is not unique on purpose: a legacy payload may repeat an
 * index, and the migration replaces a session's rows wholesale rather than
 * dropping a turn whose index it has already stored.
 *
 * `turn_json` holds the turn's deflated JSON -- the same `deflateSync` form
 * `records.parts_json` holds, and the same reason: the turn rows are the
 * split's bulk, and 40,621 of them uncompressed would give the split's
 * storage win straight back. The column's declared type is left as it was
 * when the v22 migration created the table, because the schema and the
 * migration are not this change's to make: SQLite's dynamic typing keeps the
 * deflated bytes a BLOB value in a TEXT column all the same, and the readers
 * tell the two forms apart by the storage class the value comes back with.
 * A row written before the compression landed holds the plain JSON, and the
 * readers accept that form too.
 */
export const SESSION_TURN_TABLE_SQL = `
CREATE TABLE IF NOT EXISTS session_turn (
  session_id TEXT NOT NULL,
  turn_index INTEGER NOT NULL,
  turn_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS session_turn_by_session ON session_turn (session_id, turn_index);
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
-- Sessions are keyed by COALESCE(session_id, trace_id), and neither
-- single-column index above can serve that expression: SQLite scanned all of
-- records once per session key, which on the measured corpus was ~1,900 scans
-- of an 85,600-row, 3.3 GB table per rebuild -- very nearly the whole of the
-- rebuild's ~30 minutes. Indexing the expression itself makes the load a
-- seek, and carrying timestamp as the second column lets the same index
-- satisfy ORDER BY timestamp without a sort.
CREATE INDEX IF NOT EXISTS records_by_session_key
  ON records (COALESCE(session_id, trace_id), timestamp);
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
  synced_at TEXT,
  sync_status TEXT DEFAULT 'pending',
  settled_at TEXT,
  transcript_path TEXT,
  transcript_offset INTEGER DEFAULT 0,
  -- Materialized summary of the payload: the serialized $.summary object
  -- and the json_array_length($.problems) of the payload's problem list.
  -- Both are nullable and carry no DEFAULT on purpose: NULL is the legacy
  -- sentinel meaning "not materialized", so a reader knows to derive the
  -- value from the payload instead of mistaking it for an empty summary or
  -- a zero problem count.
  summary_json TEXT,
  problems_count INTEGER,
  -- The context window's provenance, materialized from the payload's
  -- $.context.contextLimitSource: the corpus exposes that value only
  -- inside the payload blob today, so counting the unknown-window sessions
  -- paged every payload in the table. Nullable with no DEFAULT on purpose:
  -- NULL is the "not materialized" sentinel, so a reader knows to derive the
  -- value from the payload instead of mistaking it for a recorded source.
  context_limit_source TEXT,
  payload TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS session_by_started ON session (started);
-- The covering index for the sessions list is not here: it lives in
-- createSessionDenormIndex, because it cannot be unconditional DDL in this
-- constant. A store that predates the current session shape carries its own
-- older session table, and an index over columns that table does not have
-- cannot be created at all -- the store would fail to open rather than
-- migrate. The constructor runs it right after this schema, and the v20 ->
-- v21 migration runs it inside its own step, so both creation paths have it.

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
  reason TEXT NOT NULL,
  source TEXT,
  name TEXT,
  timestamp TEXT,
  -- The sender's decoded packet, kept as the evidence the detail route
  -- serves, and the harness attribution named for the row (B7/B10).
  raw_payload TEXT,
  harness TEXT
);
CREATE TABLE IF NOT EXISTS pending_logs (
  log_id TEXT PRIMARY KEY,
  payload TEXT NOT NULL,
  harness TEXT
);
CREATE TABLE IF NOT EXISTS quarantined_logs (
  log_id TEXT PRIMARY KEY,
  payload TEXT NOT NULL,
  reason TEXT NOT NULL,
  harness TEXT
);
CREATE TABLE IF NOT EXISTS enriched_logs (
  log_id TEXT PRIMARY KEY
);
CREATE TABLE IF NOT EXISTS problems (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  span_id TEXT,
  problem_key TEXT NOT NULL DEFAULT '',
  severity TEXT NOT NULL,
  code TEXT NOT NULL,
  message TEXT NOT NULL,
  location TEXT,
  session_id TEXT,
  harness TEXT,
  timestamp TEXT,
  stack TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS problems_by_identity ON problems (problem_key);
CREATE TABLE IF NOT EXISTS ingest_log (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  source TEXT NOT NULL,
  count INTEGER NOT NULL,
  timestamp TEXT NOT NULL
);
-- The coverage and meta seams read this table only through GROUP BY source
-- (sums plus MAX(timestamp)); the index keeps those scans off a full sort as
-- the audit log grows without a retention policy.
CREATE INDEX IF NOT EXISTS ingest_log_by_source ON ingest_log (source);
CREATE INDEX IF NOT EXISTS ingest_log_by_timestamp ON ingest_log (timestamp);
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
  -- Nullable since v15: an unmeasured (coverage-gap) finding persists NULL,
  -- which reads back as an absent estimate rather than zero waste.
  estimated_waste_tokens INTEGER,
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
CREATE INDEX IF NOT EXISTS finding_by_detector ON finding (detector_id);
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
-- Vendor-documented model windows (D15). A new table, not a new column on
-- an existing one, so CREATE IF NOT EXISTS on every open covers a store
-- already stamped at SCHEMA_VERSION. Landing it cost no version bump — the
-- projection contract still required the stamp of the day — and an
-- IF NOT EXISTS table needs no migration step to appear.
CREATE TABLE IF NOT EXISTS model_context_window_catalog (
  lookup_id TEXT PRIMARY KEY,
  canonical_model_id TEXT NOT NULL,
  context_window INTEGER NOT NULL CHECK (context_window > 0),
  vendor TEXT NOT NULL,
  documentation_url TEXT NOT NULL,
  retrieved_at TEXT NOT NULL,
  source_revision TEXT
);
CREATE INDEX IF NOT EXISTS model_context_window_catalog_by_vendor
  ON model_context_window_catalog (vendor);
-- Per-vendor refresh outcome, separate from the rows so a failed refresh can
-- record its error without deleting the last rows that validated.
CREATE TABLE IF NOT EXISTS model_context_window_catalog_vendor (
  vendor TEXT PRIMARY KEY,
  status TEXT NOT NULL,
  last_refresh_at TEXT,
  last_error TEXT
);
` + SOURCE_STATE_SQL + REFRESH_RUN_SQL

/**
 * The columns the sessions-list covering index carries, in order.
 *
 * `started` leads, descending, so the scan answers the reader's
 * `ORDER BY started DESC` with no sort step. The rest follow the reader's
 * select list in reader order, so the index alone answers the whole list
 * query.
 *
 * The DDL below names these columns in this order; the guard in
 * `createSessionDenormIndex` needs them by name, to decide whether a store's
 * session table can carry the index at all.
 */
const SESSION_DENORM_INDEX_COLUMNS = [
  'started',
  'session_id',
  'harness',
  'label',
  'is_subagent',
  'parent_session',
  'agent_name',
  'repo',
  'branch',
  'ended',
  'summary_json',
  'problems_count',
] as const

/**
 * The covering index for the sessions list, as DDL.
 *
 * <remarks>
 * The index is what makes the denormalization worth anything. `ALTER TABLE`
 * appends the materialized columns after `payload`, so a table-row read for
 * `summary_json` still walks the payload's overflow chain: on the measured
 * corpus the denormalization alone moved the cost rather than removing it.
 * An index carrying the reader's whole select list answers the list query
 * from the index alone.
 *
 * The rowid every SQLite index carries keeps the entries unique, so the
 * leading `started DESC` needs no tiebreaker of its own.
 */
const SESSION_DENORM_INDEX_SQL = `CREATE INDEX IF NOT EXISTS session_denorm_idx
  ON session (started DESC, session_id, harness, label, is_subagent,
              parent_session, agent_name, repo, branch, ended,
              summary_json, problems_count)`

/**
 * Create the covering index for the sessions list, when the session table
 * can carry it.
 *
 * <remarks>
 * Two creation paths, one DDL: the v20 -> v21 migration runs it inside its
 * own step, so the index lands with the columns it covers and with the
 * version stamp; the constructor runs it after the schema, so a fresh store
 * is born with it. `IF NOT EXISTS` makes both idempotent, so the
 * constructor's run on every open is a no-op once the index exists.
 *
 * The session table's shape is not the current schema's for every store: a
 * store that predates it carries its own older table, and an index over
 * columns that table does not have cannot be created at all. Such a store
 * gains nothing from the index — the reader's own statement names the same
 * columns, so it cannot read that table either — and failing the open over
 * it would be worse than leaving it without the index. So the creation is
 * skipped, and the store opens and migrates as it always did.
 *
 * @returns Whether this call created the index. A caller that needs the
 * planner's statistics for it uses this: SQLite writes one statistics row per
 * analyzed index, so an index created after the last analysis carries none,
 * and the planner then keeps choosing a different index for the sessions
 * list.
 */
function createSessionDenormIndex(db: Database): boolean {
  const hasSession = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='session'").get()
  if (!hasSession) return false
  const cols = (db.prepare('PRAGMA table_info(session)').all() as { name: string }[]).map((c) => c.name)
  if (!SESSION_DENORM_INDEX_COLUMNS.every((column) => cols.includes(column))) return false
  const existing = db
    .prepare("SELECT name FROM sqlite_master WHERE type='index' AND name='session_denorm_idx'")
    .get()
  if (existing !== undefined) return false
  db.exec(SESSION_DENORM_INDEX_SQL)
  return true
}

/** Map a `refresh_run` row out of SQLite's column names. */
function toRefreshRunRow(row: Record<string, unknown>): RefreshRunRow {
  return {
    id: String(row['id']),
    startedAt: String(row['started_at']),
    completedAt: row['completed_at'] === null ? null : String(row['completed_at']),
    status: String(row['status']) as RefreshRunStatus,
    pid: Number(row['pid']),
    trigger: String(row['trigger']) as RefreshTrigger,
    summary: row['summary'] === null ? null : String(row['summary']),
    // Rows written before migration 15→16 carry NULL (or no column at all
    // on a store that has not migrated): the window is unknown, never 0.
    historyWeeks: normalizeHistoryWeeks(row['history_weeks']),
  }
}

/**
 * Coerce a persisted coverage window to the contract every surface reads
 * (`historyWeeks?: number | null` — null means unknown, never 0).
 *
 * The persistence seam is what every surface reads, so `absent is not zero`
 * holds here, not just at the CLI parser: only a positive safe integer
 * survives; 0, negatives, fractions, NaN, Infinity, and non-numeric junk all
 * read as unknown. Shared by the store mapper and the file-backed bridge
 * mapper so both halves of the seam agree.
 */
export function normalizeHistoryWeeks(value: unknown): number | null {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value <= 0) return null
  return value
}

/**
 * Normalize an ingest audit-log key at the persistence seam. `service.name`
 * arrives over the network, so blank values fold into the shared unnamed
 * key and over-long values are capped — one more bound on a table with no
 * retention policy. Never a liveness claim: callers still decide what an
 * empty log means.
 */
export function normalizeIngestSource(source: string): string {
  const trimmed = source.trim()
  if (trimmed === '') return 'otlp'
  return trimmed.length > 256 ? trimmed.slice(0, 256) : trimmed
}

/**
 * How a migration hands the event loop back between sessions.
 *
 * <remarks>
 * The seam is injected rather than hard-wired so a test can count the yields:
 * the count is the contract, not a timing threshold.
 */
export type MigrationYield = () => Promise<void>

/**
 * The options a migration step takes.
 *
 * <remarks>
 * A step given no `yield` runs synchronously — the shape the store's
 * constructor needs, where an open is one blocking pass. A step given a
 * `yield` may hand the event loop back between sessions, and its caller
 * awaits it.
 */
export type MigrationOptions = {
  /**
   * Called between sessions to hand the event loop back.
   *
   * <remarks>
   * The seam is called inside the step's transaction. That is safe because
   * the transaction and its write lock live on the SQLite connection, not on
   * the JavaScript stack: an await between two statements neither commits
   * nor rolls back, and the connection keeps the write lock for the whole
   * transaction. The connection is the migration's own — the caller owns it
   * and hands it to nobody else — so no other writer can join the
   * transaction through it, and another connection cannot write at all
   * while the lock is held.
   */
  yield?: MigrationYield
}

/**
 * In-place upgrades, keyed by the version they upgrade FROM. Each runs inside
 * one transaction and leaves the store at `key + 1`. `SCHEMA_SQL` cannot do
 * this work: every statement in it is `IF NOT EXISTS`, so an existing table
 * never gains a column.
 *
 * A step returns a promise only when it is given a yield seam; the
 * synchronous open path passes none, and every step then runs to completion
 * before the transaction is committed.
 */
export const MIGRATIONS: Record<
  number,
  (db: Database, options?: MigrationOptions) => void | Promise<void>
> = {
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
  // v9 -> v10: index the session key expression. `records_by_session` and
  // `records_by_trace` cover their columns individually, but every derived
  // table loads records by COALESCE(session_id, trace_id), which neither can
  // serve, so each load scanned the whole table. An existing store gets the
  // index here rather than waiting for a rebuild from empty.
  9: (db) => {
    db.exec(`CREATE INDEX IF NOT EXISTS records_by_session_key
      ON records (COALESCE(session_id, trace_id), timestamp);`)
  },
  // v10 -> v11: harness-source checkpoints and per-span provenance. Additive
  // only — existing records.raw and derived history are left untouched.
  10: (db) => {
    db.exec(SOURCE_STATE_SQL)
  },
  // v11 -> v12: the refresh run log. Before it, the only record of a refresh was whether
  // the store had changed, which cannot distinguish "not refreshed lately" from
  // "refreshed and failed" — and R10.5 needs the last success to stay visible while a
  // later run is failing. Existing stores gain an empty table; the first refresh after
  // the upgrade writes the first row.
  11: (db) => {
    db.exec(REFRESH_RUN_SQL)
  },
  // v12 -> v13: collapse repeated diagnostics by their stable (span, code, location)
  // identity. Older rows have no key, so keep the newest row for each identity
  // before enforcing uniqueness. Rows with no span identity remain distinct.
  12: (db) => {
    const table = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='problems'").get()
    if (!table) return

    const columns = db.prepare('PRAGMA table_info(problems)').all() as { name: string }[]
    if (!columns.some((column) => column.name === 'problem_key')) {
      db.exec("ALTER TABLE problems ADD COLUMN problem_key TEXT NOT NULL DEFAULT ''")
    }

    rekeyProblems(db)
    db.exec('CREATE UNIQUE INDEX IF NOT EXISTS problems_by_identity ON problems (problem_key)')
  },
  // v13 -> v14: existing stores used a span/code key, so rekey their surviving
  // rows before a new diagnostic at a different location can be recorded.
  13: (db) => rekeyProblems(db),
  // v14 -> v15: unmeasured (coverage-gap) findings persist NULL waste instead
  // of a fabricated number. SQLite cannot drop NOT NULL in place, so rebuild
  // the table; every row (including its 0-valued measured estimates) copies
  // across unchanged, and fresh stores take the nullable shape from SCHEMA_SQL.
  14: (db) => {
    const table = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='finding'").get()
    if (!table) return

    db.exec(`CREATE TABLE finding_new (
      id TEXT PRIMARY KEY,
      detector_id TEXT NOT NULL,
      title TEXT NOT NULL,
      mechanism TEXT NOT NULL,
      confidence TEXT NOT NULL,
      estimated_waste_tokens INTEGER,
      recommendation TEXT NOT NULL,
      error_bar_json TEXT NOT NULL,
      evidence_links_json TEXT NOT NULL,
      outcome_risk_caveat TEXT NOT NULL,
      run_id TEXT,
      session_id TEXT,
      rank_score REAL NOT NULL DEFAULT 0.0,
      payload TEXT
    );
    INSERT INTO finding_new
      (id, detector_id, title, mechanism, confidence, estimated_waste_tokens,
       recommendation, error_bar_json, evidence_links_json, outcome_risk_caveat,
       run_id, session_id, rank_score, payload)
      SELECT id, detector_id, title, mechanism, confidence, estimated_waste_tokens,
       recommendation, error_bar_json, evidence_links_json, outcome_risk_caveat,
       run_id, session_id, rank_score, payload FROM finding;
    DROP TABLE finding;
    ALTER TABLE finding_new RENAME TO finding;
    CREATE INDEX IF NOT EXISTS finding_by_run ON finding (run_id);
    CREATE INDEX IF NOT EXISTS finding_by_session ON finding (session_id);
    CREATE INDEX IF NOT EXISTS finding_by_rank_score ON finding (rank_score DESC);`)
  },
  // v15 -> v16: persist the ingest coverage window per refresh run
  // (issues #189/#198/#199 plan, T1). The window is what discards most
  // history, and no surface can state it while `refresh_run` carries no
  // column for it. Existing rows gain a NULL column — window unknown, which
  // readers state as such rather than as 0 or the current default.
  // Renumbered from 14 after main landed the nullable-finding-waste 14→15
  // step on the same base; main's step stays 14 so a v14 store migrates
  // through both in order.
  15: (db) => {
    // Like migration 12's problems guard: a v15-stamped store with no
    // refresh_run table (partial install, hand-built fixture) skips the
    // ALTER rather than throwing a raw driver error.
    const table = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='refresh_run'").get()
    if (!table) return
    const columns = db.prepare('PRAGMA table_info(refresh_run)').all() as { name: string }[]
    if (!columns.some((column) => column.name === 'history_weeks')) {
      db.exec('ALTER TABLE refresh_run ADD COLUMN history_weeks INTEGER')
    }
  },
  // v16 -> v17: quarantine and problems metadata columns.
  // quarantine gains source, name, timestamp.
  // problems gains session_id, harness, timestamp.
  16: (db) => {
    const hasQuarantine = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='quarantine'").get()
    if (hasQuarantine) {
      const qCols = (db.prepare('PRAGMA table_info(quarantine)').all() as { name: string }[]).map((c) => c.name)
      if (!qCols.includes('source')) {
        db.exec('ALTER TABLE quarantine ADD COLUMN source TEXT')
      }
      if (!qCols.includes('name')) {
        db.exec('ALTER TABLE quarantine ADD COLUMN name TEXT')
      }
      if (!qCols.includes('timestamp')) {
        db.exec('ALTER TABLE quarantine ADD COLUMN timestamp TEXT')
      }
    }

    const hasProblems = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='problems'").get()
    if (hasProblems) {
      const pCols = (db.prepare('PRAGMA table_info(problems)').all() as { name: string }[]).map((c) => c.name)
      if (!pCols.includes('session_id')) {
        db.exec('ALTER TABLE problems ADD COLUMN session_id TEXT')
      }
      if (!pCols.includes('harness')) {
        db.exec('ALTER TABLE problems ADD COLUMN harness TEXT')
      }
      if (!pCols.includes('timestamp')) {
        db.exec('ALTER TABLE problems ADD COLUMN timestamp TEXT')
      }
    }
  },
  // v17 -> v18: incoming-evidence columns. quarantine gains raw_payload —
  // the sender's decoded packet, the evidence the detail route serves (plan
  // 2026-10-10, B7/B10): a quarantined span is held out of the corpus, and
  // for a Claude interaction span this packet is the only surviving copy of
  // the prompt. problems gains stack (the exception stack when a problem came
  // from a throw, null otherwise). Both nullable so existing rows keep
  // reading as "no stored evidence" rather than being backfilled with a
  // guess — backfilling would invent a message or stack those rows never
  // carried. Every ADD COLUMN is presence-guarded for F9: a live canon.db
  // can be stamped 18 by an off-main build that already added `raw_payload`,
  // and an unguarded ALTER would die on "duplicate column" against exactly
  // the store whose packets must survive.
  17: (db) => {
    const hasQuarantine = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='quarantine'").get()
    if (hasQuarantine) {
      const qCols = (db.prepare('PRAGMA table_info(quarantine)').all() as { name: string }[]).map((c) => c.name)
      if (!qCols.includes('raw_payload')) {
        db.exec('ALTER TABLE quarantine ADD COLUMN raw_payload TEXT')
      }
    }
    const hasProblems = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='problems'").get()
    if (hasProblems) {
      const pCols = (db.prepare('PRAGMA table_info(problems)').all() as { name: string }[]).map((c) => c.name)
      if (!pCols.includes('stack')) {
        db.exec('ALTER TABLE problems ADD COLUMN stack TEXT')
      }
    }
  },
  // v18 -> v19: the harness a quarantined span or held log row is scoped to,
  // so a per-harness wipe can key its audit rows rather than scanning
  // payloads. `quarantine` gains the column beside `raw_payload`; the two
  // log tables gain it for the same scoping. "Stamped 18" became ambiguous
  // once off-main builds (raw_payload, no stack) and main (raw_payload plus
  // stack) disagreed about what 18 means, so this step presence-guards every
  // column the v18 shape promises, not only its own: each column is added
  // only when missing, and a table missing entirely is skipped, not altered —
  // every stamped-18 store must reach the identical final shape.
  18: (db) => {
    for (const table of ['quarantine', 'pending_logs', 'quarantined_logs'] as const) {
      const hasTable = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name=?").get(table)
      if (!hasTable) continue
      const cols = (db.prepare(`PRAGMA table_info(${table})`).all() as { name: string }[]).map((c) => c.name)
      if (!cols.includes('harness')) {
        db.exec(`ALTER TABLE ${table} ADD COLUMN harness TEXT`)
      }
    }
    const hasProblems = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='problems'").get()
    if (hasProblems) {
      const pCols = (db.prepare('PRAGMA table_info(problems)').all() as { name: string }[]).map((c) => c.name)
      if (!pCols.includes('stack')) {
        db.exec('ALTER TABLE problems ADD COLUMN stack TEXT')
      }
    }
  },
  // v19 -> v20: session sync progress and settling columns.
  // Adds synced_at, sync_status, settled_at, transcript_path, transcript_offset.
  // Each ADD COLUMN is presence-guarded so re-running migration 19 is idempotent
  // and stores with pre-existing columns or missing tables don't throw.
  19: (db: Database) => {
    const hasSession = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='session'").get()
    if (!hasSession) return
    const cols = (db.prepare('PRAGMA table_info(session)').all() as { name: string }[]).map((c) => c.name)
    if (!cols.includes('synced_at')) {
      db.exec('ALTER TABLE session ADD COLUMN synced_at TEXT')
    }
    if (!cols.includes('sync_status')) {
      db.exec("ALTER TABLE session ADD COLUMN sync_status TEXT DEFAULT 'pending'")
    }
    if (!cols.includes('settled_at')) {
      db.exec('ALTER TABLE session ADD COLUMN settled_at TEXT')
    }
    if (!cols.includes('transcript_path')) {
      db.exec('ALTER TABLE session ADD COLUMN transcript_path TEXT')
    }
    if (!cols.includes('transcript_offset')) {
      db.exec('ALTER TABLE session ADD COLUMN transcript_offset INTEGER DEFAULT 0')
    }
  },
  // v20 -> v21: materialized session summary columns.
  // `summary_json` holds the serialized `$.summary` object of the session
  // payload and `problems_count` the payload's `json_array_length($.problems)`,
  // so a reader can serve them without reparsing the payload.
  // Neither column carries a DEFAULT: NULL is the legacy sentinel meaning
  // "not materialized" — a row that predates the columns must be derived from
  // its payload, never read as an empty summary or a zero problem count. A
  // DEFAULT would erase exactly that distinction.
  // Each ADD COLUMN is presence-guarded, as migration 19's are: re-running the
  // step, or opening a store an off-main build already stamped 21, must not
  // die on "duplicate column name".
  20: (db: Database) => {
    const hasSession = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='session'").get()
    if (!hasSession) return
    const cols = (db.prepare('PRAGMA table_info(session)').all() as { name: string }[]).map((c) => c.name)
    if (!cols.includes('summary_json')) {
      db.exec('ALTER TABLE session ADD COLUMN summary_json TEXT')
    }
    if (!cols.includes('problems_count')) {
      db.exec('ALTER TABLE session ADD COLUMN problems_count INTEGER')
    }
    // The covering index for the sessions list, created in this step, right
    // after the columns it covers: the constructor runs the migration first
    // -- the columns have to exist before an index can cover them -- and the
    // schema second, so the index has to be created here to land with the
    // columns and the version stamp, in one step.
    createSessionDenormIndex(db)
    // The planner's cost information for `session`: real statistics for the
    // covering index and the table as the corpus grows, and the same plan on
    // every path — which is what the contract's EXPLAIN pin asserts. The
    // covering index above is the mechanism, not these statistics: on this
    // build the planner already picks it with no `sqlite_stat1` rows at all.
    //
    // They are written here, inside the step's transaction, rather than at
    // connection open: the reader opens the store read-only and a read-only
    // connection cannot write `sqlite_stat1`.
    db.exec('ANALYZE session')
  },
  // v21 -> v22: the session's turns leave the payload blob.
  //
  // A session row carried its whole session in one `payload` column -- 15.3 MB
  // for a single session of 10,933 turns -- so reading one session's turns
  // meant reading the whole blob. The turns move to their own `session_turn`
  // table, one row per turn, and the payload keeps the rest of the session.
  // The context window's provenance moves to a column as well, so the count of
  // unknown-window sessions stops paging every payload blob in the table.
  //
  // The v21 step added `summary_json` and `problems_count` and backfilled
  // neither: the corpus was left unmaterialized, and the reader falls back to
  // the payload whenever those columns are NULL. This step is the one pass
  // that already walks every session row, so it materializes them as it goes
  // -- with the writer's own derivation, so a migrated row and a written row
  // carry the same values. A store that migrates to v22 therefore comes out
  // the other side materialized.
  //
  // The table and the column are presence-guarded, as migration 19's and 20's
  // are: re-running the step, or opening a store an off-main build already
  // stamped 22, must not die on "table already exists" or "duplicate column
  // name", and must not add a second row for a turn it already stored.
  //
  // The step runs synchronously when it is given no yield seam — the
  // constructor's open is one blocking pass — and yields between sessions
  // when it is given one, so an opening server can answer requests while the
  // corpus is upgraded. Both paths do the same work and commit the same
  // transaction.
  21: (db: Database, options?: MigrationOptions): void | Promise<void> => {
    db.exec(SESSION_TURN_TABLE_SQL)
    const hasSession = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='session'").get()
    if (!hasSession) return
    const cols = (db.prepare('PRAGMA table_info(session)').all() as { name: string }[]).map((c) => c.name)
    if (!cols.includes('context_limit_source')) {
      db.exec('ALTER TABLE session ADD COLUMN context_limit_source TEXT')
    }
    if (options?.yield === undefined) {
      backfillSessionTurns(db)
      finishSessionStatistics(db)
      return
    }
    return runYieldingSessionTurnBackfill(db, options.yield)
  },
}

/**
 * The v21 -> v22 step as the yielding open path runs it: the same backfill,
 * handing the event loop back between sessions, and the same statistics at
 * the end.
 */
async function runYieldingSessionTurnBackfill(db: Database, yieldToLoop: MigrationYield): Promise<void> {
  await backfillSessionTurnsYielding(db, yieldToLoop)
  finishSessionStatistics(db)
}

/**
 * The migration's last act: the covering index for the sessions list, and the
 * planner's statistics for `session`.
 *
 * <remarks>
 * This mirrors MIGRATIONS[20] exactly, and the order is the point: the index
 * is created first, because a statistics row can only exist for an index that
 * exists, and `ANALYZE session` writes one row per analyzed index.
 *
 * WHY the migration writes them, rather than leaving it to the close-time
 * `PRAGMA optimize`: the deployed store is held open by the engine, so that
 * optimization never runs while the dashboard serves. A store that is already
 * at v21 runs neither MIGRATIONS[20] — which the v21 -> v22 path skips — nor
 * the constructor's new-store branch, so without this the covering index is
 * left with no statistics at all, while the table's other indexes carry
 * theirs. SQLite then falls back to an estimate for the unanalysed index
 * that keeps it out of the plan, and the sessions list is planned over
 * `session_by_started` instead: a non-covering scan that reads every row's
 * payload. Measured on the deployed corpus, that is the plan the dashboard
 * served; the statistics are what put the covering scan back.
 */
function finishSessionStatistics(db: Database): void {
  createSessionDenormIndex(db)
  db.exec('ANALYZE session')
}

/**
 * The context window's provenance, as the new column stores it.
 *
 * `payload.context.contextLimitSource` is the only place the corpus exposes
 * that value today; the column carries the same value, so a reader stops
 * paging every payload blob to count the unknown-window sessions. A payload
 * with no context object, or with a source that is not a string, has no
 * provenance to materialize: NULL, never a fabricated value.
 */
function contextLimitSourceOf(payload: Record<string, unknown>): string | null {
  const context = payload['context']
  if (typeof context !== 'object' || context === null || Array.isArray(context)) return null
  const source = (context as Record<string, unknown>)['contextLimitSource']
  return typeof source === 'string' ? source : null
}

/**
 * The context window's provenance, as the column stores it, derived from the
 * serialized payload a session row stores.
 *
 * The migration derives the column from the payload it finds in the corpus;
 * the write path derives it from the payload it is about to store. Both go
 * through `contextLimitSourceOf`, so the two derivations cannot disagree: a
 * row written after the migration carries the same value the migration would
 * have written for it, and a payload with no provenance carries NULL.
 *
 * Derived from the serialized payload rather than from the caller's object,
 * for the same reason `materializeSessionSummary` does it that way: the column
 * must describe the blob the row holds, not the object the blob came from.
 */
function materializeContextLimitSource(payloadJson: string): string | null {
  const payload = parseJsonObject(payloadJson)
  return payload === undefined ? null : contextLimitSourceOf(payload)
}

/**
 * The index a turn carries inside the payload, verbatim.
 *
 * The projection writes a 0-based `index` on every turn, so copying that
 * value settles the base question without a convention of our own: the row
 * names the turn by the number the turn was given. A turn that carries no
 * index at all falls back to its position in the array, so the row still
 * names an order.
 */
function turnIndexOf(turn: unknown, position: number): number {
  if (typeof turn === 'object' && turn !== null && !Array.isArray(turn)) {
    const index = (turn as Record<string, unknown>)['index']
    if (typeof index === 'number' && Number.isFinite(index)) return index
  }
  return position
}

/**
 * One session's turns, split out of the blob that carried them.
 *
 * The blob a session row stores and the turn rows it owns are two views of
 * one session: the blob keeps the session's metadata, the rows keep its
 * turns. The split follows the rules the v22 migration applied to the rows it
 * found, so a session written now is stored the way a session upgraded now
 * would have been — the two paths cannot drift apart:
 *
 * - A payload with no `turns` key is left byte for byte as it is. There is
 *   nothing to split, and re-serializing it would rewrite bytes nobody asked
 *   to rewrite.
 * - A payload with a `turns` key loses it — an empty array included, because
 *   a key that names an empty list still says "the turns are here", and after
 *   the split they are not — and keeps every other key in the order it had,
 *   so a reader that diffs two payloads sees the turns leaving and nothing
 *   else moving.
 * - Each turn becomes a row, named by the turn's own `index` (or by its
 *   position, when the turn names none) and carrying the turn's own JSON.
 */
function splitSessionTurns(payloadJson: string): {
  payload: string
  turns: Array<{ index: number; stored: CompressedColumn }>
} {
  const payload = parseJsonObject(payloadJson)
  // A payload that does not parse is stored whole, exactly as it came: the
  // write path owns the blob it is handed, and a blob it cannot read is a
  // blob it must not rewrite.
  if (payload === undefined) return { payload: payloadJson, turns: [] }
  if (!Object.prototype.hasOwnProperty.call(payload, 'turns')) {
    return { payload: payloadJson, turns: [] }
  }
  const turns = payload['turns']
  // A `turns` key that is not an array names no turn, so it names no row --
  // but the key still leaves the payload, as the migration's split left it.
  const rows = Array.isArray(turns)
    ? turns.map((turn, position) => ({
        index: turnIndexOf(turn, position),
        // The turn's own JSON, deflated: the turn rows are the split's bulk
        // (40,621 of them on the measured corpus), so they are stored the way
        // `records.parts_json` is stored -- compressed, and read back
        // decompressed. Compressing them at the writer is what keeps the
        // split's storage win from being given back by uncompressed turn
        // JSON. A row written before this change holds the plain JSON, and
        // the readers accept both forms.
        stored: compressColumn(turn),
      }))
    : []
  delete payload['turns']
  return { payload: JSON.stringify(payload), turns: rows }
}

/**
 * Move every session's turns out of its payload and into `session_turn`, and
 * materialize the columns the corpus now reads instead of the payload.
 *
 * Three things happen per row, and they are deliberately not the same thing:
 *
 * - `summary_json`, `problems_count` and `context_limit_source` are populated
 *   for EVERY row the step processes, a row with no turns included. The
 *   columns are new to this corpus, and a consumer that reads them must not
 *   silently miss rows because the row happened to carry no turns: a NULL
 *   there would read as "no provenance recorded" for a session whose payload
 *   names one.
 * - The turn split is confined to rows that carry a `turns` key. A row
 *   without one has no turns to remove, so its payload is left exactly as it
 *   is -- byte for byte, not re-serialized.
 *
 * WHY the summary columns are materialized here, and not left to the writer:
 * the reader falls back to the payload whenever they are NULL (the legacy
 * path in `dash/src/server/bridge.ts`), so a corpus whose columns were never
 * populated pays a payload parse on every sessions-list read. Measured
 * against the live 3.3 GB corpus, that is the difference between a 4.61 ms
 * warm list and a 55.79 ms one. This step is the one pass that already walks
 * every session row, so materializing here costs no extra traversal -- and a
 * store that migrates to v22 comes out the other side materialized, exactly
 * as if every row had been written by this build.
 *
 * The values are the writer's values: `sessionSummaryOf` is the same rule
 * `upsertSession` derives the columns through, so a migrated row and a
 * written row carry identical columns for the same payload. They are derived
 * from the payload AS IT WAS BEFORE the turn split -- the summary and the
 * problems live in the session's metadata, not in its turns, so the split
 * cannot move them.
 *
 * The rewritten payload keeps the original's own key order, minus `turns`:
 * the blob is built from the parsed original's key sequence, so a reader
 * that diffs two payloads sees the turns leaving and nothing else moving.
 *
 * A session's rows are replaced wholesale -- delete, then insert -- so a
 * re-run of the step cannot leave two rows for one turn.
 *
 * The pass has two drivers over one per-session worker: the synchronous one
 * the constructor's open path uses, and the yielding one the asynchronous
 * open path uses. The work is the same in both; only the hand-back to the
 * event loop differs.
 */
function backfillSessionTurns(db: Database): void {
  const backfill = backfillStatements(db)
  for (const sessionId of backfillSessionIds(db)) backfillOneSession(backfill, sessionId)
}

/**
 * The same backfill, handing the event loop back between sessions.
 *
 * <remarks>
 * The yield is between sessions and inside the step's transaction: the
 * transaction and its write lock live on the SQLite connection, not on the
 * JavaScript stack, so the await neither commits nor rolls back and the step
 * stays one atomic unit. The connection is the migration's own, so no other
 * writer can join the transaction through it.
 */
async function backfillSessionTurnsYielding(db: Database, yieldToLoop: MigrationYield): Promise<void> {
  const backfill = backfillStatements(db)
  for (const sessionId of backfillSessionIds(db)) {
    backfillOneSession(backfill, sessionId)
    await yieldToLoop()
  }
}

/** The sessions the backfill walks, in storage order. */
function backfillSessionIds(db: Database): string[] {
  return (db.prepare('SELECT session_id FROM session').all() as { session_id: string }[]).map(
    (row) => row.session_id,
  )
}

/** The statements the backfill writes with, prepared once for the pass. */
function backfillStatements(db: Database): SessionTurnBackfill {
  return {
    readPayload: db.prepare('SELECT payload FROM session WHERE session_id = ?'),
    writePayload: db.prepare('UPDATE session SET payload = ? WHERE session_id = ?'),
    writeMaterialized: db.prepare(
      'UPDATE session SET summary_json = ?, problems_count = ?, context_limit_source = ? WHERE session_id = ?',
    ),
    deleteTurns: db.prepare('DELETE FROM session_turn WHERE session_id = ?'),
    insertTurn: db.prepare(
      'INSERT INTO session_turn (session_id, turn_index, turn_json) VALUES (?, ?, ?)',
    ),
  }
}

/**
 * One session's backfill: the materialized columns are filled in from the
 * payload the row still carries, and the turns move to their own rows.
 */
function backfillOneSession(backfill: SessionTurnBackfill, sessionId: string): void {
  const row = backfill.readPayload.get(sessionId) as { payload: string } | undefined
  if (row === undefined) return
  const payload = parseJsonObject(row.payload)
  // A payload that does not parse is left exactly as it is: the upgrade must
  // not take a whole corpus down over one unreadable row.
  if (payload === undefined) return
  // The columns are derived from the payload this row still carries, before
  // the turn split rewrites it -- and from the writer's own rule, so a
  // migrated row and a written row agree.
  const summary = sessionSummaryOf(payload)
  backfill.writeMaterialized.run(
    summary.summary_json,
    summary.problems_count,
    contextLimitSourceOf(payload),
    sessionId,
  )
  if (!Object.prototype.hasOwnProperty.call(payload, 'turns')) return
  const turns = payload['turns']
  backfill.deleteTurns.run(sessionId)
  if (Array.isArray(turns)) {
    for (const [position, turn] of turns.entries()) {
      backfill.insertTurn.run(sessionId, turnIndexOf(turn, position), compressColumn(turn))
    }
  }
  delete payload['turns']
  backfill.writePayload.run(JSON.stringify(payload), sessionId)
}

/** The statements the v22 backfill writes with. */
type SessionTurnBackfill = {
  readonly readPayload: StatementSync
  readonly writePayload: StatementSync
  readonly writeMaterialized: StatementSync
  readonly deleteTurns: StatementSync
  readonly insertTurn: StatementSync
}

/** Escape a literal for a LIKE pattern: backslash, percent, and underscore match themselves. */
function escapeLike(literal: string): string {
  return literal.replace(/\\/g, '\\\\').replace(/%/g, '\\%').replace(/_/g, '\\_')
}

/** Stable per-span/code/location key; rows without a span keep their independent legacy identity. */
export function problemIdentity(
  spanId: string | null,
  code: string,
  location: string | null,
  legacyId?: number | string,
): string {
  if (spanId === null) return JSON.stringify(['legacy-row', legacyId])
  return JSON.stringify(['span-problem', spanId, code, location])
}

function rekeyProblems(db: Database): void {
  const table = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='problems'").get()
  if (!table) return
  const rows = db
    .prepare('SELECT id, span_id, code, location FROM problems ORDER BY id')
    .all() as Array<{ id: number; span_id: string | null; code: string; location: string | null }>
  const seen = new Map<string, number>()
  const remove = db.prepare('DELETE FROM problems WHERE id = ?')
  const setKey = db.prepare('UPDATE problems SET problem_key = ? WHERE id = ?')
  for (const row of rows) {
    const key = problemIdentity(row.span_id, row.code, row.location, row.id)
    const earlier = seen.get(key)
    if (earlier !== undefined) remove.run(earlier)
    setKey.run(key, row.id)
    seen.set(key, row.id)
  }
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
export type SpanProblem = Problem & {
  spanId: string
  sessionId?: string | null
  harness?: string | null
  timestamp?: string | null
  /**
   * The exception stack when the problem came from a throw (v18+ rows). Null/absent
   * otherwise — never synthesized. A validation problem that was computed, not thrown,
   * has a message but no stack, and must not claim one.
   */
  stack?: string | null
}

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
  source?: string
  name?: string
  timestamp?: string
  /** The harness attribution named for the row; absent on rows that predate v19. */
  harness?: string | null
  /**
   * The exact incoming span JSON stored at quarantine time (v18+ rows). Absent for
   * older rows — unknown, never reconstructed from the records table (that row, when
   * one exists, is the normalized record, not the incoming payload, and mislabeling
   * it as the original message is forbidden). The sender's decoded packet, kept
   * beside the audit row as evidence (B7).
   */
  rawPayload?: string | null
}

/**
 * One quarantine row in the shape the detail route serves (plan 2026-10-10,
 * B7). Unlike `QuarantineEntry` every field is always present — the route's
 * contract is explicit null for "row has none", because a dropped key would
 * read as "field unknown" rather than "no evidence". `evidenceAvailable` is
 * the honest answer to "did this row keep a packet", never inferred.
 */
export type QuarantineEntryDetail = {
  spanId: string
  source: string | null
  name: string | null
  reason: string
  timestamp: string | null
  namespaces: string[]
  harness: string | null
  rawPayload: string | null
  evidenceAvailable: boolean
}

/** Audit metadata a quarantine row can carry beyond the reason. */
export type QuarantineMetadata = {
  source?: string
  name?: string
  timestamp?: string
  /** The sender's decoded packet, kept beside the audit row as evidence (B7). */
  rawPayload?: string
  /** The harness attribution named, so a per-harness wipe can scope the row. */
  harness?: string
}

export type QuarantinedLog = {
  logId: string
  reason: string
  log: unknown
  /** The harness tag the row was written with (B3/D-Q3); absent on rows that predate v19. */
  harness?: string
}

/** What the request-body matcher needs of a model call: no content, no raw. */
export type ModelCallCandidate = {
  spanId: string
  timestamp: string
  /** Null only if a legacy row stored none; the matcher treats that as unknown. */
  durationMs: number | null
}

type CandidateRow = { span_id: unknown; timestamp: unknown; duration_ms: unknown }

function toCandidate(row: CandidateRow): ModelCallCandidate {
  return {
    spanId: String(row.span_id),
    timestamp: String(row.timestamp),
    durationMs: typeof row.duration_ms === 'number' ? row.duration_ms : null,
  }
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

/**
 * Session-scope system-prompt texts hiding in one records row's content
 * columns, without touching anything else on the row.
 *
 * <remarks>
 * Why this shape: the turn-content fallback only needs `system_prompt`
 * texts, so it must not pay for full records. `content_json` is plain text
 * and filters with a parse; `parts_json` is deflated with no expression
 * index into it, so a parts-carrying row still inflates to filter — a true
 * indexed pre-filter would need a schema migration (a generated column over
 * the inflated parts), deliberately out of scope. Semantics match the
 * bridge's `partsFromRow`: parts win when present and non-empty (even when
 * `content_json` carries something else), otherwise `content_json` decides.
 * Both callers (the store method below and the store-less bridge path) read
 * through here so the two seams cannot diverge.
 *
 * The parts are read from either stored form — the deflated one, and the
 * plain JSON a row written before the compression landed holds — so a
 * pre-change row keeps the system prompts it holds instead of falling
 * through to a `content_json` that is '{}' on a parts-carrying row.
 * </remarks>
 */
export function systemPromptTextsFromContentColumns(
  contentJson: unknown,
  partsJson: unknown,
): string[] {
  if (partsJson !== null && partsJson !== undefined) {
    const parts = decompressColumn(partsJson)
    if (Array.isArray(parts) && parts.length > 0) {
      return [...parts]
        .sort((a, b) => (a.order ?? 0) - (b.order ?? 0))
        .filter((part) => part.part === 'system_prompt' && typeof part.text === 'string' && part.text !== '')
        .map((part) => part.text)
    }
  }
  if (typeof contentJson === 'string' && contentJson !== '' && contentJson !== '{}') {
    try {
      const content = JSON.parse(contentJson) as Partial<Record<string, unknown>>
      const text = content.system_prompt
      if (typeof text === 'string' && text !== '') return [text]
    } catch {
      return []
    }
  }
  return []
}

function text(value: unknown): string {
  return value as string
}

/**
 * Map a problems row out of SQLite's column names. `stack` is present only when the row
 * stored one; a computed validation problem has a message and no stack.
 */
function toSpanProblem(row: {
  span_id: unknown
  severity: unknown
  code: unknown
  message: unknown
  location: unknown
  session_id: unknown
  harness: unknown
  timestamp: unknown
  stack: unknown
}): SpanProblem {
  const p: SpanProblem = {
    spanId: text(row.span_id),
    severity: text(row.severity) as Problem['severity'],
    code: text(row.code),
    message: text(row.message),
  }
  if (row.location !== null && row.location !== undefined) {
    p.location = text(row.location)
  }
  if (row.session_id !== null && row.session_id !== undefined) {
    p.sessionId = text(row.session_id)
  }
  if (row.harness !== null && row.harness !== undefined) {
    p.harness = text(row.harness)
  }
  if (row.timestamp !== null && row.timestamp !== undefined) {
    p.timestamp = text(row.timestamp)
  }
  if (row.stack !== null && row.stack !== undefined) {
    p.stack = text(row.stack)
  }
  return p
}

type QuarantineRowShape = {
  span_id: unknown
  namespaces: unknown
  reason: unknown
  source: unknown
  name: unknown
  timestamp: unknown
  raw_payload: unknown
}

/**
 * Map a quarantine row out of SQLite's column names. `raw_payload` is present only when
 * the row stored one (v18+ writes); older rows read without it — absent, never invented.
 */
function toQuarantineEntry(row: QuarantineRowShape): QuarantineEntry {
  const entry: QuarantineEntry = {
    spanId: text(row.span_id),
    namespaces: JSON.parse(text(row.namespaces)) as string[],
    reason: text(row.reason),
  }
  if (row.source !== null && row.source !== undefined) {
    entry.source = text(row.source)
  }
  if (row.name !== null && row.name !== undefined) {
    entry.name = text(row.name)
  }
  if (row.timestamp !== null && row.timestamp !== undefined) {
    entry.timestamp = text(row.timestamp)
  }
  if (row.raw_payload !== null && row.raw_payload !== undefined) {
    entry.rawPayload = text(row.raw_payload)
  }
  return entry
}

/**
 * The `WHERE` fragment scoping quarantine rows to one harness filter, with its bound
 * parameters. `undefined` is unfiltered. `'unknown'` matches unattributed sources —
 * null, blank, or the neutral grouping keys — and a canonical id matches every stored
 * spelling normalizing to it (case-insensitive), so twin front-ends scope together.
 */
export function quarantineHarnessWhere(harness: string | undefined): { where: string; params: Array<string | number> } {
  if (harness === undefined) return { where: '', params: [] }
  if (harness === 'unknown') {
    const keys = QUARANTINE_UNKNOWN_SOURCES.map((key) => key.toLowerCase())
    const placeholders = keys.map(() => '?').join(', ')
    return {
      where: ` WHERE source IS NULL OR TRIM(COALESCE(source, '')) = '' OR LOWER(source) IN (${placeholders})`,
      params: keys,
    }
  }
  const spellings = sourceSpellingsForHarness(harness)
  // An id with no known spelling matches nothing; `IN ()` is not portable SQL.
  if (spellings.length === 0) return { where: ' WHERE 1 = 0', params: [] }
  const placeholders = spellings.map(() => '?').join(', ')
  return { where: ` WHERE LOWER(source) IN (${placeholders})`, params: spellings }
}

/**
 * The `WHERE` fragment scoping problem rows to one harness filter. Problems carry the
 * attributed harness id, matched case-insensitively against the same spelling expansion
 * the quarantine filter uses (so `?harness=copilot-chat` finds the twin front-end's rows
 * in both lists); `unknown` is a null or blank harness.
 */
export function problemsHarnessWhere(harness: string | undefined): { where: string; params: Array<string | number> } {
  if (harness === undefined) return { where: '', params: [] }
  if (harness === 'unknown') {
    return { where: ` WHERE harness IS NULL OR TRIM(COALESCE(harness, '')) = ''`, params: [] }
  }
  const spellings = new Set([harness.toLowerCase(), ...sourceSpellingsForHarness(harness)])
  const placeholders = [...spellings].map(() => '?').join(', ')
  return { where: ` WHERE LOWER(harness) IN (${placeholders})`, params: [...spellings] }
}

function nullableText(value: unknown): string | null {
  return (value as string | null) ?? null
}

/**
 * Install a memoized property computed on first read.
 *
 * Reading the corpus is what a rebuild spends its time on, and each pass over
 * it wants a different handful of fields: grouping spans into runs needs
 * `raw`'s attributes and never looks at the message parts, while the session
 * builder needs the parts and tokenizes them. Inflating `parts_json` for a
 * pass that discards it measured 13 of the 21 seconds each pass cost, and the
 * record objects built around it are what took the rebuild's heap high-water
 * mark to 3.5 GB.
 *
 * The property stays enumerable, configurable and writable, so a record is
 * indistinguishable from the eager object this used to build: spread,
 * `JSON.stringify`, `Object.keys` and deep equality all see the same shape,
 * in the same order, with the same values. Redefining a key the literal
 * already declared keeps its original position in that order.
 *
 * Assigning `undefined` RELEASES the field rather than storing an absent
 * value: the next read derives it again. The two uses are different on
 * purpose. The payload reduction REPLACES the payload with the attributes the
 * derived views read — a defined value, so it stays cached for the record's
 * lifetime. The parts are RELEASED once the readers have counted them: the
 * decompressed text is the session's working set, and a reader that genuinely
 * needs it again (the twin collapse's content transplant, a subagent's task
 * summary) asks for it and inflates it once more.
 */
function defineLazy<T>(target: object, key: string, compute: () => T): void {
  let value: T
  let computed = false
  Object.defineProperty(target, key, {
    enumerable: true,
    configurable: true,
    get(): T {
      if (!computed) {
        value = compute()
        computed = true
      }
      return value
    },
    set(next: T): void {
      value = next
      computed = next !== undefined
    },
  })
}

/**
 * Drop a record's parts, and the content derived from them.
 *
 * WHY: the parts are a record's largest field, and the projection reads them
 * once — to count them into the composition's bucket totals, the tool
 * catalogue and the skills listing — and then has no further use for the
 * text. `defineLazy` caches what it computed, so the decompressed parts
 * stayed alive for as long as the record, and the record lives as long as the
 * session: on the measured corpus ~10,933 turns x 64 KB of text, about 1.4 GB
 * as UTF-16, which is what the used heap spiked to.
 *
 * The release is a RELEASE, not a destruction: the lazy fields are marked
 * unread, so a later read derives them again from the row. A reader that
 * needs the raw text after the release therefore pays one more inflation, and
 * the projection keeps the reduced value rather than the text.
 */
export function releaseRecordParts(record: CanonicalRecord): void {
  releaseLazyField(record, 'parts')
  releaseLazyField(record, 'content')
}

/**
 * Release one lazy field: drop what it cached and mark it unread, so a later
 * read derives it again.
 *
 * Only a LAZY field is released. A plain value is the record's own data — a
 * record built by hand, or the twin collapse's merged record — and there is
 * nothing to re-derive it from, so releasing it would lose it.
 */
function releaseLazyField(record: CanonicalRecord, key: 'parts' | 'content'): void {
  const descriptor = Object.getOwnPropertyDescriptor(record, key)
  if (descriptor === undefined) return
  if (descriptor.get === undefined || descriptor.set === undefined) return
  ;(record as unknown as Record<string, unknown>)[key] = undefined
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
    // `content` is redefined below when parts are present; see toRecord.
    cost: JSON.parse(text(row.cost_json)) as CostBlock,
    // Declared here for its place in the key order, then made lazy: only the
    // attribute lookups read it, and they read it from a minority of records.
    raw: undefined,
  }
  defineLazy(record, 'raw', () =>
    row.raw === null || row.raw === undefined ? undefined : decompressRaw(row.raw as Uint8Array),
  )
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
    //
    // The column holds the deflated parts, and the plain JSON a row written
    // before the compression landed still holds. Both are read, so a
    // pre-change row keeps its parts: falling back to `content_json` would
    // lose them, because that column is '{}' on a parts-carrying row.
    //
    // A column that holds neither form reads as no parts, and the content
    // falls back to `content_json` — one unreadable cell must not take the
    // record's reader down with it.
    defineLazy(record, 'parts', () => decompressColumn(row.parts_json) as ContentPart[] | undefined)
    defineLazy(record, 'content', () => ({
      ...contentFromParts(record.parts ?? []),
      ...(JSON.parse(text(row.content_json)) as CanonicalRecord['content']),
    }))
  }
  return record
}

/**
 * One record of a session, with the attributes the derived views read.
 *
 * The record still carries its payload; the reduced attributes are handed
 * over beside it so the caller can compare the two before adopting them.
 */
export type ProjectedSessionRecord = {
  /** The record, in session order. */
  record: CanonicalRecord
  /** The attributes the derived views read, in place of the payload. */
  attributes: Record<string, unknown>
}

/**
 * The raw attributes the derived views read out of a record's payload.
 *
 * EVERY reader of `record.raw` in the projection path is listed here, because
 * a key missing from this list is a key whose value the projection would
 * silently lose:
 *
 *   - the session analysis (`canon/sessions.ts`): the model, the agent, the
 *     repository and branch, the tool name and status, the operation, the
 *     parent session, the subagent identity and description, and the tool
 *     arguments the task summary reads;
 *   - the twin-collector collapse (`canon/twin-dedupe.ts`): the join keys and
 *     the record's provenance;
 *   - the context-window derivation (`canon/context-window.ts`): the reported
 *     and declared window attributes, and the model identity;
 *   - the skills view (`canon/skills.ts`): the listing and invocation keys;
 *   - the run derivation (`canon/runs.ts`) and its outcome (`canon/outcome.ts`):
 *     the run, working-directory, parent-session and agent attributes, and the
 *     termination attributes;
 *   - the finding detectors (`analysis/findings.ts`, `analysis/compare.ts`,
 *     `analysis/signals.ts`): the tool arguments, the tool result and the
 *     command attributes.
 */
const PROJECTED_ATTRIBUTE_KEYS: ReadonlySet<string> = new Set([
  // The session analysis (`canon/sessions.ts`).
  'gen_ai.response.model',
  'gen_ai.request.model',
  'model',
  'gen_ai.agent.name',
  'agent.name',
  'vcs.repository.name',
  'repo',
  'vcs.ref.head.name',
  'branch',
  'gen_ai.operation.name',
  'gen_ai.tool.name',
  'tool.name',
  'gen_ai.tool.status',
  'status',
  'is_error',
  'isError',
  'gen_ai.is_subagent',
  'is_subagent',
  'parent_session',
  'parent_session_id',
  'gen_ai.parent_session_id',
  'parentSession',
  'parentSessionId',
  'gen_ai.prompt',
  'prompt',
  'agent_id',
  'agentId',
  'subagent_id',
  'subagentId',
  'agent.id',
  'gen_ai.agent.id',
  'agent_type',
  'agentType',
  'gen_ai.agent.type',
  'agent_name',
  'agentName',
  'agent',
  'description',
  'task_description',
  'taskDescription',
  'task',
  'gen_ai.agent.description',
  'summary',
  'arguments',
  'gen_ai.tool.arguments',
  'tool.arguments',
  'tool_arguments',
  'parameters',
  'gen_ai.agent.arguments',
  'task.description',
  // The context-window derivation (`canon/context-window.ts`).
  'contextWindow',
  'gen_ai.request.max_context_tokens',
  'gen_ai.request.context_window',
  'model_context_window',
  'declaredContextWindow',
  // The twin-collector collapse (`canon/twin-dedupe.ts`).
  'gen_ai.response.id',
  'request_id',
  'requestId',
  'provenance',
  // The skills view (`canon/skills.ts`).
  'kyber.skills.listed',
  'kyber.skills.invoked',
  'github.copilot.context.skills',
  'gen_ai.skills',
  'gen_ai.usage.skill_tokens',
  // The run derivation (`canon/runs.ts`).
  'process.working_directory',
  'working_directory',
  'cwd',
  'workspace.root',
  'project.path',
  'gen_ai.run.id',
  'run.id',
  'run_id',
  'runId',
  'gen_ai.workflow.id',
  'workflow.id',
  'workflow_id',
  'task.id',
  'task_id',
  // The run outcome (`canon/outcome.ts`).
  'exitCode',
  'exit_code',
  'status_code',
  'terminationReason',
  'termination_reason',
  'stop_reason',
  'finish_reason',
  'type',
  'event',
  'error',
  'exception',
  'editFailed',
  'isCorrection',
  'correctionRule',
  'userMessage',
  // The finding detectors (`analysis/findings.ts`, `analysis/compare.ts`,
  // `analysis/signals.ts`).
  'gen_ai.tool.arguments_hash',
  'gen_ai.tool.result_bytes',
  'result',
  'output',
  'tool_name',
  'tool',
  'tools',
  'command',
  'cmd',
])

/**
 * The length above which a string value counts as payload rather than as an
 * attribute: a harness's prompt, conversation or catalogue runs to tens of
 * kilobytes, and an attribute the readers look up is a name, a path or a
 * count. Values under the limit are kept whatever their key, so an attribute
 * nobody listed still survives when it is small.
 */
const PROJECTED_VALUE_LIMIT = 4096

/**
 * A record's payload, reduced to the attributes the derived views read.
 *
 * Every key survives: the timeline's evidence scan reads the key names, and a
 * key that names the evidence IS evidence. The values are what the projection
 * cannot afford — the harness's prompt, conversation and catalogue — under
 * keys no reader asks for. A value under `PROJECTED_VALUE_LIMIT` is kept
 * whatever its key; a longer one is kept only when a reader asks for it by
 * name.
 */
export function projectedAttributes(payload: unknown): Record<string, unknown> {
  if (payload === null || typeof payload !== 'object' || Array.isArray(payload)) return {}
  const attributes: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(payload as Record<string, unknown>)) {
    if (typeof value === 'string') {
      if (value.length <= PROJECTED_VALUE_LIMIT || PROJECTED_ATTRIBUTE_KEYS.has(key)) {
        attributes[key] = value
      }
      continue
    }
    if (value !== null && typeof value === 'object') {
      // An object or an array is payload unless a reader asks for it by name
      // (`provenance`, the skills listing, the detector's tool list).
      if (PROJECTED_ATTRIBUTE_KEYS.has(key)) attributes[key] = value
      continue
    }
    attributes[key] = value
  }
  return attributes
}

/**
 * Replace every record's payload with the attributes the derived views read.
 *
 * The payload is read and released one record at a time, so a session's
 * payloads are never all live at once — which is the whole point: the
 * measured corpus held 1.5 GB of them at once, and the heap spiked to 3.1 GB
 * every projection.
 */
export function reduceRecordPayloads(records: readonly CanonicalRecord[]): void {
  for (const record of records) record.raw = projectedAttributes(record.raw)
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
    summary:
      payload !== null && typeof payload === "object" && "summary" in (payload as Record<string, unknown>)
        ? ((payload as Record<string, unknown>).summary as Record<string, unknown>)
        : undefined,
    payload,
  }
}

/**
 * The two materialized summary columns of one session payload, derived from
 * the payload's own object.
 *
 * `summary_json` is the serialized `$.summary` object; `problems_count` is the
 * length of the `$.problems` array. A payload carrying no such value writes
 * NULL for that column: NULL is the "not materialized" sentinel, so the reader
 * derives the value from the payload and lands on the answer it would have
 * derived anyway. A fabricated `{}` or `0` would instead claim an empty
 * summary, and a measured zero problem count, for a payload that was never
 * measured.
 *
 * WHY this is the rule and `materializeSessionSummary` is its entry point:
 * the rule has to be in ONE place, because two places is how a migrated
 * corpus and a freshly written one end up describing the same session
 * differently. The write path derives the columns from the serialized payload
 * the row stores (`materializeSessionSummary`); the migration derives them
 * from the object it has already parsed (`sessionSummaryOf`, this function).
 * The context-limit column is shared the same way -- `contextLimitSourceOf`
 * is the rule, `materializeContextLimitSource` is the write path's entry
 * point -- so the two columns of a migrated row and a written row come from
 * one derivation each.
 */
function sessionSummaryOf(record: Record<string, unknown>): {
  summary_json: string | null
  problems_count: number | null
} {
  const summary = record.summary
  const problems = record.problems
  return {
    // A payload that carries no summary, or a null one, has no summary to
    // materialize: NULL, never the string "null" and never an empty object.
    summary_json: summary === undefined || summary === null ? null : JSON.stringify(summary),
    // Only an array has a length to count. A missing or non-array `problems`
    // is "never measured", not a count of zero.
    problems_count: Array.isArray(problems) ? problems.length : null,
  }
}

/**
 * The two materialized summary columns of one session row, derived from the
 * serialized payload the row stores.
 *
 * Derived from the serialized payload rather than from the caller's object, so
 * the columns are exactly the `$.summary` and `$.problems` the stored payload
 * holds -- the two cannot disagree. The rule itself is `sessionSummaryOf`,
 * which the migration uses over the object it parses.
 */
function materializeSessionSummary(payloadJson: string): {
  summary_json: string | null
  problems_count: number | null
} {
  let parsed: unknown
  try {
    parsed = JSON.parse(payloadJson)
  } catch {
    return { summary_json: null, problems_count: null }
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    return { summary_json: null, problems_count: null }
  }
  return sessionSummaryOf(parsed as Record<string, unknown>)
}

/**
 * The measured figures a session's summary carries.
 *
 * Every field is optional because the summary is optional: a session that
 * measured nothing carries none of them, and a figure that is missing stays
 * missing. A zero would claim a measured zero for a session that measured
 * nothing.
 */
export type SessionSummaryFigureFields = {
  turnCount?: number
  totalInput?: number
  totalOutput?: number
  totalCacheRead?: number
  totalCacheCreation?: number
  durationMs?: number
  costBasis?: string
  costStatus?: string
  costValue?: number
  costCurrency?: string
  costUsdLegacy?: number
  includedInParent?: boolean
}

/** One session's measured figures, with the session they belong to. */
export type SessionSummaryFigureRow = SessionSummaryFigureFields & { sessionId: string }

/**
 * The figures a summary carries, and where each one lives inside it.
 *
 * One list, two renderings: the materialized column holds the `$.summary`
 * object itself, so its paths are `$.<path>`, while a payload holds that same
 * object under `$.summary`, so its paths are `$.summary.<path>`. Deriving
 * both statements from this one list is what stops the materialized read and
 * the legacy read from drifting apart on which figure is which.
 */
const SESSION_FIGURE_FIELDS = [
  { alias: 'turn_count', path: 'turn_count' },
  { alias: 'total_input', path: 'total_input' },
  { alias: 'total_output', path: 'total_output' },
  { alias: 'total_cache_read', path: 'total_cache_read' },
  { alias: 'total_cache_creation', path: 'total_cache_creation' },
  { alias: 'duration_ms', path: 'duration_ms' },
  { alias: 'cost_basis', path: 'cost.basis' },
  { alias: 'cost_status', path: 'cost.status' },
  { alias: 'cost_value', path: 'cost.value' },
  { alias: 'cost_currency', path: 'cost.currency' },
  { alias: 'cost_usd', path: 'cost.usd' },
  { alias: 'included_in_parent', path: 'included_in_parent' },
] as const

/**
 * The figures of a set of sessions, read out of the materialized column.
 *
 * `summary_json` holds the serialized `$.summary` object, so every figure is
 * one JSON path inside it. The statement names no payload: naming `payload`
 * at all, even inside `json_extract`, makes SQLite read and parse the whole
 * blob of every row it touches, which is the cost this read exists to avoid.
 * `summary_json` itself is selected beside the figures because its NULL is
 * the writer's "not materialized" sentinel -- the only thing that says a row
 * still has to be read out of its payload.
 */
function sessionFiguresSelect(placeholders: string): string {
  const columns = SESSION_FIGURE_FIELDS.map(
    (field) => `json_extract(summary_json, '$.${field.path}') AS ${field.alias}`,
  ).join(', ')
  return `SELECT session_id, summary_json, ${columns} FROM session WHERE session_id IN (${placeholders})`
}

/**
 * The same figures, read out of a legacy row's own payload.
 *
 * The scope is the load-bearing part: `summary_json IS NULL` confines the
 * payload read to the rows written before the materialization existed, so a
 * materialized corpus never reads a byte of payload. A database whose schema
 * predates the column has no materialized rows to scope to, and the scope is
 * dropped for it -- every row there is a legacy row.
 */
function sessionFiguresLegacySelect(placeholders: string, scoped: boolean): string {
  const columns = SESSION_FIGURE_FIELDS.map(
    (field) => `json_extract(payload, '$.summary.${field.path}') AS ${field.alias}`,
  ).join(', ')
  const scope = scoped ? ' AND summary_json IS NULL' : ''
  return `SELECT session_id, ${columns} FROM session WHERE session_id IN (${placeholders})${scope}`
}

/** One row of the figures read, with the aliases the field list names. */
type SessionFigureDbRow = {
  session_id: unknown
  summary_json?: unknown
  turn_count?: unknown
  total_input?: unknown
  total_output?: unknown
  total_cache_read?: unknown
  total_cache_creation?: unknown
  duration_ms?: unknown
  cost_basis?: unknown
  cost_status?: unknown
  cost_value?: unknown
  cost_currency?: unknown
  cost_usd?: unknown
  included_in_parent?: unknown
}

/**
 * Whether a statement failed because the table has no such column.
 *
 * The one failure the reader recovers from: a database written before the
 * materialized columns existed has no `summary_json` to read, and every row
 * in it is a legacy row.
 */
function isMissingColumnError(err: unknown): boolean {
  return err instanceof Error && /no such column/i.test(err.message)
}

/**
 * The figures one row carries, with the guards this read has always applied.
 *
 * A figure the session did not measure stays missing: a summary without
 * `turn_count` yields no turn count, never a zero. A status or a currency
 * that is not a string is not a status or a currency. A number that is not
 * finite is not a figure: a JSON document carrying `1e999` parses to Infinity,
 * and an infinite total would poison every sum it enters.
 */
function sessionFigureFields(row: SessionFigureDbRow): SessionSummaryFigureFields {
  const figure = (value: unknown): number | undefined =>
    typeof value === 'number' && Number.isFinite(value) ? value : undefined
  const turnCount = figure(row.turn_count)
  const totalInput = figure(row.total_input)
  const totalOutput = figure(row.total_output)
  const totalCacheRead = figure(row.total_cache_read)
  const totalCacheCreation = figure(row.total_cache_creation)
  const durationMs = figure(row.duration_ms)
  const costValue = figure(row.cost_value)
  const costUsdLegacy = figure(row.cost_usd)
  return {
    ...(turnCount !== undefined ? { turnCount } : {}),
    ...(totalInput !== undefined ? { totalInput } : {}),
    ...(totalOutput !== undefined ? { totalOutput } : {}),
    ...(totalCacheRead !== undefined ? { totalCacheRead } : {}),
    ...(totalCacheCreation !== undefined ? { totalCacheCreation } : {}),
    ...(durationMs !== undefined ? { durationMs } : {}),
    ...(typeof row.cost_basis === 'string' ? { costBasis: row.cost_basis } : {}),
    ...(typeof row.cost_status === 'string' ? { costStatus: row.cost_status } : {}),
    ...(costValue !== undefined ? { costValue } : {}),
    ...(typeof row.cost_currency === 'string' ? { costCurrency: row.cost_currency } : {}),
    ...(costUsdLegacy !== undefined ? { costUsdLegacy } : {}),
    ...(row.included_in_parent === true ? { includedInParent: true as const } : {}),
  }
}

/**
 * The measured figures of a set of sessions.
 *
 * <remarks>
 * One reader for both copies of this read: the store's own accessor and the
 * bridge's direct-DB path differ only in which handle they hand over, so the
 * two cannot disagree about what a session's figures are.
 *
 * The materialized column answers first. Only a row whose `summary_json` is
 * NULL -- a row written before the materialization existed -- falls back to
 * its own payload, and it falls back one row at a time, so a mixed corpus
 * leaves the materialized rows alone and reads each legacy row's payload
 * once.
 * </remarks>
 */
export function readSessionSummaryFigures(
  db: Database,
  sessionIds: readonly string[],
): SessionSummaryFigureRow[] {
  const uniqueIds = [...new Set(sessionIds)].filter((id) => id.length > 0)
  const out: SessionSummaryFigureRow[] = []
  const chunkSize = 900

  for (let offset = 0; offset < uniqueIds.length; offset += chunkSize) {
    const chunk = uniqueIds.slice(offset, offset + chunkSize)
    for (const row of readSessionFigureRows(db, chunk)) {
      if (typeof row.session_id !== 'string') continue
      out.push({ sessionId: row.session_id, ...sessionFigureFields(row) })
    }
  }

  return out
}

/**
 * One chunk of the figures read: the materialized column, with the payload
 * read for the rows the column could not serve.
 */
function readSessionFigureRows(db: Database, ids: string[]): SessionFigureDbRow[] {
  const placeholders = ids.map(() => '?').join(', ')
  let primary: SessionFigureDbRow[]
  try {
    primary = db.prepare(sessionFiguresSelect(placeholders)).all(...ids) as SessionFigureDbRow[]
  } catch (err) {
    if (!isMissingColumnError(err)) throw err
    // A schema that predates the materialized column: every row in it is a
    // legacy row, so the payload read serves the whole chunk.
    return db.prepare(sessionFiguresLegacySelect(placeholders, false)).all(...ids) as SessionFigureDbRow[]
  }

  // NULL is the writer's "not materialized" sentinel, so these are exactly
  // the rows whose figures still live in their payload.
  const legacyIds = primary
    .filter((row) => row.summary_json === null || row.summary_json === undefined)
    .map((row) => row.session_id)
    .filter((id): id is string => typeof id === 'string')
  if (legacyIds.length === 0) return primary

  const legacyPlaceholders = legacyIds.map(() => '?').join(', ')
  const legacy = db
    .prepare(sessionFiguresLegacySelect(legacyPlaceholders, true))
    .all(...legacyIds) as SessionFigureDbRow[]
  const byId = new Map<unknown, SessionFigureDbRow>(legacy.map((row) => [row.session_id, row]))
  return primary.map((row) => {
    if (row.summary_json !== null && row.summary_json !== undefined) return row
    // The payload fills what the column could not serve. A row with no
    // materialized summary has no materialized figures either, so nothing
    // that was materialized is overwritten.
    return byId.get(row.session_id) ?? row
  })
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
  const parsedErrorBar = JSON.parse(text(row.error_bar_json)) as { lower: number; upper: number } | null
  const evidenceLinks = JSON.parse(text(row.evidence_links_json)) as FindingEvidenceLink[]
  const finding: Finding = {
    id: text(row.id),
    detectorId: text(row.detector_id) as DetectorId,
    title: text(row.title),
    mechanism: text(row.mechanism),
    evidenceLinks,
    confidence: text(row.confidence) as FindingConfidence,
    // A NULL waste column is an unmeasured (coverage-gap) finding: it
    // round-trips as absent, never as zero waste.
    ...(row.estimated_waste_tokens === null || row.estimated_waste_tokens === undefined
      ? {}
      : { estimatedWasteTokens: Number(row.estimated_waste_tokens) }),
    recommendation: text(row.recommendation),
    ...(parsedErrorBar === null || parsedErrorBar === undefined ? {} : { errorBar: parsedErrorBar }),
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

/** Server-side finding filters (issue #191): detector and harness narrow the
 * set. Paging lives in the bridge (`listFindingsPage`), which slices the
 * narrowed set once `total` is known — one owner, not two (review). */
export type FindingsListOptions = {
  detector?: string
  harness?: string
}

/** Payload bytes one quarantine page may hold; bounds startup memory for the relink pass. */
const DEFAULT_QUARANTINE_PAGE_BYTES = 16 * 1024 * 1024

/**
 * Open a canon database with the pragmas every handle uses, and establish the
 * metadata table the schema version is recorded in.
 *
 * <remarks>
 * One open for both migration paths: the constructor's synchronous one and
 * {@link migrateCanonDatabase}'s yielding one. They differ only in how the
 * steps are driven, never in how the handle is set up.
 */
function openCanonDatabase(path: string): Database {
  if (path !== ':memory:') {
    mkdirSync(dirname(path), { recursive: true })
  }
  const db = new DatabaseSync(path)
  // R2.5: under an ingest burst a second writer waits instead of failing,
  // and WAL keeps batch commits cheap.
  db.exec('PRAGMA busy_timeout = 5000')
  db.exec('PRAGMA journal_mode = WAL')
  // WAL's own default here is FULL, which fsyncs the sidecar on every commit
  // — the dominant cost of a folder import, which commits per source unit.
  // NORMAL is the documented pairing for WAL: a crash can lose the last
  // commit, it cannot corrupt the database.
  db.exec('PRAGMA synchronous = NORMAL')
  // Bound the sidecar. Without this a checkpoint reclaims nothing on disk:
  // the file keeps its high-water mark for the life of the database.
  db.exec(`PRAGMA journal_size_limit = ${WAL_SIZE_LIMIT_BYTES}`)
  db.exec(METADATA_SQL)
  return db
}

/**
 * Hand the event loop back for one turn.
 *
 * <remarks>
 * The real `setImmediate`, imported from `node:timers` rather than taken off
 * the global: a test that fakes timers replaces the global, and a caller
 * waiting for a turn nobody delivers hangs.
 *
 * The projection has the same three lines (`projection.ts`), and they are not
 * imported from there: that module imports this one, so the cycle would make
 * the initialization order of the two modules depend on which was loaded
 * first. The helper is small enough to be worth owning twice.
 */
function yieldToEventLoop(): Promise<void> {
  return new Promise((resolve) => {
    scheduleImmediate(resolve)
  })
}

/**
 * Bring a store file up to this build's schema, without blocking the event
 * loop.
 *
 * <remarks>
 * The store's constructor migrates in one blocking pass: the v21 -> v22 step
 * walks every session in the corpus, parsing and rewriting each payload, and
 * on the live corpus that is tens of megabytes of JSON in one go. An opening
 * server is blocked for the whole of it — no handler answers, and no CLI
 * caller sees progress, until the last session is written.
 *
 * This is the same migration, driven so the backfill hands the event loop
 * back between sessions. The step stays one transaction: the transaction and
 * its write lock live on the SQLite connection, not on the JavaScript stack,
 * so a yield between two sessions neither commits nor rolls back, and a
 * failure still rolls the whole step back. The connection is this function's
 * own and is never handed out, so no other writer can join the transaction
 * through it; another connection cannot write at all while the lock is held.
 *
 * The store is left at this build's schema version, so opening it afterwards
 * — with the constructor, or with anything else — finds the migration done.
 * Migrate before opening a store on the same file, not while one is open:
 * the migration holds the write lock for the whole pass.
 */
export async function migrateCanonDatabase(
  path: string,
  options: MigrationOptions = {},
): Promise<void> {
  const db = openCanonDatabase(path)
  try {
    const existing = readSchemaVersion(db)
    // A store with no version stamp is a fresh one: the constructor creates
    // its schema, and there is nothing to migrate.
    if (existing === undefined) return
    const from = Number(existing)
    if (from > SCHEMA_VERSION) {
      throw new Error(schemaVersionRefusal(path, from, SCHEMA_VERSION))
    }
    const yieldToLoop = options.yield ?? yieldToEventLoop
    for (let version = from; version < SCHEMA_VERSION; version += 1) {
      const step = MIGRATIONS[version]
      if (step === undefined) {
        throw new Error(
          `canon store at ${path} is at schema version ${version} with no migration ` +
            `to ${version + 1}; rebuild the store`,
        )
      }
      db.exec('BEGIN')
      try {
        // The step may hand the event loop back between sessions. The
        // transaction stays open across the await: it belongs to the
        // connection, and this connection belongs to this function.
        await step(db, { yield: yieldToLoop })
        db
          .prepare('INSERT OR REPLACE INTO metadata (key, value) VALUES (?, ?)')
          .run('schema_version', String(version + 1))
        db.exec('COMMIT')
      } catch (err) {
        db.exec('ROLLBACK')
        throw err
      }
    }
    // The same order the constructor uses: the version steps first, then the
    // full schema, whose statements are all `IF NOT EXISTS` and are genuine
    // no-ops by then. The file is left in the shape this build expects, so
    // opening it afterwards finds everything in place.
    db.exec(SCHEMA_SQL)
    // The covering index, and the statistics for it when this pass created
    // it: an index created after the last analysis carries no statistics, and
    // the planner then keeps choosing a different index for the sessions
    // list.
    if (createSessionDenormIndex(db)) db.exec('ANALYZE session')
  } finally {
    db.close()
  }
}

/** The schema version a database records, or undefined when it records none. */
function readSchemaVersion(db: Database): string | undefined {
  const row = db.prepare("SELECT value FROM metadata WHERE key = 'schema_version'").get() as
    | { value: string }
    | undefined
  return row?.value
}

export class CanonStore {
  private readonly db: Database
  private readonly upsertStatement: StatementSync

  /**
   * Whether the turn table has been materialized on this handle.
   *
   * The turn table is not part of `SCHEMA_SQL`: it exists where turns are, and
   * a store that has never held a turn row does not carry it. That is what
   * keeps a store that predates the turn table the store it was, instead of
   * growing a table from the build that happens to open it.
   */
  private sessionTurnTableReady = false

  /**
   * Opens (or creates) the store at `path` and brings its schema up to
   * `SCHEMA_VERSION`. `:memory:` gives a throwaway store for tests. A store
   * whose recorded schema version does not match this build refuses to open —
   * that is how a database built by an older version is detected rather than
   * silently misread.
   */
  constructor(path: string) {
    this.db = openCanonDatabase(path)

    // Order matters. `SCHEMA_SQL` is every statement in its current shape,
    // and some of them — an index over a column a later version added —
    // cannot run against an older store. So: establish `metadata`, read the
    // version, migrate the old shapes forward, and only then apply the full
    // schema, by which point every `IF NOT EXISTS` is a genuine no-op.
    const existing = this.getMetadata('schema_version')
    if (existing !== undefined && Number(existing) !== SCHEMA_VERSION) {
      this.migrate(path, Number(existing))
    }

    this.db.exec(SCHEMA_SQL)

    // The covering index for the sessions list, created here so a fresh store
    // is born with it: it lives outside `SCHEMA_SQL` because its DDL is
    // unconditional and a store whose session table predates the columns it
    // covers cannot carry it. Presence-guarded, so this is a no-op on every
    // open once the index exists.
    const createdDenormIndex = createSessionDenormIndex(this.db)

    if (existing === undefined) {
      this.db
        .prepare('INSERT INTO metadata (key, value) VALUES (?, ?)')
        .run('schema_version', String(SCHEMA_VERSION))
      this.db
        .prepare('INSERT INTO metadata (key, value) VALUES (?, ?)')
        .run('detector_version', String(DETECTOR_VERSION))
    }

    if (existing === undefined || createdDenormIndex) {
      // The planner's cost information for `session`, written from creation
      // rather than whenever an optimizer happens to run: real statistics for
      // the covering index and the table as the corpus grows, and the same
      // plan on every path — which is what the contract's EXPLAIN pin
      // asserts. The covering index is the mechanism, not these statistics:
      // on this build the planner already picks it with no `sqlite_stat1`
      // rows at all.
      //
      // A fresh store's session table is empty and SQLite skips empty
      // tables, so this creates `sqlite_stat1` without a row for `session`;
      // the close-time `PRAGMA optimize` populates it once rows exist.
      //
      // A store that reached this open without the covering index, and got it
      // here from the constructor's own creation path, is analyzed for the
      // same reason: an index created after the last analysis carries no
      // statistics, and without them the planner keeps choosing
      // `session_by_started` over the covering index — a non-covering scan
      // that reads every row's payload. The analysis is the same one the
      // v21 -> v22 migration writes for a store that arrives without it.
      this.db.exec('ANALYZE session')
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
      throw new Error(schemaVersionRefusal(path, from, SCHEMA_VERSION))
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
        const outcome = step(this.db)
        if (outcome instanceof Promise) {
          // The synchronous open path cannot commit a step that yields: the
          // transaction would close before the step finished. No step
          // returns a promise here — the only step that can yields does so
          // only when a caller hands it a yield seam, and this path never
          // does — so this is a guard, not a branch.
          void outcome.catch((asyncErr: unknown) => {
            // The sync path cannot await the step, so its rejection would
            // otherwise vanish behind the guard below. Report it rather than
            // swallowing it with a no-op.
            console.error(
              `canon store migration step ${version} -> ${version + 1} rejected asynchronously: ` +
                `${asyncErr instanceof Error ? (asyncErr.stack ?? asyncErr.message) : String(asyncErr)}`,
            )
          })
          throw new Error(
            `canon store migration step ${version} -> ${version + 1} is asynchronous; ` +
              'the synchronous open path cannot run it',
          )
        }
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
    this.writeRecords(() => {
      for (const record of records) this.writeRecord(record)
    })
  }

  private writeRecord(record: CanonicalRecord): void {
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
      // rather than stored verbatim (R12.4). The column holds the deflated
      // form, and the plain form only where deflating it would not pay.
      record.parts === undefined ? null : compressColumn(record.parts),
      record.raw === undefined ? null : compressRaw(record.raw),
    )
  }

  private writeProvenance(row: RecordProvenance): void {
    this.db
      .prepare(
        `INSERT OR REPLACE INTO record_provenance (
           span_id, harness_id, source_key, native_session_id, native_record_id,
           source_revision, parser_version, imported_at_utc, location_token
         ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      )
      .run(
        row.spanId,
        row.harnessId,
        row.sourceKey,
        row.nativeSessionId,
        row.nativeRecordId,
        row.sourceRevision,
        row.parserVersion,
        row.importedAtUtc,
        row.locationToken,
      )
  }

  private writeCheckpoint(row: SourceCheckpoint): void {
    this.db
      .prepare(
        `INSERT OR REPLACE INTO source_checkpoint (
           harness_id, source_key, provider_id, parser_id, parser_contract_version,
           format, source_root_label, revision_token, covered_from_utc, covered_through_utc,
           last_attempt_utc, last_success_utc, last_status, last_error_code,
           unit_count, record_count
         ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      )
      .run(
        row.harnessId,
        row.sourceKey,
        row.providerId,
        row.parserId,
        row.parserContractVersion,
        row.format,
        row.sourceRootLabel,
        row.revisionToken,
        row.coveredFromUtc,
        row.coveredThroughUtc,
        row.lastAttemptUtc,
        row.lastSuccessUtc,
        row.lastStatus,
        row.lastErrorCode,
        row.unitCount,
        row.recordCount,
      )
  }

  /**
   * Persist accepted rows, their provenance, and the source-unit checkpoint
   * in one transaction. A failure rolls back all three so the last successful
   * checkpoint remains the resume point.
   */
  commitSourceUnit(input: {
    records: readonly CanonicalRecord[]
    provenance: readonly RecordProvenance[]
    checkpoint: SourceCheckpoint
  }): void {
    this.writeRecords(() => {
      for (const record of input.records) this.writeRecord(record)
      for (const row of input.provenance) this.writeProvenance(row)
      this.writeCheckpoint(input.checkpoint)
    })
  }

  /**
   * Open a refresh run, returning its id. Written before any work so a run that crashes
   * still leaves a row — an interrupted refresh is a fact the footer has to be able to
   * report, and a row only written on success could never describe one.
   */
  startRefreshRun(input: {
    id: string
    startedAt: string
    pid: number
    trigger: RefreshTrigger
    /**
     * The ingest window, in weeks, this run will cover. Optional so callers
     * that predate window tracking (and the orchestrator until T2) keep
     * compiling; an omitted window persists as NULL = unknown.
     */
    historyWeeks?: number | null
  }): string {
    this.db
      .prepare(
        `INSERT INTO refresh_run (id, started_at, completed_at, status, pid, trigger, summary, history_weeks)
         VALUES (?, ?, NULL, 'running', ?, ?, NULL, ?)`,
      )
      // The persistence seam validates: only a positive safe integer is a
      // window — 0, negatives, fractions, and non-finite values persist as
      // NULL (unknown), so no surface can render `last 0 weeks`.
      .run(input.id, input.startedAt, input.pid, input.trigger, normalizeHistoryWeeks(input.historyWeeks))
    return input.id
  }

  /**
   * Mark interrupted runs failed before a new refresh starts. An inaccessible
   * PID is treated as alive; only a PID known to be absent is reconciled.
   */
  reconcileDeadRefreshRuns(
    completedAt = new Date().toISOString(),
    maxAgeMs = REFRESH_MAX_AGE_MS,
  ): number {
    const running = this.db
      .prepare("SELECT id, pid, started_at FROM refresh_run WHERE status = 'running'")
      .all() as Array<{ id: string; pid: number; started_at: string }>
    const finish = this.db.prepare(
      "UPDATE refresh_run SET completed_at = ?, status = 'failure', summary = ? WHERE id = ? AND status = 'running'",
    )
    let reconciled = 0
    const nowMs = Date.parse(completedAt)
    for (const row of running) {
      const pid = Number(row.pid)
      const isAlive = refreshProcessIsAlive(pid)
      const startedMs = Date.parse(row.started_at)
      const ageMs = Number.isFinite(nowMs) && Number.isFinite(startedMs) ? nowMs - startedMs : 0
      const isStale = ageMs > maxAgeMs
      if (isAlive && !isStale) continue
      const summary = !isAlive
        ? `Refresh process ${pid} is no longer running (dead process); marked failed during reconciliation.`
        : `Refresh process ${pid} exceeded maximum run duration (${Math.round(ageMs / 1000)}s); marked failed during reconciliation.`
      const result = finish.run(completedAt, summary, row.id)
      reconciled += Number(result.changes)
    }
    return reconciled
  }

  /** Close a refresh run. `summary` is one line: what it did, or why it failed (R10.5). */
  completeRefreshRun(
    id: string,
    status: Exclude<RefreshRunStatus, 'running'>,
    completedAt: string,
    summary: string | null,
  ): void {
    this.db
      .prepare('UPDATE refresh_run SET completed_at = ?, status = ?, summary = ? WHERE id = ?')
      .run(completedAt, status, summary, id)
  }

  /**
   * The most recent run with the given status. The footer asks three separate "most
   * recent" questions — last success, last failure, one in progress — and they are
   * deliberately independent: a failure must not hide the last success (R10.5).
   */
  latestRefreshRun(status: RefreshRunStatus): RefreshRunRow | undefined {
    const row = this.db
      .prepare('SELECT * FROM refresh_run WHERE status = ? ORDER BY started_at DESC LIMIT 1')
      .get(status) as Record<string, unknown> | undefined
    if (row === undefined) return undefined
    const parsed = toRefreshRunRow(row)
    // Liveness only: a long refresh still holds the lock and stays visible. Age is
    // reconciliation's concern, not the footer's.
    if (status === 'running' && !refreshProcessIsAlive(parsed.pid)) return undefined
    return parsed
  }

  listRefreshRuns(limit = 20): RefreshRunRow[] {
    const rows = this.db
      .prepare('SELECT * FROM refresh_run ORDER BY started_at DESC LIMIT ?')
      .all(limit) as Record<string, unknown>[]
    return rows.map(toRefreshRunRow)
  }

  getSourceCheckpoint(harnessId: string, sourceKey: string): SourceCheckpoint | undefined {
    const row = this.db
      .prepare('SELECT * FROM source_checkpoint WHERE harness_id = ? AND source_key = ?')
      .get(harnessId, sourceKey) as SourceCheckpointRow | undefined
    return row === undefined ? undefined : toSourceCheckpoint(row)
  }

  listSourceCheckpoints(harnessId?: string): SourceCheckpoint[] {
    const rows = (
      harnessId === undefined
        ? this.db.prepare('SELECT * FROM source_checkpoint ORDER BY harness_id, source_key').all()
        : this.db
            .prepare(
              'SELECT * FROM source_checkpoint WHERE harness_id = ? ORDER BY source_key',
            )
            .all(harnessId)
    ) as SourceCheckpointRow[]
    return rows.map(toSourceCheckpoint)
  }

  /**
   * Mark a unit's checkpoint unusable after a parser-contract change.
   * Canonical rows and provenance stay; the next refresh reopens coverage.
   */
  invalidateSourceCheckpoint(
    harnessId: string,
    sourceKey: string,
    parserContractVersion: string,
  ): void {
    this.db
      .prepare(
        `UPDATE source_checkpoint
         SET parser_contract_version = ?, last_status = 'invalidated',
             last_error_code = 'PARSER_CONTRACT_CHANGED',
             last_attempt_utc = ?
         WHERE harness_id = ? AND source_key = ?`,
      )
      .run(parserContractVersion, new Date().toISOString(), harnessId, sourceKey)
  }

  getRecordProvenance(spanId: string): RecordProvenance | undefined {
    const row = this.db
      .prepare('SELECT * FROM record_provenance WHERE span_id = ?')
      .get(spanId) as RecordProvenanceRow | undefined
    return row === undefined ? undefined : toRecordProvenance(row)
  }

  listProvenanceForSource(harnessId: string, sourceKey: string): RecordProvenance[] {
    const rows = this.db
      .prepare(
        'SELECT * FROM record_provenance WHERE harness_id = ? AND source_key = ? ORDER BY span_id',
      )
      .all(harnessId, sourceKey) as RecordProvenanceRow[]
    return rows.map(toRecordProvenance)
  }

  /**
   * Live canonical rows attributed to a source unit. Provenance outlives
   * quarantine (the audit row stays; the record does not), so a contract-bump
   * reconciliation that counted provenance would bake deleted spans into
   * `recordCount`. This join is the population the checkpoint actually means.
   */
  countLiveRecordsForSource(harnessId: string, sourceKey: string): number {
    const row = this.db
      .prepare(
        `SELECT COUNT(*) AS n
         FROM record_provenance p
         INNER JOIN records r ON r.span_id = p.span_id
         WHERE p.harness_id = ? AND p.source_key = ?`,
      )
      .get(harnessId, sourceKey) as { n: unknown }
    return typeof row.n === 'number' ? row.n : Number(row.n) || 0
  }

  spanIdsForSource(harnessId: string, sourceKey: string): string[] {
    return (
      this.db
        .prepare(
          'SELECT span_id FROM record_provenance WHERE harness_id = ? AND source_key = ? ORDER BY span_id',
        )
        .all(harnessId, sourceKey) as { span_id: string }[]
    ).map((row) => row.span_id)
  }

  findByNativeIdentity(
    harnessId: string,
    nativeSessionId: string,
    nativeRecordId: string,
  ): CanonicalRecord | undefined {
    const row = this.db
      .prepare(
        `SELECT r.* FROM records r
         INNER JOIN record_provenance p ON p.span_id = r.span_id
         WHERE p.harness_id = ? AND p.native_session_id = ? AND p.native_record_id = ?`,
      )
      .get(harnessId, nativeSessionId, nativeRecordId) as RecordRow | undefined
    return row === undefined ? undefined : toRecord(row)
  }

  recordsForHarnessSession(harness: string, sessionId: string): CanonicalRecord[] {
    const rows = this.db
      .prepare(
        'SELECT * FROM records WHERE harness = ? AND session_id = ? ORDER BY timestamp',
      )
      .all(harness, sessionId) as RecordRow[]
    return rows.map(toRecord)
  }

  recordsForHarnessWindow(harness: string, fromUtc: string, throughUtc: string): CanonicalRecord[] {
    const rows = this.db
      .prepare(
        `SELECT * FROM records
         WHERE harness = ? AND timestamp >= ? AND timestamp <= ?
         ORDER BY timestamp`,
      )
      .all(harness, fromUtc, throughUtc) as RecordRow[]
    return rows.map(toRecord)
  }

  /** Fetch a record by span id, decompressing the raw payload; absent id gives undefined. */
  get(spanId: string): CanonicalRecord | undefined {
    const row = this.db
      .prepare('SELECT * FROM records WHERE span_id = ?')
      .get(spanId) as RecordRow | undefined
    return row === undefined ? undefined : toRecord(row)
  }

  /** Membership only — no payload decompression or JSON parse. */
  has(spanId: string): boolean {
    return this.db.prepare('SELECT 1 FROM records WHERE span_id = ? LIMIT 1').get(spanId) !== undefined
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
    this.writeRecords(() => {
      this.db
        .prepare('UPDATE records SET content_json = ?, parts_json = ? WHERE span_id = ?')
        .run(
          hasParts ? '{}' : JSON.stringify(content),
          hasParts ? compressColumn(parts) : null,
          spanId,
        )
    })
  }

  /**
   * Empty the content columns of every record older than `cutoffUtc`, in ONE
   * transaction and ONE set-based statement.
   *
   * WHY set-based: the purge used to walk every span id, read the record, and
   * write it back one row at a time — 9,740 expired rows measured in
   * production, each its own autocommit `UPDATE` and its own fsync. One
   * statement over the expired set does the same work in one transaction.
   *
   * The cutoff is an ISO instant and the stored timestamps are ISO instants
   * (`writeRecord` normalizes a `Date` on the way in, but passes a string
   * through verbatim), so the comparison is on instants — `datetime()` on
   * both sides — rather than on strings, which would retain a row like
   * `2026-08-29T13:00:00+02:00` past a noon-Z cutoff it predates.
   * `content_json` is `'{}'` for a record with no content and `parts_json`
   * holds the parts (the empty array stored as `'[]'`), so "has content" is
   * "either column holds a non-empty value" — an already-purged row, or one
   * that never held content, matches neither and is left alone, which keeps
   * the purge idempotent.
   *
   * The records-write marker moves only when a row actually changed: the
   * marker is the corpus's change signal, and a purge that changed nothing
   * must not look like a changed corpus.
   */
  purgeContentBefore(cutoffUtc: string): number {
    this.db.exec('BEGIN')
    try {
      const info = this.db
        .prepare(
          `UPDATE records SET content_json = '{}', parts_json = NULL
           WHERE datetime(timestamp) < datetime(?)
             AND ((content_json IS NOT NULL AND content_json <> '{}') OR (parts_json IS NOT NULL AND parts_json <> '[]'))`,
        )
        .run(cutoffUtc)
      const purged = typeof info.changes === 'number' ? info.changes : Number(info.changes) || 0
      if (purged > 0) this.bumpRecordsWriteMarker()
      this.db.exec('COMMIT')
      return purged
    } catch (err) {
      this.db.exec('ROLLBACK')
      throw err
    }
  }

  /**
   * Rewrite the cost blocks of several records in ONE transaction (projection-time repricing,
   * issue #186 U9). `cost_json` is the derived, re-derivable cost cache: a failure rolls every row
   * back and rethrows, and an empty list is a no-op.
   */
  setCosts(changes: ReadonlyArray<{ spanId: string; cost: CostBlock }>): void {
    if (changes.length === 0) return
    this.writeRecords(() => {
      const update = this.db.prepare('UPDATE records SET cost_json = ? WHERE span_id = ?')
      for (const change of changes) update.run(JSON.stringify(change.cost), change.spanId)
    })
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

  /**
   * Stored records naming one span as their parent, scoped by trace because
   * OTLP span ids are only unique within one. This is the back-fill
   * direction of `parent_span_id`: the parent itself need not be a record
   * (Claude Code's interaction span is quarantined), so an exact
   * `findByTraceSpan` on it can never see the children beneath it (B8).
   */
  childrenOf(traceId: string, parentSpanId: string): CanonicalRecord[] {
    const rows = this.db
      .prepare('SELECT * FROM records WHERE trace_id = ? AND parent_span_id = ?')
      .all(traceId, parentSpanId) as RecordRow[]
    return rows.map(toRecord)
  }

  /** Rewrite the fields re-normalization decides, leaving content untouched. */
  setAttribution(
    spanId: string,
    fields: { harness: string; source: string; op: string; tokens: TokenUsage },
  ): void {
    this.writeRecords(() => {
      this.db
        .prepare('UPDATE records SET harness = ?, source = ?, op = ?, tokens_json = ? WHERE span_id = ?')
        .run(fields.harness, fields.source, fields.op, JSON.stringify(fields.tokens), spanId)
    })
  }

  /**
   * Move a record out of the canonical corpus and into quarantine atomically.
   * Reclassification uses this for rows retained by an older ingest pass:
   * the audit entry and the removal cannot disagree after a partial write.
   */
  quarantineAndDelete(
    spanId: string,
    namespaces: readonly string[],
    reason: string,
    metadata?: QuarantineMetadata,
  ): void
  quarantineAndDelete(
    spanId: string,
    namespaces: readonly string[],
    reason: string,
    source?: string,
    name?: string,
    timestamp?: string,
  ): void
  quarantineAndDelete(
    spanId: string,
    namespaces: readonly string[],
    reason: string,
    sourceOrOptions?: string | QuarantineMetadata,
    name?: string,
    timestamp?: string,
  ): void {
    let sourceVal: string | null = null
    let nameVal: string | null = null
    let timestampVal: string | null = null
    let rawPayloadVal: string | null = null
    let harnessVal: string | null = null

    if (typeof sourceOrOptions === "object" && sourceOrOptions !== null) {
      sourceVal = sourceOrOptions.source ?? null
      nameVal = sourceOrOptions.name ?? null
      timestampVal = sourceOrOptions.timestamp ?? null
      rawPayloadVal = sourceOrOptions.rawPayload ?? null
      harnessVal = sourceOrOptions.harness ?? null
    } else if (typeof sourceOrOptions === "string") {
      sourceVal = sourceOrOptions
      nameVal = name ?? null
      timestampVal = timestamp ?? null
    }

    this.writeRecords(() => {
      this.db
        .prepare(
          "INSERT OR REPLACE INTO quarantine (span_id, namespaces, reason, source, name, timestamp, raw_payload, harness) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
        )
        .run(spanId, JSON.stringify(namespaces), reason, sourceVal, nameVal, timestampVal, rawPayloadVal, harnessVal)
      this.db.prepare("DELETE FROM records WHERE span_id = ?").run(spanId)
    })
  }

  /** Attach the harness's conversation id to a stored record. */
  setSessionId(spanId: string, sessionId: string | null): void {
    this.writeRecords(() => {
      this.db.prepare('UPDATE records SET session_id = ? WHERE span_id = ?').run(sessionId, spanId)
    })
  }

  /** Find one span by the exact OTLP correlation identity. */
  findByTraceSpan(traceId: string, spanId: string): CanonicalRecord | undefined {
    const row = this.db
      .prepare('SELECT * FROM records WHERE trace_id = ? AND span_id = ?')
      .get(traceId, spanId) as RecordRow | undefined
    return row === undefined ? undefined : toRecord(row)
  }

  /**
   * The matcher's view of every `claude-code` model call (`llm.invoke`) in one
   * trace: id, start and duration only. This is the candidate set for
   * correlating a Claude request-body log stamped with a parent span rather
   * than the call itself (D11); it reads through the `trace_id` index and never
   * scans the table. The chosen call is fetched whole with `get`, so scoring a
   * session never decompresses every candidate's raw payload and content.
   */
  claudeModelCallCandidatesInTrace(traceId: string): ModelCallCandidate[] {
    const rows = this.db
      .prepare(
        "SELECT span_id, timestamp, duration_ms FROM records WHERE trace_id = ? AND harness = 'claude-code' AND op = 'llm.invoke'",
      )
      .all(traceId) as CandidateRow[]
    return rows.map(toCandidate)
  }

  /**
   * The same lightweight candidates for one session — the candidate set for a
   * Claude request-body log that carries no trace id (D13). Reads through the
   * `session_id` index.
   */
  claudeModelCallCandidatesInSession(sessionId: string): ModelCallCandidate[] {
    const rows = this.db
      .prepare(
        "SELECT span_id, timestamp, duration_ms FROM records WHERE session_id = ? AND harness = 'claude-code' AND op = 'llm.invoke'",
      )
      .all(sessionId) as CandidateRow[]
    return rows.map(toCandidate)
  }

  /**
   * Store one entry under a key of a record's `raw` object without rewriting
   * the rest of the row. A record with no `raw` gets a fresh object; a `raw`
   * that is not a plain object is left alone and the call reports `false`.
   */
  setRawEntry(spanId: string, key: string, value: unknown): boolean {
    const existing = this.get(spanId)
    if (existing === undefined) return false
    const raw: unknown = existing.raw
    if (raw !== undefined && (typeof raw !== 'object' || raw === null || Array.isArray(raw))) return false
    const next = { ...(raw as Record<string, unknown> | undefined), [key]: value }
    this.writeRecords(() => {
      this.db.prepare('UPDATE records SET raw = ? WHERE span_id = ?').run(compressRaw(next), spanId)
    })
    return true
  }

  /**
   * Find the nearest record in a session, bounded to avoid accidental joins.
   * `op` narrows the candidate set: the log-correlation fallback is only
   * allowed to land on a model call (R7) — an unfiltered nearest pick hands
   * the log to whatever tool span happened to share the window — so callers
   * pass `'llm.invoke'`. Without it any record in the window is eligible.
   */
  findBySessionTime(sessionId: string, timestamp: string, windowMs = 5_000, op?: string): CanonicalRecord | undefined {
    const rows = this.db
      .prepare('SELECT * FROM records WHERE session_id = ?')
      .all(sessionId) as RecordRow[]
    const target = Date.parse(timestamp)
    return rows
      .map(toRecord)
      .filter((record) =>
        (op === undefined || record.op === op) &&
        Math.abs(Date.parse(String(record.timestamp)) - target) <= windowMs)
      .sort((a, b) => Math.abs(Date.parse(String(a.timestamp)) - target) - Math.abs(Date.parse(String(b.timestamp)) - target))[0]
  }

  /** Merge log evidence into a record without touching model counters. */
  enrich(spanId: string, log: { body?: unknown; attributes?: Record<string, unknown>; sessionId?: string | null }): boolean {
    const existing = this.get(spanId)
    if (existing === undefined) return false
    const content = { ...existing.content } as Record<string, unknown>
    if (log.body !== undefined && log.body !== null) content.log_body = log.body
    if (log.attributes !== undefined) Object.assign(content, log.attributes)
    this.writeRecords(() => {
      this.db.prepare('UPDATE records SET content_json = ?, session_id = COALESCE(session_id, ?) WHERE span_id = ?')
        .run(JSON.stringify(content), log.sessionId ?? null, spanId)
    })
    return true
  }

  /**
   * Rewrite one record's per-metric availability map (B6). Log enrichment
   * uses it in both directions: a bucket whose parts just arrived drops its
   * `not_measurable` entry, and a bucket a recognised event could not supply
   * gains the reason it stayed unmeasured. An absent or empty map stores
   * NULL — no stated limitation.
   */
  setMeasurability(spanId: string, measurability: Measurability | undefined): void {
    const json =
      measurability === undefined || Object.keys(measurability).length === 0
        ? null
        : JSON.stringify(measurability)
    this.writeRecords(() => {
      this.db
        .prepare('UPDATE records SET measurability_json = ? WHERE span_id = ?')
        .run(json, spanId)
    })
  }

  /**
   * Hold a log whose span has not arrived. `harness` is the recognition tag
   * (B3) the v19 column exists for — it scopes the row in a per-harness
   * wipe — and stays NULL for logs no signal named, never guessed.
   */
  addPendingLog(log: { logId: string; pendingSince?: number }, harness?: string): void {
    this.db.prepare('INSERT OR REPLACE INTO pending_logs (log_id, payload, harness) VALUES (?, ?, ?)')
      .run(log.logId, JSON.stringify(log), harness ?? null)
  }

  deletePendingLog(logId: string): void {
    this.db.prepare('DELETE FROM pending_logs WHERE log_id = ?').run(logId)
  }

  getPendingLogs(): unknown[] {
    return (this.db.prepare('SELECT payload, harness FROM pending_logs ORDER BY log_id').all() as { payload: string; harness: unknown }[])
      .map((row) => {
        const entry: unknown = JSON.parse(row.payload)
        // The v19 column is the tag's authority; surface it on the returned
        // entry so a reader sees what a per-harness wipe would scope without
        // unpicking the stored payload.
        if (
          typeof row.harness === 'string' &&
          entry !== null &&
          typeof entry === 'object' &&
          !Array.isArray(entry)
        ) {
          (entry as Record<string, unknown>).harness = row.harness
        }
        return entry
      })
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

  /** Same `harness` contract as `addPendingLog`: the tag scopes the row in a per-harness wipe. */
  quarantineLog(log: { logId: string }, reason: string, harness?: string): void {
    this.db.prepare('INSERT OR REPLACE INTO quarantined_logs (log_id, payload, reason, harness) VALUES (?, ?, ?, ?)')
      .run(log.logId, JSON.stringify(log), reason, harness ?? null)
  }

  isLogEnriched(logId: string): boolean {
    return this.db.prepare('SELECT 1 FROM enriched_logs WHERE log_id = ?').get(logId) !== undefined
  }

  markLogEnriched(logId: string): void {
    this.db.prepare('INSERT OR IGNORE INTO enriched_logs (log_id) VALUES (?)').run(logId)
  }

  getQuarantinedLog(logId: string): QuarantinedLog | undefined {
    const row = this.db.prepare('SELECT payload, reason, harness FROM quarantined_logs WHERE log_id = ?')
      .get(logId) as { payload: string; reason: string; harness: unknown } | undefined
    if (row === undefined) return undefined
    const entry: QuarantinedLog = { logId, reason: row.reason, log: JSON.parse(row.payload) }
    // Same surfacing rule as `getPendingLogs`: the column is the authority,
    // exposed beside the payload rather than written back into it.
    if (typeof row.harness === 'string') entry.harness = row.harness
    return entry
  }

  /**
   * Quarantined logs tagged with `harness` whose rowid is above `afterRowid`,
   * lowest rowid first, at most `limit`. The rowid is the insertion cursor a
   * one-shot pass stores so it never re-reads rows it has already passed.
   *
   * A page stops once `maxPayloadBytes` of payload have been read, but always
   * holds at least one row so a single oversized payload cannot stall the
   * cursor. Sizes are read first, so a row past the cap is never loaded. A
   * payload that does not parse is returned flagged `unreadable` rather than
   * thrown or dropped: the caller must still move its cursor past it, and a
   * throw here would pin the cursor below every later row.
   */
  quarantinedLogsAfter(
    harness: string,
    afterRowid: number,
    limit: number,
    maxPayloadBytes: number = DEFAULT_QUARANTINE_PAGE_BYTES,
  ): { rowid: number; logId: string; log: unknown; unreadable?: true }[] {
    const sizes = this.db
      .prepare('SELECT rowid AS rowid, log_id, LENGTH(CAST(payload AS BLOB)) AS bytes FROM quarantined_logs WHERE harness = ? AND rowid > ? ORDER BY rowid LIMIT ?')
      .all(harness, afterRowid, limit) as { rowid: number; log_id: string; bytes: number }[]
    const readPayload = this.db.prepare('SELECT payload FROM quarantined_logs WHERE rowid = ?')
    const page: { rowid: number; logId: string; log: unknown; unreadable?: true }[] = []
    let total = 0
    for (const size of sizes) {
      if (page.length > 0 && total + size.bytes > maxPayloadBytes) break
      total += size.bytes
      const row = readPayload.get(size.rowid) as { payload: string } | undefined
      if (row === undefined) continue
      try {
        page.push({ rowid: size.rowid, logId: size.log_id, log: JSON.parse(row.payload) as unknown })
      } catch {
        page.push({ rowid: size.rowid, logId: size.log_id, log: undefined, unreadable: true })
      }
    }
    return page
  }

  /** Remove one quarantined log, for example once it has been correlated. */
  deleteQuarantinedLog(logId: string): void {
    this.db.prepare('DELETE FROM quarantined_logs WHERE log_id = ?').run(logId)
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
    // One row per key, not per (key, harness) pair. A record keeps the provider
    // entry that produced it, which is the right provenance to store, but
    // everything derived from records is keyed by the canonical harness: one
    // harness reached through two front ends (Claude Code's transcripts and its
    // OTLP export, Cursor and Cursor Agent) is one harness.
    //
    // Grouping by the raw harness and canonicalizing afterwards deduplicated
    // the label but not the rows, so a conversation whose spans arrived under
    // both `cursor` and `cursor-agent` came back twice — 1,899 rows for 1,481
    // keys on the measured corpus. Every caller then loaded that session's
    // records again and rebuilt the identical row (31% of all record reads),
    // and `buildRuns` produced two execution candidates sharing one execution
    // id, so one of the two runs it planned lost its execution to the other.
    //
    // The harness reported here is the earliest record's, which is the one
    // `buildSessionRow` stamps on the session, so the derived tables agree.
    const rows = this.db
      .prepare(
        `SELECT key, harness, started, ended FROM (
           SELECT COALESCE(session_id, trace_id) AS key,
                  harness,
                  MIN(timestamp) OVER (PARTITION BY COALESCE(session_id, trace_id)) AS started,
                  MAX(timestamp) OVER (PARTITION BY COALESCE(session_id, trace_id)) AS ended,
                  ROW_NUMBER() OVER (
                    PARTITION BY COALESCE(session_id, trace_id) ORDER BY timestamp
                  ) AS rn
           FROM records
           WHERE COALESCE(session_id, trace_id) IS NOT NULL
         )
         WHERE rn = 1
         ORDER BY started DESC`,
      )
      .all() as { key: string; harness: string; started: string; ended: string }[]
    return rows.map((row) => ({ ...row, harness: normalizeHarnessName(row.harness) }))
  }

  /**
   * Derived sessions one at a time, newest first.
   *
   * Materializing every session row at once held the whole table's payloads in
   * memory. The rollup only reduces each one to a handful of scalars, and the
   * payloads are the largest objects in the store — 995 MB of JSON on the
   * measured corpus, one session of it 270 MB — so building that array took the
   * rebuild's heap high-water mark to 4.4 GB. Streaming keeps one payload live
   * at a time.
   */
  *iterateSessions(harnessId?: string): Generator<SessionRow> {
    const rows =
      harnessId === undefined
        ? this.db.prepare('SELECT * FROM session ORDER BY started DESC').iterate()
        : this.db
            .prepare('SELECT * FROM session WHERE harness = ? ORDER BY started DESC')
            .iterate(harnessId)
    for (const row of rows) yield toSessionRow(row as SessionDbRow)
  }

  /**
   * Executions belonging to one harness. The rollup previously read every
   * execution in the store and filtered in memory, once per harness.
   */
  listExecutionsByHarness(harness: string): ExecutionRow[] {
    const rows = this.db
      .prepare('SELECT * FROM execution WHERE harness = ? ORDER BY started, execution_id')
      .all(harness) as ExecutionDbRow[]
    return rows.map(toExecutionRow)
  }

  /**
   * A session's measured input and output totals, read out of the materialized
   * `summary_json` column, with the stored payload as the legacy fallback.
   *
   * The delegation-overhead dimension needs two numbers per execution and was
   * parsing the whole payload for each — the most expensive way to read
   * `summary.total_input`. The `session` table now carries that summary as the
   * `summary_json` column, written from the same payload in the same statement
   * (see `upsertSession`), so the common case answers from a small column and
   * never reads the payload blob.
   *
   * A row written before the materialization existed carries NULL there, and
   * only such a row falls back to the payload — per row, not per query, so a
   * mixed corpus leaves the materialized rows alone and reads each legacy
   * row's payload once.
   *
   * A total the session recorded as not measurable is not a number, and comes
   * back here as `undefined` rather than a coerced 0.
   */
  sessionTokenTotals(sessionId: string): { input: number; output: number } | undefined {
    const row = this.db
      .prepare(
        // The column first, and the same extraction the payload path used:
        // `summary_json` holds the same `$.summary` object the payload holds,
        // so reading `$.total_input` out of it answers exactly what
        // `$.summary.total_input` out of the payload answers, without the
        // blob. `payload` is not named here at all — naming it, even inside
        // `json_extract`, makes SQLite read and parse the whole payload of
        // every row this read touches.
        `SELECT summary_json,
                json_extract(summary_json, '$.total_input') AS input,
                json_extract(summary_json, '$.total_output') AS output
         FROM session WHERE session_id = ?`,
      )
      .get(sessionId) as { summary_json: unknown; input: unknown; output: unknown } | undefined
    if (row === undefined) return undefined
    // NULL is the "not materialized" sentinel: this row predates the
    // materialization, so its totals still live in its payload.
    if (row.summary_json === null || row.summary_json === undefined) {
      return this.sessionTokenTotalsFromPayload(sessionId)
    }
    // Open thread harnesses.ts:416 — a total the session recorded as
    // not_measurable is an object, not a number. Coercing it to 0 invents a
    // measured zero for the delegation denominator; unknown stays unknown.
    if (typeof row.input !== 'number' || typeof row.output !== 'number') return undefined
    return {
      input: row.input,
      output: row.output,
    }
  }

  /**
   * One session's token totals, read out of its own stored payload.
   *
   * <remarks>
   * The legacy path: a row whose `summary_json` is NULL was written before
   * the materialization existed, so the payload is the only place its totals
   * can come from. The statement is scoped to the one id, so it reads one
   * row's payload and never the table's, and `json_extract` keeps the blob
   * inside SQLite — only the extracted fragment crosses into JS.
   * </remarks>
   */
  private sessionTokenTotalsFromPayload(
    sessionId: string,
  ): { input: number; output: number } | undefined {
    const row = this.db
      .prepare(
        `SELECT json_extract(payload, '$.summary.total_input') AS input,
                json_extract(payload, '$.summary.total_output') AS output
         FROM session WHERE session_id = ?`,
      )
      .get(sessionId) as { input: unknown; output: unknown } | undefined
    if (row === undefined) return undefined
    if (typeof row.input !== 'number' || typeof row.output !== 'number') return undefined
    return {
      input: row.input,
      output: row.output,
    }
  }

  /**
   * The connection behind the `token_cache` table, for the memo in
   * `tokens.ts`. The table is versioned by this store's schema while its
   * accessors live with tokenization, so the database is handed over rather
   * than wrapped in a pair of pass-through methods — the arrangement
   * `tokenize` already documents.
   */
  tokenCache(): TokenCacheStore {
    return this.db
  }

  /**
   * One span's harness-emitted attribute map, decompressed on demand.
   *
   * The timeline used to carry this map inline on every node, which meant the
   * derived session payload re-stored the whole raw corpus uncompressed: 266 MB
   * of one 264 MB payload, and very nearly all of the 995 MB the `session`
   * table held. The attributes live here already, deflated, so the inspector
   * fetches the one span it is showing instead of every session carrying all of
   * them. Reading one costs a primary-key seek and a single inflate.
   */
  spanAttributes(spanId: string): Record<string, unknown> | undefined {
    const row = this.db.prepare('SELECT raw FROM records WHERE span_id = ?').get(spanId) as
      | { raw: unknown }
      | undefined
    if (row === undefined || row.raw === null || row.raw === undefined) return undefined
    const raw = decompressRaw(row.raw as Uint8Array)
    return raw !== null && typeof raw === 'object' ? (raw as Record<string, unknown>) : undefined
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
   * One session's records, in timestamp order, each with the attributes the
   * derived views read.
   *
   * WHY this exists: a record's payload is the harness's whole span attribute
   * map, and the projection reads a handful of attributes out of it. On the
   * measured corpus one session held 10,933 model calls whose payloads
   * inflated and parsed to 1.5 GB, all of it live at once: `toRecord` makes
   * the payload a lazy property (the `defineLazy` pattern, for a 3.5 GB peak)
   * and that property CACHES what it computed, so the first reader of a
   * payload kept it alive for as long as the record was — and the record was
   * as long as the session. The session's records stay (the twin collapse, the
   * descendant collection and the subagent projection all need the whole
   * session); their payloads do not.
   *
   * The reduced attributes are handed over BESIDE the record rather than
   * written onto it, so a caller that can compare the two — the session
   * projection compares the timeline's evidence reading, which the payload's
   * string values decide — decides what to adopt. A caller with nothing to
   * compare adopts them directly: `record.raw = attributes` releases the
   * payload the reduction read.
   */
  *recordsForSessionProjected(key: string): Generator<ProjectedSessionRecord> {
    const rows = this.db
      .prepare(
        'SELECT * FROM records WHERE COALESCE(session_id, trace_id) = ? ORDER BY timestamp',
      )
      .iterate(key) as IterableIterator<RecordRow>
    for (const row of rows) {
      const record = toRecord(row)
      // The reduction reads the payload, so it is materialized here and
      // released as soon as the caller adopts the reduced attributes.
      yield { record, attributes: projectedAttributes(record.raw) }
    }
  }

  /**
   * Non-turn session-scope system prompts for the shared-context reader,
   * without materializing full records.
   *
   * <remarks>
   * The projection selects only the prompt-bearing content columns plus the
   * span timestamp in session order, so `raw` (the largest blob per span),
   * tokens, cost, and measurability never leave SQLite, and no
   * `CanonicalRecord` is built — unrelated rows contribute nothing but
   * their two content cells plus a short timestamp. Capture order, exact
   * texts, span ids, and per-row timestamps are preserved; budgeting and
   * truncation stay with the caller. The timestamp lets the caller keep only
   * prompts captured at or before the turn being shown: a prompt captured
   * after the selected turn must not be presented as part of what that
   * earlier turn saw. Rows with a missing or unparseable timestamp stay
   * included — the caller cannot prove they are future evidence, and
   * dropping them would invent a claim the store cannot support.
   * Turn spans are excluded by their canonical operation (`llm.invoke`, the
   * same predicate `isTurn` in `canon/sessions.ts` uses): a turn-derived
   * prompt missing from the payload turn list must never masquerade as an
   * unindexed session-scope prompt, and identity is never inferred from
   * text. The `excludeSpanId` filter stays as defense for turn spans the
   * caller already resolved.
   * </remarks>
   */
  systemPromptPartsForSession(
    sessionId: string,
    excludeSpanId?: string | Set<string>,
  ): Array<{ spanId: string; text: string; timestamp?: string }> {
    if (!sessionId) return []
    const rows = this.db
      .prepare(
        // Turn spans (`llm.invoke`) are skipped in SQL, so their (largest) parts blobs are
        // never read or inflated; a row cap would silently drop session prompts instead.
        `SELECT span_id, op, timestamp, content_json, parts_json FROM records
         WHERE COALESCE(session_id, trace_id) = ? AND COALESCE(op, '') <> 'llm.invoke'
         ORDER BY timestamp`,
      )
      .all(sessionId) as { span_id: unknown; op: unknown; timestamp: unknown; content_json: unknown; parts_json: unknown }[]
    const out: Array<{ spanId: string; text: string; timestamp?: string }> = []
    for (const row of rows) {
      if (row.op === 'llm.invoke') continue
      const spanId = String(row.span_id)
      if (typeof excludeSpanId === 'string' ? spanId === excludeSpanId : excludeSpanId?.has(spanId) === true) continue
      const timestamp = row.timestamp === null || row.timestamp === undefined ? undefined : String(row.timestamp)
      for (const text of systemPromptTextsFromContentColumns(row.content_json, row.parts_json)) {
        out.push(timestamp === undefined ? { spanId, text } : { spanId, text, timestamp })
      }
    }
    return out
  }

  /**
   * The persisted id of every canonical-harness share of every session key in
   * the store. `buildSessions`, `buildRuns` and `buildFindings` each take it
   * from here, so a session row, its execution and its findings agree on one
   * id even when a native key reads like a split share's id.
   */
  sessionIdentities(): SessionIdentities {
    const pairs = this.db
      .prepare(
        `SELECT DISTINCT COALESCE(session_id, trace_id) AS key, harness
         FROM records
         WHERE COALESCE(session_id, trace_id) IS NOT NULL`,
      )
      .all() as { key: string; harness: string }[]
    return new SessionIdentities(pairs)
  }

  /**
   * One canonical harness's share of a session key, in timestamp order: the
   * records a split share's row was built from, without its sibling's. The
   * harness is normalized first, so a raw provider name finds its share.
   */
  recordsForShare(key: string, harness: string): CanonicalRecord[] {
    const canonical = normalizeHarnessName(harness)
    return this.recordsForSession(key).filter((record) => canonicalHarnessId(record.harness) === canonical)
  }

  /**
   * Materialize the turn table on this handle, once.
   *
   * The table is created where turns are written and read: by the migration
   * that backfills them, by the session write path that stores a session, and
   * by the raw-handle accessor a consumer queries directly. `IF NOT EXISTS`
   * makes every one of those a no-op once the table is there, so the guard
   * below only saves the statement itself.
   */
  private ensureSessionTurnTable(): void {
    if (this.sessionTurnTableReady) return
    this.db.exec(SESSION_TURN_TABLE_SQL)
    this.sessionTurnTableReady = true
  }

  /**
   * Run a write as one unit, atomic whether or not a transaction is open.
   *
   * WHY a savepoint and not BEGIN/COMMIT: a write that lands inside a
   * caller's transaction must not open a second one -- SQLite refuses a
   * nested BEGIN -- and must not commit the caller's work either. A savepoint
   * nests inside an open transaction and stands alone outside one, so the
   * same write is atomic in both cases, and a failure rolls back only what
   * this call wrote.
   */
  private atomically(change: () => void): void {
    this.db.exec('SAVEPOINT kyber_write')
    try {
      change()
      this.db.exec('RELEASE SAVEPOINT kyber_write')
    } catch (err) {
      this.db.exec('ROLLBACK TO SAVEPOINT kyber_write')
      this.db.exec('RELEASE SAVEPOINT kyber_write')
      throw err
    }
  }

  /**
   * Store a derived session. These rows are a cache over `records`: dropping
   * them all and rebuilding loses nothing, which is why the payload is
   * replaced wholesale rather than merged.
   */
  upsertSession(row: SessionRow): void {
    // A session row and its turns are written together, so the session write
    // path materializes the table the turn rows go into: a store that holds a
    // session holds the table its turns live in.
    this.ensureSessionTurnTable()
    // The blob and the turn rows are one session, split in two: the row keeps
    // the session's metadata, the rows keep its turns. A payload with no
    // `turns` key is stored whole, byte for byte -- there was nothing to
    // split, so there is nothing to rewrite.
    const split = splitSessionTurns(JSON.stringify(row.payload))
    const payload = split.payload
    // One payload, one serialization: the stored blob and the three materialized
    // columns are derived from the same object, so the columns cannot drift
    // from the payload they describe.
    const { summary_json, problems_count } = materializeSessionSummary(payload)
    // The window provenance is materialized here as well, so a row written
    // after the v22 migration carries the same value the migration's backfill
    // would have written for it. A count that reads the column then finds the
    // row it would have found in the payload.
    const context_limit_source = materializeContextLimitSource(payload)

    this.atomically(() => {
      this.db
        .prepare(
          // An upsert, not INSERT OR REPLACE: a replace deletes the row, which would reset the
          // transcript sync columns (offset, status, settled_at) that the sync job owns. Those
          // five -- synced_at, sync_status, settled_at, transcript_path, transcript_offset --
          // belong to the sync job alone: this statement never names them, so a re-upsert
          // cannot disturb them.
          //
          // `payload`, `summary_json`, `problems_count` and `context_limit_source` are the
          // projection's, and this one statement writes all four: the columns take their new
          // values from `excluded`, so an insert and a re-upsert materialize exactly what the
          // stored payload holds.
          `INSERT INTO session (
             session_id, harness, label, is_subagent, parent_session,
             agent_name, repo, branch, started, ended, summary_json, problems_count,
             context_limit_source, payload
           ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
           ON CONFLICT(session_id) DO UPDATE SET
             harness = excluded.harness,
             label = excluded.label,
             is_subagent = excluded.is_subagent,
             parent_session = excluded.parent_session,
             agent_name = excluded.agent_name,
             repo = excluded.repo,
             branch = excluded.branch,
             started = excluded.started,
             ended = excluded.ended,
             summary_json = excluded.summary_json,
             problems_count = excluded.problems_count,
             context_limit_source = excluded.context_limit_source,
             payload = excluded.payload`,
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
          summary_json,
          problems_count,
          context_limit_source,
          payload,
        )

      // The turn rows are replaced wholesale -- delete, then insert -- exactly
      // the way the migration replaced them. The turn set is the session's, not
      // an accumulation of it: a re-upsert that only added rows would leave
      // the turns of the previous build behind, and a re-upsert that only
      // replaced them would leave two rows for one turn. Both are wrong, and
      // the index cannot catch the second one -- the table's index is
      // deliberately not unique -- so the delete comes first, every time.
      this.db.prepare('DELETE FROM session_turn WHERE session_id = ?').run(row.sessionId)
      if (split.turns.length > 0) {
        const insertTurn = this.db.prepare(
          'INSERT INTO session_turn (session_id, turn_index, turn_json) VALUES (?, ?, ?)',
        )
        for (const turn of split.turns) insertTurn.run(row.sessionId, turn.index, turn.stored)
      }
    })
  }

  /**
   * One derived session's payload, or undefined when it has not been built.
   *
   * <remarks>
   * The payload the store holds is the session's metadata; the turns live in
   * `session_turn` beside it. The read puts the two back together, because
   * this method is the ASAD contract the dashboard reads (ADR 0011), and the
   * bridge's copy of the same rule does the same: a session read here answers
   * with its turns, whether they are still inline in the blob or in the child
   * table.
   *
   * The two copies must answer alike (the both-copies rule): a session read
   * here answers with its turns, whether they are still inline in the blob or
   * in the child table, and the bridge's reader answers the same. The rule
   * they read with is one definition (`canon/session-read.ts`), and
   * `dash/src/server/session-read-contract.test.ts` pins that the two readers
   * answer alike for the same session.
   * </remarks>
   */
  getSessionPayload(sessionId: string): unknown | undefined {
    const assembled = this.readSessionWithTurns(sessionId)
    // `null` is "no such session"; `undefined` is "this handle cannot serve
    // the split read", and the whole blob is the fallback then.
    if (assembled === null) return undefined
    if (assembled !== undefined) return assembled
    const row = this.db.prepare(SESSION_PAYLOAD_SELECT).get(sessionId) as
      | { payload: string }
      | undefined
    if (row === undefined) return undefined
    const payload = JSON.parse(row.payload) as unknown
    if (typeof payload !== 'object' || payload === null || Array.isArray(payload)) return payload
    return this.attachSessionTurns(payload as Record<string, unknown>, sessionId)
  }

  /**
   * One session's payload with its turns back, read from the split store.
   *
   * <remarks>
   * `null` when the store holds no such session, and `undefined` when this
   * handle cannot serve the split read at all -- the caller's whole-payload
   * read is the fallback then.
   */
  private readSessionWithTurns(sessionId: string): Record<string, unknown> | null | undefined {
    let row: { payload_json?: unknown; turns_type?: unknown; turns_json?: unknown } | undefined
    try {
      row = this.db.prepare(SESSION_PAYLOAD_FRAGMENTS_SELECT).get(sessionId) as typeof row
    } catch (err) {
      // This handle cannot take the payload apart -- no JSON functions in its
      // SQLite build. Anything else (a locked store, I/O, corruption) is not
      // a missing capability and must not read as an empty session.
      if (err instanceof Error && /no such function/i.test(err.message)) return undefined
      throw err
    }
    if (row === undefined) return null
    const payload = parseJsonObject(row.payload_json)
    if (payload === undefined) return undefined

    // The turns: the payload's own when it still carries them, the split rows
    // when they have been moved out. The range scan is issued either way -- it
    // is one indexed lookup that answers whether the session has turn rows of
    // its own, and hands over every one of them when it does.
    //
    // Which of the two wins is the shared contract's rule, not this reader's:
    // the store's reader and the bridge's reader must answer alike, so the
    // decision lives in one place (`canon/session-read.ts`).
    const splitTurns = this.sessionTurnRows(sessionId)
    payload['turns'] = sessionTurnsOf(row.turns_type, row.turns_json, splitTurns)
    return payload
  }

  /**
   * One session's turns, read from the turns table in one range scan.
   *
   * <remarks>
   * `undefined` when the store carries no turns table at all: the table is
   * materialized on demand, so a store that never held a turn row may not
   * have one -- and a session in such a store has no turns anywhere. Any
   * other read failure is rethrown rather than served as an empty turn list.
   */
  private sessionTurnRows(sessionId: string): Array<Record<string, unknown>> | undefined {
    if (!this.hasSessionTurnTable()) return undefined
    let rows: Array<{ turn_json?: unknown }>
    try {
      rows = this.db.prepare(SESSION_TURNS_SELECT).all(sessionId) as Array<{ turn_json?: unknown }>
    } catch (err) {
      // The table is materialized on demand, so a race that drops it between
      // the existence check and the range scan reads as no turns. Anything
      // else (a locked store, I/O, corruption) must not launder a session
      // with turns on disk into one served as empty.
      if (err instanceof Error && /no such table/i.test(err.message)) return undefined
      throw err
    }
    const turns: Array<Record<string, unknown>> = []
    for (const row of rows) {
      // The turn row holds the deflated turn, and the plain JSON a row
      // written before the compression landed holds. Both are read.
      const turn = turnObjectOf(row.turn_json)
      // A row that does not parse is not a turn: it is dropped rather than
      // served as an empty object, which would read as "measured, and there
      // was nothing there".
      if (turn !== undefined) turns.push(turn)
    }
    return turns
  }

  /**
   * Attach the session's split-out turns to a payload that carries none.
   *
   * <remarks>
   * A payload that still carries its turns is left alone: those are its own
   * turns, and they are the fresh ones.
   */
  private attachSessionTurns(
    payload: Record<string, unknown>,
    sessionId: string,
  ): Record<string, unknown> {
    if (Array.isArray(payload['turns'])) return payload
    const turns = this.sessionTurnRows(sessionId)
    if (turns !== undefined) payload['turns'] = turns
    return payload
  }

  /**
   * Whether this handle carries the turn table.
   *
   * <remarks>
   * The table is materialized on demand -- by the migration that backfills the
   * turns, by the session write path that stores a session, and by the
   * raw-handle accessor a consumer queries directly -- so a store that never
   * held a turn row may not have one. The read path asks rather than creates:
   * a read that creates a table is a read with a side effect.
   */
  private hasSessionTurnTable(): boolean {
    const row = this.db
      .prepare("SELECT 1 FROM sqlite_master WHERE type='table' AND name = ?")
      .get('session_turn')
    return row !== undefined
  }

  /** Session ids currently built, for pruning a rebuild's leftovers. */
  builtSessionIds(): string[] {
    return (this.db.prepare('SELECT session_id FROM session').all() as { session_id: string }[]).map(
      (row) => row.session_id,
    )
  }

  /** Drop a derived session. Safe by construction: the row is a cache. */
  deleteSession(sessionId: string): void {
    // The turn rows go with the session: they are its turns, and nothing else
    // names them. Left behind, they are orphans a reader would still find.
    this.ensureSessionTurnTable()
    this.atomically(() => {
      this.db.prepare('DELETE FROM session_turn WHERE session_id = ?').run(sessionId)
      this.db.prepare('DELETE FROM session WHERE session_id = ?').run(sessionId)
    })
  }

  /** Drop derived sessions for a harness. */
  deleteSessionsByHarness(harness: string): void {
    // The turn rows go with their sessions, and the session rows are the only
    // thing that names them: the subquery finds the turns while the session
    // rows are still there to be found by.
    this.ensureSessionTurnTable()
    this.atomically(() => {
      this.db
        .prepare(
          'DELETE FROM session_turn WHERE session_id IN (SELECT session_id FROM session WHERE harness = ?)',
        )
        .run(harness)
      this.db.prepare('DELETE FROM session WHERE harness = ?').run(harness)
    })
  }

  wipeHarnesses(harnesses: readonly string[]): StoreWipeReport {
    const canonical = [...new Set(harnesses.map((h) => normalizeHarnessName(h)))]
    const rawNames = this.rawHarnessNamesFor(canonical)
    if (rawNames.length === 0) {
      this.stampClean(canonical)
      return { harnesses: canonical, records: 0, provenance: 0, checkpoints: 0 }
    }
    // Assigned inside the transaction, which either commits it or throws.
    let report!: StoreWipeReport
    this.ensureSessionTurnTable()
    this.writeRecords(() => {
      report = { harnesses: canonical, ...wipeHarnessRows(this.db, rawNames) }
    })
    // The stamp must survive the case the wipe throws: it never participates
    // in the wiped transaction.
    this.stampClean(canonical)
    return report
  }

  /**
   * Delete the whole corpus: every wipe-by-harness row plus quarantine,
   * problems, predictions, and the log-enrichment tables. The operational
   * audit survives: `ingest_log`, `refresh_run`, `metadata` (stamped),
   * `token_cache`, and the model-window catalog stay — `refresh_run` because
   * ADR 0032 D8 keeps pre-wipe rows as audit history, `metadata` because it
   * holds the shared settings as well as the schema stamps.
   *
   * The space is reclaimed after the delete transaction commits. Without it a
   * wipe empties the tables but leaves the file at whatever size it grew to,
   * which is what let a wiped store still occupy 442 MB on disk.
   */
  wipeAll(): StoreWipeReport {
    // Assigned inside the transaction, which either commits it or throws.
    let report!: StoreWipeReport
    // The turn table has to exist for the wipe to clear it: a store that
    // never held a session never had the table, and the wipe still has to
    // leave one behind that holds nothing.
    this.ensureSessionTurnTable()
    this.writeRecords(() => {
      const countAll = (table: string): number => {
        const info = this.db.prepare(`DELETE FROM ${table}`).run() as unknown as {
          changes?: unknown
        }
        return typeof info.changes === 'number' ? info.changes : Number(info.changes) || 0
      }
      report = {
        harnesses: ['*'],
        records: countAll('records'),
        provenance: countAll('record_provenance'),
        checkpoints: countAll('source_checkpoint'),
      }
      this.db.exec('DELETE FROM session')
      // The turn rows are the sessions' turns; with the session rows gone,
      // nothing names them. The whole wipe clears them with the sessions.
      this.db.exec('DELETE FROM session_turn')
      this.db.exec('DELETE FROM run')
      this.db.exec('DELETE FROM execution')
      this.db.exec('DELETE FROM harness_rollup')
      this.db.exec('DELETE FROM finding')
      this.db.exec('DELETE FROM prediction')
      this.db.exec('DELETE FROM problems')
      this.db.exec('DELETE FROM quarantine')
      this.db.exec('DELETE FROM pending_logs')
      this.db.exec('DELETE FROM quarantined_logs')
      this.db.exec('DELETE FROM enriched_logs')
    })
    this.stampClean(['*'])
    this.reclaimSpace()
    return report
  }

  /**
   * Return the file to the size its contents actually need. `VACUUM` rewrites
   * the database and cannot run inside a transaction, so it belongs after the
   * wipe commits. The WAL is truncated on both sides of it: before, so the
   * rewrite is not replaying a large sidecar; after, because in WAL mode
   * `VACUUM` writes the rewritten database back *through* the WAL, so the last
   * word on the sidecar has to come after it or the freed pages simply move
   * from one file to the other. On a `:memory:` store all three are no-ops.
   */
  private reclaimSpace(): void {
    this.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
    this.db.exec('VACUUM')
    this.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
  }

  /**
   * Raw stored `records.harness` / provenance `harness_id` values whose
   * canonical id is one of `canonical` — the fold expansion (`claude-desktop`
   * rows belong to a `claude-code` wipe) over both spellings.
   */
  private rawHarnessNamesFor(canonical: readonly string[]): string[] {
    if (canonical.length === 0) return []
    const wanted = new Set(canonical)
    const raw = new Set<string>()
    for (const table of ['records', 'record_provenance', 'source_checkpoint'] as const) {
      const column = table === 'records' ? 'harness' : 'harness_id'
      const rows = this.db.prepare(`SELECT DISTINCT ${column} AS name FROM ${table}`).all() as {
        name: unknown
      }[]
      for (const row of rows) {
        if (typeof row.name !== 'string' || row.name === '') continue
        if (wanted.has(row.name) || wanted.has(normalizeHarnessName(row.name))) raw.add(row.name)
      }
    }
    return [...raw]
  }

  /** Record that a user-initiated clean ran, outside any wiped transaction. */
  private stampClean(harnesses: readonly string[]): void {
    this.db
      .prepare('INSERT OR REPLACE INTO metadata (key, value) VALUES (?, ?)')
      .run('last_clean_at', new Date().toISOString())
    this.db
      .prepare('INSERT OR REPLACE INTO metadata (key, value) VALUES (?, ?)')
      .run('last_clean_scope', JSON.stringify(harnesses))
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

  /**
   * Narrow session-time columns for the coverage-window seam. Only
   * `(harness, started, ended)` cross the bridge — payload blobs are never
   * selected and never parsed — so the per-harness latest-time fold stays
   * cheap no matter how large session payloads grow. Uncapped by design:
   * every row participates and the maximum folds in JS epoch ms (a SQL MAX
   * over ISO strings mis-sorts `+02:00`-offset stamps).
   */
  listSessionTimeColumns(): Array<{ harness: unknown; started: unknown; ended: unknown }> {
    return this.db.prepare('SELECT harness, started, ended FROM session').all() as Array<{
      harness: unknown
      started: unknown
      ended: unknown
    }>
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
    // Canonical names, deduped: the raw record harness and its canonical name
    // would otherwise each get their own rollup row.
    return [...new Set(rows.map((r) => normalizeHarnessName(r.harness)))].sort()
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
        finding.estimatedWasteTokens ?? null,
        finding.recommendation,
        JSON.stringify(finding.errorBar ?? null),
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
  listFindings(runId?: string, sessionId?: string, options?: FindingsListOptions): Finding[] {
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
    // `detector_id` is a real column, so it filters in SQL. `harness` rides
    // in the finding payload (canon/findings.ts stamps it) and filters after
    // the round-trip below — no migration, and legacy rows without a harness
    // simply match no harness filter rather than every one.
    if (options?.detector !== undefined && options.detector !== '') {
      conditions.push('detector_id = ?')
      params.push(options.detector)
    }

    if (conditions.length > 0) {
      query += ` WHERE ${conditions.join(' AND ')}`
    }
    query += ' ORDER BY rank_score DESC, id ASC'

    const rows = this.db.prepare(query).all(...params) as FindingDbRow[]
    let findings = rows.map(toFinding)
    if (options?.harness !== undefined && options.harness !== '') {
      // One rule in both halves of the envelope (review): the request folds
      // the way derived values are stamped (`cursor-agent` is `cursor`).
      const want = normalizeHarnessName(options.harness)
      findings = findings.filter((finding) => {
        const have = (finding as { harness?: unknown }).harness
        return typeof have === 'string' && normalizeHarnessName(have) === want
      })
    }
    return findings
  }

  /** Sessions recorded per canonical harness (issue #191 review M2). Narrow
   * columns only — no payload touch — so coverage checks stay cheap. */
  countSessionsByHarness(): Record<string, number> {
    const rows = this.db
      .prepare('SELECT harness, COUNT(*) AS n FROM session GROUP BY harness')
      .all() as { harness: string; n: number }[]
    const counts: Record<string, number> = {}
    for (const row of rows) counts[row.harness] = row.n
    return counts
  }

  /** Canonical harness of one stored session, or undefined when absent. */
  sessionHarness(sessionId: string): string | undefined {
    const row = this.db
      .prepare('SELECT harness FROM session WHERE session_id = ?')
      .get(sessionId) as { harness: string } | undefined
    return row?.harness
  }

  /**
   * Sessions whose context window no source reported (issue #191, condition
   * 3): the count that keeps a suppressed-default findings list from reading
   * as "all clear". Reads the provenance the session builder persists
   * (`payload.context.contextLimitSource`); legacy payloads without it are
   * not counted either way.
   *
   * The provenance is read from the `context_limit_source` column schema v22
   * materialized it into, so the count stops paging every payload blob in the
   * table -- table-wide when no harness is given.
   */
  countUnknownWindowSessions(harness?: string): number {
    const conditions = [this.sessionWindowSourceCondition()]
    const params: string[] = []
    if (harness !== undefined && harness !== '') {
      conditions.push('harness = ?')
      params.push(normalizeHarnessName(harness))
    }
    const row = this.db
      .prepare(`SELECT COUNT(*) AS n FROM session WHERE ${conditions.join(' AND ')}`)
      .get(...params) as { n: number } | undefined
    return row?.n ?? 0
  }

  /**
   * The condition that selects the sessions whose context window no source
   * reported.
   *
   * Schema v22 materializes the window provenance into a `context_limit_source`
   * column, and the column carries the payload's `$.context.contextLimitSource`
   * verbatim, so the two paths count the same rows: a payload with no source
   * leaves the column NULL, and `NULL IN ('default', 'absent')` is not true,
   * so such a session is not counted either way.
   *
   * The payload expression stays the fallback for a handle whose session table
   * predates the column. The store migrates the handle it owns, so that is the
   * shape a caller that reaches the same database another way can present --
   * the fallback answers there instead of failing on a missing column.
   */
  private sessionWindowSourceCondition(): string {
    const columns = (this.db.prepare('PRAGMA table_info(session)').all() as { name: string }[]).map(
      (column) => column.name,
    )
    return columns.includes('context_limit_source')
      ? `context_limit_source IN ('default', 'absent')`
      : `json_extract(payload, '$.context.contextLimitSource') IN ('default', 'absent')`
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
   * Bounded batch of records matching the given harness names (case-insensitive).
   * Used for exclusion remediation sweeps without loading the full history into memory.
   */
  listRecordsByHarness(
    harnesses: readonly string[],
    limit = 500,
    sources?: readonly string[],
  ): import('./types.js').CanonicalRecord[] {
    if (harnesses.length === 0 || limit <= 0) return []
    const placeholders = harnesses.map(() => 'LOWER(harness) = ?').join(' OR ')
    const args: string[] = harnesses.map((h) => h.trim().toLowerCase())
    // A scoped remediation (renormalize --source) must not see rows from
    // sources it was not asked to touch; without the filter the sweep
    // quarantines every excluded-harness row in the database.
    let sourceClause = ''
    if (sources !== undefined && sources.length > 0) {
      sourceClause = ` AND (${sources.map(() => 'source = ?').join(' OR ')})`
      args.push(...sources)
    }
    const rows = this.db
      .prepare(`SELECT * FROM records WHERE (${placeholders})${sourceClause} ORDER BY span_id LIMIT ?`)
      .all(...args, limit) as RecordRow[]
    return rows.map(toRecord)
  }

  /**
   * Cost blocks for selected spans without inflating parts or raw payloads.
   * The run detail turn table (issue #183) needs each turn's priced figure;
   * selecting full records would decompress every record's raw span.
   */
  spanCosts(spanIds: readonly string[]): Array<{ spanId: string; cost: CostBlock }> {
    const uniqueIds = [...new Set(spanIds)].filter((id) => id.length > 0)
    const out: Array<{ spanId: string; cost: CostBlock }> = []
    const chunkSize = 900

    for (let offset = 0; offset < uniqueIds.length; offset += chunkSize) {
      const chunk = uniqueIds.slice(offset, offset + chunkSize)
      const placeholders = chunk.map(() => '?').join(', ')
      const rows = this.db
        .prepare(`SELECT span_id, cost_json FROM records WHERE span_id IN (${placeholders})`)
        .all(...chunk) as Array<{ span_id: unknown; cost_json: unknown }>

      for (const row of rows) {
        if (typeof row.span_id !== 'string') continue
        let cost: unknown
        try {
          cost = JSON.parse(String(row.cost_json))
        } catch {
          continue
        }
        if (typeof cost !== 'object' || cost === null) continue
        out.push({ spanId: row.span_id, cost: cost as CostBlock })
      }
    }

    return out
  }

  /**
   * Summary figures for selected sessions without parsing whole payloads.
   * The runs list (issue #183) needs every run's turn count, token totals and
   * priced cost; parsing each session payload would inflate megabytes per
   * row, so only the summary fields are extracted.
   *
   * The figures come out of the materialized `summary_json` column, and only
   * a row written before the materialization existed -- a row whose
   * `summary_json` is NULL -- falls back to its own payload. The read itself
   * lives in `readSessionSummaryFigures`, which the bridge's direct-DB path
   * uses as well, so the two copies of this read cannot drift apart.
   */
  sessionSummaryFigures(
    sessionIds: readonly string[],
  ): Array<{
    sessionId: string
    turnCount?: number
    totalInput?: number
    totalOutput?: number
    costStatus?: string
    costValue?: number
    costCurrency?: string
    costUsdLegacy?: number
  }> {
    return readSessionSummaryFigures(this.db, sessionIds)
  }

  /**
   * Cost facts for selected sessions without inflating or reading raw payloads.
   * The report needs only the indexed session key and the small cost block;
   * selecting full records here would decompress each record's raw span.
   */
  costContributionsForSessions(
    sessionIds: readonly string[],
  ): Array<{ sessionId: string; basis: string; status: string; value?: number }> {
    const uniqueIds = [...new Set(sessionIds)].filter((id) => id.length > 0)
    const contributions: Array<{ sessionId: string; basis: string; status: string; value?: number }> = []
    const chunkSize = 900

    for (let offset = 0; offset < uniqueIds.length; offset += chunkSize) {
      const chunk = uniqueIds.slice(offset, offset + chunkSize)
      const placeholders = chunk.map(() => '?').join(', ')
      const rows = this.db
        .prepare(
          `SELECT COALESCE(session_id, trace_id) AS session_key, cost_json
           FROM records WHERE COALESCE(session_id, trace_id) IN (${placeholders})`,
        )
        .all(...chunk) as Array<{ session_key: unknown; cost_json: unknown }>

      for (const row of rows) {
        if (typeof row.session_key !== 'string') continue
        let cost: unknown
        try {
          cost = JSON.parse(String(row.cost_json))
        } catch {
          continue
        }
        if (typeof cost !== 'object' || cost === null) continue
        const block = cost as { basis?: unknown; status?: unknown; value?: unknown }
        contributions.push({
          sessionId: row.session_key,
          basis: String(block.basis ?? 'unknown'),
          status: String(block.status ?? 'no_rate'),
          ...(typeof block.value === 'number' && Number.isFinite(block.value)
            ? { value: block.value }
            : {}),
        })
      }
    }

    return contributions
  }

  /**
   * Every record the OTLP path produced — those whose source is NOT in the
   * `codeburn/` file-source namespace.
   *
   * Filtering in SQL rather than after `listAll()` keeps the file records, and
   * their several-times-larger decompressed `parts`, off the heap entirely.
   */
  listOtlpSourced(): import('./types.js').CanonicalRecord[] {
    const rows = this.db
      .prepare(
        `SELECT * FROM records WHERE source IS NULL OR source NOT LIKE ? ESCAPE '\\' ORDER BY timestamp`,
      )
      .all(`${FILE_SOURCE_PREFIX}%`) as RecordRow[]
    return rows.map(toRecord)
  }

  /**
   * Stream the OTLP-sourced records one at a time, so a caller that needs a
   * fact from every record never holds them all.
   *
   * A refresh only needs each OTLP record's session key, but the key lives in
   * the compressed `raw` payload, so it has to decode each row to read it.
   * Materializing the whole side to do that cost ~3.3 GB resident on a corpus
   * whose OTLP rows compress to ~285 MB; yielding row by row lets each one be
   * collected as soon as its key has been read.
   */
  *streamOtlpSourced(): Generator<import('./types.js').CanonicalRecord> {
    const statement = this.db.prepare(
      `SELECT * FROM records WHERE source IS NULL OR source NOT LIKE ? ESCAPE '\\' ORDER BY span_id`,
    )
    for (const row of statement.iterate(`${FILE_SOURCE_PREFIX}%`)) {
      yield toRecord(row as unknown as RecordRow)
    }
  }

  /** Full records for the given span ids, in the order the ids are given. */
  recordsBySpanIds(spanIds: readonly string[]): import('./types.js').CanonicalRecord[] {
    if (spanIds.length === 0) return []
    const statement = this.db.prepare('SELECT * FROM records WHERE span_id = ?')
    const records: import('./types.js').CanonicalRecord[] = []
    for (const spanId of spanIds) {
      const row = statement.get(spanId) as RecordRow | undefined
      if (row !== undefined) records.push(toRecord(row))
    }
    return records
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

  /**
   * Hold a span out of the corpus; re-quarantining the same span replaces the entry.
   * `metadata.rawPayload` carries the exact incoming span JSON for the detail route;
   * omit it only when there is no incoming payload to keep (never a reconstruction).
   */
  quarantine(
    spanId: string,
    namespaces: readonly string[],
    reason: string,
    metadata?: QuarantineMetadata,
  ): void
  quarantine(
    spanId: string,
    namespaces: readonly string[],
    reason: string,
    source?: string,
    name?: string,
    timestamp?: string,
  ): void
  quarantine(
    spanId: string,
    namespaces: readonly string[],
    reason: string,
    sourceOrOptions?: string | QuarantineMetadata,
    name?: string,
    timestamp?: string,
  ): void {
    let sourceVal: string | null = null
    let nameVal: string | null = null
    let timestampVal: string | null = null
    let rawPayloadVal: string | null = null
    let harnessVal: string | null = null

    if (typeof sourceOrOptions === 'object' && sourceOrOptions !== null) {
      sourceVal = sourceOrOptions.source ?? null
      nameVal = sourceOrOptions.name ?? null
      timestampVal = sourceOrOptions.timestamp ?? null
      rawPayloadVal = sourceOrOptions.rawPayload ?? null
      harnessVal = sourceOrOptions.harness ?? null
    } else if (typeof sourceOrOptions === 'string') {
      sourceVal = sourceOrOptions
      nameVal = name ?? null
      timestampVal = timestamp ?? null
    }

    this.db
      .prepare(
        'INSERT OR REPLACE INTO quarantine (span_id, namespaces, reason, source, name, timestamp, raw_payload, harness) VALUES (?, ?, ?, ?, ?, ?, ?, ?)',
      )
      .run(spanId, JSON.stringify(namespaces), reason, sourceVal, nameVal, timestampVal, rawPayloadVal, harnessVal)
  }

  /** The quarantine entry for a span, with its parsed namespaces; absent id gives undefined. */
  getQuarantine(spanId: string): QuarantineEntry | undefined {
    const row = this.db
      .prepare(
        'SELECT span_id, namespaces, reason, source, name, timestamp, raw_payload FROM quarantine WHERE span_id = ?',
      )
      .get(spanId) as
      | {
          span_id: unknown
          namespaces: unknown
          reason: unknown
          source: unknown
          name: unknown
          timestamp: unknown
          raw_payload: unknown
        }
      | undefined
    if (row === undefined) return undefined
    return toQuarantineEntry(row)
  }

  /**
   * One quarantine row in the detail shape the route serves (B7): every
   * field present, absent values explicit null rather than a dropped key.
   * `listQuarantine`/`getQuarantine` stay the audit accessors; this is the
   * only read that surfaces a stored packet. `undefined` when no row names
   * the span.
   */
  getQuarantineEntry(spanId: string): QuarantineEntryDetail | undefined {
    const row = this.db
      .prepare(
        'SELECT span_id, namespaces, reason, source, name, timestamp, harness, raw_payload FROM quarantine WHERE span_id = ?',
      )
      .get(spanId) as
      | {
          span_id: unknown
          namespaces: unknown
          reason: unknown
          source: unknown
          name: unknown
          timestamp: unknown
          harness: unknown
          raw_payload: unknown
        }
      | undefined
    if (row === undefined) return undefined
    const rawPayload =
      row.raw_payload === null || row.raw_payload === undefined ? null : text(row.raw_payload)
    // One corrupt cell must not take the detail route down with a 500 —
    // this read is exactly where an operator looks when the store is
    // already suspect, so an unparseable namespaces blob reads as an empty
    // list while the row's other facts still answer.
    let namespaces: string[] = []
    try {
      const parsed: unknown = JSON.parse(text(row.namespaces))
      if (Array.isArray(parsed)) {
        namespaces = parsed.filter((n): n is string => typeof n === 'string')
      }
    } catch {}
    return {
      spanId: text(row.span_id),
      source: row.source === null || row.source === undefined ? null : text(row.source),
      name: row.name === null || row.name === undefined ? null : text(row.name),
      reason: text(row.reason),
      timestamp: row.timestamp === null || row.timestamp === undefined ? null : text(row.timestamp),
      namespaces,
      harness: row.harness === null || row.harness === undefined ? null : text(row.harness),
      rawPayload,
      evidenceAvailable: rawPayload !== null,
    }
  }

  /** Every quarantined span ordered by span id — the R6.3 view's row list. */
  listQuarantine(): QuarantineEntry[] {
    const rows = this.db
      .prepare(
        'SELECT span_id, namespaces, reason, source, name, timestamp, raw_payload FROM quarantine ORDER BY span_id',
      )
      .all() as {
      span_id: unknown
      namespaces: unknown
      reason: unknown
      source: unknown
      name: unknown
      timestamp: unknown
      raw_payload: unknown
    }[]
    return rows.map(toQuarantineEntry)
  }

  /**
   * Quarantine rows under one harness scope, paged. `harness` is a canonical id from
   * `parseHarnessFilter` (or `'unknown'` for unattributed sources); `undefined` means
   * unfiltered. Scoping matches the stored source grouping — the `service.name` the
   * export carried — never attribution, because quarantined spans have no harness by
   * construction. Ordered by span id, like `listQuarantine`.
   */
  listQuarantineFiltered(options: { harness?: string; limit: number; offset: number }): QuarantineEntry[] {
    const { where, params } = quarantineHarnessWhere(options.harness)
    const rows = this.db
      .prepare(
        `SELECT span_id, namespaces, reason, source, name, timestamp, raw_payload FROM quarantine${where} ORDER BY span_id LIMIT ? OFFSET ?`,
      )
      .all(...params, options.limit, options.offset) as {
      span_id: unknown
      namespaces: unknown
      reason: unknown
      source: unknown
      name: unknown
      timestamp: unknown
      raw_payload: unknown
    }[]
    return rows.map(toQuarantineEntry)
  }

  /** Quarantine row count under one harness scope, without materializing rows. */
  countQuarantineFiltered(harness?: string): number {
    const { where, params } = quarantineHarnessWhere(harness)
    return (this.db.prepare(`SELECT COUNT(*) AS n FROM quarantine${where}`).get(...params) as { n: number }).n
  }

  /** Total quarantined span count, without materializing the row list. */
  countQuarantine(): number {
    return (this.db.prepare('SELECT COUNT(*) AS n FROM quarantine').get() as { n: number }).n
  }

  /**
   * Quarantined-span counts by reason, aggregated in SQL (GROUP BY) so the
   * coverage seam never materializes the table to tally it. A null reason
   * groups as `'unknown'` — folded, never dropped.
   */
  quarantineCountsByReason(): Array<{ reason: string; count: number }> {
    const rows = this.db
      .prepare(
        `SELECT COALESCE(reason, 'unknown') AS reason, COUNT(*) AS n
         FROM quarantine GROUP BY reason ORDER BY n DESC, reason ASC`,
      )
      .all() as Array<{ reason: unknown; n: unknown }>
    return rows.map((row) => ({
      reason: typeof row.reason === 'string' && row.reason !== '' ? row.reason : 'unknown',
      count: typeof row.n === 'number' ? row.n : Number(row.n) || 0,
    }))
  }

  /**
   * Per-source record counts, aggregated in SQL (GROUP BY) so activity seams
   * never inflate the corpus through `listAll()` to tally it.
   */
  countBySource(): Map<string, number> {
    const rows = this.db
      .prepare('SELECT source, COUNT(*) AS n FROM records GROUP BY source')
      .all() as Array<{ source: unknown; n: unknown }>
    const counts = new Map<string, number>()
    for (const row of rows) {
      if (typeof row.source !== 'string') continue
      counts.set(row.source, typeof row.n === 'number' ? row.n : Number(row.n) || 0)
    }
    return counts
  }

  /**
   * Record a surfaced failure the system declines to guess about. `problem.stack` persists
   * only when the caller carried one (a throw); computed validation problems carry a
   * message and no stack, and must not claim one.
   */
  recordProblem(problem: SpanProblem): void {
    const problemKey = problemIdentity(problem.spanId, problem.code, problem.location ?? null)
    this.db
      .prepare(
        `INSERT INTO problems (span_id, problem_key, severity, code, message, location, session_id, harness, timestamp, stack)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
         ON CONFLICT(problem_key) DO UPDATE SET
           span_id = excluded.span_id,
           severity = excluded.severity,
           code = excluded.code,
           message = excluded.message,
           location = excluded.location,
           session_id = excluded.session_id,
           harness = excluded.harness,
           timestamp = excluded.timestamp,
           stack = excluded.stack`,
      )
      .run(
        problem.spanId,
        problemKey,
        problem.severity,
        problem.code,
        problem.message,
        problem.location ?? null,
        problem.sessionId ?? null,
        problem.harness ?? null,
        problem.timestamp ?? null,
        problem.stack ?? null,
      )
  }

  /** Recorded problems, optionally narrowed to one span; ordered as written. */
  getProblems(spanId?: string): SpanProblem[] {
    const rows = (
      spanId === undefined
        ? this.db
            .prepare(
              'SELECT id, span_id, severity, code, message, location, session_id, harness, timestamp, stack FROM problems',
            )
            .all()
        : this.db
            .prepare(
              'SELECT id, span_id, severity, code, message, location, session_id, harness, timestamp, stack FROM problems WHERE span_id = ?',
            )
            .all(spanId)
    ) as {
      id: unknown
      span_id: unknown
      severity: unknown
      code: unknown
      message: unknown
      location: unknown
      session_id: unknown
      harness: unknown
      timestamp: unknown
      stack: unknown
    }[]
    return rows.map(toSpanProblem)
  }

  /** One recorded problem by its row id; absent id gives undefined (the detail route's 404). */
  getProblem(id: number): (SpanProblem & { id: number }) | undefined {
    const row = this.db
      .prepare(
        'SELECT id, span_id, severity, code, message, location, session_id, harness, timestamp, stack FROM problems WHERE id = ?',
      )
      .get(id) as
      | {
          id: unknown
          span_id: unknown
          severity: unknown
          code: unknown
          message: unknown
          location: unknown
          session_id: unknown
          harness: unknown
          timestamp: unknown
          stack: unknown
        }
      | undefined
    if (row === undefined) return undefined
    return { ...toSpanProblem(row), id: Number(row.id) }
  }

  /**
   * Recorded problems under one harness scope, newest first. `harness` is a canonical id
   * from `parseHarnessFilter` (or `'unknown'`); `undefined` means unfiltered.
   */
  listProblemsFiltered(options: { harness?: string; limit: number; offset: number }): (SpanProblem & { id: number })[] {
    const { where, params } = problemsHarnessWhere(options.harness)
    const rows = this.db
      .prepare(
        `SELECT id, span_id, severity, code, message, location, session_id, harness, timestamp, stack FROM problems${where} ORDER BY id DESC LIMIT ? OFFSET ?`,
      )
      .all(...params, options.limit, options.offset) as {
      id: unknown
      span_id: unknown
      severity: unknown
      code: unknown
      message: unknown
      location: unknown
      session_id: unknown
      harness: unknown
      timestamp: unknown
      stack: unknown
    }[]
    return rows.map((row) => ({ ...toSpanProblem(row), id: Number(row.id) }))
  }

  /** Recorded problem count under one harness scope, without materializing rows. */
  countProblemsFiltered(harness?: string): number {
    const { where, params } = problemsHarnessWhere(harness)
    return (this.db.prepare(`SELECT COUNT(*) AS n FROM problems${where}`).get(...params) as { n: number }).n
  }

  /** Total recorded problem count, without loading problem details. */
  countProblems(): number {
    return (this.db.prepare('SELECT COUNT(*) AS n FROM problems').get() as { n: number }).n
  }

  /**
   * Reconcile source problems for one re-read source unit (#243): the set of
   * source-problem rows for `harness` + `sourceKey` becomes exactly the codes
   * in `keepCodes`, so a warning the reader no longer reports (a fixed
   * FUTURE_DATED timestamp, a repaired MALFORMED_TIMESTAMP) does not stay
   * forever after a clean re-import. Only rows whose span id is
   * `harness:${harness}:${sourceKey}:${code}` are in scope: ingest problems on
   * the bare `harness:id:sourceKey` span, the job row, TOKEN_* rows on real
   * spans, and any other harness or source key are left alone. Call only for a
   * unit the reader actually re-parsed — an unchanged unit skips the commit
   * path entirely, and its standing warnings with it.
   */
  reconcileSourceProblems(harness: string, sourceKey: string, keepCodes: readonly string[]): void {
    const prefix = `harness:${harness}:${sourceKey}:`
    const rows = this.db
      .prepare("SELECT span_id, code FROM problems WHERE span_id LIKE ? ESCAPE '\\'")
      .all(`${escapeLike(prefix)}%`) as Array<{ span_id: unknown; code: unknown }>
    const keep = new Set(keepCodes)
    const remove = this.db.prepare('DELETE FROM problems WHERE span_id = ? AND code = ?')
    for (const row of rows) {
      if (typeof row.span_id !== 'string' || typeof row.code !== 'string') continue
      if (!row.span_id.startsWith(prefix)) continue
      const rest = row.span_id.slice(prefix.length)
      if (rest.length === 0 || rest !== row.code) continue
      if (!keep.has(row.code)) remove.run(row.span_id, row.code)
    }
  }

  /** Append one ingest run to the audit log. */
  logIngest(source: string, count: number): void {
    this.db
      .prepare('INSERT INTO ingest_log (source, count, timestamp) VALUES (?, ?, ?)')
      .run(normalizeIngestSource(source), count, new Date().toISOString())
  }

  /**
   * Per-source ingest sums with recency, aggregated in SQL (GROUP BY) so the
   * coverage and meta seams never page the unbounded audit log to tally it.
   */
  ingestLogSums(): Map<string, { total: number; lastAt: string | null }> {
    const rows = this.db
      .prepare(
        `SELECT source, SUM(count) AS total, MAX(timestamp) AS last_at
         FROM ingest_log GROUP BY source`,
      )
      .all() as Array<{ source: unknown; total: unknown; last_at: unknown }>
    const sums = new Map<string, { total: number; lastAt: string | null }>()
    for (const row of rows) {
      if (typeof row.source !== 'string') continue
      sums.set(row.source, {
        total: typeof row.total === 'number' ? row.total : Number(row.total) || 0,
        lastAt: typeof row.last_at === 'string' ? row.last_at : null,
      })
    }
    return sums
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

  /**
   * The records-write marker's current value; 0 when nothing has ever been written.
   *
   * A rebuild asks this to decide whether the corpus moved since the last one.
   * See {@link RECORDS_WRITE_MARKER_KEY}.
   */
  recordsWriteMarker(): number {
    const val = this.getMetadata(RECORDS_WRITE_MARKER_KEY)
    if (val === undefined) return 0
    const parsed = Number(val)
    return Number.isFinite(parsed) ? parsed : 0
  }

  /**
   * Bump the records-write marker: the corpus has changed.
   *
   * Called from inside the same transaction as the write that changed it, so a
   * crash cannot leave a changed corpus with an unchanged marker (which would
   * let a rebuild skip a corpus that really did move).
   */
  bumpRecordsWriteMarker(): void {
    this.setMetadata(RECORDS_WRITE_MARKER_KEY, String(this.recordsWriteMarker() + 1))
  }

  /**
   * Run a write to `records` and mark the corpus as changed, in one transaction.
   *
   * WHY one transaction: the marker and the write it describes must land
   * together. A marker bumped after a failed write would make the next pass
   * rebuild a corpus that never changed; a marker not bumped after a
   * successful write would let the next pass skip a corpus that did.
   */
  private writeRecords(change: () => void): void {
    this.db.exec('BEGIN')
    try {
      change()
      this.bumpRecordsWriteMarker()
      this.db.exec('COMMIT')
    } catch (err) {
      this.db.exec('ROLLBACK')
      throw err
    }
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

  /**
   * Underlying SQLite database handle for bridge/server consumers that query
   * the canonical store directly without reopening the file.
   *
   * The handle it hands out carries the whole schema: the turn table is
   * materialized first, so a consumer that reads `session_turn` directly never
   * meets a store that lacks it.
   */
  getDatabase(): Database {
    this.ensureSessionTurnTable()
    return this.db
  }

  /**
   * `PRAGMA optimize` is SQLite's own guidance for the last thing a connection
   * does: it runs the bounded analysis a bulk import made worthwhile, so the
   * planner reads statistics next time instead of guessing from a table's
   * declared shape. It is cheap and self-limiting (`analysis_limit`), and it is
   * the reason a post-import read does not pay for the import's index churn.
   *
   * The optimize cannot stand between the handle and its close: if the stats
   * pass throws — a locked store, an I/O hiccup — the close still has to run,
   * or the caller leaks the handle it was getting rid of. Hence the `finally`,
   * not a plain sequence.
   */
  close(): void {
    try {
      this.db.exec('PRAGMA optimize')
    } finally {
      this.db.close()
    }
  }
}

