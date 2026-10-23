// T5 bounded outbox: offline spool with retry/backoff/jitter, drop-oldest
// accounting, 429 batch-halving, and a status surface.
//
// The host contract is absolute: `enqueue` (and therefore the plugin's
// `emit` port built on it) is synchronous, never throws, and never blocks.
// When the loopback receiver is down, events spool up to `capacity`; beyond
// that the oldest event is dropped and `dropped_spans` counts it. A
// background pass retries with exponential backoff and full-range jitter; a
// 429 halves the effective batch for the next attempt. `drain()` keeps
// passing until the queue is empty, so a receiver that comes back up sees
// everything spooled during the outage.
//
// Memory is bounded by construction: the queue never holds more than
// `capacity` events, and a failed batch is re-queued at the head without
// duplicating it.

import type { OpencodeHookEvent, OpencodePluginEmitter } from '../plugin.js'
import { DEFAULT_OTLP_ENDPOINT, OtlpDeliveryError, buildOtlpTracesBody } from './otlp-sender.js'

/** What the outbox carries: plugin hook events, span-anchored downstream. */
export type OutboxEvent = OpencodeHookEvent

export type OutboxStatus = {
  /** Events currently spooled, awaiting delivery. Never exceeds `capacity`. */
  queued: number
  capacity: number
  /** Lifetime count of events dropped oldest-first when the spool was full. */
  dropped_spans: number
  /** Lifetime count of events the receiver accepted (2xx). */
  sent_spans: number
  /** Lifetime count of failed POST attempts (network or non-2xx). */
  failed_attempts: number
  lastError: string | null
  lastStatusCode: number | null
  endpoint: string
}

export type OutboxOptions = {
  /** Maximum spooled events; beyond this the oldest is dropped. */
  capacity?: number
  /** Maximum events per POST; halved on 429 down to 1. */
  batchSize?: number
  endpoint?: string
  fetchFn?: (url: string, init?: RequestInit) => Promise<Response>
  /** First backoff delay after a failed attempt. Defaults to 250ms. */
  baseBackoffMs?: number
  /** Backoff ceiling. Defaults to 5000ms. */
  maxBackoffMs?: number
  /** Injectable clock for tests; defaults to a real timer. */
  sleepFn?: (ms: number) => Promise<void>
  /** Injectable entropy for tests; defaults to `Math.random`. */
  randomFn?: () => number
}

export type Outbox = {
  /** The plugin `emit` port: enqueue fire-and-forget, never blocks the host. */
  emit: OpencodePluginEmitter
  /** Synchronous, non-throwing spool; drops oldest-first when full. */
  enqueue: (event: OutboxEvent) => void
  /** Snapshot of the spool in arrival order (oldest first). */
  peek: () => readonly OutboxEvent[]
  /** Current accounting, including the `dropped_spans` metric. */
  status: () => OutboxStatus
  /** Wait for the in-flight background pass to settle; the queue is kept. */
  flushSettled: () => Promise<void>
  /** Pass until the queue is empty; resolves once the receiver caught up. */
  drain: () => Promise<void>
  /** Stop scheduling further work; in-flight POSTs are left to settle. */
  close: () => Promise<void>
}

const DEFAULT_CAPACITY = 500
const DEFAULT_BATCH_SIZE = 50
const DEFAULT_BASE_BACKOFF_MS = 250
const DEFAULT_MAX_BACKOFF_MS = 5000

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}

/** Create the bounded outbox. Construction touches no I/O. */
export function createOutbox(options: OutboxOptions = {}): Outbox {
  const capacity = Math.max(1, Math.floor(options.capacity ?? DEFAULT_CAPACITY))
  const maxBatchSize = Math.max(1, Math.floor(options.batchSize ?? DEFAULT_BATCH_SIZE))
  const endpoint = options.endpoint ?? DEFAULT_OTLP_ENDPOINT
  const fetchFn = options.fetchFn ?? ((url, init) => fetch(url, init))
  const baseBackoffMs = Math.max(1, options.baseBackoffMs ?? DEFAULT_BASE_BACKOFF_MS)
  const maxBackoffMs = Math.max(baseBackoffMs, options.maxBackoffMs ?? DEFAULT_MAX_BACKOFF_MS)
  const sleepFn =
    options.sleepFn ??
    ((ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms)))
  const randomFn = options.randomFn ?? Math.random

  const queue: OutboxEvent[] = []
  let effectiveBatchSize = maxBatchSize
  let consecutiveFailures = 0
  let droppedSpans = 0
  let sentSpans = 0
  let failedAttempts = 0
  let lastError: string | null = null
  let lastStatusCode: number | null = null
  let closed = false
  /**
   * Serializes every flush pass. Chaining through the microtask queue also
   * defers the first POST past the synchronous `enqueue` burst that
   * scheduled it, so one burst ships as full batches instead of singletons.
   */
  let chain: Promise<void> = Promise.resolve()

  /** Exponential backoff with full-range jitter around the nominal delay. */
  const backoffDelayMs = (): number => {
    const nominal = Math.min(maxBackoffMs, baseBackoffMs * 2 ** consecutiveFailures)
    const jittered = nominal * (0.5 + randomFn())
    return Math.max(1, Math.floor(jittered))
  }

  const postBatch = async (batch: readonly OutboxEvent[]): Promise<number> => {
    let response: Response
    try {
      response = await fetchFn(endpoint, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(buildOtlpTracesBody(batch)),
      })
    } catch (error) {
      throw error instanceof Error ? error : new Error(String(error))
    }
    if (!response.ok) {
      throw new OtlpDeliveryError(response.status, response.statusText)
    }
    return response.status
  }

  /**
   * One batch attempt: POST a view of the head of the queue and remove it
   * only once the receiver accepts it. A failure leaves the batch in place,
   * records the accounting, sleeps the backoff once, and reports the
   * outcome — the queue itself never exceeds `capacity`, and in-flight
   * events stay visible in `queued` until they are accepted.
   */
  const attemptBatch = async (): Promise<'sent' | 'failed' | 'empty'> => {
    if (queue.length === 0 || closed) return 'empty'
    const batch = queue.slice(0, effectiveBatchSize)
    let acceptedStatus: number
    try {
      acceptedStatus = await postBatch(batch)
    } catch (error) {
      failedAttempts += 1
      consecutiveFailures += 1
      lastError = messageOf(error)
      lastStatusCode = error instanceof OtlpDeliveryError ? error.statusCode : null
      if (error instanceof OtlpDeliveryError && error.statusCode === 429) {
        // The receiver is throttling: halve the next batch, floor 1.
        effectiveBatchSize = Math.max(1, Math.floor(effectiveBatchSize / 2))
      }
      await sleepFn(backoffDelayMs())
      return 'failed'
    }
    queue.splice(0, batch.length)
    sentSpans += batch.length
    consecutiveFailures = 0
    lastStatusCode = acceptedStatus
    // A recovered receiver takes full batches again.
    effectiveBatchSize = maxBatchSize
    return 'sent'
  }

  /**
   * Queue `work` behind every earlier pass. Passes never reject (failures
   * are recorded and kept in the queue), and the defensive catch keeps a
   * late rejection from escaping as an unhandled rejection past the host
   * session.
   */
  const runExclusive = (work: () => Promise<void>): Promise<void> => {
    const next = chain.then(work, work)
    chain = next.catch(() => undefined)
    return chain
  }

  /**
   * One background pass: keep sending while the receiver accepts, then stop.
   * A failed attempt already slept its backoff inside `attemptBatch`; the
   * pass returns so a permanently-down receiver cannot spin the loop hot —
   * the next `enqueue` or `drain` continues.
   */
  const backgroundPass = async (): Promise<void> => {
    while (queue.length > 0 && !closed) {
      if ((await attemptBatch()) !== 'sent') return
    }
  }

  const triggerFlush = (): void => {
    if (closed) return
    void runExclusive(backgroundPass)
  }

  const enqueue = (event: OutboxEvent): void => {
    if (closed) return
    if (queue.length >= capacity) {
      queue.shift()
      droppedSpans += 1
    }
    queue.push(event)
    triggerFlush()
  }

  const emit: OpencodePluginEmitter = (event) => {
    enqueue(event)
  }

  const flushSettled = async (): Promise<void> => {
    // The tip at call time already contains every pass scheduled by the
    // synchronous `enqueue` burst above it in the microtask queue.
    await chain
  }

  const drain = async (): Promise<void> => {
    await runExclusive(async () => {
      while (queue.length > 0 && !closed) {
        // `attemptBatch` sleeps its own backoff on failure, so this loop
        // retries until the receiver accepts everything spooled.
        if ((await attemptBatch()) === 'empty') return
      }
    })
  }

  const close = async (): Promise<void> => {
    closed = true
  }

  const status = (): OutboxStatus => ({
    queued: queue.length,
    capacity,
    dropped_spans: droppedSpans,
    sent_spans: sentSpans,
    failed_attempts: failedAttempts,
    lastError,
    lastStatusCode,
    endpoint,
  })

  const peek = (): readonly OutboxEvent[] => [...queue]

  return { emit, enqueue, peek, status, flushSettled, drain, close }
}
