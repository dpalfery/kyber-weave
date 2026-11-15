// RED contract for Task A: the maintenance pass must not hold the event loop.
//
// The engine's `JobHost` tick runs `runMaintenancePass` (retention purge + full
// re-projection of the corpus) every 30 s. The pass is synchronous CPU on the main
// thread, so every HTTP handler queues behind it. The fix has three parts, pinned
// here as structure - never as durations:
//
// 1. The pass yields between units of work, so a handler interleaves with it.
// 2. A change-detection gate skips the projection when the corpus is unchanged,
//    while the purge keeps its own time-based cadence.
// 3. The purge's per-record N+1 becomes one set-based transaction (pinned in
//    `dash/src/canon/retention.test.ts`, next to the purge's behaviour tests).
//
// All three pins target `runMaintenancePass` with the real projection path - no
// `vi.mock` on the projection module - so the invalidation tests below exercise the
// same code the gate will guard. Projection calls are observed with prototype
// spies on `CanonStore` (`vi.spyOn(CanonStore.prototype, ...)`), the convention
// `dash/src/canon/projection.test.ts` already uses; the purge writes through
// `setContent`/`setMetadata` while only the projection writes through
// `upsertSession`/`deleteSession`, so the spies separate the two halves of the pass.

import { afterEach, describe, expect, it, vi } from 'vitest'

import { CanonStore, DETECTOR_VERSION } from '../canon/store.js'
import { CONTENT_PURGED_THROUGH_KEY } from '../canon/retention.js'
import { loadO200kCounter } from '../canon/tokens.js'
import type { CanonicalRecord, TokenUsage } from '../canon/types.js'
import type { RecordProvenance, SourceCheckpoint } from '../canon/source-state.js'
import { runMaintenancePass } from './folder-import.js'

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

function staleRecord(spanId: string): CanonicalRecord {
  return {
    spanId,
    traceId: `trace-${spanId}`,
    parentSpanId: null,
    source: 'pi:agent-7f3',
    harness: 'pi',
    name: 'chat turn',
    op: 'llm.invoke',
    kind: 'internal',
    // Older than the 14-day retention window, so the purge has units of work.
    timestamp: '2020-01-01T00:00:00.000Z',
    durationMs: 100,
    status: 'ok',
    tokens: usage(),
    content: { system_prompt: 'stale content' },
    cost: { basis: 'published', status: 'priced', value: 0.01, currency: 'USD' },
    raw: { attributes: { keep: spanId } },
  }
}

function provenanceFor(spanId: string, parserVersion: string): RecordProvenance {
  return {
    spanId,
    harnessId: 'pi',
    sourceKey: 'agent-7f3',
    nativeSessionId: null,
    nativeRecordId: null,
    sourceRevision: 'rev-1',
    parserVersion,
    importedAtUtc: '2020-01-01T00:00:00.000Z',
    locationToken: 'loc-1',
  }
}

function checkpoint(parserContractVersion: string): SourceCheckpoint {
  return {
    harnessId: 'pi',
    sourceKey: 'agent-7f3',
    providerId: 'pi',
    parserId: 'pi-agent',
    parserContractVersion,
    format: 'jsonl',
    sourceRootLabel: 'pi history',
    revisionToken: 'rev-1',
    coveredFromUtc: '2019-01-01T00:00:00.000Z',
    coveredThroughUtc: '2020-01-01T00:00:00.000Z',
    lastAttemptUtc: '2020-01-01T00:00:00.000Z',
    lastSuccessUtc: '2020-01-01T00:00:00.000Z',
    lastStatus: 'ok',
    lastErrorCode: null,
    unitCount: 1,
    recordCount: 1,
  }
}

const stores: CanonStore[] = []

function seedStore(spanIds: readonly string[]): CanonStore {
  const store = new CanonStore(':memory:')
  stores.push(store)
  store.upsertMany(spanIds.map((spanId) => staleRecord(spanId)))
  return store
}

afterEach(() => {
  vi.restoreAllMocks()
  for (const store of stores.splice(0)) store.close()
})

describe('the maintenance pass yields to the event loop', () => {
  it('lets a queued handler run before the pass itself finishes', async () => {
    // WHY this ordering and not a duration: the defect is synchronous CPU - the
    // pass never returns to the loop, so every handler waits out the whole ~26 s.
    // A handler (here a `setImmediate` stand-in) scheduled BEFORE the pass must
    // therefore run before the pass completes. No sleep, no threshold: either the
    // pass yields to the loop mid-pass and the order flips, or it does not.
    // RED today: the pass is one synchronous chain (plus microtasks, which never
    // reach the check phase), so the order is ['pass-done', 'handler'].
    //
    // The tokenizer is warmed up first on purpose: `buildSessions` loads the
    // tiktoken encoder with a dynamic import on first use, and that one-off
    // module load yields to the loop. Without the warm-up the handler can win
    // on a cold worker and the test would pass without pinning anything - the
    // defect is the 26 s of synchronous CPU, not the encoder's first load.
    await loadO200kCounter()
    const store = seedStore(['yield-1', 'yield-2', 'yield-3'])

    const order: string[] = []
    setImmediate(() => {
      order.push('handler')
    })
    await runMaintenancePass(store, new Date('2026-10-20T00:00:00.000Z'))
    order.push('pass-done')
    // Flush the check phase so the assertion reads the final order, not a prefix.
    await new Promise<void>((resolve) => setImmediate(resolve))

    expect(order).toEqual(['handler', 'pass-done'])
  })
})

describe('the maintenance gate skips the projection on an unchanged corpus', () => {
  function spyOnProjection(): { upserts: ReturnType<typeof vi.spyOn>; deletes: ReturnType<typeof vi.spyOn> } {
    const upserts = vi.spyOn(CanonStore.prototype, 'upsertSession')
    const deletes = vi.spyOn(CanonStore.prototype, 'deleteSession')
    return { upserts, deletes }
  }

  it('runs no projection call on a tick whose corpus did not change', async () => {
    // The first pass establishes the gate state; the second pass sees the same
    // corpus and must do no projection work. The purge is time-based, not
    // records-based, so it still runs: its cutoff metadata must advance.
    // RED today: there is no gate, so the second pass re-projects everything.
    const store = seedStore(['gate-1', 'gate-2'])
    const { upserts, deletes } = spyOnProjection()

    await runMaintenancePass(store, new Date('2026-10-20T00:00:00.000Z'))
    expect(upserts.mock.calls.length).toBeGreaterThan(0)

    upserts.mockClear()
    deletes.mockClear()
    await runMaintenancePass(store, new Date('2026-10-20T01:00:00.000Z'))

    expect(upserts).not.toHaveBeenCalled()
    expect(deletes).not.toHaveBeenCalled()
    expect(store.getMetadata(CONTENT_PURGED_THROUGH_KEY)).toBe('2026-10-06T01:00:00.000Z')
  })

  it('re-projects after a records write (characterization: passes before the gate exists)', async () => {
    // Every records write - ingest, wipe, the projection's own repricing
    // write-back - must bump the records-write marker the gate compares, so a
    // write between ticks re-arms the projection. Passes today because every
    // pass projects unconditionally; must keep passing once the gate lands.
    const store = seedStore(['records-1'])
    const { upserts } = spyOnProjection()

    await runMaintenancePass(store, new Date('2026-10-20T00:00:00.000Z'))
    upserts.mockClear()

    store.upsertMany([staleRecord('records-2')])
    await runMaintenancePass(store, new Date('2026-10-20T01:00:00.000Z'))

    expect(upserts.mock.calls.length).toBeGreaterThan(0)
  })

  it('re-projects after the detector version moves (characterization: passes before the gate exists)', async () => {
    // A detector bump must force one pass even with no records write, or the
    // derived views silently keep the old detector's answers. `setDetectorVersion`
    // is the store's existing stamp API; stamping an older version simulates a
    // binary that moved on while the store did not.
    const store = seedStore(['detector-1'])
    const { upserts } = spyOnProjection()

    await runMaintenancePass(store, new Date('2026-10-20T00:00:00.000Z'))
    upserts.mockClear()

    store.setDetectorVersion(DETECTOR_VERSION - 1)
    await runMaintenancePass(store, new Date('2026-10-20T01:00:00.000Z'))

    expect(upserts.mock.calls.length).toBeGreaterThan(0)
  })

  it('re-projects after a parser-contract bump invalidates a checkpoint (characterization: passes before the gate exists)', async () => {
    // `invalidateSourceCheckpoint` is the store's existing contract-bump signal:
    // canonical rows stay, the unit reopens. The gate must treat an invalidated
    // checkpoint like a records change, or the next projection would keep views
    // the new parser would not have produced.
    const store = seedStore(['contract-1'])
    store.commitSourceUnit({
      records: [staleRecord('contract-1')],
      provenance: [provenanceFor('contract-1', '1')],
      checkpoint: checkpoint('1'),
    })
    const { upserts } = spyOnProjection()

    await runMaintenancePass(store, new Date('2026-10-20T00:00:00.000Z'))
    upserts.mockClear()

    store.invalidateSourceCheckpoint('pi', 'agent-7f3', '2')
    await runMaintenancePass(store, new Date('2026-10-20T01:00:00.000Z'))

    expect(upserts.mock.calls.length).toBeGreaterThan(0)
  })
})
