// T5 transport + offline/backpressure (test-first RED).
//
// Kill-receiver contract: when the loopback OTLP receiver is down, the
// plugin must spool boundedly, retry with backoff/jitter, drop oldest-first
// with a counted `dropped_spans` metric, halve the batch on 429, drain on
// resume, and never block the host session.

import { describe, expect, it, vi } from 'vitest'

import { createOpencodePlugin } from '../plugin.js'
import { buildOtlpTracesBody, createOtlpSender } from './otlp-sender.js'
import { createOutbox, type OutboxEvent } from './outbox.js'

const SESSION_ID = 'opencode-session-7f3a'

function hookEvent(kind: OutboxEvent['kind'], spanId?: string): OutboxEvent {
  return {
    kind,
    sessionId: SESSION_ID,
    turnIndex: 3,
    ...(spanId === undefined ? {} : { spanId }),
    timestamp: '2026-10-22T00:00:00.000Z',
  }
}

function okResponse(): Response {
  return new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } })
}

describe('Outbox offline spool and backpressure (T5)', () => {
  it('receiverDownSpoolsBoundedlyInsteadOfLosingDataOrBlocking', () => {
    const fetchFn = vi.fn(async () => {
      throw new Error('receiver down: fetch failed')
    })
    const sleeps: number[] = []
    const outbox = createOutbox({
      capacity: 3,
      batchSize: 10,
      fetchFn,
      sleepFn: async (ms: number) => {
        sleeps.push(ms)
      },
    })

    // Enqueue is synchronous and never throws — the host is never blocked,
    // even with the receiver down and the queue over capacity.
    for (let i = 0; i < 10; i += 1) {
      expect(() => outbox.enqueue(hookEvent('span.start', `span-${i}`))).not.toThrow()
    }

    const status = outbox.status()
    expect(status.queued).toBe(3)
    expect(status.queued).toBeLessThanOrEqual(3)
    expect(status.dropped_spans).toBe(7)
    expect(status.capacity).toBe(3)
  })

  it('dropsOldestFirstAndCountsDroppedSpans', async () => {
    const fetchFn = vi.fn(async () => {
      throw new Error('receiver down')
    })
    const outbox = createOutbox({
      capacity: 2,
      batchSize: 10,
      fetchFn,
      sleepFn: async () => {},
    })

    outbox.enqueue(hookEvent('span.start', 'span-oldest'))
    outbox.enqueue(hookEvent('span.start', 'span-middle'))
    outbox.enqueue(hookEvent('span.start', 'span-newest'))

    // Let the background flush attempt settle (it fails and keeps the queue).
    await outbox.flushSettled()

    const status = outbox.status()
    expect(status.dropped_spans).toBe(1)
    expect(status.queued).toBe(2)
    const queuedIds = outbox.peek().map((event) => event.spanId)
    expect(queuedIds).toEqual(['span-middle', 'span-newest'])
  })

  it('retriesWithBackoffAndDrainsOnResume', async () => {
    const delays: number[] = []
    let calls = 0
    const fetchFn = vi.fn(async () => {
      calls += 1
      if (calls <= 2) throw new Error(`receiver down (attempt ${calls})`)
      return okResponse()
    })
    const outbox = createOutbox({
      capacity: 10,
      batchSize: 10,
      baseBackoffMs: 100,
      maxBackoffMs: 800,
      fetchFn,
      sleepFn: async (ms: number) => {
        delays.push(ms)
      },
      randomFn: () => 0.5,
    })

    outbox.enqueue(hookEvent('span.start', 'span-a'))
    outbox.enqueue(hookEvent('span.end', 'span-a'))

    await outbox.drain()

    expect(fetchFn.mock.calls.length).toBeGreaterThanOrEqual(3)
    // Backoff grows across consecutive failures (jitter is deterministic here).
    expect(delays.length).toBeGreaterThanOrEqual(2)
    expect(delays[1]).toBeGreaterThan(delays[0]!)
    const status = outbox.status()
    expect(status.queued).toBe(0)
    expect(status.sent_spans).toBe(2)
    expect(status.dropped_spans).toBe(0)
  })

  it('halvesTheBatchWhenTheReceiverAnswers429', async () => {
    const seenBatchSizes: number[] = []
    let calls = 0
    const fetchFn = vi.fn(async (_url: string, init?: RequestInit) => {
      const body = JSON.parse(String(init?.body ?? '{}')) as {
        resourceSpans?: Array<{ scopeSpans?: Array<{ spans?: unknown[] }> }>
      }
      const spans = body.resourceSpans?.[0]?.scopeSpans?.[0]?.spans ?? []
      seenBatchSizes.push(spans.length)
      calls += 1
      if (calls === 1) return new Response('{}', { status: 429 })
      return okResponse()
    })
    const outbox = createOutbox({
      capacity: 20,
      batchSize: 8,
      fetchFn,
      sleepFn: async () => {},
      randomFn: () => 0.5,
    })

    for (let i = 0; i < 8; i += 1) {
      outbox.enqueue(hookEvent('span.start', `span-${i}`))
    }

    await outbox.drain()

    expect(seenBatchSizes[0]).toBe(8)
    // After a 429 the next attempt sends at most half the batch.
    expect(seenBatchSizes[1]).toBeLessThanOrEqual(4)
    expect(outbox.status().sent_spans).toBe(8)
  })

  it('neverBlocksTheHostWhileTheReceiverIsDown', async () => {
    const fetchFn = vi.fn(
      () =>
        new Promise<Response>(() => {
          // Hangs forever — the receiver is gone and the socket never answers.
        }),
    )
    const outbox = createOutbox({
      capacity: 4,
      batchSize: 4,
      fetchFn,
      sleepFn: async () => {},
    })
    const plugin = createOpencodePlugin({
      emit: outbox.emit,
      now: () => '2026-10-22T00:00:00.000Z',
      timeoutMs: 250,
    })

    const startedAt = Date.now()
    await expect(
      plugin.hooks['span.start']({ sessionId: SESSION_ID, turnIndex: 3, spanId: 'span-00c1' }),
    ).resolves.toBeUndefined()
    expect(Date.now() - startedAt).toBeLessThan(2000)
    expect(outbox.status().queued).toBe(1)
    await outbox.close()
  })

  it('resumeDrainDeliversEverythingSpooledDuringTheOutage', async () => {
    let receiverUp = false
    const delivered: string[] = []
    const fetchFn = vi.fn(async (_url: string, init?: RequestInit) => {
      if (!receiverUp) throw new Error('receiver down')
      const body = JSON.parse(String(init?.body ?? '{}')) as {
        resourceSpans?: Array<{ scopeSpans?: Array<{ spans?: Array<{ attributes?: unknown }> }> }>
      }
      delivered.push(JSON.stringify(body).length.toString())
      return okResponse()
    })
    const outbox = createOutbox({
      capacity: 10,
      batchSize: 4,
      fetchFn,
      sleepFn: async () => {},
    })

    outbox.enqueue(hookEvent('session.start'))
    outbox.enqueue(hookEvent('turn.start'))
    outbox.enqueue(hookEvent('span.start', 'span-1'))
    outbox.enqueue(hookEvent('span.end', 'span-1'))
    outbox.enqueue(hookEvent('turn.end'))
    await outbox.flushSettled()
    expect(outbox.status().queued).toBe(5)

    receiverUp = true
    await outbox.drain()

    expect(outbox.status().queued).toBe(0)
    expect(outbox.status().sent_spans).toBe(5)
    expect(delivered.length).toBeGreaterThan(0)
  })

  it('exposesABoundedStatusSurfaceWithDroppedSpans', () => {
    const outbox = createOutbox({ capacity: 2, batchSize: 2, sleepFn: async () => {} })
    outbox.enqueue(hookEvent('session.start'))
    outbox.enqueue(hookEvent('turn.start'))
    outbox.enqueue(hookEvent('span.start', 'span-x'))

    const status = outbox.status()
    expect(status).toMatchObject({
      queued: 2,
      capacity: 2,
      dropped_spans: 1,
    })
    expect(typeof status.sent_spans).toBe('number')
    expect(typeof status.failed_attempts).toBe('number')
    expect(status).toHaveProperty('lastError')
    expect(status).toHaveProperty('lastStatusCode')
    void outbox.close()
  })

  it('sendsSpanAnchoredOtlpToTheLoopbackReceiver', async () => {
    const fetchFn = vi.fn(async () => okResponse())
    const sender = createOtlpSender({ fetchFn })
    await sender.send([hookEvent('span.start', 'span-00c1')])

    expect(fetchFn).toHaveBeenCalledTimes(1)
    const firstCall = fetchFn.mock.calls[0] as unknown as [string, RequestInit]
    const [url, init] = firstCall
    expect(url).toBe('http://127.0.0.1:4318/v1/traces')
    const body = JSON.parse(String(init.body)) as {
      resourceSpans: Array<{ scopeSpans: Array<{ spans: Array<Record<string, unknown>> }> }>
    }
    const span = body.resourceSpans[0]?.scopeSpans[0]?.spans[0]
    expect(span).toMatchObject({ name: expect.stringContaining('span.start') })
    expect(JSON.stringify(span)).toContain(SESSION_ID)
  })

  it('buildsValidOtlpHexIdsForEveryHookKind', () => {
    const body = buildOtlpTracesBody([
      hookEvent('session.start'),
      hookEvent('turn.start'),
      hookEvent('span.start', 'span-00c1'),
      hookEvent('span.end', 'span-00c1'),
      hookEvent('turn.end'),
      hookEvent('session.end'),
    ])
    const spans = body.resourceSpans[0]?.scopeSpans[0]?.spans ?? []
    expect(spans).toHaveLength(6)
    for (const span of spans) {
      expect(span.traceId).toMatch(/^[0-9a-f]{32}$/)
      expect(span.spanId).toMatch(/^[0-9a-f]{16}$/)
    }
  })
})
