import type { IncomingMessage, ServerResponse } from 'http'
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
import type { SourceCheckpoint } from '../canon/source-state.js'

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

function sendKyberJson(res: ServerResponse, status: number, body: unknown): void {
  let serialized: string
  try {
    serialized = JSON.stringify(body)
  } catch (err) {
    // Serialization failure (e.g. circular reference) must not leak internal
    // detail to the client. Log it server-side and return a generic error.
    console.error('[KyberRoutes] Failed to serialize response:', err instanceof Error ? err.message : String(err))
    res.writeHead(500, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' })
    res.end(JSON.stringify({ error: 'Internal server error' }))
    return
  }
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
  })
  res.end(serialized)
}

type SessionViewPayload = {
  context?: unknown
  schema?: unknown
  timeline?: unknown
}

export function handleKyberRequest(
  req: IncomingMessage,
  res: ServerResponse,
  url: URL,
  bridge: KyberBridge,
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
    // #190): an arbitrary query count is not store-backed measurement.
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

  // Precedence guard: any unhandled /api/kyber/* route MUST return JSON 404, never SPA HTML
  if (url.pathname.startsWith('/api/kyber/') || url.pathname === '/api/kyber') {
    sendKyberJson(res, 404, { error: 'Not found' })
    return true
  }

  return false
}
