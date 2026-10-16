// Ensure-or-attach: the single way any caller gets "the engine".
//
// The CLI and the tray both go through here. If a live engine of the same version holds the
// endpoint, the caller attaches to it. If one of another version holds it, it is asked to
// stop and replaced, because an engine that outlives an upgrade keeps running old parsing
// and old receiver code. If none holds it, the caller's own executable starts one.

import { spawn as nodeSpawn } from 'child_process'

import { queryEngine, requestEngineShutdown, type EngineRecord } from './instance.js'

const POLL_MS = 100

export type EngineEnsureResult =
  | { outcome: 'attached'; record: EngineRecord }
  | { outcome: 'started'; record: EngineRecord }
  | { outcome: 'replaced'; record: EngineRecord; previous: EngineRecord }
  | { outcome: 'failed'; reason: string }

export type EngineEnsureOptions = {
  stateDir: string
  /** Version of the calling executable; a live engine of any other version is replaced. */
  version: string
  /** The executable to start an engine from: the same one the tray launches. */
  program: string
  /** Arguments between the program and `web --no-open` (a source checkout needs its loader flags). */
  programArgs?: readonly string[]
  startTimeoutMs?: number
  stopTimeoutMs?: number
  /** Seams for tests. */
  startEngine?: (program: string, args: readonly string[]) => void
  query?: typeof queryEngine
  shutdown?: typeof requestEngineShutdown
  sleep?: (ms: number) => Promise<void>
}

function defaultStart(program: string, args: readonly string[]): void {
  // `program` is the caller's own executable (process.execPath or the tray's resolved binary), never
  // user input, and no shell is involved: arguments go straight to execve.
  // nosemgrep: javascript.lang.security.detect-child-process.detect-child-process
  const child = nodeSpawn(program, [...args], { detached: true, stdio: 'ignore' })
  child.on('error', () => { /* surfaced as a start timeout */ })
  child.unref()
}

const delay = (ms: number): Promise<void> => new Promise((resolve) => { setTimeout(resolve, ms) })

async function waitFor<T>(
  probe: () => Promise<T | null>,
  timeoutMs: number,
  sleep: (ms: number) => Promise<void>,
): Promise<T | null> {
  let waited = 0
  for (;;) {
    const value = await probe()
    if (value !== null) return value
    if (waited >= timeoutMs) return null
    await sleep(POLL_MS)
    waited += POLL_MS
  }
}

export async function ensureEngine(options: EngineEnsureOptions): Promise<EngineEnsureResult> {
  const query = options.query ?? queryEngine
  const shutdown = options.shutdown ?? requestEngineShutdown
  const sleep = options.sleep ?? delay
  const startTimeoutMs = options.startTimeoutMs ?? 15_000
  const stopTimeoutMs = options.stopTimeoutMs ?? 10_000

  const live = await query(options.stateDir)
  if (live === 'unresponsive') {
    return { outcome: 'failed', reason: 'the engine endpoint is held by a process that does not answer; stop it and retry' }
  }
  if (live !== null && live.version === options.version) {
    // Attach only once it is serving: a record without a url is an engine still starting.
    const ready = live.url !== undefined
      ? live
      : await waitFor(async () => {
        const again = await query(options.stateDir)
        return again !== null && again !== 'unresponsive' && again.url !== undefined ? again : null
      }, startTimeoutMs, sleep)
    return ready === null
      ? { outcome: 'failed', reason: 'the engine holds its endpoint but never began serving' }
      : { outcome: 'attached', record: ready }
  }

  let previous: EngineRecord | null = null
  if (live !== null) {
    previous = live
    if (!(await shutdown(options.stateDir))) {
      return { outcome: 'failed', reason: `engine ${live.version} (pid ${live.pid}) did not accept a shutdown request` }
    }
    const gone = await waitFor(async () => ((await query(options.stateDir)) === null ? true : null), stopTimeoutMs, sleep)
    if (gone === null) return { outcome: 'failed', reason: `engine ${live.version} (pid ${live.pid}) did not stop within ${stopTimeoutMs}ms` }
  }

  ;(options.startEngine ?? defaultStart)(options.program, [...(options.programArgs ?? []), 'web', '--no-open'])
  const started = await waitFor(async () => {
    const record = await query(options.stateDir)
    return record !== null && record !== 'unresponsive' && record.url !== undefined ? record : null
  }, startTimeoutMs, sleep)
  if (started === null) return { outcome: 'failed', reason: 'the engine did not start serving in time' }
  return previous === null ? { outcome: 'started', record: started } : { outcome: 'replaced', record: started, previous }
}
