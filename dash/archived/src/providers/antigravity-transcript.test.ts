import { mkdtemp, mkdir, readFile, rm, truncate, writeFile } from 'fs/promises'
import { tmpdir } from 'os'
import { join } from 'path'
import { describe, expect, it } from 'vitest'

import { MAX_SESSION_FILE_BYTES } from '../ingest/fs-utils.js'
import { attachTranscriptActivity, parseTranscript, skillNameFromPath } from './antigravity-transcript.js'
import type { ParsedProviderCall } from './types.js'

// Synthetic transcript content only: tool names, paths and arguments are invented.

const q = (v: string) => JSON.stringify(v)

/** Every args value is JSON-encoded twice; the reader must decode both layers. */
const enc2 = (v: unknown) => JSON.stringify(JSON.stringify(v))

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

function planner(stepIndex: number, calls: Array<{ name: string; args: Record<string, unknown> }>): string {
  return JSON.stringify({
    step_index: stepIndex,
    source: 'MODEL',
    type: 'PLANNER_RESPONSE',
    status: 'DONE',
    created_at: '2026-01-01T00:00:00Z',
    tool_calls: calls.map((c) => ({ name: c.name, args: Object.fromEntries(Object.entries(c.args).map(([k, v]) => [k, enc2(v)])) })),
  })
}

function segmentCall(lsi: number | undefined, sessionId = 'conv-1'): ParsedProviderCall {
  return {
    provider: 'antigravity', model: 'm', inputTokens: 1, outputTokens: 1, cacheCreationInputTokens: 0,
    cacheReadInputTokens: 0, cachedInputTokens: 0, reasoningTokens: 0, webSearchRequests: 0, costUSD: 0,
    tools: [], bashCommands: [], timestamp: '2026-01-01T00:00:00Z', speed: 'standard',
    deduplicationKey: `antigravity:${sessionId}:${lsi ?? 'none'}`,
    ...(lsi === undefined ? {} : { pairingId: `agy-step:${lsi}` }),
    userMessage: '', sessionId,
  }
}

const FIXTURE_TRANSCRIPT = new URL(
  '../../tests/fixtures/antigravity-transcript/brain/fixture-segments/.system_generated/logs/transcript.jsonl',
  import.meta.url,
)

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

describe('attachTranscriptActivity (agy-step segments)', () => {
  /** Writes a transcript under <home>/.gemini/antigravity-cli/brain/<conversation>/... */
  async function withTranscript(
    run: (home: string) => Promise<void>,
    text: string | URL,
    conversationId = 'conv-1',
  ): Promise<void> {
    const home = await mkdtemp(join(tmpdir(), 'agy-transcript-'))
    try {
      const dir = join(home, '.gemini', 'antigravity-cli', 'brain', conversationId, '.system_generated', 'logs')
      await mkdir(dir, { recursive: true })
      const body = text instanceof URL ? await readFile(text, 'utf-8') : text
      await writeFile(join(dir, 'transcript.jsonl'), body)
      await run(home)
    } finally {
      await rm(home, { recursive: true, force: true })
    }
  }

  it('gives call N the steps of its own segment (lsi[N], lsi[N+1]], never its predecessor\'s', async () => {
    await withTranscript(async (home) => {
      const calls = [segmentCall(2, 'fixture-segments'), segmentCall(5, 'fixture-segments'), segmentCall(8, 'fixture-segments')]
      await attachTranscriptActivity(calls, { home })

      expect(calls[0]!.tools).toEqual(['run_command'])
      expect(calls[1]!.tools).toEqual(['view_file'])
      expect(calls[2]!.tools).toEqual(['grep_search'])
      // Every segment is covered by the transcript and none reads a SKILL.md: measured none.
      for (const call of calls) expect(call.skills).toEqual([])
    }, FIXTURE_TRANSCRIPT, 'fixture-segments')
  })

  it('decodes each argument value twice into the plain value on the tool sequence', async () => {
    await withTranscript(async (home) => {
      const calls = [segmentCall(2, 'fixture-segments'), segmentCall(5, 'fixture-segments'), segmentCall(8, 'fixture-segments')]
      await attachTranscriptActivity(calls, { home })

      expect(calls[0]!.toolSequence).toEqual([[{ tool: 'run_command', command: 'synthetic-command --dry-run' }]])
      expect(calls[1]!.toolSequence).toEqual([[{ tool: 'view_file', file: '/synthetic/workspace/src/sample.ts' }]])
    }, FIXTURE_TRANSCRIPT, 'fixture-segments')
  })

  it('attributes a step at exactly lsi[N+1] to call N, not to call N+1', async () => {
    const text = [
      planner(3, [{ name: 'alpha_tool', args: {} }]),
      planner(5, [{ name: 'boundary_tool', args: {} }]),
      planner(6, [{ name: 'beta_tool', args: {} }]),
    ].join('\n')
    await withTranscript(async (home) => {
      const calls = [segmentCall(2), segmentCall(5)]
      await attachTranscriptActivity(calls, { home })

      expect(calls[0]!.tools).toEqual(['alpha_tool', 'boundary_tool'])
      expect(calls[1]!.tools).toEqual(['beta_tool'])
    }, text)
  })

  it('gives no tools to a call whose segment lies before the first transcript step', async () => {
    // The transcript starts at step 6, so the segment (2, 5] has no steps at all.
    const fromStepSix = (await readFile(FIXTURE_TRANSCRIPT, 'utf-8'))
      .split(/\r?\n/)
      .filter((line) => line.trim() !== '')
      .filter((line) => (JSON.parse(line) as { step_index: number }).step_index >= 6)
      .join('\n')
    await withTranscript(async (home) => {
      const calls = [segmentCall(2, 'fixture-segments'), segmentCall(5, 'fixture-segments'), segmentCall(8, 'fixture-segments')]
      await attachTranscriptActivity(calls, { home })

      expect(calls[0]!.tools).toEqual([])
      expect(calls[0]!.skills).toBeUndefined()
      expect(calls[1]!.tools).toEqual(['view_file'])
      expect(calls[2]!.tools).toEqual(['grep_search'])
    }, fromStepSix, 'fixture-segments')
  })

  it('gives an uncovered segment no tools and no skills, even when later steps lie in its open range', async () => {
    // The transcript starts at step 9, after the planner step (6) that is this call's own
    // output. The last call's range is open above, so steps 9 and 10 fall inside it, but
    // the transcript does not cover the call: it must carry nothing from them (a call with
    // tools and no skills claim would read as an invocation that is unknown).
    const fromStepNine = (await readFile(FIXTURE_TRANSCRIPT, 'utf-8'))
      .split(/\r?\n/)
      .filter((line) => line.trim() !== '')
      .filter((line) => (JSON.parse(line) as { step_index: number }).step_index >= 9)
      .join('\n')
    await withTranscript(async (home) => {
      const calls = [segmentCall(5, 'fixture-segments')]
      await attachTranscriptActivity(calls, { home })

      expect(calls[0]!.tools).toEqual([])
      expect(calls[0]!.toolSequence ?? []).toEqual([])
      expect(calls[0]!.skills).toBeUndefined()
    }, fromStepNine, 'fixture-segments')
  })

  it('states no skills for a call before the transcript starts (measured none is not claimed there), and measured none for a covered call', async () => {
    // T2(b), mid-conversation: the transcript starts at step 6. The call at lsi 2 owns
    // (2, 5], which the transcript does not cover, so it carries no skills key at all.
    // The call at lsi 5 owns (5, 8]: step 6 (view_file) only, covered and free of a
    // SKILL.md read: `[]`. The call at lsi 8 is last, so its range is unbounded above
    // and it owns step 9 (grep_search). Three calls keep call 5 bounded; with two it
    // would also own step 9.
    const fromStepSix = (await readFile(FIXTURE_TRANSCRIPT, 'utf-8'))
      .split(/\r?\n/)
      .filter((line) => line.trim() !== '')
      .filter((line) => (JSON.parse(line) as { step_index: number }).step_index >= 6)
      .join('\n')
    await withTranscript(async (home) => {
      const calls = [segmentCall(2, 'fixture-segments'), segmentCall(5, 'fixture-segments'), segmentCall(8, 'fixture-segments')]
      await attachTranscriptActivity(calls, { home })

      expect(calls[0]!.skills).toBeUndefined()
      expect(calls[0]!.tools).toEqual([])
      expect(calls[1]!.skills).toEqual([])
      expect(calls[1]!.tools).toEqual(['view_file'])
      expect(calls[2]!.skills).toEqual([])
      expect(calls[2]!.tools).toEqual(['grep_search'])
    }, fromStepSix, 'fixture-segments')
  })

  it('reads the skill a SKILL.md read names, only within the segment that read it', async () => {
    const text = [
      planner(3, [{ name: 'view_file', args: { AbsolutePath: '/x/skills/test-dev/SKILL.md' } }]),
      planner(6, [{ name: 'view_file', args: { AbsolutePath: '/w/src/a.ts' } }]),
    ].join('\n')
    await withTranscript(async (home) => {
      const calls = [segmentCall(2), segmentCall(5)]
      await attachTranscriptActivity(calls, { home })

      expect(calls[0]!.skills).toEqual(['test-dev'])
      expect(calls[0]!.tools).toEqual(['view_file', 'Skill'])
      // Segment (5, ...] is covered by the transcript and reads no SKILL.md: measured none (D30).
      expect(calls[1]!.skills).toEqual([])
      expect(calls[1]!.tools).toEqual(['view_file'])
    }, text)
  })

  it('skips an unparseable transcript line without throwing and keeps the valid steps', async () => {
    const text = [
      planner(3, [{ name: 'alpha_tool', args: {} }]),
      '{"step_index": 4, "type": "GENERIC", "content": ',
      planner(6, [{ name: 'beta_tool', args: {} }]),
    ].join('\n')
    await withTranscript(async (home) => {
      const calls = [segmentCall(2), segmentCall(5)]
      await expect(attachTranscriptActivity(calls, { home })).resolves.toBeUndefined()

      expect(calls[0]!.tools).toEqual(['alpha_tool'])
      expect(calls[1]!.tools).toEqual(['beta_tool'])
    }, text)
  })

  it('leaves calls without a pairing id untouched, even when the transcript exists', async () => {
    await withTranscript(async (home) => {
      const calls = [segmentCall(undefined, 'fixture-segments')]
      await attachTranscriptActivity(calls, { home })

      expect(calls[0]!.tools).toEqual([])
      expect(calls[0]!.skills).toBeUndefined()
    }, FIXTURE_TRANSCRIPT, 'fixture-segments')
  })

  it('leaves calls untouched when the transcript is missing or the conversation id is unsafe', async () => {
    const home = await mkdtemp(join(tmpdir(), 'agy-transcript-'))
    try {
      const calls = [segmentCall(2, 'conv-1'), segmentCall(5, '../evil')]
      await attachTranscriptActivity(calls, { home })
      expect(calls.every((c) => c.tools.length === 0 && c.skills === undefined)).toBe(true)
    } finally {
      await rm(home, { recursive: true, force: true })
    }
  })
})

describe('attachTranscriptActivity, skills from view_file SKILL.md reads', () => {
  /** Writes one conversation's transcript under a temp home, removed after `run`. */
  async function withSkillTranscript(text: string, run: (home: string) => Promise<void>): Promise<void> {
    const home = await mkdtemp(join(tmpdir(), 'agy-skill-'))
    try {
      const dir = join(home, '.gemini', 'antigravity-cli', 'brain', 'conv-1', '.system_generated', 'logs')
      await mkdir(dir, { recursive: true })
      await writeFile(join(dir, 'transcript.jsonl'), text)
      await run(home)
    } finally {
      await rm(home, { recursive: true, force: true })
    }
  }

  it('names the skill a double-encoded SKILL.md read points at, on the call that owns the step', async () => {
    const text = planner(3, [{ name: 'view_file', args: { AbsolutePath: '/x/skills/alpha/SKILL.md' } }])
    await withSkillTranscript(text, async (home) => {
      const calls = [segmentCall(2)]
      await attachTranscriptActivity(calls, { home })

      expect(calls[0]!.skills).toEqual(['alpha'])
    })
  })

  it('puts no skill on a call whose view_file read is a README, not a SKILL.md', async () => {
    const text = planner(3, [{ name: 'view_file', args: { AbsolutePath: '/x/skills/alpha/README.md' } }])
    await withSkillTranscript(text, async (home) => {
      const calls = [segmentCall(2)]
      await attachTranscriptActivity(calls, { home })

      // The read is attached (positive control), so the empty skill list is not vacuous.
      expect(calls[0]!.tools).toEqual(['view_file'])
      expect(calls[0]!.skills).toEqual([])
    })
  })

  it('rejects a parent directory name outside ^[\\w.-]+$, while a valid sibling read still counts', async () => {
    // The accepted-name rule is the skill-name guard: a directory with a space or an
    // at-sign is not a skill name. The valid `alpha` read proves the path was read.
    const text = planner(3, [
      { name: 'view_file', args: { AbsolutePath: '/x/skills/a b/SKILL.md' } },
      { name: 'view_file', args: { AbsolutePath: '/x/skills/a@b/SKILL.md' } },
      { name: 'view_file', args: { AbsolutePath: '/x/skills/alpha/SKILL.md' } },
    ])
    await withSkillTranscript(text, async (home) => {
      const calls = [segmentCall(2)]
      await attachTranscriptActivity(calls, { home })

      expect(calls[0]!.skills).toEqual(['alpha'])
    })
  })

  it('counts two reads of the same skill in one segment as one invocation, the turn-level key holding a name once', async () => {
    // Engine convention (skills.ts extractInvokedSkills): `kyber.skills.invoked` holds a
    // name once per turn, however many times it was read. Both reads still appear in the
    // tool sequence, so the dedupe is of the skill list, not of the evidence.
    const text = planner(3, [
      { name: 'view_file', args: { AbsolutePath: '/x/skills/alpha/SKILL.md' } },
      { name: 'view_file', args: { AbsolutePath: '/x/skills/alpha/SKILL.md' } },
    ])
    await withSkillTranscript(text, async (home) => {
      const calls = [segmentCall(2)]
      await attachTranscriptActivity(calls, { home })

      expect(calls[0]!.skills).toEqual(['alpha'])
      expect(calls[0]!.tools.filter((tool) => tool === 'view_file')).toHaveLength(2)
    })
  })

  it('accepts a Windows-style SKILL.md path, taking the directory before SKILL.md as the skill', async () => {
    // Decision: backslashes are accepted, as skillNameFromPath already normalizes them
    // (its own test pins `C:\\x\\skills\\docs\\SKILL.md` to `docs`).
    const text = planner(3, [{ name: 'view_file', args: { AbsolutePath: 'C:\\x\\skills\\beta\\SKILL.md' } }])
    await withSkillTranscript(text, async (home) => {
      const calls = [segmentCall(2)]
      await attachTranscriptActivity(calls, { home })

      expect(calls[0]!.skills).toEqual(['beta'])
    })
  })

  it('reads a skill only from view_file, never from a grep whose search path ends in SKILL.md', async () => {
    const text = [
      planner(3, [{ name: 'grep_search', args: { SearchPath: '/x/skills/gamma/SKILL.md', Query: 'x' } }]),
    ].join('\n')
    await withSkillTranscript(text, async (home) => {
      const calls = [segmentCall(2)]
      await attachTranscriptActivity(calls, { home })

      expect(calls[0]!.tools).toEqual(['grep_search'])
      expect(calls[0]!.skills).toEqual([])
    })
  })

  it('gives a SKILL.md read at exactly lsi[N+1] to call N, and the next read to call N+1', async () => {
    const text = [
      planner(3, [{ name: 'view_file', args: { AbsolutePath: '/x/skills/alpha/SKILL.md' } }]),
      planner(5, [{ name: 'view_file', args: { AbsolutePath: '/x/skills/beta/SKILL.md' } }]),
      planner(6, [{ name: 'view_file', args: { AbsolutePath: '/x/skills/gamma/SKILL.md' } }]),
    ].join('\n')
    await withSkillTranscript(text, async (home) => {
      const calls = [segmentCall(2), segmentCall(5)]
      await attachTranscriptActivity(calls, { home })

      expect(calls[0]!.skills).toEqual(['alpha', 'beta'])
      expect(calls[1]!.skills).toEqual(['gamma'])
    })
  })
})

describe('attachTranscriptActivity, transcript size cap (F2)', () => {
  // The leading line is a valid planner step, so the only thing that can make the
  // transcript count as absent is its size. A transcript read whole, with no cap,
  // would attach run_command from this line.
  async function withSizedTranscript(bytes: number, run: (home: string) => Promise<void>): Promise<void> {
    const home = await mkdtemp(join(tmpdir(), 'agy-transcript-cap-'))
    try {
      const dir = join(home, '.gemini', 'antigravity-cli', 'brain', 'conv-1', '.system_generated', 'logs')
      await mkdir(dir, { recursive: true })
      const file = join(dir, 'transcript.jsonl')
      await writeFile(file, planner(3, [{ name: 'run_command', args: { CommandLine: 'synthetic-command --dry-run' } }]) + '\n')
      // Extending with zero bytes makes the file `bytes` long without writing them, as the codewhale cap test does.
      await truncate(file, bytes)
      await run(home)
    } finally {
      await rm(home, { recursive: true, force: true })
    }
  }

  it('treats a transcript over the session-file cap as no transcript, without throwing', async () => {
    await withSizedTranscript(MAX_SESSION_FILE_BYTES + 1, async (home) => {
      const calls = [segmentCall(2)]
      await expect(attachTranscriptActivity(calls, { home })).resolves.toBeUndefined()

      expect(calls[0]!.tools).toEqual([])
      expect(calls[0]!.toolSequence ?? []).toEqual([])
      expect(calls[0]!.skills).toBeUndefined()
    })
  })

  it('still attaches the activity of a transcript just under the session-file cap', async () => {
    await withSizedTranscript(MAX_SESSION_FILE_BYTES - 1, async (home) => {
      const calls = [segmentCall(2)]
      await attachTranscriptActivity(calls, { home })

      expect(calls[0]!.tools).toEqual(['run_command'])
    })
  })
})
