// Behavioural contract for ADR 0009 decisions 1–3. Logs enrich one existing
// span-shaped record; they never establish a competing record population.
import { describe, expect, it } from 'vitest'

import { ingestLogBatch, reconcileExpiredPending, type OtlpLog } from './log-ingest.js'
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
    name: 'claude_code.llm_request',
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
    name: 'claude_code.llm_request',
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

/**
 * A Claude Code interaction span — the turn's only prompt carrier (B2). It
 * carries no counters, so ingest quarantines it as a non-model span and
 * parks its `user_prompt` in `pending_logs` for the settle pass to deliver.
 */
function interactionSpan(): OtlpSpan {
  return {
    traceId: TRACE_ID,
    spanId: 'interaction-synthetic-1',
    parentSpanId: null,
    name: 'claude_code.interaction',
    kind: 'internal',
    startTimeUnixNano: '1756980000000000000',
    endTimeUnixNano: '1756980000050000000',
    timestamp: AT,
    durationMs: 50,
    status: { code: 'ok' },
    resource: { 'service.name': 'synthetic-test' },
    attributes: {
      'claude.deployment_mode': '1p',
      'span.type': 'interaction',
      user_prompt: 'Synthetic interaction prompt.',
    },
    scope: {},
  }
}


/** The log-body event name Claude Code emits for an API request body (B3). */
const CLAUDE_EVENT_NAME = 'claude_code.api_request_body'

/**
 * The api_request_body log shape Claude Code actually emits (B3): the event
 * name is the log body *and* `attributes['event.name']`, and the request JSON
 * is attribute `body`. The `attributes['claude_code.api_request_body']` key
 * these fixtures used to carry is never emitted by any exporter.
 */
function claudeRequestBodyLog(
  attributes: Record<string, unknown>,
  overrides: Partial<OtlpLog> = {},
): OtlpLog {
  return log({
    logId: 'log-claude-request-body',
    body: CLAUDE_EVENT_NAME,
    attributes: { 'event.name': 'api_request_body', ...attributes },
    ...overrides,
  })
}

/** The reasons a record declares for the metrics it cannot measure. */
function measurabilityReasons(record: CanonicalRecord | undefined): string[] {
  const measurability = record?.measurability
  if (measurability === undefined) return []
  return Object.values(measurability).flatMap((availability) =>
    typeof availability === 'object' ? [availability.reason] : [],
  )
}

/**
 * The harness tag a stored log row carries, read as the property the contract
 * names — `harness: 'claude-code'` — and never as a substring of the
 * serialized row, which a scope or resource string could satisfy by accident.
 * The getter may surface the column on the row or the stored payload may carry
 * it, so both shapes are read.
 */
function harnessTag(row: unknown): unknown {
  if (row === null || typeof row !== 'object') return undefined
  const entry = row as { harness?: unknown; log?: unknown }
  if (entry.harness !== undefined) return entry.harness
  const payload = entry.log
  if (payload !== null && typeof payload === 'object') {
    return (payload as { harness?: unknown }).harness
  }
  return undefined
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
      ingestLogBatch([claudeRequestBodyLog({ body: JSON.stringify(request) })], store),
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

  it('recognises the request body from the log-body event name alone', () => {
    const store = new CanonStore(':memory:')
    const original = modelRecord()
    const request = { system: [{ type: 'text', text: 'Synthetic Claude system prompt.' }] }
    store.upsert(original)

    // B3 recognises the event by `attributes['event.name']` OR by the log
    // body; this fixture carries only the body signal.
    expect(
      ingestLogBatch(
        [log({ body: CLAUDE_EVENT_NAME, attributes: { body: JSON.stringify(request) } })],
        store,
      ),
    ).toEqual({ enriched: 1, pending: 0, quarantined: 0 })

    const enriched = store.get(MODEL_SPAN_ID)
    expect(enriched?.tokens).toEqual(original.tokens)
    expect(enriched?.parts).toEqual([
      { part: 'system_prompt', text: 'Synthetic Claude system prompt.' },
    ])
    store.close()
  })

  it('gives no parts and names the content limit when the inline body was truncated', () => {
    const store = new CanonStore(':memory:')
    const original = modelRecord()
    store.upsert(original)

    expect(
      ingestLogBatch(
        [
          claudeRequestBodyLog({
            body: '{"system":[{"type":"text","text":"cut off mid-request',
            body_truncated: 'true',
          }),
        ],
        store,
      ),
    ).toEqual({ enriched: 1, pending: 0, quarantined: 0 })

    const enriched = store.get(MODEL_SPAN_ID)
    expect(enriched?.parts ?? []).toEqual([])
    expect(enriched?.tokens).toEqual(original.tokens)
    expect(measurabilityReasons(enriched)).toContainEqual(
      expect.stringMatching(/CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH/),
    )
    store.close()
  })

  it('gives no parts and names body_ref when the body is only a file reference', () => {
    const store = new CanonStore(':memory:')
    const original = modelRecord()
    store.upsert(original)

    expect(
      ingestLogBatch(
        [claudeRequestBodyLog({ body_ref: '/tmp/synthetic-claude-body.json' })],
        store,
      ),
    ).toEqual({ enriched: 1, pending: 0, quarantined: 0 })

    const enriched = store.get(MODEL_SPAN_ID)
    expect(enriched?.parts ?? []).toEqual([])
    expect(enriched?.content.system_prompt).toBeUndefined()
    expect(measurabilityReasons(enriched)).toContainEqual(expect.stringMatching(/body_ref/i))
    store.close()
  })

  it('caps the body_ref echo a failure reason carries', () => {
    // The reference is sender-controlled and the reason is persisted to
    // measurability_json and served on the API: echoing it verbatim would let
    // a path string of unbounded length into the store and onto the wire.
    const store = new CanonStore(':memory:')
    store.upsert(modelRecord())

    expect(
      ingestLogBatch(
        [claudeRequestBodyLog({ body_ref: `/tmp/${'x'.repeat(10_000)}/body.json` })],
        store,
      ),
    ).toEqual({ enriched: 1, pending: 0, quarantined: 0 })

    const enriched = store.get(MODEL_SPAN_ID)
    expect(measurabilityReasons(enriched)).toContainEqual(expect.stringMatching(/body_ref/i))
    expect(measurabilityReasons(enriched).every((reason) => reason.length < 1_000)).toBe(true)
    store.close()
  })

  it('never lets the session-time fallback land on a tool.invoke record', () => {
    const store = new CanonStore(':memory:')
    const request = { system: [{ type: 'text', text: 'Synthetic Claude system prompt.' }] }
    store.upsertMany([
      modelRecord({ timestamp: '2026-09-04T10:00:02.000Z' }),
      modelRecord({
        spanId: OTHER_SPAN_ID,
        traceId: 'synthetic-tool-trace',
        name: 'claude_code.tool_call',
        op: 'tool.invoke',
      }),
    ])

    expect(
      ingestLogBatch(
        [claudeRequestBodyLog({ body: JSON.stringify(request) }, { traceId: null, spanId: null })],
        store,
      ),
    ).toEqual({ enriched: 1, pending: 0, quarantined: 0 })

    // The tool span is the nearest record by timestamp, so it is exactly what
    // an unfiltered fallback would enrich. Assert that it received nothing
    // *first*: this is the assertion that fails when the fallback lands on
    // tool.invoke, so GREEN cannot arrive by fixing an unrelated name gate.
    expect(store.get(OTHER_SPAN_ID)?.content).toEqual({})
    expect(store.get(OTHER_SPAN_ID)?.parts).toBeUndefined()
    // The llm.invoke span is 2s away but inside the window, and it is the only
    // legal fallback target.
    expect(store.get(MODEL_SPAN_ID)?.parts).toEqual([
      { part: 'system_prompt', text: 'Synthetic Claude system prompt.' },
    ])
    store.close()
  })

  it('quarantines an uncorrelated Claude body log rather than enriching a tool.invoke record', () => {
    const store = new CanonStore(':memory:')
    store.upsert(
      modelRecord({
        spanId: OTHER_SPAN_ID,
        traceId: 'synthetic-tool-trace',
        name: 'claude_code.tool_call',
        op: 'tool.invoke',
      }),
    )

    const outcome = ingestLogBatch(
      [
        claudeRequestBodyLog(
          { body: JSON.stringify({ system: 'Synthetic prompt that must not land here.' }) },
          { logId: 'log-tool-only', traceId: null, spanId: null },
        ),
      ],
      store,
    )

    // The only in-window record is the tool span, so an unfiltered fallback
    // lands there and merges the body into it. Assert that first: the failure
    // is then the fallback enriching tool.invoke, not the outcome count.
    expect(store.get(OTHER_SPAN_ID)?.content).toEqual({})
    expect(store.get(OTHER_SPAN_ID)?.parts).toBeUndefined()
    // With no legal target the log can only be quarantined.
    expect(outcome).toEqual({ enriched: 0, pending: 0, quarantined: 1 })
    store.close()
  })

  it('stores a Claude body log awaiting its span as pending, tagged with its harness', () => {
    const store = new CanonStore(':memory:')

    expect(
      ingestLogBatch(
        [claudeRequestBodyLog({ body: JSON.stringify({ system: 'Synthetic pending prompt.' }) })],
        store,
      ),
    ).toEqual({ enriched: 0, pending: 1, quarantined: 0 })

    const pending = store.getPendingLogs()
    expect(pending).toHaveLength(1)
    expect(harnessTag(pending[0]), 'the pending Claude log payload must carry its harness').toBe(
      'claude-code',
    )
    store.close()
  })

  it('tags a quarantined Claude log with its harness for wipe scoping', () => {
    const store = new CanonStore(':memory:')

    // Recognised as a Claude log by scope name alone (B3), with no correlation
    // identity and no session, so it can only be quarantined.
    expect(
      ingestLogBatch(
        [
          log({
            logId: 'log-claude-scope-only',
            traceId: null,
            spanId: null,
            sessionId: null,
            body: 'synthetic claude code event',
            attributes: {},
            scope: { name: 'com.anthropic.claude_code.events' },
          }),
        ],
        store,
      ),
    ).toEqual({ enriched: 0, pending: 0, quarantined: 1 })

    const quarantined = store.getQuarantinedLog('log-claude-scope-only')
    expect(quarantined?.reason).toMatch(/correlat/i)
    expect(harnessTag(quarantined), 'the quarantined Claude log row must carry its harness').toBe(
      'claude-code',
    )
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
      ingestLogBatch(
        [
          claudeRequestBodyLog({
            body: JSON.stringify({
              system: 'Synthetic Claude system prompt that must not replace content.',
            }),
          }),
        ],
        store,
      ),
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

  it('expires a parked interaction prompt without growing a quarantined-log copy (B8)', () => {
    const store = new CanonStore(':memory:')
    ingestBatch([interactionSpan()], store)
    // Parked: the interaction span is quarantined and no llm.invoke child
    // exists yet, so its prompt waits in pending_logs under the marker.
    expect(store.getPendingLogs()).toHaveLength(1)

    // The reconcile path the collector's timer runs must bound the wait
    // like any other pending row — otherwise a prompt whose children never
    // arrive lingers until the next span batch or wipe. Deletion only: the
    // interaction span's quarantine packet is already the prompt's evidence
    // copy, so nothing belongs in quarantined_logs (T6).
    reconcileExpiredPending(store, Date.now() + 5_001, 5_000)
    expect(store.getPendingLogs()).toEqual([])
    expect(store.quarantinedLogCount()).toBe(0)
    store.close()
  })

  it('declares a reason when a recognised api_request_body event carries no body', () => {
    const store = new CanonStore(':memory:')
    store.upsert(modelRecord())

    // The event names itself through `event.name` but emits neither a body
    // attribute, a body_ref, nor body_truncated — a recognised event with
    // nothing to parse must still say why its buckets stayed unmeasured
    // rather than fall through to a silent generic enrich.
    expect(ingestLogBatch([claudeRequestBodyLog({})], store)).toEqual({
      enriched: 1,
      pending: 0,
      quarantined: 0,
    })

    const enriched = store.get(MODEL_SPAN_ID)
    expect(enriched?.parts ?? []).toEqual([])
    expect(measurabilityReasons(enriched)).toContainEqual(expect.stringMatching(/no request body/i))
    store.close()
  })

  it('does not stamp the request-body failure reason on a scope-only Claude event', () => {
    const store = new CanonStore(':memory:')
    store.upsert(modelRecord())

    // Recognised as a Claude event by instrumentation scope alone (B3) —
    // it is not api_request_body, so a malformed `body` attribute must not
    // borrow the request body's truncation reason on the wrong event type;
    // the log enriches generically instead.
    expect(
      ingestLogBatch(
        [
          log({
            logId: 'log-claude-scope-malformed-body',
            body: 'claude_code.some_other_event',
            attributes: { body: '{not-json' },
            scope: { name: 'com.anthropic.claude_code.events' },
          }),
        ],
        store,
      ),
    ).toEqual({ enriched: 1, pending: 0, quarantined: 0 })

    const enriched = store.get(MODEL_SPAN_ID)
    expect(measurabilityReasons(enriched)).not.toContainEqual(
      expect.stringMatching(/CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH/),
    )
    expect(enriched?.content).toMatchObject({ body: '{not-json' })
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
