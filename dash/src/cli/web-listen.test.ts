// Start-up ordering and the attach point of `kyberdash web` (issue #319 review, items 2
// and 4).
//
// The tray supervisor reads exactly one line - the machine-readable
// `kyberdash.web.listening` line - and nowhere else does it time out: it kills a server
// that has not announced itself within LISTENING_TIMEOUT. The old order awaited
// acquireHostedServices BEFORE binding, and `JobHost.start()` awaits its first tick,
// whose maintenance pass purges expired content and reprojects the whole canon.db. On a
// large store that first pass is minutes, so a perfectly healthy server read as hung and
// got killed with its socket never bound.
//
// The fix is an ordering statement, which no unit test of "it works" can see. These tests
// therefore block the maintenance pass on a promise nobody resolves and assert that the
// listening line, server.json and a live socket are all already there while it is still
// blocked - then that the routes which need the job host answer 503 rather than reaching
// for a host that does not exist yet. Finally they pin server.json ownership and mode:
// 0o600, never written through a symlink, and a publish that fails closes the server
// instead of leaving a bound socket no tray can find.
//
// web.test.ts and web-registry.test.ts are the approved contracts for the surrounding
// behaviour (server.json removal on close, no registry entry after a failed start) and
// are untouched by this file.
import { Server, request as httpRequest } from 'node:http'
import { chmodSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../canon/store.js'
import type { ChildResult, JobSpawner } from '../jobs/host.js'
import type { ReceiverProbe } from '../jobs/receiver-host.js'
import { JOBS_LOCK_FILE } from '../jobs/lease.js'
import { KyberBridge } from '../server/bridge.js'
import { runWebDashboard } from './web.js'

class FakeChild {
  killed = false
  readonly exited: Promise<ChildResult>
  private resolveExit!: (result: ChildResult) => void
  constructor(readonly program: string, readonly args: readonly string[]) {
    this.exited = new Promise((resolve) => {
      this.resolveExit = resolve
    })
  }
  exit(code: number | null, stderr = ''): void {
    this.resolveExit({ code, stderr })
  }
  kill(): void {
    this.killed = true
    this.resolveExit({ code: null, stderr: '' })
  }
}

class FakeSpawner implements JobSpawner {
  readonly children: FakeChild[] = []
  spawn(program: string, args: readonly string[]): FakeChild {
    const child = new FakeChild(program, args)
    this.children.push(child)
    return child
  }
}

type Options = {
  /** When set, the maintenance pass blocks on this until the test releases it. */
  maintenanceGate?: { release: () => void; blocked: Promise<void> }
  /** Prepared state dir; a fresh one is created when not given. */
  stateDir?: string
}

const cleanups: Array<() => Promise<void>> = []
afterEach(async () => {
  vi.restoreAllMocks()
  for (const cleanup of cleanups.splice(0).reverse()) await cleanup()
})

/**
 * WHY: the last `share().release()` on the hosted pair tears the job host down
 * fire-and-forget (`void services.jobHost.close()` in web.ts), so the lease release
 * unlinks `<stateDir>/jobs.lock` on a later turn than the server close this teardown
 * awaits. `rmSync` on the directory then walks it while that unlink is pending and fails
 * with ENOTEMPTY - a flake that only shows up under load, and only for a test that ended
 * with a pair still sharing the dir. Wait for the lock to go before removing, and retry
 * the removal itself: the lease is the last writer, but there may be others.
 */
async function removeTempStateDir(dir: string): Promise<void> {
  const lock = join(dir, JOBS_LOCK_FILE)
  const deadline = Date.now() + 3_000
  while (existsSync(lock) && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 25))
  }
  rmSync(dir, { recursive: true, force: true, maxRetries: 10, retryDelay: 50 })
}

function tempStateDir(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-web-listen-'))
  cleanups.push(async () => removeTempStateDir(dir))
  return dir
}

/** A maintenance pass that blocks until the returned `release` is called. */
function gatedMaintenance(): { release: () => void; blocked: Promise<void> } {
  let release!: () => void
  const blocked = new Promise<void>((resolve) => {
    release = resolve
  })
  return { release, blocked }
}

/** Starts the dashboard with every seam injected; returns the pieces the tests assert on. */
function start(options: Options = {}): {
  started: Promise<Server>
  stdout: string[]
  stateDir: string
  store: CanonStore
  bridge: KyberBridge
  jobSpawner: FakeSpawner
} {
  const stateDir = options.stateDir ?? tempStateDir()
  const store = new CanonStore(':memory:')
  const bridge = new KyberBridge({ store, reopenCheckIntervalMs: 0 })
  const stdout: string[] = []
  const jobSpawner = new FakeSpawner()
  const receiverSpawner = new FakeSpawner()
  const started = runWebDashboard({
    port: 0,
    open: false,
    writeStdout: (text: string) => {
      stdout.push(text)
    },
    kyberBridge: bridge,
    store,
    stateDir,
    program: 'kyberdash-test',
    jobSpawner,
    receiverSpawner,
    receiverProber: { probe: async (): Promise<ReceiverProbe> => 'refused' },
    ...(options.maintenanceGate === undefined
      ? {}
      : { maintenance: async () => options.maintenanceGate!.blocked }),
  })
  // A start that never resolved (a blocked maintenance pass) still owns a bound socket,
  // so the teardown goes through the promise rather than a server handle we may not have.
  let settled: Server | undefined
  cleanups.push(async () => {
    settled ??= await started.catch(() => undefined)
    if (settled?.listening === true) {
      await new Promise<void>((resolve) => settled!.close(() => resolve()))
    }
    store.close()
  })
  return { started, stdout, stateDir, store, bridge, jobSpawner }
}

type ListeningLine = { event: string; url: string; pid: number; version: string; apiVersion: number }

function tryParseListening(line: string): ListeningLine | null {
  if (!line.includes('kyberdash.web.listening')) return null
  return JSON.parse(line) as ListeningLine
}

function listeningLine(stdout: readonly string[]): ListeningLine | null {
  for (const line of stdout) {
    const parsed = tryParseListening(line)
    if (parsed !== null) return parsed
  }
  return null
}

async function waitFor(predicate: () => boolean, message: string, timeoutMs = 3_000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (predicate()) return
    await new Promise((resolve) => setTimeout(resolve, 10))
  }
  throw new Error(`timed out waiting for: ${message}`)
}

function get(base: string, path: string, method = 'GET', body?: unknown): Promise<{ status: number; text: string }> {
  return new Promise((resolve, reject) => {
    const payload = body === undefined ? undefined : JSON.stringify(body)
    const headers: Record<string, string> = {}
    if (payload !== undefined) {
      headers['content-type'] = 'application/json'
      headers['content-length'] = String(Buffer.byteLength(payload))
    }
    const req = httpRequest(`${base}${path}`, { method, headers }, (res) => {
      let text = ''
      res.setEncoding('utf8')
      res.on('data', (chunk: string) => {
        text += chunk
      })
      res.on('end', () => resolve({ status: res.statusCode ?? 0, text }))
    })
    req.on('error', reject)
    if (payload !== undefined) req.write(payload)
    req.end()
  })
}

describe('the listening line does not wait for the first maintenance pass', () => {
  it('is emitted, and server.json published, while the maintenance pass is still blocked', async () => {
    const gate = gatedMaintenance()
    const { started, stdout, stateDir } = start({ maintenanceGate: gate })

    // The whole point: this resolves while `gate.blocked` never does.
    await waitFor(() => listeningLine(stdout) !== null, 'the listening line, before the first maintenance pass')

    const line = listeningLine(stdout)!
    expect(line.event).toBe('kyberdash.web.listening')
    expect(line.url).toMatch(/^http:\/\/127\.0\.0\.1:\d+$/)
    expect(line.pid).toBe(process.pid)

    // The attach point is published before the line, so a tray that wakes on the line
    // can already read the file it is about to be told to attach to.
    const published = JSON.parse(readFileSync(join(stateDir, 'server.json'), 'utf-8')) as {
      url: string
      pid: number
    }
    expect(published).toMatchObject({ url: line.url, pid: process.pid })

    // Still blocked: nothing below proves the ordering until this is true.
    let settled = false
    void started.then(() => {
      settled = true
    })
    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(settled).toBe(false)

    gate.release()
    const server = await started
    expect(server.listening).toBe(true)
    await new Promise<void>((resolve) => server.close(() => resolve()))
  })

  it('answers the routes that need the job host with a fixed 503 until it is ready', async () => {
    const gate = gatedMaintenance()
    const { started, stdout } = start({ maintenanceGate: gate })
    await waitFor(() => listeningLine(stdout) !== null, 'the listening line')
    const base = listeningLine(stdout)!.url

    for (const path of ['/api/kyber/jobs', '/api/kyber/refresh', '/api/kyber/import-history']) {
      const answer = await get(base, path, path === '/api/kyber/jobs' ? 'GET' : 'POST', {
        surface: 'tray',
        weeks: 2,
      })
      expect(answer.status).toBe(503)
      // A fixed string: the lease and spawn errors behind it name paths on this machine.
      expect(JSON.parse(answer.text)).toEqual({ error: 'Background services are still starting' })
    }

    gate.release()
    const server = await started
    expect(server.listening).toBe(true)
    await new Promise<void>((resolve) => server.close(() => resolve()))
  })

  it('serves the routes that do not need the job host during that window', async () => {
    const gate = gatedMaintenance()
    const { started, stdout } = start({ maintenanceGate: gate })
    await waitFor(() => listeningLine(stdout) !== null, 'the listening line')
    const base = listeningLine(stdout)!.url

    const answer = await get(base, '/api/kyber/settings')
    expect(answer.status).toBe(200)

    gate.release()
    const server = await started
    await new Promise<void>((resolve) => server.close(() => resolve()))
  })
})

describe('server.json: mode, symlinks, and a publish that fails', () => {
  it('is written 0o600, and left 0o600 when a leftover was left world-readable', async () => {
    const stateDir = tempStateDir()
    const server = await start({ stateDir }).started
    const path = join(stateDir, 'server.json')
    expect(statSync(path).mode & 0o777).toBe(0o600)
    await new Promise<void>((resolve) => server.close(() => resolve()))

    // The leftover case: `mode` on writeFileSync applies only at creation, so a file a
    // crashed predecessor left world-readable stays world-readable without an explicit
    // chmod. The pid is dead, which is the normal recovery path that overwrites it.
    writeFileSync(path, '{"pid":999999,"url":"http://127.0.0.1:1"}', { mode: 0o644 })
    chmodSync(path, 0o644)
    const second = await start({ stateDir }).started
    expect(statSync(path).mode & 0o777).toBe(0o600)
    await new Promise<void>((resolve) => second.close(() => resolve()))
  })

  it('refuses to publish through a symlink and leaves the target untouched', async () => {
    const stateDir = tempStateDir()
    const elsewhere = join(stateDir, 'not-server.json')
    writeFileSync(elsewhere, '{"pid":999999,"url":"http://127.0.0.1:1"}')
    symlinkSync(elsewhere, join(stateDir, 'server.json'))

    const errors = vi.spyOn(console, 'error').mockImplementation(() => {})
    const server = await start({ stateDir }).started
    await new Promise<void>((resolve) => server.close(() => resolve()))

    expect(lstatSync(join(stateDir, 'server.json')).isSymbolicLink()).toBe(true)
    expect(readFileSync(elsewhere, 'utf-8')).toBe('{"pid":999999,"url":"http://127.0.0.1:1"}')
    expect(errors).toHaveBeenCalled()
  })

  it('closes the server and leaves nothing held when the publish fails', async () => {
    const stateDir = tempStateDir()
    // A directory where the file belongs: the write cannot succeed, and a socket that
    // stays bound with no attach point is a server no tray can ever reach.
    mkdirSync(join(stateDir, 'server.json'))
    const closeSpy = vi.spyOn(Server.prototype, 'close')

    await expect(start({ stateDir }).started).rejects.toThrow()

    expect(closeSpy).toHaveBeenCalled()
    // Nothing published, nothing held: no attach point, and no jobs lease standing
    // against the next process over this state dir.
    expect(lstatSync(join(stateDir, 'server.json')).isDirectory()).toBe(true)
    expect(existsSync(join(stateDir, 'jobs.lock'))).toBe(false)
  })
})

