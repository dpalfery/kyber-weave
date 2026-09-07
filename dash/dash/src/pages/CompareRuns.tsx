import { Card } from '../components/ui/card'
import {
  TurnAlignedDiff,
  type PhaseAlignedTurnPair,
  type RunTurnSummary,
  type TaskPhase,
} from '../components/kyber/TurnAlignedDiff'

export type OutcomeSummary = {
  status: 'success' | 'failure' | 'abandoned' | 'inconclusive' | 'not_measurable'
  exitCode?: number
  failingTests?: number
  passingTests?: number
  errorCount?: number
  reason?: string
}

export type RunCandidate = {
  runId: string
  harness: string
  label?: string
  taskFamily?: string
  workingDirectory?: string | null
  repo?: string | null
  started?: string
  ended?: string
  outcome?: OutcomeSummary
  turns: RunTurnSummary[]
}

export type ProposedPairCandidate = {
  pairId: string
  runAId: string
  runBId: string
  taskFamily: string
  confidence: number
  heuristics: string[]
  reasons: string[]
  completedPairCount: number
  meetsSufficiencyThreshold: boolean
  canPromote: boolean
  recommendationStatus: 'proposed_only' | 'promoted' | 'insufficient_history'
  verdictMessage: string
}

export type CompareRunsProps = {
  runs?: RunCandidate[]
  proposedPairs?: ProposedPairCandidate[]
  confirmedPairs?: Set<string>
  initialRunAId?: string
  initialRunBId?: string
  selectedAId?: string
  selectedBId?: string
  taskFamily?: string
  onConfirmPair?: (pairId: string) => void
  onSelectRunA?: (id: string) => void
  onSelectRunB?: (id: string) => void
}

const SAMPLE_RUNS: RunCandidate[] = [
  {
    runId: 'run-claude-baseline',
    harness: 'claude-code',
    label: 'Claude Code (Baseline)',
    taskFamily: 'auth-jwt-refresh',
    workingDirectory: '/workspace/kyber-weave',
    repo: 'kyber-weave',
    started: '2026-09-06T14:00:00Z',
    outcome: {
      status: 'success',
      exitCode: 0,
      passingTests: 18,
      failingTests: 0,
      errorCount: 0,
      reason: 'All tests passed cleanly',
    },
    turns: [
      {
        turnIndex: 0,
        phase: 'exploration',
        tools: ['find_by_name', 'read_file'],
        tokens: { freshInput: 1200, cacheRead: 300, output: 250, all: 1750 },
        cost: { basis: 'published', status: 'priced', value: 0.025, currency: 'USD' },
      },
      {
        turnIndex: 1,
        phase: 'exploration',
        tools: ['grep_search'],
        tokens: { freshInput: 850, cacheRead: 900, output: 180, all: 1930 },
        cost: { basis: 'published', status: 'priced', value: 0.015, currency: 'USD' },
      },
      {
        turnIndex: 2,
        phase: 'implementation',
        tools: ['replace_file_content'],
        tokens: { freshInput: 1500, cacheRead: 1200, output: 600, all: 3300 },
        cost: { basis: 'published', status: 'priced', value: 0.045, currency: 'USD' },
      },
      {
        turnIndex: 3,
        phase: 'verification',
        tools: ['run_tests'],
        commands: ['npm test'],
        tokens: { freshInput: 900, cacheRead: 1800, output: 120, all: 2820 },
        cost: { basis: 'published', status: 'priced', value: 0.018, currency: 'USD' },
      },
      {
        turnIndex: 4,
        phase: 'resolution',
        tools: ['git_commit'],
        commands: ['git commit -m "fix(auth): handle expired token refresh"'],
        tokens: { freshInput: 400, cacheRead: 2100, output: 90, all: 2590 },
        cost: { basis: 'published', status: 'priced', value: 0.012, currency: 'USD' },
      },
    ],
  },
  {
    runId: 'run-copilot-candidate',
    harness: 'copilot',
    label: 'Copilot (Candidate)',
    taskFamily: 'auth-jwt-refresh',
    workingDirectory: '/workspace/kyber-weave',
    repo: 'kyber-weave',
    started: '2026-09-06T14:30:00Z',
    outcome: {
      status: 'success',
      exitCode: 0,
      passingTests: 18,
      failingTests: 0,
      errorCount: 0,
      reason: 'All tests passed cleanly',
    },
    turns: [
      {
        turnIndex: 0,
        phase: 'exploration',
        tools: ['grep_search'],
        tokens: { freshInput: 950, cacheRead: 200, output: 210, all: 1360 },
        cost: { basis: 'published', status: 'priced', value: 0.018, currency: 'USD' },
      },
      {
        turnIndex: 1,
        phase: 'implementation',
        tools: ['edit_file'],
        tokens: { freshInput: 1100, cacheRead: 800, output: 450, all: 2350 },
        cost: { basis: 'published', status: 'priced', value: 0.032, currency: 'USD' },
      },
      {
        turnIndex: 2,
        phase: 'implementation',
        tools: ['edit_file'],
        tokens: { freshInput: 600, cacheRead: 1400, output: 300, all: 2300 },
        cost: { basis: 'published', status: 'priced', value: 0.024, currency: 'USD' },
      },
      {
        turnIndex: 3,
        phase: 'verification',
        tools: ['test'],
        commands: ['npm test'],
        tokens: { freshInput: 750, cacheRead: 1700, output: 110, all: 2560 },
        cost: { basis: 'published', status: 'priced', value: 0.016, currency: 'USD' },
      },
      {
        turnIndex: 4,
        phase: 'resolution',
        tools: ['task_complete'],
        tokens: { freshInput: 300, cacheRead: 1900, output: 80, all: 2280 },
        cost: { basis: 'published', status: 'priced', value: 0.01, currency: 'USD' },
      },
    ],
  },
]

const SAMPLE_PROPOSED_PAIRS: ProposedPairCandidate[] = [
  {
    pairId: 'pair:run-claude-baseline:run-copilot-candidate',
    runAId: 'run-claude-baseline',
    runBId: 'run-copilot-candidate',
    taskFamily: 'auth-jwt-refresh',
    confidence: 0.85,
    heuristics: [
      'same_task_family',
      'same_working_directory',
      'different_harness',
      'outcome_comparable',
      'temporal_proximity',
    ],
    reasons: [
      'Identical task family: "auth-jwt-refresh"',
      'Matching workspace: "/workspace/kyber-weave"',
      'Cross-harness comparison (claude-code vs copilot)',
      'Both runs achieved clean exit status: success',
    ],
    completedPairCount: 6,
    meetsSufficiencyThreshold: true,
    canPromote: true,
    recommendationStatus: 'promoted',
    verdictMessage:
      'Sufficiency threshold satisfied (n = 6 >= 5 completed pairs). Recommendations promoted for task family "auth-jwt-refresh". User confirmation required.',
  },
]

function alignTurnsByPhase(turnsA: RunTurnSummary[], turnsB: RunTurnSummary[]): PhaseAlignedTurnPair[] {
  const phases: TaskPhase[] = ['exploration', 'implementation', 'verification', 'resolution']
  const pairs: PhaseAlignedTurnPair[] = []

  for (const phase of phases) {
    const phaseTurnsA = turnsA.filter((t) => t.phase === phase)
    const phaseTurnsB = turnsB.filter((t) => t.phase === phase)
    const maxCount = Math.max(phaseTurnsA.length, phaseTurnsB.length)

    for (let i = 0; i < maxCount; i++) {
      const turnA = phaseTurnsA[i] ?? null
      const turnB = phaseTurnsB[i] ?? null

      const tokensA =
        turnA?.tokens?.all ??
        (turnA?.tokens?.reportedInput ?? 0) + (turnA?.tokens?.output ?? 0)
      const tokensB =
        turnB?.tokens?.all ??
        (turnB?.tokens?.reportedInput ?? 0) + (turnB?.tokens?.output ?? 0)
      const delta = turnA && turnB ? tokensB - tokensA : undefined

      const reading =
        turnA && turnB
          ? `Phase ${phase}: Run A spent ${tokensA.toLocaleString()} tokens; Run B spent ${tokensB.toLocaleString()} tokens (delta: ${delta && delta > 0 ? '+' : ''}${delta?.toLocaleString()}).`
          : turnA
            ? `Phase ${phase}: Run A executed ${tokensA.toLocaleString()} tokens; Run B completed phase in fewer turns.`
            : `Phase ${phase}: Run B executed ${tokensB.toLocaleString()} tokens; Run A completed phase in fewer turns.`

      pairs.push({
        phase,
        phaseIndex: i,
        runATurn: turnA,
        runBTurn: turnB,
        signals: [
          {
            name: 'total_tokens',
            label: 'Tokens',
            unit: 'tokens',
            runAValue: turnA ? tokensA : undefined,
            runBValue: turnB ? tokensB : undefined,
            delta,
            status: turnA && turnB ? 'compared' : turnA ? 'missing_in_b' : 'missing_in_a',
          },
        ],
        reading,
      })
    }
  }

  return pairs
}

export function CompareRuns({
  runs = SAMPLE_RUNS,
  proposedPairs = SAMPLE_PROPOSED_PAIRS,
  confirmedPairs = new Set<string>(),
  initialRunAId,
  initialRunBId,
  selectedAId: propA,
  selectedBId: propB,
  taskFamily = 'auth-jwt-refresh',
  onConfirmPair,
  onSelectRunA,
  onSelectRunB,
}: CompareRunsProps) {
  const selectedAId = propA ?? initialRunAId ?? runs[0]?.runId ?? ''
  const selectedBId = propB ?? initialRunBId ?? runs[1]?.runId ?? ''
  const showProposedDrawer = true

  const runA = runs.find((r) => r.runId === selectedAId)
  const runB = runs.find((r) => r.runId === selectedBId)
  const pairs = runA && runB ? alignTurnsByPhase(runA.turns, runB.turns) : []

  // Total metrics
  const totalTokensA = (runA?.turns ?? []).reduce(
    (sum, t) =>
      sum + (t.tokens?.all ?? (t.tokens?.reportedInput ?? 0) + (t.tokens?.output ?? 0)),
    0
  )
  const totalTokensB = (runB?.turns ?? []).reduce(
    (sum, t) =>
      sum + (t.tokens?.all ?? (t.tokens?.reportedInput ?? 0) + (t.tokens?.output ?? 0)),
    0
  )
  const tokenDelta = totalTokensB - totalTokensA

  // Sufficiency and outcome guard evaluation (Criterion 3)
  const activeProposedPair = proposedPairs.find(
    (p) =>
      (p.runAId === selectedAId && p.runBId === selectedBId) ||
      (p.runAId === selectedBId && p.runBId === selectedAId)
  )

  const completedPairCount = activeProposedPair?.completedPairCount ?? 6
  const meetsSufficiency = completedPairCount >= 5
  const isOutcomeRegression =
    runA?.outcome?.status === 'success' &&
    (runB?.outcome?.status === 'failure' || runB?.outcome?.status === 'abandoned')
  const canPromote = meetsSufficiency && !isOutcomeRegression

  const handleConfirmPair = (pairId: string, pAId: string, pBId: string) => {
    onSelectRunA?.(pAId)
    onSelectRunB?.(pBId)
    onConfirmPair?.(pairId)
  }

  return (
    <div className="flex flex-col gap-6 p-6">
      {/* Workspace Header */}
      <div className="flex flex-col gap-1 border-b border-border pb-4 md:flex-row md:items-center md:justify-between">
        <div>
          <h1 className="text-xl font-bold tracking-tight text-heading">
            Run Comparison Workspace
          </h1>
          <p className="text-xs text-tertiary-foreground">
            Semantic comparison aligned on task phases with outcome guards and sufficiency validation (Decision D11)
          </p>
        </div>
        <div className="flex items-center gap-2">
          <span className="rounded bg-muted px-2 py-1 font-mono text-xs text-foreground">
            Task Family: {taskFamily}
          </span>
          <button
            type="button"
            className="rounded-md border border-border bg-card px-2.5 py-1 text-xs font-medium text-foreground hover:bg-interactive-secondary"
          >
            {showProposedDrawer ? 'Hide Proposed Pairs' : 'Show Proposed Pairs'} (
            {proposedPairs.length})
          </button>
        </div>
      </div>

      {/* Auto-Pairing Proposals (Proposed Only / User Confirmed Rule - Criterion 3) */}
      {showProposedDrawer && proposedPairs.length > 0 && (
        <Card className="flex flex-col gap-3 border-sky-500/30 bg-sky-500/5 p-4 dark:border-sky-500/20 dark:bg-sky-500/10">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <span className="rounded bg-sky-500/20 px-2 py-0.5 text-[11px] font-semibold uppercase text-sky-800 dark:text-sky-200">
                Candidate Run Pairs (Proposed Only)
              </span>
              <span className="text-xs text-tertiary-foreground">
                Heuristic pairing recommendations require explicit user confirmation
              </span>
            </div>
            <span className="text-[11px] font-medium text-tertiary-foreground">
              Sufficiency: n = {completedPairCount} / 5 min
            </span>
          </div>

          <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
            {proposedPairs.map((pair) => {
              const isConfirmed = confirmedPairs.has(pair.pairId)
              const isActive =
                (selectedAId === pair.runAId && selectedBId === pair.runBId) ||
                (selectedAId === pair.runBId && selectedBId === pair.runAId)

              return (
                <div
                  key={pair.pairId}
                  className={`flex flex-col justify-between rounded-md border bg-card p-3 shadow-2xs ${
                    isActive ? 'ring-2 ring-sky-500' : 'border-border'
                  }`}
                >
                  <div className="flex flex-col gap-1.5">
                    <div className="flex items-center justify-between">
                      <span className="font-mono text-xs font-semibold text-foreground">
                        {pair.runAId} ↔ {pair.runBId}
                      </span>
                      <span className="rounded bg-sky-500/15 px-1.5 py-0.5 font-mono text-[10.5px] font-medium text-sky-700 dark:text-sky-300">
                        {Math.round(pair.confidence * 100)}% match
                      </span>
                    </div>

                    <ul className="list-inside list-disc text-[11px] text-muted-foreground">
                      {pair.reasons.map((r, i) => (
                        <li key={i}>{r}</li>
                      ))}
                    </ul>

                    <div className="mt-1 flex items-center gap-2">
                      <span
                        className={`rounded px-1.5 py-0.5 text-[10px] font-medium uppercase ${
                          pair.canPromote
                            ? 'bg-emerald-500/15 text-emerald-700 dark:text-emerald-300'
                            : 'bg-amber-500/15 text-amber-700 dark:text-amber-300'
                        }`}
                      >
                        {pair.canPromote ? 'Promoted (n ≥ 5)' : 'Proposed Only (n < 5)'}
                      </span>
                      <span className="text-[10.5px] text-tertiary-foreground">
                        Completed pairs: {pair.completedPairCount}
                      </span>
                    </div>
                  </div>

                  <div className="mt-3 flex items-center justify-end gap-2 border-t border-border/60 pt-2">
                    <button
                      type="button"
                      onClick={() => handleConfirmPair(pair.pairId, pair.runAId, pair.runBId)}
                      className={`rounded px-2.5 py-1 text-xs font-medium transition-colors ${
                        isConfirmed
                          ? 'bg-muted text-muted-foreground'
                          : 'bg-primary text-primary-foreground hover:opacity-90'
                      }`}
                    >
                      {isConfirmed ? 'Confirmed' : 'Confirm & Load Pair'}
                    </button>
                  </div>
                </div>
              )
            })}
          </div>
        </Card>
      )}

      {/* Comparison Selectors */}
      <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
        <Card className="flex flex-col gap-2 p-4">
          <label className="text-[11px] font-semibold uppercase tracking-wider text-heading">
            Run A (Baseline)
          </label>
          <select
            value={selectedAId}
            onChange={(e) => onSelectRunA?.(e.target.value)}
            className="rounded-md border border-border bg-background px-3 py-1.5 text-sm text-foreground focus:outline-none focus:ring-1 focus:ring-primary"
          >
            {runs.map((r) => (
              <option key={r.runId} value={r.runId}>
                {r.label ?? r.runId} ({r.harness})
              </option>
            ))}
          </select>
          {runA && (
            <div className="mt-2 flex flex-wrap items-center gap-3 text-xs text-tertiary-foreground">
              <span>Harness: <strong className="text-foreground">{runA.harness}</strong></span>
              <span>Turns: <strong className="text-foreground">{runA.turns.length}</strong></span>
              <span>
                Outcome:{' '}
                <strong
                  className={
                    runA.outcome?.status === 'success'
                      ? 'text-emerald-600 dark:text-emerald-400'
                      : 'text-rose-600 dark:text-rose-400'
                  }
                >
                  {runA.outcome?.status ?? 'unobserved'}
                </strong>
              </span>
            </div>
          )}
        </Card>

        <Card className="flex flex-col gap-2 p-4">
          <label className="text-[11px] font-semibold uppercase tracking-wider text-heading">
            Run B (Candidate)
          </label>
          <select
            value={selectedBId}
            onChange={(e) => onSelectRunB?.(e.target.value)}
            className="rounded-md border border-border bg-background px-3 py-1.5 text-sm text-foreground focus:outline-none focus:ring-1 focus:ring-primary"
          >
            {runs.map((r) => (
              <option key={r.runId} value={r.runId}>
                {r.label ?? r.runId} ({r.harness})
              </option>
            ))}
          </select>
          {runB && (
            <div className="mt-2 flex flex-wrap items-center gap-3 text-xs text-tertiary-foreground">
              <span>Harness: <strong className="text-foreground">{runB.harness}</strong></span>
              <span>Turns: <strong className="text-foreground">{runB.turns.length}</strong></span>
              <span>
                Outcome:{' '}
                <strong
                  className={
                    runB.outcome?.status === 'success'
                      ? 'text-emerald-600 dark:text-emerald-400'
                      : 'text-rose-600 dark:text-rose-400'
                  }
                >
                  {runB.outcome?.status ?? 'unobserved'}
                </strong>
              </span>
            </div>
          )}
        </Card>
      </div>

      {/* Sufficiency Verdict Banner (Acceptance Criterion 3) */}
      <Card
        className={`flex flex-col gap-2 p-4 ${
          isOutcomeRegression
            ? 'border-rose-500/40 bg-rose-500/5 dark:border-rose-500/30 dark:bg-rose-500/10'
            : canPromote
              ? 'border-emerald-500/40 bg-emerald-500/5 dark:border-emerald-500/30 dark:bg-emerald-500/10'
              : 'border-amber-500/40 bg-amber-500/5 dark:border-amber-500/30 dark:bg-amber-500/10'
        }`}
      >
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2">
            <span
              className={`rounded px-2 py-0.5 text-xs font-semibold uppercase ${
                isOutcomeRegression
                  ? 'bg-rose-500/20 text-rose-800 dark:text-rose-200'
                  : canPromote
                    ? 'bg-emerald-500/20 text-emerald-800 dark:text-emerald-200'
                    : 'bg-amber-500/20 text-amber-800 dark:text-amber-200'
              }`}
            >
              {isOutcomeRegression
                ? 'Outcome Regression Guard Refusal'
                : canPromote
                  ? 'Sufficiency Threshold Met (n ≥ 5)'
                  : 'Candidate Only (n < 5 Minimum Required)'}
            </span>
            <span className="text-xs font-medium text-foreground">
              Completed Pairs: {completedPairCount} / 5 minimum
            </span>
          </div>

          <span className="text-xs tabular-nums text-tertiary-foreground">
            Token Delta: {tokenDelta > 0 ? `+${tokenDelta.toLocaleString()}` : tokenDelta.toLocaleString()}
          </span>
        </div>

        <p className="text-xs text-muted-foreground">
          {isOutcomeRegression
            ? 'Outcome regression detected between Run A and Run B. Modifications cannot be promoted to recommendations when task correctness degrades.'
            : canPromote
              ? 'The comparison between baseline and candidate satisfies the documented sufficiency threshold (n ≥ 5 completed pairs) with zero outcome regressions.'
              : `Recommendation promotion refused: observed ${completedPairCount} completed pairs for task family "${taskFamily}". Minimum threshold is n ≥ 5 completed pairs. Auto-pairing is proposed only.`}
        </p>
      </Card>

      {/* Phase-Aligned Turn Diff */}
      <div className="flex flex-col gap-3">
        <h2 className="text-sm font-semibold uppercase tracking-wider text-heading">
          Phase-Aligned Turn Diff
        </h2>
        <TurnAlignedDiff
          pairs={pairs}
          runALabel={runA?.label ?? 'Run A'}
          runBLabel={runB?.label ?? 'Run B'}
        />
      </div>
    </div>
  )
}
