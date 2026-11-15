// Shared Factory (Droid) replay world backed by `kyberdash e2e-seed --world factory-replay`.

import { join } from 'node:path'

import { USER_CANON, createTempRoot, runE2eSeed } from './harness-collection-world.js'

export { USER_CANON }

export const FACTORY_CONTENT_OFF_SESSION_ID = 'factory-replay-content-off'
export const FACTORY_CONTENT_ON_SESSION_ID = 'factory-replay-content-on'
export const FACTORY_REPLAY_MODEL = 'gpt-5-codex'
export const FACTORY_REPLAY_TOOL = 'Read'
export const FACTORY_REPLAY_PROMPT = 'the recorded factory prompt that must not travel without consent'

export type FactoryReplayWorld = {
  readonly root: string
  readonly dbPath: string
}

export async function refreshFactoryTempCanon(root = createTempRoot()): Promise<FactoryReplayWorld> {
  const dbPath = join(root, 'canon.db')
  if (dbPath === USER_CANON) {
    throw new Error('refusing to use the user canon.db as a Factory replay write target')
  }
  runE2eSeed('factory-replay', dbPath)
  return { root, dbPath }
}
