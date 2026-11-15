// End-to-end skills path for real Claude Code transcript shapes (T7 blocker 1, RED).
//
// The real ingest path stamps records with the harness identity the source unit
// was classified as: `claude-cli` or `claude-desktop`. The canonical `claude-code`
// is applied later, in buildSessionRow. The skills engine, and the signal and
// finding that read it, must therefore treat those identities as the Claude
// harness: a real Claude session that listed skills and invoked none has a
// MEASURED zero invocations, not an unmeasurable one. Today they do not, so the
// invocation side reads as not measurable and the inactive-skill finding goes
// silent for exactly the sessions it exists for.
//
// Path under test, in order:
//   loadClaudeCalls(transcript)  -> ParsedProviderCall[]          (providers/claude.ts)
//   ingestProviders(['claude'], loader) -> CanonicalRecord[]      (synth/provider.ts)
//   buildSessionRow(sessionId, records, countTokens) -> payload   (canon/sessions.ts)
//   computeSignals({ harness, records }).skillUtilisation         (analysis/signals.ts)
//   detectInactiveSkillReference({ records, sessionId })          (analysis/findings.ts)
//
// Transcript lines are the committed captures (`fixtures/skills/claude-code/`)
// plus small synthetic assistant lines that wrap the captured `tool_use` block in
// a usage-bearing message. No user data is embedded.

import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { afterAll, describe, expect, it } from 'vitest'

import { computeSignals, isSignalMeasurable } from '../analysis/signals.js'
import { detectInactiveSkillReference } from '../analysis/findings.js'
import { loadClaudeCalls } from '../providers/claude.js'
import { ingestProviders } from '../synth/provider.js'
import type { ProviderLoader } from '../synth/provider.js'
import { turn } from './fixtures/records.js'
import { buildSessionRow } from './sessions.js'
import type { CanonicalRecord } from './types.js'

// ---------------------------------------------------------------------------
// Fixture kit
// ---------------------------------------------------------------------------

const CLAUDE_FIXTURES = join(dirname(fileURLToPath(import.meta.url)), 'fixtures', 'skills', 'claude-code')
const SESSION = '00000000-0000-4000-8000-0000000000aa'
const LISTED = ['app-docs-standard', 'architecture-decision-record', 'azure-cli']
const HARNESS_IDS = ['claude-cli', 'claude-desktop'] as const

function firstLine(text: string): string {
  const line = text.split('\n').find((candidate) => candidate.trim() !== '')
  if (line === undefined) throw new Error('capture is empty')
  return line
}

/** The captured skill_listing attachment line, verbatim. */
const listingLine = firstLine(readFileSync(join(CLAUDE_FIXTURES, 'capture-listing.jsonl'), 'utf8'))

/** The captured Skill tool_use block, used as the template for each Skill call. */
const captured = JSON.parse(firstLine(readFileSync(join(CLAUDE_FIXTURES, 'capture-invoke.jsonl'), 'utf8'))) as {
  message: { content: Array<Record<string, unknown>> }
}
const capturedSkillBlock = captured.message.content.find((block) => block.type === 'tool_use')!

function skillBlock(id: string, skill: string): Record<string, unknown> {
  return { ...capturedSkillBlock, id, input: { skill } }
}

function readBlock(id: string): Record<string, unknown> {
  return { type: 'tool_use', id, name: 'Read', input: { file_path: 'README.md' }, caller: { type: 'direct' } }
}

/** An assistant line with usage, so the loader turns it into one provider call. */
function assistantLine(uuid: string, timestamp: string, content: unknown[]): string {
  return JSON.stringify({
    type: 'assistant',
    uuid,
    sessionId: SESSION,
    timestamp,
    message: {
      id: `msg-${uuid}`,
      model: 'claude-sonnet-4-5',
      role: 'assistant',
      content,
      usage: { input_tokens: 120, output_tokens: 30, cache_creation_input_tokens: 0, cache_read_input_tokens: 0 },
    },
  })
}

const T1 = '2026-01-01T00:00:01.000Z'
const T2 = '2026-01-01T00:00:02.000Z'

const countTokens = (text: string): number => text.length

const dirs: string[] = []
afterAll(() => {
  for (const dir of dirs) rmSync(dir, { recursive: true, force: true })
})

/**
 * Writes the transcript to a per-test temp directory, loads it through the real
 * Claude loader, and ingests it under the given harness identity.
 */
async function ingest(harnessId: string, lines: string[]): Promise<CanonicalRecord[]> {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-skills-e2e-'))
  dirs.push(dir)
  const file = join(dir, `${SESSION}.jsonl`)
  writeFileSync(file, `${lines.join('\n')}\n`)
  const loader: ProviderLoader = () => ({
    calls: loadClaudeCalls(file),
    filePath: file,
    harnessId,
    sourceKey: `${harnessId}:${SESSION}`,
  })
  const result = await ingestProviders(['claude'], loader)
  expect(result.problems).toEqual([])
  return result.records
}

type SkillsPayload = {
  listed_count: number
  listed_uninvoked_count?: number
  rows: Array<{ name: string; listed: boolean; invocations: unknown; turns_listed: number[] }>
}
type SessionPayload = { harness: string; skills?: SkillsPayload; summary: { skills_invoked?: string[] } }

function payloadOf(records: readonly CanonicalRecord[]): SessionPayload {
  return buildSessionRow(SESSION, records, countTokens).payload as SessionPayload
}

function rowOf(payload: SessionPayload, name: string) {
  const row = payload.skills?.rows.find((candidate) => candidate.name === name)
  expect(row, `expected a skills row for ${name}`).toBeDefined()
  return row!
}

// ---------------------------------------------------------------------------
// Real ingest path, both real Claude harness identities
// ---------------------------------------------------------------------------

describe.each(HARNESS_IDS)('real Claude path with harness %s', (harnessId) => {
  it('(a) a session with Skill calls: the invoked skills count 1, the uninvoked listed skill is counted', async () => {
    const records = await ingest(harnessId, [
      listingLine,
      assistantLine('turn-1', T1, [skillBlock('toolu_a', 'app-docs-standard')]),
      assistantLine('turn-2', T2, [skillBlock('toolu_b', 'azure-cli')]),
    ])
    const payload = payloadOf(records)

    expect(payload.skills).toBeDefined()
    expect(payload.skills!.listed_count).toBe(3)
    expect(rowOf(payload, 'app-docs-standard').invocations).toBe(1)
    expect(rowOf(payload, 'azure-cli').invocations).toBe(1)
    expect(rowOf(payload, 'architecture-decision-record').invocations).toBe(0)
    expect(payload.skills!.listed_uninvoked_count).toBe(1)
    expect(payload.summary.skills_invoked).toEqual(['app-docs-standard', 'azure-cli'])
  })

  it.each([
    ['a text-only turn', []],
    ['a Read tool call', [readBlock('toolu_read')]],
  ])('(b) with no Skill call (%s): every listed row is the measured number 0', async (_label, blocks) => {
    const records = await ingest(harnessId, [listingLine, assistantLine('turn-1', T1, blocks)])
    const payload = payloadOf(records)

    for (const name of LISTED) {
      expect(rowOf(payload, name).invocations, name).toBe(0)
    }
    expect(payload.skills!.listed_uninvoked_count).toBe(LISTED.length)
    expect(payload.summary.skills_invoked).toEqual([])
  })

  it('(c) the signal and the finding read the same measured records: skill utilisation is measurable and the never-invoked listed skill is reported', async () => {
    const records = await ingest(harnessId, [
      listingLine,
      assistantLine('turn-1', T1, [skillBlock('toolu_a', 'app-docs-standard')]),
      assistantLine('turn-2', T2, [skillBlock('toolu_b', 'azure-cli')]),
    ])

    const signals = computeSignals({ harness: 'claude-code', records })
    expect(isSignalMeasurable(signals.skillUtilisation), JSON.stringify(signals.skillUtilisation)).toBe(true)

    const findings = detectInactiveSkillReference({ records, sessionId: SESSION })
    const inactive = findings.filter((finding) => finding.detectorId === 'inactive-skill-reference')
    expect(inactive.length).toBeGreaterThan(0)
    const mentioned = JSON.stringify(inactive)
    expect(mentioned).toContain('architecture-decision-record')
    expect(mentioned).not.toContain('app-docs-standard')
  })

  it('(c) with no Skill call, the finding is not silent: every listed skill is reported as never invoked', async () => {
    const records = await ingest(harnessId, [listingLine, assistantLine('turn-1', T1, [])])

    const signals = computeSignals({ harness: 'claude-code', records })
    expect(isSignalMeasurable(signals.skillUtilisation), JSON.stringify(signals.skillUtilisation)).toBe(true)
    expect(signals.skillUtilisation).toMatchObject({ value: 0 })

    const findings = detectInactiveSkillReference({ records, sessionId: SESSION })
    const mentioned = JSON.stringify(findings.filter((finding) => finding.detectorId === 'inactive-skill-reference'))
    for (const name of LISTED) expect(mentioned, name).toContain(name)
  })
})

// ---------------------------------------------------------------------------
// Hand-built canonical 'claude-code' control: the same expectations, already true
// ---------------------------------------------------------------------------

describe('hand-built claude-code control', () => {
  const listing = turn('listing-1', [], {
    source: 'claude-code',
    harness: 'claude-code',
    op: 'skill.listing',
    kind: 'internal',
    sessionId: SESSION,
    timestamp: '2026-01-01T00:00:00.000Z',
    raw: { 'kyber.skills.listed': { isInitial: true, skills: LISTED.map((name) => ({ name })) } },
  })
  const llm = (spanId: string, timestamp: string, raw: Record<string, unknown> = {}) =>
    turn(spanId, [], {
      source: 'claude-code',
      harness: 'claude-code',
      op: 'llm.invoke',
      kind: 'client',
      sessionId: SESSION,
      timestamp,
      raw,
    })

  it('a Skill call counts once and the uninvoked listed skill is counted', () => {
    const turn1 = llm('turn-1', T1)
    const call = turn('tool-1', [], {
      source: 'claude-code',
      harness: 'claude-code',
      op: 'tool.invoke',
      kind: 'internal',
      name: 'Skill',
      parentSpanId: 'turn-1',
      sessionId: SESSION,
      timestamp: T1,
      raw: { arguments: { skill: 'azure-cli' } },
    })
    const payload = payloadOf([listing, turn1, call])

    expect(rowOf(payload, 'azure-cli').invocations).toBe(1)
    expect(rowOf(payload, 'app-docs-standard').invocations).toBe(0)
    expect(payload.skills!.listed_uninvoked_count).toBe(2)
  })

  it('no Skill call gives every listed row the measured 0 and an empty summary list', () => {
    const payload = payloadOf([listing, llm('turn-1', T1)])

    for (const name of LISTED) expect(rowOf(payload, name).invocations, name).toBe(0)
    expect(payload.skills!.listed_uninvoked_count).toBe(3)
    expect(payload.summary.skills_invoked).toEqual([])
  })
})
