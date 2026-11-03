// The jobs.lock lease: at most one process hosts background jobs for a state dir.
//
// Several `kyberdash web` servers (or a web server plus a leftover one) can share
// ~/.kyberdash. Without a single host each would run its own scheduled refresh and
// maintenance pass, so the lease makes the second one stand down. It reuses the
// refresh lock's PID-liveness protocol rather than inventing another: a SIGKILLed
// holder is reclaimed on the first poll, and a live one is never taken from.

import { acquireCacheRefreshLock } from '../refresh/lock.js'

export const JOBS_LOCK_FILE = 'jobs.lock'

export type JobsLease = { readonly release: () => Promise<void> }

/**
 * Takes the lease without waiting. `null` means someone else hosts the jobs (or the
 * lock is unusable): the caller must run nothing rather than queue behind the
 * holder, because a host that blocks here would stall every tick that follows.
 */
export async function acquireJobsLease(stateDir: string): Promise<JobsLease | null> {
  const result = await acquireCacheRefreshLock({
    directory: stateDir,
    lockFile: JOBS_LOCK_FILE,
    waitMs: 0,
  })
  if (result.outcome !== 'acquired') return null
  return { release: () => result.handle.release() }
}
