// Tests for provider failure handling (task 9.2; R1.2, R1.3; design.md
// "Error Handling" table). The two load-bearing describe blocks:
//
//   * R1.2 — a provider whose session store is absent is omitted silently:
//     no records, no problem, no error. Most machines do not run most
//     agents, so absence is the ordinary shape of a first run.
//   * R1.3 — a store that exists but cannot be parsed records one problem
//     naming the provider and the file, and every other provider still
//     synthesizes: the corrupt store narrows the run, it does not abort it.
//
// Alongside them, the rest of the loader seam's contract: successful loads
// synthesize through 9.1's Synthesizer unchanged (one data path), a
// present-but-empty store is neither absence nor error, and only a negative
// existence signal (`ENOENT`) reads as absence — every other error is a
// present store with a problem.

import { afterAll, afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { DatabaseSync } from 'node:sqlite'

import type { ParsedProviderCall } from '../providers/types.js'
import { copilot } from '../providers/copilot.js'
import { createAntigravityProvider } from '../providers/antigravity.js'
import { createCursorProvider } from '../providers/cursor.js'
import { tokenValidator } from '../canon/adapters/quarantine.js'
import { measurabilityFor } from '../canon/measurability.js'
import { contextLimitOf } from '../canon/context-window.js'
import { readListingOf } from '../canon/skills.js'
import type { CanonicalRecord } from '../canon/types.js'
import { CanonStore } from '../canon/store.js'
import { Synthesizer } from './synth.js'
import { PROVIDER_PARSE_ERROR, ingestProviders, type ProviderLoaderResult } from './provider.js'
import { codexReader } from './readers/codex.js'
import { copilotCliReader, loadCopilotCliCalls } from './readers/copilot.js'

// ---------------------------------------------------------------------------
// Fixture kit
// ---------------------------------------------------------------------------

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
    tools: ['Read', 'Bash'],
    bashCommands: [],
    timestamp: '2026-08-29T12:00:00.000Z',
    speed: 'standard',
    deduplicationKey: 'claude:s-1:m-1',
    userMessage: 'run the parity check',
    sessionId: 's-1',
    ...spec,
  }
}

/** Two calls for one provider, as a parsed store would yield them. */
function callsFor(provider: string, count = 2): ParsedProviderCall[] {
  return Array.from({ length: count }, (_, turn) =>
    call({
      provider,
      deduplicationKey: `${provider}:s-1:m-${turn}`,
    }),
  )
}

/** A parse failure a loader would surface for a corrupt JSONL session file. */
function corruptStoreError(file: string): Error {
  return Object.assign(new SyntaxError(`Unexpected token '<' in JSON at position 0`), {
    file,
  })
}

/** A Node-style fs error, as `fs.readFile` rejects with them. */
function fsError(code: string, path: string): Error {
  return Object.assign(new Error(`${code}: something went wrong, open '${path}'`), { code, path })
}

const tempRoots: string[] = []

afterAll(() => {
  for (const root of tempRoots) rmSync(root, { recursive: true, force: true })
})

function claudeTranscript(): string {
  const root = mkdtempSync(join(tmpdir(), 'kyber-provider-reader-'))
  tempRoots.push(root)
  const filePath = join(root, 'session.jsonl')
  writeFileSync(filePath, [
    JSON.stringify({
      sessionId: 's-1',
      message: {
        role: 'user',
        content: [
          { type: 'text', text: 'reader-provided conversation' },
          { type: 'tool_result', content: 'reader-provided tool result' },
        ],
      },
    }),
  ].join('\n'))
  return filePath
}

function fixturePath(name: string): string {
  return fileURLToPath(new URL(`../refresh/fixtures/${name}`, import.meta.url))
}

async function parsedCalls(
  provider: { createSessionParser: (source: {
    path: string
    project: string
    provider: string
    sourceType?: 'chatsession' | 'jsonl' | 'session-store' | 'transcript' | 'otel' | 'jetbrains'
  }, seenKeys: Set<string>) => { parse: () => AsyncGenerator<ParsedProviderCall> } },
  source: {
    path: string
    project: string
    provider: string
    sourceType?: 'chatsession' | 'jsonl' | 'session-store' | 'transcript' | 'otel' | 'jetbrains'
  },
): Promise<ParsedProviderCall[]> {
  const calls: ParsedProviderCall[] = []
  for await (const entry of provider.createSessionParser(source, new Set()).parse()) calls.push(entry)
  return calls
}

function cursorEvidenceDb(): string {
  const root = mkdtempSync(join(tmpdir(), 'kyber-cursor-evidence-'))
  tempRoots.push(root)
  const dbPath = join(root, 'state.vscdb')
  const fixture = JSON.parse(readFileSync(fixturePath('cursor-partial-evidence.json'), 'utf8')) as {
    rows: Array<{ key: string; value: Record<string, unknown> }>
  }
  const db = new DatabaseSync(dbPath)
  try {
    db.exec('CREATE TABLE cursorDiskKV (key TEXT PRIMARY KEY, value TEXT NOT NULL)')
    const insert = db.prepare('INSERT INTO cursorDiskKV (key, value) VALUES (?, ?)')
    for (const row of fixture.rows) insert.run(row.key, JSON.stringify(row.value))
  } finally {
    db.close()
  }
  return dbPath
}

// ---------------------------------------------------------------------------
// R1.2 — absent store: omitted silently, never an error
// ---------------------------------------------------------------------------

describe('R1.2 — an absent provider store is omitted silently', () => {
  it('records no problem and no records when the loader reports null', async () => {
    const result = await ingestProviders(['codex'], () => null)
    expect(result.records).toEqual([])
    expect(result.problems).toEqual([])
  })

  it('treats an undefined loader result the same as null', async () => {
    const result = await ingestProviders(['kilo-code'], () => undefined)
    expect(result.records).toEqual([])
    expect(result.problems).toEqual([])
  })

  it('treats a thrown ENOENT as absence, not an error', async () => {
    const loader = () => {
      throw fsError('ENOENT', '/home/dev/.codex/sessions')
    }
    const result = await ingestProviders(['codex'], loader)
    expect(result.records).toEqual([])
    expect(result.problems).toEqual([])
  })

  it('treats a returned ENOENT error value as absence too', async () => {
    const result = await ingestProviders(['codex'], () => fsError('ENOENT', '/home/dev/.codex/sessions'))
    expect(result.records).toEqual([])
    expect(result.problems).toEqual([])
  })

  it('omits the absent provider while still ingesting the present one', async () => {
    const claude = callsFor('claude')
    const result = await ingestProviders(['codex', 'claude'], (provider) =>
      provider === 'claude' ? claude : null,
    )
    expect(result.problems).toEqual([])
    expect(result.records.map((record) => record.harness)).toEqual(['claude', 'claude'])
  })
})

// ---------------------------------------------------------------------------
// R1.3 — unparseable store: problem names provider and file, run continues
// ---------------------------------------------------------------------------

describe('R1.3 — an unparseable store is recorded and the run continues', () => {
  const corruptFile = '/home/dev/.codex/sessions/rollout-2026-08-29.jsonl'

  it('records a problem naming the provider and the file for a corrupt store', async () => {
    const result = await ingestProviders(['codex'], () => corruptStoreError(corruptFile))

    expect(result.records).toEqual([])
    expect(result.problems).toHaveLength(1)
    const problem = result.problems[0]!
    expect(problem.severity).toBe('error')
    expect(problem.code).toBe(PROVIDER_PARSE_ERROR)
    expect(problem.message).toContain('codex')
    expect(problem.message).toContain(corruptFile)
    expect(problem.location).toBe(corruptFile)
  })

  it('continues with every other provider — corruption does not abort the run', async () => {
    // The corrupt store sits in the middle: providers after it must still
    // synthesize, which is the "continue" half of R1.3.
    const claude = callsFor('claude')
    const gemini = callsFor('gemini', 1)
    const result = await ingestProviders(['claude', 'codex', 'gemini'], (provider) => {
      if (provider === 'claude') return claude
      if (provider === 'codex') return corruptStoreError(corruptFile)
      return gemini
    })

    expect(result.problems).toHaveLength(1)
    expect(result.problems[0]!.message).toContain('codex')
    expect(result.records.map((record) => record.harness)).toEqual(['claude', 'claude'])
  })

  it('records the same problem when the loader throws instead of returning', async () => {
    const loader = () => {
      throw corruptStoreError(corruptFile)
    }
    const result = await ingestProviders(['codex'], loader)
    expect(result.problems).toHaveLength(1)
    expect(result.problems[0]!.code).toBe(PROVIDER_PARSE_ERROR)
    expect(result.problems[0]!.message).toContain('codex')
    expect(result.problems[0]!.message).toContain(corruptFile)
  })

  it('takes the file from a Node fs error path for a present-but-unreadable store', async () => {
    // EACCES is a store that exists but cannot be read — a problem, never
    // absence; only ENOENT is a negative existence check.
    const locked = '/home/dev/.claude/projects/x/session.jsonl'
    const result = await ingestProviders(['claude'], () => fsError('EACCES', locked))
    expect(result.problems).toHaveLength(1)
    expect(result.problems[0]!.code).toBe(PROVIDER_PARSE_ERROR)
    expect(result.problems[0]!.message).toContain('claude')
    expect(result.problems[0]!.message).toContain(locked)
    expect(result.problems[0]!.location).toBe(locked)
  })

  it('never omits the provider from the problem, even when no file is attached', async () => {
    // A loader that lets a bare SyntaxError escape names no file; the
    // problem must still point at the provider rather than at nothing.
    const result = await ingestProviders(['pi'], () => new SyntaxError(`Unexpected end of JSON input`))
    expect(result.problems).toHaveLength(1)
    expect(result.problems[0]!.code).toBe(PROVIDER_PARSE_ERROR)
    expect(result.problems[0]!.message).toContain('pi')
    expect(result.problems[0]!.location).toBeUndefined()
  })

  it('records one problem per unparseable provider, not one for the whole pass', async () => {
    const claude = callsFor('claude', 1)
    const result = await ingestProviders(['codex', 'claude', 'pi'], (provider) => {
      if (provider === 'claude') return claude
      return corruptStoreError(`/home/dev/.${provider}/store.json`)
    })
    expect(result.problems).toHaveLength(2)
    expect(result.problems.map((problem) => problem.message)).toSatisfy((messages: string[]) =>
      messages.every((message) => message.includes('could not be parsed')),
    )
    expect(result.records.map((record) => record.harness)).toEqual(['claude'])
  })
})

// ---------------------------------------------------------------------------
// Contract — successful loads take the one data path through 9.1
// ---------------------------------------------------------------------------

describe('successful loads synthesize through the 9.1 Synthesizer', () => {
  it('produces exactly what calling the Synthesizer directly produces', async () => {
    const claude = callsFor('claude')
    const gemini = callsFor('gemini', 3)
    const result = await ingestProviders(['claude', 'gemini'], (provider) =>
      provider === 'claude' ? claude : gemini,
    )

    expect(result.problems).toEqual([])
    expect(result.records).toEqual(new Synthesizer().synthesize([...claude, ...gemini]))
    // The synthesized records hold as stored, same as the direct path (R4.1).
    for (const record of result.records) {
      expect(tokenValidator(record)).toBeUndefined()
    }
  })

  it('treats a present-but-empty store as neither absence nor error', async () => {
    const result = await ingestProviders(['cursor'], () => [])
    expect(result.records).toEqual([])
    expect(result.problems).toEqual([])
  })

  it('ingests nothing without drama when no providers are requested', async () => {
    const result = await ingestProviders([], () => {
      throw new Error('the loader must never be consulted')
    })
    expect(result).toEqual({ records: [], problems: [] })
  })
})

describe('D5 reader integration', () => {
  it('invokes Claude’s registered reader and passes its matching turn to synthesis', async () => {
    const result = await ingestProviders(['claude'], () => ({
      calls: [call()],
      filePath: claudeTranscript(),
    }))

    expect(result.problems).toEqual([])
    expect(result.records).toHaveLength(1)
    expect(result.records[0]?.content).toEqual({
      conversation_history: 'reader-provided conversation',
      tool_result_content: 'reader-provided tool result',
    })
    expect(result.records[0]?.parts).toHaveLength(2)
  })

  it('pairs request/response paired calls with turns without generating duplicate records (#232)', async () => {
    const call1 = call({
      provider: 'claude',
      sessionId: 's-duo',
      timestamp: '2026-09-23T22:43:53.000Z',
      inputTokens: 2,
      outputTokens: 563,
      cacheReadInputTokens: 39096,
      cacheCreationInputTokens: 24977,
      cachedInputTokens: 39096,
      deduplicationKey: 'claude:s-duo:req',
    })
    const call2 = call({
      provider: 'claude',
      sessionId: 's-duo',
      timestamp: '2026-09-23T22:43:58.000Z',
      inputTokens: 2,
      outputTokens: 563,
      cacheReadInputTokens: 39096,
      cacheCreationInputTokens: 24977,
      cachedInputTokens: 39096,
      deduplicationKey: 'claude:s-duo:res',
    })

    const root = mkdtempSync(join(tmpdir(), 'kyber-pair-transcript-'))
    tempRoots.push(root)
    const filePath = join(root, 'session.jsonl')
    writeFileSync(filePath, [
      JSON.stringify({
        sessionId: 's-duo',
        message: {
          role: 'user',
          content: [{ type: 'text', text: 'question' }],
        },
      }),
      JSON.stringify({
        sessionId: 's-duo',
        message: {
          role: 'assistant',
          content: [{ type: 'text', text: 'answer' }],
        },
      }),
    ].join('\n') + '\n')

    const result = await ingestProviders(['claude-desktop'], () => ({
      calls: [call1, call2],
      filePath,
      harnessId: 'claude-desktop',
      sourceKey: 'claude-desktop:s-duo',
    }))

    expect(result.problems).toEqual([])
    const invokes = result.records.filter((r) => r.op === 'llm.invoke')
    expect(invokes).toHaveLength(1)
    expect(invokes[0]?.tokens.reportedInput).toBe(64075)
    // #216: pair collapse must retain fused transcript parts, not counters-only.
    expect(invokes[0]?.parts?.length).toBeGreaterThan(0)
    expect(invokes[0]?.parts).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ part: 'conversation_history', text: 'question' }),
        expect.objectContaining({ part: 'conversation_history', text: 'answer' }),
      ]),
    )
    expect(invokes[0]?.content.conversation_history).toContain('question')
    expect(invokes[0]?.content.conversation_history).toContain('answer')
  })

  // Issue #216 / plan T1: claude-desktop synth must carry capturable parts.
  // On the ProviderLoad(filePath) path this currently PASSES (regression pin);
  // live empty inspectors are attributed to refresh pairing/fallback (T2) or
  // unrepaired store (T5), not this ingest seam alone.
  it('attaches conversation and tool_result parts on claude-desktop synth (#216)', async () => {
    const usage = {
      input_tokens: 150,
      output_tokens: 45,
      cache_read_input_tokens: 300,
      cache_creation_input_tokens: 200,
    }
    const root = mkdtempSync(join(tmpdir(), 'kyber-desktop-parts-216-'))
    tempRoots.push(root)
    const filePath = join(root, 'session.jsonl')
    writeFileSync(
      filePath,
      [
        JSON.stringify({
          type: 'user',
          sessionId: 'desk-parts-216',
          message: {
            role: 'user',
            content: [
              { type: 'text', text: 'Check git status' },
              { type: 'tool_result', tool_use_id: 'tu_bash_216', content: 'On branch main' },
            ],
          },
        }),
        JSON.stringify({
          type: 'assistant',
          sessionId: 'desk-parts-216',
          uuid: 'desk-216-asst',
          timestamp: '2026-09-01T12:00:00.000Z',
          message: {
            id: 'msg-desk-216',
            model: 'claude-sonnet-4-5',
            usage,
            content: [{ type: 'text', text: 'You are on main.' }],
          },
        }),
      ].join('\n') + '\n',
    )

    const result = await ingestProviders(['claude-desktop'], () => ({
      calls: [],
      filePath,
      harnessId: 'claude-desktop',
      sourceKey: 'claude-desktop:desk-parts-216',
    }))

    expect(result.problems).toEqual([])
    const invokes = result.records.filter((r) => r.op === 'llm.invoke')
    expect(invokes).toHaveLength(1)
    const invoke = invokes[0]!
    expect(invoke.parts?.length).toBeGreaterThan(0)
    expect(invoke.parts).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ part: 'conversation_history', text: 'Check git status' }),
        expect.objectContaining({ part: 'conversation_history', text: 'You are on main.' }),
        expect.objectContaining({ part: 'tool_result_content', text: 'On branch main' }),
      ]),
    )
    expect(invoke.content.conversation_history).toContain('Check git status')
    expect(invoke.content.conversation_history).toContain('You are on main.')
    expect(invoke.content.tool_result_content).toContain('On branch main')
  })

  it('does not fabricate claude-desktop parts when the transcript has no content (#216)', async () => {
    const usage = {
      input_tokens: 10,
      output_tokens: 5,
      cache_read_input_tokens: 0,
      cache_creation_input_tokens: 0,
    }
    const root = mkdtempSync(join(tmpdir(), 'kyber-desktop-empty-216-'))
    tempRoots.push(root)
    const filePath = join(root, 'session.jsonl')
    writeFileSync(
      filePath,
      JSON.stringify({
        type: 'assistant',
        sessionId: 'desk-empty-216',
        uuid: 'desk-empty-asst',
        timestamp: '2026-09-01T12:00:00.000Z',
        message: {
          id: 'msg-desk-empty-216',
          model: 'claude-sonnet-4-5',
          usage,
          content: [],
        },
      }) + '\n',
    )

    const result = await ingestProviders(['claude-desktop'], () => ({
      calls: [],
      filePath,
      harnessId: 'claude-desktop',
      sourceKey: 'claude-desktop:desk-empty-216',
    }))

    expect(result.problems).toEqual([])
    const invokes = result.records.filter((r) => r.op === 'llm.invoke')
    expect(invokes).toHaveLength(1)
    const invoke = invokes[0]!
    expect(invoke.parts ?? []).toEqual([])
    expect(invoke.content).toEqual({})
  })

  // Issue #216 / plan T2: refresh window-slices calls but the Claude reader
  // historically yielded every turn. Positional pairing then attaches an
  // earlier turn's text to the in-window call. Id-based pairing (or a
  // date-aligned reader) must keep same-turn content.
  it('pairs window-sliced claude-desktop calls with the same-turn reader content (#216 T2)', async () => {
    const usageEarly = {
      input_tokens: 100,
      output_tokens: 20,
      cache_read_input_tokens: 0,
      cache_creation_input_tokens: 0,
    }
    const usageLate = {
      input_tokens: 200,
      output_tokens: 40,
      cache_read_input_tokens: 50,
      cache_creation_input_tokens: 10,
    }
    const root = mkdtempSync(join(tmpdir(), 'kyber-desktop-window-216-'))
    tempRoots.push(root)
    const filePath = join(root, 'session.jsonl')
    writeFileSync(
      filePath,
      [
        JSON.stringify({
          type: 'user',
          sessionId: 'desk-window-216',
          message: { role: 'user', content: [{ type: 'text', text: 'early question' }] },
        }),
        JSON.stringify({
          type: 'assistant',
          sessionId: 'desk-window-216',
          uuid: 'desk-early-asst',
          timestamp: '2026-08-01T12:00:00.000Z',
          message: {
            id: 'msg-desk-early',
            model: 'claude-sonnet-4-5',
            usage: usageEarly,
            content: [{ type: 'text', text: 'early answer' }],
          },
        }),
        JSON.stringify({
          type: 'user',
          sessionId: 'desk-window-216',
          message: { role: 'user', content: [{ type: 'text', text: 'late question' }] },
        }),
        JSON.stringify({
          type: 'assistant',
          sessionId: 'desk-window-216',
          uuid: 'desk-late-asst',
          timestamp: '2026-09-10T12:00:00.000Z',
          message: {
            id: 'msg-desk-late',
            model: 'claude-sonnet-4-5',
            usage: usageLate,
            content: [{ type: 'text', text: 'late answer' }],
          },
        }),
      ].join('\n') + '\n',
    )

    // Refresh already sliced counters to the in-window turn only.
    const windowedCall = call({
      provider: 'claude',
      model: 'claude-sonnet-4-5',
      inputTokens: 200,
      outputTokens: 40,
      cacheCreationInputTokens: 10,
      cacheReadInputTokens: 50,
      cachedInputTokens: 50,
      timestamp: '2026-09-10T12:00:00.000Z',
      sessionId: 'desk-window-216',
      turnId: 'msg-desk-late',
      deduplicationKey: 'claude:desk-window-216:desk-late-asst',
      userMessage: '',
    })
    const dateRange = {
      start: new Date('2026-09-01T00:00:00.000Z'),
      end: new Date('2026-09-12T00:00:00.000Z'),
    }

    const result = await ingestProviders(['claude-desktop'], () => ({
      calls: [windowedCall],
      filePath,
      dateRange,
      harnessId: 'claude-desktop',
      sourceKey: 'claude-desktop:desk-window-216',
    }))

    expect(result.problems).toEqual([])
    const invokes = result.records.filter((r) => r.op === 'llm.invoke')
    expect(invokes).toHaveLength(1)
    const invoke = invokes[0]!
    expect(invoke.parts?.length).toBeGreaterThan(0)
    expect(invoke.content.conversation_history).toContain('late question')
    expect(invoke.content.conversation_history).toContain('late answer')
    expect(invoke.content.conversation_history).not.toContain('early question')
    expect(invoke.content.conversation_history).not.toContain('early answer')
  })

  // Untimestamped turns: loadClaudeCalls stamps epoch 0 and sliceCallsToWindow
  // drops them, but a reader that kept them would still yield the early turn.
  // Without message.id, positional pairing then attaches that text to the
  // sole in-window call.
  it('does not attach untimestamped early-turn text to a windowed call without message.id (#216)', async () => {
    const usage = {
      input_tokens: 100,
      output_tokens: 20,
      cache_read_input_tokens: 0,
      cache_creation_input_tokens: 0,
    }
    const root = mkdtempSync(join(tmpdir(), 'kyber-desktop-no-ts-216-'))
    tempRoots.push(root)
    const filePath = join(root, 'session.jsonl')
    writeFileSync(
      filePath,
      [
        JSON.stringify({
          type: 'user',
          sessionId: 'desk-no-ts-216',
          message: { role: 'user', content: [{ type: 'text', text: 'untimestamped early question' }] },
        }),
        JSON.stringify({
          type: 'assistant',
          sessionId: 'desk-no-ts-216',
          uuid: 'desk-early-no-ts',
          // no timestamp — counters path drops via sliceCallsToWindow
          message: {
            model: 'claude-sonnet-4-5',
            usage,
            content: [{ type: 'text', text: 'untimestamped early answer' }],
          },
        }),
        JSON.stringify({
          type: 'user',
          sessionId: 'desk-no-ts-216',
          message: { role: 'user', content: [{ type: 'text', text: 'windowed question' }] },
        }),
        JSON.stringify({
          type: 'assistant',
          sessionId: 'desk-no-ts-216',
          uuid: 'desk-late-no-id',
          timestamp: '2026-09-10T12:00:00.000Z',
          message: {
            model: 'claude-sonnet-4-5',
            usage: { ...usage, input_tokens: 200, output_tokens: 40 },
            content: [{ type: 'text', text: 'windowed answer' }],
          },
        }),
      ].join('\n') + '\n',
    )

    const windowedCall = call({
      provider: 'claude',
      model: 'claude-sonnet-4-5',
      inputTokens: 200,
      outputTokens: 40,
      cacheCreationInputTokens: 0,
      cacheReadInputTokens: 0,
      cachedInputTokens: 0,
      timestamp: '2026-09-10T12:00:00.000Z',
      sessionId: 'desk-no-ts-216',
      // no turnId — forces positional pairing
      deduplicationKey: 'claude:desk-no-ts-216:desk-late-no-id',
      userMessage: '',
    })
    const dateRange = {
      start: new Date('2026-09-01T00:00:00.000Z'),
      end: new Date('2026-09-12T00:00:00.000Z'),
    }

    const result = await ingestProviders(['claude-desktop'], () => ({
      calls: [windowedCall],
      filePath,
      dateRange,
      harnessId: 'claude-desktop',
      sourceKey: 'claude-desktop:desk-no-ts-216',
    }))

    expect(result.problems).toEqual([])
    const invokes = result.records.filter((r) => r.op === 'llm.invoke')
    expect(invokes).toHaveLength(1)
    const history = invokes[0]!.content.conversation_history ?? ''
    expect(history).toContain('windowed question')
    expect(history).toContain('windowed answer')
    expect(history).not.toContain('untimestamped early question')
    expect(history).not.toContain('untimestamped early answer')
  })

  // Kilo #276: one message.id used to set hasNativeIds for the whole file, so
  // an id-less sibling call resolved to undefined and synthesized empty history.
  it('keeps conversation history on an id-less Claude turn when a sibling turn has message.id (#276 mixed file)', async () => {
    const usageId = {
      input_tokens: 100,
      output_tokens: 20,
      cache_read_input_tokens: 0,
      cache_creation_input_tokens: 0,
    }
    const usageIdless = {
      input_tokens: 200,
      output_tokens: 40,
      cache_read_input_tokens: 10,
      cache_creation_input_tokens: 5,
    }
    const root = mkdtempSync(join(tmpdir(), 'kyber-desktop-mixed-ids-276-'))
    tempRoots.push(root)
    const filePath = join(root, 'session.jsonl')
    writeFileSync(
      filePath,
      [
        JSON.stringify({
          type: 'user',
          sessionId: 'desk-mixed-276',
          message: { role: 'user', content: [{ type: 'text', text: 'id-bearing question' }] },
        }),
        JSON.stringify({
          type: 'assistant',
          sessionId: 'desk-mixed-276',
          uuid: 'desk-mixed-id-asst',
          timestamp: '2026-09-10T12:00:00.000Z',
          message: {
            id: 'msg-mixed-id',
            model: 'claude-sonnet-4-5',
            usage: usageId,
            content: [{ type: 'text', text: 'id-bearing answer' }],
          },
        }),
        JSON.stringify({
          type: 'user',
          sessionId: 'desk-mixed-276',
          message: { role: 'user', content: [{ type: 'text', text: 'id-less question' }] },
        }),
        JSON.stringify({
          type: 'assistant',
          sessionId: 'desk-mixed-276',
          uuid: 'desk-mixed-idless-asst',
          timestamp: '2026-09-10T12:02:00.000Z',
          message: {
            model: 'claude-sonnet-4-5',
            usage: usageIdless,
            content: [{ type: 'text', text: 'id-less answer' }],
          },
        }),
      ].join('\n') + '\n',
    )

    const result = await ingestProviders(['claude-desktop'], () => ({
      calls: [],
      filePath,
      harnessId: 'claude-desktop',
      sourceKey: 'claude-desktop:desk-mixed-276',
    }))

    expect(result.problems).toEqual([])
    const invokes = result.records.filter((r) => r.op === 'llm.invoke')
    expect(invokes).toHaveLength(2)
    const idBearing = invokes.find((r) => (r.content.conversation_history ?? '').includes('id-bearing question'))
    const idLess = invokes.find((r) => (r.content.conversation_history ?? '').includes('id-less question'))
    expect(idBearing?.content.conversation_history).toContain('id-bearing answer')
    expect(idLess).toBeDefined()
    expect(idLess?.content.conversation_history).toContain('id-less answer')
    expect(idLess?.parts?.length).toBeGreaterThan(0)
  })
})

describe('D6 Copilot CLI SQLite integration', () => {
  it('turns a collectable CLI row into a canonical record without zero-filling omitted buckets', async () => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-copilot-cli-provider-'))
    tempRoots.push(root)
    const filePath = join(root, 'data.db')
    const db = new DatabaseSync(filePath)
    try {
      db.exec(`
        CREATE TABLE sessions (
          id TEXT,
          session_id TEXT,
          model TEXT,
          created_at TEXT,
          context_system_tokens INTEGER,
          context_conversation_tokens INTEGER,
          context_tier TEXT
        );
        INSERT INTO sessions VALUES (
          'synthetic-row', 'synthetic-session', 'gpt-5',
          '2026-09-04T12:00:00.000Z', 120, 340, 'standard'
        );
      `)
    } finally {
      db.close()
    }

    const result = await ingestProviders(['copilot'], () => ({ calls: [], filePath }))

    expect(result.problems).toEqual([])
    expect(result.records).toHaveLength(1)
    expect(result.records[0]?.raw).toMatchObject({
      context_system_tokens: 120,
      context_conversation_tokens: 340,
      context_tier: 'standard',
    })
    expect(result.records[0]?.raw).not.toHaveProperty('context_buffer_tokens')
  })
})

describe('T3 static source capability readers', () => {
  it('replays Copilot VS Code requests as input-side snapshots', async () => {
    const filePath = fixturePath('copilot-vscode-request.jsonl')
    const source = {
      path: filePath,
      project: 'fixture-project',
      provider: 'copilot',
      sourceType: 'chatsession' as const,
    }
    const calls = await parsedCalls(copilot, source)

    expect(calls).toHaveLength(2)
    expect(calls.map((entry) => entry.userMessage)).toEqual(['first request', 'second request'])

    const result = await ingestProviders(['copilot-vscode'], () => ({
      calls,
      filePath,
      harnessId: 'copilot-vscode',
      sourceKey: 'copilot-vscode:vscode-static-session',
    }))

    expect(result.problems).toEqual([])
    expect(result.records).toHaveLength(2)
    const first = result.records[0]!
    const second = result.records[1]!

    expect(first.tokens.reportedInput).toBe(180)
    expect(first.content.conversation_history).toContain('first request')
    expect(first.content.instruction_context).toContain('agent mode')
    expect(first.content.conversation_history).not.toContain('first response')
    expect(second.content.conversation_history).toContain('first response')
    expect(second.content.conversation_history).toContain('second request')
    expect(second.content.conversation_history).not.toContain('second response')
    expect(first.measurability?.system_prompt).toMatchObject({
      availability: 'not_measurable',
      reason: expect.stringMatching(/VS Code|chat session/i),
    })
    expect(first.measurability?.tool_definitions).toMatchObject({
      availability: 'not_measurable',
      reason: expect.stringMatching(/VS Code|chat session/i),
    })
    expect(first.measurability?.tool_result_content).toMatchObject({
      availability: 'not_measurable',
      reason: expect.stringMatching(/VS Code|chat session|tool-result/i),
    })
  })

  it('retains reconstructed input snapshots for ID-less requests when sibling request has native requestId', async () => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-copilot-idless-'))
    const filePath = join(root, 'session.jsonl')
    try {
      const line1 = JSON.stringify({
        kind: 0,
        v: { sessionId: 'vscode-mixed-ids-session', creationDate: '2026-09-22T10:00:00.000Z', requests: [] },
      })
      const line2 = JSON.stringify({
        kind: 2,
        k: ['requests'],
        v: [
          {
            // ID-less request
            timestamp: '2026-09-22T10:01:00.000Z',
            modelId: 'copilot/gpt-5',
            message: { text: 'id-less request text' },
            result: { metadata: { promptTokens: 100, outputTokens: 20, resolvedModel: 'gpt-5' } },
            response: { text: 'id-less response text' },
          },
          {
            // Sibling request with native requestId
            requestId: 'explicit-native-id',
            timestamp: '2026-09-22T10:02:00.000Z',
            modelId: 'copilot/gpt-5',
            message: { text: 'sibling request text' },
            result: { metadata: { promptTokens: 150, outputTokens: 25, resolvedModel: 'gpt-5' } },
            response: { text: 'sibling response text' },
          },
        ],
      })
      writeFileSync(filePath, `${line1}\n${line2}\n`)

      const source = {
        path: filePath,
        project: 'fixture-project',
        provider: 'copilot',
        sourceType: 'chatsession' as const,
      }
      const calls = await parsedCalls(copilot, source)
      expect(calls).toHaveLength(2)
      expect(calls[0]!.turnId).toBe('request-0')
      expect(calls[1]!.turnId).toBe('explicit-native-id')

      const result = await ingestProviders(['copilot-vscode'], () => ({
        calls,
        filePath,
        harnessId: 'copilot-vscode',
        sourceKey: 'copilot-vscode:vscode-mixed-ids-session',
      }))

      expect(result.problems).toEqual([])
      expect(result.records).toHaveLength(2)
      const first = result.records[0]!
      const second = result.records[1]!

      // First (ID-less) request MUST retain its reconstructed input snapshot
      expect(first.content.conversation_history).toContain('id-less request text')
      expect(first.spanId).toContain(':request-0')

      // Second request also matches correctly
      expect(second.content.conversation_history).toContain('sibling request text')
      expect(second.spanId).toContain(':explicit-native-id')
    } finally {
      rmSync(root, { recursive: true, force: true })
    }
  })

  it('retains Cursor prompt/context evidence while naming incomplete history', async () => {
    const dbPath = cursorEvidenceDb()
    const cursor = createCursorProvider(dbPath)
    const source = { path: dbPath, project: 'fixture-project', provider: 'cursor' }
    const nativeCalls = await parsedCalls(cursor, source)

    expect(nativeCalls.some((entry) => entry.userMessage.includes('current Cursor request'))).toBe(true)
    expect(nativeCalls.some((entry) => entry.inputTokens === 240)).toBe(true)

    const result = await ingestProviders(['cursor'], () => ({
      calls: [call({
        provider: 'cursor',
        model: 'gpt-5',
        inputTokens: 240,
        outputTokens: 60,
        cacheCreationInputTokens: 0,
        cacheReadInputTokens: 0,
        cachedInputTokens: 0,
        userMessage: 'current Cursor request',
        sessionId: 'cursor-static-session',
        turnId: 'cursor-request-1',
        deduplicationKey: 'cursor:cursor-static-session:cursor-request-1',
      })],
      filePath: dbPath,
      harnessId: 'cursor',
      sourceKey: 'cursor:cursor-static-session',
    }))

    expect(result.problems).toEqual([])
    const record = result.records[0]!
    expect(record.tokens.reportedInput).toBe(240)
    expect(record.raw).not.toHaveProperty('contextWindow')
    expect(contextLimitOf([record]).contextLimitSource).toBe('default')
    expect(record.content.instruction_context).toContain('Cursor tool context')
    expect(record.measurability?.conversation_history).toMatchObject({
      availability: 'not_measurable',
      reason: expect.stringMatching(/Cursor|cursor/i),
    })
    expect(record.measurability?.tool_definitions).toMatchObject({
      availability: 'not_measurable',
      reason: expect.stringMatching(/Cursor|cursor/i),
    })
  })

  it('ingests Cursor bubbles with distinct span identities per request', async () => {
    const dbPath = cursorEvidenceDb()
    const cursor = createCursorProvider(dbPath)
    const source = { path: dbPath, project: 'fixture-project', provider: 'cursor' }
    const calls = await parsedCalls(cursor, source)
    const prompt = calls.find((entry) => entry.deduplicationKey.endsWith(':prompt'))
    const reply = calls.find((entry) => entry.deduplicationKey.endsWith(':reply'))
    expect(prompt?.turnId).toBe('cursor-request-1')
    expect(reply?.turnId).toBeUndefined()

    const result = await ingestProviders(['cursor'], () => ({
      calls,
      filePath: dbPath,
      harnessId: 'cursor',
      sourceKey: 'cursor:cursor-static-session',
    }))
    expect(result.problems).toEqual([])
    expect(result.records).toHaveLength(calls.length)
    expect(new Set(result.records.map((record) => record.spanId)).size).toBe(calls.length)
    const promptIndex = calls.findIndex((entry) => entry.deduplicationKey.endsWith(':prompt'))
    expect(result.records[promptIndex]?.tokens.reportedInput).toBe(240)

    // Existing stores contain digest identities from before turnId was used
    // for reader matching. A refresh must replace those rows, not add copies.
    const legacy = await ingestProviders(['cursor'], () => ({
      calls: calls.map((entry) => ({ ...entry, turnId: undefined })),
      filePath: dbPath,
      harnessId: 'cursor',
      sourceKey: 'cursor:cursor-static-session',
    }))
    expect(result.records.map((record) => record.spanId)).toEqual(legacy.records.map((record) => record.spanId))
    const store = new CanonStore(':memory:')
    try {
      store.upsertMany(legacy.records)
      store.upsertMany(result.records)
      expect(store.listAll()).toHaveLength(calls.length)
    } finally {
      store.close()
    }
  })

  it('does not positionally pair an unidentified Cursor call with a native request turn', async () => {
    const dbPath = cursorEvidenceDb()
    const unidentified = call({
      provider: 'cursor',
      sessionId: 'cursor-static-session',
      turnId: undefined,
      deduplicationKey: 'cursor:bubble:unidentified',
    })
    const identified = call({
      provider: 'cursor',
      sessionId: 'cursor-static-session',
      turnId: 'cursor-request-1',
      deduplicationKey: 'cursor:bubble:identified',
    })
    const result = await ingestProviders(['cursor'], () => ({
      calls: [unidentified, identified],
      filePath: dbPath,
      harnessId: 'cursor',
    }))

    expect(result.records).toHaveLength(2)
    expect(result.records[0]?.content.instruction_context).toBeUndefined()
    expect(result.records[1]?.content.instruction_context).toContain('Cursor tool context')
  })

  // Codex parser always stamps turnId; codexReader never emits nativeRecordId.
  // Id lookup misses and must fall back to positional pairing (#276 review).
  it('pairs a Codex call with file-synth turn parts when the reader has no nativeRecordId (#276)', async () => {
    const sessionId = '00000000-0000-4000-8000-000000000099'
    const root = mkdtempSync(join(tmpdir(), 'kyber-codex-pairing-276-'))
    tempRoots.push(root)
    const filePath = join(root, 'rollout-pairing.jsonl')
    writeFileSync(
      filePath,
      [
        JSON.stringify({
          type: 'session_meta',
          payload: {
            session_id: sessionId,
            base_instructions: { text: 'You are a synthetic Codex agent for pairing tests.' },
          },
        }),
        JSON.stringify({
          type: 'response_item',
          payload: {
            type: 'message',
            role: 'user',
            content: [{ type: 'input_text', text: 'codex pairing conversation' }],
          },
        }),
        JSON.stringify({
          type: 'event_msg',
          payload: { type: 'token_count', info: { last_token_usage: { input_tokens: 12 } } },
        }),
      ].join('\n') + '\n',
    )

    // Premise: only the positional arm can attach parts for this fixture.
    // If codexReader starts emitting nativeRecordId, this fails with a why.
    const readerTurns: Array<{ nativeRecordId?: string }> = []
    for await (const turn of codexReader.read(filePath)) readerTurns.push(turn)
    expect(readerTurns.length).toBeGreaterThan(0)
    for (const turn of readerTurns) {
      expect(turn.nativeRecordId).toBeUndefined()
    }

    const result = await ingestProviders(['codex'], () => ({
      calls: [
        call({
          provider: 'codex',
          model: 'gpt-5.2-codex',
          sessionId,
          turnId: `${sessionId}:t0`,
          deduplicationKey: `codex:${sessionId}:t0`,
        }),
      ],
      filePath,
    }))

    expect(result.problems).toEqual([])
    expect(result.records).toHaveLength(1)
    const record = result.records[0]!
    expect((record.parts ?? []).length).toBeGreaterThan(0)
    expect(record.content.conversation_history).toContain('codex pairing conversation')
  })
})

describe('T4 — source-unit ingest seam', () => {
  it('names the harness and source unit on a parse problem, not only the provider', async () => {
    const unit = '/home/dev/.gemini/antigravity-cli/session.pb'
    const result = await ingestProviders(['antigravity'], () => ({
      calls: [],
      filePath: unit,
      harnessId: 'antigravity-cli',
      sourceKey: 'antigravity-cli:session.pb',
      error: Object.assign(new SyntaxError('bad protobuf'), { file: unit }),
    }))
    expect(result.records).toEqual([])
    expect(result.problems).toHaveLength(1)
    expect(result.problems[0]?.message).toContain('antigravity-cli')
    expect(result.problems[0]?.message).toContain('antigravity-cli:session.pb')
    expect(result.problems[0]?.location).toBe(unit)
  })

  it('does not persist Gemini as a harness when asked to ingest that provider', async () => {
    const result = await ingestProviders(['gemini'], () => callsFor('gemini', 2))
    expect(result.records.map((record) => record.harness)).not.toContain('gemini')
  })

  it('reads Claude transcripts under a classified claude-cli harness id', async () => {
    const result = await ingestProviders(['claude-cli'], () => ({
      calls: [call({ provider: 'claude', deduplicationKey: 'claude:s-1:m-1' })],
      filePath: claudeTranscript(),
      harnessId: 'claude-cli',
      sourceKey: 'claude-cli:s-1',
    }))
    expect(result.problems).toEqual([])
    expect(result.records[0]?.harness).toBe('claude-cli')
    expect(result.records[0]?.content.conversation_history).toBe('reader-provided conversation')
  })
})

// ---------------------------------------------------------------------------
// D23–D26 — Copilot CLI session journal, through the ingest seam
//
// Every fixture is synthetic and written into a scratch HOME. The store and
// journals mirror the shapes the CLI writes: session-store.db `sessions` rows,
// `session-state/<sid>/events.jsonl` journals, and `session.shutdown` rollups
// that the provider turns into one call per (model, leg).
// ---------------------------------------------------------------------------

describe('D23–D26 Copilot CLI session journal through ingestProviders', () => {
  const ROOT_S1 = 'Root prompt for S1.\n<available_skills>\n<skill><name>alpha-skill</name><description>Does alpha work.</description><location>/skills/alpha</location></skill>\n<skill><name>beta-skill</name><description>Does beta work.</description><location>/skills/beta</location></skill>\n</available_skills>'
  const ROOT_S4 = 'Root prompt for S4.\n<available_skills>\n<skill><name>gamma-skill</name><description>Does gamma work.</description><location>/skills/gamma</location></skill>\n</available_skills>'
  const SUB_S1 = 'Subagent prompt for S1 only; no record may carry it.'
  const PENDING_S3 = 'Prompt in pending-session:S3, which must never match session S3.'
  const ROOT_OF: Record<string, string> = { S1: ROOT_S1, S4: ROOT_S4 }
  const SKILLS_INVOKED_REASON =
    'Skill invocations are not measurable for Copilot CLI: per-message journal calls are not ingested, so the skills a turn invoked are not recorded.'

  let home = ''

  beforeEach(() => {
    home = mkdtempSync(join(tmpdir(), 'kyber-copilot-home-'))
    vi.stubEnv('HOME', home)
  })

  afterEach(() => {
    vi.unstubAllEnvs()
    rmSync(home, { recursive: true, force: true })
  })

  function writeJournal(sessionDirName: string, lines: readonly object[]): string {
    const dir = join(home, '.copilot', 'session-state', sessionDirName)
    mkdirSync(dir, { recursive: true })
    const file = join(dir, 'events.jsonl')
    writeFileSync(file, lines.map((line) => JSON.stringify(line)).join('\n') + '\n')
    return file
  }

  function writeStore(): string {
    const dir = join(home, '.copilot')
    mkdirSync(dir, { recursive: true })
    const file = join(dir, 'session-store.db')
    const db = new DatabaseSync(file)
    try {
      db.exec('CREATE TABLE sessions (id TEXT, cwd TEXT, repository TEXT);')
      const insert = db.prepare('INSERT INTO sessions (id, cwd, repository) VALUES (?, ?, ?)')
      insert.run('S1', '/synthetic/one', 'synthetic/one')
      insert.run('S2', '/synthetic/two', 'synthetic/two')
      insert.run('S3', '/synthetic/three', 'synthetic/three')
    } finally {
      db.close()
    }
    return file
  }

  /** The S1 journal: a stale root prompt, the latest root prompt, a subagent prompt and three calls. */
  function writeS1Journal(): void {
    writeJournal('S1', [
      { type: 'system.message', data: { role: 'system', content: 'Stale root prompt before the latest.' } },
      { type: 'system.message', data: { role: 'system', content: ROOT_S1 } },
      { type: 'system.message', agentId: 'agent-sub-1', data: { role: 'system', content: SUB_S1 } },
      {
        type: 'assistant.message',
        data: { messageId: 'msg-s1-root-1', toolRequests: [{ toolCallId: 'call-root-1', name: 'view', arguments: { path: '/synthetic/a.txt' } }] },
      },
      {
        type: 'assistant.message',
        agentId: 'agent-sub-1',
        data: { messageId: 'msg-s1-sub-1', toolRequests: [{ toolCallId: 'call-sub-1', name: 'grep', arguments: { pattern: 'needle' } }] },
      },
      {
        type: 'assistant.message',
        data: { messageId: 'msg-s1-root-2', toolRequests: [{ toolCallId: 'call-root-2', name: 'bash', arguments: { command: 'ls' } }] },
      },
      { type: 'tool.execution_complete', data: { toolCallId: 'call-root-1', success: true, result: { content: 'ROOT RESULT ONE' } } },
      {
        type: 'tool.execution_complete',
        agentId: 'agent-sub-1',
        data: { toolCallId: 'call-sub-1', success: true, result: { content: 'SUB RESULT ONE' } },
      },
    ])
  }

  /** The S4 journal: a root prompt and one call, with no session-store row. */
  function writeS4Journal(): string {
    return writeJournal('S4', [
      { type: 'system.message', data: { role: 'system', content: ROOT_S4 } },
      {
        type: 'assistant.message',
        data: { messageId: 'msg-s4-1', toolRequests: [{ toolCallId: 'call-s4-1', name: 'view', arguments: { path: '/synthetic/s4.txt' } }] },
      },
      { type: 'tool.execution_complete', data: { toolCallId: 'call-s4-1', success: true, result: { content: 'S4 RESULT' } } },
      { type: 'session.shutdown', data: { modelMetrics: {} } },
    ])
  }

  function writeFixture(): { storePath: string; s4Journal: string; legs: ParsedProviderCall[] } {
    const storePath = writeStore()
    writeS1Journal()
    const s4Journal = writeS4Journal()
    // No journal for S2. A directory that merely starts with a store session id must not match it.
    writeJournal('pending-session:S3', [
      { type: 'system.message', data: { role: 'system', content: PENDING_S3 } },
      {
        type: 'assistant.message',
        data: { messageId: 'msg-pending-1', toolRequests: [{ toolCallId: 'call-pending-1', name: 'view', arguments: { path: '/synthetic/pending' } }] },
      },
      { type: 'tool.execution_complete', data: { toolCallId: 'call-pending-1', success: true, result: { content: 'PENDING RESULT' } } },
    ])
    // Two rollup legs for S4, listed later-first so "earliest" is not "first in input".
    const late = shutdownLeg('gpt-5', 1, '2026-10-20T11:00:00.000Z', 3_000, 0.015)
    const early = shutdownLeg('claude-sonnet-5-5', 1, '2026-10-20T09:00:00.000Z', 4_000, 0.02)
    return { storePath, s4Journal, legs: [late, early] }
  }

  /** A `session.shutdown` rollup leg, shaped as the copilot provider yields it: output 0, dedup key `copilot:<sid>:shutdown:<model>:<n>`. */
  function shutdownLeg(model: string, n: number, timestamp: string, inputTokens: number, costUSD: number): ParsedProviderCall {
    return {
      provider: 'copilot',
      sessionId: 'S4',
      model,
      inputTokens,
      outputTokens: 0,
      cacheCreationInputTokens: 0,
      cacheReadInputTokens: 0,
      cachedInputTokens: 0,
      reasoningTokens: 0,
      webSearchRequests: 0,
      costUSD,
      costIsEstimated: false,
      tools: [],
      bashCommands: [],
      timestamp,
      speed: 'standard',
      deduplicationKey: `copilot:S4:shutdown:${model}:${n}`,
      userMessage: '',
    }
  }

  /** Placeholder rows come from the store (the empty-load fallback); rollups come from the S4 journal's load. */
  async function ingestFixture() {
    const { storePath, s4Journal, legs } = writeFixture()
    const loader = (provider: string): ProviderLoaderResult => {
      if (provider === 'copilot') return { calls: [], filePath: storePath }
      if (provider === 'copilot-cli') return { calls: legs, filePath: s4Journal }
      return null
    }
    return ingestProviders(['copilot', 'copilot-cli'], loader)
  }

  const isAccounting = (record: CanonicalRecord) => record.op !== 'tool.invoke'
  const accountingOf = (records: readonly CanonicalRecord[], sessionId: string) =>
    records.filter((record) => isAccounting(record) && String(record.sessionId) === sessionId)
  const toolsOf = (records: readonly CanonicalRecord[], sessionId: string) =>
    records.filter((record) => record.op === 'tool.invoke' && String(record.sessionId) === sessionId)
  const rawOf = (record: CanonicalRecord): Record<string, unknown> => (record.raw ?? {}) as Record<string, unknown>
  const callIdOf = (record: CanonicalRecord): unknown => rawOf(record)['gen_ai.tool.call_id']
  const systemPromptsOf = (record: CanonicalRecord): string[] =>
    (record.parts ?? []).filter((part) => part.part === 'system_prompt').map((part) => part.text)

  it('(a) S1 placeholder carries exactly the root system prompt, and no record carries the subagent prompt', async () => {
    const result = await ingestFixture()

    expect(result.problems).toEqual([])
    const placeholders = accountingOf(result.records, 'S1')
    expect(placeholders).toHaveLength(1)
    expect(placeholders[0]!.parts).toEqual([expect.objectContaining({ part: 'system_prompt', text: ROOT_S1 })])
    expect(JSON.stringify(result.records)).not.toContain(SUB_S1)
  })

  it('(a) every S4 rollup record carries the S4 root prompt', async () => {
    const result = await ingestFixture()

    const legs = accountingOf(result.records, 'S4')
    expect(legs).toHaveLength(2)
    for (const leg of legs) {
      expect(systemPromptsOf(leg)).toEqual([ROOT_S4])
    }
  })

  it('(b) readListingOf measures the skill names and descriptions from the attached root prompt', async () => {
    const result = await ingestFixture()

    const [placeholder] = accountingOf(result.records, 'S1')
    expect(readListingOf(placeholder!, placeholder!.parts)).toMatchObject({
      measurable: true,
      skills: [
        { name: 'alpha-skill', description: 'Does alpha work.' },
        { name: 'beta-skill', description: 'Does beta work.' },
      ],
    })
    for (const leg of accountingOf(result.records, 'S4')) {
      expect(readListingOf(leg, leg.parts)).toMatchObject({
        measurable: true,
        skills: [{ name: 'gamma-skill', description: 'Does gamma work.' }],
      })
    }
  })

  it('(b) skill invocations stay not measurable on Copilot CLI records, with the D26 reason', async () => {
    const result = await ingestFixture()

    const records = [...accountingOf(result.records, 'S1'), ...accountingOf(result.records, 'S4')]
    expect(records.length).toBeGreaterThan(0)
    for (const record of records) {
      expect(record.measurability?.skills_invoked).toMatchObject({
        availability: 'not_measurable',
        reason: SKILLS_INVOKED_REASON,
      })
    }
  })

  it('(c) S1 tool calls hang off its placeholder, join results on toolCallId, and leave the unfinished call without a result', async () => {
    const result = await ingestFixture()

    const [placeholder] = accountingOf(result.records, 'S1')
    const tools = toolsOf(result.records, 'S1')
    expect(tools).toHaveLength(3)
    for (const tool of tools) {
      expect(tool.parentSpanId).toBe(placeholder!.spanId)
    }

    const root1 = tools.find((tool) => callIdOf(tool) === 'call-root-1')
    expect(root1).toMatchObject({ name: 'view', status: 'ok' })
    expect(rawOf(root1!).result).toBe('ROOT RESULT ONE')

    const root2 = tools.find((tool) => callIdOf(tool) === 'call-root-2')
    expect(root2).toMatchObject({ name: 'bash', status: 'unset' })
    expect(rawOf(root2!).result).toBeUndefined()
  })

  it('(c) the subagent tool call carries its agentId, and the root calls do not', async () => {
    const result = await ingestFixture()

    const tools = toolsOf(result.records, 'S1')
    const sub = tools.find((tool) => callIdOf(tool) === 'call-sub-1')
    expect(sub).toMatchObject({ name: 'grep', status: 'ok' })
    expect(rawOf(sub!).result).toBe('SUB RESULT ONE')
    expect(JSON.stringify(rawOf(sub!))).toContain('agent-sub-1')

    const root1 = tools.find((tool) => callIdOf(tool) === 'call-root-1')
    expect(JSON.stringify(rawOf(root1!))).not.toContain('agent-sub-1')
  })

  it('(c) S4 tool calls hang off its earliest rollup, one record per call, never one per rollup leg', async () => {
    const result = await ingestFixture()

    const legs = accountingOf(result.records, 'S4')
    const earliest = legs.reduce((a, b) =>
      new Date(a.timestamp).getTime() <= new Date(b.timestamp).getTime() ? a : b,
    )
    expect(new Date(earliest.timestamp).toISOString()).toBe('2026-10-20T09:00:00.000Z')

    const tools = toolsOf(result.records, 'S4')
    expect(tools).toHaveLength(1)
    expect(tools[0]!.parentSpanId).toBe(earliest.spanId)
    expect(rawOf(tools[0]!).result).toBe('S4 RESULT')
  })

  it('(d) S2, which has no journal, carries no parts and no tool records', async () => {
    const result = await ingestFixture()

    const placeholders = accountingOf(result.records, 'S2')
    expect(placeholders).toHaveLength(1)
    expect(placeholders[0]!.parts ?? []).toEqual([])
    expect(toolsOf(result.records, 'S2')).toEqual([])
  })

  it('(e) S3 is not matched from pending-session:S3, and no journal attaches to another session', async () => {
    const result = await ingestFixture()

    const [placeholder] = accountingOf(result.records, 'S3')
    expect(placeholder).toBeDefined()
    expect(placeholder!.parts ?? []).toEqual([])
    expect(toolsOf(result.records, 'S3')).toEqual([])
    expect(JSON.stringify(result.records)).not.toContain(PENDING_S3)
    expect(result.records.filter((record) => callIdOf(record) === 'call-pending-1')).toEqual([])

    for (const record of result.records) {
      for (const text of systemPromptsOf(record)) {
        expect(text, `system prompt on ${record.spanId}`).toBe(ROOT_OF[String(record.sessionId)])
      }
    }
  })

  it('(f) an empty copilot-cli load whose filePath is events.jsonl runs the content reader with no parse problem (D24)', async () => {
    const { s4Journal } = writeFixture()
    const readSpy = vi.spyOn(copilotCliReader, 'read')
    try {
      const result = await ingestProviders(['copilot-cli'], () => ({ calls: [], filePath: s4Journal }))

      expect(result.problems).toEqual([])
      expect(readSpy.mock.calls.map(([file]) => file)).toContain(s4Journal)
    } finally {
      readSpy.mockRestore()
    }
  })

  it('(f) an empty copilot-cli load whose filePath ends in .db keeps its store rows, counts and costs unchanged (D24)', async () => {
    const { storePath } = writeFixture()

    const fromStore = await ingestProviders(['copilot'], () => ({ calls: [], filePath: storePath }))
    const explicit = await ingestProviders(['copilot'], () => ({ calls: loadCopilotCliCalls(storePath), filePath: storePath }))

    expect(fromStore.problems).toEqual([])
    const storeRows = fromStore.records.filter(isAccounting)
    const explicitRows = explicit.records.filter(isAccounting)
    expect(storeRows).toHaveLength(3)
    expect(storeRows.map((record) => record.sessionId)).toEqual(['S1', 'S2', 'S3'])
    expect(storeRows.map((record) => record.cost)).toEqual(explicitRows.map((record) => record.cost))
    expect(storeRows.map((record) => record.tokens)).toEqual(explicitRows.map((record) => record.tokens))
  })

  /** Adds one `sessions` row to a store that writeStore() already created. */
  function addStoreRow(storePath: string, id: string): void {
    const db = new DatabaseSync(storePath)
    try {
      db.prepare('INSERT INTO sessions (id, cwd, repository) VALUES (?, ?, ?)').run(id, `/synthetic/${id}`, `synthetic/${id}`)
    } finally {
      db.close()
    }
  }

  // A session that has a session-store row is owned by the store unit for its tool
  // records: they hang off its placeholder. Its events.jsonl rollup unit reads the
  // same journal, so if it also emitted tool calls every call would be filed twice,
  // once under the placeholder and once under a rollup leg.
  it('(g) a session with a store row emits each tool call once, on its store placeholder, and its rollup legs carry the prompt with no tool children', async () => {
    const ROOT_S5 = 'Root prompt for S5.\n<available_skills>\n<skill><name>delta-skill</name><description>Does delta work.</description><location>/skills/delta</location></skill>\n</available_skills>'
    const storePath = writeStore()
    addStoreRow(storePath, 'S5')
    const s5Journal = writeJournal('S5', [
      { type: 'system.message', data: { role: 'system', content: ROOT_S5 } },
      {
        type: 'assistant.message',
        data: { messageId: 'msg-s5-1', toolRequests: [{ toolCallId: 'call-s5-1', name: 'view', arguments: { path: '/synthetic/s5.txt' } }] },
      },
      { type: 'tool.execution_complete', data: { toolCallId: 'call-s5-1', success: true, result: { content: 'S5 RESULT' } } },
      { type: 'session.shutdown', data: { modelMetrics: {} } },
    ])
    // Two rollup legs for S5, listed later-first so "earliest" is not "first in input".
    const late = { ...shutdownLeg('gpt-5', 1, '2026-10-20T11:00:00.000Z', 3_000, 0.015), sessionId: 'S5', deduplicationKey: 'copilot:S5:shutdown:gpt-5:1' }
    const early = { ...shutdownLeg('claude-sonnet-5-5', 1, '2026-10-20T09:00:00.000Z', 4_000, 0.02), sessionId: 'S5', deduplicationKey: 'copilot:S5:shutdown:claude-sonnet-5-5:1' }

    // The store unit alone, to name the placeholder row the store owns for S5.
    const storeOnly = await ingestProviders(['copilot'], () => ({ calls: [], filePath: storePath }))
    const [placeholder] = accountingOf(storeOnly.records, 'S5')
    expect(placeholder, 'store placeholder for S5').toBeDefined()

    const result = await ingestProviders(['copilot', 'copilot-cli'], (provider): ProviderLoaderResult => {
      if (provider === 'copilot') return { calls: [], filePath: storePath }
      if (provider === 'copilot-cli') return { calls: [late, early], filePath: s5Journal }
      return null
    })

    expect(result.problems).toEqual([])
    const tools = toolsOf(result.records, 'S5')
    expect(tools.map(callIdOf), 'one tool.invoke per journal call').toEqual(['call-s5-1'])
    expect(tools[0]!.parentSpanId).toBe(placeholder!.spanId)

    const legs = accountingOf(result.records, 'S5').filter((record) => record.spanId !== placeholder!.spanId)
    expect(legs).toHaveLength(2)
    for (const leg of legs) {
      expect(systemPromptsOf(leg), `system prompt on ${leg.spanId}`).toEqual([ROOT_S5])
      expect(
        toolsOf(result.records, 'S5').filter((tool) => tool.parentSpanId === leg.spanId),
        `tool children of rollup leg ${leg.spanId}`,
      ).toEqual([])
    }
  })

  it('(h) every tool child of the S1 carrier states system_prompt not measurable, while the carrier measures it (D29)', async () => {
    const result = await ingestFixture()

    const [carrier] = accountingOf(result.records, 'S1')
    expect(carrier!.measurability?.['system_prompt']).toBe('measured')
    const tools = toolsOf(result.records, 'S1')
    expect(tools.length).toBeGreaterThan(0)
    for (const tool of tools) {
      expect(tool.measurability?.['system_prompt'], `system_prompt on ${tool.spanId}`).toEqual(
        measurabilityFor('copilot-cli')['system_prompt'],
      )
    }
  })
})

// ---------------------------------------------------------------------------
// D29 end to end — the Antigravity MCP catalogue lands on the first call record.
// That call is the carrier of its tool_definitions; the agy-step tool children
// the transcript hangs under it must not claim the catalogue as their own. The
// fixtures are the synthetic gen_metadata and transcript the reader tests use.
// ---------------------------------------------------------------------------

const AGY_CONVERSATION = 'fixture-segments'
const AGY_FIXTURE_ROOT = fileURLToPath(new URL('../../tests/fixtures/antigravity-transcript/', import.meta.url))

/** A synthetic Antigravity CLI root: one conversation database, optionally a transcript and an MCP tool. */
function antigravityRoot(options: { transcript: boolean; mcpTool: boolean }): string {
  const root = mkdtempSync(join(tmpdir(), 'kyber-agy-e2e-'))
  tempRoots.push(root)
  mkdirSync(join(root, 'conversations'), { recursive: true })
  const fixture = JSON.parse(readFileSync(join(AGY_FIXTURE_ROOT, 'gen-metadata.json'), 'utf-8')) as {
    rows: Array<{ idx: number; hex: string }>
  }
  const db = new DatabaseSync(join(root, 'conversations', `${AGY_CONVERSATION}.db`))
  try {
    db.exec('CREATE TABLE gen_metadata (idx integer, data blob, size integer NOT NULL DEFAULT 0, PRIMARY KEY (idx))')
    for (const row of fixture.rows) {
      const data = Buffer.from(row.hex, 'hex')
      db.prepare('INSERT INTO gen_metadata (idx, data, size) VALUES (?, ?, ?)').run(row.idx, data, data.length)
    }
  } finally {
    db.close()
  }
  if (options.transcript) {
    const logs = join(root, 'brain', AGY_CONVERSATION, '.system_generated', 'logs')
    mkdirSync(logs, { recursive: true })
    copyFileSync(
      join(AGY_FIXTURE_ROOT, 'brain', AGY_CONVERSATION, '.system_generated', 'logs', 'transcript.jsonl'),
      join(logs, 'transcript.jsonl'),
    )
  }
  if (options.mcpTool) {
    mkdirSync(join(root, 'mcp', 'srv-a'), { recursive: true })
    writeFileSync(join(root, 'mcp', 'srv-a', 'alpha_lookup.json'), JSON.stringify({ name: 'alpha_lookup', description: 'Looks up alpha.' }))
  }
  return root
}

/** Parser calls for a conversation database, through the real Antigravity provider and its pairing. */
async function antigravityCalls(dbPath: string): Promise<ParsedProviderCall[]> {
  const cacheDir = mkdtempSync(join(tmpdir(), 'kyber-agy-e2e-cache-'))
  tempRoots.push(cacheDir)
  vi.stubEnv('KYBERDASH_CACHE_DIR', cacheDir)
  try {
    const parser = createAntigravityProvider().createSessionParser(
      { path: dbPath, project: 'antigravity-cli', provider: 'antigravity' },
      new Set(),
    )
    const parsedCalls: ParsedProviderCall[] = []
    for await (const parsedCall of parser.parse()) parsedCalls.push(parsedCall)
    return parsedCalls
  } finally {
    vi.unstubAllEnvs()
  }
}

describe('D29 — Antigravity catalogue carrier and its tool children through ingestProviders', () => {
  it('states tool_definitions not measurable on every tool child of the catalogue-carrying call, while the carrier measures it', async () => {
    const root = antigravityRoot({ transcript: true, mcpTool: true })
    const dbPath = join(root, 'conversations', `${AGY_CONVERSATION}.db`)
    const calls = await antigravityCalls(dbPath)

    const result = await ingestProviders(['antigravity-cli'], () => ({
      calls,
      filePath: dbPath,
      harnessId: 'antigravity-cli',
      sourceKey: `antigravity-cli:${AGY_CONVERSATION}`,
    }))

    expect(result.problems).toEqual([])
    const carriers = result.records.filter(
      (record) => record.op !== 'tool.invoke' && record.parts?.some((part) => part.part === 'tool_definitions'),
    )
    expect(carriers).toHaveLength(1)
    const [carrier] = carriers
    expect(carrier!.measurability?.['tool_definitions']).toBe('measured')

    const children = result.records.filter((record) => record.op === 'tool.invoke' && record.parentSpanId === carrier!.spanId)
    expect(children.length, 'transcript tool children of the catalogue carrier').toBeGreaterThan(0)
    for (const child of children) {
      expect(child.measurability?.['tool_definitions'], `tool_definitions on ${child.spanId}`).toEqual(
        measurabilityFor('antigravity')['tool_definitions'],
      )
    }
  })
})
