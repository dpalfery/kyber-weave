export type TaskPhase = 'exploration' | 'implementation' | 'verification' | 'resolution'

export type SignalComparisonStatus =
  | 'compared'
  | 'not_comparable'
  | 'missing_in_a'
  | 'missing_in_b'

export type SignalComparison = {
  name: string
  label: string
  unit?: string
  runAValue?: number | string
  runBValue?: number | string
  delta?: number
  status: SignalComparisonStatus
  reason?: string
}

export type RunTurnSummary = {
  turnIndex: number
  spanId?: string
  phase?: TaskPhase
  tokens?: {
    freshInput?: number
    cacheRead?: number
    cacheCreation?: number
    output?: number
    reportedInput?: number
    reportedOutput?: number
    all?: number
  }
  cost?: {
    basis: string
    status: string
    value?: number
    currency?: string
  }
  tools?: string[]
  commands?: string[]
  status?: string
  summary?: string
}

export type PhaseAlignedTurnPair = {
  phase: TaskPhase
  phaseIndex: number
  runATurn: RunTurnSummary | null
  runBTurn: RunTurnSummary | null
  signals: SignalComparison[]
  reading: string
}

export type TurnAlignedDiffProps = {
  pairs: PhaseAlignedTurnPair[]
  runALabel?: string
  runBLabel?: string
  filterPhase?: TaskPhase | 'all'
  onFilterPhaseChange?: (phase: TaskPhase | 'all') => void
  onSelectPair?: (pair: PhaseAlignedTurnPair) => void
  selectedPairIndex?: number
  className?: string
}

const PHASE_LABELS: Record<TaskPhase, { title: string; color: string; badge: string }> = {
  exploration: {
    title: 'Exploration',
    color: 'border-sky-500/30 bg-sky-500/5 dark:border-sky-500/20 dark:bg-sky-500/10',
    badge: 'bg-sky-500/15 text-sky-700 dark:text-sky-300',
  },
  implementation: {
    title: 'Implementation',
    color: 'border-amber-500/30 bg-amber-500/5 dark:border-amber-500/20 dark:bg-amber-500/10',
    badge: 'bg-amber-500/15 text-amber-700 dark:text-amber-300',
  },
  verification: {
    title: 'Verification',
    color: 'border-emerald-500/30 bg-emerald-500/5 dark:border-emerald-500/20 dark:bg-emerald-500/10',
    badge: 'bg-emerald-500/15 text-emerald-700 dark:text-emerald-300',
  },
  resolution: {
    title: 'Resolution',
    color: 'border-purple-500/30 bg-purple-500/5 dark:border-purple-500/20 dark:bg-purple-500/10',
    badge: 'bg-purple-500/15 text-purple-700 dark:text-purple-300',
  },
}

function fmtTokens(val?: number): string {
  if (val === undefined || val === null) return '—'
  return Number(val).toLocaleString()
}

function TurnCard({
  turn,
  label,
  side,
}: {
  turn: RunTurnSummary | null
  label: string
  side: 'A' | 'B'
}) {
  if (!turn) {
    return (
      <div className="flex h-full flex-col justify-center rounded-md border border-dashed border-border/80 bg-muted/20 p-3 text-center">
        <span className="text-xs italic text-tertiary-foreground">
          No turn in {label} for this slot
        </span>
        <span className="mt-1 text-[11px] text-muted-foreground">
          Phase completed with fewer turns
        </span>
      </div>
    )
  }

  const totalTokens =
    turn.tokens?.all ??
    (turn.tokens?.reportedInput ?? 0) + (turn.tokens?.output ?? 0)
  const tools = turn.tools ?? []

  return (
    <div className="flex h-full flex-col rounded-md border border-border bg-card p-3 shadow-xs">
      <div className="mb-2 flex items-center justify-between border-b border-border/60 pb-1.5">
        <div className="flex items-center gap-1.5">
          <span className="rounded bg-muted px-1.5 py-0.5 text-[10px] font-semibold uppercase text-muted-foreground">
            {side}
          </span>
          <span className="text-xs font-semibold text-heading">
            Turn {turn.turnIndex + 1}
          </span>
        </div>
        <span className="font-mono text-xs tabular-nums text-foreground">
          {fmtTokens(totalTokens)} tokens
        </span>
      </div>

      {tools.length > 0 && (
        <div className="mb-2 flex flex-wrap gap-1">
          {tools.map((t, idx) => (
            <span
              key={idx}
              className="rounded bg-interactive-secondary px-1.5 py-0.5 font-mono text-[10.5px] text-foreground"
            >
              {t}
            </span>
          ))}
        </div>
      )}

      <div className="mt-auto grid grid-cols-2 gap-1.5 pt-2 text-[11px] text-tertiary-foreground">
        <div>
          Fresh input:{' '}
          <span className="font-mono text-foreground">
            {fmtTokens(turn.tokens?.freshInput)}
          </span>
        </div>
        <div>
          Cache read:{' '}
          <span className="font-mono text-foreground">
            {fmtTokens(turn.tokens?.cacheRead)}
          </span>
        </div>
        <div>
          Output:{' '}
          <span className="font-mono text-foreground">
            {fmtTokens(turn.tokens?.output)}
          </span>
        </div>
        {turn.cost?.value !== undefined && (
          <div>
            Cost:{' '}
            <span className="font-mono text-foreground">
              ${Number(turn.cost.value).toFixed(3)}
            </span>
          </div>
        )}
      </div>
    </div>
  )
}

function SignalDiffBadge({ signal }: { signal: SignalComparison }) {
  if (signal.status === 'not_comparable') {
    return (
      <span
        title={signal.reason ?? 'Signal not measurable'}
        className="inline-flex items-center rounded border border-amber-300/50 bg-amber-50 px-1.5 py-0.5 text-[10.5px] font-medium text-amber-800 dark:border-amber-700/50 dark:bg-amber-950/40 dark:text-amber-200"
      >
        {signal.label}: not comparable
      </span>
    )
  }

  if (signal.status === 'missing_in_a' || signal.status === 'missing_in_b') {
    return (
      <span className="inline-flex items-center rounded bg-muted/60 px-1.5 py-0.5 text-[10.5px] text-tertiary-foreground">
        {signal.label}: {signal.status === 'missing_in_a' ? 'omitted in A' : 'omitted in B'}
      </span>
    )
  }

  if (signal.delta === undefined) return null

  const delta = signal.delta
  const isZero = delta === 0
  const isNegative = delta < 0

  return (
    <span
      className={`inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-[10.5px] font-mono tabular-nums ${
        isZero
          ? 'bg-muted text-muted-foreground'
          : isNegative
            ? 'bg-emerald-500/15 text-emerald-700 dark:text-emerald-300'
            : 'bg-rose-500/15 text-rose-700 dark:text-rose-300'
      }`}
    >
      <span className="font-sans text-[10px] text-tertiary-foreground">{signal.label}:</span>
      {delta > 0 ? `+${fmtTokens(delta)}` : fmtTokens(delta)}
    </span>
  )
}

export function TurnAlignedDiff({
  pairs,
  runALabel = 'Run A (Baseline)',
  runBLabel = 'Run B (Candidate)',
  filterPhase = 'all',
  onFilterPhaseChange,
  onSelectPair,
  selectedPairIndex,
  className = '',
}: TurnAlignedDiffProps) {
  const activePhase = filterPhase
  const filteredPairs =
    activePhase === 'all' ? pairs : pairs.filter((p) => p.phase === activePhase)

  const groupedByPhase = new Map<TaskPhase, PhaseAlignedTurnPair[]>()
  for (const p of filteredPairs) {
    const list = groupedByPhase.get(p.phase) ?? []
    list.push(p)
    groupedByPhase.set(p.phase, list)
  }

  const phasesPresent = Array.from(groupedByPhase.keys())

  if (pairs.length === 0) {
    return (
      <div className="rounded-lg border border-border bg-card p-8 text-center text-sm text-tertiary-foreground">
        No phase-aligned turn pairs available to compare.
      </div>
    )
  }

  return (
    <div className={`flex flex-col gap-5 ${className}`}>
      {/* Phase Filter Tabs */}
      <div className="flex flex-wrap items-center gap-1.5 border-b border-border pb-3">
        <button
          type="button"
          onClick={() => onFilterPhaseChange?.('all')}
          className={`rounded-md px-2.5 py-1 text-xs font-medium transition-colors ${
            activePhase === 'all'
              ? 'bg-primary text-primary-foreground'
              : 'bg-interactive-secondary text-muted-foreground hover:text-foreground'
          }`}
        >
          All Phases ({pairs.length})
        </button>
        {(['exploration', 'implementation', 'verification', 'resolution'] as TaskPhase[]).map(
          (phase) => {
            const count = pairs.filter((p) => p.phase === phase).length
            if (count === 0) return null
            const label = PHASE_LABELS[phase]
            return (
              <button
                key={phase}
                type="button"
                onClick={() => onFilterPhaseChange?.(phase)}
                className={`flex items-center gap-1.5 rounded-md px-2.5 py-1 text-xs font-medium transition-colors ${
                  activePhase === phase
                    ? 'bg-primary text-primary-foreground'
                    : 'bg-interactive-secondary text-muted-foreground hover:text-foreground'
                }`}
              >
                <span>{label.title}</span>
                <span className="rounded-full bg-background/30 px-1 text-[10px] tabular-nums">
                  {count}
                </span>
              </button>
            )
          }
        )}
      </div>

      {/* Phase Groups */}
      <div className="flex flex-col gap-6">
        {phasesPresent.map((phase) => {
          const phasePairs = groupedByPhase.get(phase) ?? []
          const meta = PHASE_LABELS[phase]
          const turnsA = phasePairs.filter((p) => p.runATurn !== null).length
          const turnsB = phasePairs.filter((p) => p.runBTurn !== null).length

          return (
            <div
              key={phase}
              className={`flex flex-col gap-3 rounded-lg border p-4 ${meta.color}`}
            >
              {/* Phase Header */}
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <span className={`rounded px-2 py-0.5 text-xs font-semibold uppercase tracking-wider ${meta.badge}`}>
                    {meta.title}
                  </span>
                  <span className="text-xs text-tertiary-foreground">
                    {turnsA} turn{turnsA !== 1 ? 's' : ''} in {runALabel} · {turnsB} turn{turnsB !== 1 ? 's' : ''} in {runBLabel}
                  </span>
                </div>
                {turnsA === 0 && (
                  <span className="rounded bg-amber-500/15 px-2 py-0.5 text-[11px] font-medium text-amber-700 dark:text-amber-300">
                    Omitted in {runALabel}
                  </span>
                )}
                {turnsB === 0 && (
                  <span className="rounded bg-amber-500/15 px-2 py-0.5 text-[11px] font-medium text-amber-700 dark:text-amber-300">
                    Omitted in {runBLabel}
                  </span>
                )}
              </div>

              {/* Turn Pairs in this Phase */}
              <div className="flex flex-col gap-3">
                {phasePairs.map((pair) => {
                  const globalIdx = pairs.indexOf(pair)
                  const isSelected = selectedPairIndex === globalIdx

                  return (
                    <div
                      key={pair.phaseIndex}
                      onClick={() => onSelectPair?.(pair)}
                      className={`grid grid-cols-1 gap-3 rounded-md border border-border/80 bg-background/90 p-3 shadow-2xs transition-all lg:grid-cols-7 ${
                        isSelected ? 'ring-2 ring-primary' : 'hover:border-border'
                      }`}
                    >
                      {/* Left: Run A Turn */}
                      <div className="lg:col-span-3">
                        <TurnCard turn={pair.runATurn} label={runALabel} side="A" />
                      </div>

                      {/* Middle: Semantic Alignment & Diff */}
                      <div className="flex flex-col items-center justify-center gap-2 rounded-md bg-muted/30 p-2.5 text-center lg:col-span-1">
                        <span className="text-[10px] font-medium uppercase tracking-wider text-tertiary-foreground">
                          Aligned Slot {pair.phaseIndex + 1}
                        </span>

                        <div className="flex flex-wrap justify-center gap-1">
                          {pair.signals.map((sig, sIdx) => (
                            <SignalDiffBadge key={sIdx} signal={sig} />
                          ))}
                        </div>

                        <p className="mt-1 line-clamp-3 text-[11px] leading-snug text-muted-foreground">
                          {pair.reading}
                        </p>
                      </div>

                      {/* Right: Run B Turn */}
                      <div className="lg:col-span-3">
                        <TurnCard turn={pair.runBTurn} label={runBLabel} side="B" />
                      </div>
                    </div>
                  )
                })}
              </div>
            </div>
          )
        })}
      </div>
    </div>
  )
}
