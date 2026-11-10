// Droid content reader contract: one ReaderTurn per assistant message entry,
// keyed by that entry's id, with conversation text, tool calls, and tool
// results taken only from what the transcript stored. A malformed line is
// skipped rather than failing the whole session.
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'

import { afterAll, describe, expect, it } from 'vitest'

import { createDroidProvider } from '../../providers/droid.js'
import type { ParsedProviderCall } from '../../providers/types.js'
import { droidReader } from './droid.js'
import type { ReaderTurn } from './types.js'

const tempRoots: string[] = []

afterAll(() => {
  for (const root of tempRoots) rmSync(root, { recursive: true, force: true })
})

function writeTranscript(lines: string[]): string {
  const root = mkdtempSync(join(tmpdir(), 'kyber-droid-reader-'))
  tempRoots.push(root)
  const path = join(root, 'synthetic-droid-session.jsonl')
  writeFileSync(path, lines.join('\n') + '\n')
  return path
}

async function readTurns(path: string): Promise<ReaderTurn[]> {
  const turns: ReaderTurn[] = []
  for await (const turn of droidReader.read(path)) turns.push(turn)
  return turns
}

const conversationText = (turn: ReaderTurn): string[] =>
  turn.parts.filter((part) => part.part === 'conversation_history').map((part) => part.text)

describe('droidReader', () => {
  it('yields one ReaderTurn per assistant message, keyed by the entry id and the session_start id', async () => {
    const path = writeTranscript([
      JSON.stringify({ type: 'session_start', id: 'session-1', cwd: '/synthetic/project', title: 'Fix login bug' }),
      JSON.stringify({ type: 'message', id: 'u1', timestamp: '2026-11-09T10:00:00.000Z', message: { role: 'user', content: [{ type: 'text', text: 'fix the login bug' }] } }),
      JSON.stringify({ type: 'message', id: 'a1', timestamp: '2026-11-09T10:00:01.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'Looking' }] } }),
      JSON.stringify({ type: 'message', id: 'a2', timestamp: '2026-11-09T10:00:03.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'Done' }] } }),
    ])

    const turns = await readTurns(path)

    expect(turns).toHaveLength(2)
    expect(turns.map((turn) => turn.nativeRecordId)).toEqual(['a1', 'a2'])
    expect(turns.map((turn) => turn.sessionId)).toEqual(['session-1', 'session-1'])
  })

  it('carries text blocks as conversation_history parts', async () => {
    const path = writeTranscript([
      JSON.stringify({ type: 'session_start', id: 'session-1', cwd: '/synthetic/project' }),
      JSON.stringify({ type: 'message', id: 'a1', timestamp: '2026-11-09T10:00:01.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'Looking' }] } }),
    ])

    const [turn] = await readTurns(path)

    expect(conversationText(turn!)).toEqual(['Looking'])
  })

  it('turns each tool_use block into a toolCalls entry stamped with the entry timestamp', async () => {
    const path = writeTranscript([
      JSON.stringify({ type: 'session_start', id: 'session-1', cwd: '/synthetic/project' }),
      JSON.stringify({
        type: 'message',
        id: 'a1',
        timestamp: '2026-11-09T10:00:01.000Z',
        message: {
          role: 'assistant',
          content: [
            { type: 'text', text: 'Looking' },
            { type: 'tool_use', id: 'tu-1', name: 'Read', input: { file_path: 'src/login.ts' } },
            { type: 'tool_use', id: 'tu-2', name: 'Execute', input: { command: 'npm test' } },
          ],
        },
      }),
    ])

    const [turn] = await readTurns(path)

    expect(turn?.toolCalls).toEqual([
      { id: 'tu-1', name: 'Read', arguments: { file_path: 'src/login.ts' }, timestamp: '2026-11-09T10:00:01.000Z' },
      { id: 'tu-2', name: 'Execute', arguments: { command: 'npm test' }, timestamp: '2026-11-09T10:00:01.000Z' },
    ])
  })

  it('attaches a following user tool_result to the latest assistant turn with its error flag', async () => {
    const path = writeTranscript([
      JSON.stringify({ type: 'session_start', id: 'session-1', cwd: '/synthetic/project' }),
      JSON.stringify({
        type: 'message',
        id: 'a1',
        timestamp: '2026-11-09T10:00:01.000Z',
        message: { role: 'assistant', content: [{ type: 'tool_use', id: 'tu-1', name: 'Execute', input: { command: 'npm test' } }] },
      }),
      JSON.stringify({
        type: 'message',
        id: 'u2',
        timestamp: '2026-11-09T10:00:04.000Z',
        message: {
          role: 'user',
          content: [{ type: 'tool_result', tool_use_id: 'tu-1', content: '2 failing', is_error: true }],
        },
      }),
    ])

    const turns = await readTurns(path)

    expect(turns).toHaveLength(1)
    expect(turns[0]?.toolResults).toEqual([
      { toolCallId: 'tu-1', content: '2 failing', isError: true, timestamp: '2026-11-09T10:00:04.000Z' },
    ])
  })

  it('reports a successful tool_result with isError false', async () => {
    const path = writeTranscript([
      JSON.stringify({ type: 'session_start', id: 'session-1', cwd: '/synthetic/project' }),
      JSON.stringify({
        type: 'message',
        id: 'a1',
        timestamp: '2026-11-09T10:00:01.000Z',
        message: { role: 'assistant', content: [{ type: 'tool_use', id: 'tu-1', name: 'Read', input: { file_path: 'a.ts' } }] },
      }),
      JSON.stringify({
        type: 'message',
        id: 'u2',
        timestamp: '2026-11-09T10:00:02.000Z',
        message: { role: 'user', content: [{ type: 'tool_result', tool_use_id: 'tu-1', content: 'file body', is_error: false }] },
      }),
    ])

    const [turn] = await readTurns(path)

    expect(turn?.toolResults).toEqual([
      { toolCallId: 'tu-1', content: 'file body', isError: false, timestamp: '2026-11-09T10:00:02.000Z' },
    ])
  })

  it('skips a malformed line without dropping the turns around it', async () => {
    const path = writeTranscript([
      JSON.stringify({ type: 'session_start', id: 'session-1', cwd: '/synthetic/project' }),
      JSON.stringify({ type: 'message', id: 'a1', timestamp: '2026-11-09T10:00:01.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'before' }] } }),
      '{"type":"message","id":"broken", "message": {',
      JSON.stringify({ type: 'message', id: 'a2', timestamp: '2026-11-09T10:00:03.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'after' }] } }),
    ])

    const turns = await readTurns(path)

    expect(turns.map((turn) => turn.nativeRecordId)).toEqual(['a1', 'a2'])
    expect(turns.map((turn) => conversationText(turn))).toEqual([['before'], ['after']])
  })

  it('declares that Droid turns cannot be paired by position', () => {
    expect(droidReader.positionalPairingUnsafe).toBe(true)
  })

  // Review finding: a tool_use with only a name, or only an id, is not a fully
  // identified call and must not become a toolCalls entry.
  it('emits no tool call for a tool_use that carries only a name or only an id', async () => {
    const path = writeTranscript([
      JSON.stringify({ type: 'session_start', id: 'session-1', cwd: '/synthetic/project' }),
      JSON.stringify({
        type: 'message',
        id: 'a1',
        timestamp: '2026-11-09T10:00:01.000Z',
        message: {
          role: 'assistant',
          content: [
            { type: 'tool_use', name: 'Read', input: { file_path: 'a.ts' } },
            { type: 'tool_use', id: 'tu-9', input: { file_path: 'b.ts' } },
          ],
        },
      }),
    ])

    const [turn] = await readTurns(path)

    expect(turn?.toolCalls).toBeUndefined()
  })

  // Review finding: an assistant message without an id gets a provider pairing id
  // (msg-N) but the reader gave the turn no nativeRecordId. With positional pairing
  // disabled, the counter and its content turn never pair. Both sides must agree.
  it('gives an id-less assistant turn the same pairing id the provider gives its call', async () => {
    const path = writeTranscript([
      JSON.stringify({ type: 'session_start', id: 'session-1', cwd: '/synthetic/project' }),
      JSON.stringify({ type: 'message', id: 'u1', timestamp: '2026-11-09T10:00:00.000Z', message: { role: 'user', content: [{ type: 'text', text: 'hi' }] } }),
      JSON.stringify({ type: 'message', timestamp: '2026-11-09T10:00:01.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'hello' }] } }),
    ])
    writeFileSync(
      path.replace(/\.jsonl$/, '.settings.json'),
      JSON.stringify({ model: 'gpt-5', tokenUsage: { inputTokens: 10, outputTokens: 5, cacheCreationTokens: 0, cacheReadTokens: 0, thinkingTokens: 0 } }),
    )

    const turns = await readTurns(path)
    const provider = createDroidProvider(dirname(path))
    const calls: ParsedProviderCall[] = []
    for await (const call of provider.createSessionParser({ path, project: 'proj', provider: 'droid' }, new Set()).parse()) {
      calls.push(call)
    }

    expect(calls).toHaveLength(1)
    expect(calls[0]?.pairingId).toBeDefined()
    expect(turns[0]?.nativeRecordId).toBe(calls[0]?.pairingId)
  })
})
