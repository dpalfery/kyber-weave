import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { fetchRun, fetchTurnContent } from '../lib/kyberApi'
import { Card } from '../components/ui/card'
import { Skeleton } from '../components/ui/skeleton'
import { ContextInspector } from '../components/ContextInspector'
import { HierarchyBreadcrumb } from '../components/kyber'

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
 * The turn surface deliberately reuses the established context inspector rather
 * than creating a second content representation. The small band list is the
 * spine's direct entry point into that existing inspection contract.
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

  const selectedBlock = useMemo(
    () => data?.blocks.find((block) => block.key === selectedBlockKey),
    [data, selectedBlockKey],
  )
  const recordedBlocks = useMemo(
    () => data?.blocks.filter((block) =>
      Boolean(block.notMeasurable?.reason || block.text.trim() || block.parts.some((part) => part.text.trim())),
    ) ?? [],
    [data],
  )
  const selectedContent = selectedBlock
    ? selectedBlock.notMeasurable?.reason || selectedBlock.text || selectedBlock.parts.map((part) => part.text).join('\n\n')
    : undefined

  return (
    <div className="flex flex-col gap-4" data-testid="page-turn">
      <HierarchyBreadcrumb
        runId={runId}
        executionId={executionId}
        turnIndex={turnIndex}
        onSelectAll={onSelectAll}
        onSelectHarness={onSelectHarness}
        onSelectRun={onSelectRun}
        onSelectExecution={onSelectExecution}
      />

      <Card className="p-4">
        <h2 className="font-display text-2xl font-bold tracking-tight text-foreground">Turn {turnIndex}</h2>
        <p className="mt-1 text-xs text-muted-foreground">Select a context band to inspect its recorded content.</p>

        {isLoading ? (
          <Skeleton className="mt-4 h-20 w-full" />
        ) : recordedBlocks.length ? (
          <div className="mt-4 flex flex-wrap gap-2">
            {recordedBlocks.map((block) => (
              <button
                key={block.key}
                type="button"
                data-testid={`context-band-${block.key}`}
                onClick={() => setSelectedBlockKey(block.key)}
                className="rounded border border-border bg-interactive-secondary px-2.5 py-1 text-xs text-foreground hover:bg-primary hover:text-primary-foreground"
              >
                {block.label}
              </button>
            ))}
          </div>
        ) : (
          <p className="mt-4 text-xs text-tertiary-foreground">No context bands were recorded for this turn.</p>
        )}
      </Card>

      <Card className="p-4" data-testid="context-content">
        {selectedBlock ? (
          <pre className="whitespace-pre-wrap break-words text-xs font-mono text-foreground">
            {selectedContent}
          </pre>
        ) : (
          <ContextInspector sessionId={sessionId} turnIndex={turnIndex} data={data} />
        )}
      </Card>
    </div>
  )
}
