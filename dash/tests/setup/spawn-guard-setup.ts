// Vitest setup file (storm guard): puts every test file in spawn deny mode and fails the
// test that caused a refused spawn. Mirrors network-guard-setup.ts.
//
// A refusal throws inside the code under test, and a host that swallows errors would hide
// it, so every recorded hit also becomes a failure in the test that caused it.
import { afterEach, beforeEach } from 'vitest'

import { clearSpawnGuardHits, resetSpawnAllowance, spawnGuardHits } from './spawn-guard.js'

process.env.KYBERDASH_SPAWN_GUARD = 'deny'

/** The afterEach body: ends any opt-in, clears the hits, and throws if there were any. */
export function spawnGuardAfterEach(): void {
  const hits = [...spawnGuardHits()]
  clearSpawnGuardHits()
  resetSpawnAllowance()
  if (hits.length > 0) {
    throw new Error(`spawn guard: ${hits.length} spawn(s) refused: ${hits.join('; ')}. Inject spawnFn instead.`)
  }
}

beforeEach(() => {
  process.env.KYBERDASH_SPAWN_GUARD = 'deny'
})

afterEach(spawnGuardAfterEach)
