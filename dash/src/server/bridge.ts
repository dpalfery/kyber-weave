// SQLite query bridge for the canonical KyberDash store.

import { existsSync, readFileSync, statSync } from 'node:fs'
import type { BigIntStats } from 'node:fs'
import { inflateSync } from 'node:zlib'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { createRequire } from 'node:module'
import { APPROXIMATE_TOKENIZER, tokenizerName } from '../canon/tokens.js'
import {
  SessionIdentities,
  canonicalHarnessId,
  normalizeHarnessName,
} from '../canon/measurability.js'
import { dedupeTwinTurns } from '../canon/twin-dedupe.js'
import { refreshProcessIsAlive } from '../canon/refresh-run.js'
import {
  CanonStore,
  decompressRaw,
  normalizeHistoryWeeks,
  toFinding,
  toPrediction,
  toRecord,
  toRunRow,
  toHarnessRollupRow,
  toExecutionRow,
  type FindingDbRow,
  type PredictionDbRow,
  type RunDbRow,
  type HarnessRollupDbRow,
  type ExecutionDbRow,
} from '../canon/store.js'
import { sourceDisplayName, type SourceKind } from '../canon/measurability.js'
import {
  toSourceCheckpoint,
  type SourceCheckpoint,
  type SourceCheckpointRow,
} from '../canon/source-state.js'
import {
  calculateCalibrationCurve,
  type CalibrationCurveResult,
  type PredictionRecord,
} from '../analysis/calibration.js'
import {
  compareHarnesses,
  compareRuns as compareStoredRuns,
  type ComparisonSummary,
  type RunComparisonOptions,
} from '../analysis/compare.js'
import type { Finding } from '../analysis/findings.js'
import { DETECTOR_IDS } from '../analysis/findings.js'
import type { CleanReport, CleanRequest } from '../clean/clean.js'
import { COPILOT_CREDITS_SOURCE } from '../canon/copilot-rates.js'

import {
  CANONICAL_CONTENT_KEYS,
  type CanonicalContent,
  type CanonicalContentKey,
  type CanonicalRecord,
  type ContentPart,
  type CostBlock,
  type RunRow,
  type RunTurnRow,
  type ExecutionRow,
  type ExecutionTreeNode,
  type HarnessRollupRow,
} from '../canon/types.js'
import {
  assembleRollup,
  digestSessionPayloads,
} from '../canon/harnesses.js'
import { harnessExportsCacheCounter } from '../canon/measurability.js'
import { buildScorecard, type Scorecard } from '../analysis/scorecard.js'
import type { AsadSessionPayload } from '../canon/sessions.js'
import { projectCanonicalStore } from '../canon/projection.js'
import {
  MODEL_WINDOW_CATALOG_SOURCES,
  getModelCatalogSnapshot,
  readBundledVendorCatalog,
  refreshModelWindowCatalog,
  type ModelCatalogSnapshot,
  type ModelWindowCatalogRefreshResult,
  type ModelWindowCatalogVendor,
} from '../canon/model-window-catalog.js'

const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}
type DatabaseSync = import('node:sqlite').DatabaseSync

/** Paged findings envelope served at `GET /api/kyber/findings` (issue #191). */
export type FindingsPage = {
  findings: Finding[]
  /** Size of the narrowed set ignoring paging. */
  total: number
  limit?: number
  offset: number
  /** Per-detector counts over the run/session/harness scope, ignoring paging and the detector filter. */
  detectorCounts: Record<string, number>
  /** Sessions with an unreported context window in the harness scope. */
  unknownWindowSessions: number
}

/** A positive finite page number, floored; anything else is absent (paging lives in the bridge). */
function validPageNumber(value: number | undefined): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 ? Math.floor(value) : undefined
}

export type MetricAvailability = 'measured' | 'derived' | 'not_measurable'
export type MetricKind = 'per_turn' | 'total'
export type MetricUnit = 'tokens' | 'share' | 'currency' | 'count'

export type MetricCell = {
  measurable: boolean
  availability: MetricAvailability
  value?: number
  basis?: string
  currency?: string
  render: string
}

export type MetricRow = {
  metric: string
  kind: MetricKind
  label: string
  unit: MetricUnit
  cells: Record<string, MetricCell>
}

export type ComparisonTableResult = {
  harnesses: string[]
  rows: MetricRow[]
  problems: Array<{ severity: string; code: string; message: string }>
}

export type SessionSummary = {
  session_id: string
  harness: string
  label: string | null
  is_subagent: boolean
  parent_session: string | null
  agent_name: string | null
  repo: string | null
  branch: string | null
  started: string | null
  ended: string | null
  turn_count: number | null
  request_count: number | null
  total_input: number | null
  total_output: number | null
  /** USD figure; non-null only for a priced USD block. */
  cost_usd: number | null
  /** The canonical cost block with its basis and status. */
  cost: { basis: string; status: string; value?: number; currency?: string }
  models: string[]
  problems: number
}

/** Cost facts needed by the shared report, read without loading raw span payloads. */
export type SessionCostContribution = {
  sessionId: string
  basis: string
  status: string
  value?: number
}

/** Refresh health from the canonical refresh log. */
export type RefreshState = {
  lastSuccessAt: string | null
  lastFailure: { at: string; summary: string } | null
  inProgress: { pid: number; since: string } | null
  /**
   * The ingest window, in weeks, of the last successful run (T1's
   * `historyWeeks`, plan docs/plans/2026-09-30-issues-189-198-199 T4).
   * Optional so readers that predate window tracking keep compiling;
   * `null` means the run predates tracking or no success exists — window
   * unknown, never 0 and never the current default.
   */
  historyWeeks?: number | null
  /** Derived window end of the last success (its `startedAt`); null when unknown. */
  coveredThrough?: string | null
  /** Derived window start (`coveredThrough` minus `historyWeeks`); null when unknown. */
  coveredFrom?: string | null
}

/**
 * One stored source's ingest activity (T4 read seam).
 *
 * <remarks>
 * `recordCount` comes from `records GROUP BY source` — true for history,
 * including legacy `unattributed` and `codeburn/*` rows. `ingestedCount`
 * and `lastReceivedAt` come from `ingest_log` sums / `MAX(timestamp)` (T6's
 * shape). `display`/`kind` are T7's `sourceDisplayName` labeling only: no
 * aggregation across origins ever happens here. A missing log side reads
 * as 0 ingested / null received — a fact about the log, never a claim
 * about the receiver process.
 * </remarks>
 */
export type IngestActivitySource = {
  /** The stored source name, verbatim (auditability). */
  source: string
  /** T7 display label for the stored name. */
  display: string
  /** T7 origin kind for the stored name. */
  kind: SourceKind
  /** Rows in `records` carrying this source. */
  recordCount: number
  /** Summed `ingest_log.count` for this source; 0 when the log names it nowhere. */
  ingestedCount: number
  /** Latest `ingest_log.timestamp` for this source; null when the log names it nowhere. */
  lastReceivedAt: string | null
}

/**
 * Receiver activity over the canonical store (honest-unobservability rule).
 * `unknown` only when the log AND the table are both empty — with the
 * reason, never 0 and never `running`.
 */
export type IngestActivity =
  | { status: 'known'; sources: IngestActivitySource[]; lastReceivedAt: string | null }
  | { status: 'unknown'; reason: string; sources: []; lastReceivedAt: null }

export type QuarantineRow = {
  span_id: string
  source: string | null
  name: string | null
  namespaces: string | null
  reason: string | null
  seen_at: number | string | null
  timestamp?: string | null
}

export type ProblemRow = {
  id: number | string
  session_id: string | null
  span_id: string | null
  severity: string
  code: string
  message: string
  at: number | string | null
  harness: string | null
  timestamp?: string | null
}

export type ParsedSummary = {
  turn_count?: number | null
  request_count?: number | null
  total_input?: number | null
  total_output?: number | null
  total_cache_read?: number | null
  total_cache_creation?: number | null
  schema_tokens_per_turn?: number | null
  cost?: CostBlock | null
  models?: string[] | null
}

export type SessionPayload = Record<string, unknown> & {
  id?: string
  harness?: string
  summary?: ParsedSummary
  turns?: unknown[]
  problems?: unknown[]
}

/** Measured session-summary figures behind one session, keyed by session id. */
export type SessionSummaryFigures = Map<
  string,
  {
    turnCount?: number
    totalInput?: number
    totalOutput?: number
    costUsd?: number
    /** The session's own cost block is partial — its figure is real but incomplete. */
    costPartial?: true
  }
>

/**
 * Adapt streamed session payloads for the digest without retaining them:
 * each payload is pulled, digested, and released before the next loads.
 */
function* asadPayloads(
  source: Generator<{ payload: SessionPayload & { context?: unknown } }>,
): Generator<AsadSessionPayload> {
  for (const { payload } of source) {
    yield payload as unknown as AsadSessionPayload
  }
}

/**
 * Collapse executions sharing one session to a single digest input
 * (re-review #2: Kilo 5 — the old payload Map deduped by session id, and
 * the stream must not count a shared session twice in digest.count or the
 * peak-pressure list). Turns still stream per execution; only the digest
 * dedupes. Executions without a session id have no key and always pass.
 */
export function* dedupedRunSessions(
  source: Generator<{ execution: ExecutionRow; payload: SessionPayload & { context?: unknown } }>,
): Generator<{ execution: ExecutionRow; payload: SessionPayload & { context?: unknown } }> {
  const seen = new Set<string>()
  for (const item of source) {
    const sessionId = item.execution.sessionId
    if (typeof sessionId === 'string' && sessionId.length > 0) {
      if (seen.has(sessionId)) continue
      seen.add(sessionId)
    }
    yield item
  }
}

/** A run-figure field that names the cells it marks as subtotals. */
export type PartialRunField = 'turnCount' | 'totalInput' | 'totalOutput' | 'costUsd'

/**
 * Measured run figures. `costStatus` marks a cost subtotal; `partial` marks
 * any subtotal — a linked session with no summary, or a figure missing from
 * some of the run's summaries — and `partialFields` names exactly which
 * cells are subtotals so views mark the right ones (re-review #2: Kilo 4).
 * All three are present only when partial.
 */
export type RunMeasuredFigures = {
  turnCount?: number
  totalInput?: number
  totalOutput?: number
  costUsd?: number
  costStatus?: 'partial'
  partial?: true
  partialFields?: PartialRunField[]
}

/**
 * A priced cost block's USD figure, or undefined when it is not one.
 * Anything unpriced stays absent; a priced non-USD block stays absent too —
 * serving euros under a dollar formatter is mislabelling (review follow-up:
 * Copilot C4). The legacy `usd` shape predates currency and names dollars.
 * A `partial` block still carries a priced figure for its priced portion
 * (re-review: Kilo 5) — the run-level partial marker, not this function,
 * says the total is incomplete. In direct-DB mode a missing currency reads
 * as null rather than undefined (re-review: Kilo 7); both mean "unnamed".
 */
export function pricedUsd(
  status: unknown,
  value: unknown,
  currency: unknown,
  legacyUsd: unknown,
): number | undefined {
  if (status !== 'priced' && status !== 'ok' && status !== 'partial') return undefined
  const figure = (candidate: unknown): number | undefined =>
    typeof candidate === 'number' && Number.isFinite(candidate) ? candidate : undefined
  const priced = figure(value) ?? figure(legacyUsd)
  if (priced === undefined) return undefined
  if (figure(value) !== undefined && currency != null && currency !== 'USD') return undefined
  return priced
}

/**
 * Sum session figures into run figures — the one derivation the runs list
 * and the run detail share (review follow-up on issue #183: Kilo K7).
 * Coverage is tracked, not assumed (Kilo K3, Copilot C5): a session without
 * a priced figure makes the sum partial, and a run with no priced figure at
 * all carries no cost — never $0. The same holds beyond cost (re-review:
 * Kilo 5): a linked session with no summary row, or a figure present in
 * some summaries but missing in others, marks the whole run `partial`.
 */
export function sumSessionFigures(
  summaries: ReadonlyMap<
    string,
    { turnCount?: number; totalInput?: number; totalOutput?: number; costUsd?: number; costPartial?: true }
  >,
  sessionIds: readonly (string | null | undefined)[],
): RunMeasuredFigures {
  const uniqueIds = [...new Set(sessionIds)].filter(
    (id): id is string => typeof id === 'string' && id.length > 0,
  )
  let turnCount = 0
  let totalInput = 0
  let totalOutput = 0
  let costUsd = 0
  let seenTurns = false
  let seenInput = false
  let seenOutput = false
  let pricedSessions = 0
  let partialSessions = 0
  const fieldSeen = { turnCount: 0, totalInput: 0, totalOutput: 0, costUsd: 0 }
  for (const sessionId of uniqueIds) {
    const figures = summaries.get(sessionId)
    if (figures === undefined) continue
    if (figures.turnCount !== undefined) {
      turnCount += figures.turnCount
      seenTurns = true
      fieldSeen.turnCount += 1
    }
    if (figures.totalInput !== undefined) {
      totalInput += figures.totalInput
      seenInput = true
      fieldSeen.totalInput += 1
    }
    if (figures.totalOutput !== undefined) {
      totalOutput += figures.totalOutput
      seenOutput = true
      fieldSeen.totalOutput += 1
    }
    if (figures.costUsd !== undefined) {
      costUsd += figures.costUsd
      pricedSessions += 1
      fieldSeen.costUsd += 1
    }
    if (figures.costPartial === true) partialSessions += 1
  }
  const namedPartials = (Object.keys(fieldSeen) as Array<PartialRunField>).filter(
    (field) => fieldSeen[field] > 0 && fieldSeen[field] < uniqueIds.length,
  )
  return {
    ...(seenTurns ? { turnCount } : {}),
    ...(seenInput ? { totalInput } : {}),
    ...(seenOutput ? { totalOutput } : {}),
    ...(pricedSessions > 0 ? { costUsd } : {}),
    // A priced figure beside an unpriced session is a subtotal wearing a
    // total's suit — mark it partial so the views can say so. Any partial
    // session marks the sum too, even when every session priced something
    // (re-review #2: Kilo B — otherwise an all-partial run looks complete).
    ...(pricedSessions > 0 && (pricedSessions < uniqueIds.length || partialSessions > 0)
      ? { costStatus: 'partial' as const }
      : {}),
    ...(namedPartials.length > 0 ? { partial: true as const, partialFields: namedPartials } : {}),
  }
}

export type KyberMetaResult = {
  span_count: number
  quarantined: number
  tokenizer: {
    kind: string
    note: string
  }
  rates: {
    credit_usd: number | null
    source: string | null
    retrieved: string | null
    note: string | null
    /** The two distinct rate tables in play; the flat fields above describe `copilot_credits` only. */
    tables: Array<{
      id: string
      source: string
      retrieved: string
      applies_to: string[]
      credit_usd?: number
      note?: string
    }>
  }
  harnesses: Record<string, unknown>
  sources: Array<{ origin: string; seen: number; new: number }>
}

export type KyberBridgeOptions = {
  canonPath?: string
  ratesPath?: string
  canonDb?: DatabaseSync
  /**
   * When present, content drill-down reads through `recordsForSession` / `get`
   * instead of issuing its own SQL. Tests inject an in-memory store this way;
   * production falls back to the already-open `canonDb` handle.
   */
  store?: CanonStore
  /**
   * How often the private store accessor is allowed to `stat` `canonPath` and
   * reopen or drop the owned handle, in milliseconds. `0` means every access.
   * Ignored for an injected `canonDb` or a `:memory:` bridge — neither is ever
   * probed. Default 1000ms.
   */
  reopenCheckIntervalMs?: number
  /** Clock used for the reopen throttle. Default `Date.now`. */
  now?: () => number
}

/** File identity used to detect a replaced `canonPath` (device + inode). */
type FileIdentity = { dev: bigint; ino: bigint }

/** Default interval between reopen probes when `reopenCheckIntervalMs` is not given. */
const DEFAULT_REOPEN_CHECK_INTERVAL_MS = 1000

/**
 * Minimum gap between two "failed to stat" warnings for the same bridge
 * instance while the failure keeps recurring. A persistent, non-ENOENT stat
 * failure (EACCES, EMFILE, ELOOP, ...) must keep giving an operator a symptom
 * rather than warning once and then serving stale data silently forever —
 * see the durability note on {@link KyberBridge.reconcile}. Measured by the
 * injected `now`, so it is deterministic under test.
 */
const STAT_FAILURE_WARN_INTERVAL_MS = 60_000

/** Max distinct key+harness share-drop warnings remembered before the oldest entry is evicted. */
export const SHARE_DROP_WARN_LIMIT = 1024

/**
 * Unclipped inspector payload. `_clip` stays on the session list and the
 * session payload — those are summaries. This shape is what a band click
 * reads so a 11,000-character system prompt is the real text, not a 2,000
 * character stub.
 *
 * `{ sessionId, spanId?, parts: [{ spanId, part, text, tokens?, server?, truncated?, totalLength? }] }`
 *
 * `spanId` is present only when the caller asked for one span. `tokens` and
 * `server` are omitted when the store did not have them — absent is not zero.
 * `truncated` / `totalLength` appear only when this response hit the
 * per-response budget; silent truncation is the defect this route exists to
 * fix.
 */
export type SessionContentPart = {
  spanId: string
  part: CanonicalContentKey
  text: string
  tokens?: number
  server?: string
  truncated?: boolean
  totalLength?: number
}

export type SessionContentResult = {
  sessionId: string
  spanId?: string
  parts: SessionContentPart[]
}

export type SessionContentOptions = {
  spanId?: string
  part?: string
}

export type TurnContentPart = {
  id?: string
  spanId?: string
  part: string
  label: string
  text: string
  tokens?: number
  server?: string
  truncated?: boolean
  totalLength?: number
  order?: number
}

export type TurnContentBlock = {
  key: string
  label: string
  tokens?: number
  parts: TurnContentPart[]
  text: string
  truncated?: boolean
  totalLength?: number
  notMeasurable?: { reason: string }
}

export type TurnContentResult = {
  sessionId: string
  turnIndex: number
  spanId?: string
  model?: string
  blocks: TurnContentBlock[]
  parts: TurnContentPart[]
  assembledText: string
  truncated?: boolean
  totalLength?: number
}

/**
 * Generous ceiling for one unclipped content response (a few megabytes of
 * characters). A single system prompt fits; a 7,000-turn dump does not get
 * serialized whole. When a part does not fit, it is cut and flagged — never
 * silently shortened.
 */
export const CONTENT_RESPONSE_BUDGET = 2_000_000

interface SessionDbRow {
  session_id: string
  harness: string
  label?: string | null
  is_subagent?: number | boolean | null
  parent_session?: string | null
  agent_name?: string | null
  repo?: string | null
  branch?: string | null
  started?: string | null
  ended?: string | null
  summary_json?: string | object | null
  problems_count?: number | null
  payload?: string | null
}

interface QuarantineDbRow {
  span_id: string
  source?: string | null
  name?: string | null
  namespaces?: string | null
  reason?: string | null
  seen_at?: number | string | null
  timestamp?: string | null
}

interface ProblemDbRow {
  id: number | string
  session_id?: string | null
  span_id?: string | null
  severity: string
  code: string
  message: string
  at?: number | string | null
  timestamp?: string | null
  harness?: string | null
  location?: string | null
}

interface IngestLogRow {
  origin: string
  seen?: number | null
  new?: number | null
}

/** Maximum length for string values before leaf truncation in payloads. */
export const MAX_STRING_LENGTH = 2000

/**
 * Bounds payload size by clipping long leaf strings rather than whole structures.
 * Recursing keeps JSON shape intact so truncated tools and parts still render cleanly.
 */
export function _clip<T = unknown>(value: T, maxLen = MAX_STRING_LENGTH, depth = 0): T {
  if (typeof value === 'string') {
    return (
      value.length > maxLen
        ? value.slice(0, maxLen) + `... [truncated, ${value.length} chars]`
        : value
    ) as unknown as T
  }
  if (depth >= 8) {
    return value
  }
  if (Array.isArray(value)) {
    return value.map((item) => _clip(item, maxLen, depth + 1)) as unknown as T
  }
  if (value !== null && typeof value === 'object') {
    const res: Record<string, unknown> = {}
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      res[k] = _clip(v, maxLen, depth + 1)
    }
    return res as unknown as T
  }
  return value
}

type ContentSourceRecord = {
  spanId: string
  sessionKey: string | null
  parts: ContentPart[]
}

/**
 * The fields `assembleTurnContent` reads off a turn, whether it arrives as a
 * session-payload entry (`SessionPayload.turns` is `unknown[]`) or as a
 * canonical record carrying an ingest-specific index or model. Each one is
 * still checked at runtime before use; this only names the shape being probed.
 */
type TurnDescriptor = {
  index?: number
  /**
   * Legacy 1-based turn number some payload rows carry instead of `index`.
   * Unknown at the boundary; `turnTransportIndexOf` checks before use.
   */
  turn?: unknown
  spanId?: string
  model?: string
}

/**
 * One row's 0-based transport identity (issue #184): an explicit finite
 * `index` wins; otherwise a finite legacy 1-based `turn` resolves as
 * `turn - 1`. Anything else is no identity — the row is only reachable
 * positionally.
 */
function turnTransportIndexOf(item: TurnDescriptor): number | undefined {
  if (typeof item.index === 'number' && Number.isFinite(item.index)) return item.index
  if (typeof item.turn === 'number' && Number.isFinite(item.turn)) return item.turn - 1
  return undefined
}

/**
 * Strict turn resolution: explicit identity first, array position only for
 * rows carrying neither `index` nor `turn`. Anything else resolves to
 * nothing — never a neighboring turn.
 */
function resolveTurn<T>(
  pool: readonly T[],
  turnIndex: number,
  identityOf: (item: T) => number | undefined,
): T | undefined {
  return (
    pool.find((item) => identityOf(item) === turnIndex) ??
    pool.find((item, i) => i === turnIndex && identityOf(item) === undefined)
  )
}

/**
 * Per-metric measurability as a payload carries it, under either the flat
 * `measurability` map or `context.first.buckets`. `SessionPayload` types both
 * as `unknown`, so the read that consults them names the shape here.
 */
type MeasurabilityEntry = { availability?: string; reason?: string }
type MeasurabilityMap = Record<string, MeasurabilityEntry | undefined>

/**
 * Renders a request-supplied identifier for a log line. Control characters are
 * stripped so a crafted id cannot forge log entries, and the result is
 * truncated. Callers pass it as a printf argument, never as the format string.
 */
function logId(value: string): string {
  return value.replace(/[^\x20-\x7e]/g, '?').slice(0, 120)
}

/**
 * Sanitizes an unknown caught value for a log line. Extracts the message from
 * an Error, or converts any other value to a string, then strips CR/LF and
 * other control characters so a crafted database error cannot forge log entries.
 */
function logErr(value: unknown): string {
  const raw = value instanceof Error ? value.message : String(value)
  return raw.replace(/[^\x20-\x7e]/g, '?').slice(0, 500)
}


function isCanonicalPart(value: string): value is CanonicalContentKey {
  return (CANONICAL_CONTENT_KEYS as readonly string[]).includes(value)
}

function sessionKeyOf(record: Pick<CanonicalRecord, 'sessionId' | 'traceId'>): string | null {
  return record.sessionId ?? record.traceId ?? null
}

function partsFromRecord(record: CanonicalRecord): ContentPart[] {
  if (record.parts !== undefined && record.parts.length > 0) {
    return [...record.parts].sort((a, b) => (a.order ?? 0) - (b.order ?? 0))
  }
  const synthesized: ContentPart[] = []
  for (const key of CANONICAL_CONTENT_KEYS) {
    const text = record.content[key]
    if (typeof text === 'string' && text !== '') {
      synthesized.push({ part: key, text })
    }
  }
  return synthesized
}

function partsFromRow(row: Record<string, unknown>): ContentPart[] {
  if (row.parts_json !== null && row.parts_json !== undefined) {
    try {
      const parts = decompressRaw(row.parts_json as Uint8Array) as ContentPart[]
      if (Array.isArray(parts) && parts.length > 0) {
        return [...parts].sort((a, b) => (a.order ?? 0) - (b.order ?? 0))
      }
    } catch {
      // Fall through to content_json — a corrupt blob is not an empty session.
    }
  }
  if (typeof row.content_json === 'string' && row.content_json !== '' && row.content_json !== '{}') {
    try {
      const content = JSON.parse(row.content_json) as CanonicalContent
      const synthesized: ContentPart[] = []
      for (const key of CANONICAL_CONTENT_KEYS) {
        const text = content[key]
        if (typeof text === 'string' && text !== '') {
          synthesized.push({ part: key, text })
        }
      }
      return synthesized
    } catch {
      return []
    }
  }
  return []
}

function toContentSource(record: CanonicalRecord): ContentSourceRecord {
  return {
    spanId: record.spanId,
    sessionKey: sessionKeyOf(record),
    parts: partsFromRecord(record),
  }
}

function applyContentBudget(
  parts: SessionContentPart[],
  budget: number,
): SessionContentPart[] {
  let remaining = budget
  return parts.map((part) => {
    const totalLength = part.text.length
    if (remaining <= 0) {
      return { ...part, text: '', truncated: true, totalLength }
    }
    if (totalLength > remaining) {
      const text = part.text.slice(0, remaining)
      remaining = 0
      return { ...part, text, truncated: true, totalLength }
    }
    remaining -= totalLength
    return part
  })
}

function extractMessagesFromHistory(text: string): { userMessages: string[]; assistantMessages: string[] } {
  const userMessages: string[] = []
  const assistantMessages: string[] = []
  if (!text) return { userMessages, assistantMessages }

  let parsed: unknown = null
  try {
    parsed = JSON.parse(text)
  } catch {
    // text is not JSON
  }

  if (Array.isArray(parsed)) {
    for (const msg of parsed) {
      if (!msg || typeof msg !== 'object') continue
      const role = String((msg as Record<string, unknown>).role || '').toLowerCase()
      let msgText = ''
      const content = (msg as Record<string, unknown>).content
      const textVal = (msg as Record<string, unknown>).text
      const partsVal = (msg as Record<string, unknown>).parts

      if (typeof content === 'string') {
        msgText = content
      } else if (typeof textVal === 'string') {
        msgText = textVal
      } else if (Array.isArray(partsVal)) {
        msgText = partsVal
          .map((pt: unknown) =>
            typeof pt === 'string'
              ? pt
              : typeof (pt as Record<string, unknown>)?.text === 'string'
                ? String((pt as Record<string, unknown>).text)
                : typeof (pt as Record<string, unknown>)?.content === 'string'
                  ? String((pt as Record<string, unknown>).content)
                  : JSON.stringify(pt),
          )
          .join('\n')
      } else if (content !== undefined) {
        msgText = JSON.stringify(content, null, 2)
      }

      if (role === 'user') {
        if (msgText.trim()) userMessages.push(msgText.trim())
      } else if (role === 'assistant' || role === 'model') {
        if (msgText.trim()) assistantMessages.push(msgText.trim())
      }
    }
  }

  if (userMessages.length === 0 && assistantMessages.length === 0) {
    // Regex matching user / assistant turns in raw dialog text
    const lines = text.split('\n')
    let currentRole: 'user' | 'assistant' | null = null
    let currentChunk: string[] = []

    for (const line of lines) {
      const userMatch = line.match(/^(?:user|human):\s*(.*)$/i)
      const asstMatch = line.match(/^(?:assistant|model|bot):\s*(.*)$/i)

      if (userMatch) {
        if (currentRole === 'user' && currentChunk.length > 0) userMessages.push(currentChunk.join('\n').trim())
        if (currentRole === 'assistant' && currentChunk.length > 0) assistantMessages.push(currentChunk.join('\n').trim())
        currentRole = 'user'
        currentChunk = userMatch[1] ? [userMatch[1]] : []
      } else if (asstMatch) {
        if (currentRole === 'user' && currentChunk.length > 0) userMessages.push(currentChunk.join('\n').trim())
        if (currentRole === 'assistant' && currentChunk.length > 0) assistantMessages.push(currentChunk.join('\n').trim())
        currentRole = 'assistant'
        currentChunk = asstMatch[1] ? [asstMatch[1]] : []
      } else if (currentRole) {
        currentChunk.push(line)
      }
    }
    if (currentRole === 'user' && currentChunk.length > 0) userMessages.push(currentChunk.join('\n').trim())
    if (currentRole === 'assistant' && currentChunk.length > 0) assistantMessages.push(currentChunk.join('\n').trim())
  }

  return { userMessages, assistantMessages }
}

/** One week in milliseconds — the unit of the refresh coverage window. */
const REFRESH_WEEK_MS = 7 * 24 * 60 * 60 * 1000

/**
 * Derive the coverage window of a refresh run (T4).
 *
 * <remarks>
 * The window a run covered is `[startedAt − historyWeeks, startedAt]`
 * (`utcHistoryWindow` in `refresh/source-reader.ts` anchors on the command
 * start, which is what `started_at` persists). Anything missing or
 * unparseable degrades to null/null: unknown stays unknown, never 0.
 * </remarks>
 */
function refreshWindowBounds(
  startedAt: string | null | undefined,
  historyWeeks: number | null | undefined,
): { coveredFrom: string | null; coveredThrough: string | null } {
  if (startedAt === null || startedAt === undefined) return { coveredFrom: null, coveredThrough: null }
  if (historyWeeks === null || historyWeeks === undefined) return { coveredFrom: null, coveredThrough: null }
  const throughMs = Date.parse(startedAt)
  if (!Number.isFinite(throughMs) || !Number.isFinite(historyWeeks)) {
    return { coveredFrom: null, coveredThrough: null }
  }
  return {
    coveredFrom: new Date(throughMs - historyWeeks * REFRESH_WEEK_MS).toISOString(),
    coveredThrough: startedAt,
  }
}

/**
 * Assemble per-source ingest activity from record counts and log aggregates (T4).
 *
 * <remarks>
 * The union of both key sets is reported so a log-only source (e.g.
 * `otlp:logs`) still shows its received count beside zero records, and a
 * record-only source still shows its history beside zero ingested. Both
 * zeros are facts about their respective tables, not claims about the
 * receiver. Empty + empty is the only `unknown`.
 * </remarks>
 */
function buildIngestActivity(
  recordCounts: ReadonlyMap<string, number>,
  logSums: ReadonlyMap<string, { total: number; lastAt: string | null }>,
  lastReceivedAt: string | null,
): IngestActivity {
  if (recordCounts.size === 0 && logSums.size === 0) {
    return {
      status: 'unknown',
      reason: 'no receiver activity recorded',
      sources: [],
      lastReceivedAt: null,
    }
  }
  const keys = [...new Set([...recordCounts.keys(), ...logSums.keys()])].sort()
  const sources: IngestActivitySource[] = keys.map((source) => {
    const labeled = sourceDisplayName(source)
    return {
      source,
      display: labeled.display,
      kind: labeled.kind,
      recordCount: recordCounts.get(source) ?? 0,
      ingestedCount: logSums.get(source)?.total ?? 0,
      lastReceivedAt: logSums.get(source)?.lastAt ?? null,
    }
  })
  return { status: 'known', sources, lastReceivedAt }
}

export class KyberBridge {
  private canonDb?: DatabaseSync
  private readonly store?: CanonStore
  readonly canonPath: string
  readonly ratesPath: string | undefined

  /**
   * `true` only for a handle this bridge opened itself from `canonPath`: no
   * injected `canonDb`, and `canonPath` is not `:memory:`. Only such a handle
   * is ever probed, reopened, or dropped by {@link getDb}.
   */
  private readonly ownsHandle: boolean
  private readonly reopenCheckIntervalMs: number
  private readonly now: () => number
  private closed = false
  /** dev/ino recorded from the stat taken immediately before the current owned handle was opened. */
  private identity?: FileIdentity
  /** `undefined` until the first probe runs; construction counts as the first probe. */
  private lastProbeAt?: number
  private warnedOpenFailed = false
  private warnedCloseFailed = false
  /**
   * `now()` value at the most recent "failed to stat" warning, or
   * `undefined` before the first one. A persistent stat failure re-warns
   * once {@link STAT_FAILURE_WARN_INTERVAL_MS} has elapsed since this,
   * rather than warning only once ever per instance — see the durability
   * note on {@link reconcile}.
   */
  private lastStatFailureWarnAt?: number
  /**
   * Memoized session-identity map for compare. Keyed by a cheap records
   * generation fingerprint (`COUNT`/`MAX(rowid)` plus file identity) so the
   * full-table `SELECT DISTINCT` is not paid on every compare click; invalidated
   * when records change or the owned handle is replaced.
   */
  private identitiesMemo?: { db: DatabaseSync; generation: string; value: SessionIdentities }
  /**
   * `key\0harness` pairs whose share-drop warning was already logged, so a
   * Compare click on the same empty share does not re-warn forever. Bounded
   * by {@link SHARE_DROP_WARN_LIMIT} (oldest entry evicted on overflow);
   * cleared when the owned handle closes.
   */
  private readonly warnedShareDrops = new Set<string>()

  constructor(options?: KyberBridgeOptions) {
    this.canonPath =
      options?.canonPath ??
      process.env.KYBER_CANON_DB ??
      join(homedir(), '.kyberdash', 'canon.db')

    this.ratesPath = options?.ratesPath

    this.store = options?.store

    this.reopenCheckIntervalMs = options?.reopenCheckIntervalMs ?? DEFAULT_REOPEN_CHECK_INTERVAL_MS
    this.now = options?.now ?? Date.now

    this.ownsHandle =
      options?.canonDb === undefined &&
      this.canonPath !== ':memory:' &&
      !(this.store && typeof this.store.getDatabase === 'function')

    if (options?.canonDb) {
      this.canonDb = options.canonDb
      try {
        this.canonDb.exec('PRAGMA busy_timeout = 5000')
      } catch {}
    } else if (this.store && typeof this.store.getDatabase === 'function') {
      this.canonDb = this.store.getDatabase()
    } else if (this.canonPath === ':memory:') {
      this.canonDb = this.openMemoryDb()
    } else {
      // Construction counts as the first probe.
      this.lastProbeAt = this.now()
      this.reconcile()
    }

  }

  private openMemoryDb(): DatabaseSync | undefined {
    try {
      const db = new DatabaseSync(':memory:', { open: true })
      try {
        db.exec('PRAGMA busy_timeout = 5000')
      } catch (pragmaErr) {
        // The native handle opened successfully; a pragma failing right
        // after must not leak it. Close it here — before rethrowing into
        // the same failure handling a failed open takes below — because
        // this `db` is a local variable the outer catch never sees, and
        // it is never assigned anywhere the caller could close it from.
        try {
          db.close()
        } catch {
          // Best-effort: the handle is being discarded either way.
        }
        throw pragmaErr
      }
      return db
    } catch (err) {
      console.warn('[KyberBridge] Failed to open SQLite database at :memory::', err)
      return undefined
    }
  }

  /**
   * Statically compare `canonPath`'s current file identity against the owned
   * handle's, and open, reopen, or drop the handle so it always follows the
   * file currently at `canonPath`. Only ever touches a handle this bridge
   * opened itself — see {@link ownsHandle}. Identity is `dev`/`ino`
   * (BigInt, because Windows file indexes can exceed 2^53); size and mtime
   * are not compared because an ordinary in-place write changes both without
   * replacing the file.
   */
  private reconcile(): void {
    let info: BigIntStats | undefined
    try {
      info = statSync(this.canonPath, { bigint: true, throwIfNoEntry: false })
    } catch (err) {
      // `throwIfNoEntry: false` only suppresses ENOENT — the confirmed
      // "file absent" case, which statSync signals by returning `undefined`
      // rather than throwing. Anything that reaches this catch (EACCES,
      // EMFILE, ELOOP, a transient I/O error, ...) is NOT that signal, so it
      // must not be treated the same as "absent": doing so would silently
      // tear down a live, healthy handle over a transient failure of the
      // stat call itself. Keep whatever handle is currently held exactly as
      // it is, and let the next throttled probe retry the stat normally. If
      // no handle was held yet, there is nothing to tear down, but the
      // warning still fires so this is distinguishable from the normal,
      // silent, confirmed-absent case.
      //
      // Durability: a persistent failure (a stuck permission problem, an
      // exhausted file-descriptor table, ...) must not go silent after one
      // warning. An unattended `kyberdash web` process would otherwise keep
      // serving whatever it already had — possibly stale — forever, with no
      // ongoing symptom for an operator to notice. So this re-warns at most
      // once per `STAT_FAILURE_WARN_INTERVAL_MS` for as long as the failure
      // keeps recurring, rather than only once ever per instance.
      const nowTs = this.now()
      if (
        this.lastStatFailureWarnAt === undefined ||
        nowTs - this.lastStatFailureWarnAt >= STAT_FAILURE_WARN_INTERVAL_MS
      ) {
        this.lastStatFailureWarnAt = nowTs
        console.warn(`[KyberBridge] Failed to stat ${this.canonPath}, keeping current handle:`, err)
      }
      return
    }

    const stat: FileIdentity | undefined = info === undefined ? undefined : { dev: info.dev, ino: info.ino }

    if (stat === undefined) {
      // Confirmed absent (statSync returned rather than threw). Drop any
      // handle rather than keep serving an unlinked database. Silent, per
      // design: an absent file is not a failure.
      if (this.canonDb !== undefined) {
        this.closeHandle()
      }
      this.identity = undefined
      return
    }

    if (
      this.canonDb !== undefined &&
      this.identity !== undefined &&
      stat.dev === this.identity.dev &&
      stat.ino === this.identity.ino
    ) {
      // Present, same file: keep the handle.
      return
    }

    // Present, and either no handle yet or a different file: close the old
    // handle first, then open the new one and record its pre-open identity.
    if (this.canonDb !== undefined) {
      this.closeHandle()
    }

    try {
      const db = new DatabaseSync(this.canonPath, { readOnly: true })
      try {
        db.exec('PRAGMA busy_timeout = 5000')
      } catch (pragmaErr) {
        // The native handle opened successfully; a pragma failing right
        // after must not leak it. Close it here — before rethrowing into
        // the same failure handling a failed open takes below — because
        // this `db` is a local variable the outer catch never sees, and
        // `this.canonDb` is never assigned it.
        try {
          db.close()
        } catch {
          // Best-effort: the handle is being discarded either way.
        }
        throw pragmaErr
      }
      this.canonDb = db
      this.identity = stat
    } catch (err) {
      if (!this.warnedOpenFailed) {
        this.warnedOpenFailed = true
        console.warn(`[KyberBridge] Failed to open SQLite database at ${this.canonPath}:`, err)
      }
      this.canonDb = undefined
      this.identity = undefined
    }
  }

  /** Closes the current handle, warning once per instance on failure, and always drops the reference. */
  private closeHandle(): void {
    try {
      this.canonDb?.close()
    } catch (err) {
      if (!this.warnedCloseFailed) {
        this.warnedCloseFailed = true
        console.warn(`[KyberBridge] Failed to close SQLite database at ${this.canonPath}:`, err)
      }
    }
    this.canonDb = undefined
    this.identitiesMemo = undefined
    this.warnedShareDrops.clear()
  }

  /**
   * The private store accessor every query goes through. For an injected
   * handle or a `:memory:` bridge this just returns the current handle — it
   * is never probed. For an owned handle it probes at most once per
   * `reopenCheckIntervalMs` (measured by `now`), so an idle server never
   * stats and a query never sees two handles in one call. Callers capture
   * the result once per method and reuse the local variable for the rest of
   * that method's body.
   */
  private getDb(): DatabaseSync | undefined {
    if (this.closed) return undefined
    if (!this.ownsHandle) return this.canonDb

    const elapsed = this.lastProbeAt === undefined ? Number.POSITIVE_INFINITY : this.now() - this.lastProbeAt
    if (this.lastProbeAt !== undefined && elapsed < this.reopenCheckIntervalMs) {
      return this.canonDb
    }

    this.lastProbeAt = this.now()
    this.reconcile()
    return this.canonDb
  }

  private hasTable(db: DatabaseSync | undefined, tableName: string): boolean {
    if (!db) return false
    try {
      const row = db
        .prepare("SELECT 1 FROM sqlite_master WHERE type='table' AND name = ?")
        .get(tableName)
      return Boolean(row)
    } catch {
      return false
    }
  }

  /**
   * Close open SQLite database handles. After this the accessor never probes
   * or opens again, even if the file at `canonPath` changes.
   */
  close(): void {
    this.closed = true
    this.identitiesMemo = undefined
    if (this.store && typeof this.store.getDatabase === 'function' && this.canonDb === this.store.getDatabase()) {
      this.canonDb = undefined
    } else {
      this.closeHandle()
    }
  }

  /**
   * List all available sessions from the canonical store, sorted by started DESC.
   */
  listSessions(limit?: number): SessionSummary[] {
    const db = this.getDb()
    const list: SessionSummary[] = []

    // The canonical derived `session` cache is the only reporting authority.
    // Raw `records` are never synthesized into sessions here: projection
    // (projectCanonicalStore) is what turns accepted spans into derived rows,
    // so a record-only group is not yet a session and must not appear.
    if (this.hasTable(db, 'session')) {
      try {
        let rows: SessionDbRow[] = []
        try {
          // Fast path: use json_extract so we don't pull large payload blobs across the bridge
          rows = db!
            .prepare(
              'SELECT session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, ' +
                "json_extract(payload, '$.summary') as summary_json, " +
                "json_array_length(json_extract(payload, '$.problems')) as problems_count " +
                'FROM session ORDER BY started DESC'
            )
            .all() as unknown as SessionDbRow[]
        } catch {
          // Fallback if SQLite json functions are unavailable
          rows = db!
            .prepare(
              'SELECT session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, payload ' +
                'FROM session ORDER BY started DESC'
            )
            .all() as unknown as SessionDbRow[]
        }

        for (const row of rows) {
          let summ: ParsedSummary | null = null
          let problemsCount = 0

          if (row.summary_json !== undefined && row.summary_json !== null) {
            if (typeof row.summary_json === 'string') {
              try {
                summ = JSON.parse(row.summary_json) as ParsedSummary
              } catch {}
            } else if (typeof row.summary_json === 'object') {
              summ = row.summary_json as ParsedSummary
            }
            problemsCount = Number(row.problems_count) || 0
          } else if (typeof row.payload === 'string') {
            try {
              const p = JSON.parse(row.payload) as { summary?: ParsedSummary; problems?: unknown[] }
              summ = p?.summary ?? null
              problemsCount = Array.isArray(p?.problems) ? p.problems.length : 0
            } catch {}
          }

          summ = summ ?? {}
          const block = summ.cost
          const cost: SessionSummary['cost'] =
            block && typeof block === 'object' && block.basis && block.status
              ? {
                  basis: block.basis,
                  status: block.status,
                  ...(typeof block.value === 'number' ? { value: block.value } : {}),
                  ...(typeof block.currency === 'string' ? { currency: block.currency } : {}),
                }
              : { basis: 'unknown', status: 'no_rate' }
          list.push({
            session_id: row.session_id,
            harness: row.harness,
            label: row.label ?? null,
            is_subagent: Boolean(row.is_subagent),
            parent_session: row.parent_session ?? null,
            agent_name: row.agent_name ?? null,
            repo: row.repo ?? null,
            branch: row.branch ?? null,
            started: row.started ?? null,
            ended: row.ended ?? null,
            turn_count: summ.turn_count ?? null,
            request_count: summ.request_count ?? null,
            total_input: summ.total_input ?? null,
            total_output: summ.total_output ?? null,
            cost_usd:
              cost.status === 'priced' && cost.currency === 'USD' && typeof cost.value === 'number'
                ? cost.value
                : null,
            cost,
            models: Array.isArray(summ.models) ? summ.models : [],
            problems: problemsCount,
          })
        }
      } catch (err) {
        console.warn('[KyberBridge] Failed querying session table in canon.db:', err)
      }
    }

    // Sort globally by started DESC
    list.sort((a, b) => (b.started ?? '').localeCompare(a.started ?? ''))

    if (typeof limit === 'number' && limit > 0) {
      return list.slice(0, Math.floor(limit))
    }
    return list
  }

  /**
   * Per-harness latest session time as epoch ms (coverage-window seam).
   *
   * <remarks>
   * The harness window check needs one fact per harness — the latest
   * timestamped session — not the session table. Both branches of this seam
   * read only the narrow `(harness, started, ended)` columns (the store
   * branch via `CanonStore.listSessionTimeColumns`, the raw-db branch via
   * an explicit narrow SELECT) and fold them into a per-harness maximum in
   * one pass, so payload blobs are never pulled across the bridge. The fold
   * is deliberately uncapped — every session row participates — and runs in
   * JS epoch ms rather than SQL MAX: recency is `ended ?? started` parsed
   * to epoch ms (the report's `sessionAt` precedence in
   * `analysis/report/build.ts`) because epoch comparison sorts `+02:00`-
   * offset stamps correctly where a raw string compare does not. A harness
   * with no parseable timestamp is absent from the map — unknown, never 0.
   * </remarks>
   */
  getLatestSessionTimeByHarness(): Map<string, number> {
    const latest = new Map<string, number>()
    const track = (harness: unknown, started: unknown, ended: unknown): void => {
      if (typeof harness !== 'string' || harness === '') return
      const stamp = (typeof ended === 'string' ? ended : null) ?? (typeof started === 'string' ? started : null)
      if (stamp === null) return
      const at = Date.parse(stamp)
      if (!Number.isFinite(at)) return
      const prev = latest.get(harness)
      if (prev === undefined || at > prev) latest.set(harness, at)
    }
    if (this.store) {
      try {
        for (const row of this.store.listSessionTimeColumns()) {
          track(row.harness, row.started, row.ended)
        }
      } catch {
        return new Map<string, number>()
      }
      return latest
    }
    const db = this.getDb()
    if (!this.hasTable(db, 'session')) return latest
    try {
      const rows = db!
        .prepare('SELECT harness, started, ended FROM session')
        .all() as Array<{ harness: unknown; started: unknown; ended: unknown }>
      for (const row of rows) track(row.harness, row.started, row.ended)
    } catch {
      return new Map<string, number>()
    }
    return latest
  }

  /**
   * Per-harness session counts (issue #194 presentation overlay).
   *
   * <remarks>
   * The harness list must agree with `/sessions` without materializing the
   * session table: an uncapped `listSessions()` is forbidden on this path
   * (coverage-window seam). Both branches select only `harness` and fold
   * counts in JS. A harness with no rows is absent from the map — unknown,
   * never a fabricated 0 that would overwrite a rollup `sessionCount`.
   * </remarks>
   */
  countSessionsByHarness(): Map<string, number> {
    return this.countHarnessColumn('session')
  }

  /**
   * Per-harness run counts, same contract as `countSessionsByHarness`.
   */
  countRunsByHarness(): Map<string, number> {
    return this.countHarnessColumn('run')
  }

  private countHarnessColumn(table: 'session' | 'run'): Map<string, number> {
    const counts = new Map<string, number>()
    const add = (harness: unknown): void => {
      if (typeof harness !== 'string' || harness === '') return
      counts.set(harness, (counts.get(harness) ?? 0) + 1)
    }
    if (this.store) {
      try {
        if (table === 'session') {
          for (const row of this.store.listSessionTimeColumns()) add(row.harness)
        } else {
          for (const row of this.store.listRuns()) add(row.harness)
        }
      } catch {
        return new Map<string, number>()
      }
      return counts
    }
    const db = this.getDb()
    if (!this.hasTable(db, table)) return counts
    try {
      const rows = db!.prepare(`SELECT harness FROM ${table}`).all() as Array<{ harness: unknown }>
      for (const row of rows) add(row.harness)
    } catch {
      return new Map<string, number>()
    }
    return counts
  }

  /**
   * Get the full precomputed view payload for a given session ID.
   * The canonical session payload is returned without route-level adaptation.
   * Strings longer than maxLen are safely truncated to prevent oversized JSON responses.
   */
  getSessionPayload<T = SessionPayload>(sessionId: string): T | null {
    if (!sessionId) return null

    if (this.store) {
      const payload = this.store.getSessionPayload(sessionId)
      return payload === undefined ? null : (_clip(payload) as T)
    }

    const db = this.getDb()
    if (this.hasTable(db, 'session')) {
      try {
        const row = db!
          .prepare('SELECT payload FROM session WHERE session_id = ?')
          .get(sessionId) as { payload: string } | undefined
        if (row && typeof row.payload === 'string') {
          const parsed = JSON.parse(row.payload) as unknown
          return _clip(parsed) as T
        }
      } catch (err) {
        console.warn(
          '[KyberBridge] Error reading session payload from canon.db for %s: %s',
          logId(sessionId),
          logErr(err),
        )
      }
    }

    return null
  }

  /**
   * One span's harness-emitted attributes, for the timeline inspector (R9.2).
   *
   * The timeline nodes in a session payload no longer carry their attribute
   * map: it is the record's raw span payload, and copying it into every
   * session made the derived cache a second, uncompressed copy of the whole
   * corpus. The inspector asks for the node it is showing instead, and pays one
   * primary-key seek and one inflate for it.
   *
   * `_clip` is deliberately not applied, for the same reason it is not applied
   * to unclipped content: this route exists so a span click can show what the
   * harness actually emitted.
   */
  getSpanAttributes(spanId: string): { spanId: string; attributes: Record<string, unknown> } | null {
    if (!spanId) return null

    if (this.store) {
      const attributes = this.store.spanAttributes(spanId)
      return attributes === undefined ? null : { spanId, attributes }
    }

    const db = this.getDb()
    if (this.hasTable(db, 'records')) {
      try {
        const row = db!
          .prepare('SELECT raw FROM records WHERE span_id = ?')
          .get(spanId) as { raw: unknown } | undefined
        if (row && row.raw !== null && row.raw !== undefined) {
          const parsed = JSON.parse(
            inflateSync(Buffer.from(row.raw as Uint8Array)).toString('utf8'),
          ) as unknown
          if (parsed !== null && typeof parsed === 'object') {
            return { spanId, attributes: parsed as Record<string, unknown> }
          }
        }
      } catch (err) {
        console.warn('[KyberBridge] Error reading span attributes for %s:', logId(spanId), err)
      }
    }

    return null
  }

  /**
   * Unclipped content for the inspector. `_clip` is deliberately not applied:
   * this is the route that exists so a band click can show the real prompt.
   * Prefer `CanonStore.recordsForSession` / `get` when a store is injected;
   * otherwise the same lookup runs over the already-open `canonDb` handle
   * (opening a second CanonStore on the live file would migrate it).
   */
  getSessionContent(
    sessionId: string,
    options: SessionContentOptions = {},
  ): SessionContentResult | null {
    if (!sessionId) return null

    // An unknown part is a bad filter, not a successful empty response.
    if (options.part !== undefined && options.part !== '' && !isCanonicalPart(options.part)) {
      return null
    }

    const records = this.loadContentRecords(sessionId, options.spanId)
    if (records.length === 0 && !this.sessionKnown(sessionId)) {
      return null
    }
    // A known session does not make an arbitrary span valid.
    if (options.spanId !== undefined && options.spanId !== '' && records.length === 0) {
      return null
    }

    const partFilter =
      options.part !== undefined && options.part !== '' && isCanonicalPart(options.part)
        ? options.part
        : undefined

    const assembled: SessionContentPart[] = []
    for (const record of records) {
      for (const piece of record.parts) {
        if (partFilter !== undefined && piece.part !== partFilter) continue
        const entry: SessionContentPart = {
          spanId: record.spanId,
          part: piece.part,
          text: piece.text,
        }
        if (piece.tokens !== undefined) entry.tokens = piece.tokens
        if (piece.server !== undefined) entry.server = piece.server
        assembled.push(entry)
      }
    }
    // Canonical-but-absent is also an invalid part filter for this session/span.
    if (partFilter !== undefined && assembled.length === 0) return null

    const result: SessionContentResult = {
      sessionId,
      parts: applyContentBudget(assembled, CONTENT_RESPONSE_BUDGET),
    }
    if (options.spanId) result.spanId = options.spanId
    return result
  }

  /**
   * Unclipped assembled turn content for the Context Inspector (Task G1 / Decision D14).
   * Retrieves all blocks and parts for the given turn index, strictly 0-based:
   * each row resolves by explicit identity (`index`, else legacy 1-based `turn`
   * as `turn - 1`), and array position only matches rows carrying neither.
   * Anything else resolves to nothing (the route 404s) rather than a
   * neighboring turn (issue #184).
   *
   * Sub-divided into canonical context blocks (system_prompt, tool_definitions,
   * instruction_context, conversation_history, tool_result_content) and parts
   * (including user_messages, assistant_turns, etc.).
   */
  assembleTurnContent(
    sessionId: string,
    turnIndex: number,
    budget = CONTENT_RESPONSE_BUDGET,
  ): TurnContentResult | null {
    if (!sessionId || turnIndex < 0 || isNaN(turnIndex)) return null
    if (!this.sessionKnown(sessionId)) return null

    const db = this.getDb()
    let targetSpanId: string | undefined
    let model: string | undefined

    // 1. Try resolving via session payload if available
    const payload = this.getSessionPayload<SessionPayload>(sessionId)
    const turns: TurnDescriptor[] = Array.isArray(payload?.turns)
      ? (payload.turns as TurnDescriptor[])
      : []
    if (turns.length > 0) {
      const turnItem = resolveTurn(turns, turnIndex, turnTransportIndexOf)
      if (turnItem) {
        if (typeof turnItem.spanId === 'string') targetSpanId = turnItem.spanId
        if (typeof turnItem.model === 'string') model = turnItem.model
      } else if (turns.some((t) => turnTransportIndexOf(t) !== undefined)) {
        // The payload names its turns and none matches: stop here. Falling
        // through to the positional record lookup below could serve a
        // neighboring span's content instead of the documented 404
        // (issue #184 review). Identity-free payloads still fall through.
        return null
      }
    }

    // 2. If targetSpanId not resolved yet, search canonical records
    if (!targetSpanId) {
      if (this.store) {
        const records = this.store.recordsForSession(sessionId)
        const turnRecords = records.filter((r) => r.op === 'llm.invoke')
        const pool = turnRecords.length > 0 ? turnRecords : records
        const target = resolveTurn(pool, turnIndex, (r) =>
          turnTransportIndexOf(r as CanonicalRecord & TurnDescriptor),
        )
        if (target) {
          targetSpanId = target.spanId
          model = (target as CanonicalRecord & TurnDescriptor).model ?? target.name
        }
      } else if (this.hasTable(db, 'records')) {
        try {
          const rows = db!
            .prepare('SELECT * FROM records WHERE COALESCE(session_id, trace_id) = ? ORDER BY timestamp')
            .all(sessionId) as Record<string, unknown>[]
          const turnRows = rows.filter((r) => r.op === 'llm.invoke')
          const pool = turnRows.length > 0 ? turnRows : rows
          // DB rows predate the descriptor shape: `index` may arrive as a
          // numeric string, and a null/empty index is no identity (unlike
          // `Number(null)`, which coerces to 0 and would hijack turn 0).
          const target = resolveTurn(pool, turnIndex, (r) => {
            const rawIndex: unknown = r.index
            const index =
              rawIndex === null || rawIndex === undefined || rawIndex === '' ? undefined : Number(rawIndex)
            return turnTransportIndexOf({
              index: typeof index === 'number' && Number.isFinite(index) ? index : undefined,
              turn: r.turn,
            })
          })
          if (target) {
            targetSpanId = String(target.span_id)
            model = String(target.name || '')
          }
        } catch {
          // ignore
        }
      }
    }

    if (!targetSpanId) return null

    // Load unclipped content records for this span
    const contentRecords = this.loadContentRecords(sessionId, targetSpanId)
    const rawParts: ContentPart[] = contentRecords[0]?.parts ?? []

    // Map and extract structured parts (system prompt, tools, user messages, assistant turns, etc.)
    const parts: TurnContentPart[] = []
    let orderCounter = 0

    for (const p of rawParts) {
      if (p.part === 'system_prompt') {
        parts.push({
          id: `part-sys-${orderCounter++}`,
          spanId: targetSpanId,
          part: 'system_prompt',
          label: 'System Prompt',
          text: p.text,
          tokens: p.tokens,
          server: p.server,
          order: p.order ?? orderCounter,
        })
      } else if (p.part === 'tool_definitions') {
        const label = p.server ? `Tools (${p.server})` : 'Tools'
        parts.push({
          id: `part-tool-${orderCounter++}`,
          spanId: targetSpanId,
          part: 'tool_definitions',
          label,
          text: p.text,
          tokens: p.tokens,
          server: p.server,
          order: p.order ?? orderCounter,
        })
      } else if (p.part === 'instruction_context') {
        parts.push({
          id: `part-inst-${orderCounter++}`,
          spanId: targetSpanId,
          part: 'instruction_context',
          label: 'Instruction Context',
          text: p.text,
          tokens: p.tokens,
          server: p.server,
          order: p.order ?? orderCounter,
        })
      } else if (p.part === 'tool_result_content') {
        parts.push({
          id: `part-toolres-${orderCounter++}`,
          spanId: targetSpanId,
          part: 'tool_result_content',
          label: 'Tool Results',
          text: p.text,
          tokens: p.tokens,
          server: p.server,
          order: p.order ?? orderCounter,
        })
      } else if (p.part === 'conversation_history') {
        const { userMessages, assistantMessages } = extractMessagesFromHistory(p.text)
        if (userMessages.length > 0 || assistantMessages.length > 0) {
          if (userMessages.length > 0) {
            parts.push({
              id: `part-user-${orderCounter++}`,
              spanId: targetSpanId,
              part: 'user_messages',
              label: 'User Messages',
              text: userMessages.join('\n\n'),
              tokens: p.tokens ? Math.round(p.tokens * (userMessages.length / (userMessages.length + assistantMessages.length))) : undefined,
              order: p.order ?? orderCounter,
            })
          }
          if (assistantMessages.length > 0) {
            parts.push({
              id: `part-asst-${orderCounter++}`,
              spanId: targetSpanId,
              part: 'assistant_turns',
              label: 'Assistant Turns',
              text: assistantMessages.join('\n\n'),
              tokens: p.tokens ? Math.round(p.tokens * (assistantMessages.length / (userMessages.length + assistantMessages.length))) : undefined,
              order: p.order ?? orderCounter,
            })
          }
        } else {
          // Generic conversation history
          parts.push({
            id: `part-user-${orderCounter++}`,
            spanId: targetSpanId,
            part: 'user_messages',
            label: 'User Messages',
            text: p.text,
            tokens: p.tokens,
            order: p.order ?? orderCounter,
          })
          parts.push({
            id: `part-conv-${orderCounter++}`,
            spanId: targetSpanId,
            part: 'conversation_history',
            label: 'Conversation History',
            text: p.text,
            tokens: p.tokens,
            order: p.order ?? orderCounter,
          })
        }
      } else {
        parts.push({
          id: `part-other-${orderCounter++}`,
          spanId: targetSpanId,
          part: p.part,
          label: String(p.part).replace(/_/g, ' '),
          text: p.text,
          tokens: p.tokens,
          server: p.server,
          order: p.order ?? orderCounter,
        })
      }
    }

    // Canonical context blocks
    const blockDefs: Array<{ key: string; label: string; partKeys: string[] }> = [
      { key: 'system_prompt', label: 'System prompt', partKeys: ['system_prompt'] },
      { key: 'tool_definitions', label: 'Tools', partKeys: ['tool_definitions'] },
      { key: 'instruction_context', label: 'Instruction context', partKeys: ['instruction_context'] },
      { key: 'conversation_history', label: 'Conversation history', partKeys: ['conversation_history', 'user_messages', 'assistant_turns'] },
      { key: 'tool_result_content', label: 'Tool results', partKeys: ['tool_result_content'] },
    ]

    const blocks: TurnContentBlock[] = []
    for (const def of blockDefs) {
      const matchingParts = parts.filter((p) => def.partKeys.includes(p.part))
      // Filter out redundant conversation_history if user_messages / assistant_turns is already present
      const uniqueParts = matchingParts.filter((p, idx, arr) => {
        if (p.part === 'conversation_history' && arr.some((other) => other.part === 'user_messages' && other.text === p.text)) {
          return false
        }
        return true
      })
      const text = uniqueParts.map((p) => p.text).join('\n\n')
      const tokens = uniqueParts.reduce((sum, p) => sum + (p.tokens ?? 0), 0) || undefined

      const block: TurnContentBlock = {
        key: def.key,
        label: def.label,
        parts: uniqueParts,
        text,
        tokens,
      }

      // Check measurability from payload
      const flat = payload?.measurability as MeasurabilityMap | undefined
      const buckets = (payload?.context as { first?: { buckets?: MeasurabilityMap } } | undefined)
        ?.first?.buckets
      const measurability = flat?.[def.key] ?? buckets?.[def.key]
      if (measurability && typeof measurability === 'object' && measurability.availability === 'not_measurable') {
        block.notMeasurable = { reason: measurability.reason || 'Not measurable for this harness.' }
      }

      blocks.push(block)
    }

    // Assemble whole-turn plain text with clear headings per block
    const sectionTexts: string[] = []
    for (const b of blocks) {
      if (b.text.trim()) {
        sectionTexts.push(`=== ${b.label.toUpperCase()} ===\n${b.text.trim()}`)
      }
    }
    let assembledText = sectionTexts.join('\n\n')
    let truncated = false
    let totalLength: number | undefined

    if (assembledText.length > budget) {
      totalLength = assembledText.length
      assembledText = assembledText.slice(0, budget)
      truncated = true
    }

    // Apply budget to individual blocks and parts
    for (const block of blocks) {
      if (block.text.length > budget) {
        block.totalLength = block.text.length
        block.text = block.text.slice(0, budget)
        block.truncated = true
      }
      for (const part of block.parts) {
        if (part.text.length > budget) {
          part.totalLength = part.text.length
          part.text = part.text.slice(0, budget)
          part.truncated = true
        }
      }
    }
    for (const part of parts) {
      if (part.text.length > budget) {
        part.totalLength = part.text.length
        part.text = part.text.slice(0, budget)
        part.truncated = true
      }
    }

    const result: TurnContentResult = {
      sessionId,
      turnIndex,
      spanId: targetSpanId,
      model,
      blocks,
      parts,
      assembledText,
      ...(truncated ? { truncated, totalLength } : {}),
    }

    return result
  }


  /**
   * A session is known if a derived row exists or any record keys to it.
   * The session table is cheap; records are the fallback for a corpus that
   * has not been built into `session` yet. Checking the table first avoids
   * clipping a large payload just to decide whether to 404.
   */
  private sessionKnown(sessionId: string): boolean {
    if (this.store?.getSessionPayload(sessionId) !== undefined) return true
    const db = this.getDb()
    if (this.hasTable(db, 'session')) {
      try {
        const row = db!.prepare('SELECT 1 FROM session WHERE session_id = ?').get(sessionId)
        if (row) return true
      } catch {
        // Older or partial schemas still fall through to the records check.
      }
    }
    // Records without a derived session row still make the session real —
    // otherwise a span miss on an unbuilt session would 404 as "unknown".
    if (this.store) return this.store.recordsForSession(sessionId).length > 0
    return this.loadContentRecordsFromDb(sessionId).length > 0
  }

  /**
   * `get` when a span is named so a 7,000-turn session is not decompressed
   * just to return one band; `recordsForSession` otherwise.
   */
  private loadContentRecords(sessionId: string, spanId?: string): ContentSourceRecord[] {
    if (this.store) {
      if (spanId) {
        const record = this.store.get(spanId)
        if (record === undefined) return []
        if (sessionKeyOf(record) !== sessionId) return []
        return [toContentSource(record)]
      }
      return this.store.recordsForSession(sessionId).map(toContentSource)
    }
    return this.loadContentRecordsFromDb(sessionId, spanId)
  }

  private loadContentRecordsFromDb(sessionId: string, spanId?: string): ContentSourceRecord[] {
    const db = this.getDb()
    if (!this.hasTable(db, 'records')) return []

    if (spanId) {
      try {
        const row = db!
          .prepare('SELECT * FROM records WHERE span_id = ?')
          .get(spanId) as Record<string, unknown> | undefined
        if (row === undefined) return []
        const key =
          row.session_id !== null && row.session_id !== undefined
            ? String(row.session_id)
            : row.trace_id !== null && row.trace_id !== undefined
              ? String(row.trace_id)
              : null
        if (key !== sessionId) return []
        return [
          {
            spanId: String(row.span_id),
            sessionKey: key,
            parts: partsFromRow(row),
          },
        ]
      } catch {
        return []
      }
    }

    try {
      const rows = db!
        .prepare(
          'SELECT * FROM records WHERE COALESCE(session_id, trace_id) = ? ORDER BY timestamp',
        )
        .all(sessionId) as Record<string, unknown>[]
      return rows.map((row) => ({
        spanId: String(row.span_id),
        sessionKey: sessionId,
        parts: partsFromRow(row),
      }))
    } catch {
      try {
        const rows = db!
          .prepare('SELECT * FROM records WHERE trace_id = ? ORDER BY timestamp')
          .all(sessionId) as Record<string, unknown>[]
        return rows.map((row) => ({
          spanId: String(row.span_id),
          sessionKey: sessionId,
          parts: partsFromRow(row),
        }))
      } catch {
        return []
      }
    }
  }

  /**
   * Canonical records for one session key, timestamp order. Empty when the
   * session is unknown — never a fabricated corpus.
   */
  private recordsForSessionKey(sessionKey: string): CanonicalRecord[] {
    if (this.store) return this.store.recordsForSession(sessionKey)
    const db = this.getDb()
    if (!this.hasTable(db, 'records')) return []
    try {
      const rows = db!
        .prepare(
          'SELECT * FROM records WHERE COALESCE(session_id, trace_id) = ? ORDER BY timestamp',
        )
        .all(sessionKey) as unknown as import('../canon/store.js').RecordRow[]
      return rows.map(toRecord)
    } catch {
      return []
    }
  }

  /**
   * Cheap generation fingerprint for the identities memo: file identity (when
   * owned) plus `PRAGMA data_version` (moves when another connection commits
   * — ingest, backfill) and `total_changes()` (moves on any INSERT, UPDATE or
   * DELETE through this connection, e.g. `CanonStore.setSessionId`). Both
   * survive in-place updates and delete-then-reinsert, which a row count or
   * max rowid would not, and neither scans `records`.
   */
  private identitiesGeneration(db: DatabaseSync): string | undefined {
    const fileKey = this.identity
      ? `${this.identity.dev}:${this.identity.ino}`
      : this.canonPath
    try {
      const version = db.prepare('PRAGMA data_version').get() as { data_version: number } | undefined
      const changes = db.prepare('SELECT total_changes() AS n').get() as { n: number } | undefined
      if (version === undefined || changes === undefined) return undefined
      return `${fileKey}:${version.data_version}:${changes.n}`
    } catch {
      // Unknown generation: never serve or store a memo (see sessionIdentities).
      return undefined
    }
  }

  /** Persisted share ids for the current store — same table `buildRuns` reads. */
  private sessionIdentities(): SessionIdentities {
    const db =
      this.store && typeof this.store.getDatabase === 'function'
        ? this.store.getDatabase()
        : this.getDb()
    if (!db) return new SessionIdentities([])
    if (!this.store && !this.hasTable(db, 'records')) return new SessionIdentities([])

    const generation = this.identitiesGeneration(db)
    if (
      generation !== undefined &&
      this.identitiesMemo?.db === db &&
      this.identitiesMemo.generation === generation
    ) {
      return this.identitiesMemo.value
    }

    let value: SessionIdentities
    if (this.store) {
      value = this.store.sessionIdentities()
    } else {
      try {
        const pairs = db
          .prepare(
            `SELECT DISTINCT COALESCE(session_id, trace_id) AS key, harness
             FROM records
             WHERE COALESCE(session_id, trace_id) IS NOT NULL`,
          )
          .all() as { key: string; harness: string }[]
        value = new SessionIdentities(pairs)
      } catch (err) {
        console.warn('[KyberBridge] Failed reading session identities from canon.db:', err)
        // A failed read is not a generation's answer — do not memoize it.
        return new SessionIdentities([])
      }
    }
    this.identitiesMemo = generation === undefined ? undefined : { db, generation, value }
    return value
  }

  /**
   * One harness share of a session key — mirrors `CanonStore.recordsForShare`.
   * When records exist under the key but the harness filter (including
   * excluded identities where `canonicalHarnessId` is null) drops them all,
   * returns a `dropNote` for the side's `metricsReason` and warns once per
   * key+harness (not once per Compare click).
   */
  private recordsForShare(
    key: string,
    harness: string,
  ): { records: CanonicalRecord[]; dropNote?: string } {
    const all = this.recordsForSessionKey(key)
    const canonical = normalizeHarnessName(harness)
    const records = all.filter(
      (record) => canonicalHarnessId(record.harness) === canonical,
    )
    if (all.length === 0 || records.length > 0) return { records }
    const excluded = canonicalHarnessId(harness) === null
    const dropNote = excluded
      ? `harness ${JSON.stringify(harness)} is an excluded identity; dropped ${all.length} record(s) under session key ${JSON.stringify(key)}`
      : `no records matched harness ${JSON.stringify(harness)}; dropped ${all.length} record(s) under session key ${JSON.stringify(key)}`
    const warnKey = `${canonical}\0${key}`
    if (!this.warnedShareDrops.has(warnKey)) {
      if (this.warnedShareDrops.size >= SHARE_DROP_WARN_LIMIT) {
        const oldest = this.warnedShareDrops.values().next()
        if (!oldest.done) this.warnedShareDrops.delete(oldest.value)
      }
      this.warnedShareDrops.add(warnKey)
      console.warn(`[KyberBridge] Compare share: ${dropNote}`)
    }
    return { records, dropNote }
  }

  /**
   * Records belonging to a run via its executions' session keys.
   * Resolves qualified ids through `SessionIdentities.shareOf`, dedupes twin
   * collectors per harness-scoped share, and returns only `llm.invoke` model
   * turns (issue #190). `identities` is computed once per compare request.
   */
  private recordsForRun(
    runId: string,
    identities: SessionIdentities,
  ): { records: CanonicalRecord[]; dropNotes: string[] } {
    const executions = this.listExecutions(runId)
    type Lookup = { key: string; harness: string }
    const lookups: Lookup[] = []
    const seen = new Set<string>()
    for (const execution of executions) {
      // Empty string is a present but unusable session id — fall through
      // to executionId (?? would keep "" and drop the execution's key).
      const key = execution.sessionId || execution.executionId
      if (!key || key.length === 0) continue
      const harness = execution.harness
      const dedupeId = `${normalizeHarnessName(harness)}\0${key}`
      if (seen.has(dedupeId)) continue
      seen.add(dedupeId)
      lookups.push({ key, harness })
    }
    // Executions can exist without selecting any session key (empty ids);
    // the run id itself stays the lookup key in that case.
    if (lookups.length === 0) {
      const harness =
        executions[0]?.harness ?? this.getRun(runId)?.harness ?? 'unknown'
      lookups.push({ key: runId, harness })
    }
    const seenSpanIds = new Set<string>()
    const records: CanonicalRecord[] = []
    const dropNotes: string[] = []
    for (const { key: sessionId, harness } of lookups) {
      const share = identities.shareOf(sessionId)
      // One predicate for both the share hit and the share-miss fallback —
      // recordsForShare scopes by harness and surfaces excluded-identity drops.
      const { records: shareRecords, dropNote } = this.recordsForShare(
        share?.key ?? sessionId,
        share?.harness ?? harness,
      )
      if (dropNote !== undefined) dropNotes.push(dropNote)
      const deduped = dedupeTwinTurns(shareRecords, share?.key ?? sessionId)
      for (const record of deduped) {
        if (record.op !== 'llm.invoke') continue
        if (seenSpanIds.has(record.spanId)) continue
        seenSpanIds.add(record.spanId)
        records.push(record)
      }
    }
    records.sort((a, b) => String(a.timestamp).localeCompare(String(b.timestamp)))
    return { records, dropNotes }
  }

  /**
   * Phase-aligned comparison of two stored runs (`docs/plans/2026-09-06-kyberdash-spine.md` § B4-api).
   * Missing ids return `null` so the route can 404; never sample data.
   */
  compareRuns(runAId: string, runBId: string, options?: RunComparisonOptions): ComparisonSummary | null {
    const runA = this.getRun(runAId)
    const runB = this.getRun(runBId)
    if (runA === undefined || runB === undefined) return null

    // Identities are memoized per store generation — the DISTINCT scan reruns
    // only after `records` changes, not on every compare request.
    const identities = this.sessionIdentities()
    const loadedA = this.recordsForRun(runA.runId, identities)
    const loadedB = this.recordsForRun(runB.runId, identities)
    const summary = compareStoredRuns(
      {
        runId: runA.runId,
        harness: runA.harness,
        ...(runA.label ? { label: runA.label } : {}),
        ...(runA.workingDirectory !== undefined ? { workingDirectory: runA.workingDirectory } : {}),
        ...(runA.outcome !== undefined ? { outcome: runA.outcome } : {}),
        turns: loadedA.records,
      },
      {
        runId: runB.runId,
        harness: runB.harness,
        ...(runB.label ? { label: runB.label } : {}),
        ...(runB.workingDirectory !== undefined ? { workingDirectory: runB.workingDirectory } : {}),
        ...(runB.outcome !== undefined ? { outcome: runB.outcome } : {}),
        turns: loadedB.records,
      },
      options,
    )

    // Tell the person staring at an empty side why its records were dropped,
    // not just the log file (only when the side resolved no turns at all).
    const withDropNotes = (side: ComparisonSummary['runA'], notes: string[]): ComparisonSummary['runA'] =>
      side.availability === 'unavailable' && side.turnCount === undefined && notes.length > 0
        ? { ...side, metricsReason: `${side.metricsReason} (${notes.join('; ')})` }
        : side

    return {
      ...summary,
      runA: withDropNotes(summary.runA, loadedA.dropNotes),
      runB: withDropNotes(summary.runB, loadedB.dropNotes),
      pairs: summary.pairs.map((pair) => {
        const stripRaw = (turn: (typeof pair)['runATurn']) => {
          if (turn === null) return null
          const { raw: _raw, ...rest } = turn
          return rest
        }
        return {
          ...pair,
          runATurn: stripRaw(pair.runATurn),
          runBTurn: stripRaw(pair.runBTurn),
        }
      }),
    }
  }

  /**
   * Return cross-harness comparison matrix across all active harnesses.
   */
  private canonicalRecords(): CanonicalRecord[] {
    if (this.store) return this.store.listAll()
    const db = this.getDb()
    if (!this.hasTable(db, 'records')) return []
    try {
      const rows = db!
        .prepare('SELECT * FROM records ORDER BY timestamp')
        .all() as unknown as import('../canon/store.js').RecordRow[]
      return rows.map(toRecord)
    } catch {
      return []
    }
  }

  /**
   * Return cross-harness comparison matrix derived only from canonical records.
   */
  getComparisonTable(): ComparisonTableResult {
    const canonicalRecords = this.canonicalRecords()
    if (canonicalRecords.length === 0) {
      return { harnesses: [], rows: [], problems: [] }
    }

    const byHarness = new Map<string, CanonicalRecord[]>()
    for (const record of canonicalRecords) {
      const records = byHarness.get(record.harness) ?? []
      records.push(record)
      byHarness.set(record.harness, records)
    }

    const preferredOrder = ['copilot', 'gemini', 'pi']
    const availableHarnesses = [...byHarness.keys()]
    const harnesses = [
      ...preferredOrder.filter((harness) => byHarness.has(harness)),
      ...availableHarnesses
        .filter((harness) => !preferredOrder.includes(harness))
        .sort(),
    ]
    const table = compareHarnesses(
      harnesses.map((harness) => byHarness.get(harness) ?? []),
      harnesses,
    )
    return {
      harnesses: table.harnesses,
      problems: table.problems,
      rows: table.rows.map((row) => ({
        ...row,
        cells: Object.fromEntries(
          Object.entries(row.cells).map(([harness, cell]) => [
            harness,
            {
              ...cell,
              availability: typeof cell.availability === 'object' ? cell.availability.availability : cell.availability,
            },
          ]),
        ),
      })),
    }
  }

  getQuarantine(limit = 200, offset = 0): QuarantineRow[] {
    const db = this.getDb()
    if (!this.hasTable(db, 'quarantine')) {
      return []
    }

    const safeLimit = typeof limit === 'number' && Number.isFinite(limit) ? (limit <= 0 ? -1 : Math.floor(limit)) : 200
    const safeOffset = typeof offset === 'number' && Number.isFinite(offset) && offset >= 0 ? Math.floor(offset) : 0

    try {
      const cols = new Set(
        (db!.prepare("PRAGMA table_info('quarantine')").all() as Array<{ name: string }>).map((c) => c.name),
      )

      const selectCols = ['span_id', 'namespaces', 'reason']
      selectCols.push(cols.has('source') ? 'source' : 'NULL AS source')
      selectCols.push(cols.has('name') ? 'name' : 'NULL AS name')
      selectCols.push(cols.has('seen_at') ? 'seen_at' : 'NULL AS seen_at')
      selectCols.push(cols.has('timestamp') ? 'timestamp' : 'NULL AS timestamp')

      const orderCol = cols.has('timestamp') && cols.has('seen_at')
        ? 'COALESCE(timestamp, seen_at)'
        : cols.has('timestamp')
          ? 'timestamp'
          : cols.has('seen_at')
            ? 'seen_at'
            : 'span_id'

      const sql = `SELECT ${selectCols.join(', ')} FROM quarantine ORDER BY ${orderCol} DESC, span_id DESC LIMIT ? OFFSET ?`

      const rows = db!.prepare(sql).all(safeLimit, safeOffset) as unknown as QuarantineDbRow[]
      const results: QuarantineRow[] = []
      for (const r of rows) {
        results.push({
          span_id: r.span_id,
          source: r.source ?? null,
          name: r.name ?? null,
          namespaces: r.namespaces ?? null,
          reason: r.reason ?? null,
          seen_at: r.seen_at ?? r.timestamp ?? null,
          timestamp: r.timestamp ?? (typeof r.seen_at === 'number' ? new Date(r.seen_at * 1000).toISOString() : (r.seen_at ? String(r.seen_at) : null)),
        })
      }
      return results
    } catch (err) {
      console.warn('[KyberBridge] Failed querying quarantine from canon.db:', err)
      return []
    }
  }

  /** Count every quarantine row without applying the inspector's default page limit. */
  getQuarantineCount(): number {
    if (this.store) {
      return this.store.countQuarantine()
    }
    const db = this.getDb()
    if (!this.hasTable(db, 'quarantine')) return 0
    const row = db!.prepare('SELECT COUNT(*) AS n FROM quarantine').get() as
      | { n?: number }
      | undefined
    return Number(row?.n) || 0
  }

  /**
   * Return recorded validation errors, token reconciliation mismatches, and anomalies.
   * Reads canonical diagnostics, deduplicated by the store's problem identity.
   */
  getProblems(limit = 200, offset = 0): ProblemRow[] {
    const db = this.getDb()
    const canonTable = this.hasTable(db, 'problems')
      ? 'problems'
      : this.hasTable(db, 'problem')
        ? 'problem'
        : null

    if (!canonTable) {
      return []
    }

    const safeLimit = typeof limit === 'number' && Number.isFinite(limit) ? (limit <= 0 ? -1 : Math.floor(limit)) : 200
    const safeOffset = typeof offset === 'number' && Number.isFinite(offset) && offset >= 0 ? Math.floor(offset) : 0

    try {
      const cols = new Set(
        (db!.prepare(`PRAGMA table_info(${canonTable})`).all() as Array<{ name: string }>).map((c) => c.name),
      )

      const selectCols = ['id', 'severity', 'code', 'message']
      selectCols.push(cols.has('session_id') ? 'session_id' : 'NULL AS session_id')
      selectCols.push(cols.has('span_id') ? 'span_id' : 'NULL AS span_id')
      selectCols.push(cols.has('harness') ? 'harness' : 'NULL AS harness')
      selectCols.push(cols.has('at') ? 'at' : 'NULL AS at')
      selectCols.push(cols.has('timestamp') ? 'timestamp' : 'NULL AS timestamp')

      const sql = `SELECT ${selectCols.join(', ')} FROM ${canonTable} ORDER BY id DESC LIMIT ? OFFSET ?`

      const rows = db!.prepare(sql).all(safeLimit, safeOffset) as unknown as ProblemDbRow[]
      const results: ProblemRow[] = []
      for (const r of rows) {
        results.push({
          id: r.id,
          session_id: r.session_id ?? null,
          span_id: r.span_id ?? null,
          severity: r.severity,
          code: r.code,
          message: r.message,
          at: r.at ?? r.timestamp ?? null,
          timestamp: r.timestamp ?? (typeof r.at === 'number' ? new Date(r.at * 1000).toISOString() : (r.at ? String(r.at) : null)),
          harness: r.harness ?? null,
        })
      }
      return results
    } catch (err) {
      console.warn('[KyberBridge] Failed querying problems from canon.db:', err)
      return []
    }
  }

  /** Count every problem row without applying the inspector's default page limit. */
  getProblemCount(): number {
    if (this.store) {
      return this.store.countProblems()
    }
    const db = this.getDb()
    const table = this.hasTable(db, 'problems')
      ? 'problems'
      : this.hasTable(db, 'problem')
        ? 'problem'
        : null
    if (table === null) return 0
    const row = db!.prepare(`SELECT COUNT(*) AS n FROM ${table}`).get() as
      | { n?: number }
      | undefined
    return Number(row?.n) || 0
  }

  /**
   * Refresh facts for the shared report. Each status is queried independently so
   * a later failure does not hide the last successful refresh.
   */
  getRefreshState(): RefreshState {
    const db = this.getDb()
    const latest = (status: 'success' | 'failure' | 'running'): {
      startedAt: string
      completedAt: string | null
      pid: number
      summary: string | null
      historyWeeks?: number | null
    } | undefined => {
      try {
        if (this.store) return this.store.latestRefreshRun(status)
        if (!this.hasTable(db, 'refresh_run')) return undefined
        // T1's `history_weeks` column: old databases predate migration 14→15
        // and have no such column — they fall back to the column-less select
        // and read as null (unknown), never 0.
        let row:
          | {
              started_at: string
              completed_at: string | null
              pid: number
              summary: string | null
              history_weeks?: unknown
            }
          | undefined
        try {
          row = db!
            .prepare(
              'SELECT started_at, completed_at, pid, summary, history_weeks FROM refresh_run WHERE status = ? ORDER BY started_at DESC LIMIT 1',
            )
            .get(status) as
            | {
                started_at: string
                completed_at: string | null
                pid: number
                summary: string | null
                history_weeks?: unknown
              }
            | undefined
        } catch {
          row = db!
            .prepare(
              'SELECT started_at, completed_at, pid, summary FROM refresh_run WHERE status = ? ORDER BY started_at DESC LIMIT 1',
            )
            .get(status) as
            | {
                started_at: string
                completed_at: string | null
                pid: number
                summary: string | null
              }
            | undefined
        }
        if (row === undefined) return undefined
        if (status === 'running' && !refreshProcessIsAlive(Number(row.pid))) return undefined
        return {
          startedAt: row.started_at,
          completedAt: row.completed_at,
          pid: Number(row.pid),
          summary: row.summary,
          // A non-numeric column value reads as unknown (never NaN): the
          // shared normalizer holds for both halves of the seam.
          historyWeeks: normalizeHistoryWeeks('history_weeks' in row ? row.history_weeks : null),
        }
      } catch {
        return undefined
      }
    }

    const success = latest('success')
    const failure = latest('failure')
    const running = latest('running')
    const historyWeeks = success?.historyWeeks ?? null
    const { coveredFrom, coveredThrough } = refreshWindowBounds(success?.startedAt, historyWeeks)
    return {
      lastSuccessAt: success?.completedAt ?? success?.startedAt ?? null,
      lastFailure:
        failure === undefined
          ? null
          : { at: failure.completedAt ?? failure.startedAt, summary: failure.summary ?? 'refresh failed' },
      inProgress: running === undefined ? null : { pid: running.pid, since: running.startedAt },
      historyWeeks,
      coveredFrom,
      coveredThrough,
    }
  }

  /**
   * Cost contributions for the selected sessions. The query reads only the
   * session key and cost block; raw span payloads are never decompressed.
   */
  getSessionCostContributions(sessionIds: readonly string[]): SessionCostContribution[] {
    const uniqueIds = [...new Set(sessionIds)].filter((id) => id.length > 0)
    if (uniqueIds.length === 0) return []
    if (this.store) {
      return this.store.costContributionsForSessions(uniqueIds)
    }
    const db = this.getDb()
    if (!this.hasTable(db, 'records')) {
      throw new Error('canonical records table is unavailable for cost lookup')
    }

    const contributions: SessionCostContribution[] = []
    const chunkSize = 900
    for (let offset = 0; offset < uniqueIds.length; offset += chunkSize) {
      const chunk = uniqueIds.slice(offset, offset + chunkSize)
      const placeholders = chunk.map(() => '?').join(', ')
      const rows = db!
        .prepare(
          `SELECT COALESCE(session_id, trace_id) AS session_key, cost_json
           FROM records WHERE COALESCE(session_id, trace_id) IN (${placeholders})`,
        )
        .all(...chunk) as Array<{ session_key: unknown; cost_json: unknown }>
      for (const row of rows) {
        if (typeof row.session_key !== 'string') continue
        let cost: unknown
        try {
          cost = JSON.parse(String(row.cost_json))
        } catch {
          continue
        }
        if (typeof cost !== 'object' || cost === null) continue
        const block = cost as { basis?: unknown; status?: unknown; value?: unknown }
        contributions.push({
          sessionId: row.session_key,
          basis: String(block.basis ?? 'unknown'),
          status: String(block.status ?? 'no_rate'),
          ...(typeof block.value === 'number' && Number.isFinite(block.value)
            ? { value: block.value }
            : {}),
        })
      }
    }
    return contributions
  }

  /**
   * List ranked findings optionally filtered by runId or sessionId.
   */
  listFindings(options?: { runId?: string; sessionId?: string; detector?: string; harness?: string; limit?: number; offset?: number }): Finding[] {
    if (this.store) {
      // Paging lives here, not in the store (review): slice the narrowed
      // set so `limit` keeps the contract it always had on this method.
      const findings = this.store.listFindings(options?.runId, options?.sessionId, {
        ...(options?.detector !== undefined ? { detector: options.detector } : {}),
        ...(options?.harness !== undefined ? { harness: options.harness } : {}),
      })
      const offset = validPageNumber(options?.offset) ?? 0
      const limit = validPageNumber(options?.limit)
      if (offset > 0 || limit !== undefined) {
        return findings.slice(offset, limit === undefined ? undefined : offset + limit)
      }
      return findings
    }

    const db = this.getDb()
    if (this.hasTable(db, 'finding')) {
      try {
        let sql = 'SELECT * FROM finding'
        const params: unknown[] = []
        const conds: string[] = []
        if (options?.runId) {
          conds.push('run_id = ?')
          params.push(options.runId)
        }
        if (options?.sessionId) {
          conds.push('session_id = ?')
          params.push(options.sessionId)
        }
        if (options?.detector) {
          conds.push('detector_id = ?')
          params.push(options.detector)
        }
        if (conds.length > 0) {
          sql += ' WHERE ' + conds.join(' AND ')
        }
        sql += ' ORDER BY rank_score DESC, id ASC'
        const rows = db!.prepare(sql).all(...(params as (string | number)[])) as unknown as FindingDbRow[]
        // `harness` rides in the payload JSON and filters after the
        // round-trip (same rule as `CanonStore.listFindings`); paging slices
        // the narrowed set so `total` stays comparable across paths.
        let findings = rows.map(toFinding)
        if (options?.harness) {
          // Same fold rule as `CanonStore.listFindings` (review): legacy
          // front-end names answer under their folded owner.
          const want = normalizeHarnessName(options.harness)
          findings = findings.filter((finding) => {
            const have = (finding as { harness?: unknown }).harness
            return typeof have === 'string' && normalizeHarnessName(have) === want
          })
        }
        const offset = typeof options?.offset === 'number' && options.offset > 0 ? Math.floor(options.offset) : 0
        if (offset > 0 || (typeof options?.limit === 'number' && options.limit > 0)) {
          const limit = typeof options?.limit === 'number' && options.limit > 0 ? Math.floor(options.limit) : undefined
          findings = findings.slice(offset, limit === undefined ? undefined : offset + limit)
        }
        return findings
      } catch (err) {
        console.warn('[KyberBridge] Failed querying findings from canon.db:', err)
        return []
      }
    }

    return []
  }

  /**
   * Paged findings envelope for the workspace view (issue #191): the
   * narrowed `findings` slice plus `total` and per-detector counts over the
   * narrowed set ignoring paging, so the Context Doctor can show every
   * finding and `unknownWindowSessions` keeps a suppressed-default list
   * from reading as "all clear". Additive: `findings` rows are unchanged.
   */
  listFindingsPage(options?: {
    runId?: string
    sessionId?: string
    detector?: string
    harness?: string
    limit?: number
    offset?: number
  }): FindingsPage {
    // One findings-table read per request (review): the detector split
    // happens in memory over the same rows, so `total` and `detectorCounts`
    // can never disagree about what the table holds.
    const base = this.listFindings({
      ...(options?.runId !== undefined ? { runId: options.runId } : {}),
      ...(options?.sessionId !== undefined ? { sessionId: options.sessionId } : {}),
      ...(options?.harness !== undefined ? { harness: options.harness } : {}),
    })
    const narrowed =
      options?.detector === undefined ? base : base.filter((finding) => finding.detectorId === options.detector)
    // Per-detector counts stay scoped to run/session/harness but never to
    // the detector being browsed (review): narrowing the list must not
    // evaporate the chips that narrow it. `total` below stays narrowed.
    const detectorCounts: Record<string, number> = {}
    for (const id of DETECTOR_IDS) detectorCounts[id] = 0
    for (const finding of base) {
      detectorCounts[finding.detectorId] = (detectorCounts[finding.detectorId] ?? 0) + 1
    }
    const offset = typeof options?.offset === 'number' && Number.isFinite(options.offset) && options.offset > 0
      ? Math.floor(options.offset)
      : 0
    const limit = typeof options?.limit === 'number' && Number.isFinite(options.limit) && options.limit > 0
      ? Math.floor(options.limit)
      : undefined
    return {
      findings: narrowed.slice(offset, limit === undefined ? undefined : offset + limit),
      total: narrowed.length,
      ...(limit === undefined ? {} : { limit }),
      offset,
      detectorCounts,
      unknownWindowSessions: this.countUnknownWindowSessions({
        ...(options?.harness !== undefined ? { harness: options.harness } : {}),
        ...(options?.runId !== undefined ? { runId: options.runId } : {}),
        ...(options?.sessionId !== undefined ? { sessionId: options.sessionId } : {}),
      }),
    }
  }

  /** Canonical harness of one stored session over the raw handle (review).
   * Absent reads as undefined; a failed read reads as null — never a clean
   * zero that would pose as "no suppressed sessions" (review). */
  private sessionHarnessRaw(sessionId: string): string | null | undefined {
    const db = this.getDb()
    if (!this.hasTable(db, 'session')) return undefined
    try {
      const row = db!
        .prepare('SELECT harness FROM session WHERE session_id = ?')
        .get(sessionId) as unknown as { harness: string } | undefined
      return row?.harness
    } catch {
      return null
    }
  }

  /**
   * Sessions whose context window no source reported, scoped the way the
   * findings are: the selected run's sessions, the selected session, or the
   * harness/workspace scope (issue #191, condition 3). An unscoped count on
   * a scoped query would warn about suppressions the listed findings never
   * underwent.
   */
  countUnknownWindowSessions(scope?: { harness?: string; runId?: string; sessionId?: string }): number {
    const harness = scope?.harness
    const runId = scope?.runId
    const sessionId = scope?.sessionId
    if (sessionId !== undefined && sessionId !== '') {
      // A harness that does not own the session scopes the count to zero,
      // matching the narrowed findings (review S3a). Absent and failed look
      // different: a missing row is 0, but an unreadable row must not answer
      // at all — the payload read below still knows the window (review).
      if (harness !== undefined && harness !== '') {
        let owner: string | null | undefined
        try {
          owner = this.store ? this.store.sessionHarness(sessionId) : this.sessionHarnessRaw(sessionId)
        } catch {
          owner = null
        }
        if (owner === null) {
          const payload = this.getSessionPayload<{ context?: { contextLimitSource?: string } }>(sessionId)
          return payload?.context?.contextLimitSource === 'default' ||
            payload?.context?.contextLimitSource === 'absent'
            ? 1
            : 0
        }
        if (owner === undefined || normalizeHarnessName(owner) !== normalizeHarnessName(harness)) return 0
      }
      const payload = this.getSessionPayload<{ context?: { contextLimitSource?: string } }>(sessionId)
      return payload?.context?.contextLimitSource === 'default' ||
        payload?.context?.contextLimitSource === 'absent'
        ? 1
        : 0
    }
    if (runId !== undefined && runId !== '') {
      // Same fold rule as everywhere else (review): a run scoped to a legacy
      // front-end name still matches its folded owner's executions.
      const wantHarness = harness !== undefined && harness !== '' ? normalizeHarnessName(harness) : undefined
      const ids = [
        ...new Set(
          this.listExecutions(runId)
            .filter(
              (execution) =>
                wantHarness === undefined || normalizeHarnessName(execution.harness) === wantHarness,
            )
            .map((execution) => execution.sessionId ?? execution.executionId)
            .filter((id) => id.length > 0),
        ),
      ]
      return ids.filter(
        (id) =>
          ['default', 'absent'].includes(
            this.getSessionPayload<{ context?: { contextLimitSource?: string } }>(id)?.context
              ?.contextLimitSource ?? '',
          ),
      ).length
    }
    // Harness and workspace scopes read the persisted rollups (review): the
    // count was derived at build time, so each findings request does not
    // JSON-parse every session payload. A rollup sum is exact only when the
    // rollups still cover every session (review M2): coverage is verified
    // against narrow-column counts, and anything uncovered falls back to
    // the direct session scan rather than wearing a partial sum as a total.
    if (this.store) {
      if (harness !== undefined && harness !== '') {
        const canonical = normalizeHarnessName(harness)
        const rollup = this.store.getHarnessRollup(canonical)
        const covered = (rollup?.payload as { windowSessionsTotal?: unknown } | undefined)?.windowSessionsTotal
        if (typeof covered === 'number' && covered === (this.store.countSessionsByHarness()[canonical] ?? -1)) {
          const count = (rollup?.payload as { unknownWindowSessions?: unknown } | undefined)?.unknownWindowSessions
          if (typeof count === 'number') return count
        }
      } else {
        const rollups = this.store.listHarnessRollups()
        const byHarness = this.store.countSessionsByHarness()
        const covered = rollups.length > 0 && rollups.every((rollup) => {
          const total = (rollup.payload as { windowSessionsTotal?: unknown } | undefined)?.windowSessionsTotal
          return typeof total === 'number' && total === (byHarness[rollup.harness] ?? -1)
        })
        // covered also requires no sessions outside rollup harnesses.
        if (covered && Object.keys(byHarness).every((h) => rollups.some((rollup) => rollup.harness === h))) {
          return rollups.reduce((sum, rollup) => {
            const count = (rollup.payload as { unknownWindowSessions?: unknown } | undefined)?.unknownWindowSessions
            return sum + (typeof count === 'number' ? count : 0)
          }, 0)
        }
      }
      return this.store.countUnknownWindowSessions(harness)
    }
    const db = this.getDb()
    // Built rollups first (review): one small table, no session-blob scan —
    // but only when coverage verifies (review M2), else the session scan.
    if (this.hasTable(db, 'harness_rollup') && this.hasTable(db, 'session')) {
      try {
        const payloadOf = (payload: unknown): { unknown?: number; total?: number } => {
          if (typeof payload !== 'string') return {}
          try {
            const parsed = JSON.parse(payload) as { unknownWindowSessions?: unknown; windowSessionsTotal?: unknown }
            return {
              ...(typeof parsed.unknownWindowSessions === 'number' ? { unknown: parsed.unknownWindowSessions } : {}),
              ...(typeof parsed.windowSessionsTotal === 'number' ? { total: parsed.windowSessionsTotal } : {}),
            }
          } catch {
            return {}
          }
        }
        const grouped = db!
          .prepare('SELECT harness, COUNT(*) AS n FROM session GROUP BY harness')
          .all() as unknown as { harness: string; n: number }[]
        const actual: Record<string, number> = {}
        for (const row of grouped) actual[row.harness] = row.n
        if (harness !== undefined && harness !== '') {
          const canonical = normalizeHarnessName(harness)
          const row = db!
            .prepare('SELECT payload FROM harness_rollup WHERE harness = ?')
            .get(canonical) as unknown as { payload: unknown } | undefined
          const { unknown, total } = payloadOf(row?.payload)
          if (unknown !== undefined && total === (actual[canonical] ?? -1)) return unknown
        } else {
          const rows = db!.prepare('SELECT harness, payload FROM harness_rollup').all() as unknown as {
            harness: string
            payload: unknown
          }[]
          const covered =
            rows.length > 0 &&
            rows.every((row) => payloadOf(row.payload).total === (actual[row.harness] ?? -1)) &&
            Object.keys(actual).every((h) => rows.some((row) => row.harness === h))
          if (covered) {
            return rows.reduce((sum, row) => sum + (payloadOf(row.payload).unknown ?? 0), 0)
          }
        }
      } catch (err) {
        console.warn('[KyberBridge] Failed reading unknown-window counts from rollups:', err)
      }
    }
    if (!this.hasTable(db, 'session')) return 0
    try {
      const conds = [`json_extract(payload, '$.context.contextLimitSource') IN ('default', 'absent')`]
      const params: (string | number)[] = []
      if (harness !== undefined && harness !== '') {
        conds.push('LOWER(harness) = LOWER(?)')
        params.push(normalizeHarnessName(harness))
      }
      const row = db!
        .prepare(`SELECT COUNT(*) AS n FROM session WHERE ${conds.join(' AND ')}`)
        .get(...params) as unknown as { n: number } | undefined
      return row?.n ?? 0
    } catch (err) {
      console.warn('[KyberBridge] Failed counting unknown-window sessions:', err)
      return 0
    }
  }

  /**
   * Fetch one finding by id.
   */
  getFinding(id: string): Finding | undefined {
    if (this.store) {
      return this.store.getFinding(id)
    }

    const db = this.getDb()
    if (this.hasTable(db, 'finding')) {
      try {
        const row = db!
          .prepare('SELECT * FROM finding WHERE id = ?')
          .get(id) as unknown as FindingDbRow | undefined
        return row === undefined ? undefined : toFinding(row)
      } catch (err) {
        console.warn('[KyberBridge] Failed querying finding from canon.db:', err)
        return undefined
      }
    }

    return undefined
  }

  /**
   * List predictions, optionally filtered by runId, findingId, or scoredOnly.
   */
  listPredictions(options?: {
    runId?: string
    findingId?: string
    scoredOnly?: boolean
    limit?: number
  }): PredictionRecord[] {
    if (this.store) {
      return this.store.listPredictions(options)
    }

    const db = this.getDb()
    if (this.hasTable(db, 'prediction')) {
      try {
        let sql = 'SELECT * FROM prediction'
        const conds: string[] = []
        const params: (string | number)[] = []

        if (options?.runId) {
          conds.push('run_id = ?')
          params.push(options.runId)
        }
        if (options?.findingId) {
          conds.push('finding_id = ?')
          params.push(options.findingId)
        }
        if (options?.scoredOnly) {
          conds.push('comparison_run_id IS NOT NULL')
        }
        if (conds.length > 0) {
          sql += ' WHERE ' + conds.join(' AND ')
        }
        sql += ' ORDER BY created_at DESC, id ASC'
        if (typeof options?.limit === 'number' && options.limit > 0) {
          sql += ' LIMIT ?'
          params.push(Math.floor(options.limit))
        }

        const rows = db!.prepare(sql).all(...params) as unknown as PredictionDbRow[]
        return rows.map(toPrediction)
      } catch (err) {
        console.warn('[KyberBridge] Failed querying predictions from canon.db:', err)
        return []
      }
    }

    return []
  }

  /**
   * Fetch one prediction by id.
   */
  getPrediction(id: string): PredictionRecord | undefined {
    if (this.store) {
      return this.store.getPrediction(id)
    }

    const db = this.getDb()
    if (this.hasTable(db, 'prediction')) {
      try {
        const row = db!
          .prepare('SELECT * FROM prediction WHERE id = ?')
          .get(id) as unknown as PredictionDbRow | undefined
        return row === undefined ? undefined : toPrediction(row)
      } catch (err) {
        console.warn('[KyberBridge] Failed querying prediction from canon.db:', err)
        return undefined
      }
    }

    return undefined
  }

  /**
   * Record or update a prediction.
   */
  recordPrediction(prediction: PredictionRecord): PredictionRecord {
    if (this.store) {
      this.store.upsertPrediction(prediction)
      return prediction
    }

    const db = this.getDb()
    if (db) {
      const errorBar = prediction.errorBar ? JSON.stringify(prediction.errorBar) : null
      const payload = prediction.payload ? JSON.stringify(prediction.payload) : null
      const createdAt = prediction.createdAt || prediction.timestamp || new Date().toISOString()
      const id = prediction.id || `pred-${prediction.findingId}-${prediction.runId}`
      try {
        db
          .prepare(
            `INSERT OR REPLACE INTO prediction (
              id, finding_id, run_id, predicted_waste_tokens, confidence,
              confidence_tier, error_bar_json, created_at, comparison_run_id,
              observed_delta_tokens, calibration_score, payload
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
          )
          .run(
            id,
            prediction.findingId,
            prediction.runId,
            prediction.predictedWasteTokens,
            prediction.confidence,
            prediction.confidenceTier ?? null,
            errorBar,
            createdAt,
            prediction.comparisonRunId ?? null,
            prediction.observedDeltaTokens ?? null,
            prediction.calibrationScore ?? null,
            payload
          )
      } catch (err) {
        console.warn('[KyberBridge] Failed upserting prediction into canon.db:', err)
      }
    }

    return prediction
  }

  /**
   * Calculates calibration curve and summary metrics across predictions.
   */
  getCalibrationSummary(options?: { runId?: string }): CalibrationCurveResult {
    if (this.store) {
      return this.store.getCalibrationSummary(options)
    }

    const predictions = this.listPredictions(options)
    return calculateCalibrationCurve(predictions)
  }

  /** Alias for getCalibrationSummary to match route semantics. */
  getCalibrationCurve(options?: { runId?: string }): CalibrationCurveResult {
    return this.getCalibrationSummary(options)
  }

  /**
   * List all harness rollups in ascending harness name order.
   */
  listHarnessRollups(): HarnessRollupRow[] {
    if (this.store) {
      return this.store.listHarnessRollups()
    }
    const db = this.getDb()
    if (this.hasTable(db, 'harness_rollup')) {
      try {
        const rows = db!
          .prepare('SELECT * FROM harness_rollup ORDER BY harness ASC')
          .all() as unknown as HarnessRollupDbRow[]
        return rows.map(toHarnessRollupRow)
      } catch (err) {
        console.warn('[KyberBridge] Failed querying harness_rollup from canon.db:', err)
        return []
      }
    }
    return []
  }

  /**
   * Fetch one harness rollup by harness name.
   */
  getHarnessRollup(harness: string): HarnessRollupRow | undefined {
    if (this.store) {
      return this.store.getHarnessRollup(harness)
    }
    const db = this.getDb()
    if (this.hasTable(db, 'harness_rollup')) {
      try {
        const row = db!
          .prepare('SELECT * FROM harness_rollup WHERE harness = ?')
          .get(harness) as unknown as HarnessRollupDbRow | undefined
        return row === undefined ? undefined : toHarnessRollupRow(row)
      } catch (err) {
        console.warn('[KyberBridge] Failed querying harness_rollup from canon.db:', err)
        return undefined
      }
    }
    return undefined
  }

  /**
   * List runs, optionally narrowed to one harness; newest first.
   */
  listRuns(harnessId?: string): RunRow[] {
    if (this.store) {
      return this.store.listRuns(harnessId)
    }
    const db = this.getDb()
    if (this.hasTable(db, 'run')) {
      try {
        const rows = (
          harnessId === undefined
            ? db!.prepare('SELECT * FROM run ORDER BY started DESC').all()
            : db!.prepare('SELECT * FROM run WHERE harness = ? ORDER BY started DESC').all(harnessId)
        ) as unknown as RunDbRow[]
        return rows.map(toRunRow)
      } catch (err) {
        console.warn('[KyberBridge] Failed querying runs from canon.db:', err)
        return []
      }
    }
    return []
  }

  /**
   * Fetch one run by id; absent id gives undefined.
   */
  getRun(id: string): RunRow | undefined {
    if (this.store) {
      return this.store.getRun(id)
    }
    const db = this.getDb()
    if (this.hasTable(db, 'run')) {
      try {
        const row = db!
          .prepare('SELECT * FROM run WHERE run_id = ?')
          .get(id) as unknown as RunDbRow | undefined
        return row === undefined ? undefined : toRunRow(row)
      } catch (err) {
        console.warn('[KyberBridge] Failed querying run from canon.db:', err)
        return undefined
      }
    }
    return undefined
  }

  /**
   * List executions, optionally filtered by run id; ordered by started.
   */
  listExecutions(runId?: string): ExecutionRow[] {
    if (this.store) {
      return this.store.listExecutions(runId)
    }
    const db = this.getDb()
    if (this.hasTable(db, 'execution')) {
      try {
        const rows = (
          runId === undefined
            ? db!.prepare('SELECT * FROM execution ORDER BY started, execution_id').all()
            : db!.prepare('SELECT * FROM execution WHERE run_id = ? ORDER BY started, execution_id').all(runId)
        ) as unknown as ExecutionDbRow[]
        return rows.map(toExecutionRow)
      } catch (err) {
        console.warn('[KyberBridge] Failed querying executions from canon.db:', err)
        return []
      }
    }
    return []
  }

  /**
   * Return hierarchical execution tree for a given run id.
   */
  getExecutionTree(runId: string): ExecutionTreeNode[] {
    if (this.store) {
      return this.store.getExecutionTree(runId)
    }
    const executions = this.listExecutions(runId)
    if (executions.length === 0) return []
    const nodesById = new Map<string, ExecutionTreeNode>()
    for (const exec of executions) {
      nodesById.set(exec.executionId, { ...exec, children: [] })
    }
    const roots: ExecutionTreeNode[] = []
    for (const node of nodesById.values()) {
      if (node.parentExecutionId !== null && node.parentExecutionId !== undefined) {
        const parent = nodesById.get(node.parentExecutionId)
        if (parent !== undefined && parent !== node) {
          parent.children.push(node)
          continue
        }
      }
      roots.push(node)
    }
    return roots
  }

  /**
   * Summary figures for sessions, without parsing whole payloads. Store mode
   * reads the summary fields out of the stored JSON; direct-DB mode runs the
   * same projection. Absent figures stay absent — never 0. A priced cost is
   * carried as USD only when its block names USD (or predates currency, as
   * the legacy `usd` shape does); any other currency is omitted rather than
   * mislabelled (review follow-up: Copilot C4).
   */
  sessionSummaryFigures(sessionIds: readonly string[]): SessionSummaryFigures {
    const uniqueIds = [...new Set(sessionIds)].filter((id) => id.length > 0)
    const out: SessionSummaryFigures = new Map()
    if (uniqueIds.length === 0) return out

    type SummaryRow = {
      session_id: unknown
      turn_count: unknown
      total_input: unknown
      total_output: unknown
      cost_status: unknown
      cost_value: unknown
      cost_currency: unknown
      cost_usd: unknown
    }
    const readRows = (): SummaryRow[] => {
      if (this.store) {
        return this.store.sessionSummaryFigures(uniqueIds).map((row) => ({
          session_id: row.sessionId,
          turn_count: row.turnCount,
          total_input: row.totalInput,
          total_output: row.totalOutput,
          cost_status: row.costStatus,
          cost_value: row.costValue,
          cost_currency: row.costCurrency,
          cost_usd: row.costUsdLegacy,
        }))
      }
      const db = this.getDb()
      if (!this.hasTable(db, 'session')) return []
      const rows: SummaryRow[] = []
      const chunkSize = 900
      for (let offset = 0; offset < uniqueIds.length; offset += chunkSize) {
        const chunk = uniqueIds.slice(offset, offset + chunkSize)
        const placeholders = chunk.map(() => '?').join(', ')
        try {
          rows.push(
            ...(db!
              .prepare(
                `SELECT session_id,
                        json_extract(payload, '$.summary.turn_count') AS turn_count,
                        json_extract(payload, '$.summary.total_input') AS total_input,
                        json_extract(payload, '$.summary.total_output') AS total_output,
                        json_extract(payload, '$.summary.cost.status') AS cost_status,
                        json_extract(payload, '$.summary.cost.value') AS cost_value,
                        json_extract(payload, '$.summary.cost.currency') AS cost_currency,
                        json_extract(payload, '$.summary.cost.usd') AS cost_usd
                 FROM session WHERE session_id IN (${placeholders})`,
              )
              .all(...chunk) as SummaryRow[]),
          )
        } catch (err) {
          console.warn('[KyberBridge] Failed querying session summaries from canon.db:', err)
          return []
        }
      }
      return rows
    }

    const figure = (value: unknown): number | undefined =>
      typeof value === 'number' && Number.isFinite(value) ? value : undefined
    for (const row of readRows()) {
      if (typeof row.session_id !== 'string') continue
      const cost = pricedUsd(row.cost_status, row.cost_value, row.cost_currency, row.cost_usd)
      out.set(row.session_id, {
        ...(figure(row.turn_count) !== undefined ? { turnCount: figure(row.turn_count)! } : {}),
        ...(figure(row.total_input) !== undefined ? { totalInput: figure(row.total_input)! } : {}),
        ...(figure(row.total_output) !== undefined ? { totalOutput: figure(row.total_output)! } : {}),
        ...(cost !== undefined ? { costUsd: cost } : {}),
        // Re-review #2 (Kilo B): the status a bare figure cannot carry.
        ...(row.cost_status === 'partial' && cost !== undefined ? { costPartial: true as const } : {}),
      })
    }
    return out
  }

  /**
   * Measured run figures summed over the run's session summaries (issue #183).
   * Feeds the run detail `run` object; the runs list batches through
   * `sumSessionFigures` directly. `RunRow`'s stored shape is unchanged, so
   * figures are always live, never migrated.
   */
  runMeasuredFigures(runId: string): RunMeasuredFigures {
    const sessionIds = this.listExecutions(runId)
      .map((exec) => exec.sessionId)
      .filter((id): id is string => typeof id === 'string' && id.length > 0)
    return sumSessionFigures(this.sessionSummaryFigures(sessionIds), sessionIds)
  }

  /**
   * Session payloads behind executions, streamed one distinct session at a
   * time (review re-review on issue #183: Kilo 4, plus the open thread on
   * shared sessions — payloads can be hundreds of MB, so no consumer may
   * hold every session of the run at once, nor parse one session per
   * execution sharing it). Executions are grouped by session first: each
   * distinct payload parses once per pass, then yields one
   * (execution, payload) pair per group member so turns still stream per
   * execution. Each yielded payload is droppable as soon as the consumer
   * advances: callers must process it inline and never retain it past the
   * iteration.
   */
  *streamRunSessionPayloads(
    executions: readonly ExecutionRow[],
  ): Generator<{ execution: ExecutionRow; payload: SessionPayload & { context?: unknown } }> {
    const bySession = new Map<string, ExecutionRow[]>()
    for (const execution of executions) {
      const sessionId = execution.sessionId
      // An execution with no session carries no payload to stream; the
      // executions list itself (kept by callers) still sees it.
      if (typeof sessionId !== 'string' || sessionId.length === 0) continue
      const group = bySession.get(sessionId) ?? []
      group.push(execution)
      bySession.set(sessionId, group)
    }
    for (const [sessionId, group] of bySession) {
      const payload = this.getSessionPayload(sessionId)
      if (payload === null) continue
      for (const execution of group) {
        yield { execution, payload }
      }
    }
  }

  /**
   * Cost blocks for spans, keyed by span id. Store mode reads only the small
   * cost column; direct-DB mode runs the same projection. Parts and raw
   * payloads are never inflated for the turn table.
   */
  private readSpanCosts(spanIds: readonly string[]): Map<string, CostBlock> {
    const uniqueIds = [...new Set(spanIds)].filter((id) => id.length > 0)
    const out = new Map<string, CostBlock>()
    if (uniqueIds.length === 0) return out
    const ingest = (spanId: unknown, cost: unknown): void => {
      if (typeof spanId !== 'string') return
      if (typeof cost !== 'object' || cost === null) return
      out.set(spanId, cost as CostBlock)
    }
    if (this.store) {
      for (const row of this.store.spanCosts(uniqueIds)) ingest(row.spanId, row.cost)
      return out
    }
    const db = this.getDb()
    if (!this.hasTable(db, 'records')) return out
    const chunkSize = 900
    for (let offset = 0; offset < uniqueIds.length; offset += chunkSize) {
      const chunk = uniqueIds.slice(offset, offset + chunkSize)
      const placeholders = chunk.map(() => '?').join(', ')
      try {
        const rows = db!
          .prepare(`SELECT span_id, cost_json FROM records WHERE span_id IN (${placeholders})`)
          .all(...chunk) as Array<{ span_id: unknown; cost_json: unknown }>
        for (const row of rows) {
          let cost: unknown
          try {
            cost = JSON.parse(String(row.cost_json))
          } catch {
            continue
          }
          ingest(row.span_id, cost)
        }
      } catch (err) {
        console.warn('[KyberBridge] Failed querying span costs from canon.db:', err)
        return out
      }
    }
    return out
  }

  /**
   * Per-turn measured rows for a run, joined from the run's session payloads
   * (issue #183). Context pressure joins positionally over measured turns —
   * the engine's 1-based `TurnPressure.index` counts measured turns only, so
   * an index-equality join would land on a neighbor wherever an unmeasured
   * turn exists; a length mismatch omits every pressure rather than guessing.
   * `turnIndex` keeps the #184 transport convention: 0-based per execution.
   * Sessions stream one at a time (review re-review: Kilo 4): each payload —
   * and its span-cost query — is dropped before the next session loads, so
   * the peak stays one payload however large the run's sessions are.
   */
  getRunTurns(runId: string, executions?: readonly ExecutionRow[]): RunTurnRow[] {
    const rows: RunTurnRow[] = []
    const runExecutions = executions ?? this.listExecutions(runId)

    const number = (value: unknown): number | undefined =>
      typeof value === 'number' && Number.isFinite(value) ? value : undefined
    for (const { execution, payload } of this.streamRunSessionPayloads(runExecutions)) {
      const turns = (Array.isArray(payload.turns) ? payload.turns : []) as Array<
        Record<string, unknown>
      >
      const spanIds: string[] = []
      for (const turn of turns) {
        const spanId = turn.spanId
        if (typeof spanId === 'string' && spanId.length > 0) spanIds.push(spanId)
      }
      const costs = this.readSpanCosts(spanIds)
      // Measured turns in payload order; the engine's context turns are built
      // from exactly this subset in this order, so position j here is
      // `TurnPressure.index` j + 1 there. The position→slot map keeps the
      // join linear (review follow-up: Kilo K6, Copilot C6).
      const measuredPositions: number[] = []
      turns.forEach((turn, position) => {
        const input = number(turn.input)
        if (input !== undefined && input > 0) measuredPositions.push(position)
      })
      const measuredSlotByPosition = new Map(measuredPositions.map((p, slot) => [p, slot] as const))
      const rawContext = payload.context as
        | { measurable?: unknown; turns?: unknown }
        | undefined
      const contextTurns =
        rawContext?.measurable === true && Array.isArray(rawContext.turns)
          ? (rawContext.turns as Array<Record<string, unknown>>)
          : []
      const pressures =
        contextTurns.length === measuredPositions.length
          ? contextTurns.map((turn) => number(turn.pressure))
          : []
      turns.forEach((turn, position) => {
        const row: RunTurnRow = {
          turnIndex:
            typeof turn.index === 'number' &&
            Number.isInteger(turn.index) &&
            turn.index >= 0
              ? turn.index
              : position,
        }
        if (typeof execution.executionId === 'string') row.executionId = execution.executionId
        if (typeof execution.sessionId === 'string') row.sessionId = execution.sessionId
        if (typeof turn.model === 'string' && turn.model.length > 0) row.model = turn.model
        const input = number(turn.input)
        const output = number(turn.output)
        if (input !== undefined) row.inputTokens = input
        if (output !== undefined) row.outputTokens = output
        if (input !== undefined && output !== undefined) row.tokens = input + output
        // Review follow-up (Copilot C2): a stored cache-read zero is absence,
        // not a measured 0%, wherever the harness exports no read counter.
        const cacheRead = number(turn.cache_read)
        if (
          cacheRead !== undefined &&
          input !== undefined &&
          input > 0 &&
          harnessExportsCacheCounter(execution.harness, 'read')
        ) {
          row.cacheHitRatio = cacheRead / input
        }
        const measuredSlot = measuredSlotByPosition.get(position) ?? -1
        if (measuredSlot !== -1 && pressures.length === measuredPositions.length) {
          const pressure = pressures[measuredSlot]
          if (pressure !== undefined) row.contextPressure = pressure
        }
        if (typeof turn.timestamp === 'string') row.timestamp = turn.timestamp
        const spanId = turn.spanId
        if (typeof spanId === 'string') {
          const block = costs.get(spanId)
          // Review follow-up (Copilot C4): a priced figure is USD only when
          // its block says so — never serve euros under a dollar formatter.
          const value =
            block !== undefined
              ? pricedUsd(block.status, block.value, block.currency, undefined)
              : undefined
          if (value !== undefined) row.costUsd = value
          // Re-review #2 (Kilo B): a partial block's figure is real but
          // incomplete — the row says so beside the figure.
          if (block?.status === 'partial' && value !== undefined) row.costStatus = 'partial'
        }
        rows.push(row)
      })
    }
    return rows
  }

  /**
   * Run-scoped scorecard reusing the harness rollup machinery over the run's
   * own sessions (issue #183, Q2). Reasons are scoped to the run, so a run
   * whose sessions exported cache counters can never inherit the
   * harness-level claim that they did not. Absent sessions mean no scorecard.
   * Sessions stream through the digest one at a time (review re-review:
   * Kilo 4) — `digestSessionPayloads` pulls each payload from the generator
   * and releases it before the next loads, so the peak stays one payload.
   * Executions and summaries may be preloaded; otherwise they are loaded here.
   */
  getRunScorecard(
    runId: string,
    preload: {
      executions?: readonly ExecutionRow[]
      summaries?: SessionSummaryFigures
    } = {},
  ): Scorecard | undefined {
    const executions = preload.executions ?? this.listExecutions(runId)
    const digest = digestSessionPayloads(
      asadPayloads(dedupedRunSessions(this.streamRunSessionPayloads(executions))),
    )
    if (digest.count === 0) return undefined
    const run = this.getRun(runId)
    const harness = run?.harness ?? 'unknown'
    const summaries =
      preload.summaries ??
      this.sessionSummaryFigures(
        executions
          .map((exec) => exec.sessionId)
          .filter((id): id is string => typeof id === 'string' && id.length > 0),
      )
    const rollup = assembleRollup(harness, digest, {
      sessionCount: digest.count,
      runCount: 1,
      executionCount: executions.length,
      executions: executions.map((exec) => ({
        sessionId: exec.sessionId,
        isChild:
          !exec.isRoot ||
          (exec.parentExecutionId !== null && exec.parentExecutionId !== undefined),
      })),
      tokenTotals: (sessionId) => {
        const figures = summaries.get(sessionId)
        if (figures === undefined) return undefined
        // Review re-review (Kilo 3): buildSessionRow always writes
        // total_output as a number, so "both missing" never fires for built
        // sessions. Without a measured input the session's token share is
        // unknown — absence, not a measured zero input beside output.
        if (figures.totalInput === undefined) return undefined
        return {
          input: figures.totalInput,
          output: figures.totalOutput ?? 0,
        }
      },
      // Review follow-up (Kilo K4): a session-count ratio is not an overhead
      // ratio — with no measured token totals the run's delegation overhead
      // is unobservable, not 0%.
      allowCountFallback: false,
      scope: { kind: 'run', runId },
    })
    return buildScorecard(rollup)
  }

  /**
   * Return metadata: rate definitions, tokenizer info, span/quarantine counts, and harness presence.
   */
  getMeta(): KyberMetaResult {
    const db = this.getDb()
    let spanCount = 0
    let quarantinedCount = 0

    // Count canonical spans.
    if (this.hasTable(db, 'records')) {
      try {
        const r = db!.prepare('SELECT COUNT(*) as c FROM records').get() as { c: number }
        spanCount += Number(r.c) || 0
      } catch {}
    }

    // Count canonical quarantine entries.
    if (this.hasTable(db, 'quarantine')) {
      try {
        const r = db!.prepare('SELECT COUNT(*) as c FROM quarantine').get() as {
          c: number
        }
        quarantinedCount += Number(r.c) || 0
      } catch {}
    }

    // Rates info
    const copilotTable: KyberMetaResult['rates']['tables'][number] = {
      id: 'copilot_credits',
      source: COPILOT_CREDITS_SOURCE.url,
      retrieved: COPILOT_CREDITS_SOURCE.retrieved,
      credit_usd: COPILOT_CREDITS_SOURCE.credit_usd,
      applies_to: ['copilot'],
      note: "GitHub's published models-and-pricing table (USD per 1M tokens x100 = credits per 1M).",
    }
    const publishedTable: KyberMetaResult['rates']['tables'][number] = {
      id: 'published',
      source: 'https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json',
      retrieved: '2026-09-30',
      applies_to: ['claude-code', 'codex'],
      note: 'Bundled LiteLLM pricing snapshot (USD per token).',
    }
    let ratesInfo: KyberMetaResult['rates'] = {
      credit_usd: copilotTable.credit_usd ?? 0.01,
      source: copilotTable.source,
      retrieved: copilotTable.retrieved,
      note: 'Flat fields describe the Copilot credits table only; see tables for the published (LiteLLM) and copilot_credits tables.',
      tables: [publishedTable, copilotTable],
    }
    // A ratesPath override replaces the flat Copilot-credits fields and the copilot_credits entry
    // (they describe the same table); the published LiteLLM entry is never overridden.
    if (this.ratesPath !== undefined && existsSync(this.ratesPath)) {
      try {
        const raw = readFileSync(this.ratesPath, 'utf8')
        const parsed = JSON.parse(raw) as Partial<KyberMetaResult['rates']>
        ratesInfo = {
          credit_usd: parsed.credit_usd ?? ratesInfo.credit_usd,
          source: parsed.source ?? ratesInfo.source,
          retrieved: parsed.retrieved ?? ratesInfo.retrieved,
          note: parsed.note ?? ratesInfo.note,
          tables: [
            publishedTable,
            {
              ...copilotTable,
              credit_usd: parsed.credit_usd ?? copilotTable.credit_usd,
              source: parsed.source ?? copilotTable.source,
              retrieved: parsed.retrieved ?? copilotTable.retrieved,
            },
          ],
        }
      } catch {}
    }

    // Harnesses presence from the canonical meta table.
    const perHarness: Record<string, unknown> = {}
    if (this.hasTable(db, 'meta')) {
      try {
        const rows = db!
          .prepare("SELECT key, value FROM meta WHERE key LIKE 'meta:%'")
          .all() as Array<{ key: string; value: string }>
        for (const row of rows) {
          const h = row.key.slice('meta:'.length)
          try {
            perHarness[h] = JSON.parse(row.value) as unknown
          } catch {
            perHarness[h] = row.value
          }
        }
      } catch {}
    }

    // Ingest sources from the canonical ingest log.
    const sources: Array<{ origin: string; seen: number; new: number }> = []
    if (this.hasTable(db, 'ingest_log')) {
      try {
        const rows = db!
          .prepare(
            'SELECT source as origin, SUM(count) as seen, SUM(count) as new ' +
              'FROM ingest_log GROUP BY source ORDER BY MIN(id)'
          )
          .all() as unknown as IngestLogRow[]
        for (const r of rows) {
          sources.push({
            origin: r.origin,
            seen: Number(r.seen) || 0,
            new: Number(r.new) || 0,
          })
        }
      } catch {}
    }

    return {
      span_count: spanCount,
      quarantined: quarantinedCount,
      tokenizer: {
        // Report what actually runs. This said `tiktoken/o200k_base` while
        // the code counted `Math.ceil(length / 4)` -- naming a tokenizer that
        // never ran is worse than naming none, because a caveat sized for one
        // tokenizer's drift is read as covering the other's.
        kind: tokenizerName(),
        note:
          tokenizerName() === APPROXIMATE_TOKENIZER
            ? 'The o200k_base encoder could not be loaded; counts fall back to a character approximation and are a rough lower bound.'
            : 'Counts are tokenized with o200k_base, a proxy for models that do not publish their tokenizer. Harness-reported bucket counts, where present, are used in preference and are exact.',
      },
      rates: ratesInfo,
      harnesses: perHarness,
      sources,
    }
  }

  /**
   * Per-source ingest activity (T4, decision D2 additive seam).
   *
   * <remarks>
   * Read-only over the single handle this bridge already owns
   * (`getDb()` / the injected `store` — never a second handle, so the
   * follow-the-file contract holds). Record counts come from
   * `records GROUP BY source`; sums and recency from `ingest_log` (T6's
   * shape). T7's display helper labels only. Empty log + empty table is
   * the only `unknown`.
   * </remarks>
   */
  getIngestActivity(): IngestActivity {
    if (this.store) {
      // The store half degrades exactly like the file half below: a broken
      // or locked store reads as unknown receiver activity, never a throw —
      // /coverage promises graceful degradation on both configurations.
      // Counts come from GROUP BY aggregates, never from inflating the
      // corpus through listAll() or paging the unbounded audit log.
      try {
        const recordCounts = this.store.countBySource()
        const logSums = this.store.ingestLogSums()
        let lastReceivedAt: string | null = null
        for (const { lastAt } of logSums.values()) {
          if (lastAt !== null && (lastReceivedAt === null || lastAt > lastReceivedAt)) {
            lastReceivedAt = lastAt
          }
        }
        return buildIngestActivity(recordCounts, logSums, lastReceivedAt)
      } catch {
        return { status: 'unknown', reason: 'no receiver activity recorded', sources: [], lastReceivedAt: null }
      }
    }

    const db = this.getDb()
    const recordCounts = new Map<string, number>()
    if (this.hasTable(db, 'records')) {
      try {
        const rows = db!
          .prepare('SELECT source, COUNT(*) AS n FROM records GROUP BY source')
          .all() as Array<{ source: unknown; n: unknown }>
        for (const row of rows) {
          if (typeof row.source !== 'string') continue
          recordCounts.set(row.source, Number(row.n) || 0)
        }
      } catch {
        // A records table that cannot be grouped reads as no history,
        // not as a thrown coverage request.
      }
    }
    const logSums = new Map<string, { total: number; lastAt: string | null }>()
    let lastReceivedAt: string | null = null
    if (this.hasTable(db, 'ingest_log')) {
      try {
        const rows = db!
          .prepare(
            'SELECT source, SUM(count) AS total, MAX(timestamp) AS last_at FROM ingest_log GROUP BY source',
          )
          .all() as Array<{ source: unknown; total: unknown; last_at: unknown }>
        for (const row of rows) {
          if (typeof row.source !== 'string') continue
          const lastAt = typeof row.last_at === 'string' ? row.last_at : null
          logSums.set(row.source, { total: Number(row.total) || 0, lastAt })
          if (lastAt !== null && (lastReceivedAt === null || lastAt > lastReceivedAt)) {
            lastReceivedAt = lastAt
          }
        }
      } catch {
        // An unreadable audit log reads as no receiver activity, not a throw.
      }
    }
    return buildIngestActivity(recordCounts, logSums, lastReceivedAt)
  }

  /**
   * Source-unit checkpoint statuses (T4, decision D2 additive seam).
   *
   * <remarks>
   * Read-only over `listSourceCheckpoints` (injected store) or the same
   * single `getDb()` handle (raw file) — never a second store handle.
   * `partial` rows (including zero-record ones) are returned verbatim;
   * display grouping is the caller's concern (T8), not this seam's.
   * `null` means the read failed or the table is absent (unknown) — an
   * empty array means the read succeeded and no units exist. Callers must
   * not render zeros for null: that is the fabricated zero the
   * honest-unobservability rule forbids.
   * </remarks>
   */
  getSourceCheckpointStatuses(harnessId?: string): SourceCheckpoint[] | null {
    if (this.store) {
      try {
        return this.store.listSourceCheckpoints(harnessId)
      } catch {
        return null
      }
    }
    const db = this.getDb()
    if (!this.hasTable(db, 'source_checkpoint')) return null
    try {
      const rows = (
        harnessId === undefined
          ? db!.prepare('SELECT * FROM source_checkpoint ORDER BY harness_id, source_key').all()
          : db!.prepare('SELECT * FROM source_checkpoint WHERE harness_id = ? ORDER BY source_key').all(harnessId)
      ) as unknown as SourceCheckpointRow[]
      return rows.map(toSourceCheckpoint)
    } catch {
      return null
    }
  }

  /**
   * Quarantine counts by reason (T5 coverage seam).
   *
   * Aggregated in SQL (`GROUP BY reason`) on whichever handle this bridge
   * owns — the coverage endpoint must not materialize the quarantine table
   * to tally it. A null reason groups as `'unknown'`, never dropped. An
   * unreadable table reads as no rows, matching `getQuarantineCount`'s
   * zero-on-absent contract for this seam.
   */
  /**
   * Catalog snapshot for `GET /api/kyber/model-catalog`.
   *
   * Read-only. An empty table stays empty until `POST /refresh` fills it;
   * seeding here would make a GET rewrite the store.
   */
  getModelCatalog(): ModelCatalogSnapshot {
    const store = this.store
    if (store === undefined) {
      const vendors = {} as ModelCatalogSnapshot['vendors']
      for (const vendor of Object.keys(MODEL_WINDOW_CATALOG_SOURCES) as ModelWindowCatalogVendor[]) {
        vendors[vendor] = {
          documentationUrl: MODEL_WINDOW_CATALOG_SOURCES[vendor].documentationUrl,
          status: 'unknown',
          rowCount: 0,
          lastRefreshAt: null,
        }
      }
      return { rowCount: 0, lastRefreshAt: null, vendors }
    }
    return getModelCatalogSnapshot(store)
  }

  /**
   * User-facing refresh. One derived rebuild runs only after a vendor's rows
   * actually changed; the count is how many times that hook ran, which the
   * refresh itself caps at one.
   */  async refreshModelCatalog(): Promise<
    ModelCatalogSnapshot & ModelWindowCatalogRefreshResult & { derivedRebuildCount: number }
  > {
    const store = this.store
    if (store === undefined) {
      return { ...this.getModelCatalog(), vendorsUpdated: [], vendorsFailed: [], derivedRebuildCount: 0 }
    }
    let derivedRebuildCount = 0
    let rebuild: Promise<unknown> | undefined
    const result = refreshModelWindowCatalog(store, {
      readVendor: readBundledVendorCatalog,
      now: () => new Date().toISOString(),
      rebuildDerived: () => {
        derivedRebuildCount += 1
        rebuild = projectCanonicalStore(store)
      },
    })
    if (rebuild !== undefined) await rebuild
    return { ...getModelCatalogSnapshot(store), ...result, derivedRebuildCount }
  }

  /**
   * User-initiated database clean (issue #312), behind `POST /api/kyber/clean`.
   * The owned handle is read-only by design, so the clean runs on a
   * short-lived read-write `CanonStore` at this bridge's `canonPath` — the
   * same code the `dash clean` CLI runs — opened for the operation and closed
   * in a `finally`. Tests inject a `store` instead, which keeps the file
   * untouched. Both paths hold the store refresh lock for the operation, so
   * concurrent cleans and refreshes serialize. A busy refresh lock surfaces
   * as `CLEAN_BUSY` for the route's 409; anything else is a 500 with a
   * bounded message.
   */
  async cleanDatabase(request: CleanRequest): Promise<CleanReport> {
    if (this.store === undefined && this.canonPath === ':memory:') {
      throw new Error('cleanDatabase: no store to clean')
    }
    const { cleanDatabase, portsForClean } = await import('../clean/clean.js')
    const { acquireStoreRefreshLock } = await import('../refresh/lock.js')
    const lock = await acquireStoreRefreshLock()
    if (lock.outcome !== 'acquired') {
      const busy = new Error('a refresh or clean is already running') as Error & { code: string }
      busy.code = 'CLEAN_BUSY'
      throw busy
    }
    try {
      if (this.store !== undefined) {
        return await cleanDatabase(this.store, request, portsForClean(this.store))
      }
      const { CanonStore } = await import('../canon/store.js')
      const store = new CanonStore(this.canonPath)
      try {
        return await cleanDatabase(store, request, portsForClean(store))
      } finally {
        store.close()
      }
    } finally {
      await lock.handle.release()
    }
  }

  getQuarantineCountsByReason(): Array<{ reason: string; count: number }> {
    if (this.store) {
      try {
        return this.store.quarantineCountsByReason()
      } catch {
        return []
      }
    }
    const db = this.getDb()
    if (!this.hasTable(db, 'quarantine')) return []
    try {
      const rows = db!
        .prepare(
          `SELECT COALESCE(reason, 'unknown') AS reason, COUNT(*) AS n
           FROM quarantine GROUP BY reason ORDER BY n DESC, reason ASC`,
        )
        .all() as Array<{ reason: unknown; n: unknown }>
      return rows.map((row) => ({
        reason: typeof row.reason === 'string' && row.reason !== '' ? row.reason : 'unknown',
        count: typeof row.n === 'number' ? row.n : Number(row.n) || 0,
      }))
    } catch {
      return []
    }
  }
}
