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

import { ingestBatch } from '../canon/ingest.js'
import { CanonStore } from '../canon/store.js'
import { canonicalParts, canonicalSessionId } from '../canon/adapters/copilot.js'
import { contentFromParts, type CanonicalRecord } from '../canon/types.js'
import type { OtlpSpan } from '../otel/receiver.js'

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

/** Reconstruct the receiver shape around retained raw evidence. */
function toOtlpSpan(record: CanonicalRecord): OtlpSpan {
  const timestamp = typeof record.timestamp === 'string' ? record.timestamp : record.timestamp.toISOString()
  const startMs = Date.parse(timestamp)
  const startNano = Number.isFinite(startMs) ? BigInt(startMs) * 1_000_000n : 0n
  const durationNano = BigInt(Math.max(0, Math.round(record.durationMs * 1_000_000)))
  const endNano = startNano + durationNano
  return {
    traceId: record.traceId!,
    spanId: record.spanId,
    parentSpanId: record.parentSpanId,
    name: record.name,
    kind: record.kind,
    startTimeUnixNano: String(startNano),
    endTimeUnixNano: String(endNano),
    timestamp,
    durationMs: record.durationMs,
    status: { code: record.status === 'error' ? 'error' : record.status === 'unset' ? 'unset' : 'ok' },
    attributes: record.raw as Record<string, unknown>,
    resource: { 'service.name': record.source },
    scope: {},
  }
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
  const traceIds = store.traceIds()
  const progressEvery = options.progressEvery ?? 200
  const report: RenormalizeReport = { traces: 0, reattributed: 0, unchanged: 0, unclaimed: 0 }

  for (const traceId of traceIds) {
    report.traces += 1
    const records = store.recordsForTrace(traceId)
    const withRaw = records.filter(
      (record): record is CanonicalRecord & { raw: Record<string, unknown> } =>
        record.raw !== undefined && record.raw !== null && typeof record.raw === 'object',
    )
    if (withRaw.length === 0) continue

    const explicitGemini = withRaw.filter(
      (record) => (record.raw as Record<string, unknown>)['gen_ai.system'] === 'gemini',
    )
    const grouped = withRaw.filter(
      (record) => (record.raw as Record<string, unknown>)['gen_ai.system'] !== 'gemini',
    )
    for (const record of explicitGemini) ingestBatch([toOtlpSpan(record)], store)
    if (grouped.length > 0) ingestBatch(grouped.map(toOtlpSpan), store)
    for (const record of explicitGemini) {
      const repaired = store.get(record.spanId)
      if (repaired !== undefined && repaired.harness !== 'gemini') {
        // A retained row with explicit vendor identity must not inherit a
        // competing sibling's harness from the historical trace vote.
        store.setAttribution(record.spanId, {
          harness: 'gemini',
          source: repaired.source,
          op: repaired.op,
          tokens: repaired.tokens,
        })
      }
    }
    for (const before of withRaw) {
      const after = store.get(before.spanId)
      if (after === undefined) {
        if (store.getQuarantine(before.spanId)?.reason === 'unclaimed') report.unclaimed += 1
        continue
      }
      if (
        before.harness === after.harness &&
        before.op === after.op &&
        before.tokens.reportedInput === after.tokens.reportedInput &&
        before.tokens.freshInput === after.tokens.freshInput
      ) {
        report.unchanged += 1
      } else {
        report.reattributed += 1
      }
      // Attribution is repaired from raw evidence, while content is retained
      // exactly as collected. This protects richer signal-specific parts that
      // the adapter may not know how to reconstruct.
      if (before.parts !== undefined || Object.keys(before.content).length > 0) {
        store.setContent(before.spanId, before.content, before.parts)
      }
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
