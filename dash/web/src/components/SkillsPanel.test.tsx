// @vitest-environment happy-dom
// The skill rows are real buttons: the suites click and press keys on them
// through the DOM, so this file mounts the panel under happy-dom rather than
// reading a static markup string.
import { describe, it, expect, vi, afterEach } from 'vitest'
import type * as React from 'react'
import { render, screen, fireEvent, cleanup, within } from '@testing-library/react'

import { SkillsPanel } from './SkillsPanel.js'
import fixtures from './fixtures/skills-payload.json'

type SkillsPanelProps = React.ComponentProps<typeof SkillsPanel>

// The fixture is the transport shape; the panel's prop type is the one place the
// JSON literal is narrowed, so every test reads the same contract.
const asSkills = (value: unknown): SkillsPanelProps['skills'] => value as SkillsPanelProps['skills']
const asSummary = (value: unknown): SkillsPanelProps['summary'] => value as SkillsPanelProps['summary']

type Fixture = { summary?: unknown; skills?: unknown; turns?: unknown }
const fixtureOf = (name: keyof typeof fixtures): Fixture => fixtures[name] as Fixture

afterEach(cleanup)

function mount(name: keyof typeof fixtures, onSelectSkill?: (row: unknown) => void) {
  const fixture = fixtureOf(name)
  return render(
    <SkillsPanel
      skills={asSkills(fixture.skills)}
      summary={asSummary(fixture.summary)}
      onSelectSkill={onSelectSkill}
    />,
  )
}

/** The panel's visible wording and markup, for the vocabulary rules. */
const markupOf = (container: HTMLElement): string => container.innerHTML

const rowOf = (name: string): HTMLElement => screen.getByTestId(`skill-row-${name}`)

describe('SkillsPanel: rows from the payload', () => {
  it('renders one row per payload row, in payload order', () => {
    const { container } = mount('realistic')
    const rows = container.querySelectorAll('[data-testid^="skill-row-"]')
    expect(Array.from(rows).map((el) => el.getAttribute('data-testid'))).toEqual([
      'skill-row-docs',
      'skill-row-deploy',
      'skill-row-lint',
      'skill-row-release-notes',
    ])
  })

  it('shows a listed badge and an invoked badge on a listed, invoked row', () => {
    mount('realistic')
    const docs = rowOf('docs')
    expect(docs.textContent).toMatch(/listed/i)
    expect(docs.textContent).not.toMatch(/not listed/i)
    expect(docs.textContent).toMatch(/invoked/i)
  })

  it('marks a row that was invoked but not listed as not listed, with no listed badge', () => {
    mount('realistic')
    const releaseNotes = rowOf('release-notes')
    expect(releaseNotes.textContent).toMatch(/not listed/i)
    expect(releaseNotes.textContent).toMatch(/invoked/i)
  })

  it('does not list turn numbers on a skill row', () => {
    mount('realistic')
    const text = rowOf('docs').textContent ?? ''
    expect(text).not.toMatch(/turns? 1/i)
    expect(text).not.toMatch(/Listed in turn/)
  })

  it('labels a derived token figure as derived on its row', () => {
    mount('realistic')
    const docs = rowOf('docs')
    expect(docs.textContent).toContain('38')
    expect(docs.textContent).toMatch(/derived/i)
  })
})

describe('SkillsPanel: tiles and headline', () => {
  it('renders the skills-listed and skills-invoked tiles from the summary keys', () => {
    mount('realistic')
    const listed = screen.getByTestId('skills-tile-listed')
    const invoked = screen.getByTestId('skills-tile-invoked')
    expect(listed.textContent).toMatch(/\b3\b/)
    expect(invoked.textContent).toMatch(/\b2\b/)
  })

  it('shows the not-measurable state on a tile whose summary key is absent, never zero', () => {
    mount('listedNotMeasurableRow')
    const listed = screen.getByTestId('skills-tile-listed')
    expect(listed.textContent).toMatch(/not measurable/i)
    expect(listed.textContent).not.toMatch(/\b0\b/)
  })

  it('states how many listed skills had no observed invocation', () => {
    const { container } = mount('realistic')
    // docs was invoked; deploy and lint were listed and never invoked. release-notes is not listed.
    expect(container.textContent?.replace(/\s+/g, ' ')).toContain(
      '2 of 3 listed skills had no observed invocation',
    )
  })

  it('states zero of the listed skills had no observed invocation when every listed skill ran', () => {
    const { container } = mount('allInvoked')
    expect(container.textContent?.replace(/\s+/g, ' ')).toContain(
      '0 of 2 listed skills had no observed invocation',
    )
  })

  it('does not count a row whose invocations are not measurable as having no invocation', () => {
    const { container } = mount('invocationsNotMeasurableRow')
    expect(container.textContent?.replace(/\s+/g, ' ')).not.toContain('2 of 2 listed skills')
  })
})

describe('SkillsPanel: headline is read from the served counts, never counted from rows', () => {
  it('states the served counts even when they differ from what counting the rows would give', () => {
    // The rows give 2 of 3 (docs was invoked; deploy and lint were not). The server
    // served 5 of 7. The browser must show what it was served.
    const { container } = mount('servedCountsDifferFromRows')
    const headline = screen.getByTestId('skills-headline')
    expect(headline.textContent).toContain('5 of 7 listed skills had no observed invocation')
    expect(headline.textContent).not.toContain('2 of 3')
    expect(container.textContent?.replace(/\s+/g, ' ')).not.toContain('2 of 3 listed skills')
  })

  it('states the headline from the served pair on the realistic payload', () => {
    mount('realistic')
    expect(screen.getByTestId('skills-headline').textContent).toBe(
      '2 of 3 listed skills had no observed invocation',
    )
  })

  it.each(['listedNotMeasurableRow', 'invocationsNotMeasurableRow', 'onlyListedCountServed'] as const)(
    'says the headline is not measurable, and counts no rows, when a served count is absent (%s)',
    (name) => {
      mount(name)
      const headline = screen.getByTestId('skills-headline')
      expect(headline.textContent).toMatch(/not measurable/i)
      expect(headline.textContent).not.toMatch(/had no observed invocation/i)
      expect(headline.textContent).not.toMatch(/\d+ of \d+/)
    },
  )
})

describe('SkillsPanel: empty rows keep the no-skill line and drop the headline', () => {
  it('hides the headline when the payload is measurable with no rows, and keeps the no-skill line', () => {
    const { container } = mount('noRecordedSkills')
    expect(screen.queryByTestId('skills-headline')).toBeNull()
    expect(container.textContent).toContain('No skill was recorded for this session.')
    expect(container.textContent).not.toMatch(/had no observed invocation/i)
    expect(container.querySelector('[data-testid^="skill-row-"]')).toBeNull()
  })
})

describe('SkillsPanel: reported aggregate and derived labelling', () => {
  it('shows the harness-reported skill token aggregate, labelled as reported', () => {
    const { container } = mount('realistic')
    expect(container.textContent).toMatch(/reported/i)
    expect(container.textContent).toContain('1.0K')
  })

  it('shows no reported aggregate when the harness did not report one', () => {
    const { container } = mount('noReportedTokens')
    expect(container.textContent).not.toContain('1.0K')
    expect(container.textContent).not.toContain('1,040')
  })

  it('labels the panel figures as derived, not as an aggregate the harness reported', () => {
    const { container } = mount('realistic')
    expect(container.textContent).toMatch(/derived/i)
  })
})

describe('SkillsPanel: not-measurable states', () => {
  it('shows the not-measurable card with the reason when the whole skills block is not measurable', () => {
    const { container } = mount('panelNotMeasurable')
    const card = screen.getByTestId('skills-not-measurable')
    expect(card.textContent).toMatch(/not measurable/i)
    expect(card.textContent).toContain('the harness exported no skill listing for this session')
    expect(container.querySelector('[data-testid^="skill-row-"]')).toBeNull()
  })

  it('shows a row-level not-measurable listed value with its reason, never a listed badge or zero', () => {
    mount('listedNotMeasurableRow')
    const docs = rowOf('docs')
    expect(docs.textContent).toMatch(/not measurable/i)
    expect(docs.textContent).toContain('the harness did not export the skill listing for this session')
    expect(docs.textContent).not.toMatch(/not listed/i)
  })

  it('shows a row-level not-measurable invocations value with its reason, never zero invocations', () => {
    mount('invocationsNotMeasurableRow')
    const docs = rowOf('docs')
    expect(docs.textContent).toMatch(/not measurable/i)
    expect(docs.textContent).toContain('invocation events were not exported by this harness')
    expect(docs.textContent).not.toMatch(/never invoked|no observed invocation/i)
  })
})

describe('SkillsPanel: payload without a skills key', () => {
  it('renders not yet projected when the skills prop is absent, not an empty or no-skills state', () => {
    const { container } = mount('legacyNoSkills')
    expect(screen.getByTestId('skills-not-projected').textContent).toMatch(/not yet projected/i)
    expect(container.textContent).not.toMatch(/no skills/i)
    expect(container.querySelector('[data-testid^="skill-row-"]')).toBeNull()
  })
})

describe('SkillsPanel: skill drawer trigger', () => {
  it('calls onSelectSkill with the payload row when a skill row is clicked', () => {
    const onSelectSkill = vi.fn()
    mount('realistic', onSelectSkill)
    fireEvent.click(rowOf('docs'))
    expect(onSelectSkill).toHaveBeenCalledTimes(1)
    expect(onSelectSkill).toHaveBeenCalledWith(expect.objectContaining({ name: 'docs' }))
  })

  it('opens the same row on Enter from the keyboard, as the tool ranking rows do', () => {
    const onSelectSkill = vi.fn()
    mount('realistic', onSelectSkill)
    fireEvent.keyDown(rowOf('deploy'), { key: 'Enter' })
    expect(onSelectSkill).toHaveBeenCalledWith(expect.objectContaining({ name: 'deploy' }))
  })
})

describe('SkillsPanel: display rules', () => {
  const fixtureNames = [
    'realistic',
    'noReportedTokens',
    'allInvoked',
    'listedNotMeasurableRow',
    'invocationsNotMeasurableRow',
    'panelNotMeasurable',
    'legacyNoSkills',
    'servedCountsDifferFromRows',
    'onlyListedCountServed',
    'noRecordedSkills',
  ] as const

  it.each(fixtureNames)('shows no currency symbol or money figure for the %s payload', (name) => {
    const { container, unmount } = mount(name)
    expect(markupOf(container)).not.toMatch(/[$€£¥]|\b(?:USD|EUR|GBP|JPY)\b/)
    unmount()
  })

  it.each(fixtureNames)('uses no waste wording and no removal or deletion phrasing for the %s payload', (name) => {
    const { container, unmount } = mount(name)
    const markup = markupOf(container)
    expect(markup).not.toMatch(/waste/i)
    expect(markup).not.toMatch(/\b(?:remove|removal|removing|delete|deletion|deleting|prune|uninstall)\b/i)
    expect(markup).not.toMatch(/remove[- ]skill/i)
    unmount()
  })

  it('contains every row inside the panel root', () => {
    const { container } = mount('realistic')
    const root = container.querySelector('[data-testid="skills-panel"]')
    expect(root).not.toBeNull()
    expect(within(root as HTMLElement).getByTestId('skill-row-docs')).toBeTruthy()
  })
})

describe('SkillsPanel: Antigravity invocation count beside description and token bar', () => {
  const skills = {
    listed_count: 1,
    listed_uninvoked_count: 0,
    rows: [
      {
        name: 'docs',
        listed: true,
        invocations: 5,
        turns_listed: [],
        derived_tokens: 38,
        description: 'Use when writing or revising documentation.',
      },
    ],
  }

  it('shows the invocation count, the description, and the token bar on the same row', () => {
    render(<SkillsPanel skills={asSkills(skills)} summary={asSummary({ skills_listed: ['docs'], skills_invoked: ['docs'] })} />)
    const docs = rowOf('docs')
    expect(docs.textContent).toContain('invoked · 5')
    expect(within(docs).getByTestId('skill-description-docs').textContent).toContain(
      'Use when writing or revising documentation.',
    )
    expect(within(docs).getByTestId('skill-token-bar-docs')).toBeTruthy()
  })
})

describe('SkillsPanel: invocations measured on some turns (D30)', () => {
  // The realistic payload with the server's coverage statement added: invocations were
  // measured on 2 of the session's 3 turns, the third carrying no transcript record.
  const realistic = fixtureOf('realistic').skills as Record<string, unknown>

  it('states the measured turn count beside the counts when invocations cover some turns', () => {
    const { container } = render(
      <SkillsPanel
        skills={asSkills({ ...realistic, invocationsCoverage: { measuredTurns: 2, totalTurns: 3 } })}
        summary={asSummary(fixtureOf('realistic').summary)}
      />,
    )
    const root = container.querySelector('[data-testid="skills-panel"]') as HTMLElement
    expect(root).not.toBeNull()
    expect(root.textContent?.replace(/\s+/g, ' ')).toContain('Invocations measured on 2 of 3 turns')
  })

  it('renders the realistic payload without the coverage statement when the server sends none', () => {
    const { container } = mount('realistic')
    expect(container.textContent?.replace(/\s+/g, ' ')).not.toContain('Invocations measured on')
  })
})
