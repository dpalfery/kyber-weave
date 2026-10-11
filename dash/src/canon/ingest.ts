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
import { codexAdapter } from './adapters/codex.js'
import { copilotAdapter, UNKNOWN_TIMESTAMP } from './adapters/copilot.js'
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
// letting its `user_prompt` be deleted with the row. The two halves below
// cover the two orders the exporter can pick: children already stored get
// the prompt directly, and a prompt that arrives first waits in
// `pending_logs` for the settle pass at batch end.

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
 * Give one interaction prompt to every stored llm.invoke child (Q4), or
 * declare the redaction on them. Returns whether any child was there to
 * receive it — false means the turn's children have not arrived and the
 * prompt must wait for a later batch (B8).
 */
function deliverInteractionPrompt(
  store: CanonStore,
  traceId: string,
  interactionSpanId: string,
  prompt: ClaudeUserPrompt,
): boolean {
  const children = interactionChildren(store, traceId, interactionSpanId)
  for (const child of children) {
    if (prompt.redacted) {
      declareRedactedPrompt(store, child)
      continue
    }
    // A single prompt-derived part is everything the span carried; a later
    // api_request_body parse replaces it wholesale rather than the turn
    // drawing text from two sources (Q4).
    const parts: ContentPart[] = [{ part: 'conversation_history', text: prompt.text }]
    store.setContent(child.spanId, contentFromParts(parts), parts)
    clearSuppliedBuckets(store, child, parts)
  }
  return children.length > 0
}

/**
 * Harvest the prompt a B2 interaction span carries before its packet lands
 * in quarantine (R1). Children already stored receive it immediately;
 * otherwise it is parked and the settle pass delivers it when the turn's
 * children arrive (B8).
 */
function keepInteractionPrompt(store: CanonStore, span: RawSpan): void {
  const prompt = claudeUserPrompt(span.attributes['user_prompt'])
  if (prompt === undefined || span.traceId === null) return
  if (deliverInteractionPrompt(store, span.traceId, span.spanId, prompt)) return

  // The wait rides the pending-log machinery rather than a new table: keyed
  // by trace and interaction span id, tagged `claude-code` so a per-harness
  // wipe scopes it, under the existing TTL — an expired wait with no
  // children is retired by the settle pass, leaving the quarantine packet
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
 * Deliver prompts an earlier batch parked (B8). An entry is consumed —
 * deleted before the log replay below can see it — once at least one of its
 * interaction's llm.invoke children is stored. Parked prompts are exempt
 * from `reconcileExpiredPending`'s move to `quarantined_logs`, so this pass
 * is also where an expired wait with no children is retired: the
 * interaction span's quarantine packet then holds the prompt's only copy.
 */
function settlePendingInteractionPrompts(store: CanonStore, now: number, ttlMs: number): void {
  for (const log of store.getPendingLogs() as PendingInteractionPrompt[]) {
    const value = log.attributes[PENDING_INTERACTION_PROMPT]
    if (value === undefined || log.traceId === null || log.spanId === null) continue
    const prompt = claudeUserPrompt(value)
    if (
      prompt === undefined ||
      deliverInteractionPrompt(store, log.traceId, log.spanId, prompt)
    ) {
      // The parked value is our own write: delivered, or unclassifiable —
      // either way the row is done rather than left to ride the TTL.
      store.deletePendingLog(log.logId)
      continue
    }
    // No child has arrived. Once the wait itself has expired the row is
    // retired and the prompt survives only in the interaction span's
    // quarantine packet (B8): expiry makes the packet the sole copy — it
    // does not earn the prompt a second home in `quarantined_logs`.
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
        rawPayload: JSON.stringify(spans[index]),
      },
    )
  }
  let quarantined = unclaimed.length

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
          rawPayload: JSON.stringify(spans[index]),
          harness,
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
  // Prompts parked by an earlier batch deliver now that this batch's
  // children exist (B8). Running before the log replay is about order, not
  // just timing: an api_request_body parse landing in the same replay must
  // replace the prompt-derived part, not be replaced by it (Q4).
  settlePendingInteractionPrompts(store, Date.now(), DEFAULT_PENDING_TTL_MS)
  const pendingLogs = store.getPendingLogs()
  if (pendingLogs.length > 0) {
    ingestLogBatch(pendingLogs as Parameters<typeof ingestLogBatch>[0], store)
  }

  return { accepted: accepted.length, quarantined, rejected: claimed - accepted.length }
}
