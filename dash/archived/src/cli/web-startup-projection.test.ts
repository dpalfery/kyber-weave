// Engine start-up projection (D13, T8).
//
// A `kyberdash` upgrade can ship a newer detector over a store the previous build
// projected. The engine must project once on its own start, through the maintenance
// pass the job host already runs on its first tick, rather than leaving the old rows
// (OpenCode's measured-zero buckets among them) to be served until someone runs
// `kyber build`. These tests start the real `runWebDashboard` WITHOUT a maintenance
// seam, so the default pass is the code under test, over an in-memory store.
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import type { Server } from 'node:http'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { useAbsentLiveReceiver } from '../../tests/setup/live-receiver-stub.js'
import { CanonStore, DETECTOR_VERSION } from '../canon/store.js'
import { projectCanonicalStore } from '../canon/projection.js'
import type { CanonicalRecord } from '../canon/types.js'
import type { ChildResult, JobSpawner } from '../jobs/host.js'
import type { ReceiverProbe } from '../jobs/receiver-host.js'
import { KyberBridge } from '../server/bridge.js'
import { runWebDashboard } from './web.js'

useAbsentLiveReceiver()

/** A child that never exits on its own: the job host's refresh is not under test. */
class IdleSpawner implements JobSpawner {
  spawn(): { kill(): void; exited: Promise<ChildResult> } {
    let resolveExit!: (result: ChildResult) => void
    const exited = new Promise<ChildResult>((resolve) => {
      resolveExit = resolve
    })
    return { exited, kill: () => resolveExit({ code: null, stderr: '' }) }
  }
}

function record(spanId: string): CanonicalRecord {
  return {
    spanId,
    traceId: `trace-${spanId}`,
    parentSpanId: null,
    source: 'opencode:startup',
    harness: 'opencode',
    name: 'chat turn',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: new Date().toISOString(),
    durationMs: 100,
    status: 'ok',
    tokens: { freshInput: 10, cacheRead: 0, cacheCreation: 0, output: 4, reportedInput: 10, reportedOutput: 4 },
    content: { conversation_history: 'hello' },
    cost: { basis: 'unknown', status: 'no_rate' },
    raw: { attributes: {} },
  }
}

const cleanups: Array<() => Promise<void> | void> = []
afterEach(async () => {
  vi.restoreAllMocks()
  for (const cleanup of cleanups.splice(0).reverse()) await cleanup()
})

async function startOver(store: CanonStore): Promise<void> {
  const stateDir = mkdtempSync(join(tmpdir(), 'kyber-web-startup-'))
  const started = runWebDashboard({
    port: 0,
    open: false,
    writeStdout: () => {},
    kyberBridge: new KyberBridge({ store, reopenCheckIntervalMs: 0 }),
    store,
    stateDir,
    program: 'kyberdash-test',
    jobSpawner: new IdleSpawner(),
    receiverSpawner: new IdleSpawner(),
    receiverProber: { probe: async (): Promise<ReceiverProbe> => 'refused' },
  })
  cleanups.push(async () => {
    const server: Server | undefined = await started.catch(() => undefined)
    if (server?.listening === true) await new Promise<void>((resolve) => server.close(() => resolve()))
    rmSync(stateDir, { recursive: true, force: true, maxRetries: 10, retryDelay: 50 })
  })
}

async function waitFor(predicate: () => boolean, message: string, timeoutMs = 5_000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (predicate()) return
    await new Promise((resolve) => setTimeout(resolve, 10))
  }
  throw new Error(`timed out waiting for: ${message}`)
}

describe('an engine starting over a store with an older detector stamp', () => {
  it('projects once, bringing the stamp to this build', async () => {
    const store = new CanonStore(':memory:')
    cleanups.push(() => store.close())
    store.upsertMany([record('startup-1')])
    await projectCanonicalStore(store)
    store.setDetectorVersion(DETECTOR_VERSION - 1)
    expect(store.getDetectorVersion()).toBe(DETECTOR_VERSION - 1)

    await startOver(store)

    await waitFor(() => store.getDetectorVersion() === DETECTOR_VERSION, 'the start-up projection to stamp this build')
  })

  it('does not project again when the stamp is already current', async () => {
    const store = new CanonStore(':memory:')
    cleanups.push(() => store.close())
    store.upsertMany([record('startup-2')])
    await projectCanonicalStore(store)
    const reproject = vi.spyOn(CanonStore.prototype, 'upsertSession')

    await startOver(store)
    // Give the first maintenance tick time to run; a current store has nothing to do.
    await new Promise((resolve) => setTimeout(resolve, 300))

    expect(reproject).not.toHaveBeenCalled()
  })
})
