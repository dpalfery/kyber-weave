// Maintenance shared cadence (F1): the 30 s tick kept invoking the full
// projector whenever the corpus looked dirty, bypassing the receiver's
// ten-minute floor. These tests pin the tick to the same shared floor and
// lease every other automatic path honors — a dirty corpus inside the floor
// waits, a held lease yields to the live pass, and the purge still runs on
// its own cadence while the projection waits. Wipe semantics close the file:
// removing a harness still removes its derived sessions through a later pass.

import { afterEach, describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { CONTENT_PURGED_THROUGH_KEY } from '../canon/retention.js'
import { SHARED_PROJECTION_MIN_INTERVAL_MS, heartbeatProjectionLease, tryClaimProjectionLease, releaseProjectionLease, type ClaimedProjectionLease } from '../canon/projection-lease.js'
import type { CanonicalRecord, TokenUsage } from '../canon/types.js'
import type { RecordProvenance, SourceCheckpoint } from '../canon/source-state.js'
import { runMaintenancePass } from './folder-import.js'

const MINUTE_MS = 60_000
const T0 = Date.parse('2026-10-20T00:00:00.000Z')

function usage(): TokenUsage {
  return { freshInput: 10, cacheRead: 0, cacheCreation: 0, output: 4, reportedInput: 10, reportedOutput: 4 }
}

function freshRecord(spanId: string, sessionId: string, harness = 'pi'): CanonicalRecord {
  return {
    spanId,
    traceId: `trace-${spanId}`,
    parentSpanId: null,
    sessionId,
    source: 'pi:agent-7f3',
    harness,
    name: 'chat turn',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: '2026-10-19T12:00:00.000Z',
    durationMs: 100,
    status: 'ok',
    tokens: usage(),
    content: { system_prompt: 'fresh content' },
    cost: { basis: 'published', status: 'priced', value: 0.01, currency: 'USD' },
    raw: { attributes: { keep: spanId } },
  }
}

function provenanceFor(spanId: string, harness: string, sourceKey: string): RecordProvenance {
  return {
    spanId,
    harnessId: harness,
    sourceKey,
    nativeSessionId: `${harness}-session`,
    nativeRecordId: 'turn-1',
    sourceRevision: 'rev-1',
    parserVersion: '1',
    importedAtUtc: '2026-10-19T00:00:00.000Z',
    locationToken: 'loc-1',
  }
}

function checkpointFor(harness: string, sourceKey: string): SourceCheckpoint {
  return {
    harnessId: harness,
    sourceKey,
    providerId: harness,
    parserId: `${harness}-agent`,
    parserContractVersion: '1',
    format: 'jsonl',
    sourceRootLabel: 'history',
    revisionToken: 'rev-1',
    coveredFromUtc: '2026-10-01T00:00:00.000Z',
    coveredThroughUtc: '2026-10-19T00:00:00.000Z',
    lastAttemptUtc: '2026-10-19T00:00:00.000Z',
    lastSuccessUtc: '2026-10-19T00:00:01.000Z',
    lastStatus: 'ok',
    lastErrorCode: null,
    unitCount: 1,
    recordCount: 1,
  }
}

function seedHarness(store: CanonStore, harness: string, spanId: string, sessionId: string): void {
  const sourceKey = `session:${spanId}`
  store.commitSourceUnit({
    records: [freshRecord(spanId, sessionId, harness)],
    provenance: [provenanceFor(spanId, harness, sourceKey)],
    checkpoint: checkpointFor(harness, sourceKey),
  })
}

const stores: CanonStore[] = []

function openStore(): CanonStore {
  const store = new CanonStore(':memory:')
  stores.push(store)
  return store
}

afterEach(() => {
  vi.restoreAllMocks()
  for (const store of stores.splice(0)) store.close()
})

describe('runMaintenancePass shared cadence', () => {
  it('shares the ten-minute floor with the receiver cadence', () => {
    expect(SHARED_PROJECTION_MIN_INTERVAL_MS).toBe(10 * MINUTE_MS)
  })

  it('skips the projection inside the floor even with new records, while the purge still runs', async () => {
    const store = openStore()
    seedHarness(store, 'pi', 'floor-1', 'floor-session')
    const upserts = vi.spyOn(CanonStore.prototype, 'upsertSession')

    await runMaintenancePass(store, new Date(T0))
    expect(upserts.mock.calls.length).toBeGreaterThan(0)
    const purgedThroughFirst = store.getMetadata(CONTENT_PURGED_THROUGH_KEY)

    // New ingest moves the corpus, but the next tick is a minute later —
    // inside the shared floor — so the projection waits for a later tick.
    upserts.mockClear()
    seedHarness(store, 'pi', 'floor-2', 'floor-session')
    await runMaintenancePass(store, new Date(T0 + MINUTE_MS))

    expect(upserts).not.toHaveBeenCalled()
    // The purge keeps its own time-based cadence: it is a retention
    // deadline, not a reward for a changed corpus.
    const purgedThroughSecond = store.getMetadata(CONTENT_PURGED_THROUGH_KEY)
    expect(purgedThroughSecond).toBeDefined()
    expect((purgedThroughSecond as string) > (purgedThroughFirst as string)).toBe(true)
  })

  it('projects again once the floor has elapsed and the corpus moved', async () => {
    const store = openStore()
    seedHarness(store, 'pi', 'due-1', 'due-session')
    const upserts = vi.spyOn(CanonStore.prototype, 'upsertSession')

    await runMaintenancePass(store, new Date(T0))
    expect(upserts.mock.calls.length).toBeGreaterThan(0)

    upserts.mockClear()
    seedHarness(store, 'pi', 'due-2', 'due-session')
    await runMaintenancePass(store, new Date(T0 + SHARED_PROJECTION_MIN_INTERVAL_MS + MINUTE_MS))

    expect(upserts.mock.calls.length).toBeGreaterThan(0)
  })

  it('yields to a live foreign pass instead of projecting over it, then projects after release', async () => {
    const store = openStore()
    seedHarness(store, 'pi', 'lease-1', 'lease-session')
    const upserts = vi.spyOn(CanonStore.prototype, 'upsertSession')

    await runMaintenancePass(store, new Date(T0))
    expect(upserts.mock.calls.length).toBeGreaterThan(0)

    // A foreign automatic pass (the receiver) holds the lease across this tick.
    // Its heartbeat keeps the claim fresh: without beats the staleness gate
    // would rightly reclaim a claim this old, so the test beats it to the
    // tick the same way a live pass does.
    const pass2At = T0 + SHARED_PROJECTION_MIN_INTERVAL_MS + MINUTE_MS
    const holder = tryClaimProjectionLease(store, { owner: 'test-holder', nowMs: T0 + MINUTE_MS })
    expect(holder).not.toBeNull()
    // The live claim as of this tick: beats advance the stored bytes, and
    // only the current bytes release, so the test tracks the claim forward
    // exactly as a heartbeat driver does.
    let live = holder as ClaimedProjectionLease
    try {
      upserts.mockClear()
      seedHarness(store, 'pi', 'lease-2', 'lease-session')
      const beaten = heartbeatProjectionLease(store, live, pass2At)
      expect(beaten).not.toBeNull()
      if (beaten !== null) live = beaten
      await runMaintenancePass(store, new Date(pass2At))
      // Past the floor with a moved corpus, but the lease is held: the tick
      // skips quietly and the next tick retries.
      expect(upserts).not.toHaveBeenCalled()
    } finally {
      releaseProjectionLease(store, live)
    }

    upserts.mockClear()
    await runMaintenancePass(store, new Date(T0 + SHARED_PROJECTION_MIN_INTERVAL_MS + 2 * MINUTE_MS))
    expect(upserts.mock.calls.length).toBeGreaterThan(0)
  })

  it('a wipe still removes its derived sessions through a later pass', async () => {
    const store = openStore()
    seedHarness(store, 'pi', 'wipe-pi', 'pi-session')
    seedHarness(store, 'cursor', 'wipe-cursor', 'cursor-session')

    await runMaintenancePass(store, new Date(T0))
    expect(store.sessionCount()).toBe(2)

    const report = store.wipeHarnesses(['pi'])
    expect(report.harnesses).toEqual(['pi'])
    expect(store.get('wipe-pi')).toBeUndefined()
    expect(store.get('wipe-cursor')?.spanId).toBe('wipe-cursor')

    await runMaintenancePass(store, new Date(T0 + SHARED_PROJECTION_MIN_INTERVAL_MS + MINUTE_MS))
    expect(store.sessionCount()).toBe(1)
    expect(store.get('wipe-cursor')?.spanId).toBe('wipe-cursor')
  })
})
