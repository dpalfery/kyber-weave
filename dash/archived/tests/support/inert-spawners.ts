// Inert dashboard seams for tests that boot the web dashboard in-process.
//
// Failure this prevents: runWebDashboard with no program, jobSpawner or receiverSpawner gives the
// hosted JobHost the default spawner, whose first tick starts `dash refresh` as a real child. Under
// vitest the spawn guard refuses it and fails the test that caused it. Every seam here is a
// recorder that starts nothing, and the receiver prober never touches the network. Spread
// inertDashboardSeams() into the runWebDashboard options instead of starting a real child.
import type { JobChild, JobSpawner } from '../../src/jobs/host.js'
import type { ReceiverProber } from '../../src/jobs/receiver-host.js'

export type RecordedSpawn = { readonly program: string; readonly args: readonly string[] }

/** A spawner that records each request and starts nothing; its child exits cleanly at once. */
export type InertSpawner = JobSpawner & { readonly calls: readonly RecordedSpawn[] }

/** An injected program is taken verbatim, so the default spawn-target resolution never runs. */
export const INERT_PROGRAM = 'kyberdash-test-inert'

export function createInertSpawner(): InertSpawner {
  const calls: RecordedSpawn[] = []
  return {
    calls,
    spawn(program: string, args: readonly string[]): JobChild {
      calls.push({ program, args: [...args] })
      return {
        exited: Promise.resolve({ code: 0, stderr: '' }),
        kill: () => {},
      }
    },
  }
}

export const inertReceiverProber: ReceiverProber = {
  probe: () => Promise.resolve('refused'),
}

export type InertDashboardSeams = {
  readonly program: string
  readonly jobSpawner: InertSpawner
  readonly receiverSpawner: InertSpawner
  readonly receiverProber: ReceiverProber
}

/** Fresh inert seams for one runWebDashboard call. Spread them into the options. */
export function inertDashboardSeams(): InertDashboardSeams {
  return {
    program: INERT_PROGRAM,
    jobSpawner: createInertSpawner(),
    receiverSpawner: createInertSpawner(),
    receiverProber: inertReceiverProber,
  }
}
