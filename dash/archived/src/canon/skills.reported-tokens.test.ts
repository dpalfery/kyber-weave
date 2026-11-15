// Tests for the harness-reported skill token figure on the skills view (T1
// review pass 1, RED).
//
// `gen_ai.usage.skill_tokens` is a per-turn RESIDENT size, not an increment.
// In the real Antigravity store every turn of a session carries the same value
// (1040), because it is the size of the skill listing the turn had in context.
// Summing it across turns would report 1040 x turns. The view must therefore
// report the figure of the LATEST turn that carries a finite number, never a
// sum and never a max, and must never split it across skill rows.
//
// A second pin concerns time. A turn whose timestamp cannot be parsed cannot be
// placed against a session-level listing event, so it must not be treated as
// having seen every such event.
//
// Records are built the way `skills.test.ts` builds them: the `turn()` fixture
// and raw attribute maps that match what the adapters write. Nothing user-derived
// is embedded.

import { describe, expect, it, vi } from 'vitest'

import { turn } from './fixtures/records.js'
import { buildSkillsView } from './skills.js'
import type { CanonicalRecord, NotMeasurable } from './types.js'

// ---------------------------------------------------------------------------
// Pinned contract
// ---------------------------------------------------------------------------

/**
 * `reportedSkillTokens` is the harness counter of the latest turn that carries
 * a finite number under `gen_ai.usage.skill_tokens`. "Latest" is by parsed
 * timestamp. When two turns share a timestamp, the greater `spanId` (plain
 * string comparison) is the later one. Absent, non-numeric, and non-finite
 * values are skipped, and the key is omitted when no turn carries one.
 */
type ViewWithReported = {
  measurable: true
  rows: Array<{ name: string; listed: boolean; turnsListed: number; derivedTokens?: number }>
  reportedSkillTokens?: number
}

const SKILL_TOKENS_KEY = 'gen_ai.usage.skill_tokens'
const LISTING_KEY = 'gen_ai.skills'
const AG_SESSION = 'ag-session-1'
const AG_LISTING = 'Active Skills: agy-customizations, antigravity-guide'
const CLAUDE_SESSION = '00000000-0000-4000-8000-0000000000aa'
const AZURE_DESCRIPTION = 'use the Azure CLI to interact with Azure resources.'

// ---------------------------------------------------------------------------
// Builders
// ---------------------------------------------------------------------------

/**
 * An Antigravity `llm.invoke` turn. `extraRaw` adds or overrides attributes so
 * a test can set the token counter to absent, NaN, a string, and so on.
 */
function agyTurn(
  spanId: string,
  timestamp: string,
  extraRaw: Record<string, unknown> = { [SKILL_TOKENS_KEY]: 1040 },
): CanonicalRecord {
  return turn(spanId, [], {
    source: 'antigravity',
    harness: 'antigravity',
    op: 'llm.invoke',
    kind: 'client',
    sessionId: AG_SESSION,
    timestamp,
    raw: { [LISTING_KEY]: AG_LISTING, ...extraRaw },
  })
}

/** A Claude session-level listing event, initial, with one described skill. */
function claudeListing(timestamp: string): CanonicalRecord {
  return turn('listing-initial', [], {
    source: 'claude-code',
    harness: 'claude-code',
    op: 'skill.listing',
    kind: 'internal',
    sessionId: CLAUDE_SESSION,
    timestamp,
    raw: {
      'kyber.skills.listed': {
        isInitial: true,
        skills: [{ name: 'azure-cli', description: AZURE_DESCRIPTION }],
      },
    },
  })
}

/** A Claude model-call turn in the same session. */
function claudeTurn(spanId: string, timestamp: string, extraRaw: Record<string, unknown> = {}): CanonicalRecord {
  return turn(spanId, [], {
    source: 'claude-code',
    harness: 'claude-code',
    op: 'llm.invoke',
    kind: 'client',
    sessionId: CLAUDE_SESSION,
    timestamp,
    raw: extraRaw,
  })
}

function countTokens(text: string): number {
  return text.length
}

/** Runs the view and returns it as a measured view, failing the test otherwise. */
function measuredView(records: CanonicalRecord[], turns: CanonicalRecord[] = records): ViewWithReported {
  const view = buildSkillsView(records, turns, {}, vi.fn(countTokens)) as ViewWithReported | NotMeasurable
  expect(view, 'the view must be measurable for these Antigravity turns').toMatchObject({ measurable: true })
  return view as ViewWithReported
}

/** The view's reported figure, read without assuming the key exists. */
function reportedOf(records: CanonicalRecord[], turns?: CanonicalRecord[]): unknown {
  return (measuredView(records, turns) as { reportedSkillTokens?: unknown }).reportedSkillTokens
}

const T1 = '2026-10-13T10:00:00.000Z'
const T2 = '2026-10-13T10:05:00.000Z'
const T3 = '2026-10-13T10:10:00.000Z'

// ---------------------------------------------------------------------------
// The figure is a resident size: never summed
// ---------------------------------------------------------------------------

describe('reportedSkillTokens on Antigravity turns: resident size, not an increment', () => {
  it('three turns each carrying 1040 report 1040, not 3120', () => {
    const turns = [agyTurn('turn-1', T1), agyTurn('turn-2', T2), agyTurn('turn-3', T3)]

    expect(reportedOf(turns)).toBe(1040)
  })

  it('when the value changes, the latest turn by timestamp wins: 1040, 1040, 1200 gives 1200', () => {
    const turns = [
      agyTurn('turn-1', T1, { [SKILL_TOKENS_KEY]: 1040 }),
      agyTurn('turn-2', T2, { [SKILL_TOKENS_KEY]: 1040 }),
      agyTurn('turn-3', T3, { [SKILL_TOKENS_KEY]: 1200 }),
    ]

    expect(reportedOf(turns)).toBe(1200)
  })

  it('the latest-by-timestamp rule holds whatever order the turns are given in', () => {
    const turns = [
      agyTurn('turn-3', T3, { [SKILL_TOKENS_KEY]: 1200 }),
      agyTurn('turn-1', T1, { [SKILL_TOKENS_KEY]: 1040 }),
      agyTurn('turn-2', T2, { [SKILL_TOKENS_KEY]: 1040 }),
    ]

    expect(reportedOf(turns)).toBe(1200)
    expect(reportedOf([...turns].reverse())).toBe(1200)
  })

  it('equal timestamps are broken by spanId: the greater spanId is the later turn, in either input order', () => {
    const earlierSpan = agyTurn('turn-a', T2, { [SKILL_TOKENS_KEY]: 1040 })
    const laterSpan = agyTurn('turn-b', T2, { [SKILL_TOKENS_KEY]: 1200 })

    expect(reportedOf([earlierSpan, laterSpan])).toBe(1200)
    expect(reportedOf([laterSpan, earlierSpan])).toBe(1200)
  })

  it('never reports a sum or a max: 1040 and 1200 at distinct times do not give 2240 or 1200 by accident', () => {
    // Two turns only, with the earlier value larger, so a max and the
    // latest-wins rule disagree: latest is 1040, max would be 1200.
    const turns = [
      agyTurn('turn-1', T1, { [SKILL_TOKENS_KEY]: 1200 }),
      agyTurn('turn-2', T2, { [SKILL_TOKENS_KEY]: 1040 }),
    ]

    expect(reportedOf(turns)).toBe(1040)
  })
})

// ---------------------------------------------------------------------------
// Absence is not zero
// ---------------------------------------------------------------------------

describe('reportedSkillTokens when no turn carries a finite number', () => {
  it.each([
    ['absent', undefined],
    ['NaN', Number.NaN],
    ['a string', '1040'],
    ['null', null],
  ])('a turn whose counter is %s contributes nothing, and the key is not coerced to 0', (_label, value) => {
    const turns = [agyTurn('turn-1', T1, { [SKILL_TOKENS_KEY]: value }), agyTurn('turn-2', T2, { [SKILL_TOKENS_KEY]: value })]
    const view = measuredView(turns)

    expect(view).not.toHaveProperty('reportedSkillTokens')
  })

  it('a non-numeric later turn does not override an earlier finite figure', () => {
    // The string '1200' is not a number the harness counted; the latest turn
    // that DID report a finite number is the earlier one.
    const turns = [
      agyTurn('turn-1', T1, { [SKILL_TOKENS_KEY]: 1040 }),
      agyTurn('turn-2', T2, { [SKILL_TOKENS_KEY]: '1200' }),
    ]

    expect(reportedOf(turns)).toBe(1040)
  })

  it('a later turn without the key does not override an earlier finite figure', () => {
    const turns = [agyTurn('turn-1', T1, { [SKILL_TOKENS_KEY]: 1040 }), agyTurn('turn-2', T2, {})]

    expect(reportedOf(turns)).toBe(1040)
  })
})

// ---------------------------------------------------------------------------
// Out-of-order input resolves by time
// ---------------------------------------------------------------------------

describe('reportedSkillTokens resolves by timestamp, not input position', () => {
  it('turns given out of timestamp order still resolve to the latest timestamp', () => {
    const turns = [
      agyTurn('turn-2', T2, { [SKILL_TOKENS_KEY]: 1040 }),
      agyTurn('turn-3', T3, { [SKILL_TOKENS_KEY]: 1200 }),
      agyTurn('turn-1', T1, { [SKILL_TOKENS_KEY]: 1040 }),
    ]

    expect(reportedOf(turns)).toBe(1200)
  })
})

// ---------------------------------------------------------------------------
// The figure is never split across skill rows
// ---------------------------------------------------------------------------

describe('reportedSkillTokens is carried whole and never split across rows', () => {
  it('Antigravity rows carry no derived tokens, and no row holds the reported figure', () => {
    const view = measuredView([agyTurn('turn-1', T1), agyTurn('turn-2', T2)])

    expect(view.reportedSkillTokens).toBe(1040)
    for (const row of view.rows) {
      expect(row.derivedTokens, row.name).toBeUndefined()
      expect(Object.values(row), row.name).not.toContain(1040)
    }
  })

  it('on a view that also derives description tokens, the derived figure is that description length, untouched by 1040', () => {
    // A Claude listing with a description, plus a turn carrying the reported
    // counter. The two figures come from different sources and must not mix.
    const records = [claudeListing(T1), claudeTurn('turn-1', T2, { [SKILL_TOKENS_KEY]: 1040 })]
    const view = buildSkillsView(records, [records[1]!], {}, vi.fn(countTokens)) as ViewWithReported

    expect(view).toMatchObject({ measurable: true, reportedSkillTokens: 1040 })
    const azure = view.rows.find((row) => row.name === 'azure-cli')
    expect(azure?.derivedTokens).toBe(AZURE_DESCRIPTION.length)
    expect(azure?.derivedTokens).not.toBe(1040)
    for (const row of view.rows) {
      if (row.name !== 'azure-cli') expect(row.derivedTokens, row.name).toBeUndefined()
    }
  })
})

// ---------------------------------------------------------------------------
// A turn with an unparseable timestamp does not see every session-level listing
// ---------------------------------------------------------------------------

describe('a turn whose timestamp cannot be parsed', () => {
  it('is not treated as having seen a session-level listing: it adds no listed count', () => {
    // The listing is in effect from T1. turn-a is placed at T2 and sees it.
    // turn-b has no parseable time, so the listing cannot be shown to apply to
    // it. Counting it as listed would invent a measurement the source never made.
    const listing = claudeListing(T1)
    const seen = claudeTurn('turn-a', T2)
    const unplaced = claudeTurn('turn-b', 'not-a-timestamp')
    const turns = [seen, unplaced]

    const view = buildSkillsView([listing, ...turns], turns, {}, vi.fn(countTokens)) as ViewWithReported

    expect(view).toMatchObject({ measurable: true })
    const azure = view.rows.find((row) => row.name === 'azure-cli')
    expect(azure?.listed).toBe(true)
    expect(azure?.turnsListed).toBe(1)
  })
})
