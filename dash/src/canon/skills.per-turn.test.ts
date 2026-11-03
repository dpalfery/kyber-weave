// Per-turn listing and per-skill turn indices for the skills view (T2 contract
// extension, RED).
//
// The payload needs two things the engine does not yet expose:
//   1. For each turn, its EFFECTIVE listing with descriptions. That is the
//      union and replace fold over every listing event that applies to the turn,
//      including listings it inherits and does not carry itself.
//   2. For each skill row, WHICH turns it was listed on, as ids in turn order.
//      A bare count cannot answer "turns 1 and 2", so `turnSpanIds` is added
//      next to `turnsListed`, and the count must equal its length.
//
// Both are pinned here. Records are built the way `skills.test.ts` builds them.
// Nothing user-derived is embedded.

import { describe, expect, it, vi } from 'vitest'

import { turn } from './fixtures/records.js'
import { buildSkillsView, listedByTurn } from './skills.js'
import { contentFromParts } from './types.js'
import type { CanonicalRecord, ContentPart, NotMeasurable } from './types.js'

// ---------------------------------------------------------------------------
// Pinned contract
// ---------------------------------------------------------------------------

/**
 * `listedByTurn(records, turnRecords)`
 *   -> Map<turnSpanId, Map<skillName, description | undefined>>
 *
 * Keys are the `spanId` of each turn in `turnRecords`.
 *   - A turn to which no measured listing applies has NO entry (`has` is false).
 *     It is never an empty Map, because an empty Map reads as "measured, none".
 *   - A turn with an entry maps every effective skill to its last description,
 *     or to `undefined` when no listing gave one. Presence is `has`, not truthiness.
 *   - Session-level events fold in time order (initial replaces, non-initial
 *     unions) and apply to every same-session turn at or after their time, so a
 *     turn inherits a listing it does not carry itself.
 *   - A rollup listing (op `agent.invoke`) applies to its direct children only.
 *   - A turn whose timestamp cannot be parsed gets no session-level listing.
 *
 * `buildSkillsView` rows gain `turnSpanIds: string[]`, the ids of the turns on
 * which the skill was listed, in `turnRecords` order. `turnsListed` equals its
 * length. Everything else on the view is unchanged.
 */
type TurnListing = Map<string, string | undefined>
type ListedByTurn = Map<string, TurnListing>

type ViewRow = {
  name: string
  listed: boolean
  turnsListed: number
  turnSpanIds: string[]
  invocations?: number
  derivedTokens?: number
}

type MeasuredView = { measurable: true; rows: ViewRow[] }

// ---------------------------------------------------------------------------
// Fixture kit
// ---------------------------------------------------------------------------

const CLAUDE_SESSION = '00000000-0000-4000-8000-0000000000aa'
const T0 = '2026-01-01T00:00:00.000Z'
const T1 = '2026-01-01T00:00:01.000Z'
const T2 = '2026-01-01T00:00:02.000Z'
const AZURE = 'use the Azure CLI to interact with Azure resources.'
const COPILOT_SIX = [
  'app-docs-standard',
  'resharper-clt',
  'agent-customization',
  'create-pull-request',
  'architecture-decision-record',
  'azure-cli',
]

/** The captured Copilot system-prompt XML, carrying three descriptions. */
const COPILOT_XML = [
  '<available_skills>',
  '<skill>',
  '  <name>app-docs-standard</name>',
  '  <description>Apply the repository documentation standard.</description>',
  '  <location>project</location>',
  '</skill>',
  '<skill>',
  '  <name>azure-cli</name>',
  `  <description>${AZURE}</description>`,
  '  <location>project</location>',
  '</skill>',
  '</available_skills>',
].join('\n')

function withParts(parts: ContentPart[]): Pick<CanonicalRecord, 'parts' | 'content'> {
  return { parts, content: contentFromParts(parts) }
}

/** A Claude session-level listing event. */
function claudeListing(
  spanId: string,
  timestamp: string,
  isInitial: boolean,
  skills: Array<{ name: string; description?: string }>,
): CanonicalRecord {
  return turn(spanId, [], {
    source: 'claude-code',
    harness: 'claude-code',
    op: 'skill.listing',
    kind: 'internal',
    sessionId: CLAUDE_SESSION,
    timestamp,
    raw: { 'kyber.skills.listed': { isInitial, skills } },
  })
}

/** A Claude model-call turn in the captured session. Carries no listing itself. */
function claudeTurn(spanId: string, timestamp: string): CanonicalRecord {
  return turn(spanId, [], {
    source: 'claude-code',
    harness: 'claude-code',
    op: 'llm.invoke',
    kind: 'client',
    sessionId: CLAUDE_SESSION,
    timestamp,
    raw: {},
  })
}

/** The Copilot `invoke_agent` rollup, carrying the six-name JSON listing and the XML. */
function copilotRollup(spanId: string): CanonicalRecord {
  return turn(spanId, [], {
    source: 'github-copilot',
    harness: 'copilot',
    op: 'agent.invoke',
    kind: 'internal',
    name: 'invoke_agent conductor',
    raw: {
      'gen_ai.operation.name': 'invoke_agent',
      'github.copilot.context.skills': JSON.stringify(COPILOT_SIX),
    },
    ...withParts([{ part: 'system_prompt', text: COPILOT_XML }]),
  })
}

/** A Copilot model-call turn under the given parent. */
function copilotChat(spanId: string, parentSpanId: string | null): CanonicalRecord {
  return turn(spanId, [], {
    source: 'github-copilot',
    harness: 'copilot',
    op: 'llm.invoke',
    kind: 'client',
    parentSpanId,
    name: 'chat model',
    raw: {},
  })
}

/** A Pi model-call turn: no listing source at all. */
function piTurn(spanId: string): CanonicalRecord {
  return turn(spanId, [], { source: 'pi', harness: 'pi', op: 'llm.invoke', raw: {} })
}

function countTokens(text: string): number {
  return text.length
}

function rowNamed(view: MeasuredView, name: string): ViewRow {
  const row = view.rows.find((candidate) => candidate.name === name)
  expect(row, `expected a row for ${name}`).toBeDefined()
  return row!
}

// ---------------------------------------------------------------------------
// listedByTurn: effective listing per turn
// ---------------------------------------------------------------------------

describe('listedByTurn: inherited listings', () => {
  it('a turn with no listing record of its own inherits the one before it in its session', () => {
    const listing = claudeListing('listing-1', T0, true, [{ name: 'azure-cli', description: AZURE }])
    const turn1 = claudeTurn('turn-1', T1)
    const turn2 = claudeTurn('turn-2', T2)

    const byTurn = listedByTurn([listing, turn1, turn2], [turn1, turn2]) as ListedByTurn

    expect(byTurn.has('turn-1')).toBe(true)
    expect(byTurn.has('turn-2')).toBe(true)
    expect([...byTurn.get('turn-2')!.keys()]).toEqual(['azure-cli'])
    expect(byTurn.get('turn-2')!.get('azure-cli')).toBe(AZURE)
  })

  it('a turn before the first listing in its session has no entry', () => {
    const turn0 = claudeTurn('turn-0', T0)
    const listing = claudeListing('listing-1', T1, true, [{ name: 'azure-cli', description: AZURE }])
    const turn2 = claudeTurn('turn-2', T2)

    const byTurn = listedByTurn([turn0, listing, turn2], [turn0, turn2]) as ListedByTurn

    expect(byTurn.has('turn-0')).toBe(false)
    expect(byTurn.has('turn-2')).toBe(true)
  })
})

describe('listedByTurn: initial replaces, non-initial unions', () => {
  it('an initial listing replaces the effective list for later turns', () => {
    const first = claudeListing('listing-a', T0, true, [{ name: 'app-docs-standard' }, { name: 'azure-cli' }])
    const second = claudeListing('listing-b', T1, true, [{ name: 'docs-review' }])
    const turn2 = claudeTurn('turn-2', T2)

    const byTurn = listedByTurn([first, second, turn2], [turn2]) as ListedByTurn

    expect([...byTurn.get('turn-2')!.keys()]).toEqual(['docs-review'])
  })

  it('a non-initial listing unions into the effective list', () => {
    const first = claudeListing('listing-a', T0, true, [{ name: 'app-docs-standard' }, { name: 'azure-cli' }])
    const delta = claudeListing('listing-b', T1, false, [{ name: 'docs-review' }])
    const turn2 = claudeTurn('turn-2', T2)

    const byTurn = listedByTurn([first, delta, turn2], [turn2]) as ListedByTurn

    expect([...byTurn.get('turn-2')!.keys()].sort()).toEqual(['app-docs-standard', 'azure-cli', 'docs-review'])
  })
})

describe('listedByTurn: rollup listings apply to direct children only', () => {
  it('a rollup listing applies to its child turns and not to a sibling outside it', () => {
    const rollup = copilotRollup('conductor-agent')
    const child = copilotChat('chat-1', 'conductor-agent')
    const outside = copilotChat('chat-2', null)

    const byTurn = listedByTurn([rollup, child, outside], [child, outside]) as ListedByTurn

    expect([...byTurn.get('chat-1')!.keys()].sort()).toEqual([...COPILOT_SIX].sort())
    expect(byTurn.has('chat-2')).toBe(false)
  })
})

describe('listedByTurn: descriptions carried, and undefined when absent', () => {
  it('a description from the XML block is carried; a name the XML does not describe maps to undefined but is present', () => {
    const rollup = copilotRollup('conductor-agent')
    const child = copilotChat('chat-1', 'conductor-agent')

    const byTurn = listedByTurn([rollup, child], [child]) as ListedByTurn
    const listed = byTurn.get('chat-1')!

    expect(listed.get('azure-cli')).toBe(AZURE)
    expect(listed.has('resharper-clt')).toBe(true)
    expect(listed.get('resharper-clt')).toBeUndefined()
  })

  it('a later listing that names a skill without a description does not erase an earlier description', () => {
    const first = claudeListing('listing-a', T0, true, [{ name: 'azure-cli', description: AZURE }])
    const delta = claudeListing('listing-b', T1, false, [{ name: 'azure-cli' }])
    const turn2 = claudeTurn('turn-2', T2)

    const byTurn = listedByTurn([first, delta, turn2], [turn2]) as ListedByTurn

    expect(byTurn.get('turn-2')!.get('azure-cli')).toBe(AZURE)
  })
})

describe('listedByTurn: unmeasured turns and unparseable times', () => {
  it('a Pi turn with no listing source has no entry, never an empty map', () => {
    const pi = piTurn('pi-turn')

    const byTurn = listedByTurn([pi], [pi]) as ListedByTurn

    expect(byTurn.has('pi-turn')).toBe(false)
    expect(byTurn.get('pi-turn')).toBeUndefined()
  })

  it('a turn with an unparseable timestamp gets no session-level listing, while a placed sibling does', () => {
    const listing = claudeListing('listing-1', T0, true, [{ name: 'azure-cli', description: AZURE }])
    const placed = claudeTurn('turn-placed', T2)
    const unplaced = claudeTurn('turn-unplaced', 'not-a-timestamp')

    const byTurn = listedByTurn([listing, placed, unplaced], [placed, unplaced]) as ListedByTurn

    expect(byTurn.has('turn-placed')).toBe(true)
    expect(byTurn.has('turn-unplaced')).toBe(false)
  })
})

// ---------------------------------------------------------------------------
// Row field: turnSpanIds, in turn order, and turnsListed as its length
// ---------------------------------------------------------------------------

describe('buildSkillsView rows: turnSpanIds', () => {
  it('lists the ids of the turns each skill was listed on, in turnRecords order, with turnsListed equal to its length', () => {
    const listing = claudeListing('listing-1', T0, true, [{ name: 'azure-cli', description: AZURE }])
    const turn1 = claudeTurn('turn-1', T1)
    const turn2 = claudeTurn('turn-2', T2)

    const view = buildSkillsView([listing, turn1, turn2], [turn1, turn2], {}, vi.fn(countTokens)) as MeasuredView

    const azure = rowNamed(view, 'azure-cli')
    expect(azure.turnSpanIds).toEqual(['turn-1', 'turn-2'])
    expect(azure.turnsListed).toBe(azure.turnSpanIds.length)
  })

  it('follows turnRecords order even when that order is not timestamp order', () => {
    const listing = claudeListing('listing-1', T0, true, [{ name: 'azure-cli', description: AZURE }])
    const turn1 = claudeTurn('turn-1', T1)
    const turn2 = claudeTurn('turn-2', T2)

    const view = buildSkillsView([listing, turn1, turn2], [turn2, turn1], {}, vi.fn(countTokens)) as MeasuredView

    expect(rowNamed(view, 'azure-cli').turnSpanIds).toEqual(['turn-2', 'turn-1'])
  })

  it('a skill listed on no turn has turnSpanIds [] and turnsListed 0, because the source is measured', () => {
    const listing = claudeListing('listing-1', T0, true, [{ name: 'azure-cli', description: AZURE }])
    const turn1 = claudeTurn('turn-1', T1)
    const invoked = turn('tool-1', [], {
      source: 'claude-code',
      harness: 'claude-code',
      op: 'tool.invoke',
      kind: 'internal',
      name: 'Skill',
      parentSpanId: 'turn-1',
      sessionId: CLAUDE_SESSION,
      timestamp: T1,
      raw: { arguments: { skill: 'writing-trigger-descriptions' } },
    })

    const view = buildSkillsView([listing, turn1, invoked], [turn1], {}, vi.fn(countTokens)) as MeasuredView

    expect(rowNamed(view, 'writing-trigger-descriptions')).toMatchObject({
      listed: false,
      turnsListed: 0,
      turnSpanIds: [],
    })
  })

  it('a Copilot rollup skill row lists only the turns under the rollup', () => {
    const rollup = copilotRollup('conductor-agent')
    const child = copilotChat('chat-1', 'conductor-agent')
    const outside = copilotChat('chat-2', null)

    const view = buildSkillsView(
      [rollup, child, outside],
      [child, outside],
      {},
      vi.fn(countTokens),
    ) as MeasuredView

    expect(rowNamed(view, 'azure-cli')).toMatchObject({ turnsListed: 1, turnSpanIds: ['chat-1'] })
  })

  it('an unmeasured Pi view stays not measurable: no rows and no turnSpanIds', () => {
    const pi = piTurn('pi-turn')

    const view = buildSkillsView([pi], [pi], {}, vi.fn(countTokens)) as MeasuredView | NotMeasurable

    expect(view).toMatchObject({ availability: 'not_measurable' })
    expect(view).not.toHaveProperty('rows')
  })

  it('a listed row carries exactly the existing fields plus turnSpanIds, so nothing else changed shape', () => {
    const listing = claudeListing('listing-1', T0, true, [{ name: 'azure-cli', description: AZURE }])
    const turn1 = claudeTurn('turn-1', T1)

    const view = buildSkillsView([listing, turn1], [turn1], {}, vi.fn(countTokens)) as MeasuredView

    expect(Object.keys(rowNamed(view, 'azure-cli')).sort()).toEqual(
      ['derivedTokens', 'invocations', 'listed', 'name', 'turnSpanIds', 'turnsListed'].sort(),
    )
  })
})
