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
   * instead of queueing past this many spans plus logs — and
   * `BatchTooLargeError` for a batch that alone exceeds it, however empty
   * the backlog is. Defaults to four times the high-water mark. Strict:
   * no empty-queue exception admits an oversize batch.
   */
  maxPendingSpans?: number
  /**
   * Byte ceiling for the same backlog (F3): accounted backlog bytes past
   * this throw the same way, and a batch that alone exceeds it is
   * permanently oversize. Defaults to 256 MiB.
   */
  maxPendingBytes?: number
  /**
   * Byte measure for the backlog ledger (F3): maps an admitted batch to its
   * accounted bytes. Defaults to the conservative decoded-JSON measure. A
   * measure that throws refuses the batch with a boundary error rather than
   * ledgering it weightless — unmeasurable input must not bypass admission.
   */
  measureBytes?: (items: readonly unknown[]) => number
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
 * well below it. Strict: a batch that alone exceeds either ceiling is
 * permanently oversize and refused even into an empty backlog.
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

/**
 * The writer refused a batch before acceptance because it can never fit the
 * backlog ceilings, however empty the backlog is (F3). Permanent by
 * contract: retrying the identical batch refuses again, so the caller must
 * shrink or split the batch instead of retrying it. The receiver answers
 * 413 for these; `WriterBackpressureError` (above) is the transient sibling
 * answered with 503 + Retry-After.
 */
export class BatchTooLargeError extends Error {
  readonly retryable = false
  constructor(message: string) {
    super(message)
    this.name = 'BatchTooLargeError'
  }
}

/** Where a `checkAdmission` verdict lands: admitted, transiently full, or permanently oversize. */
export type Admission = 'ok' | 'saturated' | 'oversize'

/**
 * Conservative per-batch byte measure for the backlog ledger (F3). The
 * default JSON-measures the DECODED batch — wire bytes do not measure the
 * retained decoded objects — times a constant overhead factor for the object
 * graph JSON text does not show. Conservative on purpose: the ledger must
 * over-estimate retained bytes, never under-estimate them. Injectable so
 * tests can pin amplification without shipping megabyte fixtures.
 */
export const DECODED_BYTE_OVERHEAD_FACTOR = 2

/** Default ledger measure: decoded JSON bytes with the overhead factor above. */
function defaultMeasureBatchBytes(items: readonly unknown[]): number {
  // No fail-open fallback: a batch whose bytes cannot be measured is refused
  // by the caller (`accountedBytes` below) rather than ledgered weightless.
  return Buffer.byteLength(JSON.stringify(items), 'utf8') * DECODED_BYTE_OVERHEAD_FACTOR
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
  /**
   * Batches the queues handed to the sink but the sink has not settled (F3):
   * spliced out of the queues above, still retained, still counted. A held
   * async sink with continued intake must see its in-flight batches in the
   * admission totals, or the ledger stops bounding the moment a flush starts.
   */
  private inflightSpanCount = 0
  private inflightLogCount = 0
  private inflightByteCount = 0
  private readonly measure: (items: readonly unknown[]) => number
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
      1,
      Math.floor(opts.maxPendingSpans ?? this.highWaterMark * MAX_PENDING_SPANS_FACTOR),
    )
    this.maxPendingBytes = Math.max(
      1,
      Math.floor(opts.maxPendingBytes ?? DEFAULT_MAX_PENDING_BYTES),
    )
    this.measure = opts.measureBytes ?? defaultMeasureBatchBytes
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

  /** Records handed to the sink but not yet settled — still retained, still counted. */
  get inflightRecords(): number {
    return this.inflightSpanCount + this.inflightLogCount
  }

  /** Estimated bytes of the unsettled in-flight batches. */
  get inflightBytes(): number {
    return this.inflightByteCount
  }

  /** Queued plus in-flight records: everything retained but uncommitted. */
  private backlogCount(): number {
    return this.queue.length + this.logQueue.length + this.inflightSpanCount + this.inflightLogCount
  }

  /** Queued plus in-flight bytes: the ledger admission is computed against. */
  private backlogBytes(): number {
    return this.queuedBytes + this.inflightByteCount
  }

  /**
   * The F3 admission verdict for a batch of decoded `items` carrying `hint`
   * wire bytes: the same gate `write`/`writeLogs` enforce, exposed so the
   * receiver can shed load with a precise status BEFORE acceptance instead of
   * queueing and then failing. `saturated` is transient (retry the batch
   * later); `oversize` is permanent (the batch alone exceeds a ceiling and
   * never fits).
   */
  checkAdmission(items: readonly unknown[], hint?: OtlpWriteHint): Admission {
    if (this.stopped) return 'saturated'
    return this.verdict(items.length, this.accountedBytes(items, hint))
  }

  /**
   * Whether a batch of decoded `items` carrying `hint` bytes would be
   * admitted right now.
   */
  canAccept(items: readonly unknown[], hint?: OtlpWriteHint): boolean {
    return this.checkAdmission(items, hint) === 'ok'
  }

  /**
   * The ledgered bytes for one batch (F3): the conservative decoded measure
   * and the caller's wire hint, whichever is larger. The hint can only raise
   * the accounting, never lower it — an arbitrary `bytes: 0`, negative, or
   * NaN hint falls back to the decoded measure rather than bypassing the
   * byte ceiling. A batch the measure cannot read is refused outright:
   * unmeasurable input must not be ledgered weightless.
   */
  private accountedBytes(items: readonly unknown[], hint?: OtlpWriteHint): number {
    let decoded: number
    try {
      decoded = Math.max(0, Math.floor(this.measure(items)))
    } catch (err) {
      throw new Error(
        `cannot admit batch: byte measurement failed (${err instanceof Error ? err.message : String(err)}); refusing unmeasurable input`,
      )
    }
    const hinted = hint?.bytes
    const wire =
      typeof hinted === 'number' && Number.isFinite(hinted) && hinted > 0 ? Math.floor(hinted) : 0
    return Math.max(decoded, wire)
  }

  /** Strict verdict: oversize first (permanent), then the backlog totals. */
  private verdict(count: number, width: number): Admission {
    if (count > this.maxPendingSpans || width > this.maxPendingBytes) return 'oversize'
    if (this.backlogCount() + count > this.maxPendingSpans) return 'saturated'
    if (this.backlogBytes() + width > this.maxPendingBytes) return 'saturated'
    return 'ok'
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
   * Admission is strict (F3): a batch that alone exceeds either ceiling is
   * refused with `BatchTooLargeError` (permanent — do not retry it as-is),
   * and a batch that would overflow a full backlog with
   * `WriterBackpressureError` (transient — retry it later). Nothing refused
   * was ever accepted. `hint.bytes` may carry the request's wire bytes; the
   * ledger takes the larger of the hint and the conservative decoded measure,
   * so the hint can never shrink the accounting.
   */
  write(spans: readonly OtlpSpan[], hint?: OtlpWriteHint): void {
    if (spans.length === 0) return
    if (this.stopped) {
      throw new Error('IngestWriter is stopped; enqueueing more spans would silently drop them')
    }
    const bytes = this.accountedBytes(spans, hint)
    this.admit(spans.length, bytes)
    pushWithBytes(this.queue, this.queueBytes, spans, bytes)
    if (this.queue.length >= this.batchSize) this.autoFlush()
  }

  writeLogs(logs: readonly OtlpLog[], hint?: OtlpWriteHint): void {
    if (logs.length === 0) return
    if (this.stopped) throw new Error('IngestWriter is stopped; enqueueing more logs would silently drop them')
    const bytes = this.accountedBytes(logs, hint)
    this.admit(logs.length, bytes)
    pushWithBytes(this.logQueue, this.logQueueBytes, logs, bytes)
    if (this.logQueue.length >= this.batchSize) this.autoFlush()
  }

  /**
   * Queue spans with backpressure (R2.5): the returned promise resolves
   * only once the spans are queued *and* the backlog has been flushed back
   * under the high-water mark, so a producer outrunning persistence is
   * slowed to the store's pace. A store failure rejects here — with the
   * batch re-queued at the head of the queue, not dropped. A batch that
   * alone exceeds either ceiling is permanently oversize
   * (`BatchTooLargeError`): blocking cannot drain what never fits.
   */
  async enqueue(spans: readonly OtlpSpan[], hint?: OtlpWriteHint): Promise<void> {
    if (this.stopped) {
      throw new Error('IngestWriter is stopped; enqueueing more spans would silently drop them')
    }
    if (spans.length > 0) {
      const bytes = this.accountedBytes(spans, hint)
      if (spans.length > this.maxPendingSpans || bytes > this.maxPendingBytes) {
        throw new BatchTooLargeError(
          `batch of ${spans.length} spans (${bytes} bytes) exceeds the backlog ceiling; shrink or split the batch`,
        )
      }
      this.queuedBytes += bytes
      pushWithBytes(this.queue, this.queueBytes, spans, bytes)
      if (this.queue.length >= this.batchSize) this.autoFlush()
    }
    while (this.queue.length > this.highWaterMark || this.backlogBytes() > this.maxPendingBytes) {
      // Either rejects with the store's failure (batch re-queued, caller
      // decides what to do) or resolves with the backlog drained. The byte
      // arm bounds huge-payload trickles a span count alone would not see.
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
      // Handed to the sink but not settled: still retained, still counted.
      // Splicing out of the queues must not look like drained backlog to
      // the admission gate while the sink holds the batch.
      const sentBytes = sumBytes(batchBytes) + sumBytes(logBytes)
      this.queuedBytes -= sentBytes
      this.inflightSpanCount += batch.length
      this.inflightLogCount += logs.length
      this.inflightByteCount += sentBytes
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
        // Settled in-flight batches leave the ledger: committed bytes are the
        // sink's now, not the backlog's.
        this.inflightSpanCount -= batch.length
        this.inflightLogCount -= logs.length
        this.inflightByteCount -= sentBytes
        if (batch.length > 0 || logs.length > 0) this.notifyPersisted(batch.length, logs.length)
      } catch (err) {
        // The store refused the batch: put it back at the head, in order,
        // so no span is dropped (R2.5), and let the caller see the failure.
        // The ledgers go back with it, exactly as admitted — a refusal must
        // restore the totals, neither leaking the in-flight reservation nor
        // double-counting it.
        this.inflightSpanCount -= batch.length
        this.inflightLogCount -= logs.length
        this.inflightByteCount -= sentBytes
        this.queue.unshift(...batch)
        this.queueBytes.unshift(...batchBytes)
        this.logQueue.unshift(...logs)
        this.logQueueBytes.unshift(...logBytes)
        this.queuedBytes += sentBytes
        throw err
      }
    }
    // Both queues drained: an empty backlog holds zero bytes, exactly.
    this.queuedBytes = 0
  }

  /**
   * The F3 backlog gate: refuse `count` records carrying `width` ledgered
   * bytes with a permanent error when the batch alone exceeds a ceiling,
   * or a transient one when it would overflow a full backlog. Strict — no
   * empty-queue exception: an oversize batch is refused however empty the
   * backlog is, because retrying it could never succeed.
   */
  private admit(count: number, width: number): void {
    const verdict = this.verdict(Math.max(0, count), Math.max(0, width))
    if (verdict === 'oversize') {
      throw new BatchTooLargeError(
        `batch of ${count} records (${width} bytes) exceeds the backlog ceiling; shrink or split the batch`,
      )
    }
    if (verdict === 'saturated') {
      throw new WriterBackpressureError(
        `ingest backlog is full (${this.backlogCount()} records, ${this.backlogBytes()} bytes retained); retry the batch`,
      )
    }
    this.queuedBytes += Math.max(0, width)
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
