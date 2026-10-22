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

import { parseClaudeApiRequestBody } from './adapters/claude-code.js'
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

describe('extractListedSkills, system prompt <skills> block without a listing key', () => {
  const prompt = [
    '<skills>',
    'Here is a list of skills.',
    '<skill>',
    '<name>app-docs-standard</name>',
    '<description>Apply the documentation standard.</description>',
    '<file>/x/SKILL.md</file>',
    '</skill>',
    '</skills>',
  ].join('\n')

  it('reads the listing from the prompt block', () => {
    const record = modelTurn('copilot', 'vscode-turn', {
      source: 'copilot',
      raw: {},
      ...withParts([{ part: 'system_prompt', text: prompt }]),
    })
    expect(extractListedSkills(record)).toMatchObject({
      measurable: true,
      skills: [{ name: 'app-docs-standard', description: 'Apply the documentation standard.' }],
    })
  })

  it('stays not measurable when the prompt has no skills block', () => {
    const record = modelTurn('copilot', 'vscode-turn-2', {
      source: 'copilot',
      raw: {},
      ...withParts([{ part: 'system_prompt', text: 'No block here' }]),
    })
    expect(extractListedSkills(record)).toMatchObject({ availability: 'not_measurable' })
  })

  it('stays not measurable when the skills block is empty or unparsable', () => {
    for (const emptyOrUnparsable of [
      '<skills></skills>',
      '<skills>some prose</skills>',
      '<available_skills></available_skills>',
      '<available_skills>some prose</available_skills>',
    ]) {
      const record = modelTurn('copilot', 'vscode-turn-empty', {
        source: 'copilot',
        raw: {},
        ...withParts([{ part: 'system_prompt', text: emptyOrUnparsable }]),
      })
      const result = extractListedSkills(record)
      expect(result).toMatchObject({ availability: 'not_measurable' })
      expect(result).not.toHaveProperty('skills')
    }
  })

  describe('readListing from system prompt', () => {
    it('parses name-only skills, inverted tags, interleaved tags, and empty descriptions', () => {
      const variedPrompt = [
        '<skills>',
        '<skill><name>standalone</name></skill>',
        '<skill>',
        '  <description>Inverted description</description>',
        '  <name>inverted-skill</name>',
        '</skill>',
        '<skill>',
        '  <name>interleaved-skill</name>',
        '  <file>/x/SKILL.md</file>',
        '  <description>Interleaved description</description>',
        '</skill>',
        '<skill>',
        '  <name>empty-desc-skill</name>',
        '  <description></description>',
        '</skill>',
        '<skill>',
        '  <name>whitespace-desc-skill</name>',
        '  <description>   \n\t   </description>',
        '</skill>',
        '</skills>',
      ].join('\n')

      const record = modelTurn('copilot', 'vscode-turn-varied', {
        source: 'copilot',
        raw: {},
        ...withParts([{ part: 'system_prompt', text: variedPrompt }]),
      })

      const result = extractListedSkills(record)
      expect(result).toMatchObject({
        measurable: true,
        skills: [
          { name: 'standalone' },
          { name: 'inverted-skill', description: 'Inverted description' },
          { name: 'interleaved-skill', description: 'Interleaved description' },
          { name: 'empty-desc-skill' },
          { name: 'whitespace-desc-skill' },
        ],
      })

      if ('skills' in result) {
        expect(result.skills.find((s) => s.name === 'standalone')?.description).toBeUndefined()
        expect(result.skills.find((s) => s.name === 'inverted-skill')?.description).toBe('Inverted description')
        expect(result.skills.find((s) => s.name === 'interleaved-skill')?.description).toBe('Interleaved description')
        expect(result.skills.find((s) => s.name === 'empty-desc-skill')?.description).toBeUndefined()
        expect(result.skills.find((s) => s.name === 'whitespace-desc-skill')?.description).toBeUndefined()
      }
    })

    it('safely skips skill blocks without a name or with empty/whitespace name', () => {
      const promptWithMalformedSkills = [
        '<skills>',
        '<skill><name>valid-before</name><description>Valid before</description></skill>',
        '<skill><description>No name tag</description></skill>',
        '<skill><name>   </name><description>Whitespace name</description></skill>',
        '<skill><name></name><description>Empty name</description></skill>',
        '<skill><name>valid-after</name><description>Valid after</description></skill>',
        '</skills>',
      ].join('\n')

      const record = modelTurn('copilot', 'vscode-turn-malformed-skills', {
        source: 'copilot',
        raw: {},
        ...withParts([{ part: 'system_prompt', text: promptWithMalformedSkills }]),
      })

      const result = extractListedSkills(record)
      expect(result).toMatchObject({
        measurable: true,
        skills: [
          { name: 'valid-before', description: 'Valid before' },
          { name: 'valid-after', description: 'Valid after' },
        ],
      })

      if ('skills' in result) {
        expect(result.skills).toHaveLength(2)
        expect(result.skills.map((s) => s.name)).toEqual(['valid-before', 'valid-after'])
      }
    })
  })
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

// Antigravity invocations come from the transcript (D6): a transcript-covered turn
// carries `kyber.skills.invoked`, even as `[]` (measured, none invoked). A session
// whose records carry no such key has no transcript behind it.
const SYNTHETIC_AGY_LISTING = 'Active Skills: synthetic-alpha, synthetic-beta'

/** An Antigravity model turn carrying the synthetic listing and, when given, the transcript's invoked list. */
function antigravityTurn(spanId: string, invoked?: string[]): CanonicalRecord {
  return modelTurn('antigravity', spanId, {
    source: 'antigravity',
    raw: {
      [ANTIGRAVITY_LISTING_KEY]: SYNTHETIC_AGY_LISTING,
      ...(invoked !== undefined ? { [INVOKED_KEY]: invoked } : {}),
    },
  })
}

describe('buildSkillsView, Antigravity invocations from the transcript', () => {
  it('a transcript-covered session with no skill load reports every listed skill as measured zero', () => {
    const turns = [antigravityTurn('turn-1', []), antigravityTurn('turn-2', [])]
    const view = buildSkillsView(turns, turns, {}, characterCount()) as SkillsView

    expect(view).toMatchObject({ measurable: true })
    for (const name of ['synthetic-alpha', 'synthetic-beta']) {
      expect(rowNamed(view, name).invocations, name).toBe(0)
    }
    expect((view as { invocationsAvailability?: NotMeasurable }).invocationsAvailability).toBeUndefined()
  })

  it('counts each skill once per covered turn that names it', () => {
    const turns = [
      antigravityTurn('turn-1', ['synthetic-alpha']),
      antigravityTurn('turn-2', ['synthetic-alpha', 'synthetic-beta']),
      antigravityTurn('turn-3', []),
    ]
    const view = buildSkillsView(turns, turns, {}, characterCount()) as SkillsView

    expect(rowNamed(view, 'synthetic-alpha').invocations).toBe(2)
    expect(rowNamed(view, 'synthetic-beta').invocations).toBe(1)
  })

  it('a session with no kyber.skills.invoked key reports invocations not measurable, naming the transcript', () => {
    const turns = [antigravityTurn('turn-1')]
    const view = buildSkillsView(turns, turns, {}, characterCount()) as SkillsView

    expect(view).toMatchObject({ measurable: true })
    expect((view as { invocationsAvailability?: NotMeasurable }).invocationsAvailability).toMatchObject({
      availability: 'not_measurable',
      reason: expect.stringMatching(/transcript/i),
    })
    expect(rowNamed(view, 'synthetic-alpha')).not.toHaveProperty('invocations')
  })

  it('a Copilot session without a skill record keeps its wording: the reason does not name a transcript', () => {
    const rollup = copilotRollup('conductor-agent')
    const chat = copilotChat('chat-1', 'conductor-agent')
    const view = buildSkillsView([rollup, chat], [chat], {}, characterCount()) as SkillsView

    expect(view).toMatchObject({ measurable: true })
    const availability = (view as { invocationsAvailability?: NotMeasurable }).invocationsAvailability
    expect(availability).toMatchObject({ availability: 'not_measurable' })
    expect(availability!.reason).not.toMatch(/transcript/i)
  })
})

// D30: an Antigravity turn is measured for invocations only where its record carries
// `kyber.skills.invoked`, `[]` included. When some turns are covered and some are not,
// the figures are measured over the covered turns and the view says how many they were.
describe('buildSkillsView, Antigravity invocations per covered turn (D30)', () => {
  // turn-1 carries no key (not covered), turn-2 is measured none, turn-3 names alpha.
  const partiallyCovered = (): CanonicalRecord[] => [
    antigravityTurn('turn-1'),
    antigravityTurn('turn-2', []),
    antigravityTurn('turn-3', ['synthetic-alpha']),
  ]

  it('measures invocations over the covered turns: alpha once, beta measured none', () => {
    const turns = partiallyCovered()
    const view = buildSkillsView(turns, turns, {}, characterCount()) as SkillsView

    expect(view).toMatchObject({ measurable: true })
    expect(rowNamed(view, 'synthetic-alpha').invocations).toBe(1)
    expect(rowNamed(view, 'synthetic-beta').invocations).toBe(0)
    expect((view as { invocationsAvailability?: NotMeasurable }).invocationsAvailability).toBeUndefined()
  })

  it('states the covered and total turn counts when only some turns are covered', () => {
    const turns = partiallyCovered()
    const view = buildSkillsView(turns, turns, {}, characterCount())

    expect(view).toHaveProperty('invocationsCoverage', { measuredTurns: 2, totalTurns: 3 })
  })

  it('omits invocationsCoverage when every Antigravity turn is covered', () => {
    const turns = [antigravityTurn('turn-1', []), antigravityTurn('turn-2', ['synthetic-alpha'])]
    const view = buildSkillsView(turns, turns, {}, characterCount())

    expect(view).toMatchObject({ measurable: true })
    expect(view).not.toHaveProperty('invocationsCoverage')
  })

  it('omits invocationsCoverage when no turn is covered, and the invocations stay not measurable', () => {
    const turns = [antigravityTurn('turn-1'), antigravityTurn('turn-2')]
    const view = buildSkillsView(turns, turns, {}, characterCount()) as SkillsView

    expect(view).not.toHaveProperty('invocationsCoverage')
    expect((view as { invocationsAvailability?: NotMeasurable }).invocationsAvailability).toMatchObject({
      availability: 'not_measurable',
    })
  })

  it('leaves a Claude Code session, which has no transcript coverage, without invocationsCoverage', () => {
    const turn1 = claudeTurn('turn-1', '2026-01-01T00:00:01.000Z')
    const records = [
      turn1,
      toolCall('claude-code', 'tool-1', 'turn-1', 'Skill', { skill: 'docs' }, '2026-01-01T00:00:01.000Z'),
    ]
    const view = buildSkillsView(records, [turn1], {}, characterCount())

    expect(view).not.toHaveProperty('invocationsCoverage')
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

// ---------------------------------------------------------------------------
// Claude request-body listing (D6, D14, D21)
//
// Claude Code Desktop writes its skill listing into a `role: "system"` message
// of the API request body, not into a transcript attachment. The body parser
// turns that message into a `conversation_history` part, and the role is what
// separates the sender's reminder from text a user quoted. A listing is read
// only from system-role parts and system-prompt parts. A body that does not
// parse yields no parts, so no listing, and the reader never parses partial text.
// ---------------------------------------------------------------------------

/** The skill-listing reminder as the sender writes it inside a system-role message. */
const BODY_LISTING_MARKER = 'The following skills are available for use with the Skill tool:'
const BODY_LISTED = [
  { name: 'synthetic-skill-alpha', description: 'Synthetic description for alpha.' },
  { name: 'synthetic-skill-beta', description: 'Synthetic description for beta.' },
]
const BODY_LISTING_TEXT = [
  BODY_LISTING_MARKER,
  '',
  ...BODY_LISTED.map((skill) => `- ${skill.name}: ${skill.description}`),
].join('\n')

/** The T0 Desktop OTLP fixture, sanitized; its `api_request_body` logs carry real request-body structure. */
const DESKTOP_LOGS_PATH = join(
  dirname(fileURLToPath(import.meta.url)),
  '..',
  'otel',
  'fixtures',
  'claude-desktop',
  'logs.otlp.json',
)

/** The `api_request_body` log bodies in the T0 Desktop fixture, in file order. */
function desktopRequestBodies(): string[] {
  const exported = JSON.parse(readFileSync(DESKTOP_LOGS_PATH, 'utf8')) as {
    resourceLogs: Array<{
      scopeLogs: Array<{
        logRecords: Array<{ attributes: Array<{ key: string; value: { stringValue?: string } }> }>
      }>
    }>
  }
  return exported.resourceLogs
    .flatMap((resource) => resource.scopeLogs.flatMap((scope) => scope.logRecords))
    .map((log) => log.attributes.find((attribute) => attribute.key === 'body')?.value.stringValue)
    .filter((body): body is string => body !== undefined)
}

/** The one fixture body that carries the skill-listing reminder, failing by name if it is gone. */
function desktopListingBody(): string {
  const body = desktopRequestBodies().find((candidate) => candidate.includes(BODY_LISTING_MARKER))
  expect(body, 'the T0 fixture should carry one body with the skill listing').toBeDefined()
  return body!
}

/** A Claude model-call record whose parts came from a request body, with no transcript listing. */
function bodyRecord(parts: ContentPart[], over: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return modelTurn('claude-code', 'body-turn', {
    source: 'claude-code',
    sessionId: CLAUDE_SESSION,
    raw: {},
    ...withParts(parts),
    ...over,
  })
}

describe('extractListedSkills, Claude request-body listing in a system-role message', () => {
  it('reads the names and descriptions from a system-role part when no transcript listed the skills', () => {
    const record = bodyRecord([{ part: 'conversation_history', text: BODY_LISTING_TEXT, role: 'system' }])

    expect(extractListedSkills(record)).toMatchObject({ measurable: true, skills: BODY_LISTED })
  })

  it.each(['claude-code', 'claude-desktop'])(
    'reads the body listing for a %s record, which the Claude family covers',
    (harness) => {
      const record = bodyRecord([{ part: 'conversation_history', text: BODY_LISTING_TEXT, role: 'system' }], {
        harness,
        source: harness,
      })

      expect(extractListedSkills(record)).toMatchObject({ measurable: true, skills: BODY_LISTED })
    },
  )

  it('reads the listing from a system_prompt part that carries the marker', () => {
    const record = bodyRecord([{ part: 'system_prompt', text: BODY_LISTING_TEXT }])

    expect(extractListedSkills(record)).toMatchObject({ measurable: true, skills: BODY_LISTED })
  })

  it('reads the listing from the T0 Desktop fixture body, which keeps the sender structure', () => {
    const record = bodyRecord(parseClaudeApiRequestBody(desktopListingBody()))

    const result = extractListedSkills(record)
    expect(result).toMatchObject({ measurable: true })
    const skills = (result as { skills: ListedSkill[] }).skills
    expect(namesOf(skills)).toEqual(Array.from({ length: 12 }, (_unused, index) => `sanitized-skill-${index + 1}`))
  })

  it.each(['user', 'assistant'] as const)(
    'a listing quoted in %s message yields no listing',
    (role) => {
      const record = bodyRecord([{ part: 'conversation_history', text: BODY_LISTING_TEXT, role }])

      expect(extractListedSkills(record)).toMatchObject({ availability: 'not_measurable' })
      expect(extractListedSkills(record)).not.toHaveProperty('skills')
    },
  )

  it('reads only the system-role listing when the marker is also quoted in a user message', () => {
    const quoted = [BODY_LISTING_MARKER, '', '- synthetic-quoted-skill: Quoted by the user.'].join('\n')
    const record = bodyRecord([
      { part: 'conversation_history', text: quoted, role: 'user' },
      { part: 'conversation_history', text: BODY_LISTING_TEXT, role: 'system' },
    ])

    expect(extractListedSkills(record)).toMatchObject({ measurable: true, skills: BODY_LISTED })
  })

  it('ignores a marker in a conversation_history part that carries no role', () => {
    const record = bodyRecord([{ part: 'conversation_history', text: BODY_LISTING_TEXT }])
    const result = extractListedSkills(record)

    expect(result).toMatchObject({ availability: 'not_measurable' })
    expect((result as NotMeasurable).reason).toMatch(/skill/i)
  })

  it('stays not measurable when the marker has no entries after it', () => {
    const record = bodyRecord([
      { part: 'conversation_history', text: `${BODY_LISTING_MARKER}\n\n`, role: 'system' },
    ])

    const result = extractListedSkills(record)

    expect(result).toMatchObject({ availability: 'not_measurable' })
    expect((result as NotMeasurable).reason).toMatch(/skill/i)
    expect(result).not.toHaveProperty('skills')
  })

  it('does not read a system-role marker as a listing for a harness that is not Claude', () => {
    const parts: ContentPart[] = [{ part: 'conversation_history', text: BODY_LISTING_TEXT, role: 'system' }]
    const copilot = modelTurn('copilot', 'copilot-body-turn', { source: 'github-copilot', ...withParts(parts) })

    expect(extractListedSkills(copilot)).toMatchObject({ availability: 'not_measurable' })
  })

  it('a truncated body that cuts the listing short yields no listing and no partial parse', () => {
    const body = desktopListingBody()
    const cutAt = body.indexOf('- sanitized-skill-6') + '- sanitized-skill-6'.length
    const truncated = body.slice(0, cutAt)
    expect(truncated).toContain(BODY_LISTING_MARKER)

    const parts = parseClaudeApiRequestBody(truncated)
    expect(parts).toEqual([])
    expect(extractListedSkills(bodyRecord(parts))).toMatchObject({ availability: 'not_measurable' })
  })

  it('a transcript listing keeps priority over the body listing', () => {
    const record = bodyRecord(
      [{ part: 'conversation_history', text: BODY_LISTING_TEXT, role: 'system' }],
      {
        raw: {
          [LISTED_KEY]: {
            isInitial: true,
            skills: [{ name: 'synthetic-transcript-skill', description: 'Listed by the transcript.' }],
          },
        },
      },
    )

    expect(extractListedSkills(record)).toMatchObject({ measurable: true, skills: [{ name: 'synthetic-transcript-skill' }] })
  })

  it('feeds the skills view: body-listed skills become listed rows with a derived token count', () => {
    const record = bodyRecord([{ part: 'conversation_history', text: BODY_LISTING_TEXT, role: 'system' }])

    const view = buildSkillsView([record], [record], {}, characterCount()) as SkillsView

    expect(rowNamed(view, 'synthetic-skill-alpha')).toMatchObject({ listed: true, turnsListed: 1 })
    expect(rowNamed(view, 'synthetic-skill-alpha').derivedTokens).toBe(BODY_LISTED[0]!.description.length)
  })
})

describe('extractListedSkills, request-body entry grammar', () => {
  const listing = (...lines: string[]): CanonicalRecord =>
    bodyRecord([{ part: 'conversation_history', text: [BODY_LISTING_MARKER, '', ...lines].join('\n'), role: 'system' }])

  it('keeps a plugin-namespaced name whole and a description that itself contains a colon', () => {
    const result = extractListedSkills(listing('- plugin:skill-name: Description with: a colon'))

    expect(result).toMatchObject({
      measurable: true,
      skills: [{ name: 'plugin:skill-name', description: 'Description with: a colon' }],
    })
  })

  it('splits the real namespaced listings on the first colon followed by whitespace', () => {
    const result = extractListedSkills(
      listing(
        '- code-review:code-review: Code review a pull request',
        '- engineering:debug: Structured debugging session: reproduce, isolate, diagnose',
      ),
    )

    expect(result).toMatchObject({
      skills: [
        { name: 'code-review:code-review', description: 'Code review a pull request' },
        { name: 'engineering:debug', description: 'Structured debugging session: reproduce, isolate, diagnose' },
      ],
    })
  })

  it('keeps a name whose internal colon is not followed by whitespace', () => {
    const result = extractListedSkills(listing('- a:b:c: tail text'))

    expect(result).toMatchObject({ skills: [{ name: 'a:b:c', description: 'tail text' }] })
  })

  it('reads a name with no description', () => {
    const result = extractListedSkills(listing('- bare-skill:', '- other: Has one'))

    expect(result).toMatchObject({
      skills: [{ name: 'bare-skill' }, { name: 'other', description: 'Has one' }],
    })
  })

  it('joins an indented continuation line onto the previous description and keeps later entries', () => {
    const result = extractListedSkills(
      listing('- first: Starts here', '  and wraps onto a second line', '    and a third', '- second: Plain', '- third: Last'),
    )

    expect(result).toMatchObject({
      measurable: true,
      skills: [
        { name: 'first', description: 'Starts here and wraps onto a second line and a third' },
        { name: 'second', description: 'Plain' },
        { name: 'third', description: 'Last' },
      ],
    })
  })

  it('ends the listing at a blank line, so entries after it are not read', () => {
    const result = extractListedSkills(listing('- one: First', '- two: Second', '', '- three: After the blank'))

    expect(result).toMatchObject({ skills: [{ name: 'one' }, { name: 'two' }] })
    expect((result as { skills: unknown[] }).skills).toHaveLength(2)
  })

  it('ends the listing at an unindented non-entry line', () => {
    const result = extractListedSkills(listing('- one: First', 'Some other prose', '- two: Not read'))

    expect((result as { skills: unknown[] }).skills).toHaveLength(1)
  })
})
