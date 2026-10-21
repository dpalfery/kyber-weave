import { cn, fmtNum, fmtTokens } from '../lib/utils.js'
import type {
  KyberSkillAvailability,
  KyberSkillRow,
  KyberSkillsBlock,
  KyberSkillsSummary,
} from '../lib/kyberApi.js'

/**
 * Section 4 · Skills. The engine decides what was listed and invoked; this
 * panel only lays the payload out. Every absent or not-measurable value is
 * shown as such, never as zero or as an empty list.
 */
export type SkillsPanelProps = {
  /** The payload's `skills` block. Absent on a payload that predates the projection. */
  skills?: KyberSkillsBlock | null
  /** The session summary; `skills_listed` / `skills_invoked` are absent when not measured. */
  summary?: KyberSkillsSummary | null
  /** Opens the skill drawer with the payload row. */
  onSelectSkill?: (skill: KyberSkillRow) => void
  className?: string
}

const BADGE = 'shrink-0 rounded border px-1.5 py-0.5 text-[10px] font-semibold'
const BADGE_OK = `${BADGE} border-emerald-500/30 bg-emerald-500/15 text-emerald-700 dark:text-emerald-300`
const BADGE_WARN = `${BADGE} border-amber-500/30 bg-amber-500/15 text-amber-700 dark:text-amber-300`
const BADGE_MUTED = `${BADGE} border-border text-muted-foreground`

/** True when a served value is a not-measurable verdict rather than a measured value. */
export function unmeasured(value: unknown): value is KyberSkillAvailability {
  return (
    typeof value === 'object' &&
    value !== null &&
    !Array.isArray(value) &&
    (value as { availability?: unknown }).availability === 'not_measurable'
  )
}

/** The harness's own reason, shown verbatim; a fixed fallback only when the reason is missing. */
export function reasonOf(value: KyberSkillAvailability): string {
  return typeof value.reason === 'string' && value.reason.length > 0 ? value.reason : 'not exported by this harness'
}

/** How many turns listed the skill; the turn numbers themselves are not shown. */
export function skillTurnLabel(turnsListed: number[]): string {
  return turnsListed.length === 1 ? 'Listed in 1 turn' : `Listed in ${fmtNum(turnsListed.length)} turns`
}

function tile(testId: string, label: string, count: number | undefined, detail: string) {
  return (
    <div data-testid={testId} className="rounded-md border border-border bg-card px-3 py-2.5">
      <div className="text-[11px] uppercase tracking-wider text-tertiary-foreground">{label}</div>{' '}
      {count === undefined ? (
        <div className="mt-1 text-sm font-semibold text-tertiary-foreground">not measurable</div>
      ) : (
        <div className="mt-1 text-xl font-semibold tabular-nums tracking-tight text-foreground">{fmtNum(count)}</div>
      )}{' '}
      <div className="mt-0.5 text-[11px] text-tertiary-foreground">{detail}</div>
    </div>
  )
}

function renderRow(row: KyberSkillRow, maxTokens: number, onSelectSkill?: (skill: KyberSkillRow) => void) {
  const listedUnmeasured = unmeasured(row.listed)
  const listed = row.listed === true
  const invocationsUnmeasured = unmeasured(row.invocations)
  const invocations = typeof row.invocations === 'number' ? row.invocations : undefined
  const listedButNeverInvoked = listed && invocations === 0
  const select = () => onSelectSkill?.(row)

  return (
    <div
      key={row.name}
      data-testid={`skill-row-${row.name}`}
      role="button"
      tabIndex={0}
      onClick={select}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault()
          select()
        }
      }}
      className={cn(
        'cursor-pointer rounded-md border p-2.5 transition-colors focus:outline-hidden focus-visible:ring-1 focus-visible:ring-ring',
        listedButNeverInvoked
          ? 'border-amber-500/40 bg-amber-500/10 hover:bg-amber-500/15'
          : 'border-border hover:bg-interactive-secondary/50',
      )}
    >
      <div className="flex items-start justify-between gap-3 text-xs">
        <div className="flex min-w-0 flex-wrap items-center gap-2">
          <span className="truncate font-semibold text-foreground">{row.name}</span>
          {listed && <span className={BADGE_OK}>listed</span>}
          {row.listed === false && <span className={BADGE_MUTED}>not listed</span>}
          {listedUnmeasured && <span className={BADGE_WARN}>listing not measurable</span>}
          {invocations !== undefined && invocations > 0 && (
            <span className={BADGE_OK}>invoked · {fmtNum(invocations)}</span>
          )}
          {listedButNeverInvoked && <span className={BADGE_WARN}>never invoked</span>}
          {invocationsUnmeasured && <span className={BADGE_WARN}>invocations not measurable</span>}
        </div>
        {typeof row.derived_tokens === 'number' && (
          <span className="shrink-0 font-semibold tabular-nums text-foreground">
            {fmtTokens(row.derived_tokens)}{' '}
            <span className="font-normal text-tertiary-foreground">derived tokens</span>
          </span>
        )}
      </div>

      {typeof row.derived_tokens === 'number' && maxTokens > 0 && (
        <div
          data-testid={`skill-token-bar-${row.name}`}
          className="mt-1.5 h-1.5 w-full overflow-hidden rounded-full bg-muted"
        >
          <div
            className="h-full rounded-full bg-emerald-500/70"
            style={{ width: `${Math.max(2, Math.round((row.derived_tokens / maxTokens) * 100))}%` }}
          />
        </div>
      )}
      {typeof row.description === 'string' && row.description.length > 0 && (
        <p data-testid={`skill-description-${row.name}`} className="mt-1.5 line-clamp-3 text-[11px] text-muted-foreground">
          {row.description}
        </p>
      )}
      {unmeasured(row.listed) && (
        <p className="mt-1.5 text-[11px] text-amber-700 dark:text-amber-300">{reasonOf(row.listed)}</p>
      )}
      {unmeasured(row.invocations) && (
        <p className="mt-1.5 text-[11px] text-amber-700 dark:text-amber-300">{reasonOf(row.invocations)}</p>
      )}
    </div>
  )
}

export function SkillsPanel({ skills, summary, onSelectSkill, className }: SkillsPanelProps) {
  const rootClass = cn('rounded-lg border border-border bg-card p-4 text-card-foreground shadow-xs', className)

  if (skills === undefined || skills === null) {
    return (
      <div data-testid="skills-panel" className={rootClass}>
        <div data-testid="skills-not-projected" className="text-xs text-tertiary-foreground">
          Skills are not yet projected for this session. The payload predates the skills listing, so this is not an
          empty result.
        </div>
      </div>
    )
  }

  if (unmeasured(skills)) {
    return (
      <div data-testid="skills-panel" className={rootClass}>
        <div data-testid="skills-not-measurable" className="rounded-md border border-border bg-card p-4">
          <div className="flex items-center gap-2">
            <span className="rounded-sm bg-amber-500/10 px-2 py-0.5 text-xs font-semibold text-amber-600 dark:text-amber-400">
              Not Measurable
            </span>
            <h3 className="text-sm font-semibold text-foreground">Skills unavailable</h3>
          </div>
          <p className="mt-2 text-xs text-muted-foreground">{reasonOf(skills)}</p>
          <p className="mt-1 text-xs text-tertiary-foreground">
            An empty list would read as “nothing was listed”. That is a different fact from not being able to tell.
          </p>
        </div>
      </div>
    )
  }

  const rows = skills.rows ?? []
  const maxTokens = Math.max(0, ...rows.map((row) => (typeof row.derived_tokens === 'number' ? row.derived_tokens : 0)))

  const listedCount = Array.isArray(summary?.skills_listed) ? summary.skills_listed.length : undefined
  const invokedCount = Array.isArray(summary?.skills_invoked) ? summary.skills_invoked.length : undefined

  // The headline reads the served pair only. The browser never counts rows: if
  // either served count is absent, the figure is stated as not measurable.
  const uninvokedServed = skills.listed_uninvoked_count
  const listedServed = skills.listed_count
  const headline =
    typeof uninvokedServed === 'number' && typeof listedServed === 'number'
      ? `${uninvokedServed} of ${listedServed} listed skills had no observed invocation`
      : 'The number of listed skills with no observed invocation is not measurable.'

  return (
    <div data-testid="skills-panel" className={cn(rootClass, 'space-y-4')}>
      <div className="grid grid-cols-2 gap-3">
        {tile('skills-tile-listed', 'Skills listed', listedCount, 'listed to the agent this session')}
        {tile('skills-tile-invoked', 'Skills invoked', invokedCount, 'called at least once this session')}
      </div>

      <div className="border-b border-border pb-3">
        {rows.length > 0 && (
          <p data-testid="skills-headline" className="text-sm font-medium text-foreground">
            {headline}
          </p>
        )}
        {skills.invocationsCoverage !== undefined && (
          <p data-testid="skills-invocations-coverage" className="mt-1 text-xs text-muted-foreground">
            Invocations measured on {skills.invocationsCoverage.measuredTurns} of {skills.invocationsCoverage.totalTurns}{' '}
            turns
          </p>
        )}
        {typeof skills.reported_skill_tokens === 'number' && (
          <p data-testid="skills-reported-tokens" className="mt-1 text-xs text-muted-foreground">
            Reported by the harness:{' '}
            <span className="font-semibold tabular-nums text-foreground">{fmtTokens(skills.reported_skill_tokens)}</span>{' '}
            skill tokens
          </p>
        )}
      </div>

      <div className="space-y-2">
        {rows.length === 0 ? (
          <p className="py-4 text-center text-sm text-muted-foreground">No skill was recorded for this session.</p>
        ) : (
          rows.map((row) => renderRow(row, maxTokens, onSelectSkill))
        )}
      </div>

      <p className="text-[11px] text-tertiary-foreground">
        Derived tokens are estimated from each skill&apos;s listing; the reported total is the harness&apos;s own figure.
      </p>
    </div>
  )
}
