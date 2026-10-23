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
