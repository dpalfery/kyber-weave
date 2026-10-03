import { useEffect, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Card } from '../components/ui/card.js'
import { Skeleton } from '../components/ui/skeleton.js'
import {
  TurnAlignedDiff,
  type PhaseAlignedTurnPair,
  type RunTurnSummary,
  type SignalComparison,
  type SignalComparisonStatus,
  type TaskPhase,
} from '../components/analysis/TurnAlignedDiff.js'
import {
  fetchRunComparison,
  fetchRuns,
  type KyberComparisonRunSide,
  type KyberComparisonTotals,
  type KyberComparisonVerdict,
  type KyberPhaseAlignedTurnPair,
  type KyberRunComparison,
  type KyberRunSummary,
} from '../lib/kyberApi.js'

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
  turnCount?: number
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
  comparison?: KyberRunComparison | null
  onConfirmPair?: (pairId: string) => void
  onSelectRunA?: (id: string) => void
  onSelectRunB?: (id: string) => void
}

const PHASES: TaskPhase[] = ['exploration', 'implementation', 'verification', 'resolution']
const SIGNAL_STATUSES: ReadonlySet<string> = new Set([
  'compared',
  'not_comparable',
  'missing_in_a',
  'missing_in_b',
])
const OUTCOME_STATUSES: ReadonlySet<string> = new Set([
  'success',
  'failure',
  'abandoned',
  'inconclusive',
  'not_measurable',
])

function isPhase(value: unknown): value is TaskPhase {
  return value === 'exploration' || value === 'implementation' || value === 'verification' || value === 'resolution'
}

function asNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

function asString(value: unknown): string | undefined {
  return typeof value === 'string' ? value : undefined
}

function toOutcome(raw: unknown): OutcomeSummary | undefined {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return undefined
  const record = raw as Record<string, unknown>
  const status = asString(record.status)
  const mapped = status && OUTCOME_STATUSES.has(status)
    ? (status as OutcomeSummary['status'])
    : record.abandoned === true
      ? 'abandoned'
      : undefined
  if (!mapped) return undefined
  const testDelta = record.testDelta
  const failingFromDelta =
    testDelta && typeof testDelta === 'object' && !Array.isArray(testDelta)
      ? asNumber((testDelta as Record<string, unknown>).failed)
      : undefined
  return {
    status: mapped,
    exitCode: asNumber(record.exitCode),
    failingTests: asNumber(record.failingTests) ?? failingFromDelta,
    passingTests: asNumber(record.passingTests),
    errorCount: asNumber(record.errorCount),
    reason: asString(record.reason),
  }
}

function toCandidate(row: KyberRunSummary): RunCandidate {
  return {
    runId: row.runId,
    harness: row.harness,
    label: row.label ?? undefined,
    workingDirectory: row.workingDirectory,
    started: row.started ?? undefined,
    ended: row.ended ?? undefined,
    turnCount: row.turnCount,
    outcome: toOutcome(row.outcome),
    turns: [],
  }
}

function isoDateLabel(started?: string): string {
  if (!started) return 'date unknown'
  const prefix = started.slice(0, 10)
  return /^\d{4}-\d{2}-\d{2}$/.test(prefix) ? prefix : 'date unknown'
}

function turnCountLabel(turnCount?: number): string {
  return turnCount != null && Number.isFinite(turnCount) ? `${turnCount} turns` : 'turns unknown'
}

function shortRunId(runId: string): string {
  return runId.length <= 12 ? runId : `…${runId.slice(-8)}`
}

function formatRunOption(run: RunCandidate): string {
  const name =
    run.label && run.label !== run.runId
      ? `${run.label} (${shortRunId(run.runId)})`
      : (run.label ?? run.runId)
  return `${isoDateLabel(run.started)} · ${run.harness} · ${turnCountLabel(run.turnCount)} · ${name}`
}

function observedHarnesses(runs: RunCandidate[]): string[] {
  const seen = new Set<string>()
  const harnesses: string[] = []
  for (const run of runs) {
    if (!seen.has(run.harness)) {
      seen.add(run.harness)
      harnesses.push(run.harness)
    }
  }
  return harnesses
}

function filterRunsByHarness(runs: RunCandidate[], harness: string): RunCandidate[] {
  if (harness === '') return runs
  return runs.filter((run) => run.harness === harness)
}

function runMetricsUnavailable(
  side?: KyberComparisonRunSide,
): { unavailable: true; reason: string } | { unavailable: false } {
  if (!side) return { unavailable: false }
  const reason = side.metricsReason ?? (side.availability === 'unavailable' ? side.reason : undefined)
  return reason ? { unavailable: true, reason } : { unavailable: false }
}

function tokenDeltaUnavailable(
  totals?: KyberComparisonTotals,
): { unavailable: true; reason: string } | { unavailable: false } {
  if (!totals) return { unavailable: false }
  const reason =
    totals.tokenDeltaReason ??
    (totals.availability === 'unavailable' ? totals.reason : undefined)
  return reason ? { unavailable: true, reason } : { unavailable: false }
}

function historyUnavailable(
  verdict?: KyberComparisonVerdict,
): { unavailable: true; reason: string } | { unavailable: false } {
  if (!verdict) return { unavailable: false }
  const reason =
    verdict.historyReason ??
    (verdict.historyAvailability === 'unavailable' || verdict.historyAvailability === 'not_measurable'
      ? (verdict.refusalReason ?? 'recommendation history is not measured')
      : undefined)
  return reason ? { unavailable: true, reason } : { unavailable: false }
}

function toTurn(raw: Record<string, unknown> | null): RunTurnSummary | null {
  if (!raw) return null
  const tokensRaw = raw.tokens
  const tokens =
    tokensRaw && typeof tokensRaw === 'object' && !Array.isArray(tokensRaw)
      ? (tokensRaw as Record<string, unknown>)
      : undefined
  const costRaw = raw.cost
  const cost =
    costRaw && typeof costRaw === 'object' && !Array.isArray(costRaw)
      ? (costRaw as Record<string, unknown>)
      : undefined
  const tools = Array.isArray(raw.tools) ? raw.tools.filter((t): t is string => typeof t === 'string') : undefined
  const commands = Array.isArray(raw.commands)
    ? raw.commands.filter((t): t is string => typeof t === 'string')
    : undefined
  return {
    turnIndex: asNumber(raw.turnIndex) ?? 0,
    spanId: asString(raw.spanId),
    phase: isPhase(raw.phase) ? raw.phase : undefined,
    tokens: tokens
      ? {
          freshInput: asNumber(tokens.freshInput),
          cacheRead: asNumber(tokens.cacheRead),
          cacheCreation: asNumber(tokens.cacheCreation),
          output: asNumber(tokens.output),
          reportedInput: asNumber(tokens.reportedInput),
          reportedOutput: asNumber(tokens.reportedOutput),
          all: asNumber(tokens.all),
        }
      : undefined,
    cost: (() => {
      if (!cost) return undefined
      const basis = asString(cost.basis)
      const status = asString(cost.status)
      if (!basis || !status) return undefined
      return {
        basis,
        status,
        value: asNumber(cost.value),
        currency: asString(cost.currency),
      }
    })(),
    tools,
    commands,
    status: asString(raw.status),
    summary: asString(raw.summary),
  }
}

function toSignal(raw: Record<string, unknown>): SignalComparison {
  const status = asString(raw.status)
  return {
    name: asString(raw.name) ?? 'signal',
    label: asString(raw.label) ?? 'Signal',
    unit: asString(raw.unit),
    runAValue: typeof raw.runAValue === 'number' || typeof raw.runAValue === 'string' ? raw.runAValue : undefined,
    runBValue: typeof raw.runBValue === 'number' || typeof raw.runBValue === 'string' ? raw.runBValue : undefined,
    delta: asNumber(raw.delta),
    status: status && SIGNAL_STATUSES.has(status) ? (status as SignalComparisonStatus) : 'not_comparable',
    reason: asString(raw.reason),
  }
}

function toAlignedPair(pair: KyberPhaseAlignedTurnPair): PhaseAlignedTurnPair {
  return {
    phase: pair.phase,
    phaseIndex: pair.phaseIndex,
    runATurn: toTurn(pair.runATurn),
    runBTurn: toTurn(pair.runBTurn),
    signals: pair.signals.map(toSignal),
    reading: pair.reading,
  }
}

function tryTurnTokenTotal(turn: RunTurnSummary): number | undefined {
  if (typeof turn.tokens?.all === 'number') return turn.tokens.all
  const reportedInput = turn.tokens?.reportedInput
  const output = turn.tokens?.output
  if (typeof reportedInput === 'number' && typeof output === 'number') {
    return reportedInput + output
  }
  const fresh = turn.tokens?.freshInput
  const cache = turn.tokens?.cacheRead
  const cacheCreation = turn.tokens?.cacheCreation
  if (
    typeof fresh === 'number' &&
    typeof cache === 'number' &&
    typeof output === 'number'
  ) {
    return fresh + cache + (cacheCreation ?? 0) + output
  }
  return undefined
}

function sumTurnTokens(turns: RunTurnSummary[]): number | undefined {
  if (turns.length === 0) return undefined
  let sum = 0
  for (const turn of turns) {
    const total = tryTurnTokenTotal(turn)
    if (total === undefined) return undefined
    sum += total
  }
  return sum
}

function alignTurnsByPhase(turnsA: RunTurnSummary[], turnsB: RunTurnSummary[]): PhaseAlignedTurnPair[] {
  const pairs: PhaseAlignedTurnPair[] = []

  for (const phase of PHASES) {
    const phaseTurnsA = turnsA.filter((t) => t.phase === phase)
    const phaseTurnsB = turnsB.filter((t) => t.phase === phase)
    const maxCount = Math.max(phaseTurnsA.length, phaseTurnsB.length)

    for (let i = 0; i < maxCount; i++) {
      const turnA = phaseTurnsA[i] ?? null
      const turnB = phaseTurnsB[i] ?? null

      const tokensA = turnA ? tryTurnTokenTotal(turnA) : undefined
      const tokensB = turnB ? tryTurnTokenTotal(turnB) : undefined
      const delta =
        tokensA !== undefined && tokensB !== undefined ? tokensB - tokensA : undefined

      const reading =
        turnA && turnB
          ? tokensA !== undefined && tokensB !== undefined
            ? `Phase ${phase}: Run A spent ${tokensA.toLocaleString()} tokens; Run B spent ${tokensB.toLocaleString()} tokens (delta: ${delta && delta > 0 ? '+' : ''}${delta?.toLocaleString()}).`
            : `Phase ${phase}: Token telemetry unavailable for one or both runs in this phase slot.`
          : turnA
            ? tokensA !== undefined
              ? `Phase ${phase}: Run A executed ${tokensA.toLocaleString()} tokens; Run B completed phase in fewer turns.`
              : `Phase ${phase}: Run A token telemetry unavailable; Run B completed phase in fewer turns.`
            : tokensB !== undefined
              ? `Phase ${phase}: Run B executed ${tokensB.toLocaleString()} tokens; Run A completed phase in fewer turns.`
              : `Phase ${phase}: Run B token telemetry unavailable; Run A completed phase in fewer turns.`

      const tokenSignal: SignalComparison =
        turnA && turnB
          ? tokensA !== undefined && tokensB !== undefined
            ? {
                name: 'total_tokens',
                label: 'Tokens',
                unit: 'tokens',
                runAValue: tokensA,
                runBValue: tokensB,
                delta,
                status: 'compared',
              }
            : {
                name: 'total_tokens',
                label: 'Tokens',
                unit: 'tokens',
                status: 'not_comparable',
                reason: 'Token telemetry unavailable in one or both paired turns',
              }
          : {
              name: 'total_tokens',
              label: 'Tokens',
              unit: 'tokens',
              runAValue: turnA && tokensA !== undefined ? tokensA : undefined,
              runBValue: turnB && tokensB !== undefined ? tokensB : undefined,
              status: turnA ? 'missing_in_b' : 'missing_in_a',
            }

      pairs.push({
        phase,
        phaseIndex: i,
        runATurn: turnA,
        runBTurn: turnB,
        signals: [tokenSignal],
        reading,
      })
    }
  }

  return pairs
}

export function CompareRuns({
  runs: injectedRuns,
  proposedPairs = [],
  confirmedPairs = new Set<string>(),
  initialRunAId,
  initialRunBId,
  selectedAId: propA,
  selectedBId: propB,
  taskFamily: taskFamilyProp,
  comparison: injectedComparison,
  onConfirmPair,
  onSelectRunA,
  onSelectRunB,
}: CompareRunsProps) {
  const live = injectedRuns === undefined
  const [localA, setLocalA] = useState<string | undefined>(undefined)
  const [localB, setLocalB] = useState<string | undefined>(undefined)
  const [harnessFilterA, setHarnessFilterA] = useState('')
  const [harnessFilterB, setHarnessFilterB] = useState('')
  const [showProposedDrawer, setShowProposedDrawer] = useState(true)

  const {
    data: fetchedRuns,
    isLoading: loadingRuns,
    isError: runsError,
    error: runsErr,
  } = useQuery({
    queryKey: ['kyber-runs'],
    queryFn: () => fetchRuns(),
    enabled: live,
  })

  const runs = useMemo<RunCandidate[]>(
    () => injectedRuns ?? (fetchedRuns ?? []).map(toCandidate),
    [injectedRuns, fetchedRuns],
  )

  const rawSelectedAId = propA ?? localA ?? initialRunAId ?? ''
  const rawSelectedBId = propB ?? localB ?? initialRunBId ?? ''

  const harnesses = useMemo(() => observedHarnesses(runs), [runs])
  const filteredRunsA = useMemo(
    () => filterRunsByHarness(runs, harnessFilterA),
    [runs, harnessFilterA],
  )
  const filteredRunsB = useMemo(
    () => filterRunsByHarness(runs, harnessFilterB),
    [runs, harnessFilterB],
  )

  // Effective selection is empty when the chosen id is not visible in the filtered
  // inventory — including deep-linked initialRun* ids excluded by a harness filter.
  const selectedAId =
    rawSelectedAId !== '' && filteredRunsA.some((run) => run.runId === rawSelectedAId)
      ? rawSelectedAId
      : ''
  const selectedBId =
    rawSelectedBId !== '' && filteredRunsB.some((run) => run.runId === rawSelectedBId)
      ? rawSelectedBId
      : ''

  // When a harness filter hides the active local selection, clear it so compare stops
  // rather than keeping an orphaned value that is invisible in the dropdown.
  useEffect(() => {
    if (!live || propA !== undefined) return
    if (localA && !filteredRunsA.some((run) => run.runId === localA)) {
      setLocalA(undefined)
      onSelectRunA?.('')
    }
  }, [filteredRunsA, localA, live, onSelectRunA, propA])

  useEffect(() => {
    if (!live || propB !== undefined) return
    if (localB && !filteredRunsB.some((run) => run.runId === localB)) {
      setLocalB(undefined)
      onSelectRunB?.('')
    }
  }, [filteredRunsB, localB, live, onSelectRunB, propB])

  const canFetchComparison = live && selectedAId !== '' && selectedBId !== '' && selectedAId !== selectedBId

  const {
    data: fetchedComparison,
    isLoading: loadingComparison,
    isError: comparisonError,
    error: comparisonErr,
  } = useQuery({
    queryKey: ['kyber-compare-runs', selectedAId, selectedBId],
    queryFn: () => fetchRunComparison(selectedAId, selectedBId),
    enabled: canFetchComparison && injectedComparison === undefined,
    retry: false,
  })

  const comparison = injectedComparison === undefined ? fetchedComparison : injectedComparison
  const runA = runs.find((r) => r.runId === selectedAId)
  const runB = runs.find((r) => r.runId === selectedBId)

  const pairs =
    comparison?.pairs.map(toAlignedPair) ??
    (runA && runB ? alignTurnsByPhase(runA.turns, runB.turns) : [])

  const totalTokensA =
    asNumber(comparison?.runA.totalTokens) ?? sumTurnTokens(runA?.turns ?? [])
  const totalTokensB =
    asNumber(comparison?.runB.totalTokens) ?? sumTurnTokens(runB?.turns ?? [])
  const tokenDeltaState = tokenDeltaUnavailable(comparison?.totals)
  const tokenDelta = tokenDeltaState.unavailable
    ? undefined
    : (asNumber(comparison?.totals.tokenDelta) ??
      (totalTokensA !== undefined && totalTokensB !== undefined
        ? totalTokensB - totalTokensA
        : undefined))
  const runAMetrics = runMetricsUnavailable(comparison?.runA)
  const runBMetrics = runMetricsUnavailable(comparison?.runB)

  const activeProposedPair = proposedPairs.find(
    (p) =>
      (p.runAId === selectedAId && p.runBId === selectedBId) ||
      (p.runAId === selectedBId && p.runBId === selectedAId),
  )

  const verdict = comparison?.verdict
  const historyState = historyUnavailable(verdict)
  const completedPairCount =
    verdict?.completedPairCount ?? activeProposedPair?.completedPairCount
  const isOutcomeRegression =
    verdict?.outcomeRegression ??
    (runA?.outcome?.status === 'success' &&
      (runB?.outcome?.status === 'failure' || runB?.outcome?.status === 'abandoned'))
  const canPromote =
    verdict?.canPromote ??
    (completedPairCount != null && completedPairCount >= 5 && !isOutcomeRegression)
  const taskFamily = comparison?.taskFamily ?? taskFamilyProp ?? runA?.taskFamily ?? runB?.taskFamily
  const outcomeA = toOutcome(comparison?.runA.outcome) ?? runA?.outcome
  const outcomeB = toOutcome(comparison?.runB.outcome) ?? runB?.outcome

  const handleSelectA = (id: string) => {
    setLocalA(id)
    onSelectRunA?.(id)
  }
  const handleSelectB = (id: string) => {
    setLocalB(id)
    onSelectRunB?.(id)
  }
  const handleConfirmPair = (pairId: string, pAId: string, pBId: string) => {
    handleSelectA(pAId)
    handleSelectB(pBId)
    onConfirmPair?.(pairId)
  }

  const guardMessage = historyState.unavailable
    ? historyState.reason
    : isOutcomeRegression
      ? (verdict?.refusalReason ??
        'Outcome regression detected between Run A and Run B. Modifications cannot be promoted to recommendations when task correctness degrades.')
      : canPromote
        ? (verdict?.recommendation ??
          'The comparison between baseline and candidate satisfies the documented sufficiency threshold (n ≥ 5 completed pairs) with zero outcome regressions.')
        : (verdict?.refusalReason ??
          completedPairCount != null
            ? `Recommendation promotion refused: observed ${completedPairCount} completed pair(s)${taskFamily ? ` for task family "${taskFamily}"` : ''}. Minimum threshold is n ≥ 5 completed pairs. Auto-pairing is proposed only.`
            : 'Recommendation promotion refused: completed pair history is unavailable. Auto-pairing is proposed only.')

  if (live && loadingRuns) {
    return (
      <div className="flex flex-col gap-6 p-6" data-testid="page-compare">
        <Skeleton className="h-40" />
      </div>
    )
  }

  return (
    <div className="flex flex-col gap-6 p-6" data-testid="page-compare">
      <div className="flex flex-col gap-1 border-b border-border pb-4 md:flex-row md:items-center md:justify-between">
        <div>
          <h1 className="text-xl font-bold tracking-tight text-heading">
            Run Comparison Workspace
          </h1>
          <p className="text-xs text-tertiary-foreground">
            Semantic comparison aligned on task phases with outcome guards and sufficiency validation.
          </p>
        </div>
        <div className="flex items-center gap-2">
          {taskFamily ? (
            <span className="rounded bg-muted px-2 py-1 font-mono text-xs text-foreground">
              Task Family: {taskFamily}
            </span>
          ) : null}
          {proposedPairs.length > 0 ? (
            <button
              type="button"
              onClick={() => setShowProposedDrawer((open) => !open)}
              className="rounded-md border border-border bg-card px-2.5 py-1 text-xs font-medium text-foreground hover:bg-interactive-secondary"
            >
              {showProposedDrawer ? 'Hide Proposed Pairs' : 'Show Proposed Pairs'} (
              {proposedPairs.length})
            </button>
          ) : null}
        </div>
      </div>

      {runsError ? (
        <Card className="p-4 text-sm text-rose-700 dark:text-rose-300">
          Failed to load runs: {runsErr instanceof Error ? runsErr.message : String(runsErr)}
        </Card>
      ) : null}

      {runs.length === 0 && !runsError ? (
        <Card className="p-8 text-center text-sm text-tertiary-foreground" data-testid="compare-empty">
          No runs in the live store. Ingest telemetry into canon.db before comparing.
        </Card>
      ) : null}

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

      {runs.length > 0 ? (
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <Card className="flex flex-col gap-2 p-4">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <label htmlFor="compare-run-a" className="text-[11px] font-semibold uppercase tracking-wider text-heading">
                Run A (Baseline)
              </label>
              <label className="text-[11px] text-tertiary-foreground">
                Harness:{' '}
                <select
                  data-testid="compare-run-a-harness-filter"
                  value={harnessFilterA}
                  onChange={(e) => setHarnessFilterA(e.target.value)}
                  className="rounded-md border border-border bg-background px-2 py-0.5 text-xs text-foreground focus:outline-none focus:ring-1 focus:ring-primary"
                >
                  <option value="">All harnesses</option>
                  {harnesses.map((harness) => (
                    <option key={harness} value={harness}>
                      {harness}
                    </option>
                  ))}
                </select>
              </label>
            </div>
            <select
              id="compare-run-a"
              data-testid="compare-run-a"
              value={selectedAId}
              onChange={(e) => handleSelectA(e.target.value)}
              className="rounded-md border border-border bg-background px-3 py-1.5 text-sm text-foreground focus:outline-none focus:ring-1 focus:ring-primary"
            >
              <option value="">Select a run</option>
              {filteredRunsA.map((r) => (
                <option key={r.runId} value={r.runId}>
                  {formatRunOption(r)}
                </option>
              ))}
            </select>
            {runA && (
              <div className="mt-2 flex flex-wrap items-center gap-3 text-xs text-tertiary-foreground">
                <span>Harness: <strong className="text-foreground">{runA.harness}</strong></span>
                <span>
                  Turns:{' '}
                  {runAMetrics.unavailable ? (
                    <>
                      <strong className="text-foreground">—</strong>
                      {runAMetrics.reason ? (
                        <span className="text-muted-foreground"> ({runAMetrics.reason})</span>
                      ) : null}
                    </>
                  ) : (
                    <strong className="text-foreground">
                      {comparison?.runA.turnCount ?? runA.turnCount ?? runA.turns.length}
                    </strong>
                  )}
                </span>
                <span>
                  Outcome:{' '}
                  <strong
                    className={
                      outcomeA?.status === 'success'
                        ? 'text-emerald-600 dark:text-emerald-400'
                        : 'text-rose-600 dark:text-rose-400'
                    }
                  >
                    {outcomeA?.status ?? 'unobserved'}
                  </strong>
                </span>
              </div>
            )}
          </Card>

          <Card className="flex flex-col gap-2 p-4">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <label htmlFor="compare-run-b" className="text-[11px] font-semibold uppercase tracking-wider text-heading">
                Run B (Candidate)
              </label>
              <label className="text-[11px] text-tertiary-foreground">
                Harness:{' '}
                <select
                  data-testid="compare-run-b-harness-filter"
                  value={harnessFilterB}
                  onChange={(e) => setHarnessFilterB(e.target.value)}
                  className="rounded-md border border-border bg-background px-2 py-0.5 text-xs text-foreground focus:outline-none focus:ring-1 focus:ring-primary"
                >
                  <option value="">All harnesses</option>
                  {harnesses.map((harness) => (
                    <option key={harness} value={harness}>
                      {harness}
                    </option>
                  ))}
                </select>
              </label>
            </div>
            <select
              id="compare-run-b"
              data-testid="compare-run-b"
              value={selectedBId}
              onChange={(e) => handleSelectB(e.target.value)}
              className="rounded-md border border-border bg-background px-3 py-1.5 text-sm text-foreground focus:outline-none focus:ring-1 focus:ring-primary"
            >
              <option value="">Select a run</option>
              {filteredRunsB.map((r) => (
                <option key={r.runId} value={r.runId}>
                  {formatRunOption(r)}
                </option>
              ))}
            </select>
            {runB && (
              <div className="mt-2 flex flex-wrap items-center gap-3 text-xs text-tertiary-foreground">
                <span>Harness: <strong className="text-foreground">{runB.harness}</strong></span>
                <span>
                  Turns:{' '}
                  {runBMetrics.unavailable ? (
                    <>
                      <strong className="text-foreground">—</strong>
                      {runBMetrics.reason ? (
                        <span className="text-muted-foreground"> ({runBMetrics.reason})</span>
                      ) : null}
                    </>
                  ) : (
                    <strong className="text-foreground">
                      {comparison?.runB.turnCount ?? runB.turnCount ?? runB.turns.length}
                    </strong>
                  )}
                </span>
                <span>
                  Outcome:{' '}
                  <strong
                    className={
                      outcomeB?.status === 'success'
                        ? 'text-emerald-600 dark:text-emerald-400'
                        : 'text-rose-600 dark:text-rose-400'
                    }
                  >
                    {outcomeB?.status ?? 'unobserved'}
                  </strong>
                </span>
              </div>
            )}
          </Card>
        </div>
      ) : null}

      {comparisonError ? (
        <Card className="p-4 text-sm text-rose-700 dark:text-rose-300">
          Failed to compare runs: {comparisonErr instanceof Error ? comparisonErr.message : String(comparisonErr)}
        </Card>
      ) : null}

      {canFetchComparison && loadingComparison ? <Skeleton className="h-24" /> : null}

      {verdict || (!live && selectedAId && selectedBId && selectedAId !== selectedBId) ? (
        <Card
          data-testid="compare-n-guard"
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
                    : historyState.unavailable
                      ? 'Candidate Only (Manual Pair)'
                      : 'Candidate Only (n < 5 Minimum Required)'}
              </span>
              {historyState.unavailable ? (
                <span className="text-xs font-medium text-foreground">
                  Recommendation history: — ({historyState.reason})
                </span>
              ) : completedPairCount != null ? (
                <span className="text-xs font-medium text-foreground">
                  Completed Pairs: {completedPairCount} / 5 minimum
                </span>
              ) : null}
            </div>

            <span className="text-xs tabular-nums text-tertiary-foreground">
              {tokenDeltaState.unavailable ? (
                <>
                  Token Delta: — ({tokenDeltaState.reason})
                </>
              ) : (
                <>
                  Token Delta:{' '}
                  {tokenDelta != null && tokenDelta > 0
                    ? `+${tokenDelta.toLocaleString()}`
                    : tokenDelta?.toLocaleString() ?? '—'}
                </>
              )}
            </span>
          </div>

          <p className="text-xs text-muted-foreground">{guardMessage}</p>
        </Card>
      ) : null}

      {selectedAId && selectedBId && selectedAId !== selectedBId ? (
        <div className="flex flex-col gap-3">
          <h2 className="text-sm font-semibold uppercase tracking-wider text-heading">
            Phase-Aligned Turn Diff
          </h2>
          {loadingComparison && live ? (
            <Skeleton className="h-40" />
          ) : (
            <TurnAlignedDiff
              pairs={pairs}
              runALabel={runA?.label ?? comparison?.runA.label ?? 'Run A'}
              runBLabel={runB?.label ?? comparison?.runB.label ?? 'Run B'}
            />
          )}
        </div>
      ) : null}
    </div>
  )
}
