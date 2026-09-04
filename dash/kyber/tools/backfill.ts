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

import type { RawSpan } from '../canon/adapters/base.js'
import { AdapterRegistry } from '../canon/adapters/registry.js'
import { ADAPTERS } from '../canon/ingest.js'
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

/**
 * Re-run harness attribution and the token conversion over stored records.
 *
 * `backfillContent` repairs what a span SAID; this repairs what the system
 * concluded about it. The live collector used to decide both from
 * `service.name` — the one signal R6.2 says is never attribution evidence —
 * and applied a single token convention to every harness. Records ingested
 * that way keep those conclusions until something re-derives them, which is
 * why Claude Code's spans sat at zero tokens (its counters are un-namespaced
 * and no adapter claimed it) and why Copilot's commit-message generator was
 * filed under a harness that does not exist.
 *
 * The raw payload is the evidence and the store kept it, so this is a pure
 * re-derivation: the vote runs per trace, exactly as it does on ingest.
 */
export function renormalizeRecords(store: CanonStore, options: BackfillOptions = {}): RenormalizeReport {
  const registry = new AdapterRegistry(ADAPTERS)
  const adapterByName = new Map(ADAPTERS.map((adapter) => [adapter.name, adapter]))
  const traceIds = store.traceIds()
  const progressEvery = options.progressEvery ?? 200
  const report: RenormalizeReport = { traces: 0, reattributed: 0, unchanged: 0, unclaimed: 0 }

  for (const traceId of traceIds) {
    report.traces += 1
    const records = store.recordsForTrace(traceId)

    const rawSpans: RawSpan[] = []
    for (const record of records) {
      if (record.raw === null || typeof record.raw !== 'object') continue
      rawSpans.push({
        spanId: record.spanId,
        traceId: record.traceId,
        parentSpanId: record.parentSpanId,
        source: record.source,
        attributes: record.raw as Record<string, unknown>,
        name: record.name,
        kind: record.kind,
      })
    }
    if (rawSpans.length === 0) continue

    const attributed = registry.attribute(rawSpans)
    for (const raw of rawSpans) {
      const harness = attributed.get(raw.spanId)
      if (harness === undefined) {
        report.unclaimed += 1
        continue
      }
      const adapter = adapterByName.get(harness)
      if (adapter === undefined) continue

      const before = records.find((record) => record.spanId === raw.spanId)!
      const after = adapter.normalize(raw)
      // A record whose conclusions already match is left alone, so a re-run
      // is cheap and the report distinguishes repair from no-op.
      if (
        before.harness === after.harness &&
        before.op === after.op &&
        before.tokens.reportedInput === after.tokens.reportedInput &&
        before.tokens.freshInput === after.tokens.freshInput
      ) {
        report.unchanged += 1
        continue
      }
      store.setAttribution(raw.spanId, {
        harness: after.harness,
        source: raw.source,
        op: after.op,
        tokens: after.tokens,
      })
      report.reattributed += 1
    }

    if (report.traces % progressEvery === 0) options.onProgress?.(report.traces, traceIds.length)
  }

  options.onProgress?.(report.traces, traceIds.length)
  return report
}

export type RenormalizeReport = {
  traces: number
  /** Records whose harness, operation or token decomposition changed. */
  reattributed: number
  unchanged: number
  /** Records no adapter claimed; left as they are rather than guessed at. */
  unclaimed: number
}
