// ReceiverHost is the web server's port of the tray's receiver.rs: it hosts
// `kyberdash otel` when settings.receiver.hosted is on, stops it when off, and
// backs off on repeated failure. Pausing jobs (settings.jobs.paused) never stops
// the receiver - only scheduled jobs pause. Fake prober and spawner; no sockets,
// no child processes, no home state.

import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { SETTING_KEYS, writeSetting } from '../settings/shared-settings.js'
import { ReceiverHost } from './receiver-host.js'
import type { ReceiverProbe } from './receiver-host.js'

class FakeChild {
  killed = false
  readonly exited: Promise<{ code: number | null; stderr: string }>
  private resolveExit!: (result: { code: number | null; stderr: string }) => void

  constructor(readonly program: string, readonly args: readonly string[]) {
    this.exited = new Promise(resolve => { this.resolveExit = resolve })
  }

  exit(code: number | null): void { this.resolveExit({ code, stderr: '' }) }

  kill(): void {
    this.killed = true
    this.resolveExit({ code: null, stderr: '' })
  }
}

class FakeSpawner {
  readonly children: FakeChild[] = []
  failWith: Error | null = null
  spawnAttempts = 0

  spawn(program: string, args: readonly string[]): FakeChild {
    this.spawnAttempts++
    if (this.failWith) throw this.failWith
    const child = new FakeChild(program, args)
    this.children.push(child)
    return child
  }

  get last(): FakeChild { return this.children[this.children.length - 1] }
}

class FakeProber {
  readonly urls: string[] = []
  constructor(public reply: ReceiverProbe) {}

  async probe(url: string): Promise<ReceiverProbe> {
    this.urls.push(url)
    return this.reply
  }
}

let store: CanonStore
let spawner: FakeSpawner
let prober: FakeProber
const hosts: ReceiverHost[] = []

function makeHost(): ReceiverHost {
  const host = new ReceiverHost({ store, program: 'kyberdash', prober, spawner })
  hosts.push(host)
  return host
}

beforeEach(() => {
  store = new CanonStore(':memory:')
  spawner = new FakeSpawner()
  prober = new FakeProber('refused')
})

afterEach(async () => {
  for (const host of hosts.splice(0)) await host.close()
  store.close()
})

describe('ReceiverHost: opt-in hosting', () => {
  it('never starts a receiver while the setting is off, and says it is not reachable', async () => {
    const host = makeHost()

    const delay = await host.poll()

    expect(spawner.spawnAttempts).toBe(0)
    expect(delay).toBeNull()
    expect(host.getStatus()).toBe('not-reachable')
  })

  it('starts `kyberdash otel` once the setting is on and the port is free', async () => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    const host = makeHost()

    const delay = await host.poll()

    expect(spawner.children).toHaveLength(1)
    expect(spawner.last.program).toBe('kyberdash')
    expect(spawner.last.args).toEqual(['otel'])
    expect(delay).toBe(0)
  })

  it('probes the loopback healthz endpoint on the OTLP port', async () => {
    const host = makeHost()

    await host.poll()

    expect(prober.urls).toEqual(['http://127.0.0.1:4318/healthz'])
  })

  it('reports hosted once the receiver it started answers, and starts no second one', async () => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    const host = makeHost()
    await host.poll()

    prober.reply = 'kyberdash-receiver'
    const delay = await host.poll()

    expect(host.getStatus()).toBe('hosted')
    expect(spawner.children).toHaveLength(1)
    expect(delay).toBeNull()
  })

  it('reports reachable, not hosted, for a receiver someone else started', async () => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    prober.reply = 'kyberdash-receiver'
    const host = makeHost()

    await host.poll()

    expect(host.getStatus()).toBe('reachable')
    expect(spawner.spawnAttempts).toBe(0)
  })

  it('stops the hosted receiver when the setting is turned off', async () => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    const host = makeHost()
    await host.poll()
    const child = spawner.last

    writeSetting(store, SETTING_KEYS.receiverHosted, 'off')
    await host.poll()

    expect(child.killed).toBe(true)
  })

  it('starts a replacement when the hosted receiver exits and the port frees up', async () => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    const host = makeHost()
    await host.poll()
    spawner.last.exit(1)
    prober.reply = 'refused'

    await host.poll()

    expect(spawner.children).toHaveLength(2)
  })

  it('kills the hosted receiver on close', async () => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    const host = makeHost()
    await host.poll()
    const child = spawner.last

    await host.close()

    expect(child.killed).toBe(true)
  })
})

describe('ReceiverHost: pausing jobs does not stop the receiver', () => {
  it('keeps hosting the receiver while settings.jobs.paused is on', async () => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    writeSetting(store, SETTING_KEYS.jobsPaused, 'on')
    const host = makeHost()

    await host.poll()

    expect(spawner.children).toHaveLength(1)
    expect(spawner.last.killed).toBe(false)
  })

  it('does not stop an already hosted receiver when jobs become paused', async () => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    const host = makeHost()
    await host.poll()
    const child = spawner.last

    writeSetting(store, SETTING_KEYS.jobsPaused, 'on')
    prober.reply = 'kyberdash-receiver'
    await host.poll()

    expect(child.killed).toBe(false)
    expect(host.getStatus()).toBe('hosted')
  })
})

describe('ReceiverHost: retry backoff', () => {
  beforeEach(() => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    spawner.failWith = new Error('spawn refused')
  })

  it('doubles the retry delay from one second on each failed start', async () => {
    const host = makeHost()

    const delays = [await host.poll(), await host.poll(), await host.poll(), await host.poll()]

    expect(delays).toEqual([1000, 2000, 4000, 8000])
  })

  it('caps the retry delay at one minute', async () => {
    const host = makeHost()
    let delay: number | null = null

    for (let attempt = 0; attempt < 12; attempt++) delay = await host.poll()

    expect(delay).toBe(60_000)
  })

  it('resets the backoff after a successful start', async () => {
    const host = makeHost()
    await host.poll()
    await host.poll()
    spawner.failWith = null
    expect(await host.poll()).toBe(0)
    spawner.last.exit(1)

    spawner.failWith = new Error('spawn refused')
    expect(await host.poll()).toBe(1000)
  })

  it('does not throw out of poll when the spawner throws', async () => {
    const host = makeHost()

    await expect(host.poll()).resolves.toBe(1000)
    expect(host.getStatus()).toBe('not-reachable')
  })
})

describe('ReceiverHost: a stranger on the port', () => {
  beforeEach(() => { writeSetting(store, SETTING_KEYS.receiverHosted, 'on') })

  it('does not start a receiver and reports the port as held by another service', async () => {
    prober.reply = 'other-service'
    const host = makeHost()

    const delay = await host.poll()

    expect(host.getStatus()).toBe('port-held-by-other')
    expect(spawner.spawnAttempts).toBe(0)
    expect(delay).toBeNull()
  })

  it('does not retry in a loop while the stranger keeps the port', async () => {
    prober.reply = 'other-service'
    const host = makeHost()

    for (let attempt = 0; attempt < 5; attempt++) expect(await host.poll()).toBeNull()

    expect(spawner.spawnAttempts).toBe(0)
  })

  it('hosts again once the stranger has gone and the port is free', async () => {
    prober.reply = 'other-service'
    const host = makeHost()
    await host.poll()

    prober.reply = 'refused'
    await host.poll()

    expect(spawner.children).toHaveLength(1)
  })
})

describe('ReceiverHost: undecided probes', () => {
  it('starts nothing and reports unknown when the probe is indeterminate', async () => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    prober.reply = 'indeterminate'
    const host = makeHost()

    const delay = await host.poll()

    expect(host.getStatus()).toBe('unknown')
    expect(spawner.spawnAttempts).toBe(0)
    expect(delay).toBeNull()
  })
})
