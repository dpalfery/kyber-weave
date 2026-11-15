// Optional Aspire ingest source for KyberDash (spec: docs/specs/kyberdash,
// design.md "Ingest layer", decision D3). Where `OtlpReceiver` owns the
// OTLP port and decodes whatever collectors push, this source covers the
// shop that already runs a .NET Aspire dashboard (R2.6): instead of asking
// its collectors to change, KyberDash polls the dashboard's export endpoint
// and pulls the same OTLP payloads back out, so an existing corpus stays
// readable.
//
// Pull changes the failure model in two ways, and both shape this file.
//
// First, polling can fail: the dashboard may be down, restarting, or serving
// an undecodable body. The source is therefore *supervised* — every failed
// poll is retried on exponential backoff capped at 30 s, so a degraded
// dashboard slows ingestion down without stopping it, and without the
// optional source ever becoming a prerequisite for KyberDash itself (that
// is the D3 rejection: Aspire required of nobody).
//
// Second, the dashboard is a ring buffer, and eviction is a measured
// data-loss class (R2.7): 25 of 1,009 exported spans had already lost their
// parent, and 17 sessions held 27 run identifiers against only 20 surviving
// run spans. Owning the receiver (the default path) removes the problem
// outright, but this source still reads exports with holes in them, so the
// Python pipeline's workaround — stop grouping by ancestry, group by
// attribute instead — is kept here as a first-class seam
// (`groupByAttribute`).
//
// Two boundaries are deliberately *not* re-drawn:
//  - Decoding is the receiver's, verbatim: `fetchAspireExport` runs the
//    payload through the receiver's own `decodeOtlpJson` /
//    `decodeOtlpProtobuf`, so an export and a POST of the same bytes land as
//    identical records. That identity is pinned by test.
//  - Persistence goes through the receiver's `OtlpSpanStore` port. The
//    batching, backpressure-owning `IngestWriter` (task 6.2) implements that
//    port against `CanonStore`; wired with it, this source persists through
//    exactly the path a POST takes. Fabricating canonical records (harness,
//    tokens, cost) here instead would duplicate the normalization layer the
//    receiver deliberately leaves alone. Re-polling an unchanged export
//    re-writes the same spans, which the store collapses idempotently on
//    span id (R2.5) — the same contract a re-POST exercises.

import {
  InMemorySpanStore,
  decodeOtlpJson,
  decodeOtlpProtobuf,
  type OtlpSpan,
  type OtlpSpanStore,
} from './receiver.js'

/** How often a healthy source re-reads the dashboard's export endpoint. */
export const DEFAULT_POLL_INTERVAL_MS = 5_000

/** First retry delay after a failed poll; doubles per consecutive failure. */
export const DEFAULT_BACKOFF_BASE_MS = 1_000

/** Backoff ceiling: a dashboard that stays down is retried at most every 30 s. */
export const MAX_BACKOFF_MS = 30_000

/**
 * The attribute the R2.7 workaround groups by. `session.id`, because the
 * measured loss was session-scale — 17 sessions held 27 run identifiers —
 * so the session, not the trace, is the identity that survives ring-buffer
 * eviction.
 */
export const DEFAULT_GROUP_ATTRIBUTE = 'session.id'

/** A poll of the export endpoint failed before decoding produced spans. */
export class AspireExportError extends Error {
  constructor(message: string, options?: { cause?: unknown }) {
    super(message, options)
    this.name = 'AspireExportError'
  }
}

export type AspireSourceOptions = {
  /**
   * Full URL of the dashboard's span export endpoint, e.g.
   * `http://127.0.0.1:18888/api/traces` — the source GETs exactly this.
   */
  dashboardUrl: string
  /** Where decoded spans go; the same port the receiver writes to. */
  store?: OtlpSpanStore
  /** Poll cadence while healthy (default 5 s). */
  pollIntervalMs?: number
  /** First backoff delay after a failed poll (default 1 s). */
  backoffBaseMs?: number
  /** Backoff ceiling (default 30 s). */
  maxBackoffMs?: number
  /**
   * Invoked for every failed poll with the error and the delay before the
   * next attempt. The source keeps polling regardless; this is the owner's
   * logging/diagnostic hook.
   */
  onPollError?: (err: unknown, backoffMs: number) => void
}

/**
 * The optional Aspire ingest source (R2.6). `start()` begins a supervised
 * poll loop — poll immediately, then every `pollIntervalMs`; any failure
 * (network error, non-2xx, undecodable body) backs off exponentially up to
 * `maxBackoffMs` and a successful poll resets the ladder. `stop()` halts the
 * loop, cancels an in-flight fetch, and resolves once nothing is pending.
 */
export class AspireSource {
  readonly store: OtlpSpanStore

  private readonly dashboardUrl: string
  private readonly pollIntervalMs: number
  private readonly backoffBaseMs: number
  private readonly maxBackoffMs: number
  private readonly onPollError: ((err: unknown, backoffMs: number) => void) | undefined

  private loop: Promise<void> | undefined
  private releaseStop: (() => void) | undefined
  private stopped = true
  private abortController = new AbortController()

  constructor(opts: AspireSourceOptions) {
    this.dashboardUrl = opts.dashboardUrl
    this.store = opts.store ?? new InMemorySpanStore()
    this.pollIntervalMs = opts.pollIntervalMs ?? DEFAULT_POLL_INTERVAL_MS
    this.backoffBaseMs = opts.backoffBaseMs ?? DEFAULT_BACKOFF_BASE_MS
    this.maxBackoffMs = opts.maxBackoffMs ?? MAX_BACKOFF_MS
    this.onPollError = opts.onPollError
  }

  /** Whether the supervised loop is running. */
  get running(): boolean {
    return !this.stopped
  }

  /** Begin the supervised poll loop. The first poll starts immediately. */
  start(): void {
    if (this.running) return
    this.stopped = false
    const stopped = new Promise<void>((resolve) => {
      this.releaseStop = resolve
    })
    this.loop = this.supervise(stopped)
  }

  /**
   * Halt the loop. Cancels an in-flight fetch, waits for the loop to settle,
   * and leaves the source restartable. A poll that already completed writes
   * what it fetched (draining, not discarding); no further poll is
   * scheduled.
   */
  async stop(): Promise<void> {
    if (this.stopped) return
    this.stopped = true
    this.abortController.abort()
    this.releaseStop?.()
    const loop = this.loop
    if (loop !== undefined) await loop
    this.loop = undefined
    this.releaseStop = undefined
    this.abortController = new AbortController()
  }

  /**
   * One poll: fetch the export endpoint, decode it with the receiver's
   * decoders, write the spans to the store. Public so a caller can pull once
   * — a re-ingest script (R15.3) or a test — without starting the supervised
   * loop; the loop runs this same method, so one-shot and supervised polls
   * cannot drift apart.
   */
  async pollOnce(): Promise<OtlpSpan[]> {
    const spans = await this.fetchAspireExport()
    this.store.write(spans)
    return spans
  }

  /**
   * GET the dashboard's export endpoint and decode the body with the
   * receiver's own decoders — the same `ExportTraceServiceRequest` contract
   * as `POST /v1/traces`, in either OTLP encoding. A body that is not OTLP
   * is rejected whole with the receiver's diagnostic (`OtlpDecodeError`);
   * transport-level failures surface as `AspireExportError`.
   */
  async fetchAspireExport(): Promise<OtlpSpan[]> {
    let response: Response
    try {
      response = await fetch(this.dashboardUrl, {
        headers: { accept: 'application/json, application/x-protobuf, application/protobuf' },
        signal: this.abortController.signal,
      })
    } catch (err) {
      throw new AspireExportError(`Aspire export fetch failed: ${message(err)}`, { cause: err })
    }
    if (!response.ok) {
      throw new AspireExportError(
        `Aspire export endpoint answered ${response.status} ${response.statusText}`.trim(),
      )
    }
    const encoding = encodingForExportContentType(response.headers.get('content-type'))
    if (encoding === undefined) {
      throw new AspireExportError(
        `Aspire export content type ${response.headers.get('content-type') ?? '(none)'} is not supported: use application/json or application/x-protobuf`,
      )
    }
    let body: Uint8Array
    try {
      body = new Uint8Array(await response.arrayBuffer())
    } catch (err) {
      throw new AspireExportError(`Aspire export body read failed: ${message(err)}`, { cause: err })
    }
    return encoding === 'json'
      ? decodeOtlpJson(new TextDecoder().decode(body))
      : decodeOtlpProtobuf(body)
  }

  /**
   * The supervised loop. Backoff doubles per consecutive failure and is
   * capped at `maxBackoffMs`; one successful poll resets it to the base, so
   * a blip after an hour of uptime retries from 1 s, not from wherever the
   * last outage climbed to.
   */
  private async supervise(stopped: Promise<void>): Promise<void> {
    let backoffMs = this.backoffBaseMs
    while (!this.stopped) {
      try {
        await this.pollOnce()
        backoffMs = this.backoffBaseMs
      } catch (err) {
        // A poll cancelled by stop() is shutdown, not a failure to report.
        if (this.stopped) return
        this.onPollError?.(err, backoffMs)
        await sleepOrStop(backoffMs, stopped)
        backoffMs = Math.min(backoffMs * 2, this.maxBackoffMs)
        continue
      }
      await sleepOrStop(this.pollIntervalMs, stopped)
    }
  }
}

// ---------------------------------------------------------------------------
// Attribute grouping (R2.7)
// ---------------------------------------------------------------------------

/**
 * Group spans by an attribute instead of by ancestry (R2.7). The Aspire
 * dashboard is a ring buffer, and a span's parent may have been evicted
 * before the export ran; a grouping that walks parent links would strand
 * those spans. The group key is the named attribute's value
 * (`session.id` by default — the session is the identity that survived the
 * measured loss, and one session legitimately spans several trace ids). A
 * span without a usable value for the attribute falls back to its trace id,
 * which is itself an attribute of the span rather than a parent link, so no
 * span is ever left ungrouped.
 */
export function groupByAttribute(
  spans: readonly OtlpSpan[],
  attributeKey: string = DEFAULT_GROUP_ATTRIBUTE,
): Map<string, OtlpSpan[]> {
  const groups = new Map<string, OtlpSpan[]>()
  for (const span of spans) {
    const key = attributeGroupKey(span, attributeKey)
    const group = groups.get(key)
    if (group === undefined) groups.set(key, [span])
    else group.push(span)
  }
  return groups
}

function attributeGroupKey(span: OtlpSpan, attributeKey: string): string {
  const value = span.attributes[attributeKey]
  // Only a non-empty string groups: that is the form a session identifier
  // takes. Missing, empty, null and structured values carry no session
  // identity, and the trace id takes over rather than inventing a key the
  // telemetry never stated.
  if (typeof value === 'string' && value.length > 0) return value
  return span.traceId
}

// ---------------------------------------------------------------------------
// Private helpers
// ---------------------------------------------------------------------------

function message(err: unknown): string {
  return err instanceof Error ? err.message : String(err)
}

/**
 * Media-type detection for the export endpoint. Mirrors the receiver's
 * content-type logic (JSON or protobuf) with one pull-source difference: a
 * response with no content type at all is read as JSON — a file-backed or
 * bare-endpoint export commonly omits the header, and the dashboard's own
 * API serves JSON. An explicitly wrong type (`text/html`) is still an error.
 */
function encodingForExportContentType(value: string | null): 'json' | 'protobuf' | undefined {
  const mediaType = (value ?? '').split(';')[0].trim().toLowerCase()
  if (mediaType === '') return 'json'
  if (mediaType === 'application/json') return 'json'
  if (mediaType === 'application/x-protobuf' || mediaType === 'application/protobuf') {
    return 'protobuf'
  }
  return undefined
}

/**
 * Sleep that `stop()` interrupts: resolves early once `stopped` settles and
 * never leaves the timeout handle behind.
 */
function sleepOrStop(ms: number, stopped: Promise<void>): Promise<void> {
  return new Promise<void>((resolve) => {
    let settled = false
    const finish = (): void => {
      if (settled) return
      settled = true
      clearTimeout(timer)
      resolve()
    }
    const timer = setTimeout(finish, ms)
    void stopped.then(finish)
  })
}
