import { describe, expect, it } from 'vitest'

import { resolveRootByParentage, traceGroup } from './base.js'
import { rawSpan } from './testing.js'
import { copilotAdapter, reconcileRequest } from './copilot.js'
import { piAdapter } from './pi.js'
import { TOKEN_NEGATIVE_FRESH } from '../types.js'

// Tested the way the Python pipeline's adapter test established (design.md,
// "Testing Strategy"): each table proves the exclusive convention applied
// correctly AND the failure shape when the opposite (Copilot's inclusive)
// convention is applied to the same keys. pi and Copilot share the attribute
// key `gen_ai.usage.input_tokens` with opposite meanings — the tables exist
// so neither meaning can silently win (R4.2).

/** pi-shaped counters: input EXCLUDES the cache classes (R4.2). */
const piCounts = (
  overrides: Partial<Record<'input' | 'read' | 'creation' | 'output', number>> = {},
) => ({
  'gen_ai.usage.input_tokens': overrides.input ?? 500,
  'gen_ai.usage.output_tokens': overrides.output ?? 210,
  'gen_ai.usage.cache_read.input_tokens': overrides.read ?? 40_000,
  'gen_ai.usage.cache_creation.input_tokens': overrides.creation ?? 400,
  'pi.session.id': 's-9f2',
})

/** Copilot-shaped counters: input INCLUDES the cache classes (R4.2). */
const copilotCounts = () => ({
  'gen_ai.usage.input_tokens': 50_500,
  'gen_ai.usage.output_tokens': 700,
  'gen_ai.usage.cache_read.input_tokens': 50_000,
  'gen_ai.usage.cache_creation.input_tokens': 0,
})

describe('piAdapter.detect', () => {
  it.each([
    ['full fingerprint', piCounts(), 1],
    ['vendor namespace alone', { 'pi.session.id': 's-9f2' }, 0.6],
    ['shared GenAI usage alone', { 'gen_ai.usage.input_tokens': 4821 }, 0.4],
    ['no fingerprint', { 'github.copilot.chat.turn.id': 't-1' }, 0],
  ])('scores %s as %s', (_label, attributes, score) => {
    expect(piAdapter.detect(rawSpan({ spanId: 'a', attributes }))).toBe(score)
  })

  it('groups by trace and resolves roots by parentage like the defaults', () => {
    const span = rawSpan({ spanId: 'a', traceId: 't1', attributes: piCounts() })
    expect(piAdapter.group(span)).toBe(traceGroup(span))

    const group = [
      rawSpan({ spanId: 'root', traceId: 't1', parentSpanId: null, attributes: piCounts() }),
      rawSpan({
        spanId: 'child',
        traceId: 't1',
        parentSpanId: 'root',
        attributes: { 'gen_ai.usage.input_tokens': 512 },
      }),
    ]
    expect(piAdapter.resolveRoot(group)).toBe(resolveRootByParentage(group))
    expect(piAdapter.resolveRoot(group)).toBe('root')
  })
})

describe('piAdapter.normalize — the exclusive convention (R4.2)', () => {
  // pi's input counter is fresh input alone, so conversion takes it as
  // claimed and reassembles the converted total from the classes; the
  // reported-input identity (R4.1) holds on every row.
  it.each([
    [
      'fresh input claimed with both cache classes',
      piCounts(),
      { freshInput: 500, cacheRead: 40_000, cacheCreation: 400, reportedInput: 40_900, output: 210 },
    ],
    [
      'no cache attributes at all',
      { 'gen_ai.usage.input_tokens': 500, 'gen_ai.usage.output_tokens': 210 },
      { freshInput: 500, cacheRead: 0, cacheCreation: 0, reportedInput: 500, output: 210 },
    ],
    [
      'cache read only, via the underscored spelling',
      {
        'gen_ai.usage.input_tokens': 500,
        'gen_ai.usage.output_tokens': 210,
        'gen_ai.usage.cache_read_input_tokens': 39_500,
      },
      { freshInput: 500, cacheRead: 39_500, cacheCreation: 0, reportedInput: 40_000, output: 210 },
    ],
  ])('applies %s', (_label, attributes, expected) => {
    const record = piAdapter.normalize(
      rawSpan({ spanId: 's1', traceId: 't1', source: 'pi-abc123', attributes }),
    )

    expect(record.tokens).toMatchObject(expected)
    expect(record.tokens.freshInput + record.tokens.cacheRead + record.tokens.cacheCreation).toBe(
      record.tokens.reportedInput,
    )
    expect(record.harness).toBe('pi')
    expect(record.op).toBe('llm.invoke')
    expect(piAdapter.validate(record)).toBeUndefined()
  })

  it('declares tool_definitions unexported — 14 tools across 368 calls, none exported', () => {
    const record = piAdapter.normalize(rawSpan({ spanId: 's1', attributes: piCounts() }))
    expect(piAdapter.unexportedMetrics()).toEqual(['tool_definitions'])
    // Absent is not zero (R7.6, R8.5, R10.2): the record says the harness
    // cannot measure it, and no tool definition is invented.
    expect(record.measurability).toEqual({
      tool_definitions: expect.objectContaining({ availability: 'not_measurable' }),
    })
    expect(record.content).toEqual({})
  })
})

describe('piAdapter — the inverted convention must fail loudly (R4.2)', () => {
  it('rejects pi counters fed through the inclusive conversion with TOKEN_NEGATIVE_FRESH', () => {
    // The measured failure, in the direction that goes negative: Copilot's
    // conversion subtracts cache the pi input never contained (negative
    // fresh on 293 of 307 measured spans). The wrong adapter is applied
    // deliberately, and validation must be the alarm.
    const record = copilotAdapter.normalize(rawSpan({ spanId: 's1', traceId: 't1', attributes: piCounts() }))

    expect(record.tokens.freshInput).toBe(500 - 40_000 - 400)
    expect(record.tokens.freshInput).toBeLessThan(0)
    expect(copilotAdapter.validate(record)).toMatchObject({
      severity: 'error',
      code: TOKEN_NEGATIVE_FRESH,
      location: 's1',
    })
    // The record the system would have stored under the right convention —
    // the two adapters disagree loudly on the same attribute key, which is
    // the point of the check.
    expect(piAdapter.validate(piAdapter.normalize(rawSpan({ spanId: 's1', attributes: piCounts() })))).toBeUndefined()
  })

  it('exposes the 2× double-count through the per-request indicator (R4.5)', () => {
    // The silent direction: pi's conversion applied to Copilot's inclusive
    // counters passes record validation (the sum identity holds by
    // construction) while nearly doubling input. A request root reporting
    // the harness's own total is what catches it — the match indicator is
    // the loud failure for this direction.
    const root = rawSpan({
      spanId: 'root',
      traceId: 't1',
      parentSpanId: null,
      attributes: piCounts({ input: 50_500, read: 50_000, creation: 0, output: 700 }),
    })
    const copilotTurn = rawSpan({
      spanId: 'turn1',
      traceId: 't1',
      parentSpanId: 'root',
      attributes: copilotCounts(),
    })

    const correctlyNormalized = [copilotAdapter.normalize(root), copilotAdapter.normalize(copilotTurn)]
    expect(reconcileRequest('root', correctlyNormalized)?.match).toBe(true)

    const wronglyNormalized = [copilotAdapter.normalize(root), piAdapter.normalize(copilotTurn)]
    const reconciliation = reconcileRequest('root', wronglyNormalized)

    // The wrong conversion re-adds cache the input already contained:
    // 100_500 claimed against a harness total of 50_500 — 1.99×, the "up
    // to 2×" double-count of R4.2, caught per request.
    expect(reconciliation?.match).toBe(false)
    expect(reconciliation?.expected).toBe(100_500)
    expect(reconciliation?.actual).toBe(50_500)
  })
})
