export type Period = 'today' | 'week' | '30days' | 'month' | 'all' | 'lifetime'

export type ModelDay = {
  name: string
  cost: number
  calls: number
  inputTokens: number
  outputTokens: number
}

export type DailyEntry = {
  date: string
  cost: number
  calls: number
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  topModels: ModelDay[]
}

export type GranularSeries = { id: string; label: string }
export type GranularValue = { seriesId: string; cost: number; tokens: number }
export type GranularPoint = {
  timestamp: string
  cost: number
  tokens: number
  models: GranularValue[]
  sessions: GranularValue[]
}
export type GranularHistory = {
  bucketMinutes: number
  modelSeries: GranularSeries[]
  sessionSeries: GranularSeries[]
  points: GranularPoint[]
}

export type Current = {
  label: string
  cost: number
  calls: number
  sessions: number
  oneShotRate: number | null
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  cacheHitPercent: number
  codexCredits: number
  topActivities: Array<{ name: string; cost: number; turns: number; oneShotRate: number | null }>
  topModels: Array<{ name: string; cost: number; calls: number; savingsUSD: number }>
  providers: Record<string, number>
  providerDetails?: Array<{ id: string; label: string; cost: number }>
  topProjects: Array<{ name: string; cost: number; sessions: number; avgCostPerSession: number }>
  tools: Array<{ name: string; calls: number }>
  subagents: Array<{ name: string; calls: number; cost: number }>
  skills: Array<{ name: string; turns: number; cost: number }>
  mcpServers: Array<{ name: string; calls: number }>
  modelEfficiency: Array<{ name: string; costPerEdit: number; oneShotRate: number }>
  // Workflow-intelligence rollup for the period. Optional: an older peer's
  // payload predates the block, and the Workflow panel hides when it is absent.
  workflow?: { corrections: number; correctionRate: number | null; medianTimeToFirstEditMs: number | null }
  // Files most reworked by edit-family calls (top 8), basenames only.
  topReworkedFiles?: Array<{ path: string; sessions: number; edits: number }>
  // Share (0-1) of cost-bearing calls that resolved a price. null when not
  // computable; "unknown" must never render as 100% coverage.
  pricingCoverage?: number | null
  localModelSavings: { totalUSD: number }
  retryTax: { totalUSD: number; retries: number }
  routingWaste: { totalSavingsUSD: number }
}

export type Payload = {
  generated: string
  // Only a producer that its clients poll (the resident serve child) ever sends
  // this, and only it may answer partially. `complete: false` means the totals
  // cover the files indexed so far. Absence must be read as complete.
  hydration?: { complete: boolean; indexedFiles: number; totalFiles: number }
  current: Current
  history: { daily: DailyEntry[]; timeline?: GranularHistory }
}

export async function fetchUsage(period: Period, provider: string): Promise<Payload> {
  const res = await fetch(`/api/usage?period=${encodeURIComponent(period)}&provider=${encodeURIComponent(provider)}`)
  if (!res.ok) throw new Error(`Request failed (${res.status})`)
  return res.json() as Promise<Payload>
}

export type DeviceUsage = {
  id: string
  name: string
  local: boolean
  payload?: Payload
  error?: string
}

export const PERIODS: Array<{ key: Period; label: string }> = [
  { key: 'today', label: 'Today' },
  { key: 'week', label: '7 days' },
  { key: '30days', label: '30 days' },
  { key: 'month', label: 'Month' },
  { key: 'all', label: '6 months' },
  { key: 'lifetime', label: 'Lifetime' },
]

export type ContextProvider =
  | 'agent-all'
  | 'claude'
  | 'codex'
  | 'antigravity'
  | 'copilot-cli'
  | 'copilot-vscode'
  | 'copilot-agent'
  | 'copilot'
  | 'pi'
  | 'opencode'
  | 'kilo-code'
  | 'cursor'
