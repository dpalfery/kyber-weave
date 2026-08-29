/// Shape of the JSON returned by `codeburn status --format menubar-json`. Kept in sync with
/// `src/menubar-json.ts` (CLI) and `mac/Sources/CodeBurnMenubar/Data/MenubarPayload.swift`
/// (macOS app). Any field change there must land here too or the frontend silently drops it.
export type MenubarPayload = {
  generated: string
  current: {
    label: string
    cost: number
    calls: number
    sessions: number
    oneShotRate: number | null
    inputTokens: number
    outputTokens: number
    cacheHitPercent: number
    topActivities: Activity[]
    topModels: Model[]
    providers: Record<string, number>
  }
  optimize: {
    findingCount: number
    savingsUSD: number
    topFindings: Array<{ title: string; impact: 'high' | 'medium' | 'low'; savingsUSD: number }>
  }
  history: { daily: DailyEntry[] }
}

export type Activity = {
  name: string
  cost: number
  turns: number
  oneShotRate: number | null
}

export type Model = {
  name: string
  cost: number
  calls: number
}

export type DailyModel = {
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
  topModels?: DailyModel[]
}
