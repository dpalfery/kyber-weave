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
export function fetchKyberProblems(): Promise<{ problems: KyberProblemEntry[] }> {
  return fetchJson<{ problems: KyberProblemEntry[] }>('/api/kyber/problems')
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
  models?: string[]
  problems?: number
}

/**
 * The session payload fields ancestry needs. `runId` is optional because the
 * served document does not yet carry it — resolveAncestry reads it when present
 * rather than inventing a run by scanning other endpoints (R7.5).
 */
export type KyberSessionAncestry = {
  id?: string
  session_id?: string
  harness: string
  runId?: string
  run_id?: string
  executionId?: string
  execution_id?: string
}

export async function fetchKyberSession(sessionId: string): Promise<KyberSessionAncestry> {
  return fetchJson<KyberSessionAncestry>(`/api/kyber/session/${encodeURIComponent(sessionId)}`)
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
  // Read defensively: the bucket drill-down probes a reported-input figure on
  // turn rows too, though the engine's TurnPressure does not carry one.
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
  opts?: { span?: string; part?: string },
): Promise<KyberSessionContentResult> {
  const params = new URLSearchParams()
  if (opts?.span) params.set('span', opts.span)
  if (opts?.part) params.set('part', opts.part)
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
}

export interface KyberTurnContentResult {
  sessionId: string
  turnIndex: number
  spanId?: string
  model?: string
  blocks: KyberTurnContentBlock[]
  parts: KyberTurnContentPart[]
  assembledText: string
  truncated?: boolean
  totalLength?: number
  measurability?: string | Record<string, unknown>
  notMeasurable?: { reason: string }
}

export function fetchTurnContent(
  sessionId: string,
  turnIndex: number,
  opts?: { budget?: number },
): Promise<KyberTurnContentResult> {
  const params = new URLSearchParams()
  if (opts?.budget) params.set('budget', String(opts.budget))
  const qs = params.toString()
  const path = `/api/kyber/session/${encodeURIComponent(sessionId)}/turn/${turnIndex}/content${qs ? `?${qs}` : ''}`
  return fetchJson<KyberTurnContentResult>(path)
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
  estimatedWasteTokens: number
  recommendation: string
  errorBar: {
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
  outcome?: {
    status?: string
    exitCode?: number | null
    testDelta?: { passed?: number; failed?: number; total?: number } | null
    userCorrections?: number
    abandoned?: boolean
    reason?: string
  } | null
  findingCount?: number
  scorecard?: ScorecardData
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
  }>(`/api/kyber/run/${encodeURIComponent(runId)}`)

  const run = json.run ?? (json as unknown as KyberRunSummary)
  return {
    ...run,
    executionTree: json.executionTree ?? [],
    executions: json.executions ?? [],
    findings: json.findings ?? [],
  }
}

export async function fetchFindings(opts?: {
  runId?: string
  sessionId?: string
  harness?: string
  limit?: number
}): Promise<KyberFinding[]> {
  const params = new URLSearchParams()
  if (opts?.runId) params.set('runId', opts.runId)
  if (opts?.sessionId) params.set('sessionId', opts.sessionId)
  if (opts?.limit) params.set('limit', String(opts.limit))
  const qs = params.toString()
  const json = await fetchJson<{ findings: KyberFinding[] }>(`/api/kyber/findings${qs ? `?${qs}` : ''}`)
  let list = json.findings ?? []
  if (opts?.harness) {
    list = list.filter((f) => !f.harness || f.harness.toLowerCase() === opts.harness!.toLowerCase())
  }
  return list
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

export async function fetchCalibration(opts?: { runId?: string }): Promise<KyberCalibrationSummary> {
  const params = new URLSearchParams()
  if (opts?.runId) params.set('runId', opts.runId)
  const qs = params.toString()
  const json = await fetchJson<KyberCalibrationResponse>(`/api/kyber/calibration${qs ? `?${qs}` : ''}`)
  return json.calibration ?? json
}

/** Task phase used by phase-aligned run comparison. */
export type KyberTaskPhase = 'exploration' | 'implementation' | 'verification' | 'resolution'

export interface KyberComparisonVerdict {
  status: 'promoted' | 'candidate_only' | 'insufficient_history' | 'outcome_regression' | 'neutral'
  pairCount: number
  completedPairCount: number
  meetsSufficiencyThreshold: boolean
  outcomeRegression: boolean
  canPromote: boolean
  recommendation: string
  refusalReason?: string
  summary: string
}

export interface KyberPhaseAlignedTurnPair {
  phase: KyberTaskPhase
  phaseIndex: number
  runATurn: Record<string, unknown> | null
  runBTurn: Record<string, unknown> | null
  signals: Array<Record<string, unknown>>
  reading: string
}

/** `GET /api/kyber/compare/runs` body — engine `ComparisonSummary` without turn `raw`. */
export interface KyberRunComparison {
  runA: {
    runId: string
    harness: string
    label?: string
    outcome?: unknown
    totalTokens: number
    totalCost?: number
    turnCount: number
  }
  runB: {
    runId: string
    harness: string
    label?: string
    outcome?: unknown
    totalTokens: number
    totalCost?: number
    turnCount: number
  }
  taskFamily?: string
  pairs: KyberPhaseAlignedTurnPair[]
  phaseSummaries: Record<KyberTaskPhase, Record<string, unknown>>
  totals: Record<string, unknown>
  verdict: KyberComparisonVerdict
}

/**
 * Phase-aligned comparison of two canon runs (`docs/plans/2026-09-06-kyberdash-spine.md` § B4-api).
 * Missing runs fail with the same `Request failed (404)` pattern as `fetchRun`.
 */
export async function fetchRunComparison(
  runAId: string,
  runBId: string,
  opts?: { completedPairCount?: number },
): Promise<KyberRunComparison> {
  const params = new URLSearchParams()
  params.set('runA', runAId)
  params.set('runB', runBId)
  if (opts?.completedPairCount !== undefined) {
    params.set('completedPairCount', String(opts.completedPairCount))
  }
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

export async function fetchReviewStatus(): Promise<{ provider: string; isConfigured: boolean }> {
  return fetchJson<{ provider: string; isConfigured: boolean }>('/api/kyber/review/status')
}

