// Tests for the invoked-skills attribute on synthesized turn records (T3, RED).
//
//   * The provider parsers already put the names of the skills a turn invoked
//     into `ParsedProviderCall.skills`. The synthesizer must carry them onto the
//     stored turn record's `raw` attribute map under the fixed key
//     `kyber.skills.invoked`, as an array of the skill name strings.
//   * A turn that invoked no skills carries no key at all: absent, not `[]`.
//   * Order and repeats follow the parser's first-seen order; the synthesizer
//     never adds an entry the parser did not report.
//   * Every harness source whose provider fills `skills` bumps its
//     `parserContractVersion`, so checkpoints written before this change are
//     re-read and the skill names upsert over the stale rows (ADR 0016 repair
//     contract). The recorded values below are the registry values at the
//     commit before this change; the bump must make each one strictly greater.

import { describe, expect, it } from 'vitest'

import type { ParsedProviderCall } from '../providers/types.js'
import { HARNESS_DESCRIPTORS } from '../refresh/registry.js'
import { Synthesizer } from './synth.js'

const SKILLS_KEY = 'kyber.skills.invoked'

/** A complete upstream `ParsedProviderCall`, with the spec's fields defaulted. */
function call(spec: Partial<ParsedProviderCall> = {}): ParsedProviderCall {
  return {
    provider: 'claude',
    model: 'claude-sonnet-4.5',
    inputTokens: 1_000,
    outputTokens: 240,
    cacheCreationInputTokens: 120,
    cacheReadInputTokens: 3_800,
    cachedInputTokens: 3_800,
    reasoningTokens: 0,
    webSearchRequests: 0,
    costUSD: 0.0123,
    tools: ['Skill', 'Read'],
    bashCommands: [],
    timestamp: '2026-08-29T12:00:00.000Z',
    speed: 'standard',
    deduplicationKey: 'claude:s-1:m-1',
    userMessage: 'write the docs',
    sessionId: 's-1',
    ...spec,
  }
}

/** The turn record's raw attribute map, narrowed from `unknown` for assertion. */
function rawOf(spec: Partial<ParsedProviderCall>): Record<string, unknown> {
  // synthesize() emits the turn record first, then any tool.invoke children.
  const [turn] = new Synthesizer().synthesize([call(spec)])
  expect(turn?.op).toBe('llm.invoke')
  return turn!.raw as Record<string, unknown>
}

// ---------------------------------------------------------------------------
// The invoked-skills attribute
// ---------------------------------------------------------------------------

describe('invoked skills on the turn record (kyber.skills.invoked)', () => {
  it('carries the parsed skill names onto raw under the fixed key, in order', () => {
    const raw = rawOf({ skills: ['docs', 'review'] })

    expect(raw[SKILLS_KEY]).toEqual(['docs', 'review'])
  })

  it('omits the key entirely when the parser reported no skills field', () => {
    const raw = rawOf({ skills: undefined })

    expect(Object.keys(raw)).not.toContain(SKILLS_KEY)
  })

  it('omits the key entirely when the parser reported an empty skills list', () => {
    const raw = rawOf({ skills: [] })

    expect(Object.keys(raw)).not.toContain(SKILLS_KEY)
  })

  it('keeps first-seen order, collapses repeats, and invents no entries', () => {
    // The tool list names 'Skill' and 'Read'; neither is a skill name, so
    // neither may appear under the key. Repeats collapse to first occurrence.
    const raw = rawOf({ skills: ['review', 'docs', 'review', 'docs', 'release'] })

    expect(raw[SKILLS_KEY]).toEqual(['review', 'docs', 'release'])
  })
})

// ---------------------------------------------------------------------------
// parserContractVersion bump for the skill-filling harness sources
// ---------------------------------------------------------------------------

/** Provider names whose parsers fill `ParsedProviderCall.skills`. */
const SKILL_FILLING_PROVIDERS: ReadonlySet<string> = new Set([
  'claude',
  'codex',
  'pi',
  'copilot',
  'kilo-code',
  'opencode',
  'cline-cli',
  'codewhale',
  'openclaude',
  'dsh',
])

/**
 * The parserContractVersion of each skill-filling harness source at the commit
 * before this change, read from HARNESS_DESCRIPTORS. The bump must make each
 * strictly greater.
 */
const RECORDED_CONTRACT_VERSIONS: Readonly<Record<string, string>> = {
  'claude-cli': '5',
  'claude-desktop': '5',
  'claude-unclassified': '5',
  'codex-cli': '2',
  'codex-desktop': '2',
  'codex-unclassified': '2',
  pi: '2',
  'copilot-cli': '1',
  'copilot-vscode': '1',
  'copilot-jetbrains': '1',
  'copilot-agent': '1',
  'kilo-shared-runtime': '2',
  'kilo-vscode-legacy': '2',
  opencode: '1',
  'cline-cli': '1',
  codewhale: '1',
  openclaude: '1',
  dsh: '1',
}

describe('parserContractVersion bump for skill-filling harness sources', () => {
  it.each(Object.entries(RECORDED_CONTRACT_VERSIONS))(
    '%s parserContractVersion is greater than its recorded value',
    (harnessId, recorded) => {
      const descriptor = HARNESS_DESCRIPTORS.find((d) => d.harnessId === harnessId)
      expect(descriptor, `no registry descriptor for ${harnessId}`).toBeDefined()

      expect(Number(descriptor!.parserContractVersion)).toBeGreaterThan(Number(recorded))
    },
  )

  it('pins every registry harness source whose provider fills skills', () => {
    const skillFilling = HARNESS_DESCRIPTORS
      .filter((d) => SKILL_FILLING_PROVIDERS.has(d.providerName))
      .map((d) => d.harnessId)
      .sort()

    expect(skillFilling).toEqual(Object.keys(RECORDED_CONTRACT_VERSIONS).sort())
  })
})
