import { cn } from '../../lib/utils.js'
import { SkillsPanel } from '../SkillsPanel.js'
import { SectionLoading } from './SectionLoading.js'
import { SectionError } from './SectionError.js'
import { useSectionPage } from './useSectionPage.js'
import { fetchKyberSessionSkills, type KyberSkillsBlock } from '../../lib/kyberApi.js'
import type { OpenInspector, SessionSummaryPayload } from './types.js'

/**
 * The session's skill rows, one page at a time. A measured page renders
 * through the existing skills panel; a whole-block not-measurable verdict
 * renders as such. A skill row click opens the inspector with the lossless
 * turn content of the first turn that listed the skill.
 */
export interface SessionSkillsSectionProps {
  sessionId: string
  /** The summary record, whose served skill counts the panel's tiles read. */
  summaryRecord?: SessionSummaryPayload | null
  openInspector: OpenInspector
  className?: string
}

export function SessionSkillsSection({
  sessionId,
  summaryRecord,
  openInspector,
  className,
}: SessionSkillsSectionProps) {
  const skills = useSectionPage(sessionId, 'skills', fetchKyberSessionSkills)
  const data = skills.data

  if (skills.failure) {
    return (
      <SectionError
        testId="section-error-skills"
        label="Skills"
        message={skills.failure}
        onRetry={skills.refetch}
        className={className}
      />
    )
  }

  if (data === undefined) {
    return <SectionLoading testId="session-skills-loading" label="skills" />
  }

  // The panel reads the block shape the payload served; the section route
  // answers the same verdicts with its own paging window, so the measured page
  // narrows to the panel's measured variant and a not-measurable page to its
  // availability one.
  const block: KyberSkillsBlock = 'availability' in data
    ? { availability: data.availability, reason: data.reason }
    : { measurable: true, rows: data.rows }

  const openSkillInspector = (skill: { name: string; turns_listed: number[]; invocations: number | { availability: string } }) => {
    // Skill drawer: the listing text comes from the first turn that listed the
    // skill (the served 0-based index). A skill no turn lists has no request.
    const firstListedTurn = skill.turns_listed[0]
    openInspector({
      title: `Skill: ${skill.name}`,
      subtitle: `Listed turns: ${skill.turns_listed.length} · Invocations: ${
        typeof skill.invocations === 'number' ? skill.invocations : 'not measurable'
      }`,
      content: { skill },
      contentRequest:
        typeof firstListedTurn === 'number'
          ? { sessionId, turnIndex: firstListedTurn, full: true }
          : undefined,
    })
  }

  // The session's model names a stand-in tokenizer's family only when the session
  // ran exactly one model; with several, or none, the panel keeps its generic wording.
  const models = summaryRecord?.models
  const model = models?.length === 1 ? models[0] : undefined

  return (
    <div className={cn('space-y-3', className)} data-testid="skills-section">
      <div>
        <h2 className="text-xs font-semibold uppercase tracking-wider text-heading">
          4 · Skills
        </h2>
        <p className="text-xs text-muted-foreground mt-0.5">
          Skills the harness listed to the agent, and which of them were invoked. Select a skill for its listing detail.
        </p>
      </div>
      <SkillsPanel
        skills={block}
        summary={summaryRecord ?? undefined}
        model={model}
        onSelectSkill={openSkillInspector}
        pager={{
          page: skills.page,
          total: data.total,
          limit: data.limit,
          loading: skills.isFetching,
          onPageChange: skills.setPage,
        }}
      />
    </div>
  )
}
