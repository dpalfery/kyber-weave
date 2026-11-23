import { cn } from '../../lib/utils.js'
import { Pager } from '../ui/Pager.js'
import { SectionLoading } from './SectionLoading.js'
import { SectionError } from './SectionError.js'
import { sectionErrorMessage, useSectionPage } from './useSectionPage.js'
import { fetchKyberSessionRequests } from '../../lib/kyberApi.js'

/**
 * The session's user requests, one page at a time. The headline count is the
 * window's total, not the rows on screen; each row is labelled by its own
 * prompt preview, or by its position in the whole list when it carried none —
 * a span id names a request and is never its label.
 */
export interface SessionRequestsSectionProps {
  sessionId: string
  className?: string
}

export function SessionRequestsSection({ sessionId, className }: SessionRequestsSectionProps) {
  const requests = useSectionPage(sessionId, 'requests', fetchKyberSessionRequests)
  const data = requests.data

  if (requests.isError && data === undefined) {
    return (
      <SectionError
        testId="section-error-requests"
        label="User requests"
        message={sectionErrorMessage(requests.error)}
        onRetry={requests.refetch}
        className={className}
      />
    )
  }

  if (data === undefined) {
    return <SectionLoading testId="session-requests-loading" label="user requests" />
  }

  const rows = data.entries

  return (
    <div
      data-testid="user-requests"
      className={cn('rounded-md border border-border bg-card/60 px-3.5 py-2 text-xs text-muted-foreground', className)}
    >
      {data.total === 0 || rows.length === 0 ? (
        <span data-testid="requests-empty">No user requests recorded for this session.</span>
      ) : (
        <>
          <span className="font-semibold text-foreground mr-1.5">
            {data.total} user request{data.total === 1 ? '' : 's'} in this session:
          </span>
          <div className="mt-1 flex flex-wrap gap-x-3 gap-y-1">
            {rows.map((row, i) => {
              const position = (data.offset ?? 0) + i + 1
              return (
                <span key={row.request} className="inline-flex items-center gap-1" data-testid={`request-row-${position}`}>
                  <span className="font-mono text-tertiary-foreground">{position}.</span>
                  <span className="text-foreground max-w-xs truncate" title={row.request}>
                    {typeof row.prompt === 'string' && row.prompt ? row.prompt : `Request ${position}`}
                  </span>
                  {row.turns != null && (
                    <em className="text-tertiary-foreground text-[11px]">({row.turns} turns)</em>
                  )}
                </span>
              )
            })}
          </div>
          <Pager
            page={requests.page}
            limit={data.limit}
            total={data.total}
            onPageChange={requests.setPage}
            loading={requests.isFetching}
            ariaLabel="User request pages"
          />
        </>
      )}
    </div>
  )
}
