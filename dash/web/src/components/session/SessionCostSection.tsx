import { SessionCostPanel, type SessionCostSource } from '../SessionCostPanel.js'
import { SectionLoading } from './SectionLoading.js'
import { SectionError } from './SectionError.js'
import type { KyberSessionSummarySection } from '../../lib/kyberApi.js'
import type { SessionSummaryPayload } from './types.js'

/**
 * The session's cost and token accounting, fed by the summary the loader
 * read — the same figures the header and overview tiles render, so no second
 * request exists to wait on. The panel renders the cost blocks the server
 * sent and the basis-mismatch verdict the engine already decided; this view
 * never computes either (display layer, ADR 0033).
 */
export interface SessionCostSectionProps {
  summary: KyberSessionSummarySection | null
  summaryRecord?: SessionSummaryPayload | null
  loading: boolean
  error?: string | null
  onRetry?: () => void
  className?: string
}

export function SessionCostSection({
  summary,
  summaryRecord,
  loading,
  error,
  onRetry,
  className,
}: SessionCostSectionProps) {
  if (error) {
    return (
      <SectionError
        testId="section-error-cost"
        label="Cost accounting"
        message={error}
        onRetry={onRetry}
        className={className}
      />
    )
  }

  if (loading || summary === null) {
    return <SectionLoading testId="session-cost-loading" label="cost accounting" />
  }

  const source: SessionCostSource = {
    summary: summaryRecord ?? undefined,
    costBasisMismatch: summary.costBasisMismatch,
  }

  return <SessionCostPanel session={source} className={className} />
}
