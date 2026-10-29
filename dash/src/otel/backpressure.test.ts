// F3 regression: persistence failure must not allow an unlimited accepted
// OTLP backlog. The receiver's synchronous `write`/`writeLogs` paths bypass
// the writer's `enqueue` high-water mark, so a failing store with continued
// intake retains every payload. The fix is count AND byte admission with a
// retryable HTTP rejection BEFORE acceptance, while already-accepted batches
// are preserved and recover exactly once the store heals.

import { afterAll, describe, expect, it } from 'vitest'

import {
  OTLP_LOGS_PATH,
  OTLP_TRACES_PATH,
  OtlpReceiver,
  type OtlpLog,
  type OtlpSpan,
} from './receiver.js'
import {
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

function failingWriter(sink: FailoverSink): { writer: IngestWriter; failures: unknown[] } {
  const failures: unknown[] = []
  const writer = new IngestWriter(sink, {
    batchSize: 4,
    highWaterMark: 4,
    maxPendingSpans: 6,
    maxPendingBytes: 4_096,
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

describe('F3 writer admission under persistent persistence failure', () => {
  it('rejects batches over the count cap before acceptance, retryably', async () => {
    const sink = new FailoverSink()
    const { writer } = failingWriter(sink)

    // The first batch is always admitted (an empty queue takes work); the
    // failing sink keeps it queued, so the next batches must be refused
    // rather than retained without bound.
    writer.write(
      Array.from({ length: 4 }, (_, i) => ({ spanId: `a-${i}` }) as OtlpSpan),
      { bytes: 100 },
    )
    expect(writer.pending).toBe(4)

    const rejection = await writer
      .flush()
      .then(() => null)
      .catch((err: unknown) => err)
    expect(rejection).toBeInstanceOf(Error)

    let threw: unknown = null
    try {
      writer.write(
        Array.from({ length: 4 }, (_, i) => ({ spanId: `b-${i}` }) as OtlpSpan),
        { bytes: 100 },
      )
    } catch (err) {
      threw = err
    }
    expect(threw).toBeInstanceOf(WriterBackpressureError)
    expect((threw as WriterBackpressureError).retryable).toBe(true)
    // The refused batch was never queued: the backlog stays bounded.
    expect(writer.pending).toBe(4)
    await writer.stop().catch(() => {})
  })

  it('rejects batches over the byte cap before acceptance', () => {
    const sink = new FailoverSink()
    const { writer } = failingWriter(sink)

    writer.write([{ spanId: 'big-0' } as OtlpSpan], { bytes: 3_000 })
    expect(writer.pendingBytes).toBe(3_000)

    let threw: unknown = null
    try {
      writer.writeLogs([{ logId: 'log-big' } as OtlpLog], { bytes: 3_000 })
    } catch (err) {
      threw = err
    }
    expect(threw).toBeInstanceOf(WriterBackpressureError)
    expect(writer.pendingBytes).toBe(3_000)
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
})

describe('F3 receiver sheds load with a retryable status before acceptance', () => {
  it('POST /v1/traces returns 200 while there is room, then 503 with Retry-After; accepted spans recover exactly', async () => {
    const sink = new FailoverSink()
    const { writer } = failingWriter(sink)
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
    expect(writer.pending + writer.pendingLogs).toBeLessThanOrEqual(6)

    // No silent-200 drop: every accepted span recovers once the store heals.
    sink.failing = false
    await writer.flush()
    const committed = sink.spans.map((span) => (span.name ?? '').split('-').slice(0, 2).join('-'))
    expect(committed).toEqual(acceptedBodies.flatMap((req) => Array(4).fill(req)))
    await writer.stop().catch(() => {})
  })

  it('POST /v1/logs sheds the same way and accepted logs recover exactly', async () => {
    const sink = new FailoverSink()
    const { writer } = failingWriter(sink)
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
})
