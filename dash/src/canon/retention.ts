// Content retention for the canonical store (spine D18).
// Content stays on the record for 14 days, then the content columns are
// emptied in place. `records.raw` is never deleted (ADR 0008). There is no
// schema bump: `toRecord` JSON-parses `content_json`, so a SQL NULL would
// break reads. `setContent` writes `'{}'` / NULL `parts_json` instead.

import type { CanonStore } from './store.js'

export const CONTENT_RETENTION_DAYS = 14

/** Metadata key recording the retention window this pass applied. */
export const CONTENT_RETENTION_DAYS_KEY = 'content_retention_days'

/** Metadata key: ISO cutoff this pass treated as the end of retained content. */
export const CONTENT_PURGED_THROUGH_KEY = 'content_purged_through'

const MS_PER_DAY = 24 * 60 * 60 * 1000

/** Instant `now` minus CONTENT_RETENTION_DAYS, in epoch milliseconds. */
export function contentRetentionFloorMs(now: Date): number {
  return now.getTime() - CONTENT_RETENTION_DAYS * MS_PER_DAY
}

export type PurgeExpiredContentResult = {
  purged: number
  cutoffUtc: string
}

/**
 * Empty `content_json` / `parts_json` on records older than 14 days.
 * Rows stay; `raw` is untouched. `now` is injectable so tests do not depend
 * on wall-clock time.
 *
 * The purge is ONE set-based statement inside ONE transaction
 * (`CanonStore.purgeContentBefore`): it used to read and rewrite every span
 * one at a time, which on a real store meant 9,740 autocommit updates and an
 * fsync each. The cutoff metadata is written after the purge commits, so the
 * recorded cutoff is always one the purge actually applied.
 */
export function purgeExpiredContent(
  store: CanonStore,
  now: Date = new Date(),
): PurgeExpiredContentResult {
  const cutoffMs = contentRetentionFloorMs(now)
  const cutoffUtc = new Date(cutoffMs).toISOString()
  const purged = store.purgeContentBefore(cutoffUtc)

  store.setMetadata(CONTENT_RETENTION_DAYS_KEY, String(CONTENT_RETENTION_DAYS))
  store.setMetadata(CONTENT_PURGED_THROUGH_KEY, cutoffUtc)
  return { purged, cutoffUtc }
}
