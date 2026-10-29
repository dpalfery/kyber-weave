// F6 regression: ReceiverHost must keep sole ownership of a starting child,
// run one poll at a time, and seal itself on close.
//
// A refused probe used to spawn a receiver even while a previous child was
// still starting (startup can outlast the 10s poll interval), replacing the
// only tracked handle; close killed just the last child; and a probe that
// resolved after close spawned a new child. These tests pin the fix: no
// second spawn while a child is starting, one shared in-flight poll, and a
// sealed close with no post-close spawn or orphaned live children.

import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { SETTING_KEYS, writeSetting } from '../settings/shared-settings.js'
import { ReceiverHost, type ReceiverProbe } from './receiver-host.js'

class FakeChild {
  killed = false
  exitedCount = 0
  readonly exited: Promise<{ code: number | null; stderr: string }>
  private resolveExit!: (result: { code: number | null; stderr: string }) => void

  constructor() {
    this.exited = new Promise((resolve) => {
      this.resolveExit = resolve
    })
  }

  kill(): void {
    this.killed = true
    this.exitedCount += 1
    this.resolveExit({ code: null, stderr: '' })
  }
}

class FakeSpawner {
  readonly children: FakeChild[] = []
  spawnAttempts = 0

  spawn(): FakeChild {
    this.spawnAttempts += 1
    const child = new FakeChild()
    this.children.push(child)
    return child as never
  }
}

class DeferredProber {
  calls = 0
  private resolveProbe!: (probe: ReceiverProbe) => void
  private gate = new Promise<ReceiverProbe>((resolve) => {
    this.resolveProbe = resolve
  })

  async probe(): Promise<ReceiverProbe> {
    this.calls += 1
    return this.gate
  }

  resolve(probe: ReceiverProbe): void {
    this.resolveProbe(probe)
    this.gate = new Promise<ReceiverProbe>((resolve) => {
      this.resolveProbe = resolve
    })
  }
}

let store: CanonStore
let spawner: FakeSpawner
let prober: DeferredProber
const hosts: ReceiverHost[] = []

function makeHost(): ReceiverHost {
  const host = new ReceiverHost({
    store,
    program: 'kyberdash',
    prober: { probe: (url: string) => prober.probe(url) },
    spawner: { spawn: (program: string, args: readonly string[]) => spawner.spawn(program, args) },
  })
  hosts.push(host)
  return host
}

beforeEach(() => {
  store = new CanonStore(':memory:')
  spawner = new FakeSpawner()
  prober = new DeferredProber()
  writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
})

afterEach(async () => {
  for (const host of hosts.splice(0)) await host.close()
  store.close()
})

describe('ReceiverHost single ownership and sealed close (F6)', () => {
  it('doesNotStartASecondReceiverWhileTheFirstIsStillStarting', async () => {
    const host = makeHost()
    const first = host.poll()
    prober.resolve('refused')
    await first
    expect(spawner.children).toHaveLength(1)

    // Still starting (slow migration before bind): refused probes must not
    // replace the only tracked handle.
    for (let i = 0; i < 3; i += 1) {
      const next = host.poll()
      prober.resolve('refused')
      await next
    }
    expect(spawner.children).toHaveLength(1)
    expect(spawner.children[0]!.killed).toBe(false)

    // Once it answers, it is hosted — still exactly one child.
    const last = host.poll()
    prober.resolve('kyberdash-receiver')
    await last
    expect(host.getStatus()).toBe('hosted')
    expect(spawner.children).toHaveLength(1)
  })

  it('runsConcurrentPollsAsOneProbeAndOneSpawnDecision', async () => {
    const host = makeHost()
    const a = host.poll()
    const b = host.poll()
    for (let i = 0; i < 100 && prober.calls === 0; i += 1) {
      await new Promise((resolve) => setTimeout(resolve, 0))
    }
    expect(prober.calls).toBe(1)
    prober.resolve('refused')
    const [delayA, delayB] = await Promise.all([a, b])
    expect(delayA).toBe(delayB)
    expect(spawner.spawnAttempts).toBe(1)
    expect(prober.calls).toBe(1)
  })

  it('spawnsNothingAfterCloseEvenIfTheProbeWasInFlight', async () => {
    const host = makeHost()
    const pending = host.poll()
    for (let i = 0; i < 100 && prober.calls === 0; i += 1) {
      await new Promise((resolve) => setTimeout(resolve, 0))
    }
    await host.close()
    // The refused answer lands after the seal: no child may be created.
    prober.resolve('refused')
    await expect(pending).resolves.toBeNull()
    expect(spawner.spawnAttempts).toBe(0)

    // Sealed means sealed: later polls probe nothing and spawn nothing.
    await host.poll()
    expect(prober.calls).toBe(1)
    expect(spawner.spawnAttempts).toBe(0)
  })

  it('closeKillsTheStartingChildAndLeavesNoneLive', async () => {
    const host = makeHost()
    const first = host.poll()
    prober.resolve('refused')
    await first
    const child = spawner.children[0]!

    await host.close()

    expect(child.killed).toBe(true)
    // Ownership ran until exit: every tracked child settled.
    await child.exited
    expect(spawner.children.every((candidate) => candidate.killed)).toBe(true)
    // Sealed: no later poll spawns again (and a sealed poll probes nothing).
    await host.poll()
    expect(spawner.spawnAttempts).toBe(1)
    expect(prober.calls).toBe(1)
  })

  it('doesNotSpawnAgainstAnIndeterminateProbeWhileAChildIsStarting', async () => {
    const host = makeHost()
    const first = host.poll()
    prober.resolve('refused')
    await first
    expect(spawner.children).toHaveLength(1)

    const second = host.poll()
    prober.resolve('indeterminate')
    await second
    expect(spawner.children).toHaveLength(1)
    expect(host.getStatus()).toBe('unknown')
  })
})
