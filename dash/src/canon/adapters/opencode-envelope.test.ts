// The OpenCode plugin envelope on contract 3 (decision D9): the version guard at ingest.
//
// A span the plugin emits names `opencode.contract_version`. Ingest claims it only
// when the version is "3". A span naming any other version, or none, quarantines
// under the named unknown-version reason and is never attributed to any harness.
// A contract bump also invalidates the checkpoints written under the old contract.
//
// The bare `opencode.session.id` shape is the pre-envelope `Session.load` span. It
// carries no turn, model, tool, or version marker, so it is not an envelope.
//
// RED: written before the envelope moved from contract "2" to "3". Against the
// current code, contract-3 spans quarantine under the unknown-version reason and
// contract-2 spans are still accepted and attributed.

import { describe, expect, it } from 'vitest'

import {
  OPENCODE_CONTRACT_VERSION,
  OPENCODE_KEYS,
  SPAN_NAMES,
  type SpanKind,
} from '../../capture/harnesses/opencode-plugin/contract.js'
import type { OtlpSpan } from '../../otel/receiver.js'
import { ADAPTERS, ingestBatch } from '../ingest.js'
import { checkpointIsReusable, type SourceCheckpoint } from '../source-state.js'
import { CanonStore } from '../store.js'
import {
  OPENCODE_CONTRACT_VERSION_KEY,
  UNKNOWN_OPENCODE_CONTRACT_VERSION_REASON,
  isOpencodeEnvelopeSpan,
  isSupportedOpencodeEnvelope,
} from './opencode-envelope.js'
import { AdapterRegistry } from './registry.js'
import { rawSpan } from './testing.js'

const SESSION = 'ses_contract3_envelope'

/** A version no contract will ever carry. */
const FUTURE_VERSION = '9999-future'

const SPAN_KINDS: readonly SpanKind[] = ['request', 'step', 'tool']

/** The keys each span kind carries beyond the contract identity. */
const KIND_KEYS: Record<SpanKind, Record<string, unknown>> = {
  request: {
    [OPENCODE_KEYS.agent]: 'build',
    [OPENCODE_KEYS.promptCount]: 1,
  },
  step: {
    [OPENCODE_KEYS.agent]: 'build',
    [OPENCODE_KEYS.modelId]: 'muse-spark-1.3-contributor',
    [OPENCODE_KEYS.modelProvider]: 'opencode-go',
    [OPENCODE_KEYS.usageInput]: 120,
    [OPENCODE_KEYS.usageOutput]: 45,
  },
  tool: {
    [OPENCODE_KEYS.toolName]: 'read',
    [OPENCODE_KEYS.toolCallId]: 'call_contract3_envelope',
  },
}

/** The canonical operation each contract-3 span kind claims. */
const CLAIMED_OP: readonly { kind: SpanKind; op: string }[] = [
  { kind: 'request', op: 'agent.invoke' },
  { kind: 'step', op: 'llm.invoke' },
  { kind: 'tool', op: 'tool.invoke' },
]

/** Attributes of one contract-3 span of the given kind. */
function envelopeAttributes(kind: SpanKind, version: string = OPENCODE_CONTRACT_VERSION): Record<string, unknown> {
  return {
    [OPENCODE_KEYS.contractVersion]: version,
    [OPENCODE_KEYS.sessionId]: SESSION,
    [OPENCODE_KEYS.spanKind]: kind,
    ...KIND_KEYS[kind],
  }
}

/** A copy of `attributes` without `key`. */
function without(attributes: Record<string, unknown>, key: string): Record<string, unknown> {
  const copy = { ...attributes }
  delete copy[key]
  return copy
}

/** A decoded OTLP span, built whole so no field is left to a cast. */
function otlpSpan(
  spanId: string,
  attributes: Record<string, unknown>,
  over: Partial<OtlpSpan> = {},
): OtlpSpan {
  return {
    traceId: `trace-${spanId}`,
    parentSpanId: null,
    name: SPAN_NAMES.step,
    kind: 'internal',
    startTimeUnixNano: '1793337355759000000',
    endTimeUnixNano: '1793337363002000000',
    timestamp: '2026-10-30T12:00:00.000Z',
    durationMs: 7243,
    status: { code: 'unset' },
    resource: { 'service.name': 'kyberdash-opencode-plugin' },
    scope: {},
    ...over,
    spanId,
    attributes,
  }
}

function checkpoint(overrides: Partial<SourceCheckpoint> = {}): SourceCheckpoint {
  return {
    harnessId: 'opencode',
    sourceKey: `session:${SESSION}`,
    providerId: 'opencode',
    parserId: 'opencode-plugin',
    parserContractVersion: OPENCODE_CONTRACT_VERSION,
    format: 'otlp',
    sourceRootLabel: 'opencode-plugin',
    revisionToken: 'rev:contract3',
    coveredFromUtc: '2026-10-30T00:00:00.000Z',
    coveredThroughUtc: '2026-10-30T12:00:00.000Z',
    lastAttemptUtc: '2026-10-30T12:00:00.000Z',
    lastSuccessUtc: '2026-10-30T12:00:01.000Z',
    lastStatus: 'ok',
    lastErrorCode: null,
    unitCount: 1,
    recordCount: 1,
    ...overrides,
  }
}

describe('opencode envelope contract version (T1, contract 3)', () => {
  it('names the version under the opencode namespace, as the contract key does', () => {
    expect(OPENCODE_CONTRACT_VERSION_KEY).toBe(OPENCODE_KEYS.contractVersion)
  })

  it('supports contract "3" and no other version', () => {
    expect(OPENCODE_CONTRACT_VERSION).toBe('3')
    expect(isSupportedOpencodeEnvelope(rawSpan({ spanId: 'supported', attributes: envelopeAttributes('step') }))).toBe(
      true,
    )
    for (const version of ['2', '1', FUTURE_VERSION]) {
      expect(
        isSupportedOpencodeEnvelope(
          rawSpan({ spanId: `unsupported-${version}`, attributes: envelopeAttributes('step', version) }),
        ),
        version,
      ).toBe(false)
    }
    expect(
      isSupportedOpencodeEnvelope(
        rawSpan({ spanId: 'unsupported-missing', attributes: without(envelopeAttributes('step'), OPENCODE_KEYS.contractVersion) }),
      ),
    ).toBe(false)
  })

  it('recognises a versioned contract-3 span as envelope, and leaves the bare session id alone', () => {
    for (const kind of SPAN_KINDS) {
      expect(isOpencodeEnvelopeSpan(rawSpan({ spanId: `envelope-${kind}`, attributes: envelopeAttributes(kind) })), kind).toBe(
        true,
      )
    }
    // The pre-envelope `Session.load` shape carries only a session id: not an envelope.
    expect(
      isOpencodeEnvelopeSpan(
        rawSpan({ spanId: 'legacy-session-only', attributes: { 'opencode.session.id': 'synthetic-session-load-id' } }),
      ),
    ).toBe(false)
    expect(isOpencodeEnvelopeSpan(rawSpan({ spanId: 'empty', attributes: {} }))).toBe(false)
  })

  it('treats a contract-3 span with its version removed as envelope, so it quarantines by version', () => {
    const attributes = without(envelopeAttributes('step'), OPENCODE_KEYS.contractVersion)
    expect(isOpencodeEnvelopeSpan(rawSpan({ spanId: 'envelope-no-version', attributes }))).toBe(true)
  })

  it('does not mistake opencode.hookyMcHookface for the opencode.hook marker', () => {
    expect(
      isOpencodeEnvelopeSpan(rawSpan({ spanId: 'hook-exact', attributes: { 'opencode.hook': 'turn.start' } })),
    ).toBe(true)
    expect(
      isOpencodeEnvelopeSpan(rawSpan({ spanId: 'hook-child', attributes: { 'opencode.hook.name': 'turn.start' } })),
    ).toBe(true)
    expect(
      isOpencodeEnvelopeSpan(
        rawSpan({ spanId: 'hook-lookalike', attributes: { 'opencode.hookyMcHookface': 'turn.start' } }),
      ),
    ).toBe(false)
  })
})

describe('ingestBatch — opencode envelope on contract 3 (T1)', () => {
  it.each(CLAIMED_OP)('claims a contract-3 $kind span as an opencode $op record', ({ kind, op }) => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch(
      [otlpSpan(`env-${kind}`, envelopeAttributes(kind), { name: SPAN_NAMES[kind] })],
      store,
    )

    expect(outcome).toEqual({ accepted: 1, quarantined: 0, rejected: 0 })
    const record = store.get(`env-${kind}`)
    expect(record?.harness).toBe('opencode')
    expect(record?.op).toBe(op)
    expect(record?.sessionId).toBe(SESSION)
    store.close()
  })

  it.each([
    { label: 'contract 2', version: '2' },
    { label: 'an unknown version', version: FUTURE_VERSION },
  ])('quarantines a span naming $label under the unknown-version reason, and never attributes it', ({ version }) => {
    const spanId = `env-${version}`
    const attributes = envelopeAttributes('step', version)
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch([otlpSpan(spanId, attributes)], store)

    expect(outcome).toEqual({ accepted: 0, quarantined: 1, rejected: 0 })
    expect(store.get(spanId)).toBeUndefined()
    expect(store.getQuarantine(spanId)?.reason).toBe(UNKNOWN_OPENCODE_CONTRACT_VERSION_REASON)
    store.close()

    const attributed = new AdapterRegistry(ADAPTERS).attribute([
      rawSpan({ spanId, source: 'opencode', traceId: `trace-${spanId}`, attributes }),
    ])
    expect(attributed.get(spanId)).toBeUndefined()
  })

  it('quarantines a span with no contract version rather than claiming it', () => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch(
      [otlpSpan('env-missing', without(envelopeAttributes('step'), OPENCODE_KEYS.contractVersion))],
      store,
    )

    expect(outcome).toEqual({ accepted: 0, quarantined: 1, rejected: 0 })
    expect(store.get('env-missing')).toBeUndefined()
    expect(store.getQuarantine('env-missing')?.reason).toBe(UNKNOWN_OPENCODE_CONTRACT_VERSION_REASON)
    store.close()
  })

  it('accounts every row of a mixed batch in the outcome', () => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch(
      [
        otlpSpan('mix-request', envelopeAttributes('request'), { name: SPAN_NAMES.request }),
        otlpSpan('mix-step', envelopeAttributes('step')),
        otlpSpan('mix-tool', envelopeAttributes('tool'), { name: SPAN_NAMES.tool }),
        otlpSpan('mix-contract-2', envelopeAttributes('step', '2')),
        otlpSpan('mix-unknown', envelopeAttributes('step', FUTURE_VERSION)),
        otlpSpan('mix-missing', without(envelopeAttributes('step'), OPENCODE_KEYS.contractVersion)),
      ],
      store,
    )

    expect(outcome).toEqual({ accepted: 3, quarantined: 3, rejected: 0 })
    store.close()
  })
})

describe('opencode contract bump invalidates stale checkpoints (T1)', () => {
  it('leaves a checkpoint written under a stale contract unreusable under the current one', () => {
    const request = { revisionToken: 'rev:contract3', parserContractVersion: OPENCODE_CONTRACT_VERSION }
    expect(checkpointIsReusable(checkpoint(), request)).toBe(true)
    expect(
      checkpointIsReusable(checkpoint({ parserContractVersion: `${OPENCODE_CONTRACT_VERSION}-stale` }), request),
    ).toBe(false)
  })

  it('leaves a checkpoint written under contract 2 unreusable under contract 3', () => {
    expect(
      checkpointIsReusable(checkpoint({ parserContractVersion: '2' }), {
        revisionToken: 'rev:contract3',
        parserContractVersion: OPENCODE_CONTRACT_VERSION,
      }),
    ).toBe(false)
  })

  it('invalidates the checkpoint on a contract bump while keeping rows and provenance', () => {
    const store = new CanonStore(':memory:')
    ingestBatch([otlpSpan('ckpt-step', envelopeAttributes('step'))], store)
    const record = store.get('ckpt-step')
    expect(record).toBeDefined()
    store.commitSourceUnit({
      records: record === undefined ? [] : [record],
      provenance: [
        {
          spanId: 'ckpt-step',
          harnessId: 'opencode',
          sourceKey: `session:${SESSION}`,
          nativeSessionId: SESSION,
          nativeRecordId: 'step-1',
          sourceRevision: 'rev:contract3',
          parserVersion: `${OPENCODE_CONTRACT_VERSION}-stale`,
          importedAtUtc: '2026-10-30T12:00:01.000Z',
          locationToken: 'opencode-plugin#step-1',
        },
      ],
      checkpoint: checkpoint({ parserContractVersion: `${OPENCODE_CONTRACT_VERSION}-stale` }),
    })
    store.invalidateSourceCheckpoint('opencode', `session:${SESSION}`, OPENCODE_CONTRACT_VERSION)

    const after = store.getSourceCheckpoint('opencode', `session:${SESSION}`)
    expect(after?.lastStatus).toBe('invalidated')
    expect(after?.parserContractVersion).toBe(OPENCODE_CONTRACT_VERSION)
    expect(
      checkpointIsReusable(after, { revisionToken: 'rev:contract3', parserContractVersion: OPENCODE_CONTRACT_VERSION }),
    ).toBe(false)
    expect(store.get('ckpt-step')?.spanId).toBe('ckpt-step')
    expect(store.getRecordProvenance('ckpt-step')?.spanId).toBe('ckpt-step')
    store.close()
  })
})
