// How this process identifies itself to the engine verbs: the state directory, the CLI's own
// version, and the executable an engine would be started from. Loaded lazily (like `web.ts`'s
// own seams) so registering the commands pulls in neither `node:sea` nor the web server.

import { homedir } from 'node:os'
import { join } from 'node:path'

import { currentSpawnRuntime, isSpawnRefusal, resolveJobSpawnTarget } from './spawn-target.js'

export type EngineContext = {
  stateDir: string
  version: string
  program: string
  programArgs: readonly string[]
}

export async function engineContext(): Promise<EngineContext> {
  const { KYBERDASH_VERSION } = await import('./web.js')
  const { runningAsSea } = await import('../sea.js')
  const target = resolveJobSpawnTarget(currentSpawnRuntime(runningAsSea()))
  // Fail closed: an engine is never started from an entry that is not the CLI. The error
  // carries the resolver's reason and reaches the operator through the command's own failure path.
  if (isSpawnRefusal(target)) throw new Error(target.refused)
  return {
    stateDir: join(homedir(), '.kyberdash'),
    version: KYBERDASH_VERSION,
    program: target.program,
    programArgs: target.programArgs,
  }
}
