import { useMemo, useState } from 'react'
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
  type KyberPhaseAlignedTurnPair,
  type KyberRunComparison,
  type KyberRunSummary,
} from '../lib/kyberApi.js'
import { fmtRunTimestamp, shortRunId } from '../lib/utils.js'

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
  /** Measured turn count from `/api/kyber/runs` (picker size + D2 filter). */
  turnCount?: number
  totalInput?: number | null
  totalOutput?: number | null
  /** True when any linked session is a subagent (D2 picker filter). */
  isSubagent?: boolean
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
    outcome: toOutcome(row.outcome),
    turns: [],
    turnCount: row.turnCount,
    totalInput: row.totalInput,
    totalOutput: row.totalOutput,
    isSubagent: row.isSubagent === true,
  }
}

/**
 * Picker option text (D4): `{localDate} · {harness} · {turnCount}t · {tokens} · {shortId}`
 * with an optional `label` prefix. Unmeasured turns and tokens are "—", not a
 * fabricated zero — the same honesty `isComparePickerVisible` uses for D2.
 */
export function formatCompareRunOptionLabel(run: RunCandidate): string {
  const localDate = fmtRunTimestamp(run.started).date
  const turns = run.turnCount === undefined ? '—' : `${run.turnCount}t`
  const input = run.totalInput
  const output = run.totalOutput
  const hasTokens =
    (typeof input === 'number' && Number.isFinite(input)) ||
    (typeof output === 'number' && Number.isFinite(output))
  const tokens = hasTokens
    ? ((typeof input === 'number' ? input : 0) + (typeof output === 'number' ? output : 0)).toLocaleString()
    : '—'
  const shortId = shortRunId(run.runId, run.harness)
  const body = `${localDate} · ${run.harness} · ${turns} · ${tokens} · ${shortId}`
  return run.label ? `${run.label} · ${body}` : body
}

/**
 * Picker-card Turns figure. Measured counts win; a loaded turn list is a last
 * resort. `toCandidate` always sets `turns: []`, so treating length as a
 * fallback would reprint the #190 "Turns: 0" lie for unmeasured runs.
 */
export function formatComparePickerTurns(measured: number | undefined, loadedTurns: number): string {
  if (measured !== undefined) return String(measured)
  if (loadedTurns > 0) return String(loadedTurns)
  return '—'
}

/** D2: hide zero-turn runs always; hide subagents unless the toggle is on. */
export function isComparePickerVisible(run: RunCandidate, showSubagents: boolean): boolean {
  if (run.turnCount === 0) return false
  if (!showSubagents && run.isSubagent === true) return false
  return true
}

function workingDirectoryBasename(cwd: string | null | undefined): string | undefined {
  const trimmed = cwd?.trim()
  if (!trimmed) return undefined
  const parts = trimmed.replace(/\\/g, '/').split('/').filter(Boolean)
  return parts.at(-1)
}

/**
 * Visible `<option>` text. Colliding D4 labels get a cwd basename (or a stable
 * ordinal) appended so Chrome/Firefox users can tell the rows apart — `<option
 * title>` is not reliably surfaced.
 */
export function compareOptionLabels(runs: readonly RunCandidate[]): Map<string, string> {
  const base = new Map<string, string>()
  for (const run of runs) {
    base.set(run.runId, formatCompareRunOptionLabel(run))
  }
  const groups = new Map<string, RunCandidate[]>()
  for (const run of runs) {
    const label = base.get(run.runId) ?? formatCompareRunOptionLabel(run)
    const group = groups.get(label) ?? []
    group.push(run)
    groups.set(label, group)
  }
  const visible = new Map<string, string>()
  for (const run of runs) {
    const label = base.get(run.runId) ?? formatCompareRunOptionLabel(run)
    const group = groups.get(label) ?? [run]
    if (group.length === 1) {
      visible.set(run.runId, label)
      continue
    }
    const basename = workingDirectoryBasename(run.workingDirectory)
    const used = group.map((sibling) => workingDirectoryBasename(sibling.workingDirectory))
    const basenameUnique = basename !== undefined && used.filter((name) => name === basename).length === 1
    if (basenameUnique) {
      visible.set(run.runId, `${label} · ${basename}`)
      continue
    }
    visible.set(run.runId, `${label} · ${group.findIndex((sibling) => sibling.runId === run.runId) + 1}`)
  }
  return visible
}

function selectedRunTitle(run: RunCandidate | undefined): string | undefined {
  if (!run) return undefined
  const cwd = run.workingDirectory?.trim()
  // HTML title attributes collapse newlines; keep the pair on one readable line.
  return cwd ? `${run.runId} · ${cwd}` : run.runId
}

function RunPickerCard({
  id,
  heading,
  selectedId,
  onChange,
  run,
  outcome,
  turnCount,
  pickerRuns,
  optionLabels,
}: {
  id: string
  heading: string
  selectedId: string
  onChange: (id: string) => void
  run: RunCandidate | undefined
  outcome: OutcomeSummary | undefined
  turnCount: string
  pickerRuns: readonly RunCandidate[]
  optionLabels: ReadonlyMap<string, string>
}) {
  return (
    <Card className="flex flex-col gap-2 p-4">
      <label htmlFor={id} className="text-[11px] font-semibold uppercase tracking-wider text-heading">
        {heading}
      </label>
      <select
        id={id}
        data-testid={id}
        value={selectedId}
        title={selectedRunTitle(run)}
        onChange={(e) => onChange(e.target.value)}
        className="rounded-md border border-border bg-background px-3 py-1.5 text-sm text-foreground focus:outline-none focus:ring-1 focus:ring-primary"
      >
        <option value="">Select a run</option>
        {pickerRuns.map((candidate) => {
          const label = optionLabels.get(candidate.runId) ?? formatCompareRunOptionLabel(candidate)
          return (
            <option key={candidate.runId} value={candidate.runId}>
              {label}
            </option>
          )
        })}
      </select>
      {run && (
        <div className="mt-2 flex flex-wrap items-center gap-3 text-xs text-tertiary-foreground">
          <span>Harness: <strong className="text-foreground">{run.harness}</strong></span>
          <span>Turns: <strong className="text-foreground">{turnCount}</strong></span>
          <span>
            Outcome:{' '}
            <strong
              className={
                outcome?.status === 'success'
                  ? 'text-emerald-600 dark:text-emerald-400'
                  : 'text-rose-600 dark:text-rose-400'
              }
            >
              {outcome?.status ?? 'unobserved'}
            </strong>
          </span>
        </div>
      )}
    </Card>
  )
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

function alignTurnsByPhase(turnsA: RunTurnSummary[], turnsB: RunTurnSummary[]): PhaseAlignedTurnPair[] {
  const pairs: PhaseAlignedTurnPair[] = []

  for (const phase of PHASES) {
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

function turnTokenTotal(turn: RunTurnSummary): number {
  return turn.tokens?.all ?? (turn.tokens?.reportedInput ?? 0) + (turn.tokens?.output ?? 0)
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
  const [showProposedDrawer, setShowProposedDrawer] = useState(true)
  const [showSubagents, setShowSubagents] = useState(false)

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

  // D3: no silent list default. Deep-link / initial* / controlled props count as explicit.
  const selectedAId = propA ?? localA ?? initialRunAId ?? ''
  const selectedBId = propB ?? localB ?? initialRunBId ?? ''

  // D2 filters apply, but an explicitly selected id must stay in the <select>
  // options so deep-links / controlled props do not render a blank value while
  // compare still fetches. Do not auto-toggle Show subagents — preserve filter intent.
  const pickerRuns = useMemo(() => {
    const pinned = new Set([selectedAId, selectedBId].filter((id) => id !== ''))
    return runs.filter(
      (r) => isComparePickerVisible(r, showSubagents) || pinned.has(r.runId),
    )
  }, [runs, showSubagents, selectedAId, selectedBId])

  const optionLabels = useMemo(() => compareOptionLabels(pickerRuns), [pickerRuns])

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
    asNumber(comparison?.runA.totalTokens) ??
    (runA?.turns ?? []).reduce((sum, t) => sum + turnTokenTotal(t), 0)
  const totalTokensB =
    asNumber(comparison?.runB.totalTokens) ??
    (runB?.turns ?? []).reduce((sum, t) => sum + turnTokenTotal(t), 0)
  const tokenDelta = asNumber(comparison?.totals.tokenDelta) ?? totalTokensB - totalTokensA

  const activeProposedPair = proposedPairs.find(
    (p) =>
      (p.runAId === selectedAId && p.runBId === selectedBId) ||
      (p.runAId === selectedBId && p.runBId === selectedAId),
  )

  const verdict = comparison?.verdict
  // Honest n: API 0/1 for this pair when the client omits completedPairCount. Never invent 6.
  const completedPairCount = verdict?.completedPairCount ?? activeProposedPair?.completedPairCount ?? 0
  const isOutcomeRegression =
    verdict?.outcomeRegression ??
    (runA?.outcome?.status === 'success' &&
      (runB?.outcome?.status === 'failure' || runB?.outcome?.status === 'abandoned'))
  const canPromote = verdict?.canPromote ?? (completedPairCount >= 5 && !isOutcomeRegression)
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

  const guardMessage = isOutcomeRegression
    ? (verdict?.refusalReason ??
      'Outcome regression detected between Run A and Run B. Modifications cannot be promoted to recommendations when task correctness degrades.')
    : canPromote
      ? (verdict?.recommendation ??
        'The comparison between baseline and candidate satisfies the documented sufficiency threshold (n ≥ 5 completed pairs) with zero outcome regressions.')
      : (verdict?.refusalReason ??
        `Recommendation promotion refused: observed ${completedPairCount} completed pair(s)${taskFamily ? ` for task family "${taskFamily}"` : ''}. Minimum threshold is n ≥ 5 completed pairs. Auto-pairing is proposed only.`)

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
        <div className="flex flex-col gap-3">
          <div className="flex items-center justify-end">
            <label className="flex items-center gap-2 text-xs text-foreground">
              <input
                type="checkbox"
                checked={showSubagents}
                onChange={(e) => setShowSubagents(e.target.checked)}
                data-testid="compare-show-subagents"
              />
              Show subagents
            </label>
          </div>
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
            <RunPickerCard
              id="compare-run-a"
              heading="Run A (Baseline)"
              selectedId={selectedAId}
              onChange={handleSelectA}
              run={runA}
              outcome={outcomeA}
              turnCount={formatComparePickerTurns(comparison?.runA.turnCount ?? runA?.turnCount, runA?.turns.length ?? 0)}
              pickerRuns={pickerRuns}
              optionLabels={optionLabels}
            />
            <RunPickerCard
              id="compare-run-b"
              heading="Run B (Candidate)"
              selectedId={selectedBId}
              onChange={handleSelectB}
              run={runB}
              outcome={outcomeB}
              turnCount={formatComparePickerTurns(comparison?.runB.turnCount ?? runB?.turnCount, runB?.turns.length ?? 0)}
              pickerRuns={pickerRuns}
              optionLabels={optionLabels}
            />
          </div>
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
