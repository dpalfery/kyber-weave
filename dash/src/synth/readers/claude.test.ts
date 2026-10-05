import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { basename, extname, join } from 'node:path'

import { afterAll, describe, expect, it } from 'vitest'

import { loadClaudeCalls } from '../../providers/claude.js'
import { claudeReader, readClaudeSession, splitClaudeTurns } from './claude.js'
import type { ReaderTurn } from './types.js'

const tempRoots: string[] = []

afterAll(() => {
  for (const root of tempRoots) rmSync(root, { recursive: true, force: true })
})

function writeTranscript(entries: unknown[]): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-claude-reader-'))
  tempRoots.push(dir)
  const path = join(dir, 'session.jsonl')
  writeFileSync(path, entries.map(entry => JSON.stringify(entry)).join('\n') + '\n')
  return path
}

async function readTurns(filePath: string): Promise<ReaderTurn[]> {
  const turns: ReaderTurn[] = []
  for await (const turn of claudeReader.read(filePath)) turns.push(turn)
  return turns
}

describe('claudeReader', () => {
  it('reads a transcript path as an async iterable of canonical turns', async () => {
    const path = writeTranscript([
      {
        type: 'user',
        sessionId: 'synthetic-claude-session',
        message: { role: 'user', content: [{ type: 'text', text: 'Synthetic question.' }] },
      },
      {
        type: 'assistant',
        sessionId: 'synthetic-claude-session',
        message: { role: 'assistant', content: [{ type: 'text', text: 'Synthetic answer.' }] },
      },
    ])

    await expect(readTurns(path)).resolves.toEqual([
      expect.objectContaining({
        sessionId: 'synthetic-claude-session',
        parts: expect.arrayContaining([
          expect.objectContaining({ part: 'conversation_history', text: 'Synthetic question.' }),
          expect.objectContaining({ part: 'conversation_history', text: 'Synthetic answer.' }),
        ]),
      }),
    ])
  })

  it('yields 1 ReaderTurn per turn for paired request/response assistant lines (#232)', async () => {
    const usage = {
      input_tokens: 150,
      output_tokens: 45,
      cache_read_input_tokens: 300,
      cache_creation_input_tokens: 200,
    }
    const path = writeTranscript([
      {
        type: 'user',
        sessionId: 'session-paired-read',
        message: { role: 'user', content: [{ type: 'text', text: 'Check git status' }] },
      },
      {
        type: 'assistant',
        sessionId: 'session-paired-read',
        uuid: 'req-1',
        timestamp: '2026-09-01T12:00:00.000Z',
        message: {
          id: 'msg-1',
          model: 'claude-sonnet-4-5',
          usage,
          content: [
            { type: 'tool_use', id: 'tu_bash_1', name: 'Bash', input: { command: 'git status' } },
          ],
        },
      },
      {
        type: 'user',
        sessionId: 'session-paired-read',
        message: {
          role: 'user',
          content: [
            { type: 'tool_result', tool_use_id: 'tu_bash_1', content: 'On branch main' },
          ],
        },
      },
      {
        type: 'assistant',
        sessionId: 'session-paired-read',
        uuid: 'res-1',
        timestamp: '2026-09-01T12:00:05.000Z',
        message: {
          id: 'msg-1',
          model: 'claude-sonnet-4-5',
          usage,
          content: [
            { type: 'text', text: 'You are on main.' },
          ],
        },
      },
    ])

    const turns = await readTurns(path)
    expect(turns).toHaveLength(1)
    const turn = turns[0]!
    // Pairing keys must match loadClaudeCalls' turnId (message.id).
    expect(turn.nativeRecordId).toBe('msg-1')
    expect(turn.toolCalls).toHaveLength(1)
    expect(turn.toolCalls?.[0]?.name).toBe('Bash')
    expect(turn.toolResults).toHaveLength(1)
    expect(turn.toolResults?.[0]?.content).toBe('On branch main')
    const texts = turn.parts.map((p) => p.text)
    expect(texts).toContain('Check git status')
    expect(texts).toContain('You are on main.')
  })

  it('sets nativeRecordId from message.id and honors dateRange (#216 T2)', async () => {
    const usage = {
      input_tokens: 100,
      output_tokens: 20,
      cache_read_input_tokens: 0,
      cache_creation_input_tokens: 0,
    }
    const path = writeTranscript([
      {
        type: 'user',
        sessionId: 'session-window-read',
        message: { role: 'user', content: [{ type: 'text', text: 'before window' }] },
      },
      {
        type: 'assistant',
        sessionId: 'session-window-read',
        uuid: 'uuid-before',
        timestamp: '2026-08-01T12:00:00.000Z',
        message: {
          id: 'msg-before',
          model: 'claude-sonnet-4-5',
          usage,
          content: [{ type: 'text', text: 'before answer' }],
        },
      },
      {
        type: 'user',
        sessionId: 'session-window-read',
        message: { role: 'user', content: [{ type: 'text', text: 'inside window' }] },
      },
      {
        type: 'assistant',
        sessionId: 'session-window-read',
        uuid: 'uuid-inside',
        timestamp: '2026-09-10T12:00:00.000Z',
        message: {
          id: 'msg-inside',
          model: 'claude-sonnet-4-5',
          usage: { ...usage, input_tokens: 200 },
          content: [{ type: 'text', text: 'inside answer' }],
        },
      },
    ])

    const dateRange = {
      start: new Date('2026-09-01T00:00:00.000Z'),
      end: new Date('2026-09-12T00:00:00.000Z'),
    }
    const turns: ReaderTurn[] = []
    for await (const turn of claudeReader.read(path, dateRange)) turns.push(turn)

    expect(turns).toHaveLength(1)
    expect(turns[0]!.nativeRecordId).toBe('msg-inside')
    expect(turns[0]!.parts.map((p) => p.text)).toEqual(
      expect.arrayContaining(['inside window', 'inside answer']),
    )
    expect(turns[0]!.parts.map((p) => p.text).join('\n')).not.toContain('before window')
  })

  // sliceCallsToWindow drops missing/malformed timestamps; the reader must
  // do the same when dateRange is set, or positional pairing skews.
  it('excludes untimestamped turns when dateRange is set (#216 date-range parity)', async () => {
    const usage = {
      input_tokens: 100,
      output_tokens: 20,
      cache_read_input_tokens: 0,
      cache_creation_input_tokens: 0,
    }
    const path = writeTranscript([
      {
        type: 'user',
        sessionId: 'session-no-ts',
        message: { role: 'user', content: [{ type: 'text', text: 'untimestamped question' }] },
      },
      {
        type: 'assistant',
        sessionId: 'session-no-ts',
        uuid: 'uuid-no-ts',
        // no timestamp — loadClaudeCalls stamps epoch 0; sliceCallsToWindow drops it
        message: {
          model: 'claude-sonnet-4-5',
          usage,
          content: [{ type: 'text', text: 'untimestamped answer' }],
        },
      },
      {
        type: 'user',
        sessionId: 'session-no-ts',
        message: { role: 'user', content: [{ type: 'text', text: 'windowed question' }] },
      },
      {
        type: 'assistant',
        sessionId: 'session-no-ts',
        uuid: 'uuid-windowed',
        timestamp: '2026-09-10T12:00:00.000Z',
        message: {
          model: 'claude-sonnet-4-5',
          usage: { ...usage, input_tokens: 200 },
          content: [{ type: 'text', text: 'windowed answer' }],
        },
      },
    ])

    const dateRange = {
      start: new Date('2026-09-01T00:00:00.000Z'),
      end: new Date('2026-09-12T00:00:00.000Z'),
    }
    const turns: ReaderTurn[] = []
    for await (const turn of claudeReader.read(path, dateRange)) turns.push(turn)

    expect(turns).toHaveLength(1)
    expect(turns[0]!.nativeRecordId).toBeUndefined()
    const texts = turns[0]!.parts.map((p) => p.text)
    expect(texts).toEqual(expect.arrayContaining(['windowed question', 'windowed answer']))
    expect(texts.join('\n')).not.toContain('untimestamped')
  })

  // loadClaudeCalls keeps the first usage-line timestamp when collapsing a
  // contiguous request/response pair. timestampOfGroup must use that same
  // instant; using the last line drops (or keeps) the turn when a window
  // boundary falls inside the ≤60s merge gap on an unpaired group.
  it('keeps an unpaired merged turn when only the first usage timestamp is in the window (#276)', async () => {
    const usage = {
      input_tokens: 150,
      output_tokens: 45,
      cache_read_input_tokens: 300,
      cache_creation_input_tokens: 200,
    }
    const path = writeTranscript([
      {
        type: 'user',
        sessionId: 'session-straddle',
        message: { role: 'user', content: [{ type: 'text', text: 'straddle question' }] },
      },
      {
        type: 'assistant',
        sessionId: 'session-straddle',
        uuid: 'req-straddle',
        timestamp: '2026-09-10T12:00:25.000Z',
        message: {
          // no message.id — unpaired; window slice cannot rescue via nativeRecordId
          model: 'claude-sonnet-4-5',
          usage,
          content: [
            { type: 'tool_use', id: 'tu_1', name: 'Bash', input: { command: 'pwd' } },
          ],
        },
      },
      {
        type: 'user',
        sessionId: 'session-straddle',
        message: {
          role: 'user',
          content: [{ type: 'tool_result', tool_use_id: 'tu_1', content: '/tmp' }],
        },
      },
      {
        type: 'assistant',
        sessionId: 'session-straddle',
        uuid: 'res-straddle',
        // 20s later — still merges (≤60s), but past the window end
        timestamp: '2026-09-10T12:00:45.000Z',
        message: {
          model: 'claude-sonnet-4-5',
          usage,
          content: [{ type: 'text', text: 'straddle answer' }],
        },
      },
    ])

    const dateRange = {
      start: new Date('2026-09-10T12:00:00.000Z'),
      end: new Date('2026-09-10T12:00:30.000Z'),
    }
    const turns: ReaderTurn[] = []
    for await (const turn of claudeReader.read(path, dateRange)) turns.push(turn)

    expect(turns).toHaveLength(1)
    expect(turns[0]!.nativeRecordId).toBeUndefined()
    const texts = turns[0]!.parts.map((p) => p.text)
    expect(texts).toEqual(expect.arrayContaining(['straddle question', 'straddle answer']))
  })

  it('leaves nativeRecordId unset when message.id is absent so positional pairing remains (#216 T2)', async () => {
    const path = writeTranscript([
      {
        type: 'assistant',
        sessionId: 'session-uuid-only',
        uuid: 'uuid-only-asst',
        timestamp: '2026-09-01T12:00:00.000Z',
        message: {
          model: 'claude-sonnet-4-5',
          usage: {
            input_tokens: 10,
            output_tokens: 5,
            cache_read_input_tokens: 0,
            cache_creation_input_tokens: 0,
          },
          content: [{ type: 'text', text: 'uuid-keyed answer' }],
        },
      },
    ])

    const turns = await readTurns(path)
    expect(turns).toHaveLength(1)
    // uuid alone is not a pairing key: loadClaudeCalls only stamps turnId from
    // message.id, and an orphan nativeRecordId would block positional pairing.
    expect(turns[0]!.nativeRecordId).toBeUndefined()
  })

  it('keeps turns separate when adjacent assistant lines have differing native message IDs', async () => {
    const usage = {
      input_tokens: 150,
      output_tokens: 45,
      cache_read_input_tokens: 300,
      cache_creation_input_tokens: 200,
    }
    const path = writeTranscript([
      {
        type: 'assistant',
        sessionId: 'session-diff-ids',
        uuid: 'req-1',
        timestamp: '2026-09-01T12:00:00.000Z',
        message: {
          id: 'msg-1',
          model: 'claude-sonnet-4-5',
          usage,
          content: [{ type: 'text', text: 'First response' }],
        },
      },
      {
        type: 'assistant',
        sessionId: 'session-diff-ids',
        uuid: 'res-1',
        timestamp: '2026-09-01T12:00:05.000Z',
        message: {
          id: 'msg-2',
          model: 'claude-sonnet-4-5',
          usage,
          content: [{ type: 'text', text: 'Second response' }],
        },
      },
    ])

    const turns = await readTurns(path)
    expect(turns).toHaveLength(2)
  })

  // loadClaudeCalls refuses to collapse when defined message.ids conflict
  // (A, absent, B). splitClaudeTurns must do the same: merging on matching
  // usage alone indexes the fused turn under B and lets matchingTurns attach
  // A's content to call B while leaving call A unmatched.
  it('keeps turns separate when defined message.ids conflict across an id-less middle record (#276)', async () => {
    const usage = {
      input_tokens: 150,
      output_tokens: 45,
      cache_read_input_tokens: 300,
      cache_creation_input_tokens: 200,
    }
    const path = writeTranscript([
      {
        type: 'user',
        sessionId: 'session-id-conflict',
        message: { role: 'user', content: [{ type: 'text', text: 'prompt for A' }] },
      },
      {
        type: 'assistant',
        sessionId: 'session-id-conflict',
        uuid: 'uuid-a',
        timestamp: '2026-09-01T12:00:00.000Z',
        message: {
          id: 'msg-A',
          model: 'claude-sonnet-4-5',
          usage,
          content: [{ type: 'text', text: 'content of A' }],
        },
      },
      {
        type: 'assistant',
        sessionId: 'session-id-conflict',
        uuid: 'uuid-absent',
        timestamp: '2026-09-01T12:00:10.000Z',
        message: {
          // no message.id — may fuse with a matching neighbor, but must not
          // bridge two conflicting defined ids
          model: 'claude-sonnet-4-5',
          usage,
          content: [{ type: 'text', text: 'content without id' }],
        },
      },
      {
        type: 'assistant',
        sessionId: 'session-id-conflict',
        uuid: 'uuid-b',
        timestamp: '2026-09-01T12:00:20.000Z',
        message: {
          id: 'msg-B',
          model: 'claude-sonnet-4-5',
          usage,
          content: [{ type: 'text', text: 'content of B' }],
        },
      },
    ])

    const turns = await readTurns(path)
    expect(turns.map((t) => t.nativeRecordId)).toEqual(['msg-A', 'msg-B'])
    const turnA = turns.find((t) => t.nativeRecordId === 'msg-A')!
    const turnB = turns.find((t) => t.nativeRecordId === 'msg-B')!
    const textsA = turnA.parts.map((p) => p.text)
    expect(textsA).toEqual(
      expect.arrayContaining(['prompt for A', 'content of A', 'content without id']),
    )
    expect(textsA.join('\n')).not.toContain('content of B')
    expect(turnB.parts.map((p) => p.text).join('\n')).not.toContain('content of A')
    expect(turnB.parts.map((p) => p.text)).toEqual(
      expect.arrayContaining(['content of B']),
    )
  })

  // loadClaudeCalls measures the merge gap from the first usage line of the
  // accumulated call. A last-line walk fuses t=0,+40s,+80s (40≤60 twice)
  // while the parser emits two calls (0→80 > 60s).
  it('splits a three-usage-line chain the same way loadClaudeCalls does (#276)', () => {
    const usage = {
      input_tokens: 150,
      output_tokens: 45,
      cache_read_input_tokens: 300,
      cache_creation_input_tokens: 200,
    }
    const assistant = (timestamp: string, id?: string) => ({
      type: 'assistant',
      sessionId: 'session-three-line',
      uuid: `uuid-${timestamp}`,
      timestamp,
      message: {
        ...(id !== undefined ? { id } : {}),
        model: 'claude-sonnet-4-5',
        usage,
        content: [{ type: 'text', text: `answer at ${timestamp}` }],
      },
    })
    const path = writeTranscript([
      assistant('2026-09-01T12:00:00.000Z', 'msg-first'),
      assistant('2026-09-01T12:00:40.000Z'),
      assistant('2026-09-01T12:01:20.000Z', 'msg-third'),
    ])
    const lines = readFileSync(path, 'utf-8').split(/\r?\n/).filter((line) => line.trim() !== '')
    const calls = loadClaudeCalls(path)
    expect(splitClaudeTurns(lines, basename(path, extname(path)))).toHaveLength(calls.length)
    expect(calls).toHaveLength(2)
  })

  it('does not fuse a model-less middle record the parser would keep separate (#276)', () => {
    const usage = {
      input_tokens: 150,
      output_tokens: 45,
      cache_read_input_tokens: 300,
      cache_creation_input_tokens: 200,
    }
    const path = writeTranscript([
      {
        type: 'assistant',
        sessionId: 'session-model-gap',
        uuid: 'uuid-named',
        timestamp: '2026-09-01T12:00:00.000Z',
        message: {
          id: 'msg-named',
          model: 'claude-sonnet-4-5',
          usage,
          content: [{ type: 'text', text: 'named model' }],
        },
      },
      {
        type: 'assistant',
        sessionId: 'session-model-gap',
        uuid: 'uuid-unknown',
        timestamp: '2026-09-01T12:00:10.000Z',
        message: {
          usage,
          content: [{ type: 'text', text: 'no model' }],
        },
      },
    ])
    const lines = readFileSync(path, 'utf-8').split(/\r?\n/).filter((line) => line.trim() !== '')
    const calls = loadClaudeCalls(path)
    expect(splitClaudeTurns(lines, basename(path, extname(path)))).toHaveLength(calls.length)
    expect(calls).toHaveLength(2)
  })

  // loadClaudeCalls substitutes the file stem for a missing sessionId and
  // compares strictly; the reader used to tolerate undefined and fuse when
  // message.id matches across the pair.
  it('splits mixed sessionId presence the same way loadClaudeCalls does (#276)', () => {
    const usage = {
      input_tokens: 150,
      output_tokens: 45,
      cache_read_input_tokens: 300,
      cache_creation_input_tokens: 200,
    }
    const dir = mkdtempSync(join(tmpdir(), 'kyber-claude-sessionid-276-'))
    tempRoots.push(dir)
    const path = join(dir, 'file-stem-session.jsonl')
    writeFileSync(
      path,
      [
        JSON.stringify({
          type: 'assistant',
          sessionId: 'real-session',
          uuid: 'uuid-real',
          timestamp: '2026-09-01T12:00:00.000Z',
          message: {
            id: 'msg-shared',
            model: 'claude-sonnet-4-5',
            usage,
            content: [{ type: 'text', text: 'with sessionId' }],
          },
        }),
        JSON.stringify({
          type: 'assistant',
          uuid: 'uuid-stem',
          timestamp: '2026-09-01T12:00:10.000Z',
          message: {
            id: 'msg-shared',
            model: 'claude-sonnet-4-5',
            usage,
            content: [{ type: 'text', text: 'sessionId omitted' }],
          },
        }),
      ].join('\n') + '\n',
    )
    const lines = readFileSync(path, 'utf-8').split(/\r?\n/).filter((line) => line.trim() !== '')
    const calls = loadClaudeCalls(path)
    expect(splitClaudeTurns(lines, basename(path, extname(path)))).toHaveLength(calls.length)
    expect(calls).toHaveLength(2)
    expect(calls.map((c) => c.sessionId)).toEqual(['real-session', 'file-stem-session'])
  })

  it('measures stored conversation and tool results, but not unavailable system prompts or tool definitions', async () => {
    const path = writeTranscript([
      {
        type: 'user',
        sessionId: 'synthetic-claude-session',
        message: {
          role: 'user',
          content: [
            { type: 'text', text: 'Read the synthetic result.' },
            { type: 'tool_result', tool_use_id: 'synthetic-call', content: 'Synthetic tool result.' },
          ],
        },
      },
      {
        type: 'assistant',
        sessionId: 'synthetic-claude-session',
        message: {
          role: 'assistant',
          content: [{ type: 'tool_use', id: 'synthetic-definition', name: 'synthetic_tool', input: {} }],
        },
      },
    ])

    const [turn] = await readTurns(path)
    const buckets = turn?.parts.map(part => part.part) ?? []

    expect(buckets).toContain('conversation_history')
    expect(buckets).toContain('tool_result_content')
    expect(buckets).not.toContain('system_prompt')
    expect(buckets).not.toContain('tool_definitions')
  })

  it('extracts tool invocations with name and arguments and pairs matching tool results', async () => {
    const path = writeTranscript([
      {
        type: 'assistant',
        sessionId: 'claude-session-with-tools',
        message: {
          role: 'assistant',
          content: [
            {
              type: 'tool_use',
              id: 'tu_1',
              name: 'Bash',
              input: { command: 'npm test' },
            },
          ],
        },
      },
      {
        type: 'user',
        sessionId: 'claude-session-with-tools',
        message: {
          role: 'user',
          content: [
            {
              type: 'tool_result',
              tool_use_id: 'tu_1',
              content: 'PASS',
              is_error: false,
            },
          ],
        },
      },
    ])

    const [turn] = await readTurns(path)

    expect(turn).toBeDefined()
    expect(turn?.toolCalls).toEqual([
      expect.objectContaining({
        id: 'tu_1',
        name: 'Bash',
        arguments: { command: 'npm test' },
      }),
    ])
    expect(turn?.toolResults).toEqual([
      expect.objectContaining({
        toolCallId: 'tu_1',
        content: 'PASS',
        isError: false,
      }),
    ])
    expect(turn?.toolsOffered).toBeUndefined()
  })

  it('records isError true on ReaderToolResult when tool result indicates error', async () => {
    const path = writeTranscript([
      {
        type: 'assistant',
        sessionId: 'claude-session-tool-error',
        message: {
          role: 'assistant',
          content: [
            {
              type: 'tool_use',
              id: 'tu_err',
              name: 'Bash',
              input: { command: 'npm test --fail' },
            },
          ],
        },
      },
      {
        type: 'user',
        sessionId: 'claude-session-tool-error',
        message: {
          role: 'user',
          content: [
            {
              type: 'tool_result',
              tool_use_id: 'tu_err',
              content: 'FAIL: 1 test failed',
              is_error: true,
            },
          ],
        },
      },
    ])

    const [turn] = await readTurns(path)

    expect(turn).toBeDefined()
    expect(turn?.toolCalls).toEqual([
      expect.objectContaining({
        id: 'tu_err',
        name: 'Bash',
        arguments: { command: 'npm test --fail' },
      }),
    ])
    expect(turn?.toolResults).toEqual([
      expect.objectContaining({
        toolCallId: 'tu_err',
        content: 'FAIL: 1 test failed',
        isError: true,
      }),
    ])
  })
  it("records isError true on ReaderToolResult when exit_code is non-zero", async () => {
    const path = writeTranscript([
      {
        type: "assistant",
        sessionId: "claude-session-exit-code",
        message: {
          role: "assistant",
          content: [
            {
              type: "tool_use",
              id: "tu_ec_1",
              name: "Bash",
              input: { command: "exit 1" },
            },
          ],
        },
      },
      {
        type: "user",
        sessionId: "claude-session-exit-code",
        message: {
          role: "user",
          content: [
            {
              type: "tool_result",
              tool_use_id: "tu_ec_1",
              content: "Command exited with status 1",
              exit_code: 1,
            },
          ],
        },
      },
    ])

    const [turn] = await readTurns(path)
    expect(turn).toBeDefined()
    expect(turn?.toolResults).toEqual([
      expect.objectContaining({
        toolCallId: "tu_ec_1",
        content: "Command exited with status 1",
        isError: true,
      }),
    ])
  })

  it("records isError true on ReaderToolResult when exitCode is non-zero", async () => {
    const path = writeTranscript([
      {
        type: "assistant",
        sessionId: "claude-session-exit-code-camel",
        message: {
          role: "assistant",
          content: [
            {
              type: "tool_use",
              id: "tu_ec_2",
              name: "Bash",
              input: { command: "node script.js" },
            },
          ],
        },
      },
      {
        type: "user",
        sessionId: "claude-session-exit-code-camel",
        message: {
          role: "user",
          content: [
            {
              type: "tool_result",
              tool_use_id: "tu_ec_2",
              content: "Uncaught Error",
              exitCode: 1,
            },
          ],
        },
      },
    ])

    const [turn] = await readTurns(path)
    expect(turn).toBeDefined()
    expect(turn?.toolResults).toEqual([
      expect.objectContaining({
        toolCallId: "tu_ec_2",
        content: "Uncaught Error",
        isError: true,
      }),
    ])
  })

  it('does not infer or default toolsOffered from static profiles (honest unobservability)', async () => {
    const path = writeTranscript([
      {
        type: 'assistant',
        sessionId: 'claude-session-unobserved-tools',
        message: {
          role: 'assistant',
          content: [
            {
              type: 'tool_use',
              id: 'tu_unobs',
              name: 'Read',
              input: { path: 'file.txt' },
            },
          ],
        },
      },
    ])

    const [turn] = await readTurns(path)

    expect(turn).toBeDefined()
    expect(turn?.toolsOffered).toBeUndefined()
  })

  it('extracts toolCalls and toolResults when calling readClaudeSession directly', () => {
    const rawLines = [
      JSON.stringify({
        type: 'assistant',
        sessionId: 'claude-direct-session',
        message: {
          role: 'assistant',
          content: [
            {
              type: 'tool_use',
              id: 'tu_1',
              name: 'Bash',
              input: { command: 'npm test' },
            },
          ],
        },
      }),
      JSON.stringify({
        type: 'user',
        sessionId: 'claude-direct-session',
        message: {
          role: 'user',
          content: [
            {
              type: 'tool_result',
              tool_use_id: 'tu_1',
              content: 'PASS',
              is_error: false,
            },
          ],
        },
      }),
    ]

    const session = readClaudeSession(rawLines) as unknown as ReaderTurn

    expect(session.toolCalls).toEqual([
      expect.objectContaining({
        id: 'tu_1',
        name: 'Bash',
        arguments: { command: 'npm test' },
      }),
    ])
    expect(session.toolResults).toEqual([
      expect.objectContaining({
        toolCallId: 'tu_1',
        content: 'PASS',
      }),
    ])
    expect(session.toolsOffered).toBeUndefined()
  })

  it('guards non-object and non-string input in tool_use blocks, defaulting to empty object (Thread 13)', () => {
    const rawLines = [
      JSON.stringify({
        type: 'assistant',
        sessionId: 'claude-guard-input',
        message: {
          role: 'assistant',
          content: [
            { type: 'tool_use', id: 'tu_num', name: 'ToolNum', input: 42 },
            { type: 'tool_use', id: 'tu_arr', name: 'ToolArr', input: ['invalid', 'array'] },
            { type: 'tool_use', id: 'tu_bool', name: 'ToolBool', input: true },
            { type: 'tool_use', id: 'tu_str', name: 'ToolStr', input: 'valid-string' },
            { type: 'tool_use', id: 'tu_obj', name: 'ToolObj', input: { key: 'val' } },
          ],
        },
      }),
    ]

    const session = readClaudeSession(rawLines) as unknown as ReaderTurn
    expect(session.toolCalls).toHaveLength(5)
    expect(session.toolCalls?.[0]?.arguments).toEqual({})
    expect(session.toolCalls?.[1]?.arguments).toEqual({})
    expect(session.toolCalls?.[2]?.arguments).toEqual({})
    expect(session.toolCalls?.[3]?.arguments).toBe('valid-string')
    expect(session.toolCalls?.[4]?.arguments).toEqual({ key: 'val' })
  })
})
