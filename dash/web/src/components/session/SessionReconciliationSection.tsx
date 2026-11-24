import { cn, fmtTokens } from '../../lib/utils.js'
import { Pager } from '../ui/Pager.js'
import { SectionLoading } from './SectionLoading.js'
import { SectionError } from './SectionError.js'
import { useSectionPage } from './useSectionPage.js'
import { fetchKyberSessionReconciliation } from '../../lib/kyberApi.js'

/**
 * The session's measurable reconciliation rows, one page at a time: each
 * request's invoke_agent root against the sum of its chat turns. The summary's
 * badge carries the counts; this list carries the rows, and a mismatch is
 * marked as such — a measured match, a measured mismatch and an absent figure
 * never read alike.
 */
export interface SessionReconciliationSectionProps {
  sessionId: string
  className?: string
}

export function SessionReconciliationSection({ sessionId, className }: SessionReconciliationSectionProps) {
  const reconciliation = useSectionPage(sessionId, 'reconciliation', fetchKyberSessionReconciliation)
  const data = reconciliation.data

  if (reconciliation.failure) {
    return (
      <SectionError
        testId="section-error-reconciliation"
        label="Reconciliation"
        message={reconciliation.failure}
        onRetry={reconciliation.refetch}
        className={className}
      />
    )
  }

  if (data === undefined) {
    return <SectionLoading testId="session-reconciliation-loading" label="reconciliation" />
  }

  const rows = data.entries

  return (
    <div className={cn('space-y-2', className)} data-testid="reconciliation-section">
      <div className="flex flex-wrap items-baseline gap-2">
        <h2 className="text-xs font-semibold uppercase tracking-wider text-heading">Reconciliation</h2>
        <span className="text-[11px] text-tertiary-foreground">
          each request's root tokens against the sum of its chat turns
        </span>
      </div>

      {data.total === 0 || rows.length === 0 ? (
        <p data-testid="reconciliation-empty" className="text-xs text-tertiary-foreground">
          No measurable reconciliation rows for this session.
        </p>
      ) : (
        <div className="overflow-x-auto rounded-md border border-border">
          <table className="w-full text-left text-xs">
            <thead>
              <tr className="border-b border-border bg-interactive-secondary/40 text-[10.5px] font-semibold uppercase tracking-wider text-tertiary-foreground">
                <th className="px-2.5 py-1.5 font-mono normal-case tracking-normal">request</th>
                <th className="px-2.5 py-1.5 text-right">root in</th>
                <th className="px-2.5 py-1.5 text-right">chat sum in</th>
                <th className="px-2.5 py-1.5 text-right">input</th>
                <th className="px-2.5 py-1.5 text-right">root out</th>
                <th className="px-2.5 py-1.5 text-right">chat sum out</th>
                <th className="px-2.5 py-1.5 text-right">output</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border/40">
              {rows.map((row, i) => {
                const position = (data.offset ?? 0) + i + 1
                const mismatched = !row.input_match || !row.output_match
                return (
                  <tr
                    key={row.request}
                    data-testid={`reconciliation-row-${position}`}
                    data-mismatch={String(mismatched)}
                    className={mismatched ? 'bg-amber-500/5' : undefined}
                  >
                    <td className="px-2.5 py-1.5 text-foreground">Request {position}</td>
                    <td className="px-2.5 py-1.5 text-right tabular-nums">
                      {row.root_input != null ? fmtTokens(row.root_input) : '—'}
                    </td>
                    <td className="px-2.5 py-1.5 text-right tabular-nums">
                      {row.sum_chat_input != null ? fmtTokens(row.sum_chat_input) : '—'}
                    </td>
                    <td className="px-2.5 py-1.5 text-right">
                      {row.input_match ? (
                        <span className="text-emerald-600 dark:text-emerald-400">✓ match</span>
                      ) : (
                        <span className="font-semibold text-amber-700 dark:text-amber-300">⚠ mismatch</span>
                      )}
                    </td>
                    <td className="px-2.5 py-1.5 text-right tabular-nums">
                      {row.root_output != null ? fmtTokens(row.root_output) : '—'}
                    </td>
                    <td className="px-2.5 py-1.5 text-right tabular-nums">
                      {row.sum_chat_output != null ? fmtTokens(row.sum_chat_output) : '—'}
                    </td>
                    <td className="px-2.5 py-1.5 text-right">
                      {row.output_match ? (
                        <span className="text-emerald-600 dark:text-emerald-400">✓ match</span>
                      ) : (
                        <span className="font-semibold text-amber-700 dark:text-amber-300">⚠ mismatch</span>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
          <Pager
            page={reconciliation.page}
            limit={data.limit}
            total={data.total}
            onPageChange={reconciliation.setPage}
            loading={reconciliation.isFetching}
            ariaLabel="Reconciliation pages"
          />
        </div>
      )}
    </div>
  )
}
