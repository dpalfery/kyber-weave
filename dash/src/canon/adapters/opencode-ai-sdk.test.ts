import { describe, expect, it } from 'vitest'

import antigravitySpan from './__fixtures__/antigravity-span.json' with { type: 'json' }
import { canonicalParts } from './copilot.js'
import { opencodeAdapter } from './opencode.js'
import { rawSpan } from './testing.js'
import { AdapterRegistry } from './registry.js'
import { ingestBatch } from '../ingest.js'
import { ADAPTERS } from '../ingest.js'
import { CanonStore } from '../store.js'
import { isNotMeasurable } from '../types.js'
import type { ContentPart } from '../types.js'
import type { OtlpSpan } from '../../otel/receiver.js'
import legacyAttributes from '../fixtures/opencode-ai-sdk/legacy-ai-sdk-span.json' with { type: 'json' }
import genAiAttributes from '../fixtures/opencode-ai-sdk/gen-ai-sdk-span.json' with { type: 'json' }
import genAiCachedAttributes from '../fixtures/opencode-ai-sdk/gen-ai-sdk-with-cache-span.json' with {
  type: 'json',
}
import nonModelSpans from '../fixtures/opencode-ai-sdk/non-model-app-spans.json' with { type: 'json' }

// Synthetic OpenCode AI-SDK fixtures (P2.4b): real attribute key names from
// the T1 capability matrix, invented bodies and ids. The placeholder adapter
// claims nothing today; these tests pin the contract the Phase 2 implementer
// fills in without touching other harness vote fixtures.

const LEGACY = legacyAttributes as Record<string, unknown>
const GEN_AI = genAiAttributes as Record<string, unknown>
const GEN_AI_CACHED = genAiCachedAttributes as Record<string, unknown>

const bucketsOf = (parts: readonly ContentPart[]) =>
  parts.reduce<Record<string, number>>((acc, part) => {
    acc[part.part] = (acc[part.part] ?? 0) + 1
    return acc
  }, {})

function otlpSpan(
  attributes: Record<string, unknown>,
  over: Partial<OtlpSpan> = {},
): OtlpSpan {
  return {
    spanId: over.spanId ?? 'span-1',
    traceId: over.traceId ?? 'trace-opencode',
    parentSpanId: over.parentSpanId ?? null,
    name: over.name ?? 'ai.streamText',
    kind: over.kind ?? 'internal',
    timestamp: '2026-10-08T12:00:00.000Z',
    durationMs: 42,
    status: { code: 'ok' },
    resource: over.resource ?? { 'service.name': 'opencode' },
    attributes,
    ...over,
  } as OtlpSpan
}

/** Copilot-shaped counters from ingest.test.ts — must keep voting `copilot`. */
const copilotVoteAttributes = {
  'copilot_chat.chat_session_id': '08551cf5-b064-4095-9552-8a9a0a0f78d2',
  'gen_ai.agent.name': 'antigravity',
  'gen_ai.usage.input_tokens': 251_976,
  'gen_ai.usage.cache_read.input_tokens': 243_910,
  'gen_ai.usage.output_tokens': 1_000,
}

describe('opencodeAdapter.detect — ai.* namespace (R6.2)', () => {
  it('never treats service.name as fingerprint evidence', () => {
    const misleading = rawSpan({
      spanId: 'svc-only',
      source: 'opencode',
      attributes: {},
    })
    const namedLikeOpenCode = rawSpan({
      spanId: 'svc-named',
      source: 'opencode-desktop-runtime',
      attributes: {},
    })
    expect(opencodeAdapter.detect(misleading)).toBe(0)
    expect(opencodeAdapter.detect(namedLikeOpenCode)).toBe(0)
  })

  it('scores legacy ai.* model evidence above the registry threshold', () => {
    expect(opencodeAdapter.detect(rawSpan({ spanId: 'legacy-detect', attributes: LEGACY }))).toBeGreaterThanOrEqual(
      0.6,
    )
  })

  it('scores gen_ai.* AI-SDK model evidence above the registry threshold', () => {
    expect(opencodeAdapter.detect(rawSpan({ spanId: 'gen-detect', attributes: GEN_AI }))).toBeGreaterThanOrEqual(0.6)
  })
})

describe('ingestBatch — legacy ai.* OpenCode model span', () => {
  it('claims the span as opencode and maps legacy buckets', () => {
    const store = new CanonStore(':memory:')
    ingestBatch([otlpSpan(LEGACY, { spanId: 'legacy-model' })], store)
    const record = store.get('legacy-model')

    expect(record?.harness).toBe('opencode')
    expect(record?.op).toBe('llm.invoke')
    expect(record?.content.system_prompt).toContain('Synthetic OpenCode system directive')
    expect(record?.content.conversation_history).toContain('Synthetic user turn for legacy OpenCode fixture')
    expect(record?.content.tool_definitions).toContain('read_file')
    expect(record?.content.tool_result_content).toContain('Synthetic tool output from legacy ai.toolCall.result')
    expect(record?.tokens.reportedInput).toBe(512)
    expect(record?.tokens.reportedOutput).toBe(96)
    expect(record?.tokens.cacheRead).toBe(0)
    expect(record?.tokens.cacheCreation).toBe(0)
    store.close()
  })

  it('leaves cache classes not measurable when the legacy fixture omits them', () => {
    const store = new CanonStore(':memory:')
    ingestBatch([otlpSpan(LEGACY, { spanId: 'legacy-cache-absent' })], store)
    const record = store.get('legacy-cache-absent')

    expect(record?.harness).toBe('opencode')
    expect(isNotMeasurable(record!.measurability!.cache_read)).toBe(true)
    expect(isNotMeasurable(record!.measurability!.cache_creation)).toBe(true)
    store.close()
  })
})

describe('ingestBatch — gen_ai.* AI-SDK OpenCode model span', () => {
  it('claims the span as opencode with canonicalParts buckets', () => {
    const store = new CanonStore(':memory:')
    ingestBatch([otlpSpan(GEN_AI, { spanId: 'gen-ai-model' })], store)
    const record = store.get('gen-ai-model')
    const expected = bucketsOf(canonicalParts(GEN_AI))

    expect(record?.harness).toBe('opencode')
    expect(record?.op).toBe('llm.invoke')
    expect(bucketsOf(record!.parts ?? [])).toEqual(expected)
    expect(record?.tokens.reportedInput).toBe(640)
    expect(record?.tokens.reportedOutput).toBe(112)
    store.close()
  })

  it('measures cache classes when the gen_ai fixture carries them', () => {
    const store = new CanonStore(':memory:')
    ingestBatch([otlpSpan(GEN_AI_CACHED, { spanId: 'gen-ai-cached' })], store)
    const record = store.get('gen-ai-cached')

    expect(record?.harness).toBe('opencode')
    expect(record?.tokens.cacheRead).toBe(300)
    expect(record?.tokens.cacheCreation).toBe(40)
    expect(record!.measurability!.cache_read).toBe('measured')
    expect(record!.measurability!.cache_creation).toBe('measured')
    store.close()
  })
})

describe('ingestBatch — OpenCode app spans stay non-model', () => {
  it.each(nonModelSpans as Array<{ spanId: string; name: string; kind: string; attributes: Record<string, unknown> }>)(
    'quarantines $name without accepting a record',
    ({ spanId, name, kind, attributes }) => {
      const store = new CanonStore(':memory:')
      const outcome = ingestBatch(
        [otlpSpan(attributes, { spanId, name, kind: kind as OtlpSpan['kind'], traceId: `trace-${spanId}` })],
        store,
      )

      expect(outcome.accepted).toBe(0)
      expect(store.get(spanId)).toBeUndefined()
      expect(store.getQuarantine(spanId)?.reason).toMatch(/non[- ]model/i)
      store.close()
    },
  )
})

describe('AdapterRegistry — other harness votes unchanged (P2.4b)', () => {
  const registry = new AdapterRegistry(ADAPTERS)

  it('still attributes Antigravity corpus spans to antigravity', () => {
    const spans = [
      rawSpan({
        spanId: 'agy-vote',
        source: 'agy',
        traceId: 't-agy',
        attributes: antigravitySpan as Record<string, unknown>,
      }),
    ]
    expect(registry.scoreGroup('agy', 't-agy', spans)).toEqual({ harness: 'antigravity', confidence: 1 })
    expect(registry.attribute(spans).get('agy-vote')).toBe('antigravity')
  })

  it('still attributes pi fingerprint spans to pi', () => {
    const spans = [
      rawSpan({
        spanId: 'pi-vote',
        source: 'pi-abc123',
        traceId: 't-pi',
        attributes: { 'gen_ai.usage.input_tokens': 4821, 'pi.session.id': 's-9f2' },
      }),
    ]
    expect(registry.attribute(spans).get('pi-vote')).toBe('pi')
  })

  it('still attributes the Copilot/Antigravity inclusive shape to copilot', () => {
    const spans = [
      rawSpan({
        spanId: 'copilot-vote',
        source: 'copilot-vscode',
        traceId: 't-copilot',
        attributes: copilotVoteAttributes,
      }),
    ]
    expect(registry.attribute(spans).get('copilot-vote')).toBe('copilot')
  })
})
