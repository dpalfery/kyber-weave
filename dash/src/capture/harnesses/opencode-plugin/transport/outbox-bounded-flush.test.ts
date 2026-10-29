// F5 regression: one active flush, coalesced wake-ups, transport deadlines,
// response-body disposal, and close/abort semantics.
//
// The outbox extended a promise chain on every enqueue, so a burst against a
// held transport retained one continuation per event. These tests pin the
// fix: a burst schedules bounded work (one in-flight POST at a time), a hung
// transport is aborted at the deadline, response bodies are disposed, and
// close/flushSettled/drain always settle — even offline.

import { describe, expect, it, vi } from 'vitest'

import { createOutbox, type OutboxEvent } from './outbox.js'

const SESSION_ID = 'opencode-session-f5'

function hookEvent(kind: OutboxEvent['kind'], spanId?: string): OutboxEvent {
  return {
    kind,
    sessionId: SESSION_ID,
    turnIndex: 3,
    ...(spanId === undefined ? {} : { spanId }),
    timestamp: '2026-10-29T00:00:00.000Z',
  }
}

function okResponse(): Response {
  return new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } })
}

describe('Outbox bounded flush (F5)', () => {
  it('coalescesABurstIntoOneInFlightPost', async () => {
    let inFlight = 0
    let maxInFlight = 0
    let release!: () => void
    const gate = new Promise<void>((resolve) => {
      release = resolve
    })
    const fetchFn = vi.fn(async () => {
      inFlight += 1
      maxInFlight = Math.max(maxInFlight, inFlight)
      try {
        await gate
        return okResponse()
      } finally {
        inFlight -= 1
      }
    })
    const outbox = createOutbox({ capacity: 50, batchSize: 50, fetchFn, sleepFn: async () => {} })

    for (let i = 0; i < 10_000; i += 1) {
      outbox.enqueue(hookEvent('span.start', `span-${i}`))
    }
    // Let the scheduled flush start while the transport is still held.
    await Promise.resolve()
    await new Promise((resolve) => setTimeout(resolve, 10))
    expect(maxInFlight).toBe(1)

    release()
    await outbox.drain()
    expect(outbox.status().queued).toBe(0)
    expect(maxInFlight).toBe(1)
    // One burst ships as full batches, not one POST per event.
    expect(fetchFn.mock.calls.length).toBeLessThanOrEqual(3)
    await outbox.close()
  })

  it('abortsAHungTransportAtTheDeadline', async () => {
    let aborted = false
    const fetchFn = vi.fn(
      (_url: string, init?: RequestInit) =>
        new Promise<Response>((_resolve, reject) => {
          init?.signal?.addEventListener('abort', () => {
            aborted = true
            reject(new Error('aborted'))
          })
          if (init?.signal?.aborted === true) {
            aborted = true
            reject(new Error('aborted'))
          }
        }),
    )
    const outbox = createOutbox({
      capacity: 4,
      batchSize: 4,
      fetchFn,
      sleepFn: async () => {},
      requestTimeoutMs: 20,
    })

    outbox.enqueue(hookEvent('span.start', 'span-hung'))
    await outbox.flushSettled()

    expect(fetchFn).toHaveBeenCalledTimes(1)
    expect(aborted).toBe(true)
    const status = outbox.status()
    expect(status.failed_attempts).toBeGreaterThanOrEqual(1)
    expect(status.queued).toBe(1)
    await outbox.close()
  })

  it('disposesTheResponseBody', async () => {
    let cancelled = false
    const fetchFn = vi.fn(async () => {
      const stream = new ReadableStream({
        start(controller) {
          controller.enqueue(new TextEncoder().encode('{}'))
          // Never closes: without disposal the consumer would hang.
        },
        cancel() {
          cancelled = true
        },
      })
      return new Response(stream, { status: 200 })
    })
    const outbox = createOutbox({ capacity: 4, batchSize: 4, fetchFn, sleepFn: async () => {} })

    outbox.enqueue(hookEvent('span.start', 'span-body'))
    await outbox.drain()

    expect(cancelled).toBe(true)
    expect(outbox.status().sent_spans).toBe(1)
    expect(outbox.status().queued).toBe(0)
    await outbox.close()
  })

  it('settlesCloseAndFlushSettledWhileOffline', async () => {
    const fetchFn = vi.fn(async () => {
      throw new Error('receiver down')
    })
    const outbox = createOutbox({ capacity: 8, batchSize: 8, fetchFn, sleepFn: async () => {} })

    for (let i = 0; i < 8; i += 1) {
      outbox.enqueue(hookEvent('span.start', `span-offline-${i}`))
    }
    await outbox.flushSettled()
    expect(outbox.status().queued).toBe(8)

    // A drain against a permanently-down receiver must still settle on close.
    const drained = outbox.drain()
    await new Promise((resolve) => setTimeout(resolve, 20))
    await outbox.close()
    await expect(drained).resolves.toBeUndefined()
    await expect(outbox.flushSettled()).resolves.toBeUndefined()

    // Closed means closed: no further work is scheduled.
    const calls = fetchFn.mock.calls.length
    outbox.enqueue(hookEvent('span.start', 'span-after-close'))
    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(fetchFn.mock.calls.length).toBe(calls)
    expect(outbox.status().queued).toBe(8)
  })

  it('recoversAfterAnOutageWithoutLosingSpooledEvents', async () => {
    let receiverUp = false
    const fetchFn = vi.fn(async () => {
      if (!receiverUp) throw new Error('receiver down')
      return okResponse()
    })
    const outbox = createOutbox({ capacity: 10, batchSize: 4, fetchFn, sleepFn: async () => {} })

    for (let i = 0; i < 5; i += 1) {
      outbox.enqueue(hookEvent('span.start', `span-recover-${i}`))
    }
    await outbox.flushSettled()
    expect(outbox.status().queued).toBe(5)

    receiverUp = true
    await outbox.drain()
    expect(outbox.status().queued).toBe(0)
    expect(outbox.status().sent_spans).toBe(5)
    await outbox.close()
  })
})
