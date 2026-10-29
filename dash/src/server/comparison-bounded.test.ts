import { describe, expect, it } from 'vitest'
import { CanonStore } from '../canon/store.js'
import { KyberBridge } from './bridge.js'
import { compareHarnesses } from '../analysis/compare.js'
import type { CanonicalRecord } from '../canon/types.js'
import { contentFromParts } from '../canon/types.js'

let spanCounter = 0

function turn(
  harness: string,
  overrides: Partial<CanonicalRecord> = {},
): CanonicalRecord {
  spanCounter += 1
  const tokens = {
    freshInput: 900,
    cacheRead: 100,
    cacheCreation: 0,
    output: 200,
    reportedInput: 1000,
    reportedOutput: 200,
  }
  return {
    spanId: `f9-span-${spanCounter}`,
    traceId: 'trace-f9',
    parentSpanId: null,
    source: `${harness}-source`,
    harness,
    sessionId: `session-${harness}`,
    name: `${harness}.chat`,
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: `2026-01-0${1 + (spanCounter % 8)}T00:00:00Z`,
    durationMs: 12,
    status: 'ok',
    tokens,
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
    ...overrides,
  }
}

function corpus(): CanonicalRecord[] {
  return [
    turn('pi', { content: { tool_definitions: 'x'.repeat(400) } }),
    turn('pi', { content: { tool_definitions: 'y'.repeat(800) } }),
    turn('copilot', { content: { tool_definitions: 'z'.repeat(1200) } }),
    turn('copilot', { op: 'tool.invoke', name: 'read_file' }),
    turn('gemini', { cost: { basis: 'published', status: 'priced', value: 2, currency: 'USD' } }),
  ]
}

describe('comparison bounded streaming (F9)', () => {
  it('matches compareHarnesses exactly across harnesses, costs, and empty corpora', () => {
    const store = new CanonStore(':memory:')
    try {
      const records = corpus()
      store.upsertMany(records)
      const bridge = new KyberBridge({ store })
      try {
        const actual = bridge.getComparisonTable()
        const byHarness = new Map<string, CanonicalRecord[]>()
        for (const record of records) {
          const list = byHarness.get(record.harness) ?? []
          list.push(record)
          byHarness.set(record.harness, list)
        }
        const preferred = ['copilot', 'gemini', 'pi']
        const available = [...byHarness.keys()]
        const harnesses = [
          ...preferred.filter((h) => byHarness.has(h)),
          ...available.filter((h) => !preferred.includes(h)).sort(),
        ]
        const expected = compareHarnesses(
          harnesses.map((h) => byHarness.get(h) ?? []),
          harnesses,
        )
        const expectedRows = expected.rows.map((row) => ({
          ...row,
          cells: Object.fromEntries(
            Object.entries(row.cells).map(([harness, cell]) => [
              harness,
              {
                ...cell,
                availability:
                  typeof cell.availability === 'object'
                    ? (cell.availability as { availability: unknown }).availability
                    : cell.availability,
              },
            ]),
          ),
        }))
        expect(actual.harnesses).toEqual(expected.harnesses)
        expect(actual.rows).toEqual(expectedRows)
        expect(actual.problems).toEqual(expected.problems)
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
  })

  it('streams through iterateAll without calling listAll', () => {
    const store = new CanonStore(':memory:')
    try {
      store.upsertMany(corpus())
      const bridge = new KyberBridge({ store })
      try {
        const storeAny = store as unknown as Record<string, unknown>
        const originalListAll = storeAny['listAll'] as () => unknown
        let listAllCalls = 0
        storeAny['listAll'] = () => {
          listAllCalls += 1
          throw new Error('listAll must not be called by the bounded comparison path')
        }
        try {
          const table = bridge.getComparisonTable()
          expect(table.harnesses).toContain('pi')
          expect(listAllCalls).toBe(0)
        } finally {
          storeAny['listAll'] = originalListAll
        }
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
  })

  it('releases each record so a large corpus never roots every parts payload', () => {
    const store = new CanonStore(':memory:')
    try {
      const big = Array.from({ length: 20 }, (_, i) =>
        turn(i % 2 === 0 ? 'pi' : 'copilot', {
          content: { tool_definitions: `tool-${i}-` + 'x'.repeat(50_000) },
        }),
      )
      store.upsertMany(big)
      const bridge = new KyberBridge({ store })
      try {
        const storeAny = store as unknown as Record<string, unknown>
        let livePeak = 0
        let live = 0
        let iterateCalls = 0
        const originalIterate = (store as CanonStore).iterateAll.bind(store)
        storeAny['iterateAll'] = function* () {
          iterateCalls += 1
          for (const record of originalIterate()) {
            live += 1
            livePeak = Math.max(livePeak, live)
            try {
              yield record
            } finally {
              live -= 1
            }
          }
        }
        const table = bridge.getComparisonTable()
        expect(table.harnesses).toEqual(expect.arrayContaining(['pi', 'copilot']))
        // The bounded path must walk the corpus through the streaming
        // iterator, never by rooting listAll()'s array.
        expect(iterateCalls).toBeGreaterThan(0)
        // Streaming holds at most one live record at a time; rooting the
        // whole corpus would hold all 20 simultaneously.
        expect(livePeak).toBeLessThanOrEqual(1)
        expect(live).toBe(0)
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
  })

  it('preserves empty and missing semantics without invented zeros', () => {
    const store = new CanonStore(':memory:')
    try {
      const bridge = new KyberBridge({ store })
      try {
        expect(bridge.getComparisonTable()).toEqual({ harnesses: [], rows: [], problems: [] })
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
    const store2 = new CanonStore(':memory:')
    try {
      store2.upsertMany([turn('pi', { op: 'tool.invoke', name: 'read_file' })])
      const bridge2 = new KyberBridge({ store: store2 })
      try {
        const table = bridge2.getComparisonTable()
        const perTurn = table.rows.find((r) => (r as { metric?: unknown }).metric === 'tokens_per_turn')
        expect(perTurn).toBeDefined()
        const cell = (perTurn as unknown as { cells: Record<string, { render: string; value?: unknown }> }).cells.pi
        expect(cell.render).toBe('no turns')
        expect(cell.value).toBeUndefined()
      } finally {
        bridge2.close()
      }
    } finally {
      store2.close()
    }
  })
})
