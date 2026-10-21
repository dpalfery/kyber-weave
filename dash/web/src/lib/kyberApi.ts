export type KyberTimelineNode = import('@/components/analysis/TimelineView').TimelineNode
export type KyberComparisonTable = {
  harnesses: string[]
  rows: Array<{
    metric: string
    kind: 'per_turn' | 'total'
    label: string
    unit: string
    cells: Record<
      string,
      {
        measurable: boolean
        availability: string
        value?: number
        basis?: string
        currency?: string
        render: string
      }
    >
  }>
  problems: Array<{ severity: string; code: string; message: string }>
}
export type KyberQuarantineEntry = import('@/components/analysis/QuarantineView').QuarantineEntry
export type KyberProblemEntry = import('@/components/analysis/ProblemsView').ProblemEntry

/** Thrown when a REST call is not 2xx. `status` is how 404 becomes not-found (R5.4). */
export class KyberApiError extends Error {
  readonly status: number
  readonly path: string
  constructor(status: number, path: string) {
    super(`Request failed (${status}) for ${path}`)
    this.name = 'KyberApiError'
    this.status = status
    this.path = path
  }
}

export function isNotFoundError(error: unknown): boolean {
  if (error instanceof KyberApiError) return error.status === 404
  return typeof error === 'object' && error !== null && 'status' in error && (error as { status: unknown }).status === 404
}

async function fetchJson<T>(path: string): Promise<T> {
  const res = await fetch(path)
  if (!res.ok) throw new KyberApiError(res.status, path)
  return res.json() as Promise<T>
}

export function fetchKyberTimeline(): Promise<KyberTimelineNode> {
  return fetchJson<KyberTimelineNode>('/api/kyber/timeline')
}
export function fetchKyberComparison(): Promise<KyberComparisonTable> {
  return fetchJson<KyberComparisonTable>('/api/kyber/compare')
}
export function fetchKyberQuarantine(): Promise<{ entries: KyberQuarantineEntry[] }> {
  return fetchJson<{ entries: KyberQuarantineEntry[] }>('/api/kyber/quarantine')
}

/**
 * Quarantine list scoped to one harness. `all`/`unknown` are UI sentinels,
 * not harness ids: `all` omits the filter, `unknown` asks for rows the
 * server cannot attribute (and the panel client-filters the same way when
 * the server predates the filter). The server may ignore the param — the
 * panel always applies the same predicate locally, so the list is truthful
 * either way.
 */
export function fetchKyberQuarantineScoped(harness?: string | null): Promise<{ entries: KyberQuarantineEntry[] }> {
  if (harness === undefined || harness === null || harness === '' || harness === 'all') {
    return fetchKyberQuarantine()
  }
  return fetchJson<{ entries: KyberQuarantineEntry[] }>(
    `/api/kyber/quarantine?harness=${encodeURIComponent(harness)}`,
  )
}

/**
 * One quarantined span with whatever stored evidence the server holds.
 *
 * The envelope is deliberately wide: the detail route
 * (`GET /api/kyber/quarantine/:spanId`) does not exist on servers predating
 * this surface, and its exact shape is owned by the engine worktree. The
 * panel reads the row it already has first and layers any of these keys on
 * top — never inventing a payload, a message, or a stack.
 */
export interface KyberQuarantineDetail {
  spanId?: string
  span_id?: string
  source?: string | null
  harness?: string | null
  name?: string | null
  namespaces?: unknown
  reason?: string | null
  seen_at?: unknown
  timestamp?: string | null
  payload?: unknown
  attributes?: unknown
  message?: unknown
  [key: string]: unknown
}

export async function fetchQuarantineDetail(spanId: string): Promise<KyberQuarantineDetail> {
  return fetchJson<KyberQuarantineDetail>(`/api/kyber/quarantine/${encodeURIComponent(spanId)}`)
}
export function fetchKyberProblems(): Promise<{ problems: KyberProblemEntry[] }> {
  return fetchJson<{ problems: KyberProblemEntry[] }>('/api/kyber/problems')
}

/**
 * Problems list scoped to one harness. Same sentinel contract as the
 * quarantine list: `all` omits the filter, `unknown` asks for unattributed
 * rows, and the panel client-filters identically for servers that predate
 * the query param.
 */
export function fetchKyberProblemsScoped(harness?: string | null): Promise<{ problems: KyberProblemEntry[] }> {
  if (harness === undefined || harness === null || harness === '' || harness === 'all') {
    return fetchKyberProblems()
  }
  return fetchJson<{ problems: KyberProblemEntry[] }>(
    `/api/kyber/problems?harness=${encodeURIComponent(harness)}`,
  )
}

/**
 * One problem row with whatever stored evidence the server holds.
 * Same unknown-envelope rule as the quarantine detail: the panel shows the
 * list row's code/message plus any of these keys the server returns, and
 * states plainly when the message, payload, or stack was never stored.
 */
export interface KyberProblemDetail {
  id?: string | number
  severity?: string
  code?: string | null
  message?: unknown
  location?: unknown
  at?: unknown
  timestamp?: string | null
  harness?: string | null
  session_id?: string | null
  span_id?: string | null
  spanId?: string | null
  payload?: unknown
  stack?: unknown
  stackTrace?: unknown
  [key: string]: unknown
}

export async function fetchProblemDetail(id: string): Promise<KyberProblemDetail> {
  return fetchJson<KyberProblemDetail>(`/api/kyber/problem/${encodeURIComponent(id)}`)
}

export interface SessionRollup {
  turn_count?: number | null
  request_count?: number | null
  total_input?: number | null
  total_output?: number | null
  tokens?: number | null
  cost_usd?: number | null
}

export interface KyberSessionSummary {
  session_id: string
  sessionId?: string
  harness: string
  label?: string | null
  is_subagent?: boolean
  isSubagent?: boolean
  parent_session?: string | null
  parentSession?: string | null
  agent_name?: string | null
  agentName?: string | null
  repo?: string | null
  branch?: string | null
  started?: string | number | null
  ended?: string | number | null
  turn_count?: number | null
  turnCount?: number | null
  request_count?: number | null
  total_input?: number | null
  total_output?: number | null
  cost_usd?: number | null
  costUsd?: number | null
  /** Canonical cost block (issue #186): basis + status + optional value. */
  cost?: { basis: string; status: string; value?: number; currency?: string }
  models?: string[]
  problems?: number
  subagents?: KyberSessionSummary[]
  rollup?: SessionRollup
}

/**
 * The bounded session summary `GET /api/kyber/session/:id/summary` serves:
 * ten identity-and-summary keys, no payload, no lineage.
 *
 * WHY a second document for the same session: the detail route serves the
 * whole session blob — up to 15.3 MB and a multi-second cold page-in for a
 * session with 10,933 turns — while a consumer that needs a handful of
 * identity fields pays for all of it. The bounded route answers the same
 * question for a fraction of the cost.
 *
 * Every key but `harness` is read defensively (optional): the client resolves
 * an id and a harness, and must not break on a key the body omits. `harness`
 * is the one it cannot do without — without it there is no spine.
 */
export type KyberSessionSummaryDetail = {
  /** The session's id, the spine's execution level. */
  session_id?: string
  /** The harness that produced the session — the spine's harness level. */
  harness: string
  /** The stored label, verbatim. The sessions list applies its own title fallback. */
  label?: string | null
  is_subagent?: boolean
  parent_session?: string | null
  agent_name?: string | null
  started?: string | null
  ended?: string | null
  /** The session's summary object, parsed — never a JSON string. */
  summary?: Record<string, unknown>
  /** How many problems the session carries. */
  problems_count?: number
}

/**
 * What the ancestry consumer reads off the bounded summary.
 *
 * The lineage keys (`runId`, `run_id`, `executionId`, `execution_id`) are NOT
 * part of the served shape: the `session` table has no such column and the
 * session payload carries no such field either, so a link served here would be
 * invented rather than measured — and an invented link would push a run level
 * into the resolved spine that today's client never produces. They stay on
 * the type because the resolver probes them defensively and must keep reading
 * `undefined`, the same value it reads today, rather than having its
 * resolution logic rewritten around a lineage it cannot verify (R7.5).
 */
export type KyberSessionAncestry = KyberSessionSummaryDetail & {
  /**
   * The detail route's legacy id key. The bounded route never serves it, so
   * the resolver's `id ?? session_id` chain falls through to `session_id` and
   * resolves the same identity it resolves today.
   */
  id?: string
  runId?: string
  run_id?: string
  executionId?: string
  execution_id?: string
}

/**
 * One session's identity fields, from the bounded summary route.
 *
 * WHY this route and not the detail route: the detail route serves the entire
 * session payload, and this consumer needs the identity fields only. The
 * turns consumer (`fetchKyberSessionTurns`) reads the bounded per-turn route
 * for one turn, and the detail route for a session's whole list of them.
 */
export async function fetchKyberSession(sessionId: string): Promise<KyberSessionAncestry> {
  return fetchJson<KyberSessionAncestry>(
    `/api/kyber/session/${encodeURIComponent(sessionId)}/summary`,
  )
}

/**
 * Measured per-turn token figures from the session turn row (issue #184, Q3).
 * Every field is a harness-measured counter or null; consumers render what is
 * measured and state absence plainly — never estimate from text. A single
 * turn's row comes from the bounded per-turn route, which the turn page
 * reads; the whole list of a session's rows still comes from the full session
 * payload (`GET /api/kyber/session/:id`).
 */
export interface TurnTokenFigures {
  input?: number | null
  output?: number | null
  fresh?: number | null
  cacheRead?: number | null
}

/**
 * One turn's row, from the bounded per-turn route.
 *
 * <remarks>
 * The same row shape the detail route serves for one turn, so a consumer that
 * already reads the detail route's rows reads this one unchanged — but from
 * `GET /api/kyber/session/:id/turn/:index`, which answers one turn instead of
 * the session's whole payload.
 */
export type KyberSessionTurn = KyberSessionTurnRow & {
  /** The session the turn belongs to. */
  session_id: string
  /** The turn's index in that session. */
  turn_index: number
}

/**
 * A session's turn rows, or one turn's row.
 *
 * <remarks>
 * With a turn index, the read is the bounded per-turn route: one row, never
 * the session's whole payload — which is what a single turn's counters cost
 * before (up to 10,933 turn rows to read four numbers). Without one, the read
 * is still the detail route's turns array, the shape a list-style consumer
 * expects.
 */
export async function fetchKyberSessionTurns(sessionId: string): Promise<KyberSessionTurnRow[]>
export async function fetchKyberSessionTurns(
  sessionId: string,
  turnIndex: number,
): Promise<KyberSessionTurn>
export async function fetchKyberSessionTurns(
  sessionId: string,
  turnIndex?: number,
): Promise<KyberSessionTurnRow[] | KyberSessionTurn> {
  if (turnIndex !== undefined) {
    return fetchJson<KyberSessionTurn>(
      `/api/kyber/session/${encodeURIComponent(sessionId)}/turn/${turnIndex}`,
    )
  }
  const body = await fetchJson<{ turns?: KyberSessionTurnRow[] }>(`/api/kyber/session/${encodeURIComponent(sessionId)}`)
  return Array.isArray(body.turns) ? body.turns : []
}

/** A turn row carrying either numbering shape: 0-based `index`, legacy 1-based `turn`, or neither. */
export interface TurnIdentity {
  index?: unknown
  turn?: unknown
}

/**
 * One row's 0-based transport identity (issue #184): an explicit finite `index`
 * wins; otherwise a finite legacy 1-based `turn` resolves as `turn - 1`.
 * Anything else is no identity — the row is only reachable positionally.
 */
export function turnTransportIndex(row: TurnIdentity | null | undefined): number | undefined {
  if (row == null) return undefined
  if (typeof row.index === 'number' && Number.isFinite(row.index)) return row.index
  if (typeof row.turn === 'number' && Number.isFinite(row.turn)) return row.turn - 1
  return undefined
}

/**
 * One context row's 1-based engine number: the engine's `TurnPressure.index`
 * wins, else the legacy 1-based `turn`. Anything else has no number.
 */
export function contextTurnNumber(row: TurnIdentity | null | undefined): number | undefined {
  if (row == null) return undefined
  if (typeof row.index === 'number' && Number.isFinite(row.index)) return row.index
  if (typeof row.turn === 'number' && Number.isFinite(row.turn)) return row.turn
  return undefined
}

function findByIdentity<T>(
  rows: readonly T[] | undefined,
  target: number,
  identityOf: (row: T) => number | undefined,
  positionOf: (position: number) => number,
): T | undefined {
  return (
    rows?.find((row) => identityOf(row) === target) ??
    rows?.find((row, i) => positionOf(i) === target && identityOf(row) === undefined)
  )
}

/**
 * Strict 0-based turn lookup shared by the drawer and the turn page
 * (issue #184): explicit identity first, array position only for rows that
 * carry neither `index` nor `turn`. Anything else resolves to nothing —
 * never a neighboring turn.
 */
export function findTurnByTransport<T extends TurnIdentity>(
  rows: readonly T[] | undefined,
  turnIndex: number,
): T | undefined {
  return findByIdentity(rows, turnIndex, turnTransportIndex, (i) => i)
}

/**
 * Strict 1-based context lookup for engine-numbered rows: explicit engine
 * `index` or legacy `turn` first, array position (`i + 1`, the parallel turns
 * arrays share session order) only for rows carrying neither.
 */
export function findContextTurn<T extends TurnIdentity>(
  rows: readonly T[] | undefined,
  oneBasedTurn: number,
): T | undefined {
  return findByIdentity(rows, oneBasedTurn, contextTurnNumber, (i) => i + 1)
}

export async function fetchKyberSessions(harness?: string | null): Promise<KyberSessionSummary[]> {
  const url = harness ? `/api/kyber/sessions?harness=${encodeURIComponent(harness)}` : '/api/kyber/sessions'
  const res = await fetch(url)
  if (!res.ok) throw new Error(`Request failed (${res.status}) for ${url}`)
  const json = (await res.json()) as { sessions: KyberSessionSummary[] }
  let list = json.sessions ?? []
  if (harness) {
    list = list.filter((s) => s.harness?.toLowerCase() === harness.toLowerCase())
  }
  return list
}

// ── Session view payload (/api/kyber/session/:id) ───────────────────────────
//
// The document `buildSessionRow` (src/canon/sessions.ts) writes into the
// session table and `KyberBridge.getSessionPayload` serves verbatim. Typed
// here rather than imported from the engine so the web tree stays
// self-contained: when the `ContextReport` contract (spec task 5.1) lands,
// Stream D replaces these names with type-only imports from
// `src/analysis/report/types.ts` and rebuilds the consumers against it.

/** A figure the source could not measure, serialized as its reason object. */
export type KyberUnmeasurable = { availability: 'not_measurable'; reason: string }

/** A bucket or input total: a token count, or the explicit refusal with its reason. */
export type KyberMeasuredFigure = number | KyberUnmeasurable

/** Context composition of one turn, or of the session payload's `first`/`last` edge. */
export interface KyberContextBucket {
  buckets: Record<string, KyberMeasuredFigure>
  reported_input: KyberMeasuredFigure
}

/**
 * One turn of the serialized context analysis — the engine's `TurnPressure`
 * with its per-server map turned into an object for JSON.
 */
export interface KyberContextTurn {
  index: number
  buckets: Record<string, KyberMeasuredFigure>
  toolDefinitionsByServer: Record<string, number>
  builtinToolDefinitionTokens: number
  strippedInstructionBlocks: { count: number; tokens: number }
  bucketedTokens: number
  residual: { tokens: number; attribution: string }
  headroom: number
  pressure: number
  accumulationRate: number
  freshInput: number
  freshJumpFactor?: number
  freshInputJump?: { previous: number; factor: number }
  // The engine's measured per-turn input, served since issue #187
  // (`serializeContext` emits the engine's `inputTokens` under this key).
  // Read defensively all the same: legacy rows predate it, and the bucket
  // drill-down probes a reported-input figure on turn rows too.
  reported_input?: KyberMeasuredFigure
  // Read defensively: legacy rows numbered turns under `turn`.
  turn?: number
}

export type KyberSessionContext = {
  measurable: true
  contextLimit: number
  turns: KyberContextTurn[]
  residualTotal: number
  derivedCounts: boolean
  freshJumpFactor: number
  flaggedTurns: number[]
  sessionAccumulationRate: number
  first: KyberContextBucket
  last: KyberContextBucket
  unmeasuredTurns: number
} | {
  measurable: false
  reason: string
  turns?: number
  contextLimit?: number
  first: KyberContextBucket
  last: KyberContextBucket
  unmeasuredTurns: number
}

/**
 * One turn row of the served payload. The ten fields up to `reasoning` are
 * what `buildSessionRow` emits today; the rest are read defensively for
 * richer or older rows and are not part of the current writer's shape —
 * which is also why the row carries an index signature.
 */
export interface KyberSessionTurnRow {
  index: number
  spanId?: string
  timestamp?: string
  model?: string | null
  input?: number
  output?: number
  fresh?: number
  cache_read?: number
  cache_creation?: number
  reasoning?: number | null
  // Defensive reads, not emitted by the current writer:
  turn?: number
  cumulative_input?: number
  durationMs?: number
  buckets?: Record<string, KyberMeasuredFigure>
  content?: KyberTurnContentShadow | string | null
  has_tool_defs?: boolean
  [key: string]: unknown
}

/**
 * The server's verdict on a session's cost bases, decided in the engine where
 * `sumCosts` already refuses to blend (rule R1 — the web dashboard renders this
 * field, it never re-derives it).
 *
 * `null` when the cost blocks agree, or when nothing was priced at all; the
 * object when they do not, carrying the problem text, every basis found, and
 * the per-basis totals a display layer must keep apart.
 *
 * Optional on the served payload because a session row stored before this
 * field existed does not carry it. `undefined` means the store has not been
 * re-projected since — unknown, not "no mismatch".
 */
export interface KyberCostBasisMismatch {
  code: string
  message: string
  bases: string[]
  totalsByBasis: Record<string, number | null>
}

/** The tool-definition rows legacy turn content carried under `tool_definitions`. */
export interface KyberToolDefinitionShadow {
  name?: string
  function?: { name?: string }
  [key: string]: unknown
}

/** The content object legacy turn rows carried beside their token figures. */
export interface KyberTurnContentShadow {
  tool_definitions?: KyberToolDefinitionShadow[]
  input_messages?: KyberMessageLike[]
  output_messages?: KyberMessageLike[]
  prompt_text?: unknown
  response_text?: unknown
  reasoning_text?: unknown
  thinking_text?: unknown
  [key: string]: unknown
}

/**
 * A message as any harness emits it. The content routes serve harness-native
 * payloads verbatim, so the views probe the common aliases instead of
 * assuming one shape; every leaf stays unknown and is narrowed where used.
 */
export interface KyberMessageLike {
  type?: string
  role?: unknown
  name?: unknown
  content?: unknown
  text?: unknown
  raw?: unknown
  parts?: unknown
  response?: unknown
  result?: unknown
  output?: unknown
  arguments?: unknown
  input?: unknown
  args?: unknown
  parameters?: unknown
  [key: string]: unknown
}

export interface KyberSessionContentPart {
  spanId: string
  part: string
  text: string
  tokens?: number
  server?: string
  truncated?: boolean
  totalLength?: number
}

export interface KyberSessionContentResult {
  sessionId: string
  spanId?: string
  parts: KyberSessionContentPart[]
}

export function fetchKyberSessionContent(
  sessionId: string,
  opts?: { span?: string; part?: string; full?: boolean },
): Promise<KyberSessionContentResult> {
  const params = new URLSearchParams()
  if (opts?.span) params.set('span', opts.span)
  if (opts?.part) params.set('part', opts.part)
  if (opts?.full) params.set('full', 'true')
  const qs = params.toString()
  const path = `/api/kyber/session/${encodeURIComponent(sessionId)}/content${qs ? `?${qs}` : ''}`
  return fetchJson<KyberSessionContentResult>(path)
}

export interface KyberTurnContentPart {
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
  /**
   * Exact-match recurrence against earlier captured turns of this session
   * (same part kind, whole source text). Capture-only — never a cache claim.
   */
  recurrence?: { seenBefore: boolean }
}

export interface KyberTurnContentBlock {
  key: string
  label: string
  tokens?: number
  parts: KyberTurnContentPart[]
  text: string
  truncated?: boolean
  totalLength?: number
  notMeasurable?: { reason: string }
  /**
   * Block aggregate: `newThisTurn` when any member part is new,
   * `seenBefore` when any member part exactly matches an earlier same-kind
   * part. Both may hold at once for mixed blocks.
   */
  recurrence?: { newThisTurn: boolean; seenBefore: boolean }
}

export interface KyberTurnContentResult {
  sessionId: string
  turnIndex: number
  spanId?: string
  /** The turn record's parent span, when the record named one (B7). */
  parentSpanId?: string
  model?: string
  blocks: KyberTurnContentBlock[]
  parts: KyberTurnContentPart[]
  assembledText: string
  truncated?: boolean
  totalLength?: number
  measurability?: string | Record<string, unknown>
  notMeasurable?: { reason: string }
  /**
   * Where this turn's raw packets can be fetched (B7): the request span's
   * attributes always; the quarantined parent's packet only when that row
   * kept a payload.
   */
  rawPacket?: { spanAttributesPath: string; interactionPath?: string }
  /**
   * Session-level shared context: every captured `system_prompt` part in
   * session scope, in capture order, with section-level earlier-turn
   * recurrence. Null when none was captured. The view hides exact
   * duplicates of the turn's own parts.
   */
  sharedContext?: {
    parts: Array<{
      text: string
      spanId?: string
      turnIndex?: number
      truncated?: boolean
      totalLength?: number
      /** Display only: source turns folded into this entry (volatile per-request header lines). */
      collapsedTurns?: number[]
    }>
    seenBefore: boolean
  } | null
  /** The skills this turn listed and invoked; absent on a turn that predates the skills projection. */
  skills?: KyberTurnSkillsEntry
}

export function fetchTurnContent(
  sessionId: string,
  turnIndex: number,
  opts?: { budget?: number; full?: boolean },
): Promise<KyberTurnContentResult> {
  const params = new URLSearchParams()
  // Explicit lossless path: `full` bypasses the server's default budget so
  // the full source text returns intact. A large-number `budget` is not a
  // substitute — it still truncates past its value.
  if (opts?.full) {
    params.set('full', 'true')
  } else if (opts?.budget) {
    params.set('budget', String(opts.budget))
  }
  const qs = params.toString()
  const path = `/api/kyber/session/${encodeURIComponent(sessionId)}/turn/${turnIndex}/content${qs ? `?${qs}` : ''}`
  return fetchJson<KyberTurnContentResult>(path)
}

/**
 * Lossless whole-turn fetch: every available byte of the turn's blocks and
 * parts, with no budget truncation. Use for the turn inspector — never rely
 * on the default budget or a large-number sentinel for complete content.
 */
export function fetchTurnContentFull(
  sessionId: string,
  turnIndex: number,
): Promise<KyberTurnContentResult> {
  return fetchTurnContent(sessionId, turnIndex, { full: true })
}

export const assembleTurnContent = fetchTurnContent

export interface KyberSpanAttributes {
  spanId: string
  attributes: Record<string, unknown>
}

/**
 * One span's harness-emitted attributes (R9.2).
 *
 * Timeline nodes no longer carry their attribute map inline — it is the raw
 * span payload, and embedding it made each session row a second uncompressed
 * copy of the corpus. The inspector fetches the node it is showing instead.
 */
export function fetchSpanAttributes(spanId: string): Promise<KyberSpanAttributes> {
  return fetchJson<KyberSpanAttributes>(`/api/kyber/span/${encodeURIComponent(spanId)}/attributes`)
}

/**
 * One quarantine row in the detail shape the route serves (B7). Unlike the
 * light `KyberQuarantineEntry` list row every field is always present — absent
 * values are explicit null, because a dropped key would read as "field unknown"
 * rather than "this row kept no evidence". `evidenceAvailable` is the honest
 * answer to "did this row keep a packet", never inferred from an empty payload.
 */
export interface KyberQuarantineEntryDetail {
  spanId: string
  source: string | null
  name: string | null
  reason: string
  timestamp: string | null
  namespaces: string[]
  harness: string | null
  rawPayload: string | null
  evidenceAvailable: boolean
}

/**
 * One quarantined span's audit row plus the packet it kept (B7).
 *
 * This is the fetch behind the turn view's raw-packet disclosure, and the only
 * route that returns a stored payload: the turn's parent span was quarantined
 * out of the canonical store, so its evidence lives here or nowhere. Takes a
 * span id — passing the served path would nest a second `/api/kyber/` prefix.
 */
export function fetchQuarantineEntry(spanId: string): Promise<KyberQuarantineEntryDetail> {
  return fetchJson<KyberQuarantineEntryDetail>(`/api/kyber/quarantine/${encodeURIComponent(spanId)}`)
}

// ===========================================================================
// Spine Diagnostic Hierarchy & Finding Engine Contracts (Tasks G2, D1-D9)
// ===========================================================================

export type DiagnosticAvailability = 'measured' | 'derived' | 'not_measurable'
export type FindingConfidenceLevel = 'deterministic' | 'calibrated_statistical' | 'heuristic'
export type MeasurementClassification = 'deterministic' | 'inferred' | 'coverage-gap'

export type ScorecardDimensionKey =
  | 'contextHygiene'
  | 'cacheEfficiency'
  | 'toolYield'
  | 'skillUtilisation'
  | 'delegationOverhead'
  | 'continuity'

export interface ScorecardDimensionValue {
  name: string
  key: ScorecardDimensionKey
  value?: number | null
  formatted?: string
  status: DiagnosticAvailability
  reason?: string
  measurementClass?: MeasurementClassification
  confidence?: 'high' | 'medium' | 'low'
  detail?: string
}

export interface ScorecardData {
  dimensions: {
    contextHygiene: ScorecardDimensionValue
    cacheEfficiency: ScorecardDimensionValue
    toolYield: ScorecardDimensionValue
    skillUtilisation: ScorecardDimensionValue
    delegationOverhead: ScorecardDimensionValue
    continuity: ScorecardDimensionValue
  }
  secondaryCost?: {
    costUsd?: number | null
    basis?: string
    status?: DiagnosticAvailability
    /** Visible "(partial)" marker when the total is a subtotal. */
    partial?: boolean
  }
}

export interface KyberEvidenceLink {
  spanId: string
  turnIndex: number
  description: string
  executionId?: string
}

export interface KyberFinding {
  id: string
  detectorId: string
  title: string
  mechanism: string
  evidenceLinks: KyberEvidenceLink[]
  confidence: FindingConfidenceLevel
  // Absent for unmeasured (coverage-gap) findings: render '—', never zero.
  estimatedWasteTokens?: number
  recommendation: string
  // Absent for unmeasured (coverage-gap) findings alongside the waste
  // estimate; every consumer already guards with `finding.errorBar &&`.
  errorBar?: {
    lower: number
    upper: number
  }
  outcomeRiskCaveat: string
  runId?: string
  sessionId?: string
  executionId?: string
  harness?: string
  rankScore?: number
  measurementClass?: MeasurementClassification
  confidenceBasis?: string
  whatWouldRaiseIt?: string
  payload?: Record<string, unknown>
}

/**
 * One dimension as `src/analysis/scorecard.ts` serves it. The engine derives all six so
 * the dashboard and the CLI report cannot disagree (R11.14); the browser only renders.
 */
export interface ServedScorecardDimension {
  value: number | null
  unit?: string
  reason?: string
  display?: string
}

export type ServedHarnessScorecard = Partial<
  Record<ScorecardDimensionKey, ServedScorecardDimension>
>

export interface KyberHarnessSummary {
  harness: string
  name?: string
  sampleCount: number
  /** Live session rows for this harness, overlaid on the rollup when served. */
  sessionCount?: number
  contextPressureMedian?: number | null
  contextPressureP95?: number | null
  cacheHitRate?: number | null
  toolYield?: number | null
  delegationOverhead?: number | null
  fieldCoverage?: number | null
  measurability: Record<string, DiagnosticAvailability | string>
  costUsd?: number | null
  runCount?: number
  findingCount?: number
  scorecard?: ServedHarnessScorecard
  payload?: Record<string, unknown>
  /**
   * Display-only family label (decision D3, issues #189/#199): split client
   * surfaces stay distinct rows — grouping sums nothing across origins.
   */
  family?: string | null
  /** Verbatim zero-data reason off the rollup payload, or null when covered. */
  noDataReason?: string | null
  /**
   * Per-harness source-checkpoint unit counts by status, served for API
   * consumers (null when the checkpoint read is impossible — unknown, never
   * zeros). The dashboard's checkpoint surface is the ingest panel's global
   * `KyberCoverage.checkpoints` list, not this per-row rollup — no web
   * surface renders this field, so the matrix row type does not carry it.
   */
  checkpointSummary?: {
    ok: number
    partial: number
    failed: number
    unavailable: number
  } | null
}

export interface KyberRunSummary {
  runId: string
  harness: string
  label?: string | null
  groupingBasis: 'explicit' | 'derived'
  groupingRule?: string | null
  workingDirectory?: string | null
  started?: string | null
  ended?: string | null
  executionCount?: number
  turnCount?: number
  totalInput?: number | null
  totalOutput?: number | null
  costUsd?: number | null
  /** Present only when `costUsd` sums priced sessions beside unpriced ones. */
  costStatus?: 'partial'
  /** Present when any run figure is a subtotal over incomplete sessions. */
  partial?: boolean
  /** Names exactly which run figures are subtotals, so views mark the right cells. */
  partialFields?: Array<'turnCount' | 'totalInput' | 'totalOutput' | 'costUsd'>
  outcome?: {
    status?: string
    exitCode?: number | null
    testDelta?: { passed?: number; failed?: number; total?: number } | null
    userCorrections?: number
    abandoned?: boolean
    reason?: string
  } | null
  findingCount?: number
  /**
   * Engine-served scorecard dimensions (R11.14: the engine derives, the
   * browser renders). Harness rows carry the harness rollup's scorecard;
   * run rows carry the run-scoped scorecard (issue #183, Q2) whose reasons
   * are scoped to the run, never a claim about harness telemetry.
   */
  scorecard?: ServedHarnessScorecard
  payload?: Record<string, unknown>
}

/** A metric that is either reported, or explicitly stamped unreportable. */
export type MetricAvailabilityLike = string | { availability: string; reason?: string }

export interface KyberExecutionSummary {
  executionId: string
  runId: string
  sessionId?: string | null
  parentExecutionId?: string | null
  harness: string
  agentName?: string | null
  isRoot: boolean
  started?: string | null
  ended?: string | null
  /**
   * `'measured' | 'derived'`, or a `{ availability, reason }` object when the
   * source could not report execution structure. Rendering this value directly
   * crashes React on the object form — read it through `availabilityLabel`.
   */
  parentLinkage?: MetricAvailabilityLike
  turnCount?: number
  costUsd?: number | null
  payload?: Record<string, unknown>
  children?: KyberExecutionSummary[]
}

export interface KyberRunTurn {
  turnIndex: number
  executionId?: string
  sessionId?: string
  model?: string
  tokens?: number
  inputTokens?: number
  outputTokens?: number
  costUsd?: number | null
  /** The turn's own cost block is partial — its figure is real but incomplete. */
  costStatus?: 'partial'
  contextPressure?: number | null
  cacheHitRatio?: number | null
  timestamp?: string | null
}

export interface KyberRunDetail extends KyberRunSummary {
  executionTree: KyberExecutionSummary[]
  executions: KyberExecutionSummary[]
  findings: KyberFinding[]
  turns?: KyberRunTurn[]
}

export async function fetchHarnesses(): Promise<KyberHarnessSummary[]> {
  const json = await fetchJson<{ harnesses: KyberHarnessSummary[] }>('/api/kyber/harnesses')
  return json.harnesses ?? []
}

export async function fetchHarness(harnessId: string): Promise<KyberHarnessSummary> {
  return fetchJson<KyberHarnessSummary>(`/api/kyber/harness/${encodeURIComponent(harnessId)}`)
}

export async function fetchRuns(harness?: string | null): Promise<KyberRunSummary[]> {
  const qs = harness ? `?harness=${encodeURIComponent(harness)}` : ''
  const json = await fetchJson<{ runs: KyberRunSummary[] }>(`/api/kyber/runs${qs}`)
  return json.runs ?? []
}

export async function fetchRun(runId: string): Promise<KyberRunDetail> {
  const json = await fetchJson<{
    run?: KyberRunSummary
    executionTree?: KyberExecutionSummary[]
    executions?: KyberExecutionSummary[]
    findings?: KyberFinding[]
    turns?: KyberRunTurn[]
    scorecard?: ServedHarnessScorecard
  }>(`/api/kyber/run/${encodeURIComponent(runId)}`)

  const run = json.run ?? (json as unknown as KyberRunSummary)
  return {
    ...run,
    executionTree: json.executionTree ?? [],
    executions: json.executions ?? [],
    findings: json.findings ?? [],
    // Issue #183: the detail payload serves measured turn rows and a
    // run-scoped scorecard; both ride through when present.
    ...(Array.isArray(json.turns) ? { turns: json.turns } : {}),
    ...(json.scorecard && typeof json.scorecard === 'object' ? { scorecard: json.scorecard } : {}),
  }
}

/** Paged findings envelope served at `GET /api/kyber/findings` (issue #191). */
export interface FindingsPage {
  findings: KyberFinding[]
  total: number
  limit?: number
  offset: number
  detectorCounts: Record<string, number>
  unknownWindowSessions: number
}

export async function fetchFindings(opts?: {
  runId?: string
  sessionId?: string
  harness?: string
  detector?: string
  limit?: number
  offset?: number
}): Promise<FindingsPage> {
  const params = new URLSearchParams()
  if (opts?.runId) params.set('runId', opts.runId)
  if (opts?.sessionId) params.set('sessionId', opts.sessionId)
  // Harness and detector filter server-side (issue #191): the client-side
  // post-filter is gone, so a workspace query actually sees every finding.
  if (opts?.harness) params.set('harness', opts.harness)
  if (opts?.detector) params.set('detector', opts.detector)
  if (opts?.limit) params.set('limit', String(opts.limit))
  if (opts?.offset) params.set('offset', String(opts.offset))
  const qs = params.toString()
  const json = await fetchJson<FindingsPage>(`/api/kyber/findings${qs ? `?${qs}` : ''}`)
  return {
    findings: json.findings ?? [],
    total: typeof json.total === 'number' ? json.total : (json.findings ?? []).length,
    ...(json.limit !== undefined ? { limit: json.limit } : {}),
    offset: typeof json.offset === 'number' ? json.offset : 0,
    detectorCounts: json.detectorCounts ?? {},
    unknownWindowSessions: typeof json.unknownWindowSessions === 'number' ? json.unknownWindowSessions : 0,
  }
}

export async function fetchFinding(findingId: string): Promise<KyberFinding> {
  return fetchJson<KyberFinding>(`/api/kyber/finding/${encodeURIComponent(findingId)}`)
}

/** One bin on `GET /api/kyber/calibration` (`CalibrationCurveResult.bins`). */
export interface KyberCalibrationBin {
  bin: string
  lower: number
  upper: number
  predictionCount: number
  meanConfidence: number
  observedAccuracy: number
  calibrationError: number
}

/**
 * Payload of `GET /api/kyber/calibration`. The route spreads
 * `CalibrationCurveResult` at the top level and also nests it under `calibration`.
 */
export interface KyberCalibrationSummary {
  bins: KyberCalibrationBin[]
  totalPredictions: number
  scoredPredictions: number
  meanCalibrationError: number
  expectedCalibrationError: number
  maxCalibrationError: number
  brierScore: number
  isCalibrated: boolean
  status: 'calibrated' | 'not_yet_calibrated'
  statusMessage: string
  demoteConfidence: boolean
  threshold: number
}

type KyberCalibrationResponse = KyberCalibrationSummary & {
  calibration?: KyberCalibrationSummary
}

export async function recordFindingPrediction(finding: {
  id?: string
  runId?: string
  estimatedWasteTokens?: number | null
  confidence?: string | number
  errorBar?: { lower?: number; upper?: number }
}): Promise<void> {
  if (!finding.id || !finding.runId) return
  if (finding.estimatedWasteTokens == null) return
  await fetch('/api/kyber/predictions', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      findingId: finding.id,
      runId: finding.runId,
      predictedWasteTokens: finding.estimatedWasteTokens,
      confidence: finding.confidence,
      errorBar: finding.errorBar,
    }),
  })
}

export async function fetchCalibration(opts?: { runId?: string }): Promise<KyberCalibrationSummary> {
  const params = new URLSearchParams()
  if (opts?.runId) params.set('runId', opts.runId)
  const qs = params.toString()
  const json = await fetchJson<KyberCalibrationResponse>(`/api/kyber/calibration${qs ? `?${qs}` : ''}`)
  return json.calibration ?? json
}

/** Task phase used by phase-aligned run comparison. */
export type KyberTaskPhase = 'exploration' | 'implementation' | 'verification' | 'resolution'

/** Whether a comparison figure was measured or is explicitly unavailable (issue #190). */
export type KyberComparisonAvailability = 'measured' | 'unavailable' | 'not_measurable'

export interface KyberComparisonVerdict {
  status: 'promoted' | 'candidate_only' | 'insufficient_history' | 'outcome_regression' | 'neutral'
  pairCount?: number
  /** Omitted when recommendation history is not measured — never treat absence as zero. */
  completedPairCount?: number
  meetsSufficiencyThreshold?: boolean
  outcomeRegression?: boolean
  canPromote: boolean
  recommendation?: string
  refusalReason?: string
  summary?: string
  /** When history is not store-backed, the pair stays manual-only with no n / 5 display. */
  historyAvailability?: KyberComparisonAvailability | 'not_measured'
}

export interface KyberPhaseAlignedTurnPair {
  phase: KyberTaskPhase
  phaseIndex: number
  runATurn: Record<string, unknown> | null
  runBTurn: Record<string, unknown> | null
  signals: Array<Record<string, unknown>>
  reading: string
}

/** Measured run side of `GET /api/kyber/compare/runs`. */
export type KyberComparisonRunSideMeasured = {
  runId: string
  harness: string
  label?: string
  outcome?: unknown
  availability: 'measured'
  totalTokens: number
  totalCost?: number
  turnCount: number
}

/** Unavailable run side — token totals withheld; never a fabricated zero. */
export type KyberComparisonRunSideUnavailable = {
  runId: string
  harness: string
  label?: string
  outcome?: unknown
  availability: 'unavailable'
  reason: string
  /** Per-run metrics unavailability reason (issue #190). */
  metricsReason: string
  totalCost?: number
  turnCount?: number
  totalTokens?: never
}

export type KyberComparisonRunSide =
  | KyberComparisonRunSideMeasured
  | KyberComparisonRunSideUnavailable

/** Aggregate totals for a run pair — mirrors the server `ComparisonTotals` union. */
export type KyberComparisonTotalsMeasured = {
  availability: 'measured'
  tokensA: number
  tokensB: number
  tokenDelta: number
  turnCountA: number
  turnCountB: number
  turnDelta: number
  costA?: number
  costB?: number
  costDelta?: number
  costComparable: boolean
  costRefusalReason?: string
}

export type KyberComparisonTotalsUnavailable = {
  availability: 'unavailable'
  reason: string
  turnCountA: number
  turnCountB: number
  turnDelta: number
  costComparable: boolean
  costRefusalReason?: string
}

export type KyberComparisonTotals =
  | KyberComparisonTotalsMeasured
  | KyberComparisonTotalsUnavailable

/** `GET /api/kyber/compare/runs` body — engine `ComparisonSummary` without turn `raw`. */
export interface KyberRunComparison {
  runA: KyberComparisonRunSide
  runB: KyberComparisonRunSide
  taskFamily?: string
  pairs: KyberPhaseAlignedTurnPair[]
  phaseSummaries: Record<KyberTaskPhase, Record<string, unknown>>
  totals: KyberComparisonTotals
  verdict: KyberComparisonVerdict
}

/**
 * Phase-aligned comparison of two canon runs (`docs/plans/2026-09-06-kyberdash-spine.md` § B4-api).
 * Missing runs fail with the same `Request failed (404)` pattern as `fetchRun`.
 */
export async function fetchRunComparison(
  runAId: string,
  runBId: string,
): Promise<KyberRunComparison> {
  // Public compare does not accept a caller-supplied completedPairCount: that
  // would invent recommendation-history sufficiency. History is unavailable
  // until a store-backed measurement exists (issue #190).
  const params = new URLSearchParams()
  params.set('runA', runAId)
  params.set('runB', runBId)
  return fetchJson<KyberRunComparison>(`/api/kyber/compare/runs?${params.toString()}`)
}

export interface KyberReviewRecommendation {
  type: 'relocate' | 'progressive_disclosure' | 'on_demand' | 'defer' | 'general'
  title: string
  strategy: string
  suggestedAction: string
  outcomeRisk: string
  rawSnippet?: string
}

export interface KyberReviewResult {
  status: 'completed' | 'unconfigured' | 'error'
  source: 'model_review'
  provider: string
  model?: string
  review: string
  recommendations?: KyberReviewRecommendation[]
  instructions?: string
  inputTokens?: number
  tokensUsed?: {
    promptTokens?: number
    completionTokens?: number
    totalTokens?: number
  }
  error?: string
  timestamp: string
}

export async function requestContextReview(
  payload: {
    content: string
    blocks?: Array<{ key?: string; label?: string; tokens?: number; text: string }>
    sessionId?: string
    turnIndex?: number
    harness?: string
    model?: string
    focus?: string
  },
  options?: {
    provider?: string
    model?: string
    endpoint?: string
    apiKey?: string
  },
): Promise<KyberReviewResult> {
  const res = await fetch('/api/kyber/review', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ ...payload, options }),
  })
  if (!res.ok) {
    throw new Error(`Review request failed with status ${res.status}`)
  }
  return res.json() as Promise<KyberReviewResult>
}

/**
 * Bounded catalog refresh. Vendor names and counts only — `error` strings
 * from the server are dropped here so a remote body cannot reach the page.
 */
export interface ModelCatalogRefreshResult {
  rowCount: number
  lastRefreshAt: string | null
  vendorsUpdated: string[]
  vendorsFailed: { vendor: string }[]
  derivedRebuildCount: number
}

function stringList(value: unknown): string[] {
  if (!Array.isArray(value)) return []
  return value.filter((entry): entry is string => typeof entry === 'string' && entry.length > 0)
}

export async function refreshModelWindows(): Promise<ModelCatalogRefreshResult> {
  const path = '/api/kyber/model-catalog/refresh'
  const res = await fetch(path, { method: 'POST' })
  if (!res.ok) throw new KyberApiError(res.status, path)
  const json: unknown = await res.json()
  const body = json !== null && typeof json === 'object' ? (json as Record<string, unknown>) : {}
  const failed = Array.isArray(body.vendorsFailed) ? body.vendorsFailed : []
  return {
    rowCount: typeof body.rowCount === 'number' ? body.rowCount : 0,
    lastRefreshAt: typeof body.lastRefreshAt === 'string' ? body.lastRefreshAt : null,
    vendorsUpdated: stringList(body.vendorsUpdated),
    vendorsFailed: failed.flatMap((entry) => {
      if (entry === null || typeof entry !== 'object') return []
      const vendor = (entry as { vendor?: unknown }).vendor
      return typeof vendor === 'string' && vendor.length > 0 ? [{ vendor }] : []
    }),
    derivedRebuildCount: typeof body.derivedRebuildCount === 'number' ? body.derivedRebuildCount : 0,
  }
}

export async function fetchReviewStatus(): Promise<{ provider: string; isConfigured: boolean }> {
  return fetchJson<{ provider: string; isConfigured: boolean }>('/api/kyber/review/status')
}

/**
 * Database clean request. `confirm` is the browser's explicit consent to the
 * irreversible wipe. `reingestWeeks` is OPT-IN (issue #319): omit the key
 * entirely and no folder history is imported at all — the dashboard no longer
 * promises an automatic 7-day re-ingest the operator never asked for.
 */
export interface CleanDatabaseRequest {
  all?: boolean
  harnesses?: string[]
  /** Sent only when the operator ticked "import folder history"; 1..52. */
  reingestWeeks?: number
  confirm: true
}

/** Bounded clean summary. Counts only — never a remote error string. */
export interface CleanDatabaseResult {
  harnesses: string[]
  records: number
  reingested: boolean
  historyWeeks: number | null
}

function cleanCount(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? Math.max(0, Math.floor(value)) : 0
}

/**
 * Bounded database clean. The scope and counts are echoed from the server;
 * `error` strings are dropped here so a remote body cannot reach the page —
 * callers distinguish outcomes by `KyberApiError.status` (400 usage, 409
 * busy) instead.
 */
export async function cleanDatabase(request: CleanDatabaseRequest): Promise<CleanDatabaseResult> {
  const path = '/api/kyber/clean'
  // WHY the key is spread conditionally: `reingestWeeks: undefined` still
  // serialises as a JSON key, and a key the server cannot distinguish from a
  // decision would turn "no import" back into an implicit one.
  const { reingestWeeks, ...scope } = request
  const res = await fetch(path, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(
      reingestWeeks === undefined ? scope : { ...scope, reingestWeeks },
    ),
  })
  if (!res.ok) throw new KyberApiError(res.status, path)
  const json: unknown = await res.json()
  const body = json !== null && typeof json === 'object' ? (json as Record<string, unknown>) : {}
  const wipe = body.wipe !== null && typeof body.wipe === 'object'
    ? (body.wipe as Record<string, unknown>)
    : {}
  return {
    harnesses: stringList(body.harnesses),
    records: cleanCount(wipe.records),
    reingested: body.reingested === true,
    historyWeeks: typeof body.historyWeeks === 'number' && Number.isFinite(body.historyWeeks)
      ? body.historyWeeks
      : null,
  }
}

// ===========================================================================
// Ingest coverage (plan docs/plans/2026-09-30-issues-189-198-199 T9,
// issues #189/#198/#199): one read-only payload carrying the refresh window,
// receiver activity, quarantine reasons, and checkpoint statuses. Every count
// comes from a row that exists; anything unrecorded reads as unknown.
// ===========================================================================

/** One stored source's ingest activity: T7 display label plus the raw id for audit. */
export interface KyberCoverageSource {
  /** The stored source name, verbatim (auditability). */
  source: string
  /** Display label for the stored name (never a `codeburn/` id). */
  display: string
  /** Origin kind for the stored name. */
  kind: string
  /** Rows in `records` carrying this source. */
  recordCount: number
  /** Summed `ingest_log.count` for this source; 0 when the log names it nowhere. */
  ingestedCount: number
  /** Latest `ingest_log.timestamp` for this source; null when the log names it nowhere. */
  lastReceivedAt: string | null
}

/**
 * Receiver activity over the canonical store. `unknown` only when the log AND
 * the table are both empty — with the reason, never 0 and never `running`.
 */
export type KyberCoverageIngest =
  | { status: 'known'; sources: KyberCoverageSource[]; lastReceivedAt: string | null }
  | { status: 'unknown'; reason: string; sources: []; lastReceivedAt: null }

/** Refresh health plus the persisted ingest window; null window means unknown. */
export interface KyberCoverageRefresh {
  lastSuccessAt: string | null
  lastFailure: { at: string; summary: string } | null
  inProgress: { pid: number; since: string } | null
  historyWeeks?: number | null
  coveredThrough?: string | null
  coveredFrom?: string | null
}

/** Quarantine rows grouped by their verbatim reason. */
export interface KyberCoverageQuarantine {
  reason: string
  count: number
}

/** Source-unit checkpoint status as the store holds it (`partial` reads verbatim). */
export interface KyberCoverageCheckpoint {
  harnessId: string
  sourceKey: string
  providerId: string
  parserId: string
  parserContractVersion: string
  format: string
  sourceRootLabel: string
  revisionToken: string
  coveredFromUtc: string
  coveredThroughUtc: string
  lastAttemptUtc: string
  lastSuccessUtc: string | null
  lastStatus: string
  lastErrorCode: string | null
  unitCount: number
  recordCount: number
}

export interface KyberCoverage {
  refresh: KyberCoverageRefresh
  ingest: KyberCoverageIngest
  quarantineByReason: KyberCoverageQuarantine[]
  /** Null when the checkpoint read is unobservable; [] means zero units. */
  checkpoints: KyberCoverageCheckpoint[] | null
  /**
   * Live OTLP receiver health as the engine reports it.
   *
   * Optional because servers predating the receiver probe serve no such key:
   * absent means unknown, never healthy. When present, `healthy` is the
   * authoritative saber state — the display layer renders it and never
   * re-derives liveness from counts.
   */
  receiver?: KyberReceiverHealth | null
}

/**
 * Live OTLP receiver health (`GET /api/kyber/coverage` → `receiver`).
 *
 * Every field is read defensively: an older engine serves no `receiver` key
 * at all, and a newer one may add keys. `healthy` is authoritative; the rest
 * is supporting detail for the Status panel (endpoint, listener/paused
 * state, last accepted batch, recent errors). Health probes are excluded
 * from the server error count — that exclusion is engine behaviour, not
 * something a surface recomputes.
 */
export interface KyberReceiverHealth {
  enabled?: boolean | null
  status?: string | null
  listening?: boolean | null
  hostedByUs?: boolean | null
  paused?: boolean | null
  healthy?: boolean | null
  endpoint?: string | null
  checkedAt?: string | null
  lastAcceptedAt?: string | null
  failures5m?: number | null
  lastError?: string | null
  lastCode?: string | null
  lastErrorAt?: string | null
}

export async function fetchCoverage(): Promise<KyberCoverage> {
  return fetchJson<KyberCoverage>('/api/kyber/coverage')
}


// ===========================================================================
// Maintenance surface (issue #319, architecture rule R1): the web dashboard
// schedules nothing and imports nothing. It reads the host's status, reads the
// shared settings, and asks the server to run a bounded job. Every type below
// mirrors a route the `kyberdash web` server serves; none is computed here.
// ===========================================================================

/** The refresh job's own state as the host reports it (`JobState` server-side). */
export type KyberJobState = 'idle' | 'running' | 'running-elsewhere' | 'failed'

/** `GET /api/kyber/jobs`: the flat host status mapped to the nested wire shape. */
export interface KyberJobStatus {
  refresh: {
    state: KyberJobState
    lastSuccessAt: string | null
    lastFailure: string | null
    nextDueAt: string | null
  }
  /** Whether SCHEDULED jobs are paused. The pause button and label read this. */
  paused: boolean
  /**
   * Monotonic marker for the store's contents. It moves when a clean or a
   * rebuild rewrites the data underneath an open tab, which is what the
   * dashboard polls for (see `lib/storeGeneration.ts`).
   */
  storeGeneration: number
  /** Another process holds the jobs lease, so this host runs nothing. */
  hostedElsewhere: boolean
}

/**
 * The shared settings both display layers read and write over one API.
 * `dashboardPort` is the persisted `settings.dashboard.port` (default 4747): it applies
 * on the next engine restart, never to the running server.
 */
export interface KyberSettings {
  folderImportScheduled: boolean
  jobsPaused: boolean
  refreshCadenceMinutes: number
  receiverHosted: boolean
  dashboardPort: number
}

/** A partial `PUT /api/kyber/settings` body — every key is optional. */
export type KyberSettingsUpdate = Partial<KyberSettings>

export function fetchKyberJobs(): Promise<KyberJobStatus> {
  return fetchJson<KyberJobStatus>('/api/kyber/jobs')
}

export function fetchKyberSettings(): Promise<KyberSettings> {
  return fetchJson<KyberSettings>('/api/kyber/settings')
}

/**
 * Applies a partial settings change and returns the full settings as the server
 * now holds them, so the panel renders what was persisted rather than what was
 * asked for.
 */
export async function updateKyberSettings(update: KyberSettingsUpdate): Promise<KyberSettings> {
  const path = '/api/kyber/settings'
  const res = await fetch(path, {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(update),
  })
  if (!res.ok) throw new KyberApiError(res.status, path)
  return res.json() as Promise<KyberSettings>
}

/** Which surface asked for a refresh; `scheduled` is reserved for the host. */
export type KyberRefreshSurface = 'web' | 'tray'

/**
 * Requests a manual refresh. WHY `surface`: the host records which surface
 * triggered the run, and the dashboard must never ask for the scheduled one —
 * that decision belongs to the host's own cadence. A 409 arrives as a
 * `KyberApiError` for the caller to render as "already running".
 */
export async function requestKyberRefresh(surface: KyberRefreshSurface = 'web'): Promise<void> {
  const path = '/api/kyber/refresh'
  const res = await fetch(path, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ surface }),
  })
  if (!res.ok) throw new KyberApiError(res.status, path)
}

/**
 * One-off folder-history import (issue #319). It NEVER touches the scheduled
 * folder-import setting: "import this once" and "keep importing" are different
 * decisions, and conflating them made a single import look like a standing
 * preference.
 */
export async function importKyberHistory(request: {
  weeks: number
  harnesses?: readonly string[]
}): Promise<void> {
  const path = '/api/kyber/import-history'
  const res = await fetch(path, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      weeks: request.weeks,
      // Omitted rather than sent empty: an empty array is a claim about scope
      // the operator never made, and the server's "all harnesses" is its own.
      ...(request.harnesses && request.harnesses.length > 0
        ? { harnesses: [...request.harnesses] }
        : {}),
    }),
  })
  if (!res.ok) throw new KyberApiError(res.status, path)
}

// ===========================================================================
// Harness OpenTelemetry collection (`/api/kyber/capture`). Like the
// maintenance surface above, this is a display client: the server decides what
// is installed, what is on and what a write would change; the browser parses
// the answer into bounded, vocabulary-checked values and shows them. Every
// reader below drops unknown fields (a response must never smuggle a config
// value into the DOM), caps strings, bounds arrays, and maps an
// out-of-vocabulary enum to the value that grants nothing (`unknown`,
// `status-only`) rather than trusting it.
// ===========================================================================

const CAPTURE_PATH = '/api/kyber/capture'

/** Wire strings are capped so a hostile or broken answer cannot flood the DOM. */
const CAPTURE_TEXT_MAX = 4096
const CAPTURE_VALUE_MAX = 512
const CAPTURE_MAX_HARNESSES = 64
const CAPTURE_MAX_FILES = 16
const CAPTURE_MAX_KEYS = 64
const CAPTURE_MAX_EVIDENCE = 32
const CAPTURE_MAX_LINES = 32
const CAPTURE_MAX_WARNINGS = 16
const CAPTURE_MAX_STEPS = 16
const CAPTURE_MAX_EVENTS = 64
const CAPTURE_MAX_CHANGES = 64
const CAPTURE_MAX_ISSUES = 32

export type KyberCapturePlatform = 'darwin' | 'linux' | 'win32' | 'unknown'
export type KyberCaptureLiveness = 'kyberdash' | 'foreign' | 'none' | 'unknown'
export type KyberCaptureMode = 'managed' | 'status-only'
export type KyberCaptureInstalled = 'detected' | 'not-detected' | 'unknown'
export type KyberCaptureState = 'on' | 'off' | 'partial' | 'unknown' | 'not-applicable'
export type KyberCaptureAction = 'enable' | 'disable'
/** The server's delivery verdict; `attention` renders yellow. */
export type KyberCaptureHealth = 'ok' | 'attention' | 'off' | 'read-only'

export interface KyberCaptureIssue {
  code: string
  severity: 'blocking' | 'warning'
  text: string
  path?: string
  remedy?: string
}

/** The check's one-line summary. Absent from an older server. */
export interface KyberCaptureCheck {
  checkedAt: string
  health: 'ok' | 'attention' | 'off'
  enabled: number
  attention: number
  issueCount: number
}

/** A value the server reports for one managed key: a JSON scalar, never an object. */
export type KyberCaptureValue = string | number | boolean | null

export interface KyberCaptureDriftEntry {
  key: string
  written: KyberCaptureValue
  /** `null` when the key is absent now. */
  current: KyberCaptureValue
}

export interface KyberCaptureFile {
  id: string
  path: string
  applicable: boolean
  /** `null` is "could not be inspected" — not "absent". */
  exists: boolean | null
  expectedKeys: string[]
  presentKeys: string[]
  missingKeys: string[]
  mismatchedKeys: string[]
  drift: KyberCaptureDriftEntry[] | null
}

export interface KyberCaptureManualStep {
  id: string
  text: string
  env?: { name: string; value: string }
}

export interface KyberCaptureHarness {
  id: string
  displayName: string
  mode: KyberCaptureMode
  installed: KyberCaptureInstalled
  installEvidence: string[]
  state: KyberCaptureState
  receipt: 'recorded' | 'absent'
  files: KyberCaptureFile[]
  warnings: Array<{ code: string; text: string }>
  manualSteps: KyberCaptureManualStep[]
  statusLines: string[]
  /**
   * `null` when the server sent no verdict (an older server): the row then
   * falls back to `state` alone rather than claiming a health it was not told.
   */
  health: KyberCaptureHealth | null
  issues: KyberCaptureIssue[]
}

export interface KyberCaptureStatus {
  platform: KyberCapturePlatform
  receiver: { hosted: boolean; liveness: KyberCaptureLiveness; detail: string }
  harnesses: KyberCaptureHarness[]
  check: KyberCaptureCheck | null
}

export type KyberCaptureEventKind =
  | 'would-write'
  | 'wrote'
  | 'noop'
  | 'would-restore'
  | 'restored'
  | 'refused'
  | 'error'

export interface KyberCaptureChange {
  key: string
  /** Omitted (not `undefined`-valued) when the key was absent before. */
  before?: KyberCaptureValue
  after?: KyberCaptureValue
}

export interface KyberCaptureEvent {
  kind: KyberCaptureEventKind
  harness: string
  fileId: string
  path: string
  changes?: KyberCaptureChange[]
  reason?: string
  drift?: KyberCaptureDriftEntry[]
  code?: string
}

export interface KyberCaptureReceiverNote {
  hosted: boolean
  willEnable: boolean
  enabled: boolean
  enableFailed: boolean
  lastManagedHarnessDisabled: boolean
}

/** A 200 answer to `POST /api/kyber/capture`. */
export interface KyberCaptureResult {
  action: KyberCaptureAction
  harness: string
  dryRun: boolean
  outcome: 'previewed' | 'wrote' | 'noop'
  events: KyberCaptureEvent[]
  harnessState: KyberCaptureHarness
  receiver: KyberCaptureReceiverNote
}

export type KyberCaptureRefusalReason =
  | 'drift'
  | 'conflict'
  | 'unparseable'
  | 'unreadable'
  | 'busy'
  | 'unknown'

/** A 409 answer: the server declined and changed nothing. Shown, not thrown. */
export interface KyberCaptureRefusal {
  outcome: 'refused'
  error: string
  reason: KyberCaptureRefusalReason
  events: KyberCaptureEvent[]
}

/** Exactly the documented keys the route accepts. */
export interface KyberCaptureRequest {
  action: KyberCaptureAction
  harness: string
  dryRun: boolean
  /**
   * Settings-only opt-in: on Disable with no receipt, remove only this
   * harness's declared OTEL keys that are present (user-authorized in the
   * Settings drawer, previewed before writing). Defaults to false: omitted
   * or false keeps the receipt-only behavior for the CLI and all callers.
   */
  removeUnreceiptedKeys?: boolean
  /** Exact Settings preview echoed on confirmed manual-key removal. */
  expectedPreviewEvents?: KyberCaptureEvent[]
}

type Raw = Record<string, unknown>

function isRaw(value: unknown): value is Raw {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function capText(value: unknown, max = CAPTURE_TEXT_MAX): string {
  return typeof value === 'string' ? value.slice(0, max) : ''
}

function oneOf<T extends string>(value: unknown, allowed: readonly T[], fallback: T): T {
  return typeof value === 'string' && (allowed as readonly string[]).includes(value) ? (value as T) : fallback
}

function captureStrings(value: unknown, maxItems: number, maxLength = CAPTURE_TEXT_MAX): string[] {
  if (!Array.isArray(value)) return []
  const out: string[] = []
  for (const item of value) {
    if (typeof item === 'string') out.push(item.slice(0, maxLength))
    if (out.length >= maxItems) break
  }
  return out
}

function captureValue(value: unknown): KyberCaptureValue {
  if (typeof value === 'string') return value.slice(0, CAPTURE_VALUE_MAX)
  if (typeof value === 'number' || typeof value === 'boolean') return value
  // A nested object or array is a config value the display never needs; do not
  // render it, and do not pretend the key is absent either.
  return value === null || value === undefined ? null : '[structured value]'
}

function readDrift(value: unknown): KyberCaptureDriftEntry[] | null {
  if (!Array.isArray(value)) return null
  const out: KyberCaptureDriftEntry[] = []
  for (const item of value) {
    if (!isRaw(item) || typeof item.key !== 'string') continue
    out.push({ key: item.key.slice(0, CAPTURE_VALUE_MAX), written: captureValue(item.written), current: captureValue(item.current) })
    if (out.length >= CAPTURE_MAX_KEYS) break
  }
  return out
}

function readFile(raw: unknown): KyberCaptureFile | null {
  if (!isRaw(raw)) return null
  return {
    id: capText(raw.id, CAPTURE_VALUE_MAX),
    path: capText(raw.path),
    applicable: raw.applicable === true,
    exists: typeof raw.exists === 'boolean' ? raw.exists : null,
    expectedKeys: captureStrings(raw.expectedKeys, CAPTURE_MAX_KEYS, CAPTURE_VALUE_MAX),
    presentKeys: captureStrings(raw.presentKeys, CAPTURE_MAX_KEYS, CAPTURE_VALUE_MAX),
    missingKeys: captureStrings(raw.missingKeys, CAPTURE_MAX_KEYS, CAPTURE_VALUE_MAX),
    mismatchedKeys: captureStrings(raw.mismatchedKeys, CAPTURE_MAX_KEYS, CAPTURE_VALUE_MAX),
    drift: readDrift(raw.drift),
  }
}

function readManualStep(raw: unknown): KyberCaptureManualStep | null {
  if (!isRaw(raw)) return null
  const step: KyberCaptureManualStep = { id: capText(raw.id, CAPTURE_VALUE_MAX), text: capText(raw.text) }
  if (isRaw(raw.env) && typeof raw.env.name === 'string' && typeof raw.env.value === 'string') {
    step.env = { name: raw.env.name.slice(0, CAPTURE_VALUE_MAX), value: raw.env.value.slice(0, CAPTURE_VALUE_MAX) }
  }
  return step
}

function mapBounded<T>(value: unknown, max: number, read: (item: unknown) => T | null): T[] {
  if (!Array.isArray(value)) return []
  const out: T[] = []
  for (const item of value) {
    const parsed = read(item)
    if (parsed !== null) out.push(parsed)
    if (out.length >= max) break
  }
  return out
}

function readHarness(raw: unknown): KyberCaptureHarness | null {
  if (!isRaw(raw) || typeof raw.id !== 'string') return null
  return {
    id: raw.id.slice(0, CAPTURE_VALUE_MAX),
    displayName: capText(raw.displayName, CAPTURE_VALUE_MAX) || raw.id.slice(0, CAPTURE_VALUE_MAX),
    // An unrecognised mode degrades to read-only: it must never create a toggle.
    mode: oneOf(raw.mode, ['managed', 'status-only'], 'status-only'),
    installed: oneOf(raw.installed, ['detected', 'not-detected', 'unknown'], 'unknown'),
    installEvidence: captureStrings(raw.installEvidence, CAPTURE_MAX_EVIDENCE),
    state: oneOf(raw.state, ['on', 'off', 'partial', 'unknown', 'not-applicable'], 'unknown'),
    receipt: oneOf(raw.receipt, ['recorded', 'absent'], 'absent'),
    files: mapBounded(raw.files, CAPTURE_MAX_FILES, readFile),
    warnings: mapBounded(raw.warnings, CAPTURE_MAX_WARNINGS, (item) =>
      isRaw(item) ? { code: capText(item.code, CAPTURE_VALUE_MAX), text: capText(item.text) } : null,
    ),
    manualSteps: mapBounded(raw.manualSteps, CAPTURE_MAX_STEPS, readManualStep),
    statusLines: captureStrings(raw.statusLines, CAPTURE_MAX_LINES),
    // An unrecognised verdict is shown as needing attention, never as healthy.
    health:
      raw.health === undefined
        ? null
        : oneOf<KyberCaptureHealth>(raw.health, ['ok', 'attention', 'off', 'read-only'], 'attention'),
    issues: mapBounded(raw.issues, CAPTURE_MAX_ISSUES, readIssue),
  }
}

function readIssue(raw: unknown): KyberCaptureIssue | null {
  if (!isRaw(raw) || typeof raw.text !== 'string') return null
  const issue: KyberCaptureIssue = {
    code: capText(raw.code, CAPTURE_VALUE_MAX),
    // An unrecognised severity is the stricter one.
    severity: oneOf(raw.severity, ['blocking', 'warning'], 'blocking'),
    text: capText(raw.text),
  }
  if (typeof raw.path === 'string') issue.path = capText(raw.path)
  if (typeof raw.remedy === 'string') issue.remedy = capText(raw.remedy)
  return issue
}

function readCheck(raw: unknown): KyberCaptureCheck | null {
  if (!isRaw(raw) || typeof raw.checkedAt !== 'string') return null
  const count = (value: unknown): number =>
    typeof value === 'number' && Number.isInteger(value) && value >= 0 ? Math.min(value, 9999) : 0
  return {
    checkedAt: raw.checkedAt.slice(0, 64),
    health: oneOf(raw.health, ['ok', 'attention', 'off'], 'attention'),
    enabled: count(raw.enabled),
    attention: count(raw.attention),
    issueCount: count(raw.issueCount),
  }
}

const EVENT_KINDS = ['would-write', 'wrote', 'noop', 'would-restore', 'restored', 'refused', 'error'] as const

function readChange(raw: unknown): KyberCaptureChange | null {
  if (!isRaw(raw) || typeof raw.key !== 'string') return null
  const change: KyberCaptureChange = { key: raw.key.slice(0, CAPTURE_VALUE_MAX) }
  if ('before' in raw) change.before = captureValue(raw.before)
  if ('after' in raw) change.after = captureValue(raw.after)
  return change
}

function readEvent(raw: unknown): KyberCaptureEvent | null {
  if (!isRaw(raw) || typeof raw.kind !== 'string' || !(EVENT_KINDS as readonly string[]).includes(raw.kind)) return null
  const event: KyberCaptureEvent = {
    kind: raw.kind as KyberCaptureEventKind,
    harness: capText(raw.harness, CAPTURE_VALUE_MAX),
    fileId: capText(raw.fileId, CAPTURE_VALUE_MAX),
    path: capText(raw.path),
  }
  if (Array.isArray(raw.changes)) event.changes = mapBounded(raw.changes, CAPTURE_MAX_CHANGES, readChange)
  if (typeof raw.reason === 'string') event.reason = raw.reason.slice(0, CAPTURE_VALUE_MAX)
  if (Array.isArray(raw.drift)) event.drift = readDrift(raw.drift) ?? []
  if (typeof raw.code === 'string') event.code = raw.code.slice(0, CAPTURE_VALUE_MAX)
  return event
}

function readEvents(value: unknown): KyberCaptureEvent[] {
  return mapBounded(value, CAPTURE_MAX_EVENTS, readEvent)
}

/** A 2xx answer whose body is not the documented shape is a broken server, not data. */
function malformed(status: number): KyberApiError {
  return new KyberApiError(status, CAPTURE_PATH)
}

function parseCaptureStatus(raw: unknown, status: number): KyberCaptureStatus {
  if (!isRaw(raw) || !isRaw(raw.receiver) || !Array.isArray(raw.harnesses)) throw malformed(status)
  return {
    platform: oneOf(raw.platform, ['darwin', 'linux', 'win32'], 'unknown'),
    receiver: {
      hosted: raw.receiver.hosted === true,
      liveness: oneOf(raw.receiver.liveness, ['kyberdash', 'foreign', 'none', 'unknown'], 'unknown'),
      detail: capText(raw.receiver.detail),
    },
    harnesses: mapBounded(raw.harnesses, CAPTURE_MAX_HARNESSES, readHarness),
    check: readCheck(raw.check),
  }
}

/** `GET /api/kyber/capture`: the structured per-harness collection status. */
export async function fetchKyberCaptureStatus(): Promise<KyberCaptureStatus> {
  const res = await fetch(CAPTURE_PATH)
  if (!res.ok) throw new KyberApiError(res.status, CAPTURE_PATH)
  return parseCaptureStatus(await res.json().catch(() => null), res.status)
}

function parseCaptureResult(raw: unknown, status: number): KyberCaptureResult {
  if (!isRaw(raw) || !isRaw(raw.receiver)) throw malformed(status)
  const outcome = oneOf(raw.outcome, ['previewed', 'wrote', 'noop'], 'previewed')
  if (raw.outcome !== outcome) throw malformed(status)
  const harnessState = readHarness(raw.harnessState)
  if (harnessState === null) throw malformed(status)
  return {
    action: oneOf(raw.action, ['enable', 'disable'], 'enable'),
    harness: capText(raw.harness, CAPTURE_VALUE_MAX),
    dryRun: raw.dryRun === true,
    outcome,
    events: readEvents(raw.events),
    harnessState,
    receiver: {
      hosted: raw.receiver.hosted === true,
      willEnable: raw.receiver.willEnable === true,
      enabled: raw.receiver.enabled === true,
      enableFailed: raw.receiver.enableFailed === true,
      lastManagedHarnessDisabled: raw.receiver.lastManagedHarnessDisabled === true,
    },
  }
}

function parseCaptureRefusal(raw: unknown): KyberCaptureRefusal {
  const body = isRaw(raw) ? raw : {}
  return {
    outcome: 'refused',
    error: capText(body.error),
    reason: oneOf(body.reason, ['drift', 'conflict', 'unparseable', 'unreadable', 'busy'], 'unknown'),
    events: readEvents(body.events),
  }
}

/**
 * `POST /api/kyber/capture`. A dry run previews; a real request writes. The
 * body is built from exactly the documented keys, so nothing a caller
 * adds can reach the route. A 409 is the server declining (drift, conflict, an
 * unreadable file) and is RETURNED as a refusal for the UI to show; every other
 * non-2xx throws `KyberApiError`.
 */
export async function postKyberCapture(
  request: KyberCaptureRequest,
): Promise<KyberCaptureResult | KyberCaptureRefusal> {
  const res = await fetch(CAPTURE_PATH, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(
      request.removeUnreceiptedKeys === true
        ? {
            action: request.action,
            harness: request.harness,
            dryRun: request.dryRun,
            removeUnreceiptedKeys: true,
            ...(request.expectedPreviewEvents === undefined ? {} : { expectedPreviewEvents: request.expectedPreviewEvents }),
          }
        : { action: request.action, harness: request.harness, dryRun: request.dryRun },
    ),
  })
  if (res.status === 409) return parseCaptureRefusal(await res.json().catch(() => null))
  if (!res.ok) throw new KyberApiError(res.status, CAPTURE_PATH)
  return parseCaptureResult(await res.json().catch(() => null), res.status)
}

// ---------------------------------------------------------------------------
// Skills (session payload section "4 · Skills")
// ---------------------------------------------------------------------------

/**
 * A value the harness did not export. The reason is the harness's own
 * explanation, shown verbatim; a panel never turns it into a 0 or an empty list.
 */
export type KyberSkillAvailability = {
  availability: 'not_measurable'
  reason: string
}

/** One skill from the session's `skills.rows`. `turns_listed` is 0-based transport indices. */
export type KyberSkillRow = {
  name: string
  listed: boolean | KyberSkillAvailability
  invocations: number | KyberSkillAvailability
  turns_listed: number[]
  /** Engine-derived token estimate; labelled as derived wherever it is shown. */
  derived_tokens?: number
}

/** The session payload's `skills` block: measured rows, or a whole-block not-measurable verdict. */
export type KyberSkillsBlock =
  | {
      measurable: true
      /** Harness-reported aggregate for skill listings; absent when the harness did not report one. */
      reported_skill_tokens?: number
      /** Served count of skills listed to the agent; absent when not measured. The browser never counts rows. */
      listed_count?: number
      /** Served count of listed skills with no observed invocation; absent when not measured. */
      listed_uninvoked_count?: number
      rows: KyberSkillRow[]
    }
  | KyberSkillAvailability

/**
 * The skills fields the session summary carries. Each array is absent when
 * that fact was not measured, and absence is never read as an empty list.
 */
export type KyberSkillsSummary = {
  skills_listed?: string[] | null
  skills_invoked?: string[] | null
}

/** One skill listed in a turn, with the listing text the harness exported (absent when not recorded). */
export type KyberTurnSkillListing = {
  name: string
  text?: string
}

/**
 * A turn's `skills` entry on the turn-content route. Either side may be a
 * not-measurable verdict, and the whole entry may be one.
 */
export type KyberTurnSkillsEntry =
  | {
      listed: KyberTurnSkillListing[] | KyberSkillAvailability
      invoked: string[] | KyberSkillAvailability
    }
  | KyberSkillAvailability
