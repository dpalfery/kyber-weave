// Ingest evidence: newly quarantined spans keep their exact incoming payload and
// validation problems keep their exact message (plus the stack when thrown). Uses only
// repository-observed shapes — the Copilot fingerprint below is the one pinned in
// `ingest.test.ts` — and proves `service.name` stays grouping-only (R6.2): a Copilot
// service name without Copilot attributes quarantines under that source without
// attributing, while the OpenCode background spans quarantine as non-model.

import { describe, expect, it } from 'vitest'

import type { HarnessAdapter, RawSpan } from './adapters/base.js'
import { resolveRootByParentage, traceGroup } from './adapters/base.js'
import { copilotAdapter } from './adapters/copilot.js'
import { recordValidationProblems } from './adapters/quarantine.js'
import { ingestBatch, ingestBatchWithAdapters, toRawSpan } from './ingest.js'
import { CanonStore } from './store.js'
import type { CanonicalRecord } from './types.js'
import type { OtlpSpan } from '../otel/receiver.js'

function span(attributes: Record<string, unknown>, over: Partial<OtlpSpan> = {}): OtlpSpan {
  return {
    spanId: 'span-1',
    traceId: 'trace-1',
    parentSpanId: null,
    name: 'llm_request',
    kind: 'client',
    startTimeUnixNano: '1756478400123456789',
    endTimeUnixNano: '1756478400223456789',
    timestamp: '2026-10-10T20:05:54.000Z',
    durationMs: 100,
    status: { code: 'ok' },
    resource: { 'service.name': 'some-service' },
    attributes,
    scope: {},
    ...over,
  } as OtlpSpan
}

/** The repository-observed Copilot fingerprint (ingest.test.ts: copilot_chat/gen_ai). */
const COPILOT_ATTRIBUTES = {
  'copilot_chat.chat_session_id': '08551cf5-b064-4095-9552-8a9a0a0f78d2',
  'copilot_chat.turn.id': 'turn-1',
  'gen_ai.system': 'gemini',
  'gen_ai.usage.input_tokens': 1200,
  'gen_ai.usage.output_tokens': 150,
}

describe('ingest quarantine evidence (v18 raw_payload)', () => {
  it('stores the exact incoming span for an unclaimed span', () => {
    const store = new CanonStore(':memory:')
    try {
      const input = span(
        { 'totally.unknown': 'x' },
        { spanId: 'orphan-9', resource: { 'service.name': 'mystery-exporter' } },
      )
      const outcome = ingestBatch([input], store)

      expect(outcome).toMatchObject({ accepted: 0, quarantined: 1 })
      const entry = store.getQuarantine('orphan-9')
      expect(entry?.reason).toBe('unclaimed')
      expect(entry?.source).toBe('mystery-exporter')
      // Exact evidence: the decoded span, attributes verbatim.
      expect(entry?.rawPayload).toBe(JSON.stringify(input))
      expect(JSON.parse(entry!.rawPayload!) as OtlpSpan).toMatchObject({
        spanId: 'orphan-9',
        attributes: { 'totally.unknown': 'x' },
      })
    } finally {
      store.close()
    }
  })

  it('stores the exact incoming span for a non-model span', () => {
    const store = new CanonStore(':memory:')
    try {
      const input = span(
        { 'copilot_chat.turn.id': 'turn-2' },
        {
          spanId: 'nonmodel-9',
          name: 'copilot_chat/healthcheck',
          resource: { 'service.name': 'copilot-service' },
        },
      )
      const outcome = ingestBatch([input], store)

      expect(outcome.quarantined).toBe(1)
      const entry = store.getQuarantine('nonmodel-9')
      expect(entry?.reason).toBe('non-model span')
      expect(entry?.rawPayload).toBe(JSON.stringify(input))
    } finally {
      store.close()
    }
  })

  it('keeps attributing the repository-observed Copilot shape instead of quarantining it', () => {
    const store = new CanonStore(':memory:')
    try {
      const outcome = ingestBatch(
        [
          span(COPILOT_ATTRIBUTES, {
            spanId: 'copilot-9',
            name: 'copilot_chat/gen_ai',
            resource: { 'service.name': 'copilot-service' },
          }),
        ],
        store,
      )

      expect(outcome).toMatchObject({ accepted: 1, quarantined: 0 })
      expect(store.get('copilot-9')?.harness).toBe('copilot')
      expect(store.getQuarantine('copilot-9')).toBeUndefined()
    } finally {
      store.close()
    }
  })

  it('never attributes from service.name alone: a Copilot name without Copilot attributes quarantines', () => {
    // The follow-up's core rule: `toRawSpan` derives `source` from service.name for
    // grouping, and attribution votes on attributes only. A span named like Copilot's
    // exporter but carrying no Copilot fingerprint must quarantine under that source —
    // grouped for a human to see, never attributed as Copilot.
    const store = new CanonStore(':memory:')
    try {
      const input = span(
        { 'http.request.method': 'GET' },
        {
          spanId: 'name-only-9',
          traceId: 'trace-name-only',
          name: 'GET /healthz',
          kind: 'server',
          resource: { 'service.name': 'copilot' },
        },
      )
      const outcome = ingestBatch([input], store)

      expect(outcome.accepted).toBe(0)
      expect(outcome.quarantined).toBe(1)
      expect(store.get('name-only-9')).toBeUndefined()
      const entry = store.getQuarantine('name-only-9')
      expect(entry?.source).toBe('copilot')
      expect(JSON.parse(entry!.rawPayload!) as OtlpSpan).toMatchObject({
        resource: { 'service.name': 'copilot' },
      })
      // And the raw span still derives the grouping source without attributing.
      expect(toRawSpan(input).source).toBe('copilot')
    } finally {
      store.close()
    }
  })
})

describe('ingest problem evidence (exact message, stack only when thrown)', () => {
  const throwingAdapter: HarnessAdapter = {
    name: 'thrower',
    namespaces: ['test'],
    detect: (raw: RawSpan) => ('test.throw' in raw.attributes ? 1 : 0),
    relevance: () => 0,
    normalize: () => {
      throw new Error('cannot read counters of undefined')
    },
    group: traceGroup,
    resolveRoot: resolveRootByParentage,
    validate: () => undefined,
    unexportedMetrics: () => [],
  }

  it('records a normalize failure with its message and stack and keeps the batch moving', () => {
    const store = new CanonStore(':memory:')
    try {
      const good = span(COPILOT_ATTRIBUTES, {
        spanId: 'good-1',
        traceId: 'trace-good',
        name: 'copilot_chat/gen_ai',
        resource: { 'service.name': 'copilot-service' },
      })
      const bad = span(
        { 'test.throw': 'yes' },
        { spanId: 'bad-1', traceId: 'trace-bad', resource: { 'service.name': 'thrower-src' } },
      )
      const outcome = ingestBatchWithAdapters([good, bad], store, [copilotAdapter, throwingAdapter])

      expect(outcome.accepted).toBe(1)
      expect(outcome.rejected).toBe(1)
      expect(store.get('good-1')?.harness).toBe('copilot')
      const problems = store.getProblems('bad-1')
      expect(problems).toHaveLength(1)
      expect(problems[0]).toMatchObject({
        code: 'ADAPTER_NORMALIZE_FAILED',
        message: 'cannot read counters of undefined',
        harness: 'thrower',
      })
      expect(typeof problems[0]?.stack).toBe('string')
      expect(problems[0]?.stack).toMatch(/cannot read counters of undefined/)
    } finally {
      store.close()
    }
  })

  it('persists a computed validation problem with its exact message and no stack', () => {
    const store = new CanonStore(':memory:')
    try {
      // A record whose reported total contradicts its classes: validation computes the
      // problem rather than throwing it, so there is a message and no stack to keep.
      const record = {
        ...copilotAdapter.normalize({
          spanId: 'invalid-1',
          traceId: 'trace-invalid',
          parentSpanId: null,
          source: 'copilot-service',
          attributes: COPILOT_ATTRIBUTES,
          name: 'copilot_chat/gen_ai',
          kind: 'client',
        }),
        tokens: {
          freshInput: 5,
          cacheRead: 0,
          cacheCreation: 0,
          output: 1,
          reportedInput: 999,
          reportedOutput: 1,
        },
      } satisfies CanonicalRecord
      const accepted = recordValidationProblems([record], store, (r) => copilotAdapter.validate(r))

      expect(accepted).toHaveLength(0)
      const problems = store.getProblems('invalid-1')
      expect(problems).toHaveLength(1)
      expect(typeof problems[0]?.message).toBe('string')
      expect(problems[0]?.message.length).toBeGreaterThan(0)
      expect(problems[0]?.stack).toBeUndefined()
    } finally {
      store.close()
    }
  })
})
