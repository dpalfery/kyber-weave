import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { loadClaudeCalls } from './claude.js'

describe('claude provider - tool extraction in loadClaudeCalls', () => {
  let tmpDir: string

  beforeEach(() => {
    tmpDir = mkdtempSync(join(tmpdir(), 'claude-provider-test-'))
  })

  afterEach(() => {
    rmSync(tmpDir, { recursive: true, force: true })
  })

  it('extracts tools, bashCommands, and toolSequence from assistant messages with tool_use blocks', () => {
    const transcriptPath = join(tmpDir, 'session.jsonl')
    const lines = [
      JSON.stringify({
        type: 'assistant',
        sessionId: 'session-tools-1',
        uuid: 'turn-1',
        timestamp: '2026-09-01T12:00:00.000Z',
        message: {
          model: 'claude-sonnet-4-5',
          usage: { input_tokens: 150, output_tokens: 45 },
          content: [
            {
              type: 'tool_use',
              id: 'tu_bash_1',
              name: 'Bash',
              input: { command: 'git status && npm test' },
            },
            {
              type: 'tool_use',
              id: 'tu_read_1',
              name: 'Read',
              input: { file_path: 'src/index.ts' },
            },
          ],
        },
      }),
    ]
    writeFileSync(transcriptPath, lines.join('\n') + '\n', 'utf8')

    const calls = loadClaudeCalls(transcriptPath)
    expect(calls).toHaveLength(1)
    const call = calls[0]!

    expect(call.tools).toEqual(['Bash', 'Read'])
    expect(call.bashCommands).toEqual(['git', 'npm'])
    expect(call.toolSequence).toEqual([
      [
        { tool: 'Bash', command: 'git status && npm test' },
        { tool: 'Read', file: 'src/index.ts' },
      ],
    ])
  })
})
