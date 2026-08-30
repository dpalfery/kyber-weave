// Clean re-ingest for KyberDash (spec: docs/specs/kyberdash, task 8.2;
// requirement 15.3). When migration completes, the Python pipeline's 2.9 GB
// derived store is NOT carried forward: the corpus is reconstructible from
// the span exports that fed it, plus live OTLP from then on. This module is
// the reconstruction — the same ingest pipeline the receiver drives, applied
// to stored export batches instead of arriving ones.
//
// Reuse, not reimplementation, is the point. Each export batch is a decoded
// receiver payload (`OtlpSpan[]`, exactly what `decodeOtlpJson` /
// `decodeOtlpProtobuf` return, task 2), and from there the path is the
// normalization layer verbatim (task 5, task 6): spans are grouped and put
// to the two-pass fingerprint vote (`AdapterRegistry.attribute`, R6.2 — the
// source name rides along, never as evidence), claimed spans are normalized
// by the winning adapter's token convention (R4.2), the unclaimed remainder
// is quarantined with the namespaces it actually carried (R6.1), records are
// validated with the owning adapter's check and rejections persist problems
// rather than storing broken decompositions (R4.3, R4.4), and the accepted
// records land in one transactional `upsertMany` per batch (R2.5).
//
// One export batch is one unit of attribution — the same unit one live OTLP
// request is. That equivalence is what makes the rebuilt corpus THE corpus:
// a batch boundary is where the vote's group and the inheritance pass end,
// exactly as a request boundary is for live ingest, so re-ingesting the
// exports reproduces the corpus live ingest would have built, digest and
// all (the test asserts digest equality through `computeDigest`). Splitting
// a trace across two export batches splits its vote the same way splitting
// it across two live requests would; the exports are ingested as they were
// exported, not re-batched into a shape the original run never saw.
//
// The function is async with a yield between batches because reconstruction
// shares a process with the receiver it must not starve: requirement 15.3
// names both sources — existing span exports and live OTLP — and a
// 37,623-record rebuild must hand the event loop back between batches so
// live ingest keeps flowing while it runs.
//
// Idempotency rides on the store's key (R2.5): `span_id` is the primary key,
// so re-running the reconstruction over the same exports lands on the same
// rows. Quarantine entries replace; the problems and ingest logs are
// append-only audit trails, so a re-run appends to those, never to the
// corpus. And a freshly built store is self-describing: the schema version
// it stamped at construction (`schema_version` metadata) is how a rebuilt
// store is detected as current rather than silently misread.

import type { HarnessAdapter, RawSpan } from '../canon/adapters/base.js'
import { copilotAdapter } from '../canon/adapters/copilot.js'
import { geminiAdapter } from '../canon/adapters/gemini.js'
import { piAdapter } from '../canon/adapters/pi.js'
import { AdapterRegistry } from '../canon/adapters/registry.js'
import { quarantineUnclaimed, recordValidationProblems } from '../canon/adapters/quarantine.js'
import type { CanonStore } from '../canon/store.js'
import type { CanonicalRecord } from '../canon/types.js'
import type { OtlpSpan } from '../otel/receiver.js'

/** The harness adapters attribution votes over, in registration (tie-break) order. */
const ADAPTERS: readonly HarnessAdapter[] = [piAdapter, copilotAdapter, geminiAdapter]

/** Resource attribute the OTLP convention names the emitting process with. */
const SERVICE_NAME_ATTRIBUTE = 'service.name'

/**
 * The source name a span carries when its export named no service. A
 * grouping key only — attribution votes on attribute fingerprints, never on
 * it (R6.2) — so the neutral constant attributes nothing and hides nothing.
 */
const UNNAMED_SOURCE = 'otlp'

/** The ingest-log source stamped on reconstruction runs. */
const INGEST_SOURCE = 'span-exports'

/**
 * Project a receiver-decoded span onto the adapter seam's input shape. The
 * telemetry source is the export's `service.name` resource attribute — the
 * emitting process's instance name, used for grouping and same-source
 * inheritance lookup only (R6.2). Attributes pass through untouched; they
 * are the only admissible fingerprint evidence.
 */
function toRawSpan(span: OtlpSpan): RawSpan {
  const service = span.resource[SERVICE_NAME_ATTRIBUTE]
  return {
    spanId: span.spanId,
    traceId: span.traceId,
    parentSpanId: span.parentSpanId,
    source: typeof service === 'string' && service.length > 0 ? service : UNNAMED_SOURCE,
    attributes: span.attributes,
    name: span.name,
    kind: span.kind,
  }
}

/**
 * Give a normalized record the timing and outcome the adapter seam could not
 * carry. `RawSpan` is deliberately timing-free (the base contract fixes that
 * shape, and adapters stamp a neutral epoch), while the decoded span carries
 * the harness's own clock and status — the seam's documented growth path for
 * receiver-fed records. A corpus rebuilt from exports keeps the timestamps
 * its exports recorded; the timeline's session clock reads them.
 */
function growWithReceiverTiming(record: CanonicalRecord, span: OtlpSpan): void {
  record.timestamp = span.timestamp
  record.durationMs = span.durationMs
  record.status = span.status.code
}

/**
 * Rebuild the corpus from existing span exports into `store` (R15.3). Each
 * batch is one export — a receiver-decoded `ExportTraceServiceRequest` —
 * and is processed as one ingest unit: fingerprint vote, adapter
 * normalization, quarantine of the unclaimed remainder, validation with
 * problems persisted for rejections, then one transactional upsert of the
 * accepted records. A batch that fails is not swallowed: the error
 * propagates and nothing of that batch is stored (the upsert is a single
 * transaction), while batches already accepted stay accepted — reconstruction
 * resumes from the failure, not from zero, because re-running any batch is
 * idempotent (R2.5).
 *
 * The run appends one ingest-log entry (source `span-exports`) with the
 * total records it stored, so the audit trail shows the reconstruction
 * happened and how big it was.
 */
export async function reingestFromExports(
  exports: OtlpSpan[][],
  store: CanonStore,
): Promise<void> {
  const registry = new AdapterRegistry(ADAPTERS)
  const adapterByName = new Map(ADAPTERS.map((adapter) => [adapter.name, adapter]))

  let stored = 0
  for (const batch of exports) {
    const rawSpans = batch.map(toRawSpan)
    const attributed = registry.attribute(rawSpans)

    // Normalize every claimed span with the adapter that won its group's
    // vote, grouped per harness so each record is validated by the adapter
    // that produced it (R4.3). Unclaimed spans are quarantined with the
    // namespaces they carried (R6.1) — the work-order view's raw material.
    quarantineUnclaimed(rawSpans, attributed, store)

    const recordsByHarness = new Map<string, { adapter: HarnessAdapter; records: CanonicalRecord[] }>()
    for (const [index, raw] of rawSpans.entries()) {
      const harness = attributed.get(raw.spanId)
      if (harness === undefined) continue // quarantined above
      const adapter = adapterByName.get(harness)
      if (adapter === undefined) {
        // Unreachable: the registry only names adapters it was built with.
        throw new Error(`attribution named unregistered harness "${harness}"`)
      }
      const record = adapter.normalize(raw)
      growWithReceiverTiming(record, batch[index])
      const group = recordsByHarness.get(harness)
      if (group === undefined) {
        recordsByHarness.set(harness, { adapter, records: [record] })
      } else {
        group.records.push(record)
      }
    }

    // Validate per harness (R4.3, R4.4): rejected records are not stored —
    // their problems are persisted instead — and the accepted subset lands
    // in one transaction (R2.5).
    const accepted: CanonicalRecord[] = []
    for (const { adapter, records } of recordsByHarness.values()) {
      accepted.push(...recordValidationProblems(records, store, (record) => adapter.validate(record)))
    }
    store.upsertMany(accepted)
    stored += accepted.length

    // Hand the event loop back between batches so a live receiver in this
    // process keeps ingesting while the rebuild runs (R15.3: both sources).
    await new Promise<void>((resolve) => setImmediate(resolve))
  }

  store.logIngest(INGEST_SOURCE, stored)
}
