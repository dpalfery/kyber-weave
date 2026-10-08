// T4 RED: identity attributes must be stripped at receiver decode (Seam 3).
// Span, resource and log attributes carrying identity keys must not survive
// decode, so they never reach the stored `raw`. Session, conversation and
// `gen_ai.*` keys are retained.

import { describe, expect, it } from 'vitest'

import { decodeOtlpJson, decodeOtlpLogJson } from './receiver.js'

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
