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

import { afterAll, describe, expect, it } from 'vitest'
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { DatabaseSync } from 'node:sqlite'

import type { ParsedProviderCall } from '../providers/types.js'
import { copilot } from '../providers/copilot.js'
import { createCursorProvider } from '../providers/cursor.js'
import { createKiloCodeProvider } from '../providers/kilo-code.js'
import { tokenValidator } from '../canon/adapters/quarantine.js'
import { contextLimitOf } from '../canon/context-window.js'
import { CanonStore } from '../canon/store.js'
import { Synthesizer } from './synth.js'
import { PROVIDER_PARSE_ERROR, ingestProviders } from './provider.js'
import { codexReader } from './readers/codex.js'

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

/**
 * A kilo.db shaped so index pairing and id pairing disagree: a zero-token
 * assistant message sits between the two token-bearing ones. `buildAssistantCall`
 * yields a call for it (it carries text), the reader yields no turn for it, so
 * the call list is one longer than the turn list.
 */
function kiloPairingStore(): { sessionId: string; sourcePath: string } {
  const root = mkdtempSync(join(tmpdir(), 'kyber-kilo-pairing-'))
  tempRoots.push(root)
  const dbPath = join(root, 'kilo.db')
  const sessionId = 'sess-kilo-pairing'
  const db = new DatabaseSync(dbPath)
  try {
    db.exec(`
      CREATE TABLE session (
        id TEXT PRIMARY KEY, project_id TEXT NOT NULL, parent_id TEXT,
        slug TEXT NOT NULL, directory TEXT NOT NULL, title TEXT NOT NULL,
        version TEXT NOT NULL, time_created INTEGER, time_updated INTEGER,
        time_archived INTEGER
      );
      CREATE TABLE message (
        id TEXT PRIMARY KEY, session_id TEXT NOT NULL,
        time_created INTEGER, time_updated INTEGER, data TEXT NOT NULL
      );
      CREATE TABLE part (
        id TEXT PRIMARY KEY, message_id TEXT NOT NULL,
        session_id TEXT NOT NULL, time_created INTEGER,
        time_updated INTEGER, data TEXT NOT NULL
      );
    `)

    const now = Date.parse('2026-09-10T10:00:00.000Z')
    db.prepare(`INSERT INTO session (id, project_id, parent_id, slug, directory, title, version,
                                     time_created, time_updated, time_archived)
                VALUES (?, 'proj-1', NULL, 'slug-pairing', '/Users/hal/myproject', 'Kilo pairing',
                        '1.4.2', ?, ?, NULL)`)
      .run(sessionId, now, now)

    const message = (id: string, offsetMs: number, data: Record<string, unknown>) => {
      db.prepare(`INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES (?, ?, ?, ?, ?)`)
        .run(id, sessionId, now + offsetMs, now + offsetMs, JSON.stringify(data))
    }
    const part = (id: string, messageId: string, offsetMs: number, data: Record<string, unknown>) => {
      db.prepare(`INSERT INTO part (id, message_id, session_id, time_created, time_updated, data) VALUES (?, ?, ?, ?, ?, ?)`)
        .run(id, messageId, sessionId, now + offsetMs, now + offsetMs, JSON.stringify(data))
    }

    message('msg-u1', 0, { role: 'user' })
    part('part-u1', 'msg-u1', 0, { type: 'text', text: 'first question' })
    message('msg-a1', 1000, { role: 'assistant', modelID: 'claude-opus-4-6', tokens_input: 100, tokens_output: 20 })
    part('part-a1-text', 'msg-a1', 1000, { type: 'text', text: 'first answer' })
    part('part-a1-tool', 'msg-a1', 1000, {
      type: 'tool',
      callID: 'call-1',
      tool: 'bash',
      state: { status: 'completed', input: { command: 'ls' }, output: 'first tool output' },
    })
    message('msg-router', 2000, { role: 'assistant', modelID: 'edenai/router-model' })
    part('part-router', 'msg-router', 2000, { type: 'text', text: 'router filler answer' })
    message('msg-u2', 3000, { role: 'user' })
    part('part-u2', 'msg-u2', 3000, { type: 'text', text: 'second question' })
    message('msg-a2', 4000, { role: 'assistant', modelID: 'claude-opus-4-6', tokens_input: 300, tokens_output: 60 })
    part('part-a2-text', 'msg-a2', 4000, { type: 'text', text: 'second answer' })
    part('part-a2-tool', 'msg-a2', 4000, {
      type: 'tool',
      callID: 'call-2',
      tool: 'read',
      state: { status: 'error', input: { filePath: '/nope' }, output: 'ENOENT: no such file' },
    })
  } finally {
    db.close()
  }
  return { sessionId, sourcePath: `${dbPath}:${sessionId}` }
}

/** Parse a Kilo source unit through the provider seam, as refresh does. */
async function kiloCalls(sourcePath: string): Promise<ParsedProviderCall[]> {
  const provider = createKiloCodeProvider()
  const source = { path: sourcePath, project: 'myproject', provider: 'kilo-code' }
  const calls: ParsedProviderCall[] = []
  for await (const entry of provider.createSessionParser(source, new Set()).parse()) calls.push(entry)
  return calls
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

// ---------------------------------------------------------------------------
// T2 — Kilo reader parts pair to their call by pairingId (decision D7)
// ---------------------------------------------------------------------------

describe('T2 — Kilo reader-to-call pairing', () => {
  async function ingestKilo(sourcePath: string, sessionId: string) {
    const calls = await kiloCalls(sourcePath)
    const result = await ingestProviders(['kilo-code'], () => ({
      calls,
      filePath: sourcePath,
      harnessId: 'kilo-shared-runtime',
      sourceKey: `kilo-shared-runtime:${sessionId}`,
    }))
    return { calls, result }
  }

  it('attaches each assistant message its own conversation and tool-result parts, not its neighbour\'s', async () => {
    const { sessionId, sourcePath } = kiloPairingStore()
    const { calls, result } = await ingestKilo(sourcePath, sessionId)

    // Premise: the parser yields a call for the zero-token router message, so
    // the call list is longer than the turn list and index pairing cannot be
    // right — every call after the skipped message would take its neighbour's
    // content.
    expect(calls.map((call) => call.deduplicationKey)).toEqual([
      `kilo-code:${sessionId}:msg-a1`,
      `kilo-code:${sessionId}:msg-router`,
      `kilo-code:${sessionId}:msg-a2`,
    ])
    expect(result.problems).toEqual([])

    const turns = result.records.filter((record) => record.op === 'llm.invoke')
    expect(turns).toHaveLength(3)
    const [first, router, second] = turns

    expect(first!.content.conversation_history).toBe('first question\nfirst answer')
    expect(first!.content.tool_result_content).toBe('first tool output')
    // The router message has no reader turn: no parts at all, and — the point —
    // it did not take the next message's parts positionally.
    expect(router!.parts).toBeUndefined()
    expect(router!.content).toEqual({})
    expect(second!.content.conversation_history).toBe('second question\nsecond answer')
    expect(second!.content.tool_result_content).toBe('ENOENT: no such file')
  })

  it('synthesizes each tool call under the turn it belongs to, with the errored result marked', async () => {
    const { sessionId, sourcePath } = kiloPairingStore()
    const { result } = await ingestKilo(sourcePath, sessionId)

    const parents = result.records.filter((record) => record.op === 'llm.invoke')
    const toolRecords = result.records.filter((record) => record.op === 'tool.invoke')
    expect(toolRecords.map((record) => record.name)).toEqual(['bash', 'read'])
    expect(toolRecords.map((record) => record.parentSpanId)).toEqual([parents[0]!.spanId, parents[2]!.spanId])
    expect(toolRecords[1]!.status, 'a state.status === "error" part is an errored tool record').toBe('error')
    expect(toolRecords[1]!.parts?.[0]?.text).toBe('ENOENT: no such file')
  })

  it('declares the record\'s content measured while prompt, definitions and schema stay unmeasurable', async () => {
    const { sessionId, sourcePath } = kiloPairingStore()
    const { result } = await ingestKilo(sourcePath, sessionId)
    const first = result.records.filter((record) => record.op === 'llm.invoke')[0]!

    expect(first.measurability?.['conversation_history'], 'kilo.db stores the conversation').toBeUndefined()
    expect(first.measurability?.['tool_result_content'], 'kilo.db stores completed tool output').toBeUndefined()
    for (const metric of ['system_prompt', 'tool_definitions', 'instruction_context', 'schema_cost']) {
      expect(first.measurability?.[metric], `${metric} must stay declared not measurable`).toMatchObject({
        availability: 'not_measurable',
        reason: expect.any(String),
      })
    }
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
