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
import {
  claudeCodeAdapter,
  claudeUserPrompt,
  isClaudeInteractionSpan,
  REDACTED_USER_PROMPT_REASON,
  type ClaudeUserPrompt,
} from './adapters/claude-code.js'
import { canonicalSessionId } from './adapters/copilot.js'
import { codexAdapter } from './adapters/codex.js'
import { copilotAdapter, UNKNOWN_TIMESTAMP } from './adapters/copilot.js'
import { geminiAdapter } from './adapters/gemini.js'
import { antigravityAdapter } from './adapters/antigravity.js'
import { opencodeAdapter } from './adapters/opencode.js'
import { piAdapter } from './adapters/pi.js'
import { derivePiToolRecords } from './adapters/pi-tools.js'
import { AdapterRegistry } from './adapters/registry.js'
import {
  observedNamespaces,
  recordValidationProblems,
} from './adapters/quarantine.js'
import type { CanonStore } from './store.js'
import {
  contentFromParts,
  isNotMeasurable,
  notMeasurable,
  type CanonicalRecord,
  type ContentPart,
  type Measurability,
} from './types.js'
import type { OtlpLog, OtlpSpan } from '../otel/receiver.js'
import { DEFAULT_PENDING_TTL_MS, ingestLogBatch, PENDING_INTERACTION_PROMPT } from './log-ingest.js'

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

// ---------------------------------------------------------------------------
// Claude Code interaction prompt (B2/Q4/B8)
// ---------------------------------------------------------------------------
//
// The interaction span is the turn's only prompt carrier: Claude Code's
// `llm_request` spans hold counters and metadata, never message text (R1).
// Quarantining the span as non-model is still right; what was wrong was
// letting its `user_prompt` be deleted with the row. The prompt therefore
// always parks in `pending_logs`: children already stored get it at once,
// and the settle pass keeps serving each llm.invoke child that turns up
// later still lacking the prompt's bucket, until the wait's TTL retires the row (B8).

/**
 * The instrumentation scope Claude Code event logs carry (B3). Stamping it
 * on a parked prompt keeps `isClaudeLog` recognising the row, so the
 * harness tag the wipe scopes by survives every re-pend the replay performs.
 */
const CLAUDE_EVENTS_SCOPE = 'com.anthropic.claude_code.events'

/** The `pending_logs` payload of a parked interaction prompt (B8). */
type PendingInteractionPrompt = OtlpLog & { pendingSince?: number; harness?: string }

/**
 * The `llm.invoke` records beneath one interaction span — Q4's whole answer
 * to "which records get the prompt". The lookup keys on parentage (trace +
 * parent span), never on the interaction itself: it is quarantined, so no
 * record exists for an exact span lookup to find. The `harness` + `op`
 * gate is B1's — the same one log enrichment applies — and span names stay
 * out of it.
 */
function interactionChildren(
  store: CanonStore,
  traceId: string,
  interactionSpanId: string,
): CanonicalRecord[] {
  return store
    .childrenOf(traceId, interactionSpanId)
    .filter((record) => record.harness === 'claude-code' && record.op === 'llm.invoke')
}

/**
 * A redacted prompt is not absent data — the sender holds it back — so the
 * record keeps an honest `not_measurable` naming the opt-in that would
 * release it (Q2), in place of the generic unexported-bucket reason
 * normalize stamped.
 */
function declareRedactedPrompt(store: CanonStore, record: CanonicalRecord): void {
  store.setMeasurability(record.spanId, {
    ...record.measurability,
    conversation_history: notMeasurable(REDACTED_USER_PROMPT_REASON),
  })
}

/**
 * A bucket the prompt just supplied is measurable again (B6): drop its
 * `not_measurable` entry — the same per-record rule log enrichment applies
 * (`clearSuppliedBuckets` in log-ingest.ts). Buckets the prompt cannot
 * supply keep the reasons they were declared with.
 */
function clearSuppliedBuckets(
  store: CanonStore,
  record: CanonicalRecord,
  parts: readonly ContentPart[],
): void {
  const gained = new Set(parts.map((part) => part.part))
  const measurability: Measurability = { ...record.measurability }
  let cleared = false
  for (const bucket of gained) {
    const declared = measurability[bucket]
    if (declared !== undefined && isNotMeasurable(declared)) {
      delete measurability[bucket]
      cleared = true
    }
  }
  if (cleared) store.setMeasurability(record.spanId, measurability)
}

/**
 * Give one interaction prompt to every stored llm.invoke child that still
 * lacks the prompt's bucket (Q4), or declare the redaction on it. The guard
 * is per bucket, not per child: a child holding only a `system_prompt` part
 * (a body parse cut before its messages array) still has no
 * `conversation_history`, and the prompt is the only source for that bucket —
 * delivering appends the prompt part to what the child already carries rather
 * than replacing it. A child that already holds `conversation_history` got it
 * from an api_request_body parse — the richer source, which subsumes the
 * prompt-derived part — or from an earlier pass over this same prompt; either
 * way the interaction path leaves it alone. The same check is the redacted
 * case's protection: a redaction explains absent text, so it never stamps
 * `not_measurable` over a bucket the record actually holds. The bucket check
 * is also what makes the parked re-delivery below idempotent.
 */
function deliverInteractionPrompt(
  store: CanonStore,
  traceId: string,
  interactionSpanId: string,
  prompt: ClaudeUserPrompt,
): void {
  const children = interactionChildren(store, traceId, interactionSpanId)
  for (const child of children) {
    const hasHistory =
      (child.parts?.some((part) => part.part === 'conversation_history') ?? false) ||
      child.content['conversation_history'] !== undefined
    if (hasHistory) continue
    if (prompt.redacted) {
      declareRedactedPrompt(store, child)
      continue
    }
    // Merge with whatever the child already holds: a body-derived part and the
    // prompt-derived part describe different buckets here, and replacing the
    // parts list would drop the body's buckets (Q4).
    const parts: ContentPart[] = [
      ...(child.parts ?? []),
      { part: 'conversation_history', text: prompt.text },
    ]
    store.setContent(child.spanId, contentFromParts(parts), parts)
    clearSuppliedBuckets(store, child, parts)
  }
}

/**
 * Harvest the prompt a B2 interaction span carries before its packet lands
 * in quarantine (R1). Children already stored receive it immediately, but
 * the prompt parks regardless: the turn's llm.invoke calls do not all
 * arrive in one batch — a child later in THIS batch has not even been
 * written yet — so the parked row is what lets the settle pass keep
 * serving every child that shows up still lacking the prompt's bucket (B8).
 */
function keepInteractionPrompt(store: CanonStore, span: RawSpan): void {
  const prompt = claudeUserPrompt(span.attributes['user_prompt'])
  if (prompt === undefined || span.traceId === null) return
  deliverInteractionPrompt(store, span.traceId, span.spanId, prompt)

  // The wait rides the pending-log machinery rather than a new table: keyed
  // by trace and interaction span id, tagged `claude-code` so a per-harness
  // wipe scopes it, under the existing TTL — an expired wait is retired by
  // the settle pass or the reconcile timer, leaving the quarantine packet
  // the prompt's only copy (B8). Its shape is what
  // the `ingestLogBatch` replay leaves alone: trace/span ids set (a null
  // pair would quarantine it), no sessionId (the session/time fallback
  // would land it on a single child), and the events scope so `isClaudeLog`
  // keeps the harness tag across re-pends.
  const entry: PendingInteractionPrompt = {
    logId: `interaction-prompt:${span.traceId}:${span.spanId}`,
    traceId: span.traceId,
    spanId: span.spanId,
    sessionId: null,
    timestamp: span.timestamp ?? UNKNOWN_TIMESTAMP,
    body: null,
    attributes: { [PENDING_INTERACTION_PROMPT]: span.attributes['user_prompt'] },
    resource: {},
    scope: { name: CLAUDE_EVENTS_SCOPE },
    pendingSince: Date.now(),
  }
  store.addPendingLog(entry, 'claude-code')
}

/**
 * Deliver prompts an earlier batch parked (B8). Delivery is per-child —
 * Q4's "every llm.invoke child" means a turn whose model calls arrive over
 * several batches must see each one served, so the row stays parked while
 * it is young enough for more children to be on their way, and each pass
 * skips whatever already carries parts. The row leaves only two ways: the
 * parked value proves unclassifiable, or the wait itself expires — expiry
 * makes the interaction span's quarantine packet the prompt's sole copy;
 * it does not earn the prompt a second home in `quarantined_logs`.
 */
function settlePendingInteractionPrompts(store: CanonStore, now: number, ttlMs: number): void {
  for (const log of store.getPendingLogs() as PendingInteractionPrompt[]) {
    const value = log.attributes[PENDING_INTERACTION_PROMPT]
    if (value === undefined || log.traceId === null || log.spanId === null) continue
    const prompt = claudeUserPrompt(value)
    if (prompt === undefined) {
      // The parked value is our own write: unclassifiable means done rather
      // than left to ride the TTL.
      store.deletePendingLog(log.logId)
      continue
    }
    deliverInteractionPrompt(store, log.traceId, log.spanId, prompt)
    const pendingSince = log.pendingSince ?? now
    if (now - pendingSince >= ttlMs) store.deletePendingLog(log.logId)
  }
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
        // The whole decoded span is the evidence the audit row keeps (B7) —
        // once the record is gone this packet is the only surviving copy.
        // No harness: attribution named none for this span.
        rawPayload: rawPayloadOf(index),
      },
    )
  }
  let quarantined = unclaimed.length
  let rejected = 0

  const byHarness = new Map<string, { adapter: HarnessAdapter; records: CanonicalRecord[] }>()
  // Derived tool span ids already claimed in this batch. The same Pi
  // (session, call id) recurs on every turn span, and within one batch the
  // store has none of them yet, so this is what keeps the first sighting.
  const derivedSeen = new Set<string>()
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
      // A claude-code interaction span carries the turn's only prompt text
      // (R1): harvest it for the llm.invoke children before the packet is
      // filed into quarantine — once the row is gone the quarantine packet
      // is the only place the prompt still exists (B2/Q4/B8).
      if (harness === 'claude-code' && isClaudeInteractionSpan(raw)) {
        keepInteractionPrompt(store, raw)
      }
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
          // Same evidence as the unclaimed path, plus the harness attribution
          // named so the row is scoped for a per-harness wipe (B7/B10).
          rawPayload: rawPayloadOf(index),
          harness,
        },
      )
      quarantined += 1
      continue
    }

    const group = byHarness.get(harness)
    if (group === undefined) byHarness.set(harness, { adapter, records: [record] })
    else group.records.push(record)

    // Pi exports no child tool spans: the tool calls its conversation carries
    // inline are recovered here and stored with their turn. Harness-scoped on
    // purpose — every other OTLP harness emits real `gen_ai.tool.name` spans,
    // and deriving from those would double-count them.
    if (harness === 'pi') {
      const piGroup = byHarness.get(harness)
      if (piGroup !== undefined) {
        for (const toolRecord of derivePiToolRecords(record, raw)) {
          // Keep the first write (an earlier batch's, or this batch's): the
          // call's parent and timing are then the earliest turn that carried it.
          if (derivedSeen.has(toolRecord.spanId) || store.has(toolRecord.spanId)) continue
          derivedSeen.add(toolRecord.spanId)
          piGroup.records.push(toolRecord)
        }
      }
    }
  }

  let claimed = 0
  const accepted: CanonicalRecord[] = []
  for (const { adapter, records } of byHarness.values()) {
    claimed += records.length
    accepted.push(...recordValidationProblems(records, store, (record) => adapter.validate(record)))
  }
  store.upsertMany(accepted)
  // Prompts parked by an earlier batch deliver now that this batch's
  // children exist (B8). Running before the log replay is about order, not
  // just timing: an api_request_body parse landing in the same replay must
  // replace the prompt-derived part, not be replaced by it (Q4).
  settlePendingInteractionPrompts(store, Date.now(), DEFAULT_PENDING_TTL_MS)
  const pendingLogs = store.getPendingLogs()
  if (pendingLogs.length > 0) {
    ingestLogBatch(pendingLogs as Parameters<typeof ingestLogBatch>[0], store)
  }

  // `rejected` counts both validation rejections and normalize failures: neither produced
  // a canonical record, and both persisted their problem instead.
  return { accepted: accepted.length, quarantined, rejected: claimed - accepted.length + rejected }
}
