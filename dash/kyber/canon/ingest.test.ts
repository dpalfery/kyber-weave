import { describe, expect, it } from 'vitest'

import { ingestBatch } from './ingest.js'
import { CanonStore } from './store.js'
import { otlpSpanToRecord } from '../otel/service.js'
import type { OtlpSpan } from '../otel/receiver.js'

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

  it('quarantines a span no adapter claims instead of inventing a harness', () => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch([span({ 'totally.unknown': 'x' }, { spanId: 'orphan-1' })], store)

    expect(outcome.accepted).toBe(0)
    expect(outcome.quarantined).toBe(1)
    expect(store.get('orphan-1')).toBeUndefined()
    store.close()
  })
})
