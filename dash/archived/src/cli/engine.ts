// `kyberdash engine ensure --if-running` (plan D12, T5).
//
// Contract for the callers (`kyber-weave update`, Squad):
//   stdout  exactly one line:
//             engine: not-running                  no engine holds the endpoint; nothing was started
//             engine: attached <version>           a live engine already runs the CLI's version
//             engine: replaced <old> -> <new>      a live engine of another version was stopped and restarted
//             engine: started <version>            (rare) an engine was absent by the time ensure ran
//   stderr  `engine: unresponsive <reason>` (the endpoint holder never answers; nothing was started)
//           `engine: failed <reason>` (replacing or starting failed)
//   exit    0 not-running, attached, replaced (and started); 1 unresponsive or failed;
//           2 usage (`--if-running` missing).
// It never starts an engine on its own: with none running it reports `not-running` and returns.

import { ensureEngine, type EngineEnsureOptions, type EngineEnsureResult } from '../engine/ensure.js'
import { queryEngine } from '../engine/instance.js'
import { formatEnsureOutcome, type EngineProbeResult } from '../engine/outcome.js'

export type EngineEnsureCommandOptions = {
  ifRunning: boolean
  stateDir: string
  /** Version of this CLI; a live engine of any other version is replaced. */
  version: string
  /** The executable an engine is started from (this CLI itself). */
  program: string
  programArgs?: readonly string[]
  /** Seams for tests: nothing below is needed in production. */
  query?: (stateDir: string) => Promise<EngineProbeResult>
  ensure?: (options: EngineEnsureOptions) => Promise<EngineEnsureResult>
  startEngine?: EngineEnsureOptions['startEngine']
  shutdown?: EngineEnsureOptions['shutdown']
  sleep?: EngineEnsureOptions['sleep']
}

export type EngineEnsureCommandResult = { exitCode: number; stdout: string; stderr: string }

export const ENGINE_USAGE_EXIT = 2

export async function runEngineEnsure(options: EngineEnsureCommandOptions): Promise<EngineEnsureCommandResult> {
  if (!options.ifRunning) {
    return {
      exitCode: ENGINE_USAGE_EXIT,
      stdout: '',
      stderr: 'kyberdash engine ensure: only `--if-running` is supported (it never starts an engine on its own).\n',
    }
  }
  const query = options.query ?? queryEngine
  const live = await query(options.stateDir)
  if (live === null) return { exitCode: 0, stdout: 'engine: not-running\n', stderr: '' }
  if (live === 'unresponsive') {
    return {
      exitCode: 1,
      stdout: '',
      stderr: 'engine: unresponsive the engine endpoint is held by a process that does not answer; stop it and retry\n',
    }
  }
  const ensure = options.ensure ?? ensureEngine
  const result = await ensure({
    stateDir: options.stateDir,
    version: options.version,
    program: options.program,
    ...(options.programArgs !== undefined ? { programArgs: options.programArgs } : {}),
    query: async (stateDir) => query(stateDir),
    ...(options.startEngine !== undefined ? { startEngine: options.startEngine } : {}),
    ...(options.shutdown !== undefined ? { shutdown: options.shutdown } : {}),
    ...(options.sleep !== undefined ? { sleep: options.sleep } : {}),
  })
  const outcome = formatEnsureOutcome(result)
  return outcome.stream === 'stdout'
    ? { exitCode: 0, stdout: `${outcome.line}\n`, stderr: '' }
    : { exitCode: 1, stdout: '', stderr: `${outcome.line}\n` }
}
