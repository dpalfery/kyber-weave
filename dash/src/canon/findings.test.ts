import { describe, expect, it } from 'vitest'

import { buildFindings } from './findings.js'
import { buildRuns } from './runs.js'
import { buildSessions } from './sessions.js'
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

  it('attributes each persisted hazard to the canonical session key the run carried', async () => {
    // Two harness sessions that never emitted a session id land in one run
    // (a shared explicit run id) and are keyed by their trace ids — the store
    // keys every record by COALESCE(session_id, trace_id), and buildSessions
    // and buildRuns name the row after that key. Persisting one merged
    // placeholder finding would measure both sessions against whichever
    // window came first and match no session row.
    const store = new CanonStore(':memory:')
    store.upsertMany([
      turn('t-a1', [], {
        sessionId: null,
        traceId: 'trace-a',
        tokens: tokens({ freshInput: 100_000, reportedInput: 100_000 }),
        raw: { 'gen_ai.run.id': 'run-shared', 'gen_ai.request.max_context_tokens': 200_000 },
      }),
      turn('t-a2', [], {
        sessionId: null,
        traceId: 'trace-a',
        timestamp: '2026-09-03T10:05:00.000Z',
        tokens: tokens({ freshInput: 190_000, reportedInput: 190_000 }),
        raw: { 'gen_ai.run.id': 'run-shared', 'gen_ai.request.max_context_tokens': 200_000 },
      }),
      turn('t-b1', [], {
        sessionId: null,
        traceId: 'trace-b',
        timestamp: '2026-09-03T10:06:00.000Z',
        tokens: tokens({ freshInput: 100_000, reportedInput: 100_000 }),
        raw: { 'gen_ai.run.id': 'run-shared', 'gen_ai.request.max_context_tokens': 1_000_000 },
      }),
      turn('t-b2', [], {
        sessionId: null,
        traceId: 'trace-b',
        timestamp: '2026-09-03T10:07:00.000Z',
        tokens: tokens({ freshInput: 195_000, reportedInput: 195_000 }),
        raw: { 'gen_ai.run.id': 'run-shared', 'gen_ai.request.max_context_tokens': 1_000_000 },
      }),
    ])
    await buildRuns(store)

    buildFindings(store)

    const hazards = persistedCompactionHazards(store)
    expect(hazards).toHaveLength(1)
    const hazard = hazards[0]!
    // trace-a's own window puts its 190,000-token peak over the 85% line;
    // trace-b's 1,000,000 window keeps its 195,000 well under it. The finding
    // is named after the session that fired, not the bucket that absorbed both.
    expect(hazard.sessionId).toBe('trace-a')
    expect(hazard.estimatedWasteTokens).toBe(20_000)
    const provenance = hazard as ContextWindowProvenance
    expect(provenance.contextLimit).toBe(200_000)
    expect(provenance.contextLimitSource).toBe('reported')
    store.close()
  })
})

describe('buildFindings over a session key that spans several harnesses', () => {
  // When one key's records span several canonical harnesses, buildSessions and
  // buildRuns give each harness's share the id `${harness}:${key}`. That id is
  // no record's key, so a gather that looks it up verbatim returns nothing and
  // the share silently contributes no findings and no run outcome
  // (docs/todo/canon-multi-harness-session-gather.md).

  const EMPTY_OUTCOME_REASON = 'No records provided to observe run outcome.'

  /** An antigravity share that crosses the 85% line, and a copilot-cli share that does not. */
  function multiHarnessRecords(key: string, over: Partial<CanonicalRecord> = {}): CanonicalRecord[] {
    return [
      turn('m-ag-1', [], {
        sessionId: key,
        tokens: tokens({ freshInput: 100_000, reportedInput: 100_000 }),
        raw: { 'gen_ai.request.max_context_tokens': 200_000 },
        ...over,
      }),
      turn('m-ag-2', [], {
        sessionId: key,
        timestamp: '2026-09-03T10:05:00.000Z',
        tokens: tokens({ freshInput: 190_000, reportedInput: 190_000 }),
        raw: { 'gen_ai.request.max_context_tokens': 200_000 },
        ...over,
      }),
      turn('m-cp-1', [], {
        source: 'copilot-cli',
        harness: 'copilot-cli',
        sessionId: key,
        // A day later: past the inactivity window, so the share is a run of its own.
        timestamp: '2026-09-04T10:00:00.000Z',
        tokens: tokens({ freshInput: 50_000, reportedInput: 50_000 }),
        raw: { 'gen_ai.request.max_context_tokens': 200_000 },
        ...over,
      }),
    ]
  }

  it('derives the hazard from its own harness share and names the session row that share built', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany(multiHarnessRecords('sess-multi'))
    await buildRuns(store)

    const runs = store.listRuns()
    // Runs are grouped per harness, so each share is a run of its own and the
    // detector is handed one harness's records: it cannot see from them that
    // the key spans two.
    expect(runs.map((run) => run.harness).sort()).toEqual(['antigravity', 'copilot-cli'])
    expect(store.listExecutions().map((execution) => execution.sessionId).sort()).toEqual([
      'antigravity:sess-multi',
      'copilot-cli:sess-multi',
    ])

    const report = buildFindings(store)

    expect(report.runsExamined).toBe(2)
    const hazards = persistedCompactionHazards(store)
    expect(hazards).toHaveLength(1)
    const hazard = hazards[0]!
    expect(hazard.sessionId).toBe('antigravity:sess-multi')
    expect(hazard.estimatedWasteTokens).toBe(20_000)
    // The copilot-cli share's 50,000 tokens stayed out of the antigravity run.
    expect(hazard.evidenceLinks.map((link) => link.spanId).sort()).toEqual(['m-ag-1', 'm-ag-2'])
    store.close()
  })

  it('derives each harness share run outcome from that share records', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany(multiHarnessRecords('sess-outcome'))

    await buildRuns(store)

    const runs = store.listRuns()
    expect(runs).toHaveLength(2)
    for (const run of runs) {
      expect(run.outcome?.statusReason, `run ${run.runId} (${run.harness})`).not.toBe(EMPTY_OUTCOME_REASON)
    }
    store.close()
  })

  it('keeps a split share apart from a native session whose own key reads like its id', async () => {
    // `antigravity:sess-x` is a native key of its own here, and `sess-x` is
    // split across antigravity and copilot-cli. Minting the split share as
    // `antigravity:sess-x` would land both on one session and execution row
    // (both tables replace on their id), and the gather could not tell which
    // records the id meant.
    const store = new CanonStore(':memory:')
    store.upsertMany([
      ...multiHarnessRecords('sess-x'),
      turn('native-1', [], {
        sessionId: 'antigravity:sess-x',
        timestamp: '2026-09-05T10:00:00.000Z',
        tokens: tokens({ freshInput: 10_000, reportedInput: 10_000 }),
        raw: { 'gen_ai.request.max_context_tokens': 200_000 },
      }),
    ])

    await buildSessions(store)

    const sessionIds = store.builtSessionIds().sort()
    expect(sessionIds).toHaveLength(3)
    expect(sessionIds).toContain('antigravity:sess-x')
    expect(sessionIds).toContain('copilot-cli:sess-x')
    const executionIds = store.listExecutions().map((execution) => execution.executionId).sort()
    expect(executionIds).toEqual(sessionIds)

    const hazards = persistedCompactionHazards(store)
    expect(hazards).toHaveLength(1)
    const hazard = hazards[0]!
    // The hazard is the split share's, named after the row that share built —
    // not the native session's id, and not measured over its records.
    expect(hazard.sessionId).not.toBe('antigravity:sess-x')
    expect(sessionIds).toContain(hazard.sessionId)
    expect(hazard.evidenceLinks.map((link) => link.spanId).sort()).toEqual(['m-ag-1', 'm-ag-2'])
    store.close()
  })

  it('still gathers a single-harness session whose own key contains a colon', async () => {
    // Native session ids may carry a colon, so the harness a derived id belongs
    // to is never recovered by splitting the id.
    const store = new CanonStore(':memory:')
    store.upsertMany(multiHarnessRecords('workspace:sess-colon').filter((record) => record.harness === 'antigravity'))
    await buildRuns(store)

    buildFindings(store)

    expect(persistedCompactionHazards(store).map((hazard) => hazard.sessionId)).toEqual(['workspace:sess-colon'])
    store.close()
  })
})
