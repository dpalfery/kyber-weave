import { describe, expect, it, vi } from 'vitest'

import { allowRealSpawns } from '../../tests/setup/spawn-guard.js'
import { ensureEngine } from './ensure.js'
import type { EngineRecord } from './instance.js'

const record = (over: Partial<EngineRecord> = {}): EngineRecord => ({
  pid: 4242,
  exe: '/usr/local/bin/kyberdash',
  version: '1.0.0',
  startedAt: '2026-10-16T00:00:00.000Z',
  url: 'http://127.0.0.1:4747',
  ...over,
})

const noSleep = (): Promise<void> => Promise.resolve()

describe('ensureEngine', () => {
  it('attaches to a live engine of the same version and starts nothing', async () => {
    const startEngine = vi.fn()

    const result = await ensureEngine({
      stateDir: '/s', version: '1.0.0', program: '/bin/kd',
      query: async () => record(), startEngine, sleep: noSleep,
    })

    expect(result).toEqual({ outcome: 'attached', record: record() })
    expect(startEngine).not.toHaveBeenCalled()
  })

  it('starts the engine from the caller\'s own executable when none runs, then attaches to it', async () => {
    let started = false
    const startEngine = vi.fn(() => { started = true })

    const result = await ensureEngine({
      stateDir: '/s', version: '1.0.0', program: '/Users/me/.local/bin/kyberdash',
      query: async () => (started ? record() : null), startEngine, sleep: noSleep,
    })

    expect(startEngine).toHaveBeenCalledWith('/Users/me/.local/bin/kyberdash', ['web', '--no-open'])
    expect(result.outcome).toBe('started')
  })

  it('passes loader arguments through when the CLI runs from a source checkout', async () => {
    let started = false
    const startEngine = vi.fn(() => { started = true })

    await ensureEngine({
      stateDir: '/s', version: '1.0.0', program: 'node', programArgs: ['--import', 'tsx', 'src/launcher.ts'],
      query: async () => (started ? record() : null), startEngine, sleep: noSleep,
    })

    expect(startEngine).toHaveBeenCalledWith('node', ['--import', 'tsx', 'src/launcher.ts', 'web', '--no-open'])
  })

  it('replaces an engine of another version: stop it, wait for the endpoint to free, start the new one', async () => {
    const events: string[] = []
    let phase: 'old' | 'gone' | 'new' = 'old'
    const result = await ensureEngine({
      stateDir: '/s', version: '2.0.0', program: '/bin/kd',
      query: async () => {
        if (phase === 'old') return record({ version: '1.0.0' })
        return phase === 'gone' ? null : record({ version: '2.0.0', pid: 777 })
      },
      shutdown: async () => { events.push('shutdown'); phase = 'gone'; return true },
      startEngine: () => { events.push('start'); phase = 'new' },
      sleep: noSleep,
    })

    expect(events).toEqual(['shutdown', 'start'])
    expect(result).toMatchObject({ outcome: 'replaced', previous: { version: '1.0.0' }, record: { version: '2.0.0', pid: 777 } })
  })

  it('does not start a second engine when the old one refuses to stop', async () => {
    const startEngine = vi.fn()

    const result = await ensureEngine({
      stateDir: '/s', version: '2.0.0', program: '/bin/kd', stopTimeoutMs: 300,
      query: async () => record({ version: '1.0.0' }),
      shutdown: async () => true, startEngine, sleep: noSleep,
    })

    expect(result.outcome).toBe('failed')
    expect(startEngine).not.toHaveBeenCalled()
  })

  it('does not start an engine over a holder that will not answer', async () => {
    const startEngine = vi.fn()

    const result = await ensureEngine({
      stateDir: '/s', version: '1.0.0', program: '/bin/kd',
      query: async () => 'unresponsive', startEngine, sleep: noSleep,
    })

    expect(result.outcome).toBe('failed')
    expect(startEngine).not.toHaveBeenCalled()
  })

  it('waits for a starting engine to publish its url before attaching', async () => {
    let calls = 0
    const result = await ensureEngine({
      stateDir: '/s', version: '1.0.0', program: '/bin/kd',
      query: async () => (++calls < 3 ? record({ url: undefined }) : record()), sleep: noSleep,
    })

    expect(result).toMatchObject({ outcome: 'attached', record: { url: 'http://127.0.0.1:4747' } })
  })

  it('fails when a started engine never serves', async () => {
    const result = await ensureEngine({
      stateDir: '/s', version: '1.0.0', program: '/bin/kd', startTimeoutMs: 300,
      query: async () => null, startEngine: () => {}, sleep: noSleep,
    })

    expect(result.outcome).toBe('failed')
  })

  it('reports a program that cannot be started at once, with the cause', async () => {
    // The program path does not exist, so the OS refuses with ENOENT and no process ever starts.
    allowRealSpawns('ENOENT path: spawn of a nonexistent program starts no process')
    const result = await ensureEngine({
      stateDir: '/s', version: '1.0.0', program: '/nonexistent/kyberdash',
      // The spawn error is an event-loop turn away; a real sleep yields, the test's no-op one does not.
      query: async () => null, sleep: () => new Promise((resolve) => { setImmediate(resolve) }),
    })

    expect(result.outcome).toBe('failed')
    expect(result).toMatchObject({ reason: expect.stringContaining('ENOENT') })
  })
})
