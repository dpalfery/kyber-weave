// T3: the opencode plugin span mapper turns real-shaped hook payloads into
// envelope OTLP spans (test-first RED).
//
// One codebase serves both opencode targets from the dash-CLI-managed home
// `dash/src/capture/harnesses/opencode-plugin/`. The T0 skeleton
// (`fixtures/span-payload-skeleton.json`) surveys the session/turn/span,
// token, tool, and error shapes; the T1 envelope (`canon/adapters/opencode-
// envelope.ts`) is what ingest claims. The mapper is the bridge: session
// identity, model/tool markers, OpenInference token counters (exclusive
// convention), timing, and error status map exactly, and marker-less spans
// stay the non-model quarantine T1 names.

import { describe, expect, it } from 'vitest'

import { ingestBatch } from '../../../canon/ingest.js'
import { CanonStore } from '../../../canon/store.js'
import { validateTokens } from '../../../canon/types.js'
import {
  OPENCODE_CONTRACT_VERSION,
  isOpencodeEnvelopeSpan,
  isSupportedOpencodeEnvelope,
} from '../../../canon/adapters/opencode-envelope.js'
import { rawSpan } from '../../../canon/adapters/testing.js'
import { opencodeAdapter } from '../../../canon/adapters/opencode.js'
import {
  mapOpencodePayloadToAttributes,
  mapOpencodePayloadToOtlpSpan,
  type OpencodePluginSpanPayload,
} from './mapper.js'

const MODEL_PAYLOAD: OpencodePluginSpanPayload = {
  traceId: 'trace-t3-model',
  spanId: 'span-t3-model',
  parentSpanId: null,
  name: 'opencode.turn',
  kind: 'internal',
  sessionID: 'ses_t3_model',
  messageID: 'msg_t3_001',
  callID: 'call_t3_001',
  turnIndex: 3,
  agent: 'build',
  model: { providerID: 'anthropic', modelID: 'claude-opus-4-6' },
  prompt_text: 'T3 model prompt text',
  tokens: {
    input_tokens: 120,
    output_tokens: 45,
    cache_read_tokens: 300,
    cache_creation_tokens: 40,
    reasoning_tokens: 12,
  },
  startTimeUnixNano: '1720000000000000000',
  endTimeUnixNano: '1720000000042000000',
}

const TOOL_PAYLOAD: OpencodePluginSpanPayload = {
  traceId: 'trace-t3-tool',
  spanId: 'span-t3-tool',
  parentSpanId: 'span-t3-model',
  name: 'Tool.execute',
  kind: 'internal',
  sessionID: 'ses_t3_model',
  turnIndex: 3,
  agent: 'build',
  tool: 'read',
  callID: 'call_t3_tool_001',
  messageID: 'msg_t3_001',
  tokens: {
    input_tokens: 0,
    output_tokens: 0,
  },
  startTimeUnixNano: '1720000000100000000',
  endTimeUnixNano: '1720000000115000000',
}

const ERROR_PAYLOAD: OpencodePluginSpanPayload = {
  ...MODEL_PAYLOAD,
  spanId: 'span-t3-error',
  traceId: 'trace-t3-error',
  errors: { type: 'ProviderError', message: 'upstream 500', status: 'error' },
}

const NON_MODEL_PAYLOAD: OpencodePluginSpanPayload = {
  traceId: 'trace-t3-app',
  spanId: 'span-t3-app',
  parentSpanId: null,
  name: 'Session.load',
  kind: 'internal',
  sessionID: 'synthetic-session-load-id',
  startTimeUnixNano: '1720000000200000000',
  endTimeUnixNano: '1720000000201000000',
}

describe('opencode plugin span mapper carries the T0 payload onto the T1 envelope', () => {
  it('stamps the supported contract version and the envelope session identity', () => {
    const attributes = mapOpencodePayloadToAttributes(MODEL_PAYLOAD)

    expect(attributes['opencode.contract_version']).toBe(OPENCODE_CONTRACT_VERSION)
    expect(attributes['opencode.session.id']).toBe('ses_t3_model')
    expect(attributes['opencode.turn.index']).toBe(3)
    const span = mapOpencodePayloadToOtlpSpan(MODEL_PAYLOAD)
    expect(isOpencodeEnvelopeSpan(rawSpan({ spanId: span.spanId, attributes: span.attributes }))).toBe(true)
    expect(
      isSupportedOpencodeEnvelope(rawSpan({ spanId: span.spanId, attributes: span.attributes })),
    ).toBe(true)
  })

  it('maps model provider, id, and prompt text onto envelope markers', () => {
    const attributes = mapOpencodePayloadToAttributes(MODEL_PAYLOAD)

    expect(attributes['opencode.model.provider']).toBe('anthropic')
    expect(attributes['opencode.model.id']).toBe('claude-opus-4-6')
    expect(attributes['opencode.prompt.text']).toBe('T3 model prompt text')
  })

  it('maps T0 token counters onto exclusive OpenInference counters', () => {
    const attributes = mapOpencodePayloadToAttributes(MODEL_PAYLOAD)

    expect(attributes['llm.token_count.prompt']).toBe(120)
    expect(attributes['llm.token_count.completion']).toBe(45)
    expect(attributes['llm.token_count.prompt_details.cache_read']).toBe(300)
    expect(attributes['llm.token_count.prompt_details.cache_write']).toBe(40)
    expect(attributes['llm.token_count.completion_details.reasoning']).toBe(12)
  })

  it('maps tool name and call id so the tool marker wins the operation vote', () => {
    const attributes = mapOpencodePayloadToAttributes(TOOL_PAYLOAD)

    expect(attributes['opencode.tool.name']).toBe('read')
    expect(attributes['opencode.tool.call.id']).toBe('call_t3_tool_001')
  })

  it('maps T0 timing onto the OTLP span timestamp, duration, and kind', () => {
    const span = mapOpencodePayloadToOtlpSpan(MODEL_PAYLOAD)

    expect(span.timestamp).toBe(new Date(1720000000000).toISOString())
    expect(span.durationMs).toBe(42)
    expect(span.kind).toBe('internal')
    expect(span.name).toBe('opencode.turn')
  })

  it('maps T0 errors onto the OTLP error status without dropping the session', () => {
    const span = mapOpencodePayloadToOtlpSpan(ERROR_PAYLOAD)

    expect(span.status.code).toBe('error')
    expect(String(span.status.message ?? '')).toMatch(/upstream 500/)
    expect(span.attributes['opencode.session.id']).toBe('ses_t3_model')
  })

  it('omits non-finite token counters instead of stamping NaN/Infinity', () => {
    const attributes = mapOpencodePayloadToAttributes({
      ...MODEL_PAYLOAD,
      spanId: 'span-t3-nan-tokens',
      tokens: {
        input_tokens: Number.NaN,
        output_tokens: Number.POSITIVE_INFINITY,
        cache_read_tokens: Number.NaN,
        cache_creation_tokens: Number.POSITIVE_INFINITY,
        reasoning_tokens: Number.NaN,
      },
    })

    expect(attributes['llm.token_count.prompt']).toBeUndefined()
    expect(attributes['llm.token_count.completion']).toBeUndefined()
    expect(attributes['llm.token_count.prompt_details.cache_read']).toBeUndefined()
    expect(attributes['llm.token_count.prompt_details.cache_write']).toBeUndefined()
    expect(attributes['llm.token_count.completion_details.reasoning']).toBeUndefined()
  })

  it('never throws on junk timestamps, falling back to a valid span', () => {
    const span = mapOpencodePayloadToOtlpSpan({
      ...MODEL_PAYLOAD,
      spanId: 'span-t3-junk-time',
      startTimeUnixNano: 'not-a-timestamp',
      endTimeUnixNano: 'also-junk',
    })

    expect(span.startTimeUnixNano).toBe('not-a-timestamp')
    expect(Number.isNaN(Date.parse(span.timestamp))).toBe(false)
    expect(span.durationMs).toBeGreaterThanOrEqual(0)
  })
})

describe('opencode plugin span mapper round-trips through the opencode adapter', () => {
  it('round-trips a model fixture with exact tokens, timing, and session identity', () => {
    const store = new CanonStore(':memory:')
    const span = mapOpencodePayloadToOtlpSpan(MODEL_PAYLOAD)
    const outcome = ingestBatch([span], store)

    expect(outcome).toEqual({ accepted: 1, quarantined: 0, rejected: 0 })
    const record = store.get('span-t3-model')
    expect(record?.harness).toBe('opencode')
    expect(record?.op).toBe('llm.invoke')
    expect(record?.sessionId).toBe('ses_t3_model')
    expect(record?.tokens.freshInput).toBe(120)
    expect(record?.tokens.cacheRead).toBe(300)
    expect(record?.tokens.cacheCreation).toBe(40)
    expect(record?.tokens.output).toBe(45)
    expect(record?.tokens.reasoning).toBe(12)
    expect(record?.tokens.reportedInput).toBe(460)
    expect(record?.tokens.reportedOutput).toBe(45)
    expect(validateTokens(record!.tokens).valid).toBe(true)
    expect(opencodeAdapter.validate(record!)).toBeUndefined()
    expect(record?.timestamp).toBe(span.timestamp)
    expect(record?.durationMs).toBe(42)
    expect(record?.content.conversation_history).toContain('T3 model prompt text')
    store.close()
  })

  it('round-trips a tool fixture as a tool.invoke record', () => {
    const store = new CanonStore(':memory:')
    const span = mapOpencodePayloadToOtlpSpan(TOOL_PAYLOAD)
    const outcome = ingestBatch([span], store)

    expect(outcome).toEqual({ accepted: 1, quarantined: 0, rejected: 0 })
    const record = store.get('span-t3-tool')
    expect(record?.harness).toBe('opencode')
    expect(record?.op).toBe('tool.invoke')
    expect(record?.sessionId).toBe('ses_t3_model')
    store.close()
  })

  it('round-trips an error fixture with the harness error status preserved', () => {
    const store = new CanonStore(':memory:')
    const span = mapOpencodePayloadToOtlpSpan(ERROR_PAYLOAD)
    ingestBatch([span], store)

    const record = store.get('span-t3-error')
    expect(record?.harness).toBe('opencode')
    expect(record?.status).toBe('error')
    store.close()
  })

  it('filters a marker-less app span as non-model per the T1 envelope rules', () => {
    const store = new CanonStore(':memory:')
    const span = mapOpencodePayloadToOtlpSpan(NON_MODEL_PAYLOAD)
    const outcome = ingestBatch([span], store)

    expect(outcome.accepted).toBe(0)
    expect(store.get('span-t3-app')).toBeUndefined()
    expect(store.getQuarantine('span-t3-app')?.reason).toMatch(/non[- ]model/i)
    store.close()
  })
})

// T4 (plan 2026-10-23 §6-T4 RED): the T0 context fixture
// (`fixtures/context-payload.json`: system, tools, skills, model/agent
// identity) must round-trip onto envelope v2 attributes
// (`opencode.context.system/.tools/.skills`, model markers when named).
// Absent fields stay absent — never empty-string padding.

describe('opencode plugin span mapper carries T0 context fields onto envelope v2 (T4)', () => {
  const CONTEXT_PAYLOAD: OpencodePluginSpanPayload = {
    ...MODEL_PAYLOAD,
    traceId: 'trace-t4-context',
    spanId: 'span-t4-context',
    systemPrompt: 'T4 assembled system prompt',
    toolsText: '[{"name":"read","description":"read a file"}]',
    skills: ['plan', 'review'],
  }

  it('maps system prompt, tool definitions, and skills onto opencode.context.* keys', () => {
    const attributes = mapOpencodePayloadToAttributes(CONTEXT_PAYLOAD)

    expect(attributes['opencode.context.system']).toBe('T4 assembled system prompt')
    expect(attributes['opencode.context.tools']).toBe(
      '[{"name":"read","description":"read a file"}]',
    )
    expect(attributes['opencode.context.skills']).toBe('["plan","review"]')
  })

  it("drops an empty skills array and its '[]' serialization alike (review PR #425 finding 3, RED)", () => {
    const fromArray = mapOpencodePayloadToAttributes({
      ...MODEL_PAYLOAD,
      spanId: 'span-skills-empty-array',
      skills: [],
    })
    expect('opencode.context.skills' in fromArray).toBe(false)
    const fromString = mapOpencodePayloadToAttributes({
      ...MODEL_PAYLOAD,
      spanId: 'span-skills-empty-string',
      skills: '[]',
    })
    expect('opencode.context.skills' in fromString).toBe(false)
  })

  it('keeps malformed skills stamped for the named not-measurable path (review PR #425 finding 3, RED)', () => {
    const attributes = mapOpencodePayloadToAttributes({
      ...MODEL_PAYLOAD,
      spanId: 'span-skills-malformed',
      skills: 'not-json',
    })
    expect(attributes['opencode.context.skills']).toBe('not-json')
  })

  it('leaves context keys absent when the payload names no content', () => {
    const attributes = mapOpencodePayloadToAttributes(MODEL_PAYLOAD)

    expect(attributes['opencode.context.system']).toBeUndefined()
    expect(attributes['opencode.context.tools']).toBeUndefined()
    expect(attributes['opencode.context.skills']).toBeUndefined()
    expect('opencode.context.system' in attributes).toBe(false)
    expect('opencode.context.tools' in attributes).toBe(false)
    expect('opencode.context.skills' in attributes).toBe(false)
  })
})

// T2 (R1, D6): the 2.1.4 catalog window rides as the optional additive
// attribute `opencode.model.context_limit` inside contract '2'. Only a
// positive finite window is stamped; absent stays absent, never 0.
describe('opencode plugin span mapper carries the reported context window (T2, D6)', () => {
  it('stamps opencode.model.context_limit as a number without a contract bump', () => {
    const attributes = mapOpencodePayloadToAttributes({
      ...MODEL_PAYLOAD,
      spanId: 'span-context-limit',
      contextLimit: 200000,
    })
    expect(attributes['opencode.model.context_limit']).toBe(200000)
    expect(attributes['opencode.contract_version']).toBe('2')
  })

  it.each([0, -1, Number.NaN, Number.POSITIVE_INFINITY])(
    'leaves the window absent for the unusable value %s',
    (contextLimit) => {
      const attributes = mapOpencodePayloadToAttributes({
        ...MODEL_PAYLOAD,
        spanId: 'span-context-limit-bad',
        contextLimit,
      })
      expect('opencode.model.context_limit' in attributes).toBe(false)
    },
  )

  it('leaves the window absent when the payload names none', () => {
    const attributes = mapOpencodePayloadToAttributes(MODEL_PAYLOAD)
    expect('opencode.model.context_limit' in attributes).toBe(false)
  })
})
