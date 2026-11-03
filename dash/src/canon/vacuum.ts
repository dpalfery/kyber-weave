// Reclaiming the store's freelist.
//
// WHY: SQLite never gives a freed page back to the filesystem. It parks the
// page on the database's freelist so the next insert can reuse it, and the
// file stays the size it grew to. A store whose corpus was wiped and
// re-ingested carries that history in its file: the live store was 3.33 GB
// with 343,510 free pages out of 813,331 — 42% of the file held by nobody.
//
// `VACUUM` is the only thing that returns those pages: it rewrites the
// database into a fresh file with the free pages left out and swaps it in.
// It cannot be simulated, so this module keeps the two promises apart:
//
//   - the dry run reads SQLite's own counters (`page_count`,
//     `freelist_count`, `page_size`) and reports what the freelist holds.
//     It promises exactly that and nothing more. A VACUUM also re-lays-out
//     the live pages, so the file it writes can come out smaller than the
//     freelist alone accounts for — and never larger, because the free pages
//     were there to begin with.
//   - the real run rewrites the file and reports what it actually did.
//
// WHY this opens its own handle rather than using `CanonStore`: a dry run
// must not write, and `CanonStore`'s open applies the schema and its close
// runs `PRAGMA optimize`, which both write. A read-only handle cannot write
// at all, so "the dry run changed nothing" is a property of the connection
// rather than a promise about which code path was taken.
//
// WHY the schema guard is repeated here rather than delegated to the store:
// the guard lives in `CanonStore.migrate`, which only runs when the store is
// opened for writing. A dry run must not open it for writing, so it applies
// the same rule itself — with `schemaVersionRefusal`, the store's own
// wording, so the two cannot drift.

import { createRequire } from 'node:module'

import { SCHEMA_VERSION, schemaVersionRefusal } from './store.js'

const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}

/** What a vacuum run reports. */
export type VacuumReport = {
  /** The store that was measured, or vacuumed. */
  path: string
  /** True when the run reported without writing. */
  dryRun: boolean
  /** The store's size before the run: `page_count * page_size`. */
  beforeBytes: number
  /** The store's size after the run. A dry run leaves this equal to `beforeBytes`. */
  afterBytes: number
  /** `beforeBytes - afterBytes`. Zero for a dry run, which wrote nothing. */
  reclaimedBytes: number
  /** What the freelist held: `freelist_count * page_size`. */
  reclaimableBytes: number
  /** The free pages the run reclaimed — or, for a dry run, the ones it would. */
  freelistPages: number
  /** The store's page count before the run. */
  pageCountBefore: number
  /** The store's page count after the run; the same as before, for a dry run. */
  pageCountAfter: number
  /** The store's page size in bytes. */
  pageSize: number
}

export type VacuumOptions = {
  /**
   * Report the reclaimable space without writing anything.
   *
   * The default is to write: a vacuum that quietly did nothing would be
   * indistinguishable from one that reclaimed nothing.
   */
  dryRun?: boolean
}

/** SQLite's own counters for a database file. */
type PageStats = {
  pageCount: number
  freelistCount: number
  pageSize: number
}

/**
 * Reclaim the store's freelist, or report what it holds.
 *
 * Safe against a live store in the sense that matters: it rewrites the file
 * atomically (SQLite builds the new database beside the old one and swaps it
 * in) and it never touches a row. It is not safe to run *concurrently* with
 * the engine, which is why the command takes the same writer lock a refresh
 * and a clean take.
 */
export function vacuumStore(path: string, options: VacuumOptions = {}): VacuumReport {
  const dryRun = options.dryRun === true
  const db = openStore(path, dryRun)
  try {
    assertSchemaIsReadable(path, db)
    const before = readPageStats(db)
    if (dryRun) {
      return {
        path,
        dryRun: true,
        beforeBytes: before.pageCount * before.pageSize,
        afterBytes: before.pageCount * before.pageSize,
        reclaimedBytes: 0,
        reclaimableBytes: before.freelistCount * before.pageSize,
        freelistPages: before.freelistCount,
        pageCountBefore: before.pageCount,
        pageCountAfter: before.pageCount,
        pageSize: before.pageSize,
      }
    }
    // VACUUM cannot run inside a transaction, and it needs the file's pages
    // to itself: it writes a fresh database beside this one and swaps it in.
    db.exec('VACUUM')
    const after = readPageStats(db)
    return {
      path,
      dryRun: false,
      beforeBytes: before.pageCount * before.pageSize,
      afterBytes: after.pageCount * after.pageSize,
      reclaimedBytes: (before.pageCount - after.pageCount) * before.pageSize,
      reclaimableBytes: before.freelistCount * before.pageSize,
      freelistPages: before.freelistCount,
      pageCountBefore: before.pageCount,
      pageCountAfter: after.pageCount,
      pageSize: before.pageSize,
    }
  } finally {
    db.close()
  }
}

/**
 * Open the store for the run.
 *
 * A dry run opens read-only: SQLite refuses every write on such a
 * connection, so the run cannot touch the file even by accident.
 */
function openStore(path: string, readOnly: boolean): InstanceType<typeof DatabaseSync> {
  if (readOnly) return new DatabaseSync(path, { readOnly: true })
  const db = new DatabaseSync(path)
  // The pragmas `CanonStore` opens with, so the vacuum runs against the
  // database in the mode the engine uses it in: WAL, and a busy timeout so
  // a concurrent writer waits instead of failing the run.
  db.exec('PRAGMA busy_timeout = 5000')
  db.exec('PRAGMA journal_mode = WAL')
  db.exec('PRAGMA synchronous = NORMAL')
  return db
}

/**
 * Refuse a store this build does not understand, in the store's own words.
 *
 * A store from a newer KyberDash has a schema this build would misread, and
 * a vacuum rewrites the file — so refusing it is the difference between a
 * clear message and a corrupted store.
 *
 * An *older* store is not refused: the store migrates it in place the next
 * time the engine opens it, and a VACUUM preserves whatever schema it finds,
 * so rewriting the file changes its size and nothing else.
 */
function assertSchemaIsReadable(path: string, db: InstanceType<typeof DatabaseSync>): void {
  const hasMetadata =
    db
      .prepare("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'metadata'")
      .get() !== undefined
  if (!hasMetadata) return
  const row = db.prepare("SELECT value FROM metadata WHERE key = 'schema_version'").get() as
    | { value?: unknown }
    | undefined
  if (row === undefined || row.value === undefined || row.value === null) return
  const from = Number(row.value)
  if (!Number.isFinite(from)) return
  if (from > SCHEMA_VERSION) {
    throw new Error(schemaVersionRefusal(path, from, SCHEMA_VERSION))
  }
}

/** SQLite's own counters for the open database. */
function readPageStats(db: InstanceType<typeof DatabaseSync>): PageStats {
  const pageCount = Number(
    (db.prepare('PRAGMA page_count').get() as { page_count: number }).page_count,
  )
  const freelistCount = Number(
    (db.prepare('PRAGMA freelist_count').get() as { freelist_count: number }).freelist_count,
  )
  const pageSize = Number(
    (db.prepare('PRAGMA page_size').get() as { page_size: number }).page_size,
  )
  return { pageCount, freelistCount, pageSize }
}
