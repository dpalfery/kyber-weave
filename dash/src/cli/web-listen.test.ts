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
// 0o600, never written through a symlink, a state dir that does not exist yet (created,
// 0o700) - and a publish that still fails warning ONCE and leaving the dashboard serving,
// because a tray launches a server by its listening line, and an attach point nobody can
// write is not a reason to kill a working dashboard. That pair is the self-update smoke
// run's failure (issue #319, CI on ubuntu and macOS): its temp HOME has no `~/.kyberdash`
// at all, because the smoke also points KYBER_CANON_DB somewhere else entirely.
//
// Nothing here checks a path and then acts on it: every mode, kind and absence claim is
// read from an open descriptor, or is the last thing done to a path. That is CodeQL's
// `js/file-system-race` (check-then-use), and it is also the honest way to assert on a
// file a concurrent teardown is rewriting.
//
// web.test.ts and web-registry.test.ts are the approved contracts for the surrounding
// behaviour (server.json removal on close, no registry entry after a failed start) and
// are untouched by this file.
import { Server, request as httpRequest } from 'node:http'
import { chmodSync, closeSync, fstatSync, lstatSync, mkdirSync, mkdtempSync, openSync, readdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
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
  /** Prepared state dir; a fresh one is created when not given. A path that does not exist
   *  yet is exactly the smoke run's condition, and is left missing. */
  stateDir?: string
  /**
   * The canon.db the store opens when it must live somewhere unusual - with its parent
   * directory absent, the way `KYBER_CANON_DB=<home>/does-not-exist/canon.db` puts it in
   * the smoke run. The store creates the parent itself, exactly as it does in production.
   */
  canonDb?: string
}

const cleanups: Array<() => Promise<void>> = []
afterEach(async () => {
  vi.restoreAllMocks()
  vi.unstubAllEnvs()
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
  while (probeExists(lock) && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 25))
  }
  rmSync(dir, { recursive: true, force: true, maxRetries: 10, retryDelay: 50 })
}

/**
 * Whether a path is there, by trying to OPEN it rather than by stat-then-use. A stat
 * followed by an operation on the same path is a race (CodeQL `js/file-system-race`), and
 * here the other writer is a teardown on a later turn of the event loop; one open attempt
 * cannot be raced into a use of a path that has since been replaced.
 */
function probeExists(path: string): boolean {
  try {
    closeSync(openSync(path, 'r'))
    return true
  } catch {
    return false
  }
}

/** The permission bits of a path, read from its own descriptor rather than by path stat. */
function modeOf(path: string): number {
  const fd = openSync(path, 'r')
  try {
    return fstatSync(fd).mode & 0o777
  } finally {
    closeSync(fd)
  }
}

/** Waits for a path to be gone. Polls by open-attempt, never uses the path it probes. */
async function waitForGone(path: string, timeoutMs = 3_000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (probeExists(path)) {
    if (Date.now() > deadline) throw new Error(`timed out waiting for ${path} to disappear`)
    await new Promise((resolve) => setTimeout(resolve, 25))
  }
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
  if (options.canonDb !== undefined) vi.stubEnv('KYBER_CANON_DB', options.canonDb)
  const store = new CanonStore(options.canonDb ?? ':memory:')
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
    expect(modeOf(path)).toBe(0o600)
    await new Promise<void>((resolve) => server.close(() => resolve()))

    // The leftover case: `mode` on writeFileSync applies only at creation, so a file a
    // crashed predecessor left world-readable stays world-readable without an explicit
    // chmod. The pid is dead, which is the normal recovery path that overwrites it.
    writeFileSync(path, '{"pid":999999,"url":"http://127.0.0.1:1"}', { mode: 0o644 })
    chmodSync(path, 0o644)
    const second = await start({ stateDir }).started
    expect(modeOf(path)).toBe(0o600)
    await new Promise<void>((resolve) => second.close(() => resolve()))
  })

  it('refuses to publish through a symlink and leaves the target untouched', async () => {
    const stateDir = tempStateDir()
    const elsewhere = join(stateDir, 'not-server.json')
    writeFileSync(elsewhere, '{"pid":999999,"url":"http://127.0.0.1:1"}')
    symlinkSync(elsewhere, join(stateDir, 'server.json'))

    const errors = vi.spyOn(console, 'error').mockImplementation(() => {})
    const { started, stdout } = start({ stateDir })
    await waitFor(() => listeningLine(stdout) !== null, 'the listening line')
    // The dashboard serves regardless: the link is refused, not the server.
    const server = await started
    expect(server.listening).toBe(true)
    await new Promise<void>((resolve) => server.close(() => resolve()))

    // `readFileSync(elsewhere)` is on the LINK TARGET, a path nothing here checked, so this
    // is a read of an unrelated name rather than a use of the one that was inspected.
    expect(readFileSync(elsewhere, 'utf-8')).toBe('{"pid":999999,"url":"http://127.0.0.1:1"}')
    expect(errors.mock.calls.map((call) => String(call[0])).join('\n')).toMatch(/is a symbolic link/)
    // The final thing done to that path: the link is still a link.
    expect(lstatSync(join(stateDir, 'server.json')).isSymbolicLink()).toBe(true)
  })

  it('keeps serving, warns once and leaves no partial file when the publish fails', async () => {
    const stateDir = tempStateDir()
    // A directory where the file belongs: the swap onto it cannot succeed. A tray that
    // cannot find the attach point is a smaller problem than a dashboard that is gone, so
    // the server stays up and says so exactly once.
    mkdirSync(join(stateDir, 'server.json'))
    const errors = vi.spyOn(console, 'error').mockImplementation(() => {})

    const { started, stdout } = start({ stateDir })
    await waitFor(() => listeningLine(stdout) !== null, 'the listening line even though the publish failed')
    const server = await started
    expect(server.listening).toBe(true)

    const warnings = errors.mock.calls.map((call) => String(call[0]))
    expect(warnings.filter((line) => line.includes('could not publish server.json'))).toHaveLength(1)
    expect(warnings.join('\n')).toMatch(/could not publish server\.json \(E[A-Z]+\): the tray cannot attach to this server/)
    // The temp file the swap staged is cleaned up: a failed publish leaves nothing behind
    // but the one line.
    expect(readdirSync(stateDir).filter((name) => name.startsWith('.server.json.'))).toEqual([])

    await new Promise<void>((resolve) => server.close(() => resolve()))
    // Nothing held: the lease went back with the last share, so the next process over this
    // state dir is not stood down by this one. The release is fire-and-forget (see
    // removeTempStateDir), so this waits for the unlink rather than racing it.
    await waitForGone(join(stateDir, JOBS_LOCK_FILE))
    // The last thing done to that path: still the directory the test put there.
    expect(lstatSync(join(stateDir, 'server.json')).isDirectory()).toBe(true)
  })

  // Issue #319, CI: the self-update smoke run builds the real binary, hands it a temp HOME
  // and a KYBER_CANON_DB under a directory that does not exist, and waits for the listening
  // line. The state dir is therefore absent and, because the store lives elsewhere, nothing
  // else has created it: the publish used to die on ENOENT and the child exited before the
  // supervisor saw its line.
  describe('a state dir that does not exist yet', () => {
    /** A temp root plus a state dir path under it that is deliberately never created. */
    function missingStateDir(): string {
      const root = mkdtempSync(join(tmpdir(), 'kyber-web-fresh-'))
      cleanups.push(async () => removeTempStateDir(root))
      return join(root, 'kyberdash')
    }

    it('creates it 0o700 and publishes server.json 0o600 into it', async () => {
      const stateDir = missingStateDir()
      const { started, stdout } = start({ stateDir })

      const line = await waitFor(() => listeningLine(stdout) !== null, 'the listening line', 5_000).then(() => listeningLine(stdout)!)
      expect(line.event).toBe('kyberdash.web.listening')
      expect(line.url).toMatch(/^http:\/\/127\.0\.0\.1:\d+$/)

      const server = await started
      expect(server.listening).toBe(true)
      expect(modeOf(stateDir)).toBe(0o700)
      expect(modeOf(join(stateDir, 'server.json'))).toBe(0o600)
      expect(JSON.parse(readFileSync(join(stateDir, 'server.json'), 'utf-8'))).toMatchObject({
        url: line.url,
        pid: process.pid,
      })
      await new Promise<void>((resolve) => server.close(() => resolve()))
    })

    it('starts with KYBER_CANON_DB in a non-existent directory and the state dir missing too', async () => {
      const stateDir = missingStateDir()
      const canonDb = join(dirname(stateDir), 'does-not-exist', 'canon.db')
      const { started, stdout } = start({ stateDir, canonDb })

      await waitFor(() => listeningLine(stdout) !== null, 'the listening line with a store path that has no parent', 5_000)
      const server = await started
      expect(server.listening).toBe(true)
      // Both sides of the smoke's environment landed: the store's parent was created by the
      // store, and the state dir by the server.
      expect(modeOf(join(stateDir, 'server.json'))).toBe(0o600)
      const answer = await get(listeningLine(stdout)!.url, '/api/kyber/settings')
      expect(answer.status).toBe(200)
      await new Promise<void>((resolve) => server.close(() => resolve()))
    })

    // Skipped rather than faked: root ignores the mode bits, and Windows has no 0o500.
    const unwritable = process.platform === 'win32' || process.getuid?.() === 0 ? it.skip : it
    unwritable('still serves when the state dir exists but cannot be written to', async () => {
      const stateDir = tempStateDir()
      chmodSync(stateDir, 0o500)
      cleanups.push(async () => {
        chmodSync(stateDir, 0o700)
      })
      const errors = vi.spyOn(console, 'error').mockImplementation(() => {})

      const { started, stdout } = start({ stateDir })
      await waitFor(() => listeningLine(stdout) !== null, 'the listening line with an unwritable state dir', 5_000)
      const server = await started
      expect(server.listening).toBe(true)

      const warnings = errors.mock.calls.map((call) => String(call[0]))
      expect(warnings.filter((line) => line.includes('could not publish server.json'))).toHaveLength(1)
      expect(warnings.join('\n')).toMatch(/could not publish server\.json \(EACCES\): the tray cannot attach to this server/)
      expect(readdirSync(stateDir).filter((name) => name.startsWith('.server.json.'))).toEqual([])

      // It is still a working dashboard: the routes answer.
      expect((await get(listeningLine(stdout)!.url, '/api/kyber/settings')).status).toBe(200)
      await new Promise<void>((resolve) => server.close(() => resolve()))
      chmodSync(stateDir, 0o700)
    })
  })
})

