// Dashboard port setting: fixed-port bind failures are actionable errors, never a
// silent fallback (RED first).
//
// `kyberdash web` used to swallow EADDRINUSE and re-listen on a random free port: the
// tray kept the configured URL, the operator kept the configured bookmark, and neither
// pointed at the server that actually started. These tests pin the replacement contract:
//
// - A configured (non-zero) port that is already taken rejects with a
//   `DashboardPortOccupiedError` naming the port and the remedy. Nothing is published:
//   no listening line, no server.json, no held host pair — and the socket that failed
//   to bind is released, so a retry after the occupant leaves can take the port.
// - `port: 0` keeps its existing meaning: an ephemeral loopback bind for the in-process
//   suites that share one machine (web.test.ts, web-listen.test.ts and the rest).
// - Port resolution precedence is explicit CLI `--port` > persisted
//   `settings.dashboard.port` > the 4747 default, and it is resolved inside the engine
//   (web.ts), not in the CLI surface.
// - Changing the setting never rebinds the running server: the current process keeps
//   its port, and the next startup over the same store takes the persisted one.
//
// Nothing here touches the real machine: every server gets an injected in-memory store,
// a temp state dir, and fake job spawners, and the "occupant" is an isolated loopback
// listener opened and closed by the test itself.

import { createServer, request as httpRequest, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { closeSync, mkdtempSync, openSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { JOBS_LOCK_FILE } from '../jobs/lease.js'
import type { ChildResult, JobChild, JobSpawner } from '../jobs/host.js'
import { KyberBridge } from '../server/bridge.js'
import { SETTING_KEYS, readSetting, writeSetting } from '../settings/shared-settings.js'
import { DashboardPortOccupiedError, DashboardPortUnavailableError, portRefusalFor, resolveDashboardPort, runWebDashboard } from './web.js'

/**
 * A spawned child that runs until killed, then exits like a real reaped process.
 * `kill()` must resolve `exited`: `JobHost.close()` kills its children and awaits
 * their exit before releasing `jobs.lock`, so a kill that never resolves wedges the
 * lock and every later startup over the same state dir with it.
 */
class NeverChild implements JobChild {
  readonly exited: Promise<ChildResult>
  private resolveExit!: (result: ChildResult) => void
  constructor() {
    this.exited = new Promise<ChildResult>((resolve) => {
      this.resolveExit = resolve
    })
  }
  kill(): void {
    this.resolveExit({ code: null, stderr: '' })
  }
}

class NeverSpawner implements JobSpawner {
  spawn(): JobChild {
    return new NeverChild()
  }
}

const cleanups: Array<() => Promise<void>> = []
afterEach(async () => {
  vi.restoreAllMocks()
  vi.unstubAllEnvs()
  for (const cleanup of cleanups.splice(0).reverse()) await cleanup()
})

function tempStateDir(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-web-port-'))
  cleanups.push(async () => {
    rmSync(dir, { recursive: true, force: true, maxRetries: 10, retryDelay: 50 })
  })
  return dir
}

/** An isolated loopback occupant: holds one fixed port the dashboard must then refuse. */
function occupy(port: number): Promise<Server> {
  return new Promise((resolve, reject) => {
    const occupant = createServer((_, res) => {
      res.end('occupant')
    })
    occupant.once('error', reject)
    occupant.listen(port, '127.0.0.1', () => {
      occupant.off('error', reject)
      // A listener that outlives its test wedges every later bind of the same port.
      cleanups.push(async () => {
        await new Promise<void>((done) => occupant.close(() => done()))
      })
      resolve(occupant)
    })
  })
}

/** A free loopback port, found by binding ephemeral and letting go before use. */
async function freePort(): Promise<number> {
  const probe = await occupy(0)
  const port = (probe.address() as AddressInfo).port
  await new Promise<void>((done) => probe.close(() => done()))
  return port
}

/**
 * Starts the dashboard with the host seams injected (fake spawners, a refusing prober)
 * so no live job can ever run. An omitted `store` exercises the engine-owned path —
 * the process-singleton default store and the default bridge — which a failed start
 * must release on its own; the cleanup below therefore closes only an injected store,
 * never an engine-owned one.
 */
function startDashboard(options: {
  port?: number
  store?: CanonStore
  stateDir: string
  stdout?: string[]
}): { started: Promise<Server>; stdout: string[] } {
  const stdout = options.stdout ?? []
  const started = runWebDashboard({
    ...(options.port === undefined ? {} : { port: options.port }),
    open: false,
    writeStdout: (text: string) => {
      stdout.push(text)
    },
    ...(options.store === undefined
      ? {}
      : {
          kyberBridge: new KyberBridge({ store: options.store, reopenCheckIntervalMs: 0 }),
          store: options.store,
        }),
    stateDir: options.stateDir,
    program: 'kyberdash-test',
    jobSpawner: new NeverSpawner(),
    receiverSpawner: new NeverSpawner(),
    receiverProber: { probe: async () => 'refused' as const },
  })
  let settled: Server | undefined
  cleanups.push(async () => {
    settled ??= await started.catch(() => undefined)
    if (settled?.listening === true) {
      await new Promise<void>((resolve) => settled!.close(() => resolve()))
    }
    options.store?.close()
  })
  return { started, stdout }
}

function listeningUrl(stdout: readonly string[]): string | null {
  for (const line of stdout) {
    if (!line.includes('kyberdash.web.listening')) continue
    return (JSON.parse(line) as { url: string }).url
  }
  return null
}

/**
 * A PUT with a JSON body over a connection that closes: global `fetch` pools
 * keep-alive sockets, and `server.close()` waits for idle pooled connections to drain
 * before its callback fires — which reads as a hung teardown. node:http ends the
 * socket with the response, so the close below always completes.
 */
function putJson(base: string, path: string, body: unknown): Promise<{ status: number; json: unknown }> {
  return new Promise((resolve, reject) => {
    const payload = JSON.stringify(body)
    const req = httpRequest(
      `${base}${path}`,
      {
        method: 'PUT',
        headers: { 'content-type': 'application/json', 'content-length': String(Buffer.byteLength(payload)) },
      },
      (res) => {
        let text = ''
        res.setEncoding('utf8')
        res.on('data', (chunk: string) => {
          text += chunk
        })
        res.on('end', () => resolve({ status: res.statusCode ?? 0, json: JSON.parse(text) as unknown }))
      },
    )
    req.on('error', reject)
    req.write(payload)
    req.end()
  })
}

/**
 * Whether a path is there, by trying to OPEN it rather than by stat-then-use. A stat
 * followed by an operation on the same path is a race when a teardown on a later turn
 * of the event loop is the other writer; one open attempt cannot be raced.
 */
function probeExists(path: string): boolean {
  try {
    closeSync(openSync(path, 'r'))
    return true
  } catch {
    return false
  }
}

/** Waits for a path to be gone. Polls by open-attempt, never uses the path it probes. */
async function waitForGone(path: string, timeoutMs = 10_000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (probeExists(path)) {
    if (Date.now() > deadline) throw new Error(`timed out waiting for ${path} to disappear`)
    await new Promise((resolve) => setTimeout(resolve, 25))
  }
}

function getStatus(base: string, path: string): Promise<number> {
  return new Promise((resolve, reject) => {
    const req = httpRequest(`${base}${path}`, (res) => {
      res.resume()
      res.on('end', () => resolve(res.statusCode ?? 0))
    })
    req.on('error', reject)
    req.end()
  })
}

describe('a configured port that is already taken', () => {
  it('rejects with an actionable error naming the port, not a silent random port', async () => {
    const port = await freePort()
    await occupy(port)
    const store = new CanonStore(':memory:')
    const stdout: string[] = []

    const failure = await startDashboard({ port, store, stateDir: tempStateDir(), stdout }).started.then(
      () => null,
      (err: unknown) => err,
    )

    expect(failure).toBeInstanceOf(DashboardPortOccupiedError)
    expect(String((failure as Error).message)).toContain(String(port))
    // Actionable: the message must say what to do next, not just what failed.
    expect(String((failure as Error).message)).toMatch(/--port|dashboard\.port|restart/i)
    // Nothing was announced: a listening line for a server that never bound would hand
    // the tray a URL that refuses connections.
    expect(listeningUrl(stdout)).toBeNull()
  })

  it('publishes no server.json for the server that never bound', async () => {
    const port = await freePort()
    await occupy(port)
    const stateDir = tempStateDir()
    const store = new CanonStore(':memory:')

    await startDashboard({ port, store, stateDir }).started.then(
      () => null,
      () => null,
    )

    expect(() => readFileSync(join(stateDir, 'server.json'), 'utf-8')).toThrow()
  })

  it('releases the failed socket so a retry after the occupant leaves can bind', async () => {
    const port = await freePort()
    const occupant = await occupy(port)
    const stateDir = tempStateDir()
    const firstStore = new CanonStore(':memory:')
    await startDashboard({ port, store: firstStore, stateDir }).started.then(
      () => null,
      () => null,
    )

    // The occupant leaves; the port must be bindable again, which it is not when the
    // failed server kept a handle on it.
    await new Promise<void>((done) => occupant.close(() => done()))
    const secondStore = new CanonStore(':memory:')
    const { started, stdout } = startDashboard({ port, store: secondStore, stateDir })
    const server = await started
    try {
      expect(server.listening).toBe(true)
      expect(listeningUrl(stdout)).toContain(`:${port}`)
    } finally {
      await new Promise<void>((resolve) => server.close(() => resolve()))
    }
  })

  it('leaves engine-owned handles releasable: a retry with defaults fails the same clean way', async () => {
    // No injected store or bridge: the start resolves the process-singleton default
    // store (under a stubbed KYBER_CANON_DB, never the real one) and builds its own
    // bridge. Both must be released by the failure itself — a second identical attempt
    // has to reject the same clean way rather than trip over a wedged singleton (a
    // reused closed handle would throw "database is not open", not the port error).
    // Host seams stay injected, so no live job can run on either attempt.
    const root = mkdtempSync(join(tmpdir(), 'kyber-web-port-owned-'))
    cleanups.push(async () => {
      rmSync(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 50 })
    })
    vi.stubEnv('KYBER_CANON_DB', join(root, 'canon.db'))
    const port = await freePort()
    await occupy(port)
    const stateDir = tempStateDir()

    for (let attempt = 0; attempt < 2; attempt++) {
      const stdout: string[] = []
      const failure = await startDashboard({ port, stateDir, stdout }).started.then(
        () => null,
        (err: unknown) => err,
      )
      expect(failure).toBeInstanceOf(DashboardPortOccupiedError)
      expect(String((failure as Error).message)).toContain(String(port))
      expect(listeningUrl(stdout)).toBeNull()
      expect(() => readFileSync(join(stateDir, 'server.json'), 'utf-8')).toThrow()
    }
  })
})

describe('port: 0 keeps its ephemeral meaning for the in-process suites', () => {
  it('binds a free loopback port and announces it', async () => {
    const store = new CanonStore(':memory:')
    const { started, stdout } = startDashboard({ port: 0, store, stateDir: tempStateDir() })
    const server = await started
    try {
      expect(server.listening).toBe(true)
      const url = listeningUrl(stdout)
      expect(url).toMatch(/^http:\/\/127\.0\.0\.1:\d+$/)
      expect(await getStatus(url!, '/api/kyber/settings')).toBe(200)
    } finally {
      await new Promise<void>((resolve) => server.close(() => resolve()))
    }
  })
})

describe('dashboard port resolution precedence', () => {
  it('defaults to 4747 when no explicit port is given and nothing is persisted', () => {
    const store = new CanonStore(':memory:')
    try {
      expect(resolveDashboardPort(undefined, store)).toBe(4747)
      expect(readSetting(store, SETTING_KEYS.dashboardPort)).toBe(4747)
    } finally {
      store.close()
    }
  })

  it('prefers the persisted setting over the default', () => {
    const store = new CanonStore(':memory:')
    try {
      writeSetting(store, SETTING_KEYS.dashboardPort, 4848)
      expect(resolveDashboardPort(undefined, store)).toBe(4848)
    } finally {
      store.close()
    }
  })

  it('prefers an explicit CLI port over the persisted setting', () => {
    const store = new CanonStore(':memory:')
    try {
      writeSetting(store, SETTING_KEYS.dashboardPort, 4848)
      expect(resolveDashboardPort(4949, store)).toBe(4949)
    } finally {
      store.close()
    }
  })

  it('an explicit port: 0 still means ephemeral', () => {
    const store = new CanonStore(':memory:')
    try {
      writeSetting(store, SETTING_KEYS.dashboardPort, 4848)
      expect(resolveDashboardPort(0, store)).toBe(0)
    } finally {
      store.close()
    }
  })
})

describe('portRefusalFor: every bind refusal reads like the feature, not a traceback', () => {
  it('maps EADDRINUSE to the occupied error naming the port and the remedy', () => {
    const err = portRefusalFor(4747, 'EADDRINUSE')
    expect(err).toBeInstanceOf(DashboardPortOccupiedError)
    expect(String(err!.message)).toContain('4747')
    expect(String(err!.message)).toMatch(/--port|dashboard\.port|restart/i)
  })

  it('maps EACCES to an actionable unavailable error instead of a raw traceback', () => {
    // A privileged port persisted as dashboard.port and restarted as non-root.
    const err = portRefusalFor(80, 'EACCES')
    expect(err).toBeInstanceOf(DashboardPortUnavailableError)
    expect(String(err!.message)).toContain('80')
    expect(String(err!.message)).toMatch(/privilege|--port|dashboard\.port/i)
  })

  it('maps ERR_SOCKET_BAD_PORT without the sub-1024 privilege lecture, and leaves port 0 and unknown codes to the caller', () => {
    // A socket-layer rejection on a high port must not claim privilege is the issue,
    // but must keep the same remedies.
    const err = portRefusalFor(70000, 'ERR_SOCKET_BAD_PORT')
    expect(err).toBeInstanceOf(DashboardPortUnavailableError)
    expect(String(err!.message)).toContain('70000')
    expect(String(err!.message)).not.toMatch(/privilege/i)
    expect(String(err!.message)).toMatch(/--port|dashboard\.port|restart/i)
    // 0 is an ephemeral bind, never a configured choice, so the raw error is kept.
    expect(portRefusalFor(0, 'EADDRINUSE')).toBeUndefined()
    expect(portRefusalFor(4747, 'ENOTFOUND')).toBeUndefined()
  })
})

describe('the persisted setting applies on the next startup, never to the running server', () => {
  it('keeps serving the bound port after the setting changes, and the next start uses the new one', async () => {
    // One real database file, never the real machine's: the proof is that a value
    // saved through the HTTP API is still there after the handle closes and a fresh
    // handle reopens the same file — no in-memory copying between two `:memory:`
    // stores, which would prove nothing about persistence.
    const root = mkdtempSync(join(tmpdir(), 'kyber-web-port-db-'))
    cleanups.push(async () => {
      rmSync(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 50 })
    })
    const dbPath = join(root, 'canon.db')
    const stateDir = tempStateDir()
    const secondPort = await freePort()

    // First startup takes an explicit CLI port for this run only; nothing is persisted.
    const firstPort = await freePort()
    expect(secondPort).not.toBe(firstPort)
    const firstStore = new CanonStore(dbPath)
    const first = startDashboard({ port: firstPort, store: firstStore, stateDir })
    const firstServer = await first.started
    try {
      const firstUrl = listeningUrl(first.stdout)!
      expect(firstUrl).toContain(`:${firstPort}`)
      expect(readSetting(firstStore, SETTING_KEYS.dashboardPort)).toBe(4747)

      // Saving through the API answers the new value but does not rebind this process.
      const updated = await putJson(firstUrl, '/api/kyber/settings', { dashboardPort: secondPort })
      expect(updated.status).toBe(200)
      expect((updated.json as { dashboardPort: number }).dashboardPort).toBe(secondPort)
      expect((firstServer.address() as AddressInfo).port).toBe(firstPort)
      expect(await getStatus(firstUrl, '/api/kyber/settings')).toBe(200)
    } finally {
      await new Promise<void>((resolve) => firstServer.close(() => resolve()))
    }
    // The host pair tears down fire-and-forget; the jobs lease must be back before the
    // next startup, or the second host waits out a lease held by a closed server.
    await waitForGone(join(stateDir, JOBS_LOCK_FILE))

    // A fresh handle over the same file sees the saved row without anything rewriting it.
    const secondStore = new CanonStore(dbPath)
    expect(readSetting(secondStore, SETTING_KEYS.dashboardPort)).toBe(secondPort)
    // Next startup resolves the persisted port on its own (no explicit port given).
    // The harness cleanup owns the store lifetime, so this test never closes it twice.
    const second = startDashboard({ store: secondStore, stateDir })
    const secondServer = await second.started
    try {
      expect(listeningUrl(second.stdout)).toContain(`:${secondPort}`)
    } finally {
      await new Promise<void>((resolve) => secondServer.close(() => resolve()))
    }
  })
})
