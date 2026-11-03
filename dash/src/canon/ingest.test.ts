import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { ADAPTERS, ingestBatch, ingestBatchWithAdapters } from './ingest.js'
import { FACTORY_CONTENT_ATTRIBUTES, stripFactoryContent } from './droid-content.js'
import { ingestLogBatch, reconcileExpiredPending, type OtlpLog } from './log-ingest.js'
import { buildSessions } from './sessions.js'
import { CanonStore, type QuarantineEntry } from './store.js'
import { otlpSpanToRecord } from '../otel/service.js'
import type { OtlpSpan, OtlpStatusCode } from '../otel/receiver.js'
import { resolveRootByParentage, traceGroup, type HarnessAdapter } from './adapters/base.js'
import { baseRecord } from './adapters/copilot.js'
import type { CanonicalRecord } from './types.js'
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
      { part: 'conversation_history', text: 'Body-parse turn text.', role: 'user' },
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
      { part: 'conversation_history', text: 'Body-parse turn text.', role: 'user' },
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

  it('delivers the prompt to a child whose body parse filled only other buckets', () => {
    // Per-bucket, not all-or-nothing: a body parse that supplied system_prompt
    // but no messages (a body cut before its messages array) leaves
    // conversation_history empty, and the prompt is the only source for it.
    // The delivery must merge with the parts the parse delivered, never
    // replace them.
    const store = new CanonStore(':memory:')
    ingestBatch([llmChild('llm-1')], store)
    ingestLogBatch(
      [apiRequestBodyLog('llm-1', { system: [{ type: 'text', text: 'Body-parse system prompt.' }] })],
      store,
    )
    expect(store.get('llm-1')?.parts).toEqual([
      { part: 'system_prompt', text: 'Body-parse system prompt.' },
    ])

    ingestBatch([interactionSpan(PROMPT)], store)

    expect(store.get('llm-1')?.parts).toEqual([
      { part: 'system_prompt', text: 'Body-parse system prompt.' },
      { part: 'conversation_history', text: PROMPT },
    ])
    expect(store.get('llm-1')?.content).toMatchObject({
      system_prompt: 'Body-parse system prompt.',
      conversation_history: PROMPT,
    })
    store.close()
  })

  it('declares the redacted prompt on a child that has parts but no conversation_history', () => {
    // The per-bucket redaction guard: a record holding only system_prompt has
    // no prompt text for the redaction to lie about, so the honest
    // not_measurable naming the opt-in lands on exactly that empty bucket.
    const store = new CanonStore(':memory:')
    ingestBatch([llmChild('llm-1')], store)
    ingestLogBatch(
      [apiRequestBodyLog('llm-1', { system: 'Body-parse system prompt.' })],
      store,
    )

    ingestBatch([interactionSpan(REDACTED_PROMPT)], store)

    expect(store.get('llm-1')?.parts).toEqual([
      { part: 'system_prompt', text: 'Body-parse system prompt.' },
    ])
    expect(notMeasurableReason(store, 'llm-1', 'conversation_history')).toMatch(/OTEL_LOG_USER_PROMPTS=1/)
    store.close()
  })

  it('does not let a body parse that fills only other buckets discard the delivered prompt', () => {
    // Symmetry with the delivery-side merge: the body parse replaces the
    // buckets it supplies, and the prompt bucket it does not speak for
    // survives the rewrite instead of being lost until a re-settle.
    const store = new CanonStore(':memory:')
    ingestBatch([llmChild('llm-1')], store)
    ingestBatch([interactionSpan(PROMPT)], store)
    expect(conversationHistory(store, 'llm-1')).toEqual([PROMPT])

    ingestLogBatch(
      [apiRequestBodyLog('llm-1', { system: [{ type: 'text', text: 'Body-parse system prompt.' }] })],
      store,
    )

    expect(store.get('llm-1')?.parts).toEqual([
      { part: 'conversation_history', text: PROMPT },
      { part: 'system_prompt', text: 'Body-parse system prompt.' },
    ])
    expect(store.get('llm-1')?.content).toMatchObject({
      conversation_history: PROMPT,
      system_prompt: 'Body-parse system prompt.',
    })
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

// ---------------------------------------------------------------------------
// The stored error status at the receiver-timing seam
// ---------------------------------------------------------------------------
//
// An adapter can decide that a span FAILED — Factory's exporter marks a
// policy-denied tool call with an `error.type` attribute, and its adapter
// reads that attribute as the record's status: a denied call is an error, not
// an ordinary tool call. The receiver-timing seam then hands the record the
// exporter's own OTLP status code — and an exporter does not ALSO mark the
// transport status as an error. The error is decided correctly at the adapter
// and thrown away one line later, so the STORED record reads as an ordinary
// tool call.
//
// THE RULE: an error status is never downgraded. The seam gives the record the
// timing and the outcome UNLESS the adapter already determined one.
//
// The rule is the SEAM's, and the vehicle is interchangeable: the cases below
// drive it through an adapter that states the outcome itself, because the
// Factory native claim this branch withdraws (plan D50) was the vehicle the
// rule was first exercised through.
//
// Every case below goes through the production ingest path. The defect only
// appears after normalization, so a case that stopped at the adapter would
// prove nothing.

/** The session Factory's native export reports. */
const NATIVE_SESSION = 'droid-native-session-1'

/** The tool the native export names on its tool span. */
const NATIVE_TOOL = 'Read'

/** The telemetry source Factory's own native export arrives under. */
const NATIVE_SOURCE = 'droid-native-export'

/** The trace the native export's root span and its children share. */
const NATIVE_TRACE = 'trace-native-genai'

/** The telemetry source the Kyber-owned Factory plugin's export arrives under. */
const PLUGIN_SOURCE = 'kyberdash-droid-plugin'

/** The OTLP status codes an exporter can carry. */
const EXPORTER_STATUS_CODES = ['ok', 'unset', 'error'] as const satisfies readonly OtlpStatusCode[]

/**
 * Factory's native export, in its `genai` format: the agent identity that
 * names the span, the operation, and the conversation and session ids.
 *
 * The agent identity is on the ROOT alone — the export names it there and
 * nowhere else, which is what makes a real trace's group vote 1/(1+k).
 */
function nativeAttributes(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    'gen_ai.operation.name': 'invoke_agent',
    'gen_ai.agent.name': 'droid',
    'gen_ai.conversation.id': NATIVE_SESSION,
    'session.id': NATIVE_SESSION,
    'gen_ai.request.model': 'claude-sonnet-4-5',
    'gen_ai.provider.name': 'anthropic',
    ...over,
  }
}

/** The native export's child attributes: the operation, the ids and the model. */
function nativeChildAttributes(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    'gen_ai.operation.name': 'chat',
    'gen_ai.conversation.id': NATIVE_SESSION,
    'session.id': NATIVE_SESSION,
    'gen_ai.request.model': 'claude-sonnet-4-5',
    'gen_ai.provider.name': 'anthropic',
    ...over,
  }
}

/** A native export span, carrying the exporter's own OTLP status code. */
function nativeSpan(
  spanId: string,
  attributes: Record<string, unknown>,
  over: Partial<OtlpSpan> = {},
): OtlpSpan {
  return span(attributes, {
    spanId,
    traceId: `trace-${spanId}`,
    name: 'gen_ai.chat',
    resource: { 'service.name': NATIVE_SOURCE },
    status: { code: 'unset' },
    ...over,
  })
}

/** The native export's chat span: the session's own model call. */
function nativeTurnSpan(spanId: string, over: Partial<OtlpSpan> = {}): OtlpSpan {
  return nativeSpan(spanId, nativeChildAttributes(), { name: 'gen_ai.chat', ...over })
}

/** The native export's policy-denied tool call: the span carrying `error.type`. */
function deniedToolSpan(spanId: string, over: Partial<OtlpSpan> = {}): OtlpSpan {
  return nativeSpan(
    spanId,
    nativeChildAttributes({
      'gen_ai.operation.name': 'execute_tool',
      'gen_ai.tool.name': NATIVE_TOOL,
      'gen_ai.tool.call.id': `call_${spanId}`,
      'gen_ai.tool.type': 'function',
      'error.type': 'rejected',
    }),
    { name: 'gen_ai.execute_tool', ...over },
  )
}

/**
 * The native trace's root span: the `invoke_agent` span, and the only span of
 * a real trace that carries the agent identity.
 */
function nativeRootSpan(spanId: string, over: Partial<OtlpSpan> = {}): OtlpSpan {
  return nativeSpan(spanId, nativeAttributes(), {
    name: 'invoke_agent',
    traceId: NATIVE_TRACE,
    ...over,
  })
}

/**
 * An adapter that states the outcome itself.
 *
 * The seam's rule is about what happens to a status an ADAPTER determined, so
 * the case needs an adapter that determines one. The stub claims the spans
 * that carry `outcome.claimed` and judges a denial only when the span says
 * `outcome.denied`; nothing else about it matters.
 */
const outcomeDecidingAdapter: HarnessAdapter = {
  name: 'outcome-deciding',
  namespaces: ['outcome'],
  detect: (span) => (span.attributes['outcome.claimed'] === true ? 1 : 0),
  relevance: () => 1,
  normalize(raw) {
    const record = baseRecord(this, raw)
    if (raw.attributes['outcome.denied'] === true) record.status = 'error'
    return record
  },
  group: traceGroup,
  resolveRoot: resolveRootByParentage,
  validate: () => undefined,
  unexportedMetrics: () => [],
}

/** The session the outcome-deciding adapter's spans report. */
const OUTCOME_SESSION = 'outcome-session-1'

/** The tool the outcome-deciding adapter's span names. */
const OUTCOME_TOOL = 'Read'

/** A span the outcome-deciding adapter claims, with the exporter's status code. */
function outcomeSpan(spanId: string, over: Partial<OtlpSpan> = {}): OtlpSpan {
  return span(
    {
      'outcome.claimed': true,
      'gen_ai.tool.name': OUTCOME_TOOL,
      'gen_ai.conversation.id': OUTCOME_SESSION,
    },
    {
      spanId,
      traceId: `trace-${spanId}`,
      name: 'outcome.tool',
      resource: { 'service.name': 'outcome-exporter' },
      status: { code: 'unset' },
      ...over,
    },
  )
}

/** The same span, with the adapter's own denial evidence on it. */
function deniedOutcomeSpan(spanId: string, over: Partial<OtlpSpan> = {}): OtlpSpan {
  return span(
    {
      'outcome.claimed': true,
      'outcome.denied': true,
      'gen_ai.tool.name': OUTCOME_TOOL,
      'gen_ai.conversation.id': OUTCOME_SESSION,
    },
    {
      spanId,
      traceId: `trace-${spanId}`,
      name: 'outcome.tool',
      resource: { 'service.name': 'outcome-exporter' },
      status: { code: 'unset' },
      ...over,
    },
  )
}

/** The session's model call: the turn the denied tool call belongs to. */
function outcomeTurnSpan(spanId: string, over: Partial<OtlpSpan> = {}): OtlpSpan {
  return span(
    {
      'outcome.claimed': true,
      'gen_ai.usage.input_tokens': 1_000,
      'gen_ai.conversation.id': OUTCOME_SESSION,
    },
    {
      spanId,
      traceId: `trace-${spanId}`,
      name: 'outcome.turn',
      resource: { 'service.name': 'outcome-exporter' },
      status: { code: 'unset' },
      ...over,
    },
  )
}

/**
 * The plugin envelope's turn span for a session: the span the Kyber-owned
 * plugin emits for the session's own model call.
 */
function pluginTurnSpan(spanId: string, sessionId: string = NATIVE_SESSION): OtlpSpan {
  return span(
    {
      'factory.contract_version': '1',
      'factory.session.id': sessionId,
      'factory.model.id': 'gpt-5-codex',
    },
    {
      spanId,
      traceId: `trace-${spanId}`,
      name: 'factory.turn',
      resource: { 'service.name': PLUGIN_SOURCE },
      status: { code: 'unset' },
    },
  )
}

/**
 * A native Factory row already in the corpus, as the withdrawn claim left it:
 * the harness, the session and the export's own agent identity.
 */
function storedNativeRecord(): CanonicalRecord {
  return {
    spanId: 'native-stored',
    traceId: 'trace-native-stored',
    parentSpanId: null,
    source: NATIVE_SOURCE,
    harness: 'droid',
    sessionId: NATIVE_SESSION,
    name: 'gen_ai.chat',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: '2026-10-30T12:00:00.000Z',
    durationMs: 1000,
    status: 'ok',
    tokens: {
      freshInput: 0,
      cacheRead: 0,
      cacheCreation: 0,
      output: 0,
      reportedInput: 0,
      reportedOutput: 0,
    },
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
    raw: { 'gen_ai.agent.name': 'droid', 'gen_ai.conversation.id': NATIVE_SESSION },
  }
}

/**
 * The tool rows a session's projection published, by tool name.
 *
 * `getSessionPayload` hands back the projection's own stored document, so the
 * shape is read through a local type: this reads the one field the session's
 * error surface is (canon/sessions.ts counts a tool record as an error when
 * its stored status is `error`).
 */
async function projectedToolRows(
  store: CanonStore,
  sessionId: string,
): Promise<Map<string, { invocations: number; errors: number | undefined }>> {
  await buildSessions(store)
  const payload = store.getSessionPayload(sessionId) as
    | { tools?: Array<{ name?: unknown; invocations?: unknown; errors?: unknown }> }
    | undefined
  if (payload === undefined) throw new Error(`no session payload was built for ${sessionId}`)
  const rows = new Map<string, { invocations: number; errors: number | undefined }>()
  for (const tool of payload.tools ?? []) {
    if (typeof tool.name !== 'string') continue
    rows.set(tool.name, {
      invocations: typeof tool.invocations === 'number' ? tool.invocations : 0,
      errors: typeof tool.errors === 'number' ? tool.errors : undefined,
    })
  }
  return rows
}

describe('ingestBatch — the stored error status at the receiver-timing seam', () => {
  it('stores a denied call as an error even when the exporter status is unset', () => {
    const store = new CanonStore(':memory:')
    ingestBatchWithAdapters(
      [deniedOutcomeSpan('outcome-denied', { status: { code: 'unset' } })],
      store,
      [outcomeDecidingAdapter],
    )

    const record = store.get('outcome-denied')
    expect(record?.harness).toBe('outcome-deciding')
    expect(
      record?.status,
      'a denied call must be STORED as an error, whatever the exporter status says',
    ).toBe('error')
    store.close()
  })

  it('stores a denied call as an error even when the exporter status is ok', () => {
    const store = new CanonStore(':memory:')
    ingestBatchWithAdapters(
      [deniedOutcomeSpan('outcome-denied', { status: { code: 'ok' } })],
      store,
      [outcomeDecidingAdapter],
    )

    const record = store.get('outcome-denied')
    expect(record?.harness).toBe('outcome-deciding')
    expect(
      record?.status,
      'an exporter status of ok must not downgrade a status the adapter already determined',
    ).toBe('error')
    store.close()
  })

  it('stores a span whose exporter status is error as an error', () => {
    const store = new CanonStore(':memory:')
    ingestBatchWithAdapters(
      [deniedOutcomeSpan('outcome-denied', { status: { code: 'error' } })],
      store,
      [outcomeDecidingAdapter],
    )

    const record = store.get('outcome-denied')
    expect(record?.harness).toBe('outcome-deciding')
    expect(record?.status, 'an exporter error status must reach the stored record').toBe('error')
    store.close()
  })

  it('keeps the exporter status code exactly when no adapter determined an outcome', () => {
    // The regression guard: the rule must not turn every record into an
    // error, and it must not keep the adapter's neutral default either. The
    // exporter's own code is the stored status, whatever it says.
    for (const code of EXPORTER_STATUS_CODES) {
      const store = new CanonStore(':memory:')
      ingestBatchWithAdapters(
        [outcomeSpan(`outcome-${code}`, { status: { code } })],
        store,
        [outcomeDecidingAdapter],
      )

      const record = store.get(`outcome-${code}`)
      expect(record?.harness).toBe('outcome-deciding')
      expect(
        record?.status,
        `a span with no adapter-determined outcome must keep the exporter status code "${code}"`,
      ).toBe(code)
      store.close()
    }
  })

  it('carries the denied call into the session projection as a tool error', async () => {
    const store = new CanonStore(':memory:')
    ingestBatchWithAdapters(
      [
        outcomeTurnSpan('outcome-turn'),
        deniedOutcomeSpan('outcome-denied', { status: { code: 'unset' } }),
      ],
      store,
      [outcomeDecidingAdapter],
    )

    const rows = await projectedToolRows(store, OUTCOME_SESSION)
    expect(rows.get(OUTCOME_TOOL)?.invocations, 'the denied call is a tool invocation').toBe(1)
    expect(
      rows.get(OUTCOME_TOOL)?.errors,
      'the denied call must reach the projection as a tool error, not as an ordinary call',
    ).toBe(1)
    store.close()
  })

  it("leaves another harness's stored status alone beside a denied call", () => {
    // The seam is shared by every harness, so one harness's rule must not move
    // another harness's records: the copilot span keeps the exporter's own
    // status code whether or not a denied call sits in the same batch.
    const alone = new CanonStore(':memory:')
    ingestBatch([span(geminiAttributes, { spanId: 'other-1', status: { code: 'unset' } })], alone)

    const beside = new CanonStore(':memory:')
    ingestBatchWithAdapters(
      [
        span(geminiAttributes, { spanId: 'other-1', status: { code: 'unset' } }),
        deniedOutcomeSpan('outcome-denied', { status: { code: 'unset' } }),
      ],
      beside,
      [...ADAPTERS, outcomeDecidingAdapter],
    )

    expect(alone.get('other-1')?.harness).toBe('copilot')
    expect(alone.get('other-1')?.status).toBe('unset')
    expect(beside.get('other-1')?.status).toBe('unset')
    expect(
      beside.get('other-1')?.status,
      'another harness must not inherit the denial',
    ).not.toBe('error')
    // The denied call IS in the batch, so the guard above is not vacuous: it is
    // the presence of that call that leaves the other harness's record alone.
    expect(
      beside.get('outcome-denied'),
      "the denied call is in the batch beside the other harness's span",
    ).toBeDefined()
    alone.close()
    beside.close()
  })
})

// ---------------------------------------------------------------------------
// The withdrawn native Factory claim (D50)
// ---------------------------------------------------------------------------
//
// Factory's own native OpenTelemetry export is not claimed by this branch: the
// `droid` adapter's native claim is withdrawn, and a native span is
// quarantined as `unclaimed` with its decoded packet kept as the evidence.
//
// THE RULES: a native span is never stored; the ingest never supersedes a
// stored native row when the plugin's row for the same session arrives; and
// the quarantine reason is the ordinary `unclaimed` — no native-specific
// reason string is left in the seam.

describe('ingestBatch — the withdrawn native Factory claim', () => {
  it('quarantines the root of a native trace as unclaimed and stores no record', () => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch([nativeRootSpan('native-root')], store)

    expect(outcome).toMatchObject({ accepted: 0, quarantined: 1, rejected: 0 })
    expect(store.get('native-root'), 'a native span must never become a record').toBeUndefined()
    expect(store.getQuarantine('native-root')?.reason).toBe('unclaimed')
    store.close()
  })

  it('quarantines a native chat span as unclaimed and stores no record', () => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch([nativeTurnSpan('native-turn')], store)

    expect(outcome).toMatchObject({ accepted: 0, quarantined: 1, rejected: 0 })
    expect(store.get('native-turn')).toBeUndefined()
    expect(store.getQuarantine('native-turn')?.reason).toBe('unclaimed')
    store.close()
  })

  it('quarantines the tool child of a native trace as unclaimed and stores no record', () => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch([deniedToolSpan('native-tool')], store)

    expect(outcome).toMatchObject({ accepted: 0, quarantined: 1, rejected: 0 })
    expect(store.get('native-tool')).toBeUndefined()
    expect(store.getQuarantine('native-tool')?.reason).toBe('unclaimed')
    store.close()
  })

  it('quarantines every span of a native trace alike — the root and both children', () => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch(
      [
        nativeRootSpan('native-root'),
        nativeTurnSpan('native-chat', { traceId: NATIVE_TRACE, parentSpanId: 'native-root' }),
        deniedToolSpan('native-tool', { traceId: NATIVE_TRACE, parentSpanId: 'native-root' }),
      ],
      store,
    )

    expect(outcome).toMatchObject({ accepted: 0, quarantined: 3, rejected: 0 })
    expect(store.count(), 'a native trace must leave no record behind').toBe(0)
    for (const spanId of ['native-root', 'native-chat', 'native-tool']) {
      expect(
        store.getQuarantine(spanId)?.reason,
        `${spanId} must be quarantined as unclaimed`,
      ).toBe('unclaimed')
    }
    store.close()
  })

  it('keeps the decoded packet of a quarantined native span as the evidence', () => {
    const store = new CanonStore(':memory:')
    ingestBatch([nativeRootSpan('native-root')], store)

    const packet = store.getQuarantine('native-root')?.rawPayload
    expect(typeof packet, 'the quarantined span must keep its decoded packet').toBe('string')
    expect(packet).toContain('gen_ai.agent.name')
    expect(packet).toContain('droid')
    store.close()
  })

  it('no longer supersedes a stored native row when the plugin row for its session arrives', () => {
    // The precedence rule the withdrawal removes: a native row that reached
    // the corpus before the plugin's row for its session used to leave it for
    // quarantine. Nothing supersedes a stored row now — the ingest stores what
    // it claims and leaves the rest of the corpus alone.
    const store = new CanonStore(':memory:')
    store.upsert(storedNativeRecord())
    ingestBatch([pluginTurnSpan('plugin-turn')], store)

    expect(store.get('native-stored'), 'a stored row must not be deleted by a later batch').toBeDefined()
    expect(
      store.getQuarantine('native-stored'),
      'a stored native row must not be superseded any more',
    ).toBeUndefined()
    // The plugin's own row is stored as before.
    expect(store.get('plugin-turn')?.harness).toBe('droid')
    store.close()
  })
})

// ---------------------------------------------------------------------------
// The Factory content consent gate (T42)
// ---------------------------------------------------------------------------
//
// KyberDash's own consent file (`~/.kyberdash/factory-plugin.json`, with the
// `KYBERDASH_FACTORY_CONTEXT` override) decides whether Factory's content is ever
// stored. Factory reaches KyberDash through two routes: the Kyber-owned plugin, whose
// prompt is gated at emission and whose tool arguments and results are byte counts
// (never content), and Factory's NATIVE OpenTelemetry export, which carries the
// prompt, the reply, the tool arguments and the tool result as span attributes when
// the harness's own content switch is on.
//
// While the consent is off, none of that text reaches the store: not a canonical
// record, and not the quarantine packet that keeps a span nothing claimed. The gate
// runs BEFORE normalization and BEFORE the quarantine write, so no intermediate
// holds the text.
//
// THE RULES:
//   1. The gate covers BOTH of Factory's routes — the native export and the
//      plugin envelope — and it strips exactly the content-bearing attributes.
//      The correlation and usage metadata — the session, the conversation, the
//      model, the counters — is not content and survives.
//   2. Another harness's content is untouched. The gate is scoped to Factory's own
//      spans, so Copilot's and pi's `gen_ai.input.messages` still reach their
//      records.
//   3. The file source's own records are untouched: its `userMessage` fragment is
//      the owner's transcript, read by a different route (D57).
//   4. The plugin route is gated here too. Its prompt attribute is gated at
//      emission (D61), but the emission gate reads the consent file at the moment
//      the span is mapped, and a span the plugin PARKED because the delivery
//      failed is re-delivered later WITHOUT the consent being read again. The
//      store-side gate is therefore the last line that can keep the owner's
//      prompt text out of the corpus: a plugin envelope carrying
//      `factory.turn.prompt.text` must not reach the store while the consent
//      says off — and the envelope's identity attributes survive the gate.
//
// The consent file lives in the owner's home. Every case below points HOME at a
// throwaway directory, so no case ever reads the owner's real file.

/** The prompt Factory's native export carried while its content switch was on. */
const FACTORY_PROMPT_TEXT = 'the owner asked about the private file'

/** The assistant's reply, on the same export. */
const FACTORY_REPLY_TEXT = 'the assistant answered with the private file contents'

/** The tool arguments, on the same export. */
const FACTORY_TOOL_ARGUMENTS = 'the private path /Users/owner/private.txt'

/** The tool result, on the same export. */
const FACTORY_TOOL_RESULT = 'the tool read the private file'

/** The throwaway homes the consent cases wrote their consent file into. */
const consentHomes: string[] = []

/**
 * A throwaway home holding the consent file, or no consent file at all.
 *
 * The file body is what the owner's decision looks like on disk: `{"version":1,
 * "content":true}` is the only body that turns content on.
 */
function consentHome(body?: string): string {
  const home = mkdtempSync(join(tmpdir(), 'kd-factory-consent-'))
  consentHomes.push(home)
  if (body !== undefined) {
    mkdirSync(join(home, '.kyberdash'), { recursive: true })
    writeFileSync(join(home, '.kyberdash', 'factory-plugin.json'), body)
  }
  return home
}

afterEach(() => {
  // The consent override is not one of the vars the environment isolation clears,
  // so a case that sets it would decide every case after it.
  delete process.env['KYBERDASH_FACTORY_CONTEXT']
  for (const home of consentHomes.splice(0)) rmSync(home, { recursive: true, force: true })
})

/**
 * A native Factory span carrying the content the export's content switch turned on.
 *
 * The span is a CHILD: the `genai` format names the agent on the trace root alone,
 * so the child carries the operation, the ids and the model — and its content.
 */
function nativeContentSpan(spanId: string, over: Partial<OtlpSpan> = {}): OtlpSpan {
  return nativeSpan(
    spanId,
    nativeChildAttributes({
      'gen_ai.input.messages': JSON.stringify([{ role: 'user', content: FACTORY_PROMPT_TEXT }]),
      'gen_ai.output.messages': JSON.stringify([{ role: 'assistant', content: FACTORY_REPLY_TEXT }]),
      'gen_ai.tool.call.arguments': FACTORY_TOOL_ARGUMENTS,
      'gen_ai.tool.call.result': FACTORY_TOOL_RESULT,
    }),
    { traceId: `trace-content-${spanId}`, ...over },
  )
}

/**
 * A Factory `legacy`-format content span: one of Factory's own `droid.*` spans,
 * carrying its message text on `message.content`.
 *
 * The legacy format's content spans carry no attribute fingerprint at all, so
 * Factory's own span names are what identify them.
 */
function legacyMessageSpan(spanId: string, over: Partial<OtlpSpan> = {}): OtlpSpan {
  return span(
    { 'message.content': FACTORY_PROMPT_TEXT, 'session.id': NATIVE_SESSION },
    {
      spanId,
      traceId: `trace-legacy-${spanId}`,
      name: 'droid.message.user',
      resource: { 'service.name': NATIVE_SOURCE },
      ...over,
    },
  )
}

/** Another harness's own shape: Copilot's vendor namespace, its counters and its messages. */
const COPILOT_CONTENT_ATTRIBUTES = {
  'copilot_chat.chat_session_id': '08551cf5-b064-4095-9552-8a9a0a0f78d2',
  'copilot_chat.turn.id': 'turn-content-1',
  'gen_ai.usage.input_tokens': 1200,
  'gen_ai.usage.output_tokens': 150,
  'gen_ai.input.messages': JSON.stringify([{ role: 'user', content: "another harness's own prompt" }]),
}

/** The text another harness's own content carries. */
const OTHER_HARNESS_PROMPT_TEXT = "another harness's own prompt"

describe('ingestBatch — the Factory content consent gate (T42)', () => {
  it('stores no Factory prompt, reply, tool argument or tool result while the consent is off', () => {
    const store = new CanonStore(':memory:')
    process.env['HOME'] = consentHome('{"version":1,"content":false}')

    const outcome = ingestBatch([nativeContentSpan('native-content')], store)

    expect(outcome).toMatchObject({ accepted: 0, quarantined: 1 })
    const packet = store.getQuarantine('native-content')?.rawPayload ?? ''
    expect(packet, 'the Factory prompt must not reach the quarantine packet').not.toContain(
      FACTORY_PROMPT_TEXT,
    )
    expect(packet, 'the Factory reply must not reach the quarantine packet').not.toContain(
      FACTORY_REPLY_TEXT,
    )
    expect(packet, 'the Factory tool arguments must not reach the quarantine packet').not.toContain(
      FACTORY_TOOL_ARGUMENTS,
    )
    expect(packet, 'the Factory tool result must not reach the quarantine packet').not.toContain(
      FACTORY_TOOL_RESULT,
    )
    store.close()
  })

  it('stores no legacy Factory message content while the consent is off', () => {
    const store = new CanonStore(':memory:')
    process.env['HOME'] = consentHome('{"version":1,"content":false}')

    const outcome = ingestBatch([legacyMessageSpan('legacy-message')], store)

    expect(outcome).toMatchObject({ accepted: 0, quarantined: 1 })
    const packet = store.getQuarantine('legacy-message')?.rawPayload ?? ''
    expect(packet, "the legacy span's message content must not reach the packet").not.toContain(
      FACTORY_PROMPT_TEXT,
    )
    store.close()
  })

  it('keeps the correlation and usage metadata while the consent is off', () => {
    const store = new CanonStore(':memory:')
    process.env['HOME'] = consentHome('{"version":1,"content":false}')

    ingestBatch(
      [
        nativeSpan(
          'native-metadata',
          nativeChildAttributes({
            'gen_ai.input.messages': JSON.stringify([{ role: 'user', content: FACTORY_PROMPT_TEXT }]),
            'gen_ai.usage.input_tokens': 1200,
            'gen_ai.usage.output_tokens': 150,
          }),
          { traceId: 'trace-metadata-native' },
        ),
      ],
      store,
    )

    // The counters, the ids and the model are measurements, not content: the gate
    // strips the text and leaves everything that describes the call.
    const packet = store.getQuarantine('native-metadata')?.rawPayload ?? ''
    expect(packet).toContain('gen_ai.conversation.id')
    expect(packet).toContain(NATIVE_SESSION)
    expect(packet).toContain('gen_ai.request.model')
    expect(packet).toContain('gen_ai.usage.input_tokens')
    expect(packet).toContain('1200')
    expect(packet, 'the prompt is still the one thing that must go').not.toContain(
      FACTORY_PROMPT_TEXT,
    )
    store.close()
  })

  it('stores the Factory text while the consent is on', () => {
    const store = new CanonStore(':memory:')
    process.env['HOME'] = consentHome('{"version":1,"content":true}')

    ingestBatch([nativeContentSpan('native-content-on')], store)

    const packet = store.getQuarantine('native-content-on')?.rawPayload ?? ''
    expect(packet, 'the consent is on, so the text is stored as it is today').toContain(
      FACTORY_PROMPT_TEXT,
    )
    expect(packet).toContain(FACTORY_REPLY_TEXT)
    expect(packet).toContain(FACTORY_TOOL_ARGUMENTS)
    expect(packet).toContain(FACTORY_TOOL_RESULT)
    store.close()
  })

  it('stores the Factory text while the env override turns the consent on', () => {
    const store = new CanonStore(':memory:')
    process.env['HOME'] = consentHome('{"version":1,"content":false}')
    process.env['KYBERDASH_FACTORY_CONTEXT'] = '1'

    ingestBatch([nativeContentSpan('native-content-env')], store)

    const packet = store.getQuarantine('native-content-env')?.rawPayload ?? ''
    expect(packet, 'the env override wins over the file').toContain(FACTORY_PROMPT_TEXT)
    store.close()
  })

  it('leaves another harness\'s own gen_ai.input.messages untouched', () => {
    const store = new CanonStore(':memory:')
    process.env['HOME'] = consentHome('{"version":1,"content":false}')

    ingestBatch(
      [
        span(COPILOT_CONTENT_ATTRIBUTES, {
          spanId: 'copilot-content',
          name: 'copilot_chat/gen_ai',
          traceId: 'trace-copilot-content',
          resource: { 'service.name': 'copilot-service' },
        }),
      ],
      store,
    )

    const record = store.get('copilot-content')
    expect(record?.harness, 'the other harness\'s span is still its own').toBe('copilot')
    const parts = (record?.parts ?? []).map((part) => part.text).join('\n')
    expect(parts, "another harness's prompt must reach its own record").toContain(
      OTHER_HARNESS_PROMPT_TEXT,
    )
    store.close()
  })

  it('stores no Factory content for a child that arrives before its root', () => {
    const store = new CanonStore(':memory:')
    process.env['HOME'] = consentHome('{"version":1,"content":false}')

    // The child comes first: the `genai` root names the agent and is exported when
    // the invocation ends, so a child can arrive before the span that identifies
    // its trace.
    ingestBatch([nativeContentSpan('native-early-child')], store)
    const earlyPacket = store.getQuarantine('native-early-child')?.rawPayload ?? ''
    expect(earlyPacket, 'the early child\'s prompt must not reach the packet').not.toContain(
      FACTORY_PROMPT_TEXT,
    )

    // The root arrives afterwards and names the trace.
    ingestBatch([nativeRootSpan('native-late-root')], store)
    store.close()
  })

  it('stores no Factory content for a child that arrives after its root', () => {
    const store = new CanonStore(':memory:')
    process.env['HOME'] = consentHome('{"version":1,"content":false}')

    const traceId = 'trace-root-first'
    ingestBatch([nativeRootSpan('native-first-root', { traceId })], store)
    ingestBatch(
      [
        nativeContentSpan('native-later-child', {
          traceId,
          parentSpanId: 'native-first-root',
        }),
      ],
      store,
    )

    const packet = store.getQuarantine('native-later-child')?.rawPayload ?? ''
    expect(packet, 'the later child\'s prompt must not reach the packet').not.toContain(
      FACTORY_PROMPT_TEXT,
    )
    store.close()
  })

  it('stores no Factory content for a child a second harness claimed', () => {
    const store = new CanonStore(':memory:')
    process.env['HOME'] = consentHome('{"version":1,"content":false}')

    // The root is seen first, so the trace is known to be Factory's.
    const traceId = 'trace-claimed-child'
    ingestBatch([nativeRootSpan('native-claimed-root', { traceId })], store)

    // The child then arrives under another harness's source, and the registry's
    // source inheritance hands the group to that harness: the span is claimed,
    // and its content would land in a record without the gate.
    ingestBatch(
      [
        span(COPILOT_CONTENT_ATTRIBUTES, {
          spanId: 'copilot-sibling',
          name: 'copilot_chat/gen_ai',
          traceId: 'trace-copilot-sibling',
          resource: { 'service.name': 'shared-source' },
        }),
        nativeContentSpan('native-claimed-child', {
          traceId,
          parentSpanId: 'native-claimed-root',
          resource: { 'service.name': 'shared-source' },
          attributes: {
            ...nativeChildAttributes({
              'gen_ai.input.messages': JSON.stringify([
                { role: 'user', content: FACTORY_PROMPT_TEXT },
              ]),
              'gen_ai.usage.input_tokens': 1200,
              'gen_ai.usage.output_tokens': 150,
            }),
          },
        }),
      ],
      store,
    )

    const record = store.get('native-claimed-child')
    expect(record, 'the claimed span is stored as before').toBeDefined()
    const raw = (record?.raw ?? {}) as Record<string, unknown>
    expect(
      raw['gen_ai.input.messages'],
      'the claimed Factory span must carry no prompt',
    ).toBeUndefined()
    const parts = (record?.parts ?? []).map((part) => part.text).join('\n')
    expect(parts, 'the claimed Factory span must carry no prompt in its parts').not.toContain(
      FACTORY_PROMPT_TEXT,
    )
    store.close()
  })

  it('does not touch the file source\'s own records', () => {
    const store = new CanonStore(':memory:')
    process.env['HOME'] = consentHome('{"version":1,"content":false}')

    // The file source's records carry the owner's own first-message fragment. That
    // route is outside the gate (D57): the fragment is the owner's transcript, read
    // by KyberDash's file source, not by this route.
    const fileSource: CanonicalRecord = {
      ...storedNativeRecord(),
      spanId: 'file-source-record',
      raw: { userMessage: 'the owner\'s own first message fragment' },
    }
    store.upsert(fileSource)

    ingestBatch([nativeContentSpan('native-content-beside-file')], store)

    const stored = store.get('file-source-record')
    expect(stored, 'the file source\'s record must survive the gate').toBeDefined()
    const storedRaw = (stored?.raw ?? {}) as Record<string, unknown>
    expect(storedRaw['userMessage']).toBe('the owner\'s own first message fragment')
    expect(store.getQuarantine('file-source-record'), 'and it must not be quarantined').toBeUndefined()
    store.close()
  })

  it('stores no plugin prompt text while the consent is off, and keeps the envelope\'s identity', () => {
    const store = new CanonStore(':memory:')
    process.env['HOME'] = consentHome('{"version":1,"content":false}')

    // The plugin's own prompt attribute is gated at emission, by the plugin's own
    // consent read (D61) — but the emission gate is not the last gate, and it is
    // not the only one. A span the plugin parked while the consent was on is
    // re-delivered later without the consent being read again, so the store-side
    // gate is what keeps the owner's prompt text out of the corpus.
    ingestBatch(
      [
        span(
          {
            'factory.contract_version': '1',
            'factory.session.id': NATIVE_SESSION,
            'factory.model.id': 'gpt-5-codex',
            'factory.tool.name': NATIVE_TOOL,
            'factory.turn.prompt.text': FACTORY_PROMPT_TEXT,
          },
          {
            spanId: 'plugin-content',
            name: 'factory.turn',
            traceId: 'trace-plugin-content',
            resource: { 'service.name': PLUGIN_SOURCE },
          },
        ),
      ],
      store,
    )

    const record = store.get('plugin-content')
    expect(record?.harness).toBe('droid')
    const pluginRaw = (record?.raw ?? {}) as Record<string, unknown>

    // The owner's prompt text does not reach the stored record.
    expect(
      pluginRaw['factory.turn.prompt.text'],
      'the plugin envelope must carry no prompt text while the consent is off',
    ).toBeUndefined()
    const parts = (record?.parts ?? []).map((part) => part.text).join('\n')
    expect(parts, 'the plugin envelope must carry no prompt text in its parts').not.toContain(
      FACTORY_PROMPT_TEXT,
    )

    // The envelope's identity survives the gate: the span id, the session id,
    // the model and the tool name describe the call, and they are not the
    // owner's text.
    expect(record?.spanId).toBe('plugin-content')
    expect(record?.sessionId).toBe(NATIVE_SESSION)
    expect(pluginRaw['factory.session.id']).toBe(NATIVE_SESSION)
    expect(pluginRaw['factory.model.id']).toBe('gpt-5-codex')
    expect(pluginRaw['factory.tool.name']).toBe(NATIVE_TOOL)
    expect(pluginRaw['gen_ai.tool.name']).toBe(NATIVE_TOOL)
    store.close()
  })

  it('stores no plugin prompt text for a span the plugin parked while the consent was on and re-delivered after it was turned off', () => {
    const store = new CanonStore(':memory:')

    // The consent is ON when the plugin maps the span, so the plugin's own
    // emission gate lets the prompt text travel. The delivery failed, so the
    // span is parked in the plugin's file-backed spool.
    process.env['HOME'] = consentHome('{"version":1,"content":true}')
    const parked = span(
      {
        'factory.contract_version': '1',
        'factory.session.id': NATIVE_SESSION,
        'factory.model.id': 'gpt-5-codex',
        'factory.turn.prompt.text': FACTORY_PROMPT_TEXT,
      },
      {
        spanId: 'plugin-parked',
        name: 'factory.turn',
        traceId: 'trace-plugin-parked',
        resource: { 'service.name': PLUGIN_SOURCE },
      },
    )
    ingestBatch([parked], store)
    const parkedRaw = (store.get('plugin-parked')?.raw ?? {}) as Record<string, unknown>
    expect(
      parkedRaw['factory.turn.prompt.text'],
      'the parked span carried the prompt text while the consent was on',
    ).toBe(FACTORY_PROMPT_TEXT)

    // The owner turns the consent off. The spool re-delivers the parked span
    // as it was mapped — the drain does not read the consent file again — so
    // the span arrives carrying the owner's prompt text while the consent
    // says off.
    process.env['HOME'] = consentHome('{"version":1,"content":false}')
    ingestBatch([parked], store)

    const record = store.get('plugin-parked')
    const raw = (record?.raw ?? {}) as Record<string, unknown>
    expect(
      raw['factory.turn.prompt.text'],
      'the re-delivered parked span must carry no prompt text while the consent is off',
    ).toBeUndefined()
    const parts = (record?.parts ?? []).map((part) => part.text).join('\n')
    expect(parts, 'the re-delivered parked span must carry no prompt text in its parts').not.toContain(
      FACTORY_PROMPT_TEXT,
    )
    store.close()
  })

  it('lists the plugin envelope\'s own prompt attribute among the attributes the gate strips', () => {
    // The gate can only strip an attribute that is ON ITS LIST. The plugin's own
    // prompt attribute was not on it, so nothing the gate did could take the
    // owner's prompt text off a span that carried it.
    const contentAttributes: readonly string[] = FACTORY_CONTENT_ATTRIBUTES
    expect(
      contentAttributes,
      'the plugin envelope\'s prompt attribute must be on the gate\'s content list',
    ).toContain('factory.turn.prompt.text')

    // And the list is what the strip acts on: the text goes, the identity stays.
    const stripped = stripFactoryContent({
      'factory.turn.prompt.text': FACTORY_PROMPT_TEXT,
      'factory.session.id': NATIVE_SESSION,
    })
    expect(stripped['factory.turn.prompt.text']).toBeUndefined()
    expect(stripped['factory.session.id']).toBe(NATIVE_SESSION)
  })

  it('stores no plugin prompt text carried by a native Factory span either', () => {
    const store = new CanonStore(':memory:')
    process.env['HOME'] = consentHome('{"version":1,"content":false}')

    // A span Factory's OWN export sent, carrying the plugin's prompt attribute.
    // The gate owns the span (the agent identity names it Factory's), so the
    // gate is what takes the text off it — and it can only take off what its
    // list names. The attribute sits in the `factory.` namespace, so the
    // envelope marker claims the span and the contract-version guard holds it
    // back: the quarantine packet is where a span nothing stored keeps its
    // text, and the text must not be there.
    ingestBatch(
      [
        nativeRootSpan('native-with-plugin-prompt', {
          traceId: 'trace-native-with-plugin-prompt',
          attributes: {
            ...nativeAttributes(),
            'factory.turn.prompt.text': FACTORY_PROMPT_TEXT,
          },
        }),
      ],
      store,
    )

    const packet = store.getQuarantine('native-with-plugin-prompt')?.rawPayload ?? ''
    expect(packet, 'the native span must not carry the plugin prompt text').not.toContain(
      FACTORY_PROMPT_TEXT,
    )
    store.close()
  })
})
