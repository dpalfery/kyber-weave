// T4: opencode plugin turn mapping + log correlation (test-first RED).
//
// Turn records join spans; tool outcomes ride /v1/logs correlated by
// (trace_id, span_id) then session+bounds (ADR 0009 rule), idempotent replay;
// an unmatched log quarantines, never drops. Ready plan authority: Q1
// dash-CLI-managed, Q2 live OTLP, Q3 span-anchored. T1 envelope done, T3 span
// mapping done. Scope: plugin turn module wired to mapper/skeleton only; no
// receiver/transport/outbox, web, tray, resolver, or API change.

import { describe, expect, it } from 'vitest'

import { ingestBatch } from '../../../canon/ingest.js'
import { ingestLogBatch, reconcileExpiredPending } from '../../../canon/log-ingest.js'
import { CanonStore } from '../../../canon/store.js'
import {
  mapOpencodePayloadToOtlpSpan,
  type OpencodePluginSpanPayload,
} from './mapper.js'
import {
  OPENCODE_TOOL_RESULT_BODY,
  OPENCODE_TOOL_RESULT_SCOPE,
  findOpencodeTurnTarget,
  isOpencodeToolResultLog,
  mapOpencodeTurnToOtlpLog,
  opencodeTurnOutcome,
} from './turn.js'

const TRACE_ID = 'trace-t4-turn'
const TOOL_SPAN_ID = 'span-t4-tool'
const SESSION_ID = 'ses_t4_turn'
const AT_NANOS = '1720000000100000000'
const AT_ISO = new Date(1720000000100).toISOString()

const TOOL_SPAN_PAYLOAD: OpencodePluginSpanPayload = {
  traceId: TRACE_ID,
  spanId: TOOL_SPAN_ID,
  parentSpanId: 'span-t4-model',
  name: 'Tool.execute',
  kind: 'internal',
  sessionID: SESSION_ID,
  turnIndex: 7,
  agent: 'build',
  tool: 'read',
  callID: 'call_t4_tool_001',
  messageID: 'msg_t4_001',
  tokens: { input_tokens: 0, output_tokens: 0 },
  startTimeUnixNano: AT_NANOS,
  endTimeUnixNano: '1720000000115000000',
}

function outcomeLog() {
  return mapOpencodeTurnToOtlpLog({
    traceId: TRACE_ID,
    spanId: TOOL_SPAN_ID,
    sessionID: SESSION_ID,
    turnIndex: 7,
    tool: 'read',
    callID: 'call_t4_tool_001',
    messageID: 'msg_t4_001',
    agent: 'build',
    success: true,
    durationMs: 15,
    resultText: 'file contents: hello',
    resultSizeBytes: 19,
    timestampNanos: '1720000000116000000',
  })
}

describe('opencode turn outcome rides /v1/logs with its correlation identity', () => {
  it('stamps the tool_result body under the com.opencode scope with trace, span, and session', () => {
    const log = outcomeLog()

    expect(log.body).toBe(OPENCODE_TOOL_RESULT_BODY)
    expect(log.scope.name).toBe(OPENCODE_TOOL_RESULT_SCOPE)
    expect(log.traceId).toBe(TRACE_ID)
    expect(log.spanId).toBe(TOOL_SPAN_ID)
    expect(log.sessionId).toBe(SESSION_ID)
    expect(isOpencodeToolResultLog(log)).toBe(true)
  })

  it('carries the turn outcome and duration as structured log attributes', () => {
    const log = outcomeLog()
    const outcome = opencodeTurnOutcome(log)

    expect(outcome).toMatchObject({ success: true, durationMs: 15 })
    expect(log.attributes['tool_name']).toBe('read')
    expect(log.attributes['agent']).toBe('build')
    expect(log.attributes['success']).toBe(true)
    expect(log.attributes['duration_ms']).toBe(15)
    expect(log.attributes['tool_result_size_bytes']).toBe(19)
    expect(log.attributes['session.id']).toBe(SESSION_ID)
  })

  it('builds a stable log identity so replay is idempotent', () => {
    const first = outcomeLog()
    const second = outcomeLog()

    expect(second.logId).toBe(first.logId)
  })
})

describe('opencode turn log correlation joins the span-shaped record', () => {
  it('enriches exactly one span-shaped record and never inserts a parallel one', () => {
    const store = new CanonStore(':memory:')
    try {
      const span = mapOpencodePayloadToOtlpSpan(TOOL_SPAN_PAYLOAD)
      const ingested = ingestBatch([span], store)
      expect(ingested).toEqual({ accepted: 1, quarantined: 0, rejected: 0 })

      const log = outcomeLog()
      const outcome = ingestLogBatch([log], store)

      expect(outcome.enriched).toBe(1)
      expect(outcome.quarantined).toBe(0)
      const record = store.get(TOOL_SPAN_ID)
      expect(record?.harness).toBe('opencode')
      expect(record?.op).toBe('tool.invoke')
      // The correlated turn carries its outcome and duration.
      expect(record?.content).toMatchObject({
        tool_name: 'read',
        success: true,
        duration_ms: 15,
      })
      // Exactly one span-shaped record: the log enriched it, nothing else landed.
      expect(store.has(TOOL_SPAN_ID)).toBe(true)
      expect(store.get(log.logId)).toBeUndefined()
    } finally {
      store.close()
    }
  })

  it('replays idempotently with no duplicate enrichment', () => {
    const store = new CanonStore(':memory:')
    try {
      ingestBatch([mapOpencodePayloadToOtlpSpan(TOOL_SPAN_PAYLOAD)], store)
      const log = outcomeLog()

      const first = ingestLogBatch([log], store)
      const before = store.get(TOOL_SPAN_ID)?.content
      const second = ingestLogBatch([log], store)

      expect(first.enriched).toBe(1)
      expect(second.enriched).toBe(0)
      expect(second.quarantined).toBe(0)
      expect(store.get(TOOL_SPAN_ID)?.content).toEqual(before)
      expect(store.isLogEnriched(log.logId)).toBe(true)
    } finally {
      store.close()
    }
  })

  it('quarantines an unmatched log instead of dropping it', () => {
    const store = new CanonStore(':memory:')
    try {
      const log = mapOpencodeTurnToOtlpLog({
        traceId: 'trace-t4-unknown',
        spanId: 'span-t4-unknown',
        sessionID: SESSION_ID,
        tool: 'read',
        success: false,
        durationMs: 3,
        timestampNanos: '1720000000999000000',
      })

      const outcome = ingestLogBatch([log], store)
      expect(outcome.enriched).toBe(0)
      // An identified log waits for its span; expiry quarantines it with a reason.
      expect(outcome.pending).toBe(1)
      const quarantined = reconcileExpiredPending(store, Date.now() + 60_000, 5_000)
      expect(quarantined).toBe(1)
      expect(store.getQuarantinedLog(log.logId)?.reason).toMatch(/unmatched|correlate/i)
      expect(store.get('span-t4-unknown')).toBeUndefined()
    } finally {
      store.close()
    }
  })
})

describe('opencode turn session+bounds fallback (ADR 0009 rule)', () => {
  it('joins a session log to the nearest tool span inside the window', () => {
    const log = mapOpencodeTurnToOtlpLog({
      sessionID: SESSION_ID,
      tool: 'read',
      success: true,
      durationMs: 15,
      timestampNanos: AT_NANOS,
    })
    expect(log.traceId).toBeNull()
    expect(log.spanId).toBeNull()

    const target = findOpencodeTurnTarget(
      [
        {
          spanId: TOOL_SPAN_ID,
          traceId: TRACE_ID,
          sessionId: SESSION_ID,
          timestamp: AT_ISO,
          durationMs: 15,
          op: 'tool.invoke',
        },
      ],
      log,
    )

    expect(target?.spanId).toBe(TOOL_SPAN_ID)
  })

  it('leaves a session log unmatched outside the window', () => {
    const log = mapOpencodeTurnToOtlpLog({
      sessionID: SESSION_ID,
      tool: 'read',
      success: true,
      durationMs: 15,
      timestampNanos: '1720000010100000000',
    })

    const target = findOpencodeTurnTarget(
      [
        {
          spanId: TOOL_SPAN_ID,
          traceId: TRACE_ID,
          sessionId: SESSION_ID,
          timestamp: AT_ISO,
          durationMs: 15,
          op: 'tool.invoke',
        },
      ],
      log,
      5_000,
    )

    expect(target).toBeUndefined()
  })

  it('never throws on garbage timestamps, falling back to now', () => {
    const junkNanos = mapOpencodeTurnToOtlpLog({
      sessionID: SESSION_ID,
      tool: 'read',
      success: true,
      timestampNanos: 'not-a-timestamp',
    })
    expect(Number.isNaN(Date.parse(junkNanos.timestamp))).toBe(false)

    const junkIso = mapOpencodeTurnToOtlpLog({
      sessionID: SESSION_ID,
      tool: 'read',
      success: true,
      timestamp: 'garbage-iso',
    })
    expect(Number.isNaN(Date.parse(junkIso.timestamp))).toBe(false)
  })
})
