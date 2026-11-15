// Tests for the skill listing carried from a Claude transcript onto the provider
// call that follows it (T10, RED).
//
//   * Claude Code writes the skills available to a session as an `attachment`
//     line of type `skill_listing`. Its `content` is one `- name: description`
//     line per skill. The listing is not a turn of its own: it attaches to the
//     ParsedProviderCall produced by the assistant turn that FOLLOWS it.
//   * The field is `ParsedProviderCall.skillsListed` = { isInitial, skills }.
//     The parser carries each listing as it is seen. Whether a later listing
//     replaces or unions with an earlier one is decided downstream, not here.
//   * A transcript with no listing leaves `skillsListed` absent, not empty.
//   * Parsing is by `loadClaudeCalls`, the producer of ParsedProviderCall for
//     Claude transcripts. A line over the 32 KiB large-line threshold used by
//     the ingest parser (parser.ts LARGE_JSONL_LINE_BYTES) must give the same
//     result as a small one.

import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { loadClaudeCalls } from '../providers/claude.js'
import { compactEntry, parseJsonlLine } from './parser.js'

const CAPTURE_PATH = join(
  dirname(fileURLToPath(import.meta.url)),
  '../canon/fixtures/skills/claude-code/capture-listing.jsonl',
)

/** parser.ts LARGE_JSONL_LINE_BYTES: lines longer than this take the large-line path. */
const LARGE_JSONL_LINE_BYTES = 32 * 1024

const SESSION_ID = 'skills-listing-session'

/** The committed sanitized real-shape capture: one `skill_listing` attachment line. */
function captureLine(): string {
  const lines = readFileSync(CAPTURE_PATH, 'utf8').split(/\r?\n/).filter((line) => line.trim() !== '')
  expect(lines).toHaveLength(1)
  return lines[0]!
}

/**
 * The listing the capture must parse to, derived from the capture's own
 * `content` lines ("- name: description") rather than hand-copied, so the
 * descriptions are compared exactly as written in the file.
 */
function expectedInitialListing(): { isInitial: boolean; skills: Array<{ name: string; description: string }> } {
  const { attachment } = JSON.parse(captureLine()) as {
    attachment: { content: string; names: string[]; isInitial: boolean }
  }
  const skills = attachment.content.split('\n').map((line) => {
    const body = line.replace(/^- /, '')
    const separator = body.indexOf(': ')
    return { name: body.slice(0, separator), description: body.slice(separator + 2) }
  })
  // The derivation is only a valid oracle if it agrees with the capture's own name list.
  expect(skills.map((skill) => skill.name)).toEqual(attachment.names)
  return { isInitial: attachment.isInitial, skills }
}

/** A capture line padded past the large-line threshold, same record otherwise. */
function largeCaptureLine(): string {
  const record = JSON.parse(captureLine()) as Record<string, unknown>
  record['padding'] = 'x'.repeat(40_000)
  const line = JSON.stringify(record)
  expect(line.length).toBeGreaterThan(LARGE_JSONL_LINE_BYTES)
  return line
}

function userLine(): string {
  return JSON.stringify({
    type: 'user',
    sessionId: SESSION_ID,
    uuid: 'user-1',
    timestamp: '2026-08-01T10:00:00.000Z',
    message: { role: 'user', content: 'what skills do you have' },
  })
}

/** A minimal assistant turn with usage, so loadClaudeCalls produces one call for it. */
function assistantLine(messageId: string, minute: number): string {
  return JSON.stringify({
    type: 'assistant',
    sessionId: SESSION_ID,
    uuid: `uuid-${messageId}`,
    timestamp: `2026-08-01T10:${String(minute).padStart(2, '0')}:00.000Z`,
    message: {
      id: messageId,
      role: 'assistant',
      model: 'claude-sonnet-4-5',
      usage: { input_tokens: 120, output_tokens: 30 },
      content: [{ type: 'text', text: 'ok' }],
    },
  })
}

/** A later, non-initial listing with its own entries (synthetic, no user data). */
function nonInitialListingLine(): string {
  return JSON.stringify({
    type: 'attachment',
    attachment: {
      type: 'skill_listing',
      content: '- release-notes: Draft release notes from merged pull requests.\n- docs: Write docs.',
      skillCount: 2,
      isInitial: false,
      names: ['release-notes', 'docs'],
    },
    uuid: 'listing-2',
    timestamp: '2026-08-01T10:15:00.000Z',
    sessionId: SESSION_ID,
  })
}

describe('loadClaudeCalls carries skill_listing onto the call that follows it', () => {
  let tmpDir: string

  beforeEach(() => {
    tmpDir = mkdtempSync(join(tmpdir(), 'skill-listing-parser-'))
  })

  afterEach(() => {
    rmSync(tmpDir, { recursive: true, force: true })
  })

  function transcriptCalls(lines: string[]) {
    const transcriptPath = join(tmpDir, 'session.jsonl')
    writeFileSync(transcriptPath, lines.join('\n') + '\n', 'utf8')
    return loadClaudeCalls(transcriptPath)
  }

  it('attaches the initial listing to the assistant call that follows it, entries in order', () => {
    const calls = transcriptCalls([captureLine(), userLine(), assistantLine('msg-1', 0)])

    expect(calls).toHaveLength(1)
    expect(calls[0]!.skillsListed).toEqual(expectedInitialListing())
    expect(calls[0]!.skillsListed?.skills.map((skill) => skill.name)).toEqual([
      'app-docs-standard',
      'architecture-decision-record',
      'azure-cli',
    ])
    expect(calls[0]!.skillsListed?.isInitial).toBe(true)
  })

  it('gives the large-line path the same skillsListed as the small-line path', () => {
    const small = transcriptCalls([captureLine(), userLine(), assistantLine('msg-1', 0)])
    const large = transcriptCalls([largeCaptureLine(), userLine(), assistantLine('msg-1', 0)])

    expect(small).toHaveLength(1)
    expect(large).toHaveLength(1)
    expect(small[0]!.skillsListed).toEqual(expectedInitialListing())
    expect(large[0]!.skillsListed).toEqual(small[0]!.skillsListed)
  })

  it('leaves skillsListed absent, not empty, when the transcript has no skill_listing', () => {
    const calls = transcriptCalls([userLine(), assistantLine('msg-1', 0)])

    expect(calls).toHaveLength(1)
    expect(Object.keys(calls[0]!)).not.toContain('skillsListed')
  })

  it('carries a later non-initial listing as isInitial false with its own entries', () => {
    const calls = transcriptCalls([
      captureLine(),
      userLine(),
      assistantLine('msg-1', 0),
      nonInitialListingLine(),
      userLine(),
      assistantLine('msg-2', 20),
    ])

    expect(calls).toHaveLength(2)
    expect(calls[0]!.skillsListed).toEqual(expectedInitialListing())
    expect(calls[1]!.skillsListed).toEqual({
      isInitial: false,
      skills: [
        { name: 'release-notes', description: 'Draft release notes from merged pull requests.' },
        { name: 'docs', description: 'Write docs.' },
      ],
    })
  })

  it('attaches each listing only to the single call that follows it', () => {
    const calls = transcriptCalls([
      assistantLine('msg-1', 0),
      captureLine(),
      userLine(),
      assistantLine('msg-2', 10),
      assistantLine('msg-3', 20),
    ])

    expect(calls).toHaveLength(3)
    expect(Object.keys(calls[0]!)).not.toContain('skillsListed')
    expect(calls[1]!.skillsListed).toEqual(expectedInitialListing())
    expect(Object.keys(calls[2]!)).not.toContain('skillsListed')
  })
})

// ---------------------------------------------------------------------------
// The ingest journal entry (parser.ts parseJsonlLine), normal and large-line
// ---------------------------------------------------------------------------
//
// Pinned minimal shape on the JournalEntry, beside deferred_tools_delta:
//   entry.attachment = { type: 'skill_listing', isInitial: boolean,
//                        skills: [{ name, description? }] }
// No `names` key on the journal entry. Both the normal JSON.parse path and the
// large-line scanner (parseLargeJsonl) must produce the same value.

/**
 * The journal entry's listing attachment as parseClaudeEntries stores it:
 * parseJsonlLine, then compactEntry (the stage that narrows attachments).
 */
function journalListing(line: string | Buffer): unknown {
  const entry = parseJsonlLine(line)
  expect(entry, 'parseJsonlLine returned null for a well-formed attachment line').not.toBeNull()
  const compacted = compactEntry(entry!)
  return (compacted as unknown as Record<string, unknown>)['attachment']
}

/** The journal shape the capture must produce: the call listing plus the `type` tag. */
function expectedJournalListing(): { type: string; isInitial: boolean; skills: Array<{ name: string; description: string }> } {
  return { type: 'skill_listing', ...expectedInitialListing() }
}

describe('parseJsonlLine carries skill_listing on the journal entry', () => {
  it('extracts the listing from a normal-size attachment line', () => {
    expect(journalListing(captureLine())).toEqual(expectedJournalListing())
  })

  it('extracts the same listing from a large-line attachment (Buffer and string)', () => {
    expect(journalListing(Buffer.from(largeCaptureLine()))).toEqual(expectedJournalListing())
    expect(journalListing(largeCaptureLine())).toEqual(expectedJournalListing())
  })

  it('gives the normal and large-line paths identical attachments', () => {
    const normal = journalListing(captureLine())
    expect(normal, 'normal-size journal entry carries no skill_listing attachment').toBeDefined()
    expect(journalListing(Buffer.from(largeCaptureLine()))).toEqual(normal)
  })

  it('keeps isInitial false and its own entries on a later listing, large-line too', () => {
    const later = nonInitialListingLine()
    const padded = JSON.stringify({ ...(JSON.parse(later) as Record<string, unknown>), padding: 'x'.repeat(40_000) })

    const expected = {
      type: 'skill_listing',
      isInitial: false,
      skills: [
        { name: 'release-notes', description: 'Draft release notes from merged pull requests.' },
        { name: 'docs', description: 'Write docs.' },
      ],
    }
    expect(journalListing(later)).toEqual(expected)
    expect(journalListing(Buffer.from(padded))).toEqual(expected)
  })
})

describe('the following call carries the same listing the journal entry holds', () => {
  let tmpDir: string

  beforeEach(() => {
    tmpDir = mkdtempSync(join(tmpdir(), 'skill-listing-flow-'))
  })

  afterEach(() => {
    rmSync(tmpDir, { recursive: true, force: true })
  })

  it.each([
    ['normal-size', () => captureLine()],
    ['large-line', () => largeCaptureLine()],
  ])('for a %s listing line, the following call skillsListed equals the journal listing', (_label, lineFor) => {
    const transcriptPath = join(tmpDir, 'session.jsonl')
    writeFileSync(
      transcriptPath,
      [lineFor(), userLine(), assistantLine('msg-1', 0)].join('\n') + '\n',
      'utf8',
    )
    const [call] = loadClaudeCalls(transcriptPath)

    const listing = journalListing(lineFor())
    expect(listing, 'journal entry carries no skill_listing attachment').toBeDefined()
    const { isInitial, skills } = listing as { isInitial: boolean; skills: unknown }
    expect(call!.skillsListed).toEqual({ isInitial, skills })
  })
})
