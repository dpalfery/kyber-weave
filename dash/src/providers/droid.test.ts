import { describe, it, expect, beforeEach, afterEach } from 'vitest'
import { mkdtemp, mkdir, writeFile, rm } from 'fs/promises'
import { join } from 'path'
import { tmpdir } from 'os'

import { createDroidProvider, deriveProjectName, sessionProjectOf } from './droid.js'
import type { ParsedProviderCall } from './types.js'

let factoryDir: string

async function writeSession(opts: {
  projectDir?: string
  sessionId?: string
  lines?: unknown[]
  settings?: unknown
  subdir?: string
}): Promise<string> {
  const sessionId = opts.sessionId ?? 'session-1'
  const projectDir = opts.projectDir ?? '/tmp/my-project'
  const subdir = opts.subdir ?? '-tmp-my-project'
  const dir = join(factoryDir, 'sessions', subdir)
  await mkdir(dir, { recursive: true })
  const jsonlPath = join(dir, `${sessionId}.jsonl`)
  const lines = opts.lines ?? [
    { type: 'session_start', id: sessionId, cwd: projectDir, title: 'Test session' },
    { type: 'message', id: 'u1', timestamp: '2026-04-20T10:00:00.000Z', message: { role: 'user', content: [{ type: 'text', text: 'build this' }] } },
    { type: 'message', id: 'a1', timestamp: '2026-04-20T10:00:01.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'done' }] } },
  ]
  await writeFile(jsonlPath, lines.map(line => JSON.stringify(line)).join('\n'))

  if (opts.settings !== undefined) {
    await writeFile(join(dir, `${sessionId}.settings.json`), JSON.stringify(opts.settings))
  }

  return jsonlPath
}

async function parseAll(filePath: string, seen = new Set<string>()): Promise<ParsedProviderCall[]> {
  const provider = createDroidProvider(factoryDir)
  const parser = provider.createSessionParser({ path: filePath, project: 'proj', provider: 'droid' }, seen)
  const calls: ParsedProviderCall[] = []
  for await (const call of parser.parse()) calls.push(call)
  return calls
}

describe('droid provider', () => {
  beforeEach(async () => {
    factoryDir = await mkdtemp(join(tmpdir(), 'codeburn-droid-test-'))
  })

  afterEach(async () => {
    await rm(factoryDir, { recursive: true, force: true })
  })

  it('discovers Droid JSONL sessions', async () => {
    await writeSession({ settings: { model: 'gpt-5', tokenUsage: { inputTokens: 10, outputTokens: 5, cacheCreationTokens: 0, cacheReadTokens: 0, thinkingTokens: 0 } } })

    const provider = createDroidProvider(factoryDir)
    const sessions = await provider.discoverSessions()

    expect(sessions).toHaveLength(1)
    expect(sessions[0]!.provider).toBe('droid')
    expect(sessions[0]!.path.endsWith('session-1.jsonl')).toBe(true)
  })

  it('parses calls and distributes session-level token usage', async () => {
    const path = await writeSession({
      lines: [
        { type: 'session_start', id: 'session-1', cwd: '/tmp/my-project' },
        { type: 'message', id: 'u1', timestamp: '2026-04-20T10:00:00.000Z', message: { role: 'user', content: [{ type: 'text', text: '<system-reminder>x</system-reminder>' }, { type: 'text', text: 'build this' }] } },
        { type: 'message', id: 'a1', timestamp: '2026-04-20T10:00:01.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'first' }] } },
        { type: 'message', id: 'a2', timestamp: '2026-04-20T10:00:02.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'second' }] } },
      ],
      settings: { model: 'custom:gpt-5-[Proxy]-0', tokenUsage: { inputTokens: 101, outputTokens: 51, cacheCreationTokens: 7, cacheReadTokens: 11, thinkingTokens: 5 } },
    })

    const calls = await parseAll(path)

    expect(calls).toHaveLength(2)
    expect(calls[0]!.provider).toBe('droid')
    expect(calls[0]!.model).toBe('gpt-5')
    expect(calls[0]!.inputTokens).toBe(50)
    expect(calls[1]!.inputTokens).toBe(51)
    expect(calls[0]!.outputTokens).toBe(25)
    expect(calls[1]!.outputTokens).toBe(26)
    expect(calls[0]!.cacheReadInputTokens).toBe(5)
    expect(calls[1]!.cacheReadInputTokens).toBe(6)
    expect(calls[0]!.userMessage).toBe('build this')
    expect(calls[0]!.sessionId).toBe('session-1')
  })

  it('extracts tools and meaningful bash command names', async () => {
    const path = await writeSession({
      lines: [
        { type: 'session_start', id: 'session-1', cwd: '/tmp/my-project' },
        { type: 'message', id: 'a1', timestamp: '2026-04-20T10:00:01.000Z', message: { role: 'assistant', content: [
          { type: 'tool_use', name: 'Execute', input: { command: "python3 - <<'PY'\nimport os\n}\nPY" } },
          { type: 'tool_use', name: 'Read', input: { file_path: '/tmp/a' } },
          { type: 'tool_use', name: 'Task', input: { prompt: 'do work' } },
        ] } },
      ],
      settings: { model: 'gpt-5', tokenUsage: { inputTokens: 10, outputTokens: 5, cacheCreationTokens: 0, cacheReadTokens: 0, thinkingTokens: 0 } },
    })

    const calls = await parseAll(path)

    expect(calls).toHaveLength(1)
    expect(calls[0]!.tools).toEqual(['Bash', 'Read', 'Agent'])
    expect(calls[0]!.bashCommands).toContain('python3')
    expect(calls[0]!.bashCommands).not.toContain('import')
    expect(calls[0]!.bashCommands).not.toContain('}')
  })

  it('deduplicates calls by session and message id', async () => {
    const path = await writeSession({ settings: { model: 'gpt-5', tokenUsage: { inputTokens: 10, outputTokens: 5, cacheCreationTokens: 0, cacheReadTokens: 0, thinkingTokens: 0 } } })
    const seen = new Set<string>()

    expect(await parseAll(path, seen)).toHaveLength(1)
    expect(await parseAll(path, seen)).toHaveLength(0)
  })

  it('strips Droid model wrappers for display', () => {
    const provider = createDroidProvider(factoryDir)
    expect(provider.modelDisplayName('custom:GLM-5.1-[Proxy]-0')).toBe('GLM-5.1')
    expect(provider.modelDisplayName('custom:claude-sonnet-4-6-1')).toBe('Sonnet 4.6')
  })

  it('returns no calls when settings are missing', async () => {
    const path = await writeSession({})
    expect(await parseAll(path)).toHaveLength(0)
  })

  it('skips internal .factory sessions during discovery', async () => {
    await writeSession({ projectDir: factoryDir, subdir: '-internal', settings: { model: 'gpt-5', tokenUsage: { inputTokens: 10, outputTokens: 5, cacheCreationTokens: 0, cacheReadTokens: 0, thinkingTokens: 0 } } })

    const provider = createDroidProvider(factoryDir)
    expect(await provider.discoverSessions()).toHaveLength(0)
  })

  it('derives the session id from the session_start record rather than from the transcript file name', async () => {
    const path = await writeSession({
      sessionId: 'file-stem',
      lines: [
        { type: 'session_start', id: 'record-session-id', cwd: '/tmp/my-project' },
        { type: 'message', id: 'a1', timestamp: '2026-04-20T10:00:01.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'done' }] } },
      ],
      settings: { model: 'custom:gpt-5-[Proxy]-0', tokenUsage: { inputTokens: 10, outputTokens: 5, cacheCreationTokens: 0, cacheReadTokens: 0, thinkingTokens: 0 } },
    })

    const calls = await parseAll(path)

    expect(calls).toHaveLength(1)
    expect(calls[0]!.provider).toBe('droid')
    expect(calls[0]!.sessionId).toBe('record-session-id')
    expect(calls[0]!.deduplicationKey).toBe('droid:record-session-id:a1')
  })

  it('takes the token counters from the companion settings file, not from the transcript', async () => {
    // The transcript carries no usage information of its own: no assistant
    // record has a `usage` field, so every counter below can only have come
    // from the companion <session-id>.settings.json file. The companion
    // tokenUsage also carries a factoryCredits count, which is not a token
    // class and never reaches the counters.
    const path = await writeSession({
      lines: [
        { type: 'session_start', id: 'session-1', cwd: '/tmp/my-project' },
        { type: 'message', id: 'a1', timestamp: '2026-04-20T10:00:01.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'done' }] } },
      ],
      settings: {
        model: 'custom:gpt-5-[Proxy]-0',
        tokenUsage: { inputTokens: 1234, outputTokens: 567, cacheCreationTokens: 89, cacheReadTokens: 21, thinkingTokens: 7, factoryCredits: 900 },
      },
    })

    const calls = await parseAll(path)

    expect(calls).toHaveLength(1)
    expect(calls[0]!.inputTokens).toBe(1234)
    expect(calls[0]!.outputTokens).toBe(567)
    expect(calls[0]!.cacheCreationInputTokens).toBe(89)
    expect(calls[0]!.cacheReadInputTokens).toBe(21)
    expect(calls[0]!.cachedInputTokens).toBe(21)
    expect(calls[0]!.reasoningTokens).toBe(7)
  })

  it('emits no call at all for a transcript with no companion settings file, while a companion file of zero counters still yields a zero-token call', async () => {
    const path = await writeSession({
      lines: [
        { type: 'session_start', id: 'session-1', cwd: '/tmp/my-project' },
        { type: 'message', id: 'a1', timestamp: '2026-04-20T10:00:01.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'done' }] } },
      ],
    })

    expect(await parseAll(path)).toHaveLength(0)

    // The contrast that keeps the case honest: a companion file that reports
    // zero tokens still produces a call, so the empty result above is the
    // missing companion file and not the provider rounding tokens away.
    const zeroPath = await writeSession({
      sessionId: 'session-zero',
      lines: [
        { type: 'session_start', id: 'session-zero', cwd: '/tmp/my-project' },
        { type: 'message', id: 'a1', timestamp: '2026-04-20T10:00:01.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'done' }] } },
      ],
      settings: { model: 'custom:gpt-5-[Proxy]-0', tokenUsage: { inputTokens: 0, outputTokens: 0, cacheCreationTokens: 0, cacheReadTokens: 0, thinkingTokens: 0 } },
    })

    const zeroCalls = await parseAll(zeroPath)
    expect(zeroCalls).toHaveLength(1)
    expect(zeroCalls[0]!.inputTokens).toBe(0)
  })

  // The even split is a known lossy approximation: Droid reports token usage
  // once per session, so the provider divides the session totals evenly across
  // the assistant calls and assigns the remainder to the last call. Pinned
  // deliberately, in exactly this form; a per-call attribution rule is slated
  // for a separate follow-up and must not be folded into this characterization.
  it('splits the session totals evenly across the assistant calls and puts the remainder on the last call', async () => {
    const path = await writeSession({
      lines: [
        { type: 'session_start', id: 'session-1', cwd: '/tmp/my-project' },
        { type: 'message', id: 'u1', timestamp: '2026-04-20T10:00:00.000Z', message: { role: 'user', content: [{ type: 'text', text: 'build this' }] } },
        { type: 'message', id: 'a1', timestamp: '2026-04-20T10:00:01.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'first' }] } },
        { type: 'message', id: 'a2', timestamp: '2026-04-20T10:00:02.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'second' }] } },
        { type: 'message', id: 'a3', timestamp: '2026-04-20T10:00:03.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'third' }] } },
      ],
      settings: {
        model: 'custom:gpt-5-[Proxy]-0',
        tokenUsage: { inputTokens: 100, outputTokens: 50, cacheCreationTokens: 7, cacheReadTokens: 11, thinkingTokens: 5 },
      },
    })

    const calls = await parseAll(path)

    expect(calls).toHaveLength(3)
    expect(calls.map((call) => call.inputTokens)).toEqual([33, 33, 34])
    expect(calls.map((call) => call.outputTokens)).toEqual([16, 16, 18])
    expect(calls.map((call) => call.cacheCreationInputTokens)).toEqual([2, 2, 3])
    expect(calls.map((call) => call.cacheReadInputTokens)).toEqual([3, 3, 5])
    expect(calls.map((call) => call.reasoningTokens)).toEqual([1, 1, 3])
  })

  it('attributes each session to the model named in its own companion settings file', async () => {
    const gptPath = await writeSession({
      sessionId: 'session-gpt',
      lines: [
        { type: 'session_start', id: 'session-gpt', cwd: '/tmp/my-project' },
        { type: 'message', id: 'a1', timestamp: '2026-04-20T10:00:01.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'done' }] } },
      ],
      settings: { model: 'custom:gpt-5-[Proxy]-0', tokenUsage: { inputTokens: 10, outputTokens: 5, cacheCreationTokens: 0, cacheReadTokens: 0, thinkingTokens: 0 } },
    })
    const claudePath = await writeSession({
      sessionId: 'session-claude',
      lines: [
        { type: 'session_start', id: 'session-claude', cwd: '/tmp/my-project' },
        { type: 'message', id: 'a1', timestamp: '2026-04-20T10:00:01.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'done' }] } },
      ],
      settings: { model: 'custom:claude-sonnet-4-6-1', tokenUsage: { inputTokens: 10, outputTokens: 5, cacheCreationTokens: 0, cacheReadTokens: 0, thinkingTokens: 0 } },
    })
    const unknownPath = await writeSession({
      sessionId: 'session-unknown',
      lines: [
        { type: 'session_start', id: 'session-unknown', cwd: '/tmp/my-project' },
        { type: 'message', id: 'a1', timestamp: '2026-04-20T10:00:01.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'done' }] } },
      ],
      settings: { tokenUsage: { inputTokens: 10, outputTokens: 5, cacheCreationTokens: 0, cacheReadTokens: 0, thinkingTokens: 0 } },
    })

    expect((await parseAll(gptPath))[0]!.model).toBe('gpt-5')
    expect((await parseAll(claudePath))[0]!.model).toBe('claude-sonnet-4-6')
    expect((await parseAll(unknownPath))[0]!.model).toBe('unknown')
  })

  it('ignores the companion file child-usage map, so the child tokens never reach the session', async () => {
    const path = await writeSession({
      lines: [
        { type: 'session_start', id: 'session-1', cwd: '/tmp/my-project' },
        { type: 'message', id: 'a1', timestamp: '2026-04-20T10:00:01.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'done' }] } },
      ],
      settings: {
        model: 'custom:gpt-5-[Proxy]-0',
        tokenUsage: { inputTokens: 100, outputTokens: 50, cacheCreationTokens: 7, cacheReadTokens: 11, thinkingTokens: 5 },
        childInclusiveTokenUsageBySessionId: {
          'child-session-1': { inputTokens: 9000, outputTokens: 800, cacheCreationTokens: 90, cacheReadTokens: 70, thinkingTokens: 60 },
        },
        lastCallTokenUsage: { inputTokens: 100, outputTokens: 50, cacheCreationTokens: 7, cacheReadTokens: 11, thinkingTokens: 5 },
        inclusiveTokenUsage: { inputTokens: 118, outputTokens: 55, cacheCreationTokens: 7, cacheReadTokens: 11, thinkingTokens: 5 },
        providerLock: 'anthropic',
        apiProviderLock: 'anthropic',
        toolExecutionModeModelId: 'custom:gpt-5-[Proxy]-0',
        reasoningEffort: 'high',
        interactionMode: 'agent',
        autonomyLevel: 'high',
        artifacts: [],
        allowSubagentsInScripts: true,
        compactionThresholdCheckEnabled: true,
        specModeModel: 'custom:gpt-5-[Proxy]-0',
      },
    })

    const calls = await parseAll(path)

    expect(calls).toHaveLength(1)
    // The session counters are the parent session's own tokenUsage; the child
    // session's 9000 input tokens are not folded into them.
    expect(calls[0]!.inputTokens).toBe(100)
    expect(calls[0]!.outputTokens).toBe(50)
    expect(calls[0]!.cacheCreationInputTokens).toBe(7)
    expect(calls[0]!.cacheReadInputTokens).toBe(11)
    expect(calls[0]!.reasoningTokens).toBe(5)
  })

  it('resolves the Factory root from FACTORY_DIR instead of the home directory', async () => {
    await writeSession({ settings: { model: 'custom:gpt-5-[Proxy]-0', tokenUsage: { inputTokens: 10, outputTokens: 5, cacheCreationTokens: 0, cacheReadTokens: 0, thinkingTokens: 0 } } })

    const previous = process.env['FACTORY_DIR']
    process.env['FACTORY_DIR'] = factoryDir
    try {
      const provider = createDroidProvider()

      const sessions = await provider.discoverSessions()

      expect(sessions).toHaveLength(1)
      expect(sessions[0]!.path.startsWith(factoryDir)).toBe(true)
      const roots = await provider.probeRoots!()
      expect(roots[0]!.path).toBe(join(factoryDir, 'sessions'))
    } finally {
      if (previous === undefined) delete process.env['FACTORY_DIR']
      else process.env['FACTORY_DIR'] = previous
    }
  })

  it('returns no calls for empty sessions', async () => {
    const path = await writeSession({
      lines: [{ type: 'session_start', id: 'empty', cwd: '/tmp/my-project' }],
      settings: { model: 'gpt-5', tokenUsage: { inputTokens: 10, outputTokens: 5, cacheCreationTokens: 0, cacheReadTokens: 0, thinkingTokens: 0 } },
    })

    expect(await parseAll(path)).toHaveLength(0)
  })
})

describe('the project a Factory session is filed under', () => {
  it('strips the home directory only at a path boundary', () => {
    // A directory that merely starts with the same characters is not under
    // the home: stripping the prefix there would file the session under a
    // project that belongs to a different tree.
    expect(deriveProjectName('/home/alice/kyber', '/home/alice')).toBe('kyber')
    expect(deriveProjectName('/home/alice2/kyber', '/home/alice')).toBe('home/alice2/kyber')
    expect(deriveProjectName('/home/alice', '/home/alice')).toBe('~')
  })

  it('files a session under the project its own file names', async () => {
    const dir = await mkdtemp(join(tmpdir(), 'kyber-droid-project-'))
    try {
      const sessionDir = join(dir, 'sessions', 'workspace')
      await mkdir(sessionDir, { recursive: true })
      const path = join(sessionDir, 'session-1.jsonl')
      await writeFile(
        path,
        `${JSON.stringify({ type: 'session_start', id: 'session-1', cwd: '/tmp/my-project' })}\n`,
        'utf-8',
      )

      // The live counter pass and the file source's own discovery must name
      // one session the same way.
      const provider = createDroidProvider(dir)
      const discovered = await provider.discoverSessions()
      expect(discovered[0]?.project).toBe('tmp/my-project')
      expect(await sessionProjectOf(path, dir)).toBe('tmp/my-project')
    } finally {
      await rm(dir, { recursive: true, force: true })
    }
  })

  it('keeps no counters for a session whose working directory is the Factory root', async () => {
    const dir = await mkdtemp(join(tmpdir(), 'kyber-droid-internal-'))
    try {
      const sessionDir = join(dir, 'sessions', 'workspace')
      await mkdir(sessionDir, { recursive: true })
      const path = join(sessionDir, 'session-1.jsonl')
      await writeFile(
        path,
        `${JSON.stringify({ type: 'session_start', id: 'session-1', cwd: dir })}\n`,
        'utf-8',
      )

      expect(await sessionProjectOf(path, dir)).toBeUndefined()
    } finally {
      await rm(dir, { recursive: true, force: true })
    }
  })
})
