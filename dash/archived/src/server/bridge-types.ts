// The bridge's result, option and figure types, and the small figure helpers
// they carry. Split from `./bridge.ts`, which re-exports every name here that it
// exported before.

import { CanonStore } from '../canon/store.js'
import { type SourceKind } from '../canon/measurability.js'
import type { Finding } from '../analysis/findings.js'
import { type CanonicalContentKey, type CostBlock, type ExecutionRow, type NotMeasurable } from '../canon/types.js'
import type { CostBasisMismatch } from '../canon/cost.js'
import type { AsadSessionPayload } from '../canon/sessions.js'
import { type TurnSkillsContent } from './bridge-content.js'
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
export function validPageNumber(value: number | undefined): number | undefined {
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
export function* asadPayloads(
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
export type FileIdentity = { dev: bigint; ino: bigint }

/** Default interval between reopen probes when `reopenCheckIntervalMs` is not given. */
export const DEFAULT_REOPEN_CHECK_INTERVAL_MS = 1000

/**
 * Minimum gap between two "failed to stat" warnings for the same bridge
 * instance while the failure keeps recurring. A persistent, non-ENOENT stat
 * failure (EACCES, EMFILE, ELOOP, ...) must keep giving an operator a symptom
 * rather than warning once and then serving stale data silently forever —
 * see the durability note on {@link KyberBridge.reconcile}. Measured by the
 * injected `now`, so it is deterministic under test.
 */
export const STAT_FAILURE_WARN_INTERVAL_MS = 60_000

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
