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
import { canonicalSessionId } from './adapters/copilot.js'
import { claudeCodeAdapter } from './adapters/claude-code.js'
import { codexAdapter } from './adapters/codex.js'
import { copilotAdapter } from './adapters/copilot.js'
import { geminiAdapter } from './adapters/gemini.js'
import { antigravityAdapter } from './adapters/antigravity.js'
import { opencodeAdapter } from './adapters/opencode.js'
import { piAdapter } from './adapters/pi.js'
import { AdapterRegistry } from './adapters/registry.js'
import {
  observedNamespaces,
  recordValidationProblems,
} from './adapters/quarantine.js'
import type { CanonStore } from './store.js'
import type { CanonicalRecord } from './types.js'
import type { OtlpSpan } from '../otel/receiver.js'
import { ingestLogBatch } from './log-ingest.js'

/** The harness adapters attribution votes over, in registration (tie-break) order. */
export const ADAPTERS: readonly HarnessAdapter[] = [
  piAdapter,
  copilotAdapter,
  // Antigravity stays after Copilot (the abbreviated Antigravity shape
  // pinned at ingest.test.ts:56-67 ties Copilot and resolves to it) and
  // before Gemini (ties on the full agent-identity shape resolve here).
  antigravityAdapter,
  geminiAdapter,
  claudeCodeAdapter,
  codexAdapter,
  // OpenCode placeholder last: it detects nothing and claims no span, so
  // every existing vote resolves above it. The Phase 2 OpenCode task fills in
  // adapters/opencode.ts without moving this tie-break.
  opencodeAdapter,
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
    timestamp: span.timestamp,
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

/** Distinguish ambient telemetry from a span merely lacking an adapter. */
function isStructuralSpan(span: RawSpan): boolean {
  const name = span.name.toLowerCase()
  return (
    span.kind === 'server' ||
    name.includes('health') ||
    Object.keys(span.attributes).some((key) => key.startsWith('http.') || key.startsWith('db.'))
  )
}

/** What one batch did, so a caller can log or assert on it. */
export type IngestOutcome = {
  /** Records validated and stored. */
  accepted: number
  /** Spans held out of the corpus because no adapter claimed them or they were non-model. */
  quarantined: number
  /** Claimed records whose token decomposition failed validation. */
  rejected: number
}

/**
 * Longest quarantine `rawPayload` stored, in UTF-16 code units. A quarantined span keeps
 * its decoded JSON as evidence, and one span can carry whole prompts; past this the
 * stored text is cut and ends in a marker naming the original length, so a truncated
 * payload is never mistaken for the complete one (it is no longer valid JSON, and says so).
 */
export const MAX_QUARANTINE_RAW_PAYLOAD_CHARS = 256 * 1024

/** The payload as stored: whole when within the cap, else a prefix plus a truncation marker. */
export function boundRawPayload(json: string): string {
  if (json.length <= MAX_QUARANTINE_RAW_PAYLOAD_CHARS) return json
  return `${json.slice(0, MAX_QUARANTINE_RAW_PAYLOAD_CHARS)}\n...[truncated: stored ${MAX_QUARANTINE_RAW_PAYLOAD_CHARS} of ${json.length} characters]`
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
  return ingestBatchWithAdapters(spans, store, ADAPTERS)
}

/**
 * The batch core with the adapter set injected. Production passes `ADAPTERS`; tests pass
 * a throwing adapter to prove a normalize failure is recorded with its stack rather than
 * aborting the batch (a throw that escaped here used to propagate through the writer's
 * flush, re-queue the whole batch, and retry it forever).
 */
export function ingestBatchWithAdapters(
  spans: readonly OtlpSpan[],
  store: CanonStore,
  adapters: readonly HarnessAdapter[],
): IngestOutcome {
  if (spans.length === 0) return { accepted: 0, quarantined: 0, rejected: 0 }

  const registry = new AdapterRegistry(adapters)
  const adapterByName = new Map(adapters.map((adapter) => [adapter.name, adapter]))

  const rawSpans = spans.map(toRawSpan)
  const attributed = registry.attribute(rawSpans)

  // The exact incoming span for the quarantine detail route (v18 `raw_payload`).
  // Decoded-span JSON, not the normalized record: the record is what attribution made
  // of the span, and mislabeling it as the original message is forbidden.
  const rawPayloadOf = (index: number): string => boundRawPayload(JSON.stringify(spans[index]))

  const unclaimed: string[] = []
  for (const [index, raw] of rawSpans.entries()) {
    if (attributed.has(raw.spanId)) continue
    unclaimed.push(raw.spanId)
    store.quarantineAndDelete(
      raw.spanId,
      observedNamespaces(raw.attributes),
      isStructuralSpan(raw) ? 'non-model span' : 'unclaimed',
      {
        source: raw.source,
        name: raw.name,
        timestamp: spans[index]?.timestamp ?? raw.timestamp,
        rawPayload: rawPayloadOf(index),
      },
    )
  }
  let quarantined = unclaimed.length
  let rejected = 0

  const byHarness = new Map<string, { adapter: HarnessAdapter; records: CanonicalRecord[] }>()
  for (const [index, raw] of rawSpans.entries()) {
    const harness = attributed.get(raw.spanId)
    if (harness === undefined) continue
    const adapter = adapterByName.get(harness)
    if (adapter === undefined) {
      // Unreachable: the registry only names adapters it was built with.
      throw new Error(`attribution named unregistered harness "${harness}"`)
    }

    let record: CanonicalRecord
    try {
      record = adapter.normalize(raw)
    } catch (err) {
      // The span WAS attributed; its normalization is what failed. Record the failure
      // with the exact message and the exception stack, and keep the batch moving —
      // the alternative is the writer re-queueing this batch on every flush, forever.
      rejected += 1
      store.recordProblem({
        spanId: raw.spanId,
        severity: 'error',
        code: 'ADAPTER_NORMALIZE_FAILED',
        message: err instanceof Error ? err.message : String(err),
        ...(err instanceof Error && typeof err.stack === 'string' ? { stack: err.stack } : {}),
        location: raw.spanId,
        sessionId: canonicalSessionId(raw.attributes) ?? null,
        harness,
        timestamp: spans[index]?.timestamp ?? raw.timestamp ?? null,
      })
      continue
    }
    growWithReceiverTiming(record, spans[index]!)
    if (record.op === 'unspecified') {
      // Attribution establishes the harness, not that every span in its trace
      // is a model call. Keep structural and ambient spans out of both records
      // and derived sessions while retaining an auditable quarantine row.
      store.quarantineAndDelete(
        raw.spanId,
        observedNamespaces(raw.attributes),
        'non-model span',
        {
          source: raw.source,
          name: raw.name,
          timestamp: spans[index]?.timestamp ?? raw.timestamp,
          rawPayload: rawPayloadOf(index),
        },
      )
      quarantined += 1
      continue
    }

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
  const pendingLogs = store.getPendingLogs()
  if (pendingLogs.length > 0) {
    ingestLogBatch(pendingLogs as Parameters<typeof ingestLogBatch>[0], store)
  }

  // `rejected` counts both validation rejections and normalize failures: neither produced
  // a canonical record, and both persisted their problem instead.
  return { accepted: accepted.length, quarantined, rejected: claimed - accepted.length + rejected }
}
