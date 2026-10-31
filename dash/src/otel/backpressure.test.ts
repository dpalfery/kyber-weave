// F3 regression: persistence failure must not allow an unlimited accepted
// OTLP backlog. The receiver's synchronous `write`/`writeLogs` paths bypassed
// the writer's `enqueue` high-water mark, so a failing store with continued
// intake retained every payload. The fix is count AND byte admission with a
// precise pre-acceptance verdict — transient 503 + Retry-After when the
// backlog is full, permanent 413 when the batch alone exceeds a ceiling —
// while already-accepted batches are retained and recover exactly once the
// store heals. In-flight batches a held async sink has not settled count
// toward the ceilings until they commit.

import { afterAll, describe, expect, it, vi } from 'vitest'

import {
  OTLP_LOGS_PATH,
  OTLP_TRACES_PATH,
  OtlpReceiver,
  type OtlpLog,
  type OtlpSpan,
} from './receiver.js'
import {
  BatchTooLargeError,
  IngestWriter,
  WriterBackpressureError,
  type SignalBatchSink,
} from './writer.js'

const TRACE_ID = '0af7651916cd43dd8448eb211c80319c'

function spanBody(request: number, spans: number): string {
  return JSON.stringify({
    resourceSpans: [
      {
        scopeSpans: [
          {
            spans: Array.from({ length: spans }, (_, i) => ({
              traceId: TRACE_ID,
              spanId: `${request.toString(16).padStart(8, '0')}${i.toString(16).padStart(8, '0')}`,
              name: `req-${request}-span-${i}`,
              startTimeUnixNano: '1756478400000000000',
              endTimeUnixNano: '1756478400100000000',
            })),
          },
        ],
      },
    ],
  })
}

function logBody(request: number, logs: number): string {
  return JSON.stringify({
    resourceLogs: [
      {
        scopeLogs: [
          {
            logRecords: Array.from({ length: logs }, (_, i) => ({
              timeUnixNano: '1756478400123456789',
              traceId: TRACE_ID,
              spanId: `${request.toString(16).padStart(8, '0')}${i.toString(16).padStart(8, '0')}`,
              body: { stringValue: `req-${request}-log-${i}` },
            })),
          },
        ],
      },
    ],
  })
}

/** A batch sink that fails until healed, recording everything it commits. */
class FailoverSink implements SignalBatchSink {
  readonly spans: OtlpSpan[] = []
  readonly logs: OtlpLog[] = []
  failing = true

  upsertMany(spans: readonly OtlpSpan[]): void {
    if (this.failing) throw new Error('simulated disk full')
    this.spans.push(...spans)
  }

  upsertLogs(logs: readonly OtlpLog[]): void {
    if (this.failing) throw new Error('simulated disk full')
    this.logs.push(...logs)
  }
}

/** An async sink the test holds open and settles by hand. */
class AsyncSink implements SignalBatchSink {
  readonly spans: OtlpSpan[] = []
  readonly logs: OtlpLog[] = []
  private readonly held: Array<{ settle: () => void; fail: (error: unknown) => void }> = []

  upsertMany(batch: readonly OtlpSpan[]): Promise<void> {
    return new Promise<void>((resolve, reject) => {
      this.held.push({
        settle: () => {
          this.spans.push(...batch)
          resolve()
        },
        fail: reject,
      })
    })
  }

  upsertLogs(batch: readonly OtlpLog[]): Promise<void> {
    return new Promise<void>((resolve, reject) => {
      this.held.push({
        settle: () => {
          this.logs.push(...batch)
          resolve()
        },
        fail: reject,
      })
    })
  }

  get heldCount(): number {
    return this.held.length
  }

  settleOldest(): void {
    this.held.shift()?.settle()
  }

  failOldest(error: unknown = new Error('simulated async persistence failure')): void {
    this.held.shift()?.fail(error)
  }
}

function failingWriter(
  sink: FailoverSink,
  overrides: { maxPendingSpans?: number; maxPendingBytes?: number } = {},
): { writer: IngestWriter; failures: unknown[] } {
  const failures: unknown[] = []
  const writer = new IngestWriter(sink, {
    batchSize: 4,
    highWaterMark: 4,
    maxPendingSpans: 8,
    maxPendingBytes: 4_096,
    ...overrides,
    onError: (error) => {
      failures.push(error)
    },
  })
  return { writer, failures }
}

const receivers: OtlpReceiver[] = []

afterAll(async () => {
  await Promise.all(receivers.map((receiver) => receiver.stop()))
})

/**
 * Settle every batch a held async sink has taken until the writer is fully
 * drained: each settle resolves on a microtask and the flush dispatches the
 * next batch after it, so the loop yields to the event loop between rounds.
 */
async function settleHeldDrain(writer: IngestWriter, sink: AsyncSink): Promise<void> {
  // Kick a drain for anything still queued: settling alone cannot start one.
  const draining = writer.flush()
  for (let round = 0; round < 1_000; round++) {
    while (sink.heldCount > 0) sink.settleOldest()
    await new Promise((resolve) => setImmediate(resolve))
    if (sink.heldCount === 0 && writer.pending + writer.pendingLogs === 0 && writer.inflightRecords === 0) {
      await draining
      return
    }
  }
  throw new Error('held sink did not drain')
}

describe('F3 writer admission under persistent persistence failure', () => {
  it('rejects batches over the count cap before acceptance, retryably', async () => {
    const sink = new FailoverSink()
    // Bytes cannot be the binding arm here: the count ceiling decides.
    const { writer } = failingWriter(sink, { maxPendingBytes: 1_000_000_000 })

    // The failing sink keeps the first batch queued, so the next batches must
    // be refused rather than retained without bound.
    writer.write(
      Array.from({ length: 4 }, (_, i) => ({ spanId: `a-${i}` }) as OtlpSpan),
      { bytes: 100 },
    )
    expect(writer.pending).toBe(4)

    // A flush failure surfaces; nothing accepted is dropped.
    await writer.flush().catch(() => {})
    expect(writer.pending).toBe(4)

    writer.write(
      Array.from({ length: 4 }, (_, i) => ({ spanId: `b-${i}` }) as OtlpSpan),
      { bytes: 100 },
    )
    expect(writer.pending).toBe(8)

    let threw: unknown = null
    try {
      writer.write([{ spanId: 'c-0' } as OtlpSpan], { bytes: 100 })
    } catch (err) {
      threw = err
    }
    expect(threw).toBeInstanceOf(WriterBackpressureError)
    expect((threw as WriterBackpressureError).retryable).toBe(true)
    // The refused batch was never queued: the backlog stays bounded.
    expect(writer.pending).toBe(8)
    await writer.stop().catch(() => {})
  })

  it('rejects batches over the byte cap before acceptance', () => {
    const sink = new FailoverSink()
    const failures: unknown[] = []
    const writer = new IngestWriter(sink, {
      batchSize: 4,
      // Counts cannot bind here: the byte ceiling decides. The seam pins the
      // ledger exactly — ten bytes per record, whatever the fixture weighs.
      maxPendingSpans: 1_000_000,
      maxPendingBytes: 300,
      measureBytes: (items) => items.length * 10,
      onError: (error) => {
        failures.push(error)
      },
    })

    writer.write([{ spanId: 'big-0' } as OtlpSpan, { spanId: 'big-1' } as OtlpSpan])
    expect(writer.pendingBytes).toBe(20)

    let threw: unknown = null
    try {
      writer.writeLogs(
        Array.from({ length: 30 }, (_, i) => ({ logId: `log-${i}` }) as OtlpLog),
      )
    } catch (err) {
      threw = err
    }
    expect(threw).toBeInstanceOf(WriterBackpressureError)
    expect(writer.pendingBytes).toBe(20)
  })

  it('refuses an oversize batch even into an empty backlog, permanently', () => {
    const sink = new FailoverSink()
    const failures: unknown[] = []
    const writer = new IngestWriter(sink, {
      batchSize: 4,
      maxPendingSpans: 2,
      maxPendingBytes: 100,
      measureBytes: (items) => items.length * 10,
      onError: (error) => {
        failures.push(error)
      },
    })

    // Four records against a two-record ceiling: oversize by count, with room
    // to spare on bytes — and nothing queued yet, so no backlog to blame.
    let byCount: unknown = null
    try {
      writer.write(
        Array.from({ length: 4 }, (_, i) => ({ spanId: `x-${i}` }) as OtlpSpan),
      )
    } catch (err) {
      byCount = err
    }
    expect(byCount).toBeInstanceOf(BatchTooLargeError)
    expect((byCount as BatchTooLargeError).retryable).toBe(false)

    // One record against a 100-byte ceiling at ten bytes a record... fits;
    // eleven records (110 bytes) do not: oversize by bytes, empty backlog.
    let byBytes: unknown = null
    try {
      writer.write(
        Array.from({ length: 11 }, (_, i) => ({ spanId: `y-${i}` }) as OtlpSpan),
      )
    } catch (err) {
      byBytes = err
    }
    expect(byBytes).toBeInstanceOf(BatchTooLargeError)

    // Neither refusal queued anything.
    expect(writer.pending).toBe(0)
    expect(writer.pendingBytes).toBe(0)
  })

  it('does not let a dishonest wire hint shrink the byte accounting', async () => {
    const sink = new FailoverSink()
    const failures: unknown[] = []
    const writer = new IngestWriter(sink, {
      batchSize: 4,
      maxPendingSpans: 1_000_000,
      maxPendingBytes: 450,
      measureBytes: (items) => items.length * 100,
      onError: (error) => {
        failures.push(error)
      },
    })

    // Zero, negative, and NaN hints all fall back to the decoded measure:
    // 200 ledgered bytes, not the hinted nothing.
    writer.write(
      [{ spanId: 'h-0' } as OtlpSpan, { spanId: 'h-1' } as OtlpSpan],
      { bytes: 0 },
    )
    expect(writer.pendingBytes).toBe(200)
    writer.write([{ spanId: 'h-2' } as OtlpSpan], { bytes: Number.NaN })
    writer.write([{ spanId: 'h-3' } as OtlpSpan], { bytes: -50 })

    // 400 ledgered bytes against the 450 ceiling: one more saturates, retryably.
    let threw: unknown = null
    try {
      writer.write([{ spanId: 'h-4' } as OtlpSpan], { bytes: 0 })
    } catch (err) {
      threw = err
    }
    expect(threw).toBeInstanceOf(WriterBackpressureError)
    expect(writer.pendingBytes).toBe(400)
    await writer.stop().catch(() => {})
  })

  it('refuses unmeasurable input with a boundary error instead of ledgering it weightless', () => {
    const sink = new FailoverSink()
    const failures: unknown[] = []
    const writer = new IngestWriter(sink, {
      batchSize: 4,
      onError: (error) => {
        failures.push(error)
      },
    })

    const circular: Record<string, unknown> = {}
    circular.self = circular
    let threw: unknown = null
    try {
      writer.write([circular as unknown as OtlpSpan])
    } catch (err) {
      threw = err
    }
    expect(threw).toBeInstanceOf(Error)
    expect((threw as Error).message).toMatch(/byte measurement failed/)
    expect(writer.pending).toBe(0)
    expect(writer.pendingBytes).toBe(0)
  })

  it('counts in-flight batches a held async sink has not settled toward the ceilings', async () => {
    const sink = new AsyncSink()
    const failures: unknown[] = []
    const writer = new IngestWriter(sink, {
      batchSize: 2,
      highWaterMark: 2,
      maxPendingSpans: 4,
      maxPendingBytes: 1_000_000,
      measureBytes: (items) => items.length * 10,
      onError: (error) => {
        failures.push(error)
      },
    })

    writer.write([{ spanId: 'a-0' } as OtlpSpan, { spanId: 'a-1' } as OtlpSpan])
    // The sink holds the batch: spliced out of the queue, still retained.
    expect(writer.inflightRecords).toBe(2)
    expect(writer.pending).toBe(0)

    writer.write([{ spanId: 'b-0' } as OtlpSpan, { spanId: 'b-1' } as OtlpSpan])
    expect(writer.pending).toBe(2)

    // Two in flight plus two queued against a four-record ceiling: the next
    // batch is saturated even though the queues alone would fit it.
    let threw: unknown = null
    try {
      writer.write([{ spanId: 'c-0' } as OtlpSpan])
    } catch (err) {
      threw = err
    }
    expect(threw).toBeInstanceOf(WriterBackpressureError)

    // Settle the held batch, which dispatches the queued one; settle that
    // too, then the writer drains cleanly.
    const draining = writer.flush()
    await vi.waitFor(() => expect(sink.heldCount).toBe(1))
    sink.settleOldest()
    await vi.waitFor(() => expect(sink.spans).toHaveLength(2))
    await vi.waitFor(() => expect(sink.heldCount).toBe(1))
    sink.settleOldest()
    await draining

    expect(sink.spans.map((span) => span.spanId)).toEqual(['a-0', 'a-1', 'b-0', 'b-1'])
    expect(writer.pending).toBe(0)
    expect(writer.inflightRecords).toBe(0)
    expect(writer.pendingBytes).toBe(0)
    await writer.stop()
  })

  it('restores the exact admission totals when a held batch fails instead of committing', async () => {
    const sink = new AsyncSink()
    const failures: unknown[] = []
    const writer = new IngestWriter(sink, {
      batchSize: 2,
      highWaterMark: 2,
      maxPendingSpans: 4,
      maxPendingBytes: 1_000_000,
      measureBytes: (items) => items.length * 10,
      onError: (error) => {
        failures.push(error)
      },
    })

    writer.write([{ spanId: 'a-0' } as OtlpSpan, { spanId: 'a-1' } as OtlpSpan])
    writer.write([{ spanId: 'b-0' } as OtlpSpan, { spanId: 'b-1' } as OtlpSpan])
    expect(writer.inflightRecords).toBe(2)
    expect(writer.pending).toBe(2)

    // The held batch fails instead of committing: it goes back to the head
    // of the queue, and the ledgers come back exactly as admitted — the
    // refusal leaks no in-flight reservation and double-counts nothing.
    sink.failOldest()
    await expect(writer.flush()).rejects.toThrow(/simulated async persistence failure/)
    expect(writer.pending).toBe(4)
    expect(writer.inflightRecords).toBe(0)
    expect(writer.pendingBytes).toBe(40)

    // A small batch still fits exactly as before the failure: no phantom
    // inflation from the restore.
    expect(writer.checkAdmission([{ spanId: 'probe' } as OtlpSpan])).toBe('saturated')
    expect(writer.checkAdmission([])).toBe('ok')

    await settleHeldDrain(writer, sink)
    expect(sink.spans.map((span) => span.spanId)).toEqual(['a-0', 'a-1', 'b-0', 'b-1'])
    expect(writer.pending).toBe(0)
    expect(writer.pendingBytes).toBe(0)
    await writer.stop()
  })

  it('preserves accepted batches and recovers them exactly once the store heals', async () => {
    const sink = new FailoverSink()
    const { writer } = failingWriter(sink)

    writer.write(
      Array.from({ length: 4 }, (_, i) => ({ spanId: `span-${i}` }) as OtlpSpan),
      { bytes: 400 },
    )
    writer.writeLogs(
      Array.from({ length: 2 }, (_, i) => ({ logId: `log-${i}` }) as OtlpLog),
      { bytes: 200 },
    )
    // Failures surface; nothing accepted is dropped.
    await writer.flush().catch(() => {})
    expect(writer.pending).toBe(4)

    sink.failing = false
    await writer.flush()

    expect(sink.spans.map((span) => span.spanId)).toEqual(['span-0', 'span-1', 'span-2', 'span-3'])
    expect(sink.logs.map((log) => log.logId)).toEqual(['log-0', 'log-1'])
    expect(writer.pending).toBe(0)
    expect(writer.pendingBytes).toBe(0)
    await writer.stop()
  })

  it('keeps concurrent enqueue calls behind a held sink within the ceilings instead of piling on', async () => {
    const sink = new AsyncSink()
    const failures: unknown[] = []
    const writer = new IngestWriter(sink, {
      batchSize: 2,
      highWaterMark: 1,
      maxPendingSpans: 8,
      maxPendingBytes: 1_000_000,
      measureBytes: (items) => items.length * 10,
      onError: (error) => {
        failures.push(error)
      },
    })

    // Ten producers race behind a sink that settles nothing: admission runs
    // before adding, so the first four batches are accepted and block while
    // the other six are refused instead of piling past the ceiling.
    const pending = Array.from({ length: 10 }, (_, batch) =>
      writer
        .enqueue([
          { spanId: `q${batch}-0` } as OtlpSpan,
          { spanId: `q${batch}-1` } as OtlpSpan,
        ])
        .then(
          () => 'admitted' as const,
          (error: unknown) => error,
        ),
    )
    // Every admit/push ran synchronously in call order before any waiter
    // could proceed: four accepted-and-blocked, six refused up front.
    // Queued plus in-flight, exactly at the ceiling — never past it.
    expect(writer.pending + writer.inflightRecords).toBe(8)
    expect(writer.pendingBytes + writer.inflightBytes).toBe(80)
    await settleHeldDrain(writer, sink)
    const outcomes = await Promise.all(pending)
    expect(outcomes.filter((outcome) => outcome === 'admitted')).toHaveLength(4)
    const refused = outcomes.filter((outcome) => outcome !== 'admitted')
    expect(refused).toHaveLength(6)
    for (const error of refused) expect(error).toBeInstanceOf(WriterBackpressureError)

    expect(sink.spans.map((span) => span.spanId)).toEqual([
      'q0-0',
      'q0-1',
      'q1-0',
      'q1-1',
      'q2-0',
      'q2-1',
      'q3-0',
      'q3-1',
    ])
    await writer.stop()
  })

  it('refuses a non-finite custom byte measure instead of poisoning the ledger', () => {
    const sink = new FailoverSink()
    const failures: unknown[] = []
    const writer = new IngestWriter(sink, {
      batchSize: 4,
      measureBytes: () => Number.NaN,
      onError: (error) => {
        failures.push(error)
      },
    })

    let threw: unknown = null
    try {
      writer.write([{ spanId: 'nan-0' } as OtlpSpan])
    } catch (err) {
      threw = err
    }
    expect(threw).toBeInstanceOf(Error)
    expect((threw as Error).message).toMatch(/non-finite/)
    // Nothing was ledgered: the ceilings still bind exactly as before.
    expect(writer.pending).toBe(0)
    expect(writer.pendingBytes).toBe(0)
    expect(() => writer.checkAdmission([{ spanId: 'probe' } as OtlpSpan])).toThrow(/non-finite/)
    expect(writer.pending).toBe(0)
  })
})

describe('F3 receiver sheds load with a precise status before acceptance', () => {
  it('POST /v1/traces returns 200 while there is room, then 503 with Retry-After; accepted spans recover exactly', async () => {
    const sink = new FailoverSink()
    // Counts cannot bind here: the byte ceiling decides, through real HTTP.
    const failures: unknown[] = []
    const writer = new IngestWriter(sink, {
      batchSize: 4,
      highWaterMark: 4,
      maxPendingSpans: 1_000_000,
      maxPendingBytes: 3_000,
      onError: (error) => {
        failures.push(error)
      },
    })
    const receiver = new OtlpReceiver({ port: 0, store: writer })
    receivers.push(receiver)
    await receiver.start()
    const url = `http://127.0.0.1:${receiver.port}`

    const acceptedBodies: string[] = []
    let rejected = 0
    for (let request = 0; request < 10; request++) {
      const response = await fetch(`${url}${OTLP_TRACES_PATH}`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: spanBody(request, 4),
      })
      if (response.status === 200) {
        acceptedBodies.push(`req-${request}`)
      } else {
        rejected += 1
        expect(response.status).toBe(503)
        expect(response.headers.get('retry-after')).not.toBeNull()
      }
    }

    // Load was shed: some requests were refused, and the backlog stayed bounded.
    expect(rejected).toBeGreaterThan(0)
    expect(acceptedBodies.length).toBeGreaterThan(0)
    expect(writer.pending + writer.inflightRecords).toBeLessThanOrEqual(1_000_000)
    expect(writer.pendingBytes + writer.inflightBytes).toBeLessThanOrEqual(3_000)

    // No silent-200 drop: every accepted span recovers once the store heals.
    sink.failing = false
    await writer.flush()
    const committed = sink.spans.map((span) => (span.name ?? '').split('-').slice(0, 2).join('-'))
    expect(committed).toEqual(acceptedBodies.flatMap((req) => Array(4).fill(req)))
    await writer.stop().catch(() => {})
  })

  it('POST /v1/logs sheds the same way and accepted logs recover exactly', async () => {
    const sink = new FailoverSink()
    const failures: unknown[] = []
    const writer = new IngestWriter(sink, {
      batchSize: 4,
      highWaterMark: 4,
      maxPendingSpans: 1_000_000,
      maxPendingBytes: 2_000,
      onError: (error) => {
        failures.push(error)
      },
    })
    const receiver = new OtlpReceiver({ port: 0, store: writer })
    receivers.push(receiver)
    await receiver.start()
    const url = `http://127.0.0.1:${receiver.port}`

    const accepted: string[] = []
    let rejected = 0
    for (let request = 0; request < 10; request++) {
      const response = await fetch(`${url}${OTLP_LOGS_PATH}`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: logBody(request, 2),
      })
      if (response.status === 200) {
        accepted.push(`req-${request}`)
      } else {
        rejected += 1
        expect(response.status).toBe(503)
        expect(response.headers.get('retry-after')).not.toBeNull()
      }
    }

    expect(rejected).toBeGreaterThan(0)
    expect(accepted.length).toBeGreaterThan(0)

    sink.failing = false
    await writer.flush()
    expect(sink.logs.map((log) => String((log.body as string) ?? '').split('-').slice(0, 2).join('-'))).toEqual(
      accepted.flatMap((req) => Array(2).fill(req)),
    )
    await writer.stop().catch(() => {})
  })

  it('POST /v1/traces refuses an oversize-first batch with 413 and queues nothing', async () => {
    const sink = new FailoverSink()
    const failures: unknown[] = []
    const writer = new IngestWriter(sink, {
      batchSize: 4,
      maxPendingSpans: 1_000_000,
      // Far below one real request body: the very first batch is oversize.
      maxPendingBytes: 100,
      onError: (error) => {
        failures.push(error)
      },
    })
    const receiver = new OtlpReceiver({ port: 0, store: writer })
    receivers.push(receiver)
    await receiver.start()
    const url = `http://127.0.0.1:${receiver.port}`

    const response = await fetch(`${url}${OTLP_TRACES_PATH}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: spanBody(0, 4),
    })
    expect(response.status).toBe(413)
    expect((await response.json()) as unknown).toMatchObject({
      error: { code: 'OTLP_BATCH_TOO_LARGE' },
    })
    // Permanent, not retryable: no Retry-After is advertised.
    expect(response.headers.get('retry-after')).toBeNull()
    // The refused batch was never queued — not even into an empty backlog.
    expect(writer.pending).toBe(0)
    expect(writer.pendingBytes).toBe(0)
    await writer.stop().catch(() => {})
  })

  it('POST /v1/logs refuses an oversize-first batch with 413 and queues nothing', async () => {
    const sink = new FailoverSink()
    const failures: unknown[] = []
    const writer = new IngestWriter(sink, {
      batchSize: 4,
      maxPendingSpans: 1_000_000,
      maxPendingBytes: 100,
      onError: (error) => {
        failures.push(error)
      },
    })
    const receiver = new OtlpReceiver({ port: 0, store: writer })
    receivers.push(receiver)
    await receiver.start()
    const url = `http://127.0.0.1:${receiver.port}`

    const response = await fetch(`${url}${OTLP_LOGS_PATH}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: logBody(0, 2),
    })
    expect(response.status).toBe(413)
    expect((await response.json()) as unknown).toMatchObject({
      error: { code: 'OTLP_BATCH_TOO_LARGE' },
    })
    expect(response.headers.get('retry-after')).toBeNull()
    expect(writer.pendingLogs).toBe(0)
    expect(writer.pendingBytes).toBe(0)
    await writer.stop().catch(() => {})
  })

  it('POST /v1/traces counts batches a held async sink has not settled toward the ceiling', async () => {
    const sink = new AsyncSink()
    const failures: unknown[] = []
    const writer = new IngestWriter(sink, {
      batchSize: 4,
      highWaterMark: 4,
      maxPendingSpans: 10,
      maxPendingBytes: 1_000_000_000,
      onError: (error) => {
        failures.push(error)
      },
    })
    const receiver = new OtlpReceiver({ port: 0, store: writer })
    receivers.push(receiver)
    await receiver.start()
    const url = `http://127.0.0.1:${receiver.port}`

    const acceptedBodies: string[] = []
    let rejected = 0
    for (let request = 0; request < 10; request++) {
      const response = await fetch(`${url}${OTLP_TRACES_PATH}`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: spanBody(request, 4),
      })
      if (response.status === 200) {
        acceptedBodies.push(`req-${request}`)
      } else {
        rejected += 1
        expect(response.status).toBe(503)
        expect(response.headers.get('retry-after')).not.toBeNull()
      }
    }

    // The sink settled nothing, yet load was shed: the in-flight batches
    // counted, so the backlog could not grow past the ceiling behind them.
    expect(sink.heldCount).toBeGreaterThan(0)
    expect(rejected).toBeGreaterThan(0)
    expect(acceptedBodies.length).toBeGreaterThan(0)
    expect(writer.pending + writer.inflightRecords).toBeLessThanOrEqual(10)

    // Settle everything the test held: every accepted span recovers, in order.
    await settleHeldDrain(writer, sink)
    const committed = sink.spans.map((span) => (span.name ?? '').split('-').slice(0, 2).join('-'))
    expect(committed).toEqual(acceptedBodies.flatMap((req) => Array(4).fill(req)))
    await writer.stop()
  })

  it('POST /v1/logs counts batches a held async sink has not settled toward the ceiling', async () => {
    const sink = new AsyncSink()
    const failures: unknown[] = []
    const writer = new IngestWriter(sink, {
      batchSize: 4,
      highWaterMark: 4,
      maxPendingSpans: 10,
      maxPendingBytes: 1_000_000_000,
      onError: (error) => {
        failures.push(error)
      },
    })
    const receiver = new OtlpReceiver({ port: 0, store: writer })
    receivers.push(receiver)
    await receiver.start()
    const url = `http://127.0.0.1:${receiver.port}`

    const accepted: string[] = []
    let rejected = 0
    for (let request = 0; request < 10; request++) {
      const response = await fetch(`${url}${OTLP_LOGS_PATH}`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: logBody(request, 2),
      })
      if (response.status === 200) {
        accepted.push(`req-${request}`)
      } else {
        rejected += 1
        expect(response.status).toBe(503)
        expect(response.headers.get('retry-after')).not.toBeNull()
      }
    }

    expect(sink.heldCount).toBeGreaterThan(0)
    expect(rejected).toBeGreaterThan(0)
    expect(accepted.length).toBeGreaterThan(0)
    expect(writer.pendingLogs + writer.inflightRecords).toBeLessThanOrEqual(10)

    await settleHeldDrain(writer, sink)
    expect(sink.logs.map((log) => String((log.body as string) ?? '').split('-').slice(0, 2).join('-'))).toEqual(
      accepted.flatMap((req) => Array(2).fill(req)),
    )
    await writer.stop()
  })
})
