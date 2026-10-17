import { describe, it, expect, beforeEach, afterEach } from 'vitest'
import { mkdtemp, mkdir, writeFile, rm } from 'fs/promises'
import { join } from 'path'
import { tmpdir } from 'os'

import { createCommandCodeProvider } from './commandcode.js'
import type { ParsedProviderCall } from './types.js'

let tmpDir: string

beforeEach(async () => {
  tmpDir = await mkdtemp(join(tmpdir(), 'commandcode-test-'))
})

afterEach(async () => {
  await rm(tmpDir, { recursive: true, force: true })
})

const header = JSON.stringify({
  type: 'session',
  version: 3,
  id: 'sess-001',
  timestamp: '2026-10-17T10:00:00.000Z',
  cwd: '/Users/test/myproject',
})

function userEntry(text: string) {
  return JSON.stringify({
    type: 'message',
    id: 'u1',
    parentId: null,
    timestamp: '2026-10-17T10:00:01.000Z',
    message: { role: 'user', content: [{ type: 'text', text }] },
  })
}

function assistantEntry(opts: { id: string; usage?: Record<string, number>; content?: unknown[] }) {
  return JSON.stringify({
    type: 'message',
    id: opts.id,
    parentId: 'u1',
    timestamp: '2026-10-17T10:00:02.000Z',
    message: { role: 'assistant', content: opts.content ?? [{ type: 'text', text: 'done' }] },
    ...(opts.usage ? { usage: opts.usage } : {}),
    model: 'moonshotai/Kimi-K2.5',
  })
}

async function writeSession(lines: string[], fileName = 'sess-001.jsonl'): Promise<string> {
  const dir = join(tmpDir, 'users-test-myproject')
  await mkdir(dir, { recursive: true })
  const path = join(dir, fileName)
  await writeFile(path, lines.join('\n') + '\n')
  return path
}

async function parseAll(): Promise<ParsedProviderCall[]> {
  const provider = createCommandCodeProvider(tmpDir)
  const calls: ParsedProviderCall[] = []
  for (const source of await provider.discoverSessions()) {
    for await (const call of provider.createSessionParser(source, new Set()).parse()) calls.push(call)
  }
  return calls
}

describe('commandcode provider', () => {
  it('discovers transcripts and skips sidecar files', async () => {
    await writeSession([header])
    await writeSession([header], 'sess-001.checkpoints.jsonl')
    await writeSession([header], 'sess-001.prompts.jsonl')

    const sources = await createCommandCodeProvider(tmpDir).discoverSessions()

    expect(sources).toHaveLength(1)
    expect(sources[0]).toMatchObject({ project: 'myproject', provider: 'commandcode' })
  })

  it('subtracts cache classes from the inclusive input counter and keeps reported cost', async () => {
    await writeSession([
      header,
      userEntry('fix the bug'),
      assistantEntry({
        id: 'a1',
        usage: { inputTokens: 1000, outputTokens: 200, cacheReadTokens: 600, cacheWriteTokens: 100, costUsd: 0.012 },
      }),
    ])

    const [call] = await parseAll()

    expect(call).toMatchObject({
      provider: 'commandcode',
      model: 'moonshotai/Kimi-K2.5',
      inputTokens: 300,
      outputTokens: 200,
      cacheReadInputTokens: 600,
      cacheCreationInputTokens: 100,
      costUSD: 0.012,
      userMessage: 'fix the bug',
      sessionId: 'sess-001',
    })
  })

  it('maps tools, skills, subagents and shell commands', async () => {
    await writeSession([
      header,
      assistantEntry({
        id: 'a1',
        usage: { inputTokens: 10, outputTokens: 5, cacheReadTokens: 0, cacheWriteTokens: 0 },
        content: [
          { type: 'tool_use', id: 't1', name: 'read_file', input: { path: 'a.ts' } },
          { type: 'tool_use', id: 't2', name: 'activate_skill', input: { name: 'code-review' } },
          { type: 'tool_use', id: 't3', name: 'agent', input: { subagent_type: 'architect', prompt: 'plan' } },
          { type: 'tool_use', id: 't4', name: 'shell_command', input: { command: 'npm test' } },
        ],
      }),
    ])

    const [call] = await parseAll()

    expect(call?.tools).toEqual(['Read', 'Skill', 'Agent', 'Bash'])
    expect(call?.skills).toEqual(['code-review'])
    expect(call?.subagentTypes).toEqual(['architect'])
    expect(call?.bashCommands).toContain('npm')
  })

  it('ignores assistant entries without usage', async () => {
    await writeSession([header, assistantEntry({ id: 'a1' })])

    expect(await parseAll()).toEqual([])
  })
})
