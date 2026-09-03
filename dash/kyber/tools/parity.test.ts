// Parity gate tests (R15.1, R15.2). The fixture corpus below is the span
// corpus this test owns: six records shaped to drive every section of the
// digest down a real path — priced and unpriced bases, an explicitly
// not-billed block, a fence-embedded instruction block, a flagged fresh-input
// rise, an orphan whose parent never arrived, a subagent dispatch, auxiliary
// title generation, and one record whose token decomposition cannot hold.
// The `pythonDigest` literal is the Python pipeline's digest over that same
// corpus — the authoritative, expected side (R15.2): when the ported
// pipeline's digest differs, the test fails naming the section, and the
// difference is resolved before migration proceeds.

import { describe, expect, it } from 'vitest'

import type { CanonicalRecord, TokenUsage } from '../canon/types.js'
import {
  PARITY_CONTEXT_LIMIT,
  compareDigests,
  computeDigest,
  type ParityDigest,
} from './parity.js'

// ---------------------------------------------------------------------------
// Fixture corpus
// ---------------------------------------------------------------------------

function usage(overrides: Partial<TokenUsage> = {}): TokenUsage {
  return {
    freshInput: 0,
    cacheRead: 0,
    cacheCreation: 0,
    output: 0,
    reportedInput: 0,
    reportedOutput: 0,
    ...overrides,
  }
}

function record(overrides: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return {
    spanId: 'sp-0',
    traceId: 'tr-0',
    parentSpanId: null,
    source: 'pi:agent-7f3',
    harness: 'pi',
    name: 'chat turn',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: '2026-08-29T10:00:00.000Z',
    durationMs: 1_000,
    status: 'ok',
    tokens: usage(),
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
    ...overrides,
  }
}

/** One instruction fence riding inside conversation history, as the context analysis strips it. */
const FENCED_HISTORY =
  'H'.repeat(120) + '<system-reminder>' + 'R'.repeat(160) + '</system-reminder>' + 'H'.repeat(120)

/** The run this digest summarizes held two spans out as unclaimed (R6.1). */
const RUN = { quarantineCount: 2 } as const

function fixtureCorpus(): CanonicalRecord[] {
  return [
    // r1 — the session's first turn: every canonical content key, a fenced
    // instruction block inside history, a published-table price.
    record({
      spanId: 'sp-r1',
      tokens: usage({
        freshInput: 1_000,
        output: 500,
        reasoning: 100,
        reportedInput: 1_000,
        reportedOutput: 500,
      }),
      content: {
        system_prompt: 'S'.repeat(400),
        tool_definitions: 'T'.repeat(800),
        instruction_context: 'I'.repeat(200),
        conversation_history: FENCED_HISTORY,
        tool_result_content: 'D'.repeat(600),
      },
      cost: {
        basis: 'published',
        status: 'priced',
        value: 0.015625,
        currency: 'USD',
        byModel: { 'claude-sonnet-4.5': 0.015625 },
      },
    }),
    // r2 — the second turn: cache-read heavy, fresh input tripled (the
    // flagged sharp rise, R7.5), no message structure, the harness's own
    // arithmetic instead of a table price.
    record({
      spanId: 'sp-r2',
      parentSpanId: 'sp-r1',
      name: 'chat turn 2',
      timestamp: '2026-08-29T10:00:02.000Z',
      durationMs: 6_000,
      tokens: usage({
        freshInput: 3_000,
        cacheRead: 5_000,
        output: 800,
        reportedInput: 8_000,
        reportedOutput: 800,
      }),
      cost: { basis: 'harness', status: 'priced', value: 0.125, currency: 'USD' },
    }),
    // r3 — a tool invocation inside the same trace; unpriced.
    record({
      spanId: 'sp-r3',
      parentSpanId: 'sp-r2',
      name: 'read file',
      op: 'tool.invoke',
      kind: 'client',
      timestamp: '2026-08-29T10:00:03.000Z',
    }),
    // r4 — a subagent dispatch rooting its own trace (R9.3); a model the
    // publisher explicitly does not bill (R5.5).
    record({
      spanId: 'sp-r4',
      traceId: 'tr-2',
      name: 'dispatch subagent',
      op: 'agent.dispatch',
      timestamp: '2026-08-29T10:00:01.000Z',
      tokens: usage({ freshInput: 200, output: 100, reportedInput: 200, reportedOutput: 100 }),
      cost: { basis: 'published', status: 'not_billed' },
    }),
    // r5 — an orphan from a third harness whose parent never arrived; its
    // decomposition cannot hold (30 ≠ 999), so ingest would have rejected
    // it with a problem — and the digest recomputes exactly that (R4.4).
    record({
      spanId: 'sp-r5',
      traceId: 'tr-3',
      parentSpanId: 'sp-gone',
      source: 'copilot-eu-2',
      harness: 'copilot',
      name: 'read config',
      op: 'unspecified',
      timestamp: '2026-08-29T10:00:04.000Z',
      tokens: usage({
        freshInput: 10,
        cacheRead: 20,
        reportedInput: 999,
      }),
    }),
    // r6 — auxiliary title generation (R9.4), priced on the table.
    record({
      spanId: 'sp-r6',
      parentSpanId: 'sp-r1',
      name: 'generate session title',
      op: 'title.generate',
      timestamp: '2026-08-29T10:00:01.500Z',
      tokens: usage({ freshInput: 50, output: 200, reportedInput: 50, reportedOutput: 200 }),
      cost: { basis: 'published', status: 'priced', value: 0.0078125, currency: 'USD' },
    }),
  ]
}

// ---------------------------------------------------------------------------
// The Python pipeline's digest over the same corpus — the expected side
// ---------------------------------------------------------------------------

const pythonDigest: ParityDigest = {
  recordCount: 6,
  tokenStats: {
    // 1_000+3_000+200+10+50 input classes plus 500+800+100+0+200 output.
    totalTokens: 10_880,
    freshInput: 4_260,
    cacheRead: 5_020,
    cacheCreation: 0,
    output: 1_600,
  },
  costStats: {
    byBasis: {
      published: {
        blocks: 3,
        priced: 2,
        statuses: { priced: 2, partial: 0, no_rate: 0, out_of_scope: 0, not_billed: 1 },
        // Powers of two so the sum is exact in binary: 2^-6 + 2^-7.
        total: 0.0234375,
        currencies: ['USD'],
      },
      harness: {
        blocks: 1,
        priced: 1,
        statuses: { priced: 1, partial: 0, no_rate: 0, out_of_scope: 0, not_billed: 0 },
        total: 0.125,
        currencies: ['USD'],
      },
      unknown: {
        blocks: 2,
        priced: 0,
        statuses: { priced: 0, partial: 0, no_rate: 2, out_of_scope: 0, not_billed: 0 },
      },
    },
    // Absent on purpose: the corpus mixes bases, and sumCosts refuses to
    // blend them (R5.1) — the Python digest refused too.
  },
  contextBuckets: {
    measurable: true,
    turns: 2,
    buckets: {
      system_prompt: 100,
      tool_definitions: 200,
      // 50 direct instruction context plus 40 stripped out of history.
      instruction_context: 90,
      // 240 history characters survive the fence, at four to a token.
      conversation_history: 60,
      tool_result_content: 150,
    },
    // r1's buckets cover 600 of 1_000; r2's empty structure leaves all 8_000.
    residualTotal: 8_400,
    derivedCounts: true,
    flaggedTurns: 1,
  },
  schemaCost: {
    measurable: true,
    definitionCount: 1,
    neverInvokedCount: 1,
    tokenResidencies: 200,
    floor: 0,
    ceiling: 200,
    servers: 0,
  },
  timeline: {
    spans: 6,
    rootChildren: 4,
    orphanGroups: 1,
    auxiliarySpans: 1,
    subagentSpans: 1,
    maxDepth: 3,
  },
  quarantineCount: 2,
  problemCount: 1,
}

/** Deep-copy the Python literal and apply a mutation, for divergence tests. */
function mutated(apply: (draft: ParityDigest) => void): ParityDigest {
  const draft: ParityDigest = JSON.parse(JSON.stringify(pythonDigest))
  apply(draft)
  return draft
}

// ---------------------------------------------------------------------------
// R15.1 — the ported pipeline reproduces the Python digest
// ---------------------------------------------------------------------------

describe('R15.1 — the ported pipeline reproduces the Python digest', () => {
  it('matches the Python digest over the same corpus, section for section', () => {
    const ported = computeDigest(fixtureCorpus(), RUN)

    const comparison = compareDigests(pythonDigest, ported)
    expect(
      comparison.equal,
      // R15.2 in the failure path: the Python pipeline stays authoritative,
      // and the entries say which section diverged so it can be resolved.
      `parity digest diverged — the Python pipeline remains authoritative (R15.2); resolve:\n  ${comparison.diff.join('\n  ')}`
    ).toBe(true)

    // The section set, pinned: a section lost on both sides is a shape
    // regression, and the comparison above cannot see it.
    expect(Object.keys(ported)).toEqual([
      'recordCount',
      'tokenStats',
      'costStats',
      'contextBuckets',
      'schemaCost',
      'timeline',
      'quarantineCount',
      'problemCount',
    ])

    // Deep equality agrees: the comparator and the object must not diverge.
    expect(ported).toEqual(pythonDigest)
  })

  it('digests the same corpus to the same value every time', () => {
    const corpus = fixtureCorpus()
    const first = computeDigest(corpus, RUN)
    const second = computeDigest(corpus, RUN)
    expect(second).toEqual(first)
    expect(compareDigests(first, second)).toEqual({ equal: true, diff: [] })
  })

  it('carries no content, identifiers or names — the digest is content-free', () => {
    const digest = computeDigest(fixtureCorpus(), RUN)
    const serialized = JSON.stringify(digest)

    const forbidden = [
      // Message content, whole and in recognizable slices.
      '<system-reminder>',
      'SSSS',
      'TTTT',
      'IIII',
      'HHHH',
      'RRRR',
      'DDDD',
      // Span, trace and parent identifiers.
      'sp-r1',
      'sp-r2',
      'sp-r3',
      'sp-r4',
      'sp-r5',
      'sp-r6',
      'sp-gone',
      'tr-1',
      'tr-2',
      'tr-3',
      // Source names, harness attributions, span names, model identifiers.
      'pi:agent-7f3',
      'copilot',
      'claude-sonnet',
      'chat turn',
      'read file',
      'dispatch subagent',
      'read config',
      'generate session title',
    ]
    for (const secret of forbidden) {
      expect(serialized.includes(secret), `digest leaks "${secret}"`).toBe(false)
    }

    // Every section is a plain JSON value — the digest can travel to where
    // the comparison happens, which is the point of it being content-free.
    expect(JSON.parse(serialized)).toEqual(digest)
  })

  it('leaves totalCost absent when the corpus mixes cost bases (R5.1, R5.4)', () => {
    const digest = computeDigest(fixtureCorpus(), RUN)
    expect(digest.costStats.totalCost).toBeUndefined()
    expect('totalCost' in digest.costStats).toBe(false)
  })

  it('prices a single-basis corpus into one total figure', () => {
    const only = computeDigest([fixtureCorpus()[0]])
    expect(only.costStats.totalCost).toEqual({
      basis: 'published',
      value: 0.015625,
      currency: 'USD',
    })
  })
})

// ---------------------------------------------------------------------------
// R15.2 — a differing digest names its sections
// ---------------------------------------------------------------------------

describe('R15.2 — a differing digest reports its sections', () => {
  it('names the section and the leaf that diverged', () => {
    const ported = mutated((draft) => {
      draft.tokenStats.totalTokens += 1
      if (draft.contextBuckets.measurable) draft.contextBuckets.flaggedTurns = 0
    })

    const comparison = compareDigests(pythonDigest, ported)

    expect(comparison.equal).toBe(false)
    expect(comparison.diff).toEqual([
      'tokenStats.totalTokens: python=10880 ported=10881',
      'contextBuckets.flaggedTurns: python=1 ported=0',
    ])

    // Every entry's first segment is a section name — that is the contract
    // a reader of a failed parity run depends on.
    const sections = comparison.diff.map((entry) => entry.split(/[.[:]/)[0])
    expect(sections).toEqual(['tokenStats', 'contextBuckets'])
  })

  it('reports a scalar section directly, the authoritative value first', () => {
    const ported = mutated((draft) => {
      draft.quarantineCount = 0
    })

    const comparison = compareDigests(pythonDigest, ported)

    expect(comparison.equal).toBe(false)
    expect(comparison.diff).toEqual(['quarantineCount: python=2 ported=0'])
  })

  it('reports a leaf that exists on one side only', () => {
    const digest = computeDigest([fixtureCorpus()[0]])
    const withoutTotal: ParityDigest = {
      ...digest,
      costStats: { byBasis: digest.costStats.byBasis },
    }

    const comparison = compareDigests(digest, withoutTotal)

    expect(comparison.equal).toBe(false)
    expect(comparison.diff).toEqual(['costStats.totalCost: present in python only'])
  })
})

// ---------------------------------------------------------------------------
// Run context — what the corpus cannot say about its run
// ---------------------------------------------------------------------------

describe('run context — counts the corpus cannot express', () => {
  it('recomputes validation problems over the corpus and defaults quarantine to none', () => {
    // r5's decomposition cannot hold; the digest says so without being told.
    const digest = computeDigest(fixtureCorpus())
    expect(digest.problemCount).toBe(1)
    expect(digest.quarantineCount).toBe(0)
  })

  it('accepts the run’s recorded counts in place of the recomputed ones', () => {
    const digest = computeDigest(fixtureCorpus(), { quarantineCount: 4, problemCount: 7 })
    expect(digest.quarantineCount).toBe(4)
    expect(digest.problemCount).toBe(7)
  })

  it('declares the context window the parity run assumes', () => {
    // Pinned so a change to the harness's default window is a visible
    // decision, not a silent drift in every digest computed after it.
    expect(PARITY_CONTEXT_LIMIT).toBe(200_000)
  })
})
