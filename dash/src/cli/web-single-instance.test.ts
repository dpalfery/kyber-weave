// One engine per user: `kyberdash web` started with `singleInstance` refuses to become a
// second engine over the same state dir, and hands the endpoint back when it stops.
import type { Server } from 'node:http'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { acquireEngine, queryEngine, readEngineRecordFile } from '../engine/instance.js'
import type { ChildResult, JobSpawner } from '../jobs/host.js'
import type { ReceiverProbe } from '../jobs/receiver-host.js'
import { KyberBridge } from '../server/bridge.js'
import { EngineAlreadyRunningError, KYBERDASH_VERSION, runWebDashboard } from './web.js'

class IdleChild {
  readonly exited: Promise<ChildResult> = new Promise(() => {})
  kill(): void {}
}
const idleSpawner: JobSpawner = { spawn: () => new IdleChild() }

const cleanups: Array<() => Promise<void> | void> = []
afterEach(async () => {
  for (const cleanup of cleanups.splice(0).reverse()) await cleanup()
})

function startEngine(stateDir: string): Promise<Server> {
  const store = new CanonStore(':memory:')
  const bridge = new KyberBridge({ store, reopenCheckIntervalMs: 0 })
  return runWebDashboard({
    port: 0,
    open: false,
    singleInstance: true,
    writeStdout: () => {},
    kyberBridge: bridge,
    store,
    stateDir,
    program: 'kyberdash-test',
    jobSpawner: idleSpawner,
    receiverSpawner: idleSpawner,
    receiverProber: { probe: async (): Promise<ReceiverProbe> => 'refused' },
  })
}

function stateDirFor(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-engine-web-'))
  cleanups.push(() => { rmSync(dir, { recursive: true, force: true, maxRetries: 10, retryDelay: 50 }) })
  return dir
}

function closeServer(server: Server): Promise<void> {
  return new Promise((resolve) => { server.close(() => resolve()) })
}

describe('kyberdash web with one engine per user', () => {
  it('publishes the engine record with its pid, version and url once listening', async () => {
    const stateDir = stateDirFor()
    const server = await startEngine(stateDir)
    cleanups.push(() => closeServer(server))

    const live = await queryEngine(stateDir)

    expect(live).toMatchObject({ pid: process.pid, version: KYBERDASH_VERSION })
    expect((live as { url?: string }).url).toMatch(/^http:\/\/127\.0\.0\.1:\d+$/)
  })

  it('refuses to start a second engine over the same state dir and names the one running', async () => {
    const stateDir = stateDirFor()
    const first = await startEngine(stateDir)
    cleanups.push(() => closeServer(first))

    const second = startEngine(stateDir)

    await expect(second).rejects.toBeInstanceOf(EngineAlreadyRunningError)
    await expect(second).rejects.toMatchObject({ record: { pid: process.pid, version: KYBERDASH_VERSION } })
  })

  it('leaves the first engine serving after the second is refused', async () => {
    const stateDir = stateDirFor()
    const first = await startEngine(stateDir)
    cleanups.push(() => closeServer(first))

    await startEngine(stateDir).catch(() => {})

    expect(await queryEngine(stateDir)).toMatchObject({ pid: process.pid })
  })

  it('hands the endpoint back when the engine stops, so a new process can own it', async () => {
    const stateDir = stateDirFor()
    const first = await startEngine(stateDir)
    await closeServer(first)
    // The engine record is released from the server's close handler on a later turn.
    for (let i = 0; i < 80 && (await queryEngine(stateDir)) !== null; i++) await new Promise((r) => setTimeout(r, 25))

    expect(await queryEngine(stateDir)).toBeNull()
    expect(readEngineRecordFile(stateDir)).toBeNull()
    const next = await acquireEngine({ stateDir, version: KYBERDASH_VERSION })
    expect(next.kind).toBe('owner')
    if (next.kind === 'owner') await next.release()
  })

  it('does not enforce anything when singleInstance is off, so in-process suites keep working', async () => {
    const stateDir = stateDirFor()
    const store = new CanonStore(':memory:')
    const bridge = new KyberBridge({ store, reopenCheckIntervalMs: 0 })
    const server = await runWebDashboard({
      port: 0, open: false, writeStdout: () => {}, kyberBridge: bridge, store, stateDir,
      program: 'kyberdash-test', jobSpawner: idleSpawner, receiverSpawner: idleSpawner,
      receiverProber: { probe: async (): Promise<ReceiverProbe> => 'refused' },
    })
    cleanups.push(() => closeServer(server))

    expect(await queryEngine(stateDir)).toBeNull()
  })
})
