// Spawn guard rules (storm guard, T1a). The guard is the one chokepoint every child
// process must pass through, so that a test can never re-run the current entry script.
//
// Failure this prevents: dash/src/jobs/host.ts nodeSpawner re-ran the current entry
// script under vitest. Each run re-entered the suite, which spawned again. The storm
// reached 536 node processes and crashed three developer Macs. Only a guard that refuses
// the spawn before it happens stops that, so these tests drive the refusal through the
// real factories (createNodeSpawner, DetachedLauncher, nodeVerifier) with a recording
// spawnFn. No test here starts a process. The one real spawner check lives in
// dash/tests/setup/spawn-guard.test.ts and is refused before it starts anything.
//
// The setup file (dash/tests/setup/spawn-guard-setup.ts) runs under deny mode for every
// test file and fails any test that caused a hit. Deliberate hits are cleared here, the
// way the network guard test clears its deliberate hit.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { DetachedLauncher } from '../engine/ensure.js'
import { nodeVerifier } from '../install/node-deps.js'
import { createNodeSpawner } from '../jobs/host.js'
import {
  SpawnRefusedError,
  allowRealSpawns,
  clearSpawnGuardHits,
  guardedSpawn,
  spawnGuardHits,
} from './spawn-guard.js'

const ENV_KEYS = ['KYBERDASH_SPAWN_GUARD', 'KYBERDASH_SPAWN_ROLE', 'KYBERDASH_SPAWN_DEPTH'] as const

type RecordedSpawn = {
  readonly program: string
  readonly args: readonly string[]
  readonly options: { readonly env?: NodeJS.ProcessEnv }
}

/** A spawnFn that records each call and returns nothing. It never starts a process. */
function recordingSpawn() {
  const calls: RecordedSpawn[] = []
  const spawnFn = vi.fn((program: string, args: readonly string[], options: RecordedSpawn['options']) => {
    calls.push({ program, args, options })
    return undefined
  })
  return { spawnFn, calls }
}

let savedEnv: Record<string, string | undefined> = {}

beforeEach(() => {
  savedEnv = {}
  for (const key of ENV_KEYS) savedEnv[key] = process.env[key]
  process.env.KYBERDASH_SPAWN_GUARD = 'deny'
  delete process.env.KYBERDASH_SPAWN_ROLE
  delete process.env.KYBERDASH_SPAWN_DEPTH
})

// Restores only. Hits are cleared by each test that causes one on purpose, so the setup
// file's afterEach still sees any accidental hit from a test that did not expect one.
afterEach(() => {
  for (const key of ENV_KEYS) {
    const value = savedEnv[key]
    if (value === undefined) delete process.env[key]
    else process.env[key] = value
  }
})

describe('deny mode refuses a spawn before it starts', () => {
  it('throws SpawnRefusedError, records one hit, and never calls spawnFn under deny', () => {
    const { spawnFn } = recordingSpawn()

    expect(() => guardedSpawn(spawnFn, { kind: 'engine', program: '/bin/echo', args: ['x'] })).toThrow(SpawnRefusedError)

    expect(spawnFn).not.toHaveBeenCalled()
    expect(spawnGuardHits()).toHaveLength(1)
    clearSpawnGuardHits()
  })

  it('calls spawnFn once after allowRealSpawns is given a reason', () => {
    const { spawnFn } = recordingSpawn()
    allowRealSpawns('storm-guard unit test: one permitted spawn')

    guardedSpawn(spawnFn, { kind: 'engine', program: '/bin/echo', args: ['x'] })

    expect(spawnFn).toHaveBeenCalledTimes(1)
  })

  it('is back in deny mode in the next test because the opt-in is cleared after afterEach', () => {
    const { spawnFn } = recordingSpawn()

    expect(() => guardedSpawn(spawnFn, { kind: 'engine', program: '/bin/echo', args: ['x'] })).toThrow(SpawnRefusedError)

    expect(spawnFn).not.toHaveBeenCalled()
    clearSpawnGuardHits()
  })
})

describe('the child environment carries the role and the depth', () => {
  it('stamps KYBERDASH_SPAWN_ROLE=<kind> and KYBERDASH_SPAWN_DEPTH=1 from depth 0', () => {
    const { spawnFn, calls } = recordingSpawn()
    allowRealSpawns('storm-guard unit test: env from depth 0')

    guardedSpawn(spawnFn, { kind: 'receiver', program: '/bin/echo', args: [] })

    expect(calls).toHaveLength(1)
    expect(calls[0]?.options.env?.KYBERDASH_SPAWN_ROLE).toBe('receiver')
    expect(calls[0]?.options.env?.KYBERDASH_SPAWN_DEPTH).toBe('1')
  })

  it('stamps KYBERDASH_SPAWN_DEPTH=2 when the calling process is at depth 1', () => {
    const { spawnFn, calls } = recordingSpawn()
    allowRealSpawns('storm-guard unit test: env from depth 1')
    process.env.KYBERDASH_SPAWN_DEPTH = '1'

    guardedSpawn(spawnFn, { kind: 'job', program: '/bin/echo', args: [] })

    expect(calls).toHaveLength(1)
    expect(calls[0]?.options.env?.KYBERDASH_SPAWN_ROLE).toBe('job')
    expect(calls[0]?.options.env?.KYBERDASH_SPAWN_DEPTH).toBe('2')
  })
})

describe('the calling process role and depth decide what may be spawned', () => {
  it('refuses every kind when the calling role is job', () => {
    process.env.KYBERDASH_SPAWN_ROLE = 'job'
    allowRealSpawns('storm-guard unit test: job role must not spawn')

    for (const kind of ['engine', 'job', 'receiver'] as const) {
      const { spawnFn } = recordingSpawn()
      expect(() => guardedSpawn(spawnFn, { kind, program: '/bin/echo', args: [] }), kind).toThrow(SpawnRefusedError)
      expect(spawnFn, kind).not.toHaveBeenCalled()
    }
    clearSpawnGuardHits()
  })

  it('refuses every kind when the calling role is receiver', () => {
    process.env.KYBERDASH_SPAWN_ROLE = 'receiver'
    allowRealSpawns('storm-guard unit test: receiver role must not spawn')

    for (const kind of ['engine', 'job', 'receiver'] as const) {
      const { spawnFn } = recordingSpawn()
      expect(() => guardedSpawn(spawnFn, { kind, program: '/bin/echo', args: [] }), kind).toThrow(SpawnRefusedError)
      expect(spawnFn, kind).not.toHaveBeenCalled()
    }
    clearSpawnGuardHits()
  })

  it('refuses an engine spawn from an engine but allows a job spawn from an engine', () => {
    process.env.KYBERDASH_SPAWN_ROLE = 'engine'
    allowRealSpawns('storm-guard unit test: engine role spawns only jobs')

    const refused = recordingSpawn()
    expect(() => guardedSpawn(refused.spawnFn, { kind: 'engine', program: '/bin/echo', args: [] })).toThrow(SpawnRefusedError)
    expect(refused.spawnFn).not.toHaveBeenCalled()
    clearSpawnGuardHits()

    const allowed = recordingSpawn()
    guardedSpawn(allowed.spawnFn, { kind: 'job', program: '/bin/echo', args: [] })
    expect(allowed.spawnFn).toHaveBeenCalledTimes(1)
  })

  it('refuses every kind when the calling process is already at depth 2', () => {
    process.env.KYBERDASH_SPAWN_DEPTH = '2'
    allowRealSpawns('storm-guard unit test: depth 2 must not spawn')

    const { spawnFn } = recordingSpawn()
    expect(() => guardedSpawn(spawnFn, { kind: 'job', program: '/bin/echo', args: [] })).toThrow(SpawnRefusedError)
    expect(spawnFn).not.toHaveBeenCalled()
    clearSpawnGuardHits()
  })
})

describe('the real factories route through the guard', () => {
  it('createNodeSpawner refuses a spawn under deny and never calls its spawnFn', () => {
    const { spawnFn } = recordingSpawn()

    expect(() => createNodeSpawner(spawnFn).spawn(process.execPath, ['-e', ''])).toThrow(SpawnRefusedError)

    expect(spawnFn).not.toHaveBeenCalled()
    clearSpawnGuardHits()
  })

  it('DetachedLauncher refuses a start under deny and never calls its spawnFn', () => {
    const { spawnFn } = recordingSpawn()
    const launcher = new DetachedLauncher(process.execPath, ['web'], spawnFn)

    expect(() => launcher.start(['--no-open'])).toThrow(SpawnRefusedError)

    expect(spawnFn).not.toHaveBeenCalled()
    clearSpawnGuardHits()
  })

  it('the install node-deps verifier refuses codesign and spctl under deny without starting them', async () => {
    await expect(nodeVerifier.codesign('/nonexistent/KyberDash.app')).rejects.toThrow(SpawnRefusedError)
    await expect(nodeVerifier.spctl('/nonexistent/KyberDash.app')).rejects.toThrow(SpawnRefusedError)

    expect(spawnGuardHits()).toHaveLength(2)
    clearSpawnGuardHits()
  })
})
