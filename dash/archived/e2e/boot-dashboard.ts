// The one way an e2e boot starts a dashboard: in this process, inert, and isolated.
//
// Failure this prevents: the boots called runWebDashboard with no program, jobSpawner or
// receiverSpawner. The hosted JobHost's first tick then spawned `dash refresh` children that
// re-booted the dashboard, and the storm reached 536 node processes. Here every spawner is a
// recorder that starts nothing, the receiver prober never touches the network, and the state
// dir lives under the world's own temp root (never ~/.kyberdash).

import { mkdirSync, mkdtempSync } from 'node:fs'
import type { Server } from 'node:http'
import { join } from 'node:path'

import { runWebDashboard } from '../src/cli/web.js'
import type { JobChild, JobHost, JobSpawner } from '../src/jobs/host.js'
import type { ReceiverProber } from '../src/jobs/receiver-host.js'

export type RecordedSpawn = { readonly program: string; readonly args: readonly string[] }

/** A spawner that records each request and starts nothing; its child exits cleanly at once. */
export type InertSpawner = JobSpawner & { readonly calls: readonly RecordedSpawn[] }

/**
 * The report filters (`period`, `provider`, `project`, `exclude`) are accepted for the boots'
 * existing call shape but are not forwarded: `runWebDashboard` no longer takes them, because the
 * dashboard reads its filters per request.
 */
export type BootIsolatedOptions = {
  /** The world's temp root; the state dir is created beneath it. */
  readonly root: string
  readonly canonDbPath: string
  /** 0 binds an ephemeral loopback port. */
  readonly port: number
  readonly period: string
  readonly provider: string
  readonly project: readonly string[]
  readonly exclude: readonly string[]
  readonly maintenance?: () => Promise<void>
}

export type IsolatedDashboard = {
  readonly server: Server
  readonly port: number
  readonly stateDir: string
  readonly jobSpawner: InertSpawner
  readonly receiverSpawner: InertSpawner
  /** Settled once the hosted JobHost has finished its first tick. */
  readonly firstTick: Promise<void>
  closedJobHostCount(): number
  close(): Promise<void>
}

function createInertSpawner(): InertSpawner {
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

/**
 * An injected program is taken verbatim, which keeps the default spawn-target resolution (it
 * would refuse a boot script) out of the picture. The spawners never start it.
 */
const INERT_PROGRAM = 'kyberdash-e2e-inert'

const inertReceiverProber: ReceiverProber = {
  probe: () => Promise.resolve('refused'),
}

export async function bootIsolatedDashboard(options: BootIsolatedOptions): Promise<IsolatedDashboard> {
  mkdirSync(options.root, { recursive: true })
  const stateDir = mkdtempSync(join(options.root, 'state-'))
  const jobSpawner = createInertSpawner()
  const receiverSpawner = createInertSpawner()

  const closing: Promise<void>[] = []
  let closedJobHosts = 0
  const closeJobHost = (jobHost: JobHost): Promise<void> => {
    closedJobHosts += 1
    const closed = jobHost.close()
    closing.push(closed)
    return closed
  }

  // The override lives only as long as the boot: a reused test worker must not see a path to a
  // temp DB that is about to be deleted.
  const hadCanonDb = Object.hasOwn(process.env, 'KYBER_CANON_DB')
  const previousCanonDb = process.env['KYBER_CANON_DB']
  const restoreCanonDb = (): void => {
    if (hadCanonDb) process.env['KYBER_CANON_DB'] = previousCanonDb
    else delete process.env['KYBER_CANON_DB']
  }
  process.env['KYBER_CANON_DB'] = options.canonDbPath

  let server: Server
  try {
    server = await runWebDashboard({
      stateDir,
      program: INERT_PROGRAM,
      jobSpawner,
      receiverSpawner,
      receiverProber: inertReceiverProber,
      closeJobHost,
      ...(options.maintenance === undefined ? {} : { maintenance: options.maintenance }),
      port: options.port,
      open: false,
      writeStdout: () => {},
    })
  } catch (err) {
    restoreCanonDb()
    throw err
  }

  const address = server.address()
  if (address === null || typeof address === 'string') {
    await new Promise<void>((resolve) => server.close(() => resolve()))
    restoreCanonDb()
    throw new Error('dashboard did not bind a TCP port')
  }

  return {
    server,
    port: address.port,
    stateDir,
    jobSpawner,
    receiverSpawner,
    // runWebDashboard resolves only after the hosted pair's first tick has run.
    firstTick: Promise.resolve(),
    closedJobHostCount: () => closedJobHosts,
    close: async () => {
      // Every step runs even when an earlier one fails, and the env var is restored whatever
      // happens; the first failure is rethrown afterwards.
      const serverClosed = new Promise<void>((resolve, reject) => {
        server.close((err) => (err === undefined ? resolve() : reject(err)))
        server.closeAllConnections()
      })
      try {
        const results = await Promise.allSettled([serverClosed])
        results.push(...(await Promise.allSettled(closing)))
        const failures = results
          .filter((result): result is PromiseRejectedResult => result.status === 'rejected')
          .map((result) => result.reason as unknown)
        if (failures.length === 1) throw failures[0]
        if (failures.length > 1) {
          throw new AggregateError(failures, `${failures.length} closes failed while stopping the isolated dashboard`)
        }
      } finally {
        restoreCanonDb()
      }
    },
  }
}
