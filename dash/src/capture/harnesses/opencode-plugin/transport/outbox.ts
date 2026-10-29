// T5 bounded outbox: offline spool with retry/backoff/jitter, drop-oldest
// accounting, 429 batch-halving, and a status surface.
//
// The host contract is absolute: `enqueue` (and therefore the plugin's
// `emit` port built on it) is synchronous, never throws, and never blocks.
// When the loopback receiver is down, events spool up to `capacity`; beyond
// that the oldest event is dropped and `dropped_spans` counts it. A
// background pass retries with exponential backoff and full-range jitter; a
// 429 halves the effective batch for the next attempt. `drain()` keeps
// passing until the queue is empty (or `close()` runs), so a receiver that
// comes back up sees everything spooled during the outage.
//
// Memory is bounded by construction (F5): the queue never holds more than
// `capacity` events, a failed batch stays in place without duplicating it,
// and — unlike the earlier promise-chain design — an enqueue burst schedules
// no per-event continuation. One worker loop is ever active; concurrent
// `enqueue` calls only set a coalesced wake flag. Each POST carries an abort
// deadline (`requestTimeoutMs`) and its response body is disposed, so a hung
// or chatty transport cannot wedge the worker; `close()` aborts the
// in-flight POST and settles every waiter.

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
  /** Backoff waits currently parked on `close()`; at most one — the worker is single-flight. */
  pending_waits: number
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
  /**
   * Abort deadline per POST (F5). A transport that never answers is aborted
   * instead of holding the single worker forever. Defaults to 10s.
   */
  requestTimeoutMs?: number
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
  /** Stop scheduling further work; aborts the in-flight POST and settles every waiter. */
  close: () => Promise<void>
}

const DEFAULT_CAPACITY = 500
const DEFAULT_BATCH_SIZE = 50
const DEFAULT_BASE_BACKOFF_MS = 250
const DEFAULT_MAX_BACKOFF_MS = 5000
const DEFAULT_REQUEST_TIMEOUT_MS = 10_000
/** Bound on one response-body teardown: disposal must never wedge the worker. */
const BODY_DISPOSE_TIMEOUT_MS = 250

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
  const requestTimeoutMs = Math.max(1, Math.floor(options.requestTimeoutMs ?? DEFAULT_REQUEST_TIMEOUT_MS))

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
   * Single-flight flush state (F5). `active` is the one running worker loop,
   * if any; `wake` is the coalesced reschedule flag an `enqueue` burst sets
   * without allocating per-event continuations. `draining` selects the loop
   * mode: the background trigger stops after one failed attempt (the backoff
   * already slept), while `drain()` retries through failures until the queue
   * empties or `close()` runs.
   */
  let active: Promise<void> | null = null
  let wake = false
  let draining = false
  /** A worker start deferred past the synchronous `enqueue` burst (microtask). */
  let scheduled = false
  /** Abort handle of the in-flight POST, if any; `close()` fires it. */
  let inflight: AbortController | null = null
  /**
   * Cancelable backoff waits (F5 review): each parked `sleepOrClosed` registers
   * exactly one listener here and removes it on settle, so retries accumulate
   * no continuations against a lifetime promise. Single-flight work means at
   * most one entry; `close()` fires and clears them.
   */
  const closeWaiters = new Set<() => void>()

  /** Exponential backoff with full-range jitter around the nominal delay. */
  const backoffDelayMs = (): number => {
    const nominal = Math.min(maxBackoffMs, baseBackoffMs * 2 ** consecutiveFailures)
    const jittered = nominal * (0.5 + randomFn())
    return Math.max(1, Math.floor(jittered))
  }

  /**
   * Fire-and-forget response-body disposal. Cancel releases the socket; a
   * cancellation that never settles must not wedge the worker, so the wait
   * itself is bounded and unobserved. Late arrivals (a POST that resolves
   * after its attempt already aborted) go through the same path, so no
   * socket or body leaks while nobody awaits them.
   */
  const disposeBody = (response: Response): void => {
    try {
      const cancelled = response.body?.cancel()
      if (cancelled !== undefined) {
        void Promise.race([
          cancelled.catch(() => undefined),
          new Promise<void>((resolve) => setTimeout(resolve, BODY_DISPOSE_TIMEOUT_MS)),
        ])
      }
    } catch {
      // Body teardown must never fail a delivery the receiver accepted.
    }
  }

  const postBatch = async (batch: readonly OutboxEvent[]): Promise<number> => {
    const controller = new AbortController()
    inflight = controller
    const timer = setTimeout(() => {
      controller.abort(new Error(`OTLP POST timed out after ${requestTimeoutMs}ms`))
    }, requestTimeoutMs)
    let response: Response | undefined
    try {
      // The deadline must settle the attempt even when the transport ignores
      // the signal: race the POST against the abort instead of merely handing
      // the signal over. Everything the port touches — body construction, the
      // fetch call itself — runs inside the guarded region, so a synchronous
      // throw cleans up the timer and the in-flight handle like any failure.
      const pending = fetchFn(endpoint, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(buildOtlpTracesBody(batch)),
        signal: controller.signal,
      })
      const aborted = new Promise<never>((_, reject) => {
        if (controller.signal.aborted) {
          reject(controller.signal.reason ?? new Error('OTLP POST aborted'))
        } else {
          controller.signal.addEventListener(
            'abort',
            () => reject(controller.signal.reason ?? new Error('OTLP POST aborted')),
            { once: true },
          )
        }
      })
      void aborted.catch(() => undefined)
      try {
        response = await Promise.race([pending, aborted])
      } catch (error) {
        // The attempt aborted but the POST may still resolve late: release
        // its body without blocking this failure. The race already counts
        // the POST as handled, so this is disposal only.
        void pending.then(
          (late) => disposeBody(late),
          () => undefined,
        )
        throw error instanceof Error ? error : new Error(String(error))
      }
      if (!response.ok) {
        throw new OtlpDeliveryError(response.status, response.statusText)
      }
      return response.status
    } finally {
      clearTimeout(timer)
      if (inflight === controller) inflight = null
      // Disposal, not consumption, and never awaited: a faulty transport
      // answering an endless body — or a cancellation that never settles —
      // must not hold the worker or `close()` past the deadline.
      if (response !== undefined) disposeBody(response)
    }
  }

  /**
   * Backoff that also settles when `close()` runs, so shutdown never waits out
   * a delay. One listener per parked wait, removed on settle — no per-retry
   * continuations accumulate against a lifetime promise.
   */
  const sleepOrClosed = (ms: number): Promise<void> => {
    if (closed) return Promise.resolve()
    return new Promise<void>((resolve) => {
      let settled = false
      const done = (): void => {
        if (settled) return
        settled = true
        closeWaiters.delete(done)
        resolve()
      }
      closeWaiters.add(done)
      sleepFn(ms).then(done, done)
    })
  }

  /**
   * Yield to the event loop (a macrotask, not a microtask). Retry and waiter
   * loops below must never hot-spin on microtasks alone: with an immediate
   * clock and a refused connection every step would resolve in microtasks
   * and `setTimeout`-driven work — including `close()` — would starve while
   * the heap grows.
   */
  const yieldEventLoop = async (): Promise<void> => {
    await new Promise<void>((resolve) => setTimeout(resolve, 0))
  }

  /**
   * One batch attempt: POST a view of the head of the queue and remove it
   * only once the receiver accepts it. A failure leaves the batch in place,
   * records the accounting, sleeps the backoff once, and reports the
   * outcome — the queue itself never exceeds `capacity`, and in-flight
   * events stay visible in `queued` until they are accepted.
   *
   * Removal is by queue ownership identity (F5 review): an event evicted
   * oldest-first while its POST was still in flight is already counted in
   * `dropped_spans` and is gone, so only batch members still present are
   * removed — and only those count as sent. A count-based splice would
   * delete a never-sent replacement without counting it anywhere.
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
      await sleepOrClosed(backoffDelayMs())
      return 'failed'
    }
    const inBatch = new Set<OutboxEvent>(batch)
    let sent = 0
    for (let index = queue.length - 1; index >= 0; index -= 1) {
      if (inBatch.has(queue[index]!)) {
        queue.splice(index, 1)
        sent += 1
      }
    }
    sentSpans += sent
    consecutiveFailures = 0
    lastStatusCode = acceptedStatus
    // A recovered receiver takes full batches again.
    effectiveBatchSize = maxBatchSize
    return 'sent'
  }

  /**
   * The one worker loop. It runs while there is flagged work; a background
   * trigger stops after the first failed attempt (the backoff already slept,
   * and the next `enqueue` or `drain` continues), while a `drain()` retries
   * through failures. Unexpected failures are accounted, never swallowed: a
   * last-resort catch records them like any failed attempt instead of
   * hiding a new failure mode.
   */
  const workerLoop = async (): Promise<void> => {
    try {
      for (;;) {
        if (closed) return
        wake = false
        const keepGoing = draining
        for (;;) {
          if (closed || queue.length === 0) break
          const outcome = await attemptBatch()
          if (outcome === 'sent') continue
          if (outcome === 'empty') break
          if (!keepGoing) return
          // Drain retries through failures: yield between attempts so a hot
          // failure cannot starve the event loop (see `yieldEventLoop`).
          await yieldEventLoop()
        }
        if (closed) return
        if (draining && queue.length === 0) draining = false
        if (!wake && !(draining && queue.length > 0)) return
      }
    } catch (error) {
      failedAttempts += 1
      consecutiveFailures += 1
      lastError = messageOf(error)
    }
  }

  const ensureWorker = (): void => {
    if (closed || active !== null || scheduled) return
    // Deferred past the synchronous burst that scheduled it, so one burst
    // ships as full batches instead of singletons — and a synchronous
    // `close()` still wins the race before any POST is attempted.
    scheduled = true
    void Promise.resolve().then(() => {
      scheduled = false
      if (closed || active !== null) return
      // `workerLoop` accounts its own unexpected failures; this outer catch
      // is unreachable by construction, and still accounts rather than hides.
      const running = workerLoop().catch((error: unknown) => {
        failedAttempts += 1
        lastError = messageOf(error)
      })
      active = running
      void running.finally(() => {
        if (active === running) active = null
        // A wake that landed while the loop exited restarts exactly one loop.
        if ((wake || (draining && queue.length > 0)) && !closed) ensureWorker()
      })
    })
  }

  const triggerFlush = (): void => {
    if (closed) return
    wake = true
    ensureWorker()
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

  /**
   * Wait until the worker is idle with no flagged work. Under a steady
   * arrival stream this waits for a lull, like the old chain tip did — but
   * arrivals schedule no per-event continuation, so the wait itself is free.
   */
  const flushSettled = async (): Promise<void> => {
    for (;;) {
      const running = active
      if (running !== null) {
        await running
        continue
      }
      if (wake || scheduled) {
        // A flag set (or start deferred) but no loop yet: yield so the
        // scheduler runs, then recheck.
        await yieldEventLoop()
        continue
      }
      return
    }
  }

  /**
   * Pass until the queue is empty or `close()` runs. A receiver that comes
   * back up sees everything spooled during the outage; a receiver that
   * stays down leaves `drain()` pending until `close()` settles it — it
   * never spins hot (each attempt sleeps its backoff) and never hangs past
   * shutdown.
   */
  const drain = async (): Promise<void> => {
    draining = true
    wake = true
    ensureWorker()
    try {
      for (;;) {
        if (closed) return
        if (queue.length === 0) {
          const running = active
          if (running === null && !wake && !scheduled) return
          if (running !== null) await running
          else await yieldEventLoop()
          continue
        }
        const running = active
        if (running !== null) await running
        else await yieldEventLoop()
      }
    } finally {
      draining = false
    }
  }

  const close = async (): Promise<void> => {
    if (!closed) {
      closed = true
      // Settle every parked backoff wait, then forget them: no per-retry
      // continuation survives shutdown.
      for (const done of [...closeWaiters]) done()
      closeWaiters.clear()
    }
    wake = false
    draining = false
    scheduled = false
    inflight?.abort(new Error('outbox closed'))
    const running = active
    if (running !== null) await running
  }

  const status = (): OutboxStatus => ({
    queued: queue.length,
    capacity,
    dropped_spans: droppedSpans,
    sent_spans: sentSpans,
    failed_attempts: failedAttempts,
    pending_waits: closeWaiters.size,
    lastError,
    lastStatusCode,
    endpoint,
  })

  const peek = (): readonly OutboxEvent[] => [...queue]

  return { emit, enqueue, peek, status, flushSettled, drain, close }
}
