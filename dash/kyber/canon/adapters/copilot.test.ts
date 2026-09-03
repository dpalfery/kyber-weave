import { describe, expect, it } from 'vitest'

import { AdapterRegistry } from './registry.js'
import { rawSpan } from './testing.js'
import { TOKEN_NEGATIVE_FRESH, TOKEN_SUM_MISMATCH } from '../types.js'
import { copilotAdapter, reconcileRequest } from './copilot.js'
import { geminiAdapter } from './gemini.js'
import { piAdapter } from './pi.js'

// The ported adapters are tested the way the Python pipeline's adapter test
// established (design.md, "Testing Strategy"): every table proves BOTH that
// the convention is applied correctly AND that applying the wrong one fails
// loudly. A table that only proved the happy path is the table that missed
// the pi/Copilot inversion of R4.2 in the first place.

/** Copilot-shaped counters: input INCLUDES cache read and creation (R4.2). */
const copilotCounts = (
  overrides: Partial<Record<'input' | 'read' | 'creation' | 'output', number>> = {},
) => ({
  'gen_ai.usage.input_tokens': overrides.input ?? 50_500,
  'gen_ai.usage.output_tokens': overrides.output ?? 700,
  'gen_ai.usage.cache_read.input_tokens': overrides.read ?? 40_000,
  'gen_ai.usage.cache_creation.input_tokens': overrides.creation ?? 500,
})

/** pi-shaped counters: input EXCLUDES the cache classes (R4.2). */
const piCounts = () => ({
  'gen_ai.usage.input_tokens': 500,
  'gen_ai.usage.output_tokens': 210,
  'gen_ai.usage.cache_read.input_tokens': 40_000,
  'gen_ai.usage.cache_creation.input_tokens': 400,
})

describe('copilotAdapter.detect', () => {
  it.each([
    ['full fingerprint', { ...copilotCounts(), 'github.copilot.chat.turn.id': 't-1' }, 1],
    ['vendor namespace alone', { 'codeburn.provider': 'github-copilot' }, 0.6],
    ['shared GenAI usage alone', copilotCounts(), 0.4],
    ['no fingerprint', { 'trae.session': 'x' }, 0],
  ])('scores %s as %s', (_label, attributes, score) => {
    expect(copilotAdapter.detect(rawSpan({ spanId: 'a', attributes }))).toBe(score)
  })
})

describe('copilotAdapter.normalize — the inclusive convention (R4.2)', () => {
  // input INCLUDES the cache classes, so conversion subtracts them: the
  // classes stay disjoint and the reported-input identity holds (R4.1).
  it.each([
    [
      'cache split across read and creation',
      copilotCounts(),
      { freshInput: 10_000, cacheRead: 40_000, cacheCreation: 500, reportedInput: 50_500, output: 700 },
    ],
    [
      'no cache attributes at all',
      { 'gen_ai.usage.input_tokens': 120, 'gen_ai.usage.output_tokens': 30 },
      { freshInput: 120, cacheRead: 0, cacheCreation: 0, reportedInput: 120, output: 30 },
    ],
    [
      'string-typed counters from the SQLite store',
      {
        'gen_ai.usage.input_tokens': '50500',
        'gen_ai.usage.output_tokens': '700',
        'gen_ai.usage.cache_read.input_tokens': '40000',
        'gen_ai.usage.cache_creation.input_tokens': '500',
      },
      { freshInput: 10_000, cacheRead: 40_000, cacheCreation: 500, reportedInput: 50_500, output: 700 },
    ],
    [
      'underscored cache spellings read identically',
      {
        'gen_ai.usage.input_tokens': 50_500,
        'gen_ai.usage.output_tokens': 700,
        'gen_ai.usage.cache_read_input_tokens': 40_000,
        'gen_ai.usage.cache_creation_input_tokens': 500,
      },
      { freshInput: 10_000, cacheRead: 40_000, cacheCreation: 500, reportedInput: 50_500, output: 700 },
    ],
  ])('applies %s', (_label, attributes, expected) => {
    const record = copilotAdapter.normalize(
      rawSpan({ spanId: 's1', traceId: 't1', source: 'codeburn-prod-7', attributes }),
    )

    expect(record.tokens).toMatchObject(expected)
    expect(record.tokens.freshInput + record.tokens.cacheRead + record.tokens.cacheCreation).toBe(
      record.tokens.reportedInput,
    )
    expect(record.harness).toBe('copilot')
    expect(record.op).toBe('llm.invoke')
    // The record-level check (R4.3, R4.4) passes and stores nothing untoward.
    expect(copilotAdapter.validate(record)).toBeUndefined()
  })

  it('maps a tool span to the canonical tool operation', () => {
    const record = copilotAdapter.normalize(
      rawSpan({
        spanId: 's1',
        attributes: { 'gen_ai.tool.name': 'Read', 'github.copilot.chat.turn.id': 't-1' },
      }),
    )
    expect(record.op).toBe('tool.invoke')
  })

  it('stamps unexported metrics as not measurable rather than zero (R10.2)', () => {
    const record = copilotAdapter.normalize(rawSpan({ spanId: 's1', attributes: copilotCounts() }))
    expect(copilotAdapter.unexportedMetrics()).toEqual(['reasoning'])
    expect(record.measurability).toEqual({ reasoning: 'not_measurable' })
  })

  it('keeps structure, placeholder cost and the raw payload', () => {
    const record = copilotAdapter.normalize(
      rawSpan({
        spanId: 's1',
        traceId: 't1',
        parentSpanId: 'root',
        source: 'codeburn-prod-7',
        name: 'chat turn',
        kind: 'internal',
        attributes: copilotCounts(),
      }),
    )
    expect(record).toMatchObject({
      spanId: 's1',
      traceId: 't1',
      parentSpanId: 'root',
      source: 'codeburn-prod-7',
      name: 'chat turn',
      kind: 'internal',
      cost: { basis: 'unknown', status: 'no_rate' },
    })
    expect(record.raw).toEqual(copilotCounts())
  })
})

describe('copilotAdapter — the inverted convention must fail loudly (R4.2)', () => {
  // The measured failure: applying Copilot's inclusive conversion to pi's
  // exclusive counters subtracts cache the input never contained. On the
  // measured corpus this went negative on 293 of 307 spans; every row here
  // is cache-heavy the way real turns are, so every row must reject.
  it.each([
    ['pi-shaped counters', piCounts(), 500 - 40_000 - 400],
    [
      'cache read alone exceeding the claim',
      { 'gen_ai.usage.input_tokens': 1000, 'gen_ai.usage.cache_read.input_tokens': 50_000 },
      1000 - 50_000,
    ],
  ])('rejects %s with TOKEN_NEGATIVE_FRESH', (_label, attributes, freshInput) => {
    const record = copilotAdapter.normalize(rawSpan({ spanId: 's1', traceId: 't1', attributes }))

    // No clamping: the negative survives so validation can see it. A
    // Math.max(0, …) here is how the inversion would be silenced.
    expect(record.tokens.freshInput).toBe(freshInput)
    expect(record.tokens.freshInput).toBeLessThan(0)

    const problem = copilotAdapter.validate(record)
    expect(problem).toMatchObject({
      severity: 'error',
      code: TOKEN_NEGATIVE_FRESH,
      location: 's1',
    })
  })

  it('surfaces a corrupted sum as TOKEN_SUM_MISMATCH', () => {
    const record = copilotAdapter.normalize(rawSpan({ spanId: 's2', attributes: copilotCounts() }))
    record.tokens.reportedInput = record.tokens.reportedInput + 1
    expect(copilotAdapter.validate(record)).toMatchObject({ severity: 'error', code: TOKEN_SUM_MISMATCH })
  })
})

describe('reconcileRequest — the per-request match indicator (R4.5)', () => {
  const requestSpans = [
    rawSpan({
      spanId: 'root',
      traceId: 't1',
      attributes: {
        ...copilotCounts({ input: 60_500, read: 40_000, creation: 500, output: 750 }),
        'github.copilot.chat.turn.id': 't-1',
      },
    }),
    rawSpan({ spanId: 'turn1', traceId: 't1', parentSpanId: 'root', attributes: copilotCounts() }),
    rawSpan({
      spanId: 'turn2',
      traceId: 't1',
      parentSpanId: 'root',
      attributes: copilotCounts({ input: 10_000, read: 0, creation: 0, output: 50 }),
    }),
  ]

  it('matches when the per-turn sums equal the harness-reported root total', () => {
    const records = requestSpans.map((span) => copilotAdapter.normalize(span))
    const reconciliation = reconcileRequest('root', records)

    // Σ per-turn input (50_500 + 10_000) equals the root's own inclusive
    // claim of 60_500; outputs reconcile too (700 + 50 = 750).
    expect(reconciliation).toEqual({
      match: true,
      expected: 60_500,
      actual: 60_500,
      outputExpected: 750,
      outputActual: 750,
    })
  })

  it('exposes a mismatch rather than dropping either side', () => {
    const records = requestSpans.map((span) => copilotAdapter.normalize(span))
    records[0].tokens.reportedInput = 61_000
    const reconciliation = reconcileRequest('root', records)

    expect(reconciliation?.match).toBe(false)
    expect(reconciliation?.expected).toBe(60_500)
    expect(reconciliation?.actual).toBe(61_000)
  })

  it('catches the silent direction of the inversion — the 2× double-count', () => {
    // pi's exclusive conversion applied to Copilot's inclusive counters
    // passes record validation (the sum identity holds by construction) but
    // inflates every turn's total; against the harness's own root claim the
    // per-request indicator is what fails loudly (R4.5 catching what R4.1
    // cannot see).
    const records = requestSpans.map((span) =>
      span.spanId === 'root' ? copilotAdapter.normalize(span) : piAdapter.normalize(span),
    )
    const reconciliation = reconcileRequest('root', records)

    expect(reconciliation?.match).toBe(false)
    // Each turn's total was re-added its own cache: 91_000 + 10_000 claimed
    // against a harness total of 60_500.
    expect(reconciliation?.expected).toBe(101_000)
    expect(reconciliation?.actual).toBe(60_500)
  })

  it('returns undefined when the root is not among the records', () => {
    expect(reconcileRequest('missing', [])).toBeUndefined()
  })
})

describe('the ported adapters under AdapterRegistry (R6.2)', () => {
  const registry = new AdapterRegistry([copilotAdapter, piAdapter, geminiAdapter])

  it('attributes by fingerprint even when the source name suggests the other harness', () => {
    const copilotFingerprint = (source: string) =>
      rawSpan({
        spanId: 'a',
        traceId: 't1',
        source,
        attributes: { ...copilotCounts(), 'github.copilot.chat.turn.id': 't-1' },
      })
    const piFingerprint = (source: string) =>
      rawSpan({
        spanId: 'b',
        traceId: 't1',
        source,
        attributes: { ...piCounts(), 'pi.session.id': 's-9f2' },
      })

    // The source names lie in both directions; the vote must not listen.
    expect(registry.attribute([copilotFingerprint('pi-abc123')]).get('a')).toBe('copilot')
    expect(registry.attribute([piFingerprint('codeburn-prod-7')]).get('b')).toBe('pi')
  })

  it('leaves GenAI-only spans undecided for quarantine or inheritance', () => {
    // The alone-in-its-trace shape: shared usage keys score 0.4 for every
    // adapter, below the threshold, so no harness is claimed.
    const span = rawSpan({ spanId: 'a', traceId: 't1', attributes: { 'gen_ai.usage.input_tokens': 512 } })
    expect(registry.attribute([span]).has('a')).toBe(false)
  })
})
