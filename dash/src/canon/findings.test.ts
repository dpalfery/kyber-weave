import { describe, expect, it } from 'vitest'

import { buildFindings } from './findings.js'
import { buildRuns } from './runs.js'
import { CanonStore } from './store.js'
import type { CanonicalRecord, ContentPart } from './types.js'

// `buildFindings` is the wire between the pure detectors and the dashboard: it
// turns the records a run actually carries into the `finding` rows
// `listFindings` serves. These tests hold one property of that wire: a
// compaction-hazard percentage is only honest when it is measured against the
// context window the records themselves report — the
// `gen_ai.request.max_context_tokens` family, the same source the session
// analysis reads — and the fixed 200,000 default is reserved for records that
// report nothing, with the window it used stated in the persisted payload
// rather than implied.

const tokens = (over: Partial<CanonicalRecord['tokens']> = {}) => ({
  freshInput: 1000,
  cacheRead: 0,
  cacheCreation: 0,
  output: 100,
  reportedInput: 1000,
  reportedOutput: 100,
  ...over,
})

function turn(spanId: string, parts: ContentPart[], over: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return {
    spanId,
    traceId: 'trace-1',
    parentSpanId: null,
    source: 'antigravity',
    harness: 'antigravity',
    sessionId: 'sess-1',
    name: 'llm_request',
    op: 'llm.invoke',
    kind: 'client',
    timestamp: '2026-09-03T10:00:00.000Z',
    durationMs: 100,
    status: 'ok',
    tokens: tokens(),
    content: {},
    parts,
    cost: { basis: 'unknown', status: 'no_rate' },
    ...over,
  }
}

/** The window provenance the persisted payload carries (D2). */
type ContextWindowProvenance = {
  contextLimit?: number
  contextLimitSource?: 'reported' | 'default'
}

function persistedCompactionHazards(store: CanonStore) {
  return store.listFindings().filter((finding) => finding.detectorId === 'compaction-hazard')
}

describe('buildFindings', () => {
  it('measures the persisted compaction hazard against the window the records report', async () => {
    // A 900,000-token peak inside a reported 1,000,000 window is a 90%
    // finding with 50,000 tokens above the 85% threshold. Measured against the
    // 200,000 default it would read 450% and invent 730,000 recoverable
    // tokens — the over-report the FINDINGS surface showed.
    const store = new CanonStore(':memory:')
    store.upsertMany([
      turn('w-1', [], {
        sessionId: 'sess-reported',
        tokens: tokens({ freshInput: 100_000, reportedInput: 100_000 }),
        raw: { 'gen_ai.request.max_context_tokens': 1_000_000 },
      }),
      turn('w-2', [], {
        sessionId: 'sess-reported',
        timestamp: '2026-09-03T10:05:00.000Z',
        tokens: tokens({ freshInput: 900_000, reportedInput: 900_000 }),
        raw: { 'gen_ai.request.max_context_tokens': 1_000_000 },
      }),
    ])
    await buildRuns(store)

    buildFindings(store)

    const hazards = persistedCompactionHazards(store)
    expect(hazards).toHaveLength(1)
    const hazard = hazards[0]!
    expect(hazard.sessionId).toBe('sess-reported')
    // Waste is measured from the reported window's threshold, not the default's.
    expect(hazard.estimatedWasteTokens).toBe(50_000)
    expect(hazard.mechanism).toMatch(/of 1000000 token window/)
    const provenance = hazard as ContextWindowProvenance
    expect(provenance.contextLimit).toBe(1_000_000)
    expect(provenance.contextLimitSource).toBe('reported')
    store.close()
  })

  it('keeps the default window for records that report none, and marks it as default', async () => {
    // No attribute names a window, so the 200,000 default still governs — but
    // a reader of the finding must be able to tell that the denominator is an
    // assumption rather than a measurement (honest unobservability).
    const store = new CanonStore(':memory:')
    store.upsertMany([
      turn('d-1', [], {
        sessionId: 'sess-default',
        tokens: tokens({ freshInput: 100_000, reportedInput: 100_000 }),
      }),
      turn('d-2', [], {
        sessionId: 'sess-default',
        timestamp: '2026-09-03T10:05:00.000Z',
        tokens: tokens({ freshInput: 190_000, reportedInput: 190_000 }),
      }),
    ])
    await buildRuns(store)

    buildFindings(store)

    const hazards = persistedCompactionHazards(store)
    expect(hazards).toHaveLength(1)
    const hazard = hazards[0]!
    expect(hazard.estimatedWasteTokens).toBe(20_000)
    expect(hazard.mechanism).toMatch(/of 200000 token window/)
    const provenance = hazard as ContextWindowProvenance
    expect(provenance.contextLimit).toBe(200_000)
    expect(provenance.contextLimitSource).toBe('default')
    store.close()
  })

  it('does not report a hazard the reported window keeps below the threshold', async () => {
    // 190,000 tokens is 95% of the 200,000 default but 19% of the reported
    // 1,000,000 window. Persisting a hazard for it is the false alarm a large
    // -window model sees on every session.
    const store = new CanonStore(':memory:')
    store.upsertMany([
      turn('c-1', [], {
        sessionId: 'sess-large',
        tokens: tokens({ freshInput: 100_000, reportedInput: 100_000 }),
        raw: { 'gen_ai.request.max_context_tokens': 1_000_000 },
      }),
      turn('c-2', [], {
        sessionId: 'sess-large',
        timestamp: '2026-09-03T10:05:00.000Z',
        tokens: tokens({ freshInput: 190_000, reportedInput: 190_000 }),
        raw: { 'gen_ai.request.max_context_tokens': 1_000_000 },
      }),
    ])
    await buildRuns(store)

    buildFindings(store)

    const hazards = persistedCompactionHazards(store)
    expect(
      hazards,
      `unexpected compaction hazards: ${JSON.stringify(hazards.map((finding) => finding.id))}`,
    ).toHaveLength(0)
    store.close()
  })
})
