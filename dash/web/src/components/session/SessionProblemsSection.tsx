import { cn } from '../../lib/utils.js'
import { Pager } from '../ui/Pager.js'
import { SectionLoading } from './SectionLoading.js'
import { SectionError } from './SectionError.js'
import { useSectionPage } from './useSectionPage.js'
import { fetchKyberSessionProblems } from '../../lib/kyberApi.js'

/**
 * The session's stored ingest problems, one page at a time. An empty page
 * states that no problem was stored — never that the session was flawless
 * beyond what the store recorded.
 */
export interface SessionProblemsSectionProps {
  sessionId: string
  className?: string
}

const SEVERITY_CLASS: Record<string, string> = {
  error: 'bg-red-500/15 text-red-600 dark:text-red-400',
  warning: 'bg-amber-500/15 text-amber-700 dark:text-amber-300',
  info: 'bg-interactive-secondary text-muted-foreground',
}

export function SessionProblemsSection({ sessionId, className }: SessionProblemsSectionProps) {
  const problems = useSectionPage(sessionId, 'problems', fetchKyberSessionProblems)
  const data = problems.data

  if (problems.failure) {
    return (
      <SectionError
        testId="section-error-problems"
        label="Stored ingest problems"
        message={problems.failure}
        onRetry={problems.refetch}
        className={className}
      />
    )
  }

  if (data === undefined) {
    return <SectionLoading testId="session-problems-loading" label="stored ingest problems" />
  }

  const rows = data.entries

  return (
    <div className={cn('space-y-2', className)} data-testid="problems-section">
      <div className="flex flex-wrap items-baseline gap-2">
        <h2 className="text-xs font-semibold uppercase tracking-wider text-heading">Stored Ingest Problems</h2>
        <span className="text-[11px] text-tertiary-foreground">
          {data.total === 0 ? 'none stored' : `${data.total} problem${data.total === 1 ? '' : 's'} the ingest recorded`}
        </span>
      </div>

      {data.total === 0 || rows.length === 0 ? (
        <p data-testid="problems-empty" className="text-xs text-tertiary-foreground">
          No stored ingest problems for this session.
        </p>
      ) : (
        <div className="overflow-x-auto rounded-md border border-border">
          <table className="w-full text-left text-xs">
            <thead>
              <tr className="border-b border-border bg-interactive-secondary/40 text-[10.5px] font-semibold uppercase tracking-wider text-tertiary-foreground">
                <th className="px-2.5 py-1.5">severity</th>
                <th className="px-2.5 py-1.5 font-mono normal-case tracking-normal">code</th>
                <th className="px-2.5 py-1.5">message</th>
                <th className="px-2.5 py-1.5 font-mono normal-case tracking-normal">location</th>
                <th className="px-2.5 py-1.5 font-mono normal-case tracking-normal">timestamp</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border/40">
              {rows.map((row) => (
                <tr key={row.id} data-testid={`problem-row-${row.id}`}>
                  <td className="px-2.5 py-1.5">
                    <span
                      data-testid={`problem-severity-${row.id}`}
                      className={cn(
                        'rounded px-1.5 py-0.5 text-[10px] font-semibold uppercase',
                        SEVERITY_CLASS[row.severity] ?? SEVERITY_CLASS.info,
                      )}
                    >
                      {row.severity}
                    </span>
                  </td>
                  <td className="px-2.5 py-1.5 font-mono text-foreground">{row.code}</td>
                  <td className="px-2.5 py-1.5 text-foreground">{row.message}</td>
                  <td className="px-2.5 py-1.5 font-mono text-tertiary-foreground">{row.location ?? '—'}</td>
                  <td className="px-2.5 py-1.5 font-mono text-tertiary-foreground">{row.timestamp ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <Pager
            {...problems.pagerProps}
            ariaLabel="Stored problem pages"
          />
        </div>
      )}
    </div>
  )
}
