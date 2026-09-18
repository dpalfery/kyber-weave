/// Period rollups shared by the aggregators.
///
/// These types were carved out of `menubar-json.ts` when that module and
/// `status --format menubar-json` were deleted (R7.6). They describe one time
/// window's spend, which the day-aggregator and `buildPeriodData` compute for
/// their own sake — the payload they used to feed was only one consumer.
import type { ReworkedFile } from './workflow-insights.js'
import type { PrRow, BranchRow } from './sessions-report.js'

export type PeriodData = {
  label: string
  cost: number
  /// Counterfactual USD the same tokens would have cost on the paid
  /// baseline configured for each local model. Stays `0` when no
  /// `codeburn model-savings` mappings are active. Always shown
  /// separately from `cost` so the two never get summed into a "real
  /// spend" number by accident.
  savingsUSD: number
  /// Portion of `cost` priced from estimated tokens (see ParsedApiCall.isEstimated).
  /// Display/metadata only; never summed into `cost`. Optional so PeriodData
  /// producers predating the field keep compiling.
  estimatedCostUSD?: number
  calls: number
  sessions: number
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  /// Total Codex credits consumed in the period (issues #408/#495). Optional so
  /// non-menubar PeriodData producers don't have to compute it.
  codexCredits?: number
  categories: Array<{ name: string; cost: number; savingsUSD: number; turns: number; editTurns: number; oneShotTurns: number }>
  models: Array<{ name: string; cost: number; savingsUSD: number; calls: number; estimatedCostUSD?: number }>
  /// Models with usage in the period whose pricing lookup fails against the
  /// current tables (#638): their calls contribute $0 to `cost`. Optional so
  /// PeriodData producers that predate the field keep compiling.
  unpricedModels?: Array<{ model: string; calls: number; tokens: number }>
  projects?: Array<{ name: string; cost: number; savingsUSD: number; sessions: number; sessionDetails?: Array<{ cost: number; savingsUSD: number; calls: number; inputTokens: number; outputTokens: number; date: string; models: Array<{ name: string; cost: number; savingsUSD: number }> }> }>
  modelEfficiency?: Array<{ name: string; costPerEdit: number | null; oneShotRate: number | null }>
  topSessions?: Array<{ project: string; cost: number; savingsUSD: number; calls: number; date: string }>
  /// Workflow-intelligence rollups (issue: workflow intelligence). Optional so
  /// the day-aggregator PeriodData path (which has no per-turn data) can omit
  /// them; the fresh-parse payload path always sets them.
  workflow?: { corrections: number; correctionRate: number | null; medianTimeToFirstEditMs: number | null }
  /// Files most reworked by edit-family calls, relative to project root, ranked
  /// by distinct sessions then edits. Full (top 15) list; the payload basenames
  /// and trims it.
  topReworkedFiles?: ReworkedFile[]
  /// Share (0-1) of cost-bearing calls that resolved a price.
  pricingCoverage?: number
  /// Spend attributed by referenced pull request (from Claude session
  /// transcripts), at turn granularity. Rows carry attributed cost/calls and ARE
  /// summable; `attributedCost`/`unattributedCost` split the PR-linked spend.
  /// Absent when no PR links were observed.
  pullRequests?: PullRequestsPayload
  /// Per-branch spend, last-seen branch carried forward across each session's
  /// turns. A `null` branch is unbranched spend inside a branch-bearing session.
  /// Rows are by-reference (a session that switched branches counts toward each),
  /// so never sum them. Absent when no branch data was observed.
  byBranch?: BranchRow[]
}

export type PullRequestsPayload = {
  /// Every attributed PR row, cost-descending.
  rows: PrRow[]
  /// PR-linked spend, now INCLUDING the subagent runs folded into those sessions
  /// (so it can exceed the parents' own spend). Equals `attributedCost +
  /// unattributedCost`; kept for backward compatibility.
  distinctCost: number
  /// Count of distinct PR-linked PARENT sessions.
  distinctSessions: number
  /// Count of subagent (sidechain) runs whose spend was folded into those parent
  /// sessions. Each remains a standalone row in the sessions list; here it only
  /// explains why the totals exceed the parents' own spend. 0 when none folded.
  subagentSessions?: number
  /// Sum of every PR's attributed cost.
  attributedCost: number
  /// PR-linked spend not tied to any specific PR (pre-reference session
  /// overhead). `attributedCost + unattributedCost === distinctCost`.
  unattributedCost: number
}
