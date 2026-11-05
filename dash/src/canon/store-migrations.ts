// The canonical store's in-place migrations and the backfills they drive.
// Split from `./store.ts`, which re-exports every name here that it exported
// before.

import { SOURCE_STATE_SQL } from './source-state.js'
import { REFRESH_RUN_SQL } from './refresh-run.js'
import { compressColumn, type CompressedColumn } from './compressed-column.js'
import { parseJsonObject, turnObjectOf } from './session-read.js'
import { DETECTOR_VERSION, SESSION_TURN_TABLE_SQL, createSessionDenormIndex } from './store-schema.js'
import { sessionSummaryOf } from './store-rows.js'
type StatementSync = import('node:sqlite').StatementSync
type Database = import('node:sqlite').DatabaseSync

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
  // v22 -> v23: the metric point's interval start.
  //
  // A metric point's identity is its content digest, and the digest did not
  // carry the interval the point's measurement covers. Two points that differed
  // ONLY in that interval -- the same name, kind, unit, temporality,
  // observation time, value and attributes, over a different window -- so
  // collided onto one point_key, and the second upserted onto the first row.
  //
  // The column lands the interval on the row, and the digest carries it, so
  // the interval is part of the point's identity and a reader can recover the
  // window a measurement covers.
  //
  // The ADD COLUMN is presence-guarded, as migration 19's and 20's are:
  // re-running the step, or opening a store an off-main build already stamped
  // 23, must not die on "duplicate column name". A store that has no
  // `metric_point` table at all gains the table WITH the column from the
  // schema, so there is nothing to add there.
  22: (db: Database) => {
    const hasTable = db
      .prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='metric_point'")
      .get()
    if (!hasTable) return
    const cols = (db.prepare('PRAGMA table_info(metric_point)').all() as { name: string }[]).map(
      (c) => c.name,
    )
    if (!cols.includes('start_time_unix_nano')) {
      db.exec('ALTER TABLE metric_point ADD COLUMN start_time_unix_nano TEXT')
    }
  },
}

/**
 * The v21 -> v22 step as the yielding open path runs it: the same backfill,
 * handing the event loop back between sessions, and the same statistics at
 * the end.
 */
export async function runYieldingSessionTurnBackfill(db: Database, yieldToLoop: MigrationYield): Promise<void> {
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
export function finishSessionStatistics(db: Database): void {
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
export function contextLimitSourceOf(payload: Record<string, unknown>): string | null {
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
export function materializeContextLimitSource(payloadJson: string): string | null {
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
export function turnIndexOf(turn: unknown, position: number): number {
  if (typeof turn === 'object' && turn !== null && !Array.isArray(turn)) {
    const index = (turn as Record<string, unknown>)['index']
    if (typeof index === 'number' && Number.isFinite(index)) return index
  }
  return position
}

/** Every column `upsertSession` writes, in compared form. */
export type SessionDerivedColumns = {
  harness: string
  label: string | null
  isSubagent: boolean
  parentSession: string | null
  agentName: string | null
  repo: string | null
  branch: string | null
  started: string | null
  ended: string | null
  summaryJson: string | null
  problemsCount: number | null
  contextLimitSource: string | null
  payload: string
}

/** A TEXT column read back: the string, null, or undefined when mistyped. */
export function storedText(value: unknown): string | null | undefined {
  if (value === null) return null
  return typeof value === 'string' ? value : undefined
}

/** A numeric column read back, or undefined when mistyped. */
export function storedCount(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

/**
 * Whether the stored session row already holds these derived columns. A
 * column that does not read back in its written shape counts as different:
 * the guard may only skip a write it can prove redundant.
 */
export function sessionColumnsMatch(existing: Record<string, unknown>, columns: SessionDerivedColumns): boolean {
  // problems_count is the one nullable numeric column: NULL in the store
  // matches only null from the derivation, never a mistyped value that
  // merely fails to parse as a number.
  const storedProblems = existing['problems_count']
  const problemsMatch = storedProblems === null
    ? columns.problemsCount === null
    : storedCount(storedProblems) === columns.problemsCount
  return (
    storedText(existing['harness']) === columns.harness &&
    storedText(existing['label']) === columns.label &&
    storedCount(existing['is_subagent']) === (columns.isSubagent ? 1 : 0) &&
    storedText(existing['parent_session']) === columns.parentSession &&
    storedText(existing['agent_name']) === columns.agentName &&
    storedText(existing['repo']) === columns.repo &&
    storedText(existing['branch']) === columns.branch &&
    storedText(existing['started']) === columns.started &&
    storedText(existing['ended']) === columns.ended &&
    storedText(existing['summary_json']) === columns.summaryJson &&
    problemsMatch &&
    storedText(existing['context_limit_source']) === columns.contextLimitSource &&
    storedText(existing['payload']) === columns.payload
  )
}

/**
 * One turn in comparable form. Both stored shapes — the deflated bytes new
 * writes produce and the plain JSON older rows still hold — parse to the
 * same object, so the comparison normalizes through a re-serialization and
 * reads those as the same turn instead of rewriting one into the other on
 * every pass.
 */
export function normalizedTurnJson(value: unknown): string | undefined {
  const turn = turnObjectOf(value)
  return turn === undefined ? undefined : JSON.stringify(turn)
}

/**
 * Whether the stored turn rows already hold exactly these turns. Compared
 * as a multiset keyed by index and content: the table's index is
 * deliberately not unique and neither side promises an order, so an
 * order-sensitive comparison would rewrite sessions whose turns merely
 * arrived in a different sequence. Anything unreadable counts as different.
 */
export function sessionTurnsMatch(
  stored: Array<{ turn_index?: unknown; turn_json?: unknown }>,
  next: Array<{ index: number; stored: CompressedColumn }>,
): boolean {
  if (stored.length !== next.length) return false
  const keyOf = (index: unknown, json: string | undefined): string | undefined =>
    typeof index === 'number' && Number.isFinite(index) && json !== undefined ? `${index}${json}` : undefined
  const left = stored.map((row) => keyOf(row.turn_index, normalizedTurnJson(row.turn_json)))
  const right = next.map((turn) => keyOf(turn.index, normalizedTurnJson(turn.stored)))
  if (left.includes(undefined) || right.includes(undefined)) return false
  const a = (left as string[]).sort()
  const b = (right as string[]).sort()
  // No separator between the index and the text: both sides stringify
  // objects, so every text starts with `{`, and an index that is a digit
  // prefix of another still differs at the first text character (`{` is no
  // digit). Equal indexes fall through to the texts, which decide.
  return a.every((value, position) => value === b[position])
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
export function splitSessionTurns(payloadJson: string): {
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
export function backfillSessionTurns(db: Database): void {
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
export async function backfillSessionTurnsYielding(db: Database, yieldToLoop: MigrationYield): Promise<void> {
  const backfill = backfillStatements(db)
  for (const sessionId of backfillSessionIds(db)) {
    backfillOneSession(backfill, sessionId)
    await yieldToLoop()
  }
}

/** The sessions the backfill walks, in storage order. */
export function backfillSessionIds(db: Database): string[] {
  return (db.prepare('SELECT session_id FROM session').all() as { session_id: string }[]).map(
    (row) => row.session_id,
  )
}

/** The statements the backfill writes with, prepared once for the pass. */
export function backfillStatements(db: Database): SessionTurnBackfill {
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
export function backfillOneSession(backfill: SessionTurnBackfill, sessionId: string): void {
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
export type SessionTurnBackfill = {
  readonly readPayload: StatementSync
  readonly writePayload: StatementSync
  readonly writeMaterialized: StatementSync
  readonly deleteTurns: StatementSync
  readonly insertTurn: StatementSync
}

/** Escape a literal for a LIKE pattern: backslash, percent, and underscore match themselves. */
export function escapeLike(literal: string): string {
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

export function rekeyProblems(db: Database): void {
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
