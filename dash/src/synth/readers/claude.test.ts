import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterAll, describe, expect, it } from 'vitest'

import { claudeReader, readClaudeSession } from './claude.js'
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
})
