import { fmtTokens } from './utils.js'
import { buildTurnContentView, type TurnContentSourcePart } from './turnContentBlocks.js'

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
  /** How many subagents the session has in total; the first page rides inline in `subagents`. */
  subagent_total?: number
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
 * WHY this route and not the detail route: the detail route served the entire
 * session payload, and this consumer needs the identity fields only.
 */
export async function fetchKyberSession(sessionId: string): Promise<KyberSessionAncestry> {
  return fetchJson<KyberSessionAncestry>(
    `/api/kyber/session/${encodeURIComponent(sessionId)}/summary`,
  )
}

/**
 * Measured per-turn token figures from the session turn row (issue #184, Q3).
 * Every field is a harness-measured counter or null; consumers render what is
 * measured and state absence plainly — never estimate from text. The row comes
 * from the bounded per-turn route.
 */
export interface TurnTokenFigures {
  input?: number | null
  output?: number | null
  fresh?: number | null
  cacheRead?: number | null
}

/**
 * One turn's row, from the bounded per-turn route
 * `GET /api/kyber/session/:id/turn/:index`.
 *
 * A turn index is required: the route answers one turn, so there is no
 * whole-payload list read left to fall back to.
 */
export async function fetchKyberSessionTurns(
  sessionId: string,
  turnIndex: number,
): Promise<KyberSessionTurnRow | undefined> {
  const section = await fetchKyberSessionTurn(sessionId, turnIndex)
  return section.turn
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

export const KYBER_SESSIONS_PAGE_LIMIT = 100

/** The subagents a root previews inline and a subagent page holds. */
export const KYBER_SUBAGENTS_PAGE_LIMIT = 25

/**
 * One paged envelope a `/api/kyber` paging route serves: the page's rows
 * under the engine's `data` key (and the route's own items key beside it),
 * plus the window it answered under.
 *
 * A 2xx body without these members is a broken server, not an empty list:
 * the validator refuses it, and the consumer renders the error state.
 */
export type KyberPage<T> = {
  /** The page's rows, the generic name consumers read. */
  entries: T[]
  /** The same rows under the engine's envelope key. */
  data: T[]
  /** How many rows the whole list holds, across every page. */
  total: number
  /** The one-based page this envelope answers. */
  page: number
  /** Rows per page, after the server clamped it. */
  limit: number
}

function malformedPage(path: string, why: string): Error {
  return new Error(`Malformed paged envelope for ${path}: ${why}`)
}

/**
 * Validates one paged envelope. The rows are read from `data` — the one items
 * key every paging route serves — and each row must be an object, so a route
 * that answers a different document cannot pose as a page.
 */
function parseKyberPage<T>(
  raw: unknown,
  path: string,
  readRow: (row: unknown) => T,
  itemsKey = 'data',
): KyberPage<T> {
  if (!isRaw(raw) || !Array.isArray(raw[itemsKey])) throw malformedPage(path, `${itemsKey} is not an array`)
  const total = raw.total
  const page = raw.page
  const limit = raw.limit
  if (typeof total !== 'number' || !Number.isInteger(total) || total < 0) throw malformedPage(path, 'total is not a row count')
  if (typeof page !== 'number' || !Number.isInteger(page) || page < 1) throw malformedPage(path, 'page is not a page number')
  if (typeof limit !== 'number' || !Number.isInteger(limit) || limit < 1) throw malformedPage(path, 'limit is not a page size')
  const entries = raw[itemsKey].map((row) => {
    if (!isRaw(row)) throw malformedPage(path, 'a row is not an object')
    return readRow(row)
  })
  return { entries, data: entries, total, page, limit }
}

/**
 * Reads one sessions-list row: every row must name its session (non-empty), since
 * the list keys, links and selects rows by that id, and its harness, which the
 * list groups and labels rows by.
 */
function readSessionSummaryRow(path: string): (row: unknown) => KyberSessionSummary {
  return (row) => {
    if (!isRaw(row) || typeof row.session_id !== 'string' || row.session_id.trim() === '') throw malformedPage(path, 'a row has no session_id')
    if (typeof row.harness !== 'string' || row.harness.trim() === '') throw malformedPage(path, 'a row has no harness')
    return row as unknown as KyberSessionSummary
  }
}

/**
 * One page of the sessions list, from `GET /api/kyber/sessions`.
 *
 * The store chooses the page — root resolution, harness filter, search,
 * order and window — so the shell's harness scope and the search text travel
 * on the query string, and a page costs what it shows rather than what the
 * store holds. Every root carries its first subagents inline under
 * `subagents` and its full subagent count under `subagent_total`; the rest
 * are paged by `fetchKyberSubagentPage`.
 */
export async function fetchKyberSessionPage(opts: {
  page?: number
  limit?: number
  harness?: string | null
  q?: string
}): Promise<KyberPage<KyberSessionSummary>> {
  const path = '/api/kyber/sessions'
  const params = new URLSearchParams()
  params.set('page', String(opts.page ?? 1))
  params.set('limit', String(opts.limit ?? KYBER_SESSIONS_PAGE_LIMIT))
  // `all` (and its older spelling) is the shell's word for every harness; the
  // route has no such harness, so it is never sent.
  const harness = opts.harness
  if (harness !== undefined && harness !== null && harness !== '' && harness !== 'all' && harness !== 'agent-all') {
    params.set('harness', harness)
  }
  if (opts.q) params.set('q', opts.q)
  const query = params.toString()
  const body = await fetchJson<unknown>(`${path}?${query}`)
  return parseKyberPage<KyberSessionSummary>(body, `${path}?${query}`, readSessionSummaryRow(path))
}

/**
 * One page of a session's subagents, from `GET /api/kyber/session/:id/subagents`.
 * The same set a root's `subagent_total` counts and its inline preview begins.
 */
export async function fetchKyberSubagentPage(parentId: string, page = 1): Promise<KyberPage<KyberSessionSummary>> {
  const params = new URLSearchParams()
  params.set('page', String(page))
  params.set('limit', String(KYBER_SUBAGENTS_PAGE_LIMIT))
  const path = `/api/kyber/session/${encodeURIComponent(parentId)}/subagents?${params.toString()}`
  const body = await fetchJson<unknown>(path)
  return parseKyberPage<KyberSessionSummary>(body, path, readSessionSummaryRow(path))
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

/** One tokenizer that counted a text-derived figure, and whether it is the model's own. */
export interface KyberTokenizerLabel {
  name: string
  isModelOwn: boolean
}

/**
 * The label trio the engine adds beside a figure it counted from text. Absent
 * as a whole when the harness reported the figure: a harness-reported count
 * carries no tokenizer and is not a stand-in for one.
 */
export interface KyberTokenizerFigure {
  tokenizer?: string
  tokenizers?: KyberTokenizerLabel[]
  tokenizerIsModelOwn?: boolean | null
}

/**
 * The model family a stand-in tokenizer is being compared against, for the
 * "not Claude's own" half of its label. A model the dashboard does not know
 * is named as served, so the sentence never invents a family.
 */
function modelFamilyName(model?: string | null): string {
  if (!model) return 'the model'
  const lower = model.toLowerCase()
  if (lower.includes('claude')) return 'Claude'
  if (lower.includes('gemini')) return 'Gemini'
  if (lower.includes('gpt') || lower.includes('openai')) return 'GPT'
  return model
}

/**
 * The visible label for a text-derived token figure: the tokenizer that
 * counted it and whether it is the model's own. Undefined when the figure
 * came from harness-reported counters and therefore carries no label.
 */
export function tokenizerLabel(
  figure: KyberTokenizerFigure,
  model?: string | null,
): string | undefined {
  const labels =
    figure.tokenizers && figure.tokenizers.length > 0
      ? figure.tokenizers
      : figure.tokenizer && figure.tokenizer !== 'mixed'
        ? [{ name: figure.tokenizer, isModelOwn: figure.tokenizerIsModelOwn === true }]
        : []

  if (labels.length === 0) {
    return figure.tokenizer === 'mixed' ? 'mixed tokenizers' : undefined
  }

  if (labels.length > 1 || figure.tokenizer === 'mixed') {
    const named = labels.map((label) =>
      label.isModelOwn
        ? `${label.name} (the model's own)`
        : `${label.name} (standard, not ${modelFamilyName(model)}'s own)`,
    )
    return `mixed tokenizers: ${named.join(', ')}`
  }

  const only = labels[0]!
  if (only.isModelOwn) return `${only.name}, the model's own tokenizer`
  return `${only.name}, standard tokenizer — not ${modelFamilyName(model)}'s own`
}

/** A token figure with its label, or 'not measured' when it is absent. */
export function fmtMeasuredTokens(n: number | null | undefined): string {
  return n == null ? 'not measured' : fmtTokens(n)
}

/** Context composition of one turn, or of the session payload's `first`/`last` edge. */
export interface KyberContextBucket {
  buckets: Record<string, KyberMeasuredFigure>
  reported_input: KyberMeasuredFigure
}

/**
 * One turn of the serialized context analysis — the engine's `TurnPressure`
 * with its per-server map turned into an object for JSON.
 */
export interface KyberContextTurn extends KyberTokenizerFigure {
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

export type KyberSessionContext = ({
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
} & KyberTokenizerFigure) | {
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
  /**
   * False when the definition is an MCP catalogue entry the session offered
   * without keeping it resident in the model's context. Absent or true means
   * the definition was resident.
   */
  resident?: boolean
  /** Whether the session offered this definition to the model. */
  offered?: boolean
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

export interface KyberSessionContentPart extends KyberTokenizerFigure {
  spanId: string
  part: string
  text: string
  tokens?: number
  server?: string
  truncated?: boolean
  totalLength?: number
  /**
   * `false` on a tool definition the turn offered without keeping resident.
   * Absent means resident; the drawer labels only the `false` case.
   */
  resident?: false
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

export interface KyberTurnContentPart extends KyberTokenizerFigure {
  id?: string
  spanId?: string
  part: string
  /** Human label the web parser derives from the part key; the engine never sends it. */
  label: string
  text: string
  tokens?: number
  server?: string
  truncated?: boolean
  totalLength?: number
  order?: number
  /**
   * False on a `tool_definitions` part is an MCP catalogue entry the session
   * offered without keeping resident in the model's context. Absent or true
   * means the definition was resident.
   */
  resident?: boolean
  /**
   * Exact-match recurrence against earlier captured turns of this session
   * (same part kind, whole source text). Capture-only — never a cache claim.
   * The restored C# route does not annotate it; consumers render without it.
   */
  recurrence?: { seenBefore: boolean }
}

export interface KyberTurnContentBlock extends KyberTokenizerFigure {
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
   * part. Both may hold at once for mixed blocks. The restored C# route
   * carries no recurrence; consumers render without it.
   */
  recurrence?: { newThisTurn: boolean; seenBefore: boolean }
}

export interface KyberTurnContentResult {
  sessionId: string
  turnIndex: number
  /** The turn's own span, served at the top level by the restored C# route. */
  spanId?: string
  /**
   * Legacy field of the retired TypeScript server. The restored C# route does not
   * serve it; the raw-packet disclosure renders without it.
   */
  parentSpanId?: string
  /** The turn's model, omitted by the engine when the turn recorded none. */
  model?: string
  /**
   * The canonical blocks, built by the web parser from the served parts — never
   * trusted off the wire.
   */
  blocks: KyberTurnContentBlock[]
  /** The labelled parts, built by the web parser from the served parts. */
  parts: KyberTurnContentPart[]
  /** Whole-turn plain text with a heading per non-blank block, built web-side. */
  assembledText: string
  /**
   * True only when the budgeted parts read left at least one scoped part behind —
   * the engine decides it with a content-free count read, and `?full=true` is never
   * truncated. Absent means not truncated.
   */
  truncated?: boolean
  /** Not served by the restored route; the views render without it. */
  totalLength?: number
  measurability?: string | Record<string, unknown>
  notMeasurable?: { reason: string }
  /**
   * Where this turn's raw packets can be fetched: the turn span's attributes,
   * always, at `/api/kyber/span/{spanId}/attributes`. The restored C# route links
   * no `interactionPath` — the quarantined-parent packet pointer of the retired
   * TypeScript server is not served.
   */
  rawPacket?: { spanAttributesPath: string; interactionPath?: string }
  /**
   * Session-level shared context: every captured `system_prompt` part in
   * session scope, in capture order, with section-level earlier-turn
   * recurrence. Null when none was captured. Not restored: the C# route serves
   * the turn's own parts only, so this stays absent and the drawer renders
   * without the section.
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
  /**
   * The skills this turn listed and invoked. Not restored: the C# route serves
   * the turn's own parts only, so the entry stays absent and consumers render
   * without it.
   */
  skills?: KyberTurnSkillsEntry
  /**
   * The sections' failure marker: present with no content fields when this turn's
   * projection read faulted. `blocks`/`parts`/`assembledText` are then inert empty
   * transport fields — the page renders the failure state, never an empty turn.
   */
  projection_status?: string
}

/**
 * Parses the content route's body. A faulted projection read answers the sections'
 * failure shape (identity plus the marker, no content fields); the parser keeps the
 * marker and fills the built transport fields with nothing, so consumers gate on
 * the marker rather than reading the fault as an empty turn.
 *
 * The restored C# route serves the byte-budgeted parts alone — no blocks, no
 * assembledText — so the normal path validates the parts and builds the view from
 * them. A body that still carries a blocks array is parsed the same way: one
 * contract, built from parts.
 */
function parseTurnContentResult(raw: unknown, path: string): KyberTurnContentResult {
  if (!isRaw(raw)) throw malformedSection(path, 'body is not an object')
  if (raw.projection_status === TURN_PROJECTION_FAILED) {
    return {
      sessionId: requireStringField(raw, 'sessionId', path),
      turnIndex: requireIntegerField(raw, 'turnIndex', path, 0),
      projection_status: raw.projection_status,
      blocks: [],
      parts: [],
      assembledText: '',
    }
  }
  const sessionId = requireStringField(raw, 'sessionId', path)
  const turnIndex = requireIntegerField(raw, 'turnIndex', path, 0)
  if (!Array.isArray(raw.parts)) throw malformedSection(path, 'parts is not an array')
  const sourceParts: TurnContentSourcePart[] = raw.parts.map((entry: unknown) => {
    if (!isRaw(entry)) throw malformedSection(path, 'parts entry is not an object')
    const label = readPartTokenizerFigure(entry, path, 'a content part')
    return {
      part: requireStringField(entry, 'part', path),
      text: requireStringField(entry, 'text', path),
      spanId: typeof entry.spanId === 'string' ? entry.spanId : undefined,
      tokens: typeof entry.tokens === 'number' && Number.isInteger(entry.tokens) && entry.tokens >= 0 ? entry.tokens : undefined,
      server: typeof entry.server === 'string' ? entry.server : undefined,
      resident: entry.resident === false ? false : undefined,
      truncated: entry.truncated === true ? true : undefined,
      ...label,
    }
  })
  const { parts, blocks, assembledText } = buildTurnContentView(sourceParts)
  const result: KyberTurnContentResult = { sessionId, turnIndex, parts, blocks, assembledText }
  if (typeof raw.spanId === 'string') result.spanId = raw.spanId
  if (typeof raw.model === 'string') result.model = raw.model
  if (isRaw(raw.rawPacket)) {
    const packet: NonNullable<KyberTurnContentResult['rawPacket']> = {
      spanAttributesPath: requireStringField(raw.rawPacket, 'spanAttributesPath', path),
    }
    if (typeof raw.rawPacket.interactionPath === 'string') packet.interactionPath = raw.rawPacket.interactionPath
    result.rawPacket = packet
  }
  if (raw.truncated === true) result.truncated = true
  return result
}

/** The sections' fault marker, matched exactly by both turn-route parsers. */
const TURN_PROJECTION_FAILED = 'PROJECTION_FAILED'

export async function fetchTurnContent(
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
  return parseTurnContentResult(await fetchJson<unknown>(path), path)
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
  // Absent or null for unmeasured (coverage-gap) findings: render 'not measured', never zero.
  estimatedWasteTokens?: number | null
  recommendation: string
  // Absent or null for unmeasured (coverage-gap) findings; the dashboard shows no
  // error bar at all, because an invented band is an estimate (D15).
  errorBar?: {
    lower: number
    upper: number
  } | null
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
  errorBar?: { lower?: number; upper?: number } | null
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

/** `GET /api/kyber/version`: the running engine's version and its API contract version. */
export interface KyberVersion {
  version: string
  apiVersion: number
}

const VERSION_PATH = '/api/kyber/version'

/**
 * A 2xx version body must carry a real version string (not blank, not the `0.0.0`
 * placeholder a dev build stamps) and a finite API version number.
 */
function isKyberVersion(value: unknown): value is KyberVersion {
  return (
    isRaw(value) &&
    typeof value.version === 'string' &&
    value.version.trim() !== '' &&
    value.version.trim() !== '0.0.0' &&
    typeof value.apiVersion === 'number' &&
    Number.isFinite(value.apiVersion)
  )
}

/**
 * Reads the engine's version. A 2xx body that is not the documented shape (or not
 * JSON at all) is a broken engine, so it is rejected as a `KyberApiError` rather
 * than passed on for the surface to render as `undefined` or `0.0.0`. The returned
 * version is trimmed, matching what the route serves, so every surface shows it bare.
 */
export async function fetchKyberVersion(): Promise<KyberVersion> {
  const res = await fetch(VERSION_PATH)
  if (!res.ok) throw new KyberApiError(res.status, VERSION_PATH)
  let body: unknown
  try {
    body = await res.json()
  } catch {
    body = null
  }
  if (!isKyberVersion(body)) throw new KyberApiError(res.status, VERSION_PATH)
  return { version: body.version.trim(), apiVersion: body.apiVersion }
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
const CAPTURE_MAX_NOTES = 32

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
  /**
   * The engine's own words about what the action did or would do. A surface
   * renders them; it never writes the sentence itself, so what a switch
   * changes is stated by the engine that changes it.
   */
  notes: string[]
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

/** The engine's words about the change, bounded like every other wire string. */
function readNotes(value: unknown): string[] {
  return captureStrings(value, CAPTURE_MAX_NOTES)
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
    notes: readNotes(raw.notes),
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
export type KyberSkillRow = KyberTokenizerFigure & {
  name: string
  listed: boolean | KyberSkillAvailability
  invocations: number | KyberSkillAvailability
  turns_listed: number[]
  /** Engine-derived token count; labelled with its tokenizer wherever it is shown. */
  derived_tokens?: number
  /** The skill description: from the listing, or the skill file for names-only harnesses. */
  description?: string
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
      /** Turns on which invocations were measured, out of the session's turns; absent when the server states no coverage. */
      invocationsCoverage?: { measuredTurns: number; totalTurns: number }
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

/**
 * One typed cell of the catalog-driven stored-fields inspection
 * (`GET /api/kyber/session/:sessionKey/stored-fields`). The server serializes
 * the C# record with camelCase keys, so `textValue` / `integerValue` /
 * `realValue` carry the cell's figure under the affinity named by
 * `columnType` — and every one of them is null when the store held no value.
 * A null cell was never measured; the panel renders it as not reported,
 * never as `0` or `""`. `columnType` stays `unknown` on the wire because the
 * server may serialize the affinity as a number or a name; the panel reads
 * the value keys, never the affinity.
 */
export interface StoredFieldCell {
  columnName: string
  columnType?: unknown
  textValue?: string | null
  integerValue?: number | null
  realValue?: number | null
}

/** One persisted row: the cells in the owning table's `columnNames` order. */
export interface StoredFieldRow {
  cells: StoredFieldCell[]
}

/**
 * One catalog table's inspection entry in the stored-fields inventory: the
 * reachable `table.column` labels live in `columnNames`, `rowCount` counts
 * the session's persisted rows across every page, and `missingParentKeys`
 * names the parent keys with no row in this optional child table. The rows
 * themselves load through `fetchStoredTableRowsPage` only when the viewer
 * opens the table (decision D15) — the inventory carries no row content.
 */
export interface StoredTableFields {
  tableName: string
  columnNames: string[]
  rowCount: number
  missingParentKeys: string[]
}

/** One page of one catalog table's stored rows, read when the viewer opens the table. */
export interface StoredFieldRowsPage {
  tableName: string
  columnNames: string[]
  /** Rows belonging to the session, across every page. */
  totalRows: number
  page: number
  limit: number
  offset: number
  rows: StoredFieldRow[]
}

/**
 * The catalog-driven relational view of one session: every applicable table
 * in catalog order. `tables` is empty only when nothing was ever projected,
 * never when the session is merely quiet.
 */
export interface StoredFieldsResponse {
  sessionKey: string
  tables: StoredTableFields[]
}

/**
 * Reads the catalog-driven stored-fields inventory for a session: the tables
 * and columns the store can hold for it, with row counts — no row content.
 *
 * WHY this route and not the session payload: the payload serves the
 * session's measured counters, while this route serves what the relational
 * store actually persisted — every reachable catalog column labeled
 * `table.column` — for the session execution inspection. A large table's
 * rows never ride along here; they page through `fetchStoredTableRowsPage`
 * when the viewer opens that table.
 */
export async function fetchKyberSessionStoredFields(sessionKey: string): Promise<StoredFieldsResponse> {
  const path = `/api/kyber/session/${encodeURIComponent(sessionKey)}/stored-fields`
  return parseStoredFields(await fetchJson<unknown>(path), path)
}

/** The stored-fields section's fetcher under its original name. */
export const fetchStoredFields = fetchKyberSessionStoredFields

/**
 * Reads one page of one catalog table's stored rows for a session, from
 * `GET /api/kyber/session/:sessionKey/stored-fields/:table`. The table name
 * is a catalog identifier from the inventory, and it is URL-encoded into the
 * path like every other id.
 */
export async function fetchStoredTableRowsPage(sessionKey: string, table: string, page = 1): Promise<StoredFieldRowsPage> {
  const params = new URLSearchParams()
  params.set('page', String(page))
  params.set('limit', String(KYBER_SECTION_PAGE_LIMIT))
  const path = `/api/kyber/session/${encodeURIComponent(sessionKey)}/stored-fields/${encodeURIComponent(table)}?${params.toString()}`
  return parseStoredFieldRowsPage(await fetchJson<unknown>(path), path)
}

// ── Session view sections (/api/kyber/session/:id/{section}) ────────────────
//
// Every section route answers one slice of the session view. The client
// validates each answered document here, so a 2xx body that is not the
// documented shape becomes an error state rather than a crash or a silent
// default. The list routes build on T3's paged-envelope validator; summary,
// charts, skills, timeline, turn and stored-fields carry their own members
// beside the shared window.

/** Rows per page every session section list route serves when the caller names none. */
export const KYBER_SECTION_PAGE_LIMIT = 25

/** The sections a session view can request; the query key's third member. */
export type KyberSessionSection =
  | 'summary'
  | 'charts'
  | 'tools'
  | 'servers'
  | 'skills'
  | 'kw-skills'
  | 'timeline'
  | 'requests'
  | 'reconciliation'
  | 'problems'
  | 'stored-fields'
  | 'turn'

/**
 * The query key one session section is cached under: `['kyber-session', id, section, page]`.
 * The whole-session key `['kyber-session', id]` is its prefix, so invalidating the
 * session invalidates every section read with it.
 */
export function kyberSessionSectionQueryKey(
  sessionId: string,
  section: KyberSessionSection,
  page = 1,
): readonly ['kyber-session', string, KyberSessionSection, number] {
  return ['kyber-session', sessionId, section, page]
}

/** The one-based page window every section list route answers. */
export interface KyberSectionWindow {
  /** How many rows the whole list holds, across every page. */
  total: number
  /** The one-based page this document answers. */
  page: number
  /** Rows per page, after the server clamped it. */
  limit: number
  /** Zero-based offset of this page's first row. */
  offset: number
  /**
   * The session's projection fault, when the section's projection read faulted;
   * absent when it did not. A payload carrying a fault is an error state, never data.
   */
  projection_status?: string
}

/** One page of a session section: the T3 page envelope plus its window offset. */
export interface KyberSectionPage<T> extends KyberPage<T>, KyberSectionWindow {}

function malformedSection(path: string, why: string): Error {
  return new Error(`Malformed session section for ${path}: ${why}`)
}

function requireStringField(raw: Raw, key: string, path: string): string {
  const value = raw[key]
  if (typeof value !== 'string') throw malformedSection(path, `${key} is not a string`)
  return value
}

function requireNumberField(raw: Raw, key: string, path: string): number {
  const value = raw[key]
  if (typeof value !== 'number' || !Number.isFinite(value)) throw malformedSection(path, `${key} is not a number`)
  return value
}

function requireIntegerField(raw: Raw, key: string, path: string, minimum: number): number {
  const value = requireNumberField(raw, key, path)
  if (!Number.isInteger(value) || value < minimum) throw malformedSection(path, `${key} is not a whole number`)
  return value
}

function requireBooleanField(raw: Raw, key: string, path: string): boolean {
  const value = raw[key]
  if (typeof value !== 'boolean') throw malformedSection(path, `${key} is not a boolean`)
  return value
}

function readNullableString(value: unknown, path: string, key: string): string | null {
  if (value === null) return null
  if (typeof value === 'string') return value
  throw malformedSection(path, `${key} is not a string or null`)
}

function readNullableNumber(value: unknown, path: string, key: string): number | null {
  if (value === null) return null
  if (typeof value === 'number' && Number.isFinite(value)) return value
  throw malformedSection(path, `${key} is not a number or null`)
}

function readOptionalString(value: unknown, path: string, key: string): string | null | undefined {
  if (value === undefined || value === null) return value
  if (typeof value === 'string') return value
  throw malformedSection(path, `${key} is not a string`)
}

function readOptionalNumber(value: unknown, path: string, key: string): number | null | undefined {
  if (value === undefined || value === null) return value
  if (typeof value === 'number' && Number.isFinite(value)) return value
  throw malformedSection(path, `${key} is not a number`)
}

/**
 * Reads the tokenizer label trio the engine adds beside a text-derived figure.
 * The trio is all-or-none: a figure with no label came from harness-reported
 * counters, and a response carrying only part of one cannot be shown honestly.
 */
function readTokenizerFigure(raw: Raw, path: string, what: string): KyberTokenizerFigure {
  const hasTokenizer = raw.tokenizer !== undefined
  const hasList = raw.tokenizers !== undefined
  const hasOwn = raw.tokenizerIsModelOwn !== undefined
  if (!hasTokenizer && !hasList && !hasOwn) return {}
  if (!hasTokenizer || !hasList || !hasOwn) {
    throw malformedSection(path, `${what} carries an incomplete tokenizer label`)
  }
  if (typeof raw.tokenizer !== 'string' || raw.tokenizer.length === 0) {
    throw malformedSection(path, `${what}.tokenizer is not a non-empty string`)
  }
  if (!Array.isArray(raw.tokenizers) || raw.tokenizers.length === 0) {
    throw malformedSection(path, `${what}.tokenizers is not a non-empty array`)
  }
  const tokenizers: KyberTokenizerLabel[] = raw.tokenizers.map((entry) => {
    if (!isRaw(entry)) throw malformedSection(path, `${what}.tokenizers holds a non-object`)
    return {
      name: requireStringField(entry, 'name', `${path} ${what}.tokenizers`),
      isModelOwn: requireBooleanField(entry, 'isModelOwn', `${path} ${what}.tokenizers`),
    }
  })
  const isModelOwn = raw.tokenizerIsModelOwn
  if (isModelOwn !== null && typeof isModelOwn !== 'boolean') {
    throw malformedSection(path, `${what}.tokenizerIsModelOwn is not a boolean or null`)
  }
  if (tokenizers.length > 1) {
    if (raw.tokenizer !== 'mixed' || isModelOwn !== null) {
      throw malformedSection(path, `${what} names several tokenizers without the mixed label`)
    }
  } else {
    const only = tokenizers[0]!
    if (raw.tokenizer !== only.name || isModelOwn !== only.isModelOwn) {
      throw malformedSection(path, `${what} tokenizer label disagrees with its list`)
    }
  }
  return { tokenizer: raw.tokenizer, tokenizers, tokenizerIsModelOwn: isModelOwn }
}

/**
 * Reads a content part's single-tokenizer label. A part may carry tokens with
 * no label (a harness-reported count), but it may not carry half a label.
 */
function readPartTokenizerFigure(raw: Raw, path: string, what: string): KyberTokenizerFigure {
  const hasTokenizer = raw.tokenizer !== undefined
  const hasOwn = raw.tokenizerIsModelOwn !== undefined
  if (!hasTokenizer && !hasOwn) return {}
  if (!hasTokenizer || !hasOwn) {
    throw malformedSection(path, `${what} carries an incomplete tokenizer label`)
  }
  if (typeof raw.tokenizer !== 'string' || raw.tokenizer.length === 0) {
    throw malformedSection(path, `${what}.tokenizer is not a non-empty string`)
  }
  const isModelOwn = raw.tokenizerIsModelOwn
  if (isModelOwn !== null && typeof isModelOwn !== 'boolean') {
    throw malformedSection(path, `${what}.tokenizerIsModelOwn is not a boolean or null`)
  }
  return {
    tokenizer: raw.tokenizer,
    tokenizers: [{ name: raw.tokenizer, isModelOwn: isModelOwn === true }],
    tokenizerIsModelOwn: isModelOwn,
  }
}


function readStringArray(value: unknown, path: string, key: string): string[] {
  if (!Array.isArray(value)) throw malformedSection(path, `${key} is not an array`)
  return value.map((entry) => {
    if (typeof entry !== 'string') throw malformedSection(path, `${key} holds a non-string`)
    return entry
  })
}

function readNumberMap(value: unknown, path: string, key: string): Record<string, number> {
  if (!isRaw(value)) throw malformedSection(path, `${key} is not an object`)
  const out: Record<string, number> = {}
  for (const [entryKey, entry] of Object.entries(value)) {
    if (typeof entry !== 'number' || !Number.isFinite(entry)) {
      throw malformedSection(path, `${key}.${entryKey} is not a number`)
    }
    out[entryKey] = entry
  }
  return out
}

function readNullableNumberMap(
  value: unknown,
  path: string,
  key: string,
): Record<string, number | null> {
  if (!isRaw(value)) throw malformedSection(path, `${key} is not an object`)
  const out: Record<string, number | null> = {}
  for (const [entryKey, entry] of Object.entries(value)) {
    out[entryKey] = readNullableNumber(entry, path, `${key}.${entryKey}`)
  }
  return out
}

function readSectionWindow(raw: Raw, path: string): KyberSectionWindow {
  const window: KyberSectionWindow = {
    total: requireIntegerField(raw, 'total', path, 0),
    page: requireIntegerField(raw, 'page', path, 1),
    limit: requireIntegerField(raw, 'limit', path, 1),
    offset: requireIntegerField(raw, 'offset', path, 0),
  }
  if (typeof raw.projection_status === 'string') window.projection_status = raw.projection_status
  return window
}

/** Reads one section page from the route's own items key, on top of T3's envelope. */
function parseSectionPage<T>(
  raw: unknown,
  path: string,
  itemsKey: string,
  readRow: (row: Raw, path: string) => T,
): KyberSectionPage<T> {
  const page = parseKyberPage<T>(raw, path, (row) => readRow(row as Raw, path), itemsKey)
  if (!isRaw(raw)) throw malformedSection(path, 'body is not an object')
  const section: KyberSectionPage<T> = { ...page, offset: requireIntegerField(raw, 'offset', path, 0) }
  if (typeof raw.projection_status === 'string') section.projection_status = raw.projection_status
  return section
}

// ── Summary ─────────────────────────────────────────────────────────────────

/** The bounded summary section: identity, counts and notes, no turn bodies. */
export interface KyberSessionSummarySection {
  session_id: string
  harness: string
  label?: string | null
  is_subagent: boolean
  parent_session?: string | null
  agent_name?: string | null
  repo?: string | null
  branch?: string | null
  started?: string | null
  ended?: string | null
  span_count: number
  problems: number
  problems_count: number
  request_count: number
  context_limit_source?: string | null
  costBasisMismatch: KyberCostBasisMismatch | null
  summary: Record<string, unknown> | null
  notes: string[]
  reconciliation: { total: number; mismatched: number }
  tools_offered_count: number
  tools_invoked_count: number
  unused_tools_count: number
  skills_count: number
  projection_status?: string
}

function readCostBasisMismatch(value: unknown, path: string): KyberCostBasisMismatch | null {
  if (value === null) return null
  if (!isRaw(value)) throw malformedSection(path, 'costBasisMismatch is not an object or null')
  return {
    code: requireStringField(value, 'code', path),
    message: requireStringField(value, 'message', path),
    bases: readStringArray(value.bases, path, 'costBasisMismatch.bases'),
    totalsByBasis: readNullableNumberMap(
      value.totalsByBasis,
      path,
      'costBasisMismatch.totalsByBasis',
    ),
  }
}

function parseSessionSummarySection(raw: unknown, path: string): KyberSessionSummarySection {
  if (!isRaw(raw)) throw malformedSection(path, 'body is not an object')
  const reconciliation = raw.reconciliation
  if (!isRaw(reconciliation)) throw malformedSection(path, 'reconciliation is not an object')
  if (!Array.isArray(raw.notes) || raw.notes.some((note) => typeof note !== 'string')) {
    throw malformedSection(path, 'notes is not a string array')
  }
  if (raw.summary !== null && !isRaw(raw.summary)) {
    throw malformedSection(path, 'summary is not an object or null')
  }

  const section: KyberSessionSummarySection = {
    session_id: requireStringField(raw, 'session_id', path),
    harness: requireStringField(raw, 'harness', path),
    is_subagent: requireBooleanField(raw, 'is_subagent', path),
    span_count: requireNumberField(raw, 'span_count', path),
    problems: requireNumberField(raw, 'problems', path),
    problems_count: requireNumberField(raw, 'problems_count', path),
    request_count: requireNumberField(raw, 'request_count', path),
    costBasisMismatch: readCostBasisMismatch(raw.costBasisMismatch, path),
    summary: raw.summary === null ? null : (raw.summary as Record<string, unknown>),
    notes: raw.notes as string[],
    reconciliation: {
      total: requireNumberField(reconciliation, 'total', path),
      mismatched: requireNumberField(reconciliation, 'mismatched', path),
    },
    tools_offered_count: requireNumberField(raw, 'tools_offered_count', path),
    tools_invoked_count: requireNumberField(raw, 'tools_invoked_count', path),
    unused_tools_count: requireNumberField(raw, 'unused_tools_count', path),
    skills_count: requireNumberField(raw, 'skills_count', path),
  }

  for (const key of [
    'label',
    'parent_session',
    'agent_name',
    'repo',
    'branch',
    'started',
    'ended',
    'context_limit_source',
  ] as const) {
    const value = readOptionalString(raw[key], path, key)
    if (value !== undefined) section[key] = value
  }
  if (typeof raw.projection_status === 'string') section.projection_status = raw.projection_status

  return section
}

// ── Charts ──────────────────────────────────────────────────────────────────

/** The charts section: the detail payload's turns and context, without its other reads. */
export interface KyberSessionChartsSection {
  sessionId: string
  turns: KyberSessionTurnRow[]
  context: KyberSessionContext
  projection_status?: string
}

function readChartTurn(value: unknown, path: string): KyberSessionTurnRow {
  if (!isRaw(value)) throw malformedSection(path, 'a turn is not an object')
  requireNumberField(value, 'index', path)
  return value as unknown as KyberSessionTurnRow
}

function parseSessionChartsSection(raw: unknown, path: string): KyberSessionChartsSection {
  if (!isRaw(raw)) throw malformedSection(path, 'body is not an object')
  if (!Array.isArray(raw.turns)) throw malformedSection(path, 'turns is not an array')
  const context = raw.context
  if (!isRaw(context) || typeof context.measurable !== 'boolean') {
    throw malformedSection(path, 'context is not a context document')
  }
  if (context.measurable) {
    if (!Array.isArray(context.turns)) throw malformedSection(path, 'context.turns is not an array')
    requireNumberField(context, 'contextLimit', path)
    const labels = readTokenizerFigure(context, path, 'context')
    for (const turn of context.turns) {
      if (!isRaw(turn)) throw malformedSection(path, 'context.turns holds a non-object')
      readTokenizerFigure(turn, path, 'a context turn')
    }
    const section: KyberSessionChartsSection = {
      sessionId: requireStringField(raw, 'sessionId', path),
      turns: raw.turns.map((turn) => readChartTurn(turn, path)),
      context: { ...(context as unknown as KyberSessionContext), ...labels },
    }
    if (typeof raw.projection_status === 'string') section.projection_status = raw.projection_status
    return section
  }
  if (typeof context.reason !== 'string') {
    throw malformedSection(path, 'context.reason is not a string')
  }

  const section: KyberSessionChartsSection = {
    sessionId: requireStringField(raw, 'sessionId', path),
    turns: raw.turns.map((turn) => readChartTurn(turn, path)),
    context: context as unknown as KyberSessionContext,
  }
  if (typeof raw.projection_status === 'string') section.projection_status = raw.projection_status
  return section
}

// ── Tools ───────────────────────────────────────────────────────────────────

/** One ranked tool row: never-invoked rows included. */
export interface KyberSessionToolRow {
  name: string
  server: string | null
  invocations: number
  schema_tokens?: number | null
  turns_resident?: number | null
  total_schema_cost?: number | null
  invoked: boolean
  in_definitions: boolean
  is_mcp: boolean
}

/** The unused-schema cost band, served once for every row rather than per page. */
export interface KyberUnusedRange {
  tokenResidencies: number
  floor: number
  ceiling: number
  currency: string | null
}

/** The tools section's aggregate, priced over every row before paging. */
export interface KyberSessionToolsAggregate {
  maxCost: number | null
  residentCostTotal: number
  invocationCount: number
  unusedCount: number
  unusedRange: KyberUnusedRange | null
  byServer: Record<string, number>
  definitionsSpanId: string | null
  /** The engine's measurability verdict: true only when the session stored a schema. */
  measurable: boolean
  /** Why the schema is not measurable; null when it is. */
  reason: string | null
}

/** One page of the tools section with the aggregate over the whole ranking. */
export interface KyberSessionToolsPage extends KyberSectionPage<KyberSessionToolRow> {
  aggregate: KyberSessionToolsAggregate
}

function readUnusedRange(value: unknown, path: string): KyberUnusedRange | null {
  if (value === null) return null
  if (!isRaw(value)) throw malformedSection(path, 'aggregate.unusedRange is not an object or null')
  const currency = value.currency === undefined ? null : readNullableString(value.currency, path, 'aggregate.unusedRange.currency')
  return {
    tokenResidencies: requireNumberField(value, 'tokenResidencies', path),
    floor: requireNumberField(value, 'floor', path),
    ceiling: requireNumberField(value, 'ceiling', path),
    currency,
  }
}

function readToolsAggregate(value: unknown, path: string): KyberSessionToolsAggregate {
  if (!isRaw(value)) throw malformedSection(path, 'aggregate is not an object')
  return {
    maxCost: readNullableNumber(value.maxCost, path, 'aggregate.maxCost'),
    residentCostTotal: requireNumberField(value, 'residentCostTotal', path),
    invocationCount: requireNumberField(value, 'invocationCount', path),
    unusedCount: requireNumberField(value, 'unusedCount', path),
    unusedRange: readUnusedRange(value.unusedRange, path),
    byServer: readNumberMap(value.byServer, path, 'aggregate.byServer'),
    definitionsSpanId: readNullableString(value.definitionsSpanId, path, 'aggregate.definitionsSpanId'),
    measurable: requireBooleanField(value, 'measurable', path),
    reason: readNullableString(value.reason, path, 'aggregate.reason'),
  }
}

function readToolRow(raw: Raw, path: string): KyberSessionToolRow {
  const row: KyberSessionToolRow = {
    name: requireStringField(raw, 'name', path),
    server: readNullableString(raw.server, path, 'server'),
    invocations: requireNumberField(raw, 'invocations', path),
    invoked: requireBooleanField(raw, 'invoked', path),
    in_definitions: requireBooleanField(raw, 'in_definitions', path),
    is_mcp: requireBooleanField(raw, 'is_mcp', path),
  }
  const schemaTokens = readOptionalNumber(raw.schema_tokens, path, 'schema_tokens')
  if (schemaTokens !== undefined) row.schema_tokens = schemaTokens
  const turnsResident = readOptionalNumber(raw.turns_resident, path, 'turns_resident')
  if (turnsResident !== undefined) row.turns_resident = turnsResident
  const totalSchemaCost = readOptionalNumber(raw.total_schema_cost, path, 'total_schema_cost')
  if (totalSchemaCost !== undefined) row.total_schema_cost = totalSchemaCost
  return row
}

function parseSessionToolsPage(raw: unknown, path: string): KyberSessionToolsPage {
  const page = parseSectionPage(raw, path, 'tools', readToolRow)
  if (!isRaw(raw)) throw malformedSection(path, 'body is not an object')
  return { ...page, aggregate: readToolsAggregate(raw.aggregate, path) }
}

// ── Servers ─────────────────────────────────────────────────────────────────

/** One per-server schema and invocation rollup row. */
export interface KyberSessionServerRow {
  server: string
  is_mcp: boolean
  tools: number
  schema_tokens: number
  total_schema_cost: number
  invocations: number
  unused_tools: number
  unused_cost: number
}

function readServerRow(raw: Raw, path: string): KyberSessionServerRow {
  return {
    server: requireStringField(raw, 'server', path),
    is_mcp: requireBooleanField(raw, 'is_mcp', path),
    tools: requireNumberField(raw, 'tools', path),
    schema_tokens: requireNumberField(raw, 'schema_tokens', path),
    total_schema_cost: requireNumberField(raw, 'total_schema_cost', path),
    invocations: requireNumberField(raw, 'invocations', path),
    unused_tools: requireNumberField(raw, 'unused_tools', path),
    unused_cost: requireNumberField(raw, 'unused_cost', path),
  }
}

// ── Skills ──────────────────────────────────────────────────────────────────

/**
 * The skills section: the detail payload's skill block with its rows paged.
 * A session that recorded no skills answers the whole-block not-measurable
 * verdict, with no rows.
 */
export type KyberSessionSkillsPage = (
  | { measurable: true; rows: KyberSkillRow[] }
  | (KyberSkillAvailability & { rows: KyberSkillRow[] })
) &
  KyberSectionWindow

function readSkillAvailability(value: unknown, path: string, key: string): KyberSkillAvailability {
  if (!isRaw(value) || value.availability !== 'not_measurable' || typeof value.reason !== 'string') {
    throw malformedSection(path, `${key} is not a measured figure`)
  }
  return { availability: 'not_measurable', reason: value.reason }
}

function readSkillFlag(value: unknown, path: string, key: string): boolean | KyberSkillAvailability {
  if (typeof value === 'boolean') return value
  return readSkillAvailability(value, path, key)
}

function readSkillCount(value: unknown, path: string, key: string): number | KyberSkillAvailability {
  if (typeof value === 'number' && Number.isInteger(value) && value >= 0) return value
  return readSkillAvailability(value, path, key)
}

function readIntegerArray(value: unknown, path: string, key: string): number[] {
  if (!Array.isArray(value)) throw malformedSection(path, `${key} is not an array`)
  return value.map((entry) => {
    if (typeof entry !== 'number' || !Number.isInteger(entry)) {
      throw malformedSection(path, `${key} holds a non-integer`)
    }
    return entry
  })
}

function readSkillRow(raw: Raw, path: string): KyberSkillRow {
  const row: KyberSkillRow = {
    name: requireStringField(raw, 'name', path),
    listed: readSkillFlag(raw.listed, path, 'listed'),
    invocations: readSkillCount(raw.invocations, path, 'invocations'),
    turns_listed: readIntegerArray(raw.turns_listed, path, 'turns_listed'),
  }
  const derivedTokens = readOptionalNumber(raw.derived_tokens, path, 'derived_tokens')
  if (derivedTokens !== undefined && derivedTokens !== null) row.derived_tokens = derivedTokens
  Object.assign(row, readPartTokenizerFigure(raw, path, 'a skill row'))
  const description = readOptionalString(raw.description, path, 'description')
  if (description !== undefined && description !== null) row.description = description
  return row
}

function parseSessionSkillsPage(raw: unknown, path: string): KyberSessionSkillsPage {
  if (!isRaw(raw)) throw malformedSection(path, 'body is not an object')
  if (!Array.isArray(raw.rows)) throw malformedSection(path, 'rows is not an array')
  const rows = raw.rows.map((row) => {
    if (!isRaw(row)) throw malformedSection(path, 'a skill row is not an object')
    return readSkillRow(row, path)
  })
  const window = readSectionWindow(raw, path)
  if (raw.availability === 'not_measurable') {
    if (typeof raw.reason !== 'string') throw malformedSection(path, 'reason is not a string')
    return { availability: 'not_measurable', reason: raw.reason, rows, ...window }
  }
  if (raw.measurable !== true) throw malformedSection(path, 'measurable is not true')
  return { measurable: true, rows, ...window }
}

/**
 * One KW skill load: a skill the agent loaded on demand, with the agent, the
 * session and span it came from, and when it loaded. A load whose skill name
 * was not recorded carries `skill: null`; nothing is defaulted. Its session and
 * span ids are always present, since the row is keyed and linked by the span.
 */
export interface KyberKwSkillRow {
  skill: string | null
  agent: string | null
  session_id: string
  span_id: string
  loaded_at: string | null
}

/** One page of a session's KW skill loads, from `GET /api/kyber/session/:id/kw-skills`. */
export interface KyberSessionKwSkillsPage extends KyberSectionWindow {
  rows: KyberKwSkillRow[]
}

function readKwSkillRow(raw: Raw, path: string): KyberKwSkillRow {
  const sessionId = requireStringField(raw, 'session_id', path)
  if (sessionId.trim() === '') throw malformedSection(path, 'session_id is empty')
  const spanId = requireStringField(raw, 'span_id', path)
  if (spanId.trim() === '') throw malformedSection(path, 'span_id is empty')
  return {
    skill: readNullableString(raw.skill, path, 'skill'),
    agent: readNullableString(raw.agent, path, 'agent'),
    session_id: sessionId,
    span_id: spanId,
    loaded_at: readNullableString(raw.loaded_at, path, 'loaded_at'),
  }
}

function parseSessionKwSkillsPage(raw: unknown, path: string): KyberSessionKwSkillsPage {
  if (!isRaw(raw)) throw malformedSection(path, 'body is not an object')
  if (!Array.isArray(raw.rows)) throw malformedSection(path, 'rows is not an array')
  const rows = raw.rows.map((row) => {
    if (!isRaw(row)) throw malformedSection(path, 'a KW skill row is not an object')
    return readKwSkillRow(row, path)
  })
  return { rows, ...readSectionWindow(raw, path) }
}

// ── Timeline ────────────────────────────────────────────────────────────────

/** One timeline node: today's node fields minus `children`, plus its child count. */
export interface KyberSessionTimelineNode {
  spanId: string
  parentId: string | null
  startMs: number
  durationMs: number
  kind: string
  name: string
  status: string | null
  attributes: Record<string, unknown>
  isSubagent: boolean
  isAuxiliary: boolean
  cost: Record<string, unknown>
  childCount: number
}

/** One page of one timeline level, with the whole tree's bounds. */
export interface KyberSessionTimelinePage extends KyberSectionWindow {
  sessionId: string
  /** The parent whose children this page lists; null for the top level. */
  parent: string | null
  nodes: KyberSessionTimelineNode[]
  bounds: { startMs: number; endMs: number }
}

function readTimelineNode(raw: Raw, path: string): KyberSessionTimelineNode {
  const attributes = raw.attributes
  if (!isRaw(attributes)) throw malformedSection(path, 'attributes is not an object')
  const cost = raw.cost
  if (!isRaw(cost)) throw malformedSection(path, 'cost is not an object')
  return {
    spanId: requireStringField(raw, 'spanId', path),
    parentId: readNullableString(raw.parentId, path, 'parentId'),
    startMs: requireNumberField(raw, 'startMs', path),
    durationMs: requireNumberField(raw, 'durationMs', path),
    kind: requireStringField(raw, 'kind', path),
    name: requireStringField(raw, 'name', path),
    status: readNullableString(raw.status, path, 'status'),
    attributes,
    isSubagent: requireBooleanField(raw, 'isSubagent', path),
    isAuxiliary: requireBooleanField(raw, 'isAuxiliary', path),
    cost,
    childCount: requireNumberField(raw, 'childCount', path),
  }
}

function parseSessionTimelinePage(raw: unknown, path: string): KyberSessionTimelinePage {
  if (!isRaw(raw)) throw malformedSection(path, 'body is not an object')
  if (!Array.isArray(raw.nodes)) throw malformedSection(path, 'nodes is not an array')
  const bounds = raw.bounds
  if (!isRaw(bounds)) throw malformedSection(path, 'bounds is not an object')
  const page: KyberSessionTimelinePage = {
    sessionId: requireStringField(raw, 'sessionId', path),
    parent: readNullableString(raw.parent, path, 'parent'),
    nodes: raw.nodes.map((node) => {
      if (!isRaw(node)) throw malformedSection(path, 'a node is not an object')
      return readTimelineNode(node, path)
    }),
    ...readSectionWindow(raw, path),
    bounds: {
      startMs: requireNumberField(bounds, 'startMs', path),
      endMs: requireNumberField(bounds, 'endMs', path),
    },
  }
  return page
}

// ── Requests, reconciliation and problems ───────────────────────────────────

/** One request row: identity and position only; its prompt preview may be absent. */
export interface KyberSessionRequestRow {
  request: string
  timestamp: string | null
  turns: number | null
  model: string | null
  prompt: string | null
}

function readRequestRow(raw: Raw, path: string): KyberSessionRequestRow {
  return {
    request: requireStringField(raw, 'request', path),
    timestamp: readNullableString(raw.timestamp, path, 'timestamp'),
    turns: readNullableNumber(raw.turns, path, 'turns'),
    model: readNullableString(raw.model, path, 'model'),
    prompt: readNullableString(raw.prompt, path, 'prompt'),
  }
}

/** One measurable input/output reconciliation row. */
export interface KyberSessionReconciliationRow {
  request: string
  root_input: number | null
  sum_chat_input: number | null
  input_match: boolean
  root_output: number | null
  sum_chat_output: number | null
  output_match: boolean
}

function readReconciliationRow(raw: Raw, path: string): KyberSessionReconciliationRow {
  return {
    request: requireStringField(raw, 'request', path),
    root_input: readNullableNumber(raw.root_input, path, 'root_input'),
    sum_chat_input: readNullableNumber(raw.sum_chat_input, path, 'sum_chat_input'),
    input_match: requireBooleanField(raw, 'input_match', path),
    root_output: readNullableNumber(raw.root_output, path, 'root_output'),
    sum_chat_output: readNullableNumber(raw.sum_chat_output, path, 'sum_chat_output'),
    output_match: requireBooleanField(raw, 'output_match', path),
  }
}

/** One stored ingest problem row. */
export interface KyberIngestProblemRow {
  id: number
  spanId: string
  severity: string
  code: string
  message: string
  location: string | null
  sessionId: string | null
  harness: string | null
  timestamp: string | null
}

function readIngestProblemRow(raw: Raw, path: string): KyberIngestProblemRow {
  return {
    id: requireNumberField(raw, 'id', path),
    spanId: requireStringField(raw, 'spanId', path),
    severity: requireStringField(raw, 'severity', path),
    code: requireStringField(raw, 'code', path),
    message: requireStringField(raw, 'message', path),
    location: readNullableString(raw.location, path, 'location'),
    sessionId: readNullableString(raw.sessionId, path, 'sessionId'),
    harness: readNullableString(raw.harness, path, 'harness'),
    timestamp: readNullableString(raw.timestamp, path, 'timestamp'),
  }
}

// ── Stored fields ───────────────────────────────────────────────────────────

function readStoredCell(value: unknown, path: string): StoredFieldCell {
  if (!isRaw(value)) throw malformedSection(path, 'a stored cell is not an object')
  const cell: StoredFieldCell = { columnName: requireStringField(value, 'columnName', path) }
  if (value.columnType !== undefined) cell.columnType = value.columnType
  const text = readOptionalString(value.textValue, path, 'textValue')
  if (text !== undefined) cell.textValue = text
  const integer = readOptionalNumber(value.integerValue, path, 'integerValue')
  if (integer !== undefined) cell.integerValue = integer
  const real = readOptionalNumber(value.realValue, path, 'realValue')
  if (real !== undefined) cell.realValue = real
  return cell
}

function readStoredRow(value: unknown, path: string): StoredFieldRow {
  if (!isRaw(value) || !Array.isArray(value.cells)) {
    throw malformedSection(path, 'a stored row is not a cell array carrier')
  }
  return { cells: value.cells.map((cell) => readStoredCell(cell, path)) }
}

function readStoredTable(value: unknown, path: string): StoredTableFields {
  if (!isRaw(value)) throw malformedSection(path, 'a stored table is not an object')
  const rowCount = value.rowCount
  if (typeof rowCount !== 'number' || !Number.isFinite(rowCount)) {
    throw malformedSection(path, 'a stored table rowCount is not a number')
  }
  return {
    tableName: requireStringField(value, 'tableName', path),
    columnNames: readStringArray(value.columnNames, path, 'columnNames'),
    rowCount,
    missingParentKeys: readStringArray(value.missingParentKeys, path, 'missingParentKeys'),
  }
}

function parseStoredFields(raw: unknown, path: string): StoredFieldsResponse {
  if (!isRaw(raw)) throw malformedSection(path, 'body is not an object')
  if (!Array.isArray(raw.tables)) throw malformedSection(path, 'tables is not an array')
  return {
    sessionKey: requireStringField(raw, 'sessionKey', path),
    tables: raw.tables.map((table) => readStoredTable(table, path)),
  }
}

function parseStoredFieldRowsPage(raw: unknown, path: string): StoredFieldRowsPage {
  if (!isRaw(raw)) throw malformedSection(path, 'body is not an object')
  if (!Array.isArray(raw.rows)) throw malformedSection(path, 'rows is not an array')
  const totalRows = raw.totalRows
  if (typeof totalRows !== 'number' || !Number.isFinite(totalRows)) {
    throw malformedSection(path, 'totalRows is not a number')
  }
  const page = raw.page
  const limit = raw.limit
  const offset = raw.offset
  if (typeof page !== 'number' || !Number.isInteger(page) || page < 1) throw malformedSection(path, 'page is not a page number')
  if (typeof limit !== 'number' || !Number.isInteger(limit) || limit < 1) throw malformedSection(path, 'limit is not a page size')
  if (typeof offset !== 'number' || !Number.isInteger(offset) || offset < 0) throw malformedSection(path, 'offset is not a row offset')
  return {
    tableName: requireStringField(raw, 'tableName', path),
    columnNames: readStringArray(raw.columnNames, path, 'columnNames'),
    totalRows,
    page,
    limit,
    offset,
    rows: raw.rows.map((row) => readStoredRow(row, path)),
  }
}

// ── Turn ────────────────────────────────────────────────────────────────────

/** The widened per-turn section: the turn's figures plus its stored context buckets. */
export interface KyberSessionTurnSection {
  sessionId: string
  turnIndex: number
  /** Absent when the payload names a faulted projection instead of a turn row. */
  turn?: KyberSessionTurnRow
  context: KyberContextTurn | null
  projection_status?: string
}

function parseSessionTurnSection(raw: unknown, path: string): KyberSessionTurnSection {
  if (!isRaw(raw)) throw malformedSection(path, 'body is not an object')
  // A faulted projection read answers the sections' failure shape: identity plus the
  // projection_status marker, no turn row. The section carries the marker; the page
  // renders the failure state, never a neighbour's figures.
  if (raw.projection_status === TURN_PROJECTION_FAILED && !isRaw(raw.turn)) {
    return {
      sessionId: requireStringField(raw, 'sessionId', path),
      turnIndex: requireIntegerField(raw, 'turnIndex', path, 0),
      context: null,
      projection_status: raw.projection_status,
    }
  }
  const turn = raw.turn
  if (!isRaw(turn)) throw malformedSection(path, 'turn is not an object')
  requireNumberField(turn, 'index', path)
  if (raw.context !== null) {
    if (!isRaw(raw.context)) throw malformedSection(path, 'context is not an object or null')
    requireNumberField(raw.context, 'index', path)
    if (!isRaw(raw.context.buckets)) throw malformedSection(path, 'context.buckets is not an object')
    readTokenizerFigure(raw.context, path, 'context')
  }
  const section: KyberSessionTurnSection = {
    sessionId: requireStringField(raw, 'sessionId', path),
    turnIndex: requireIntegerField(raw, 'turnIndex', path, 0),
    turn: turn as unknown as KyberSessionTurnRow,
    context: raw.context === null ? null : (raw.context as unknown as KyberContextTurn),
  }
  if (typeof raw.projection_status === 'string') section.projection_status = raw.projection_status
  return section
}

// ── Section fetchers ────────────────────────────────────────────────────────

function sectionPath(sessionId: string, section: string, page: number): string {
  const params = new URLSearchParams()
  params.set('page', String(page))
  params.set('limit', String(KYBER_SECTION_PAGE_LIMIT))
  return `/api/kyber/session/${encodeURIComponent(sessionId)}/${section}?${params.toString()}`
}

/** Reads the bounded summary section. */
export async function fetchKyberSessionSummary(sessionId: string): Promise<KyberSessionSummarySection> {
  const path = `/api/kyber/session/${encodeURIComponent(sessionId)}/summary`
  return parseSessionSummarySection(await fetchJson<unknown>(path), path)
}

/** Reads the charts section: the session's turns and context only. */
export async function fetchKyberSessionCharts(sessionId: string): Promise<KyberSessionChartsSection> {
  const path = `/api/kyber/session/${encodeURIComponent(sessionId)}/charts`
  return parseSessionChartsSection(await fetchJson<unknown>(path), path)
}

/** Reads one page of the ranked tool rows with the aggregate over every row. */
export async function fetchKyberSessionTools(
  sessionId: string,
  page = 1,
): Promise<KyberSessionToolsPage> {
  const path = sectionPath(sessionId, 'tools', page)
  return parseSessionToolsPage(await fetchJson<unknown>(path), path)
}

/** Reads one page of the per-server schema and invocation rollup. */
export async function fetchKyberSessionServers(
  sessionId: string,
  page = 1,
): Promise<KyberSectionPage<KyberSessionServerRow>> {
  const path = sectionPath(sessionId, 'servers', page)
  return parseSectionPage(await fetchJson<unknown>(path), path, 'servers', readServerRow)
}

/** Reads one page of the session's skill rows. */
export async function fetchKyberSessionSkills(
  sessionId: string,
  page = 1,
): Promise<KyberSessionSkillsPage> {
  const path = sectionPath(sessionId, 'skills', page)
  return parseSessionSkillsPage(await fetchJson<unknown>(path), path)
}

/** Reads one page of the session's KW skill loads. */
export async function fetchKyberSessionKwSkills(
  sessionId: string,
  page = 1,
): Promise<KyberSessionKwSkillsPage> {
  const path = sectionPath(sessionId, 'kw-skills', page)
  return parseSessionKwSkillsPage(await fetchJson<unknown>(path), path)
}

/** Reads one level of the session's timeline tree, page by page. */
export async function fetchKyberSessionTimeline(
  sessionId: string,
  opts?: { parent?: string | null; page?: number },
): Promise<KyberSessionTimelinePage> {
  const page = opts?.page ?? 1
  const params = new URLSearchParams()
  params.set('page', String(page))
  params.set('limit', String(KYBER_SECTION_PAGE_LIMIT))
  if (opts?.parent !== undefined && opts.parent !== null && opts.parent !== '') {
    params.set('parent', opts.parent)
  }
  const path = `/api/kyber/session/${encodeURIComponent(sessionId)}/timeline?${params.toString()}`
  return parseSessionTimelinePage(await fetchJson<unknown>(path), path)
}

/** Reads one page of the session's request rows, each with its prompt preview. */
export async function fetchKyberSessionRequests(
  sessionId: string,
  page = 1,
): Promise<KyberSectionPage<KyberSessionRequestRow>> {
  const path = sectionPath(sessionId, 'requests', page)
  return parseSectionPage(await fetchJson<unknown>(path), path, 'requests', readRequestRow)
}

/** Reads one page of the session's measurable reconciliation rows. */
export async function fetchKyberSessionReconciliation(
  sessionId: string,
  page = 1,
): Promise<KyberSectionPage<KyberSessionReconciliationRow>> {
  const path = sectionPath(sessionId, 'reconciliation', page)
  return parseSectionPage(
    await fetchJson<unknown>(path),
    path,
    'reconciliation',
    readReconciliationRow,
  )
}

/** Reads one page of the session's stored ingest problems. */
export async function fetchKyberSessionProblems(
  sessionId: string,
  page = 1,
): Promise<KyberSectionPage<KyberIngestProblemRow>> {
  const path = sectionPath(sessionId, 'problems', page)
  return parseSectionPage(await fetchJson<unknown>(path), path, 'problems', readIngestProblemRow)
}

/** Reads the widened per-turn section: the turn's figures and its context buckets. */
export async function fetchKyberSessionTurn(
  sessionId: string,
  turnIndex: number,
): Promise<KyberSessionTurnSection> {
  const path = `/api/kyber/session/${encodeURIComponent(sessionId)}/turn/${turnIndex}`
  return parseSessionTurnSection(await fetchJson<unknown>(path), path)
}
