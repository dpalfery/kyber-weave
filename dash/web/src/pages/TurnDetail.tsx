import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import {
  fetchKyberSessionTurns,
  fetchRun,
  fetchTurnContent,
  type KyberSessionTurnRow,
  type TurnTokenFigures,
} from '../lib/kyberApi.js'
import { cn } from '../lib/utils.js'
import { Card } from '../components/ui/card.js'
import { Skeleton } from '../components/ui/skeleton.js'
import { ContextInspector } from '../components/ContextInspector.js'
import { ContextReviewPanel, HierarchyBreadcrumb } from '../components/analysis/index.js'

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
/**
 * The session turn row whose measured counters describe a 0-based `turnIndex`
 * (issue #184). Mirrors the server resolver's strict contract: the payload's
 * 0-based `index`, the positional fallback, or a legacy 1-based `turn` row
 * matched as `turn - 1`. Anything else is no row — never a neighbor.
 */
export function findSessionTurnRow(
  turns: readonly KyberSessionTurnRow[] | undefined,
  turnIndex: number,
): KyberSessionTurnRow | undefined {
  return turns?.find((t, i) => {
    const legacyTurn: unknown = t.turn
    return (
      t.index === turnIndex ||
      i === turnIndex ||
      (typeof legacyTurn === 'number' && legacyTurn - 1 === turnIndex)
    )
  })
}

/** Coerce a served turn-row counter to a measured figure or null (issue #184, Q3). */
export function asMeasuredFigure(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

export function turnRowFigures(row: KyberSessionTurnRow | undefined): TurnTokenFigures | undefined {
  if (!row) return undefined
  return {
    input: asMeasuredFigure(row.input),
    output: asMeasuredFigure(row.output),
    fresh: asMeasuredFigure(row.fresh),
    cacheRead: asMeasuredFigure(row.cache_read),
  }
}

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
  const { data, isLoading, isError } = useQuery({
    queryKey: ['kyber-turn-content', sessionId, turnIndex],
    queryFn: () => fetchTurnContent(sessionId!, turnIndex),
    enabled: !!sessionId,
    retry: false,
  })
  // Measured per-turn counters for the inspector header (issue #184, Q3).
  const { data: sessionTurns } = useQuery({
    queryKey: ['kyber-session-turns', sessionId],
    queryFn: () => fetchKyberSessionTurns(sessionId!),
    enabled: !!sessionId,
  })
  const turnTokens = turnRowFigures(findSessionTurnRow(sessionTurns, turnIndex))

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
        {/* Issue #184: transport is 0-based `turnIndex`; humans see 1-based. */}
        <h2 className="font-display text-density-display font-bold tracking-density text-foreground">Turn {turnIndex + 1}</h2>
        <p className="mt-density-hair text-density-xs text-muted-foreground leading-density">Select a context band to inspect its recorded content.</p>

        {isLoading ? (
          <Skeleton className="mt-density-stack h-20 w-full" />
        ) : isError ? (
          <p className="mt-density-stack text-density-xs text-tertiary-foreground" data-testid="turn-not-found">
            No such turn in this session (session {sessionId ?? 'unknown'} · turn {turnIndex}).
          </p>
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
          turnTokens={turnTokens}
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
