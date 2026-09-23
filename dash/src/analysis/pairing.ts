// Candidate run pairing heuristics for KyberDash (Plan Task G4 / Decision D11).
//
// Acceptance Criterion 3:
// "Candidate pairing rule: Auto-pairing is proposed only, requiring n >= 5 completed
// pairs before promoting recommendations."
//
// Key principles:
// 1. Automatic pairing is PROPOSED ONLY, never applied without explicit user confirmation.
// 2. Candidate pairing heuristics evaluate task family identity, workspace similarity,
//    harness divergence (A/B testing), and outcome comparability.
// 3. Promoting a candidate pair into an automated recommendation strictly requires
//    n >= 5 completed pairs on the same task family without outcome regression.

import type { OutcomeBlock } from '../canon/outcome.js'

/** Minimum completed historical pairs required before promoting recommendations (Acceptance Criterion 3). */
export const MINIMUM_COMPLETED_PAIRS = 5

/** Heuristics used to identify candidate run pairs. */
export type PairingHeuristic =
  | 'same_task_family'
  | 'same_working_directory'
  | 'same_repo'
  | 'different_harness'
  | 'outcome_comparable'
  | 'temporal_proximity'

/** Candidate run input for pairing analysis. */
export type CandidateRun = {
  runId: string
  harness: string
  label?: string
  taskFamily?: string
  workingDirectory?: string | null
  repo?: string | null
  started?: string | null
  ended?: string | null
  outcome?: OutcomeBlock
  executionCount?: number
  turnCount?: number
}

/**
 * Result of evaluating pair candidate confidence.
 */
export type PairConfidenceResult = {
  confidence: number
  heuristics: PairingHeuristic[]
  reasons: string[]
}

/**
 * Proposed run pair carrying pairing heuristics and recommendation status.
 * In accordance with Acceptance Criterion 3, status is ALWAYS 'proposed'
 * requiring user confirmation.
 */
export type ProposedRunPair = {
  pairId: string
  runA: CandidateRun
  runB: CandidateRun
  taskFamily?: string
  confidence: number
  heuristics: PairingHeuristic[]
  reasons: string[]
  /** Auto-pairing is proposed only, always user-confirmed (Criterion 3). */
  status: 'proposed'
  completedPairCount: number
  meetsSufficiencyThreshold: boolean
  canPromote: boolean
  recommendationStatus: 'proposed_only' | 'promoted' | 'insufficient_history'
  verdictMessage: string
  userConfirmed?: boolean
}

export type ProposeRunPairsOptions = {
  minConfidence?: number
  maxPairs?: number
  requireSameWorkspace?: boolean
  completedPairsCount?: number | Record<string, number>
}

export type PairingRecommendation = {
  taskFamily: string
  completedPairCount: number
  meetsSufficiencyThreshold: boolean
  canPromote: boolean
  recommendationStatus: 'proposed_only' | 'promoted' | 'insufficient_history'
  message: string
  pairs: ProposedRunPair[]
}

function normalizeRepo(repo?: string | null): string | undefined {
  if (!repo) return undefined
  return repo.trim().toLowerCase().replace(/\.git$/, '')
}

function normalizePath(p?: string | null): string | undefined {
  if (!p) return undefined
  return p.trim().toLowerCase().replace(/\\/g, '/').replace(/\/+$/, '')
}

/**
 * Evaluates candidate pairing confidence between two runs based on task family,
 * workspace, harness, and outcome telemetry.
 */
export function pairConfidence(
  runA: CandidateRun,
  runB: CandidateRun,
  options?: { requireSameWorkspace?: boolean }
): PairConfidenceResult {
  // A run cannot pair with itself
  if (runA.runId === runB.runId) {
    return { confidence: 0, heuristics: [], reasons: ['Cannot pair run with itself'] }
  }

  const heuristics: PairingHeuristic[] = []
  const reasons: string[] = []
  let score = 0

  const tfA = runA.taskFamily?.trim()
  const tfB = runB.taskFamily?.trim()
  const cwdA = normalizePath(runA.workingDirectory)
  const cwdB = normalizePath(runB.workingDirectory)
  const repoA = normalizeRepo(runA.repo)
  const repoB = normalizeRepo(runB.repo)

  // 1. Same Task Family (strongest signal)
  if (tfA && tfB && tfA.toLowerCase() === tfB.toLowerCase()) {
    heuristics.push('same_task_family')
    reasons.push(`Identical task family: "${tfA}"`)
    score += 0.4
  }

  // 2. Same Working Directory
  if (cwdA && cwdB && cwdA === cwdB) {
    heuristics.push('same_working_directory')
    reasons.push(`Matching workspace directory: "${cwdA}"`)
    score += 0.3
  }

  // 3. Same Repository
  if (repoA && repoB && repoA === repoB) {
    heuristics.push('same_repo')
    reasons.push(`Matching repository: "${repoA}"`)
    score += 0.25
  }

  // Workspace requirement check
  if (options?.requireSameWorkspace) {
    const hasSharedWorkspace = (cwdA && cwdB && cwdA === cwdB) || (repoA && repoB && repoA === repoB)
    if (!hasSharedWorkspace) {
      return {
        confidence: 0,
        heuristics: [],
        reasons: ['Rejected: requireSameWorkspace is set but runs do not share working directory or repo'],
      }
    }
  }

  // 4. Different Harness or Configuration (ideal for A/B comparison)
  if (runA.harness.toLowerCase() !== runB.harness.toLowerCase()) {
    heuristics.push('different_harness')
    reasons.push(`Cross-harness candidate (${runA.harness} vs ${runB.harness})`)
    score += 0.2
  }

  // 5. Comparable Outcome Telemetry
  const hasOutcomeA = runA.outcome !== undefined && runA.outcome.status !== 'not_measurable'
  const hasOutcomeB = runB.outcome !== undefined && runB.outcome.status !== 'not_measurable'
  if (hasOutcomeA && hasOutcomeB) {
    heuristics.push('outcome_comparable')
    reasons.push(
      `Both runs carry observable outcome status (${runA.outcome?.status} vs ${runB.outcome?.status})`
    )
    score += 0.15
  }

  // 6. Temporal Proximity (within 24 hours)
  if (runA.started && runB.started) {
    const timeA = Date.parse(runA.started)
    const timeB = Date.parse(runB.started)
    if (!isNaN(timeA) && !isNaN(timeB)) {
      const diffHours = Math.abs(timeA - timeB) / (1000 * 60 * 60)
      if (diffHours <= 24) {
        heuristics.push('temporal_proximity')
        reasons.push(`Executed within ${diffHours.toFixed(1)} hours of each other`)
        score += 0.1
      }
    }
  }

  const confidence = Math.min(1.0, Math.round(score * 100) / 100)
  return { confidence, heuristics, reasons }
}

/**
 * Evaluates whether completed pair count satisfies the documented sufficiency threshold (n >= 5).
 */
export function evaluatePairSufficiency(
  taskFamily: string,
  completedPairsCount: number
): {
  meetsThreshold: boolean
  canPromote: boolean
  status: 'promoted' | 'insufficient_history'
  message: string
} {
  const meetsThreshold = completedPairsCount >= MINIMUM_COMPLETED_PAIRS
  if (!meetsThreshold) {
    return {
      meetsThreshold: false,
      canPromote: false,
      status: 'insufficient_history',
      message: `Auto-pairing is proposed only. Observed ${completedPairsCount} completed pair(s) for task family "${taskFamily}". Minimum threshold is n >= ${MINIMUM_COMPLETED_PAIRS} completed pairs before promoting recommendations. User confirmation required.`,
    }
  }

  return {
    meetsThreshold: true,
    canPromote: true,
    status: 'promoted',
    message: `Sufficiency threshold satisfied (n = ${completedPairsCount} >= ${MINIMUM_COMPLETED_PAIRS} completed pairs). Recommendations promoted for task family "${taskFamily}". User confirmation required to finalize comparison.`,
  }
}

/**
 * Propose candidate run pairs aligned by task family and workspace heuristics (Task G4).
 * Enforces the candidate pairing rule: auto-pairing is proposed only, requiring n >= 5 completed
 * pairs before promoting recommendations (Acceptance Criterion 3).
 */
export function proposeRunPairs(
  runs: readonly CandidateRun[],
  options?: ProposeRunPairsOptions
): ProposedRunPair[] {
  const minConfidence = options?.minConfidence ?? 0.4
  const maxPairs = options?.maxPairs ?? 50
  const pairs: ProposedRunPair[] = []

  // Count completed runs per task family to estimate completed pairs if not explicitly passed
  const completedPerFamily = new Map<string, number>()
  for (const run of runs) {
    const fam = run.taskFamily ?? run.workingDirectory ?? 'default'
    if (run.outcome?.status === 'success' || run.outcome?.status === 'failure') {
      completedPerFamily.set(fam, (completedPerFamily.get(fam) ?? 0) + 1)
    }
  }

  for (let i = 0; i < runs.length; i++) {
    for (let j = i + 1; j < runs.length; j++) {
      const runA = runs[i]!
      const runB = runs[j]!

      const { confidence, heuristics, reasons } = pairConfidence(runA, runB, {
        requireSameWorkspace: options?.requireSameWorkspace,
      })

      if (confidence < minConfidence) {
        continue
      }

      const taskFamily =
        runA.taskFamily ??
        runB.taskFamily ??
        runA.workingDirectory ??
        runB.workingDirectory ??
        'default'

      let completedCount = 0
      if (typeof options?.completedPairsCount === 'number') {
        completedCount = options.completedPairsCount
      } else if (
        options?.completedPairsCount &&
        typeof options.completedPairsCount === 'object' &&
        taskFamily in options.completedPairsCount
      ) {
        completedCount = options.completedPairsCount[taskFamily] ?? 0
      } else {
        // Estimate from completed runs in this family: pairs = count / 2
        const runsInFamily = completedPerFamily.get(taskFamily) ?? 0
        completedCount = Math.floor(runsInFamily / 2)
      }

      const sufficiency = evaluatePairSufficiency(taskFamily, completedCount)

      const pairId = `pair:${runA.runId}:${runB.runId}`
      pairs.push({
        pairId,
        runA,
        runB,
        taskFamily,
        confidence,
        heuristics,
        reasons,
        status: 'proposed',
        completedPairCount: completedCount,
        meetsSufficiencyThreshold: sufficiency.meetsThreshold,
        canPromote: sufficiency.canPromote,
        recommendationStatus: sufficiency.status,
        verdictMessage: sufficiency.message,
      })

      if (pairs.length >= maxPairs) {
        break
      }
    }
    if (pairs.length >= maxPairs) {
      break
    }
  }

  // Sort candidate pairs by confidence descending
  return pairs.sort((a, b) => b.confidence - a.confidence)
}
