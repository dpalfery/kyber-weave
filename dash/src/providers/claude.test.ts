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

  it('groups contiguous paired request/response assistant usage blocks into 1 call (#232)', () => {
    const transcriptPath = join(tmpDir, 'paired-session.jsonl')
    const usage = {
      input_tokens: 150,
      output_tokens: 45,
      cache_read_input_tokens: 300,
      cache_creation_input_tokens: 200,
    }
    const lines = [
      JSON.stringify({
        type: 'assistant',
        sessionId: 'session-paired-1',
        uuid: 'turn-1-req',
        timestamp: '2026-09-01T12:00:00.000Z',
        message: {
          id: 'msg-1',
          model: 'claude-sonnet-4-5',
          usage,
          content: [
            {
              type: 'tool_use',
              id: 'tu_bash_1',
              name: 'Bash',
              input: { command: 'git status' },
            },
          ],
        },
      }),
      JSON.stringify({
        type: 'user',
        sessionId: 'session-paired-1',
        message: {
          content: [
            {
              type: 'tool_result',
              tool_use_id: 'tu_bash_1',
              content: 'On branch main',
            },
          ],
        },
      }),
      JSON.stringify({
        type: 'assistant',
        sessionId: 'session-paired-1',
        uuid: 'turn-1-res',
        timestamp: '2026-09-01T12:00:05.000Z',
        message: {
          id: 'msg-1',
          model: 'claude-sonnet-4-5',
          usage,
          content: [
            {
              type: 'text',
              text: 'You are on branch main.',
            },
          ],
        },
      }),
    ]
    writeFileSync(transcriptPath, lines.join('\n') + '\n', 'utf8')

    const calls = loadClaudeCalls(transcriptPath)
    expect(calls).toHaveLength(1)
    const call = calls[0]!
    expect(call.tools).toEqual(['Bash'])
    expect(call.bashCommands).toEqual(['git'])
    expect(call.inputTokens).toBe(150)
    expect(call.outputTokens).toBe(45)
    expect(call.cacheReadInputTokens).toBe(300)
    expect(call.cacheCreationInputTokens).toBe(200)
  })

  it('keeps calls separate when adjacent assistant lines have differing native message IDs', () => {
    const transcriptPath = join(tmpDir, 'different-msg-ids.jsonl')
    const usage = {
      input_tokens: 150,
      output_tokens: 45,
      cache_read_input_tokens: 300,
      cache_creation_input_tokens: 200,
    }
    const lines = [
      JSON.stringify({
        type: 'assistant',
        sessionId: 'session-diff-ids',
        uuid: 'turn-1',
        timestamp: '2026-09-01T12:00:00.000Z',
        message: {
          id: 'msg-first',
          model: 'claude-sonnet-4-5',
          usage,
        },
      }),
      JSON.stringify({
        type: 'assistant',
        sessionId: 'session-diff-ids',
        uuid: 'turn-2',
        timestamp: '2026-09-01T12:00:05.000Z',
        message: {
          id: 'msg-second',
          model: 'claude-sonnet-4-5',
          usage,
        },
      }),
    ]
    writeFileSync(transcriptPath, lines.join('\n') + '\n', 'utf8')

    const calls = loadClaudeCalls(transcriptPath)
    expect(calls).toHaveLength(2)
    expect(calls[0]!.deduplicationKey).toBe('claude:session-diff-ids:turn-1')
    expect(calls[1]!.deduplicationKey).toBe('claude:session-diff-ids:turn-2')
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
