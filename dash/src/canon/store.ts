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

import { createRequire } from 'node:module'
import { FILE_SOURCE_PREFIX, SessionIdentities, canonicalHarnessId, normalizeHarnessName } from './measurability.js'
import { mkdirSync } from 'node:fs'
import { dirname } from 'node:path'
import { setImmediate as scheduleImmediate } from 'node:timers'
import type { TokenCacheStore } from './tokens.js'
import { toRecordProvenance, toSourceCheckpoint, type RecordProvenance, type RecordProvenanceRow, type SourceCheckpoint, type SourceCheckpointRow } from './source-state.js'
import { REFRESH_MAX_AGE_MS, refreshProcessIsAlive, type RefreshRunRow, type RefreshRunStatus, type RefreshTrigger } from './refresh-run.js'
import type { OtlpMetric } from '../otel/receiver.js'
import { compressColumn, type CompressedColumn } from './compressed-column.js'
import { SESSION_PAYLOAD_FRAGMENTS_SELECT, SESSION_PAYLOAD_SELECT, SESSION_TURNS_SELECT, parseJsonObject, sessionTurnsOf, turnObjectOf } from './session-read.js'
import type { CanonicalRecord, ContentPart, CostBlock, ExecutionRow, ExecutionTreeNode, HarnessRollupRow, Measurability, RunRow, SessionRow, TokenUsage } from './types.js'
import type { DetectorId, Finding, FindingConfidence, FindingEvidenceLink } from '../analysis/findings.js'
import { calculateCalibrationCurve, type CalibrationCurveResult, type PredictionRecord } from '../analysis/calibration.js'
import { DETECTOR_VERSION, METADATA_SQL, RECORDS_WRITE_MARKER_KEY, SCHEMA_SQL, SCHEMA_VERSION, SESSION_TURN_TABLE_SQL, WAL_SIZE_LIMIT_BYTES, createSessionDenormIndex, normalizeHistoryWeeks, normalizeIngestSource, schemaVersionRefusal, toRefreshRunRow } from './store-schema.js'
import { MIGRATIONS, type MigrationOptions, type SessionDerivedColumns, escapeLike, materializeContextLimitSource, problemIdentity, sessionColumnsMatch, sessionTurnsMatch, splitSessionTurns, storedCount, storedText } from './store-migrations.js'
import { type CandidateRow, DEFAULT_PENDING_PAGE_BYTES, DEFAULT_QUARANTINE_PAGE_BYTES, type ExecutionDbRow, type FindingDbRow, type FindingsListOptions, type HarnessRollupDbRow, type IngestLogEntry, type MetricPointRow, type ModelCallCandidate, type PendingInteractionPromptRow, type PendingLogEntry, type PendingLogMetadata, type PredictionDbRow, type ProjectedSessionRecord, type QuarantineEntry, type QuarantineEntryDetail, type QuarantineMetadata, type QuarantinedLog, type RecordRow, type RunDbRow, type SessionDbRow, type SpanProblem, UPSERT_METRIC_POINT_SQL, UPSERT_RECORD_SQL, compressRaw, decompressRaw, materializeSessionSummary, metadataChangeCount, metricPointDrafts, problemsHarnessWhere, projectedAttributes, quarantineHarnessWhere, readSessionSummaryFigures, systemPromptTextsFromContentColumns, text, toCandidate, toExecutionRow, toFinding, toHarnessRollupRow, toMetricPointRow, toPrediction, toQuarantineEntry, toRecord, toRunRow, toSessionRow, toSpanProblem } from './store-rows.js'
const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}
type StatementSync = import('node:sqlite').StatementSync
type Database = import('node:sqlite').DatabaseSync

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
 * attribution and stays for the complete wipe. `metric_point` has none
 * either -- a metric point has no span, so it names no harness -- and stays
 * for the same reason: a scoped wipe cannot claim a row it cannot attribute.
 * Call inside the caller's transaction, like `commitSourceUnit`'s inverse: a
 * failure rolls back everything, so records and checkpoints can never
 * disagree about coverage.
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

// Split into `./store-schema.ts`, `./store-migrations.ts` and `./store-rows.ts`;
// re-exported so every importer of this module keeps resolving the same names.
export { SCHEMA_VERSION, schemaVersionRefusal, WAL_SIZE_LIMIT_BYTES, DETECTOR_VERSION, RECORDS_WRITE_MARKER_KEY, METADATA_SQL, SESSION_TURN_TABLE_SQL, SCHEMA_SQL, normalizeHistoryWeeks, normalizeIngestSource } from './store-schema.js'
export { MIGRATIONS, problemIdentity, type MigrationYield, type MigrationOptions } from './store-migrations.js'
export { compressRaw, decompressRaw, systemPromptTextsFromContentColumns, quarantineHarnessWhere, problemsHarnessWhere, releaseRecordParts, toRecord, projectedAttributes, reduceRecordPayloads, toSessionRow, readSessionSummaryFigures, toRunRow, toExecutionRow, toHarnessRollupRow, toFinding, toPrediction, DEFAULT_PENDING_PAGE_BYTES, type SpanProblem, type IngestLogEntry, type MetricPointRow, type QuarantineEntry, type QuarantineEntryDetail, type QuarantineMetadata, type QuarantinedLog, type ModelCallCandidate, type RecordRow, type ProjectedSessionRecord, type SessionDbRow, type SessionSummaryFigureFields, type SessionSummaryFigureRow, type RunDbRow, type ExecutionDbRow, type HarnessRollupDbRow, type FindingDbRow, type PredictionDbRow, type FindingsListOptions, type PendingLogMetadata, type PendingLogEntry, type PendingInteractionPromptRow } from './store-rows.js'

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
  private readonly upsertMetricPointStatement: StatementSync

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
    this.upsertMetricPointStatement = this.db.prepare(UPSERT_METRIC_POINT_SQL)
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
    // Targeted by SQL (F8): the old full-snapshot filter is preserved exactly
    // — `(trace, span) or span alone, or the session` — but only matching
    // rows' payloads are parsed. A row whose span id is NULL never equals the
    // wanted one; a NULL session wants nothing (NULL never equals in SQL,
    // exactly as `log.sessionId === sessionId` is false for it in JS).
    const rows = this.db.prepare(
      `SELECT payload FROM pending_logs
       WHERE json_extract(payload, '$.spanId') = ?
          OR (? IS NOT NULL AND json_extract(payload, '$.sessionId') = ?)
       ORDER BY log_id`,
    ).all(spanId, sessionId ?? null, sessionId ?? null) as { payload: string }[]
    const matched = rows.map((row) => {
      const entry: unknown = JSON.parse(row.payload)
      return entry
    }).filter((entry) => {
      const log = entry as { traceId?: string | null; spanId?: string | null; sessionId?: string | null }
      return ((log.traceId === traceId && log.spanId === spanId) || log.spanId === spanId) ||
        (sessionId !== null && sessionId !== undefined && log.sessionId === sessionId)
    })
    const del = this.db.prepare('DELETE FROM pending_logs WHERE log_id = ?')
    for (const log of matched) {
      del.run((log as { logId: string }).logId)
    }
    return matched
  }

  /**
   * One row of `pending_logs` without its payload (F8): the metadata-only
   * read expiry, dedup, and replay planning run on, so a large backlog of
   * heavy bodies costs a small-column scan instead of a parse-everything
   * snapshot. `pendingSince` falls back to null when the stored row predates
   * it — the callers treat that as "arrived now", exactly as before.
   */
  pendingLogMetadata(): PendingLogMetadata[] {
    return (this.db.prepare(
      `SELECT log_id, harness,
              LENGTH(CAST(payload AS BLOB)) AS bytes,
              json_extract(payload, '$.pendingSince') AS pendingSince,
              json_extract(payload, '$.traceId') AS traceId,
              json_extract(payload, '$.spanId') AS spanId,
              json_extract(payload, '$.sessionId') AS sessionId
       FROM pending_logs ORDER BY log_id`,
    ).all() as {
      log_id: string; harness: string | null; bytes: number; pendingSince: number | null;
      traceId: string | null; spanId: string | null; sessionId: string | null
    }[]).map((row) => ({
      logId: row.log_id,
      harness: row.harness,
      bytes: row.bytes,
      pendingSince: typeof row.pendingSince === 'number' ? row.pendingSince : null,
      traceId: typeof row.traceId === 'string' ? row.traceId : null,
      spanId: typeof row.spanId === 'string' ? row.spanId : null,
      sessionId: typeof row.sessionId === 'string' ? row.sessionId : null,
    }))
  }

  /**
   * Expired pending logs' payloads only (F8): SQL selects which rows waited
   * past their TTL — the ordinary TTL for identified logs, the longer Claude
   * request-body TTL for `api_request_body` events — and only those payloads
   * are parsed. A busy backlog of live bodies is never decoded for this
   * decision. Rows predating `pendingSince` count as arrived at `now`, and a
   * non-numeric `pendingSince` counts as unknown rather than expired, both
   * exactly as the old full-scan reconcile did.
   */
  takeExpiredPendingLogs(now: number, ttlMs: number, claudeTtlMs: number): PendingLogEntry[] {
    const age = `COALESCE(CASE WHEN typeof(json_extract(payload, '$.pendingSince')) IN ('integer', 'real')
      THEN json_extract(payload, '$.pendingSince') END, ?)`
    // The request-body predicate mirrors `isClaudeRequestBodyLog` (body or
    // event name only — scope alone never marks a request body). It lives in
    // SQL here so the long-TTL rows are selected, not filtered, in the store.
    // Null-safe `IS`: `= NULL` is never true, so an `=` here would make
    // `NOT (false OR NULL)` collapse to NULL and wrongly exclude every
    // ordinary row from the short-TTL pass.
    const claudeBody = `(json_extract(payload, '$.body') IS 'claude_code.api_request_body'
      OR json_extract(payload, '$.attributes."event.name"') IS 'api_request_body')`
    const select = (ttl: number, claude: boolean): { payload: string; harness: string | null }[] =>
      this.db.prepare(
        `SELECT payload, harness FROM pending_logs
         WHERE ${age} <= ? - ? AND ${claude ? claudeBody : `NOT (${claudeBody})`}
         ORDER BY log_id`,
      ).all(now, now, ttl) as { payload: string; harness: string | null }[]
    const rows = [...select(ttlMs, false), ...select(Math.max(ttlMs, claudeTtlMs), true)]
    // Parse once: the parse below is the only full decode this decision
    // performs, and it covers exactly the expired rows.
    const parsed = rows.map((row) => {
      const entry: Record<string, unknown> = JSON.parse(row.payload)
      if (typeof row.harness === 'string') entry.harness = row.harness
      return entry as PendingLogEntry
    })
    parsed.sort((a, b) => (a.logId < b.logId ? -1 : a.logId > b.logId ? 1 : 0))
    return parsed
  }

  /**
   * Pending logs after `afterLogId`, at most `limit` rows and at most
   * `maxPayloadBytes` of payload — always at least one row, so one oversized
   * payload cannot stall the cursor (F8). Sizes are read first, so a row past
   * the cap is never loaded. The replay walks these pages in `log_id` order
   * instead of snapshotting the whole backlog at once.
   */
  pendingLogsPage(
    afterLogId: string,
    limit: number,
    maxPayloadBytes: number = DEFAULT_PENDING_PAGE_BYTES,
  ): { logId: string; log: unknown }[] {
    const sizes = this.db
      .prepare(`SELECT log_id, LENGTH(CAST(payload AS BLOB)) AS bytes FROM pending_logs WHERE log_id > ? ORDER BY log_id LIMIT ?`)
      .all(afterLogId, limit) as { log_id: string; bytes: number }[]
    const readPayload = this.db.prepare('SELECT payload FROM pending_logs WHERE log_id = ?')
    const page: { logId: string; log: unknown }[] = []
    let total = 0
    for (const size of sizes) {
      if (page.length > 0 && total + size.bytes > maxPayloadBytes) break
      total += size.bytes
      const row = readPayload.get(size.log_id) as { payload: string } | undefined
      // A row deleted between the size read and this read (same-handle
      // callers cannot interleave, but a second handle could) is skipped:
      // the cursor still advances past it via `afterLogId`.
      if (row === undefined) continue
      page.push({ logId: size.log_id, log: JSON.parse(row.payload) as unknown })
    }
    return page
  }

  /**
   * Parked interaction prompts' correlation fields only (F8): the settle pass
   * finds prompt rows by their marker without parsing every unrelated
   * payload. The prompt text itself is small; the bodies it spares are not.
   */
  pendingInteractionPrompts(): PendingInteractionPromptRow[] {
    return (this.db.prepare(
      `SELECT log_id,
              json_extract(payload, '$.traceId') AS traceId,
              json_extract(payload, '$.spanId') AS spanId,
              json_extract(payload, '$.pendingSince') AS pendingSince,
              json_extract(payload, '$.attributes."kyber.interaction_prompt"') AS prompt
       FROM pending_logs
       WHERE json_extract(payload, '$.attributes."kyber.interaction_prompt"') IS NOT NULL
       ORDER BY log_id`,
    ).all() as {
      log_id: string; traceId: unknown; spanId: unknown; pendingSince: unknown; prompt: unknown
    }[]).map((row) => ({
      logId: row.log_id,
      traceId: typeof row.traceId === 'string' ? row.traceId : null,
      spanId: typeof row.spanId === 'string' ? row.spanId : null,
      pendingSince: typeof row.pendingSince === 'number' ? row.pendingSince : undefined,
      prompt: row.prompt,
    }))
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
    harness?: string,
  ): Array<{ spanId: string; text: string; timestamp?: string }> {
    if (!sessionId) return []
    const share = harness === undefined ? undefined : normalizeHarnessName(harness)
    const rows = this.db
      .prepare(
        // Turn spans (`llm.invoke`) are skipped in SQL, so their (largest) parts blobs are
        // never read or inflated; a row cap would silently drop session prompts instead.
        `SELECT span_id, op, harness, timestamp, content_json, parts_json FROM records
         WHERE COALESCE(session_id, trace_id) = ? AND COALESCE(op, '') <> 'llm.invoke'
         ORDER BY timestamp`,
      )
      .all(sessionId) as { span_id: unknown; op: unknown; harness: unknown; timestamp: unknown; content_json: unknown; parts_json: unknown }[]
    const out: Array<{ spanId: string; text: string; timestamp?: string }> = []
    for (const row of rows) {
      if (row.op === 'llm.invoke') continue
      if (share !== undefined && canonicalHarnessId(String(row.harness)) !== share) continue
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

    // An unchanged re-projection writes nothing: the row and its turns are
    // compared against what is stored, and an identical re-upsert returns
    // before opening a transaction. Every maintenance pass re-upserts every
    // session, so without this guard each pass rewrote every turn row even
    // when the corpus had not moved — the dirty-page churn the shared
    // cadence exists to bound. A mismatch in any column or turn still writes
    // the whole session, exactly as before.
    if (
      this.sessionDerivedRowsAreCurrent(row.sessionId, {
        harness: row.harness,
        label: row.label ?? null,
        isSubagent: row.isSubagent ?? false,
        parentSession: row.parentSession ?? null,
        agentName: row.agentName ?? null,
        repo: row.repo ?? null,
        branch: row.branch ?? null,
        started: row.started ?? null,
        ended: row.ended ?? null,
        summaryJson: summary_json,
        problemsCount: problems_count,
        contextLimitSource: context_limit_source,
        payload,
      }, split.turns)
    ) {
      return
    }

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
   * Whether the stored derived rows already hold exactly this session.
   *
   * Compares every column the upsert writes plus every turn row it owns.
   * Anything unreadable — a missing row, a mistyped column, an unparseable
   * turn — reads as different, so the guard only ever skips a write it can
   * prove redundant and never preserves a row it cannot read. The sync job's
   * columns are not compared because the upsert never writes them; skipping
   * the write preserves them exactly as writing would have.
   */
  private sessionDerivedRowsAreCurrent(
    sessionId: string,
    columns: SessionDerivedColumns,
    turns: Array<{ index: number; stored: CompressedColumn }>,
  ): boolean {
    const existing = this.db
      .prepare(
        'SELECT harness, label, is_subagent, parent_session, agent_name, repo, branch, ' +
          'started, ended, summary_json, problems_count, context_limit_source, payload ' +
          'FROM session WHERE session_id = ?',
      )
      .get(sessionId) as Record<string, unknown> | undefined
    if (existing === undefined) return false
    if (!sessionColumnsMatch(existing, columns)) return false
    const storedTurns = this.db.prepare(SESSION_TURNS_SELECT).all(sessionId) as Array<{
      turn_index?: unknown
      turn_json?: unknown
    }>
    return sessionTurnsMatch(storedTurns, turns)
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
      // The metric points carry no harness, so a scoped per-harness wipe
      // cannot claim them and leaves them; the whole corpus wipe clears
      // everything the store holds, these rows included.
      this.db.exec('DELETE FROM metric_point')
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
    const payload = payloadObj === undefined || payloadObj === null ? null : JSON.stringify(payloadObj)

    // Same bargain as the session guard: every projection rebuilds every run,
    // so an unchanged re-upsert must not dirty the row it already holds.
    const existing = this.db.prepare(
      'SELECT harness, label, grouping_basis, grouping_rule, working_directory, started, ended, ' +
        'execution_count, payload FROM run WHERE run_id = ?',
    ).get(run.runId) as Record<string, unknown> | undefined
    if (
      existing !== undefined &&
      storedText(existing['harness']) === run.harness &&
      storedText(existing['label']) === (run.label ?? null) &&
      storedText(existing['grouping_basis']) === run.groupingBasis &&
      storedText(existing['grouping_rule']) === (run.groupingRule ?? null) &&
      storedText(existing['working_directory']) === (run.workingDirectory ?? null) &&
      storedText(existing['started']) === (run.started ?? null) &&
      storedText(existing['ended']) === (run.ended ?? null) &&
      storedCount(existing['execution_count']) === (run.executionCount ?? 0) &&
      storedText(existing['payload']) === payload
    ) {
      return
    }

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
        payload,
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
    const parentLinkageJson = JSON.stringify(execution.parentLinkage)
    const payload = execution.payload === undefined ? null : JSON.stringify(execution.payload)

    // Same bargain as the session guard: every projection rebuilds every
    // execution, so an unchanged re-upsert must not dirty the row it holds.
    const existing = this.db.prepare(
      'SELECT run_id, session_id, parent_execution_id, harness, agent_name, is_root, started, ended, ' +
        'parent_linkage_json, payload FROM execution WHERE execution_id = ?',
    ).get(execution.executionId) as Record<string, unknown> | undefined
    if (
      existing !== undefined &&
      storedText(existing['run_id']) === execution.runId &&
      storedText(existing['session_id']) === (execution.sessionId ?? null) &&
      storedText(existing['parent_execution_id']) === (execution.parentExecutionId ?? null) &&
      storedText(existing['harness']) === execution.harness &&
      storedText(existing['agent_name']) === (execution.agentName ?? null) &&
      storedCount(existing['is_root']) === (execution.isRoot ? 1 : 0) &&
      storedText(existing['started']) === (execution.started ?? null) &&
      storedText(existing['ended']) === (execution.ended ?? null) &&
      storedText(existing['parent_linkage_json']) === parentLinkageJson &&
      storedText(existing['payload']) === payload
    ) {
      return
    }

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
        parentLinkageJson,
        payload,
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
   * Every canonical record, one at a time, in timestamp order — the streaming
   * counterpart of `listAll` for passes that must walk the whole corpus without
   * holding it in memory (F9: corpus-wide summaries over compressed payload
   * columns). The cursor yields one row per `next()` call, so only one record
   * is live at a time; callers needing a snapshot should keep using `listAll`.
   * The store must stay open for the generator's lifetime.
   */
  *iterateAll(): Generator<import('./types.js').CanonicalRecord> {
    const rows = this.db.prepare('SELECT * FROM records ORDER BY timestamp').iterate() as IterableIterator<RecordRow>
    for (const row of rows) yield toRecord(row)
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

  /**
   * Persist a decoded metrics batch: one row per metric data point.
   *
   * This is the destination for native OpenTelemetry metrics, and Factory's
   * default export shape is metrics only, so it is the path a real deployment
   * hits first. Nothing is dropped and nothing is fabricated: every decoded
   * point becomes a row, and a point that carried no value of its own keeps
   * the value absent rather than zero.
   *
   * The rows carry the exporter's `service.name` as their source and no
   * harness attribution: a metric point has no span, so it belongs to no
   * harness and a scoped per-harness wipe cannot claim it.
   *
   * The batch commits as one transaction, so a batch that fails leaves no
   * half-written rows. It does NOT bump the records-write marker: a metric
   * point changes no record, and the marker is the corpus's change signal --
   * a metrics batch that moved it would ask for a projection it did not
   * change.
   */
  upsertMetrics(metrics: readonly OtlpMetric[]): void {
    const drafts = metricPointDrafts(metrics)
    if (drafts.length === 0) return
    this.db.exec('BEGIN')
    try {
      for (const draft of drafts) {
        this.upsertMetricPointStatement.run(
          draft.pointKey,
          draft.name,
          draft.kind,
          draft.unit,
          draft.aggregationTemporality,
          draft.timeUnixNano,
          draft.startTimeUnixNano,
          draft.timestamp,
          draft.value,
          draft.count,
          draft.sum,
          draft.attributesJson,
          draft.resourceJson,
          draft.scopeJson,
          draft.source,
        )
      }
      this.db.exec('COMMIT')
    } catch (err) {
      this.db.exec('ROLLBACK')
      throw err
    }
  }

  /**
   * The persisted metric points, in the order they were first stored.
   *
   * `limit` caps the read for a caller that wants a window rather than the
   * whole table; without one every row the store holds is returned, which is
   * what the read-back accessor is for.
   */
  getMetricPoints(limit?: number): MetricPointRow[] {
    const cap =
      limit === undefined || !Number.isFinite(limit) || limit < 0 ? null : Math.floor(limit)
    const rows = this.db
      .prepare(
        `SELECT name, kind, unit, aggregation_temporality, time_unix_nano,
                start_time_unix_nano, timestamp, value, point_count, point_sum,
                attributes_json, resource_json, scope_json, source
         FROM metric_point ORDER BY id${cap === null ? '' : ' LIMIT ?'}`,
      )
      .all(...(cap === null ? [] : [cap])) as Record<string, unknown>[]
    return rows.map(toMetricPointRow)
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
   * Write a metadata value only when it still holds `expected`.
   *
   * The write itself is one conditional statement, so two processes racing
   * here serialize in SQLite and exactly one wins: the loser's write matches
   * zero rows. The read before it is only a fast path — a value that moved
   * between the read and the write still fails the statement's own guard.
   * `expected: undefined` means the key must be absent; `value: null`
   * deletes the key instead of writing it. Deleting an absent key and
   * writing the value already there both read as success — the key ends up
   * in the requested state either way.
   *
   * This is the atomic primitive the shared projection lease is built on;
   * a read-then-write through `getMetadata`/`setMetadata` could not claim
   * across processes without letting both sides believe they won.
   */
  compareAndSetMetadata(key: string, expected: string | undefined, value: string | null): boolean {
    const current = this.getMetadata(key)
    if (current !== expected) return false
    if (expected === undefined) {
      if (value === null) return true
      const inserted = this.db
        .prepare('INSERT INTO metadata (key, value) SELECT ?, ? WHERE NOT EXISTS (SELECT 1 FROM metadata WHERE key = ?)')
        .run(key, value, key) as unknown as { changes?: unknown }
      return metadataChangeCount(inserted) === 1
    }
    const written = value === null
      ? this.db.prepare('DELETE FROM metadata WHERE key = ? AND value = ?').run(key, expected)
      : this.db.prepare('UPDATE metadata SET value = ? WHERE key = ? AND value = ?').run(value, key, expected)
    return metadataChangeCount(written as unknown as { changes?: unknown }) === 1
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
