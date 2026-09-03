// Content backfill (spec: docs/specs/kyberdash; R7.1). The store keeps every
// span's original attribute map in the compressed `raw` column, so a content
// mapping that missed attributes can be corrected without re-collecting
// anything — the evidence is already on disk.
//
// This exists because the shipped mapping read exactly one attribute,
// `gen_ai.prompt`, which none of the harnesses in the corpus emit. Every one
// of the stored records therefore carries `content_json = '{}'` while its raw
// payload holds the system prompt, the message list, the workspace rules, the
// skill catalogue and the tool definitions. The bug is in the mapping, not in
// the collection, and this pass is the repair.
//
// The pass is idempotent: it recomputes content from raw and writes what it
// finds, so running it twice lands on the same rows.

import { CanonStore } from '../canon/store.js'
import { canonicalParts, canonicalSessionId } from '../canon/adapters/copilot.js'
import { contentFromParts } from '../canon/types.js'

export type BackfillReport = {
  /** Records examined. */
  scanned: number
  /** Records whose raw payload yielded at least one content part. */
  filled: number
  /** Records with no raw payload, or whose raw payload carried no content. */
  empty: number
  /** Records whose raw payload could not be read as an attribute map. */
  unreadable: number
  /** Records whose raw payload named the harness's own conversation. */
  sessionsNamed: number
}

export type BackfillOptions = {
  /** Called after each batch, for progress on a corpus of this size. */
  onProgress?: (done: number, total: number) => void
  /** Rows between progress callbacks. */
  progressEvery?: number
}

/**
 * Re-derive canonical content for every record from its stored raw payload.
 *
 * A record whose raw payload carries no content attributes is left with empty
 * content rather than a fabricated one — Claude Code's spans, for instance,
 * genuinely report counters and nothing else, and that absence is a finding
 * the charts must be able to state (R10.1).
 */
export function backfillContent(store: CanonStore, options: BackfillOptions = {}): BackfillReport {
  const spanIds = store.spanIds()
  const progressEvery = options.progressEvery ?? 500
  const report: BackfillReport = { scanned: 0, filled: 0, empty: 0, unreadable: 0, sessionsNamed: 0 }

  for (const spanId of spanIds) {
    report.scanned += 1

    const record = store.get(spanId)
    if (record?.raw === undefined || record.raw === null) {
      report.empty += 1
    } else if (typeof record.raw !== 'object') {
      report.unreadable += 1
    } else {
      const attributes = record.raw as Record<string, unknown>

      // Session identity was promoted to a column in schema v3 so grouping is
      // a GROUP BY rather than a scan that decompresses every raw payload.
      // Records collected before that carry it only inside `raw`.
      const sessionId = canonicalSessionId(attributes)
      if (sessionId !== undefined && record.sessionId !== sessionId) {
        store.setSessionId(spanId, sessionId)
      }
      if (sessionId !== undefined) report.sessionsNamed += 1

      const parts = canonicalParts(attributes)
      if (parts.length === 0) {
        report.empty += 1
      } else {
        store.setContent(spanId, contentFromParts(parts), parts)
        report.filled += 1
      }
    }

    if (report.scanned % progressEvery === 0) {
      options.onProgress?.(report.scanned, spanIds.length)
    }
  }

  options.onProgress?.(report.scanned, spanIds.length)
  return report
}
