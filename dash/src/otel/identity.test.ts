// T4 RED: identity attributes must be stripped at receiver decode (Seam 3).
// Span, resource and log attributes carrying identity keys must not survive
// decode, so they never reach the stored `raw`. Session, conversation and
// `gen_ai.*` keys are retained.

import { describe, expect, it } from 'vitest'

import { decodeOtlpJson, decodeOtlpLogJson, decodeOtlpLogProtobuf, decodeOtlpProtobuf } from './receiver.js'

const TRACE_ID = '0af7651916cd43dd8448eb211c80319c'
const SPAN_ID = 'b7ad6b7169203331'

// Synthetic identity keys (structure mirrors D4; values are invented).
const IDENTITY_KEYS = [
  'user.email',
  'user.id',
  'organization.id',
  'enduser.id',
  'org.id',
  'account.id',
  'acme.account_id',
  'acme.account_uuid',
  'user.account_id',
  'org.account_uuid',
]

const RETAINED_SPAN_ATTRIBUTES: Record<string, string> = {
  'session.id': 'synth-session-1',
  'conversation.id': 'synth-conversation-1',
  'gen_ai.request.model': 'synth-model',
}

function kv(key: string, value: string): unknown {
  return { key, value: { stringValue: value } }
}

function traceBody(
  spanAttributes: Record<string, string>,
  resourceAttributes: Record<string, string>,
): string {
  return JSON.stringify({
    resourceSpans: [
      {
        resource: {
          attributes: Object.entries(resourceAttributes).map(([key, value]) => kv(key, value)),
        },
        scopeSpans: [
          {
            scope: {},
            spans: [
              {
                traceId: TRACE_ID,
                spanId: SPAN_ID,
                name: 'agent.turn',
                kind: 1,
                startTimeUnixNano: '1756478400123456789',
                endTimeUnixNano: '1756478401373456789',
                attributes: Object.entries(spanAttributes).map(([key, value]) => kv(key, value)),
                status: { code: 1 },
              },
            ],
          },
        ],
      },
    ],
  })
}

function logBody(
  logAttributes: Record<string, string>,
  resourceAttributes: Record<string, string>,
): string {
  return JSON.stringify({
    resourceLogs: [
      {
        resource: {
          attributes: Object.entries(resourceAttributes).map(([key, value]) => kv(key, value)),
        },
        scopeLogs: [
          {
            scope: {},
            logRecords: [
              {
                timeUnixNano: '1756478400123456789',
                traceId: TRACE_ID,
                spanId: SPAN_ID,
                body: { stringValue: 'synth log body' },
                attributes: Object.entries(logAttributes).map(([key, value]) => kv(key, value)),
              },
            ],
          },
        ],
      },
    ],
  })
}

function identityAttributes(): Record<string, string> {
  return Object.fromEntries(IDENTITY_KEYS.map((key) => [key, `synth-${key}`]))
}

describe('receiver identity stripping (T4)', () => {
  it('strips identity keys from decoded span attributes and retains session/conversation/gen_ai', () => {
    const spans = decodeOtlpJson(traceBody({ ...identityAttributes(), ...RETAINED_SPAN_ATTRIBUTES }, {}))
    expect(spans).toHaveLength(1)
    for (const key of IDENTITY_KEYS) {
      expect(spans[0]?.attributes, `span attribute ${key}`).not.toHaveProperty(key)
    }
    for (const [key, value] of Object.entries(RETAINED_SPAN_ATTRIBUTES)) {
      expect(spans[0]?.attributes[key]).toBe(value)
    }
  })

  it('strips identity keys from decoded resource attributes', () => {
    const spans = decodeOtlpJson(traceBody({}, { ...identityAttributes(), 'service.name': 'synth-service' }))
    expect(spans).toHaveLength(1)
    for (const key of IDENTITY_KEYS) {
      expect(spans[0]?.resource, `resource attribute ${key}`).not.toHaveProperty(key)
    }
    expect(spans[0]?.resource['service.name']).toBe('synth-service')
  })

  it('strips identity keys from decoded log attributes and log resource attributes', () => {
    const logs = decodeOtlpLogJson(
      logBody({ ...identityAttributes(), 'session.id': 'synth-session-1' }, identityAttributes()),
    )
    expect(logs).toHaveLength(1)
    for (const key of IDENTITY_KEYS) {
      expect(logs[0]?.attributes, `log attribute ${key}`).not.toHaveProperty(key)
      expect(logs[0]?.resource, `log resource attribute ${key}`).not.toHaveProperty(key)
    }
    expect(logs[0]?.attributes['session.id']).toBe('synth-session-1')
  })
})

// --- local OTLP protobuf encoders (T4 rework 1) ---------------------------
// Minimal wire-format writers for the OTLP trace/log schema the receiver
// decodes (field numbers are documented in receiver.ts). Written fresh for
// this suite rather than imported from receiver.test.ts: these tests audit
// the protobuf decode path, so they must not share encoding code with the
// suite they audit.

function pbVarint(value: bigint): number[] {
  let rest = value
  const out: number[] = []
  for (;;) {
    const byte = Number(rest & 0x7fn)
    rest >>= 7n
    if (rest === 0n) {
      out.push(byte)
      return out
    }
    out.push(byte | 0x80)
  }
}

function pbTag(field: number, wire: number): number[] {
  return pbVarint(BigInt((field << 3) | wire))
}

function pbRawBytes(field: number, data: Uint8Array): number[] {
  const bytes = Array.from(data)
  return [...pbTag(field, 2), ...pbVarint(BigInt(bytes.length)), ...bytes]
}

function pbUtf8(field: number, value: string): number[] {
  return pbRawBytes(field, Buffer.from(value, 'utf8'))
}

function pbHex(field: number, hex: string): number[] {
  return pbRawBytes(field, Buffer.from(hex, 'hex'))
}

function pbFixed64(field: number, value: bigint): number[] {
  const out = pbTag(field, 1)
  for (let index = 0; index < 8; index++) {
    out.push(Number((value >> BigInt(8 * index)) & 0xffn))
  }
  return out
}

function pbVarintField(field: number, value: number): number[] {
  return [...pbTag(field, 0), ...pbVarint(BigInt(value))]
}

function pbMessage(field: number, content: number[]): number[] {
  return pbRawBytes(field, Uint8Array.from(content))
}

/** An `AnyValue` carrying a string (`string_value` = field 1). */
function pbStringValue(value: string): number[] {
  return pbUtf8(1, value)
}

/** A `KeyValue` pair with a string value. */
function pbKeyValue(key: string, value: string): number[] {
  return [...pbUtf8(1, key), ...pbMessage(2, pbStringValue(value))]
}

/** A repeated `KeyValue` attribute list carried on `field`. */
function pbAttributes(field: number, attributes: Record<string, string>): number[] {
  return Object.entries(attributes).flatMap(([key, value]) => pbMessage(field, pbKeyValue(key, value)))
}

const PB_START = 1756478400123456789n
const PB_END = 1756478401373456789n

/** Genuine OTLP protobuf bytes for one span plus its resource. */
function protobufTrace(
  spanAttributes: Record<string, string>,
  resourceAttributes: Record<string, string>,
): Uint8Array {
  // Resource { repeated KeyValue attributes = 1 } nests inside the
  // ResourceSpans resource message (field 1), not directly on it.
  const resource = pbAttributes(1, resourceAttributes)
  const span = [
    ...pbHex(1, TRACE_ID),
    ...pbHex(2, SPAN_ID),
    ...pbUtf8(5, 'agent.turn'),
    ...pbVarintField(6, 1), // SPAN_KIND_INTERNAL
    ...pbFixed64(7, PB_START),
    ...pbFixed64(8, PB_END),
    ...pbAttributes(9, spanAttributes),
  ]
  const scopeSpans = [...pbMessage(2, span)]
  return Uint8Array.from(pbMessage(1, [...pbMessage(1, resource), ...pbMessage(2, scopeSpans)]))
}

/** Genuine OTLP protobuf bytes for one log record plus its resource. */
function protobufLogs(
  logAttributes: Record<string, string>,
  resourceAttributes: Record<string, string>,
): Uint8Array {
  const resource = pbAttributes(1, resourceAttributes)
  const record = [
    ...pbFixed64(1, PB_START),
    ...pbMessage(5, pbStringValue('synth log body')),
    ...pbAttributes(6, logAttributes),
    ...pbHex(9, TRACE_ID),
    ...pbHex(10, SPAN_ID),
  ]
  const scopeLogs = [...pbMessage(2, record)]
  return Uint8Array.from(pbMessage(1, [...pbMessage(1, resource), ...pbMessage(2, scopeLogs)]))
}

describe('receiver protobuf identity stripping (T4 rework 1)', () => {
  it('strips identity keys from protobuf span attributes and resource', () => {
    const spans = decodeOtlpProtobuf(
      protobufTrace(
        { ...identityAttributes(), ...RETAINED_SPAN_ATTRIBUTES },
        { ...identityAttributes(), 'service.name': 'synth-service' },
      ),
    )
    expect(spans).toHaveLength(1)
    for (const key of IDENTITY_KEYS) {
      expect(spans[0]?.attributes, `protobuf span attribute ${key}`).not.toHaveProperty(key)
      expect(spans[0]?.resource, `protobuf resource attribute ${key}`).not.toHaveProperty(key)
    }
    for (const [key, value] of Object.entries(RETAINED_SPAN_ATTRIBUTES)) {
      expect(spans[0]?.attributes[key]).toBe(value)
    }
    expect(spans[0]?.resource['service.name']).toBe('synth-service')
  })

  it('strips identity keys from protobuf log attributes and log resource', () => {
    const logs = decodeOtlpLogProtobuf(
      protobufLogs(
        { ...identityAttributes(), 'session.id': 'synth-session-1' },
        { ...identityAttributes(), 'service.name': 'synth-service' },
      ),
    )
    expect(logs).toHaveLength(1)
    for (const key of IDENTITY_KEYS) {
      expect(logs[0]?.attributes, `protobuf log attribute ${key}`).not.toHaveProperty(key)
      expect(logs[0]?.resource, `protobuf log resource attribute ${key}`).not.toHaveProperty(key)
    }
    expect(logs[0]?.attributes['session.id']).toBe('synth-session-1')
    expect(logs[0]?.resource['service.name']).toBe('synth-service')
  })
})

// Correlation-adjacent keys that merely contain an identity principal
// elsewhere in the key: stripping them would lose turn/request correlation
// (T4 rework 1, item 2). Asserted through the JSON decode path.

const NEAR_MISS_RETAINED = [
  'gen_ai.session.id',
  'gen_ai.conversation.id',
  'pi.session.id',
  'copilot_chat.chat_session_id',
  'agent_id',
  'parent_agent_id',
  'request_id',
  'user.message.id',
  'user_prompt_id',
  'account.session.id',
]

describe('receiver near-miss retention (T4 rework 1)', () => {
  it('retains correlation keys that merely contain an identity principal', () => {
    const attributes = Object.fromEntries(NEAR_MISS_RETAINED.map((key) => [key, `synth-${key}`]))
    const spans = decodeOtlpJson(traceBody(attributes, {}))
    expect(spans).toHaveLength(1)
    for (const key of NEAR_MISS_RETAINED) {
      expect(spans[0]?.attributes[key], `retained ${key}`).toBe(`synth-${key}`)
    }
  })
})

// CamelCase identity shapes (T4 rework 1, item 3): per D4 ("and similar"),
// `userId` is the same key as `user.id`, so it is stripped; `sessionId` is
// the same key as `session.id`, so it is retained.

const CAMEL_IDENTITY_KEYS = [
  'userId',
  'orgId',
  'organizationId',
  'enduserId',
  'accountId',
  'userEmail',
]

describe('receiver camelCase identity shapes (T4 rework 1)', () => {
  it('strips camelCase identity keys through decode', () => {
    const attributes = Object.fromEntries(CAMEL_IDENTITY_KEYS.map((key) => [key, `synth-${key}`]))
    const spans = decodeOtlpJson(traceBody(attributes, {}))
    expect(spans).toHaveLength(1)
    for (const key of CAMEL_IDENTITY_KEYS) {
      expect(spans[0]?.attributes, `camelCase identity ${key}`).not.toHaveProperty(key)
    }
  })

  it('retains camelCase correlation keys through decode', () => {
    const spans = decodeOtlpJson(traceBody({ sessionId: 'synth-session-1', conversationId: 'synth-conversation-1' }, {}))
    expect(spans).toHaveLength(1)
    expect(spans[0]?.attributes['sessionId']).toBe('synth-session-1')
    expect(spans[0]?.attributes['conversationId']).toBe('synth-conversation-1')
  })
})
