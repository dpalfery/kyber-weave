import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  DEFAULT_PENDING_TTL_MS,
  ingestLogBatch,
  type OtlpLog,
} from '../canon/log-ingest.js'
import { startOtlpCollectorService } from './service.js'

function identifiedLog(): OtlpLog {
  return {
    logId: 'service-test-expired-log',
    traceId: '0af7651916cd43dd8448eb211c80319c',
    spanId: 'b7ad6b7169203331',
    sessionId: 'service-test-session',
    timestamp: '2026-09-04T10:00:00.000Z',
    body: 'pending',
    attributes: {},
    resource: {},
    scope: {},
  }
}

describe('startOtlpCollectorService pending-log lifecycle', () => {
  afterEach(() => {
    vi.useRealTimers()
  })

  it('reconciles expired pending logs without a later ingestion batch', async () => {
    vi.useFakeTimers()
    vi.setSystemTime(1_000)
    const service = await startOtlpCollectorService({ port: 0, dbPath: ':memory:' })

    try {
      expect(ingestLogBatch([identifiedLog()], service.canon)).toEqual({
        enriched: 0,
        pending: 1,
        quarantined: 0,
      })

      await vi.advanceTimersByTimeAsync(DEFAULT_PENDING_TTL_MS)

      expect(service.canon.getPendingLogs()).toEqual([])
      expect(service.canon.getQuarantinedLog('service-test-expired-log')).toMatchObject({
        logId: 'service-test-expired-log',
        reason: expect.stringMatching(/expir/i),
      })
      expect(service.canon.quarantinedLogCount()).toBe(1)
    } finally {
      await service.close()
    }
  })
})
