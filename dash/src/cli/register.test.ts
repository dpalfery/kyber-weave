import { existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { Command, CommanderError } from 'commander'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { STORE_LOCK_FILE, readLockHolder } from '../refresh/lock.js'
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
})
