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
