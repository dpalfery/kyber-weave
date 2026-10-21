import { mkdtemp, mkdir, rm, writeFile } from 'fs/promises'
import { tmpdir } from 'os'
import { join } from 'path'
import { describe, expect, it } from 'vitest'

import { attachTranscriptActivity, parseTranscript, skillNameFromPath } from './antigravity-transcript.js'
import type { ParsedProviderCall } from './types.js'

const q = (v: string) => JSON.stringify(v)

function step(at: string, calls: Array<{ name: string; args: Record<string, string> }>): string {
  return JSON.stringify({
    step_index: 1,
    source: 'MODEL',
    type: 'PLANNER_RESPONSE',
    status: 'DONE',
    created_at: at,
    tool_calls: calls.map((c) => ({ name: c.name, args: Object.fromEntries(Object.entries(c.args).map(([k, v]) => [k, q(v)])) })),
  })
}

function call(id: string, timestamp: string, sessionId = 'conv-1'): ParsedProviderCall {
  return {
    provider: 'antigravity', model: 'm', inputTokens: 1, outputTokens: 1, cacheCreationInputTokens: 0,
    cacheReadInputTokens: 0, cachedInputTokens: 0, reasoningTokens: 0, webSearchRequests: 0, costUSD: 0,
    tools: [], bashCommands: [], timestamp, speed: 'standard', deduplicationKey: id, userMessage: '', sessionId,
  }
}

const TRANSCRIPT = [
  JSON.stringify({ step_index: 0, type: 'USER_INPUT', created_at: '2026-01-01T00:00:00Z', content: 'hi' }),
  step('2026-01-01T00:00:10Z', [
    { name: 'run_command', args: { CommandLine: 'git status', Cwd: '/w' } },
    { name: 'view_file', args: { AbsolutePath: '/home/u/.gemini/config/skills/test-dev/SKILL.md' } },
  ]),
  JSON.stringify({ step_index: 3, type: 'GENERIC', created_at: '2026-01-01T00:00:11Z', content: 'ok' }),
  step('2026-01-01T00:01:00Z', [{ name: 'view_file', args: { AbsolutePath: '/w/src/a.ts' } }]),
  'not json',
].join('\n')

describe('parseTranscript', () => {
  it('reads tool calls and infers skills from SKILL.md reads only', () => {
    const steps = parseTranscript(TRANSCRIPT)
    expect(steps).toHaveLength(2)
    expect(steps[0]!.tools).toEqual(['run_command', 'view_file', 'Skill'])
    expect(steps[0]!.skills).toEqual(['test-dev'])
    expect(steps[0]!.toolSequence[0]).toEqual({ tool: 'run_command', command: 'git status' })
    expect(steps[1]!.skills).toEqual([])
  })

  it('names a skill by its directory', () => {
    expect(skillNameFromPath('C:\\x\\skills\\docs\\SKILL.md')).toBe('docs')
    expect(skillNameFromPath('/x/skills/docs/README.md')).toBeUndefined()
  })
})

describe('attachTranscriptActivity', () => {
  async function withTranscript(run: (home: string) => Promise<void>, text = TRANSCRIPT): Promise<void> {
    const home = await mkdtemp(join(tmpdir(), 'agy-transcript-'))
    try {
      const dir = join(home, '.gemini', 'antigravity-cli', 'brain', 'conv-1', '.system_generated', 'logs')
      await mkdir(dir, { recursive: true })
      await writeFile(join(dir, 'transcript.jsonl'), text)
      await run(home)
    } finally {
      await rm(home, { recursive: true, force: true })
    }
  }

  it('assigns steps to the nearest call and sets skills', async () => {
    await withTranscript(async (home) => {
      const calls = [call('a', '2026-01-01T00:00:09Z'), call('b', '2026-01-01T00:01:01Z')]
      await attachTranscriptActivity(calls, { mode: 'nearest', home })
      expect(calls[0]!.tools).toEqual(['run_command', 'view_file', 'Skill'])
      expect(calls[0]!.skills).toEqual(['test-dev'])
      expect(calls[1]!.tools).toEqual(['view_file'])
      expect(calls[1]!.skills).toBeUndefined()
    })
  })

  it('assigns steps to the following snapshot in statusline mode, last takes the tail', async () => {
    await withTranscript(async (home) => {
      const calls = [call('a', '2026-01-01T00:00:30Z'), call('b', '2026-01-01T00:00:40Z')]
      await attachTranscriptActivity(calls, { mode: 'following', home })
      expect(calls[0]!.tools).toEqual(['run_command', 'view_file', 'Skill'])
      expect(calls[1]!.tools).toEqual(['view_file'])
    })
  })

  it('leaves calls untouched when the transcript is missing or the id is unsafe', async () => {
    const home = await mkdtemp(join(tmpdir(), 'agy-transcript-'))
    try {
      const calls = [call('a', '2026-01-01T00:00:09Z'), call('b', '2026-01-01T00:00:09Z', '../evil')]
      await attachTranscriptActivity(calls, { mode: 'nearest', home })
      expect(calls.every((c) => c.tools.length === 0 && c.skills === undefined)).toBe(true)
    } finally {
      await rm(home, { recursive: true, force: true })
    }
  })
})
