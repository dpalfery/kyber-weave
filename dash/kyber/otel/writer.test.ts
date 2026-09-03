// Tests for task 6.2 (R2.4, R2.5): the receiver's port-conflict diagnostic
// and the batching/backpressure `IngestWriter`.
//
// The port-conflict tests bind one receiver to an ephemeral port, then
// start a second on the same port: the second must reject with
// `PORT_CONFLICT`, name the port, name the occupying process wherever the
// host's tools can see one, say plainly that it did not rebind, and end up
// bound nowhere (R2.4). The occupant parsers are additionally unit-tested
// against sample `lsof`/`ss`/`netstat` output from each platform, since
// which tool exists varies by host.
//
// The burst test drives 500 spans into a store slower than they arrive and
// asserts every one persists — the acceptance shape of R2.5 — both through
// a paced in-memory sink and through the real transactional `CanonStore`,
// with a fixture span→record mapper standing in for the normalization
// layer (task 5), which is where that mapping actually belongs.

import { afterAll, describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../canon/store.js'
import type { CanonicalRecord } from '../canon/types.js'
import {
  OTLP_TRACES_PATH,
  OtlpReceiver,
  PortConflictError,
  parseLsofOutput,
  parseNetstatOutput,
  parseSsOutput,
  type OtlpSpan,
} from './receiver.js'
import {
  DEFAULT_BATCH_SIZE,
  DEFAULT_FLUSH_INTERVAL_MS,
  DEFAULT_HIGH_WATER_MARK,
  IngestWriter,
  type SpanBatchSink,
} from './writer.js'

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

function makeSpans(count: number): OtlpSpan[] {
  return Array.from({ length: count }, (_, index) => ({
    traceId: '0af7651916cd43dd8448eb211c80319c',
    spanId: index.toString(16).padStart(16, '0'),
    parentSpanId: null,
    name: `span.${index}`,
    kind: 'internal',
    startTimeUnixNano: '1756478400000000000',
    endTimeUnixNano: '1756478400100000000',
    timestamp: '2025-08-29T14:40:00.000Z',
    durationMs: 100,
    status: { code: 'ok' },
    attributes: {},
    resource: {},
    scope: {},
  }))
}

/**
 * Stand-in for the normalization layer (task 5): a minimal, honest record
 * per span. Token decomposition, harness attribution and cost are the
 * adapters' job, so the fixture leaves them empty/unknown — the point here
 * is the writer's batching against the real store, not the mapping.
 */
function spanToRecord(span: OtlpSpan): CanonicalRecord {
  return {
    spanId: span.spanId,
    traceId: span.traceId,
    parentSpanId: span.parentSpanId,
    source: 'otlp',
    harness: 'unattributed',
    name: span.name,
    op: span.name,
    kind: span.kind,
    timestamp: span.timestamp,
    durationMs: span.durationMs,
    status: span.status.code,
    tokens: {
      freshInput: 0,
      cacheRead: 0,
      cacheCreation: 0,
      output: 0,
      reportedInput: 0,
      reportedOutput: 0,
    },
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
  }
}

/** A batch sink whose persistence takes `delayMs` per batch — slower than the burst arrives. */
class SlowBatchSink implements SpanBatchSink {
  readonly batches: OtlpSpan[][] = []
  readonly spans: OtlpSpan[] = []
  failNext = false

  constructor(private readonly delayMs: number) {}

  async upsertMany(batch: readonly OtlpSpan[]): Promise<void> {
    if (this.failNext) {
      this.failNext = false
      throw new Error('simulated store failure: transaction rolled back')
    }
    await sleep(this.delayMs)
    this.batches.push([...batch])
    this.spans.push(...batch)
  }
}

/** A batch sink that persists only when the test releases it, chunk by chunk. */
class GatedBatchSink implements SpanBatchSink {
  readonly batches: OtlpSpan[][] = []
  private readonly gates: Array<() => void> = []

  upsertMany(batch: readonly OtlpSpan[]): Promise<void> {
    this.batches.push([...batch])
    return new Promise<void>((resolve) => {
      this.gates.push(resolve)
    })
  }

  /** Let the oldest dispatched batch finish persisting. */
  release(): void {
    this.gates.shift()?.()
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

const started: OtlpReceiver[] = []

afterAll(async () => {
  await Promise.all(started.map((receiver) => receiver.stop()))
})

/** Capture a rejection as a value so its fields can be asserted. */
async function captureRejection(promise: Promise<void>): Promise<unknown> {
  return await promise.then(
    () => null,
    (err: unknown) => err,
  )
}

// ---------------------------------------------------------------------------
// IngestWriter (R2.5)
// ---------------------------------------------------------------------------

describe('IngestWriter batching and backpressure (R2.5)', () => {
  it('exposes coherent defaults', () => {
    expect(DEFAULT_BATCH_SIZE).toBeGreaterThan(0)
    expect(DEFAULT_FLUSH_INTERVAL_MS).toBeGreaterThan(0)
    expect(DEFAULT_HIGH_WATER_MARK).toBeGreaterThanOrEqual(DEFAULT_BATCH_SIZE)
  })

  it('flush persists queued spans in FIFO order, batch-sized, emptying the queue', async () => {
    const sink = new SlowBatchSink(0)
    const writer = new IngestWriter(sink, { batchSize: 8 })
    const spans = makeSpans(10)

    await writer.enqueue(spans)
    await writer.flush()

    expect(sink.spans).toEqual(spans) // arrival order, nothing reordered
    expect(sink.batches.map((batch) => batch.length)).toEqual([8, 2])
    expect(writer.pending).toBe(0)
  })

  it('a 500-span burst against a store slower than arrival: all 500 persist, none dropped', async () => {
    const sink = new SlowBatchSink(20) // 100-span batches, 20 ms each
    const writer = new IngestWriter(sink, { batchSize: 100, highWaterMark: 250 })
    const burst = makeSpans(500)

    // Five producers enqueue faster than the sink persists (five instant
    // 100-span arrivals vs 100 spans per 20 ms).
    const began = Date.now()
    await Promise.all(
      [0, 1, 2, 3, 4].map((chunk) => writer.enqueue(burst.slice(chunk * 100, chunk * 100 + 100))),
    )
    const enqueued = Date.now()
    await writer.stop()

    // Every span persisted exactly once, in arrival order, none dropped.
    expect(sink.spans).toHaveLength(500)
    expect(sink.spans.map((span) => span.spanId)).toEqual(burst.map((span) => span.spanId))
    expect(writer.pending).toBe(0)
    // The work went as batches, not span-at-a-time writes.
    expect(sink.batches.map((batch) => batch.length)).toEqual([100, 100, 100, 100, 100])
    // Backpressure actually engaged: draining the queue under the
    // 250-span mark requires at least two 20 ms store cycles.
    expect(enqueued - began).toBeGreaterThanOrEqual(35)
  })

  it('enqueue waits for a flush while the queue is over the high-water mark', async () => {
    const sink = new GatedBatchSink()
    const writer = new IngestWriter(sink, { batchSize: 4, highWaterMark: 4 })

    const enqueued = writer.enqueue(makeSpans(12))
    // The first batch of 4 is in the sink's hands; 8 remain queued, over
    // the mark of 4, so the enqueue cannot resolve yet.
    expect(sink.batches.length).toBe(1)
    expect(writer.pending).toBe(8)
    let resolved = false
    void enqueued.then(() => {
      resolved = true
    })
    await sleep(5)
    expect(resolved).toBe(false)

    sink.release() // batch 1 lands; batch 2 dispatches and blocks
    await vi.waitFor(() => expect(sink.batches.length).toBe(2))
    expect(resolved).toBe(false) // the queue is still over the mark

    sink.release() // batch 2 lands; batch 3 dispatches
    await vi.waitFor(() => expect(sink.batches.length).toBe(3))
    sink.release() // batch 3 lands; the drain completes
    await enqueued

    expect(writer.pending).toBe(0)
    expect(sink.batches.flat().map((span) => span.spanId)).toEqual(
      makeSpans(12).map((span) => span.spanId),
    )
  })

  it('write() — the receiver store port — queues without blocking and never drops', async () => {
    const sink = new SlowBatchSink(0)
    const writer = new IngestWriter(sink, { batchSize: 8 })

    writer.write(makeSpans(20))
    expect(writer.pending).toBeGreaterThan(0) // queued synchronously
    expect(writer.pending).toBeLessThanOrEqual(20) // a flush may already have claimed a batch

    await writer.stop()
    expect(sink.spans).toHaveLength(20)
    expect(writer.pending).toBe(0)
  })

  it('wires straight into the receiver as its store: posted spans all persist', async () => {
    const sink = new SlowBatchSink(0)
    const writer = new IngestWriter(sink, { batchSize: 64 })
    const receiver = new OtlpReceiver({ port: 0, store: writer })
    await receiver.start()
    started.push(receiver)
    try {
      for (let batch = 0; batch < 3; batch++) {
        const response = await fetch(`http://127.0.0.1:${receiver.port}${OTLP_TRACES_PATH}`, {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: otlpJsonSpans(5),
        })
        expect(response.status).toBe(200)
        expect(await response.json()).toEqual({ acceptedSpans: 5 })
      }
      await writer.stop()
      expect(sink.spans).toHaveLength(15)
      expect(new Set(sink.spans.map((span) => span.spanId)).size).toBe(15)
    } finally {
      await receiver.stop()
    }
  })

  it('start() flushes on an interval; stop() drains the remainder and closes the writer', async () => {
    const sink = new SlowBatchSink(0)
    const writer = new IngestWriter(sink, { batchSize: 100, flushIntervalMs: 10 })

    writer.start()
    await writer.enqueue(makeSpans(30)) // under the batch size: nothing flushes yet
    expect(sink.spans).toHaveLength(0)
    await vi.waitFor(() => expect(sink.spans).toHaveLength(30), { interval: 5 })

    await writer.enqueue(makeSpans(10))
    await writer.stop()
    expect(sink.spans).toHaveLength(40)
    expect(writer.pending).toBe(0)

    // Closed: further writes refuse loudly rather than silently dropping.
    expect(() => writer.write(makeSpans(1))).toThrow(/stopped/)
    await expect(writer.enqueue(makeSpans(1))).rejects.toThrow(/stopped/)
    // And the timer is gone: nothing more arrives.
    await sleep(40)
    expect(sink.spans).toHaveLength(40)
  })

  it('a store failure re-queues the batch — nothing dropped — and surfaces through onError', async () => {
    const sink = new SlowBatchSink(0)
    sink.failNext = true
    const failures: unknown[] = []
    const writer = new IngestWriter(sink, { batchSize: 3, onError: (err) => failures.push(err) })

    writer.write(makeSpans(6)) // two batches; the first store call fails
    await vi.waitFor(() => expect(failures).toHaveLength(1))
    expect(writer.pending).toBe(6) // the refused batch went back to the head

    await writer.flush() // retry: both batches land
    expect(sink.spans).toHaveLength(6)
    expect(writer.pending).toBe(0)
  })

  it('a 500-span burst through the real CanonStore persists 500 records, idempotently', async () => {
    const canon = new CanonStore(':memory:')
    const toCanon: SpanBatchSink = {
      upsertMany: (spans) => canon.upsertMany(spans.map(spanToRecord)),
    }
    const burst = makeSpans(500)

    const writer = new IngestWriter(toCanon, { batchSize: 128 })
    await Promise.all([writer.enqueue(burst.slice(0, 250)), writer.enqueue(burst.slice(250))])
    await writer.stop()
    expect(canon.count()).toBe(500)

    // Re-ingesting the same corpus changes nothing: span_id is the key (R2.5).
    const reingest = new IngestWriter(toCanon, { batchSize: 128 })
    await reingest.enqueue(burst)
    await reingest.stop()
    expect(canon.count()).toBe(500)
    canon.close()
  })
})

let postedSpanSeq = 0

/** A minimal hand-rolled OTLP JSON body with `count` unique spans. */
function otlpJsonSpans(count: number): string {
  const first = postedSpanSeq
  postedSpanSeq += count
  return JSON.stringify({
    resourceSpans: [
      {
        scopeSpans: [
          {
            spans: Array.from({ length: count }, (_, index) => ({
              traceId: '0af7651916cd43dd8448eb211c80319c',
              spanId: (first + index).toString(16).padStart(16, '0'),
              name: `posted.${first + index}`,
              startTimeUnixNano: '1756478400000000000',
              endTimeUnixNano: '1756478400100000000',
            })),
          },
        ],
      },
    ],
  })
}

// ---------------------------------------------------------------------------
// Port-conflict diagnostics (R2.4)
// ---------------------------------------------------------------------------

describe('port-occupant parsers', () => {
  it('parses lsof output: the holding process is named by PID', () => {
    const lsof = [
      'COMMAND   PID  USER   FD   TYPE   DEVICE SIZE/OFF NODE NAME',
      'node    5712 dave   24u  IPv4  0x8f1a2b3c      0t0  TCP 127.0.0.1:4318 (LISTEN)',
    ].join('\n')
    expect(parseLsofOutput(lsof)).toEqual([
      { pid: '5712', name: 'node', detail: expect.stringContaining('4318') },
    ])
    expect(parseLsofOutput('')).toEqual([])
    // Header only, or garbage: no occupants claimed.
    expect(parseLsofOutput('COMMAND PID USER FD TYPE DEVICE SIZE/OFF NODE NAME')).toEqual([])
    expect(parseLsofOutput('not lsof output at all')).toEqual([])
  })

  it('parses ss output: the users:(("name",pid=N)) column names the process', () => {
    const ss = [
      'State  Recv-Q Send-Q Local Address:Port  Peer Address:Port Process',
      'LISTEN 0      128    127.0.0.1:4318       0.0.0.0:*            users:(("otelcol",pid=999,fd=7))',
    ].join('\n')
    expect(parseSsOutput(ss)).toEqual([
      { pid: '999', name: 'otelcol', detail: expect.stringContaining('pid=999') },
    ])
    // A listening row with no process info (no -p) claims nothing.
    expect(parseSsOutput('LISTEN 0 128 127.0.0.1:4318 0.0.0.0:*')).toEqual([])
    expect(parseSsOutput('')).toEqual([])
  })

  it('parses netstat output: the trailing PID column on the port listener', () => {
    const netstat = [
      'Active Connections',
      '',
      '  Proto  Local Address      Foreign Address   State      PID',
      '  TCP    127.0.0.1:4318     0.0.0.0:0         LISTENING  3132',
      '  TCP    127.0.0.1:9999     0.0.0.0:0         LISTENING  42',
    ].join('\r\n')
    expect(parseNetstatOutput(netstat, 4318)).toEqual([
      { pid: '3132', name: undefined, detail: expect.stringContaining('4318') },
    ])
    expect(parseNetstatOutput(netstat, 9999)).toEqual([
      { pid: '42', name: undefined, detail: expect.any(String) },
    ])
    expect(parseNetstatOutput(netstat, 12345)).toEqual([])
  })
})

describe('port-conflict diagnostic (R2.4)', () => {
  it('a second receiver on a bound port fails with PORT_CONFLICT naming the port and the occupant', async () => {
    const holder = new OtlpReceiver({ port: 0 })
    await holder.start()
    started.push(holder)
    const boundPort = holder.port

    const second = new OtlpReceiver({ port: boundPort })
    const failure = await captureRejection(second.start())

    if (!(failure instanceof PortConflictError)) {
      throw new Error(`expected PortConflictError, got: ${String(failure)}`)
    }
    expect(failure.code).toBe('PORT_CONFLICT')
    expect(failure.port).toBe(boundPort)
    expect(failure.host).toBe('127.0.0.1')
    expect((failure.cause as { code?: string } | undefined)?.code).toBe('EADDRINUSE')

    // The report names the port and says it did not rebind.
    expect(failure.message).toContain(`127.0.0.1:${boundPort}`)
    expect(failure.message).toContain('already bound')
    expect(failure.message).toMatch(/did not rebind/)

    // Where the occupying process is discoverable it is named; where not,
    // the report says so instead of guessing. (The holder is this test
    // process, so a competent tool finds it — but the test must not depend
    // on which tools the host ships.)
    if (failure.occupants.length > 0) {
      for (const occupant of failure.occupants) {
        expect(occupant.pid).toMatch(/^\d+$/)
        expect(failure.message).toContain(occupant.pid)
      }
    } else {
      expect(failure.occupants).toEqual([])
      expect(failure.message).toContain('could not be identified')
    }

    // The failed receiver did not quietly end up bound somewhere else…
    expect(second.listening).toBe(false)
    expect(second.port).toBe(boundPort) // it still reports the configured port
    // …and the first receiver still owns it.
    const probe = await fetch(`http://127.0.0.1:${boundPort}/`)
    expect(probe.status).toBe(404)
  })

  it('reports an injected occupant: the diagnostic names what discovery found', async () => {
    const holder = new OtlpReceiver({ port: 0 })
    await holder.start()
    started.push(holder)

    const second = new OtlpReceiver({
      port: holder.port,
      discoverOccupants: async () => [
        { pid: '4242', name: 'opentelemetry-collector', detail: 'injected fixture' },
      ],
    })
    const failure = await captureRejection(second.start())

    if (!(failure instanceof PortConflictError)) {
      throw new Error(`expected PortConflictError, got: ${String(failure)}`)
    }
    expect(failure.occupants).toEqual([
      { pid: '4242', name: 'opentelemetry-collector', detail: 'injected fixture' },
    ])
    expect(failure.message).toContain('4242')
    expect(failure.message).toContain('opentelemetry-collector')
    expect(failure.message).toContain(`127.0.0.1:${holder.port}`)
    expect(second.listening).toBe(false)
  })

  it('with nothing discoverable, the report says so and names the tools it tried', () => {
    const error = new PortConflictError(4318, '127.0.0.1', [])
    expect(error.code).toBe('PORT_CONFLICT')
    expect(error.port).toBe(4318)
    expect(error.message).toContain('4318')
    expect(error.message).toContain('could not be identified')
    expect(failureNamesTools(error.message)).toBe(true)
    expect(error.message).toMatch(/did not rebind/)
  })
})

/** The "not discoverable" report must still tell the operator where to look. */
function failureNamesTools(message: string): boolean {
  return ['lsof', 'ss', 'netstat'].every((tool) => message.includes(tool))
}
