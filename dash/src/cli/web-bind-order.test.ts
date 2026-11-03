// Storm guard (T3): a job or receiver child is refused before it binds the dashboard port.
//
// Ordering caveat from T2: runWebDashboard binds the listening socket first and only then reaches
// acquireHostedServices, where assertMayHost refuses the child. The refusal is right but late: the
// child opens a socket it may never serve on, and the refusal has to close it again. The check
// belongs before the bind, so a job child never opens a socket at all.
//
// Deterministic and fake-only: http.Server.prototype.listen is replaced by a fake that records the
// call and reports a bind error. No socket is opened, so no port is held and nothing is closed in
// finally. A correct start never calls listen and rejects with the refusal; a late refusal calls
// listen, which the test reports.
import { existsSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { Server as HttpServer } from 'node:http'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../canon/store.js'
import type { ChildResult, JobChild, JobSpawner } from '../jobs/host.js'
import type { ReceiverProbe } from '../jobs/receiver-host.js'
import { KyberBridge } from '../server/bridge.js'
import { runWebDashboard } from './web.js'

const ROLE_VARIABLE = 'KYBERDASH_SPAWN_ROLE'
let stateDir = ''

beforeEach(() => {
  delete process.env[ROLE_VARIABLE]
  stateDir = mkdtempSync(join(tmpdir(), 'kyber-web-bind-order-'))
})

afterEach(() => {
  delete process.env[ROLE_VARIABLE]
  vi.restoreAllMocks()
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

describe('storm guard: a job or receiver child is refused before the dashboard port is bound', () => {
  it.each(['job', 'receiver'] as const)(
    'refuses a %s child without ever calling listen, and publishes nothing',
    async (role) => {
      process.env[ROLE_VARIABLE] = role
      // The fake bind: records the call, then reports the bind failure so a late refusal cannot
      // hang. The listening callback is never invoked, so no socket is ever serving.
      const listen = vi.spyOn(HttpServer.prototype, 'listen').mockImplementation(function (this: HttpServer) {
        this.emit('error', new Error('listen was reached in a child process'))
        return this
      })
      const jobSpawner = new RecordingSpawner()
      const receiverSpawner = new RecordingSpawner()
      const store = new CanonStore(':memory:')
      const bridge = new KyberBridge({ store, reopenCheckIntervalMs: 0 })
      try {
        const refusal = await runWebDashboard({
          port: 0,
          open: false,
          writeStdout: () => {},
          kyberBridge: bridge,
          store,
          stateDir,
          program: 'kyberdash-test',
          jobSpawner,
          receiverSpawner,
          receiverProber: { probe: async (): Promise<ReceiverProbe> => 'refused' },
          maintenance: async () => {},
        }).then(
          () => null,
          (err: unknown) => err,
        )

        expect(listen, `a ${role} child reached listen: the refusal came after the socket was bound`).not.toHaveBeenCalled()
        expect(refusal, `a ${role} child must be refused with SpawnedChildMayNotHostError`).toMatchObject({
          name: 'SpawnedChildMayNotHostError',
        })
        expect(existsSync(join(stateDir, 'server.json')), `a ${role} child published a server record`).toBe(false)
        expect(jobSpawner.calls, `a ${role} child spawned job children`).toEqual([])
        expect(receiverSpawner.calls, `a ${role} child spawned receiver children`).toEqual([])
      } finally {
        bridge.close()
        store.close()
      }
    },
  )
})
