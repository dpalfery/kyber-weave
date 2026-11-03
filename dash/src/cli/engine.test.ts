import { describe, expect, it, vi } from 'vitest'
import { Command } from 'commander'

import type { EngineEnsureResult } from '../engine/ensure.js'
import type { EngineRecord } from '../engine/instance.js'
import { runEngineEnsure } from './engine.js'
import { registerKyberCommands } from './register.js'

const record = (over: Partial<EngineRecord> = {}): EngineRecord => ({
  pid: 4242,
  exe: '/usr/local/bin/kyberdash',
  version: '2.1.0',
  startedAt: '2026-10-26T00:00:00.000Z',
  url: 'http://127.0.0.1:4747',
  ...over,
})

const base = { stateDir: '/temp-state', version: '2.1.1', program: '/bin/kd', ifRunning: true }
const noSleep = (): Promise<void> => Promise.resolve()

describe('kyberdash engine ensure --if-running', () => {
  it('starts nothing and prints `engine: not-running` (exit 0) when no engine holds the endpoint', async () => {
    const ensure = vi.fn()
    const startEngine = vi.fn()
    const result = await runEngineEnsure({ ...base, query: async () => null, ensure, startEngine })
    expect(result).toEqual({ exitCode: 0, stdout: 'engine: not-running\n', stderr: '' })
    expect(ensure).not.toHaveBeenCalled()
    expect(startEngine).not.toHaveBeenCalled()
  })

  it('prints `engine: attached <version>` for a live engine of the CLI version', async () => {
    const result = await runEngineEnsure({
      ...base,
      query: async () => record({ version: '2.1.1' }),
      startEngine: vi.fn(),
      sleep: noSleep,
    })
    expect(result).toEqual({ exitCode: 0, stdout: 'engine: attached 2.1.1\n', stderr: '' })
  })

  it('replaces a live engine of another version: `engine: replaced <old> -> <new>` (exit 0)', async () => {
    let stopped = false
    let started = false
    const startEngine = vi.fn(() => { started = true })
    const shutdown = vi.fn(async () => { stopped = true; return true })
    const query = async (): Promise<EngineRecord | null> => {
      if (started) return record({ version: '2.1.1' })
      return stopped ? null : record({ version: '2.1.0' })
    }
    const result = await runEngineEnsure({ ...base, query, shutdown, startEngine, sleep: noSleep })
    expect(result).toEqual({ exitCode: 0, stdout: 'engine: replaced 2.1.0 -> 2.1.1\n', stderr: '' })
    expect(shutdown).toHaveBeenCalledTimes(1)
    expect(startEngine).toHaveBeenCalledWith('/bin/kd', ['web', '--no-open'])
  })

  it('exits 1 with the reason when the endpoint holder does not answer, and starts nothing', async () => {
    const startEngine = vi.fn()
    const ensure = vi.fn()
    const result = await runEngineEnsure({ ...base, query: async () => 'unresponsive', startEngine, ensure })
    expect(result.exitCode).toBe(1)
    expect(result.stdout).toBe('')
    expect(result.stderr).toMatch(/^engine: unresponsive .+\n$/)
    expect(startEngine).not.toHaveBeenCalled()
    expect(ensure).not.toHaveBeenCalled()
  })

  it('exits 1 with the reason when replacing fails', async () => {
    const failed: EngineEnsureResult = { outcome: 'failed', reason: 'engine 2.1.0 (pid 4242) did not accept a shutdown request' }
    const result = await runEngineEnsure({ ...base, query: async () => record(), ensure: async () => failed })
    expect(result).toEqual({
      exitCode: 1,
      stdout: '',
      stderr: 'engine: failed engine 2.1.0 (pid 4242) did not accept a shutdown request\n',
    })
  })

  it('reports a start that raced the check as `engine: started <version>` (exit 0)', async () => {
    const started: EngineEnsureResult = { outcome: 'started', record: record({ version: '2.1.1' }) }
    const result = await runEngineEnsure({ ...base, query: async () => record(), ensure: async () => started })
    expect(result).toEqual({ exitCode: 0, stdout: 'engine: started 2.1.1\n', stderr: '' })
  })

  it('is a usage error (exit 2) without --if-running', async () => {
    const query = vi.fn()
    const result = await runEngineEnsure({ ...base, ifRunning: false, query })
    expect(result.exitCode).toBe(2)
    expect(result.stderr).toMatch(/--if-running/)
    expect(query).not.toHaveBeenCalled()
  })
})

describe('engine ensure registration', () => {
  it('registers `engine ensure` with --if-running', () => {
    const program = new Command()
    registerKyberCommands(program)
    const ensure = program.commands.find((c) => c.name() === 'engine')?.commands.find((c) => c.name() === 'ensure')
    expect(ensure).toBeDefined()
    expect(ensure?.options.some((option) => option.long === '--if-running')).toBe(true)
  })
})
