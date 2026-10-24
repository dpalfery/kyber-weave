// T4: the OpenCode adapter is registered last in ADAPTERS, below every
// adapter whose fingerprint must win a tie, and claims no span that another
// harness owns.
//
// The second half of this file is the live defect: the deployed OpenCode
// (2.0.0) does not emit the Vercel AI SDK vocabularies this adapter was first
// written for at all. It emits OpenInference, so `detect` scored 0 on every
// `opencode.llm` / `Tool.execute` span and the whole trace quarantined as
// unclaimed. `llm-span.json` is the verbatim payload captured from the
// receiver's quarantine store; the tool and app-span fixtures carry the same
// vocabulary (only the LLM payload was captured with its attributes intact).

import { describe, expect, it } from 'vitest'

import { ADAPTERS, ingestBatch } from '../ingest.js'
import { CanonStore } from '../store.js'
import { opencodeAdapter } from './opencode.js'
import { OPENCODE_CONTRACT_VERSION_KEY } from './opencode-envelope.js'
import { AdapterRegistry } from './registry.js'
import { rawSpan } from './testing.js'
import type { OtlpSpan } from '../../otel/receiver.js'
import openInferenceLlmAttributes from '../fixtures/opencode-openinference/llm-span.json' with {
  type: 'json',
}
import openInferenceToolAttributes from '../fixtures/opencode-openinference/tool-execute-span.json' with {
  type: 'json',
}
import structuralAppSpans from '../fixtures/opencode-openinference/structural-app-spans.json' with {
  type: 'json',
}

const OPENINFERENCE_LLM = openInferenceLlmAttributes as Record<string, unknown>
const OPENINFERENCE_TOOL = openInferenceToolAttributes as Record<string, unknown>

/** A decoded OTLP span as the receiver hands it to ingest, from attribute fixtures. */
function otlpSpan(
  attributes: Record<string, unknown>,
  over: Partial<OtlpSpan> = {},
): OtlpSpan {
  return {
    spanId: over.spanId ?? 'span-1',
    traceId: over.traceId ?? 'trace-opencode',
    parentSpanId: over.parentSpanId ?? null,
    name: over.name ?? 'opencode.llm',
    kind: over.kind ?? 'internal',
    timestamp: '2026-10-16T12:00:00.000Z',
    durationMs: 42,
    status: { code: 'ok' },
    resource: over.resource ?? { 'service.name': 'opencode', 'app.version': '2.0.0' },
    attributes,
    ...over,
  } as OtlpSpan
}

describe('opencode adapter registration and tie-break (T4)', () => {
  it('registers the opencode adapter', () => {
    const adapter = ADAPTERS.find((entry) => entry.name === 'opencode')
    expect(adapter).toBeDefined()
  })

  it('claims no span that carries only shared or opencode-namespaced keys', () => {
    const adapter = ADAPTERS.find((entry) => entry.name === 'opencode')
    expect(adapter).toBeDefined()
    if (adapter === undefined) return
    const candidates = [
      rawSpan({ spanId: 'empty', attributes: {} }),
      rawSpan({ spanId: 'usage', attributes: { 'gen_ai.usage.input_tokens': 512 } }),
      rawSpan({
        spanId: 'vendor',
        attributes: { 'opencode.session.id': 'synth-session', 'gen_ai.usage.input_tokens': 512 },
      }),
    ]
    for (const span of candidates) {
      expect(adapter.detect(span), span.spanId).toBe(0)
    }
  })

  it('leaves the existing ingest votes unchanged', () => {
    const registry = new AdapterRegistry([...ADAPTERS])
    const attributed = registry.attribute([
      rawSpan({
        spanId: 'pi-span',
        source: 'pi-synth',
        traceId: 'trace-pi',
        attributes: { 'pi.session.id': 'synth-session', 'gen_ai.usage.input_tokens': 4821 },
      }),
      rawSpan({
        spanId: 'codex-span',
        source: 'codex-synth',
        traceId: 'trace-codex',
        attributes: { 'codex.tool.name': 'synth-tool', 'gen_ai.usage.input_tokens': 100 },
      }),
    ])
    expect(attributed.get('pi-span')).toBe('pi')
    expect(attributed.get('codex-span')).toBe('codex')
  })

  it('does not file a message envelope as conversation when content is missing', () => {
    const record = opencodeAdapter.normalize(
      rawSpan({
        spanId: 'legacy-missing-content',
        attributes: {
          'ai.prompt.messages': [
            { role: 'user', content: 'keep this sentence' },
            { role: 'user', content: null, id: 'synthetic-id' },
          ],
        },
      }),
    )
    const text = (record.parts ?? []).map((part) => part.text).join('\n')
    expect(text).toContain('keep this sentence')
    expect(text).not.toContain('synthetic-id')
  })
})

describe('opencodeAdapter.detect — OpenInference vocabulary (R6.2)', () => {
  it('scores the live opencode.llm payload above the registry threshold', () => {
    expect(
      opencodeAdapter.detect(rawSpan({ spanId: 'oi-llm', attributes: OPENINFERENCE_LLM })),
    ).toBeGreaterThanOrEqual(0.6)
  })

  it('scores the Tool.execute payload above the registry threshold', () => {
    expect(
      opencodeAdapter.detect(rawSpan({ spanId: 'oi-tool', attributes: OPENINFERENCE_TOOL })),
    ).toBeGreaterThanOrEqual(0.6)
  })

  it('never lets the source name change the score (R6.2)', () => {
    const namedOpenCode = rawSpan({
      spanId: 'oi-source-opencode',
      source: 'opencode',
      attributes: OPENINFERENCE_LLM,
    })
    const namedOtherwise = rawSpan({
      spanId: 'oi-source-other',
      source: 'some-other-runtime',
      attributes: OPENINFERENCE_LLM,
    })
    expect(opencodeAdapter.detect(namedOpenCode)).toBe(opencodeAdapter.detect(namedOtherwise))
    expect(opencodeAdapter.detect(namedOpenCode)).toBeGreaterThanOrEqual(0.6)
  })
})

describe('ingestBatch — OpenInference OpenCode model span', () => {
  it('claims the live payload as opencode with the live token decomposition', () => {
    const store = new CanonStore(':memory:')
    ingestBatch(
      [otlpSpan(OPENINFERENCE_LLM, { spanId: 'oi-llm', name: 'opencode.llm', kind: 'client' })],
      store,
    )
    const record = store.get('oi-llm')

    expect(record?.harness).toBe('opencode')
    expect(record?.op).toBe('llm.invoke')
    // `llm.token_count.prompt` (616) excludes cache read (159744): 616 + 159744
    // + 0 (cache_write) + 242 (completion) + 145 (reasoning) is the reported
    // total, 160747. The cache-exclusive conversion takes the prompt as fresh
    // and reassembles the reported input.
    expect(record?.tokens.freshInput).toBe(616)
    expect(record?.tokens.cacheRead).toBe(159744)
    expect(record?.tokens.cacheCreation).toBe(0)
    expect(record?.tokens.output).toBe(242)
    expect(record?.tokens.reasoning).toBe(145)
    expect(record?.tokens.reportedInput).toBe(160360)
    expect(record?.tokens.reportedOutput).toBe(242)
    expect(record?.sessionId).toBe('ses_eb9e8b987ffeoHdwdgtd63Tn5Z')
    expect(record?.content.conversation_history).toContain('completely wipe the database')
    expect(record?.measurability?.cache_read).toBe('measured')
    expect(record?.measurability?.cache_creation).toBe('measured')
    store.close()
  })

  it('declares a cache class absent when its OpenInference counter is absent', () => {
    const store = new CanonStore(':memory:')
    const { ['llm.token_count.prompt_details.cache_read']: _dropped, ...withoutCacheRead } =
      OPENINFERENCE_LLM
    ingestBatch([otlpSpan(withoutCacheRead, { spanId: 'oi-no-cache-read' })], store)
    const record = store.get('oi-no-cache-read')

    expect(record?.harness).toBe('opencode')
    expect(record?.measurability?.cache_read).toMatchObject({ availability: 'not_measurable' })
    store.close()
  })
})

describe('ingestBatch — OpenInference OpenCode tool span', () => {
  it('claims Tool.execute as a tool.invoke record', () => {
    const store = new CanonStore(':memory:')
    ingestBatch(
      [otlpSpan(OPENINFERENCE_TOOL, { spanId: 'oi-tool', name: 'Tool.execute' })],
      store,
    )
    const record = store.get('oi-tool')

    expect(record?.harness).toBe('opencode')
    expect(record?.op).toBe('tool.invoke')
    store.close()
  })
})

describe('ingestBatch — OpenInference structural app spans never become records', () => {
  it.each(
    structuralAppSpans as Array<{
      spanId: string
      name: string
      kind: string
      attributes: Record<string, unknown>
    }>,
  )('declines or quarantines $name without accepting a record', ({ spanId, name, kind, attributes }) => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch(
      [otlpSpan(attributes, { spanId, name, kind, traceId: `trace-${spanId}` })],
      store,
    )

    expect(outcome.accepted).toBe(0)
    expect(store.get(spanId)).toBeUndefined()
    expect(store.getQuarantine(spanId)).toBeDefined()
    store.close()
  })

  it('keeps the model and tool records while the structural span in the same trace quarantines', () => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch(
      [
        otlpSpan(OPENINFERENCE_LLM, { spanId: 'oi-llm', name: 'opencode.llm', traceId: 'trace-oc' }),
        otlpSpan(OPENINFERENCE_TOOL, {
          spanId: 'oi-tool',
          name: 'Tool.execute',
          traceId: 'trace-oc',
          parentSpanId: 'oi-llm',
        }),
        otlpSpan(
          {},
          {
            spanId: 'oc-credential-all',
            name: 'Credential.all',
            traceId: 'trace-oc',
            parentSpanId: 'oi-llm',
          },
        ),
      ],
      store,
    )

    expect(outcome.accepted).toBe(2)
    expect(store.get('oi-llm')?.op).toBe('llm.invoke')
    expect(store.get('oi-tool')?.op).toBe('tool.invoke')
    expect(store.get('oc-credential-all')).toBeUndefined()
    expect(store.getQuarantine('oc-credential-all')?.reason).toMatch(/non[- ]model/i)
    store.close()
  })
})

describe('AdapterRegistry — OpenInference spans do not steal other harnesses', () => {
  const registry = new AdapterRegistry(ADAPTERS)

  it('leaves a pi fingerprint span with pi', () => {
    const spans = [
      rawSpan({
        spanId: 'pi-vote',
        source: 'pi-abc123',
        traceId: 't-pi',
        attributes: { 'pi.session.id': 's-9f2', 'gen_ai.usage.input_tokens': 4821 },
      }),
    ]
    expect(registry.attribute(spans).get('pi-vote')).toBe('pi')
  })

  it('leaves a Copilot fingerprint span with copilot', () => {
    const spans = [
      rawSpan({
        spanId: 'copilot-vote',
        source: 'copilot-vscode',
        traceId: 't-copilot',
        attributes: {
          'copilot_chat.chat_session_id': '08551cf5-b064-4095-9552-8a9a0a0f78d2',
          'gen_ai.agent.name': 'antigravity',
          'gen_ai.usage.input_tokens': 251_976,
          'gen_ai.usage.cache_read.input_tokens': 243_910,
          'gen_ai.usage.output_tokens': 1_000,
        },
      }),
    ]
    expect(registry.attribute(spans).get('copilot-vote')).toBe('copilot')
  })
})

describe('opencode envelope v2 context bucketing (plan 2026-10-23 §5-T5, D4)', () => {
  // T5 RED: the envelope carries context content, but applyEnvelope buckets
  // nothing — these fail until the adapter maps context keys to parts.
  const contextAttributes = (): Record<string, unknown> => ({
    'opencode.session.id': 'ses_t5_ctx',
    [OPENCODE_CONTRACT_VERSION_KEY]: '2',
    'opencode.model.id': 'claude-opus-4-6',
    'opencode.model.provider': 'anthropic',
    'opencode.context.system': 'you are a build agent',
    'opencode.context.tools': JSON.stringify([{ name: 'read', description: 'read a file' }]),
    'opencode.context.skills': JSON.stringify(['review']),
  })

  it('buckets context system text as system_prompt and tools JSON as tool_definitions', () => {
    const record = opencodeAdapter.normalize(
      rawSpan({ spanId: 'ctx-bucket', attributes: contextAttributes() }),
    )
    expect(record.op).toBe('llm.invoke')
    const byPart = new Map((record.parts ?? []).map((part) => [part.part, part.text]))
    expect(byPart.get('system_prompt')).toBe('you are a build agent')
    expect(byPart.get('tool_definitions')).toContain('read')
  })

  it('maps a context span with no model markers as llm.invoke with zero fabricated counters', () => {
    const { 'opencode.model.id': _id, 'opencode.model.provider': _provider, ...contextOnly } =
      contextAttributes()
    const record = opencodeAdapter.normalize(rawSpan({ spanId: 'ctx-alone', attributes: contextOnly }))
    expect(record.op).toBe('llm.invoke')
    expect(record.tokens.freshInput).toBe(0)
    expect(record.tokens.output).toBe(0)
    expect(record.measurability?.cache_read).toMatchObject({ availability: 'not_measurable' })
  })
})
