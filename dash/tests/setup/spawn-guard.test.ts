// Spawn-guard setup file rules (storm guard, T1a). Mirrors network-guard.test.ts.
//
// Failure this prevents: a test spawns a real child process (or re-runs the entry script)
// and the spawn goes unnoticed. The setup file runs in deny mode for every test file and
// turns any recorded hit into a failure in the test that caused it.
//
// The spawn-guard modules are imported inside each test, not at the top of the file. A
// missing module then fails only the test that needs it, and the real-spawner test below
// fails on its own reason. Those reasons are what this file is meant to pin.
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it, vi } from 'vitest'

import { nodeSpawner, type JobChild } from '../../src/jobs/host.js'

describe('spawn guard setup file', () => {
  it('starts every test in deny mode', () => {
    expect(process.env.KYBERDASH_SPAWN_GUARD).toBe('deny')
  })

  it('is registered in vitest setupFiles so every test file gets it', () => {
    const config = readFileSync(fileURLToPath(new URL('../../vitest.config.ts', import.meta.url)), 'utf8')
    const setupFiles = /setupFiles:\s*\[([\s\S]*?)\]/.exec(config)?.[1] ?? ''

    expect(setupFiles).toContain('spawn-guard-setup.ts')
  })

  it('fails the test that caused a guard hit, and the failure names the refused spawn', async () => {
    const { spawnGuardAfterEach } = await import('./spawn-guard-setup.js')
    const { SpawnRefusedError, guardedSpawn, spawnGuardHits } = await import('../../src/process/spawn-guard.js')
    const spawnFn = vi.fn()

    expect(() => guardedSpawn(spawnFn, { kind: 'engine', program: '/bin/echo', args: ['x'] })).toThrow(SpawnRefusedError)

    // The handler is the afterEach body: it must throw on a recorded hit and clear it, so
    // the hit is reported once, in the test that caused it.
    expect(() => spawnGuardAfterEach()).toThrow(/spawn guard/)
    expect(spawnGuardHits()).toHaveLength(0)
    expect(spawnFn).not.toHaveBeenCalled()
  })

  it('the real default spawner refuses under the setup deny mode and starts no process', async () => {
    // Failure this prevents: nodeSpawner re-ran the entry script under vitest. This is the
    // only test that touches a real spawner. Under deny it must throw before anything starts.
    let child: JobChild | undefined
    let thrown: unknown
    try {
      child = nodeSpawner.spawn(process.execPath, ['-e', ''])
    } catch (error) {
      thrown = error
    } finally {
      child?.kill()
    }

    expect((thrown as Error | undefined)?.name, 'the real spawner must refuse under deny').toBe('SpawnRefusedError')

    const { clearSpawnGuardHits, spawnGuardHits } = await import('../../src/process/spawn-guard.js')
    expect(spawnGuardHits()).toHaveLength(1)
    clearSpawnGuardHits()
  })
})
