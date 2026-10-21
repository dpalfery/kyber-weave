// Command Code content reader contract: one ReaderTurn per assistant message,
// keyed by the message id, with conversation text, tool calls, and tool
// results taken only from what the transcript stored.
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterAll, describe, expect, it } from 'vitest'

import { createCommandCodeProvider } from '../../providers/commandcode.js'
import type { ParsedProviderCall } from '../../providers/types.js'
import { HARNESS_CONTENT_CAPABILITIES, parserContractVersionFor } from '../../refresh/registry.js'
import { PROVIDER_READERS } from '../provider.js'
import { READER_UNMEASURABLE } from '../../canon/measurability.js'
import { commandcodeReader } from './commandcode.js'
import type { ReaderTurn } from './types.js'
import { Synthesizer } from '../synth.js'
import { ingestProviders } from '../provider.js'

const tempRoots: string[] = []

afterAll(() => {
  for (const root of tempRoots) rmSync(root, { recursive: true, force: true })
})

function writeSession(entries: unknown[]): string {
  const root = mkdtempSync(join(tmpdir(), 'kyber-commandcode-reader-'))
  tempRoots.push(root)
  const path = join(root, 'synthetic-commandcode-session.jsonl')
  writeFileSync(path, entries.map((entry) => JSON.stringify(entry)).join('\n') + '\n')
  return path
}

async function readTurns(path: string): Promise<ReaderTurn[]> {
  const turns: ReaderTurn[] = []
  for await (const turn of commandcodeReader.read(path)) turns.push(turn)
  return turns
}

const header = { type: 'session', version: 3, id: 'cc-session-1', timestamp: '2026-10-21T10:00:00.000Z', cwd: '/synthetic/project' }

const userText = (id: string, text: string) => ({
  type: 'message',
  id,
  parentId: null,
  timestamp: '2026-10-21T10:00:01.000Z',
  message: { role: 'user', content: [{ type: 'text', text }] },
})

const assistant = (id: string, content: unknown[], usage?: Record<string, number>) => ({
  type: 'message',
  id,
  parentId: 'u1',
  timestamp: '2026-10-21T10:00:02.000Z',
  message: { role: 'assistant', content },
  model: 'synthetic/model',
  ...(usage ? { usage } : {}),
})

const toolResultMessage = (id: string, toolUseId: string, content: string) => ({
  type: 'message',
  id,
  parentId: 'a1',
  timestamp: '2026-10-21T10:00:03.000Z',
  message: { role: 'user', content: [{ type: 'tool_result', tool_use_id: toolUseId, content }] },
})

describe('commandcode reader', () => {
  it('yields one turn per assistant message keyed by the message id with the session id', async () => {
    const path = writeSession([
      header,
      userText('u1', 'hello'),
      assistant('a1', [{ type: 'text', text: 'first reply' }]),
      userText('u2', 'again'),
      assistant('a2', [{ type: 'text', text: 'second reply' }]),
    ])

    const turns = await readTurns(path)

    expect(turns.map((turn) => turn.nativeRecordId)).toEqual(['a1', 'a2'])
    expect(turns.every((turn) => turn.sessionId === 'cc-session-1')).toBe(true)
    expect(turns[0]?.parts).toEqual([{ part: 'conversation_history', text: 'first reply', order: 0 }])
    expect(turns[1]?.parts).toEqual([{ part: 'conversation_history', text: 'second reply', order: 0 }])
  })

  it('takes tool calls from tool_use blocks and attaches the following tool results to their turn', async () => {
    const path = writeSession([
      header,
      userText('u1', 'run it'),
      assistant('a1', [
        { type: 'text', text: 'running' },
        { type: 'tool_use', id: 'call-1', name: 'shell_command', input: { command: 'ls' } },
      ]),
      toolResultMessage('r1', 'call-1', 'file.txt'),
      assistant('a2', [{ type: 'text', text: 'done' }]),
    ])

    const turns = await readTurns(path)

    expect(turns).toHaveLength(2)
    expect(turns[0]?.toolCalls).toEqual([{ id: 'call-1', name: 'shell_command', arguments: { command: 'ls' } }])
    expect(turns[0]?.toolResults).toEqual([{ toolCallId: 'call-1', content: 'file.txt', isError: false }])
    expect(turns[1]?.toolCalls).toBeUndefined()
    expect(turns[1]?.toolResults).toBeUndefined()
  })

  it('flattens array tool_result content to its text, not a JSON envelope', async () => {
    const path = writeSession([
      header,
      userText('u1', 'run it'),
      assistant('a1', [{ type: 'tool_use', id: 'call-1', name: 'shell_command', input: { command: 'ls' } }]),
      {
        type: 'message',
        id: 'r1',
        parentId: 'a1',
        timestamp: '2026-10-21T10:00:03.000Z',
        message: {
          role: 'user',
          content: [{ type: 'tool_result', tool_use_id: 'call-1', content: [{ type: 'text', text: 'file.txt' }] }],
        },
      },
    ])

    const turns = await readTurns(path)

    expect(turns[0]?.toolResults).toEqual([{ toolCallId: 'call-1', content: 'file.txt', isError: false }])
  })

  it('keeps plain strings in array tool_result content unquoted', async () => {
    const path = writeSession([
      header,
      userText('u1', 'run it'),
      assistant('a1', [{ type: 'tool_use', id: 'call-1', name: 'shell_command', input: { command: 'ls' } }]),
      {
        type: 'message',
        id: 'r1',
        parentId: 'a1',
        timestamp: '2026-10-21T10:00:03.000Z',
        message: {
          role: 'user',
          content: [{ type: 'tool_result', tool_use_id: 'call-1', content: ['file.txt'] }],
        },
      },
    ])

    const turns = await readTurns(path)

    expect(turns[0]?.toolResults).toEqual([{ toolCallId: 'call-1', content: 'file.txt', isError: false }])
  })

  it('emits nothing for a transcript without assistant messages', async () => {
    const path = writeSession([header, userText('u1', 'only a prompt')])
    expect(await readTurns(path)).toEqual([])
  })
})

describe('commandcode registry wiring', () => {
  it('registers the reader and keeps the reader audit consistent', () => {
    expect(PROVIDER_READERS.get('commandcode')).toBe(commandcodeReader)
    expect(HARNESS_CONTENT_CAPABILITIES.get('commandcode')?.reader).toBe(true)
  })

  it('bumps the parser contract to 2 so checkpoints re-open', () => {
    expect(parserContractVersionFor({ providerName: 'commandcode', harnessId: 'commandcode' })).toBe('2')
  })

  it('keeps the honest-unobservability buckets not_measurable', () => {
    // tool_result_content is not a bucket here: the reader carries the flattened
    // tool_result text, and declaring it not_measurable would deny that text.
    expect(READER_UNMEASURABLE.get('commandcode')).toEqual([
      'schema_cost',
      'system_prompt',
      'instruction_context',
      'tool_definitions',
    ])
  })
})

describe('commandcode provider pairing', () => {
  it('keys each assistant call on the message id as pairingId, matching the reader nativeRecordId', async () => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-commandcode-pairing-'))
    tempRoots.push(root)
    const dir = join(root, 'users-synthetic-project')
    mkdirSync(dir, { recursive: true })
    const entries = [
      header,
      userText('u1', 'hello'),
      assistant('a1', [{ type: 'text', text: 'reply' }], {
        inputTokens: 10,
        outputTokens: 5,
        cacheReadTokens: 0,
        cacheWriteTokens: 0,
      }),
    ]
    writeFileSync(join(dir, 'cc-session-1.jsonl'), entries.map((entry) => JSON.stringify(entry)).join('\n') + '\n')

    const provider = createCommandCodeProvider(root)
    const calls: ParsedProviderCall[] = []
    for (const source of await provider.discoverSessions()) {
      for await (const call of provider.createSessionParser(source, new Set()).parse()) calls.push(call)
    }

    expect(calls).toHaveLength(1)
    expect(calls[0]?.pairingId).toBe('a1')
    expect(calls[0]?.turnId).toBeUndefined()
    const turns = await readTurns(join(dir, 'cc-session-1.jsonl'))
    expect(turns[0]?.nativeRecordId).toBe(calls[0]?.pairingId)
  })
})

// Characterization: the provider call and reader turn already reach the record
// through the pairing path (provider.ts envelopesFor / Synthesizer), so no failing
// run exists for the flattened-text path. It pins the end-to-end contract that
// flattened tool_result text lands on the tool.invoke record. The declaration
// guard is a separate test below so its outcome never masks this one.
describe('commandcode end-to-end synthesis', () => {
  async function synthesizeListFilesSession() {
    const root = mkdtempSync(join(tmpdir(), 'kyber-commandcode-e2e-'))
    tempRoots.push(root)
    const dir = join(root, 'users-synthetic-project')
    mkdirSync(dir, { recursive: true })
    const entries = [
      header,
      userText('u1', 'list files'),
      assistant(
        'a1',
        [
          { type: 'text', text: 'listing' },
          { type: 'tool_use', id: 'call-1', name: 'shell_command', input: { command: 'ls' } },
        ],
        { inputTokens: 10, outputTokens: 5, cacheReadTokens: 0, cacheWriteTokens: 0 },
      ),
      {
        type: 'message',
        id: 'r1',
        parentId: 'a1',
        timestamp: '2026-10-21T10:00:03.000Z',
        message: {
          role: 'user',
          content: [{ type: 'tool_result', tool_use_id: 'call-1', content: [{ type: 'text', text: 'file.txt' }] }],
        },
      },
    ]
    const path = join(dir, 'cc-session-1.jsonl')
    writeFileSync(path, entries.map((entry) => JSON.stringify(entry)).join('\n') + '\n')

    const provider = createCommandCodeProvider(root)
    const calls: ParsedProviderCall[] = []
    for (const source of await provider.discoverSessions()) {
      for await (const call of provider.createSessionParser(source, new Set()).parse()) calls.push(call)
    }
    const turns = await readTurns(path)
    // Paired by index, as matchingTurns aligns them for this single-turn file.
    return new Synthesizer().synthesize(calls, turns)
  }

  it('carries conversation text and flattened tool_result text onto the synthesized records', async () => {
    const records = await synthesizeListFilesSession()

    const llm = records.find((record) => record.op === 'llm.invoke')
    const tool = records.find((record) => record.op === 'tool.invoke')
    expect(llm?.parts?.some((part) => part.part === 'conversation_history' && part.text === 'listing')).toBe(true)
    expect(tool?.name).toBe('shell_command')
    expect(tool?.parts?.some((part) => part.part === 'tool_result_content' && part.text === 'file.txt')).toBe(true)
  })

  // Guards the declaration against the record that carries the text: a
  // not_measurable stamp for tool_result_content here would present flattened
  // tool output as absent. An absent declaration states no limitation, so only
  // the not_measurable object is refused.
  it('does not declare tool_result_content not_measurable on the tool.invoke record that carries its text', async () => {
    const records = await synthesizeListFilesSession()

    const tool = records.find((record) => record.op === 'tool.invoke')
    expect(tool?.parts?.some((part) => part.part === 'tool_result_content' && part.text === 'file.txt')).toBe(true)
    const declared = tool?.measurability?.['tool_result_content']
    expect(typeof declared === 'object' ? declared.availability : declared).not.toBe('not_measurable')
  })
})

// Span identity must not depend on the pairing key. Shipped spans derive from
// the session, timestamp, and ordinal digest; a pairing key stamped as
// `turnId` would re-key every span and duplicate rows on a contract re-parse.
describe('commandcode span identity', () => {
  it('keeps record spanIds identical to the pre-pairing identity', async () => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-commandcode-identity-'))
    tempRoots.push(root)
    const dir = join(root, 'users-synthetic-project')
    mkdirSync(dir, { recursive: true })
    const entries = [
      header,
      userText('u1', 'hello'),
      assistant('a1', [{ type: 'text', text: 'reply' }], { inputTokens: 10, outputTokens: 5, cacheReadTokens: 0, cacheWriteTokens: 0 }),
    ]
    const path = join(dir, 'cc-session-1.jsonl')
    writeFileSync(path, entries.map((entry) => JSON.stringify(entry)).join('\n') + '\n')

    const provider = createCommandCodeProvider(root)
    const calls: ParsedProviderCall[] = []
    for (const source of await provider.discoverSessions()) {
      for await (const call of provider.createSessionParser(source, new Set()).parse()) calls.push(call)
    }
    // The pre-pairing shape: no pairing key on the call at all.
    const baselineCalls = calls.map(({ turnId: _turnId, pairingId: _pairingId, ...rest }) => rest as ParsedProviderCall)

    const baseline = await ingestProviders(['commandcode'], () => ({ calls: baselineCalls, filePath: path }))
    const paired = await ingestProviders(['commandcode'], () => ({ calls, filePath: path }))

    const baselineIds = baseline.records.map((record) => record.spanId)
    expect(baselineIds.length).toBeGreaterThan(0)
    expect(paired.records.map((record) => record.spanId)).toEqual(baselineIds)
  })
})

// Guard for pairing: the second assistant's call must carry only its own
// content. The first assistant has no usage, so the provider yields no call
// for it; a positional fallback would hand the call the wrong turn.
describe('commandcode pairing guard', () => {
  it('pairs each call with its own assistant message when an earlier assistant has no usage', async () => {
    const path = writeSession([
      header,
      userText('u1', 'first'),
      assistant('a2', [{ type: 'text', text: 'ORPHAN-A2' }]),
      userText('u2', 'second'),
      assistant('a3', [{ type: 'text', text: 'A3-OWN' }], { inputTokens: 10, outputTokens: 5, cacheReadTokens: 0, cacheWriteTokens: 0 }),
    ])
    const root = mkdtempSync(join(tmpdir(), 'kyber-commandcode-guard-'))
    tempRoots.push(root)
    const dir = join(root, 'users-synthetic-project')
    mkdirSync(dir, { recursive: true })
    const target = join(dir, 'cc-session-1.jsonl')
    writeFileSync(target, readFileSync(path, 'utf-8'))

    const provider = createCommandCodeProvider(root)
    const calls: ParsedProviderCall[] = []
    for (const source of await provider.discoverSessions()) {
      for await (const call of provider.createSessionParser(source, new Set()).parse()) calls.push(call)
    }
    expect(calls.map((call) => call.turnId ?? call.pairingId)).toEqual(['a3'])

    const records = (await ingestProviders(['commandcode'], () => ({ calls, filePath: target }))).records
    const llm = records.find((record) => record.op === 'llm.invoke')
    const texts = (llm?.parts ?? []).filter((part) => part.part === 'conversation_history').map((part) => part.text)
    expect(texts).toEqual(['A3-OWN'])
  })
})
