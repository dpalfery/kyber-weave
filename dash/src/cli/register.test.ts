import { existsSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { Command, CommanderError } from 'commander'
import { afterEach, describe, expect, it } from 'vitest'

import { registerKyberCommands } from './register.js'

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
