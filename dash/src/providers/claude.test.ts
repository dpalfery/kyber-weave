import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { claude, loadClaudeCalls } from './claude.js'
import type { ParsedProviderCall } from './types.js'

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

  it('recurses into subagent directories when parsing sessions (Thread 11)', async () => {
    const subagentsDir = join(tmpDir, 'subagents')
    mkdirSync(subagentsDir)

    const mainTranscript = join(tmpDir, 'main.jsonl')
    const subTranscript = join(subagentsDir, 'subagent.jsonl')

    const line = (sessionId: string, uuid: string) =>
      JSON.stringify({
        type: 'assistant',
        sessionId,
        uuid,
        timestamp: '2026-09-01T12:00:00.000Z',
        message: {
          model: 'claude-sonnet-4-5',
          usage: { input_tokens: 100, output_tokens: 20 },
          content: [{ type: 'text', text: 'ok' }],
        },
      })

    writeFileSync(mainTranscript, line('main-session', 'u-main') + '\n')
    writeFileSync(subTranscript, line('sub-session', 'u-sub') + '\n')

    const seenKeys = new Set<string>()
    const parser = claude.createSessionParser!({ path: tmpDir, provider: 'claude', project: 'test-project' }, seenKeys)
    const calls: ParsedProviderCall[] = []
    for await (const call of parser.parse()) {
      calls.push(call)
    }

    expect(calls).toHaveLength(2)
    const sessionIds = calls.map((c) => c.sessionId).sort()
    expect(sessionIds).toEqual(['main-session', 'sub-session'])
  })

  it('does not poison seenKeys when a call is outside dateRange window (Thread 12)', async () => {
    // Two files: file1 has duplicate key outside date range, file2 has same key inside date range
    const file1 = join(tmpDir, 'file1.jsonl')
    const file2 = join(tmpDir, 'file2.jsonl')

    const makeLine = (ts: string) =>
      JSON.stringify({
        type: 'assistant',
        sessionId: 'shared-session',
        uuid: 'turn-shared',
        timestamp: ts,
        message: {
          model: 'claude-sonnet-4-5',
          usage: { input_tokens: 100, output_tokens: 20 },
          content: [{ type: 'text', text: 'msg' }],
        },
      })

    // Out of range (August)
    writeFileSync(file1, makeLine('2026-08-01T10:00:00.000Z') + '\n')
    // In range (September)
    writeFileSync(file2, makeLine('2026-09-10T10:00:00.000Z') + '\n')

    const seenKeys = new Set<string>()
    const parser = claude.createSessionParser!(
      { path: tmpDir, provider: 'claude', project: 'test-project' },
      seenKeys,
      { start: new Date('2026-09-01T00:00:00.000Z'), end: new Date('2026-09-30T00:00:00.000Z') },
    )
    const calls: ParsedProviderCall[] = []
    for await (const call of parser.parse()) {
      calls.push(call)
    }

    // File 2's in-range call must not be suppressed by File 1's out-of-range call
    expect(calls).toHaveLength(1)
    expect(calls[0]?.timestamp).toBe('2026-09-10T10:00:00.000Z')
  })
})
