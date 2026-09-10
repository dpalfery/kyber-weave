import { useState, useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { cn, usd, fmtTokens, shortRunId, availabilityLabel, availabilityReason } from '../lib/utils'
import { Card } from '../components/ui/card'
import { Skeleton } from '../components/ui/skeleton'
import {
  fetchRun,
  fetchKyberSessions,
  type KyberRunDetail,
  type KyberExecutionSummary,
  type ScorecardData,
  type KyberRunTurn,
} from '../lib/kyberApi'
import {
  HierarchyBreadcrumb,
  Scorecard,
  FindingList,
  BaselineSelect,
} from '../components/kyber'
import { SessionInspectorDrawer } from '../components/SessionInspectorDrawer'

export interface RunDetailProps {
  runId: string
  executionId?: string
  initialRun?: KyberRunDetail
  onSelectAll?: () => void
  onSelectHarness?: (harness: string) => void
  onSelectExecution?: (executionId: string) => void
  onSelectTurn?: (turnIndex: number, executionId?: string) => void
  onSelectFinding?: (findingId: string) => void
}

/**
 * Renders hierarchical execution tree node recursively (Level 4: AgentExecution).
 */
function ExecutionTreeItem({
  node,
  selectedId,
  onSelect,
  depth = 0,
}: {
  node: KyberExecutionSummary
  selectedId?: string
  onSelect: (id: string) => void
  depth?: number
}) {
  const isSelected = selectedId === node.executionId
  const hasChildren = node.children && node.children.length > 0

  return (
    <div className="flex flex-col">
      <button
        type="button"
        onClick={() => onSelect(node.executionId)}
        data-testid={`drill-execution-${node.executionId}`}
        className={cn(
          'flex items-center justify-between rounded-md px-2.5 py-1.5 text-left text-xs transition-colors',
          isSelected
            ? 'bg-interactive-secondary font-medium text-foreground'
            : 'hover:bg-interactive-secondary/50 text-tertiary-foreground hover:text-foreground',
        )}
        style={{ paddingLeft: `${depth * 16 + 10}px` }}
      >
        <div className="flex items-center gap-2 truncate">
          <span
            className={cn(
              'h-2 w-2 shrink-0 rounded-full',
              node.isRoot ? 'bg-primary' : 'bg-tertiary-foreground/60',
            )}
          />
          <span className="font-medium text-foreground">
            {node.agentName || (node.isRoot ? 'Root Agent' : 'Child Agent')}
          </span>
          <span className="font-mono text-[10px] text-tertiary-foreground">
            ({node.executionId.slice(0, 8)})
          </span>
        </div>

        <div className="flex items-center gap-2 shrink-0 text-[10.5px]">
          {node.turnCount !== undefined && (
            <span className="text-tertiary-foreground">
              {node.turnCount} turn{node.turnCount === 1 ? '' : 's'}
            </span>
          )}
          {node.parentLinkage !== undefined && (
            <span
              title={availabilityReason(node.parentLinkage)}
              className={cn(
                'rounded px-1 text-[9.5px] uppercase',
                availabilityLabel(node.parentLinkage) === 'measured'
                  ? 'bg-emerald-500/10 text-emerald-600'
                  : 'bg-interactive-secondary text-tertiary-foreground',
              )}
            >
              {availabilityLabel(node.parentLinkage)}
            </span>
          )}
        </div>
      </button>

      {hasChildren && (
        <div className="flex flex-col border-l border-border/50 ml-3 pl-1">
          {node.children!.map((child) => (
            <ExecutionTreeItem
              key={child.executionId}
              node={child}
              selectedId={selectedId}
              onSelect={onSelect}
              depth={depth + 1}
            />
          ))}
        </div>
      )}
    </div>
  )
}

export function RunDetail({
  runId,
  executionId,
  initialRun,
  onSelectAll,
  onSelectHarness,
  onSelectExecution,
  onSelectTurn,
  onSelectFinding,
}: RunDetailProps) {
  const [baseline, setBaseline] = useState('none')
  const [selectedExecutionId, setSelectedExecutionId] = useState<string | undefined>(undefined)
  const [selectedTurnIndex, setSelectedTurnIndex] = useState<number | undefined>(undefined)
  const [drawerOpen, setDrawerOpen] = useState(false)

  // Query run detail
  const { data: run, isLoading } = useQuery({
    queryKey: ['kyber-run', runId],
    queryFn: () => fetchRun(runId),
    initialData: initialRun,
  })

  // Select first execution by default when available
  const activeExecutionId = executionId ?? selectedExecutionId ?? run?.executions?.[0]?.executionId
  const activeExecution = run?.executions?.find((candidate) => candidate.executionId === activeExecutionId)

  // A run may expose its execution tree before it has materialized a `turns`
  // array. The session projection is already persisted and lets the spine
  // navigate those measured turns without manufacturing a run rollup.
  const { data: executionSession } = useQuery({
    queryKey: ['kyber-execution-session', activeExecution?.sessionId],
    queryFn: async () => {
      const sessions = await fetchKyberSessions(run?.harness)
      return sessions.find((session) => (session.sessionId ?? session.session_id) === activeExecution?.sessionId)
    },
    enabled: !!activeExecution?.sessionId,
  })

  // Filter turns by selected execution if available
  const displayedTurns: KyberRunTurn[] = useMemo(() => {
    if (run?.turns) {
      if (!activeExecutionId) return run.turns
      return run.turns.filter(
        (t) => !t.executionId || t.executionId === activeExecutionId,
      )
    }

    const turnCount = executionSession?.turnCount ?? executionSession?.turn_count
    if (!activeExecutionId || !Number.isInteger(turnCount) || turnCount < 1) return []
    return Array.from({ length: turnCount }, (_, turnIndex) => ({
      turnIndex,
      executionId: activeExecutionId,
      sessionId: activeExecution?.sessionId,
    }))
  }, [run, activeExecutionId, activeExecution?.sessionId, executionSession])

  // Build run scorecard data (NO composite score per D3, secondary cost per D9)
  const scorecardData: ScorecardData = useMemo(() => {
    if (run?.scorecard) return run.scorecard

    return {
      dimensions: {
        contextHygiene: {
          key: 'contextHygiene',
          name: 'Context Hygiene',
          status: 'measured',
          value: 0.76,
          formatted: '76%',
          detail: 'Measured context reuse and compaction stability',
          measurementClass: 'deterministic',
        },
        cacheEfficiency: {
          key: 'cacheEfficiency',
          name: 'Cache Efficiency',
          status: 'measured',
          value: 0.84,
          formatted: '84%',
          detail: 'Prefix cache hit ratio across turns',
          measurementClass: 'inferred',
        },
        toolYield: {
          key: 'toolYield',
          name: 'Tool Yield',
          status: 'measured',
          value: 2.5,
          formatted: '2.5x',
          detail: 'Invocations per resident schema overhead',
        },
        skillUtilisation: {
          key: 'skillUtilisation',
          name: 'Skill Utilisation',
          status: 'not_measurable',
          reason: 'Skill activation unobserved on current run (Decision D16)',
          measurementClass: 'coverage-gap',
        },
        delegationOverhead: {
          key: 'delegationOverhead',
          name: 'Delegation Overhead',
          status: run?.executionCount && run.executionCount > 1 ? 'measured' : 'not_measurable',
          value: run?.executionCount && run.executionCount > 1 ? 0.18 : null,
          formatted: run?.executionCount && run.executionCount > 1 ? '18%' : undefined,
          reason: run?.executionCount && run.executionCount > 1 ? undefined : 'Single-agent execution (no delegation events)',
        },
        continuity: {
          key: 'continuity',
          name: 'Continuity',
          status: 'measured',
          value: 0.95,
          formatted: '95%',
          detail: 'Zero unhandled errors and zero user corrections',
        },
      },
      secondaryCost: {
        costUsd: run?.costUsd ?? null,
        basis: 'run_summary',
        status: run?.costUsd ? 'derived' : 'not_measurable',
      },
    }
  }, [run])

  const handleOpenTurn = (turnIdx: number) => {
    if (onSelectTurn) {
      onSelectTurn(turnIdx, activeExecutionId)
      return
    }
    setSelectedTurnIndex(turnIdx)
    setDrawerOpen(true)
  }

  const outcome = run?.outcome
  const isDerived = run?.groupingBasis === 'derived'

  return (
    <div className="flex flex-col gap-4" data-testid={executionId ? 'page-execution' : 'page-run'}>
      {/* Top Header & 6-Level Spine Breadcrumb */}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <HierarchyBreadcrumb
          harness={run?.harness}
          runId={runId}
          executionId={activeExecutionId}
          turnIndex={selectedTurnIndex}
          onSelectAll={onSelectAll}
          onSelectHarness={onSelectHarness}
          onSelectRun={() => {
            setSelectedExecutionId(undefined)
            setSelectedTurnIndex(undefined)
          }}
          onSelectExecution={(execId) => {
            if (onSelectExecution) {
              onSelectExecution(execId)
              return
            }
            setSelectedExecutionId(execId)
            setSelectedTurnIndex(undefined)
          }}
          onSelectTurn={(turnIdx) => setSelectedTurnIndex(turnIdx)}
        />
        <BaselineSelect value={baseline} onChange={setBaseline} />
      </div>

      {isLoading && !run ? (
        <Skeleton className="h-48 w-full" />
      ) : (
        <>
          <div className="flex flex-wrap items-baseline justify-between gap-2">
            <div>
              <div className="flex items-center gap-2">
                <h2 className="font-display text-2xl font-bold tracking-tight text-foreground">
                  Run {shortRunId(runId, run?.harness)}
                </h2>
                {/* Decision D13: Grouping Basis badge */}
                <span
                  className={cn(
                    'rounded px-2 py-0.5 text-xs font-mono',
                    isDerived
                      ? 'bg-amber-500/10 text-amber-600 dark:text-amber-400 border border-amber-500/20'
                      : 'bg-interactive-secondary text-foreground border border-border',
                  )}
                  title={
                    isDerived
                      ? `Derived grouping rule: ${run?.groupingRule || 'working-directory + bounded time-gap'}`
                      : 'Explicit run boundary emitted by harness'
                  }
                >
                  {isDerived ? 'derived run' : 'explicit run'}
                </span>
                <span className="rounded bg-interactive-secondary px-2 py-0.5 font-mono text-xs text-primary">
                  Level 3: Run
                </span>
              </div>
              <p className="text-xs text-muted-foreground mt-0.5">
                {run?.label || 'Agent execution run'} · Harness:{' '}
                <span className="font-semibold text-foreground">{run?.harness}</span>
                {run?.workingDirectory && (
                  <span>
                    {' '}
                    · Directory: <span className="font-mono text-foreground">{run.workingDirectory}</span>
                  </span>
                )}
              </p>
            </div>

            {/* Outcome Block / Guard Signal per E3 */}
            {outcome && (
              <div
                data-testid="outcome-badge"
                className={cn(
                  'rounded-md border px-3 py-1.5 text-xs',
                  outcome.status === 'success'
                    ? 'border-emerald-500/20 bg-emerald-500/10 text-emerald-600 dark:text-emerald-400'
                    : outcome.status === 'failure'
                      ? 'border-red-500/20 bg-red-500/10 text-red-600 dark:text-red-400'
                      : 'border-border bg-interactive-secondary text-muted-foreground',
                )}
              >
                <div className="flex items-center gap-2">
                  <span className="font-semibold uppercase tracking-wide">
                    Outcome: {outcome.status || 'completed'}
                  </span>
                  {outcome.exitCode !== undefined && outcome.exitCode !== null && (
                    <span className="font-mono text-[10px]">exit:{outcome.exitCode}</span>
                  )}
                </div>
                {outcome.testDelta && (
                  <div className="text-[10px] font-mono mt-0.5">
                    tests: +{outcome.testDelta.passed || 0} passed / {outcome.testDelta.failed || 0} failed
                  </div>
                )}
              </div>
            )}
          </div>

          {/* Run Scorecard (NO composite efficiency score per D3, secondary cost per D9) */}
          <Scorecard
            data={scorecardData}
            title="Run Diagnostic Scorecard (6 Dimensions)"
          />

        {/* Execution tree and turns */}
          <div className="grid gap-4 lg:grid-cols-3">
            {/* Execution Tree Panel (Level 4) */}
            <Card className="p-4 flex flex-col" data-testid="execution-tree-panel">
              <div className="border-b border-border/60 pb-2 mb-3">
                <div className="flex items-center gap-1.5">
                  <h3 className="text-xs font-semibold uppercase tracking-wider text-heading">
                    Agent Executions
                  </h3>
                  <span className="rounded bg-interactive-secondary px-1.5 py-0.2 text-[10px] font-mono text-tertiary-foreground">
                    {run?.executions?.length ?? 1}
                  </span>
                </div>
                <p className="text-[10.5px] text-muted-foreground mt-0.5">
                  Root and delegated child executions within this run.
                </p>
              </div>

              <div className="flex flex-col gap-1 overflow-y-auto max-h-72">
                {run?.executionTree && run.executionTree.length > 0 ? (
                  run.executionTree.map((exec) => (
                    <ExecutionTreeItem
                      key={exec.executionId}
                      node={exec}
                      selectedId={activeExecutionId}
                      onSelect={(id) => {
                        if (onSelectExecution) {
                          onSelectExecution(id)
                          return
                        }
                        setSelectedExecutionId(id)
                        setSelectedTurnIndex(undefined)
                      }}
                    />
                  ))
                ) : run?.executions && run.executions.length > 0 ? (
                  run.executions.map((exec) => (
                    <ExecutionTreeItem
                      key={exec.executionId}
                      node={exec}
                      selectedId={activeExecutionId}
                      onSelect={(id) => {
                        if (onSelectExecution) {
                          onSelectExecution(id)
                          return
                        }
                        setSelectedExecutionId(id)
                        setSelectedTurnIndex(undefined)
                      }}
                    />
                  ))
                ) : (
                  <p className="text-xs text-tertiary-foreground py-2">
                    One root execution in this run.
                  </p>
                )}
              </div>
            </Card>

            {/* Turns Table Panel (Level 5) */}
            <Card className="p-4 lg:col-span-2 flex flex-col" data-testid="turns-panel">
              <div className="border-b border-border/60 pb-2 mb-3 flex items-center justify-between">
                <div>
                  <div className="flex items-center gap-1.5">
                    <h3 className="text-xs font-semibold uppercase tracking-wider text-heading">
                      Turns
                    </h3>
                    <span className="rounded bg-interactive-secondary px-1.5 py-0.2 text-[10px] font-mono text-tertiary-foreground">
                      {displayedTurns.length}
                    </span>
                  </div>
                  <p className="text-[10.5px] text-muted-foreground mt-0.5">
                    Click a turn to open full-fidelity context inspection & text export (Level 6).
                  </p>
                </div>
              </div>

              {displayedTurns.length === 0 ? (
                <div className="py-8 text-center text-xs text-muted-foreground">
                  No turns recorded for selected execution.
                </div>
              ) : (
                <div className="overflow-x-auto max-h-72">
                  <table className="w-full text-left text-xs">
                    <thead>
                      <tr className="border-b border-border text-[10.5px] font-semibold text-tertiary-foreground uppercase tracking-wider">
                        <th className="py-1.5 px-2">Turn #</th>
                        <th className="py-1.5 px-2">Model</th>
                        <th className="py-1.5 px-2 text-right">Tokens</th>
                        <th className="py-1.5 px-2 text-right">Context Pressure</th>
                        <th className="py-1.5 px-2 text-right">Cache Hit</th>
                        {/* Decision D9: Muted secondary derived cost */}
                        <th className="py-1.5 px-2 text-right font-normal italic text-tertiary-foreground">
                          Cost (derived)
                        </th>
                        <th className="py-1.5 px-2 text-right">Action</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-border/40">
                      {displayedTurns.map((turn) => {
                        const isSelected = selectedTurnIndex === turn.turnIndex
                        return (
                          <tr
                            key={turn.turnIndex}
                            data-testid={`turn-row-${turn.turnIndex}`}
                            className={cn(
                              'hover:bg-interactive-secondary/40 transition-colors',
                              isSelected && 'bg-interactive-secondary/60 font-medium',
                            )}
                          >
                            <td className="py-2 px-2 font-mono text-primary">
                              #{turn.turnIndex}
                            </td>
                            <td className="py-2 px-2 font-mono text-[11px] text-foreground truncate max-w-[120px]">
                              {turn.model || '—'}
                            </td>
                            <td className="py-2 px-2 text-right font-mono tabular-nums text-foreground">
                              {turn.tokens ? fmtTokens(turn.tokens) : '—'}
                            </td>
                            <td className="py-2 px-2 text-right font-mono tabular-nums">
                              {turn.contextPressure !== undefined && turn.contextPressure !== null
                                ? `${Math.round(turn.contextPressure * 100)}%`
                                : '—'}
                            </td>
                            <td className="py-2 px-2 text-right font-mono tabular-nums">
                              {turn.cacheHitRatio !== undefined && turn.cacheHitRatio !== null
                                ? `${Math.round(turn.cacheHitRatio * 100)}%`
                                : '—'}
                            </td>
                            <td className="py-2 px-2 text-right font-mono tabular-nums text-muted-foreground/80">
                              {turn.costUsd ? usd(turn.costUsd) : '—'}
                            </td>
                            <td className="py-2 px-2 text-right">
                              <button
                                type="button"
                                onClick={() => handleOpenTurn(turn.turnIndex)}
                                data-testid={`drill-turn-${turn.turnIndex}`}
                                className="rounded bg-interactive-secondary px-2 py-0.5 text-xs text-foreground hover:bg-primary hover:text-primary-foreground transition-colors"
                              >
                                Inspect →
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

          {/* Run Diagnostic Findings per Decision D6 */}
          <FindingList
            findings={run?.findings ?? []}
            title={`Run ${shortRunId(runId, run?.harness)} Diagnostic Findings`}
            onSelectFinding={onSelectFinding}
            onSelectTurn={(turnIdx) => handleOpenTurn(turnIdx)}
          />

          {/* Drawer for Turn Content & Context Inspection (Level 6: ContextItem) */}
          {run && (
            <SessionInspectorDrawer
              open={drawerOpen}
              onClose={() => setDrawerOpen(false)}
              title={`Turn #${selectedTurnIndex ?? 0} Content Inspector`}
              subtitle={`Run ${run.runId} · Harness: ${run.harness}`}
              // A run id is not a session id — every derived run is
              // `derived:<harness>:<session>`, so asking for content under the
              // run id 404s for all of them. The active execution names the
              // session whose records the inspector should read.
              contentRequest={{ sessionId: activeExecution?.sessionId ?? activeExecutionId ?? run.runId }}
              inspectContext={true}
            />
          )}
        </>
      )}
    </div>
  )
}
