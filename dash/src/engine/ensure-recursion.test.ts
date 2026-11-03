// Storm guard (T2): a job or engine child may not start an engine.
//
// Failure this prevents: a child process that was started to do one job reached ensureEngine
// and started an engine of its own, which ran its own jobs, which started more children. The
// guard refuses before the launcher is called, so no engine process starts from a child.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { ensureEngine } from './ensure.js'

const ROLE_VARIABLES = ['KYBERDASH_SPAWN_ROLE', 'KYBERDASH_SPAWN_DEPTH'] as const
const priorEnvironment = new Map<string, string | undefined>()

beforeEach(() => {
  for (const name of ROLE_VARIABLES) {
    priorEnvironment.set(name, process.env[name])
    delete process.env[name]
  }
})

afterEach(() => {
  for (const name of ROLE_VARIABLES) {
    const prior = priorEnvironment.get(name)
    if (prior === undefined) delete process.env[name]
    else process.env[name] = prior
  }
  priorEnvironment.clear()
})

const noSleep = (): Promise<void> => Promise.resolve()

describe('ensureEngine: a child process may not start an engine', () => {
  it('under role engine, refuses to start an engine and never calls the launcher', async () => {
    process.env.KYBERDASH_SPAWN_ROLE = 'engine'
    // No live engine is found, so ensureEngine must start one; the launcher seam is what it
    // would call. startTimeoutMs is zero so a refusal that starts nothing ends at once.
    const startEngine = vi.fn()

    const result = await ensureEngine({
      stateDir: '/s', version: '1.0.0', program: '/bin/kd',
      query: async () => null, startEngine, sleep: noSleep, startTimeoutMs: 0,
    })

    expect(startEngine, 'an engine child called the launcher').not.toHaveBeenCalled()
    expect(result).toMatchObject({
      outcome: 'failed',
      reason: expect.stringMatching(/engine child may not start an engine/),
    })
  })

  it('under role job, refuses to start an engine and never calls the launcher', async () => {
    process.env.KYBERDASH_SPAWN_ROLE = 'job'
    const startEngine = vi.fn()

    const result = await ensureEngine({
      stateDir: '/s', version: '1.0.0', program: '/bin/kd',
      query: async () => null, startEngine, sleep: noSleep, startTimeoutMs: 0,
    })

    expect(startEngine, 'a job child called the launcher').not.toHaveBeenCalled()
    expect(result).toMatchObject({
      outcome: 'failed',
      reason: expect.stringMatching(/may not start an engine|never spawns/),
    })
  })
})
