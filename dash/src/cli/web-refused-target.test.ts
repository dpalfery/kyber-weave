// Storm guard (T3): a dashboard started without an explicit `program` takes its spawn target from
// this process's own entry. Under vitest that entry is not the kyberdash CLI, so the resolver
// refuses it. The refusal must reach both hosts as their recorded failure, nothing may spawn, and
// the dashboard must still serve.
//
// Real argv is deliberately left alone: the refusal comes from process.argv as vitest launched it.
import { request as httpRequest } from 'node:http'
import type { Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { clearSpawnGuardHits, spawnGuardHits } from '../process/spawn-guard.js'
import { CanonStore } from '../canon/store.js'
import type { ChildResult, JobChild, JobSpawner } from '../jobs/host.js'
import type { ReceiverProbe } from '../jobs/receiver-host.js'
import { KyberBridge } from '../server/bridge.js'
import { SETTING_KEYS, writeSetting } from '../settings/shared-settings.js'
import { currentSpawnRuntime, isSpawnRefusal, resolveJobSpawnTarget } from './spawn-target.js'
import { runWebDashboard } from './web.js'

const ROLE_VARIABLES = ['KYBERDASH_SPAWN_ROLE', 'KYBERDASH_SPAWN_DEPTH'] as const
const priorEnvironment = new Map<string, string | undefined>()
let stateDir = ''

beforeEach(() => {
  for (const name of ROLE_VARIABLES) {
    priorEnvironment.set(name, process.env[name])
    delete process.env[name]
  }
  clearSpawnGuardHits()
  stateDir = mkdtempSync(join(tmpdir(), 'kyber-web-refused-target-'))
})

afterEach(() => {
  for (const name of ROLE_VARIABLES) {
    const prior = priorEnvironment.get(name)
    if (prior === undefined) delete process.env[name]
    else process.env[name] = prior
  }
  priorEnvironment.clear()
  clearSpawnGuardHits()
  rmSync(stateDir, { recursive: true, force: true })
})

/** Records every spawn it is asked for and starts nothing. */
class RecordingSpawner implements JobSpawner {
  readonly calls: Array<{ program: string; args: readonly string[] }> = []

  spawn(program: string, args: readonly string[]): JobChild {
    this.calls.push({ program, args: [...args] })
    const exited: Promise<ChildResult> = Promise.resolve({ code: 0, stderr: '' })
    return { exited, kill: () => {} }
  }
}

/** GET a path on the running server and parse the JSON body, one connection per request. */
function getJson(port: number, path: string): Promise<{ status: number; body: unknown }> {
  return new Promise((resolve, reject) => {
    const req = httpRequest({ host: '127.0.0.1', port, path, method: 'GET', agent: false }, (res) => {
      let text = ''
      res.setEncoding('utf8')
      res.on('data', (chunk: string) => {
        text += chunk
      })
      res.on('end', () => {
        let body: unknown = text
        try {
          body = JSON.parse(text)
        } catch {
          // A non-JSON body is returned as text so the failure shows what the server said.
        }
        resolve({ status: res.statusCode ?? 0, body })
      })
    })
    req.on('error', reject)
    req.end()
  })
}

function closeServer(server: Server): Promise<void> {
  return new Promise((resolve) => {
    server.close(() => resolve())
  })
}

/**
 * Polls a condition until it holds. The hosts start after the socket is bound, so their state
 * arrives on their own schedule; this waits on the observable condition, not on a fixed delay.
 */
async function eventually<T>(read: () => Promise<T>, done: (value: T) => boolean, what: string): Promise<T> {
  const deadline = Date.now() + 10_000
  for (;;) {
    const value = await read()
    if (done(value)) return value
    if (Date.now() > deadline) throw new Error(`timed out waiting for ${what}; last read: ${JSON.stringify(value)}`)
    await new Promise((resolve) => { setTimeout(resolve, 20) })
  }
}

describe('storm guard: a dashboard with no explicit program refuses its unknown entry and still serves', () => {
  it('records the refusal on both hosts, spawns nothing, and keeps serving', async () => {
    // Precondition: this process, as vitest launched it, must be refused. Otherwise the test would
    // pass for the wrong reason, because the default target would be a real CLI entry.
    expect(
      isSpawnRefusal(resolveJobSpawnTarget(currentSpawnRuntime(false))),
      'vitest was expected to report an entry the resolver refuses',
    ).toBe(true)

    const store = new CanonStore(':memory:')
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    const bridge = new KyberBridge({ store, reopenCheckIntervalMs: 0 })
    const jobSpawner = new RecordingSpawner()
    const receiverSpawner = new RecordingSpawner()
    let server: Server | undefined
    try {
      // No `program` option: the default target is resolved from the real argv.
      server = await runWebDashboard({
        port: 0,
        open: false,
        writeStdout: () => {},
        kyberBridge: bridge,
        store,
        stateDir,
        jobSpawner,
        receiverSpawner,
        receiverProber: { probe: async (): Promise<ReceiverProbe> => 'refused' },
        maintenance: async () => {},
      })
      const { port } = server.address() as AddressInfo

      const refresh = await eventually(
        () => getJson(port, '/api/kyber/jobs'),
        (res) => res.status === 200
          && (res.body as { refresh: { state: string } }).refresh.state === 'failed',
        'the scheduled refresh to record a failure',
      )
      const lastFailure = (refresh.body as { refresh: { lastFailure: string | null } }).refresh.lastFailure
      expect(lastFailure, 'the scheduled refresh must name the refusal').toContain('spawn target refused')

      const coverage = await eventually(
        () => getJson(port, '/api/kyber/coverage'),
        (res) => res.status === 200
          && typeof (res.body as { receiver: { lastError: unknown } }).receiver.lastError === 'string',
        'the receiver to report its last error',
      )
      const receiverError = (coverage.body as { receiver: { lastError: string } }).receiver.lastError
      expect(receiverError, 'the receiver must report the refusal as its last error').toContain('spawn target refused')
      // The job failure is the same refusal with a timestamp and a stderr path appended, so the
      // receiver's reason must be contained in it: both hosts name the one refusal.
      expect(lastFailure, 'the job failure and the receiver must name the same refusal').toContain(receiverError)

      expect(jobSpawner.calls, 'a refused target must not start a job child').toEqual([])
      expect(receiverSpawner.calls, 'a refused target must not start a receiver child').toEqual([])
      expect(spawnGuardHits(), 'no spawn may reach the spawn guard').toEqual([])

      const served = await getJson(port, '/api/kyber/settings')
      expect(served.status, 'a refused target must not stop the dashboard from serving').toBe(200)
    } finally {
      if (server !== undefined) await closeServer(server)
      bridge.close()
      store.close()
    }
  })
})
