// The hosted-services registry publishes a pair only once it is actually running
// (issue #319 T12 rework, defect 2).
//
// A pair published before `jobHost.start()` resolves is a pair a second caller reuses by
// identity: a start that rejected leaves a refs=0 entry holding a jobs lease, looking
// perfectly shareable, and the next server over that state dir hands back hosts that were
// never started. So: publish after start, clean up on rejection, and prove it here by
// failing one start and then running a second server over the same state dir.
import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import type { AddressInfo } from 'node:net'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import type { ChildResult, JobSpawner } from '../jobs/host.js'
import { JOBS_LOCK_FILE } from '../jobs/lease.js'
import type { ReceiverProbe } from '../jobs/receiver-host.js'
import { KyberBridge } from '../server/bridge.js'
import { runWebDashboard } from './web.js'

const START_FAILURE = 'settings store unavailable'

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
  readonly calls: Array<{ program: string; args: readonly string[] }> = []
  spawn(program: string, args: readonly string[]): FakeChild {
    this.calls.push({ program, args })
    return new FakeChild()
  }
}

const cleanups: Array<() => Promise<void>> = []
afterEach(async () => {
  for (const cleanup of cleanups.splice(0).reverse()) await cleanup()
})

async function tempStateDir(): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), 'kyber-web-registry-'))
  cleanups.push(() => rm(dir, { recursive: true, force: true }))
  return dir
}

/** A real in-memory store whose settings reads throw, so `JobHost.start()` rejects. */
function storeThatFailsToStart(): CanonStore {
  const real = new CanonStore(':memory:')
  return new Proxy(real, {
    get(target, property, _receiver) {
      if (property === 'getMetadata') {
        return () => {
          throw new Error(START_FAILURE)
        }
      }
      const value = Reflect.get(target, property, target) as unknown
      return typeof value === 'function' ? value.bind(target) : value
    },
  })
}

function seamsFor(stateDir: string, store: CanonStore, jobSpawner: FakeSpawner): {
  store: CanonStore
  stateDir: string
  program: string
  jobSpawner: JobSpawner
  receiverSpawner: JobSpawner
  receiverProber: { probe: () => Promise<ReceiverProbe> }
} {
  return {
    store,
    stateDir,
    program: 'kyberdash-test',
    jobSpawner,
    receiverSpawner: new FakeSpawner(),
    receiverProber: { probe: async () => 'refused' as ReceiverProbe },
  }
}

describe('hosted services registry', () => {
  it('leaves no entry behind when a start rejects, and serves the next run normally', async () => {
    const stateDir = await tempStateDir()
    const failing = storeThatFailsToStart()
    const failingBridge = new KyberBridge({ store: failing, reopenCheckIntervalMs: 0 })
    const failingSpawner = new FakeSpawner()

    await expect(
      runWebDashboard({
        port: 0,
        open: false,
        writeStdout: () => {},
        kyberBridge: failingBridge,
        ...seamsFor(stateDir, failing, failingSpawner),
      }),
    ).rejects.toThrow(START_FAILURE)

    // Nothing published, and nothing held: a server.json would name a port nobody is on,
    // and a held jobs lock would keep the next process from ever hosting.
    expect(existsSync(join(stateDir, 'server.json'))).toBe(false)
    expect(existsSync(join(stateDir, JOBS_LOCK_FILE))).toBe(false)
    failing.close()

    // The same state dir, the same program, and a store that works: this must build and
    // start its own pair rather than reuse the one whose start threw.
    const store = new CanonStore(':memory:')
    const bridge = new KyberBridge({ store, reopenCheckIntervalMs: 0 })
    const jobSpawner = new FakeSpawner()
    const server = await runWebDashboard({
      port: 0,
      open: false,
      writeStdout: () => {},
      kyberBridge: bridge,
      ...seamsFor(stateDir, store, jobSpawner),
    })
    cleanups.push(async () => {
      if (server.listening) await new Promise<void>((done) => server.close(() => done()))
      bridge.close()
      store.close()
    })

    expect(server.listening).toBe(true)
    // The scheduled refresh ran at start: these are hosts that were actually started.
    expect(jobSpawner.calls.map((call) => call.args.join(' '))).toContain(
      'dash refresh --trigger scheduled',
    )
  })
})

// server.json is the tray's only attach point, so it is owned by identity: the file says
// which process published it, and a server that did not publish one must neither delete
// it nor overwrite it. A second `kyberdash web` over one state dir stands down at the
// jobs lease but still reaches the publish and close paths, so a path-based rm there
// strips the FIRST process's URL out from under the tray.
describe('server.json ownership', () => {
  async function closeServer(server: import('node:http').Server): Promise<void> {
    await new Promise<void>((done) => server.close(() => done()))
  }

  async function startOver(
    stateDir: string,
    store: CanonStore,
  ): Promise<{ server: import('node:http').Server; bridge: KyberBridge; spawner: FakeSpawner }> {
    const bridge = new KyberBridge({ store, reopenCheckIntervalMs: 0 })
    const spawner = new FakeSpawner()
    const server = await runWebDashboard({
      port: 0,
      open: false,
      writeStdout: () => {},
      kyberBridge: bridge,
      ...seamsFor(stateDir, store, spawner),
    })
    return { server, bridge, spawner }
  }

  it('removes its own server.json on close, so no tray looks for a dead URL', async () => {
    const stateDir = await tempStateDir()
    const store = new CanonStore(':memory:')
    const { server, bridge } = await startOver(stateDir, store)

    const path = join(stateDir, 'server.json')
    const published = JSON.parse(readFileSync(path, 'utf-8')) as { pid: number; url: string }
    expect(published.pid).toBe(process.pid)
    expect(published.url).toBe(`http://127.0.0.1:${(server.address() as AddressInfo).port}`)

    await closeServer(server)
    expect(existsSync(path)).toBe(false)
    bridge.close()
    store.close()
  })

  it('leaves another process\'s live server.json alone, on close and on publish', async () => {
    const stateDir = await tempStateDir()
    const path = join(stateDir, 'server.json')
    // A live foreign pid: this test process's parent, which is alive by construction and
    // is not this process. That is exactly the second-process case - a real `kyberdash
    // web` holding the file while a second one stands down at the jobs lease.
    const foreign = JSON.stringify({ pid: process.ppid, url: 'http://127.0.0.1:65000', apiVersion: 1 })
    writeFileSync(path, foreign)

    const store = new CanonStore(':memory:')
    const { server, bridge } = await startOver(stateDir, store)
    // Publishing did not take the file over: the standing server's URL is still the one
    // the tray would read.
    expect(readFileSync(path, 'utf-8')).toBe(foreign)

    await closeServer(server)
    expect(readFileSync(path, 'utf-8')).toBe(foreign)
    bridge.close()
    store.close()
  })

  it('takes over a server.json whose holder is gone, and deletes it by identity on close', async () => {
    const stateDir = await tempStateDir()
    const path = join(stateDir, 'server.json')
    // A pid that cannot be running: well above any live pid, and the same shape a
    // SIGKILLed server leaves behind.
    writeFileSync(path, JSON.stringify({ pid: 0x7ffffffe, url: 'http://127.0.0.1:65000', apiVersion: 1 }))

    const store = new CanonStore(':memory:')
    const { server, bridge } = await startOver(stateDir, store)
    expect((JSON.parse(readFileSync(path, 'utf-8')) as { pid: number }).pid).toBe(process.pid)

    await closeServer(server)
    expect(existsSync(path)).toBe(false)
    bridge.close()
    store.close()
  })

  it('ignores an unreadable server.json on close instead of throwing', async () => {
    const stateDir = await tempStateDir()
    const path = join(stateDir, 'server.json')
    writeFileSync(path, 'not json')

    const store = new CanonStore(':memory:')
    const { server, bridge } = await startOver(stateDir, store)
    await closeServer(server)
    // Ours by then, so it goes; a corrupt body simply carries no ownership claim.
    expect(existsSync(path)).toBe(false)
    bridge.close()
    store.close()
  })
})

// Two servers over ONE state dir, started concurrently rather than one after the other.
// The in-flight map is what stops the second from building its own pair: a second
// `JobHost.start()` would block on the jobs lease this first pair holds (the lease
// serializes in-process rather than standing down), so the second `runWebDashboard` would
// never resolve at all. The promise resolving is therefore itself the assertion, and the
// spawn counts confirm only one host did the work.
describe('hosted services registry: two concurrent servers, one state dir', () => {
  /** Waits for a path to disappear, or fails. The teardown is fire-and-forget by design. */
  async function waitForGone(path: string, timeoutMs = 2_000): Promise<void> {
    const deadline = Date.now() + timeoutMs
    while (existsSync(path)) {
      if (Date.now() > deadline) throw new Error(`${path} still exists after ${timeoutMs}ms`)
      await new Promise((done) => setTimeout(done, 10))
    }
  }
  it('creates one host pair, serves both, and tears it down only when the last one closes', async () => {
    const stateDir = await tempStateDir()
    const store = new CanonStore(':memory:')
    // Identical seams by object identity for both calls: same store handle, same spawners,
    // same program. These are the seams that make sharing legal.
    const jobSpawner = new FakeSpawner()
    const receiverSpawner = new FakeSpawner()
    const probes: ReceiverProbe[] = []
    const receiverProber = {
      probe: async (): Promise<ReceiverProbe> => {
        probes.push('refused')
        return 'refused'
      },
    }
    const start = (): Promise<{ server: import('node:http').Server; bridge: KyberBridge }> => {
      const bridge = new KyberBridge({ store, reopenCheckIntervalMs: 0 })
      return runWebDashboard({
        port: 0,
        open: false,
        writeStdout: () => {},
        kyberBridge: bridge,
        store,
        stateDir,
        program: 'kyberdash-test',
        jobSpawner,
        receiverSpawner,
        receiverProber,
      }).then((server) => ({ server, bridge }))
    }

    const [first, second] = await Promise.all([start(), start()])

    try {
      expect(first.server.listening).toBe(true)
      expect(second.server.listening).toBe(true)
      // One host pair, so one lease and one first tick: a second pair would have meant a
      // second scheduled refresh and a second receiver poll against the same state dir.
      expect(jobSpawner.calls.map((call) => call.args.join(' '))).toEqual([
        'dash refresh --trigger scheduled',
      ])
      expect(probes).toEqual(['refused'])
      expect(existsSync(join(stateDir, JOBS_LOCK_FILE))).toBe(true)

      // Both servers really answer the shared API over their own port.
      const jobsStatus = async (server: import('node:http').Server): Promise<{ refresh: { state: string } }> => {
        const { port } = server.address() as AddressInfo
        const response = await fetch(`http://127.0.0.1:${port}/api/kyber/jobs`)
        expect(response.status).toBe(200)
        return response.json() as Promise<{ refresh: { state: string } }>
      }
      expect(await jobsStatus(first.server)).toMatchObject({ refresh: { state: expect.any(String) } })
      expect(await jobsStatus(second.server)).toMatchObject({ refresh: { state: expect.any(String) } })

      // Closing one server must not tear down the pair the other is still using: the
      // second start()'s lease belongs to the pair, not to the server that created it.
      await new Promise<void>((done) => first.server.close(() => done()))
      expect(first.server.listening).toBe(false)
      expect(second.server.listening).toBe(true)
      expect(existsSync(join(stateDir, JOBS_LOCK_FILE))).toBe(true)
      expect(await jobsStatus(second.server)).toMatchObject({ refresh: { state: expect.any(String) } })

      // The last one out stops both hosts, which release the jobs lease. The teardown is
      // async (`release()` does not await the hosts' close), so poll rather than assert
      // against the state of the very next tick.
      await new Promise<void>((done) => second.server.close(() => done()))
      await waitForGone(join(stateDir, JOBS_LOCK_FILE))
    } finally {
      first.bridge.close()
      second.bridge.close()
      store.close()
    }
  })
})