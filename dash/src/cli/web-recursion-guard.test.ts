// Storm guard (T2): a process spawned as a job or receiver child must never host the dashboard.
//
// Failure this prevents: a dashboard's JobHost spawned a child that booted another dashboard,
// which spawned another, until 536 node processes were running and three developer Macs
// crashed. A child stamped with a job or receiver role runs one piece of work and exits; if it
// reaches runWebDashboard it has become a host, and the recursion starts again.
import { EventEmitter } from 'node:events'
import type { ChildProcess } from 'node:child_process'
import type { Server } from 'node:http'
import { connect as connectSocket, createServer as createSocketServer, type AddressInfo } from 'node:net'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import * as spawnGuard from '../process/spawn-guard.js'
import { allowRealSpawns, type SpawnFn } from '../process/spawn-guard.js'
import { CanonStore } from '../canon/store.js'
import { createNodeSpawner, type ChildResult, type JobChild, type JobSpawner } from '../jobs/host.js'
import type { ReceiverProbe } from '../jobs/receiver-host.js'
import { KyberBridge } from '../server/bridge.js'
import { runWebDashboard } from './web.js'

const ROLE_VARIABLES = ['KYBERDASH_SPAWN_ROLE', 'KYBERDASH_SPAWN_DEPTH'] as const
const priorEnvironment = new Map<string, string | undefined>()
let stateDir = ''

beforeEach(() => {
  for (const name of ROLE_VARIABLES) {
    priorEnvironment.set(name, process.env[name])
    delete process.env[name]
  }
  stateDir = mkdtempSync(join(tmpdir(), 'kyber-web-recursion-'))
})

afterEach(() => {
  for (const name of ROLE_VARIABLES) {
    const prior = priorEnvironment.get(name)
    if (prior === undefined) delete process.env[name]
    else process.env[name] = prior
  }
  priorEnvironment.clear()
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

/** A port number that was free a moment ago. Used so a test can prove nothing listens on it. */
function reserveFreePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const probe = createSocketServer()
    probe.once('error', reject)
    probe.listen(0, '127.0.0.1', () => {
      const { port } = probe.address() as AddressInfo
      probe.close(() => resolve(port))
    })
  })
}

/** True when something accepts a TCP connection on 127.0.0.1:port. */
function isListening(port: number): Promise<boolean> {
  return new Promise((resolve) => {
    const socket = connectSocket({ host: '127.0.0.1', port })
    socket.once('connect', () => {
      socket.destroy()
      resolve(true)
    })
    socket.once('error', () => resolve(false))
  })
}

function closeServer(server: Server): Promise<void> {
  return new Promise((resolve) => {
    server.close(() => resolve())
  })
}

/** A child process stand-in that closes cleanly on the next turn of the event loop. */
function fakeChildProcess(): ChildProcess {
  const child = new EventEmitter() as EventEmitter & { kill: () => boolean; stderr: null }
  child.kill = () => true
  child.stderr = null
  setImmediate(() => { child.emit('close', 0) })
  return child as unknown as ChildProcess
}

describe('storm guard: a job or receiver child may not host the dashboard', () => {
  it.each(['job', 'receiver'] as const)(
    'refuses to start the dashboard in a %s child, spawns nothing, and leaves its port closed',
    async (role) => {
      process.env.KYBERDASH_SPAWN_ROLE = role
      const port = await reserveFreePort()
      const store = new CanonStore(':memory:')
      const bridge = new KyberBridge({ store, reopenCheckIntervalMs: 0 })
      const jobSpawner = new RecordingSpawner()
      const receiverSpawner = new RecordingSpawner()
      let started: Server | undefined
      try {
        const refusal = await runWebDashboard({
          port,
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
          (server) => {
            started = server
            return null
          },
          (err: unknown) => err,
        )

        expect(started, `a ${role} child started a dashboard server`).toBeUndefined()
        expect(jobSpawner.calls, `a ${role} child spawned job children`).toEqual([])
        expect(receiverSpawner.calls, `a ${role} child spawned receiver children`).toEqual([])
        expect(await isListening(port), `a ${role} child left its port open`).toBe(false)
        expect(
          (spawnGuard as Record<string, unknown>)['SpawnedChildMayNotHostError'],
          'src/process/spawn-guard.ts must export SpawnedChildMayNotHostError',
        ).toBeTypeOf('function')
        expect(refusal, `a ${role} child must be refused with SpawnedChildMayNotHostError`).toMatchObject({
          name: 'SpawnedChildMayNotHostError',
        })
      } finally {
        if (started !== undefined) await closeServer(started)
        bridge.close()
        store.close()
      }
    },
  )

  it('with no role set, starts the dashboard and spawns its first scheduled refresh in the job role', async () => {
    // The fake spawn function records the call; no process starts. The spawn guard is deny-by-
    // default in tests, so the test opts in explicitly, and the opt-in is scoped to this test.
    allowRealSpawns('the spawn function is a recorder; no process is started')
    const recorded: Array<{ program: string; args: readonly string[]; env: NodeJS.ProcessEnv }> = []
    const recordingSpawnFn: SpawnFn<ChildProcess | undefined> = (program, args, options) => {
      recorded.push({ program, args: [...args], env: { ...options.env } })
      return fakeChildProcess()
    }
    const store = new CanonStore(':memory:')
    const bridge = new KyberBridge({ store, reopenCheckIntervalMs: 0 })
    let server: Server | undefined
    try {
      server = await runWebDashboard({
        port: 0,
        open: false,
        writeStdout: () => {},
        kyberBridge: bridge,
        store,
        stateDir,
        program: 'kyberdash-test',
        jobSpawner: createNodeSpawner(recordingSpawnFn),
        receiverSpawner: new RecordingSpawner(),
        receiverProber: { probe: async (): Promise<ReceiverProbe> => 'refused' },
        maintenance: async () => {},
      })

      const refresh = recorded.find((call) => call.args.join(' ') === 'dash refresh --trigger scheduled')
      expect(refresh, 'the first tick must spawn dash refresh --trigger scheduled').toBeDefined()
      expect(
        refresh?.env.KYBERDASH_SPAWN_ROLE,
        'the scheduled refresh child must carry the job role so it cannot host a dashboard',
      ).toBe('job')
    } finally {
      if (server !== undefined) await closeServer(server)
      bridge.close()
      store.close()
    }
  })
})
