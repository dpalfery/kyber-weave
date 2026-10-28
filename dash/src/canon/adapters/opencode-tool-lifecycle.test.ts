// B1: the adapter claims usage-free tool-lifecycle envelope spans.
//
// A `tool.execute.before/after` hook maps (via the B1 stamper) onto an
// envelope span carrying the tool marker, the hook marker, and —
// deliberately — no usage counters: native OTel owns usage, so the plugin
// never emits it (double-count guard). These spans must still vote
// `tool.invoke` with the envelope session, zero fabricated counters, and
// not-measurable cache classes.

import { describe, expect, it } from 'vitest'

import { ingestBatch } from '../ingest.js'
import { CanonStore } from '../store.js'
import type { OtlpSpan } from '../../otel/receiver.js'
import { opencodeAdapter } from './opencode.js'
import {
  OPENCODE_CONTRACT_VERSION,
  OPENCODE_CONTRACT_VERSION_KEY,
} from './opencode-envelope.js'
import { rawSpan } from './testing.js'

const SESSION_ID = 'ses_b1_tool'

/** Envelope attributes exactly as the B1 stamper emits them for a tool hook. */
function toolHookAttributes(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    'opencode.session.id': SESSION_ID,
    [OPENCODE_CONTRACT_VERSION_KEY]: OPENCODE_CONTRACT_VERSION,
    'opencode.tool.name': 'read',
    'opencode.tool.call.id': 'call_b1_001',
    'opencode.hook': 'tool.execute.after',
    ...over,
  }
}

function otlpSpan(attributes: Record<string, unknown>, over: Partial<OtlpSpan> = {}): OtlpSpan {
  return {
    traceId: `trace-${String(over.spanId ?? 'b1')}`,
    parentSpanId: null,
    name: 'opencode.tool.execute.after',
    kind: 'internal',
    timestamp: '2026-10-28T00:00:00.000Z',
    durationMs: 15,
    status: { code: 'ok' },
    resource: { 'service.name': 'opencode' },
    attributes,
    ...over,
  } as OtlpSpan
}

describe('opencode adapter claims B1 tool-lifecycle envelope spans', () => {
  it.each(['tool.execute.before', 'tool.execute.after'] as const)(
    'scores a usage-free %s span above the registry threshold',
    (hook) => {
      const span = rawSpan({
        spanId: `b1-detect-${hook}`,
        attributes: toolHookAttributes({ 'opencode.hook': hook }),
      })
      expect(opencodeAdapter.detect(span)).toBeGreaterThanOrEqual(0.6)
    },
  )

  it('normalizes a tool.execute.after span to tool.invoke with no fabricated counters', () => {
    const record = opencodeAdapter.normalize(
      rawSpan({ spanId: 'b1-normalize', attributes: toolHookAttributes() }),
    )
    expect(record.harness).toBe('opencode')
    expect(record.op).toBe('tool.invoke')
    expect(record.sessionId).toBe(SESSION_ID)
    expect(record.tokens.freshInput).toBe(0)
    expect(record.tokens.output).toBe(0)
    expect(record.measurability?.token_usage).toMatchObject({ availability: 'not_measurable' })
    expect(record.measurability?.output).toMatchObject({ availability: 'not_measurable' })
    expect(record.measurability?.cache_read).toMatchObject({ availability: 'not_measurable' })
    expect(record.measurability?.cache_creation).toMatchObject({ availability: 'not_measurable' })
    expect(opencodeAdapter.validate(record)).toBeUndefined()
  })

  it('ingests the span-plus-log pair as one tool.invoke record', () => {
    const store = new CanonStore(':memory:')
    try {
      const outcome = ingestBatch(
        [otlpSpan(toolHookAttributes(), { spanId: 'b1-tool-span' })],
        store,
      )
      expect(outcome).toEqual({ accepted: 1, quarantined: 0, rejected: 0 })
      const record = store.get('b1-tool-span')
      expect(record?.harness).toBe('opencode')
      expect(record?.op).toBe('tool.invoke')
      expect(record?.sessionId).toBe(SESSION_ID)
    } finally {
      store.close()
    }
  })

  it('lets the tool marker win when a tool span also carries model markers', () => {
    const record = opencodeAdapter.normalize(
      rawSpan({
        spanId: 'b1-both',
        attributes: toolHookAttributes({
          'opencode.model.id': 'claude-opus-4-6',
          'opencode.model.provider': 'anthropic',
        }),
      }),
    )
    expect(record.op).toBe('tool.invoke')
  })
})
