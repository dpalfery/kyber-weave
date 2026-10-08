import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

import { dedupeTwinTurns } from './twin-dedupe.js'
import type { CanonicalRecord, ContentPart, TokenUsage } from './types.js'

// Real Claude Code shape: OTel `llm_request` carries `request_id` and a
// matching `gen_ai.response.id` (`req_…`); transcript synth rows carry the
// same API request id separately from `message.id` (`msg_…`). P2.0 joins on
// request id without disturbing the msg_-shaped id join or exact-counter path.

const FIXTURE_DIR = join(dirname(fileURLToPath(import.meta.url)), 'fixtures/claude-request-id')

type OtelFixture = {
  sessionId: string
  spanId: string
  request_id: string
  'gen_ai.response.id': string
  tokens: TokenUsage
  timestamp: string
}

type FileFixture = {
  sessionId: string
  spanId: string
  requestId: string
  turnId: string
  nativeRecordId: string
  tokens: TokenUsage
  timestamp: string
  parts: ContentPart[]
}

function loadJson<T>(name: string): T {
  return JSON.parse(readFileSync(join(FIXTURE_DIR, name), 'utf8')) as T
}

function otelFromFixture(spec: OtelFixture, over: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return {
    spanId: spec.spanId,
    traceId: 'a1b2c3d4e5f60718293a4b5c6d7e8f90',
    parentSpanId: null,
    source: 'claude-code',
    harness: 'claude-code',
    sessionId: spec.sessionId,
    name: 'llm_request',
    op: 'llm.invoke',
    kind: 'client',
    timestamp: spec.timestamp,
    durationMs: 900,
    status: 'ok',
    tokens: { ...spec.tokens },
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
    raw: {
      'session.id': spec.sessionId,
      request_id: spec.request_id,
      'gen_ai.response.id': spec['gen_ai.response.id'],
    },
    ...over,
  }
}

function fileFromFixture(spec: FileFixture, over: Partial<CanonicalRecord> = {}): CanonicalRecord {
  const content: CanonicalRecord['content'] = {}
  for (const part of spec.parts) {
    content[part.part] = content[part.part] === undefined ? part.text : `${content[part.part]}\n${part.text}`
  }
  return {
    spanId: spec.spanId,
    traceId: `synth:claude-code:${spec.sessionId}`,
    parentSpanId: null,
    source: 'codeburn/claude',
    harness: 'claude-code',
    sessionId: spec.sessionId,
    name: 'llm_request',
    op: 'llm.invoke',
    kind: 'client',
    timestamp: spec.timestamp,
    durationMs: 0,
    status: 'ok',
    tokens: { ...spec.tokens },
    content,
    parts: spec.parts,
    cost: { basis: 'unknown', status: 'no_rate' },
    raw: {
      requestId: spec.requestId,
      turnId: spec.turnId,
      provenance: {
        harnessId: 'claude-code',
        nativeSessionId: spec.sessionId,
        nativeRecordId: spec.nativeRecordId,
      },
    },
    ...over,
  }
}

describe('dedupeTwinTurns Claude request-id join (E1 / P2.0)', () => {
  const otelSpec = loadJson<OtelFixture>('otel-req-a.json')
  const fileSpec = loadJson<FileFixture>('file-req-a-msg-b.json')

  it('joins OTel request_id to file requestId with OTel counters and D10 content', () => {
    const out = dedupeTwinTurns([otelFromFixture(otelSpec), fileFromFixture(fileSpec)])

    expect(out).toHaveLength(1)
    expect(out[0]!.spanId).toBe(otelSpec.spanId)
    expect(out[0]!.tokens).toEqual(otelSpec.tokens)
    expect(out[0]!.parts).toEqual(fileSpec.parts)
    expect(out[0]!.content.conversation_history).toBe(
      'user: synthetic fixture question\nassistant: synthetic fixture answer',
    )
  })

  it('does not join when OTel request_id differs from file requestId', () => {
    const otel = otelFromFixture(otelSpec)
    const file = fileFromFixture(fileSpec, {
      raw: {
        requestId: 'req_C01Synthetic0003',
        turnId: fileSpec.turnId,
        provenance: {
          harnessId: 'claude-code',
          nativeSessionId: fileSpec.sessionId,
          nativeRecordId: fileSpec.nativeRecordId,
        },
      },
    })
    const out = dedupeTwinTurns([otel, file])

    expect(out).toEqual([otel, file])
  })

  it('still joins msg_-shaped gen_ai.response.id to message.id (native record id)', () => {
    const msgId = 'msg_msgShapedSynthetic01'
    const otel = otelFromFixture(otelSpec, {
      spanId: 'otel-msg-shaped',
      raw: {
        'session.id': otelSpec.sessionId,
        request_id: 'req_sidecarIgnoredForMsgJoin',
        'gen_ai.response.id': msgId,
      },
    })
    const file = fileFromFixture(fileSpec, {
      spanId: 'synth:msg-shaped',
      raw: {
        requestId: otelSpec.request_id,
        turnId: msgId,
        provenance: {
          harnessId: 'claude-code',
          nativeSessionId: fileSpec.sessionId,
          nativeRecordId: msgId,
        },
      },
    })
    const out = dedupeTwinTurns([otel, file])

    expect(out).toHaveLength(1)
    expect(out[0]!.spanId).toBe('otel-msg-shaped')
    expect(out[0]!.tokens).toEqual(otelSpec.tokens)
    expect(out[0]!.parts).toEqual(fileSpec.parts)
  })

  it('keeps exact-counter join when ids disagree and counters match (no mismatched-id veto)', () => {
    const shared = otelSpec.tokens
    const otel = otelFromFixture(otelSpec, {
      spanId: 'otel-exact-counter',
      tokens: { ...shared },
      raw: {
        'session.id': otelSpec.sessionId,
        request_id: 'req_exactCounterSynthetic',
        'gen_ai.response.id': 'req_exactCounterSynthetic',
      },
    })
    const file = fileFromFixture(fileSpec, {
      spanId: 'synth:exact-counter',
      tokens: { ...shared },
      raw: {
        turnId: 'msg_exactCounterSynthetic',
        provenance: {
          harnessId: 'claude-code',
          nativeSessionId: fileSpec.sessionId,
          nativeRecordId: 'msg_exactCounterSynthetic',
        },
      },
    })
    const out = dedupeTwinTurns([otel, file])

    expect(out).toHaveLength(1)
    expect(out[0]!.spanId).toBe('otel-exact-counter')
    expect(out[0]!.tokens).toEqual(shared)
    expect(out[0]!.parts).toEqual(fileSpec.parts)
  })

  it('fills only buckets the OTel row lacks when joining on request id (D10)', () => {
    const otelParts: ContentPart[] = [
      { part: 'system_prompt', text: 'otel system bucket' },
      { part: 'tool_definitions', text: '{"name":"otel_tool"}' },
    ]
    const otel = otelFromFixture(otelSpec, {
      parts: otelParts,
      content: {
        system_prompt: 'otel system bucket',
        tool_definitions: '{"name":"otel_tool"}',
      },
    })
    const out = dedupeTwinTurns([otel, fileFromFixture(fileSpec)])

    expect(out).toHaveLength(1)
    expect(out[0]!.parts).toEqual([
      ...otelParts,
      ...fileSpec.parts,
    ])
    expect(out[0]!.content).toEqual({
      system_prompt: 'otel system bucket',
      tool_definitions: '{"name":"otel_tool"}',
      conversation_history: 'user: synthetic fixture question\nassistant: synthetic fixture answer',
    })
  })
})
