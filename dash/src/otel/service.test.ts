import { afterEach, describe, expect, it, vi } from 'vitest'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import {
  DEFAULT_PENDING_TTL_MS,
  ingestLogBatch,
  type OtlpLog,
} from '../canon/log-ingest.js'
import { CanonStore } from '../canon/store.js'
import { startOtlpCollectorService } from './service.js'
import type { OtlpSpan } from './receiver.js'

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

// Canonical projection (plan: "Canonical projection correction", T20 → T21):
// the live collector accepts spans and logs but must also keep the derived
// session cache fresh — a claimable span becomes a persisted derived session
// WITHOUT a static refresh, a matching log enrichment reprojects the session
// so its payload measurably changes, accepted records survive a projection
// failure, and close() drains the writer and the projector before the store.
//
// The scheduler is reached through the service handle rather than imported
// from `../canon/projection.js` on purpose: this file must keep loading and
// its pre-existing regression must keep running while the seam module does
// not exist.

const TRACE_ID = '0af7651916cd43dd8448eb211c80319c'
const SPAN_ID = 'b7ad6b7169203331'
const SECOND_TRACE_ID = '11bb22cc33dd44ee55ff001122334455'
const SECOND_SPAN_ID = '11bb22cc33dd44ee'

/**
 * A span the Claude Code adapter's attribute fingerprint claims (R6.2), so
 * the live writer's batch sink accepts it as a committed canonical record.
 * Claude Code exports no session attribute on this span, so the derived
 * session is keyed by the trace id.
 */
function claimableSpan(over: Partial<OtlpSpan> = {}): OtlpSpan {
  return {
    traceId: TRACE_ID,
    spanId: SPAN_ID,
    parentSpanId: null,
    name: 'llm_request',
    kind: 'client',
    startTimeUnixNano: '1756980000000000000',
    endTimeUnixNano: '1756980000100000000',
    timestamp: '2026-09-04T10:00:00.000Z',
    durationMs: 100,
    status: { code: 'ok' },
    resource: { 'service.name': 'projection-fixture' },
    scope: {},
    attributes: {
      'claude.deployment_mode': '1p',
      input_tokens: 10,
      output_tokens: 5,
    },
    ...over,
  }
}

/**
 * The matching Claude Code API-request-body log for the claimable span: it
 * correlates by exact trace and span identity and carries the request body
 * whose parsed tool definition is what measurably changes the projected
 * payload — Claude Code telemetry alone reports no tool definitions.
 */
function enrichingLog(): OtlpLog {
  return {
    logId: 'projection-fixture-api-body-1',
    traceId: TRACE_ID,
    spanId: SPAN_ID,
    sessionId: null,
    timestamp: '2026-09-04T10:00:00.000Z',
    body: 'claude api request body',
    attributes: {
      'claude_code.api_request_body': JSON.stringify({
        system: [{ type: 'text', text: 'Projection fixture system prompt.' }],
        tools: [
          {
            name: 'projection_fixture_tool',
            server: 'projection-fixture-server',
            description: 'Fixture tool definition.',
            input_schema: {
              type: 'object',
              properties: { path: { type: 'string' } },
              required: ['path'],
            },
          },
        ],
      }),
    },
    resource: {},
    scope: {},
  }
}

type ProjectionScheduler = {
  request(): Promise<void>
  drain(): Promise<void>
  close(): Promise<void>
}

type CollectorService = Awaited<ReturnType<typeof startOtlpCollectorService>> & {
  scheduler?: ProjectionScheduler
}

function projectionScheduler(service: CollectorService): ProjectionScheduler {
  expect(service.scheduler, 'the collector must expose its projection scheduler').toBeDefined()
  return service.scheduler!
}

type SessionPayloadView = {
  harness?: string
  tools?: Array<{ schema_tokens?: number }>
}

describe('startOtlpCollectorService canonical projection', () => {
  it('turns an accepted claimable span into a derived session without a static refresh', async () => {
    const service = await startOtlpCollectorService({ port: 0, dbPath: ':memory:' })
    try {
      await service.writer.enqueue([claimableSpan()])
      await service.writer.flush()

      // The fixture is valid independently of the projection seam: the writer
      // committed the record today's collector is known to persist.
      expect(service.canon.get(SPAN_ID)).toBeDefined()

      await projectionScheduler(service).drain()

      // Records alone are not the projection: the derived session and its
      // persisted payload exist without any static refresh having run.
      expect(service.canon.getSessionPayload(TRACE_ID)).toMatchObject({ harness: 'claude-code' })
      expect(service.canon.listSessions().map((session) => session.sessionId)).toContain(TRACE_ID)
      expect(service.canon.listRefreshRuns()).toEqual([])
    } finally {
      await service.close()
    }
  })

  it('reprojects an enriched session so the persisted payload measurably changes', async () => {
    const service = await startOtlpCollectorService({ port: 0, dbPath: ':memory:' })
    try {
      await service.writer.enqueue([claimableSpan()])
      await service.writer.flush()
      await projectionScheduler(service).drain()

      const before = service.canon.getSessionPayload(TRACE_ID) as SessionPayloadView | undefined
      expect(before).toBeDefined()
      // Claude Code telemetry carries no tool definitions to count.
      expect(before?.tools).toEqual([])

      service.writer.writeLogs([enrichingLog()])
      await service.writer.flush()
      await projectionScheduler(service).drain()

      const after = service.canon.getSessionPayload(TRACE_ID) as SessionPayloadView | undefined
      expect(after).not.toEqual(before)
      // The measured change: the request-body tool definition is now counted
      // in the reprojected payload.
      expect(after?.tools).toHaveLength(1)
      expect(after?.tools?.[0]?.schema_tokens).toBeGreaterThan(0)
    } finally {
      await service.close()
    }
  })

  it('keeps accepted records when a projection fails and retries on the next accepted batch', async () => {
    const service = await startOtlpCollectorService({ port: 0, dbPath: ':memory:' })
    const reported: string[] = []
    const errors = vi.spyOn(console, 'error').mockImplementation((...args: unknown[]) => {
      reported.push(args.map((arg) => (arg instanceof Error ? arg.message : String(arg))).join(' '))
    })
    try {
      const scheduler = projectionScheduler(service)
      // Deterministic projection failure: every derived-session write aborts,
      // and only that — records and every other table stay writable. This is
      // the injected-failure stand-in for a derivation error.
      service.canon.tokenCache().exec(
        `CREATE TRIGGER projection_failure_fixture
         BEFORE INSERT ON session
         BEGIN SELECT RAISE(ABORT, 'projection failure injected'); END`,
      )

      await service.writer.enqueue([claimableSpan()])
      // This flush must resolve: accepted work is not failed because a later
      // projection of it errored.
      await service.writer.flush()
      await scheduler.drain()

      expect(service.canon.get(SPAN_ID)).toBeDefined()
      expect(service.canon.getSessionPayload(TRACE_ID)).toBeUndefined()
      expect(reported.some((line) => line.includes('projection failure injected'))).toBe(true)

      service.canon.tokenCache().exec('DROP TRIGGER projection_failure_fixture')
      await service.writer.enqueue([
        claimableSpan({ traceId: SECOND_TRACE_ID, spanId: SECOND_SPAN_ID }),
      ])
      await service.writer.flush()
      await scheduler.drain()

      // The retry recovered the failed session too, not only the new one —
      // the failed pass's dirty work was retained, not dropped.
      expect(service.canon.getSessionPayload(TRACE_ID)).toMatchObject({ harness: 'claude-code' })
      expect(service.canon.getSessionPayload(SECOND_TRACE_ID)).toMatchObject({ harness: 'claude-code' })
    } finally {
      errors.mockRestore()
      await service.close()
    }
  })

  it('drains the writer and the projection before closing the store', async () => {
    const root = mkdtempSync(join(tmpdir(), 'kyberdash-projection-close-'))
    try {
      const service = await startOtlpCollectorService({ port: 0, dbPath: join(root, 'canon.db') })
      // Enqueued but under the batch size: at close time the span is still
      // only in the writer's queue, so a store closed before the writer
      // drained would lose it.
      await service.writer.enqueue([claimableSpan()])
      await service.close()

      const reopened = new CanonStore(join(root, 'canon.db'))
      try {
        // The writer drained before the SQLite close.
        expect(reopened.get(SPAN_ID)).toBeDefined()
        // The projection drained before the SQLite close too: the derived
        // session is on disk, which a records-only close ordering never
        // produces.
        expect(reopened.getSessionPayload(TRACE_ID)).toMatchObject({ harness: 'claude-code' })
      } finally {
        reopened.close()
      }
    } finally {
      rmSync(root, { recursive: true, force: true })
    }
  })
})
