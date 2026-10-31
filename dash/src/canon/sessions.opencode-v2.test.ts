// The session projection over OpenCode V2 records (design §3.3, D12, D13, D16). The records are
// hand-built to the shape the capture plugin's contract-3 spans map to: a parentless
// `agent.invoke` request root carrying `kyber.session.label`, `llm.invoke` steps with usage and
// the harness-reported cost, and `tool.invoke` grandchildren. No adapter is involved, so these
// tests do not wait for adapter work.

import { describe, expect, it } from 'vitest'

import { tokens, turn } from './fixtures/records.js'
import { CanonStore } from './store.js'
import { buildSessionRow, buildSessions } from './sessions.js'
import { approximateO200kBase } from './tokens.js'
import type { CanonicalRecord, SessionRow } from './types.js'

type PayloadView = {
  label: string
  summary: {
    turn_count: number
    request_count: number
    total_input: number
    total_cache_read: number
    cache_hit_ratio?: number
    cost: { basis: string; status: string; value?: number; currency?: string }
  }
}

function payloadOf(row: SessionRow): PayloadView {
  return row.payload as unknown as PayloadView
}

const SESSION = 'ses_opencode_v2'
const T0 = Date.parse('2026-10-30T09:00:00.000Z')
const at = (offsetMs: number): string => new Date(T0 + offsetMs).toISOString()

/** An OpenCode execution's request root. `label` is what the plugin wrote to `kyber.session.label`. */
function requestRoot(
  spanId: string,
  over: Partial<CanonicalRecord> & { label?: string } = {},
): CanonicalRecord {
  const { label, ...rest } = over
  return turn(spanId, [], {
    op: 'agent.invoke',
    name: 'opencode.request',
    source: 'opencode',
    harness: 'opencode',
    sessionId: SESSION,
    kind: 'internal',
    timestamp: at(0),
    durationMs: 13367,
    tokens: tokens({ freshInput: 0, cacheRead: 0, cacheCreation: 0, output: 0, reportedInput: 0, reportedOutput: 0 }),
    cost: { basis: 'unknown', status: 'no_rate' },
    ...(label === undefined ? {} : { raw: { 'kyber.session.label': label } }),
    ...rest,
  })
}

/** One model call with its usage counters and the harness-reported cost the adapter would stamp. */
function step(
  spanId: string,
  parentSpanId: string,
  usage: { input: number; output: number; cacheRead: number; cacheWrite: number },
  cost: CanonicalRecord['cost'] = { basis: 'harness', status: 'priced', value: 0.02, currency: 'USD' },
  over: Partial<CanonicalRecord> = {},
): CanonicalRecord {
  return turn(spanId, [], {
    parentSpanId,
    op: 'llm.invoke',
    name: 'opencode.step',
    source: 'opencode',
    harness: 'opencode',
    sessionId: SESSION,
    kind: 'internal',
    timestamp: at(100),
    durationMs: 4000,
    tokens: tokens({
      freshInput: usage.input,
      cacheRead: usage.cacheRead,
      cacheCreation: usage.cacheWrite,
      output: usage.output,
      reportedInput: usage.input + usage.cacheRead + usage.cacheWrite,
      reportedOutput: usage.output,
    }),
    cost,
    raw: { 'gen_ai.request.model': 'claude-opus-4-6' },
    ...over,
  })
}

/** runA's three model calls: fresh input, output with reasoning folded in, cache read, cache write. */
function runASteps(parentSpanId: string): CanonicalRecord[] {
  return [
    step('a-step-1', parentSpanId, { input: 10935, output: 69 + 957, cacheRead: 1521, cacheWrite: 0 }),
    step('a-step-2', parentSpanId, { input: 1259, output: 65 + 223, cacheRead: 12401, cacheWrite: 0 }, undefined, {
      timestamp: at(4400),
    }),
    step('a-step-3', parentSpanId, { input: 439, output: 19 + 196, cacheRead: 13553, cacheWrite: 0 }, undefined, {
      timestamp: at(9700),
    }),
  ]
}

describe('OpenCode session label: the kyber.session.label the plugin wrote (D12)', () => {
  it('labels the session with the latest kyber.session.label on its records, by time not array order', () => {
    // The newer request is listed first, so an implementation that takes the last array entry
    // reads the older label and fails.
    const records = [
      requestRoot('req-new', { label: 'Fix the login bug', timestamp: at(60000) }),
      ...runASteps('req-new').slice(0, 1),
      requestRoot('req-old', { label: 'Refactor auth', timestamp: at(0) }),
    ]

    const row = buildSessionRow(SESSION, records, approximateO200kBase)

    expect(row.label).toBe('Fix the login bug')
    expect(payloadOf(row).label).toBe('Fix the login bug')
  })

  it('serves the kyber label through the stored session payload the API reads', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([
      requestRoot('req-stored', { label: 'Fix the login bug' }),
      ...runASteps('req-stored'),
    ])

    await buildSessions(store)
    const payload = store.getSessionPayload(SESSION) as unknown as PayloadView

    expect(payload.label).toBe('Fix the login bug')
    store.close()
  })

  it('falls back to the harness label, never the request span name, when no label was recorded', () => {
    // Today the label is the span name `opencode.request`. The projection must pass the
    // machine name through the title fallback, which names the harness.
    const records = [requestRoot('req-plain'), ...runASteps('req-plain').slice(0, 1)]

    const payload = payloadOf(buildSessionRow(SESSION, records, approximateO200kBase))

    expect(payload.label).not.toBe('opencode.request')
    expect(payload.label).toMatch(/^opencode session$/i)
  })
})

describe('OpenCode session cache hit ratio (rule 6, D13)', () => {
  it('publishes cache_hit_ratio as cache read over input once the steps reported input', () => {
    // Input is fresh + cache read + cache write across the three steps: 12633 + 27475 = 40108.
    // Cache read is 27475. The ratio is the cache-read share of that input.
    const records = [requestRoot('req-cache'), ...runASteps('req-cache')]

    const payload = payloadOf(buildSessionRow(SESSION, records, approximateO200kBase))

    expect(payload.summary.total_input).toBe(40108)
    expect(payload.summary.total_cache_read).toBe(27475)
    expect(payload.summary.cache_hit_ratio).toBeCloseTo(27475 / 40108, 10)
  })

  it('omits cache_hit_ratio when the session reported no input', () => {
    // Passes on today's projection: with no measured input there is no denominator, so no ratio.
    const records = [
      requestRoot('req-empty'),
      step('empty-step', 'req-empty', { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }),
    ]

    const payload = payloadOf(buildSessionRow(SESSION, records, approximateO200kBase))

    expect(payload.summary.cache_hit_ratio).toBeUndefined()
  })
})

describe('OpenCode session cost: a harness zero is a priced figure', () => {
  it('shows a harness-reported zero cost as priced $0.00, not no_rate', () => {
    // Passes on today's projection: a harness-reported block is never repriced, and a priced
    // zero sums to a priced zero. Kept as a pin on the summary the dashboard reads.
    const records = [
      requestRoot('req-free'),
      step('free-step', 'req-free', { input: 800, output: 40, cacheRead: 0, cacheWrite: 0 }, {
        basis: 'harness',
        status: 'priced',
        value: 0,
        currency: 'USD',
      }),
    ]

    const payload = payloadOf(buildSessionRow(SESSION, records, approximateO200kBase))

    expect(payload.summary.cost).toMatchObject({ basis: 'harness', status: 'priced', value: 0, currency: 'USD' })
    expect(payload.summary.cost.status).not.toBe('no_rate')
  })
})
