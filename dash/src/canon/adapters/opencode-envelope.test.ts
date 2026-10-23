// T1: the opencode plugin span/turn envelope carries a contract version key.
// A span the plugin emits names `opencode.contract_version`; ingest claims it
// only when the version is the supported one. An envelope span with an unknown
// or missing version quarantines with a named reason and is never attributed
// to any harness, and a contract bump invalidates stale checkpoints.
//
// The bare `opencode.session.id`-only shape below is the pre-envelope
// `Session.load` app span (see non-model-app-spans.json): it carries no turn,
// model, tool, prompt, or version marker, so it is not an envelope and keeps
// the legacy non-model path.

import { describe, expect, it } from 'vitest'

import { ADAPTERS, ingestBatch } from '../ingest.js'
import { checkpointIsReusable, type SourceCheckpoint } from '../source-state.js'
import { CanonStore } from '../store.js'
import type { OtlpSpan } from '../../otel/receiver.js'
import { opencodeAdapter } from './opencode.js'
import {
  OPENCODE_CONTRACT_VERSION,
  OPENCODE_CONTRACT_VERSION_KEY,
  UNKNOWN_OPENCODE_CONTRACT_VERSION_REASON,
  isOpencodeEnvelopeSpan,
  isSupportedOpencodeEnvelope,
} from './opencode-envelope.js'
import { AdapterRegistry } from './registry.js'
import { rawSpan } from './testing.js'
import {
  mapOpencodePayloadToAttributes,
  mapOpencodePayloadToOtlpSpan,
} from '../../capture/harnesses/opencode-plugin/mapper.js'
import { toRawSpan } from '../ingest.js'

/** A version value no supported contract will ever equal. */
const FUTURE_VERSION = '9999-future'

function envelopeAttributes(
  version: string | undefined,
  kind: 'model' | 'tool' | 'app',
): Record<string, unknown> {
  const base: Record<string, unknown> = {
    'opencode.session.id': 'ses_t1_envelope',
    'opencode.turn.index': 3,
    'opencode.agent': 'build',
  }
  if (version !== undefined) base[OPENCODE_CONTRACT_VERSION_KEY] = version
  if (kind === 'model') {
    return {
      ...base,
      'opencode.model.provider': 'anthropic',
      'opencode.model.id': 'claude-opus-4-6',
      'opencode.prompt.text': 'T1 envelope prompt text',
    }
  }
  if (kind === 'tool') {
    return {
      ...base,
      'opencode.tool.name': 'read',
      'opencode.tool.call.id': 'call_t1_envelope',
    }
  }
  return base
}

/** A decoded OTLP span as the receiver hands it to ingest. */
function otlpSpan(
  attributes: Record<string, unknown>,
  over: Partial<OtlpSpan> & Pick<OtlpSpan, 'spanId'>,
): OtlpSpan {
  return {
    traceId: `trace-${over.spanId}`,
    parentSpanId: null,
    name: 'opencode.turn',
    kind: 'internal',
    timestamp: '2026-10-21T12:00:00.000Z',
    durationMs: 42,
    status: { code: 'ok' },
    resource: { 'service.name': 'opencode' },
    attributes,
    ...over,
  } as OtlpSpan
}

function checkpoint(overrides: Partial<SourceCheckpoint> = {}): SourceCheckpoint {
  return {
    harnessId: 'opencode',
    sourceKey: 'session:ses_t1_envelope',
    providerId: 'opencode',
    parserId: 'opencode-plugin',
    parserContractVersion: OPENCODE_CONTRACT_VERSION,
    format: 'otlp',
    sourceRootLabel: 'opencode-plugin',
    revisionToken: 'rev:t1',
    coveredFromUtc: '2026-10-21T00:00:00.000Z',
    coveredThroughUtc: '2026-10-21T12:00:00.000Z',
    lastAttemptUtc: '2026-10-21T12:00:00.000Z',
    lastSuccessUtc: '2026-10-21T12:00:01.000Z',
    lastStatus: 'ok',
    lastErrorCode: null,
    unitCount: 1,
    recordCount: 1,
    ...overrides,
  }
}

describe('opencode envelope contract version key (T1)', () => {
  it('names the version key under the opencode namespace with a non-empty contract version', () => {
    expect(OPENCODE_CONTRACT_VERSION_KEY.startsWith('opencode.')).toBe(true)
    expect(OPENCODE_CONTRACT_VERSION).toMatch(/^.+$/)
  })

  it('recognises versioned plugin spans as envelope while leaving the legacy session-only shape alone', () => {
    for (const kind of ['model', 'tool', 'app'] as const) {
      const span = rawSpan({
        spanId: `envelope-${kind}`,
        attributes: envelopeAttributes(OPENCODE_CONTRACT_VERSION, kind),
      })
      expect(isOpencodeEnvelopeSpan(span), kind).toBe(true)
    }
    // Turn markers without a version are still the envelope — the missing
    // version is what the quarantine path names.
    expect(
      isOpencodeEnvelopeSpan(
        rawSpan({ spanId: 'envelope-no-version', attributes: envelopeAttributes(undefined, 'model') }),
      ),
    ).toBe(true)
    // The pre-envelope `Session.load` shape carries only a session id and no
    // turn, model, tool, prompt, or version marker: not an envelope, so its
    // legacy non-model quarantine is unchanged.
    expect(
      isOpencodeEnvelopeSpan(
        rawSpan({
          spanId: 'legacy-session-only',
          attributes: { 'opencode.session.id': 'synthetic-session-load-id' },
        }),
      ),
    ).toBe(false)
    expect(isOpencodeEnvelopeSpan(rawSpan({ spanId: 'empty', attributes: {} }))).toBe(false)
  })

  it('supports only the current contract version, never an unknown or missing one', () => {
    const supported = rawSpan({
      spanId: 'supported',
      attributes: envelopeAttributes(OPENCODE_CONTRACT_VERSION, 'model'),
    })
    expect(isSupportedOpencodeEnvelope(supported)).toBe(true)
    for (const [spanId, attributes] of [
      ['unknown', envelopeAttributes(FUTURE_VERSION, 'model')],
      ['missing', envelopeAttributes(undefined, 'model')],
      ['empty', {}],
    ] as const) {
      expect(
        isSupportedOpencodeEnvelope(rawSpan({ spanId, attributes: attributes as Record<string, unknown> })),
        spanId,
      ).toBe(false)
    }
  })

  it('scores a supported envelope above the registry threshold', () => {
    for (const kind of ['model', 'tool'] as const) {
      expect(
        opencodeAdapter.detect(
          rawSpan({ spanId: `detect-${kind}`, attributes: envelopeAttributes(OPENCODE_CONTRACT_VERSION, kind) }),
        ),
        kind,
      ).toBeGreaterThanOrEqual(0.6)
    }
  })

  it('scores an unknown or missing version zero so no vote can claim it', () => {
    for (const [spanId, attributes] of [
      ['detect-unknown', envelopeAttributes(FUTURE_VERSION, 'model')],
      ['detect-missing', envelopeAttributes(undefined, 'model')],
    ] as const) {
      expect(opencodeAdapter.detect(rawSpan({ spanId, attributes })), spanId).toBe(0)
    }
  })

  it('never lets the source name change the envelope score (R6.2)', () => {
    const attributes = envelopeAttributes(OPENCODE_CONTRACT_VERSION, 'model')
    const namedOpenCode = rawSpan({ spanId: 'envelope-src-a', source: 'opencode', attributes })
    const namedOtherwise = rawSpan({ spanId: 'envelope-src-b', source: 'some-other-runtime', attributes })
    expect(opencodeAdapter.detect(namedOpenCode)).toBe(opencodeAdapter.detect(namedOtherwise))
  })
})

describe('ingestBatch — opencode envelope (T1)', () => {
  it('claims a supported model envelope as an opencode llm.invoke record', () => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch(
      [otlpSpan(envelopeAttributes(OPENCODE_CONTRACT_VERSION, 'model'), { spanId: 'env-model' })],
      store,
    )

    expect(outcome).toEqual({ accepted: 1, quarantined: 0, rejected: 0 })
    const record = store.get('env-model')
    expect(record?.harness).toBe('opencode')
    expect(record?.op).toBe('llm.invoke')
    expect(record?.sessionId).toBe('ses_t1_envelope')
    expect(record?.content.conversation_history).toContain('T1 envelope prompt text')
    store.close()
  })

  it('claims a supported tool envelope as an opencode tool.invoke record', () => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch(
      [otlpSpan(envelopeAttributes(OPENCODE_CONTRACT_VERSION, 'tool'), { spanId: 'env-tool' })],
      store,
    )

    expect(outcome).toEqual({ accepted: 1, quarantined: 0, rejected: 0 })
    const record = store.get('env-tool')
    expect(record?.harness).toBe('opencode')
    expect(record?.op).toBe('tool.invoke')
    store.close()
  })

  it('quarantines an envelope app span as non-model without accepting a record', () => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch(
      [
        otlpSpan(envelopeAttributes(OPENCODE_CONTRACT_VERSION, 'app'), {
          spanId: 'env-app',
          name: 'SessionRunner.step',
        }),
      ],
      store,
    )

    expect(outcome.accepted).toBe(0)
    expect(store.get('env-app')).toBeUndefined()
    expect(store.getQuarantine('env-app')?.reason).toMatch(/non[- ]model/i)
    store.close()
  })

  it('quarantines an unknown contract version with a named reason and never attributes it', () => {
    const attributes = envelopeAttributes(FUTURE_VERSION, 'model')
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch([otlpSpan(attributes, { spanId: 'env-unknown' })], store)

    expect(outcome.accepted).toBe(0)
    expect(outcome.quarantined).toBe(1)
    expect(store.get('env-unknown')).toBeUndefined()
    expect(store.getQuarantine('env-unknown')?.reason).toBe(UNKNOWN_OPENCODE_CONTRACT_VERSION_REASON)
    store.close()

    const registry = new AdapterRegistry(ADAPTERS)
    const attributed = registry.attribute([
      rawSpan({ spanId: 'env-unknown', source: 'opencode', traceId: 'trace-env-unknown', attributes }),
    ])
    expect(attributed.get('env-unknown')).toBeUndefined()
  })

  it('quarantines a missing contract version rather than claiming the span', () => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch(
      [otlpSpan(envelopeAttributes(undefined, 'model'), { spanId: 'env-missing' })],
      store,
    )

    expect(outcome.accepted).toBe(0)
    expect(outcome.quarantined).toBe(1)
    expect(store.get('env-missing')).toBeUndefined()
    expect(store.getQuarantine('env-missing')?.reason).toBe(UNKNOWN_OPENCODE_CONTRACT_VERSION_REASON)
    store.close()
  })

  it('accounts every row of a mixed envelope batch in the outcome', () => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch(
      [
        otlpSpan(envelopeAttributes(OPENCODE_CONTRACT_VERSION, 'model'), { spanId: 'mix-model' }),
        otlpSpan(envelopeAttributes(OPENCODE_CONTRACT_VERSION, 'tool'), { spanId: 'mix-tool' }),
        otlpSpan(envelopeAttributes(OPENCODE_CONTRACT_VERSION, 'app'), {
          spanId: 'mix-app',
          name: 'SessionRunner.step',
        }),
        otlpSpan(envelopeAttributes(FUTURE_VERSION, 'model'), { spanId: 'mix-unknown' }),
        otlpSpan(envelopeAttributes(undefined, 'model'), { spanId: 'mix-missing' }),
      ],
      store,
    )

    expect(outcome).toEqual({ accepted: 2, quarantined: 3, rejected: 0 })
    expect(outcome.accepted + outcome.quarantined + outcome.rejected).toBe(5)
    store.close()
  })
})

describe('opencode contract bump invalidates stale checkpoints (T1)', () => {
  it('leaves a checkpoint written under a stale contract unreusable under the current one', () => {
    const request = { revisionToken: 'rev:t1', parserContractVersion: OPENCODE_CONTRACT_VERSION }
    expect(checkpointIsReusable(checkpoint(), request)).toBe(true)
    expect(
      checkpointIsReusable(
        checkpoint({ parserContractVersion: `${OPENCODE_CONTRACT_VERSION}-stale` }),
        request,
      ),
    ).toBe(false)
  })

  it('invalidates the checkpoint on a contract bump while keeping rows and provenance', () => {
    const store = new CanonStore(':memory:')
    ingestBatch(
      [otlpSpan(envelopeAttributes(OPENCODE_CONTRACT_VERSION, 'model'), { spanId: 'ckpt-model' })],
      store,
    )
    const record = store.get('ckpt-model')
    expect(record).toBeDefined()
    store.commitSourceUnit({
      records: record === undefined ? [] : [record],
      provenance: [
        {
          spanId: 'ckpt-model',
          harnessId: 'opencode',
          sourceKey: 'session:ses_t1_envelope',
          nativeSessionId: 'ses_t1_envelope',
          nativeRecordId: 'turn-3',
          sourceRevision: 'rev:t1',
          parserVersion: `${OPENCODE_CONTRACT_VERSION}-stale`,
          importedAtUtc: '2026-10-21T12:00:01.000Z',
          locationToken: 'opencode-plugin#turn-3',
        },
      ],
      checkpoint: checkpoint({ parserContractVersion: `${OPENCODE_CONTRACT_VERSION}-stale` }),
    })
    store.invalidateSourceCheckpoint('opencode', 'session:ses_t1_envelope', OPENCODE_CONTRACT_VERSION)

    const after = store.getSourceCheckpoint('opencode', 'session:ses_t1_envelope')
    expect(after?.lastStatus).toBe('invalidated')
    expect(after?.parserContractVersion).toBe(OPENCODE_CONTRACT_VERSION)
    expect(
      checkpointIsReusable(after, { revisionToken: 'rev:t1', parserContractVersion: OPENCODE_CONTRACT_VERSION }),
    ).toBe(false)
    expect(store.get('ckpt-model')?.spanId).toBe('ckpt-model')
    expect(store.getRecordProvenance('ckpt-model')?.spanId).toBe('ckpt-model')
    store.close()
  })
})

describe('opencode mapper onto the envelope (T3)', () => {
  it('detects a mapper model span above the registry threshold with the envelope session', () => {
    const span = mapOpencodePayloadToOtlpSpan({
      traceId: 'trace-t3-adapter-model',
      spanId: 't3-adapter-model',
      parentSpanId: null,
      name: 'opencode.turn',
      kind: 'internal',
      sessionID: 'ses_t3_adapter',
      turnIndex: 3,
      agent: 'build',
      model: { providerID: 'anthropic', modelID: 'claude-opus-4-6' },
      prompt_text: 'T3 adapter prompt',
      tokens: { input_tokens: 120, output_tokens: 45, cache_read_tokens: 300, cache_creation_tokens: 40 },
      startTimeUnixNano: '1720000000000000000',
      endTimeUnixNano: '1720000000042000000',
    })

    expect(opencodeAdapter.detect(toRawSpan(span))).toBeGreaterThanOrEqual(0.6)
    expect(span.attributes['opencode.session.id']).toBe('ses_t3_adapter')
  })

  it('normalizes a mapper model span with exact tokens, session, and prompt', () => {
    const span = mapOpencodePayloadToOtlpSpan({
      traceId: 'trace-t3-adapter-model',
      spanId: 't3-adapter-normalize',
      parentSpanId: null,
      name: 'opencode.turn',
      kind: 'internal',
      sessionID: 'ses_t3_adapter',
      turnIndex: 3,
      agent: 'build',
      model: { providerID: 'anthropic', modelID: 'claude-opus-4-6' },
      prompt_text: 'T3 adapter prompt',
      tokens: {
        input_tokens: 120,
        output_tokens: 45,
        cache_read_tokens: 300,
        cache_creation_tokens: 40,
        reasoning_tokens: 12,
      },
      startTimeUnixNano: '1720000000000000000',
      endTimeUnixNano: '1720000000042000000',
    })
    const record = opencodeAdapter.normalize(toRawSpan(span))

    expect(record.harness).toBe('opencode')
    expect(record.op).toBe('llm.invoke')
    expect(record.sessionId).toBe('ses_t3_adapter')
    expect(record.tokens.freshInput).toBe(120)
    expect(record.tokens.cacheRead).toBe(300)
    expect(record.tokens.cacheCreation).toBe(40)
    expect(record.tokens.output).toBe(45)
    expect(record.tokens.reasoning).toBe(12)
    expect(record.tokens.reportedInput).toBe(460)
    expect(record.content.conversation_history).toContain('T3 adapter prompt')
    expect(opencodeAdapter.validate(record)).toBeUndefined()
  })

  it('lets the tool marker win when a mapper span carries both model and tool', () => {
    const attributes = mapOpencodePayloadToAttributes({
      traceId: 'trace-t3-adapter-both',
      spanId: 't3-adapter-both',
      parentSpanId: null,
      sessionID: 'ses_t3_adapter',
      turnIndex: 3,
      agent: 'build',
      model: { providerID: 'anthropic', modelID: 'claude-opus-4-6' },
      tool: 'read',
      callID: 'call_t3_both',
      tokens: { input_tokens: 10, output_tokens: 5 },
      startTimeUnixNano: '1720000000000000000',
      endTimeUnixNano: '1720000000010000000',
    })
    const record = opencodeAdapter.normalize(
      rawSpan({ spanId: 't3-adapter-both', attributes }),
    )

    expect(record.op).toBe('tool.invoke')
    expect(opencodeAdapter.validate(record)).toBeUndefined()
  })

  it('keeps a mapper app span unspecified so ingest quarantines it as non-model', () => {
    const span = mapOpencodePayloadToOtlpSpan({
      traceId: 'trace-t3-adapter-app',
      spanId: 't3-adapter-app',
      parentSpanId: null,
      name: 'Session.load',
      kind: 'internal',
      sessionID: 'synthetic-session-load-id',
      startTimeUnixNano: '1720000000000000000',
      endTimeUnixNano: '1720000000001000000',
    })
    const record = opencodeAdapter.normalize(toRawSpan(span))

    expect(record.op).toBe('unspecified')
    expect(opencodeAdapter.validate(record)).toBeUndefined()
  })

  it('preserves a mapper error status on the decoded span for the receiver seam', () => {
    const span = mapOpencodePayloadToOtlpSpan({
      traceId: 'trace-t3-adapter-error',
      spanId: 't3-adapter-error',
      parentSpanId: null,
      sessionID: 'ses_t3_adapter',
      model: { providerID: 'anthropic', modelID: 'claude-opus-4-6' },
      tokens: { input_tokens: 120, output_tokens: 45 },
      errors: { type: 'ProviderError', message: 'upstream 500', status: 'error' },
      startTimeUnixNano: '1720000000000000000',
      endTimeUnixNano: '1720000000042000000',
    })

    expect(span.status.code).toBe('error')
    expect(String(span.status.message ?? '')).toMatch(/upstream 500/)
  })
})
