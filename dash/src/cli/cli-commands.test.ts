import { spawnSync } from 'node:child_process'

import { Command, CommanderError } from 'commander'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { SETTING_KEYS, readSetting, writeSetting } from '../settings/shared-settings.js'
import { DEFAULT_FOLDER_IMPORT_WEEKS } from '../refresh/folder-import.js'
import type { FolderImportOptions } from '../refresh/folder-import.js'
import { REFRESH_BUSY_EXIT_CODE, registerKyberCommands } from './register.js'
import type { KyberCommandDependencies } from './register.js'
import { buildProgram } from './program.js'

// Parsing is what these tests are about, so the actions stay out of the way:
// nothing installs a tray, probes a provider, or reads this machine's config.
const installTray = vi.hoisted(() => vi.fn(async () => null))
vi.mock('../install/menubar.js', () => ({ installTray }))
vi.mock('../install/node-deps.js', () => ({ nodeInstallDeps: () => ({}) }))
vi.mock('./doctor.js', () => ({
  collectDoctorReport: async () => ({}),
  renderDoctorTable: () => '',
  renderDoctorJson: () => '{}',
}))
vi.mock('../config.js', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../config.js')>()),
  readConfig: async () => ({}),
}))

async function parse(...args: string[]): Promise<void> {
  await buildProgram().exitOverride().parseAsync(['node', 'kyberdash', ...args])
}

// Requirements 2.1 and 2.2 of the KyberDash context surfaces specification: the CLI registers
// the context-troubleshooting commands and nothing inherited from the spend product. Hidden
// commands count too, which is why this reads the tree rather than the help text.
const RETAINED = ['dash', 'doctor', 'kyber', 'menubar', 'otel', 'report', 'web']

describe('registered top-level commands', () => {
  it('are exactly the retained set', () => {
    const names = buildProgram()
      .commands.map(command => command.name())
      .sort()
    expect(names).toEqual(RETAINED)
  })

  it('keep the KyberDash subcommands', () => {
    const program = buildProgram()
    const sub = (name: string) =>
      program.commands
        .find(command => command.name() === name)!
        .commands.map(command => command.name())
        .sort()
    expect(sub('kyber')).toEqual(['antigravity-statusline', 'backfill', 'build', 'capture', 'cursor-hook', 'otel', 'renormalize'])
    // The dash group grew by design (issue #319): import-history (alias `import`) and settings.
    expect(sub('dash')).toEqual(['clean', 'import-history', 'refresh', 'settings'])
  })

  it('keep report as the default command', () => {
    const program = buildProgram() as unknown as { _defaultCommandName?: string }
    expect(program._defaultCommandName).toBe('report')
  })
})

describe('menubar', () => {
  /**
   * The installer's own behaviour is covered in `src/install/menubar.test.ts`
   * against injected ports. What is worth checking through a real spawn is the
   * wiring: the command reaches the installer, and a failure there exits
   * non-zero with the step named (R15.6).
   *
   * The origin is pointed at a port nothing is listening on, so the run fails
   * at the first fetch without touching the network or this machine's
   * /Applications.
   */
  it('runs the installer and exits non-zero naming the failing step', () => {
    const result = spawnSync(process.execPath, ['--import', 'tsx', 'src/launcher.ts', 'menubar'], {
      cwd: new URL('../..', import.meta.url),
      encoding: 'utf8',
      timeout: 60_000,
      env: { ...process.env, KYBER_WEAVE_RELEASE_ORIGIN: 'http://127.0.0.1:1/release' },
    })

    expect(result.status).toBe(1)
    expect(result.stderr).toContain('kyberdash menubar:')
    // Either the platform refusal (Linux) or the first network step; both name
    // where it stopped, which is the contract.
    expect(result.stderr).toMatch(/checksums:|platform:/)
  })

  it('takes --force, --update, and --version, plus the shared options', () => {
    const menubar = buildProgram().commands.find(command => command.name() === 'menubar')!
    const flags = menubar.options.map(option => option.long).sort()
    expect(flags).toEqual(['--force', '--timezone', '--update', '--verbose', '--version'])
  })

  /**
   * Declaring `--version` is not the same as receiving it: the root's own
   * `--version` used to match first, so this exact line — the one
   * `kyber-weave update` runs — printed the CLI version and exited 0 without
   * reaching the installer.
   */
  it('hands the self-updater line to the installer, not to the root --version', async () => {
    installTray.mockClear()

    await parse('menubar', '--update', '--version', '1.2.3')

    expect(installTray).toHaveBeenCalledOnce()
    expect(installTray).toHaveBeenCalledWith({}, { force: undefined, update: true, version: '1.2.3' })
  })
})

describe('the root --version', () => {
  it('still prints the CLI version', async () => {
    const program = buildProgram().exitOverride()
    let written = ''
    program.configureOutput({ writeOut: text => { written += text } })

    const error = await program.parseAsync(['node', 'kyberdash', '--version']).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(CommanderError)
    expect((error as CommanderError).code).toBe('commander.version')
    expect(written.trim()).toMatch(/^\d+\.\d+\.\d+/)
  })
})

describe('shared options', () => {
  const saved = { tz: process.env.TZ, verbose: process.env['KYBERDASH_VERBOSE'] }

  afterEach(() => {
    for (const [key, value] of [['TZ', saved.tz], ['KYBERDASH_VERBOSE', saved.verbose]] as const) {
      if (value === undefined) delete process.env[key]
      else process.env[key] = value
    }
  })

  it('are declared on every command, so positional parsing does not strand them', () => {
    const walk = (command: ReturnType<typeof buildProgram>): string[] =>
      command.commands.flatMap(child => {
        const flags = child.options.map(option => option.long)
        const missing = ['--verbose', '--timezone'].filter(flag => !flags.includes(flag))
        return [...missing.map(flag => `${child.name()} ${flag}`), ...walk(child)]
      })
    expect(walk(buildProgram())).toEqual([])
  })

  it.each([
    ['after the subcommand', ['doctor', '--verbose', '--timezone', 'Asia/Tokyo']],
    ['before the subcommand', ['--verbose', '--timezone', 'Asia/Tokyo', 'doctor']],
  ])('reach the pre-action hook when given %s', async (_where, args) => {
    delete process.env['KYBERDASH_VERBOSE']

    await parse(...args)

    expect(process.env['KYBERDASH_VERBOSE']).toBe('1')
    expect(process.env.TZ).toBe('Asia/Tokyo')
  })
})

// `dash import-history` (alias `dash import`): the CLI face of the one-off, operator-driven
// history backfill. It is the same ingest `dash refresh` runs, narrowed by `--harness` and
// widened by `--weeks`, so what these tests pin down is the CLI contract only:
//
//  - the weeks window is validated at parse time, before the store opens, so a rejected
//    request leaves no database behind (a half-created canon.db is indistinguishable from a
//    successful import's on disk);
//  - it takes the same refresh lock and reports "already running" with the same exit 3 as
//    refresh and clean (ADR 0023 D4) — a caller cannot tell which command it started, so it
//    cannot be told a different code;
//  - it never reads or writes `settings.folder_import.scheduled`. "Import this once" and
//    "keep importing" are different decisions, and the setting gates only the scheduled and
//    automatic refreshes (see folderSourcesAllowed).
//
// The command does not exist yet, so every test below fails until it lands.
describe('import-history', () => {
  const savedExitCode = process.exitCode

  afterEach(() => {
    // process.exitCode is process-global: a value assigned by one test would otherwise
    // decide the exit status of the vitest run itself.
    process.exitCode = savedExitCode
  })

  it('registers import-history beneath the top-level dash command with --weeks and --harness', () => {
    const program = new Command()
    registerKyberCommands(program)

    const dash = program.commands.find((command) => command.name() === 'dash')
    const importHistory = dash?.commands.find((command) => command.name() === 'import-history')
    expect(importHistory).toBeDefined()
    expect(importHistory?.options.some((option) => option.long === '--weeks')).toBe(true)
    expect(importHistory?.options.some((option) => option.long === '--harness')).toBe(true)
  })

  /**
   * Drive the command through registerKyberCommands with every collaborator faked: the
   * store, the lock, and importFolderHistory itself. The fakes record the arguments rather
   * than performing an ingest, because the ingest itself is covered in
   * `src/refresh/folder-import.test.ts` — here what matters is that the right options reach
   * it through the same dependency seam refresh uses.
   *
   * `importFolderHistory` is not on `KyberCommandDependencies` yet, so the dependency bag
   * is a variable rather than an inline literal: excess-property checking would turn the
   * unknown seam into a compile error, which is not valid RED evidence.
   */
  function makeHarness(lock: 'acquired' | 'timed-out', scheduled: 'on' | 'off' = 'off') {
    const store = new CanonStore(':memory:')
    writeSetting(store, SETTING_KEYS.folderImportScheduled, scheduled)
    const calls: FolderImportOptions[] = []
    let createStoreCalls = 0
    let released = false
    // Annotated with the declared dependency type so `outcome` keeps its literal union
    // instead of widening to `string`.
    const dependencies: KyberCommandDependencies = {
      write: () => {},
      writeError: () => {},
      createStore: () => {
        createStoreCalls += 1
        return store
      },
      acquireStoreRefreshLock: async () =>
        lock === 'acquired'
          ? {
              outcome: 'acquired',
              handle: {
                token: 't',
                release: async () => { released = true },
                verifyStillOwner: async () => true,
              },
            }
          : { outcome: 'timed-out' },
      importFolderHistory: async (_store: CanonStore, options: FolderImportOptions) => {
        calls.push(options)
      },
    }
    return { store, calls, dependencies, released: () => released, createStoreCalls: () => createStoreCalls }
  }

  /** Run one of the two spellings and report the outcome without asserting on it. */
  async function runImport(
    command: string,
    args: string[],
    lock: 'acquired' | 'timed-out' = 'acquired',
    scheduled: 'on' | 'off' = 'off',
  ): Promise<{
    exitCode: number | undefined
    usageError: CommanderError | undefined
    createStoreCalls: number
    calls: FolderImportOptions[]
    scheduledAfter: string
    released: boolean
  }> {
    const harness = makeHarness(lock, scheduled)
    const program = new Command()
    program.exitOverride()
    registerKyberCommands(program, harness.dependencies)

    process.exitCode = undefined
    let usageError: CommanderError | undefined
    try {
      await program.parseAsync(['node', 'kyberdash', 'dash', command, ...args])
    } catch (error) {
      if (error instanceof CommanderError) usageError = error
      else throw error
    }
    const exitCode = process.exitCode
    process.exitCode = savedExitCode
    // Read the setting before closing: the assertion is about what the command left in the
    // store, so it has to be read from the same store the command was handed.
    const scheduledAfter = readSetting(harness.store, SETTING_KEYS.folderImportScheduled)
    harness.store.close()
    return {
      exitCode: exitCode as number | undefined,
      usageError,
      createStoreCalls: harness.createStoreCalls(),
      calls: harness.calls,
      scheduledAfter,
      released: harness.released(),
    }
  }

  // 52 weeks is the ceiling a re-ingest window has ever had (MAX_CLEAN_REINGEST_WEEKS), so
  // 53 is out of range; 0, 1.5, and 'abc' are not whole weeks at all.
  it.each([
    ['0', 'zero weeks'],
    ['53', 'more than a year'],
    ['1.5', 'fractional weeks'],
    ['abc', 'a non-numeric window'],
  ])('exits 2 for --weeks %s (%s) before the store is opened', async (value) => {
    const result = await runImport('import-history', ['--weeks', value])

    expect(result.usageError?.exitCode).toBe(2)
    expect(result.createStoreCalls).toBe(0)
    expect(result.calls).toEqual([])
  })

  it('exits 3 and imports nothing when another operation holds the refresh lock', async () => {
    const result = await runImport('import-history', [], 'timed-out')

    // The distinct code is the contract: the tray and the job host cannot tell which
    // command they launched, so "already running" has to mean the same thing for all three.
    expect(result.exitCode).toBe(REFRESH_BUSY_EXIT_CODE)
    expect(REFRESH_BUSY_EXIT_CODE).toBe(3)
    expect(result.calls).toEqual([])
    expect(result.createStoreCalls).toBe(0)
  })

  it('imports with a default window of one week, exits 0, and releases the lock', async () => {
    const result = await runImport('import-history', [])

    expect(result.exitCode ?? 0).toBe(0)
    expect(result.calls).toHaveLength(1)
    expect(result.calls[0]!.weeks ?? DEFAULT_FOLDER_IMPORT_WEEKS).toBe(DEFAULT_FOLDER_IMPORT_WEEKS)
    expect(result.calls[0]!.trigger).toBe('cli')
    expect(result.released).toBe(true)
  })

  it('passes --weeks 4 and --harness pi through to the import', async () => {
    const result = await runImport('import-history', ['--weeks', '4', '--harness', 'pi'])

    expect(result.exitCode ?? 0).toBe(0)
    expect(result.calls).toHaveLength(1)
    expect(result.calls[0]!.weeks).toBe(4)
    expect(result.calls[0]!.harnesses).toEqual(['pi'])
  })

  // "Import this once" must not become "keep importing": the operator asked for a run, not
  // for a change to how every later scheduled refresh behaves.
  it('never changes the folder_import.scheduled setting', async () => {
    for (const scheduled of ['on', 'off'] as const) {
      const result = await runImport('import-history', ['--weeks', '2'], 'acquired', scheduled)

      expect(result.exitCode ?? 0).toBe(0)
      expect(result.calls).toHaveLength(1)
      expect(result.scheduledAfter).toBe(scheduled)
    }
  })

  // The setting gates scheduled and automatic refreshes only. An explicit import is a user
  // opt-in, so it has to work with the switch off — otherwise the one command that could
  // turn it on would be the only command that cannot use it.
  it('imports while folder_import.scheduled is off, whatever the trigger', async () => {
    const result = await runImport('import-history', ['--weeks', '2'], 'acquired', 'off')

    expect(result.calls).toHaveLength(1)
    expect(result.calls[0]!.trigger).toBe('cli')
    expect(result.scheduledAfter).toBe('off')
  })

  it('behaves identically under the `import` alias', async () => {
    const aliased = await runImport('import', ['--weeks', '2'])
    const canonical = await runImport('import-history', ['--weeks', '2'])

    expect(aliased.exitCode ?? 0).toBe(0)
    expect(aliased.calls).toEqual(canonical.calls)
    expect(aliased.calls[0]!.weeks).toBe(2)
    expect(aliased.scheduledAfter).toBe(canonical.scheduledAfter)
  })
})
