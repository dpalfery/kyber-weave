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
