// Ingest writer for KyberDash (spec: docs/specs/kyberdash, design.md
// "Ingest layer", task 6.2). `IngestWriter` sits between the receiver and
// the store and owns the two things the receiver deliberately does not:
//
//   * batching (R2.5) — decoded spans are queued and persisted in
//     batch-sized units through the sink's `upsertMany`, one transaction
//     per unit, so a burst that outruns persistence is committed as units
//     rather than record-at-a-time;
//   * backpressure (R2.5) — `enqueue` does not resolve while the queue is
//     over the high-water mark: a producer outrunning the store is slowed
//     to the store's pace instead of being allowed to pile spans up or
//     drop them.
//
// No code path drops a span. A store that rejects a batch gets the batch
// back at the head of the queue, in order, and the failure surfaces — to
// the awaiting caller, or to `onError` when the writer itself triggered
// the flush — never silently.
//
// The writer implements the receiver's `OtlpSpanStore` port (`write`), so
// `new OtlpReceiver({ store: writer })` is the entire wiring. The sink
// contract is span-shaped — `upsertMany(spans)` — because mapping a
// decoded span onto a canonical record is the normalization layer's job
// (task 5), not the ingest layer's: nothing here fabricates harness
// attribution, token decomposition or cost. `CanonStore` is reached by
// wrapping it with that mapping where it is built; writer.test.ts shows
// the seam against the real transactional store.

import type { OtlpSpan, OtlpSpanStore } from './receiver.js'

/**
 * What `IngestWriter` persists into: a batch sink that commits a batch as
 * a unit — the `CanonStore` shape, applied to spans — or the receiver's
 * simple `OtlpSpanStore` port. An `upsertMany` return value may be a
 * promise; a slow store is awaited, which is how write throughput paces
 * the queue.
 */
export type SpanBatchSink = {
  upsertMany(spans: readonly OtlpSpan[]): unknown
}

/** Either sink shape the writer accepts. */
export type IngestSink = SpanBatchSink | OtlpSpanStore

export type IngestWriterOptions = {
  /** Queue depth at which a size-triggered flush fires. */
  batchSize?: number
  /** Periodic flush cadence once `start()` has been called. */
  flushIntervalMs?: number
  /**
   * Backpressure threshold (R2.5): `enqueue` awaits a flush while the
   * queue holds more spans than this. Defaults to eight batches.
   */
  highWaterMark?: number
  /**
   * Where flush failures land when the writer itself triggered the flush
   * (the periodic timer, or the receiver's fire-and-forget `write` port).
   * Without one the failure is rethrown on the next event-loop turn — a
   * persistence failure is never swallowed.
   */
  onError?: (error: unknown) => void
}

export const DEFAULT_BATCH_SIZE = 512
export const DEFAULT_FLUSH_INTERVAL_MS = 1_000
export const DEFAULT_HIGH_WATER_MARK = DEFAULT_BATCH_SIZE * 8

function isThenable(value: unknown): value is PromiseLike<unknown> {
  return (
    typeof value === 'object' &&
    value !== null &&
    typeof (value as { then?: unknown }).then === 'function'
  )
}

function isBatchSink(sink: IngestSink): sink is SpanBatchSink {
  return typeof (sink as Partial<SpanBatchSink>).upsertMany === 'function'
}

export class IngestWriter implements OtlpSpanStore {
  private readonly batchSink: SpanBatchSink | null
  private readonly spanStore: OtlpSpanStore | null
  private readonly batchSize: number
  private readonly flushIntervalMs: number
  private readonly highWaterMark: number
  private readonly report: (error: unknown) => void

  private queue: OtlpSpan[] = []
  private flushInFlight: Promise<void> | null = null
  private timer: ReturnType<typeof setInterval> | null = null
  private stopped = false

  constructor(sink: IngestSink, opts: IngestWriterOptions = {}) {
    this.batchSink = isBatchSink(sink) ? sink : null
    this.spanStore = isBatchSink(sink) ? null : sink
    this.batchSize = Math.max(1, Math.floor(opts.batchSize ?? DEFAULT_BATCH_SIZE))
    this.flushIntervalMs = Math.max(
      1,
      Math.floor(opts.flushIntervalMs ?? DEFAULT_FLUSH_INTERVAL_MS),
    )
    this.highWaterMark = Math.max(
      this.batchSize,
      Math.floor(opts.highWaterMark ?? DEFAULT_HIGH_WATER_MARK),
    )
    const onError = opts.onError
    this.report = (error: unknown) => {
      if (onError !== undefined) {
        onError(error)
        return
      }
      // No handler configured: fail loudly on the next turn rather than
      // swallowing a persistence failure.
      setImmediate(() => {
        throw error
      })
    }
  }

  /** Spans queued but not yet handed to the sink — the backpressure gauge. */
  get pending(): number {
    return this.queue.length
  }

  /**
   * The receiver's store port (task 6.1): queue decoded spans without
   * blocking the decode path, triggering a flush whenever a batch is full.
   * Spans are never dropped; a flush failure surfaces through `onError`.
   */
  write(spans: readonly OtlpSpan[]): void {
    if (spans.length === 0) return
    if (this.stopped) {
      throw new Error('IngestWriter is stopped; enqueueing more spans would silently drop them')
    }
    for (const span of spans) this.queue.push(span)
    if (this.queue.length >= this.batchSize) this.autoFlush()
  }

  /**
   * Queue spans with backpressure (R2.5): the returned promise resolves
   * only once the spans are queued *and* the queue has been flushed back
   * under the high-water mark, so a producer outrunning persistence is
   * slowed to the store's pace. A store failure rejects here — with the
   * batch re-queued at the head of the queue, not dropped.
   */
  async enqueue(spans: readonly OtlpSpan[]): Promise<void> {
    if (this.stopped) {
      throw new Error('IngestWriter is stopped; enqueueing more spans would silently drop them')
    }
    if (spans.length > 0) {
      for (const span of spans) this.queue.push(span)
      if (this.queue.length >= this.batchSize) this.autoFlush()
    }
    while (this.queue.length > this.highWaterMark) {
      // Either rejects with the store's failure (batch re-queued, caller
      // decides what to do) or resolves with the queue drained.
      await this.pokeFlush()
    }
  }

  /** Persist everything queued now; resolves once the queue is empty. */
  async flush(): Promise<void> {
    await this.pokeFlush()
  }

  /** Begin flushing every `flushIntervalMs` until `stop()`. */
  start(): void {
    if (this.stopped) throw new Error('IngestWriter is stopped and cannot be restarted')
    if (this.timer !== null) return
    this.timer = setInterval(() => this.autoFlush(), this.flushIntervalMs)
    // The flush timer must never be the reason a CLI process stays open.
    this.timer.unref()
  }

  /**
   * Stop the periodic timer, flush everything still queued, and close the
   * writer to further writes. Resolves only once the queue is empty, so
   * nothing queued before the call can be dropped (R2.5).
   */
  async stop(): Promise<void> {
    if (this.timer !== null) {
      clearInterval(this.timer)
      this.timer = null
    }
    this.stopped = true
    await this.pokeFlush()
  }

  private autoFlush(): void {
    this.pokeFlush().catch((err: unknown) => this.report(err))
  }

  /**
   * Start (or join) the single-flight flush. Concurrent callers — timer,
   * `write`, `enqueue`, `stop` — share one drain; the returned promise
   * rejects if the store does.
   */
  private pokeFlush(): Promise<void> {
    if (this.flushInFlight !== null) return this.flushInFlight
    if (this.queue.length === 0) return Promise.resolve()
    const flush = this.runFlush().finally(() => {
      // Single-flight: nothing can replace this flush while it runs, so
      // clearing unconditionally here is safe.
      this.flushInFlight = null
    })
    this.flushInFlight = flush
    return flush
  }

  private async runFlush(): Promise<void> {
    while (this.queue.length > 0) {
      const batch = this.queue.splice(0, this.batchSize)
      try {
        let result: unknown
        if (this.batchSink !== null) {
          result = this.batchSink.upsertMany(batch)
        } else if (this.spanStore !== null) {
          this.spanStore.write(batch)
        }
        if (isThenable(result)) await result
      } catch (err) {
        // The store refused the batch: put it back at the head, in order,
        // so no span is dropped (R2.5), and let the caller see the failure.
        this.queue.unshift(...batch)
        throw err
      }
    }
  }
}
