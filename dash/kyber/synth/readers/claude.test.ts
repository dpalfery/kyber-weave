import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterAll, describe, expect, it } from 'vitest'

import { claudeReader } from './claude.js'
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
})
