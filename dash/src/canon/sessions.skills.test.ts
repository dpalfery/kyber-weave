// Tests for the skills view on the session projection (T2, RED: written before
// `buildSessionRow` carries skills into the payload).
//
// `buildSessionRow` must project the skills a session and its turns had access
// to, from the pure engine in `skills.ts`, into the payload served at
// GET /api/kyber/session/:id and stored in canon.db. The additive contract:
//
//   - summary.skills_listed / summary.skills_invoked: name arrays, each key
//     absent when that side is not measured.
//   - payload.skills: { measurable: true, reported_skill_tokens?, listed_count?,
//     listed_uninvoked_count?, rows[] } or { availability: 'not_measurable', reason }.
//   - rows: { name, listed, invocations, turns_listed (0-based), derived_tokens? }.
//   - turns[i].skills_listed: present only when that turn's listing is measured.
//
// Records are built the way the synthesizer writes them: `kyber.skills.listed`
// and `kyber.skills.invoked` sit on turn records, a Skill call is a `tool.invoke`
// named `Skill`, and the other harnesses use their own raw keys. Nothing
// user-derived is embedded.

import { describe, expect, it, vi } from 'vitest'

import { tokens, turn } from './fixtures/records.js'
import { buildSessionRow } from './sessions.js'
import type { CanonicalRecord, ContentPart, NotMeasurable, SessionRow } from './types.js'

// ---------------------------------------------------------------------------
// Pinned contract (the minimal shapes these tests depend on)
// ---------------------------------------------------------------------------

type SkillRowView = {
  name: string
  listed: boolean | NotMeasurable
  invocations: number | NotMeasurable
  turns_listed: number[]
  derived_tokens?: number
  /** The first listing text observed for the skill in the session; absent when none carried one. */
  description?: string
}

type SkillsPayloadView =
  | {
      measurable: true
      reported_skill_tokens?: number
      listed_count?: number
      listed_uninvoked_count?: number
      rows: SkillRowView[]
    }
  | NotMeasurable

type SessionPayloadView = {
  summary: Record<string, unknown>
  skills: SkillsPayloadView
  turns: Array<{ index: number; skills_listed?: string[]; skills_invoked?: string[]; [key: string]: unknown }>
  measurability: Record<string, unknown>
}

// ---------------------------------------------------------------------------
// Fixture kit
// ---------------------------------------------------------------------------

const SESSION = 'sess-skills-1'
const TRACE = 'trace-skills-1'
const T0 = '2026-09-03T10:00:00.000Z'
const T1 = '2026-09-03T10:01:00.000Z'
const T2 = '2026-09-03T10:02:00.000Z'

const LISTED = 'kyber.skills.listed'
const INVOKED = 'kyber.skills.invoked'
const AZURE = 'use the Azure CLI to interact with Azure resources.'
const DOCS = 'Apply the repository documentation standard.'

/** The counter is injected; a length counter makes the derived figure checkable. */
const countTokens = (text: string) => text.length

function payloadOf(row: SessionRow): SessionPayloadView {
  return row.payload as unknown as SessionPayloadView
}

function skillsOf(row: SessionRow): SkillsPayloadView {
  return payloadOf(row).skills
}

function measuredSkills(row: SessionRow): Exclude<SkillsPayloadView, NotMeasurable> {
  const skills = skillsOf(row)
  expect(skills, 'payload.skills must be present').toBeDefined()
  expect(skills, 'payload.skills must be measured').not.toHaveProperty('availability')
  return skills as Exclude<SkillsPayloadView, NotMeasurable>
}

const sorted = (values: readonly string[]) => [...values].sort()

/** A summary name array, sorted. Fails by name when the key is absent. */
function summaryNames(summary: Record<string, unknown>, key: string): string[] {
  expect(summary, `summary.${key}`).toHaveProperty(key)
  expect(Array.isArray(summary[key]), `summary.${key} must be an array`).toBe(true)
  return sorted(summary[key] as string[])
}

/** A Claude Code turn: `llm.invoke`, optionally carrying its skill listing or invocations. */
function claudeTurn(spanId: string, timestamp: string, raw: Record<string, unknown> = {}): CanonicalRecord {
  return turn(spanId, [], {
    harness: 'claude-code',
    source: 'claude-code',
    sessionId: SESSION,
    traceId: TRACE,
    timestamp,
    raw,
  })
}

/** A `Skill` tool call under a turn, as the adapter writes it (`arguments.skill`). */
function skillCall(spanId: string, parentSpanId: string, skill: string): CanonicalRecord {
  return turn(spanId, [], {
    harness: 'claude-code',
    source: 'claude-code',
    sessionId: SESSION,
    traceId: TRACE,
    parentSpanId,
    op: 'tool.invoke',
    kind: 'internal',
    name: 'Skill',
    timestamp: T1,
    tokens: tokens({ freshInput: 0, output: 0, reportedInput: 0, reportedOutput: 0 }),
    raw: { arguments: { skill } },
  })
}

/** An Antigravity model-call span carrying `gen_ai.skills` and the resident token counter. */
function antigravityTurn(spanId: string, timestamp: string, raw: Record<string, unknown>): CanonicalRecord {
  return turn(spanId, [], {
    harness: 'antigravity',
    source: 'antigravity',
    sessionId: SESSION,
    traceId: TRACE,
    timestamp,
    raw,
  })
}

const LISTING_WITH_DESCRIPTIONS = {
  isInitial: true,
  skills: [
    { name: 'azure-cli', description: AZURE },
    { name: 'app-docs-standard', description: DOCS },
    { name: 'unused-helper' },
  ],
}

// ---------------------------------------------------------------------------
// Claude Code: listing and invocation both measured
// ---------------------------------------------------------------------------

describe('buildSessionRow: skills on a Claude Code session', () => {
  // turn-0 predates the listing; turn-1 lists the skills and calls azure-cli;
  // turn-2 inherits the listing and invokes a skill the listing never named.
  const records = (): CanonicalRecord[] => [
    claudeTurn('turn-0', T0),
    claudeTurn('turn-1', T1, { [LISTED]: LISTING_WITH_DESCRIPTIONS }),
    skillCall('call-1', 'turn-1', 'azure-cli'),
    claudeTurn('turn-2', T2, { [INVOKED]: ['not-listed-skill'] }),
  ]

  it('projects each skill row with its listed flag, invocation count and turns listed', () => {
    const row = buildSessionRow(SESSION, records(), countTokens)
    const skills = measuredSkills(row)

    expect(skills.rows).toStrictEqual([
      {
        name: 'app-docs-standard',
        listed: true,
        invocations: 0,
        turns_listed: [1, 2],
        derived_tokens: DOCS.length,
        description: DOCS,
      },
      {
        name: 'azure-cli',
        listed: true,
        invocations: 1,
        turns_listed: [1, 2],
        derived_tokens: AZURE.length,
        description: AZURE,
      },
      { name: 'not-listed-skill', listed: false, invocations: 1, turns_listed: [] },
      { name: 'unused-helper', listed: true, invocations: 0, turns_listed: [1, 2] },
    ])
    expect(skills.listed_count).toBe(3)
    expect(skills.listed_uninvoked_count).toBe(2)
  })

  it('states the skill names on the summary: listed ones as skills_listed, called ones as skills_invoked', () => {
    const summary = payloadOf(buildSessionRow(SESSION, records(), countTokens)).summary

    expect(summaryNames(summary, 'skills_listed')).toEqual(['app-docs-standard', 'azure-cli', 'unused-helper'])
    expect(summaryNames(summary, 'skills_invoked')).toEqual(['azure-cli', 'not-listed-skill'])
  })

  it('keeps the first description observed for a skill, not a later different one', () => {
    const REWRITTEN = 'a later listing rewrote this description.'
    const rewritten = [
      claudeTurn('turn-1', T1, { [LISTED]: { isInitial: true, skills: [{ name: 'azure-cli', description: AZURE }] } }),
      claudeTurn('turn-2', T2, {
        [LISTED]: { isInitial: true, skills: [{ name: 'azure-cli', description: REWRITTEN }] },
      }),
    ]
    const skills = measuredSkills(buildSessionRow(SESSION, rewritten, countTokens))

    expect(skills.rows).toStrictEqual([
      { name: 'azure-cli', listed: true, invocations: 0, turns_listed: [0, 1], derived_tokens: AZURE.length, description: AZURE },
    ])
  })

  it('omits description on a row that no listing described', () => {
    const skills = measuredSkills(buildSessionRow(SESSION, records(), countTokens))

    expect(skills.rows.find((row) => row.name === 'unused-helper')).not.toHaveProperty('description')
    expect(skills.rows.find((row) => row.name === 'not-listed-skill')).not.toHaveProperty('description')
  })

  it('carries turns[i].skills_invoked with the names each turn invoked, and [] for a measured turn with none', () => {
    const payload = payloadOf(buildSessionRow(SESSION, records(), countTokens))

    expect(payload.turns[0]?.skills_invoked).toEqual([])
    expect(payload.turns[1]?.skills_invoked).toEqual(['azure-cli'])
    expect(payload.turns[2]?.skills_invoked).toEqual(['not-listed-skill'])
  })

  it('reports an empty skills_invoked when a listed Claude session made no skill calls', () => {
    const listedOnly = [claudeTurn('turn-1', T1, { [LISTED]: LISTING_WITH_DESCRIPTIONS })]
    const row = buildSessionRow(SESSION, listedOnly, countTokens)

    expect(payloadOf(row).summary.skills_invoked).toEqual([])
    expect(measuredSkills(row).listed_uninvoked_count).toBe(3)
  })

  it('never reports reported_skill_tokens for a Claude session, which carries no such counter', () => {
    const skills = measuredSkills(buildSessionRow(SESSION, records(), countTokens))

    expect(skills).not.toHaveProperty('reported_skill_tokens')
  })

  it('numbers turns_listed by 0-based transport index, not by position in the turn count', () => {
    const listedFromFirstTurn = [
      claudeTurn('turn-0', T0, { [LISTED]: { isInitial: true, skills: [{ name: 'azure-cli' }] } }),
      claudeTurn('turn-1', T1),
    ]
    const skills = measuredSkills(buildSessionRow(SESSION, listedFromFirstTurn, countTokens))

    expect(skills.rows).toStrictEqual([
      { name: 'azure-cli', listed: true, invocations: 0, turns_listed: [0, 1] },
    ])
  })

  it('carries skills_listed on a turn only when that turn has a measured listing', () => {
    const payload = payloadOf(buildSessionRow(SESSION, records(), countTokens))

    expect(payload.turns[0]).not.toHaveProperty('skills_listed')
    expect(summaryNames(payload.turns[1]! as Record<string, unknown>, 'skills_listed')).toEqual([
      'app-docs-standard',
      'azure-cli',
      'unused-helper',
    ])
  })

  it('derives each listed row token count from its description through the injected counter', () => {
    const counter = vi.fn(countTokens)
    const skills = measuredSkills(buildSessionRow(SESSION, records(), counter))

    expect(counter).toHaveBeenCalledWith(AZURE)
    expect(counter).toHaveBeenCalledWith(DOCS)
    expect(skills.rows.find((row) => row.name === 'azure-cli')?.derived_tokens).toBe(AZURE.length)
    // No description, no derived figure: absent, never zero.
    expect(skills.rows.find((row) => row.name === 'unused-helper')).not.toHaveProperty('derived_tokens')
  })

  it('orders skill rows by name whatever order the records arrive in', () => {
    const forward = measuredSkills(buildSessionRow(SESSION, records(), countTokens)).rows.map((row) => row.name)
    const reversed = measuredSkills(buildSessionRow(SESSION, [...records()].reverse(), countTokens)).rows.map(
      (row) => row.name,
    )

    expect(forward).toEqual(sorted(forward))
    expect(reversed).toEqual(forward)
  })
})

// ---------------------------------------------------------------------------
// Antigravity: listing measured, invocations not measurable
// ---------------------------------------------------------------------------

describe('buildSessionRow: skills on an Antigravity session', () => {
  const AGY_LISTING = 'Active Skills: agy-customizations, antigravity-guide'
  const records = (): CanonicalRecord[] => [
    antigravityTurn('agy-0', T0, { 'gen_ai.skills': AGY_LISTING, 'gen_ai.usage.skill_tokens': 1040 }),
    antigravityTurn('agy-1', T1, { 'gen_ai.skills': AGY_LISTING, 'gen_ai.usage.skill_tokens': 1040 }),
    antigravityTurn('agy-2', T2, { 'gen_ai.skills': AGY_LISTING, 'gen_ai.usage.skill_tokens': 1040 }),
  ]

  it('reports the per-turn resident skill tokens once, never summed across the turns that carry it', () => {
    const skills = measuredSkills(buildSessionRow(SESSION, records(), countTokens))

    expect(skills.reported_skill_tokens).toBe(1040)
  })

  it('omits listed_uninvoked_count and the summary skills_invoked when invocations are not measurable', () => {
    const row = buildSessionRow(SESSION, records(), countTokens)
    const skills = measuredSkills(row)

    expect(skills.listed_count).toBe(2)
    expect(skills).not.toHaveProperty('listed_uninvoked_count')
    expect(payloadOf(row).summary).not.toHaveProperty('skills_invoked')
    expect(summaryNames(payloadOf(row).summary, 'skills_listed')).toEqual(['agy-customizations', 'antigravity-guide'])
  })

  it('omits turns[i].skills_invoked when invocations are not measured, never [] for an unknown', () => {
    const row = buildSessionRow(SESSION, records(), countTokens)
    // The listing side is measured here, so the absent per-turn key is the only gap.
    measuredSkills(row)
    const payload = payloadOf(row)

    for (const turnView of payload.turns) {
      expect(turnView, `turns[${turnView.index}]`).not.toHaveProperty('skills_invoked')
    }
  })

  it('declares each row invocations not measurable and never reports an unmeasured count as zero', () => {
    const skills = measuredSkills(buildSessionRow(SESSION, records(), countTokens))

    expect(skills.rows.map((row) => row.listed)).toEqual([true, true])
    for (const row of skills.rows) {
      expect(row.invocations).toStrictEqual({ availability: 'not_measurable', reason: expect.any(String) })
    }
  })
})

// ---------------------------------------------------------------------------
// Copilot: descriptions from the system prompt, no invocation source
// ---------------------------------------------------------------------------

describe('buildSessionRow: skills on a Copilot session', () => {
  it('takes descriptions from the system prompt and omits the summary skills_invoked', () => {
    const systemPrompt: ContentPart = {
      part: 'system_prompt',
      text: '<skill><name>architect</name><description>Design systems.</description></skill>',
    }
    const chat = turn('copilot-0', [systemPrompt], {
      harness: 'github-copilot',
      source: 'github-copilot',
      sessionId: SESSION,
      traceId: TRACE,
      timestamp: T0,
      raw: { 'github.copilot.context.skills': '["architect"]' },
    })
    const row = buildSessionRow(SESSION, [chat], countTokens)
    const skills = measuredSkills(row)

    expect(skills.rows).toStrictEqual([
      {
        name: 'architect',
        listed: true,
        invocations: { availability: 'not_measurable', reason: expect.any(String) },
        turns_listed: [0],
        derived_tokens: 'Design systems.'.length,
        description: 'Design systems.',
      },
    ])
    expect(skills.listed_count).toBe(1)
    expect(skills).not.toHaveProperty('listed_uninvoked_count')
    expect(payloadOf(row).summary).not.toHaveProperty('skills_invoked')
    expect(payloadOf(row).summary.skills_listed).toEqual(['architect'])
  })
})

// ---------------------------------------------------------------------------
// No skill source: the whole view is declared not measurable
// ---------------------------------------------------------------------------

describe('buildSessionRow: skills on a harness with no skill source', () => {
  it.each(['pi', 'unknown'])('declares skills not measurable, with a reason, for %s', (harness) => {
    const plain = turn('plain-0', [], {
      harness,
      source: harness,
      sessionId: SESSION,
      traceId: TRACE,
      timestamp: T0,
    })
    const row = buildSessionRow(SESSION, [plain], countTokens)
    const skills = skillsOf(row) as NotMeasurable

    expect(skills, 'payload.skills must be present').toBeDefined()
    expect(skills).toStrictEqual({ availability: 'not_measurable', reason: expect.any(String) })
    expect(skills.reason.length).toBeGreaterThan(0)
    expect(payloadOf(row).summary).not.toHaveProperty('skills_listed')
    expect(payloadOf(row).summary).not.toHaveProperty('skills_invoked')
  })

  it('omits turns[i].skills_invoked when no invocation source is measured', () => {
    const plain = turn('pi-1', [], {
      harness: 'pi',
      source: 'pi',
      sessionId: SESSION,
      traceId: TRACE,
      timestamp: T0,
    })
    const payload = payloadOf(buildSessionRow(SESSION, [plain], countTokens))

    // Pi's skills verdict is the whole-view declaration; the turn key must follow it, absent.
    expect(payload.skills, 'payload.skills must be present').toHaveProperty('availability', 'not_measurable')
    expect(payload.turns[0]).not.toHaveProperty('skills_invoked')
  })

  it('merges the skills verdict and its reason into measurability.skills', () => {
    const plain = turn('pi-0', [], {
      harness: 'pi',
      source: 'pi',
      sessionId: SESSION,
      traceId: TRACE,
      timestamp: T0,
    })
    const payload = payloadOf(buildSessionRow(SESSION, [plain], countTokens))
    expect(payload.skills, 'payload.skills must be present').toBeDefined()
    const verdict = payload.skills as NotMeasurable

    expect(payload.measurability.skills).toStrictEqual({ availability: 'not_measurable', reason: verdict.reason })
  })
})
