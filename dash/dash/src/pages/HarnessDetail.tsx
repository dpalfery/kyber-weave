import { useState, useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { cn, usd } from '../lib/utils'
import { Card } from '../components/ui/card'
import { Skeleton } from '../components/ui/skeleton'
import {
  fetchHarness,
  fetchRuns,
  fetchFindings,
  type KyberHarnessSummary,
  type KyberRunSummary,
  type KyberFinding,
  type ScorecardData,
} from '../lib/kyberApi'
import {
  HierarchyBreadcrumb,
  Scorecard,
  FindingList,
  ContextPressureStrip,
  BaselineSelect,
} from '../components/kyber'

export interface HarnessDetailProps {
  harnessId: string
  initialHarness?: KyberHarnessSummary
  initialRuns?: KyberRunSummary[]
  initialFindings?: KyberFinding[]
  onSelectAll?: () => void
  onSelectRun?: (runId: string) => void
  onSelectFinding?: (findingId: string) => void
  onSelectTurn?: (turnIndex: number, executionId?: string, runId?: string) => void
}

export function HarnessDetail({
  harnessId,
  initialHarness,
  initialRuns,
  initialFindings,
  onSelectAll,
  onSelectRun,
  onSelectFinding,
  onSelectTurn,
}: HarnessDetailProps) {
  const [baseline, setBaseline] = useState('none')

  // Query harness rollup
  const { data: harnessData } = useQuery({
    queryKey: ['kyber-harness', harnessId],
    queryFn: () => fetchHarness(harnessId),
    initialData: initialHarness,
  })

  // Query recent runs for this harness
  const { data: runsData, isLoading: loadingRuns } = useQuery({
    queryKey: ['kyber-runs', harnessId],
    queryFn: () => fetchRuns(harnessId),
    initialData: initialRuns,
  })

  // Query findings scoped to this harness
  const { data: findingsData, isLoading: loadingFindings } = useQuery({
    queryKey: ['kyber-findings', harnessId],
    queryFn: () => fetchFindings({ harness: harnessId }),
    initialData: initialFindings,
  })

  const harness = harnessData ?? initialHarness
  const runs = runsData ?? []
  const findings = findingsData ?? []

  // Construct honest 6-dimension scorecard for this harness (no composite score per D3!)
  const scorecardData: ScorecardData = useMemo(() => {
    const hasPressure = typeof harness?.contextPressureMedian === 'number'
    const hasCache = typeof harness?.cacheHitRate === 'number'
    const hasYield = typeof harness?.toolYield === 'number'
    const hasDelegation = typeof harness?.delegationOverhead === 'number'

    return {
      dimensions: {
        contextHygiene: {
          key: 'contextHygiene',
          name: 'Context Hygiene',
          value: hasPressure ? harness!.contextPressureMedian : null,
          formatted: hasPressure ? `${Math.round(harness!.contextPressureMedian! * 100)}%` : undefined,
          status: hasPressure ? 'measured' : 'not_measurable',
          reason: hasPressure
            ? 'Measured across runs'
            : 'Telemetry missing: context pressure unrecorded on this harness',
          measurementClass: 'deterministic',
        },
        cacheEfficiency: {
          key: 'cacheEfficiency',
          name: 'Cache Efficiency',
          value: hasCache ? harness!.cacheHitRate : null,
          formatted: hasCache ? `${Math.round(harness!.cacheHitRate! * 100)}%` : undefined,
          status: hasCache ? 'measured' : 'not_measurable',
          reason: hasCache
            ? 'Measured prompt cache read ratio'
            : 'Telemetry missing: harness does not export cache read/creation counters',
          measurementClass: hasCache ? 'inferred' : 'coverage-gap',
        },
        toolYield: {
          key: 'toolYield',
          name: 'Tool Yield',
          value: hasYield ? harness!.toolYield : null,
          formatted: hasYield ? `${harness!.toolYield!.toFixed(1)}x` : undefined,
          status: hasYield ? 'measured' : 'not_measurable',
          reason: hasYield
            ? 'Active calls per resident schema token'
            : 'Telemetry missing: tool call definitions or invocations absent',
        },
        skillUtilisation: {
          key: 'skillUtilisation',
          name: 'Skill Utilisation',
          status: 'not_measurable',
          reason: 'Skill activation is not observed on this harness',
          measurementClass: 'coverage-gap',
        },
        delegationOverhead: {
          key: 'delegationOverhead',
          name: 'Delegation Overhead',
          value: hasDelegation ? harness!.delegationOverhead : null,
          formatted: hasDelegation ? `${Math.round(harness!.delegationOverhead! * 100)}%` : undefined,
          status: hasDelegation ? 'measured' : 'not_measurable',
          reason: hasDelegation
            ? 'Observed delegation handoff ratio'
            : 'Single-agent executions or uninstrumented subagent tree',
        },
        continuity: {
          key: 'continuity',
          name: 'Continuity',
          status: 'not_measurable',
          reason: 'User corrections and turn recovery unrecorded',
          measurementClass: 'coverage-gap',
        },
      },
      secondaryCost: {
        costUsd: harness?.costUsd ?? null,
        basis: 'harness_rollup',
        status: harness?.costUsd ? 'derived' : 'not_measurable',
      },
    }
  }, [harness])

  const coveragePct =
    typeof harness?.fieldCoverage === 'number'
      ? `${Math.round(harness.fieldCoverage * 100)}%`
      : '—'

  return (
    <div className="flex flex-col gap-4" data-testid="page-harness">
      {/* Top Header & Spine Navigation */}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <HierarchyBreadcrumb
          harness={harnessId}
          onSelectAll={onSelectAll}
        />
        <BaselineSelect value={baseline} onChange={setBaseline} />
      </div>

      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <div>
          <div className="flex items-center gap-2">
            <h2 className="font-display text-2xl font-bold tracking-tight text-foreground capitalize">
              {harness?.name || harnessId}
            </h2>
            <span className="rounded bg-interactive-secondary px-2 py-0.5 font-mono text-xs text-primary">
              Level 2: Harness
            </span>
          </div>
          <p className="text-xs text-muted-foreground mt-0.5">
            Aggregated rollup metrics, 6-dimension diagnostic posture, and recent runs for{' '}
            <span className="font-mono text-foreground">{harnessId}</span>.
          </p>
        </div>

        <div className="flex items-center gap-3 text-xs">
          <div className="rounded border border-border/70 bg-card px-3 py-1.5 text-right">
            <div className="text-[10px] uppercase tracking-wider text-tertiary-foreground">
              Field Coverage
            </div>
            <div className="font-mono font-semibold text-foreground">{coveragePct}</div>
          </div>
          <div className="rounded border border-border/70 bg-card px-3 py-1.5 text-right">
            <div className="text-[10px] uppercase tracking-wider text-tertiary-foreground">
              Recorded Runs
            </div>
            <div className="font-mono font-semibold text-foreground">
              {runs.length}
            </div>
          </div>
        </div>
      </div>

      {/* Scorecard for this harness (NO composite score per D3, secondary cost per D9) */}
      <Scorecard
        data={scorecardData}
        title={`${harness?.name || harnessId} Diagnostic Scorecard`}
      />

      {/* Context Pressure Strip */}
      <ContextPressureStrip
        pressureMedian={harness?.contextPressureMedian}
        pressureP95={harness?.contextPressureP95}
        isUnmeasurable={typeof harness?.contextPressureMedian !== 'number'}
        unmeasuredReason="Telemetry missing: context pressure unrecorded on this harness"
      />

      {/* Harness-Specific Findings */}
      {loadingFindings ? (
        <Skeleton className="h-44 w-full" />
      ) : (
        <FindingList
          findings={findings}
          title={`${harness?.name || harnessId} Diagnostic Findings`}
          description="Deterministic waste outranks inferred claims."
          onSelectFinding={onSelectFinding}
          onSelectTurn={(turnIdx, execId) => onSelectTurn?.(turnIdx, execId)}
        />
      )}

      {/* Recent Runs Table (Level 3 Drill-Down) */}
      <Card className="p-4" data-testid="harness-runs-table">
        <div className="flex items-center justify-between border-b border-border/60 pb-3 mb-3">
          <div>
            <h3 className="text-xs font-semibold uppercase tracking-wider text-heading">
              Recent Runs ({runs.length})
            </h3>
            <p className="text-[11px] text-muted-foreground mt-0.5">
              Select a run to drill down into Level 3 (Run Detail), execution trees, and turn inspections.
            </p>
          </div>
        </div>

        {loadingRuns ? (
          <div className="flex flex-col gap-2">
            {Array.from({ length: 3 }).map((_, i) => (
              <Skeleton key={i} className="h-12 w-full" />
            ))}
          </div>
        ) : runs.length === 0 ? (
          <div className="my-6 rounded border border-dashed border-border p-6 text-center text-xs text-muted-foreground">
            No runs recorded yet for harness <span className="font-mono">{harnessId}</span>.
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-left text-xs">
              <thead>
                <tr className="border-b border-border text-[11px] font-semibold text-tertiary-foreground uppercase tracking-wider">
                  <th className="py-2.5 px-3">Run ID</th>
                  <th className="py-2.5 px-3">Label / Task</th>
                  <th className="py-2.5 px-3">Grouping Basis</th>
                  <th className="py-2.5 px-3 text-right">Executions</th>
                  <th className="py-2.5 px-3 text-right">Turns</th>
                  <th className="py-2.5 px-3">Outcome</th>
                  <th className="py-2.5 px-3 text-right">Findings</th>
                  {/* Decision D9: Secondary derived cost */}
                  <th className="py-2.5 px-3 text-right text-tertiary-foreground font-normal italic">
                    Cost (derived)
                  </th>
                  <th className="py-2.5 px-3 text-right">Action</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border/40">
                {runs.map((r) => {
                  const isDerived = r.groupingBasis === 'derived'
                  const outcomeStatus = r.outcome?.status || 'unobserved'

                  return (
                    <tr
                      key={r.runId}
                      data-testid={`run-row-${r.runId}`}
                      className="hover:bg-interactive-secondary/30 transition-colors"
                    >
                      <td className="py-2.5 px-3 font-mono text-xs text-primary font-medium">
                        {r.runId.slice(0, 10)}
                      </td>
                      <td className="py-2.5 px-3">
                        <div className="font-medium text-foreground truncate max-w-[200px]">
                          {r.label || 'untitled run'}
                        </div>
                        {r.workingDirectory && (
                          <div className="text-[10px] font-mono text-tertiary-foreground truncate max-w-[200px]">
                            {r.workingDirectory}
                          </div>
                        )}
                      </td>
                      {/* Decision D13: Explicit vs Derived grouping basis */}
                      <td className="py-2.5 px-3">
                        <span
                          className={cn(
                            'rounded px-1.5 py-0.5 text-[10px] font-mono',
                            isDerived
                              ? 'bg-amber-500/10 text-amber-600 dark:text-amber-400'
                              : 'bg-interactive-secondary text-foreground',
                          )}
                          title={
                            isDerived
                              ? `Derived grouping rule: ${r.groupingRule || 'working-directory + bounded time-gap'}`
                              : 'Explicit run boundary emitted by harness'
                          }
                        >
                          {r.groupingBasis}
                        </span>
                      </td>
                      <td className="py-2.5 px-3 text-right font-mono tabular-nums text-foreground">
                        {r.executionCount ?? 1}
                      </td>
                      <td className="py-2.5 px-3 text-right font-mono tabular-nums text-foreground">
                        {r.turnCount ?? '—'}
                      </td>
                      <td className="py-2.5 px-3">
                        <span
                          className={cn(
                            'rounded px-1.5 py-0.5 text-[10px] font-medium uppercase',
                            outcomeStatus === 'success'
                              ? 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400'
                              : outcomeStatus === 'failure'
                                ? 'bg-red-500/10 text-red-600 dark:text-red-400'
                                : 'bg-interactive-secondary text-tertiary-foreground',
                          )}
                        >
                          {outcomeStatus}
                        </span>
                      </td>
                      <td className="py-2.5 px-3 text-right font-mono tabular-nums text-amber-500 dark:text-amber-400">
                        {r.findingCount ?? 0}
                      </td>
                      {/* Decision D9: Secondary derived cost */}
                      <td className="py-2.5 px-3 text-right font-mono tabular-nums text-muted-foreground/80">
                        {r.costUsd ? usd(r.costUsd) : '—'}
                      </td>
                      <td className="py-2.5 px-3 text-right">
                        <button
                          type="button"
                          onClick={() => onSelectRun?.(r.runId)}
                          data-testid={`drill-run-${r.runId}`}
                          className="rounded bg-interactive-secondary px-2 py-1 text-xs font-medium text-foreground hover:bg-primary hover:text-primary-foreground transition-colors"
                        >
                          Inspect Run →
                        </button>
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </div>
  )
}
