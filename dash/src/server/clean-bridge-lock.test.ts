// `KyberBridge.cleanDatabase` with an injected store (issue #312, review F4):
// the injected-`store` path must hold the store refresh lock, exactly like the
// file path, so concurrent cleans and refreshes serialize. A busy lock
// surfaces as `CLEAN_BUSY` and wipes nothing.
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { KyberBridge } from './bridge.js'

const { acquireStoreRefreshLockMock } = vi.hoisted(() => ({
  acquireStoreRefreshLockMock: vi.fn(),
}))

vi.mock('../refresh/lock.js', () => ({
  acquireStoreRefreshLock: acquireStoreRefreshLockMock,
}))

function seedRecord(store: CanonStore, spanId: string, harness: string): void {
  store.upsert({
    spanId,
    traceId: `trace-${spanId}`,
    parentSpanId: null,
    source: 'test:clean-lock',
    harness,
    sessionId: `${harness}-session`,
    name: 'probe turn',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: '2026-10-01T12:00:00.000Z',
    durationMs: 100,
    status: 'ok',
    tokens: {
      freshInput: 10,
      cacheRead: 0,
      cacheCreation: 0,
      output: 5,
      reasoning: 0,
      reportedInput: 10,
      reportedOutput: 5,
    },
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
  })
}

beforeEach(() => {
  acquireStoreRefreshLockMock.mockReset()
})

function acquiredLock(): { release: ReturnType<typeof vi.fn> } {
  const release = vi.fn(async () => {})
  acquireStoreRefreshLockMock.mockResolvedValue({
    outcome: 'acquired',
    handle: { token: 'test-token', release, verifyStillOwner: async () => true },
  })
  return { release }
}

describe('KyberBridge.cleanDatabase with an injected store holds the refresh lock (issue #312 F4)', () => {
  it('acquires the lock around the clean and releases it afterwards', async () => {
    const { release } = acquiredLock()
    const store = new CanonStore(':memory:')
    const bridge = new KyberBridge({ store })
    try {
      seedRecord(store, 'span-pi', 'pi')

      const report = await bridge.cleanDatabase({ all: true, confirm: true, reingestWeeks: null })

      expect(report.harnesses).toEqual(['*'])
      expect(store.count()).toBe(0)
      expect(acquireStoreRefreshLockMock).toHaveBeenCalledTimes(1)
      expect(release).toHaveBeenCalledTimes(1)
    } finally {
      bridge.close()
      store.close()
    }
  })

  it('surfaces CLEAN_BUSY without wiping when the lock is held', async () => {
    acquireStoreRefreshLockMock.mockResolvedValue({ outcome: 'timed-out' })
    const store = new CanonStore(':memory:')
    const bridge = new KyberBridge({ store })
    try {
      seedRecord(store, 'span-pi', 'pi')

      await expect(
        bridge.cleanDatabase({ all: true, confirm: true, reingestWeeks: null }),
      ).rejects.toMatchObject({ code: 'CLEAN_BUSY' })
      expect(store.count()).toBe(1)
    } finally {
      bridge.close()
      store.close()
    }
  })
})
