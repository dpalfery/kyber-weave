// F10 regression: repeated web start/close must not retain signal handlers.
// `runWebDashboard` removed its per-start SIGINT handler on server close but
// kept the `exit` and `SIGTERM` registrations, so every in-process restart
// leaked listeners toward MaxListenersExceededWarning.

import type { Server } from 'node:http'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import type { ChildResult, JobSpawner } from '../jobs/host.js'
import type { ReceiverProbe } from '../jobs/receiver-host.js'
import { KyberBridge } from '../server/bridge.js'
import { runWebDashboard } from './web.js'

class FakeChild {
  readonly exited: Promise<ChildResult>
  constructor() {
    this.exited = Promise.resolve({ code: 0, stderr: '' })
  }
  kill(): void {
    /* nothing to kill: no child was ever asked for */
  }
}

class FakeSpawner implements JobSpawner {
  spawn(_program: string, _args: readonly string[]): FakeChild {
    return new FakeChild()
  }
}

const cleanups: Array<() => Promise<void> | void> = []
afterEach(async () => {
  for (const cleanup of cleanups.splice(0).reverse()) await cleanup()
})

function closeServer(server: Server): Promise<void> {
  return new Promise((resolve) => {
    server.close(() => resolve())
  })
}

describe('F10 web dashboard lifecycle', () => {
  it('returns SIGINT/SIGTERM/exit listener counts to baseline across repeated start/close cycles', async () => {
    const stateDir = mkdtempSync(join(tmpdir(), 'kyber-web-lifecycle-'))
    cleanups.push(() => {
      rmSync(stateDir, { recursive: true, force: true })
    })

    async function startAndClose(): Promise<void> {
      const store = new CanonStore(':memory:')
      const bridge = new KyberBridge({ store, reopenCheckIntervalMs: 0 })
      const server = await runWebDashboard({
        port: 0,
        open: false,
        singleInstance: true,
        writeStdout: () => {},
        kyberBridge: bridge,
        store,
        stateDir,
        program: 'kyberdash-test',
        jobSpawner: new FakeSpawner(),
        receiverSpawner: new FakeSpawner(),
        receiverProber: { probe: async (): Promise<ReceiverProbe> => 'refused' },
      })
      await closeServer(server)
      bridge.close()
      store.close()
    }

    // One warmup cycle first: the refresh lock arms its process-once SIGINT /
    // SIGTERM cleanup handlers the first time any start takes a lock, and
    // those belong to the process lifetime, not to one server start — they
    // must not move after the warmup. The assertion below pins exactly what
    // F10 owns: per-start registrations removed on every close.
    await startAndClose()
    const before = {
      SIGINT: process.listenerCount('SIGINT'),
      SIGTERM: process.listenerCount('SIGTERM'),
      exit: process.listenerCount('exit'),
    }

    await startAndClose()
    await startAndClose()

    expect({
      SIGINT: process.listenerCount('SIGINT'),
      SIGTERM: process.listenerCount('SIGTERM'),
      exit: process.listenerCount('exit'),
    }).toEqual(before)
  })
})
