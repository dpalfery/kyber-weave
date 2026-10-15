// Tests for the skills view engine (T1, RED: written before the module exists).
//
// `skills.ts` is a pure engine: it reads canonical records and answers which
// skills a session or turn had access to (listed) and which it invoked. Every
// figure it returns is measured, derived, or declared not measurable. Absence
// of a source is never coerced to an empty list or to zero.
//
// Inputs are built from the committed sanitized captures under
// `fixtures/skills/<harness>/`, so the record shapes here are the shapes the
// adapters and the synthesizer actually write. Nothing else user-derived is
// embedded in this file.
//
// The module under test is imported by its intended path. Until GREEN it fails
// to resolve, which is the expected RED evidence.

import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { describe, expect, it, vi } from 'vitest'

import { turn } from './fixtures/records.js'
import { buildSkillsView, extractInvokedSkills, extractListedSkills } from './skills.js'
import { contentFromParts, notMeasurable } from './types.js'
import type { CanonicalRecord, ContentPart, NotMeasurable } from './types.js'

// ---------------------------------------------------------------------------
// Pinned contract (the minimal shapes these tests depend on)
// ---------------------------------------------------------------------------

/** One listed skill. `sourceKey` identifies the listing that reported it. */
type ListedSkill = { name: string; description?: string; sourceKey: string }

/** `extractListedSkills(record)`: a measured list, or a declared absence. */
type ListedResult = { measurable: true; skills: ListedSkill[] } | NotMeasurable

/** One counted invocation: the skill name and the turn it was made in. */
type Invocation = { name: string; turnSpanId: string }

/** `extractInvokedSkills(records)`: one entry per counted call (deduplicated). */
type InvokedResult = { measurable: true; invocations: Invocation[] } | NotMeasurable

/** One row of the skills view. Token figures are always `derived`-named. */
type SkillRow = {
  name: string
  listed: boolean
  /** Absent when the invocation source is not measurable, never zero. */
  invocations?: number
  turnsListed: number
  /** Tokens counted from the description through the injected `countTokens`. */
  derivedTokens?: number
}

/**
 * `buildSkillsView(records, turnRecords, measurability, countTokens)`.
 * `reportedSkillTokens` is the harness counter, carried whole, never split.
 * `invocationsAvailability` is set when the invocation source is unmeasurable.
 */
type SkillsView =
  | {
      measurable: true
      rows: SkillRow[]
      reportedSkillTokens?: number
      invocationsAvailability?: NotMeasurable
    }
  | NotMeasurable

/** Attribute keys the module reads. Literal, as the plan names them. */
const LISTED_KEY = 'kyber.skills.listed'
const INVOKED_KEY = 'kyber.skills.invoked'
const COPILOT_LISTING_KEY = 'github.copilot.context.skills'
const ANTIGRAVITY_LISTING_KEY = 'gen_ai.skills'
const ANTIGRAVITY_SKILL_TOKENS_KEY = 'gen_ai.usage.skill_tokens'

// ---------------------------------------------------------------------------
// Fixture kit
// ---------------------------------------------------------------------------

const FIXTURE_DIR = join(dirname(fileURLToPath(import.meta.url)), 'fixtures', 'skills')

function capture(harness: string, file: string): string {
  return readFileSync(join(FIXTURE_DIR, harness, file), 'utf8')
}

/** The first non-blank line of a JSON Lines capture. */
function firstLine(text: string): string {
  const line = text.split('\n').find((candidate) => candidate.trim() !== '')
  if (line === undefined) throw new Error('capture is empty')
  return line
}

/** A JSON capture's `attributes` map, as the adapters receive it. */
function attributesOf(harness: string, file: string): Record<string, unknown> {
  const parsed = JSON.parse(capture(harness, file)) as { attributes: Record<string, unknown> }
  return { ...parsed.attributes }
}

// --- Claude Code: attachment of type skill_listing, and a Skill tool_use ----

const CLAUDE_SESSION = '00000000-0000-4000-8000-0000000000aa'
const CLAUDE_LISTING_TS = '2026-01-01T00:00:00.000Z'

type ClaudeListingAttachment = {
  attachment: {
    type: 'skill_listing'
    content: string
    skillCount: number
    isInitial: boolean
    names: string[]
  }
}

const claudeListing = JSON.parse(firstLine(capture('claude-code', 'capture-listing.jsonl'))) as ClaudeListingAttachment

/** `- name: description` lines, split at the first `: `. */
function parseListingContent(content: string): Array<{ name: string; description: string }> {
  return content.split('\n').map((line) => {
    const body = line.replace(/^- /, '')
    const cut = body.indexOf(': ')
    return { name: body.slice(0, cut), description: body.slice(cut + 2) }
  })
}

/** The skills the synthesizer would store under `kyber.skills.listed`. */
const claudeListedSkills = parseListingContent(claudeListing.attachment.content)

type ClaudeToolUse = { type: string; name: string; input: Record<string, unknown> }

const claudeInvoke = JSON.parse(firstLine(capture('claude-code', 'capture-invoke.jsonl'))) as {
  message: { content: ClaudeToolUse[] }
}

const claudeSkillCall = claudeInvoke.message.content.find((block) => block.type === 'tool_use')

// --- Copilot Chat: JSON-array string listing, optional XML system prompt ----

const copilotAttributes = attributesOf('copilot-chat', 'capture-listing.json')
const copilotSystemPromptXml = capture('copilot-chat', 'capture-listing-system-prompt.xml')

/** `<skill><name/><description/>` pairs from the captured XML block. */
function xmlDescriptions(xml: string): Map<string, string> {
  const found = new Map<string, string>()
  for (const match of xml.matchAll(/<skill>\s*<name>([^<]*)<\/name>\s*<description>([^<]*)<\/description>/g)) {
    found.set(match[1]!, match[2]!)
  }
  return found
}

const COPILOT_SIX_NAMES = [
  'app-docs-standard',
  'resharper-clt',
  'agent-customization',
  'create-pull-request',
  'architecture-decision-record',
  'azure-cli',
]

// --- Antigravity: 'Active Skills: a, b, c, d' on the llm.invoke span ---------

const antigravityAttributes = attributesOf('antigravity', 'capture-listing.json')

const ANTIGRAVITY_FOUR_NAMES = ['agy-customizations', 'antigravity-guide', 'chrome-extensions', 'modern-web-guidance']

// --- Record builders ---------------------------------------------------------

/** A record whose parts and flat content agree, as the adapters write them. */
function withParts(parts: ContentPart[]): Pick<CanonicalRecord, 'parts' | 'content'> {
  return { parts, content: contentFromParts(parts) }
}

/** A model-call turn record. */
function modelTurn(harness: string, spanId: string, over: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return turn(spanId, [], { source: harness, harness, op: 'llm.invoke', kind: 'client', ...over })
}

/** A child `tool.invoke` record: `name` is the tool, `args` its arguments. */
function toolCall(
  harness: string,
  spanId: string,
  parentSpanId: string,
  name: string,
  args: unknown,
  timestamp?: string,
): CanonicalRecord {
  return turn(spanId, [], {
    source: harness,
    harness,
    parentSpanId,
    op: 'tool.invoke',
    kind: 'internal',
    name,
    raw: { arguments: args },
    ...(timestamp !== undefined ? { timestamp } : {}),
  })
}

/** A Claude session-level skill listing record, initial or incremental. */
function claudeListingRecord(
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
    raw: { [LISTED_KEY]: { isInitial, skills } },
  })
}

/** A Claude model-call turn in the captured session. */
function claudeTurn(spanId: string, timestamp: string): CanonicalRecord {
  return modelTurn('claude-code', spanId, { source: 'claude-code', sessionId: CLAUDE_SESSION, timestamp })
}

/** The Copilot `invoke_agent` rollup record, carrying the captured listing. */
function copilotRollup(spanId: string, over: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return turn(spanId, [], {
    source: 'github-copilot',
    harness: 'copilot',
    op: 'agent.invoke',
    kind: 'internal',
    name: 'invoke_agent conductor',
    raw: { ...copilotAttributes },
    ...over,
  })
}

/** A Copilot model-call turn under a rollup. */
function copilotChat(spanId: string, parentSpanId: string | null): CanonicalRecord {
  return modelTurn('copilot', spanId, {
    source: 'github-copilot',
    parentSpanId,
    name: 'chat model',
    raw: {},
  })
}

/** Non-null lookup of a view row, failing with the skill's name. */
function rowNamed(view: SkillsView, name: string): SkillRow {
  if (!('rows' in view)) throw new Error(`view is not measurable, so has no row for ${name}`)
  const row = view.rows.find((candidate) => candidate.name === name)
  expect(row, `expected a row for ${name}`).toBeDefined()
  return row!
}

function namesOf(skills: ReadonlyArray<{ name: string }>): string[] {
  return skills.map((skill) => skill.name)
}

/** Token counter the tests inject: one token per character, enough to pin the wiring. */
function characterCount(): (text: string) => number {
  return vi.fn((text: string) => text.length)
}

// ---------------------------------------------------------------------------
// extractListedSkills: Claude Code skill_listing
// ---------------------------------------------------------------------------

describe('extractListedSkills, Claude Code skill_listing', () => {
  it('yields exactly the three captured skills, each with its description', () => {
    // The capture's own `names` must agree with its content lines, so the
    // expectation below is derived from the committed capture, not invented.
    expect(claudeListedSkills.map((skill) => skill.name)).toEqual(claudeListing.attachment.names)

    const record = claudeListingRecord('listing-initial', CLAUDE_LISTING_TS, true, claudeListedSkills)
    const result = extractListedSkills(record) as ListedResult

    expect(result).toMatchObject({ measurable: true })
    expect(result).toEqual({
      measurable: true,
      skills: [
        { name: 'app-docs-standard', description: claudeListedSkills[0]!.description, sourceKey: expect.any(String) },
        {
          name: 'architecture-decision-record',
          description: claudeListedSkills[1]!.description,
          sourceKey: expect.any(String),
        },
        { name: 'azure-cli', description: 'use the Azure CLI to interact with Azure resources.', sourceKey: expect.any(String) },
      ],
    })
  })

  it('gives every skill in one listing the same non-empty sourceKey', () => {
    const record = claudeListingRecord('listing-initial', CLAUDE_LISTING_TS, true, claudeListedSkills)
    const result = extractListedSkills(record) as ListedResult

    expect(result).toMatchObject({ measurable: true })
    const keys = (result as { skills: ListedSkill[] }).skills.map((skill) => skill.sourceKey)
    expect(keys.every((key) => key.length > 0)).toBe(true)
    expect(new Set(keys).size).toBe(1)
  })
})

// ---------------------------------------------------------------------------
// Listing over turns: initial replaces, non-initial unions
// ---------------------------------------------------------------------------

describe('listing effect on turns: union and replace', () => {
  it('a non-initial listing unions into the effective list', () => {
    const records = [
      claudeListingRecord('listing-initial', '2026-01-01T00:00:00.000Z', true, [
        { name: 'app-docs-standard' },
        { name: 'azure-cli' },
      ]),
      claudeListingRecord('listing-delta', '2026-01-01T00:00:01.500Z', false, [{ name: 'docs-review' }]),
    ]
    const turns = [claudeTurn('turn-1', '2026-01-01T00:00:02.000Z')]

    const view = buildSkillsView(
      [...records, ...turns],
      turns,
      {},
      characterCount(),
    ) as SkillsView

    expect(view).toMatchObject({ measurable: true })
    const listed = (view as { rows: SkillRow[] }).rows.filter((row) => row.listed).map((row) => row.name)
    expect(listed.sort()).toEqual(['app-docs-standard', 'azure-cli', 'docs-review'])
    expect(rowNamed(view, 'docs-review').turnsListed).toBe(1)
  })

  it('an initial listing replaces the effective list', () => {
    const records = [
      claudeListingRecord('listing-initial', '2026-01-01T00:00:00.000Z', true, [
        { name: 'app-docs-standard' },
        { name: 'azure-cli' },
      ]),
      claudeListingRecord('listing-replace', '2026-01-01T00:00:01.500Z', true, [{ name: 'docs-review' }]),
    ]
    const turns = [claudeTurn('turn-1', '2026-01-01T00:00:02.000Z')]

    const view = buildSkillsView([...records, ...turns], turns, {}, characterCount()) as SkillsView

    expect(view).toMatchObject({ measurable: true })
    const listed = (view as { rows: SkillRow[] }).rows.filter((row) => row.listed).map((row) => row.name)
    expect(listed).toEqual(['docs-review'])
    expect(rowNamed(view, 'docs-review').turnsListed).toBe(1)
  })
})

// ---------------------------------------------------------------------------
// extractListedSkills: Copilot
// ---------------------------------------------------------------------------

describe('extractListedSkills, Copilot listing', () => {
  it('yields the six names from the JSON-array string on the invoke_agent rollup', () => {
    expect(copilotAttributes[COPILOT_LISTING_KEY]).toEqual(expect.any(String))

    const result = extractListedSkills(copilotRollup('conductor-agent')) as ListedResult

    expect(result).toMatchObject({ measurable: true })
    expect(namesOf((result as { skills: ListedSkill[] }).skills)).toEqual(COPILOT_SIX_NAMES)
  })

  it('takes descriptions from the system_prompt XML block where it names the skill', () => {
    const xml = xmlDescriptions(copilotSystemPromptXml)
    expect(xml.size).toBe(3)

    const record = copilotRollup('conductor-agent', withParts([{ part: 'system_prompt', text: copilotSystemPromptXml }]))
    const result = extractListedSkills(record) as ListedResult
    const skills = (result as { skills: ListedSkill[] }).skills

    for (const [name, description] of xml) {
      expect(skills.find((skill) => skill.name === name)?.description, name).toBe(description)
    }
    expect(skills.find((skill) => skill.name === 'azure-cli')?.description).toBe(
      'use the Azure CLI to interact with Azure resources.',
    )
    // Named in the listing but absent from the XML block: the name stays, the description does not.
    expect(skills.find((skill) => skill.name === 'resharper-clt')).toBeDefined()
    expect(skills.find((skill) => skill.name === 'resharper-clt')?.description).toBeUndefined()
  })

  it('without the system_prompt part, keeps the six names and omits every description', () => {
    const result = extractListedSkills(copilotRollup('conductor-agent')) as ListedResult
    const skills = (result as { skills: ListedSkill[] }).skills

    expect(namesOf(skills)).toEqual(COPILOT_SIX_NAMES)
    expect(skills.every((skill) => skill.description === undefined)).toBe(true)
  })

  it('a malformed JSON string is not measurable, never an empty list', () => {
    const record = copilotRollup('conductor-agent', {
      raw: { [COPILOT_LISTING_KEY]: '["app-docs-standard",' },
    })
    const result = extractListedSkills(record)

    expect(result).toMatchObject({ availability: 'not_measurable', reason: expect.any(String) })
    expect(result).not.toHaveProperty('skills')
  })

  it('a JSON value that is not an array of names is not measurable', () => {
    const record = copilotRollup('conductor-agent', {
      raw: { [COPILOT_LISTING_KEY]: '{"skills":["app-docs-standard"]}' },
    })
    const result = extractListedSkills(record)

    expect(result).toMatchObject({ availability: 'not_measurable', reason: expect.any(String) })
    expect(result).not.toHaveProperty('skills')
  })
})

// ---------------------------------------------------------------------------
// extractListedSkills: Antigravity
// ---------------------------------------------------------------------------

describe('extractListedSkills, Antigravity listing', () => {
  it('reads the four names after the Active Skills prefix, with no descriptions', () => {
    expect(antigravityAttributes[ANTIGRAVITY_LISTING_KEY]).toMatch(/^Active Skills: /)

    const record = turn('chat-antigravity', [], {
      source: 'antigravity',
      harness: 'antigravity',
      op: 'llm.invoke',
      raw: { ...antigravityAttributes },
    })
    const result = extractListedSkills(record) as ListedResult

    expect(result).toMatchObject({ measurable: true })
    const skills = (result as { skills: ListedSkill[] }).skills
    expect(namesOf(skills)).toEqual(ANTIGRAVITY_FOUR_NAMES)
    expect(skills.every((skill) => skill.description === undefined)).toBe(true)
  })
})

// ---------------------------------------------------------------------------
// Rule text in the instruction_context bucket is never a skill
// ---------------------------------------------------------------------------

describe('instruction_context is never read as a skill listing', () => {
  it('Antigravity text that reached instruction_context without the raw listing key is not measurable', () => {
    // The Antigravity adapter routes `gen_ai.skills` into instruction_context.
    // Reading the bucket back would be a second, unsanctioned listing source.
    const record = turn('chat-antigravity', [], {
      source: 'antigravity',
      harness: 'antigravity',
      op: 'llm.invoke',
      raw: {},
      ...withParts([{ part: 'instruction_context', text: 'Active Skills: agy-customizations, antigravity-guide' }]),
    })
    const result = extractListedSkills(record)

    expect(result).toMatchObject({ availability: 'not_measurable', reason: expect.any(String) })
    expect(result).not.toHaveProperty('skills')
  })

  it('a Copilot rule that looks like the JSON listing is not reported as a skill', () => {
    const record = copilotRollup('conductor-agent', {
      raw: {},
      ...withParts([
        { part: 'instruction_context', text: `${COPILOT_LISTING_KEY}: ["fake-rule-skill"]` },
      ]),
    })
    const result = extractListedSkills(record)

    expect(result).toMatchObject({ availability: 'not_measurable', reason: expect.any(String) })
    expect(result).not.toHaveProperty('skills')
  })
})

// ---------------------------------------------------------------------------
// Harnesses with no listing source: not measurable, never []
// ---------------------------------------------------------------------------

describe('extractListedSkills, harnesses with no listing source', () => {
  it.each(['pi', 'opencode', 'copilot-cli', 'mystery-harness'])(
    '%s has no listing, so the listing is not measurable with a reason naming skills',
    (harness) => {
      const record = modelTurn(harness, `${harness}-turn`, {
        source: harness,
        raw: {},
        ...withParts([{ part: 'system_prompt', text: 'Available skills: docs, review' }]),
      })
      const result = extractListedSkills(record)

      expect(result).toMatchObject({ availability: 'not_measurable' })
      expect((result as NotMeasurable).reason).toMatch(/skill/i)
      expect(result).not.toHaveProperty('skills')
    },
  )
})

// ---------------------------------------------------------------------------
// extractInvokedSkills
// ---------------------------------------------------------------------------

describe('extractInvokedSkills', () => {
  it('counts a Skill tool call by its input.skill argument, not by the tool name', () => {
    expect(claudeSkillCall).toMatchObject({ name: 'Skill', input: { skill: 'writing-trigger-descriptions' } })

    const records = [
      claudeTurn('turn-1', '2026-01-01T00:00:01.000Z'),
      toolCall('claude-code', 'tool-1', 'turn-1', 'Skill', claudeSkillCall!.input, '2026-01-01T00:00:01.000Z'),
    ]
    const result = extractInvokedSkills(records) as InvokedResult

    expect(result).toMatchObject({ measurable: true })
    expect(result).toEqual({
      measurable: true,
      invocations: [{ name: 'writing-trigger-descriptions', turnSpanId: 'turn-1' }],
    })
  })

  it('accepts the lowercase skill tool name and string-encoded arguments', () => {
    const records = [toolCall('claude-code', 'tool-1', 'turn-1', 'skill', '{"skill":"azure-cli"}')]
    const result = extractInvokedSkills(records) as InvokedResult

    expect(namesOf((result as { invocations: Invocation[] }).invocations)).toEqual(['azure-cli'])
  })

  it('never reads a tool name as a skill name', () => {
    // A tool named after a skill, and a Skill call with no skill argument,
    // name no skill: the tool name is not evidence of an invocation.
    const records = [
      toolCall('claude-code', 'tool-1', 'turn-1', 'azure-cli', {}),
      toolCall('claude-code', 'tool-2', 'turn-1', 'Skill', {}),
    ]
    const result = extractInvokedSkills(records) as InvokedResult

    expect(result).toMatchObject({ measurable: true })
    expect((result as { invocations: Invocation[] }).invocations).toEqual([])
  })

  it('reads the turn-level kyber.skills.invoked list', () => {
    const records = [
      modelTurn('claude-code', 'turn-1', { raw: { [INVOKED_KEY]: ['docs', 'review'] } }),
    ]
    const result = extractInvokedSkills(records) as InvokedResult

    expect(namesOf((result as { invocations: Invocation[] }).invocations)).toEqual(['docs', 'review'])
  })

  it('counts one call once when the turn key and its own Skill tool record both report it', () => {
    const records = [
      modelTurn('claude-code', 'turn-1', { raw: { [INVOKED_KEY]: ['writing-trigger-descriptions'] } }),
      toolCall('claude-code', 'tool-1', 'turn-1', 'Skill', { skill: 'writing-trigger-descriptions' }),
    ]
    const result = extractInvokedSkills(records) as InvokedResult

    const counted = (result as { invocations: Invocation[] }).invocations.filter(
      (invocation) => invocation.name === 'writing-trigger-descriptions',
    )
    expect(counted).toHaveLength(1)
  })

  it('Copilot with no skill tool record is not measurable', () => {
    // The captured Copilot store holds view, task, bash and edit tool spans only.
    const records = [
      copilotChat('chat-1', 'conductor-agent'),
      toolCall('copilot', 'tool-view', 'chat-1', 'view', { path: 'README.md' }),
      toolCall('copilot', 'tool-bash', 'chat-1', 'bash', { command: 'ls' }),
      toolCall('copilot', 'tool-edit', 'chat-1', 'edit', {}),
      toolCall('copilot', 'tool-task', 'chat-1', 'task', {}),
    ]
    const result = extractInvokedSkills(records)

    expect(result).toMatchObject({ availability: 'not_measurable', reason: expect.any(String) })
    expect(result).not.toHaveProperty('invocations')
    expect((result as NotMeasurable).reason).toMatch(/skill/i)
  })

  it('Copilot with a skill tool record counts the call by its skill argument', () => {
    const records = [
      copilotChat('chat-1', 'conductor-agent'),
      toolCall('copilot', 'tool-skill', 'chat-1', 'skill', { skill: 'azure-cli' }),
    ]
    const result = extractInvokedSkills(records) as InvokedResult

    expect(namesOf((result as { invocations: Invocation[] }).invocations)).toEqual(['azure-cli'])
  })

  it('Antigravity invocation is not measurable, even with a skill-name attribute present', () => {
    // The capture also carries `github.copilot.tool.parameters.skill_name`
    // holding the listing text. That attribute is not an invocation.
    expect(antigravityAttributes['github.copilot.tool.parameters.skill_name']).toBeDefined()

    const record = turn('chat-antigravity', [], {
      source: 'antigravity',
      harness: 'antigravity',
      op: 'llm.invoke',
      raw: { ...antigravityAttributes },
    })
    const result = extractInvokedSkills([record])

    expect(result).toMatchObject({ availability: 'not_measurable', reason: expect.any(String) })
    expect(result).not.toHaveProperty('invocations')
  })
})

// ---------------------------------------------------------------------------
// buildSkillsView
// ---------------------------------------------------------------------------

describe('buildSkillsView, Claude Code', () => {
  it('rows carry listed, invocations and turnsListed; an invoked but unlisted skill is listed false', () => {
    const listing = claudeListingRecord('listing-initial', CLAUDE_LISTING_TS, true, claudeListedSkills)
    const turn1 = claudeTurn('turn-1', '2026-01-01T00:00:01.000Z')
    const turn2 = claudeTurn('turn-2', '2026-01-01T00:00:02.000Z')
    const skillCall = toolCall(
      'claude-code',
      'tool-1',
      'turn-2',
      'Skill',
      claudeSkillCall!.input,
      '2026-01-01T00:00:02.000Z',
    )

    const view = buildSkillsView([listing, turn1, turn2, skillCall], [turn1, turn2], {}, characterCount())

    expect(view).toMatchObject({ measurable: true })
    expect(rowNamed(view as SkillsView, 'app-docs-standard')).toMatchObject({
      listed: true,
      turnsListed: 2,
      invocations: 0,
    })
    expect(rowNamed(view as SkillsView, 'writing-trigger-descriptions')).toMatchObject({
      listed: false,
      turnsListed: 0,
      invocations: 1,
    })
  })

  it('derives a token count from each listed description, labelled derived; the harness has no reported skill total', () => {
    const countTokens = characterCount()
    const listing = claudeListingRecord('listing-initial', CLAUDE_LISTING_TS, true, claudeListedSkills)
    const turn1 = claudeTurn('turn-1', '2026-01-01T00:00:01.000Z')

    const view = buildSkillsView([listing, turn1], [turn1], {}, countTokens) as SkillsView

    const azure = 'use the Azure CLI to interact with Azure resources.'
    expect(rowNamed(view, 'azure-cli').derivedTokens).toBe(azure.length)
    expect(countTokens).toHaveBeenCalledWith(azure)
    expect(view).not.toHaveProperty('reportedSkillTokens')
    for (const row of (view as { rows: SkillRow[] }).rows) {
      expect(Object.keys(row).filter((key) => /token/i.test(key))).toEqual(
        row.derivedTokens === undefined ? [] : ['derivedTokens'],
      )
    }
  })

  it('a declared not_measurable listing refuses the view with the declared reason', () => {
    const listing = claudeListingRecord('listing-initial', CLAUDE_LISTING_TS, true, claudeListedSkills)
    const turn1 = claudeTurn('turn-1', '2026-01-01T00:00:01.000Z')
    const declared = notMeasurable('Pi exports no skill listing for this session.')

    const view = buildSkillsView([listing, turn1], [turn1], { skills_listed: declared }, characterCount())

    expect(view).toEqual(declared)
  })
})

describe('buildSkillsView, Copilot', () => {
  it('a rollup listing applies to the turns under it only, and invocations are omitted, never zero', () => {
    const rollup = copilotRollup('conductor-agent')
    const underRollup = copilotChat('chat-1', 'conductor-agent')
    const outside = copilotChat('chat-2', null)
    const view = buildSkillsView(
      [rollup, underRollup, outside, toolCall('copilot', 'tool-view', 'chat-1', 'view', {})],
      [underRollup, outside],
      {},
      characterCount(),
    ) as SkillsView

    expect(view).toMatchObject({ measurable: true })
    for (const name of COPILOT_SIX_NAMES) {
      const row = rowNamed(view, name)
      expect(row.listed, name).toBe(true)
      expect(row.turnsListed, name).toBe(1)
      expect(row.invocations, name).toBeUndefined()
    }
    expect((view as { invocationsAvailability?: NotMeasurable }).invocationsAvailability).toMatchObject({
      availability: 'not_measurable',
      reason: expect.any(String),
    })
  })
})

describe('buildSkillsView, Antigravity', () => {
  it('four listed rows, the harness skill_tokens carried whole and never split across skills', () => {
    const countTokens = characterCount()
    const chat = turn('chat-antigravity', [], {
      source: 'antigravity',
      harness: 'antigravity',
      op: 'llm.invoke',
      raw: { ...antigravityAttributes },
      ...withParts([{ part: 'instruction_context', text: String(antigravityAttributes[ANTIGRAVITY_LISTING_KEY]) }]),
    })

    const view = buildSkillsView([chat], [chat], {}, countTokens) as SkillsView

    expect(view).toMatchObject({ measurable: true, reportedSkillTokens: 1040 })
    expect(antigravityAttributes[ANTIGRAVITY_SKILL_TOKENS_KEY]).toBe(1040)
    const rows = (view as { rows: SkillRow[] }).rows
    expect(rows.map((row) => row.name).sort()).toEqual([...ANTIGRAVITY_FOUR_NAMES].sort())
    expect(rows.every((row) => row.listed && row.derivedTokens === undefined)).toBe(true)
    for (const row of rows) expect(Object.values(row)).not.toContain(1040)
    // No description means nothing to tokenize.
    expect(countTokens).not.toHaveBeenCalled()
    expect((view as { invocationsAvailability?: NotMeasurable }).invocationsAvailability).toMatchObject({
      availability: 'not_measurable',
    })
  })
})

describe('buildSkillsView, not measurable and wording', () => {
  it('a Pi session returns not_measurable with a reason naming skills, never rows', () => {
    const pi = modelTurn('pi', 'pi-turn', { source: 'pi', raw: {} })

    const view = buildSkillsView([pi], [pi], {}, characterCount())

    expect(view).toMatchObject({ availability: 'not_measurable', reason: expect.any(String) })
    expect(view).not.toHaveProperty('rows')
    expect((view as NotMeasurable).reason).toMatch(/skill/i)
  })

  it('no view or reason wording contains "waste"', () => {
    const claudeListingView = buildSkillsView(
      [claudeListingRecord('listing-initial', CLAUDE_LISTING_TS, true, claudeListedSkills)],
      [],
      {},
      characterCount(),
    )
    const pi = modelTurn('pi', 'pi-turn', { source: 'pi', raw: {} })
    const piView = buildSkillsView([pi], [pi], {}, characterCount())

    expect(JSON.stringify(claudeListingView)).not.toMatch(/waste/i)
    expect(JSON.stringify(piView)).not.toMatch(/waste/i)
  })
})
