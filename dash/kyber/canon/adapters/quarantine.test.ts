import { describe, expect, it } from 'vitest'

import type { CanonicalRecord, TokenUsage } from '../types.js'
import {
  TOKEN_NEGATIVE_CLASS,
  TOKEN_NEGATIVE_FRESH,
  TOKEN_REASONING_EXCEEDS_OUTPUT,
  TOKEN_SUM_MISMATCH,
} from '../types.js'
import { CanonStore } from '../store.js'
import {
  UNCLAIMED_REASON,
  getProblems,
  getQuarantined,
  observedNamespaces,
  problemSummary,
  quarantineSummary,
  quarantineUnclaimed,
  recordValidationProblems,
  type RecordValidator,
} from './quarantine.js'
import { PI_FINGERPRINT, defaultRegistry, piAdapter, rawSpan } from './testing.js'

/** A canonical record with the token decomposition under test swapped in. */
function recordWith(
  spanId: string,
  tokens: TokenUsage,
  harness = 'pi',
): CanonicalRecord {
  return {
    spanId,
    traceId: 't1',
    parentSpanId: null,
    source: 'pi-abc123',
    harness,
    name: 'chat turn',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: '2026-08-29T12:00:00.000Z',
    durationMs: 0,
    status: 'ok',
    tokens,
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
  }
}

/** A decomposition that reconciles exactly; every failure row mutates one class of it. */
const SOUND_TOKENS: TokenUsage = {
  freshInput: 100,
  cacheRead: 0,
  cacheCreation: 0,
  output: 10,
  reportedInput: 100,
  reportedOutput: 10,
}

describe('observedNamespaces', () => {
  it.each([
    {
      name: 'collapses a vendor namespace to its root',
      attributes: { 'trae.tool.name': 'bash', 'trae.session.id': 's-1' },
      expected: ['trae'],
    },
    {
      name: 'reports a bare GenAI namespace without claiming its harness',
      attributes: { 'gen_ai.usage.input_tokens': 512 },
      expected: ['gen_ai'],
    },
    {
      name: 'keeps two vendor namespaces apart, sorted',
      attributes: { 'copilot.model': 'claude', 'codeburn.provider': 'github-copilot' },
      expected: ['codeburn', 'copilot'],
    },
    {
      name: 'treats a dotted key without a dot-bearing prefix as its own namespace',
      attributes: { bare: 1, 'weird.key': 2 },
      expected: ['bare', 'weird'],
    },
    {
      name: 'preserves the absence of attributes as an empty observation',
      attributes: {},
      expected: [],
    },
  ])('$name', ({ attributes, expected }) => {
    expect(observedNamespaces(attributes)).toEqual(expected)
  })
})

describe('quarantineUnclaimed — R6.1', () => {
  it.each([
    {
      name: "an unmodelled 'trae' harness",
      attributes: { 'trae.tool.name': 'bash', 'trae.session.id': 's-1' },
      expectedNamespaces: ['trae'],
    },
    {
      name: 'a bare GenAI span below threshold, recognizable yet unclaimed by pi',
      attributes: { 'gen_ai.usage.input_tokens': 512 },
      expectedNamespaces: ['gen_ai'],
    },
    {
      name: 'partial copilot evidence below threshold',
      attributes: { 'codeburn.provider': 'github-copilot', 'unknown.vendor.thing': 1 },
      expectedNamespaces: ['codeburn', 'unknown'],
    },
    {
      name: 'a span with no attributes at all',
      attributes: {},
      expectedNamespaces: [],
    },
  ])('quarantines $name with its observed namespaces, never a guessed harness', ({
    attributes,
    expectedNamespaces,
  }) => {
    const store = new CanonStore(':memory:')
    const span = rawSpan({ spanId: 'lonely', source: 'unmodelled-01', traceId: 't1', attributes })
    const attributed = defaultRegistry().attribute([span])

    // The precondition: no adapter claimed the span with sufficient confidence.
    expect(attributed.has('lonely')).toBe(false)

    const quarantined = quarantineUnclaimed([span], attributed, store)
    expect(quarantined).toEqual(['lonely'])
    expect(store.getQuarantine('lonely')).toEqual({
      spanId: 'lonely',
      namespaces: expectedNamespaces,
      reason: UNCLAIMED_REASON,
    })
    // Nothing was stored for it, and nothing ever will be: the span has no harness.
    expect(store.count()).toBe(0)
  })

  it('leaves claimed spans out of quarantine while holding out their unclaimed batch-mates', () => {
    const store = new CanonStore(':memory:')
    const spans = [
      rawSpan({ spanId: 'req', source: 'pi-abc123', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
      rawSpan({ spanId: 'trae', source: 'trae-01', traceId: 't2', attributes: { 'trae.tool.name': 'bash' } }),
    ]
    const attributed = defaultRegistry().attribute(spans)
    expect(attributed.get('req')).toBe('pi')

    expect(quarantineUnclaimed(spans, attributed, store)).toEqual(['trae'])
    expect(store.getQuarantine('req')).toBeUndefined()
    expect(store.getQuarantine('trae')?.namespaces).toEqual(['trae'])
  })

  it('re-quarantining the same spans replaces entries rather than duplicating them', () => {
    const store = new CanonStore(':memory:')
    const span = rawSpan({ spanId: 'lonely', source: 'trae-01', traceId: 't1', attributes: { 'trae.tool.name': 'bash' } })
    const attributed = new Map<string, string>()

    quarantineUnclaimed([span], attributed, store)
    quarantineUnclaimed([span], attributed, store)

    expect(store.listQuarantine()).toHaveLength(1)
    expect(getQuarantined(store)).toEqual([
      { spanId: 'lonely', namespaces: ['trae'], reason: UNCLAIMED_REASON },
    ])
  })
})

describe('getQuarantined and quarantineSummary — R6.3', () => {
  function quarantineAll(
    spans: ReturnType<typeof rawSpan>[],
    store: CanonStore,
  ): void {
    quarantineUnclaimed(spans, defaultRegistry().attribute(spans), store)
  }

  it('exposes every quarantined span with at least the namespaces needed to write the missing adapter', () => {
    const store = new CanonStore(':memory:')
    quarantineAll(
      [
        rawSpan({ spanId: 'a2', source: 'trae-01', traceId: 't1', attributes: { 'trae.tool.name': 'bash' } }),
        rawSpan({ spanId: 'a1', source: 'trae-01', traceId: 't2', attributes: { 'trae.session.id': 's' } }),
        rawSpan({ spanId: 'w1', source: 'windsurf-02', traceId: 't3', attributes: { 'windsurf.model': 'm' } }),
      ],
      store,
    )

    const entries = getQuarantined(store)
    // Ordered by span id, so the view is deterministic across runs.
    expect(entries.map((entry) => entry.spanId)).toEqual(['a1', 'a2', 'w1'])
    for (const entry of entries) {
      // The fingerprint-bearing spans each expose a namespace to model; the
      // view's whole purpose is that none of them reads as an empty row.
      expect(entry.namespaces.length).toBeGreaterThanOrEqual(1)
    }
    expect(entries.map((entry) => entry.namespaces)).toEqual([['trae'], ['trae'], ['windsurf']])
  })

  it('counts quarantined spans by namespace signature', () => {
    const store = new CanonStore(':memory:')
    quarantineAll(
      [
        rawSpan({ spanId: 'a1', source: 'trae-01', traceId: 't1', attributes: { 'trae.tool.name': 'bash' } }),
        rawSpan({ spanId: 'a2', source: 'trae-01', traceId: 't2', attributes: { 'trae.session.id': 's' } }),
        rawSpan({ spanId: 'w1', source: 'windsurf-02', traceId: 't3', attributes: { 'windsurf.model': 'm' } }),
      ],
      store,
    )

    expect(quarantineSummary(store)).toEqual({
      total: 3,
      byNamespaces: [
        { namespaces: ['trae'], count: 2 },
        { namespaces: ['windsurf'], count: 1 },
      ],
    })
  })

  it('reports an empty quarantine as zero rows', () => {
    const store = new CanonStore(':memory:')
    expect(getQuarantined(store)).toEqual([])
    expect(quarantineSummary(store)).toEqual({ total: 0, byNamespaces: [] })
  })
})

describe('recordValidationProblems — R4.4', () => {
  it.each([
    {
      name: 'negative fresh input (the inverted-convention signature)',
      tokens: { ...SOUND_TOKENS, freshInput: -5, reportedInput: -5 },
      code: TOKEN_NEGATIVE_FRESH,
    },
    {
      name: 'a negative token class',
      tokens: { ...SOUND_TOKENS, cacheRead: -1, reportedInput: 99 },
      code: TOKEN_NEGATIVE_CLASS,
    },
    {
      name: 'reasoning exceeding output',
      tokens: { ...SOUND_TOKENS, output: 10, reasoning: 20 },
      code: TOKEN_REASONING_EXCEEDS_OUTPUT,
    },
    {
      name: 'classes that do not reconcile with reported input',
      tokens: { ...SOUND_TOKENS, reportedInput: 150 },
      code: TOKEN_SUM_MISMATCH,
    },
  ])('rejects $name, persisting severity, code and location', ({ tokens, code }) => {
    const store = new CanonStore(':memory:')
    const bad = recordWith('bad', tokens as TokenUsage)
    const good = recordWith('good', SOUND_TOKENS)

    const accepted = recordValidationProblems([bad, good], store)

    // The failing record is rejected — only the accepted subset may be stored.
    expect(accepted).toEqual([good])
    store.upsertMany(accepted)
    expect(store.get('bad')).toBeUndefined()
    expect(store.get('good')?.spanId).toBe('good')

    expect(store.getProblems('bad')).toEqual([
      {
        spanId: 'bad',
        severity: 'error',
        code,
        message: expect.any(String),
        location: 'bad',
      },
    ])
    expect(store.getProblems('good')).toEqual([])

    // A validation failure is never quarantined as an unclaimed harness: the
    // span was attributed; its decomposition is what failed.
    expect(store.getQuarantine('bad')).toBeUndefined()
  })

  it('runs a supplied record validator beside the token default and defaults its location', () => {
    const store = new CanonStore(':memory:')
    const strict: RecordValidator = (record) =>
      record.spanId === 'flagged'
        ? { severity: 'warning', code: 'ADAPTER_ASSERTION', message: 'stub adapter rejected the record' }
        : undefined

    const accepted = recordValidationProblems(
      [recordWith('flagged', SOUND_TOKENS), recordWith('plain', SOUND_TOKENS)],
      store,
      strict,
    )

    expect(accepted.map((record) => record.spanId)).toEqual(['plain'])
    expect(store.getProblems('flagged')).toEqual([
      {
        spanId: 'flagged',
        severity: 'warning',
        code: 'ADAPTER_ASSERTION',
        message: 'stub adapter rejected the record',
        location: 'flagged',
      },
    ])
  })
})

describe('getProblems and problemSummary — R6.4', () => {
  it('exposes recorded problems with grouped counts rather than only logs', () => {
    const store = new CanonStore(':memory:')
    recordValidationProblems(
      [
        recordWith('s1', { ...SOUND_TOKENS, reportedInput: 150 }),
        recordWith('s2', { ...SOUND_TOKENS, reportedInput: 151 }),
        recordWith('s3', { ...SOUND_TOKENS, freshInput: -5, reportedInput: -5 }),
      ],
      store,
    )

    expect(getProblems(store)).toHaveLength(3)
    expect(problemSummary(store)).toEqual({
      total: 3,
      byCode: [
        { code: TOKEN_SUM_MISMATCH, severities: ['error'], count: 2 },
        { code: TOKEN_NEGATIVE_FRESH, severities: ['error'], count: 1 },
      ],
    })
  })

  it('reports no problems as zero rows', () => {
    const store = new CanonStore(':memory:')
    expect(getProblems(store)).toEqual([])
    expect(problemSummary(store)).toEqual({ total: 0, byCode: [] })
  })
})

describe('the composed normalization seam — attribute, quarantine, validate, store', () => {
  it('files one batch into the corpus, quarantine, and problems without overlap', () => {
    const store = new CanonStore(':memory:')
    const adapter = piAdapter()
    const spans = [
      rawSpan({ spanId: 'req', source: 'pi-abc123', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
      rawSpan({ spanId: 'trae', source: 'trae-01', traceId: 't2', attributes: { 'trae.tool.name': 'bash' } }),
    ]

    // Two-pass attribution decides the batch; the remainder quarantines (R6.1).
    const attributed = defaultRegistry().attribute(spans)
    expect(quarantineUnclaimed(spans, attributed, store)).toEqual(['trae'])

    // Claimed spans normalize under the harness that won the vote.
    const normalized = spans
      .filter((span) => attributed.get(span.spanId) === adapter.name)
      .map((span) => adapter.normalize(span))
    const withFailure = [
      ...normalized,
      recordWith('bad', { ...SOUND_TOKENS, freshInput: -5, reportedInput: -5 }, adapter.name),
    ]

    const accepted = recordValidationProblems(withFailure, store)
    store.upsertMany(accepted)
    store.logIngest('unit', accepted.length)

    // One record stored, one span quarantined with its namespaces, one
    // problem persisted — three destinations, no span in two of them.
    expect(store.count()).toBe(1)
    expect(quarantineSummary(store)).toEqual({
      total: 1,
      byNamespaces: [{ namespaces: ['trae'], count: 1 }],
    })
    expect(problemSummary(store)).toEqual({
      total: 1,
      byCode: [{ code: TOKEN_NEGATIVE_FRESH, severities: ['error'], count: 1 }],
    })
  })
})
