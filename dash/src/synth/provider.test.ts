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
import { tokenValidator } from '../canon/adapters/quarantine.js'
import { contextLimitOf } from '../canon/context-window.js'
import { Synthesizer } from './synth.js'
import { PROVIDER_PARSE_ERROR, ingestProviders } from './provider.js'

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
    expect(result.records.find((record) => record.spanId.endsWith(':cursor-request-1'))?.tokens.reportedInput).toBe(240)
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
