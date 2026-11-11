// The canonical store's schema: the version contract, the table SQL and the
// small normalizers the schema and its migrations share. Split from `./store.ts`,
// which re-exports every name here that it exported before.

import { SOURCE_STATE_SQL } from './source-state.js'
import { REFRESH_RUN_SQL, type RefreshRunRow, type RefreshRunStatus, type RefreshTrigger } from './refresh-run.js'
type Database = import('node:sqlite').DatabaseSync

/**
 * Bump when SCHEMA_SQL changes shape. A store built under a version this
 * build does not understand refuses to open; a store built under an older
 * one is migrated in place by `MIGRATIONS` rather than rebuilt, because the
 * corpus is the expensive thing here and re-collecting it is not always
 * possible.
 */
export const SCHEMA_VERSION = 24

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
-- Native OpenTelemetry metric points: one row per decoded data point. This
-- is the destination for Factory's native metrics export, whose DEFAULT shape
-- is metrics only, so it is the path a real deployment hits first.
--
-- A new table, not a new column on an existing one, so the CREATE ... IF NOT
-- EXISTS the store applies on every open covers a store already stamped at
-- SCHEMA_VERSION: landing it cost no version bump and needs no migration
-- step -- the model_context_window_catalog precedent. A store that predates
-- the table opens unchanged and grows the table on the next open. A COLUMN
-- added to the table afterwards is a different matter: the same IF NOT EXISTS
-- leaves an existing table without it, so a column costs a version bump and a
-- migration step.
--
-- A metric point has no span, so the row carries no harness and no adapter
-- fingerprint: the exporter's service.name is its source, and a row is never
-- attributed to a harness. That is also why a scoped per-harness wipe cannot
-- clear these rows -- it cannot attribute what it would remove -- and only
-- the full corpus wipe clears them.
--
-- point_key is the point's identity: a digest of everything the row carries.
-- A metric point has no span id to key on, so the content digest is what
-- makes a re-delivered point land on the row it already occupies instead of
-- beside it. The interval the point's measurement covers is part of that
-- digest: the same measurement over a different window is a different point.
--
-- start_time_unix_nano is the start of that interval. The column landed with
-- the v22 -> v23 migration, because every statement in this schema is IF NOT
-- EXISTS: a store that already carries the table never gained the column from
-- the CREATE TABLE, so the migration is what adds it to an existing store.
CREATE TABLE IF NOT EXISTS metric_point (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  point_key TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL,
  kind TEXT NOT NULL,
  unit TEXT,
  aggregation_temporality INTEGER,
  time_unix_nano TEXT,
  start_time_unix_nano TEXT,
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
export const SESSION_DENORM_INDEX_COLUMNS = [
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
export const SESSION_DENORM_INDEX_SQL = `CREATE INDEX IF NOT EXISTS session_denorm_idx
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
export function createSessionDenormIndex(db: Database): boolean {
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
export function toRefreshRunRow(row: Record<string, unknown>): RefreshRunRow {
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
