// Tool and schema cost (R8). The ranking multiplies schema tokens by turns
// resident, so it surfaces what a server costs to keep connected rather than
// how loudly it is used; the never-invoked subset is the removable portion
// and is reported separately; grouping reads only the ground-truth server a
// definition carries — never a split of the prefixed tool name; and the
// unused cost is a range between the cache-read floor and the fresh-input
// ceiling, because the cache behaviour behind the true figure is not in the
// telemetry. A source that reports invocations but no definitions says so
// instead of ranking zeros.

import { describe, expect, it } from 'vitest'

import {
  TOKEN_RESIDENCY_RATES,
  rankSchemas,
  type SchemaCostAnalysis,
  type ToolDefinition,
} from './schema.js'

function def(overrides: Partial<ToolDefinition> = {}): ToolDefinition {
  return { name: 'read', server: 'filesystem', tokens: 100, ...overrides }
}

/** Narrow a result to its measurable variant, failing loudly otherwise. */
function expectMeasurable(result: SchemaCostAnalysis) {
  if (!result.measurable) {
    throw new Error(
      `expected a measurable ranking, got not measurable (${result.invocationCount} invocations)`
    )
  }
  return result
}

// Anthropic-style prices for the priced-range tests: cache reads at a tenth
// of fresh input, both per million tokens, as the cost engine quotes them.
const RATES = { cacheReadRate: 0.3, freshInputRate: 3, currency: 'USD' } as const

describe('rankSchemas — resident-cost ranking (R8.1)', () => {
  it('ranks by schema tokens multiplied by turns resident', () => {
    const result = expectMeasurable(
      rankSchemas(
        [
          def({ name: 'read', tokens: 900 }),
          def({ name: 'write', tokens: 500 }),
          def({ name: 'glob', tokens: 200 }),
        ],
        12,
        ['read']
      )
    )

    expect(result.ranked.map((tool) => tool.name)).toEqual(['read', 'write', 'glob'])
    expect(result.ranked.map((tool) => tool.cost)).toEqual([10_800, 6_000, 2_400])
  })

  it('a smaller schema resident all session outranks a larger one that arrived late', () => {
    // 900 tokens × 2 turns = 1_800 against 400 × 12 = 4_800: the product
    // ranks, not the schema size — which is the point of R8.1's formula.
    const result = expectMeasurable(
      rankSchemas(
        [def({ name: 'search', tokens: 900, turnsResident: 2 }), def({ name: 'read', tokens: 400 })],
        12,
        []
      )
    )

    expect(result.ranked.map((tool) => tool.name)).toEqual(['read', 'search'])
    expect(result.ranked.map((tool) => tool.cost)).toEqual([4_800, 1_800])
  })

  it('breaks equal costs by name so the ranking is deterministic', () => {
    const result = expectMeasurable(
      rankSchemas([def({ name: 'zsearch', tokens: 300 }), def({ name: 'aread', tokens: 300 })], 5, [])
    )

    expect(result.ranked.map((tool) => tool.name)).toEqual(['aread', 'zsearch'])
  })

  it('keeps invoked and never-invoked definitions in one ranking', () => {
    const result = expectMeasurable(
      rankSchemas([def({ name: 'read' }), def({ name: 'write' })], 10, ['read'])
    )

    expect(result.ranked.map((tool) => tool.name)).toEqual(['read', 'write'])
    expect(result.ranked.map((tool) => tool.invoked)).toEqual([true, false])
  })
})

describe('rankSchemas — never-invoked cost reported separately (R8.2)', () => {
  it('lists only the tools no turn invoked, with their resident cost', () => {
    const result = expectMeasurable(
      rankSchemas(
        [
          def({ name: 'read', tokens: 300 }),
          def({ name: 'deploy', tokens: 700 }),
          def({ name: 'write', tokens: 200 }),
        ],
        10,
        ['read', 'write']
      )
    )

    expect(result.neverInvoked.map((tool) => tool.name)).toEqual(['deploy'])
    expect(result.neverInvoked.map((tool) => tool.cost)).toEqual([7_000])
    // Still in the ranking: separately reported, not ranked instead of.
    expect(result.ranked.map((tool) => tool.name)).toContain('deploy')
  })

  it('marks every entry of the separate list as not invoked', () => {
    const result = expectMeasurable(
      rankSchemas([def({ name: 'read' }), def({ name: 'deploy' })], 10, ['read'])
    )

    expect(result.neverInvoked.every((tool) => !tool.invoked)).toBe(true)
  })

  it('matches invocation names exactly — a prefixed form does not count', () => {
    // The harness may log the call as `filesystem__read` while the
    // definition is `read`. Inferring a match from the prefix is the same
    // guess R8.3 forbids for servers, applied to invocations.
    const result = expectMeasurable(rankSchemas([def({ name: 'read' })], 8, ['filesystem__read']))

    expect(result.neverInvoked.map((tool) => tool.name)).toEqual(['read'])
  })

  it('ignores invocations of tools the source never defined', () => {
    const result = expectMeasurable(rankSchemas([def({ name: 'read' })], 8, ['ghost', 'phantom']))

    expect(result.ranked.map((tool) => tool.name)).toEqual(['read'])
    expect(result.ranked[0].invoked).toBe(false)
  })
})

describe('rankSchemas — grouped by MCP server against ground truth (R8.3)', () => {
  it('sums resident cost per ground-truth server, most expensive first', () => {
    // All three are never invoked here, and still count toward their
    // servers: the rent is owed whether or not the tool was used.
    const result = expectMeasurable(
      rankSchemas(
        [
          def({ name: 'query', server: 'postgres', tokens: 600 }),
          def({ name: 'plan', server: 'postgres', tokens: 200 }),
          def({ name: 'read', server: 'filesystem', tokens: 100 }),
        ],
        10,
        []
      )
    )

    expect(result.byServer).toEqual(
      new Map([
        ['postgres', 8_000],
        ['filesystem', 1_000],
      ])
    )
    expect([...result.byServer.keys()]).toEqual(['postgres', 'filesystem'])
  })

  it('never derives a server by splitting a prefixed name — the R8.3 regression', () => {
    // The name says `postgres__query`; the telemetry said nothing about a
    // server. Splitting the identifier would group the tool under
    // `postgres` on the strength of a string delimiter that real server
    // names also contain.
    const result = expectMeasurable(
      rankSchemas([def({ name: 'postgres__query', server: undefined, tokens: 500 })], 6, [])
    )

    expect(result.byServer.size).toBe(0)
    expect(result.byServer.has('postgres')).toBe(false)
    expect(result.ranked[0].server).toBeUndefined()
  })

  it('keeps two servers that expose the same tool name distinct', () => {
    const result = expectMeasurable(
      rankSchemas(
        [
          def({ name: 'read', server: 'remote-fs', tokens: 300 }),
          def({ name: 'read', server: 'filesystem', tokens: 300 }),
        ],
        10,
        ['read']
      )
    )

    // Equal costs, so the servers order by name — deterministically, and as
    // two groups, because the ground truth names two different servers.
    expect(result.byServer).toEqual(
      new Map([
        ['filesystem', 3_000],
        ['remote-fs', 3_000],
      ])
    )
  })

  it('ranks built-in tools but leaves them out of the server grouping', () => {
    const result = expectMeasurable(
      rankSchemas(
        [def({ name: 'bash', server: undefined, tokens: 800 }), def({ name: 'read', tokens: 100 })],
        10,
        ['bash']
      )
    )

    expect(result.ranked.map((tool) => tool.name)).toEqual(['bash', 'read'])
    expect(result.ranked[0].server).toBeUndefined()
    expect(result.byServer).toEqual(new Map([['filesystem', 1_000]]))
  })
})

describe('rankSchemas — unused cost as a bounded range (R8.4)', () => {
  it('bounds the unused cost between the cache-read floor and the fresh-input ceiling', () => {
    // 700 never-invoked tokens × 10 turns = 7_000 residencies, priced at
    // cache-read 0.30 and fresh-input 3.00 per million.
    const result = expectMeasurable(
      rankSchemas(
        [def({ name: 'read', tokens: 300 }), def({ name: 'deploy', tokens: 700 })],
        10,
        ['read'],
        RATES
      )
    )

    expect(result.unusedRange.tokenResidencies).toBe(7_000)
    expect(result.unusedRange.floor).toBeCloseTo((7_000 * 0.3) / 1_000_000, 12)
    expect(result.unusedRange.ceiling).toBeCloseTo((7_000 * 3) / 1_000_000, 12)
    expect(result.unusedRange.currency).toBe('USD')
    expect(result.unusedRange.floor).toBeLessThan(result.unusedRange.ceiling)
  })

  it('states an unpriced range in token residencies: free-if-cached to full-price', () => {
    const result = expectMeasurable(rankSchemas([def({ name: 'deploy', tokens: 700 })], 10, []))

    expect(result.unusedRange).toEqual({ tokenResidencies: 7_000, floor: 0, ceiling: 7_000 })
    expect('currency' in result.unusedRange).toBe(false)
  })

  it('prices the default bounds at per-token rates of zero and one', () => {
    // The constant is the contract: no published rate may sneak in here and
    // turn a token-residency bound into a fabricated currency figure.
    expect(TOKEN_RESIDENCY_RATES).toEqual({ cacheReadRate: 0, freshInputRate: 1_000_000 })
  })

  it('counts only never-invoked residencies in the range', () => {
    const result = expectMeasurable(
      rankSchemas([def({ name: 'read', tokens: 300 }), def({ name: 'deploy', tokens: 700 })], 10, ['read'])
    )

    expect(result.unusedRange.tokenResidencies).toBe(7_000)
  })

  it('honors per-definition residency in the unused range', () => {
    const result = expectMeasurable(
      rankSchemas([def({ name: 'deploy', tokens: 700, turnsResident: 3 })], 10, [])
    )

    expect(result.unusedRange.tokenResidencies).toBe(2_100)
  })

  it('a fully-used session reports a genuine zero, not a missing figure', () => {
    const result = expectMeasurable(rankSchemas([def({ name: 'read', tokens: 300 })], 10, ['read']))

    expect(result.neverInvoked).toEqual([])
    expect(result.unusedRange).toEqual({ tokenResidencies: 0, floor: 0, ceiling: 0 })
  })

  it('orders the bounds even when a table prices cache reads above fresh input', () => {
    const result = expectMeasurable(
      rankSchemas([def({ name: 'deploy', tokens: 700 })], 10, [], {
        cacheReadRate: 5,
        freshInputRate: 1,
      })
    )

    expect(result.unusedRange.floor).toBe((7_000 * 1) / 1_000_000)
    expect(result.unusedRange.ceiling).toBe((7_000 * 5) / 1_000_000)
    expect(result.unusedRange.floor).toBeLessThanOrEqual(result.unusedRange.ceiling)
  })
})

describe('rankSchemas — not measurable without definitions (R8.5)', () => {
  it('answers not measurable with the count it refused to rank — never a zero ranking', () => {
    // pi, measured: 14 tools across 368 calls, none exported. The source
    // can prove tools ran; it cannot prove what was resident, so no
    // ranking, grouping or range exists to render — and none is fabricated.
    const result = rankSchemas([], 368, Array.from({ length: 368 }, (_, i) => `call-${i}`))

    expect(result).toEqual({ measurable: false, invocationCount: 368 })
  })

  it('carries no ranking, grouping or range to be misread as zero', () => {
    const result = rankSchemas([], 10, ['read'])

    expect(result.measurable).toBe(false)
    expect('ranked' in result).toBe(false)
    expect('byServer' in result).toBe(false)
    expect('unusedRange' in result).toBe(false)
  })

  it('a session with no tools and no invocations is measurable and empty', () => {
    // Nothing was offered and nothing ran — a different fact from being
    // unable to tell, so it is not the not-measurable answer.
    const result = expectMeasurable(rankSchemas([], 10, []))

    expect(result.ranked).toEqual([])
    expect(result.neverInvoked).toEqual([])
    expect(result.byServer.size).toBe(0)
    expect(result.unusedRange).toEqual({ tokenResidencies: 0, floor: 0, ceiling: 0 })
  })
})

// The session the requirement was written for: four servers, one of them
// dead weight. The ranking names the resident cost, the separate list names
// what to remove, the grouping names who to disconnect, and the range prices
// the waste honestly under both cache behaviours.
describe('rankSchemas — the session R8 was written for', () => {
  const result = expectMeasurable(
    rankSchemas(
      [
        def({ name: 'get-library-docs', server: 'context7', tokens: 950 }),
        def({ name: 'resolve-library-id', server: 'context7', tokens: 640 }),
        def({ name: 'search', server: 'exa', tokens: 420 }),
        def({ name: 'crawl', server: 'exa', tokens: 380 }),
        def({ name: 'read', server: 'filesystem', tokens: 310 }),
        def({ name: 'bash', server: undefined, tokens: 260 }),
      ],
      40,
      ['read', 'bash', 'search', 'crawl'],
      RATES
    )
  )

  it('puts the unused schemas at the top of the ranking', () => {
    expect(result.ranked.slice(0, 2).map((tool) => tool.name)).toEqual([
      'get-library-docs',
      'resolve-library-id',
    ])
  })

  it('reports exactly the never-invoked server as removable', () => {
    expect(result.neverInvoked.map((tool) => tool.server)).toEqual(['context7', 'context7'])
  })

  it('attributes the largest server cost to the server that earned nothing', () => {
    expect([...result.byServer.entries()][0]).toEqual(['context7', (950 + 640) * 40])
  })

  it('prices the waste as a range an order of magnitude wide', () => {
    const { tokenResidencies, floor, ceiling } = result.unusedRange

    expect(tokenResidencies).toBe((950 + 640) * 40)
    expect(floor).toBeCloseTo((tokenResidencies * 0.3) / 1_000_000, 12)
    expect(ceiling).toBeCloseTo((tokenResidencies * 3) / 1_000_000, 12)
    expect(ceiling / floor).toBeCloseTo(10, 10)
  })
})
