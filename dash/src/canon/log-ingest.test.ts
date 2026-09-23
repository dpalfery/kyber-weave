// Behavioural contract for ADR 0009 decisions 1–3. Logs enrich one existing
// span-shaped record; they never establish a competing record population.
import { describe, expect, it } from 'vitest'

import { ingestLogBatch, type OtlpLog } from './log-ingest.js'
import { CanonStore } from './store.js'
import type { CanonicalRecord } from './types.js'
import { ingestBatch } from './ingest.js'
import { decodeOtlpLogJson, type OtlpSpan } from '../otel/receiver.js'

const TRACE_ID = '0af7651916cd43dd8448eb211c80319c'
const MODEL_SPAN_ID = 'b7ad6b7169203331'
const OTHER_SPAN_ID = '5b8efff798038103'
const SESSION_ID = 'session-synthetic-1'
const AT = '2026-09-04T10:00:00.000Z'

function modelRecord(overrides: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return {
    spanId: MODEL_SPAN_ID,
    traceId: TRACE_ID,
    parentSpanId: null,
    source: 'synthetic-test',
    harness: 'claude-code',
    sessionId: SESSION_ID,
    name: 'llm_request',
    op: 'llm.invoke',
    kind: 'client',
    timestamp: AT,
    durationMs: 100,
    status: 'ok',
    tokens: {
      freshInput: 10,
      cacheRead: 0,
      cacheCreation: 0,
      output: 5,
      reportedInput: 10,
      reportedOutput: 5,
    },
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
    ...overrides,
  }
}

function log(overrides: Partial<OtlpLog> = {}): OtlpLog {
  return {
    logId: 'log-synthetic-1',
    traceId: TRACE_ID,
    spanId: MODEL_SPAN_ID,
    sessionId: SESSION_ID,
    timestamp: AT,
    body: 'synthetic log enrichment',
    attributes: { 'synthetic.content': 'attached' },
    resource: { 'service.name': 'synthetic-test' },
    scope: { name: 'synthetic.logger' },
    ...overrides,
  }
}

function correlatableSpan(): OtlpSpan {
  return {
    traceId: TRACE_ID,
    spanId: MODEL_SPAN_ID,
    parentSpanId: null,
    name: 'llm_request',
    kind: 'client',
    startTimeUnixNano: '1756980000000000000',
    endTimeUnixNano: '1756980000100000000',
    timestamp: AT,
    durationMs: 100,
    status: { code: 'ok' },
    resource: { 'service.name': 'synthetic-test' },
    attributes: {
      'claude.deployment_mode': '1p',
      input_tokens: 10,
      output_tokens: 5,
      session_id: SESSION_ID,
    },
    scope: {},
  }
}

describe('ingestLogBatch (ADR 0009)', () => {
  it('uses the exact trace and span identity before any session-time fallback', () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([
      modelRecord(),
      modelRecord({
        spanId: OTHER_SPAN_ID,
        traceId: 'different-trace',
        timestamp: '2026-09-04T10:00:00.001Z',
      }),
    ])

    const outcome = ingestLogBatch([log()], store)

    expect(outcome).toEqual({ enriched: 1, pending: 0, quarantined: 0 })
    expect(store.count()).toBe(2)
    expect(store.get(MODEL_SPAN_ID)?.content).toMatchObject({ 'synthetic.content': 'attached' })
    expect(store.get(OTHER_SPAN_ID)?.content).toEqual({})
    store.close()
  })

  it('falls back to the same session inside the bounded timestamp window', () => {
    const store = new CanonStore(':memory:')
    store.upsert(modelRecord({ traceId: 'different-trace', spanId: OTHER_SPAN_ID }))

    const outcome = ingestLogBatch([log({ traceId: null, spanId: null })], store)

    expect(outcome).toEqual({ enriched: 1, pending: 0, quarantined: 0 })
    expect(store.count()).toBe(1)
    expect(store.get(OTHER_SPAN_ID)?.content).toMatchObject({ 'synthetic.content': 'attached' })
    store.close()
  })

  it('does not use a same-session record outside the bounded timestamp window', () => {
    const store = new CanonStore(':memory:')
    store.upsert(modelRecord({ spanId: OTHER_SPAN_ID, timestamp: '2026-09-04T10:10:00.000Z' }))

    const outcome = ingestLogBatch([log({ traceId: null, spanId: null })], store)

    expect(outcome).toEqual({ enriched: 0, pending: 0, quarantined: 1 })
    expect(store.count()).toBe(1)
    expect(store.get(OTHER_SPAN_ID)?.content).toEqual({})
    expect(store.getQuarantinedLog('log-synthetic-1')).toMatchObject({
      reason: expect.stringMatching(/correlat/i),
    })
    store.close()
  })

  it('enriches without changing model counters and duplicate delivery is idempotent', () => {
    const store = new CanonStore(':memory:')
    const original = modelRecord()
    store.upsert(original)

    expect(ingestLogBatch([log()], store)).toEqual({ enriched: 1, pending: 0, quarantined: 0 })
    expect(ingestLogBatch([log()], store)).toEqual({ enriched: 0, pending: 0, quarantined: 0 })

    expect(store.count()).toBe(1)
    expect(store.get(MODEL_SPAN_ID)?.tokens).toEqual(original.tokens)
    expect(store.get(MODEL_SPAN_ID)?.content).toMatchObject({ 'synthetic.content': 'attached' })
    store.close()
  })

  it('enriches the matching Claude request span with parsed API-body parts', () => {
    const store = new CanonStore(':memory:')
    const original = modelRecord()
    const request = {
      system: [{ type: 'text', text: 'Synthetic Claude system prompt.' }],
      tools: [
        {
          name: 'read_synthetic_file',
          server: 'synthetic-mcp-server',
          description: 'Read synthetic fixture content.',
          input_schema: {
            type: 'object',
            properties: { path: { type: 'string' } },
            required: ['path'],
          },
        },
      ],
    }
    store.upsert(original)

    expect(
      ingestLogBatch([
        log({
          attributes: { 'claude_code.api_request_body': JSON.stringify(request) },
        }),
      ], store),
    ).toEqual({ enriched: 1, pending: 0, quarantined: 0 })

    const enriched = store.get(MODEL_SPAN_ID)
    expect(store.count()).toBe(1)
    expect(enriched?.tokens).toEqual(original.tokens)
    expect(enriched?.parts).toEqual([
      { part: 'system_prompt', text: 'Synthetic Claude system prompt.' },
      {
        part: 'tool_definitions',
        text: JSON.stringify(request.tools[0]),
        server: 'synthetic-mcp-server',
      },
    ])
    expect(enriched?.content).toEqual({
      system_prompt: 'Synthetic Claude system prompt.',
      tool_definitions: JSON.stringify(request.tools[0]),
    })
    store.close()
  })

  it('does not apply Claude API-body parts to a correlated non-Claude request', () => {
    const store = new CanonStore(':memory:')
    const original = modelRecord({
      harness: 'copilot',
      name: 'tool_call',
      op: 'tool.invoke',
      content: { conversation_history: 'Existing synthetic content.' },
      parts: [{ part: 'conversation_history', text: 'Existing synthetic content.' }],
    })
    store.upsert(original)

    expect(
      ingestLogBatch([
        log({
          attributes: {
            'claude_code.api_request_body': JSON.stringify({
              system: 'Synthetic Claude system prompt that must not replace content.',
            }),
          },
        }),
      ], store),
    ).toEqual({ enriched: 1, pending: 0, quarantined: 0 })

    const enriched = store.get(MODEL_SPAN_ID)
    expect(enriched?.content).toMatchObject(original.content)
    expect(enriched?.content.system_prompt).toBeUndefined()
    expect(enriched?.parts).toEqual(original.parts)
    store.close()
  })

  it('keeps a log before its span auditable and enriches the span when it arrives', () => {
    const store = new CanonStore(':memory:')

    expect(ingestLogBatch([log()], store)).toEqual({ enriched: 0, pending: 1, quarantined: 0 })
    expect(store.count()).toBe(0)
    expect(store.getPendingLogs()).toEqual([expect.objectContaining({ logId: 'log-synthetic-1' })])

    ingestBatch([correlatableSpan()], store)

    expect(store.count()).toBe(1)
    expect(store.get(MODEL_SPAN_ID)?.content).toMatchObject({ 'synthetic.content': 'attached' })
    expect(store.getPendingLogs()).toEqual([])
    store.close()
  })

  it('quarantines an unresolved log and never creates a parallel canonical record', () => {
    const store = new CanonStore(':memory:')
    const unresolved = log({
      logId: 'log-unresolved',
      traceId: null,
      spanId: null,
      sessionId: 'no-such-session',
    })

    expect(ingestLogBatch([unresolved], store)).toEqual({ enriched: 0, pending: 0, quarantined: 1 })
    expect(store.count()).toBe(0)
    expect(store.getQuarantinedLog('log-unresolved')).toMatchObject({
      logId: 'log-unresolved',
      reason: expect.stringMatching(/correlat/i),
    })
    expect(store.quarantinedLogCount()).toBe(1)
    store.close()
  })

  it('expires an identified pending log into quarantine and counts it', () => {
    const store = new CanonStore(':memory:')
    const now = 1_000
    const identified = log({ logId: 'log-expired' })

    expect(ingestLogBatch([identified], store, { now: () => now, pendingTtlMs: 100 })).toEqual({
      enriched: 0,
      pending: 1,
      quarantined: 0,
    })
    expect(ingestLogBatch([], store, { now: () => now + 100, pendingTtlMs: 100 })).toEqual({
      enriched: 0,
      pending: 0,
      quarantined: 1,
    })
    expect(store.getPendingLogs()).toEqual([])
    expect(store.getQuarantinedLog('log-expired')).toMatchObject({
      logId: 'log-expired',
      reason: expect.stringMatching(/expir/i),
    })
    expect(store.quarantinedLogCount()).toBe(1)
    store.close()
  })

  it('enriches both records when the same eventName repeats on different spans', () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([
      modelRecord(),
      modelRecord({ spanId: OTHER_SPAN_ID, timestamp: '2026-09-04T10:00:01.000Z' }),
    ])

    const logs = decodeOtlpLogJson(
      JSON.stringify({
        resourceLogs: [
          {
            scopeLogs: [
              {
                logRecords: [
                  {
                    timeUnixNano: '1756980000000000000',
                    traceId: TRACE_ID,
                    spanId: MODEL_SPAN_ID,
                    eventName: 'claude_code.api_request_body',
                    body: { stringValue: 'first-turn-body' },
                    attributes: [{ key: 'session_id', value: { stringValue: SESSION_ID } }],
                  },
                  {
                    timeUnixNano: '1756980001000000000',
                    traceId: TRACE_ID,
                    spanId: OTHER_SPAN_ID,
                    eventName: 'claude_code.api_request_body',
                    body: { stringValue: 'second-turn-body' },
                    attributes: [{ key: 'session_id', value: { stringValue: SESSION_ID } }],
                  },
                ],
              },
            ],
          },
        ],
      }),
    )

    expect(logs).toHaveLength(2)
    expect(logs[0]?.logId).not.toBe(logs[1]?.logId)
    expect(logs[0]?.logId).not.toBe('claude_code.api_request_body')
    expect(ingestLogBatch(logs, store)).toEqual({ enriched: 2, pending: 0, quarantined: 0 })
    expect(store.get(MODEL_SPAN_ID)?.content).toMatchObject({ log_body: 'first-turn-body' })
    expect(store.get(OTHER_SPAN_ID)?.content).toMatchObject({ log_body: 'second-turn-body' })
    store.close()
  })
})
