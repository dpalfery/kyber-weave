import { describe, expect, it } from 'vitest'

import { ingestBatch } from './ingest.js'
import { ingestLogBatch, reconcileExpiredPending, type OtlpLog } from './log-ingest.js'
import { CanonStore, type QuarantineEntry } from './store.js'
import { otlpSpanToRecord } from '../otel/service.js'
import type { OtlpSpan } from '../otel/receiver.js'
import antigravitySpan from './adapters/__fixtures__/antigravity-span.json' with { type: 'json' }

// The live collector had its own normalization that never went through the
// adapter vote. These tests pin the difference, because the failure it caused
// is invisible to `validateTokens`: applying the cache-EXCLUSIVE conversion
// to cache-INCLUSIVE counters still satisfies the sum identity, it just
// counts the cached input twice.

function span(attributes: Record<string, unknown>, over: Partial<OtlpSpan> = {}): OtlpSpan {
  return {
    spanId: 'span-1',
    traceId: 'trace-1',
    parentSpanId: null,
    name: 'llm_request',
    kind: 'client',
    timestamp: '2026-09-03T10:00:00.000Z',
    durationMs: 100,
    status: { code: 'ok' },
    resource: { 'service.name': 'some-service' },
    attributes,
    ...over,
  } as OtlpSpan
}

/**
 * Antigravity's real fingerprint, from the corpus: it emits Copilot-namespaced
 * attributes, so the vote claims it for the copilot adapter and it gets that
 * adapter's cache-INCLUSIVE convention — which the data confirms is correct.
 * The harness LABEL is therefore `copilot` even though `gen_ai.agent.name`
 * says `antigravity`; attribution is by attribute fingerprint, never by a
 * name a span asserts about itself (R6.2).
 */
const geminiAttributes = {
  'copilot_chat.chat_session_id': '08551cf5-b064-4095-9552-8a9a0a0f78d2',
  'gen_ai.agent.name': 'antigravity',
  'gen_ai.usage.input_tokens': 251_976,
  'gen_ai.usage.cache_read.input_tokens': 243_910,
  'gen_ai.usage.output_tokens': 1_000,
}

/** Claude Code shape: bare keys, and input_tokens EXCLUDES cache. */
const claudeAttributes = {
  'claude.deployment_mode': '1p',
  input_tokens: 2,
  cache_read_tokens: 35_739,
  cache_creation_tokens: 22_748,
  output_tokens: 13,
}

describe('ingestBatch — per-harness token conventions (R4.2)', () => {
  it('treats Antigravity/Copilot counters as cache-inclusive', () => {
    const store = new CanonStore(':memory:')
    ingestBatch([span(geminiAttributes)], store)
    const record = store.get('span-1')

    // Inclusive: fresh is recovered by SUBTRACTING the cache classes, and
    // the reported total is the harness's own figure, unchanged.
    expect(record?.tokens.freshInput).toBe(251_976 - 243_910)
    expect(record?.tokens.reportedInput).toBe(251_976)
    expect(record?.harness).toBe('copilot')
    store.close()
  })

  it('attributes the live agy shape to antigravity, not the excluded gemini identity', () => {
    // Issue #195: the live Antigravity exporter sets `service.name: 'agy'`
    // and carries both `gen_ai.agent.name: 'antigravity'` and
    // `gen_ai.system: 'gemini'`. The agent identity wins the vote, so the
    // rows land on the surveyed `antigravity` harness the projection keeps.
    const store = new CanonStore(':memory:')
    ingestBatch(
      [
        span(antigravitySpan as Record<string, unknown>, {
          resource: { 'service.name': 'agy' },
        } as Partial<OtlpSpan>),
      ],
      store,
    )
    const record = store.get('span-1')

    expect(record?.harness).toBe('antigravity')
    expect(record?.source).toBe('agy')
    expect(record?.tokens.freshInput).toBe(251_976 - 243_910)
    expect(record?.tokens.reportedInput).toBe(251_976)
    store.close()
  })

  it('does not double-count cached input the way the collector used to', () => {
    // The old path reassembled the total as input + cacheRead, counting the
    // cached input twice — a 1.97x inflation on this span. It is kept here
    // as the regression this test exists to prevent.
    const legacy = otlpSpanToRecord(span(geminiAttributes))
    const store = new CanonStore(':memory:')
    ingestBatch([span(geminiAttributes)], store)
    const fixed = store.get('span-1')

    expect(legacy.tokens.reportedInput).toBe(495_886)
    expect(fixed?.tokens.reportedInput).toBe(251_976)
    expect(legacy.tokens.reportedInput / fixed!.tokens.reportedInput).toBeCloseTo(1.97, 1)
    store.close()
  })

  it('treats Claude Code counters as cache-exclusive, and reads its bare keys', () => {
    const store = new CanonStore(':memory:')
    ingestBatch([span(claudeAttributes)], store)
    const record = store.get('span-1')

    // Exclusive: fresh is taken as claimed, the total is reassembled.
    expect(record?.tokens.freshInput).toBe(2)
    expect(record?.tokens.reportedInput).toBe(2 + 35_739 + 22_748)
    expect(record?.harness).toBe('claude-code')
    store.close()
  })

  it('would have produced an impossible decomposition under the wrong convention', () => {
    // Every Claude Code span with cache activity goes negative under the
    // inclusive conversion. That is what makes the corpus unambiguous.
    expect(2 - 35_739 - 22_748).toBeLessThan(0)
  })
})

describe('ingestBatch — attribution and structure', () => {
  it('never takes the harness from the service name (R6.2)', () => {
    const store = new CanonStore(':memory:')
    ingestBatch(
      [span(geminiAttributes, { resource: { 'service.name': 'totally-misleading' } } as Partial<OtlpSpan>)],
      store,
    )

    expect(store.get('span-1')?.harness).toBe('copilot')
    // The source is retained for grouping, distinct from the harness.
    expect(store.get('span-1')?.source).toBe('totally-misleading')
    store.close()
  })

  it('decides the operation from what the span carries, not a constant', () => {
    const store = new CanonStore(':memory:')
    ingestBatch(
      [
        span({ ...geminiAttributes, 'gen_ai.tool.name': 'read_file' }, { spanId: 'tool-1' }),
        span(geminiAttributes, { spanId: 'llm-1' }),
      ],
      store,
    )

    // The old collector stamped `llm.invoke` on every span, which made tool
    // spans indistinguishable from model calls — so nothing downstream could
    // tell which tools were ever actually invoked.
    expect(store.get('tool-1')?.op).toBe('tool.invoke')
    expect(store.get('llm-1')?.op).toBe('llm.invoke')
    store.close()
  })

  it('quarantines claimed health and HTTP-server noise while retaining the model call', () => {
    // Trace inheritance establishes the harness, not that every span in its trace
    // is a model call. These are the two high-volume ambient shapes that inflated
    // the corpus into sessions which never happened.
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch(
      [
        span({ ...geminiAttributes, 'codeburn.provider': 'github-copilot' }, { spanId: 'model-1', traceId: 'claimed-trace' }),
        span(
          { 'codeburn.provider': 'github-copilot' },
          { spanId: 'health-1', traceId: 'claimed-trace', name: 'GlobalHttpApi.health' },
        ),
        span(
          { 'codeburn.provider': 'github-copilot', 'http.request.method': 'GET', 'url.path': '/v1/messages' },
          { spanId: 'http-server-1', traceId: 'claimed-trace', name: 'GET /v1/messages', kind: 'server' },
        ),
      ],
      store,
    )

    expect(outcome).toMatchObject({ accepted: 1, quarantined: 2, rejected: 0 })
    expect(store.count()).toBe(1)
    expect(store.get('model-1')?.op).toBe('llm.invoke')
    expect(store.get('health-1')).toBeUndefined()
    expect(store.get('http-server-1')).toBeUndefined()
    expect(store.getQuarantine('health-1')?.reason).toMatch(/non[- ]model/i)
    expect(store.getQuarantine('http-server-1')?.reason).toMatch(/non[- ]model/i)
    store.close()
  })

  it.each([
    ['health span', 'GlobalHttpApi.health', {}],
    ['HTTP server span', 'GET /healthz', { 'http.request.method': 'GET', 'url.path': '/healthz' }],
    ['other span without model-call evidence', 'connection pool checkout', { 'db.system.name': 'sqlite' }],
  ])('quarantines wholly unattributed $0 with a non-model reason', (_shape, name, attributes) => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch([span(attributes, { spanId: `noise-${name}`, name })], store)

    expect(outcome).toMatchObject({ accepted: 0, quarantined: 1, rejected: 0 })
    expect(store.count()).toBe(0)
    expect(store.getQuarantine(`noise-${name}`)?.reason).toMatch(/non[- ]model/i)
    store.close()
  })

  it('retains a supported tool child with the adapter-derived operation', () => {
    const store = new CanonStore(':memory:')
    ingestBatch(
      [
        span({ ...geminiAttributes, 'codeburn.provider': 'github-copilot' }, { spanId: 'model-1', traceId: 'tool-trace' }),
        span(
          { 'codeburn.provider': 'github-copilot', 'gen_ai.tool.name': 'read_file' },
          { spanId: 'tool-1', traceId: 'tool-trace', parentSpanId: 'model-1', name: 'read_file' },
        ),
      ],
      store,
    )

    expect(store.get('model-1')?.op).toBe('llm.invoke')
    expect(store.get('tool-1')?.op).toBe('tool.invoke')
    expect(store.getQuarantine('tool-1')).toBeUndefined()
    store.close()
  })

  it('quarantines a span no adapter claims instead of inventing a harness', () => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch([span({ 'totally.unknown': 'x' }, { spanId: 'orphan-1' })], store)

    expect(outcome.accepted).toBe(0)
    expect(outcome.quarantined).toBe(1)
    expect(store.get('orphan-1')).toBeUndefined()
    store.close()
  })
})

describe('ingest copilot — attribution and quarantine forwarding (Task T2)', () => {
  it('correctly attributes copilot_chat/gen_ai spans and does not quarantine as excluded_harness', () => {
    const store = new CanonStore(':memory:')
    const copilotChatSpan = span(
      {
        'copilot_chat.chat_session_id': '08551cf5-b064-4095-9552-8a9a0a0f78d2',
        'copilot_chat.turn.id': 'turn-1',
        'gen_ai.system': 'gemini',
        'gen_ai.usage.input_tokens': 1200,
        'gen_ai.usage.output_tokens': 150,
      },
      {
        spanId: 'copilot-chat-span-1',
        name: 'copilot_chat/gen_ai',
        timestamp: '2026-09-30T10:00:00.000Z',
        resource: { 'service.name': 'copilot-service' },
      },
    )

    const outcome = ingestBatch([copilotChatSpan], store)
    expect(outcome.accepted).toBe(1)
    expect(outcome.quarantined).toBe(0)

    const record = store.get('copilot-chat-span-1')
    expect(record).toBeDefined()
    expect(record?.harness).toBe('copilot')
    expect(store.getQuarantine('copilot-chat-span-1')).toBeUndefined()
    store.close()
  })

  it('forwards source, name, and timestamp metadata to quarantine store when spans are quarantined as unclaimed', () => {
    const store = new CanonStore(':memory:')
    const orphanSpan = span(
      { 'unknown.vendor': 'val' },
      {
        spanId: 'quarantine-orphan-1',
        name: 'unattributed_operation',
        timestamp: '2026-09-30T11:00:00.000Z',
        resource: { 'service.name': 'orphan-service' },
      },
    )

    const outcome = ingestBatch([orphanSpan], store)
    expect(outcome.quarantined).toBe(1)

    const entry = store.getQuarantine('quarantine-orphan-1')
    expect(entry).toBeDefined()
    expect(entry?.reason).toBe('unclaimed')
    expect(entry?.source).toBe('orphan-service')
    expect(entry?.name).toBe('unattributed_operation')
    expect(entry?.timestamp).toBe('2026-09-30T11:00:00.000Z')
    store.close()
  })

  it('forwards source, name, and timestamp metadata when non-model spans are quarantined', () => {
    const store = new CanonStore(':memory:')
    const nonModelSpan = span(
      { 'copilot_chat.turn.id': 'turn-2' },
      {
        spanId: 'quarantine-nonmodel-1',
        name: 'copilot_chat/healthcheck',
        timestamp: '2026-09-30T11:30:00.000Z',
        resource: { 'service.name': 'copilot-service' },
      },
    )

    const outcome = ingestBatch([nonModelSpan], store)
    expect(outcome.quarantined).toBe(1)

    const entry = store.getQuarantine('quarantine-nonmodel-1')
    expect(entry).toBeDefined()
    expect(entry?.reason).toBe('non-model span')
    expect(entry?.source).toBe('copilot-service')
    expect(entry?.name).toBe('copilot_chat/healthcheck')
    expect(entry?.timestamp).toBe('2026-09-30T11:30:00.000Z')
    store.close()
  })
})

// ---------------------------------------------------------------------------
// Claude Code turn content (T5)
// ---------------------------------------------------------------------------
//
// The interaction span is the turn's only prompt carrier: Claude Code's
// `llm_request` spans hold counters and metadata, never message text (F4 R1).
// Quarantining the interaction span as a non-model span is correct; letting its
// `user_prompt` be deleted with the row is the defect, and it is why every live
// turn renders `parts: []`.

/**
 * The quarantine evidence T14 adds: the harness a row is scoped to, and the
 * decoded span JSON kept beside the audit row. `QuarantineEntry` grows both
 * fields there; widening it locally keeps this RED file type-clean until then,
 * and the assertions below are the contract either way.
 */
type QuarantineEvidence = QuarantineEntry & {
  harness?: string | null
  rawPayload?: string | null
}

/** One interaction span's quarantine row, read as the contract it must grow into. */
function quarantineEvidence(store: CanonStore, spanId: string): QuarantineEvidence | undefined {
  return store.getQuarantine(spanId) as QuarantineEvidence | undefined
}

/** The `conversation_history` text a stored record carries, in part order. */
function conversationHistory(store: CanonStore, spanId: string): string[] {
  return (store.get(spanId)?.parts ?? [])
    .filter((part) => part.part === 'conversation_history')
    .map((part) => part.text)
}

/** The reason a record gives for a bucket it declares unmeasurable, when it declares one. */
function notMeasurableReason(store: CanonStore, spanId: string, bucket: string): string | undefined {
  const availability = store.get(spanId)?.measurability?.[bucket]
  return typeof availability === 'object' ? availability.reason : undefined
}

/** The prompt text these fixtures use; synthetic, never a real user prompt. */
const PROMPT = 'Fix the bug'

/** The redaction the sender substitutes when `OTEL_LOG_USER_PROMPTS` is unset (F1). */
const REDACTED_PROMPT = '<REDACTED>'

const INTERACTION_TRACE = 'interaction-trace'
const INTERACTION_SPAN_ID = 'interaction-1'

/**
 * The two ways B2 recognises an interaction span. The live packet carries both,
 * and either one alone must survive a single rename, so all three shapes are
 * exercised — a fixture that only ever sets both would hide the other two.
 */
type InteractionShape = {
  label: string
  name: string
  attributes: Record<string, unknown>
}

const INTERACTION_SHAPES: readonly InteractionShape[] = [
  {
    label: 'the name and the span.type attribute',
    name: 'claude_code.interaction',
    attributes: { 'span.type': 'interaction' },
  },
  {
    label: 'the span.type attribute alone',
    name: 'interaction',
    attributes: { 'span.type': 'interaction' },
  },
  {
    label: 'the name alone',
    name: 'claude_code.interaction',
    attributes: {},
  },
]

/**
 * Claude Code's interaction span. It carries no counters, so `canonicalOp`
 * leaves it unspecified and ingest quarantines it as a non-model span; the
 * `user_prompt` is the only text it holds.
 */
function interactionSpan(
  userPrompt: string,
  shape: InteractionShape = INTERACTION_SHAPES[0]!,
): OtlpSpan {
  return span(
    { 'claude.deployment_mode': '1p', user_prompt: userPrompt, ...shape.attributes },
    { spanId: INTERACTION_SPAN_ID, traceId: INTERACTION_TRACE, name: shape.name },
  )
}

/** One Claude Code model call in the interaction's turn: counters, and no content of its own. */
function llmChild(spanId: string): OtlpSpan {
  return span(claudeAttributes, {
    spanId,
    traceId: INTERACTION_TRACE,
    parentSpanId: INTERACTION_SPAN_ID,
    name: 'claude_code.llm_request',
  })
}

/**
 * The `api_request_body` log one llm child emits (B3): correlated by exact
 * trace and span id and recognised by both signals — `event.name` and the
 * log body. `attributes.body` is the request JSON `parseClaudeApiRequestBody`
 * turns into parts.
 */
function apiRequestBodyLog(childSpanId: string, request: unknown): OtlpLog {
  return {
    logId: `api-body:${childSpanId}`,
    traceId: INTERACTION_TRACE,
    spanId: childSpanId,
    sessionId: null,
    timestamp: '2026-09-03T10:00:00.000Z',
    body: 'claude_code.api_request_body',
    attributes: { 'event.name': 'api_request_body', body: JSON.stringify(request) },
    resource: {},
    scope: { name: 'com.anthropic.claude_code.events' },
  }
}

/**
 * A Claude Code tool span. It carries no usage counters either, so it stays
 * quarantined as a non-model span exactly as it does today (F4 R1 counts 634
 * such spans in the live session); the prompt must not reach it.
 */
function toolChild(spanId: string): OtlpSpan {
  return span(
    { 'claude.deployment_mode': '1p', 'claude_code.tool_name': 'Read' },
    { spanId, traceId: INTERACTION_TRACE, parentSpanId: INTERACTION_SPAN_ID, name: 'claude_code.tool' },
  )
}

type InteractionTurn = {
  /** `conversation_history` text per llm child, in the order the children were given. */
  children: Array<{ spanId: string; history: string[] }>
  /** What the interaction span left behind in quarantine. */
  interaction: QuarantineEvidence | undefined
  /** What the turn's tool span left behind in quarantine. */
  tool: QuarantineEvidence | undefined
}

/**
 * Ingest one interaction turn in a chosen batch order, reporting everything the
 * contract speaks about. The exporter decides the order, so both must end the
 * same way: children first exercises the back-fill onto stored records, and
 * interaction first exercises B8's wait in `pending_logs`.
 */
function ingestInteractionTurn(
  store: CanonStore,
  order: 'children-first' | 'interaction-first',
): InteractionTurn {
  const children = [llmChild('llm-1'), llmChild('llm-2')]
  const tool = toolChild('tool-1')
  const interaction = interactionSpan(PROMPT)
  if (order === 'children-first') {
    ingestBatch([...children, tool], store)
    ingestBatch([interaction], store)
  } else {
    ingestBatch([interaction], store)
    ingestBatch([...children, tool], store)
  }
  return {
    children: children.map((child) => ({
      spanId: child.spanId,
      history: conversationHistory(store, child.spanId),
    })),
    interaction: quarantineEvidence(store, INTERACTION_SPAN_ID),
    tool: quarantineEvidence(store, 'tool-1'),
  }
}

describe('ingestBatch — Claude Code interaction prompt (Task T5)', () => {
  it.each(['children-first', 'interaction-first'] as const)(
    'gives every llm.invoke child of the interaction the prompt as conversation_history (%s)',
    (order) => {
      const store = new CanonStore(':memory:')

      const turn = ingestInteractionTurn(store, order)

      expect(turn.children).toEqual([
        { spanId: 'llm-1', history: [PROMPT] },
        { spanId: 'llm-2', history: [PROMPT] },
      ])
      // The collapsed bucket carries the same text, so consumers predating
      // structured parts see the prompt too.
      expect(store.get('llm-1')?.content.conversation_history).toBe(PROMPT)
      store.close()
    },
  )

  it('gives the same turn content whichever batch order arrives', () => {
    const childrenFirst = new CanonStore(':memory:')
    const interactionFirst = new CanonStore(':memory:')

    const first = ingestInteractionTurn(childrenFirst, 'children-first')
    const second = ingestInteractionTurn(interactionFirst, 'interaction-first')

    expect(first).toEqual(second)
    // Equality alone would hold while both orders drop the prompt; the prompt
    // itself is what the two orders must agree on.
    expect(second.children.map((child) => child.history)).toEqual([[PROMPT], [PROMPT]])
    childrenFirst.close()
    interactionFirst.close()
  })

  it('retires a parked prompt at the pending TTL when no children arrive, keeping one copy (B8)', () => {
    const store = new CanonStore(':memory:')
    ingestBatch([interactionSpan(PROMPT)], store)

    // The collector's reconcile timer fires past the TTL before any child
    // arrives: the parked row is deleted rather than left to ride
    // pending_logs until the next span batch or wipe, and it is not moved
    // into quarantined_logs either — the interaction span's quarantine
    // packet is already the prompt's evidence copy (T6), so a second one
    // would only duplicate prompt text.
    reconcileExpiredPending(store, Date.now() + 5_001, 5_000)
    expect(store.getPendingLogs()).toEqual([])
    expect(store.quarantinedLogCount()).toBe(0)

    // The wait is TTL-bounded like any other pending row: children that
    // arrive after it closed get nothing.
    ingestBatch([llmChild('llm-1'), llmChild('llm-2')], store)
    expect(conversationHistory(store, 'llm-1')).toEqual([])
    expect(conversationHistory(store, 'llm-2')).toEqual([])
    store.close()
  })

  it('serves a same-batch llm child stored after the interaction span was parked (Q4)', () => {
    // llm-1 is already stored when a batch carries the interaction and
    // llm-2 together. The interaction is quarantined before `upsertMany`
    // writes llm-2, so an immediate-delivery check sees llm-1 alone — a
    // consumed-on-first-delivery prompt would never reach llm-2.
    const store = new CanonStore(':memory:')
    ingestBatch([llmChild('llm-1')], store)
    ingestBatch([interactionSpan(PROMPT), llmChild('llm-2')], store)

    expect(conversationHistory(store, 'llm-1')).toEqual([PROMPT])
    expect(conversationHistory(store, 'llm-2')).toEqual([PROMPT])
    store.close()
  })

  it('keeps serving llm children that arrive in later batches while the parked wait lasts (Q4)', () => {
    // A turn's model calls do not arrive in one export: batch-2's child must
    // not consume the parked prompt out from under batch-3's.
    const store = new CanonStore(':memory:')
    ingestBatch([interactionSpan(PROMPT)], store)
    ingestBatch([llmChild('llm-1')], store)
    ingestBatch([llmChild('llm-2')], store)

    expect(conversationHistory(store, 'llm-1')).toEqual([PROMPT])
    expect(conversationHistory(store, 'llm-2')).toEqual([PROMPT])
    store.close()
  })

  it('does not let the interaction prompt replace parts an api_request_body parse delivered (Q4)', () => {
    // Order: child, then the body log, then the interaction span. The body
    // parse is the richer source and subsumes the prompt-derived part —
    // delivering the prompt after it would replace system_prompt +
    // conversation_history with the prompt alone.
    const store = new CanonStore(':memory:')
    ingestBatch([llmChild('llm-1')], store)
    ingestLogBatch(
      [
        apiRequestBodyLog('llm-1', {
          system: [{ type: 'text', text: 'Body-parse system prompt.' }],
          messages: [{ role: 'user', content: 'Body-parse turn text.' }],
        }),
      ],
      store,
    )
    const bodyParts = [
      { part: 'system_prompt', text: 'Body-parse system prompt.' },
      { part: 'conversation_history', text: 'Body-parse turn text.' },
    ]
    expect(store.get('llm-1')?.parts).toEqual(bodyParts)

    ingestBatch([interactionSpan(PROMPT)], store)

    expect(store.get('llm-1')?.parts).toEqual(bodyParts)
    expect(store.get('llm-1')?.content).toMatchObject({
      system_prompt: 'Body-parse system prompt.',
      conversation_history: 'Body-parse turn text.',
    })
    store.close()
  })

  it('does not stamp the redacted-prompt reason over parts a body parse delivered (Q2/Q4)', () => {
    // Same subsumption rule for a redacted prompt: the record holds real
    // conversation_history, so declaring it not_measurable would be a lie —
    // the reason describes content that is present, not withheld.
    const store = new CanonStore(':memory:')
    ingestBatch([llmChild('llm-1')], store)
    ingestLogBatch(
      [
        apiRequestBodyLog('llm-1', {
          system: 'Body-parse system prompt.',
          messages: [{ role: 'user', content: 'Body-parse turn text.' }],
        }),
      ],
      store,
    )

    ingestBatch([interactionSpan(REDACTED_PROMPT)], store)

    expect(store.get('llm-1')?.parts).toEqual([
      { part: 'system_prompt', text: 'Body-parse system prompt.' },
      { part: 'conversation_history', text: 'Body-parse turn text.' },
    ])
    expect(notMeasurableReason(store, 'llm-1', 'conversation_history')).toBeUndefined()
    store.close()
  })

  it.each(INTERACTION_SHAPES)('recognises the interaction span from $label', (shape) => {
    const store = new CanonStore(':memory:')
    ingestBatch([llmChild('llm-1')], store)
    ingestBatch([interactionSpan(PROMPT, shape)], store)

    expect(conversationHistory(store, 'llm-1')).toEqual([PROMPT])
    store.close()
  })

  it('keeps the interaction span quarantined as a non-model span, with its packet as evidence', () => {
    const store = new CanonStore(':memory:')
    const interaction = interactionSpan(PROMPT)

    const outcome = ingestBatch([interaction], store)

    expect(outcome).toMatchObject({ accepted: 0, quarantined: 1, rejected: 0 })
    expect(store.get(INTERACTION_SPAN_ID)).toBeUndefined()
    // B7 made getQuarantineEntry the sole packet surface; getQuarantine is
    // the light audit read and no longer exposes harness or rawPayload.
    const evidence = store.getQuarantineEntry(INTERACTION_SPAN_ID)
    expect(evidence?.reason).toBe('non-model span')
    expect(evidence?.harness).toBe('claude-code')
    expect(JSON.parse(evidence?.rawPayload ?? 'null')).toEqual(interaction)
    store.close()
  })

  it("quarantines the turn's tool span as before, without attaching the prompt to it", () => {
    const store = new CanonStore(':memory:')
    ingestBatch([toolChild('tool-1')], store)
    ingestBatch([interactionSpan(PROMPT)], store)

    expect(store.get('tool-1')).toBeUndefined()
    const evidence = store.getQuarantineEntry('tool-1')
    expect(evidence?.reason).toBe('non-model span')
    expect(evidence?.harness).toBe('claude-code')
    const packet = JSON.parse(evidence?.rawPayload ?? 'null') as OtlpSpan | null
    expect(packet?.attributes.user_prompt).toBeUndefined()
    store.close()
  })

  it('renders a redacted prompt as no part, naming the env var that would supply it', () => {
    const store = new CanonStore(':memory:')
    ingestBatch([llmChild('llm-1'), llmChild('llm-2')], store)
    ingestBatch([interactionSpan(REDACTED_PROMPT)], store)

    expect(conversationHistory(store, 'llm-1')).toEqual([])
    expect(conversationHistory(store, 'llm-2')).toEqual([])
    expect(notMeasurableReason(store, 'llm-1', 'conversation_history')).toMatch(/OTEL_LOG_USER_PROMPTS=1/)
    store.close()
  })
})
