import { existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { Command, CommanderError } from 'commander'
import { afterEach, describe, expect, it } from 'vitest'

import type { RefreshRunRow } from '../canon/refresh-run.js'
import { CanonStore } from '../canon/store.js'
import { STORE_LOCK_FILE, readLockHolder } from '../refresh/lock.js'
import { SETTING_KEYS, writeSetting } from '../settings/shared-settings.js'
import type { KyberCommandDependencies } from './register.js'
import { REFRESH_BUSY_EXIT_CODE, registerKyberCommands } from './register.js'

const temporaryRoots: string[] = []

afterEach(() => {
  for (const root of temporaryRoots.splice(0)) rmSync(root, { recursive: true, force: true })
})

describe('kyber CLI registration', () => {
  it('registers local-provider refresh beneath the top-level dash command', () => {
    const program = new Command()
    registerKyberCommands(program)

    const dash = program.commands.find((command) => command.name() === 'dash')
    const refresh = dash?.commands.find((command) => command.name() === 'refresh')
    expect(refresh).toBeDefined()
    const kyber = program.commands.find((command) => command.name() === 'kyber')
    expect(kyber?.commands.find((command) => command.name() === 'dash')).toBeUndefined()

    expect(refresh?.options.some((option) => option.long === '--history-weeks')).toBe(true)
    expect(refresh?.options.some((option) => option.long === '--provider')).toBe(false)
    expect(refresh?.description()).toMatch(/harness-source/i)
  })

  it('registers cursor-hook and routes its stdin through the OTLP delivery and output seams', async () => {
    const stdin = [
      JSON.stringify({
        type: 'agent_turn.started',
        sessionId: 'cursor-cli-session',
        turnId: 'cursor-cli-turn',
        timestamp: '2026-09-04T20:12:00.000Z',
      }),
      JSON.stringify({
        type: 'agent_turn.completed',
        sessionId: 'cursor-cli-session',
        turnId: 'cursor-cli-turn',
        timestamp: '2026-09-04T20:12:01.000Z',
      }),
    ].join('\n')
    const written: string[] = []
    const posted: Record<string, unknown>[] = []
    const program = new Command()
    program.exitOverride()
    registerKyberCommands(program, {
      readStdin: async () => stdin,
      write: (line) => written.push(line),
      postCursorHookOtlp: async (payload) => { posted.push(payload) },
    })

    expect(program.commands.find((command) => command.name() === 'kyber')
      ?.commands.find((command) => command.name() === 'cursor-hook')).toBeDefined()

    await program.parseAsync(['node', 'codeburn', 'kyber', 'cursor-hook'])

    expect(posted).toHaveLength(1)
    expect(written).toHaveLength(1)
    expect(posted).toEqual([JSON.parse(written[0]!)])
  })

  it('registers antigravity-statusline and routes its stdin through the recorder seam', async () => {
    // Synthetic agy statusLine payload (C3): no real conversation id, path, or account data.
    const payload = {
      conversation_id: 'synthetic-conversation-0001',
      session_id: 'synthetic-session-0001',
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
    const recorded: unknown[] = []
    const written: string[] = []
    const program = new Command()
    program.exitOverride()
    // Deliberately a variable rather than an inline literal: `recordAntigravityStatusLine`
    // is the seam this task asks for and is not on `KyberCommandDependencies` yet, and
    // excess-property checking would turn that into a compile error — which is not valid
    // RED evidence.
    const dependencies = {
      readStdin: async () => JSON.stringify(payload),
      write: (line: string) => { written.push(line) },
      recordAntigravityStatusLine: async (input: unknown) => { recorded.push(input); return true },
    }
    registerKyberCommands(program, dependencies)

    expect(program.commands.find((command) => command.name() === 'kyber')
      ?.commands.find((command) => command.name() === 'antigravity-statusline')).toBeDefined()

    await program.parseAsync(['node', 'kyberdash', 'kyber', 'antigravity-statusline'])

    expect(recorded).toEqual([payload])
    expect(written).toEqual([])
  })
})

describe('dash refresh option validation', () => {
  async function parseRefresh(args: string[]): Promise<CommanderError> {
    const program = new Command()
    program.exitOverride()
    registerKyberCommands(program)
    try {
      await program.parseAsync(['node', 'codeburn', 'dash', 'refresh', ...args])
      throw new Error('expected usage error')
    } catch (error) {
      if (error instanceof CommanderError) return error
      throw error
    }
  }

  it.each([
    ['0'],
    ['-1'],
    ['1.5'],
    ['abc'],
  ])('exits 2 for --history-weeks %s before creating the database', async (value) => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-refresh-usage-'))
    temporaryRoots.push(root)
    const db = join(root, 'must-not-exist', 'canon.db')
    const error = await parseRefresh(['--history-weeks', value, '--db', db])
    expect(error.exitCode).toBe(2)
    expect(existsSync(db)).toBe(false)
  })

  it('exits 2 when duplicate --history-weeks values conflict', async () => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-refresh-usage-'))
    temporaryRoots.push(root)
    const db = join(root, 'must-not-exist', 'canon.db')
    const error = await parseRefresh(['--history-weeks', '2', '--history-weeks', '6', '--db', db])
    expect(error.exitCode).toBe(2)
    expect(existsSync(db)).toBe(false)
  })

  it('closes the store when refresh throws', async () => {
    let closed = 0
    const program = new Command()
    program.exitOverride()
    registerKyberCommands(program, {
      createStore: () => ({ close: () => { closed += 1 } }) as never,
      refreshHarnessSources: async () => {
        throw new Error('refresh boom')
      },
    })
    const root = mkdtempSync(join(tmpdir(), 'kyber-refresh-close-'))
    temporaryRoots.push(root)
    await expect(program.parseAsync([
      'node', 'codeburn', 'dash', 'refresh', '--db', join(root, 'canon.db'),
    ])).rejects.toThrow('refresh boom')
    expect(closed).toBe(1)
  })
})

describe('dash refresh: one refresh at a time (R10.3, R10.4)', () => {
  /** Run `dash refresh` with the lock reporting the given outcome, capturing its output. */
  async function refreshWith(
    outcome: 'acquired' | 'timed-out',
  ): Promise<{ exitCode: number | undefined; stderr: string[]; refreshed: boolean }> {
    const stderr: string[] = []
    let refreshed = false
    let released = false
    const program = new Command()
    program.exitOverride()
    registerKyberCommands(program, {
      write: () => {},
      writeError: (line) => stderr.push(line),
      createStore: () => new CanonStore(':memory:'),
      acquireStoreRefreshLock: async () =>
        outcome === 'acquired'
          ? {
              outcome: 'acquired',
              handle: {
                token: 't',
                release: async () => { released = true },
                verifyStillOwner: async () => true,
              },
            }
          : { outcome: 'timed-out' },
      refreshHarnessSources: async () => {
        refreshed = true
        return {
          historyWeeks: 2,
          commandStartedAt: new Date().toISOString(),
          rows: [],
          derived: { sessions: 0, runs: 0, executions: 0, rollups: 0 },
          failedJobs: 0,
          derivationFailed: false,
          exitCode: 0,
        }
      },
    })

    const previous = process.exitCode
    process.exitCode = undefined
    await program.parseAsync(['node', 'kyberdash', 'dash', 'refresh'])
    const exitCode = process.exitCode
    process.exitCode = previous
    if (outcome === 'acquired') expect(released).toBe(true)
    return { exitCode: exitCode as number | undefined, stderr, refreshed }
  }

  it('refreshes and releases the lock when it is free', async () => {
    const result = await refreshWith('acquired')
    expect(result.refreshed).toBe(true)
    expect(result.exitCode ?? 0).toBe(0)
  })

  it('exits 3 without writing when another refresh holds the lock', async () => {
    const result = await refreshWith('timed-out')
    // The distinct code is the contract: a caller must be able to tell "already running"
    // from "the refresh ran and failed" (1) without reading the message.
    expect(result.exitCode).toBe(REFRESH_BUSY_EXIT_CODE)
    expect(REFRESH_BUSY_EXIT_CODE).toBe(3)
    expect(result.refreshed).toBe(false)
  })

  it('names the holding process so the operator knows what to wait for', async () => {
    const directory = mkdtempSync(join(tmpdir(), 'kyber-refresh-lock-'))
    temporaryRoots.push(directory)
    writeFileSync(
      join(directory, STORE_LOCK_FILE),
      JSON.stringify({ pid: 4242, token: 'tok', at: Date.parse('2026-09-19T08:00:00.000Z') }),
    )
    const holder = readLockHolder(directory)
    expect(holder).toEqual({ pid: 4242, since: '2026-09-19T08:00:00.000Z' })
  })

  it('reads no holder from a corrupt lock body, rather than inventing one', () => {
    const directory = mkdtempSync(join(tmpdir(), 'kyber-refresh-lock-'))
    temporaryRoots.push(directory)
    writeFileSync(join(directory, STORE_LOCK_FILE), 'not json')
    expect(readLockHolder(directory)).toBeNull()
  })

  it('reads no holder when there is no lock file at all', () => {
    const directory = mkdtempSync(join(tmpdir(), 'kyber-refresh-lock-'))
    temporaryRoots.push(directory)
    expect(readLockHolder(directory)).toBeNull()
  })

  // The folder-import gate, exercised through the CLI. `folderSourcesAllowed` already
  // encodes the rule in the refresh module and is unit-tested there; what is only
  // observable from here is that `dash refresh` *consults* it, and what a caller (the
  // tray, a scheduler) gets to see when the answer is no: no ingest, no refresh_run row
  // claiming one happened, a line naming `settings`, and a success exit code — a skipped
  // run is not a failed one, and a tray that reads non-zero as "the dashboard is
  // broken" would be wrong.
  async function refreshBehindGate(
    args: string[],
    scheduled: 'on' | 'off',
  ): Promise<{
    exitCode: number | undefined
    output: string[]
    refreshed: number
    maintenancePasses: number
    refreshRuns: RefreshRunRow[]
    lockReleased: boolean
  }> {
    const stdout: string[] = []
    const stderr: string[] = []
    const store = new CanonStore(':memory:')
    // The setting is written before the command runs, so the command reads exactly
    // what an operator would have configured through the tray or the web surface.
    writeSetting(store, SETTING_KEYS.folderImportScheduled, scheduled)
    // The command closes the store before returning, so the run log is snapshotted on
    // the way out: "no refresh_run row was written" is only checkable while the handle
    // is open, and the close is what proves the command reached its cleanup.
    let refreshRuns: RefreshRunRow[] = []
    const closeStore = store.close.bind(store)
    store.close = () => {
      refreshRuns = store.listRefreshRuns()
      closeStore()
    }

    let refreshed = 0
    let maintenancePasses = 0
    // The lock is observed exactly as the refresh tests above do: release flips a flag.
    let lockReleased = false
    // A variable, not an inline literal: `runMaintenancePass` is not yet a member of
    // KyberCommandDependencies, and excess-property checking would turn the intended
    // injection into a compile error rather than the runtime failure this test wants.
    const dependencies: KyberCommandDependencies & {
      runMaintenancePass?: (store: CanonStore, now: Date) => Promise<void>
    } = {
      write: (line) => stdout.push(line),
      writeError: (line) => stderr.push(line),
      createStore: () => store,
      acquireStoreRefreshLock: async () => ({
        outcome: 'acquired' as const,
        handle: {
          token: 't',
          release: async () => { lockReleased = true },
          verifyStillOwner: async () => true,
        },
      }),
      refreshHarnessSources: async () => {
        refreshed += 1
        return {
          historyWeeks: 2,
          commandStartedAt: new Date().toISOString(),
          rows: [],
          derived: { sessions: 0, runs: 0, executions: 0, rollups: 0 },
          failedJobs: 0,
          derivationFailed: false,
          exitCode: 0,
        }
      },
      runMaintenancePass: async () => { maintenancePasses += 1 },
    }

    const program = new Command()
    program.exitOverride()
    registerKyberCommands(program, dependencies)

    const previous = process.exitCode
    process.exitCode = undefined
    await program.parseAsync(['node', 'kyberdash', 'dash', 'refresh', ...args])
    const exitCode = process.exitCode
    process.exitCode = previous
    return { exitCode: exitCode as number | undefined, output: [...stdout, ...stderr], refreshed, maintenancePasses, refreshRuns, lockReleased }
  }

  it('imports folder sources for a manual `dash refresh` while scheduled folder import is off', async () => {
    // A person typed the command. Gating this on the setting would leave the operator
    // with no way to read their own history back, which is the setting's whole point:
    // it gates *unattended* runs, not explicit requests.
    const result = await refreshBehindGate([], 'off')
    expect(result.refreshed).toBe(1)
    expect(result.exitCode ?? 0).toBe(0)
  })

  it.each(['scheduled', 'tray', 'web'] as const)(
    'skips the %s ingest and runs maintenance while folder_import.scheduled is off',
    async (trigger) => {
      const result = await refreshBehindGate(['--trigger', trigger], 'off')

      expect(result.refreshed).toBe(0)
      // Retention and projection still have to happen on the cadence, or the derived
      // views age forever while the source read is withheld.
      expect(result.maintenancePasses).toBe(1)
      // A refresh row says "sources were read". Writing one for a run that read none
      // would make the audit trail — and the coverage window read from it — claim work
      // that never happened.
      expect(result.refreshRuns).toEqual([])
      // The message has to be actionable: the operator needs to know the run was
      // skipped deliberately and which setting turns it back on.
      const notices = result.output.filter((line) => /skip/i.test(line) && /settings/i.test(line))
      expect(notices.length).toBeGreaterThanOrEqual(1)
      // Skipping is not failing: the tray's cadence treats non-zero as a broken refresh.
      expect(result.exitCode ?? 0).toBe(0)
      // A skipped run still held the single-writer lock, so it must still hand it back:
      // leaking it here would wedge every later refresh and import with a permanent exit 3.
      expect(result.lockReleased).toBe(true)
    },
  )

  it.each(['scheduled', 'tray', 'web'] as const)(
    'imports folder sources for the %s trigger once folder_import.scheduled is on',
    async (trigger) => {
      const result = await refreshBehindGate(['--trigger', trigger], 'on')

      expect(result.refreshed).toBe(1)
      expect(result.maintenancePasses).toBe(0)
      expect(result.exitCode ?? 0).toBe(0)
    },
  )
})

// Issue #312, plan T6 RED: `dash clean` wipes a harness scope (or all) and
// re-ingests it, through the central `cleanDatabase` module. The CLI is a thin
// caller: scope flags, `--yes` confirmation, the refresh lock (exit 3 when
// held, like refresh), and exit codes. The subcommand does not exist yet —
// every test below fails until T6 lands it.
// A store that cannot be opened is a real failure mode - EACCES on the state directory,
// a canon.db that is not a database - and it is one that happens BEFORE any work. The
// refresh lock is already held by then, so a createStore outside the try would skip the
// finally, leak the lock, and leave every later refresh, clean and import reading the
// holder as a permanent exit 3 with nothing to explain it.
describe('dash import-history: the lock is released even when the store cannot be opened', () => {
  it('exits 1 and releases the lock when createStore throws', async () => {
    const stderr: string[] = []
    let released = false
    let imported = false
    const program = new Command()
    program.exitOverride()
    registerKyberCommands(program, {
      write: () => {},
      writeError: (line) => stderr.push(line),
      createStore: () => {
        throw new Error('EACCES: permission denied, open canon.db')
      },
      acquireStoreRefreshLock: async () => ({
        outcome: 'acquired' as const,
        handle: { token: 't', release: async () => { released = true }, verifyStillOwner: async () => true },
      }),
      importFolderHistory: async () => { imported = true },
    })

    const previous = process.exitCode
    process.exitCode = undefined
    await program.parseAsync(['node', 'kyberdash', 'dash', 'import-history'])
    const exitCode = process.exitCode
    process.exitCode = previous

    // The failure is reported, not thrown: the operator gets exit 1 and a line.
    expect(exitCode).toBe(1)
    expect(stderr.join('\n')).toMatch(/import failed/i)
    expect(imported).toBe(false)
    // The contract that matters: the lock goes back, so the next run is not wedged.
    expect(released).toBe(true)
  })

  it('releases the lock on dash refresh and dash clean too', async () => {
    // Same pattern, same consequence: both commands hold the lock before they open the
    // store, so both have to release it on every path including this one.
    for (const argv of [
      ['dash', 'refresh'],
      ['dash', 'clean', '--all', '--yes', '--no-reingest'],
    ]) {
      let released = false
      const program = new Command()
      program.exitOverride()
      registerKyberCommands(program, {
        write: () => {},
        writeError: () => {},
        createStore: () => {
          throw new Error('EACCES: permission denied, open canon.db')
        },
        acquireStoreRefreshLock: async () => ({
          outcome: 'acquired' as const,
          handle: { token: 't', release: async () => { released = true }, verifyStillOwner: async () => true },
        }),
      })

      // `refresh` lets the failure propagate; `clean` reports it and exits 1. Only the
      // lock release is the same contract for both, which is all this asserts.
      await program.parseAsync(['node', 'kyberdash', ...argv]).catch(() => undefined)
      expect(released, `${argv.join(' ')} must release the lock`).toBe(true)
    }
  })
})

describe('dash clean option validation', () => {
  async function parseClean(args: string[]): Promise<CommanderError> {
    const program = new Command()
    program.exitOverride()
    registerKyberCommands(program)
    try {
      await program.parseAsync(['node', 'kyberdash', 'dash', 'clean', ...args])
      throw new Error('expected usage error')
    } catch (error) {
      if (error instanceof CommanderError) return error
      throw error
    }
  }

  it('registers clean beneath the top-level dash command with --all/--harness/--yes', () => {
    const program = new Command()
    registerKyberCommands(program)

    const dash = program.commands.find((command) => command.name() === 'dash')
    const clean = dash?.commands.find((command) => command.name() === 'clean')
    expect(clean).toBeDefined()
    expect(clean?.options.some((option) => option.long === '--all')).toBe(true)
    expect(clean?.options.some((option) => option.long === '--harness')).toBe(true)
    expect(clean?.options.some((option) => option.long === '--yes')).toBe(true)
    expect(clean?.options.some((option) => option.long === '--reingest-weeks')).toBe(true)
    expect(clean?.options.some((option) => option.long === '--no-reingest')).toBe(true)
    expect(clean?.description()).toMatch(/clean/i)
  })

  it.each([
    ['--all', '--harness', 'pi'],
    [],
  ])('exits 2 for conflicting or missing scope %s before creating the database', async (...args) => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-clean-usage-'))
    temporaryRoots.push(root)
    const db = join(root, 'must-not-exist', 'canon.db')
    const error = await parseClean([...args, '--yes', '--db', db])
    expect(error.exitCode).toBe(2)
    expect(existsSync(db)).toBe(false)
  })

  it('exits 2 without --yes before creating the database', async () => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-clean-usage-'))
    temporaryRoots.push(root)
    const db = join(root, 'must-not-exist', 'canon.db')
    const error = await parseClean(['--all', '--db', db])
    expect(error.exitCode).toBe(2)
    expect(existsSync(db)).toBe(false)
  })

  it.each([
    ['0'],
    ['-1'],
    ['1.5'],
    ['abc'],
    ['53'],
    ['9007199254740991'],
  ])('exits 2 for --reingest-weeks %s before creating the database', async (value) => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-clean-usage-'))
    temporaryRoots.push(root)
    const db = join(root, 'must-not-exist', 'canon.db')
    const error = await parseClean(['--all', '--yes', '--reingest-weeks', value, '--db', db])
    expect(error.exitCode).toBe(2)
    expect(existsSync(db)).toBe(false)
  })
})

describe('dash clean: one clean at a time', () => {
  /** Run `dash clean` with the lock reporting the given outcome, capturing its output. */
  async function cleanWith(
    outcome: 'acquired' | 'timed-out',
  ): Promise<{ exitCode: number | undefined; stderr: string[]; cleaned: boolean }> {
    const stderr: string[] = []
    let cleaned = false
    let released = false
    const program = new Command()
    program.exitOverride()
    registerKyberCommands(program, {
      write: () => {},
      writeError: (line) => stderr.push(line),
      createStore: () => new CanonStore(':memory:'),
      acquireStoreRefreshLock: async () =>
        outcome === 'acquired'
          ? {
              outcome: 'acquired',
              handle: {
                token: 't',
                release: async () => { released = true },
                verifyStillOwner: async () => true,
              },
            }
          : { outcome: 'timed-out' },
      cleanDatabase: (async () => {
        cleaned = true
        return { harnesses: ['*'], wipe: { harnesses: ['*'], records: 0, provenance: 0, checkpoints: 0 }, reingested: false, historyWeeks: null }
      }) as never,
    })

    const previous = process.exitCode
    process.exitCode = undefined
    await program.parseAsync(['node', 'kyberdash', 'dash', 'clean', '--all', '--yes', '--no-reingest'])
    const exitCode = process.exitCode
    process.exitCode = previous
    if (outcome === 'acquired') expect(released).toBe(true)
    return { exitCode: exitCode as number | undefined, stderr, cleaned }
  }

  it('cleans and releases the lock when it is free', async () => {
    const result = await cleanWith('acquired')
    expect(result.cleaned).toBe(true)
    expect(result.exitCode ?? 0).toBe(0)
  })

  it('exits 3 without writing when another operation holds the lock', async () => {
    const result = await cleanWith('timed-out')
    expect(result.exitCode).toBe(REFRESH_BUSY_EXIT_CODE)
    expect(result.cleaned).toBe(false)
  })

  /**
   * Run `dash clean --all --yes` with the given extra flags, capturing the options the
   * fake `cleanDatabase` was handed. The fake is the only seam that can show *whether*
   * a wipe was followed by a folder read, and it must stay a fake: a real re-ingest
   * would read the developer's own harness folders.
   */
  async function cleanCapturingOptions(
    args: string[],
  ): Promise<{ exitCode: number | undefined; options: { reingestWeeks?: number | null } | undefined }> {
    let options: { reingestWeeks?: number | null } | undefined
    let cleaned = false
    const program = new Command()
    program.exitOverride()
    registerKyberCommands(program, {
      write: () => {},
      writeError: () => {},
      createStore: () => new CanonStore(':memory:'),
      acquireStoreRefreshLock: async () => ({
        outcome: 'acquired' as const,
        handle: { token: 't', release: async () => {}, verifyStillOwner: async () => true },
      }),
      cleanDatabase: (async (_store: unknown, requested: { reingestWeeks?: number | null }) => {
        cleaned = true
        options = requested
        return { harnesses: ['*'], wipe: { harnesses: ['*'], records: 0, provenance: 0, checkpoints: 0 }, reingested: false, historyWeeks: null }
      }) as never,
    })

    const previous = process.exitCode
    process.exitCode = undefined
    await program.parseAsync(['node', 'kyberdash', 'dash', 'clean', '--all', '--yes', ...args])
    const exitCode = process.exitCode
    process.exitCode = previous
    expect(cleaned).toBe(true)
    return { exitCode: exitCode as number | undefined, options }
  }

  it('does not re-ingest folder history after a plain `--all --yes` wipe', async () => {
    // The re-ingest window is now an explicit opt-in, so the CLI states the decision
    // itself (null) rather than leaving it absent: an absent key would hand the
    // decision back to cleanDatabase's historical one-week default, which is exactly
    // the unrequested folder read being removed.
    const result = await cleanCapturingOptions([])

    expect(result.options).toHaveProperty('reingestWeeks', null)
    expect(result.exitCode ?? 0).toBe(0)
  })

  it('re-ingests the window asked for by --reingest-weeks', async () => {
    // An operator who does want the history back after a wipe states how much of it,
    // and that number is what reaches the wipe.
    const result = await cleanCapturingOptions(['--reingest-weeks', '2'])

    expect(result.options?.reingestWeeks).toBe(2)
    expect(result.exitCode ?? 0).toBe(0)
  })

  it('still accepts --no-reingest, and it imports nothing', async () => {
    // The escape hatch stays valid even though it is no longer the default: scripts
    // that spell it out must keep working rather than start failing on an unknown flag.
    const result = await cleanCapturingOptions(['--no-reingest'])

    expect(result.options).toHaveProperty('reingestWeeks', null)
    expect(result.exitCode ?? 0).toBe(0)
  })
})
