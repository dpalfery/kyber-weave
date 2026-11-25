import { useCallback } from 'react'
import { cn } from '../../lib/utils.js'
import { SessionSpendCharts, type ContextCompositionData, type TurnSpendItem } from '../SessionSpendCharts.js'
import { SectionLoading } from './SectionLoading.js'
import { SectionError } from './SectionError.js'
import { useSectionPage } from './useSectionPage.js'
import { fetchKyberSessionCharts } from '../../lib/kyberApi.js'
import type { OpenInspector } from './types.js'

/**
 * The session's per-turn spend and context composition, from the bounded charts
 * section. The charts are the existing views, fed the turns and context the
 * route answered; the drawer is opened with keys only (D9): a turn index, or a
 * turn index plus a bucket name. The drawer fetches the turn — widened with
 * that turn's context — and derives figures and content requests itself.
 */
export interface SessionChartsSectionProps {
  sessionId: string
  openInspector: OpenInspector
  className?: string
}

export function SessionChartsSection({ sessionId, openInspector, className }: SessionChartsSectionProps) {
  const charts = useSectionPage(sessionId, 'charts', fetchKyberSessionCharts)
  const data = charts.data

  const openTurnInspector = useCallback(
    (turnIndex: number, bucketName?: string) => {
      // Issue #184: transport `turnIndex` is 0-based; humans see 1-based.
      if (bucketName) {
        openInspector({
          title: `Turn ${turnIndex + 1} · ${bucketName}`,
          content: { turnIndex, bucket: bucketName },
          contentRequest: { sessionId, turnIndex, bucket: bucketName },
        })
      } else {
        openInspector({
          title: `Turn ${turnIndex + 1} · Full context`,
          content: { turnIndex },
          contentRequest: { sessionId, turnIndex, full: true },
        })
      }
    },
    [sessionId, openInspector],
  )

  // A faulted projection is an error state, not a chart: the payload names the
  // fault, so nothing here may render as an honest-looking zero.
  if (charts.failure) {
    return (
      <SectionError
        testId="section-error-charts"
        label="Spend charts"
        message={charts.failure}
        onRetry={charts.refetch}
        className={className}
      />
    )
  }

  if (data === undefined) {
    return <SectionLoading testId="session-charts-loading" label="spend charts" />
  }

  return (
    <div className={cn('space-y-2', className)} data-testid="spend-composition-section">
      <SessionSpendCharts
        turns={data.turns as unknown as TurnSpendItem[]}
        context={data.context as unknown as ContextCompositionData}
        contextPager={{ ...charts.pagerProps, ariaLabel: 'Context composition pages' }}
        onSelectTurn={(turnIndex, bucket) => openTurnInspector(turnIndex, bucket)}
      />
    </div>
  )
}
