import { useCallback } from 'react'
import { useQuery } from '@tanstack/react-query'
import { cn, fmtTokens } from '../../lib/utils.js'
import { formatDuration, formatModelLabel } from '../../lib/labels.js'
import { SessionSpendCharts, CONTEXT_BUCKET_LABELS, type ContextCompositionData, type TurnSpendItem } from '../SessionSpendCharts.js'
import { SectionLoading } from './SectionLoading.js'
import { SectionError } from './SectionError.js'
import { sectionErrorMessage } from './useSectionPage.js'
import {
  findContextTurn,
  findTurnByTransport,
  fetchKyberSessionCharts,
  kyberSessionSectionQueryKey,
  type KyberContextBucket,
  type KyberContextTurn,
  type KyberSessionContext,
} from '../../lib/kyberApi.js'
import type { DrawerContent, OpenInspector } from './types.js'

/**
 * The session's per-turn spend and context composition, from the bounded charts
 * section. The charts are the existing views, fed the turns and context the
 * route answered; the drawer requests a turn or one of its buckets opens are
 * this section's own to compute, from the data it holds.
 */
export interface SessionChartsSectionProps {
  sessionId: string
  openInspector: OpenInspector
  className?: string
}

export function SessionChartsSection({ sessionId, openInspector, className }: SessionChartsSectionProps) {
  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: kyberSessionSectionQueryKey(sessionId, 'charts'),
    queryFn: () => fetchKyberSessionCharts(sessionId),
  })

  const openTurnInspector = useCallback(
    (turnIndex: number, bucketName?: string) => {
      const turns = data?.turns || []
      // Shared strict lookup (issue #184 review): explicit identity first,
      // position only for rows carrying neither.
      const turn = findTurnByTransport(turns, turnIndex)
      if (!turn) return

      if (bucketName) {
        const ctx = data?.context
        const contextTurns: KyberContextTurn[] = ctx?.measurable ? ctx.turns : []
        // Context rows carry the engine's 1-based numbering (`TurnPressure.index`,
        // legacy `turn`); the transport `turnIndex` is 0-based, so compare in
        // 1-based space with the shared strict lookup.
        const oneBased = turnIndex + 1
        // Edge fallbacks compare in 1-based space (issue #184 review): the
        // first turn reads `ctx.first`, the final turn `ctx.last`. Any other
        // turn without a context row falls back to its own buckets below —
        // never to the opposite edge of the session.
        const ctxTurn: KyberContextTurn | KyberContextBucket | undefined =
          findContextTurn(contextTurns, oneBased) ||
          (oneBased === 1 ? ctx?.first : undefined) ||
          (oneBased === turns.length ? ctx?.last : undefined)

        const rawBuckets = ctxTurn?.buckets ?? turn.buckets ?? {}
        const tokens = Number(rawBuckets[bucketName] ?? turn[bucketName] ?? 0)
        const bucketSum = Object.values(rawBuckets).reduce(
          (sum: number, v) => sum + (typeof v === 'number' ? v : 0),
          0,
        )
        const reported = Number(
          ctxTurn?.reported_input ??
            turn.cumulative_input ??
            turn.input ??
            turn.fresh ??
            bucketSum ??
            0,
        )
        const total = Math.max(reported, bucketSum)
        const label =
          (CONTEXT_BUCKET_LABELS as Record<string, string>)[bucketName] || bucketName
        const content =
          turn.content && typeof turn.content === 'object' && bucketName in turn.content
            ? turn.content[bucketName]
            : (turn[bucketName] ?? (typeof turn.content === 'string' ? turn.content : turn))

        // Issue #184: transport `turnIndex` is 0-based; humans see 1-based.
        const title = `Turn ${turnIndex + 1} · ${bucketName}`
        const subtitle = `Bucket analysis · ${turn.model ? `Model: ${formatModelLabel(turn.model)} · ` : ''}${fmtTokens(total || turn.cumulative_input || turn.input)} total tokens`
        // The session-level system prompt lives on the session content route,
        // not on any single turn span (the first turn span carries none, so a
        // span-scoped request 404s). Omit the span so the request reads
        // `/content?part=system_prompt`; every other bucket keeps its turn span.
        const contentRequest = bucketName === 'system_prompt'
          ? { sessionId, part: bucketName }
          : { sessionId, span: turn.spanId, part: bucketName }
        const drawerContent: DrawerContent = {
          turnIndex,
          bucket: bucketName,
          key: bucketName,
          tokens,
          value: tokens,
          total,
          label,
          content,
          turn,
          context: ctx as KyberSessionContext,
        }
        openInspector({ title, subtitle, content: drawerContent, contentRequest })
      } else {
        // Whole-turn view: a plain turn selection opens the complete
        // available context for that turn via the turn-content endpoint
        // (lossless `full` path), not just the payload row or one bucket.
        // Issue #184: transport `turnIndex` is 0-based; humans see 1-based.
        const title = `Turn ${turnIndex + 1} · Full context`
        const subtitle = `${turn.model ? `Model: ${formatModelLabel(turn.model)} · ` : ''}${formatDuration(turn.durationMs)}`
        const drawerContent: DrawerContent = { turnIndex, turn }
        openInspector({ title, subtitle, content: drawerContent, contentRequest: { sessionId, turnIndex, full: true } })
      }
    },
    [data, sessionId, openInspector],
  )

  if (isError) {
    return (
      <SectionError
        testId="section-error-charts"
        label="Spend charts"
        message={sectionErrorMessage(error)}
        onRetry={() => void refetch()}
        className={className}
      />
    )
  }

  if (isPending || data === undefined) {
    return <SectionLoading testId="session-charts-loading" label="spend charts" />
  }

  return (
    <div className={cn('space-y-2', className)} data-testid="spend-composition-section">
      <SessionSpendCharts
        turns={data.turns as unknown as TurnSpendItem[]}
        context={data.context as unknown as ContextCompositionData}
        onSelectTurn={(turnIndex, bucket) => openTurnInspector(turnIndex, bucket)}
      />
    </div>
  )
}
