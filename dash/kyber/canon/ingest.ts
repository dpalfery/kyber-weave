// The ingest pipeline, shared by every path that puts spans into the store
// (spec: docs/specs/kyberdash; R2.5, R4.2, R4.3, R6.1, R6.2).
//
// This exists because there were two ingest paths and only one of them was
// correct. `tools/reingest.ts` ran the documented pipeline — fingerprint
// vote, per-adapter token convention, quarantine of the unclaimed remainder,
// validation with problems persisted — while the live OTLP collector in
// `otel/service.ts` did none of it. It attributed the harness from
// `service.name` (the one thing R6.2 says is never evidence), set `source`
// and `harness` to that same string, stamped `op: 'llm.invoke'` on every
// span whether or not it was a model call, and applied the cache-EXCLUSIVE
// conversion to every harness regardless of the convention that harness
// actually uses.
//
// That last one is not cosmetic. Feeding cache-INCLUSIVE counters through
// the exclusive conversion reassembles the total as
// `input + cacheRead + cacheCreation` when `input` already contained both,
// which double-counts input by up to 2x. The record still satisfies the sum
// identity, so `validateTokens` cannot see it — the per-adapter convention
// is the only thing that catches it, and the live path skipped the adapters
// entirely. Everything a live span was worth was decided by code that had
// never been through the vote.

import type { HarnessAdapter, RawSpan } from './adapters/base.js'
import { claudeCodeAdapter } from './adapters/claude-code.js'
import { copilotAdapter } from './adapters/copilot.js'
import { geminiAdapter } from './adapters/gemini.js'
import { piAdapter } from './adapters/pi.js'
import { AdapterRegistry } from './adapters/registry.js'
import { quarantineUnclaimed, recordValidationProblems } from './adapters/quarantine.js'
import type { CanonStore } from './store.js'
import type { CanonicalRecord } from './types.js'
import type { OtlpSpan } from '../otel/receiver.js'

/** The harness adapters attribution votes over, in registration (tie-break) order. */
export const ADAPTERS: readonly HarnessAdapter[] = [
  piAdapter,
  copilotAdapter,
  geminiAdapter,
  claudeCodeAdapter,
]

/** Resource attribute the OTLP convention names the emitting process with. */
const SERVICE_NAME_ATTRIBUTE = 'service.name'

/**
 * The source name a span carries when its export named no service. A
 * grouping key only — attribution votes on attribute fingerprints, never on
 * it (R6.2) — so the neutral constant attributes nothing and hides nothing.
 */
const UNNAMED_SOURCE = 'otlp'

/**
 * Project a receiver-decoded span onto the adapter seam's input shape. The
 * telemetry source is the export's `service.name` resource attribute, used
 * for grouping and same-source inheritance only. Attributes pass through
 * untouched; they are the only admissible fingerprint evidence.
 */
export function toRawSpan(span: OtlpSpan): RawSpan {
  const service = span.resource?.[SERVICE_NAME_ATTRIBUTE]
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
 * Give a normalized record the timing and outcome the adapter seam cannot
 * carry: `RawSpan` is deliberately timing-free and adapters stamp a neutral
 * epoch, while the decoded span carries the harness's own clock and status.
 */
export function growWithReceiverTiming(record: CanonicalRecord, span: OtlpSpan): void {
  record.timestamp = span.timestamp
  record.durationMs = span.durationMs
  record.status = span.status.code
}

/** What one batch did, so a caller can log or assert on it. */
export type IngestOutcome = {
  /** Records validated and stored. */
  accepted: number
  /** Spans no adapter claimed; held back with the namespaces they carried. */
  quarantined: number
  /** Claimed records whose token decomposition failed validation. */
  rejected: number
}

/**
 * Normalize and store one batch of decoded spans.
 *
 * A batch is one unit of attribution — the same unit one live OTLP request
 * is — so a live request and a stored export batch travel identical code and
 * land identical rows. Accepted records land in a single transaction (R2.5);
 * rejections persist their problem instead of a broken decomposition.
 */
export function ingestBatch(spans: readonly OtlpSpan[], store: CanonStore): IngestOutcome {
  if (spans.length === 0) return { accepted: 0, quarantined: 0, rejected: 0 }

  const registry = new AdapterRegistry(ADAPTERS)
  const adapterByName = new Map(ADAPTERS.map((adapter) => [adapter.name, adapter]))

  const rawSpans = spans.map(toRawSpan)
  const attributed = registry.attribute(rawSpans)

  quarantineUnclaimed(rawSpans, attributed, store)
  const quarantined = rawSpans.filter((raw) => attributed.get(raw.spanId) === undefined).length

  const byHarness = new Map<string, { adapter: HarnessAdapter; records: CanonicalRecord[] }>()
  for (const [index, raw] of rawSpans.entries()) {
    const harness = attributed.get(raw.spanId)
    if (harness === undefined) continue
    const adapter = adapterByName.get(harness)
    if (adapter === undefined) {
      // Unreachable: the registry only names adapters it was built with.
      throw new Error(`attribution named unregistered harness "${harness}"`)
    }
    const record = adapter.normalize(raw)
    growWithReceiverTiming(record, spans[index]!)
    const group = byHarness.get(harness)
    if (group === undefined) byHarness.set(harness, { adapter, records: [record] })
    else group.records.push(record)
  }

  let claimed = 0
  const accepted: CanonicalRecord[] = []
  for (const { adapter, records } of byHarness.values()) {
    claimed += records.length
    accepted.push(...recordValidationProblems(records, store, (record) => adapter.validate(record)))
  }
  store.upsertMany(accepted)

  return { accepted: accepted.length, quarantined, rejected: claimed - accepted.length }
}
