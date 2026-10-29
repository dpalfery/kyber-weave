// SQLite query bridge for the canonical KyberDash store.

import { existsSync, readFileSync, statSync } from 'node:fs'
import type { BigIntStats } from 'node:fs'
import { createHash } from 'node:crypto'
import { inflateSync } from 'node:zlib'
import { randomUUID } from 'node:crypto'
import { createRequire } from 'node:module'
import { resolveCanonDbPath } from '../canon/paths.js'
import { APPROXIMATE_TOKENIZER, tokenizerName } from '../canon/tokens.js'
import {
  SessionIdentities,
  canonicalHarnessId,
  normalizeHarnessName,
} from '../canon/measurability.js'
import { dedupeTwinTurns } from '../canon/twin-dedupe.js'
import { refreshProcessIsAlive } from '../canon/refresh-run.js'
import { formatSessionTitle } from '../canon/naming.js'
import {
  CanonStore,
  normalizeHistoryWeeks,
  problemsHarnessWhere,
  quarantineHarnessWhere,
  readSessionSummaryFigures,
  releaseRecordParts,
  systemPromptTextsFromContentColumns,
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
  type QuarantineEntryDetail,
  type SessionSummaryFigureRow,
} from '../canon/store.js'
import { decompressColumn, decompressColumnBounded } from '../canon/compressed-column.js'
import { StreamingJsonError, StreamingJsonReader, type ColumnChunkReader, type JsonStringEvent } from '../canon/streaming-json.js'
import {
  SESSION_PAYLOAD_FRAGMENTS_SELECT,
  SESSION_PAYLOAD_SELECT,
  SESSION_TURNS_SELECT,
  parseJsonObject,
  sessionTurnsOf,
  turnObjectOf,
} from '../canon/session-read.js'
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
  compareIterableRecords,
  compareRuns as compareStoredRuns,
  type ComparisonSummary,
  type ComparisonTable,
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
  type NotMeasurable,
  notMeasurable,
} from '../canon/types.js'
import type { CostBasisMismatch } from '../canon/cost.js'
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

export type SessionRollup = {
  turn_count: number
  request_count: number
  total_input: number
  total_output: number
  cost_usd: number | null
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
  subagents?: SessionSummary[]
  rollup?: SessionRollup
  /**
   * Set on a projected subagent row whose records are already inside its parent's own
   * counters (the projection builds both from one merged record set), so a rollup must not
   * add it a second time.
   */
  included_in_parent?: boolean
}

/**
 * One session's identity and summary, without its payload.
 *
 * <remarks>
 * The bounded shape `GET /api/kyber/session/:id/summary` serves. It carries
 * no run or execution linkage: the `session` table has no such column and the
 * session payload carries no such field either, so a link served here would be
 * invented rather than measured.
 * </remarks>
 */
export type SessionSummaryDetail = {
  session_id: string
  harness: string
  /** The stored label, the same string the session payload carries. */
  label: string | null
  /** The `INTEGER` column as a boolean, as the session list reports it. */
  is_subagent: boolean
  parent_session: string | null
  agent_name: string | null
  started: string | null
  ended: string | null
  /** The session's summary object, parsed — never a JSON string. */
  summary: Record<string, unknown>
  /** How many problems the session carries. */
  problems_count: number
}

/**
 * One turn's measured counters, read without the session's payload.
 *
 * <remarks>
 * The four counters the turn inspector renders. A counter the stored row does
 * not carry is absent, never a zero: a zero would claim the harness measured
 * nothing, and absence is what the row actually says.
 */
export type SessionTurnFigures = {
  /** The turn's own index, as the stored row names it. */
  index: number
  input?: number
  output?: number
  fresh?: number
  cache_read?: number
}

/**
 * One session's timeline, read as a narrow fragment of its payload.
 *
 * <remarks>
 * `timeline` is the payload's own value, so the served bytes are the bytes
 * the whole-payload read produced. A session that carries no timeline serves
 * `undefined`, which the route answers as the empty timeline it served
 * before.
 */
export type SessionTimelineRead = {
  timeline: unknown
}

/**
 * One session's persisted context analysis, read as a narrow fragment of its
 * payload.
 *
 * <remarks>
 * `context` is the payload's own `context` block — where the per-turn
 * pressure, buckets and residual live — and `lastTurnIndex` the index of the
 * session's last turn, which is the fallback for a turn the analysis does not
 * number itself.
 */
export type SessionContextRead = {
  context: Record<string, unknown> | undefined
  lastTurnIndex: number | undefined
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
  /**
   * Rows in `records` carrying this source — canonical stored telemetry only.
   * Quarantined spans are held out of the corpus and never counted here.
   */
  recordCount: number
  /**
   * Summed `ingest_log.count` for this source: received OTLP span/log volume as the
   * collector tallied it on arrival — including spans later quarantined or rejected.
   * `ingestedCount > 0` beside `recordCount === 0` therefore means transport is
   * receiving while nothing is landing in the corpus (quarantine, not silence); 0 when
   * the log names the source nowhere.
   */
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

/**
 * One quarantined span with its incoming evidence. `rawPayload` is the exact span JSON
 * stored at quarantine time (v18+ rows), parsed; older rows carry null with
 * `evidenceAvailable: false` — explicitly unavailable, never reconstructed from the
 * records table (that row is the normalized record, not the incoming payload).
 * List routes never serve this shape; it is metadata-only there (bounded).
 */
export type QuarantineDetail = QuarantineRow & {
  rawPayload: unknown | null
  evidenceAvailable: boolean
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

/**
 * One recorded problem with its exception evidence. `message` is the exact stored
 * message; `stack` is the exception stack when the problem came from a throw, null
 * otherwise — a computed validation problem has no stack and must not claim one.
 * List routes never serve the stack; it is metadata-only there (bounded).
 */
export type ProblemDetail = ProblemRow & {
  stack: string | null
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
  included_in_parent?: boolean | null
}

export type SessionPayload = Record<string, unknown> & {
  id?: string
  harness?: string
  summary?: ParsedSummary
  turns?: unknown[]
  problems?: unknown[]
  /**
   * The server's cost-basis verdict for this session (rule R1 — the display
   * layers render it, they never re-derive it). `null` when the cost blocks
   * agree, or when nothing was priced; an object when they do not, carrying
   * the problem text and the separate per-basis totals.
   *
   * Optional because a row stored before this field existed does not carry it.
   * `undefined` means "this store has not been re-projected by a build that
   * computes it" — unknown, which a consumer must not read as "no mismatch".
   */
  costBasisMismatch?: CostBasisMismatch | null
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

/** Narrow projection row consumed by the read-only analytics service. */
export type AnalyticsSessionProjection = {
  id: string
  runId: null
  label: string | null
  startedAt: string | null
  harness: string
  repository: string | null
  branch: string | null
  isSubagent: boolean
  includedInParent: boolean
  inputTokens: number | null
  freshInputTokens: number | null
  cacheReadTokens: number | null
  cacheCreationTokens: number | null
  outputTokens: number | null
  durationMs: number | null
  costs: readonly { amount: number; currency: string; basis: string; status: string }[]
}

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
  /**
   * `false` on a tool definition the turn offered without keeping resident.
   * Absent means resident, so the route never invents the mark on a part that
   * did not carry it. Mirrors `ContentPart.resident`.
   */
  resident?: false
}

export type SessionContentResult = {
  sessionId: string
  spanId?: string
  parts: SessionContentPart[]
}

export type SessionContentOptions = {
  spanId?: string
  part?: string
  /**
   * Explicit lossless request (`?full=true` on the session-content route):
   * bypasses `CONTENT_RESPONSE_BUDGET` so the full source text returns
   * intact with no truncation flags. A large-number budget is not offered
   * here — lossless is opt-in and documented, the default stays capped so
   * existing callers keep their current behavior.
   */
  full?: boolean
}

/**
 * Captured-context recurrence for one turn-content part: whether its exact
 * whole source text was captured as the same part kind in an earlier turn of
 * this session. Exact bytes only — no normalization, trimming, or
 * token-level similarity. Recurrence is independent of model cache use: a
 * repeated part may still be uncached, and a new part may sit in a cached
 * prefix. Never convert this into a cache claim.
 */
export type TurnContentPartRecurrence = {
  seenBefore: boolean
}

/**
 * Aggregated recurrence for one canonical block: `newThisTurn` when at
 * least one member part is new, `seenBefore` when at least one member part
 * exactly matches an earlier same-kind part. Both may hold at once for a
 * block mixing new and repeated parts.
 */
export type TurnContentBlockRecurrence = {
  newThisTurn: boolean
  seenBefore: boolean
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
  recurrence?: TurnContentPartRecurrence
  /**
   * `false` on a tool definition the turn offered without keeping resident.
   * Absent means resident. Mirrors `ContentPart.resident`.
   */
  resident?: false
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
  recurrence?: TurnContentBlockRecurrence
}

/**
 * One session-level shared prompt part. `truncated`/`totalLength` appear
 * only when the default (non-`full`) response budget cut this part — the
 * same truthful flagging as every other budgeted text in this response.
 * `turnIndex` is the 0-based transport index of the turn that contributed a
 * turn-derived occurrence; it is present only on turn-derived parts.
 * Session-scope (non-turn) parts carry no `turnIndex` — absent is not zero
 * and must never be guessed as a turn.
 */
export type TurnContentSharedPart = {
  text: string
  spanId?: string
  turnIndex?: number
  truncated?: boolean
  totalLength?: number
}

/**
 * Session-level shared context for a turn: every captured `system_prompt`
 * part in session scope at or before this turn, in capture order — the turn
 * spans' own prompts (preceding plus current, each occurrence carrying its
 * contributing `turnIndex`) united with the session-wide scan that reaches
 * only non-turn prompts (turn spans are excluded by operation as well as by
 * span id, so a turn span never appears as an unindexed duplicate). `seenBefore`
 * holds when any part exactly matches an earlier-turn same-kind part; it is
 * capture evidence only, never a cache claim. Null when no system prompt
 * was captured in scope. The client hides exact duplicates of the turn's
 * own parts and otherwise labels the section `Shared session context`.
 */
export type TurnContentSharedContext = {
  parts: TurnContentSharedPart[]
  seenBefore: boolean
}

/**
 * Payload-size note (no silent omission): the explicit lossless path
 * returns every source byte, and by design the same bytes appear up to
 * three times per response — once in `assembledText`, once across
 * `blocks[].text`/`blocks[].parts[].text`, and once in `parts[].text` —
 * because supported callers (`ContextInspector`, the TurnDetail page) read
 * `assembledText` and per-part views respectively. Callers that cannot hold
 * ~3x source bytes must use the default budget (explicit `truncated` flags),
 * never a trimmed illusion of completeness.
 */
export type TurnContentResult = {
  sessionId: string
  turnIndex: number
  spanId?: string
  /** The turn record's parent span, when the record named one (B7). */
  parentSpanId?: string
  model?: string
  blocks: TurnContentBlock[]
  parts: TurnContentPart[]
  assembledText: string
  truncated?: boolean
  totalLength?: number
  /**
   * Where this turn's raw packets can be fetched (B7): the request span's
   * attributes always — a served turn is a stored record — and the
   * quarantined parent's packet only when that row kept a payload, so the
   * pointer is never a link to nothing.
   */
  rawPacket: { spanAttributesPath: string; interactionPath?: string }
  sharedContext?: TurnContentSharedContext | null
  /**
   * The skills this turn had access to and invoked. Not a content block: it
   * sits beside `blocks` and leaves them unchanged. The whole entry is declared
   * not measurable when the session's payload carries no skills view.
   */
  skills?: TurnSkillsContent | NotMeasurable
}

/**
 * Generous ceiling for one unclipped content response (a few megabytes of
 * characters). A single system prompt fits; a 7,000-turn dump does not get
 * serialized whole. When a part does not fit, it is cut and flagged — never
 * silently shortened.
 *
 * The turn-content route also accepts an explicit lossless request
 * (`?full=true`): callers that need every byte pass `full` instead of
 * guessing a large `budget` number. See `isFullContentRequest` in routes.ts.
 */
export const CONTENT_RESPONSE_BUDGET = 2_000_000

/**
 * Documented sentinel for an explicit lossless turn-content request. The
 * route checks the `full` query value (`true`/`1`); anything else keeps the
 * default budget. A large-number `budget` is not a substitute — it is
 * undocumented and still truncates past its value.
 */
export const TURN_CONTENT_FULL_PARAM = 'full'

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

/**
 * One row's summary, as an object.
 *
 * <remarks>
 * The stored summary is a JSON string; the served shape is the object it
 * encodes. A summary that is not an object, or that does not parse, is an
 * absent summary — never a fabricated one.
 * </remarks>
 */
function parseSummaryObject(value: string | object | null | undefined): Record<string, unknown> {
  if (typeof value === 'object' && value !== null) return value as Record<string, unknown>
  if (typeof value !== 'string' || value === '') return {}
  try {
    const parsed = JSON.parse(value) as unknown
    if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) return {}
    return parsed as Record<string, unknown>
  } catch {
    return {}
  }
}

/**
 * One JSON value, parsed from the text SQLite's JSON functions return.
 *
 * <remarks>
 * `NULL` — an absent path — is no value at all. A JSON `null` is a value and
 * stays one, so a timeline that is null is served as null rather than as
 * absent.
 */
function parseJsonValue(value: unknown): unknown {
  if (typeof value !== 'string') return undefined
  try {
    return JSON.parse(value) as unknown
  } catch {
    return undefined
  }
}

/**
 * One turn row's measured counters.
 *
 * <remarks>
 * The counters are taken as the row holds them. A counter the row does not
 * carry stays absent, because a zero would claim the harness measured
 * nothing — and absence is what the row actually says.
 */
function turnFiguresOf(turn: Record<string, unknown>, turnIndex: number): SessionTurnFigures {
  const index = turn['index']
  return {
    index: typeof index === 'number' && Number.isFinite(index) ? index : turnIndex,
    input: turnCounter(turn, 'input'),
    output: turnCounter(turn, 'output'),
    fresh: turnCounter(turn, 'fresh'),
    cache_read: turnCounter(turn, 'cache_read'),
  }
}

/** One measured counter, or `undefined` when the row carries no finite one. */
function turnCounter(turn: Record<string, unknown>, key: string): number | undefined {
  const value = turn[key]
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

/**
 * One row's summary and problem count, read out of its own payload.
 *
 * <remarks>
 * The reading the session list already makes for a row it cannot serve from
 * the materialized columns, narrowed to one row.
 * </remarks>
 */
function summaryFromPayload(payload: string | null | undefined): {
  summary: Record<string, unknown>
  problems_count: number
} {
  if (typeof payload !== 'string' || payload === '') return { summary: {}, problems_count: 0 }
  try {
    const parsed = JSON.parse(payload) as { summary?: unknown; problems?: unknown } | null
    if (parsed === null || typeof parsed !== 'object') return { summary: {}, problems_count: 0 }
    return {
      summary: parseSummaryObject(parsed.summary as string | object | null | undefined),
      problems_count: Array.isArray(parsed.problems) ? parsed.problems.length : 0,
    }
  } catch {
    return { summary: {}, problems_count: 0 }
  }
}

/**
 * Whether a row is missing either of the two materialized summary columns.
 *
 * <remarks>
 * NULL is the writer's per-column "not materialized" sentinel, so a row is
 * never whole or empty: a payload carrying a problems array and no summary is
 * written as a NULL summary beside a real count, and a payload carrying a
 * summary and no problems array is written as a NULL count beside a real
 * summary. A row is complete only when it carries both columns.
 */
function isMissingSessionColumn(row: SessionDbRow): boolean {
  return (
    row.summary_json === null ||
    row.summary_json === undefined ||
    row.problems_count === null ||
    row.problems_count === undefined
  )
}

/**
 * Fill a row's missing materialized columns from a legacy reading.
 *
 * <remarks>
 * Only what the row is missing is filled in: a materialized value is never
 * replaced by the payload's, and a value the payload does not carry leaves
 * the row's own NULL in place. The session list and the bounded summary route
 * both merge through this, so the two surfaces cannot drift apart on what a
 * NULL column means.
 */
function fillMissingSessionColumns(
  row: SessionDbRow,
  legacy: { summary_json?: string | object | null; problems_count?: number | null },
): void {
  if (row.summary_json === null || row.summary_json === undefined) {
    row.summary_json = legacy.summary_json ?? null
  }
  if (row.problems_count === null || row.problems_count === undefined) {
    row.problems_count = legacy.problems_count ?? null
  }
}

/**
 * The session list, read from the materialized columns only.
 *
 * <remarks>
 * `summary_json` and `problems_count` carry the two facts the list needs out
 * of the payload blob (the serialized `$.summary`, and the length of
 * `$.problems`), so a materialized corpus is served without reading a byte of
 * `payload`. The statement is column-only on purpose: naming `payload` here
 * at all — even inside `json_extract` — makes SQLite read and parse the whole
 * blob of every row, which is what made a list request cost the size of the
 * payload store.
 * </remarks>
 */
const SESSION_LIST_SELECT =
  'SELECT session_id, harness, label, is_subagent, parent_session, agent_name, ' +
  'repo, branch, started, ended, summary_json, problems_count ' +
  'FROM session ORDER BY started DESC'

/**
 * The legacy subset: rows missing a materialized value.
 *
 * <remarks>
 * The predicate is the load-bearing part: it confines the payload read to the
 * rows that are not fully materialized, so a corpus where every row carries
 * both values never issues this statement at all. It covers a row missing
 * *either* value, because NULL is the writer's per-column "not materialized"
 * sentinel — a payload with a problems array and no summary is written as a
 * NULL summary beside a real count, and a payload with no problems array is
 * written as a NULL count beside a real summary. The extracted values are
 * merged back into the primary rows by `session_id`, and a value the row
 * already carries is never overwritten.
 * </remarks>
 */
const SESSION_LIST_LEGACY_SELECT =
  "SELECT session_id, json_extract(payload, '$.summary') AS summary_json, " +
  "json_array_length(json_extract(payload, '$.problems')) AS problems_count " +
  'FROM session WHERE summary_json IS NULL OR problems_count IS NULL'

/**
 * The last resort: the whole payload, parsed in JS.
 *
 * <remarks>
 * Taken when the SQLite build has no JSON functions (the legacy fallback
 * cannot run) or when the schema predates the materialized columns (the
 * primary statement cannot run). Unchanged from the pre-materialization
 * reader, so a database the fast path cannot serve still lists.
 * </remarks>
 */
const SESSION_LIST_PAYLOAD_SELECT =
  'SELECT session_id, harness, label, is_subagent, parent_session, agent_name, ' +
  'repo, branch, started, ended, payload ' +
  'FROM session ORDER BY started DESC'

/**
 * One session's identity columns and its materialized summary.
 *
 * <remarks>
 * The narrow read the bounded summary is built on. `payload` is not named
 * here: a session's blob is unbounded — 15.3 MB for a 10,933-turn session —
 * and this read answers from the columns alone.
 * </remarks>
 */
const SESSION_SUMMARY_SELECT =
  'SELECT session_id, harness, label, is_subagent, parent_session, agent_name, ' +
  'started, ended, summary_json, problems_count ' +
  'FROM session WHERE session_id = ?'

/**
 * The legacy subset for one row: the summary and the problem count read out
 * of that row's own payload.
 *
 * <remarks>
 * Transitional, and scoped to the requested id — the `WHERE` clause is that
 * same single id, so this reads one row's payload and never the table's. It
 * is issued only for a row that is missing *either* materialized value, since
 * NULL is the writer's per-column "not materialized" sentinel, so a fully
 * materialized corpus never runs it. `json_extract` keeps the blob inside
 * SQLite: only the extracted fragment crosses into JS.
 * </remarks>
 */
const SESSION_SUMMARY_LEGACY_SELECT =
  "SELECT json_extract(payload, '$.summary') AS summary_json, " +
  "json_array_length(json_extract(payload, '$.problems')) AS problems_count " +
  'FROM session WHERE session_id = ?'

/**
 * The last resort for one row: its payload, parsed in JS.
 *
 * <remarks>
 * Taken when the schema predates the materialized columns (the narrow read
 * cannot run) or when the SQLite build has no JSON functions (the legacy
 * extraction cannot run). One row only, so it is never worse than the detail
 * route's whole-payload read.
 * </remarks>
 */
const SESSION_SUMMARY_PAYLOAD_SELECT =
  'SELECT session_id, harness, label, is_subagent, parent_session, agent_name, ' +
  'started, ended, payload ' +
  'FROM session WHERE session_id = ?'

/**
 * One turn row, read from the turns table.
 *
 * <remarks>
 * A session's turns live in `session_turn`, one row each, indexed by
 * `(session_id, turn_index)`. One turn is therefore a seek — not the session
 * blob, which held 10,933 turn rows and 15.3 MB for the corpus's largest
 * session.
 */
const SESSION_TURN_SELECT =
  'SELECT turn_json FROM session_turn WHERE session_id = ? AND turn_index = ?'

/**
 * Whether a session has turn rows of its own.
 *
 * <remarks>
 * The load-bearing part of the legacy fallback below: a session whose turns
 * are split out no longer carries them in its payload, so an index with no
 * row is simply out of range — and saying so must not cost a payload read.
 */
const SESSION_TURN_ROWS_SELECT =
  'SELECT 1 AS present FROM session_turn WHERE session_id = ? LIMIT 1'

/**
 * The legacy turn read: one turn out of a payload that still carries its turns.
 *
 * <remarks>
 * `json_each` walks the payload's own turn array inside SQLite, so only the
 * one matching turn crosses into JS. The identity rule is the writer's own: a
 * turn that names an `index` is that turn, and a turn that names none is its
 * position — the rule the turns table itself was built with, so the two paths
 * answer for the same turn.
 *
 * The array check is load-bearing: a `turns` key that is not an array is a
 * malformed payload, and walking it as one would answer for a turn that does
 * not exist.
 */
const SESSION_TURN_LEGACY_SELECT =
  'SELECT t.value AS turn_json FROM session s, json_each(json_extract(s.payload, \'$.turns\')) AS t ' +
  "WHERE s.session_id = ? AND json_type(s.payload, '$.turns') = 'array' AND COALESCE(" +
  "CASE WHEN json_type(t.value, '$.index') IN ('integer', 'real') " +
  "THEN CAST(json_extract(t.value, '$.index') AS INTEGER) END, " +
  'CAST(t.key AS INTEGER)) = ? LIMIT 1'

/**
 * Whether a derived session row exists.
 *
 * <remarks>
 * Membership only: a seek on the session table's primary key, and the whole
 * of what "does this session exist" needs. The session's content is not part
 * of that answer — a reader that took it from the payload would decompress
 * the blob and reassemble every turn of the session out of `session_turn` to
 * learn what the row's key already carries.
 */
const SESSION_EXISTS_SELECT = 'SELECT 1 FROM session WHERE session_id = ?'

/**
 * One session's timeline, read as a narrow fragment of its payload.
 *
 * <remarks>
 * `->` hands over the JSON text of the value at the path, so the timeline
 * crosses into JS and the blob around it does not — and the text is the
 * value's own JSON, so a timeline of any JSON type is served byte for byte
 * as the whole-payload read served it. The timeline is session metadata: it
 * lives beside the turns, not inside them, so it survives the payload split
 * intact.
 */
const SESSION_TIMELINE_SELECT =
  "SELECT (payload -> '$.timeline') AS timeline_json FROM session WHERE session_id = ?"

/**
 * One session's context analysis, read as narrow fragments of its payload.
 *
 * <remarks>
 * Both fragments are taken by SQLite's JSON functions: the persisted context
 * block, and the index of the session's last turn. The blob around them never
 * crosses into JS.
 */
const SESSION_CONTEXT_SELECT =
  "SELECT json_extract(payload, '$.context') AS context_json, " +
  "json_extract(payload, '$.turns[#-1].index') AS last_turn_index " +
  'FROM session WHERE session_id = ?'

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
  /** The record's parent span; the quarantined packet a turn may point at (B7). */
  parentSpanId: string | null
  parts: ContentPart[]
  /**
   * Captured timestamp of the span, when the seam carries one. Lets the
   * turn-content reader scope session prompts to those captured at or
   * before the turn being shown without a second lookup per span.
   */
  timestamp?: string
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
  /** The projected skills of this turn (names); read by the skills entry, never trusted blind. */
  skills_listed?: unknown
  skills_invoked?: unknown
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

/** The turn's skills entry for the content route (T2), as one turn of the content response. */
export type TurnSkillsContent = {
  /** What the turn had access to; each with its row's first observed description as `text` when one exists. */
  listed: Array<{ name: string; text?: string }> | NotMeasurable
  /** The distinct skills the turn itself invoked; `[]` is a measured none. */
  invoked: string[] | NotMeasurable
}

const isRecordValue = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)

const isStringArray = (value: unknown): value is string[] =>
  Array.isArray(value) && value.every((item) => typeof item === 'string')

/**
 * A turn's skills entry, read from the already-loaded session payload only.
 * The narrow turn-content seam must not walk session records, so the payload
 * carries what the projection already derived: `skills.rows[].description`
 * and each turn's `skills_listed` / `skills_invoked`. A payload projected
 * before skills existed has no `skills` key and is declared so.
 *
 * Known limit (accepted): `text` is the skill's first observed description in
 * the session. A later listing that changes the description does not alter the
 * text served for earlier turns.
 */
function skillsContentFromPayload(
  payload: SessionPayload | null | undefined,
  turn: TurnDescriptor | undefined,
): TurnSkillsContent | NotMeasurable {
  const skills = payload?.skills
  if (!isRecordValue(skills)) return notMeasurable('session not yet projected with skills')
  if (skills.availability === 'not_measurable') {
    return notMeasurable(typeof skills.reason === 'string' ? skills.reason : 'Skills are not measurable for this session.')
  }

  const descriptions = new Map<string, string>()
  if (Array.isArray(skills.rows)) {
    for (const row of skills.rows) {
      if (isRecordValue(row) && typeof row.name === 'string' && typeof row.description === 'string') {
        descriptions.set(row.name, row.description)
      }
    }
  }

  const listed: TurnSkillsContent['listed'] = isStringArray(turn?.skills_listed)
    ? turn.skills_listed.map((name) => {
        const text = descriptions.get(name)
        return text === undefined ? { name } : { name, text }
      })
    : notMeasurable('This turn has no skill listing in the projected session, so the skills it had access to are not measurable.')

  const invoked: TurnSkillsContent['invoked'] = isStringArray(turn?.skills_invoked)
    ? [...turn.skills_invoked]
    : notMeasurable('Skill invocations are not measured for this session, so the skills this turn invoked are not measurable.')

  return { listed, invoked }
}

/**
 * Index of explicit 0-based transport identities over one ordered turn list,
 * built in a single pass. First identity wins on duplicates, matching the
 * historic `find` order.
 */
export function buildTransportIndex<T>(
  items: readonly T[],
  identityOf: (item: T) => number | undefined,
): Map<number, T> {
  const byIdentity = new Map<number, T>()
  for (const item of items) {
    const id = identityOf(item)
    if (id !== undefined && !byIdentity.has(id)) byIdentity.set(id, item)
  }
  return byIdentity
}

/**
 * Strict turn resolution over one ordered list: explicit identity first,
 * array position only for the row that carries neither `index` nor `turn`.
 * Anything else resolves to nothing — never a neighboring turn (issue #184).
 */
export function lookupTransport<T>(
  items: readonly T[] | undefined,
  byIdentity: Map<number, T> | undefined,
  identityOf: (item: T) => number | undefined,
  turnIndex: number,
): T | undefined {
  const direct = byIdentity?.get(turnIndex)
  if (direct !== undefined) return direct
  const positional = items !== undefined && turnIndex >= 0 && turnIndex < items.length ? items[turnIndex] : undefined
  if (positional !== undefined && identityOf(positional) === undefined) return positional
  return undefined
}

/**
 * Normalized turn-resolution row: the span an index resolves to plus its
 * explicit 0-based identity (`undefined` for position-only rows), with the
 * one-pass identity map beside the ordered entries. Built once per response
 * and shared by the target lookup and every preceding-turn lookup.
 */
export type TurnResolutionEntry = {
  spanId: string
  model?: string
  identity: number | undefined
}

export type TurnResolutionPool = {
  entries: TurnResolutionEntry[]
  byIdentity: Map<number, TurnResolutionEntry>
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

/**
 * Stable hash of exact source bytes for recurrence comparison. Hashes (not
 * texts) are retained per earlier part so a long session keeps one small
 * digest per captured part instead of every earlier byte in memory.
 */
function sha256Hex(text: string): string {
  return createHash('sha256').update(text, 'utf8').digest('hex')
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
    // The column holds the deflated parts, and the plain JSON a row written
    // before the compression landed holds. Both are read here, so a pre-change
    // row keeps its parts — the fall-through to `content_json` below would
    // lose them, because that column is '{}' on a parts-carrying row.
    const parts = decompressColumn(row.parts_json)
    if (Array.isArray(parts) && parts.length > 0) {
      return [...parts].sort((a, b) => (a.order ?? 0) - (b.order ?? 0))
    }
    // A column that holds nothing usable falls through to content_json — a
    // corrupt blob is not an empty session.
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

/** A record row's parent span id, or null when it carries none. */
function parentSpanIdOf(row: Record<string, unknown>): string | null {
  return row.parent_span_id === null || row.parent_span_id === undefined
    ? null
    : String(row.parent_span_id)
}

function toContentSource(record: CanonicalRecord): ContentSourceRecord {
  return {
    spanId: record.spanId,
    sessionKey: sessionKeyOf(record),
    parentSpanId: record.parentSpanId,
    parts: partsFromRecord(record),
    timestamp: normalizeRecordTimestamp(record.timestamp),
  }
}

/**
 * A canonical timestamp as the narrow stored string. A `Date` round-trips
 * through ISO; anything else is unknown rather than invented.
 */
function normalizeRecordTimestamp(value: CanonicalRecord['timestamp'] | unknown): string | undefined {
  if (typeof value === 'string') return value
  if (value instanceof Date && !Number.isNaN(value.getTime())) return value.toISOString()
  return undefined
}

/**
 * Temporal gate for shared session prompts: only a prompt provably captured
 * after the shown turn is withheld. Both timestamps parse and the candidate
 * is strictly later — anything missing or unparseable stays included,
 * because the reader cannot prove it is future evidence and dropping it
 * would invent a claim the corpus cannot support. Equal timestamps stay
 * included for the same reason: millisecond equality cannot order two
 * captures, so the limitation is documented, not decided.
 */
function sessionPromptAtOrBefore(candidate: string | undefined, target: string | undefined): boolean {
  if (candidate === undefined || target === undefined) return true
  const candidateMs = Date.parse(candidate)
  const targetMs = Date.parse(target)
  if (!Number.isFinite(candidateMs) || !Number.isFinite(targetMs)) return true
  return candidateMs <= targetMs
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

/**
 * A session's own counters plus its descendants', counted once. A child whose records are
 * already inside this session's counters (`included_in_parent`) adds nothing, and neither
 * does anything beneath it; every other child contributes its own rollup, leaf-first.
 * `visiting` guards a malformed parent cycle from recursing forever.
 */
function computeSessionRollup(session: SessionSummary, visiting: Set<string> = new Set()): SessionRollup {
  visiting.add(session.session_id)
  let turnCount = session.turn_count ?? 0
  let requestCount = session.request_count ?? 0
  let totalInput = session.total_input ?? 0
  let totalOutput = session.total_output ?? 0
  let costUsdSum: number | null = session.cost_usd != null ? session.cost_usd : null

  for (const child of session.subagents ?? []) {
    if (child.included_in_parent === true || visiting.has(child.session_id)) continue
    const rolled = child.rollup ?? computeSessionRollup(child, visiting)
    turnCount += rolled.turn_count
    requestCount += rolled.request_count
    totalInput += rolled.total_input
    totalOutput += rolled.total_output
    if (rolled.cost_usd != null) costUsdSum = (costUsdSum ?? 0) + rolled.cost_usd
  }
  visiting.delete(session.session_id)

  return {
    turn_count: turnCount,
    request_count: requestCount,
    total_input: totalInput,
    total_output: totalOutput,
    cost_usd: costUsdSum,
  }
}

/**
 * Complete captured-context traversal budget (plan defaults): at most 100
 * live traversals, each evicted after two minutes idle. A new traversal past
 * the cap answers `budget_exceeded` rather than evicting a live reader out
 * from under its cursor.
 */
const CAPTURED_CONTEXT_MAX_TRAVERSALS = 100
const CAPTURED_CONTEXT_TRAVERSAL_TTL_MS = 120_000
/**
 * Page fill target for captured-context content pages. Each chunk text is
 * capped at 16,384 UTF-8 bytes, so filling to 60,000 leaves headroom for the
 * chunk metadata plus the service envelope wrapped around the page — the
 * whole encoded response stays within 65,536 bytes.
 */
const CAPTURED_CONTEXT_PAGE_BUDGET = 60_000
/** Per-chunk text ceiling, in UTF-8 bytes (validated by the inspect seam; re-clamped here). */
const CAPTURED_CONTEXT_BYTE_LIMIT = 16_384
/** Manifest page bounds (validated by the inspect seam; re-clamped here). */
const CAPTURED_CONTEXT_MANIFEST_DEFAULT_LIMIT = 25
const CAPTURED_CONTEXT_MANIFEST_MAX_LIMIT = 100
/** Placeholder cursor length assumed while filling a page, so the sealed cursor cannot push it over budget. */
const CAPTURED_CONTEXT_CURSOR_PLACEHOLDER = '0'.repeat(256)

type CapturedContextOrigin = 'turn' | 'session_shared'

type CapturedContextUnit = {
  sessionId: string
  turnIndex?: number
  spanId: string
  origin: CapturedContextOrigin
}

type CapturedContextPart = {
  kind: string
  role: string | null
  order: number | null
  resident: boolean
  text: string
}

type CapturedContextChunk = {
  partId: string
  blockId: string
  sessionId: string
  turnIndex?: number
  spanId: string
  origin: CapturedContextOrigin
  text: string
  byteOffset: number
  complete: boolean
  role: string | null
  order: number
  resident: boolean
}

type CapturedContextTraversal = {
  binding: string
  touchedAt: number
}

type CapturedContextTarget =
  | { kind: 'run'; runId: string }
  | { kind: 'session'; sessionId: string }
  | { kind: 'turn'; sessionId: string; turnIndex: number }

/** Canonical block identity for a captured part kind; unknown kinds keep their own name. */
function capturedBlockIdOf(kind: string): string {
  if (kind === 'user_messages' || kind === 'assistant_turns') return 'conversation_history'
  if (
    kind === 'system_prompt' ||
    kind === 'tool_definitions' ||
    kind === 'instruction_context' ||
    kind === 'conversation_history' ||
    kind === 'tool_result_content'
  ) {
    return kind
  }
  return kind
}

/**
 * A part's role, from what the harness stored or — only for the two
 * unambiguous legacy buckets — the bucket itself. Anything else stays
 * absent rather than guessed, so a reconstructed view never invents
 * provenance.
 */
function capturedRoleOf(kind: string, stored: unknown): string | null {
  if (stored === 'system' || stored === 'user' || stored === 'assistant') return stored
  if (kind === 'system_prompt') return 'system'
  if (kind === 'user_messages') return 'user'
  if (kind === 'assistant_turns') return 'assistant'
  return null
}

/**
 * Split text into pieces of at most `byteLimit` UTF-8 bytes without splitting
 * a multi-byte code point across pages: the cut backs up over continuation
 * bytes, exactly like the turn-block reader. A single code point wider than
 * the page is kept whole rather than split or dropped, so pagination can
 * neither lose bytes nor duplicate them.
 *
 * WHY byte cuts, not character iteration: cutting the encoded bytes and
 * backing over continuation bytes lands on exactly the same greedy
 * character boundaries, without a per-character byte-length call — the
 * scan cost that dominates multi-megabyte page splits.
 */
function splitUtf8Text(text: string, byteLimit: number): string[] {
  if (text === '') return []
  const bytes = Buffer.from(text, 'utf8')
  if (bytes.byteLength <= byteLimit) return [text]
  const pieces: string[] = []
  let start = 0
  while (start < bytes.byteLength) {
    let end = Math.min(start + byteLimit, bytes.byteLength)
    while (end > start && (bytes[end] & 0xc0) === 0x80) end -= 1
    if (end === start) {
      end = start + 1
      while (end < bytes.byteLength && (bytes[end] & 0xc0) === 0x80) end += 1
    }
    pieces.push(bytes.subarray(start, end).toString('utf8'))
    start = end
  }
  return pieces
}

/**
 * A normalized captured-context target, or null when the request names no
 * usable one. Normalization keeps key order fixed, so the cursor binding
 * seals the same target the same way on every replay.
 */
function capturedContextTargetOf(value: unknown): CapturedContextTarget | null {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return null
  const record = value as Record<string, unknown>
  if (record.kind === 'run' && typeof record.runId === 'string' && record.runId.length > 0) {
    return { kind: 'run', runId: record.runId }
  }
  if (record.kind === 'session' && typeof record.sessionId === 'string' && record.sessionId.length > 0) {
    return { kind: 'session', sessionId: record.sessionId }
  }
  if (
    record.kind === 'turn' &&
    typeof record.sessionId === 'string' &&
    record.sessionId.length > 0 &&
    typeof record.turnIndex === 'number' &&
    Number.isSafeInteger(record.turnIndex) &&
    record.turnIndex >= 0
  ) {
    return { kind: 'turn', sessionId: record.sessionId, turnIndex: record.turnIndex }
  }
  return null
}

export class KyberBridge {
  private canonDb?: DatabaseSync
  private readonly store?: CanonStore
  readonly canonPath: string
  readonly ratesPath: string | undefined

  /**
   * Live complete-context traversals: one entry per multi-page scope, keyed
   * by the binding each resumption cursor is sealed against.
   *
   * Why no retained pages: every page is re-derived from the store on
   * demand — re-streamed through the bounded column reader and re-split by
   * the same greedy fill, so replays stay byte-exact without holding the
   * transcript. A traversal entry is only its binding plus the last touch
   * time, so entries neither grow with the pages served nor outlive two
   * idle minutes; they are capped at
   * {@link CAPTURED_CONTEXT_MAX_TRAVERSALS} live traversals and drain when
   * the last page is served. Cursors stay process-local opaque HMACs over
   * the binding: they carry no transcript bytes and cannot be forged or
   * moved across requests.
   */
  private readonly capturedContextTraversals = new Map<string, CapturedContextTraversal>()
  /** Per-process HMAC key sealing captured-context cursors to their binding. */
  private readonly capturedContextKey = randomUUID()

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
    // The shared resolver, so the server can only ever read the store the CLI
    // and the receiver write (src/canon/paths.ts).
    this.canonPath = resolveCanonDbPath(options?.canonPath)

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

  /**
   * The handle the session reads go through.
   *
   * <remarks>
   * The store's handle when a store is injected: the store owns the sessions,
   * and a bridge that carries both a store and a handle of its own reads the
   * sessions from the store — the two can be different databases, and the
   * store's is the one that holds them. The store's handle also outlives the
   * bridge: closing drops the bridge's reference to it, not the handle.
   *
   * Without a store, the bridge's own handle — which it stops using once it
   * has closed.
   */
  private sessionHandle(): DatabaseSync | undefined {
    if (this.store && typeof this.store.getDatabase === 'function') {
      try {
        return this.store.getDatabase()
      } catch {
        return undefined
      }
    }
    return this.getDb()
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
   * Child subagent sessions are hierarchically nested under their parent session,
   * with read-time spend and counters rolled up to the parent.
   */
  listSessions(limit?: number): SessionSummary[] {
    const db = this.getDb()
    const rawSessions: SessionSummary[] = []

    // The canonical derived `session` cache is the only reporting authority.
    // Raw `records` are never synthesized into sessions here: projection
    // (projectCanonicalStore) is what turns accepted spans into derived rows,
    // so a record-only group is not yet a session and must not appear.
    if (this.hasTable(db, 'session')) {
      try {
        const rows = this.readSessionListRows(db!)
        for (const row of rows) {
          let summ: ParsedSummary | null = null
          let problemsCount = 0

          // The two materialized columns are independent facts. A payload can
          // carry a problems array without a summary, and the writer stores
          // that as a NULL summary beside a real count; a payload can carry a
          // summary without a problems array, and the writer stores that as a
          // NULL count beside a real summary. So neither column gates the
          // other: a missing summary never hides a count, and a missing count
          // never discards a summary.
          if (row.summary_json !== undefined && row.summary_json !== null) {
            if (typeof row.summary_json === 'string') {
              try {
                summ = JSON.parse(row.summary_json) as ParsedSummary
              } catch {}
            } else if (typeof row.summary_json === 'object') {
              summ = row.summary_json as ParsedSummary
            }
          }
          if (row.problems_count !== undefined && row.problems_count !== null) {
            problemsCount = Number(row.problems_count) || 0
          } else if (typeof row.payload === 'string') {
            // Last resort only: a row that reached here without a materialized
            // count was read by the payload statement, because this SQLite
            // build has no JSON functions or the schema predates the
            // materialized columns.
            try {
              const p = JSON.parse(row.payload) as { summary?: ParsedSummary; problems?: unknown[] }
              summ = summ ?? p?.summary ?? null
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

          const rawLabel = row.label ?? null
          const label =
            rawLabel && rawLabel.trim() !== ''
              ? rawLabel
              : formatSessionTitle({
                  sessionId: row.session_id,
                  agentName: row.agent_name,
                  harness: row.harness,
                  rawLabel: row.label,
                })

          rawSessions.push({
            session_id: row.session_id,
            harness: row.harness,
            label,
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
            subagents: [],
            ...(summ.included_in_parent === true ? { included_in_parent: true } : {}),
          })
        }
      } catch (err) {
        console.warn('[KyberBridge] Failed querying session table in canon.db:', err)
      }
    }

    // Index all sessions by session_id for fast parent lookup
    const sessionMap = new Map<string, SessionSummary>()
    for (const session of rawSessions) {
      sessionMap.set(session.session_id, session)
    }

    // Attach each child to its parent. A child is not attached when that would close a cycle
    // (the parent is the child or one of its descendants), so malformed ancestry cannot make
    // sessions vanish from the list.
    const attachedChildIds = new Set<string>()
    const isAncestorOrSelf = (candidateId: string, ofId: string): boolean => {
      const seen = new Set<string>()
      for (let id: string | null | undefined = ofId; id != null && !seen.has(id); id = sessionMap.get(id)?.parent_session) {
        if (id === candidateId) return true
        seen.add(id)
      }
      return false
    }

    for (const session of rawSessions) {
      const isChild = session.is_subagent || Boolean(session.parent_session)
      if (!isChild || !session.parent_session) continue
      const parent = sessionMap.get(session.parent_session)
      if (parent === undefined || isAncestorOrSelf(session.session_id, parent.session_id)) continue
      parent.subagents = parent.subagents ?? []
      parent.subagents.push(session)
      attachedChildIds.add(session.session_id)
    }

    // Every session keeps its own children (a chain A <- B <- C nests two deep), ordered by
    // start. Rollups are computed leaf-first so a parent folds in already-rolled children.
    for (const session of rawSessions) {
      session.subagents = (session.subagents ?? []).sort((a, b) => (a.started ?? '').localeCompare(b.started ?? ''))
    }
    const rolledUp = new Set<string>()
    const rollUp = (session: SessionSummary): void => {
      if (rolledUp.has(session.session_id)) return
      rolledUp.add(session.session_id)
      for (const child of session.subagents ?? []) rollUp(child)
      session.rollup = computeSessionRollup(session)
    }
    for (const session of rawSessions) rollUp(session)

    // Filter to root sessions only
    const rootSessions = rawSessions.filter((s) => !attachedChildIds.has(s.session_id))

    // Sort globally by started DESC
    rootSessions.sort((a, b) => (b.started ?? '').localeCompare(a.started ?? ''))

    if (typeof limit === 'number' && limit > 0) {
      // The limit stays here, after the nesting and the rollup: a SQL-side
      // LIMIT would count rows, not roots, and would cut the children of a
      // returned parent out of the tree (and out of its rolled-up counters).
      return rootSessions.slice(0, Math.floor(limit))
    }
    return rootSessions
  }

  /**
   * Read the rows the session list is built from, without touching the
   * `payload` blob unless a row forces it.
   *
   * <remarks>
   * Three shapes, in order:
   *
   * 1. The materialized read — the whole table, columns only.
   * 2. The legacy fallback — issued only when the primary result carries a
   *    row that is missing a materialized value, i.e. a row written before
   *    the materialization existed, not yet re-projected, or holding a
   *    payload the writer could not materialize both values for. It reads the
   *    payload of exactly those rows and is merged back by `session_id`.
   * 3. The payload read — the last resort, unchanged: taken when the SQLite
   *    build has no JSON functions (the fallback cannot run) or when the
   *    schema predates the materialized columns (the primary cannot run).
   * </remarks>
   */
  private readSessionListRows(db: DatabaseSync): SessionDbRow[] {
    let primary: SessionDbRow[]
    try {
      primary = db.prepare(SESSION_LIST_SELECT).all() as unknown as SessionDbRow[]
    } catch {
      // A schema without the materialized columns: only the payload read can
      // serve it.
      return this.readSessionListRowsFromPayload(db)
    }

    // A row is complete when it carries both materialized values. The
    // fallback covers every row that does not, and the two values are read
    // independently downstream — so a row that is missing one of them keeps
    // the other.
    const incomplete = primary.filter((row) => isMissingSessionColumn(row))
    if (incomplete.length === 0) return primary

    let legacy: SessionDbRow[]
    try {
      legacy = db.prepare(SESSION_LIST_LEGACY_SELECT).all() as unknown as SessionDbRow[]
    } catch {
      // No JSON functions in this SQLite build: the fallback cannot run, so
      // the payload read is the only way to serve the rows it would have
      // completed.
      return this.readSessionListRowsFromPayload(db)
    }

    const extracted = new Map(legacy.map((row) => [row.session_id, row]))
    for (const row of incomplete) {
      const legacyRow = extracted.get(row.session_id)
      if (legacyRow === undefined) continue
      // Only what the row is missing is filled in, one column at a time.
      fillMissingSessionColumns(row, legacyRow)
    }
    return primary
  }

  /**
   * The last resort: every row's payload, parsed in JS.
   *
   * <remarks>
   * Unchanged from the pre-materialization reader, so a database the fast
   * path cannot serve still lists.
   * </remarks>
   */
  private readSessionListRowsFromPayload(db: DatabaseSync): SessionDbRow[] {
    return db.prepare(SESSION_LIST_PAYLOAD_SELECT).all() as unknown as SessionDbRow[]
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
   *
   * <remarks>
   * The session's turns are read from `session_turn` — one range scan for the
   * whole session, however many turns it holds — and the rest of the session
   * from the payload row without them. A session's blob was 15.3 MB for a
   * 10,933-turn session, and reading it whole to serve its turns is what the
   * split exists to stop.
   *
   * Both shapes are in the store at once, and both are served the same way.
   * A row whose payload still carries a `turns` array was written before the
   * split, or by a build that still writes whole payloads: its turns are the
   * ones it carries, and they are the fresh ones — a re-write by such a build
   * leaves the split rows behind it stale. A row without one has had its
   * turns split out, and they are read from the turns table. The payload's
   * own `turns` key is the signal, because it is the one thing the split
   * leaves behind: the migration removed the key and moved the turns, and the
   * writer drops the key as it writes the rows.
   *
   * The turns are re-attached at the end of the payload, because the split
   * took the key out and nothing records where it stood.
   * </remarks>
   */
  getSessionPayload<T = SessionPayload>(sessionId: string): T | null {
    if (!sessionId) return null

    const db = this.sessionHandle()
    if (this.hasTable(db, 'session')) {
      const assembled = this.readSessionWithTurns(db!, sessionId)
      if (assembled !== undefined) return assembled === null ? null : (_clip(assembled) as T)
    }

    // The whole payload, as the store holds it: the last resort for a handle
    // that cannot serve the split read — a schema without the JSON functions
    // the fragments are taken with, or a store that answers the payload
    // itself. The turns are attached back from the turns table, so this path
    // serves the same session the split read does.
    if (this.store) {
      const payload = this.store.getSessionPayload(sessionId)
      if (payload === undefined) return null
      return _clip(this.attachSessionTurns(db, payload as SessionPayload, sessionId)) as T
    }
    if (this.hasTable(db, 'session')) {
      try {
        const row = db!.prepare(SESSION_PAYLOAD_SELECT).get(sessionId) as { payload?: unknown } | undefined
        if (row !== undefined && typeof row.payload === 'string') {
          const payload = JSON.parse(row.payload) as SessionPayload
          return _clip(this.attachSessionTurns(db!, payload, sessionId)) as T
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
   * One session's payload with its turns back, read from the split store.
   *
   * <remarks>
   * `null` when the store holds no such session, and `undefined` when this
   * handle cannot serve the split read at all — the caller's whole-payload
   * read is the fallback then.
   */
  private readSessionWithTurns(
    db: DatabaseSync,
    sessionId: string,
  ): Record<string, unknown> | null | undefined {
    let row: { payload_json?: unknown; turns_type?: unknown; turns_json?: unknown } | undefined
    try {
      row = db.prepare(SESSION_PAYLOAD_FRAGMENTS_SELECT).get(sessionId) as typeof row
    } catch (err) {
      // This handle cannot take the payload apart — no JSON functions in its
      // SQLite build. Anything else is not a missing capability and must not
      // read as an empty session.
      if (err instanceof Error && /no such function/i.test(err.message)) return undefined
      throw err
    }
    if (row === undefined) return null
    const payload = parseJsonObject(row.payload_json)
    if (payload === undefined) return undefined

    // The turns: the payload's own when it still carries them, the split rows
    // when they have been moved out. The range scan is issued either way — it
    // is one indexed lookup that answers whether the session has turn rows of
    // its own, and hands over every one of them when it does.
    //
    // Which of the two wins is the shared contract's rule, not this reader's:
    // the store's reader and the bridge's reader must answer alike, so the
    // decision lives in one place (`canon/session-read.ts`).
    const splitTurns = this.readSessionTurnRows(db, sessionId)
    payload['turns'] = sessionTurnsOf(row.turns_type, row.turns_json, splitTurns)
    return payload
  }

  /**
   * One session's turns, read from the turns table in one range scan.
   *
   * <remarks>
   * `undefined` when the store carries no turns table at all: the table is
   * materialized on demand, so a store that never held a turn row may not
   * have one — and a session in such a store has no turns anywhere. Any
   * other read failure is rethrown rather than served as an empty turn list.
   */
  private readSessionTurnRows(
    db: DatabaseSync,
    sessionId: string,
  ): Array<Record<string, unknown>> | undefined {
    if (!this.hasTable(db, 'session_turn')) return undefined
    let rows: Array<{ turn_json?: unknown }>
    try {
      rows = db.prepare(SESSION_TURNS_SELECT).all(sessionId) as Array<{ turn_json?: unknown }>
    } catch (err) {
      if (err instanceof Error && /no such table/i.test(err.message)) return undefined
      throw err
    }
    const turns: Array<Record<string, unknown>> = []
    for (const row of rows) {
      const turn = turnObjectOf(row.turn_json)
      // A row that does not parse is not a turn: it is dropped rather than
      // served as an empty object, which would read as "measured, and there
      // was nothing there".
      if (turn !== undefined) turns.push(turn)
    }
    return turns
  }

  /**
   * One session's identity and summary, read without its payload.
   *
   * <remarks>
   * The bounded read behind `GET /api/kyber/session/:id/summary`. A consumer
   * that needs a handful of identity fields should not pay for the whole
   * session blob — 15.3 MB and a multi-second cold page-in for a session with
   * 10,933 turns, to learn a handful of fields.
   *
   * The answer is bounded by construction, so no `_clip` is applied here. The
   * detail route clips because its payload is unbounded; clipping a summary
   * would only risk truncating a measured figure.
   *
   * A row that predates the materialization — or one the writer could not
   * materialize both values for — is read out of that one row's payload, the
   * transitional shape the session list already falls back to. The fallback
   * is per column, exactly as the list merges it: the payload fills only the
   * column the row is missing, so a materialized value is never replaced by
   * the payload's. The shape carries no run or execution linkage — neither
   * the `session` table nor the session payload holds such a field, so
   * serving one would invent a link the client does not have today.
   * </remarks>
   */
  getSessionSummary(sessionId: string): SessionSummaryDetail | undefined {
    const id = sessionId.trim()
    if (!id) return undefined

    const db = this.getDb()
    if (!this.hasTable(db, 'session')) return undefined

    let row: SessionDbRow | undefined
    try {
      row = db!.prepare(SESSION_SUMMARY_SELECT).get(id) as SessionDbRow | undefined
    } catch {
      // A schema without the materialized columns: this row's own payload is
      // the only place its summary lives.
      return this.readSessionSummaryFromPayload(db!, id)
    }
    if (row === undefined) return undefined

    // The two materialized columns are independent facts, and NULL is the
    // writer's per-column "not materialized" sentinel: a missing count never
    // discards a materialized summary, and a missing summary never hides a
    // materialized count. So the row is resolved column by column, the same
    // way the session list resolves it — a row carrying both is served from
    // the columns alone, with no payload read at all, and a row missing
    // either one falls back to that one row's payload for the missing
    // column only.
    if (isMissingSessionColumn(row)) {
      const legacy = this.readSessionSummaryLegacy(db!, id)
      if (legacy === undefined) return undefined
      fillMissingSessionColumns(row, legacy)
    }

    return this.buildSessionSummary(row, {
      summary:
        row.summary_json === null || row.summary_json === undefined
          ? {}
          : parseSummaryObject(row.summary_json),
      problems_count: Number(row.problems_count) || 0,
    })
  }

  /**
   * The two materialized columns of one row, read out of that row's payload.
   *
   * <remarks>
   * Returned as columns, not as served facts, so the caller merges them per
   * column: a value the row already carries is never replaced by the
   * payload's, and a value the payload does not carry leaves the row's own
   * NULL in place.
   */
  private readSessionSummaryLegacy(
    db: DatabaseSync,
    sessionId: string,
  ): { summary_json?: string | object | null; problems_count?: number | null } | undefined {
    try {
      const row = db
        .prepare(SESSION_SUMMARY_LEGACY_SELECT)
        .get(sessionId) as { summary_json?: string | null; problems_count?: number | null } | undefined
      if (row === undefined) return undefined
      return {
        summary_json: row.summary_json ?? null,
        problems_count: row.problems_count ?? null,
      }
    } catch {
      // No JSON functions in this SQLite build: the extraction cannot run, so
      // this row's payload is the only way to serve it.
      const row = db.prepare(SESSION_SUMMARY_PAYLOAD_SELECT).get(sessionId) as SessionDbRow | undefined
      if (row === undefined) return undefined
      const facts = summaryFromPayload(row.payload)
      return { summary_json: facts.summary, problems_count: facts.problems_count }
    }
  }

  /**
   * One row's summary, parsed from its own payload.
   *
   * <remarks>
   * The last resort, and the same reading the session list makes when the
   * materialized columns cannot serve it. One row only, so it never costs
   * more than the detail route's whole-payload read.
   * </remarks>
   */
  private readSessionSummaryFromPayload(
    db: DatabaseSync,
    sessionId: string,
  ): SessionSummaryDetail | undefined {
    const row = db.prepare(SESSION_SUMMARY_PAYLOAD_SELECT).get(sessionId) as SessionDbRow | undefined
    if (row === undefined) return undefined
    return this.buildSessionSummary(row, summaryFromPayload(row.payload))
  }

  /**
   * The served shape, from one row's columns plus its summary facts.
   */
  private buildSessionSummary(
    row: SessionDbRow,
    facts: { summary: Record<string, unknown>; problems_count: number },
  ): SessionSummaryDetail {
    return {
      session_id: row.session_id,
      harness: row.harness,
      label: row.label ?? null,
      // The column is an INTEGER; the session list reports it as a boolean,
      // and so does this.
      is_subagent: Boolean(row.is_subagent),
      parent_session: row.parent_session ?? null,
      agent_name: row.agent_name ?? null,
      started: row.started ?? null,
      ended: row.ended ?? null,
      summary: facts.summary,
      problems_count: facts.problems_count,
    }
  }

  /**
   * One turn's measured counters, read without the session's payload.
   *
   * <remarks>
   * The bounded read behind `GET /api/kyber/session/:id/turn/:index`. A
   * consumer that needs one turn's counters should not pay for the session
   * blob: the detail route served 10,933 turn rows to read four numbers.
   *
   * A session whose turns were never split out of their payload — a row
   * written before the split — is read the transitional way, with SQLite
   * walking the payload's own turn array and handing over the one turn that
   * matches. The identity rule is the writer's own, so both paths answer for
   * the same turn.
   * </remarks>
   */
  getSessionTurnFigures(sessionId: string, turnIndex: number): SessionTurnFigures | null {
    if (!sessionId || !Number.isInteger(turnIndex) || turnIndex < 0) return null
    const db = this.getDb()
    if (!db || !this.hasTable(db, 'session')) return null

    if (this.hasTable(db, 'session_turn')) {
      const row = db.prepare(SESSION_TURN_SELECT).get(sessionId, turnIndex) as
        | { turn_json?: unknown }
        | undefined
      const turn = row === undefined ? undefined : turnObjectOf(row.turn_json)
      if (turn !== undefined) return turnFiguresOf(turn, turnIndex)
      // No row for that index. A session that has turn rows of its own is
      // split, and its payload no longer carries them: the index is out of
      // range, and no payload is read to say so.
      if (db.prepare(SESSION_TURN_ROWS_SELECT).get(sessionId) !== undefined) return null
    }

    // A session written before the split still carries its turns in the
    // payload. That is the transitional read, and it hands over one turn.
    const legacy = db.prepare(SESSION_TURN_LEGACY_SELECT).get(sessionId, turnIndex) as
      | { turn_json?: unknown }
      | undefined
    if (legacy === undefined) return null
    const turn = turnObjectOf(legacy.turn_json)
    return turn === undefined ? null : turnFiguresOf(turn, turnIndex)
  }

  /**
   * One session's timeline, read without the session's payload.
   *
   * <remarks>
   * The bounded read behind `GET /api/kyber/timeline`. The timeline is
   * session metadata: it lives beside the turns, not inside them, so it
   * survives the payload split — and reading it never needs the turns that
   * were the blob's bulk.
   *
   * `null` when the store holds no such session: the route's 404. The
   * timeline itself is the payload's own value, so the served bytes are the
   * bytes the whole-payload read produced.
   * </remarks>
   */
  getSessionTimeline(sessionId: string): SessionTimelineRead | null {
    if (!sessionId) return null
    const db = this.getDb()
    if (!db || !this.hasTable(db, 'session')) return null

    const row = db.prepare(SESSION_TIMELINE_SELECT).get(sessionId) as
      | { timeline_json?: unknown }
      | undefined
    if (row === undefined) return null
    const timeline = parseJsonValue(row.timeline_json)
    // Clipped at the depth the timeline sat at inside the payload, so the
    // served bytes are the bytes the whole-payload read produced.
    return { timeline: timeline === undefined ? undefined : _clip(timeline, MAX_STRING_LENGTH, 1) }
  }

  /**
   * One session's persisted context analysis, read without the session's payload.
   *
   * <remarks>
   * The bounded read behind the report's latest-session block. The context
   * analysis — the per-turn pressure, buckets, residual and tool-definition
   * sources — lives in the payload's `context` key, while the payload also
   * carries the session's whole turn list, which this block needs none of.
   *
   * `null` when the store holds no such session.
   * </remarks>
   */
  getSessionContext(sessionId: string): SessionContextRead | null {
    if (!sessionId) return null
    const db = this.getDb()
    if (!db || !this.hasTable(db, 'session')) return null

    const row = db.prepare(SESSION_CONTEXT_SELECT).get(sessionId) as
      | { context_json?: unknown; last_turn_index?: unknown }
      | undefined
    if (row === undefined) return null
    const context = parseJsonObject(row.context_json)
    const lastTurnIndex = row.last_turn_index
    return {
      // Clipped at the depth the context block sat at inside the payload, so
      // the derived figures are the ones the whole-payload read produced.
      context: context === undefined ? undefined : _clip(context, MAX_STRING_LENGTH, 1),
      lastTurnIndex:
        typeof lastTurnIndex === 'number' && Number.isFinite(lastTurnIndex) ? lastTurnIndex : undefined,
    }
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
        // Copied only when set: the drawer labels a definition as offered on
        // `false`, so a resident part must come back with no key at all.
        if (piece.resident === false) entry.resident = false
        assembled.push(entry)
      }
    }
    // Canonical-but-absent is also an invalid part filter for this session/span.
    if (partFilter !== undefined && assembled.length === 0) return null

    const result: SessionContentResult = {
      sessionId,
      // Lossless callers pass `full` so content longer than the default
      // budget returns exact with no flags; everyone else keeps the capped
      // behavior with explicit `truncated`/`totalLength` markers. Flags are
      // only ever added by the budget below — never invented, and upstream
      // (storage-level) truncation stays visible wherever the store records
      // it rather than being papered over here.
      parts: applyContentBudget(assembled, options.full === true ? Number.POSITIVE_INFINITY : CONTENT_RESPONSE_BUDGET),
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
   *
   * Every part carries exact-match `recurrence` against earlier captured
   * turns of this session (same part kind, whole text, hashed), aggregated
   * per block; `sharedContext` carries every captured `system_prompt` part
   * in scope at or before this turn, in capture order, each with its own recurrence. Recurrence is
   * capture-only and never a cache claim — cache totals stay on the turn
   * row, unattributed to blocks.
   *
   * `budget` caps the returned characters with explicit `truncated` flags.
   * Pass `Number.POSITIVE_INFINITY` for the documented lossless path (the
   * route maps `?full=true` to it): no branch silently truncates, so content
   * longer than `CONTENT_RESPONSE_BUDGET` returns intact.
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

    // 1. Try resolving via session payload if available. The identity map is
    // built in one pass and shared by the target lookup and every
    // preceding-turn lookup below — O(1) per index, no repeated scans.
    const payload = this.getSessionPayload<SessionPayload>(sessionId)
    const turns: TurnDescriptor[] = Array.isArray(payload?.turns)
      ? (payload.turns as TurnDescriptor[])
      : []
    const payloadByIdentity = buildTransportIndex(turns, turnTransportIndexOf)
    let payloadTurn: TurnDescriptor | undefined
    if (turns.length > 0) {
      const turnItem = lookupTransport(turns, payloadByIdentity, turnTransportIndexOf, turnIndex)
      if (turnItem) {
        payloadTurn = turnItem
        if (typeof turnItem.spanId === 'string') targetSpanId = turnItem.spanId
        if (typeof turnItem.model === 'string') model = turnItem.model
      } else if (payloadByIdentity.size > 0) {
        // The payload names its turns and none matches: stop here. Falling
        // through to the positional record lookup below could serve a
        // neighboring span's content instead of the documented 404
        // (issue #184 review). Identity-free payloads still fall through.
        return null
      }
    }

    // Ordered record pool, loaded at most once and shared by the target
    // lookup and the preceding-turn reads (payload-less sessions).
    let pool: TurnResolutionPool | null | undefined
    const getPool = (): TurnResolutionPool | null => {
      if (pool === undefined) pool = this.loadTurnResolutionPool(sessionId, db)
      return pool
    }

    // 2. If targetSpanId not resolved yet, search canonical records
    if (!targetSpanId) {
      const resolvedPool = getPool()
      const resolved = lookupTransport(
        resolvedPool?.entries,
        resolvedPool?.byIdentity,
        (e: TurnResolutionEntry) => e.identity,
        turnIndex,
      )
      if (resolved) {
        targetSpanId = resolved.spanId
        model = resolved.model
      }
    }

    if (!targetSpanId) return null

    // Load unclipped content records for this span
    const contentRecords = this.loadContentRecords(sessionId, targetSpanId)
    const rawParts: ContentPart[] = contentRecords[0]?.parts ?? []
    // The turn's parent span is what a quarantined packet would be filed
    // under (B7); absent when the record named no parent.
    const parentSpanId = contentRecords[0]?.parentSpanId ?? null

    // Map and extract structured parts (system prompt, tools, user messages, assistant turns, etc.)
    const parts = this.mapTurnContentParts(rawParts, targetSpanId)

    // Captured-context recurrence: exact whole-part-text matches of the same
    // part kind in earlier captured turns. Only per-kind digest sets plus the
    // system-prompt candidate texts below are retained — each preceding part
    // is hashed and released turn-by-turn, never accumulated. No
    // normalization, no token-level similarity: a changed part is new unless
    // its exact text was captured before. Recurrence is independent of model
    // cache use and is never a cache claim; computed before budgeting so
    // truncation cannot rewrite it.
    const preceding = this.loadPrecedingTurnDigests(sessionId, turnIndex, turns, payloadByIdentity, getPool)
    for (const p of parts) {
      p.recurrence = { seenBefore: preceding.digests.get(p.part)?.has(sha256Hex(p.text)) ?? false }
    }

    // Session-level shared context: the union of the turn spans at or
    // before this turn (preceding plus current, each occurrence carrying its
    // contributing turnIndex) and every non-turn captured system_prompt in
    // session scope at or before this turn, in capture order. A mixed session
    // — an `llm.invoke` turn prompt coexisting with one or more non-turn
    // (e.g. `session.start`) prompts — keeps both: the old only-when-empty
    // fallback silently dropped the non-turn prompt as soon as any turn
    // prompt existed. Occurrence identity is the contributing turn index,
    // never the text alone: identical text from distinct turns stays
    // repeated, and one span serving two turn indexes contributes two
    // occurrences. Turn spans are excluded from the session scan by their
    // canonical operation (`llm.invoke`) as well as by the resolved turn
    // span set (never by text inference), so a turn span missing from the
    // payload turn list never appears as an unindexed duplicate.
    // Temporal scope comes from captured timestamps — a prompt provably
    // captured after this turn is withheld; missing, unparseable, or equal
    // timestamps stay included (see `sessionPromptAtOrBefore`), because the
    // corpus cannot order those captures and the reader must not invent it.
    // Each part's own recurrence follows the same exact-match rule against
    // strictly earlier turns; the client hides exact duplicates of the
    // turn's own parts and otherwise labels the section Shared session
    // context. `seenBefore` for the section holds when any part has earlier
    // evidence; parts without it show no recurrence badge (never "new" —
    // first capture is unverifiable outside the turn spans).
    const targetTimestamp = contentRecords[0]?.timestamp
    // Turn span ids known from the resolved turn set (payload first, so the
    // narrow seam pays no extra record walk; the already-loaded pool joins
    // when present). The session scan filters these out, so a turn span is
    // never contributed as an unindexed session duplicate — turn occurrences
    // arrive only with their contributing turnIndex below. Identity is never
    // inferred from text.
    const turnSpanIds = new Set<string>()
    for (const t of turns) {
      if (typeof t.spanId === 'string') turnSpanIds.add(t.spanId)
    }
    if (pool !== undefined && pool !== null) {
      for (const e of pool.entries) turnSpanIds.add(e.spanId)
    }
    type SharedCandidate = TurnContentSharedPart & { timestamp?: string; seq: number }
    const sharedCandidates: SharedCandidate[] = []
    let sharedSeq = 0
    for (const fallback of this.loadSessionSystemPrompts(sessionId, turnSpanIds)) {
      if (!sessionPromptAtOrBefore(fallback.timestamp, targetTimestamp)) continue
      const spanId = fallback.spanId ?? targetSpanId
      if (spanId === undefined) continue
      if (turnSpanIds.has(spanId)) continue
      sharedCandidates.push({ text: fallback.text, spanId, timestamp: fallback.timestamp, seq: sharedSeq++ })
    }
    for (const p of preceding.prompts) {
      sharedCandidates.push({ text: p.text, spanId: p.spanId, turnIndex: p.turnIndex, timestamp: p.timestamp, seq: sharedSeq++ })
    }
    // The current turn contributes its own occurrences under the requested
    // turn index — even when its span also served an earlier turn index. Each
    // full-turn copy is a distinct API-seen occurrence, so the same span id
    // at turn 0 and turn 1 yields two entries with different turnIndex
    // values. Same-turn repeated identical parts stay repeated.
    for (const p of parts.filter((p) => p.part === 'system_prompt' && p.text !== '')) {
      const spanId = p.spanId ?? targetSpanId
      sharedCandidates.push({ text: p.text, spanId, turnIndex, timestamp: targetTimestamp, seq: sharedSeq++ })
    }
    // Capture order: provable timestamps order the union; missing,
    // unparseable, or equal timestamps keep insertion order (stable sort) —
    // the corpus cannot order those captures and the reader must not invent it.
    sharedCandidates.sort((a, b) => {
      const aMs = a.timestamp === undefined ? Number.NaN : Date.parse(a.timestamp)
      const bMs = b.timestamp === undefined ? Number.NaN : Date.parse(b.timestamp)
      const aKnown = Number.isFinite(aMs)
      const bKnown = Number.isFinite(bMs)
      if (aKnown && bKnown && aMs !== bMs) return (aMs as number) - (bMs as number)
      return a.seq - b.seq
    })
    const sharedParts: TurnContentSharedPart[] = sharedCandidates.map(({ text, spanId, turnIndex: ti, truncated, totalLength }) =>
      ti === undefined ? { text, spanId, ...(truncated !== undefined ? { truncated } : {}), ...(totalLength !== undefined ? { totalLength } : {}) } : { text, spanId, turnIndex: ti },
    )
    let sharedContext: TurnContentSharedContext | null = null
    if (sharedParts.length > 0) {
      sharedContext = {
        parts: sharedParts,
        seenBefore: sharedParts.some(
          (c) => preceding.digests.get('system_prompt')?.has(sha256Hex(c.text)) ?? false,
        ),
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
      // A non-resident definition stays in the block's parts so the reader can
      // show it, but it adds no tokens: the block's figure is the resident one (D7).
      const tokens = uniqueParts.reduce((sum, p) => sum + (p.resident === false ? 0 : (p.tokens ?? 0)), 0) || undefined

      const block: TurnContentBlock = {
        key: def.key,
        label: def.label,
        parts: uniqueParts,
        text,
        tokens,
      }

      // Check measurability from payload — but only for a block this turn
      // has no parts for (R6/B6): the payload's declaration is
      // session-level, and a session-wide "not measurable" must not
      // survive onto a block the turn itself carries text for.
      if (uniqueParts.length === 0) {
        const flat = payload?.measurability as MeasurabilityMap | undefined
        const buckets = (payload?.context as { first?: { buckets?: MeasurabilityMap } } | undefined)
          ?.first?.buckets
        const measurability = flat?.[def.key] ?? buckets?.[def.key]
        if (measurability && typeof measurability === 'object' && measurability.availability === 'not_measurable') {
          block.notMeasurable = { reason: measurability.reason || 'Not measurable for this harness.' }
        }
      }

      // Block recurrence aggregates its rendered members: new when any
      // member part is new, seen when any member part exactly matches an
      // earlier same-kind part. Both hold for mixed blocks.
      block.recurrence = {
        newThisTurn: uniqueParts.some((p) => p.recurrence !== undefined && !p.recurrence.seenBefore),
        seenBefore: uniqueParts.some((p) => p.recurrence?.seenBefore === true),
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
    // Shared context is budgeted and flagged exactly like every other text:
    // a default response caps each part with explicit markers, while the
    // explicit lossless path (unbounded budget) returns every byte intact.
    if (sharedContext) {
      for (const sharedPart of sharedContext.parts) {
        if (sharedPart.text.length > budget) {
          sharedPart.totalLength = sharedPart.text.length
          sharedPart.text = sharedPart.text.slice(0, budget)
          sharedPart.truncated = true
        }
      }
    }

    // B7's raw-packet pointers. The turn's own span is a stored record, so
    // its attributes are always reachable; the parent's packet is linked
    // only when its quarantine row exists and kept a payload — a pointer
    // to nothing is worse than none.
    const rawPacket: TurnContentResult['rawPacket'] = {
      spanAttributesPath: `/api/kyber/span/${targetSpanId}/attributes`,
    }
    if (parentSpanId !== null) {
      const entry = this.getQuarantineEntry(parentSpanId)
      if (entry !== null && entry.rawPayload !== null) {
        rawPacket.interactionPath = `/api/kyber/quarantine/${parentSpanId}`
      }
    }

    const result: TurnContentResult = {
      sessionId,
      turnIndex,
      spanId: targetSpanId,
      ...(parentSpanId !== null ? { parentSpanId } : {}),
      model,
      blocks,
      parts,
      assembledText,
      rawPacket,
      ...(truncated ? { truncated, totalLength } : {}),
      sharedContext,
      skills: skillsContentFromPayload(payload, payloadTurn),
    }

    return result
  }

  /**
   * Structured turn parts for one span, in raw order: system prompt, tools,
   * instruction context, tool results, conversation history split into user
   * messages / assistant turns (or kept generic when unparseable), and any
   * other part kind passed through with its exact text. The current turn and
   * every preceding turn map through this same function so recurrence
   * compares identical kinds.
   */
  private mapTurnContentParts(rawParts: ContentPart[], spanId: string): TurnContentPart[] {
    const parts: TurnContentPart[] = []
    let orderCounter = 0

    for (const p of rawParts) {
      if (p.part === 'system_prompt') {
        parts.push({
          id: `part-sys-${orderCounter++}`,
          spanId,
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
          spanId,
          part: 'tool_definitions',
          label,
          text: p.text,
          tokens: p.tokens,
          server: p.server,
          order: p.order ?? orderCounter,
          ...(p.resident === false ? { resident: false } : {}),
        })
      } else if (p.part === 'instruction_context') {
        parts.push({
          id: `part-inst-${orderCounter++}`,
          spanId,
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
          spanId,
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
              spanId,
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
              spanId,
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
            spanId,
            part: 'user_messages',
            label: 'User Messages',
            text: p.text,
            tokens: p.tokens,
            order: p.order ?? orderCounter,
          })
          parts.push({
            id: `part-conv-${orderCounter++}`,
            spanId,
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
          spanId,
          part: p.part,
          label: String(p.part).replace(/_/g, ' '),
          text: p.text,
          tokens: p.tokens,
          server: p.server,
          order: p.order ?? orderCounter,
        })
      }
    }

    return parts
  }

  /**
   * Normalized turn-resolution row: the span an index resolves to plus its
   * explicit 0-based identity (`undefined` for position-only rows).
   */
  private loadTurnResolutionPool(
    sessionId: string,
    db: DatabaseSync | undefined,
  ): TurnResolutionPool | null {
    if (this.store) {
      const records = this.store.recordsForSession(sessionId)
      const turnRecords = records.filter((r) => r.op === 'llm.invoke')
      const pool = turnRecords.length > 0 ? turnRecords : records
      const entries = pool.map((r) => ({
        spanId: r.spanId,
        model: (r as CanonicalRecord & TurnDescriptor).model ?? r.name,
        identity: turnTransportIndexOf(r as CanonicalRecord & TurnDescriptor),
      }))
      return { entries, byIdentity: buildTransportIndex(entries, (e) => e.identity) }
    }
    if (this.hasTable(db, 'records')) {
      try {
        const rows = db!
          .prepare('SELECT * FROM records WHERE COALESCE(session_id, trace_id) = ? ORDER BY timestamp')
          .all(sessionId) as Record<string, unknown>[]
        const turnRows = rows.filter((r) => r.op === 'llm.invoke')
        const pool = turnRows.length > 0 ? turnRows : rows
        // DB rows predate the descriptor shape: `index` may arrive as a
        // numeric string, and a null/empty index is no identity (unlike
        // `Number(null)`, which coerces to 0 and would hijack turn 0).
        const entries = pool.map((r) => {
          const rawIndex: unknown = r.index
          const index =
            rawIndex === null || rawIndex === undefined || rawIndex === '' ? undefined : Number(rawIndex)
          return {
            spanId: String(r.span_id),
            model: String(r.name || ''),
            identity: turnTransportIndexOf({
              index: typeof index === 'number' && Number.isFinite(index) ? index : undefined,
              turn: r.turn,
            }),
          }
        })
        return { entries, byIdentity: buildTransportIndex(entries, (e) => e.identity) }
      } catch {
        return null
      }
    }
    return null
  }

  /**
   * Recurrence evidence for every captured turn before `turnIndex`: per-kind
   * digest sets plus the system-prompt candidate texts in turn order.
   * Resolution reuses the caller's one-pass payload index and the shared
   * pool (loaded at most once per response); content itself is read with
   * single-span gets — never a full-session content walk. Each preceding
   * part is hashed and released immediately: only digests and prompt texts
   * are retained, so prefix cost is one small digest per earlier part.
   * Comparisons stay exact and follow the same identity rules as the target
   * lookup (issue #184).
   */
  private loadPrecedingTurnDigests(
    sessionId: string,
    turnIndex: number,
    payloadTurns: TurnDescriptor[],
    payloadByIdentity: Map<number, TurnDescriptor>,
    getPool: () => TurnResolutionPool | null,
  ): {
    digests: Map<string, Set<string>>
    prompts: Array<{ text: string; spanId: string; turnIndex: number; timestamp?: string }>
  } {
    const digests = new Map<string, Set<string>>()
    const prompts: Array<{ text: string; spanId: string; turnIndex: number; timestamp?: string }> = []
    if (turnIndex <= 0) return { digests, prompts }
    const occurrences: Array<{ span: string; turnIndex: number }> = []
    for (let i = 0; i < turnIndex; i++) {
      let span: string | undefined
      if (payloadTurns.length > 0) {
        const item = lookupTransport(payloadTurns, payloadByIdentity, turnTransportIndexOf, i)
        if (item && typeof item.spanId === 'string') {
          span = item.spanId
        } else if (payloadByIdentity.size > 0) {
          // An identity-bearing payload that names no turn i contributes
          // nothing for i — same guard as the target lookup (issue #184).
          continue
        }
      }
      if (span === undefined) {
        const pool = getPool()
        const found = lookupTransport(pool?.entries, pool?.byIdentity, (e) => e.identity, i)
        if (found) span = found.spanId
      }
      if (span !== undefined) occurrences.push({ span, turnIndex: i })
    }
    // Load each distinct span once for digests, then replicate its system
    // prompts once per turn index that references it: complete turn copies
    // repeat a prompt, so the same span id at two turn indexes is two
    // API-seen occurrences with different turnIndex values.
    const cached = new Map<string, { texts: string[]; timestamp?: string }>()
    for (const { span } of occurrences) {
      if (cached.has(span)) continue
      const records = this.loadContentRecords(sessionId, span)
      const raw: ContentPart[] = records[0]?.parts ?? []
      const timestamp = records[0]?.timestamp
      const texts: string[] = []
      for (const p of this.mapTurnContentParts(raw, span)) {
        const set = digests.get(p.part) ?? new Set<string>()
        set.add(sha256Hex(p.text))
        digests.set(p.part, set)
        // System-prompt texts alone are retained (shared-context
        // candidates); every other part releases its text after hashing.
        if (p.part === 'system_prompt' && p.text !== '') {
          texts.push(p.text)
        }
      }
      cached.set(span, { texts, timestamp })
    }
    for (const { span, turnIndex: sourceIndex } of occurrences) {
      const entry = cached.get(span)
      if (!entry) continue
      for (const text of entry.texts) {
        prompts.push({ text, spanId: span, turnIndex: sourceIndex, timestamp: entry.timestamp })
      }
    }
    return { digests, prompts }
  }

  /**
   * Non-turn session-wide system prompts for the shared-context reader: every
   * captured `system_prompt` part outside `excludeSpan`, in session order.
   * Turn spans are excluded by their canonical operation (`llm.invoke`) as
   * well as by the caller's resolved span set, so a turn-derived prompt
   * missing from the payload turn list never masquerades as an unindexed
   * session-scope prompt; identity is never inferred from text. Always
   * combined with the turn spans' own prompts by the caller (a mixed
   * session keeps both); retains system prompts alone — never the
   * surrounding content. Each entry carries its row's captured timestamp so
   * the caller can withhold prompts captured after the turn being shown.
   *
   * <remarks>
   * Cost: this deliberately never calls `loadContentRecords(sessionId)` — that
   * walk selects every column (including the large `raw` blob) and inflates
   * every session row's parts just to keep the prompts. The store seam reads
   * only the content columns plus the short timestamp through
   * `CanonStore.systemPromptPartsForSession`, and the store-less seam runs the
   * same projection directly; rows carrying parts still inflate their own blob
   * to filter (deflated bytes admit no SQL index — an indexed pre-filter would
   * need a schema migration, out of scope), but unrelated rows are never
   * materialized into records and their other columns never leave SQLite.
   * Returned content, order, texts, and span ids match the old walk exactly.
   * </remarks>
   */
  private loadSessionSystemPrompts(
    sessionId: string,
    excludeSpans?: string | Set<string>,
  ): Array<{ text: string; spanId?: string; timestamp?: string }> {
    if (!sessionId) return []
    const isExcluded = (spanId: string): boolean => {
      if (excludeSpans === undefined) return false
      if (typeof excludeSpans === 'string') return spanId === excludeSpans
      return excludeSpans.has(spanId)
    }
    if (this.store) {
      return this.store
        .systemPromptPartsForSession(sessionId, excludeSpans)
        .map(({ spanId, text, timestamp }) => (timestamp === undefined ? { text, spanId } : { text, spanId, timestamp }))
    }
    const db = this.getDb()
    if (!this.hasTable(db, 'records')) return []
    try {
      const rows = db!
        .prepare(
          'SELECT span_id, op, timestamp, content_json, parts_json FROM records WHERE COALESCE(session_id, trace_id) = ? ORDER BY timestamp',
        )
        .all(sessionId) as { span_id: unknown; op: unknown; timestamp: unknown; content_json: unknown; parts_json: unknown }[]
      const out: Array<{ text: string; spanId?: string; timestamp?: string }> = []
      for (const row of rows) {
        if (row.op === 'llm.invoke') continue
        const spanId = String(row.span_id)
        if (isExcluded(spanId)) continue
        const timestamp = row.timestamp === null || row.timestamp === undefined ? undefined : String(row.timestamp)
        for (const text of systemPromptTextsFromContentColumns(row.content_json, row.parts_json)) {
          out.push(timestamp === undefined ? { text, spanId } : { text, spanId, timestamp })
        }
      }
      return out
    } catch {
      return []
    }
  }


  /**
   * Whether a derived `session` row exists for `sessionId`.
   *
   * <remarks>
   * Membership only, and deliberately so. "Does this session exist" is
   * answered by the session table's key: the row's presence is the fact, and
   * the session's content is not part of the answer. The check used to ask the
   * store for the session's payload, and that read is the split reader — it
   * decompresses the blob and reassembles every turn of the session out of
   * `session_turn`, 10,933 of them on the corpus's largest session — so every
   * existence check cost a full read of the session it was only sizing up.
   *
   * The seek costs the same whatever the session holds, so a session's size no
   * longer decides what an existence check pays.
   */
  private sessionRowExists(db: DatabaseSync | undefined, sessionId: string): boolean {
    if (!this.hasTable(db, 'session')) return false
    try {
      return db!.prepare(SESSION_EXISTS_SELECT).get(sessionId) !== undefined
    } catch {
      // Older or partial schemas still fall through to the records check.
      return false
    }
  }

  /**
   * A session is known if a derived row exists or any record keys to it.
   * The session table is cheap; records are the fallback for a corpus that
   * has not been built into `session` yet. Checking the table first avoids
   * clipping a large payload just to decide whether to 404.
   *
   * <remarks>
   * The derived row is probed, never read: the session's payload and its turns
   * are the answer to a different question, and reading them here made every
   * existence check reassemble the whole session before the caller could ask
   * for any of it.
   *
   * The store's handle is probed first — it is the one that holds the
   * sessions. The bridge's own handle is probed as well, for a bridge that
   * carries both a store and a handle of its own.
   */
  private sessionKnown(sessionId: string): boolean {
    const sessionDb = this.sessionHandle()
    if (this.sessionRowExists(sessionDb, sessionId)) return true
    const ownDb = this.getDb()
    if (ownDb !== sessionDb && this.sessionRowExists(ownDb, sessionId)) return true
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
            parentSpanId: parentSpanIdOf(row),
            parts: partsFromRow(row),
            timestamp: normalizeRecordTimestamp(row.timestamp),
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
        parentSpanId: parentSpanIdOf(row),
        parts: partsFromRow(row),
        timestamp: normalizeRecordTimestamp(row.timestamp),
      }))
    } catch {
      try {
        const rows = db!
          .prepare('SELECT * FROM records WHERE trace_id = ? ORDER BY timestamp')
          .all(sessionId) as Record<string, unknown>[]
        return rows.map((row) => ({
          spanId: String(row.span_id),
          sessionKey: sessionId,
          parentSpanId: parentSpanIdOf(row),
          parts: partsFromRow(row),
          timestamp: normalizeRecordTimestamp(row.timestamp),
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
   *
   * WHY streaming, not `listAll`: the comparison folds every canonical
   * record, including compressed payload columns. `listAll` roots the whole
   * corpus (every raw/parts payload live at once) while the aggregate runs.
   * The streaming walk below holds one live record plus bounded counters:
   * the store's `iterateAll` yields timestamp-ordered records one at a
   * time, and the direct-DB walk pages the same way without selecting the
   * `raw` column at all. Each record's parts are released before the next
   * arrives, so peak working set never grows with corpus size. Figures match
   * the batch path exactly via `compareIterableRecords`.
   */
  getComparisonTable(): ComparisonTableResult {
    try {
      const table =
        this.store !== undefined
          ? compareIterableRecords(
              this.store.iterateAll(),
              {},
              (record) => releaseRecordParts(record),
            )
          : this.compareDirectDb()
      if (table.harnesses.length === 0) {
        return { harnesses: [], rows: [], problems: [] }
      }
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
    } catch (err) {
      if (this.store !== undefined) throw err
      return { harnesses: [], rows: [], problems: [] }
    }
  }

  /**
   * Direct-DB streaming walk for bridges without an injected store: the same
   * one-record-at-a-time fold as the store path, over a cursor that never
   * selects the `raw` column (comparison never reads it) and never buffers
   * the corpus. Any read or decode failure yields the empty table, matching
   * the previous `SELECT *` path's catch-all.
   */
  private compareDirectDb(): ComparisonTable {
    const db = this.getDb()
    if (!this.hasTable(db, 'records')) {
      return { harnesses: [], rows: [], problems: [] }
    }
    try {
      const rows = db!
        .prepare(
          'SELECT span_id, trace_id, parent_span_id, source, harness, session_id, name, op, kind, timestamp, duration_ms, status, tokens_json, content_json, cost_json, measurability_json, parts_json FROM records ORDER BY timestamp',
        )
        .iterate() as unknown as IterableIterator<import('../canon/store.js').RecordRow>
      const records = (function* (): Generator<CanonicalRecord> {
        for (const row of rows) {
          const record = toRecord(row)
          try {
            yield record
          } finally {
            releaseRecordParts(record)
          }
        }
      })()
      return compareIterableRecords(records, {}, (record) => releaseRecordParts(record))
    } catch {
      return { harnesses: [], rows: [], problems: [] }
    }
  }

  /**
   * Quarantine rows, metadata-only (bounded — never the stored raw payload).
   * `harness` scopes the list: `undefined` is unfiltered (backwards-compatible),
   * `'unknown'` keeps unattributed sources only, and a canonical id keeps the stored
   * source spellings normalizing to it. The scope matches the stored source grouping,
   * never attribution — quarantined spans have no harness by construction.
   */
  getQuarantine(limit = 200, offset = 0, harness?: string): QuarantineRow[] {
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
      // A scope needs the column it matches on; without `source` no row can be scoped.
      if (harness !== undefined && !cols.has('source')) return []
      const { where, params } = quarantineHarnessWhere(harness)

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

      const sql = `SELECT ${selectCols.join(', ')} FROM quarantine${where} ORDER BY ${orderCol} DESC, span_id DESC LIMIT ? OFFSET ?`

      const rows = db!.prepare(sql).all(...params, safeLimit, safeOffset) as unknown as QuarantineDbRow[]
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

  /** Count quarantine rows under one scope, without applying the page limit. */
  getQuarantineCount(harness?: string): number {
    if (this.store) {
      // Unscoped keeps the legacy seam so older store fakes still throw their own
      // errors through; a scope needs the filtered seam (unfiltered would mis-scope).
      if (harness === undefined) return this.store.countQuarantine()
      return this.store.countQuarantineFiltered(harness)
    }
    const db = this.getDb()
    if (!this.hasTable(db, 'quarantine')) return 0
    if (harness !== undefined) {
      const cols = new Set(
        (db!.prepare("PRAGMA table_info('quarantine')").all() as Array<{ name: string }>).map((c) => c.name),
      )
      if (!cols.has('source')) return 0
    }
    const { where, params } = quarantineHarnessWhere(harness)
    const row = db!.prepare(`SELECT COUNT(*) AS n FROM quarantine${where}`).get(...params) as
      | { n?: number }
      | undefined
    return Number(row?.n) || 0
  }

  /**
   * One quarantine row in the detail shape the route serves (B7): every
   * field present, absent values explicit null. The list stays light — this
   * is the only bridge surface that returns a stored packet, and a row
   * written before the columns existed answers `rawPayload: null,
   * evidenceAvailable: false` rather than inventing evidence. `null` when
   * no row names the span.
   */
  getQuarantineEntry(spanId: string): QuarantineEntryDetail | null {
    if (!spanId) return null

    if (this.store) {
      return this.store.getQuarantineEntry(spanId) ?? null
    }

    const db = this.getDb()
    if (!this.hasTable(db, 'quarantine')) return null

    try {
      // The shared canon.db may predate the evidence columns; presence-check
      // each one exactly like getQuarantine does for its optional fields.
      const cols = new Set(
        (db!.prepare("PRAGMA table_info('quarantine')").all() as Array<{ name: string }>).map((c) => c.name),
      )
      const selectCols = ['span_id', 'namespaces', 'reason']
      for (const col of ['source', 'name', 'timestamp', 'harness', 'raw_payload']) {
        selectCols.push(cols.has(col) ? col : `NULL AS ${col}`)
      }
      const row = db!
        .prepare(`SELECT ${selectCols.join(', ')} FROM quarantine WHERE span_id = ?`)
        .get(spanId) as
        | {
            span_id: string
            namespaces: string | null
            reason: string | null
            source: string | null
            name: string | null
            timestamp: string | null
            harness: string | null
            raw_payload: string | null
          }
        | undefined
      if (row === undefined) return null

      let namespaces: string[] = []
      try {
        const parsed: unknown = JSON.parse(row.namespaces ?? '[]')
        if (Array.isArray(parsed)) {
          namespaces = parsed.filter((n): n is string => typeof n === 'string')
        }
      } catch {}

      return {
        spanId: row.span_id,
        source: row.source ?? null,
        name: row.name ?? null,
        reason: row.reason ?? '',
        timestamp: row.timestamp ?? null,
        namespaces,
        harness: row.harness ?? null,
        rawPayload: row.raw_payload ?? null,
        evidenceAvailable: row.raw_payload !== null && row.raw_payload !== undefined,
      }
    } catch (err) {
      console.warn('[KyberBridge] Failed querying quarantine entry from canon.db:', err)
      return null
    }
  }

  /**
   * Return recorded validation errors, token reconciliation mismatches, and anomalies.
   * Reads canonical diagnostics, deduplicated by the store's problem identity.
   * Metadata-only (bounded — never the exception stack); `harness` scopes like the
   * quarantine list (`undefined` is unfiltered and backwards-compatible).
   */
  getProblems(limit = 200, offset = 0, harness?: string): ProblemRow[] {
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

      // A scope needs the column it matches on; without `harness` no row can be scoped.
      if (harness !== undefined && !cols.has('harness')) return []
      const { where, params } = problemsHarnessWhere(harness)

      const sql = `SELECT ${selectCols.join(', ')} FROM ${canonTable}${where} ORDER BY id DESC LIMIT ? OFFSET ?`

      const rows = db!.prepare(sql).all(...params, safeLimit, safeOffset) as unknown as ProblemDbRow[]
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

  /** Count problem rows under one scope, without applying the page limit. */
  getProblemCount(harness?: string): number {
    if (this.store) {
      // Unscoped keeps the legacy seam so older store fakes still throw their own
      // errors through; a scope needs the filtered seam (unfiltered would mis-scope).
      if (harness === undefined) return this.store.countProblems()
      return this.store.countProblemsFiltered(harness)
    }
    const db = this.getDb()
    const table = this.hasTable(db, 'problems')
      ? 'problems'
      : this.hasTable(db, 'problem')
        ? 'problem'
        : null
    if (table === null) return 0
    if (harness !== undefined) {
      const cols = new Set(
        (db!.prepare(`PRAGMA table_info(${table})`).all() as Array<{ name: string }>).map((c) => c.name),
      )
      if (!cols.has('harness')) return 0
    }
    const { where, params } = problemsHarnessWhere(harness)
    const row = db!.prepare(`SELECT COUNT(*) AS n FROM ${table}${where}`).get(...params) as
      | { n?: number }
      | undefined
    return Number(row?.n) || 0
  }

  /**
   * One recorded problem with its exception evidence. `message` is the exact stored
   * message; `stack` is the exception stack when the problem came from a throw, null
   * otherwise. Absent id gives undefined (the route's 404).
   */
  getProblemEntry(id: number | string): ProblemDetail | undefined {
    const numeric = typeof id === 'number' ? id : /^\d+$/.test(id.trim()) ? Number(id.trim()) : NaN
    if (!Number.isSafeInteger(numeric)) return undefined
    const db = this.getDb()
    const canonTable = this.hasTable(db, 'problems')
      ? 'problems'
      : this.hasTable(db, 'problem')
        ? 'problem'
        : null
    if (!canonTable) return undefined
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
      selectCols.push(cols.has('stack') ? 'stack' : 'NULL AS stack')
      const row = db!.prepare(`SELECT ${selectCols.join(', ')} FROM ${canonTable} WHERE id = ?`).get(numeric) as
        | (ProblemDbRow & { stack?: unknown })
        | undefined
      if (row === undefined) return undefined
      return {
        id: row.id,
        session_id: row.session_id ?? null,
        span_id: row.span_id ?? null,
        severity: row.severity,
        code: row.code,
        message: row.message,
        at: row.at ?? row.timestamp ?? null,
        timestamp: row.timestamp ?? (typeof row.at === 'number' ? new Date(row.at * 1000).toISOString() : (row.at ? String(row.at) : null)),
        harness: row.harness ?? null,
        stack: typeof row.stack === 'string' && row.stack !== '' ? row.stack : null,
      }
    } catch (err) {
      console.warn('[KyberBridge] Failed querying problem entry from canon.db:', err)
      return undefined
    }
  }

  /**
   * The store's last clean stamp (`last_clean_at`), or null when the store cannot say.
   * Read through whichever handle this bridge owns: an injected store and its own
   * read-only file handle must agree, or the coverage window would depend on which path
   * the caller happened to take.
   */
  private lastCleanAt(): string | null {
    try {
      if (this.store) return this.store.getMetadata('last_clean_at') ?? null
      const db = this.getDb()
      if (!this.hasTable(db, 'metadata')) return null
      const row = db!.prepare('SELECT value FROM metadata WHERE key = ?').get('last_clean_at') as
        | { value?: unknown }
        | undefined
      return typeof row?.value === 'string' && row.value !== '' ? row.value : null
    } catch {
      // Unknown, not "never cleaned": an unreadable stamp must not retract a real window.
      return null
    }
  }

  /** True when `at` is strictly older than `since` (epoch compare, offset stamps included). */
  private stampedBefore(at: string, since: string | null): boolean {
    if (since === null) return false
    const atMs = Date.parse(at)
    const sinceMs = Date.parse(since)
    if (!Number.isFinite(atMs) || !Number.isFinite(sinceMs)) return false
    return atMs < sinceMs
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
    // A clean deletes the data this window described, and it does NOT delete the
    // refresh_run row that described it - that row is audit history. So after a wipe the
    // newest success row describes data that no longer exists, and the report would claim
    // coverage it does not have. `cleanDatabase` stamps `last_clean_at`; a success run that
    // STARTED before that stamp describes the wiped store and is ignored, while a run
    // that started after it describes the new data and counts again.
    const superseded = success !== undefined && this.stampedBefore(success.startedAt, this.lastCleanAt())
    const windowRun = superseded ? undefined : success
    const historyWeeks = windowRun?.historyWeeks ?? null
    const { coveredFrom, coveredThrough } = refreshWindowBounds(windowRun?.startedAt, historyWeeks)
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
      const conds = [this.sessionWindowSourceCondition(db!)]
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
   * The condition that selects the sessions whose context window no source
   * reported.
   *
   * Schema v22 materializes the window provenance into a `context_limit_source`
   * column, and the column carries the payload's `$.context.contextLimitSource`
   * verbatim, so the two paths count the same rows: a payload with no source
   * leaves the column NULL, and `NULL IN ('default', 'absent')` is not true,
   * so such a session is not counted either way.
   *
   * The payload expression stays the fallback for a database whose session
   * table predates the column -- a handle opened without the store that would
   * have migrated it -- so the count still answers there instead of failing on
   * a missing column.
   */
  private sessionWindowSourceCondition(db: DatabaseSync): string {
    const columns = (db.prepare('PRAGMA table_info(session)').all() as Array<{ name: string }>).map(
      (column) => column.name,
    )
    return columns.includes('context_limit_source')
      ? `context_limit_source IN ('default', 'absent')`
      : `json_extract(payload, '$.context.contextLimitSource') IN ('default', 'absent')`
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
   *
   * Both paths read the figures out of the materialized `summary_json`
   * column, and only a row written before the materialization existed — a
   * row whose `summary_json` is NULL — falls back to its own payload. The
   * read is the store's own (`readSessionSummaryFigures`), so the store mode
   * and the direct-DB mode cannot drift apart on what a session's figures
   * are.
   */
  sessionSummaryFigures(sessionIds: readonly string[]): SessionSummaryFigures {
    const uniqueIds = [...new Set(sessionIds)].filter((id) => id.length > 0)
    const out: SessionSummaryFigures = new Map()
    if (uniqueIds.length === 0) return out

    for (const row of this.readSessionSummaryFigureRows(uniqueIds)) {
      const cost = pricedUsd(row.costStatus, row.costValue, row.costCurrency, row.costUsdLegacy)
      out.set(row.sessionId, {
        ...(row.turnCount !== undefined ? { turnCount: row.turnCount } : {}),
        ...(row.totalInput !== undefined ? { totalInput: row.totalInput } : {}),
        ...(row.totalOutput !== undefined ? { totalOutput: row.totalOutput } : {}),
        ...(cost !== undefined ? { costUsd: cost } : {}),
        // Re-review #2 (Kilo B): the status a bare figure cannot carry.
        ...(row.costStatus === 'partial' && cost !== undefined ? { costPartial: true as const } : {}),
      })
    }
    return out
  }

  /**
   * Bounded session identifiers for analytics admission. This is deliberately
   * a projection-only read: loading `listSessions` here would parse every
   * payload before the service can reject an oversized scope.
   */
  analyticsSessionIds(scope: { from?: string; to?: string; harness?: string; repository?: string; branch?: string; sessionId?: string }, cap: number): string[] {
    const db = this.getDb()
    if (!this.hasTable(db, 'session') || !Number.isInteger(cap) || cap < 1) return []
    const where: string[] = []
    const params: string[] = []
    if (scope.from !== undefined) { where.push('started >= ?'); params.push(scope.from) }
    if (scope.to !== undefined) { where.push('started < ?'); params.push(scope.to) }
    if (scope.harness !== undefined) { where.push('harness = ?'); params.push(scope.harness) }
    if (scope.repository !== undefined) { where.push('repo = ?'); params.push(scope.repository) }
    if (scope.branch !== undefined) { where.push('branch = ?'); params.push(scope.branch) }
    if (scope.sessionId !== undefined) { where.push('session_id = ?'); params.push(scope.sessionId) }
    const predicate = where.length === 0 ? '' : ` WHERE ${where.join(' AND ')}`
    const rows = db!.prepare(`SELECT session_id FROM session${predicate} ORDER BY started DESC, session_id ASC LIMIT ?`).all(...params, cap) as Array<{ session_id: unknown }>
    return rows.map((row) => row.session_id).filter((id): id is string => typeof id === 'string')
  }

  /**
   * Metadata plus materialized summary figures for one bounded analytics batch.
   * The query never selects `payload`; legacy figure fallback remains centralized
   * in `sessionSummaryFigures`, preserving the existing compressed/materialized path.
   */
  analyticsSessionProjections(sessionIds: readonly string[]): AnalyticsSessionProjection[] {
    const ids = [...new Set(sessionIds)].filter((id) => id.length > 0)
    if (ids.length === 0) return []
    const db = this.getDb()
    if (!this.hasTable(db, 'session')) return []
    const placeholders = ids.map(() => '?').join(', ')
    const rows = db!.prepare(`SELECT session_id, label, started, harness, repo, branch, is_subagent, parent_session FROM session WHERE session_id IN (${placeholders})`).all(...ids) as Array<Record<string, unknown>>
    // The full figure rows — not the collapsed `sessionSummaryFigures` map —
    // so token classes, duration and the cost block's own basis/currency/
    // status survive into the analytics summaries. A figure that is missing
    // stays missing; fresh input is derived only when every class measured.
    const finite = (value: unknown): number | undefined =>
      typeof value === 'number' && Number.isFinite(value) ? value : undefined
    const figureById = new Map(this.readSessionSummaryFigureRows(ids).map((figure) => [figure.sessionId, figure]))
    return rows.flatMap((row) => {
      if (typeof row.session_id !== 'string' || typeof row.harness !== 'string') return []
      const figure = figureById.get(row.session_id)
      const inputTokens = finite(figure?.totalInput) ?? null
      const cacheReadTokens = finite(figure?.totalCacheRead) ?? null
      const cacheCreationTokens = finite(figure?.totalCacheCreation) ?? null
      const freshInputTokens =
        inputTokens !== null && cacheReadTokens !== null && cacheCreationTokens !== null
          ? inputTokens - cacheReadTokens - cacheCreationTokens
          : null
      const outputTokens = finite(figure?.totalOutput) ?? null
      const durationMs = finite(figure?.durationMs) ?? null
      const amount = finite(figure?.costValue) ?? finite(figure?.costUsdLegacy)
      return [{
        id: row.session_id,
        runId: null,
        label: typeof row.label === 'string' ? row.label : null,
        startedAt: typeof row.started === 'string' ? row.started : null,
        harness: row.harness,
        repository: typeof row.repo === 'string' ? row.repo : null,
        branch: typeof row.branch === 'string' ? row.branch : null,
        isSubagent: row.is_subagent === 1 || row.is_subagent === true,
        includedInParent: figure?.includedInParent === true,
        inputTokens,
        freshInputTokens,
        cacheReadTokens,
        cacheCreationTokens,
        outputTokens,
        durationMs,
        costs: amount === undefined
          ? []
          : [{
            amount,
            currency: typeof figure?.costCurrency === 'string' ? figure.costCurrency : 'USD',
            basis: typeof figure?.costBasis === 'string' ? figure.costBasis : 'unknown',
            status: typeof figure?.costStatus === 'string' ? figure.costStatus : 'measured',
          }],
      }]
    })
  }

  /**
   * One canonical content block from one exact turn, streamed through the
   * bounded captured-context column reader instead of a whole-column decode.
   *
   * <remarks>
   * The turn descriptor stays a small bounded decode, but the retained text
   * is never inflated whole behind a hard cap: `capturedSpanParts` walks the
   * column through bounded `substr` reads plus the streaming JSON decoder,
   * so a block larger than 64 KiB decoded pages instead of refusing. A
   * corrupt column still reads as unavailable, never as a neighboring turn.
   * When the column carries only the legacy content object, its buckets page
   * the same way — previously that shape was unavailable on this path.
   * </remarks>
   */
  async analyticsTurnBlock(request: { sessionId: string; turnIndex: number; blockKey: string; byteLimit: number; offset?: number }): Promise<{ text: string; byteLength: number; nextOffset: number | null } | undefined> {
    const { sessionId, turnIndex, blockKey, byteLimit, offset = 0 } = request
    if (!sessionId || !Number.isInteger(turnIndex) || turnIndex < 0 || !Number.isSafeInteger(byteLimit) || byteLimit < 1 || byteLimit > 16_384 || !Number.isSafeInteger(offset) || offset < 0) return undefined
    const db = this.getDb()
    if (!this.hasTable(db, 'session_turn') || !this.hasTable(db, 'records')) return undefined
    const turnRow = db!.prepare('SELECT turn_json FROM session_turn WHERE session_id = ? AND turn_index = ? LIMIT 1').get(sessionId, turnIndex) as { turn_json?: unknown } | undefined
    const turn = turnRow === undefined ? undefined : decompressColumnBounded(turnRow.turn_json, 65_536)
    if (typeof turn !== 'object' || turn === null || Array.isArray(turn)) return undefined
    const spanId = (turn as Record<string, unknown>).spanId
    if (typeof spanId !== 'string' || spanId.length === 0) return undefined
    const outcome = await this.capturedSpanParts(db!, spanId)
    if ('corrupt' in outcome || outcome.parts.length === 0) return undefined
    // WHY streamed order: parts arrive in canonical order, so joining the
    // block's pieces reproduces the retained text exactly, the way the
    // legacy whole-column decode did for well-formed rows.
    const text = outcome.parts
      .filter((part) => part.kind === blockKey && typeof part.text === 'string')
      .map((part) => part.text)
      .join('\n')
    if (text === '') return undefined
    const bytes = Buffer.from(text, 'utf8')
    if (offset >= bytes.byteLength) return undefined
    const end = Math.min(offset + byteLimit, bytes.byteLength)
    let safeEnd = end
    while (safeEnd > offset && (bytes[safeEnd] & 0xc0) === 0x80) safeEnd -= 1
    if (safeEnd === offset) return undefined
    return { text: bytes.subarray(offset, safeEnd).toString('utf8'), byteLength: safeEnd - offset, nextOffset: safeEnd < bytes.byteLength ? safeEnd : null }
  }

  /**
   * Complete captured-context traversal. The implementation is intentionally
   * asynchronous at the query seam: a request can span a retained compressed
   * column without making the synchronous SQLite handle pretend it can be
   * interrupted by a JavaScript timer.
   */
  private capturedColumnReader(
    db: DatabaseSync,
    table: 'records' | 'session_turn',
    column: 'parts_json' | 'content_json' | 'turn_json',
    identity: string | number,
  ): ColumnChunkReader | undefined {
    const supported =
      (table === 'records' && (column === 'parts_json' || column === 'content_json')) ||
      (table === 'session_turn' && column === 'turn_json')
    if (!supported) return undefined
    const predicate = table === 'records' ? 'span_id = ?' : 'rowid = ?'
    const storage = db.prepare(`SELECT typeof(${column}) AS storage FROM ${table} WHERE ${predicate} LIMIT 1`).get(identity) as { storage?: unknown } | undefined
    if (storage?.storage !== 'blob' && storage?.storage !== 'text') return undefined
    const read = db.prepare(`SELECT substr(CAST(${column} AS BLOB), ?, ?) AS chunk FROM ${table} WHERE ${predicate} LIMIT 1`)
    return {
      storage: storage.storage,
      read: (offset, length) => {
        if (!Number.isSafeInteger(offset) || offset < 0 || !Number.isSafeInteger(length) || length < 1 || length > 16_384) return null
        const row = read.get(offset + 1, length, identity) as { chunk?: unknown } | undefined
        const chunk = row?.chunk
        // An empty slice is the end of the column, not a zero-byte page:
        // the streaming decoder treats a zero-length read as a corrupt
        // column, so EOF must read as absent here.
        return chunk instanceof Uint8Array && chunk.byteLength > 0 ? chunk : null
      },
    }
  }

  private pruneCapturedContextTraversals(): void {
    const now = this.now()
    for (const [binding, traversal] of this.capturedContextTraversals) {
      if (now - traversal.touchedAt > CAPTURED_CONTEXT_TRAVERSAL_TTL_MS) {
        this.capturedContextTraversals.delete(binding)
      }
    }
  }

  /**
   * Retained traversal bookkeeping for tests: live entry count plus the
   * retained bytes across live entries. Page content is re-derived from the
   * store per request, so entries hold only their binding — this stays flat
   * no matter how many pages are served.
   */
  capturedContextTraversalStats(): { liveTraversals: number; retainedBytes: number } {
    let retainedBytes = 0
    for (const binding of this.capturedContextTraversals.keys()) {
      retainedBytes += Buffer.byteLength(binding, 'utf8')
    }
    return { liveTraversals: this.capturedContextTraversals.size, retainedBytes }
  }

  async analyticsCapturedContext(request: Record<string, unknown>, options?: { signal?: AbortSignal }): Promise<Record<string, unknown>> {
    // Complete captured-context traversal over the retained canonical
    // columns. Every content byte served here is streamed through bounded
    // `substr` reads plus the streaming JSON decoder — never a full-column
    // `decompressColumn`, never `assembleTurnContent(..., Infinity)`, and
    // never a whole-session record walk for content.
    this.pruneCapturedContextTraversals()
    const target = capturedContextTargetOf(request.target)
    if (target === null) return { error: 'content_unavailable' }
    const representation = request.representation === 'assembled_turn' ? 'assembled_turn' : 'captured_parts'
    if (representation === 'assembled_turn' && target.kind !== 'turn') return { error: 'content_unavailable' }
    const byteLimit =
      typeof request.byteLimit === 'number' && Number.isSafeInteger(request.byteLimit) && request.byteLimit >= 1
        ? Math.min(request.byteLimit, CAPTURED_CONTEXT_BYTE_LIMIT)
        : CAPTURED_CONTEXT_BYTE_LIMIT
    const limit =
      typeof request.limit === 'number' && Number.isSafeInteger(request.limit) && request.limit >= 1
        ? Math.min(request.limit, CAPTURED_CONTEXT_MANIFEST_MAX_LIMIT)
        : CAPTURED_CONTEXT_MANIFEST_DEFAULT_LIMIT
    const generation =
      typeof request.projectionGeneration === 'string' && request.projectionGeneration.length > 0
        ? request.projectionGeneration
        : 'unversioned'
    const cursor = typeof request.cursor === 'string' ? request.cursor : undefined
    if (request.mode === 'manifest' || request.mode === 'content') {
      try {
        return request.mode === 'manifest'
          ? await this.capturedContextManifest(target, representation, limit, cursor, generation, options?.signal)
          : await this.capturedContextContent(target, representation, byteLimit, cursor, generation, options?.signal)
      } catch (error: unknown) {
        // WHY cancelled is not a corrupt gap: the streaming decoder reports
        // an aborted read with the same error class as a torn column. Only
        // the corrupt code becomes a gap; cancellation answers cancelled
        // with no data, so a caller can tell stopping apart from damage.
        if (error instanceof StreamingJsonError && error.code === 'cancelled') return { error: 'cancelled' }
        throw error
      }
    }
    return { error: 'content_unavailable' }
  }

  /**
   * The canonical binding a captured-context cursor is sealed against: the
   * request shape plus the retained-content fingerprint. Any replay against
   * a different target, representation, byte or manifest limit, projection
   * generation, or changed retained bytes reseals differently, so the old
   * cursor reads as stale instead of serving the wrong page.
   */
  private capturedContextBinding(
    mode: 'manifest' | 'content',
    target: CapturedContextTarget,
    representation: 'captured_parts' | 'assembled_turn',
    byteLimit: number,
    limit: number,
    generation: string,
    fingerprint: string,
  ): string {
    return JSON.stringify({ mode, target, representation, byteLimit, limit, generation, fingerprint })
  }

  private capturedContextSeal(binding: string): string {
    return createHash('sha256').update(this.capturedContextKey).update('\0').update(binding).digest('hex')
  }

  private capturedContextCursor(binding: string, generation: string, offset: number): string {
    return Buffer.from(JSON.stringify({ v: 1, g: generation, h: this.capturedContextSeal(binding), o: offset }), 'utf8').toString('base64url')
  }

  /** A resumption offset, or null when the cursor is foreign, tampered, rebound, or regenerated. */
  private capturedContextOffset(cursor: string, binding: string, generation: string): number | null {
    let parsed: unknown
    try {
      parsed = JSON.parse(Buffer.from(cursor, 'base64url').toString('utf8'))
    } catch {
      return null
    }
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return null
    const record = parsed as Record<string, unknown>
    if (record.v !== 1 || record.g !== generation) return null
    if (typeof record.o !== 'number' || !Number.isSafeInteger(record.o) || record.o < 0) return null
    if (record.h !== this.capturedContextSeal(binding)) return null
    return record.o
  }

  /** Declared turn identities for one session, from the captured turn table — never a synthesized range. */
  private capturedTurnEntries(db: DatabaseSync, sessionId: string): Array<{ turnIndex: number; spanId: string | null }> | undefined {
    if (!this.hasTable(db, 'session_turn')) return undefined
    try {
      const rows = db
        .prepare('SELECT turn_index, turn_json FROM session_turn WHERE session_id = ? ORDER BY turn_index')
        .all(sessionId) as Array<{ turn_index?: unknown; turn_json?: unknown }>
      const entries: Array<{ turnIndex: number; spanId: string | null }> = []
      for (const row of rows) {
        if (typeof row.turn_index !== 'number' || !Number.isSafeInteger(row.turn_index) || row.turn_index < 0) continue
        // Turn rows are small descriptors; the bounded decode caps them well
        // above any descriptor while refusing a corrupt oversized blob.
        const decoded = decompressColumnBounded(row.turn_json, 65_536)
        let spanId: string | null = null
        if (typeof decoded === 'object' && decoded !== null && !Array.isArray(decoded)) {
          const candidate = (decoded as Record<string, unknown>).spanId
          if (typeof candidate === 'string' && candidate.length > 0) spanId = candidate
        }
        entries.push({ turnIndex: row.turn_index, spanId })
      }
      return entries
    } catch {
      return []
    }
  }

  /**
   * Non-turn session-scope spans in capture order. Turn spans are excluded
   * by their resolved span set as well as by their canonical operation, so
   * a turn span missing from the turn table never masquerades as an
   * unindexed duplicate — identity is never inferred from text.
   */
  private capturedSharedSpans(
    db: DatabaseSync,
    sessionId: string,
    exclude: ReadonlySet<string>,
    beforeOrAt?: string,
  ): Array<{ spanId: string }> {
    if (!this.hasTable(db, 'records')) return []
    try {
      const rows = db
        .prepare('SELECT span_id, op, timestamp FROM records WHERE COALESCE(session_id, trace_id) = ? ORDER BY timestamp')
        .all(sessionId) as Array<{ span_id?: unknown; op?: unknown; timestamp?: unknown }>
      const spans: Array<{ spanId: string }> = []
      for (const row of rows) {
        if (typeof row.span_id !== 'string' || row.span_id.length === 0 || exclude.has(row.span_id)) continue
        if (row.op === 'llm.invoke') continue
        const timestamp = typeof row.timestamp === 'string' ? row.timestamp : undefined
        if (beforeOrAt !== undefined && !sessionPromptAtOrBefore(timestamp, beforeOrAt)) continue
        spans.push({ spanId: row.span_id })
      }
      return spans
    } catch {
      return []
    }
  }

  private capturedRecordTimestamp(db: DatabaseSync, spanId: string): string | undefined {
    if (!this.hasTable(db, 'records')) return undefined
    try {
      const row = db.prepare('SELECT timestamp FROM records WHERE span_id = ? LIMIT 1').get(spanId) as { timestamp?: unknown } | undefined
      return typeof row?.timestamp === 'string' ? row.timestamp : undefined
    } catch {
      return undefined
    }
  }

  private capturedRecordExists(db: DatabaseSync, spanId: string): boolean {
    try {
      return db.prepare('SELECT 1 FROM records WHERE span_id = ? LIMIT 1').get(spanId) !== undefined
    } catch {
      return false
    }
  }

  /**
   * Cheap retained-content fingerprint: per-span `length()` of both content
   * columns, without reading any bytes. A purge (`parts_json` nulled) moves
   * the lengths, so a cursor sealed before the purge no longer matches.
   */
  private capturedColumnLengths(db: DatabaseSync, spanIds: readonly string[]): Map<string, string> {
    const lengths = new Map<string, string>()
    if (!this.hasTable(db, 'records')) return lengths
    try {
      for (let start = 0; start < spanIds.length; start += 500) {
        const chunk = spanIds.slice(start, start + 500)
        if (chunk.length === 0) continue
        const rows = db
          .prepare(`SELECT span_id, length(parts_json) AS parts, length(content_json) AS content FROM records WHERE span_id IN (${chunk.map(() => '?').join(',')})`)
          .all(...chunk) as Array<{ span_id?: unknown; parts?: unknown; content?: unknown }>
        for (const row of rows) {
          if (typeof row.span_id !== 'string') continue
          const parts = typeof row.parts === 'number' ? String(row.parts) : 'null'
          const content = typeof row.content === 'number' ? String(row.content) : 'null'
          lengths.set(row.span_id, `${parts}/${content}`)
        }
      }
    } catch {
      // A failed probe invalidates nothing by itself: spans absent from the
      // map fingerprint as unknown, which still fails a pre-probe binding.
    }
    return lengths
  }

  private capturedParentSession(db: DatabaseSync, sessionId: string): string | null {
    if (!this.hasTable(db, 'session')) return null
    try {
      const row = db.prepare('SELECT parent_session FROM session WHERE session_id = ? LIMIT 1').get(sessionId) as { parent_session?: unknown } | undefined
      return typeof row?.parent_session === 'string' && row.parent_session.length > 0 ? row.parent_session : null
    } catch {
      return null
    }
  }

  /**
   * One session's turn and session-shared units. Sparse turns — declared in
   * the turn table but without a captured record — contribute an explicit
   * `not_captured` gap, never a neighboring turn's span.
   */
  private appendSessionUnits(
    db: DatabaseSync,
    sessionId: string,
    units: CapturedContextUnit[],
    gaps: Array<Record<string, unknown>>,
    turnDescriptors: string[],
    declared: Array<{ sessionId: string; turnIndex: number; spanId: string | null }>,
    onlyTurnIndex?: number,
  ): void {
    const entries = this.capturedTurnEntries(db, sessionId)
    if (entries === undefined) {
      gaps.push({ code: 'unsupported_legacy', reason: 'The store does not expose split turn rows.', sessionId })
      return
    }
    const wanted = onlyTurnIndex === undefined ? entries : entries.filter((entry) => entry.turnIndex === onlyTurnIndex)
    if (onlyTurnIndex !== undefined && wanted.length === 0) {
      gaps.push({ code: 'not_captured', reason: 'The turn has no captured record.', sessionId, turnIndex: onlyTurnIndex })
      return
    }
    const exclude = new Set<string>()
    for (const entry of entries) {
      if (entry.spanId !== null) exclude.add(entry.spanId)
    }
    // Shared session context heads the scope — it is the preamble a reader
    // meets before the turn bulk, so a bounded first page still carries it.
    // A turn-scoped read whose own turn is uncaptured carries nothing at
    // all: shared context without its turn would read as a neighboring
    // turn's content under a sparse index.
    const turnUnits: CapturedContextUnit[] = []
    let gate: string | undefined
    for (const entry of wanted) {
      turnDescriptors.push(`${sessionId}:${entry.turnIndex}=${entry.spanId ?? ''}`)
      declared.push({ sessionId, turnIndex: entry.turnIndex, spanId: entry.spanId })
      if (entry.spanId === null) {
        gaps.push({ code: 'not_captured', reason: 'The declared turn names no captured span.', sessionId, turnIndex: entry.turnIndex })
        continue
      }
      if (!this.capturedRecordExists(db, entry.spanId)) {
        gaps.push({ code: 'not_captured', reason: 'The turn has no captured record.', sessionId, turnIndex: entry.turnIndex, spanId: entry.spanId })
        continue
      }
      turnUnits.push({ sessionId, turnIndex: entry.turnIndex, spanId: entry.spanId, origin: 'turn' })
      if (onlyTurnIndex !== undefined) gate = this.capturedRecordTimestamp(db, entry.spanId)
    }
    if (onlyTurnIndex === undefined || turnUnits.length > 0) {
      for (const shared of this.capturedSharedSpans(db, sessionId, exclude, gate)) {
        units.push({ sessionId, spanId: shared.spanId, origin: 'session_shared' })
      }
    }
    for (const turnUnit of turnUnits) units.push(turnUnit)
  }

  /** Ordered in-scope units plus the fingerprint sealing their retained bytes. */
  private resolveCapturedScope(
    db: DatabaseSync,
    target: CapturedContextTarget,
  ): {
    units: CapturedContextUnit[]
    gaps: Array<Record<string, unknown>>
    fingerprint: string
    declared: Array<{ sessionId: string; turnIndex: number; spanId: string | null }>
  } {
    const units: CapturedContextUnit[] = []
    const gaps: Array<Record<string, unknown>> = []
    const turnDescriptors: string[] = []
    const declared: Array<{ sessionId: string; turnIndex: number; spanId: string | null }> = []
    const executionDescriptors: string[] = []
    if (target.kind === 'run') {
      const executions = this.listExecutions(target.runId)
      if (executions.length === 0) {
        gaps.push({ code: 'not_captured', reason: 'The run has no captured executions.', runId: target.runId })
      }
      const seenSessions = new Set<string>()
      for (const execution of executions) {
        executionDescriptors.push(`${execution.executionId}=${execution.sessionId ?? ''}`)
        if (typeof execution.sessionId !== 'string' || execution.sessionId.length === 0) {
          gaps.push({ code: 'unknown_linkage', reason: 'The execution has no captured session identity.', executionId: execution.executionId })
          continue
        }
        if (seenSessions.has(execution.sessionId)) continue
        seenSessions.add(execution.sessionId)
        this.appendSessionUnits(db, execution.sessionId, units, gaps, turnDescriptors, declared)
      }
    } else if (target.kind === 'session') {
      this.appendSessionUnits(db, target.sessionId, units, gaps, turnDescriptors, declared)
    } else {
      this.appendSessionUnits(db, target.sessionId, units, gaps, turnDescriptors, declared, target.turnIndex)
    }
    const lengths = this.capturedColumnLengths(db, units.map((unit) => unit.spanId))
    const fingerprint = JSON.stringify({
      target,
      units: units.map((unit) => [unit.sessionId, unit.turnIndex ?? null, unit.spanId, unit.origin]),
      columns: units.map((unit) => lengths.get(unit.spanId) ?? 'unknown'),
      turns: turnDescriptors,
      executions: executionDescriptors,
    })
    return { units, gaps, fingerprint, declared }
  }

  /**
   * Stream one span's captured parts through bounded `substr` reads. Large
   * unrelated fields are parsed past, never accumulated; only the part
   * texts the caller retains cross the memory boundary. Legacy rows whose
   * content lives only in `content_json` fall back to that column, mirroring
   * the record reader's parts-when-present rule.
   */
  private async capturedSpanParts(
    db: DatabaseSync,
    spanId: string,
    signal?: AbortSignal,
  ): Promise<{ parts: CapturedContextPart[]; purged: boolean } | { corrupt: true }> {
    const partsReader = this.capturedColumnReader(db, 'records', 'parts_json', spanId)
    if (partsReader !== undefined) {
      const streamed = await this.streamPartsColumn(partsReader, signal)
      if (streamed === 'corrupt') return { corrupt: true }
      if (streamed !== null && streamed.length > 0) return { parts: streamed, purged: false }
    }
    const contentReader = this.capturedColumnReader(db, 'records', 'content_json', spanId)
    if (contentReader === undefined) return { parts: [], purged: partsReader === undefined }
    const legacy = await this.streamContentColumn(contentReader, signal)
    if (legacy === 'corrupt') return { corrupt: true }
    if (legacy.length > 0) return { parts: legacy, purged: false }
    return { parts: [], purged: partsReader === undefined }
  }

  /** Stream a `parts_json` array, collecting per-element identity, role, order, and text. */
  private async streamPartsColumn(reader: ColumnChunkReader, signal?: AbortSignal): Promise<'corrupt' | CapturedContextPart[] | null> {
    type Slot = { kind: string; role: string; order: unknown; resident: unknown; texts: string[] }
    const slots = new Map<number, Slot>()
    const slotFor = (index: number): Slot => {
      const existing = slots.get(index)
      if (existing !== undefined) return existing
      const slot: Slot = { kind: '', role: '', order: undefined, resident: undefined, texts: [] }
      slots.set(index, slot)
      return slot
    }
    let sawIndexed = false
    // WHY full-width reads: the reader caps substr at 16 KiB, and fewer
    // round-trips per scan is pure pacing — the decoded bytes are identical.
    const stream = new StreamingJsonReader(reader, { inputChunkBytes: 16_384 })
    try {
      for (;;) {
        const result = await stream.pump((event: JsonStringEvent) => {
          const head = event.path[0]
          if (typeof head === 'number') sawIndexed = true
          if (event.type === 'string-chunk') {
            if (event.path.length === 2 && typeof head === 'number') {
              const slot = slotFor(head)
              if (event.path[1] === 'text') slot.texts.push(event.text)
              else if (event.path[1] === 'part') slot.kind += event.text
              else if (event.path[1] === 'role') slot.role += event.text
            }
          } else if (event.type === 'scalar') {
            if (event.path.length === 2 && typeof head === 'number') {
              const slot = slotFor(head)
              if (event.path[1] === 'order' && typeof event.value === 'number') slot.order = event.value
              else if (event.path[1] === 'resident' && typeof event.value === 'boolean') slot.resident = event.value
            }
          }
        }, signal)
        if (result.kind === 'eof') break
      }
    } catch (error: unknown) {
      // WHY cancelled propagates: an aborted read is not a damaged column.
      // Only the corrupt code becomes a gap; cancellation must answer
      // cancelled with no data so the two stay distinguishable.
      if (error instanceof StreamingJsonError) {
        if (error.code === 'cancelled') throw error
        return 'corrupt'
      }
      throw error
    } finally {
      stream.dispose()
    }
    // A non-array root is not a parts list: the caller falls back to
    // `content_json`, the same rule the record reader applies.
    if (!sawIndexed) return null
    const ordered: CapturedContextPart[] = []
    for (const index of [...slots.keys()].sort((a, b) => a - b)) {
      const slot = slots.get(index)
      if (slot === undefined) continue
      const kind = slot.kind === '' ? 'unknown' : slot.kind
      ordered.push({
        kind,
        role: capturedRoleOf(kind, slot.role === '' ? null : slot.role),
        order: typeof slot.order === 'number' && Number.isFinite(slot.order) ? slot.order : null,
        resident: slot.resident === false ? false : true,
        text: slot.texts.join(''),
      })
    }
    ordered.sort((a, b) => (a.order ?? 0) - (b.order ?? 0))
    return ordered
  }

  /** Stream a legacy `content_json` object, collecting the canonical buckets in key order. */
  private async streamContentColumn(reader: ColumnChunkReader, signal?: AbortSignal): Promise<'corrupt' | CapturedContextPart[]> {
    const texts = new Map<string, string[]>()
    // WHY full-width reads: the reader caps substr at 16 KiB, and fewer
    // round-trips per scan is pure pacing — the decoded bytes are identical.
    const stream = new StreamingJsonReader(reader, { inputChunkBytes: 16_384 })
    try {
      for (;;) {
        const result = await stream.pump((event: JsonStringEvent) => {
          if (event.type !== 'string-chunk' || event.path.length !== 1) return
          const key = event.path[0]
          if (typeof key !== 'string' || !(CANONICAL_CONTENT_KEYS as readonly string[]).includes(key)) return
          const list = texts.get(key)
          if (list === undefined) texts.set(key, [event.text])
          else list.push(event.text)
        }, signal)
        if (result.kind === 'eof') break
      }
    } catch (error: unknown) {
      // WHY cancelled propagates: an aborted read is not a damaged column.
      // Only the corrupt code becomes a gap; cancellation must answer
      // cancelled with no data so the two stay distinguishable.
      if (error instanceof StreamingJsonError) {
        if (error.code === 'cancelled') throw error
        return 'corrupt'
      }
      throw error
    } finally {
      stream.dispose()
    }
    const parts: CapturedContextPart[] = []
    for (const [position, key] of CANONICAL_CONTENT_KEYS.entries()) {
      const text = (texts.get(key) ?? []).join('')
      if (text === '') continue
      parts.push({ kind: key, role: key === 'system_prompt' ? 'system' : null, order: position, resident: true, text })
    }
    return parts
  }

  /** Stream every in-scope unit's parts; spans yielding nothing become explicit gaps. */
  private async streamScopeParts(
    db: DatabaseSync,
    units: readonly CapturedContextUnit[],
    signal?: AbortSignal,
  ): Promise<{ streamed: Array<{ unit: CapturedContextUnit; parts: CapturedContextPart[] }>; gaps: Array<Record<string, unknown>> }> {
    const streamed: Array<{ unit: CapturedContextUnit; parts: CapturedContextPart[] }> = []
    const gaps: Array<Record<string, unknown>> = []
    for (const unit of units) {
      const outcome = await this.capturedSpanParts(db, unit.spanId, signal)
      if ('corrupt' in outcome) {
        gaps.push({
          code: 'corrupt_column',
          reason: 'A retained column could not be decoded.',
          sessionId: unit.sessionId,
          ...(unit.turnIndex === undefined ? {} : { turnIndex: unit.turnIndex }),
          spanId: unit.spanId,
        })
        continue
      }
      if (outcome.parts.length === 0) {
        gaps.push(
          outcome.purged
            ? { code: 'content_purged', reason: 'Retained content was purged after capture.', sessionId: unit.sessionId, spanId: unit.spanId }
            : { code: 'not_captured', reason: 'The span has no retained parts.', sessionId: unit.sessionId, spanId: unit.spanId },
        )
        continue
      }
      streamed.push({ unit, parts: outcome.parts })
    }
    return { streamed, gaps }
  }

  /**
   * Split streamed parts into page-sized chunks. Part identity is the span
   * plus the streamed position, so the manifest and the content pages agree;
   * repeated identical texts stay repeated — one chunk per occurrence — and
   * concatenating every page reproduces exactly the retained bytes.
   */
  private splitCapturedParts(
    streamed: ReadonlyArray<{ unit: CapturedContextUnit; parts: CapturedContextPart[] }>,
    byteLimit: number,
  ): CapturedContextChunk[] {
    const chunks: CapturedContextChunk[] = []
    for (const { unit, parts } of streamed) {
      parts.forEach((part, position) => {
        const pieces = splitUtf8Text(part.text, byteLimit)
        let byteOffset = 0
        pieces.forEach((text, pieceIndex) => {
          chunks.push({
            partId: `${unit.spanId}:part:${position}`,
            blockId: capturedBlockIdOf(part.kind),
            sessionId: unit.sessionId,
            ...(unit.turnIndex === undefined ? {} : { turnIndex: unit.turnIndex }),
            spanId: unit.spanId,
            origin: unit.origin,
            text,
            byteOffset,
            complete: pieceIndex === pieces.length - 1,
            role: part.role,
            order: part.order ?? position,
            resident: part.resident,
          })
          byteOffset += Buffer.byteLength(text, 'utf8')
        })
      })
    }
    return chunks
  }

  /**
   * A labelled reconstruction of one turn's retained parts, grouped
   * shared/user/assistant and headed as a reconstruction — never presented
   * as the exact model input. System and session-shared context head the
   * view, mirroring request order, so a bounded first page still labels
   * every group it had room to name.
   */
  private assembleCapturedTurn(
    sessionId: string,
    turnIndex: number,
    spanId: string,
    streamed: ReadonlyArray<{ unit: CapturedContextUnit; parts: CapturedContextPart[] }>,
    byteLimit: number,
  ): CapturedContextChunk[] {
    const user: string[] = []
    const assistant: string[] = []
    const shared: string[] = []
    for (const { unit, parts } of streamed) {
      for (const part of parts) {
        if (part.text === '') continue
        if (unit.origin === 'session_shared') shared.push(part.text)
        else if (part.role === 'user') user.push(part.text)
        else if (part.role === 'assistant') assistant.push(part.text)
        else shared.push(part.text)
      }
    }
    const sections: Array<[string, string[]]> = [['SHARED', shared], ['USER', user], ['ASSISTANT', assistant]]
    const bodies = sections
      .filter((section) => section[1].length > 0)
      .map((section) => `=== ${section[0]} ===\n${section[1].join('\n\n')}`)
    const full = ['Assembled turn view: a labelled reconstruction of retained captured parts, not the exact model input.', ...bodies].join('\n\n')
    const pieces = splitUtf8Text(full, byteLimit)
    if (pieces.length === 0) return []
    const chunks: CapturedContextChunk[] = []
    let byteOffset = 0
    pieces.forEach((text, pieceIndex) => {
      chunks.push({
        partId: `assembled:turn:${pieceIndex}`,
        blockId: 'assembled_turn',
        sessionId,
        turnIndex,
        spanId,
        origin: 'turn',
        text,
        byteOffset,
        complete: pieceIndex === pieces.length - 1,
        role: null,
        order: pieceIndex,
        resident: true,
      })
      byteOffset += Buffer.byteLength(text, 'utf8')
    })
    return chunks
  }

  /** Greedily fill 60 KB pages; a single chunk always fits, so progress is guaranteed. */
  private splitContentPages(
    target: CapturedContextTarget,
    representation: 'captured_parts' | 'assembled_turn',
    chunks: readonly CapturedContextChunk[],
    gaps: readonly Record<string, unknown>[],
  ): CapturedContextChunk[][] {
    // WHY incremental accounting: re-stringifying the growing page per
    // chunk is quadratic in page count. Prefix/suffix lengths plus per-chunk
    // JSON lengths reproduce the same encoded size exactly — same key order,
    // same sealed placeholder cursor — without the repeated re-encode.
    const prefixLength = Buffer.byteLength(
      `{"data":{"target":${JSON.stringify(target)},"representation":${JSON.stringify(representation)},"chunks":[`,
      'utf8',
    )
    const suffixLength = Buffer.byteLength(
      `],"coverage":${JSON.stringify({ completeness: 'captured_only', gaps })},"pagination":${JSON.stringify({ nextCursor: CAPTURED_CONTEXT_CURSOR_PLACEHOLDER, hasMore: true })}}`,
      'utf8',
    )
    const pages: CapturedContextChunk[][] = []
    let current: CapturedContextChunk[] = []
    let currentBytes = prefixLength + suffixLength
    for (const chunk of chunks) {
      const chunkBytes = Buffer.byteLength(JSON.stringify(chunk), 'utf8')
      if (current.length > 0 && currentBytes + 1 + chunkBytes > CAPTURED_CONTEXT_PAGE_BUDGET) {
        pages.push(current)
        current = []
        currentBytes = prefixLength + suffixLength
      }
      current.push(chunk)
      currentBytes += (current.length > 1 ? 1 : 0) + chunkBytes
    }
    if (current.length > 0) pages.push(current)
    return pages.length === 0 ? [[]] : pages
  }

  private serveContentPage(
    binding: string,
    generation: string,
    target: CapturedContextTarget,
    representation: 'captured_parts' | 'assembled_turn',
    gaps: ReadonlyArray<Record<string, unknown>>,
    page: CapturedContextChunk[],
    offset: number,
    last: boolean,
  ): Record<string, unknown> {
    if (last) this.capturedContextTraversals.delete(binding)
    return {
      data: { target, representation, chunks: page },
      coverage: { completeness: 'captured_only', gaps: [...gaps] },
      pagination: { nextCursor: last ? null : this.capturedContextCursor(binding, generation, offset + 1), hasMore: !last },
    }
  }

  private async capturedContextContent(
    target: CapturedContextTarget,
    representation: 'captured_parts' | 'assembled_turn',
    byteLimit: number,
    cursor: string | undefined,
    generation: string,
    signal?: AbortSignal,
  ): Promise<Record<string, unknown>> {
    const db = this.sessionHandle()
    if (db === undefined) return { error: 'content_unavailable' }
    const resolved = this.resolveCapturedScope(db, target)
    const binding = this.capturedContextBinding('content', target, representation, byteLimit, CAPTURED_CONTEXT_MANIFEST_DEFAULT_LIMIT, generation, resolved.fingerprint)
    let offset = 0
    if (cursor !== undefined) {
      const parsed = this.capturedContextOffset(cursor, binding, generation)
      const traversal = parsed === null ? undefined : this.capturedContextTraversals.get(binding)
      if (parsed === null || traversal === undefined) return { error: 'stale_cursor' }
      traversal.touchedAt = this.now()
      offset = parsed
    }
    // WHY re-derived, not retained: the page list below is transient working
    // memory, dropped on return. The registry keeps only the binding's
    // liveness, so serving page N of a large transcript never holds pages
    // 0..N — retention stays flat no matter how many pages are served.
    const { streamed, gaps: streamGaps } = await this.streamScopeParts(db, resolved.units, signal)
    const gaps = [...resolved.gaps, ...streamGaps]
    const chunks =
      representation === 'assembled_turn' && target.kind === 'turn'
        ? this.assembleCapturedTurn(target.sessionId, target.turnIndex, streamed.find((entry) => entry.unit.origin === 'turn')?.unit.spanId ?? '', streamed, byteLimit)
        : this.splitCapturedParts(streamed, byteLimit)
    const pages = this.splitContentPages(target, representation, chunks, gaps)
    const page = pages[offset]
    if (page === undefined) return { error: 'stale_cursor' }
    const last = offset === pages.length - 1
    if (pages.length > 1) {
      if (!this.capturedContextTraversals.has(binding) && this.capturedContextTraversals.size >= CAPTURED_CONTEXT_MAX_TRAVERSALS) {
        return { error: 'budget_exceeded' }
      }
      this.capturedContextTraversals.set(binding, { binding, touchedAt: this.now() })
    }
    return this.serveContentPage(binding, generation, target, representation, gaps, page, offset, last)
  }

  private serveManifestPage(
    binding: string,
    generation: string,
    target: CapturedContextTarget,
    representation: 'captured_parts' | 'assembled_turn',
    gaps: ReadonlyArray<Record<string, unknown>>,
    page: Array<Record<string, unknown>>,
    offset: number,
    last: boolean,
  ): Record<string, unknown> {
    if (last) this.capturedContextTraversals.delete(binding)
    return {
      data: { target, representation, items: page },
      coverage: { completeness: 'captured_only', gaps: [...gaps] },
      pagination: { nextCursor: last ? null : this.capturedContextCursor(binding, generation, offset + 1), hasMore: !last },
    }
  }

  private async capturedContextManifest(
    target: CapturedContextTarget,
    representation: 'captured_parts' | 'assembled_turn',
    limit: number,
    cursor: string | undefined,
    generation: string,
    signal?: AbortSignal,
  ): Promise<Record<string, unknown>> {
    const db = this.sessionHandle()
    if (db === undefined) return { error: 'content_unavailable' }
    const resolved = this.resolveCapturedScope(db, target)
    const binding = this.capturedContextBinding('manifest', target, representation, CAPTURED_CONTEXT_BYTE_LIMIT, limit, generation, resolved.fingerprint)
    let offset = 0
    if (cursor !== undefined) {
      const parsed = this.capturedContextOffset(cursor, binding, generation)
      const traversal = parsed === null ? undefined : this.capturedContextTraversals.get(binding)
      if (parsed === null || traversal === undefined) return { error: 'stale_cursor' }
      traversal.touchedAt = this.now()
      offset = parsed
    }
    const items = await this.buildManifestItems(db, target, resolved.units, resolved.declared, signal)
    const gaps = [...resolved.gaps]
    const pages: Array<Array<Record<string, unknown>>> = []
    for (let start = 0; start < items.length; start += limit) {
      pages.push(items.slice(start, start + limit))
    }
    const page = pages[offset] ?? (offset === 0 ? [] : undefined)
    if (page === undefined) return { error: 'stale_cursor' }
    const last = offset === pages.length - 1 || (pages.length === 0 && offset === 0)
    // WHY re-derived, not retained: same rule as the content path — the
    // item pages above are transient, and the registry keeps only liveness.
    if (pages.length > 1) {
      if (!this.capturedContextTraversals.has(binding) && this.capturedContextTraversals.size >= CAPTURED_CONTEXT_MAX_TRAVERSALS) {
        return { error: 'budget_exceeded' }
      }
      this.capturedContextTraversals.set(binding, { binding, touchedAt: this.now() })
    }
    return this.serveManifestPage(binding, generation, target, representation, gaps, page, offset, last)
  }

  /**
   * Manifest identities only — never captured text bodies. Turn items carry
   * the session/turn/span identity; part items carry the streamed
   * part/block/origin/role identity the content pages chunk under, so a
   * caller can plan traversal from the manifest alone.
   */
  private async buildManifestItems(
    db: DatabaseSync,
    target: CapturedContextTarget,
    units: readonly CapturedContextUnit[],
    declared: ReadonlyArray<{ sessionId: string; turnIndex: number; spanId: string | null }>,
    signal?: AbortSignal,
  ): Promise<Array<Record<string, unknown>>> {
    const items: Array<Record<string, unknown>> = []
    if (target.kind === 'run') {
      const executions = this.listExecutions(target.runId)
      const sessionExecution = new Map<string, string>()
      for (const execution of executions) {
        const sessionId = typeof execution.sessionId === 'string' && execution.sessionId.length > 0 ? execution.sessionId : null
        items.push({
          kind: 'execution',
          runId: execution.runId,
          executionId: execution.executionId,
          parentExecutionId: execution.parentExecutionId ?? null,
          sessionId,
          parentSessionId: sessionId === null ? null : this.capturedParentSession(db, sessionId),
          isSubagent: !execution.isRoot,
        })
        if (sessionId !== null && !sessionExecution.has(sessionId)) sessionExecution.set(sessionId, execution.executionId)
      }
      for (const turn of declared) {
        items.push({
          kind: 'turn',
          runId: target.runId,
          executionId: sessionExecution.get(turn.sessionId) ?? null,
          sessionId: turn.sessionId,
          turnIndex: turn.turnIndex,
          spanId: turn.spanId,
        })
      }
      const { streamed } = await this.streamScopeParts(db, units, signal)
      for (const { unit, parts } of streamed) {
        parts.forEach((part, position) => {
          items.push({
            kind: 'part',
            partId: `${unit.spanId}:part:${position}`,
            blockId: capturedBlockIdOf(part.kind),
            sessionId: unit.sessionId,
            ...(unit.turnIndex === undefined ? {} : { turnIndex: unit.turnIndex }),
            spanId: unit.spanId,
            origin: unit.origin,
            role: part.role,
            order: part.order ?? position,
          })
        })
      }
      return items
    }
    if (target.kind === 'session') {
      // Declared turn identities straight from the turn table — a declared
      // but uncaptured turn stays a listed gap, never a neighbor's span.
      for (const turn of declared) {
        items.push({ kind: 'turn', sessionId: turn.sessionId, turnIndex: turn.turnIndex, spanId: turn.spanId })
      }
      for (const unit of units) {
        if (unit.origin === 'session_shared') {
          items.push({ kind: 'shared', sessionId: unit.sessionId, spanId: unit.spanId, origin: 'session_shared' })
        }
      }
      return items
    }
    const { streamed } = await this.streamScopeParts(db, units, signal)
    for (const { unit, parts } of streamed) {
      parts.forEach((part, position) => {
        items.push({
          kind: 'part',
          partId: `${unit.spanId}:part:${position}`,
          blockId: capturedBlockIdOf(part.kind),
          sessionId: unit.sessionId,
          ...(unit.turnIndex === undefined ? {} : { turnIndex: unit.turnIndex }),
          spanId: unit.spanId,
          origin: unit.origin,
          role: part.role,
          order: part.order ?? position,
        })
      })
    }
    return items
  }

  /**
   * The figures of a set of sessions, read from whichever handle this bridge
   * has.
   *
   * <remarks>
   * With a store injected, the store's own reader answers; without one, this
   * bridge's handle does. Both are the same read, so the two paths agree.
   * A direct read that fails leaves the figures empty rather than taking the
   * caller down with it.
   * </remarks>
   */
  private readSessionSummaryFigureRows(sessionIds: readonly string[]): SessionSummaryFigureRow[] {
    if (this.store) return this.store.sessionSummaryFigures(sessionIds)
    const db = this.getDb()
    if (!this.hasTable(db, 'session')) return []
    try {
      return readSessionSummaryFigures(db!, sessionIds)
    } catch (err) {
      console.warn('[KyberBridge] Failed querying session summaries from canon.db:', err)
      return []
    }
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
      const payload = this.readSessionBlobPayload(sessionId)
      if (payload === null) continue
      for (const execution of group) {
        yield { execution, payload }
      }
    }
  }

  /**
   * One session's whole payload, as the store holds it.
   *
   * <remarks>
   * The run digest reads whole sessions, one at a time, and wants the blob
   * the store holds: the store's own read is the canonical one, and the
   * digest's laziness contract — one payload parsed per pulled session — is
   * measured against it. A session whose turns have been split out of the
   * payload gets them attached back from the turns table, so the digest sees
   * the same session the detail route serves.
   */
  private readSessionBlobPayload(sessionId: string): SessionPayload | null {
    const db = this.sessionHandle()
    if (this.store) {
      const payload = this.store.getSessionPayload(sessionId)
      if (payload === undefined) return null
      return _clip(this.attachSessionTurns(db, payload as SessionPayload, sessionId))
    }
    if (!this.hasTable(db, 'session')) return null
    try {
      const row = db!.prepare(SESSION_PAYLOAD_SELECT).get(sessionId) as { payload?: unknown } | undefined
      if (row === undefined || typeof row.payload !== 'string') return null
      const payload = JSON.parse(row.payload) as SessionPayload
      return _clip(this.attachSessionTurns(db, payload, sessionId))
    } catch (err) {
      console.warn(
        '[KyberBridge] Error reading session payload from canon.db for %s: %s',
        logId(sessionId),
        logErr(err),
      )
      return null
    }
  }

  /**
   * Attach the session's split-out turns to a payload that carries none.
   *
   * <remarks>
   * A payload that still carries its turns is left alone: those are its own
   * turns, and they are the fresh ones.
   */
  private attachSessionTurns(
    db: DatabaseSync | undefined,
    payload: SessionPayload,
    sessionId: string,
  ): SessionPayload {
    if (Array.isArray(payload.turns) || db === undefined) return payload
    const turns = this.readSessionTurnRows(db, sessionId)
    if (turns !== undefined) payload.turns = turns
    return payload
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
