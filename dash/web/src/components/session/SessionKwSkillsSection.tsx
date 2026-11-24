import { cn } from '../../lib/utils.js'
import { Pager } from '../ui/Pager.js'
import { SectionLoading } from './SectionLoading.js'
import { SectionError } from './SectionError.js'
import { useSectionPage } from './useSectionPage.js'
import { fetchKyberSessionKwSkills } from '../../lib/kyberApi.js'

/**
 * The session's KW skill loads, one page at a time, under their own heading
 * apart from the Skills section. A load whose skill name was not recorded says
 * so; no column is ever filled with a placeholder value.
 */
export interface SessionKwSkillsSectionProps {
  sessionId: string
  className?: string
}

export function SessionKwSkillsSection({ sessionId, className }: SessionKwSkillsSectionProps) {
  const kwSkills = useSectionPage(sessionId, 'kw-skills', fetchKyberSessionKwSkills)
  const data = kwSkills.data

  if (kwSkills.failure) {
    return (
      <SectionError
        testId="section-error-kw-skills"
        label="KW skills"
        message={kwSkills.failure}
        onRetry={kwSkills.refetch}
        className={className}
      />
    )
  }

  if (data === undefined) {
    return <SectionLoading testId="session-kw-skills-loading" label="KW skills" />
  }

  const rows = data.rows

  return (
    <div data-testid="kw-skills-section" className={cn('space-y-3', className)}>
      <div>
        <h2 className="text-xs font-semibold uppercase tracking-wider text-heading">KW skills</h2>
        <p className="text-xs text-muted-foreground mt-0.5">
          Skills loaded on demand by the KW agent in this session.
        </p>
      </div>
      {data.total === 0 || rows.length === 0 ? (
        <p data-testid="kw-skills-empty" className="text-xs text-tertiary-foreground">
          No KW skills were loaded in this session.
        </p>
      ) : (
        <>
          <div className="overflow-x-auto">
            <table className="w-full text-xs text-left">
              <thead className="text-tertiary-foreground">
                <tr>
                  <th scope="col" className="px-2 py-1 font-semibold">Skill</th>
                  <th scope="col" className="px-2 py-1 font-semibold">Agent</th>
                  <th scope="col" className="px-2 py-1 font-semibold">Session</th>
                  <th scope="col" className="px-2 py-1 font-semibold">Time</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((row, i) => {
                  const position = data.offset + i + 1
                  return (
                    <tr
                      key={`${row.span_id}-${position}`}
                      data-testid={`kw-skill-row-${position}`}
                      className="border-t border-border"
                    >
                      <td className="px-2 py-1 font-mono text-foreground">
                        {row.skill || <em className="text-tertiary-foreground">not recorded</em>}
                      </td>
                      <td className="px-2 py-1 text-foreground">
                        {row.agent || <em className="text-tertiary-foreground">not recorded</em>}
                      </td>
                      <td className="px-2 py-1 font-mono text-tertiary-foreground">{row.session_id}</td>
                      <td className="px-2 py-1 font-mono text-tertiary-foreground">
                        {row.loaded_at || <em>not recorded</em>}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
          <Pager
            page={kwSkills.page}
            limit={data.limit}
            total={data.total}
            onPageChange={kwSkills.setPage}
            loading={kwSkills.isFetching}
            ariaLabel="KW skill pages"
          />
        </>
      )}
    </div>
  )
}
