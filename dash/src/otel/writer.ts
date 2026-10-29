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

import type { OtlpLog, OtlpSignalStore, OtlpSpan, OtlpSpanStore, OtlpWriteHint } from './receiver.js'
import { Buffer } from 'node:buffer'

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

export type SignalBatchSink = SpanBatchSink & {
  upsertLogs(logs: readonly OtlpLog[]): unknown
}

/** Either sink shape the writer accepts. */
export type IngestSink = SpanBatchSink | SignalBatchSink | OtlpSpanStore | OtlpSignalStore

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
   * Backlog ceiling (F3): `write`/`writeLogs` throw `WriterBackpressureError`
   * instead of queueing past this many spans plus logs. Defaults to four
   * times the high-water mark; always at least `batchSize`.
   */
  maxPendingSpans?: number
  /**
   * Byte ceiling for the same backlog (F3): estimated queued bytes past this
   * throw `WriterBackpressureError` the same way. Defaults to 256 MiB.
   */
  maxPendingBytes?: number
  /**
   * Where flush failures land when the writer itself triggered the flush
   * (the periodic timer, or the receiver's fire-and-forget `write` port).
   * Without one the failure is rethrown on the next event-loop turn — a
   * persistence failure is never swallowed.
   */
  onError?: (error: unknown) => void
  /**
   * Called after each batch the sink commits, with the committed sizes. The collector
   * service uses this to move the receiver's `lastAcceptedAt`: receipt (the receiver's
   * 200) and persistence (this hook) are different moments, and only the second proves
   * telemetry landed. Never called for a failed or empty flush. A throwing hook is
   * fenced — persistence already happened, so a hook failure must not fail the flush.
   */
  onPersisted?: (info: { spans: number; logs: number }) => void
}

export const DEFAULT_BATCH_SIZE = 512
export const DEFAULT_FLUSH_INTERVAL_MS = 1_000
export const DEFAULT_HIGH_WATER_MARK = DEFAULT_BATCH_SIZE * 8
/**
 * Backlog ceiling above the `enqueue` high-water mark: `write` (the receiver's
 * fire-and-forget port) never blocks, so without its own ceiling a failing store
 * with continued intake retains every payload (F3). Sized as a multiple of the
 * high-water mark so ordinary bursts that engage `enqueue` backpressure stay
 * well below it. An empty queue always admits the arriving batch, so a batch
 * larger than the ceiling can still make progress instead of wedging its
 * exporter in a retry loop; the ceiling bounds the *backlog*, not one batch.
 */
export const MAX_PENDING_SPANS_FACTOR = 4
/**
 * Byte ceiling for the same backlog: spans carry prompt-shaped payloads, so a
 * count alone cannot bound retained bytes. Sized to hold a few per-request
 * bodies (`DEFAULT_MAX_BODY_BYTES` in the receiver) behind a failing store.
 */
export const DEFAULT_MAX_PENDING_BYTES = 256 * 1024 * 1024
/** Retry-After seconds the receiver advertises when it sheds an over-cap batch. */
export const DEFAULT_SHED_RETRY_AFTER_MS = 1_000

/**
 * The writer refused a batch before acceptance because the backlog is full
 * (F3). Retryable by contract: the batch was never queued, so the exporter
 * must retry it — unlike an accepted batch, which the writer retains and the
 * exporter must not resend.
 */
export class WriterBackpressureError extends Error {
  readonly retryable = true
  readonly retryAfterMs: number
  constructor(message: string, retryAfterMs: number = DEFAULT_SHED_RETRY_AFTER_MS) {
    super(message)
    this.name = 'WriterBackpressureError'
    this.retryAfterMs = retryAfterMs
  }
}

/** Wire-size estimate for a batch the caller could not measure (F3). */
function estimateBatchBytes(items: readonly unknown[]): number {
  try {
    return Buffer.byteLength(JSON.stringify(items), 'utf8')
  } catch {
    // Unserializable input is the sink's problem, not the ledger's: count
    // the batch as weightless rather than refuse to queue it.
    return 0
  }
}

/**
 * Push `items` with their share of `totalBytes` (integer-distributed, so the
 * ledger sums back to exactly `totalBytes`). The running total itself is
 * adjusted by the caller (`admit`), so this only maintains the per-item
 * ledger `runFlush` splices back off.
 */
function pushWithBytes<T>(queue: T[], ledger: number[], items: readonly T[], totalBytes: number): void {
  const width = Math.max(0, Math.floor(totalBytes))
  const share = items.length > 0 ? Math.floor(width / items.length) : 0
  for (let index = 0; index < items.length; index++) {
    queue.push(items[index] as T)
    // The last item takes the remainder, so the ledger holds exactly `width`.
    ledger.push(index === items.length - 1 ? width - share * (items.length - 1) : share)
  }
}

function sumBytes(ledger: readonly number[]): number {
  let total = 0
  for (const bytes of ledger) total += bytes
  return total
}

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

function isSignalSink(sink: IngestSink): sink is SignalBatchSink | OtlpSignalStore {
  return typeof (sink as Partial<SignalBatchSink>).upsertLogs === 'function' ||
    typeof (sink as Partial<OtlpSignalStore>).writeLogs === 'function'
}

export class IngestWriter implements OtlpSpanStore {
  private readonly batchSink: SpanBatchSink | null
  private readonly spanStore: OtlpSpanStore | null
  private readonly signalSink: SignalBatchSink | OtlpSignalStore | null
  private readonly batchSize: number
  private readonly flushIntervalMs: number
  private readonly highWaterMark: number
  private readonly report: (error: unknown) => void

  private queue: OtlpSpan[] = []
  private logQueue: OtlpLog[] = []
  /** Estimated bytes per queued span/log, same order as the queues above. */
  private queueBytes: number[] = []
  private logQueueBytes: number[] = []
  /** Sum of the byte ledgers above: the backlog's estimated retained bytes. */
  private queuedBytes = 0
  private readonly maxPendingSpans: number
  private readonly maxPendingBytes: number
  private flushInFlight: Promise<void> | null = null
  private timer: ReturnType<typeof setInterval> | null = null
  private stopped = false
  private onPersisted: ((info: { spans: number; logs: number }) => void) | undefined

  constructor(sink: IngestSink, opts: IngestWriterOptions = {}) {
    this.batchSink = isBatchSink(sink) ? sink : null
    this.spanStore = isBatchSink(sink) ? null : sink
    this.signalSink = isSignalSink(sink) ? sink : null
    this.batchSize = Math.max(1, Math.floor(opts.batchSize ?? DEFAULT_BATCH_SIZE))
    this.flushIntervalMs = Math.max(
      1,
      Math.floor(opts.flushIntervalMs ?? DEFAULT_FLUSH_INTERVAL_MS),
    )
    this.highWaterMark = Math.max(
      this.batchSize,
      Math.floor(opts.highWaterMark ?? DEFAULT_HIGH_WATER_MARK),
    )
    this.maxPendingSpans = Math.max(
      this.batchSize,
      Math.floor(opts.maxPendingSpans ?? this.highWaterMark * MAX_PENDING_SPANS_FACTOR),
    )
    this.maxPendingBytes = Math.max(
      4096,
      Math.floor(opts.maxPendingBytes ?? DEFAULT_MAX_PENDING_BYTES),
    )
    this.onPersisted = opts.onPersisted
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

  /** Logs queued but not yet handed to the sink. */
  get pendingLogs(): number {
    return this.logQueue.length
  }

  /** Estimated bytes held across both queues — the byte-admission gauge. */
  get pendingBytes(): number {
    return this.queuedBytes
  }

  /**
   * Whether `count` records carrying `bytes` would be admitted right now
   * (F3): the same gate `write`/`writeLogs` enforce, exposed so the receiver
   * can shed load with a retryable status BEFORE acceptance instead of
   * queueing and then failing.
   */
  canAccept(count: number, bytes: number): boolean {
    if (this.stopped) return false
    if (this.queue.length + this.logQueue.length === 0) return true
    return (
      this.queue.length + this.logQueue.length + Math.max(0, count) <= this.maxPendingSpans &&
      this.queuedBytes + Math.max(0, bytes) <= this.maxPendingBytes
    )
  }

  /**
   * Replace the persisted hook after construction. The collector service sets this once
   * the receiver exists (the receiver needs the writer as its store, so the writer is
   * always built first) — one hook at a time, latest wins.
   */
  setPersistedHook(hook: ((info: { spans: number; logs: number }) => void) | undefined): void {
    this.onPersisted = hook
  }

  /**
   * The receiver's store port (task 6.1): queue decoded spans without
   * blocking the decode path, triggering a flush whenever a batch is full.
   * Spans are never dropped; a flush failure surfaces through `onError`.
   *
   * Past the backlog ceiling the batch is refused with
   * `WriterBackpressureError` instead of queued (F3): the caller must retry
   * it, because nothing accepted is ever dropped and nothing refused was
   * ever accepted. `hint.bytes` should carry the request's wire bytes (the
   * receiver knows them without re-serializing); without a hint the batch
   * is JSON-measured, which costs a serialization on that path.
   */
  write(spans: readonly OtlpSpan[], hint?: OtlpWriteHint): void {
    if (spans.length === 0) return
    if (this.stopped) {
      throw new Error('IngestWriter is stopped; enqueueing more spans would silently drop them')
    }
    const bytes = hint?.bytes ?? estimateBatchBytes(spans)
    this.admit(spans.length, bytes)
    pushWithBytes(this.queue, this.queueBytes, spans, bytes)
    if (this.queue.length >= this.batchSize) this.autoFlush()
  }

  writeLogs(logs: readonly OtlpLog[], hint?: OtlpWriteHint): void {
    if (logs.length === 0) return
    if (this.stopped) throw new Error('IngestWriter is stopped; enqueueing more logs would silently drop them')
    const bytes = hint?.bytes ?? estimateBatchBytes(logs)
    this.admit(logs.length, bytes)
    pushWithBytes(this.logQueue, this.logQueueBytes, logs, bytes)
    if (this.logQueue.length >= this.batchSize) this.autoFlush()
  }

  /**
   * Queue spans with backpressure (R2.5): the returned promise resolves
   * only once the spans are queued *and* the queue has been flushed back
   * under the high-water mark, so a producer outrunning persistence is
   * slowed to the store's pace. A store failure rejects here — with the
   * batch re-queued at the head of the queue, not dropped.
   */
  async enqueue(spans: readonly OtlpSpan[], hint?: OtlpWriteHint): Promise<void> {
    if (this.stopped) {
      throw new Error('IngestWriter is stopped; enqueueing more spans would silently drop them')
    }
    if (spans.length > 0) {
      const bytes = hint?.bytes ?? estimateBatchBytes(spans)
      this.admit(spans.length, bytes)
      pushWithBytes(this.queue, this.queueBytes, spans, bytes)
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
    if (this.queue.length === 0 && this.logQueue.length === 0) return Promise.resolve()
    const flush = this.runFlush().finally(() => {
      // Single-flight: nothing can replace this flush while it runs, so
      // clearing unconditionally here is safe.
      this.flushInFlight = null
    })
    this.flushInFlight = flush
    return flush
  }

  private async runFlush(): Promise<void> {
    while (this.queue.length > 0 || this.logQueue.length > 0) {
      const batch = this.queue.splice(0, this.batchSize)
      const batchBytes = this.queueBytes.splice(0, this.batchSize)
      const logs = this.logQueue.splice(0, this.batchSize)
      const logBytes = this.logQueueBytes.splice(0, this.batchSize)
      this.queuedBytes -= sumBytes(batchBytes) + sumBytes(logBytes)
      try {
        let result: unknown
        if (this.batchSink !== null) {
          result = this.batchSink.upsertMany(batch)
        } else if (this.spanStore !== null) {
          this.spanStore.write(batch)
        }
        if (logs.length > 0 && this.signalSink !== null) {
          if (typeof (this.signalSink as Partial<SignalBatchSink>).upsertLogs === 'function') {
            result = (this.signalSink as SignalBatchSink).upsertLogs(logs)
          } else {
            result = (this.signalSink as OtlpSignalStore).writeLogs(logs)
          }
        }
        if (isThenable(result)) await result
        // The sink committed: receipt and persistence are different moments, and this is
        // the second one. A batch the sink refused never reaches here (it re-queues below).
        if (batch.length > 0 || logs.length > 0) this.notifyPersisted(batch.length, logs.length)
      } catch (err) {
        // The store refused the batch: put it back at the head, in order,
        // so no span is dropped (R2.5), and let the caller see the failure.
        // The byte ledger goes back with it, so the refusal does not look
        // like drained backlog to the admission gate.
        this.queue.unshift(...batch)
        this.queueBytes.unshift(...batchBytes)
        this.logQueue.unshift(...logs)
        this.logQueueBytes.unshift(...logBytes)
        this.queuedBytes += sumBytes(batchBytes) + sumBytes(logBytes)
        throw err
      }
    }
    // Both queues drained: an empty backlog holds zero bytes, exactly.
    this.queuedBytes = 0
  }

  /**
   * The F3 backlog gate: refuse `count` records carrying `bytes` with a
   * retryable error when the backlog is already full, instead of queueing
   * without bound behind a failing store. An empty queue always admits the
   * arriving batch (liveness over strictness for one batch); the ceiling
   * bounds what can accumulate, not what one request may carry.
   */
  private admit(count: number, bytes: number): void {
    if (this.queue.length + this.logQueue.length === 0) {
      this.queuedBytes += Math.max(0, bytes)
      return
    }
    const size = Math.max(0, count)
    const width = Math.max(0, bytes)
    if (this.queue.length + this.logQueue.length + size > this.maxPendingSpans) {
      throw new WriterBackpressureError(
        `ingest backlog is full (${this.queue.length + this.logQueue.length} records queued); retry the batch`,
      )
    }
    if (this.queuedBytes + width > this.maxPendingBytes) {
      throw new WriterBackpressureError(
        `ingest backlog is full (${this.queuedBytes} bytes queued); retry the batch`,
      )
    }
    this.queuedBytes += width
  }

  private notifyPersisted(spans: number, logs: number): void {
    const hook = this.onPersisted
    if (hook === undefined) return
    try {
      hook({ spans, logs })
    } catch (err) {
      // The batch is committed; a hook failure must not fail the flush or re-queue it,
      // but it is logged so a broken hook is not invisible.
      console.warn('[ingest] onPersisted hook failed (batch already committed):', err)
    }
  }
}
