// Server lifecycle for `kyberdash web` as the shared root application (issue #319 T11 RED,
// architecture rule R1). The web server hosts the JobHost and the ReceiverHost, publishes
// server.json so the tray can find it, and serves ONE API at /api/kyber/* for both the tray
// and the web UI. Everything is injected: fake child spawners, a fake receiver prober, an
// in-memory store, and a temp state dir. Nothing touches the real home or spawns a process.
//
// Injection seams assumed on `runWebDashboard` (the server entry point; there is no
// separate `startWebServer`), all optional with production defaults:
//   store:           CanonStore            shared settings + job host store (default: the bridge's store)
//   stateDir:        string                where server.json and jobs.lock live (default ~/.kyberdash)
//   program:         string                executable the hosts spawn (default: this CLI)
//   jobSpawner:      JobSpawner            children of the JobHost (refresh/import/clean)
//   receiverSpawner: JobSpawner            the `otel` receiver child
//   receiverProber:  ReceiverProber        the :4318 health probe
// When the server's `close` event fires it stops both hosts, kills their children, releases
// the jobs lease, and removes server.json.
import { existsSync, readFileSync } from 'node:fs'
import { mkdtemp, rm } from 'node:fs/promises'
import { request, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { REPORT_SCHEMA_VERSION } from '../analysis/report/types.js'
import { CanonStore } from '../canon/store.js'
import { acquireJobsLease } from '../jobs/lease.js'
import type { ChildResult, JobSpawner } from '../jobs/host.js'
import type { ReceiverProbe } from '../jobs/receiver-host.js'
import { KyberBridge } from '../server/bridge.js'
import { SETTING_KEYS, readSetting, writeSetting } from '../settings/shared-settings.js'
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
  failWith: string | null = null
  spawn(program: string, args: readonly string[]): FakeChild {
    if (this.failWith !== null) throw new Error(this.failWith)
    const child = new FakeChild(program, args)
    this.children.push(child)
    return child
  }
  argv(): string[] {
    return this.children.map((c) => c.args.join(' '))
  }
}

type Harness = {
  server: Server
  base: string
  stateDir: string
  store: CanonStore
  jobSpawner: FakeSpawner
  receiverSpawner: FakeSpawner
  stdout: string[]
}

const cleanups: Array<() => Promise<void>> = []
afterEach(async () => {
  for (const cleanup of cleanups.splice(0).reverse()) await cleanup()
})

async function waitFor(predicate: () => boolean, message: string, timeoutMs = 3_000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    if (predicate()) return
    await new Promise((resolve) => setTimeout(resolve, 10))
  }
  throw new Error(`timed out waiting for: ${message}`)
}

async function start(options: {
  receiverHosted?: boolean
  jobsPaused?: boolean
  probe?: ReceiverProbe
  spawnFailure?: string
} = {}): Promise<Harness> {
  const stateDir = await mkdtemp(join(tmpdir(), 'kyber-web-test-'))
  const store = new CanonStore(':memory:')
  if (options.receiverHosted === true) writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
  if (options.jobsPaused === true) writeSetting(store, SETTING_KEYS.jobsPaused, 'on')
  const jobSpawner = new FakeSpawner()
  if (options.spawnFailure !== undefined) jobSpawner.failWith = options.spawnFailure
  const receiverSpawner = new FakeSpawner()
  const stdout: string[] = []
  const bridge = new KyberBridge({ store, reopenCheckIntervalMs: 0 })
  const opts = {
    port: 0,
    open: false,
    kyberBridge: bridge,
    writeStdout: (text: string) => {
      stdout.push(text)
    },
    store,
    stateDir,
    program: 'kyberdash-test',
    jobSpawner,
    receiverSpawner,
    receiverProber: { probe: async () => options.probe ?? 'refused' },
  }
  // The injection options do not exist yet (RED); the cast keeps this file compiling.
  const server = await (runWebDashboard as unknown as (o: unknown) => Promise<Server>)(opts)
  const harness: Harness = {
    server,
    base: `http://127.0.0.1:${(server.address() as AddressInfo).port}`,
    stateDir,
    store,
    jobSpawner,
    receiverSpawner,
    stdout,
  }
  cleanups.push(async () => {
    if (server.listening) await new Promise<void>((resolve) => server.close(() => resolve()))
    store.close()
    await rm(stateDir, { recursive: true, force: true })
  })
  return harness
}

function send(
  base: string,
  method: string,
  path: string,
  options: { body?: unknown; origin?: string } = {},
): Promise<{ status: number; text: string; json: unknown }> {
  return new Promise((resolve, reject) => {
    const payload = options.body === undefined ? undefined : JSON.stringify(options.body)
    const headers: Record<string, string> = {}
    if (payload !== undefined) {
      headers['content-type'] = 'application/json'
      headers['content-length'] = String(Buffer.byteLength(payload))
    }
    // No Origin header unless asked: the tray is a native client and sends none.
    if (options.origin !== undefined) headers['origin'] = options.origin
    const req = request(`${base}${path}`, { method, headers }, (res) => {
      let text = ''
      res.setEncoding('utf8')
      res.on('data', (chunk: string) => {
        text += chunk
      })
      res.on('end', () => {
        let json: unknown
        try {
          json = JSON.parse(text)
        } catch {
          json = undefined
        }
        resolve({ status: res.statusCode ?? 0, text, json })
      })
    })
    req.on('error', reject)
    if (payload !== undefined) req.write(payload)
    req.end()
  })
}

function closeServer(server: Server): Promise<void> {
  return new Promise((resolve) => server.close(() => resolve()))
}

describe('kyberdash web hosts the background services', () => {
  it('starts the JobHost, whose first tick launches the scheduled refresh', async () => {
    const h = await start()
    await waitFor(() => h.jobSpawner.children.length > 0, 'the scheduled refresh child')
    expect(h.jobSpawner.argv()).toEqual(['dash refresh --trigger scheduled'])
    expect(h.jobSpawner.children[0]!.program).toBe('kyberdash-test')
  })

  it('starts the ReceiverHost, which launches the otel receiver when hosting is on and the port is free', async () => {
    const h = await start({ receiverHosted: true, probe: 'refused' })
    await waitFor(() => h.receiverSpawner.children.length > 0, 'the otel receiver child')
    expect(h.receiverSpawner.argv()).toEqual(['otel'])
  })

  it('does not launch the receiver when hosting is off', async () => {
    const h = await start({ receiverHosted: false, probe: 'refused' })
    await waitFor(() => h.jobSpawner.children.length > 0, 'the host to be running')
    expect(h.receiverSpawner.children).toEqual([])
  })

  it('does not run the scheduled refresh while jobs are paused', async () => {
    const h = await start({ jobsPaused: true })
    const status = await send(h.base, 'GET', '/api/kyber/jobs')
    expect(status.status).toBe(200)
    expect(status.json).toMatchObject({ paused: true })
    expect(h.jobSpawner.children).toEqual([])
  })
})

describe('server.json', () => {
  it('is written with pid, url and apiVersion once the server is listening', async () => {
    const h = await start()
    const file = join(h.stateDir, 'server.json')
    await waitFor(() => existsSync(file), 'server.json')
    const written = JSON.parse(readFileSync(file, 'utf8')) as Record<string, unknown>
    expect(written).toEqual({ pid: process.pid, url: h.base, apiVersion: REPORT_SCHEMA_VERSION })
    // The tray reads the stdout line today; server.json must agree with it.
    const listening = JSON.parse(h.stdout[0]!) as { url: string }
    expect(listening.url).toBe(written['url'])
  })

  it('is removed on close, together with the children and the jobs lease', async () => {
    const h = await start({ receiverHosted: true })
    const file = join(h.stateDir, 'server.json')
    await waitFor(() => existsSync(file), 'server.json')
    await waitFor(() => h.jobSpawner.children.length > 0 && h.receiverSpawner.children.length > 0, 'both children')

    await closeServer(h.server)

    await waitFor(() => !existsSync(file), 'server.json removal')
    await waitFor(() => h.jobSpawner.children.every((c) => c.killed), 'the job child to be killed')
    await waitFor(() => h.receiverSpawner.children.every((c) => c.killed), 'the receiver child to be killed')
    const lease = await acquireJobsLease(h.stateDir)
    expect(lease, 'jobs lease must be free after close').not.toBeNull()
    await lease?.release()
  })
})

describe('the shared API and its origin rule', () => {
  it('lets a loopback request with no Origin header PUT settings (the tray)', async () => {
    const h = await start({ jobsPaused: true })
    const res = await send(h.base, 'PUT', '/api/kyber/settings', { body: { refreshCadenceMinutes: 15 } })
    expect(res.status).toBe(200)
    expect(res.json).toMatchObject({ refreshCadenceMinutes: 15, jobsPaused: true })
    expect(readSetting(h.store, SETTING_KEYS.refreshCadenceMinutes)).toBe(15)
  })

  it('lets a loopback request with no Origin header POST a refresh and starts a tray-triggered job', async () => {
    const h = await start({ jobsPaused: true })
    const res = await send(h.base, 'POST', '/api/kyber/refresh', { body: { surface: 'tray' } })
    expect(res.status).toBe(202)
    await waitFor(() => h.jobSpawner.children.length > 0, 'the tray refresh child')
    expect(h.jobSpawner.argv()).toEqual(['dash refresh --trigger tray'])
  })

  it('serves the same settings to a same-origin browser request', async () => {
    const h = await start({ jobsPaused: true })
    const res = await send(h.base, 'GET', '/api/kyber/settings', { origin: h.base })
    expect(res.status).toBe(200)
    expect(res.json).toMatchObject({ jobsPaused: true })
  })

  it.each(['https://evil.example', 'http://localhost.evil.example', 'null'])(
    'rejects a cross-origin PUT from %s and changes nothing',
    async (origin) => {
      const h = await start({ jobsPaused: true })
      const res = await send(h.base, 'PUT', '/api/kyber/settings', { body: { jobsPaused: false }, origin })
      expect(res.status).toBe(403)
      expect(readSetting(h.store, SETTING_KEYS.jobsPaused)).toBe('on')
    },
  )

  it('rejects a cross-origin refresh and starts no job', async () => {
    const h = await start({ jobsPaused: true })
    const res = await send(h.base, 'POST', '/api/kyber/refresh', {
      body: { surface: 'web' },
      origin: 'https://evil.example',
    })
    expect(res.status).toBe(403)
    expect(h.jobSpawner.children).toEqual([])
  })

  it('rejects a cross-origin import and clean', async () => {
    const h = await start({ jobsPaused: true })
    const imp = await send(h.base, 'POST', '/api/kyber/import-history', {
      body: { weeks: 2 },
      origin: 'https://evil.example',
    })
    const clean = await send(h.base, 'POST', '/api/kyber/clean', {
      body: { all: true, confirm: true },
      origin: 'https://evil.example',
    })
    expect([imp.status, clean.status]).toEqual([403, 403])
    expect(h.jobSpawner.children).toEqual([])
  })

  it('exposes the nested jobs shape over HTTP', async () => {
    const h = await start({ jobsPaused: true })
    const res = await send(h.base, 'GET', '/api/kyber/jobs')
    expect(res.status).toBe(200)
    expect(res.json).toMatchObject({
      refresh: { state: expect.any(String) },
      paused: true,
      hostedElsewhere: false,
    })
    expect(typeof (res.json as { storeGeneration: unknown }).storeGeneration).toBe('number')
  })

  it('never returns a remote error string when a job cannot start', async () => {
    const h = await start({ jobsPaused: true, spawnFailure: 'spawn ENOENT /Users/secret/bin/kyberdash' })
    const refresh = await send(h.base, 'POST', '/api/kyber/refresh', { body: { surface: 'tray' } })
    const imp = await send(h.base, 'POST', '/api/kyber/import-history', { body: { weeks: 2 } })
    const bad = await send(h.base, 'PUT', '/api/kyber/settings', { body: { nope: 1 } })
    const missing = await send(h.base, 'GET', '/api/kyber/does-not-exist')
    for (const res of [refresh, imp, bad, missing]) {
      expect(res.text).not.toContain('secret')
      expect(res.text).not.toContain('ENOENT')
    }
    expect(refresh.status).toBe(202)
    expect(bad.status).toBe(400)
  })
})
