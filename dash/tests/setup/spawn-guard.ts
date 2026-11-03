// Test-side surface of the spawn guard. A test that really needs a bounded child calls
// allowRealSpawns(reason); the setup file ends the opt-in after the test.
export {
  allowRealSpawns,
  clearSpawnGuardHits,
  resetSpawnAllowance,
  spawnGuardHits,
} from '../../src/process/spawn-guard.js'
