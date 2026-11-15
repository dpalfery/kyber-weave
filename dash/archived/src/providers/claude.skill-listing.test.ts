// Tests for how loadClaudeCalls treats skill_listing text (PR #351 finding 1, RED).
//
//   * A real `skill_listing` attachment line is not a turn. Its listing waits
//     for the next call that is pushed, and attaches to it exactly once.
//   * The line-level pre-filter must not drop an assistant turn just because the
//     quoted string `"skill_listing"` appears somewhere inside it. A turn that
//     carries message.usage is a real call, whether the string sits in a tool
//     input value, an object key, or anywhere else.
//   * An attachment that holds no parseable entries falls through harmlessly:
//     it neither produces a call nor clears a pending listing.

import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { loadClaudeCalls } from './claude.js'

const CAPTURE_PATH = join(
  dirname(fileURLToPath(import.meta.url)),
  '../canon/fixtures/skills/claude-code/capture-listing.jsonl',
)

const SESSION_ID = 'skill-listing-prefilter-session'

/** The committed sanitized real-shape capture: one real `skill_listing` attachment line. */
function captureLine(): string {
  const lines = readFileSync(CAPTURE_PATH, 'utf8').split(/\r?\n/).filter((line) => line.trim() !== '')
  expect(lines).toHaveLength(1)
  return lines[0]!
}

/** The listing the capture must parse to (no `type`; the call shape is {isInitial, skills}). */
function expectedCaptureListing(): { isInitial: boolean; skills: Array<{ name: string; description: string }> } {
  return {
    isInitial: true,
    skills: [
      {
        name: 'app-docs-standard',
        description: 'Apply the repository documentation standard when creating or updating application, service, tool, or system documentation.',
      },
      {
        name: 'architecture-decision-record',
        description: 'Capture an architectural decision as an ADR once it has been reached — a choice that constrains future work, had viable alternatives that were rejected, and would be expensive to revisit. Use when a design discussion has converged on such a decision, when the user asks for an ADR, or when a decision needs superseding. Do NOT use for reversible implementation choices, library picks with no lasting constraint, or to minute a discussion that has not concluded.',
      },
      { name: 'azure-cli', description: 'use the Azure CLI to interact with Azure resources.' },
    ],
  }
}

function userLine(): string {
  return JSON.stringify({
    type: 'user',
    sessionId: SESSION_ID,
    uuid: 'user-1',
    timestamp: '2026-08-01T10:00:00.000Z',
    message: { role: 'user', content: 'run the search' },
  })
}

/** An assistant turn with fixed usage and the given content blocks. */
function assistantLine(messageId: string, minute: number, content: unknown[]): string {
  return JSON.stringify({
    type: 'assistant',
    sessionId: SESSION_ID,
    uuid: `uuid-${messageId}`,
    timestamp: `2026-08-01T10:${String(minute).padStart(2, '0')}:00.000Z`,
    message: {
      id: messageId,
      role: 'assistant',
      model: 'claude-sonnet-4-5',
      usage: {
        input_tokens: 120,
        output_tokens: 30,
        cache_read_input_tokens: 500,
        cache_creation_input_tokens: 40,
      },
      content,
    },
  })
}

/** A Grep tool_use whose input VALUE is the structural string. */
const GREP_VALUE_CONTENT = [
  { type: 'tool_use', id: 'tu_grep_1', name: 'Grep', input: { pattern: 'skill_listing', path: 'src' } },
]

/** A tool_use whose input has the structural string as an object KEY. */
const OBJECT_KEY_CONTENT = [
  { type: 'tool_use', id: 'tu_mcp_1', name: 'mcp__svc__lookup', input: { skill_listing: true, query: 'x' } },
]

/** An attachment of the right type with no parseable entries. */
function emptyListingAttachmentLine(): string {
  return JSON.stringify({
    type: 'attachment',
    sessionId: SESSION_ID,
    uuid: 'listing-empty',
    timestamp: '2026-08-01T10:05:00.000Z',
    attachment: {
      type: 'skill_listing',
      content: '',
      skillCount: 0,
      isInitial: true,
      names: [],
    },
  })
}

describe('loadClaudeCalls keeps an assistant turn whose content mentions skill_listing', () => {
  let tmpDir: string

  beforeEach(() => {
    tmpDir = mkdtempSync(join(tmpdir(), 'skill-listing-prefilter-'))
  })

  afterEach(() => {
    rmSync(tmpDir, { recursive: true, force: true })
  })

  function transcriptCalls(lines: string[]) {
    const transcriptPath = join(tmpDir, 'session.jsonl')
    writeFileSync(transcriptPath, lines.join('\n') + '\n', 'utf8')
    return loadClaudeCalls(transcriptPath)
  }

  it('(1) produces a call with its exact tokens when a tool_use input value holds the string', () => {
    const calls = transcriptCalls([userLine(), assistantLine('msg-1', 0, GREP_VALUE_CONTENT)])

    expect(calls).toHaveLength(1)
    expect(calls[0]!.inputTokens).toBe(120)
    expect(calls[0]!.outputTokens).toBe(30)
    expect(calls[0]!.cacheReadInputTokens).toBe(500)
    expect(calls[0]!.cacheCreationInputTokens).toBe(40)
    expect(calls[0]!.tools).toEqual(['Grep'])
  })

  it('(2) keeps the pending listing for the assistant turn that is the next call, exactly once', () => {
    const calls = transcriptCalls([
      captureLine(),
      userLine(),
      assistantLine('msg-1', 0, GREP_VALUE_CONTENT),
      assistantLine('msg-2', 10, [{ type: 'text', text: 'done' }]),
    ])

    expect(calls).toHaveLength(2)
    expect(calls[0]!.turnId).toBe('msg-1')
    expect(calls[0]!.skillsListed).toEqual(expectedCaptureListing())
    expect(calls[0]!.inputTokens).toBe(120)
    expect(Object.keys(calls[1]!)).not.toContain('skillsListed')
  })

  it('(3) produces a call with its exact tokens when the string is an object key', () => {
    const calls = transcriptCalls([userLine(), assistantLine('msg-1', 0, OBJECT_KEY_CONTENT)])

    expect(calls).toHaveLength(1)
    expect(calls[0]!.inputTokens).toBe(120)
    expect(calls[0]!.outputTokens).toBe(30)
    expect(calls[0]!.cacheReadInputTokens).toBe(500)
    expect(calls[0]!.cacheCreationInputTokens).toBe(40)
    expect(calls[0]!.tools).toEqual(['mcp__svc__lookup'])
  })

  it('(3b) keeps the pending listing when the object-key turn is the next call', () => {
    const calls = transcriptCalls([
      captureLine(),
      userLine(),
      assistantLine('msg-1', 0, OBJECT_KEY_CONTENT),
    ])

    expect(calls).toHaveLength(1)
    expect(calls[0]!.skillsListed).toEqual(expectedCaptureListing())
  })

  it('(4a) a real attachment with no usage still attaches to the following call, as before', () => {
    const calls = transcriptCalls([captureLine(), userLine(), assistantLine('msg-1', 0, [])])

    expect(calls).toHaveLength(1)
    expect(calls[0]!.skillsListed).toEqual(expectedCaptureListing())
  })

  it('(4b) an attachment with no parseable entries falls through and does not clear a pending listing', () => {
    const calls = transcriptCalls([
      captureLine(),
      emptyListingAttachmentLine(),
      userLine(),
      assistantLine('msg-1', 0, []),
    ])

    expect(calls).toHaveLength(1)
    expect(calls[0]!.skillsListed).toEqual(expectedCaptureListing())
  })

  it('(4c) an attachment with no parseable entries produces no call and no skillsListed on its own', () => {
    const calls = transcriptCalls([emptyListingAttachmentLine(), userLine(), assistantLine('msg-1', 0, [])])

    expect(calls).toHaveLength(1)
    expect(Object.keys(calls[0]!)).not.toContain('skillsListed')
  })
})
