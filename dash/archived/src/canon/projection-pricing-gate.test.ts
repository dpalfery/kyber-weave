// The projection gate must notice a pricing change.
//
// WHY: a session's derived cost is priced from the bundled pricing table, not
// from the corpus. A binary whose pricing table moved — a repriced model, a
// new pricing rule — changes the money the dashboard shows while every
// record, every detector and every parser contract stays exactly as it was.
// The gate watched the records-write marker, the detector version and the
// parser contracts, and none of them can see that: a maintenance tick that
// skipped the projection would serve stale money figures until the next
// refresh.
//
// The shape follows `dash/src/refresh/maintenance-contract.test.ts`: the real
// maintenance path, with the projection observed through prototype spies on
// `CanonStore` — no `vi.mock` on the projection module, so the gate that
// guards the projection is the one under test. Only the pricing module's
// generation key is replaced, which is the input under test: it stands for
// "the running binary's pricing table moved".

import { afterEach, describe, expect, it, vi } from 'vitest'

import { runMaintenancePass } from '../refresh/folder-import.js'
import { CanonStore } from './store.js'
import type { CanonicalRecord, TokenUsage } from './types.js'

/** The pricing state the running binary reports. Only a pricing change moves it. */
const pricing = vi.hoisted(() => ({ generation: 'pricing-generation-a' }))

vi.mock('../pricing/models.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../pricing/models.js')>()
  return { ...actual, getPricingGenerationKey: () => pricing.generation }
})

function usage(): TokenUsage {
  return {
    freshInput: 10,
    cacheRead: 0,
    cacheCreation: 0,
    output: 4,
    reportedInput: 10,
    reportedOutput: 4,
  }
}

function record(spanId: string): CanonicalRecord {
  return {
    spanId,
    traceId: `trace-${spanId}`,
    parentSpanId: null,
    source: 'pi:agent-7f3',
    harness: 'pi',
    name: 'chat turn',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: '2026-10-20T00:00:00.000Z',
    durationMs: 100,
    status: 'ok',
    tokens: usage(),
    content: { system_prompt: 'content' },
    cost: { basis: 'published', status: 'priced', value: 0.01, currency: 'USD' },
    raw: { attributes: { keep: spanId } },
  }
}

const stores: CanonStore[] = []

function seedStore(spanIds: readonly string[]): CanonStore {
  const store = new CanonStore(':memory:')
  stores.push(store)
  store.upsertMany(spanIds.map((spanId) => record(spanId)))
  return store
}

afterEach(() => {
  vi.restoreAllMocks()
  pricing.generation = 'pricing-generation-a'
  for (const store of stores.splice(0)) store.close()
})

describe('the projection gate watches the pricing table', () => {
  it('re-projects after the pricing table moves', async () => {
    // The gate's other three inputs are unchanged: the corpus did not move,
    // the detector version did not move, the parser contracts did not move.
    // Only the pricing table did — and the derived costs the dashboard shows
    // are priced from it, so the projection must run again.
    const store = seedStore(['pricing-1'])
    const upserts = vi.spyOn(CanonStore.prototype, 'upsertSession')

    await runMaintenancePass(store, new Date('2026-10-20T00:00:00.000Z'))
    expect(upserts.mock.calls.length).toBeGreaterThan(0)

    upserts.mockClear()
    pricing.generation = 'pricing-generation-b'
    await runMaintenancePass(store, new Date('2026-10-20T01:00:00.000Z'))

    expect(upserts.mock.calls.length).toBeGreaterThan(0)
  })

  it('skips the projection when nothing moved, the pricing table included', async () => {
    // The counterpart: a pricing input that does not move must not arm the
    // gate. The corpus, the detector version, the parser contracts and the
    // pricing table are all where they were, so the second pass does no
    // projection work.
    const store = seedStore(['pricing-2'])
    const upserts = vi.spyOn(CanonStore.prototype, 'upsertSession')
    const deletes = vi.spyOn(CanonStore.prototype, 'deleteSession')

    await runMaintenancePass(store, new Date('2026-10-20T00:00:00.000Z'))
    expect(upserts.mock.calls.length).toBeGreaterThan(0)

    upserts.mockClear()
    deletes.mockClear()
    await runMaintenancePass(store, new Date('2026-10-20T01:00:00.000Z'))

    expect(upserts).not.toHaveBeenCalled()
    expect(deletes).not.toHaveBeenCalled()
  })
})
