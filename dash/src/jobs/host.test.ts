// JobHost is the one place every background job runs (architecture rule R1):
// the tray and web are display layers, and the `kyberdash web` server hosts the
// scheduled refresh, the maintenance pass, and the manual/import/clean children.
// This is the port of the tray's scheduler.rs. Everything here uses a fake clock,
// a fake spawner, an in-memory canon store, and a temp state dir - never the home
// canon.db, server.json, or jobs.lock.

import { existsSync, readFileSync, writeFileSync } from 'fs'
import { mkdtemp, rm } from 'fs/promises'
import { tmpdir } from 'os'
import { join } from 'path'

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { SETTING_KEYS, writeSetting } from '../settings/shared-settings.js'
import { JobHost } from './host.js'

const T0 = 1_700_000_000_000
const MINUTE = 60_000
const TICK_MS = 1000
// Alive for the whole run (the test runner's parent) but never this process, which the
// lease treats as "not a foreign holder".
const LIVE_FOREIGN_PID = process.ppid
// Far beyond any real pid space: signal 0 reports ESRCH.
const DEAD_PID = 2_147_483_646

type ChildResult = { code: number | null; stderr: string }

class FakeChild {
  killed = false
  readonly exited: Promise<ChildResult>
  private resolveExit!: (result: ChildResult) => void

  constructor(readonly program: string, readonly args: readonly string[]) {
    this.exited = new Promise(resolve => { this.resolveExit = resolve })
  }

  exit(code: number | null, stderr = ''): void { this.resolveExit({ code, stderr }) }

  kill(): void {
    this.killed = true
    this.resolveExit({ code: null, stderr: '' })
  }
}

class FakeSpawner {
  readonly children: FakeChild[] = []

  spawn(program: string, args: readonly string[]): FakeChild {
    const child = new FakeChild(program, args)
    this.children.push(child)
    return child
  }

  get last(): FakeChild { return this.children[this.children.length - 1] }
}

class FakeClock {
  private current = T0
  private readonly intervals = new Map<number, { fn: () => void; every: number; next: number }>()
  private nextId = 1

  now(): number { return this.current }

  set(ms: number): void { this.current = ms }

  setInterval(fn: () => void, every: number): number {
    const id = this.nextId++
    this.intervals.set(id, { fn, every, next: this.current + every })
    return id
  }

  clearInterval(id: number): void { this.intervals.delete(id) }

  get activeIntervals(): number { return this.intervals.size }

  advance(ms: number): void {
    const end = this.current + ms
    for (;;) {
      const due = [...this.intervals.values()].filter(i => i.next <= end).sort((a, b) => a.next - b.next)[0]
      if (!due) break
      this.current = due.next
      due.next += due.every
      due.fn()
    }
    this.current = end
  }
}

let stateDir: string
let store: CanonStore
let clock: FakeClock
let spawner: FakeSpawner
let maintenanceRuns: number
const hosts: JobHost[] = []

function makeHost(overrides: { stateDir?: string } = {}): JobHost {
  const host = new JobHost({
    store,
    stateDir: overrides.stateDir ?? stateDir,
    program: 'kyberdash',
    clock,
    spawner,
    tickIntervalMs: TICK_MS,
    maintenance: () => { maintenanceRuns++ },
  })
  hosts.push(host)
  return host
}

beforeEach(async () => {
  stateDir = await mkdtemp(join(tmpdir(), 'kyberdash-jobhost-'))
  store = new CanonStore(':memory:')
  clock = new FakeClock()
  spawner = new FakeSpawner()
  maintenanceRuns = 0
})

afterEach(async () => {
  for (const host of hosts.splice(0)) await host.close()
  store.close()
  await rm(stateDir, { recursive: true, force: true })
})

async function finishLast(code: number, stderr = ''): Promise<void> {
  spawner.last.exit(code, stderr)
  await vi.waitFor(() => expect(['running']).not.toContain(hosts[0].getStatus().state))
}

describe('JobHost: scheduled refresh', () => {
  it('runs a scheduled refresh at start and not again inside the cadence', async () => {
    const host = makeHost()

    await host.start()
    expect(spawner.children).toHaveLength(1)
    expect(spawner.last.program).toBe('kyberdash')
    expect(spawner.last.args).toEqual(['dash', 'refresh', '--trigger', 'scheduled'])
    await finishLast(0)

    clock.set(T0 + 4 * MINUTE + 59_000)
    await host.tick()

    expect(spawner.children).toHaveLength(1)
  })

  it('runs again once the default five-minute cadence elapses', async () => {
    const host = makeHost()
    await host.start()
    await finishLast(0)

    clock.set(T0 + 5 * MINUTE)
    await host.tick()

    expect(spawner.children).toHaveLength(2)
    expect(spawner.last.args).toEqual(['dash', 'refresh', '--trigger', 'scheduled'])
  })

  it('arms an interval on start so ticks happen without a caller', async () => {
    const host = makeHost()
    await host.start()
    await finishLast(0)

    clock.advance(5 * MINUTE)

    await vi.waitFor(() => expect(spawner.children).toHaveLength(2))
  })

  it('applies a cadence change on the next tick', async () => {
    const host = makeHost()
    await host.start()
    await finishLast(0)

    writeSetting(store, SETTING_KEYS.refreshCadenceMinutes, 1)
    clock.set(T0 + MINUTE)
    await host.tick()

    expect(spawner.children).toHaveLength(2)
    await finishLast(0)

    writeSetting(store, SETTING_KEYS.refreshCadenceMinutes, 10)
    clock.set(T0 + MINUTE + 9 * MINUTE)
    await host.tick()
    expect(spawner.children).toHaveLength(2)

    clock.set(T0 + MINUTE + 10 * MINUTE)
    await host.tick()
    expect(spawner.children).toHaveLength(3)
  })

  it('runs only one job at a time', async () => {
    const host = makeHost()
    await host.start()

    clock.set(T0 + 20 * MINUTE)
    await host.tick()

    expect(spawner.children).toHaveLength(1)
    expect(host.getStatus().state).toBe('running')
  })

  it('does not run a clock that moved backwards as a refresh storm', async () => {
    const host = makeHost()
    await host.start()
    await finishLast(0)

    clock.set(T0 - 10 * MINUTE)
    await host.tick()

    expect(spawner.children).toHaveLength(1)
  })
})

describe('JobHost: pause stops only scheduled jobs', () => {
  beforeEach(() => { writeSetting(store, SETTING_KEYS.jobsPaused, 'on') })

  it('starts no scheduled refresh child while paused, at start or on later ticks', async () => {
    const host = makeHost()

    await host.start()
    clock.set(T0 + 30 * MINUTE)
    await host.tick()

    expect(spawner.children).toHaveLength(0)
    expect(host.getStatus().paused).toBe(true)
  })

  it('still runs the maintenance pass while paused', async () => {
    const host = makeHost()
    await host.start()
    const afterStart = maintenanceRuns

    await host.tick()

    expect(afterStart).toBeGreaterThanOrEqual(1)
    expect(maintenanceRuns).toBeGreaterThan(afterStart)
  })

  it.each(['web', 'tray'] as const)('still starts a manual refresh requested by %s', async (trigger) => {
    const host = makeHost()
    await host.start()

    const outcome = host.runNow(trigger)
    await vi.waitFor(() => expect(spawner.children).toHaveLength(1))
    expect(spawner.last.args).toEqual(['dash', 'refresh', '--trigger', trigger])
    spawner.last.exit(0)

    expect(await outcome).toMatchObject({ outcome: 'succeeded' })
  })

  it.each(['import', 'clean'] as const)('still starts a %s job', async (kind) => {
    const host = makeHost()
    await host.start()

    const outcome = host.runJob(kind)
    await vi.waitFor(() => expect(spawner.children).toHaveLength(1))
    expect(spawner.last.args).toContain(kind)
    spawner.last.exit(0)

    expect(await outcome).toMatchObject({ outcome: 'succeeded' })
  })

  it('resumes scheduled refreshes on the next tick once pause is turned off', async () => {
    const host = makeHost()
    await host.start()
    expect(spawner.children).toHaveLength(0)

    writeSetting(store, SETTING_KEYS.jobsPaused, 'off')
    await host.tick()

    expect(spawner.children).toHaveLength(1)
    expect(host.getStatus().paused).toBe(false)
  })

  it('reports no next due time while paused', async () => {
    const host = makeHost()
    await host.start()

    expect(host.getStatus().nextDueAt).toBeNull()
  })
})

describe('JobHost: manual runs and exclusion', () => {
  it('declines a manual refresh while another job is running', async () => {
    const host = makeHost()
    await host.start()

    expect(await host.runNow('web')).toMatchObject({ outcome: 'declined' })
    expect(await host.runJob('import')).toMatchObject({ outcome: 'declined' })

    expect(spawner.children).toHaveLength(1)
  })
})

describe('JobHost: outcomes of a finished child', () => {
  it('reports running-elsewhere, not a failure, when the child exits 3', async () => {
    const host = makeHost()
    await host.start()

    await finishLast(3)

    const status = host.getStatus()
    expect(status.state).toBe('running-elsewhere')
    expect(status.lastFailure).toBeNull()
    expect(status.lastSuccessAt).toBeNull()
  })

  it('tries again at the next cadence after a busy exit rather than staying blocked', async () => {
    const host = makeHost()
    await host.start()
    await finishLast(3)

    clock.set(T0 + 5 * MINUTE)
    await host.tick()

    expect(spawner.children).toHaveLength(2)
  })

  it('reports failed with the reason and keeps the last success time when the child exits 1', async () => {
    const host = makeHost()
    await host.start()
    await finishLast(0)
    const success = host.getStatus().lastSuccessAt
    expect(success).toBe(new Date(T0).toISOString())

    clock.set(T0 + 10 * MINUTE)
    await host.tick()
    await finishLast(1, 'canon.db is locked by another writer\nat stack frame\n')

    const status = host.getStatus()
    expect(status.state).toBe('failed')
    expect(status.lastSuccessAt).toBe(success)
    expect(status.lastFailure).toContain('canon.db is locked by another writer')
    expect(status.lastFailure).not.toContain('stack frame')
    expect(status.lastFailure).toContain(new Date(T0 + 10 * MINUTE).toISOString())
  })

  it('records the success time and the next due time after a clean exit', async () => {
    const host = makeHost()
    await host.start()

    await finishLast(0)

    const status = host.getStatus()
    expect(status.state).toBe('idle')
    expect(status.lastSuccessAt).toBe(new Date(T0).toISOString())
    expect(status.lastFailure).toBeNull()
    expect(status.nextDueAt).toBe(new Date(T0 + 5 * MINUTE).toISOString())
    expect(status.paused).toBe(false)
  })

  it('moves the next due time when the cadence changes', async () => {
    const host = makeHost()
    await host.start()
    await finishLast(0)

    writeSetting(store, SETTING_KEYS.refreshCadenceMinutes, 15)
    await host.tick()

    expect(host.getStatus().nextDueAt).toBe(new Date(T0 + 15 * MINUTE).toISOString())
  })
})

describe('JobHost: the jobs.lock lease', () => {
  const lockPath = (): string => join(stateDir, 'jobs.lock')
  const writeHolder = (pid: number): void => {
    writeFileSync(lockPath(), JSON.stringify({ pid, token: 'foreign-token', at: Date.now() }))
  }

  it('takes the lease in the state dir and runs jobs', async () => {
    const host = makeHost()

    await host.start()

    expect(existsSync(lockPath())).toBe(true)
    expect(host.getStatus().hostedElsewhere).toBe(false)
    expect(spawner.children).toHaveLength(1)
  })

  it('runs nothing and reports hostedElsewhere while a live process holds the lease', async () => {
    writeHolder(LIVE_FOREIGN_PID)
    const host = makeHost()

    await host.start()
    clock.set(T0 + 30 * MINUTE)
    await host.tick()

    expect(host.getStatus().hostedElsewhere).toBe(true)
    expect(spawner.children).toHaveLength(0)
    expect(maintenanceRuns).toBe(0)
    expect(JSON.parse(readFileSync(lockPath(), 'utf-8')).pid).toBe(LIVE_FOREIGN_PID)
  })

  it('declines manual runs while hosted elsewhere', async () => {
    writeHolder(LIVE_FOREIGN_PID)
    const host = makeHost()
    await host.start()

    expect(await host.runNow('web')).toMatchObject({ outcome: 'hosted-elsewhere' })
    expect(await host.runJob('clean')).toMatchObject({ outcome: 'hosted-elsewhere' })

    expect(spawner.children).toHaveLength(0)
  })

  it('reclaims a lease whose holder is dead', async () => {
    writeHolder(DEAD_PID)
    const host = makeHost()

    await host.start()

    expect(host.getStatus().hostedElsewhere).toBe(false)
    expect(spawner.children).toHaveLength(1)
    expect(JSON.parse(readFileSync(lockPath(), 'utf-8')).pid).toBe(process.pid)
  })

  it('takes over on a later tick once a live holder has gone', async () => {
    writeHolder(LIVE_FOREIGN_PID)
    const host = makeHost()
    await host.start()
    expect(spawner.children).toHaveLength(0)

    writeHolder(DEAD_PID)
    clock.set(T0 + MINUTE)
    await host.tick()

    expect(host.getStatus().hostedElsewhere).toBe(false)
    expect(spawner.children).toHaveLength(1)
  })

  it('releases the lease on close so another host can take it', async () => {
    const host = makeHost()
    await host.start()

    await host.close()

    expect(existsSync(lockPath())).toBe(false)
  })
})

describe('JobHost: shutdown', () => {
  it('kills the running child on close', async () => {
    const host = makeHost()
    await host.start()
    const child = spawner.last

    await host.close()

    expect(child.killed).toBe(true)
  })

  it('stops ticking after close', async () => {
    const host = makeHost()
    await host.start()
    await finishLast(0)

    await host.close()

    expect(clock.activeIntervals).toBe(0)
    clock.set(T0 + 60 * MINUTE)
    await host.tick()
    expect(spawner.children).toHaveLength(1)
  })

  it('does not record a killed child as a failed refresh', async () => {
    const host = makeHost()
    await host.start()
    await finishLast(0)
    clock.set(T0 + 5 * MINUTE)
    await host.tick()

    await host.close()

    const status = host.getStatus()
    expect(status.lastFailure).toBeNull()
    expect(status.lastSuccessAt).toBe(new Date(T0).toISOString())
  })
})
