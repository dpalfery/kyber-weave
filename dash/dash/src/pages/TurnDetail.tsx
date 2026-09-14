import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { fetchRun, fetchTurnContent } from '../lib/kyberApi'
import { cn } from '../lib/utils'
import { Card } from '../components/ui/card'
import { Skeleton } from '../components/ui/skeleton'
import { ContextInspector } from '../components/ContextInspector'
import { ContextReviewPanel, HierarchyBreadcrumb } from '../components/kyber'

export interface TurnDetailProps {
  runId: string
  executionId?: string
  turnIndex: number
  onSelectAll?: () => void
  onSelectHarness?: (harnessId: string) => void
  onSelectRun?: (runId: string) => void
  onSelectExecution?: (executionId: string) => void
}

/**
 * The session whose records a turn's content lives in.
 *
 * A run id is not a session id. Every derived run is keyed
 * `derived:<harness>:<session>`, so asking the content route for a run id 404s
 * — which is what left this page showing "Session or turn content not found"
 * with no context bands. The turn belongs to an execution, and the execution
 * names its session; a turn reached without an execution (from a finding, or
 * any link that names only the run) indexes into the run's first execution
 * rather than resolving to nothing.
 */
export function resolveTurnSessionId(
  run: { executions?: readonly { executionId: string; sessionId?: string | null }[] } | undefined,
  executionId: string | undefined,
): string | undefined {
  const executions = run?.executions
  const execution = executionId
    ? executions?.find((candidate) => candidate.executionId === executionId)
    : executions?.[0]
  return execution?.sessionId ?? execution?.executionId ?? executionId
}

/**
 * Turn is the spine entry for unclipped context inspection. Band clicks select
 * a block inside ContextInspector rather than swapping in a raw pre that would
 * hide clipping banners. See docs/plans/2026-09-06-kyberdash-spine.md § B3.
 */
export function TurnDetail({
  runId,
  executionId,
  turnIndex,
  onSelectAll,
  onSelectHarness,
  onSelectRun,
  onSelectExecution,
}: TurnDetailProps) {
  const [selectedBlockKey, setSelectedBlockKey] = useState<string | undefined>()
  const { data: run } = useQuery({
    queryKey: ['kyber-turn-run', runId],
    queryFn: () => fetchRun(runId),
  })
  const sessionId = resolveTurnSessionId(run, executionId)
  const { data, isLoading } = useQuery({
    queryKey: ['kyber-turn-content', sessionId, turnIndex],
    queryFn: () => fetchTurnContent(sessionId!, turnIndex),
    enabled: !!sessionId,
  })

  const recordedBlocks = useMemo(
    () => data?.blocks.filter((block) =>
      Boolean(block.notMeasurable?.reason || block.text.trim() || block.parts.some((part) => part.text.trim())),
    ) ?? [],
    [data],
  )

  return (
    <div className="flex flex-col gap-density-stack" data-testid="page-turn">
      <HierarchyBreadcrumb
        runId={runId}
        executionId={executionId}
        turnIndex={turnIndex}
        onSelectAll={onSelectAll}
        onSelectHarness={onSelectHarness}
        onSelectRun={onSelectRun}
        onSelectExecution={onSelectExecution}
      />

      <Card className="p-chrome">
        <h2 className="font-display text-density-display font-bold tracking-density text-foreground">Turn {turnIndex}</h2>
        <p className="mt-density-hair text-density-xs text-muted-foreground leading-density">Select a context band to inspect its recorded content.</p>

        {isLoading ? (
          <Skeleton className="mt-density-stack h-20 w-full" />
        ) : recordedBlocks.length ? (
          <div className="mt-density-stack flex flex-wrap gap-density-cluster">
            {recordedBlocks.map((block) => (
              <button
                key={block.key}
                type="button"
                data-testid={`context-band-${block.key}`}
                onClick={() => setSelectedBlockKey(block.key)}
                className={cn(
                  'rounded-chrome border border-border px-chrome-sm py-chrome-xs text-density-xs hover:bg-primary hover:text-primary-foreground',
                  selectedBlockKey === block.key
                    ? 'bg-primary text-primary-foreground'
                    : 'bg-interactive-secondary text-foreground',
                )}
              >
                {block.label}
              </button>
            ))}
          </div>
        ) : (
          <p className="mt-density-stack text-density-xs text-tertiary-foreground">No context bands were recorded for this turn.</p>
        )}
      </Card>

      <Card className="p-chrome" data-testid="context-content">
        <ContextInspector
          sessionId={sessionId}
          turnIndex={turnIndex}
          data={data}
          initialBlockKey={selectedBlockKey}
        />
      </Card>

      <ContextReviewPanel
        content={data?.assembledText ?? ''}
        turnIndex={turnIndex}
        sessionId={sessionId}
        blocks={data?.blocks.map((block) => ({
          key: block.key,
          label: block.label,
          text: block.text,
        }))}
        harness={run?.harness}
        model={data?.model}
      />
    </div>
  )
}
