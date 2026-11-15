// Corrupt `kyber.skills.invoked` values must not read as a measured zero (PR #351
// review finding 4, RED).
//
// `extractInvokedSkills` decides that a non-Claude record's invoked side is
// measurable when the turn-level key is PRESENT. A presence check cannot tell a
// well-formed list from a corrupt value, so `null`, a string or a number under
// the key would count as "measured, none invoked". The invoked side is measurable
// only when the value is an array of names. Anything else is not measurable and
// says why, never zero.
//
// Claude-family records are measurable whatever the key holds: a Claude transcript
// always exposes its Skill calls, so an absent or corrupt key carries no weight.
//
// Records are built with the `turn()` fixture, as in `skills.test.ts`. No user
// data is embedded.

import { describe, expect, it } from 'vitest'

import { turn } from './fixtures/records.js'
import { buildSkillsView, extractInvokedSkills } from './skills.js'
import type { CanonicalRecord, NotMeasurable } from './types.js'

const INVOKED_KEY = 'kyber.skills.invoked'
const NON_CLAUDE_HARNESSES = ['copilot', 'antigravity'] as const
const CLAUDE_FAMILY = ['claude-code', 'claude-cli', 'claude-desktop'] as const

/** Values a corrupt or mis-typed key can hold, none of them an array. */
const CORRUPT_VALUES: ReadonlyArray<[string, unknown]> = [
  ['null', null],
  ['a string', 'docs'],
  ['a number', 42],
  ['a plain object', { names: ['docs'] }],
]

/** A model-call turn of the given harness, carrying the given raw attributes. */
function llmTurn(harness: string, spanId: string, raw: Record<string, unknown>): CanonicalRecord {
  return turn(spanId, [], {
    source: harness,
    harness,
    op: 'llm.invoke',
    kind: 'client',
    sessionId: 'session-1',
    timestamp: '2026-10-13T10:00:00.000Z',
    raw,
  })
}

/** A child Skill tool call under a turn. */
function skillCall(harness: string, parentSpanId: string, skill: string): CanonicalRecord {
  return turn(`tool-${skill}`, [], {
    source: harness,
    harness,
    parentSpanId,
    op: 'tool.invoke',
    kind: 'internal',
    name: 'Skill',
    sessionId: 'session-1',
    timestamp: '2026-10-13T10:00:00.000Z',
    raw: { arguments: { skill } },
  })
}

/** Antigravity's listing, so the view is measurable and only the invoked side is in question. */
const ANTIGRAVITY_LISTING = 'Active Skills: agy-customizations, antigravity-guide'

describe.each(NON_CLAUDE_HARNESSES)('non-Claude harness %s: a corrupt invoked key is not measurable', (harness) => {
  it.each(CORRUPT_VALUES)('%s value: the invoked side is not_measurable, never a measured zero', (_label, value) => {
    const result = extractInvokedSkills([llmTurn(harness, 'turn-1', { [INVOKED_KEY]: value })])

    expect(result).toMatchObject({ availability: 'not_measurable', reason: expect.any(String) })
    expect(result).not.toHaveProperty('invocations')
  })
})

describe.each(NON_CLAUDE_HARNESSES)('non-Claude harness %s: an array invoked key is measurable', (harness) => {
  it('an array of names is measured, and names come from it', () => {
    const result = extractInvokedSkills([llmTurn(harness, 'turn-1', { [INVOKED_KEY]: ['docs'] })])

    expect(result).toMatchObject({ measurable: true })
    expect((result as { invocations: Array<{ name: string }> }).invocations.map((call) => call.name)).toEqual(['docs'])
  })

  it('an empty array is a measured none, not an unmeasured turn', () => {
    const result = extractInvokedSkills([llmTurn(harness, 'turn-1', { [INVOKED_KEY]: [] })])

    expect(result).toEqual({ measurable: true, invocations: [] })
  })
})

describe.each(NON_CLAUDE_HARNESSES)('non-Claude harness %s: a Skill call is a source whatever the key holds', (harness) => {
  it('a corrupt key with a Skill call still counts the call, and the side is measurable', () => {
    const records = [
      llmTurn(harness, 'turn-1', { [INVOKED_KEY]: null }),
      skillCall(harness, 'turn-1', 'azure-cli'),
    ]
    const result = extractInvokedSkills(records)

    expect(result).toMatchObject({ measurable: true })
    expect((result as { invocations: Array<{ name: string }> }).invocations.map((call) => call.name)).toEqual([
      'azure-cli',
    ])
  })
})

describe.each(CLAUDE_FAMILY)('Claude-family harness %s stays measurable whatever the key holds', (harness) => {
  it.each(CORRUPT_VALUES)('%s value: measurable, with no invocations counted', (_label, value) => {
    const result = extractInvokedSkills([llmTurn(harness, 'turn-1', { [INVOKED_KEY]: value })])

    expect(result).toEqual({ measurable: true, invocations: [] })
  })
})

describe('buildSkillsView: a corrupt invoked key never shows as zero invocations', () => {
  it('a null-valued key on an Antigravity turn yields invocations not_measurable on the view, and no row reads 0', () => {
    const chat = turn('chat-1', [], {
      source: 'antigravity',
      harness: 'antigravity',
      op: 'llm.invoke',
      kind: 'client',
      sessionId: 'session-1',
      timestamp: '2026-10-13T10:00:00.000Z',
      raw: { 'gen_ai.skills': ANTIGRAVITY_LISTING, [INVOKED_KEY]: null },
    })

    const view = buildSkillsView([chat], [chat], {}, (text) => text.length)

    expect(view).toMatchObject({ measurable: true })
    expect((view as { invocationsAvailability?: NotMeasurable }).invocationsAvailability).toMatchObject({
      availability: 'not_measurable',
      reason: expect.any(String),
    })
    for (const row of (view as { rows: Array<{ invocations?: number }> }).rows) {
      expect(row.invocations).toBeUndefined()
    }
  })

  it('an empty array on the same Antigravity turn is a measured none: every row reads 0', () => {
    const chat = turn('chat-1', [], {
      source: 'antigravity',
      harness: 'antigravity',
      op: 'llm.invoke',
      kind: 'client',
      sessionId: 'session-1',
      timestamp: '2026-10-13T10:00:00.000Z',
      raw: { 'gen_ai.skills': ANTIGRAVITY_LISTING, [INVOKED_KEY]: [] },
    })

    const view = buildSkillsView([chat], [chat], {}, (text) => text.length)

    expect(view).toMatchObject({ measurable: true })
    expect((view as { invocationsAvailability?: unknown }).invocationsAvailability).toBeUndefined()
    const rows = (view as { rows: Array<{ invocations?: number }> }).rows
    expect(rows.length).toBeGreaterThan(0)
    for (const row of rows) expect(row.invocations).toBe(0)
  })
})
