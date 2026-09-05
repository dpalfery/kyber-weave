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

import { ingestBatch } from '../canon/ingest.js'
import type { CanonStore } from '../canon/store.js'
import type { OtlpSpan } from '../otel/receiver.js'

/** The ingest-log source stamped on reconstruction runs. */
const INGEST_SOURCE = 'span-exports'

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
  let stored = 0
  for (const batch of exports) {
    stored += ingestBatch(batch, store).accepted

    // Hand the event loop back between batches so a live receiver in this
    // process keeps ingesting while the rebuild runs (R15.3: both sources).
    await new Promise<void>((resolve) => setImmediate(resolve))
  }

  store.logIngest(INGEST_SOURCE, stored)
}
