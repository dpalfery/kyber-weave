// How this process identifies itself to the engine verbs: the state directory, the CLI's own
// version, and the executable an engine would be started from. Loaded lazily (like `web.ts`'s
// own seams) so registering the commands pulls in neither `node:sea` nor the web server.

import { homedir } from 'node:os'
import { join } from 'node:path'

import { currentSpawnRuntime, resolveJobSpawnTarget } from './spawn-target.js'

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
  return {
    stateDir: join(homedir(), '.kyberdash'),
    version: KYBERDASH_VERSION,
    program: target.program,
    programArgs: target.programArgs,
  }
}
