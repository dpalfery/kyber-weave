// T11 — `kyberdash kyber antigravity-statusline`.
//
// Two contracts live here. The stream contract (C9): the command writes nothing to stdout
// and always exits 0, whatever it is handed, because a status-line host renders stdout and
// disables a command that fails. The real recorder (C8): a valid payload appends exactly one
// line to KyberDash's own cache file, under a temp `KYBERDASH_CACHE_DIR` — never the real one.
//
// RED evidence is a runtime assertion failure, never a compile error: the seam this file
// injects is declared locally, so the file typechecks before the command exists.

import { existsSync, mkdtempSync, readFileSync, rmSync, statSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { Command } from 'commander'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { registerKyberCommands } from './register.js'

/**
 * One synthetic `agy` statusLine payload (C3): no real conversation id, path, or account
 * data. Written from the published payload schema, carrying the three things the recorder
 * requires — a non-empty `conversation_id`, a resolvable `model`, and a
 * `context_window.current_usage` with at least one non-zero token count.
 */
const SYNTHETIC_AGY_STATUS_LINE = {
  conversation_id: 'synthetic-conversation-0001',
  session_id: 'synthetic-session-0001',
  workspace: { current_dir: '/synthetic/workspace' },
  model: { id: 'gemini-3.5-flash-high', display_name: 'Gemini 3.5 Flash (High)' },
  context_window: {
    current_usage: {
      input_tokens: 1024,
      output_tokens: 64,
      cache_creation_input_tokens: 0,
      cache_read_input_tokens: 0,
    },
  },
}

/** All-zero usage: parseable, but the recorder ignores it rather than recording a zero turn. */
const ZERO_USAGE_STATUS_LINE = {
  ...SYNTHETIC_AGY_STATUS_LINE,
  context_window: {
    current_usage: {
      input_tokens: 0,
      output_tokens: 0,
      cache_creation_input_tokens: 0,
      cache_read_input_tokens: 0,
    },
  },
}

/** No resolvable model: the recorder has no model to attribute the usage to, so it ignores it. */
const MODEL_LESS_STATUS_LINE = {
  conversation_id: 'synthetic-conversation-0002',
  context_window: SYNTHETIC_AGY_STATUS_LINE.context_window,
}

/**
 * The dependency shape this command's seams must satisfy, spelled out here rather than
 * imported: `recordAntigravityStatusLine` is the seam T11 asks for and does not exist on
 * `KyberCommandDependencies` yet.
 */
type StatusLineDependencies = {
  readStdin: () => Promise<string>
  write: (line: string) => void
  writeError: (line: string) => void
  recordAntigravityStatusLine?: (input: unknown) => Promise<boolean>
}

type StatusLineRun = {
  failure: unknown
  exitCode: number
  stdoutLines: string[]
  stderrLines: string[]
  recorded: unknown[]
}

const temporaryRoots: string[] = []

// The real-recorder cases point the recorder at a temp cache dir. Capture the previous
// value before each test and put it back after, so KYBERDASH_CACHE_DIR cannot leak past
// the case that set it (T11 audit; same pattern as open-design.test.ts).
let previousCacheDir: string | undefined

beforeEach(() => {
  previousCacheDir = process.env['KYBERDASH_CACHE_DIR']
})

afterEach(() => {
  for (const root of temporaryRoots.splice(0)) rmSync(root, { recursive: true, force: true })
  if (previousCacheDir === undefined) delete process.env['KYBERDASH_CACHE_DIR']
  else process.env['KYBERDASH_CACHE_DIR'] = previousCacheDir
  vi.restoreAllMocks()
})

/** Every emitted line, counting a seam's line writes and a direct stream write alike. */
function emittedLines(...chunkGroups: ReadonlyArray<readonly string[]>): string[] {
  return chunkGroups
    .flatMap((chunks) => chunks.flatMap((chunk) => chunk.split('\n')))
    .filter((line) => line !== '')
}

/**
 * Drive `kyberdash kyber antigravity-statusline` with an isolated stdin payload.
 *
 * The output seams are injected and the process streams are spied as well: C9 is a claim
 * about what the command emits, so a direct `process.stdout.write` that bypassed the seam
 * must not be able to satisfy the silence assertions.
 */
async function runAntigravityStatusLine(options: {
  stdin: string
  record?: (input: unknown) => Promise<boolean>
}): Promise<StatusLineRun> {
  const seamStdout: string[] = []
  const seamStderr: string[] = []
  const directStdout: string[] = []
  const directStderr: string[] = []
  const recorded: unknown[] = []

  const stdoutSpy = vi.spyOn(process.stdout, 'write').mockImplementation(((chunk: unknown) => {
    directStdout.push(String(chunk))
    return true
  }) as typeof process.stdout.write)
  const stderrSpy = vi.spyOn(process.stderr, 'write').mockImplementation(((chunk: unknown) => {
    directStderr.push(String(chunk))
    return true
  }) as typeof process.stderr.write)

  const dependencies: StatusLineDependencies = {
    readStdin: async () => options.stdin,
    write: (line) => { seamStdout.push(line) },
    writeError: (line) => { seamStderr.push(line) },
  }
  const record = options.record
  if (record !== undefined) {
    dependencies.recordAntigravityStatusLine = async (input: unknown) => {
      recorded.push(input)
      return record(input)
    }
  }

  const program = new Command()
  program.exitOverride()
  registerKyberCommands(program, dependencies)

  const previousExitCode = process.exitCode
  process.exitCode = undefined
  let failure: unknown = null
  try {
    await program.parseAsync(['node', 'kyberdash', 'kyber', 'antigravity-statusline'])
  } catch (error) {
    failure = error
  } finally {
    stdoutSpy.mockRestore()
    stderrSpy.mockRestore()
  }
  const exitCode = process.exitCode ?? 0
  process.exitCode = previousExitCode

  return {
    failure,
    exitCode,
    stdoutLines: emittedLines(seamStdout, directStdout),
    stderrLines: emittedLines(seamStderr, directStderr),
    recorded,
  }
}

describe('kyber antigravity-statusline stream contract (C9)', () => {
  it('records a valid payload without writing to stdout and exits 0', async () => {
    const result = await runAntigravityStatusLine({
      stdin: JSON.stringify(SYNTHETIC_AGY_STATUS_LINE),
      record: async () => true,
    })

    expect(result.failure).toBeNull()
    expect(result.stdoutLines).toEqual([])
    expect(result.recorded).toEqual([SYNTHETIC_AGY_STATUS_LINE])
    expect(result.exitCode).toBe(0)
  })

  it.each([
    { name: 'an all-zero usage payload', payload: ZERO_USAGE_STATUS_LINE },
    { name: 'a payload with no model', payload: MODEL_LESS_STATUS_LINE },
  ])('ignores $name without writing to stdout and exits 0', async ({ payload }) => {
    const result = await runAntigravityStatusLine({
      stdin: JSON.stringify(payload),
      // The recorder decides what is ignorable — it returns false and writes nothing. The
      // command's job is to hand it the parsed payload, not to filter first.
      record: async () => false,
    })

    expect(result.failure).toBeNull()
    expect(result.stdoutLines).toEqual([])
    expect(result.recorded).toEqual([payload])
    expect(result.exitCode).toBe(0)
  })

  it.each([
    { name: 'malformed JSON', stdin: '{"conversation_id": ' },
    { name: 'empty stdin', stdin: '' },
  ])('survives $name without writing to stdout and exits 0', async ({ stdin }) => {
    const result = await runAntigravityStatusLine({ stdin, record: async () => true })

    expect(result.failure).toBeNull()
    expect(result.stdoutLines).toEqual([])
    expect(result.exitCode).toBe(0)
  })

  it('writes at most one stderr line and still exits 0 when the recorder fails', async () => {
    const result = await runAntigravityStatusLine({
      stdin: JSON.stringify(SYNTHETIC_AGY_STATUS_LINE),
      record: async () => { throw new Error('synthetic cache write failure') },
    })

    expect(result.failure).toBeNull()
    expect(result.stdoutLines).toEqual([])
    expect(result.stderrLines.length).toBeLessThanOrEqual(1)
    expect(result.exitCode).toBe(0)
  })
})

describe('kyber antigravity-statusline real recorder (C8)', () => {
  /** Point the recorder at a temp cache dir, so no test can touch the real ~/.kyberdash/cache. */
  function tempCacheDir(): string {
    const root = mkdtempSync(join(tmpdir(), 'kyber-statusline-'))
    temporaryRoots.push(root)
    process.env['KYBERDASH_CACHE_DIR'] = root
    return root
  }

  it('appends exactly one line to antigravity-statusline.jsonl under KYBERDASH_CACHE_DIR', async () => {
    const cacheDir = tempCacheDir()

    const result = await runAntigravityStatusLine({ stdin: JSON.stringify(SYNTHETIC_AGY_STATUS_LINE) })

    expect(result.failure).toBeNull()
    expect(result.stdoutLines).toEqual([])
    expect(result.exitCode).toBe(0)

    const file = join(cacheDir, 'antigravity-statusline.jsonl')
    expect(existsSync(file)).toBe(true)
    const lines = readFileSync(file, 'utf8').split('\n').filter((line) => line !== '')
    expect(lines).toHaveLength(1)
    expect(JSON.parse(lines[0]!)).toMatchObject({
      conversationId: 'synthetic-conversation-0001',
      sessionId: 'synthetic-session-0001',
      model: 'gemini-3.5-flash-high',
      usage: {
        inputTokens: 1024,
        outputTokens: 64,
        cacheCreationInputTokens: 0,
        cacheReadInputTokens: 0,
      },
    })
  })

  // Windows has no POSIX mode bits, and D4 defers Windows support to its own issue.
  it.skipIf(process.platform === 'win32')('creates the cache file owner-only (0600)', async () => {
    const cacheDir = tempCacheDir()

    const result = await runAntigravityStatusLine({ stdin: JSON.stringify(SYNTHETIC_AGY_STATUS_LINE) })

    expect(result.failure).toBeNull()
    expect(result.exitCode).toBe(0)
    const mode = statSync(join(cacheDir, 'antigravity-statusline.jsonl')).mode & 0o777
    expect(mode.toString(8)).toBe('600')
  })

  it('writes no cache file at all for an ignored payload', async () => {
    const cacheDir = tempCacheDir()

    const result = await runAntigravityStatusLine({ stdin: JSON.stringify(ZERO_USAGE_STATUS_LINE) })

    expect(result.failure).toBeNull()
    expect(result.exitCode).toBe(0)
    expect(existsSync(join(cacheDir, 'antigravity-statusline.jsonl'))).toBe(false)
  })
})
