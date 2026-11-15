// Storm guard (T3): `kyber otel` without `--serve` attaches to the one engine, and starts that
// engine from this process's own entry. When the entry is not the kyberdash CLI, the command must
// fail closed: name the refusal on stderr, exit non-zero, and never call ensureEngine.
//
// Seams: the resolver reads process.argv[1] live (set per test, restored after), ensureEngine is
// replaced by a spy so a start would be observable, and process.exit throws a sentinel so the
// command stops where the real process would have exited.
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { Command } from 'commander'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { CanonStore, migrateCanonDatabase } from '../canon/store.js'
import { ensureEngine } from '../engine/ensure.js'
import { SETTING_KEYS, readSetting } from '../settings/shared-settings.js'
import { registerKyberCommands } from './register.js'

vi.mock('../engine/ensure.js', () => ({ ensureEngine: vi.fn() }))

/** Thrown in place of process.exit, so the command stops at the exit it would have taken. */
class ExitSentinel extends Error {
  constructor(readonly code: number) {
    super(`process.exit(${code})`)
  }
}

const REFUSED_ENTRY = '/repo/dash/e2e/harness-collection-boot.ts'
let priorEntry: string | undefined
let dbDir = ''

beforeEach(() => {
  priorEntry = process.argv[1]
  dbDir = mkdtempSync(join(tmpdir(), 'kyber-otel-refused-'))
  vi.mocked(ensureEngine).mockClear()
})

afterEach(() => {
  process.argv[1] = priorEntry ?? ''
  vi.restoreAllMocks()
  rmSync(dbDir, { recursive: true, force: true })
})

async function runKyberOtel(): Promise<ExitSentinel> {
  const program = new Command()
  registerKyberCommands(program)
  const outcome = await program
    .parseAsync(['kyber', 'otel', '--db', join(dbDir, 'canon.db')], { from: 'user' })
    .then(
      () => { throw new Error('kyber otel returned without exiting') },
      (err: unknown) => err,
    )
  expect(outcome, 'kyber otel must end at process.exit').toBeInstanceOf(ExitSentinel)
  return outcome as ExitSentinel
}

/** Captures stdout and stderr and turns process.exit into a sentinel throw. */
function captureOutputAndExit(): { stderr: string[]; stdout: string[] } {
  const stderr: string[] = []
  const stdout: string[] = []
  vi.spyOn(process.stderr, 'write').mockImplementation((chunk: string | Uint8Array) => {
    stderr.push(String(chunk))
    return true
  })
  vi.spyOn(process.stdout, 'write').mockImplementation((chunk: string | Uint8Array) => {
    stdout.push(String(chunk))
    return true
  })
  vi.spyOn(process, 'exit').mockImplementation(((code?: number) => {
    throw new ExitSentinel(code ?? 0)
  }) as never)
  return { stderr, stdout }
}

describe('kyber otel: an engine is never started from an entry that is not the CLI', () => {
  it('reports the refusal on stderr, exits 1, and never calls ensureEngine', async () => {
    process.argv[1] = REFUSED_ENTRY
    const { stderr, stdout } = captureOutputAndExit()

    const exit = await runKyberOtel()

    expect(exit.code, 'a refused entry must exit non-zero').toBe(1)
    expect(stderr.join(''), 'the refusal must reach the operator').toContain(
      `kyberdash otel: spawn target refused: ${REFUSED_ENTRY} is not the kyberdash CLI entry`,
    )
    expect(ensureEngine, 'a refused entry must start no engine').not.toHaveBeenCalled()
    expect(stdout.join(''), 'a refused entry must not report an engine as started').not.toContain('KyberDash engine')
  })

  it('leaves the receiver setting off in the store, since no receiver was attached', async () => {
    process.argv[1] = REFUSED_ENTRY
    captureOutputAndExit()

    await runKyberOtel()

    const dbPath = join(dbDir, 'canon.db')
    await migrateCanonDatabase(dbPath)
    const store = new CanonStore(dbPath)
    try {
      expect(
        readSetting(store, SETTING_KEYS.receiverHosted),
        'a refused `kyber otel` must not record the receiver as hosted: the setting must stay off',
      ).toBe('off')
    } finally {
      store.close()
    }
  })
})
