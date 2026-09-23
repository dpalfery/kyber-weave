// Content retention for the canonical store (spine D18).
// Content stays on the record for 14 days, then the content columns are
// emptied in place. `records.raw` is never deleted (ADR 0008). There is no
// schema bump: `toRecord` JSON-parses `content_json`, so a SQL NULL would
// break reads. `setContent` writes `'{}'` / NULL `parts_json` instead.

import type { CanonStore } from './store.js'
import type { CanonicalRecord } from './types.js'

export const CONTENT_RETENTION_DAYS = 14

/** Metadata key recording the retention window this pass applied. */
export const CONTENT_RETENTION_DAYS_KEY = 'content_retention_days'

/** Metadata key: ISO cutoff this pass treated as the end of retained content. */
export const CONTENT_PURGED_THROUGH_KEY = 'content_purged_through'

const MS_PER_DAY = 24 * 60 * 60 * 1000

export type PurgeExpiredContentResult = {
  purged: number
  cutoffUtc: string
}

function recordTimeMs(timestamp: Date | string): number {
  return (timestamp instanceof Date ? timestamp : new Date(timestamp)).getTime()
}

function hasStoredContent(record: CanonicalRecord): boolean {
  if (record.parts !== undefined && record.parts.length > 0) return true
  return Object.keys(record.content).length > 0
}

/**
 * Empty `content_json` / `parts_json` on records older than 14 days.
 * Rows stay; `raw` is untouched. `now` is injectable so tests do not depend
 * on wall-clock time.
 */
export function purgeExpiredContent(
  store: CanonStore,
  now: Date = new Date(),
): PurgeExpiredContentResult {
  const cutoffMs = now.getTime() - CONTENT_RETENTION_DAYS * MS_PER_DAY
  const cutoffUtc = new Date(cutoffMs).toISOString()
  let purged = 0

  for (const spanId of store.spanIds()) {
    const record = store.get(spanId)
    if (record === undefined) continue
    if (recordTimeMs(record.timestamp) >= cutoffMs) continue
    if (!hasStoredContent(record)) continue
    store.setContent(spanId, {})
    purged += 1
  }

  store.setMetadata(CONTENT_RETENTION_DAYS_KEY, String(CONTENT_RETENTION_DAYS))
  store.setMetadata(CONTENT_PURGED_THROUGH_KEY, cutoffUtc)
  return { purged, cutoffUtc }
}
