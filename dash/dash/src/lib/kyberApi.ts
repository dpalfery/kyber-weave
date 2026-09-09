export type KyberContextAnalysis = import('@/components/kyber/ContextView').ContextAnalysis
export type KyberSchemaAnalysis = import('@/components/kyber/SchemaView').SchemaCostAnalysis
export type KyberTimelineNode = import('@/components/kyber/TimelineView').TimelineNode
export type KyberComparisonTable = import('@/components/kyber/CompareView').ComparisonTable
export type KyberQuarantineEntry = import('@/components/kyber/QuarantineView').QuarantineEntry
export type KyberProblemEntry = import('@/components/kyber/ProblemsView').ProblemEntry

async function fetchJson<T>(path: string): Promise<T> {
  const res = await fetch(path)
  if (!res.ok) throw new Error(`Request failed (${res.status}) for ${path}`)
  return res.json() as Promise<T>
}

export function fetchKyberContext(): Promise<KyberContextAnalysis> {
  return fetchJson<KyberContextAnalysis>('/api/kyber/context')
}
export function fetchKyberSchema(): Promise<KyberSchemaAnalysis> {
  return fetchJson<KyberSchemaAnalysis>('/api/kyber/schema')
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
  scorecard?: ScorecardData
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

