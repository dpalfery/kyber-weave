import type { IncomingMessage, ServerResponse } from 'http'
import type { CanonStore } from '../canon/store.js'
import type { JobHost, JobOutcome } from '../jobs/host.js'
import { storeGeneration } from './store-generation.js'
import { applyPartialSettings, parseSettingsPatch, readSettings } from './settings-view.js'
import type { KyberBridge } from './bridge.js'
import { sumSessionFigures } from './bridge.js'
import { runContextReview, type ReviewRequest } from '../analysis/review.js'
import { createReviewProvider } from '../analysis/review-providers/index.js'
import { recordPrediction } from '../analysis/calibration.js'
import { buildScorecard } from '../analysis/scorecard.js'
import { buildContextReport } from '../analysis/report/build.js'
import {
  DEFAULT_FINDING_LIMIT,
  DEFAULT_SECTIONS,
  DEFAULT_WINDOW_DAYS,
  REPORT_SCHEMA_VERSION,
  type ReportScope,
  type ReportSection,
} from '../analysis/report/types.js'
import { createRequire } from 'node:module'
import { harnessFamily, normalizeHarnessName } from '../canon/measurability.js'
import { resolveHarnessScope } from '../refresh/registry.js'
import type { SourceCheckpoint } from '../canon/source-state.js'
import type { CleanRequest } from '../clean/clean.js'
import { MAX_CLEAN_REINGEST_WEEKS } from '../clean/clean.js'

/** The build this server is, carried on `/meta` so a client can check it (R6.7). */
const KYBERDASH_VERSION = String(
  (createRequire(import.meta.url)('../../package.json') as { version?: string }).version ?? '0.0.0',
)

const VALID_SECTIONS: readonly ReportSection[] = [
  'coverage',
  'detection',
  'findings',
  'harnesses',
  'latestSession',
  'cost',
]

/**
 * Read the report scope off the query string, or return the message explaining why it is
 * unusable. A bad `days` is rejected rather than silently defaulted: a caller asking for
 * `days=-1` has a bug, and answering with a 7-day window would hide it.
 */
function parseReportScope(url: URL): ReportScope | string {
  const raw = url.searchParams.get('days')
  let days = DEFAULT_WINDOW_DAYS
  if (raw !== null && raw.trim() !== '') {
    const parsed = Number(raw)
    if (!Number.isInteger(parsed) || parsed <= 0) {
      return `days must be a positive integer (got ${JSON.stringify(raw)})`
    }
    days = parsed
  }
  const scope: ReportScope = { days }
  const harness = url.searchParams.get('harness')?.trim()
  const sessionId = url.searchParams.get('session')?.trim()
  const runId = url.searchParams.get('run')?.trim()
  if (harness) scope.harness = harness
  if (sessionId) scope.sessionId = sessionId
  if (runId) scope.runId = runId
  return scope
}

/**
 * `sections` is a comma list; absent means every section except `detection`, which walks
 * the filesystem and so is opt-in (design C1). An unknown name is an error rather than
 * ignored, so a typo does not silently return a report missing what was asked for.
 */
function parseSections(url: URL): readonly ReportSection[] | string {
  const raw = url.searchParams.get('sections')
  if (raw === null || raw.trim() === '') return DEFAULT_SECTIONS
  const asked = raw.split(',').map((part) => part.trim()).filter(Boolean)
  const unknown = asked.filter((name) => !VALID_SECTIONS.includes(name as ReportSection))
  if (unknown.length > 0) {
    return `unknown section(s): ${unknown.join(', ')}; valid: ${VALID_SECTIONS.join(', ')}`
  }
  return asked as ReportSection[]
}

/**
 * `/api/kyber/session/:id/content` — everything between the prefix and the
 * `/content` suffix is the session id, so encoded ids stay intact. `null`
 * means this is not the content route (so `/session/content` is not treated
 * as an empty id).
 */
function parseSessionContentPath(pathname: string): string | null {
  const prefix = '/api/kyber/session/'
  const suffix = '/content'
  if (!pathname.startsWith(prefix) || !pathname.endsWith(suffix)) return null
  const middle = pathname.slice(prefix.length, pathname.length - suffix.length)
  if (!middle || middle.endsWith('/')) return null
  return decodeURIComponent(middle).trim()
}

/**
 * `/api/kyber/span/:spanId/attributes` — the harness-emitted attribute map for
 * one span (R9.2). Session payloads no longer carry these inline; see
 * `CanonStore.spanAttributes`.
 */
function parseSpanAttributesPath(pathname: string): string | null {
  const prefix = '/api/kyber/span/'
  const suffix = '/attributes'
  if (!pathname.startsWith(prefix) || !pathname.endsWith(suffix)) return null
  const middle = pathname.slice(prefix.length, pathname.length - suffix.length)
  if (!middle || middle.includes('/')) return null
  return decodeURIComponent(middle).trim()
}

/**
 * `/api/kyber/session/:id/turn/:index/content` — returns unclipped assembled context
 * for a specific turn index (Task G1 / Decision D14).
 */
function parseTurnContentPath(pathname: string): { sessionId: string; turnIndex: number } | null {
  const prefix = '/api/kyber/session/'
  const suffix = '/content'
  if (!pathname.startsWith(prefix) || !pathname.endsWith(suffix)) return null
  const middle = pathname.slice(prefix.length, pathname.length - suffix.length)
  const turnMarker = '/turn/'
  const turnIdx = middle.lastIndexOf(turnMarker)
  if (turnIdx === -1) return null
  const sessionIdRaw = middle.slice(0, turnIdx)
  const turnIndexRaw = middle.slice(turnIdx + turnMarker.length)
  if (!sessionIdRaw || !turnIndexRaw) return null
  const turnIndex = parseInt(turnIndexRaw, 10)
  if (isNaN(turnIndex)) return null
  return {
    sessionId: decodeURIComponent(sessionIdRaw).trim(),
    turnIndex,
  }
}

interface PaginationParams {
  limit: number
  offset: number
  page: number
}

function parsePaginationParams(url: URL, defaultLimit = 200): PaginationParams {
  const limitParam = url.searchParams.get('limit')
  const parsedLimit = limitParam !== null && limitParam.trim() !== '' ? parseInt(limitParam, 10) : undefined
  const limit = parsedLimit !== undefined && !isNaN(parsedLimit) && parsedLimit > 0 ? parsedLimit : defaultLimit

  const offsetParam = url.searchParams.get('offset')
  const parsedOffset = offsetParam !== null && offsetParam.trim() !== '' ? parseInt(offsetParam, 10) : undefined

  const pageParam = url.searchParams.get('page')
  const parsedPage = pageParam !== null && pageParam.trim() !== '' ? parseInt(pageParam, 10) : undefined

  let offset = 0
  let page = 1

  if (parsedOffset !== undefined && !isNaN(parsedOffset)) {
    offset = Math.max(0, parsedOffset)
    page = Math.floor(offset / limit) + 1
  } else if (parsedPage !== undefined && !isNaN(parsedPage)) {
    page = Math.max(1, parsedPage)
    offset = Math.max(0, (page - 1) * limit)
  }

  return { limit, offset, page }
}

/**
 * Verbatim zero-data reason off a rollup payload, or null when the row holds
 * data (T8, issues #189/#199). Zero-data rows keep their rollup reason
 * word-for-word: no surface may render `0 sessions` where the truth is none
 * in the coverage window.
 */
function payloadNumber(payload: unknown, key: 'sessionCount' | 'runCount'): number | undefined {
  if (payload === null || typeof payload !== 'object') return undefined
  const value = (payload as Record<string, unknown>)[key]
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

/**
 * Live session/run counts keyed by harness, so the harness list cannot drift
 * from `/sessions` and `/runs` (issue #194). Only harnesses that have a row
 * appear in the map — a miss falls back to the stored rollup figure.
 */
function liveHarnessCounts(bridge: KyberBridge): {
  sessions: Map<string, number>
  runs: Map<string, number>
} {
  // Narrow-column counts — never uncapped listSessions() (coverage-window seam).
  return { sessions: bridge.countSessionsByHarness(), runs: bridge.countRunsByHarness() }
}

function withLiveCounts<T extends { harness: string; sampleCount: number; payload?: unknown }>(
  row: T,
  live: { sessions: Map<string, number>; runs: Map<string, number> },
): T & { sessionCount: number; runCount: number; sampleCount: number } {
  const sessionCount = live.sessions.get(row.harness) ?? payloadNumber(row.payload, 'sessionCount') ?? row.sampleCount
  const runCount = live.runs.get(row.harness) ?? payloadNumber(row.payload, 'runCount') ?? row.sampleCount
  return {
    ...row,
    sessionCount,
    runCount,
    sampleCount: live.sessions.get(row.harness) ?? row.sampleCount,
  }
}

function noDataReasonOf(row: { payload?: unknown }): string | null {
  if (row.payload !== null && typeof row.payload === 'object' && 'reason' in row.payload) {
    const reason = (row.payload as { reason?: unknown }).reason
    if (typeof reason === 'string' && reason.trim() !== '') return reason
  }
  return null
}

/**
 * Per-harness checkpoint-unit counts by status (T8, read via the T4 seam).
 *
 * <remarks>
 * `unchanged` units hold reusable coverage so they count as `ok`;
 * `invalidated` units need reprocessing so they count as `failed`; any other
 * status reads as `unavailable` rather than being dropped. These are unit
 * counts — record counts are never summed and nothing is merged across
 * harnesses, so family grouping (D3) sums nothing.
 * A null seam read (failed or impossible — table absent, store locked) maps
 * to null (unknown), never to zeros: `{ok: 0, ...}` for unreadable coverage
 * is the fabricated zero the honest-unobservability rule forbids.
 * </remarks>
 */
function checkpointSummaryOf(statuses: readonly SourceCheckpoint[] | null): {
  ok: number
  partial: number
  failed: number
  unavailable: number
} | null {
  if (statuses === null) return null
  const summary = { ok: 0, partial: 0, failed: 0, unavailable: 0 }
  for (const status of statuses) {
    switch (status.lastStatus) {
      case 'ok':
      case 'unchanged':
        summary.ok += 1
        break
      case 'partial':
        summary.partial += 1
        break
      case 'failed':
      case 'invalidated':
        summary.failed += 1
        break
      default:
        summary.unavailable += 1
        break
    }
  }
  return summary
}

/**
 * Per-harness in-window state (issue #189, T8).
 *
 * The rollup reason is all-time: a harness with only pre-window history
 * carries no reason and would read as covered. When the refresh window is
 * known, the harness's own latest session timestamp decides — a latest
 * timestamp at or after `coveredFrom` keeps the verbatim state; a latest
 * timestamp older than the window overrides it with a windowed reason.
 * Anything else (unknown window, unreadable sessions, no timestamped
 * sessions at all) keeps the verbatim reason: absence of evidence is not
 * evidence of absence. Recency is `ended ?? started` parsed to epoch ms,
 * matching the report's `sessionAt` (`analysis/report/build.ts`): the end
 * is what "in the window" means, and epoch comparison sorts offset stamps
 * (`+02:00`) chronologically where a raw string compare does not.
 */
function inWindowNoDataReason(
  harness: string,
  latestByHarness: ReadonlyMap<string, number>,
  coveredFrom: string | null,
  verbatim: string | null,
): string | null {
  if (verbatim !== null || coveredFrom === null) return verbatim
  const floor = Date.parse(coveredFrom)
  if (!Number.isFinite(floor)) return verbatim
  const latest = latestByHarness.get(harness)
  if (latest === undefined) return verbatim
  if (latest >= floor) return null
  return `No sessions in coverage window (since ${coveredFrom})`
}

/**
 * The persisted window bound plus the per-harness latest session times that
 * decide per-harness in-window state, or nulls when either is unknowable.
 * Reads are fenced so a locked store degrades to verbatim reasons rather
 * than a 500. The session times come from the bridge's narrow-column
 * `getLatestSessionTimeByHarness` seam (every session row's
 * `(harness, started, ended)` folded to a per-harness maximum in JS epoch
 * ms, payload-free and uncapped — no LIMIT, no SQL MAX): this context must
 * never materialize the session table via an uncapped `listSessions()`.
 */
function windowContextOf(bridge: KyberBridge): {
  coveredFrom: string | null
  latestByHarness: ReadonlyMap<string, number>
} {
  let coveredFrom: string | null = null
  try {
    coveredFrom = bridge.getRefreshState().coveredFrom ?? null
  } catch {
    coveredFrom = null
  }
  let latestByHarness: ReadonlyMap<string, number> = new Map<string, number>()
  if (coveredFrom !== null) {
    try {
      latestByHarness = bridge.getLatestSessionTimeByHarness()
    } catch {
      latestByHarness = new Map<string, number>()
    }
  }
  return { coveredFrom, latestByHarness }
}

/**
 * Checkpoints recorded under any front-end of one harness (issue #182).
 * Null (unreadable table) stays null — never an empty list posing as none.
 */
function filterCheckpointsByHarness(
  statuses: readonly SourceCheckpoint[] | null,
  harnessId: string,
): readonly SourceCheckpoint[] | null {
  if (statuses === null) return null
  const want = normalizeHarnessName(harnessId)
  return statuses.filter((status) => normalizeHarnessName(status.harnessId) === want)
}

function groupCheckpointsByHarness(statuses: readonly SourceCheckpoint[]): Map<string, SourceCheckpoint[]> {
  // Grouped by canonical harness (issue #182): checkpoints are recorded
  // under raw front-end ids (`claude-desktop`, `cursor-agent`) while rollup
  // rows carry the folded owner, so the raw key would drop them.
  const byHarness = new Map<string, SourceCheckpoint[]>()
  for (const status of statuses) {
    const key = normalizeHarnessName(status.harnessId)
    const list = byHarness.get(key) ?? []
    list.push(status)
    byHarness.set(key, list)
  }
  return byHarness
}

function sendKyberJson(
  res: ServerResponse,
  status: number,
  body: unknown,
  headers: Record<string, string> = {},
): void {
  let serialized: string
  try {
    serialized = JSON.stringify(body)
  } catch (err) {
    // Serialization failure (e.g. circular reference) must not leak internal
    // detail to the client. Log it server-side and return a generic error.
    console.error('[KyberRoutes] Failed to serialize response:', err instanceof Error ? err.message : String(err))
    res.writeHead(500, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' })
    res.end(JSON.stringify({ error: ERR_INTERNAL }))
    return
  }
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
    ...headers,
  })
  res.end(serialized)
}

type SessionViewPayload = {
  context?: unknown
  schema?: unknown
  timeline?: unknown
}

/**
 * Bound on a `POST /api/kyber/clean` body. The payload is a tiny
 * scope/confirm/window document, so anything past 64KB is an oversized POST
 * in front of a wipe endpoint — reject it before the accumulator can grow
 * without bound (F2), matching the receiver's `readBody` discipline.
 */
const MAX_CLEAN_BODY_BYTES = 64 * 1024

/**
 * Validate a `POST /api/kyber/clean` body: exactly one scope (`all` or a
 * non-empty `harnesses` list), explicit `confirm: true`, and — when present —
 * a `reingestWeeks` between 1 and MAX_CLEAN_REINGEST_WEEKS (one year) or
 * explicit null to skip re-ingestion. Anything else answers 400 with nothing
 * wiped.
 */
function parseCleanBody(bodyText: string): CleanRequest | undefined {
  let parsed: unknown
  try {
    parsed = JSON.parse(bodyText || '{}')
  } catch {
    return undefined
  }
  if (typeof parsed !== 'object' || parsed === null) return undefined
  const body = parsed as Record<string, unknown>
  if (body['confirm'] !== true) return undefined
  const all = body['all']
  const harnesses = body['harnesses']
  const hasAll = all === true
  const hasHarnesses =
    Array.isArray(harnesses) &&
    harnesses.length > 0 &&
    harnesses.every((h) => typeof h === 'string' && h.trim() !== '')
  if ((hasAll && hasHarnesses) || (!hasAll && !hasHarnesses)) return undefined
  const request: CleanRequest = { confirm: true }
  if (hasAll) request.all = true
  else request.harnesses = (harnesses as string[]).map((h) => h.trim())
  if ('reingestWeeks' in body) {
    const weeks = body['reingestWeeks']
    if (weeks === null) {
      request.reingestWeeks = null
    } else if (typeof weeks === 'number' && Number.isSafeInteger(weeks) && weeks >= 1 && weeks <= MAX_CLEAN_REINGEST_WEEKS) {
      request.reingestWeeks = weeks
    } else {
      return undefined
    }
  }
  return request
}

/**
 * The clean route reaches its parser through this one-method object rather than calling
 * `parseCleanBody` directly, so the hardening test can force the parser to throw. A
 * malformed payload is answered 400 from inside `parseCleanBody`'s own JSON guard, so
 * nothing a client can send reaches that throw - which is the point: the route has to
 * survive the defect becoming reachable, and the only honest way to test that is to
 * inject it. Everything here still validates exactly as `parseCleanBody` does.
 */
export const cleanBodyParser: { parse(bodyText: string): CleanRequest | undefined } = {
  parse: parseCleanBody,
}

/**
 * The collaborators the shared settings, jobs and job-triggering routes need (rule R1):
 * the same store the bridge serves, and the JobHost that owns every background job. The
 * `web` server always passes them; `deps` is optional only so a caller that reaches the
 * route without a host still gets a bounded answer instead of a crash.
 */
export type KyberRouteDeps = {
  readonly store: CanonStore
  /**
   * Absent only while the host pair is still coming up. The store is resolved before the
   * socket binds (the settings routes work from the first accepted connection), the
   * JobHost is not: `JobHost.start()` awaits its first tick, whose maintenance pass can
   * outlast a client. The job routes answer 503 in that window rather than reporting a
   * server that is right there as one that does not exist.
   */
  readonly jobHost?: Pick<JobHost, 'getStatus' | 'runNow' | 'runJob'>
}

/** Fixed error strings: a failure body must never carry a key, a path or a host error. */
const ERR_INVALID_BODY = 'Invalid request payload'
const ERR_NOT_FOUND = 'Not found'
const ERR_JOB_RUNNING = 'A job is already running'
const ERR_INTERNAL = 'Internal server error'
/** The bounded answer while the JobHost is still starting; shared with the `web` server. */
export const ERR_SERVICES_STARTING = 'Background services are still starting'

/** Bound on a job-triggering request body, matching the clean route's 64KB cap (F2). */
const MAX_JSON_BODY_BYTES = 64 * 1024

/**
 * How long a started job gets to answer before the route commits to 202.
 *
 * The host answers synchronously for the cases the client must hear about (declined,
 * busy, hosted elsewhere), and stays pending for the case it must not wait for (the job
 * is running). The window only has to outlive that synchronous answer — the lease check
 * and the spawn decision — never the job itself, which takes minutes.
 *
 * It was 5ms, which the lease round trip can outlast, so a declined run answered 202. It
 * is now 25ms: comfortably above a local lease check, and still far below anything a
 * client could call a wait for a route that has already started the job.
 */
const JOB_OUTCOME_GRACE_MS = 25

function requireJsonContentType(req: IncomingMessage): boolean {
  const contentType = req.headers['content-type']
  return typeof contentType === 'string' && contentType.toLowerCase().startsWith('application/json')
}

/**
 * Close a request whose body is oversized, once the 413 has actually left.
 *
 * <remarks>
 * Destroying the socket in the same tick as `res.end()` is what made the client see
 * ECONNRESET instead of the 413: the response was still queued behind the rest of the
 * body. So the socket is destroyed on the response's own `finish`, after the bytes are
 * flushed, and `Connection: close` tells the peer the socket is finished with. Reading
 * stops first so an oversized upload is not pulled down the wire after the answer is
 * already decided.
 *
 * The `close` fallback covers a response that never finishes (client gone mid-answer):
 * without it the request would sit half-read. Both are skipped when the response object
 * has no emitter interface, which is how the route unit tests drive it; there the destroy
 * happens immediately, after `end`.
 * </remarks>
 */
function destroyAfterResponse(req: IncomingMessage, res: ServerResponse): void {
  const emitter = res as unknown as { on?: (event: string, listener: () => void) => unknown }
  if (typeof emitter.on !== 'function') {
    req.destroy()
    return
  }
  req.pause()
  res.on('finish', () => {
    req.destroy()
  })
  res.on('close', () => {
    if (!res.writableFinished) req.destroy()
  })
}

/**
 * Run a `req.on('end')` body handler under the guard that keeps it from escaping the
 * listener. `run` may throw synchronously or return a promise that rejects later - and
 * `answerFailure` decides what that answers, because the failure is route-specific (a
 * settings write and a database clean report different things).
 *
 * Both failure paths check the response before answering, and the check is the point: a
 * handler that answered 202 and *then* rejected would otherwise call `answerFailure` on a
 * finished response, and the `ERR_STREAM_WRITE_AFTER_END` thrown by that second write
 * escapes as an unhandled rejection - the process-level failure this whole guard exists
 * to prevent, reintroduced through its own error path. Nothing is owed to a client that
 * already has an answer, so a late rejection is logged and swallowed; a rejection before
 * any answer is still a real 5xx.
 *
 * Exported because no route can reach the late-rejection branch on its own - every current
 * body handler is synchronous or hands its promise to a helper that owns it - which is
 * exactly why the missing guard stayed latent.
 */
export function runGuardedBodyHandler(
  res: ServerResponse,
  run: () => void | Promise<void>,
  answerFailure: (err: unknown) => void,
): void {
  const answerable = (): boolean => !res.headersSent && !res.writableEnded
  try {
    const started = run()
    if (started instanceof Promise) {
      void started.then(undefined, (err: unknown) => {
        console.error('[KyberRoutes] request body handler failed:', err)
        if (!answerable()) return
        answerFailure(err)
      })
    }
  } catch (err) {
    console.error('[KyberRoutes] request body handler failed:', err)
    // The answer may already be out if the handler answered and then threw; see above.
    if (!answerable()) return
    answerFailure(err)
  }
}

/**
 * Read a small JSON object body and hand it to `onBody`. Answers 415 for a non-JSON
 * content type, 413 (destroying the request once the answer has flushed) above the size
 * cap, and 400 for malformed or non-object JSON — all before any engine call, so a
 * rejected request changes nothing.
 *
 * `onBody` runs from the `req.on('end')` listener, which is outside `createServer`'s
 * try/catch, so both a synchronous throw and a rejection from it are caught here. The
 * settings handler is synchronous all the way down to `store.setMetadata`, so a sqlite
 * failure (SQLITE_BUSY, a closed handle, a full disk) would otherwise become an uncaught
 * exception and take the web server down. The body is a fixed string for the same reason
 * every other failure body is: the underlying message names a path on this machine.
 */
function serveJsonObjectBody(
  req: IncomingMessage,
  res: ServerResponse,
  onBody: (body: Record<string, unknown>) => void | Promise<void>,
): void {
  let bodyText = ''
  let received = 0
  let settled = false
  const fail = (status: number, error: string): void => {
    if (settled) return
    settled = true
    sendKyberJson(res, status, { error })
  }
  const failOversize = (): void => {
    if (settled) return
    settled = true
    sendKyberJson(
      res,
      413,
      { error: `Request body exceeds the ${MAX_JSON_BODY_BYTES}-byte limit` },
      { connection: 'close' },
    )
    destroyAfterResponse(req, res)
  }
  const invoke = (body: Record<string, unknown>): void => {
    // A fixed string, like every other failure body here: a sqlite or job-host message
    // names a path on this machine.
    runGuardedBodyHandler(res, () => onBody(body), () => {
      sendKyberJson(res, 500, { error: ERR_INTERNAL })
    })
  }
  req.on('data', (chunk) => {
    if (settled) return
    received += typeof chunk === 'string' ? Buffer.byteLength(chunk) : chunk.length
    if (received > MAX_JSON_BODY_BYTES) {
      failOversize()
      return
    }
    bodyText += chunk
  })
  req.on('error', () => {
    fail(400, ERR_INVALID_BODY)
  })
  req.on('end', () => {
    if (settled) return
    let parsed: unknown
    try {
      parsed = JSON.parse(bodyText === '' ? '{}' : bodyText)
    } catch {
      fail(400, ERR_INVALID_BODY)
      return
    }
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
      fail(400, ERR_INVALID_BODY)
      return
    }
    settled = true
    invoke(parsed as Record<string, unknown>)
  })
}

/**
 * Answer a job trigger. `declined`, `busy` and `hosted-elsewhere` mean this server did not
 * start anything, and the client can act on that; a started job answers 202 and reports
 * its own outcome through `GET /api/kyber/jobs`.
 */
function sendJobStarted(res: ServerResponse, started: Promise<JobOutcome>, failure: string): void {
  let answered = false
  let grace: NodeJS.Timeout | undefined
  const answer = (status: number, body: unknown): void => {
    if (answered) return
    answered = true
    // The grace timer exists to bound a request that settles late, so it belongs to this
    // request and nothing longer: once the real answer is out, leaving it armed keeps a
    // live handle per answered request for the rest of the process. `answered` already
    // stops it writing; clearing it stops it existing.
    if (grace !== undefined) {
      clearTimeout(grace)
      grace = undefined
    }
    sendKyberJson(res, status, body)
  }
  // The rejection is answered with a fixed string: a spawn failure names real paths on
  // this machine and has no business crossing the wire.
  void started.then(
    (outcome) => {
      if (outcome.outcome === 'declined' || outcome.outcome === 'busy' || outcome.outcome === 'hosted-elsewhere') {
        answer(409, { error: ERR_JOB_RUNNING })
      }
    },
    () => {
      answer(500, { error: failure })
    },
  )
  const timer = setTimeout(() => answer(202, { accepted: true }), JOB_OUTCOME_GRACE_MS)
  // Never hold the event loop open for an answer the client is waiting on.
  if (typeof timer.unref === 'function') timer.unref()
  grace = timer
}

/**
 * A harness the import job could actually read: either a canonical harness id with a
 * source descriptor, or a family label (`codex`, what the native tray sends) that expands
 * to its member descriptors. The name is forwarded as sent, so the id the child resolves
 * never differs from the one the caller asked for — and the expansion itself is the
 * registry's, so a family label that reaches the import does not become a throw inside
 * the child after this route has already answered 202.
 */
function isImportableHarness(name: string): boolean {
  return resolveHarnessScope([name]).descriptors.length > 0
}

/** `POST /api/kyber/refresh`: only a real UI surface may ask; the host owns `scheduled`. */
function parseRefreshBody(body: Record<string, unknown>): 'tray' | 'web' | undefined {
  const surface = body['surface']
  return surface === 'tray' || surface === 'web' ? surface : undefined
}

/** A folder-history import window of 1..52 weeks; a year is the widest anyone has asked for. */
const MAX_IMPORT_HISTORY_WEEKS = 52

/**
 * `POST /api/kyber/import-history`: one-off history import, never the scheduled setting.
 * The singular `harness` (what the native tray sends) and the plural `harnesses` (the web
 * UI) are both accepted, and together they are ambiguous, so both is a 400.
 */
function parseImportHistoryBody(
  body: Record<string, unknown>,
): { weeks: number; harnesses: string[] } | undefined {
  const rawWeeks = body['weeks']
  let weeks = 1
  if (rawWeeks !== undefined) {
    if (
      typeof rawWeeks !== 'number' ||
      !Number.isSafeInteger(rawWeeks) ||
      rawWeeks < 1 ||
      rawWeeks > MAX_IMPORT_HISTORY_WEEKS
    ) {
      return undefined
    }
    weeks = rawWeeks
  }
  const hasSingular = 'harness' in body
  const hasPlural = 'harnesses' in body
  if (hasSingular && hasPlural) return undefined
  if (!hasSingular && !hasPlural) return { weeks, harnesses: [] }

  const asked: unknown[] = hasSingular ? [body['harness']] : body['harnesses'] as unknown[]
  if (!Array.isArray(asked) || asked.length === 0) return undefined
  const harnesses: string[] = []
  for (const entry of asked) {
    // descriptorFor is the registry's own resolver, so a harness the import could not read
    // is rejected here rather than failing inside the child job after a 202.
    if (typeof entry !== 'string' || !isImportableHarness(entry.trim())) {
      return undefined
    }
    harnesses.push(entry.trim())
  }
  return { weeks, harnesses }
}

export function handleKyberRequest(
  req: IncomingMessage,
  res: ServerResponse,
  url: URL,
  bridge: KyberBridge,
  deps?: KyberRouteDeps,
): boolean {
  if (!url.pathname.startsWith('/api/kyber/') && url.pathname !== '/api/kyber') {
    return false
  }

  // Kyber agent session analysis endpoints
  if (url.pathname === '/api/kyber/sessions') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const limitParam = url.searchParams.get('limit')
    const limit = limitParam ? parseInt(limitParam, 10) : undefined
    let sessions = bridge.listSessions(limit && !isNaN(limit) ? limit : undefined)
    const harnessParam = url.searchParams.get('harness')
    if (harnessParam) {
      sessions = sessions.filter((s) => s.harness?.toLowerCase() === harnessParam.toLowerCase())
    }
    sendKyberJson(res, 200, { sessions })
    return true
  }

  // One span's attributes, fetched on demand by the timeline inspector.
  const spanAttributesId = parseSpanAttributesPath(url.pathname)
  if (spanAttributesId !== null) {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    if (!spanAttributesId) {
      sendKyberJson(res, 400, { error: 'Missing span id' })
      return true
    }
    const body = bridge.getSpanAttributes(spanAttributesId)
    if (!body) {
      sendKyberJson(res, 404, { error: 'Span attributes not found' })
      return true
    }
    sendKyberJson(res, 200, body)
    return true
  }

  // Full-fidelity turn context content (Task G1 / Decision D14)
  const turnContentRoute = parseTurnContentPath(url.pathname)
  if (turnContentRoute !== null) {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const { sessionId, turnIndex } = turnContentRoute
    if (!sessionId) {
      sendKyberJson(res, 400, { error: 'Missing session id' })
      return true
    }
    const budgetParam = url.searchParams.get('budget')
    const budget = budgetParam ? parseInt(budgetParam, 10) : undefined
    const body = bridge.assembleTurnContent(sessionId, turnIndex, budget && !isNaN(budget) ? budget : undefined)
    if (!body) {
      sendKyberJson(res, 404, { error: 'Turn content not found' })
      return true
    }
    sendKyberJson(res, 200, body)
    return true
  }

  // Full-fidelity content. Must run before /session/:id — that handler would
  // otherwise treat ".../content" as part of the session id and 404.
  const contentSessionId = parseSessionContentPath(url.pathname)
  if (contentSessionId !== null) {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    if (!contentSessionId) {
      sendKyberJson(res, 400, { error: 'Missing session id' })
      return true
    }
    const turnParam = url.searchParams.get('turn')
    if (turnParam !== null && turnParam.trim() !== '') {
      const turnIndex = parseInt(turnParam, 10)
      if (!isNaN(turnIndex)) {
        const budgetParam = url.searchParams.get('budget')
        const budget = budgetParam ? parseInt(budgetParam, 10) : undefined
        const turnBody = bridge.assembleTurnContent(contentSessionId, turnIndex, budget && !isNaN(budget) ? budget : undefined)
        if (!turnBody) {
          sendKyberJson(res, 404, { error: 'Turn content not found' })
          return true
        }
        sendKyberJson(res, 200, turnBody)
        return true
      }
    }
    const span = (url.searchParams.get('span') ?? '').trim() || undefined
    const part = (url.searchParams.get('part') ?? '').trim() || undefined
    const body = bridge.getSessionContent(contentSessionId, { spanId: span, part })
    if (!body) {
      sendKyberJson(res, 404, { error: 'Session not found' })
      return true
    }
    sendKyberJson(res, 200, body)
    return true
  }

  if (url.pathname === '/api/kyber/session' || url.pathname.startsWith('/api/kyber/session/')) {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    let id = ''
    if (url.pathname.startsWith('/api/kyber/session/')) {
      id = decodeURIComponent(url.pathname.slice('/api/kyber/session/'.length)).trim()
    }
    if (!id) {
      id = (url.searchParams.get('id') ?? '').trim()
    }
    if (!id) {
      sendKyberJson(res, 400, { error: 'Missing session id' })
      return true
    }
    const payload = bridge.getSessionPayload<SessionViewPayload>(id)
    if (!payload) {
      sendKyberJson(res, 404, { error: 'Session not found' })
      return true
    }
    sendKyberJson(res, 200, payload)
    return true
  }

  // Phase-aligned run comparison (`docs/plans/2026-09-06-kyberdash-spine.md` § B4-api).
  // Must run before `/api/kyber/compare` if that path ever becomes a prefix match.
  if (url.pathname === '/api/kyber/compare/runs') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const runA = (url.searchParams.get('runA') ?? url.searchParams.get('a') ?? '').trim()
    const runB = (url.searchParams.get('runB') ?? url.searchParams.get('b') ?? '').trim()
    if (!runA || !runB) {
      sendKyberJson(res, 400, { error: 'Missing runA or runB' })
      return true
    }
    // Recommendation history is not caller-supplied on the public route (issue
    // #190): an arbitrary query count is not store-backed measurement. Reject
    // the retired parameter rather than silently ignoring it (same house rule
    // as parseReportScope for a bad `days`).
    if (url.searchParams.has('completedPairCount')) {
      sendKyberJson(res, 400, {
        error:
          'completedPairCount is not accepted: recommendation history is not caller-supplied (issue #190)',
      })
      return true
    }
    const comparison = bridge.compareRuns(runA, runB)
    if (!comparison) {
      sendKyberJson(res, 404, { error: 'Run not found' })
      return true
    }
    sendKyberJson(res, 200, comparison)
    return true
  }

  if (url.pathname === '/api/kyber/compare') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const table = bridge.getComparisonTable()
    sendKyberJson(res, 200, table)
    return true
  }

  if (url.pathname === '/api/kyber/quarantine') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const { limit, offset, page } = parsePaginationParams(url, 200)
    const entries = bridge.getQuarantine(limit, offset)
    const total = bridge.getQuarantineCount()
    sendKyberJson(res, 200, {
      entries,
      data: entries,
      total,
      page,
      limit,
    })
    return true
  }

  if (url.pathname === '/api/kyber/problems') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const { limit, offset, page } = parsePaginationParams(url, 200)
    const problems = bridge.getProblems(limit, offset)
    const total = bridge.getProblemCount()
    sendKyberJson(res, 200, {
      problems,
      data: problems,
      total,
      page,
      limit,
    })
    return true
  }

  if (url.pathname === '/api/kyber/meta') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    // `apiVersion` is the REST contract's own version, distinct from the store's schema
    // and from the build: the tray checks it to decide whether it understands this server
    // before it renders anything (R6.7).
    sendKyberJson(res, 200, {
      ...bridge.getMeta(),
      version: KYBERDASH_VERSION,
      apiVersion: REPORT_SCHEMA_VERSION,
    })
    return true
  }

  // Ingest coverage (plan docs/plans/2026-09-30-issues-189-198-199 T5,
  // issues #189/#198/#199). Refresh window, receiver activity, quarantine
  // reasons, and checkpoint statuses in one read-only payload — every count
  // comes from a row that exists, and anything unrecorded reads as unknown.
  if (url.pathname === '/api/kyber/coverage') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const refresh = bridge.getRefreshState()
    const ingest = bridge.getIngestActivity()
    // Per-reason counts come from the bridge's GROUP BY aggregate over the
    // handle it already owns (no second store): the coverage request must
    // not materialize the quarantine table to tally it. A null reason
    // groups as 'unknown' inside the seam, never dropped.
    const quarantineByReason = bridge.getQuarantineCountsByReason()
    // An unreadable checkpoint read is unknown (null), never []: null means
    // the checkpoint status is not observable from this page, [] means the
    // read succeeded and zero units exist (genuine zero).
    const checkpoints = bridge.getSourceCheckpointStatuses()
    sendKyberJson(res, 200, { refresh, ingest, quarantineByReason, checkpoints })
    return true
  }

  // The one report every surface reads (Decision D2, R7.1, R7.5).
  if (url.pathname === '/api/kyber/report') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const scope = parseReportScope(url)
    if (typeof scope === 'string') {
      sendKyberJson(res, 400, { error: scope })
      return true
    }
    const sections = parseSections(url)
    if (typeof sections === 'string') {
      sendKyberJson(res, 400, { error: sections })
      return true
    }
    const limit = Number.parseInt(url.searchParams.get('limit') ?? '', 10)
    sendKyberJson(
      res,
      200,
      buildContextReport(bridge, scope, {
        sections,
        findingLimit: Number.isFinite(limit) && limit > 0 ? limit : DEFAULT_FINDING_LIMIT,
        kyberdashVersion: KYBERDASH_VERSION,
      }),
    )
    return true
  }

  // Ranked findings endpoint (Task F3 / Decision D5 / D6)
  if (url.pathname === '/api/kyber/findings') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const runId = (url.searchParams.get('runId') ?? url.searchParams.get('run_id') ?? '').trim() || undefined
    const sessionId = (url.searchParams.get('sessionId') ?? url.searchParams.get('session_id') ?? '').trim() || undefined
    const detector = (url.searchParams.get('detector') ?? '').trim() || undefined
    const harness = (url.searchParams.get('harness') ?? '').trim() || undefined
    const limitParam = url.searchParams.get('limit')
    const offsetParam = url.searchParams.get('offset')
    // Strict positive integers (council review): parseInt would silently
    // truncate `10abc` to 10 and mask a bad URL with HTTP 200.
    const parsePageNumber = (raw: string | null, name: string, min: number): number | undefined | string => {
      if (raw === null || raw.trim() === '') return undefined
      const parsed = Number(raw)
      if (!Number.isInteger(parsed) || parsed < min) {
        return `${name} must be an integer >= ${min} (got ${JSON.stringify(raw)})`
      }
      return parsed
    }
    const limit = parsePageNumber(limitParam, 'limit', 1)
    if (typeof limit === 'string') {
      sendKyberJson(res, 400, { error: limit })
      return true
    }
    const offset = parsePageNumber(offsetParam, 'offset', 0)
    if (typeof offset === 'string') {
      sendKyberJson(res, 400, { error: offset })
      return true
    }

    // Paged envelope (issue #191): `findings` keeps its shape; `total`,
    // `detectorCounts` and `unknownWindowSessions` describe the narrowed set.
    const page = bridge.listFindingsPage({
      runId,
      sessionId,
      detector,
      harness,
      ...(limit === undefined ? {} : { limit }),
      ...(offset === undefined ? {} : { offset }),
    })
    sendKyberJson(res, 200, page)
    return true
  }

  // Individual finding detail endpoint
  if (url.pathname === '/api/kyber/finding' || url.pathname.startsWith('/api/kyber/finding/')) {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    let id = ''
    if (url.pathname.startsWith('/api/kyber/finding/')) {
      id = decodeURIComponent(url.pathname.slice('/api/kyber/finding/'.length)).trim()
    }
    if (!id) {
      id = (url.searchParams.get('id') ?? '').trim()
    }
    if (!id) {
      sendKyberJson(res, 400, { error: 'Missing finding id' })
      return true
    }
    const finding = bridge.getFinding(id)
    if (!finding) {
      sendKyberJson(res, 404, { error: 'Finding not found' })
      return true
    }
    sendKyberJson(res, 200, finding)
    return true
  }

  // Predictions endpoint (Task F4 / Decision D11)
  if (url.pathname === '/api/kyber/predictions') {
    if (req.method === 'GET') {
      const runId = (url.searchParams.get('runId') ?? url.searchParams.get('run_id') ?? '').trim() || undefined
      const findingId = (url.searchParams.get('findingId') ?? url.searchParams.get('finding_id') ?? '').trim() || undefined
      const scoredOnlyParam = url.searchParams.get('scoredOnly') ?? url.searchParams.get('scored_only')
      const scoredOnly = scoredOnlyParam === 'true' || scoredOnlyParam === '1'
      const limitParam = url.searchParams.get('limit')
      const limit = limitParam ? parseInt(limitParam, 10) : undefined

      const predictions = bridge.listPredictions({
        runId,
        findingId,
        scoredOnly: scoredOnly ? true : undefined,
        limit: limit && !isNaN(limit) ? limit : undefined,
      })
      sendKyberJson(res, 200, { predictions })
      return true
    }

    if (req.method === 'POST') {
      let bodyText = ''
      req.on('data', (chunk) => {
        bodyText += chunk
      })
      req.on('end', () => {
        try {
          const parsed = JSON.parse(bodyText || '{}')
          if (Array.isArray(parsed)) {
            const recorded = parsed.map((item) => {
              const rec = recordPrediction(item)
              return bridge.recordPrediction(rec)
            })
            sendKyberJson(res, 201, { predictions: recorded })
          } else {
            const rec = recordPrediction(parsed)
            const saved = bridge.recordPrediction(rec)
            sendKyberJson(res, 201, { prediction: saved })
          }
        } catch {
          sendKyberJson(res, 400, { error: 'Invalid prediction payload' })
        }
      })
      return true
    }

    sendKyberJson(res, 405, { error: 'Method Not Allowed' })
    return true
  }

  // Calibration curve and scoring summary endpoint (Task F4 / Decision D11)
  if (url.pathname === '/api/kyber/calibration') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const runId = (url.searchParams.get('runId') ?? url.searchParams.get('run_id') ?? '').trim() || undefined
    const calibration = bridge.getCalibrationSummary({ runId })
    sendKyberJson(res, 200, { calibration, ...calibration })
    return true
  }

  // Harness rollups endpoint (Task G2 / Decision D1)
  if (url.pathname === '/api/kyber/harnesses') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    // `scorecard` is served rather than left for each client to derive: the report and
    // this endpoint must agree on dimensions (R11.14), which one derivation guarantees.
    // `family` is the T7 display-only label (D3 — rows stay per-origin, nothing is
    // summed); `noDataReason` keeps the rollup's verbatim zero-data reason unless
    // the window proves the harness has only pre-window history (#189); and
    // `checkpointSummary` counts source-checkpoint units by status via the T4 seam
    // (null when that read is impossible — unknown, never zeros).
    const { coveredFrom, latestByHarness } = windowContextOf(bridge)
    // A failed checkpoint read is unknown for every row (null); a
    // successful read with no units for this harness is genuinely zero.
    const allCheckpoints = bridge.getSourceCheckpointStatuses()
    const checkpointsByHarness =
      allCheckpoints === null ? null : groupCheckpointsByHarness(allCheckpoints)
    const liveCounts = liveHarnessCounts(bridge)
    const harnesses = bridge.listHarnessRollups().map((row) => ({
      ...withLiveCounts(row, liveCounts),
      family: harnessFamily(row.harness),
      noDataReason: inWindowNoDataReason(row.harness, latestByHarness, coveredFrom, noDataReasonOf(row)),
      checkpointSummary:
        checkpointsByHarness === null
          ? null
          // The join is canonical on both sides. The map is keyed by
          // `normalizeHarnessName` and a rollup row written before the fold
          // (issue #182) still carries its raw front-end id until an operator
          // rebuilds derived tables, so the lookup key is normalised too. A
          // miss after that is a measured zero, not an unknown: the read
          // succeeded and this harness recorded no units (D3).
          : checkpointSummaryOf(checkpointsByHarness.get(normalizeHarnessName(row.harness)) ?? []),
      scorecard: buildScorecard(row),
    }))
    sendKyberJson(res, 200, { harnesses })
    return true
  }

  // Individual harness detail endpoint
  if (url.pathname === '/api/kyber/harness' || url.pathname.startsWith('/api/kyber/harness/')) {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    let id = ''
    if (url.pathname.startsWith('/api/kyber/harness/')) {
      id = decodeURIComponent(url.pathname.slice('/api/kyber/harness/'.length)).trim()
    }
    if (!id) {
      id = (url.searchParams.get('id') ?? '').trim()
    }
    if (!id) {
      sendKyberJson(res, 400, { error: 'Missing harness id' })
      return true
    }
    const rollup = bridge.getHarnessRollup(id)
    if (!rollup) {
      sendKyberJson(res, 404, { error: 'Harness not found' })
      return true
    }
    // Same coverage facts as the list endpoint, so the two agree (R11.14).
    // The window override applies here too: a pre-window-only harness reads
    // as no-data on the detail route exactly as on the list route.
    const { coveredFrom: detailCoveredFrom, latestByHarness: detailLatest } = windowContextOf(bridge)
    sendKyberJson(res, 200, {
      ...withLiveCounts(rollup, liveHarnessCounts(bridge)),
      family: harnessFamily(rollup.harness),
      noDataReason: inWindowNoDataReason(
        rollup.harness,
        detailLatest,
        detailCoveredFrom,
        noDataReasonOf(rollup),
      ),
      checkpointSummary: checkpointSummaryOf(filterCheckpointsByHarness(bridge.getSourceCheckpointStatuses(), id)),
      scorecard: buildScorecard(rollup),
    })
    return true
  }

  // Runs endpoint (Task G2 / Decision D1 / D2)
  if (url.pathname === '/api/kyber/runs') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const harnessParam = (url.searchParams.get('harness') ?? '').trim() || undefined
    // `run` carries no finding count of its own. Without this join the table
    // renders `findingCount ?? 0` for every row — a fabricated zero sitting
    // directly beneath a findings panel listing the same runs' findings.
    const findingCounts = new Map<string, number>()
    for (const finding of bridge.listFindings()) {
      if (finding.runId === undefined) continue
      findingCounts.set(finding.runId, (findingCounts.get(finding.runId) ?? 0) + 1)
    }
    // Measured run figures ride along so the runs table never renders a dash
    // beside measured data (issue #183). One bounded executions read
    // bucketed in memory plus one summary batch for the whole list, with
    // per-run sums through the shared `sumSessionFigures` derivation.
    const listedRuns = bridge.listRuns(harnessParam)
    // One bounded executions read, bucketed in memory (open thread on
    // routes.ts:567 — the per-run loop reintroduced N+1 round trips on the
    // landing-page endpoint). Only listed runs' sessions reach the summary
    // batch, so no other harness's figures are ever read.
    const listedRunIds = new Set(listedRuns.map((run) => run.runId))
    const executionsByRun = new Map<string, string[]>()
    for (const execution of bridge.listExecutions()) {
      if (!listedRunIds.has(execution.runId)) continue
      if (typeof execution.sessionId !== 'string' || execution.sessionId.length === 0) continue
      const group = executionsByRun.get(execution.runId) ?? []
      group.push(execution.sessionId)
      executionsByRun.set(execution.runId, group)
    }
    const listedSessionIds = [...new Set([...executionsByRun.values()].flat())]
    const listedSummaries = bridge.sessionSummaryFigures(listedSessionIds)
    const runs = listedRuns.map((run) => ({
      ...run,
      ...sumSessionFigures(listedSummaries, executionsByRun.get(run.runId) ?? []),
      findingCount: findingCounts.get(run.runId) ?? 0,
    }))
    sendKyberJson(res, 200, { runs })
    return true
  }

  // Individual run detail endpoint (with execution tree, executions, and findings)
  if (url.pathname === '/api/kyber/run' || url.pathname.startsWith('/api/kyber/run/')) {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    let id = ''
    if (url.pathname.startsWith('/api/kyber/run/')) {
      id = decodeURIComponent(url.pathname.slice('/api/kyber/run/'.length)).trim()
    }
    if (!id) {
      id = (url.searchParams.get('id') ?? '').trim()
    }
    if (!id) {
      sendKyberJson(res, 400, { error: 'Missing run id' })
      return true
    }
    const run = bridge.getRun(id)
    if (!run) {
      sendKyberJson(res, 404, { error: 'Run not found' })
      return true
    }
    const executionTree = bridge.getExecutionTree(id)
    const executions = bridge.listExecutions(id)
    const findings = bridge.listFindings({ runId: id })
    // Issue #183: the detail payload serves what its views need — measured
    // per-turn rows, enriched run figures, and a run-scoped scorecard — so
    // the turn table and scorecard render figures instead of dashes. Summaries
    // batch once (review follow-up: Kilo K7); session payloads stream one at
    // a time inside the turns and scorecard builders (re-review: Kilo 4), so
    // the route never holds the run's payloads at once.
    const detailSessionIds = executions
      .map((exec) => exec.sessionId)
      .filter((sessionId): sessionId is string => typeof sessionId === 'string')
    const summaries = bridge.sessionSummaryFigures(detailSessionIds)
    const figures = sumSessionFigures(summaries, detailSessionIds)
    const enrichedExecutions = executions.map((exec) => {
      const sessionFigures = exec.sessionId !== null && exec.sessionId !== undefined
        ? summaries.get(exec.sessionId)
        : undefined
      return {
        ...exec,
        ...(sessionFigures?.turnCount !== undefined ? { turnCount: sessionFigures.turnCount } : {}),
        ...(sessionFigures?.costUsd !== undefined ? { costUsd: sessionFigures.costUsd } : {}),
      }
    })
    sendKyberJson(res, 200, {
      run: { ...run, ...figures },
      executionTree,
      executions: enrichedExecutions,
      findings,
      turns: bridge.getRunTurns(id, executions),
      scorecard: bridge.getRunScorecard(id, { executions, summaries }) ?? null,
    })
    return true
  }

  // Backward-compatible endpoints for older/legacy callers
  if (url.pathname === '/api/kyber/context') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    let id = (url.searchParams.get('id') ?? '').trim()
    if (!id) {
      const list = bridge.listSessions(1)
      if (list.length === 0) {
        sendKyberJson(res, 404, { error: 'No sessions available' })
        return true
      }
      id = list[0].session_id
    }
    const payload = bridge.getSessionPayload<SessionViewPayload>(id)
    if (!payload) {
      sendKyberJson(res, 404, { error: 'Session not found' })
      return true
    }
    sendKyberJson(res, 200, payload.context ?? {})
    return true
  }

  if (url.pathname === '/api/kyber/schema') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    let id = (url.searchParams.get('id') ?? '').trim()
    if (!id) {
      const list = bridge.listSessions(1)
      if (list.length === 0) {
        sendKyberJson(res, 404, { error: 'No sessions available' })
        return true
      }
      id = list[0].session_id
    }
    const payload = bridge.getSessionPayload<SessionViewPayload>(id)
    if (!payload) {
      sendKyberJson(res, 404, { error: 'Session not found' })
      return true
    }
    sendKyberJson(res, 200, payload.schema ?? null)
    return true
  }

  if (url.pathname === '/api/kyber/timeline') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    let id = (url.searchParams.get('id') ?? '').trim()
    if (!id) {
      const list = bridge.listSessions(1)
      if (list.length === 0) {
        sendKyberJson(res, 404, { error: 'No sessions available' })
        return true
      }
      id = list[0].session_id
    }
    const payload = bridge.getSessionPayload<SessionViewPayload>(id)
    if (!payload) {
      sendKyberJson(res, 404, { error: 'Session not found' })
      return true
    }
    sendKyberJson(res, 200, payload.timeline ?? [])
    return true
  }

  // LLM context review endpoint (Task G5 / Decision D10)
  if (url.pathname === '/api/kyber/review') {
    if (req.method !== 'POST') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    let bodyText = ''
    req.on('data', (chunk) => {
      bodyText += chunk
    })
    req.on('end', async () => {
      try {
        const parsed = JSON.parse(bodyText || '{}')
        const request: ReviewRequest = {
          content: parsed.content || '',
          blocks: parsed.blocks,
          sessionId: parsed.sessionId,
          turnIndex: parsed.turnIndex,
          harness: parsed.harness,
          model: parsed.model,
          focus: parsed.focus,
        }
        const result = await runContextReview(request, parsed.options || {})
        sendKyberJson(res, 200, result)
      } catch {
        sendKyberJson(res, 400, { error: 'Invalid review request payload' })
      }
    })
    return true
  }

  // Review provider configuration status
  if (url.pathname === '/api/kyber/review/status') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const provider = createReviewProvider()
    sendKyberJson(res, 200, {
      provider: provider.name,
      isConfigured: provider.isConfigured,
    })
    return true
  }

  // Model-window catalog (D15). Refresh is matched first so a snapshot
  // prefix cannot swallow it. Neither route reads a caller-supplied URL.
  if (url.pathname === '/api/kyber/model-catalog/refresh') {
    if (req.method !== 'POST') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    void bridge.refreshModelCatalog().then(
      (body) => sendKyberJson(res, 200, body),
      () => sendKyberJson(res, 500, { error: 'Model catalog refresh failed' }),
    )
    return true
  }

  if (url.pathname === '/api/kyber/model-catalog') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    sendKyberJson(res, 200, bridge.getModelCatalog())
    return true
  }

  // Shared settings (rule R1): the tray and the web UI read and write the same four
  // switches, so both go through this one route. Storage and the on/off encoding stay in
  // shared-settings.ts; this is only the JSON view over it.
  if (url.pathname === '/api/kyber/settings') {
    // No store means this process does not host the shared settings (see KyberRouteDeps):
    // the route is simply not mounted here, and the JSON 404 catch-all owns the answer.
    const store = deps?.store
    if (store === undefined) {
      sendKyberJson(res, 404, { error: ERR_NOT_FOUND })
      return true
    }
    if (req.method !== 'GET' && req.method !== 'PUT') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    if (req.method === 'GET') {
      sendKyberJson(res, 200, readSettings(store))
      return true
    }
    if (!requireJsonContentType(req)) {
      sendKyberJson(res, 415, { error: 'Content-Type must be application/json' })
      return true
    }
    serveJsonObjectBody(req, res, (body) => {
      // Validate the whole patch first: a body with one bad key writes none of them.
      const patch = parseSettingsPatch(body)
      if (patch === undefined) {
        sendKyberJson(res, 400, { error: 'Invalid settings payload' })
        return
      }
      sendKyberJson(res, 200, applyPartialSettings(store, body, patch))
    })
    return true
  }

  // Job status for the tray and the web UI. The host status is flat; the tray UI expects
  // the refresh facts nested, so the mapping happens here once and both surfaces see the
  // same shape.
  if (url.pathname === '/api/kyber/jobs') {
    // No JobHost yet means the host pair is still coming up (see KyberRouteDeps), which
    // is "not yet", not "not here": a 404 would report a server that is right there as
    // one that does not exist.
    const jobHost = deps?.jobHost
    if (jobHost === undefined) {
      sendKyberJson(res, deps === undefined ? 404 : 503, {
        error: deps === undefined ? ERR_NOT_FOUND : ERR_SERVICES_STARTING,
      })
      return true
    }
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const status = jobHost.getStatus()
    sendKyberJson(res, 200, {
      refresh: {
        state: status.state,
        lastSuccessAt: status.lastSuccessAt,
        lastFailure: status.lastFailure,
        nextDueAt: status.nextDueAt,
      },
      paused: status.paused,
      storeGeneration: storeGeneration(deps!.store),
      hostedElsewhere: status.hostedElsewhere,
    })
    return true
  }

  // Manual refresh. `scheduled` and `cli` are deliberately not accepted here: the host
  // owns the schedule, and a client-triggered run must say which surface asked for it.
  if (url.pathname === '/api/kyber/refresh') {
    // Still coming up rather than not here; see the jobs route and KyberRouteDeps.
    const jobHost = deps?.jobHost
    if (jobHost === undefined) {
      sendKyberJson(res, deps === undefined ? 404 : 503, {
        error: deps === undefined ? ERR_NOT_FOUND : ERR_SERVICES_STARTING,
      })
      return true
    }
    if (req.method !== 'POST') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    if (!requireJsonContentType(req)) {
      sendKyberJson(res, 415, { error: 'Content-Type must be application/json' })
      return true
    }
    serveJsonObjectBody(req, res, (body) => {
      const surface = parseRefreshBody(body)
      if (surface === undefined) {
        sendKyberJson(res, 400, { error: 'Invalid refresh request payload' })
        return
      }
      const status = jobHost.getStatus()
      // A job already running here means nothing was started, so the host is not called.
      if (status.state === 'running') {
        sendKyberJson(res, 409, { error: ERR_JOB_RUNNING })
        return
      }
      // Another process holds the jobs lease, so this host will decline the run only
      // after its own lease attempt completes — which can outlast the grace window and
      // turn "nothing started anywhere" into a 202 saying the opposite. The status
      // already knows, so ask it first and start nothing.
      if (status.hostedElsewhere) {
        sendKyberJson(res, 409, { error: ERR_JOB_RUNNING })
        return
      }
      sendJobStarted(res, jobHost.runNow(surface), 'Refresh failed to start')
    })
    return true
  }

  // One-off folder-history import, requested explicitly by a surface. It never touches
  // the scheduled folder-import setting: "import this once" and "keep importing" are
  // different decisions.
  if (url.pathname === '/api/kyber/import-history') {
    // Unmounted without the collaborators: see KyberRouteDeps.
    if (deps === undefined) {
      sendKyberJson(res, 404, { error: ERR_NOT_FOUND })
      return true
    }
    const jobHost = deps?.jobHost
    if (jobHost === undefined) {
      sendKyberJson(res, deps === undefined ? 404 : 503, {
        error: deps === undefined ? ERR_NOT_FOUND : ERR_SERVICES_STARTING,
      })
      return true
    }
    if (req.method !== 'POST') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    if (!requireJsonContentType(req)) {
      sendKyberJson(res, 415, { error: 'Content-Type must be application/json' })
      return true
    }
    serveJsonObjectBody(req, res, (body) => {
      const request = parseImportHistoryBody(body)
      if (request === undefined) {
        sendKyberJson(res, 400, { error: 'Invalid import-history request payload' })
        return
      }
      const status = jobHost.getStatus()
      if (status.state === 'running' || status.hostedElsewhere) {
        sendKyberJson(res, 409, { error: ERR_JOB_RUNNING })
        return
      }
      const args = ['--weeks', String(request.weeks)]
      for (const harness of request.harnesses) args.push('--harness', harness)
      sendJobStarted(res, jobHost.runJob('import', args), 'Import failed to start')
    })
    return true
  }

  // Database clean (issue #312). The web dash reaches the central
  // `cleanDatabase` module over this route; the request body carries the
  // scope, the confirmation, and the optional re-ingest window. Matched
  // before the JSON-404 catch-all like every other /api/kyber/* route.
  if (url.pathname === '/api/kyber/clean') {
    if (req.method !== 'POST') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    let bodyText = ''
    let received = 0
    let settled = false
    // Only the malformed-answer path uses this; the oversize answer is sent inline below
    // because it also has to close the connection (destroyAfterResponse).
    const rejectCleanBody = (status: number, error: string): void => {
      if (settled) return
      settled = true
      sendKyberJson(res, status, { error })
    }
    req.on('data', (chunk) => {
      if (settled) return
      received += typeof chunk === 'string' ? Buffer.byteLength(chunk) : chunk.length
      if (received > MAX_CLEAN_BODY_BYTES) {
        settled = true
        sendKyberJson(
          res,
          413,
          { error: `Clean request body exceeds the ${MAX_CLEAN_BODY_BYTES}-byte limit` },
          { connection: 'close' },
        )
        destroyAfterResponse(req, res)
        return
      }
      bodyText += chunk
    })
    req.on('error', () => {
      rejectCleanBody(400, 'Invalid clean request payload')
    })
    req.on('end', () => {
      if (settled) return
      settled = true
      // The parse and the clean both belong inside the guard: this listener is an
      // `async` callback on the request stream, outside `createServer`'s try/catch, so a
      // throw here would reject a promise nobody awaits and take the web server with it.
      // The answers are unchanged - 400 for a body the parser rejects, 409 when a clean
      // is already running, 500 'Clean failed' for anything else.
      runGuardedBodyHandler(
        res,
        () => {
          const parsed = cleanBodyParser.parse(bodyText)
          if (parsed === undefined) {
            sendKyberJson(res, 400, { error: 'Invalid clean request payload' })
            return
          }
          return bridge.cleanDatabase(parsed).then((report) => {
            sendKyberJson(res, 200, report)
          })
        },
        (err) => {
          if (err instanceof Error && (err as { code?: string }).code === 'CLEAN_BUSY') {
            sendKyberJson(res, 409, { error: 'A refresh or clean is already running' })
            return
          }
          sendKyberJson(res, 500, { error: 'Clean failed' })
        },
      )
    })
    return true
  }

  // Precedence guard: any unhandled /api/kyber/* route MUST return JSON 404, never SPA HTML
  if (url.pathname.startsWith('/api/kyber/') || url.pathname === '/api/kyber') {
    sendKyberJson(res, 404, { error: 'Not found' })
    return true
  }

  return false
}
