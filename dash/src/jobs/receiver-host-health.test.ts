// ReceiverHost health propagation: the poll carries the real receiver state — endpoint,
// paused, accepted time, and the five-minute window — into the coverage indicator without
// fabricating it. Statuses: off, on-but-refused, hosted, foreign-but-reachable,
// port-held-by-other, and unknown are all proven, alongside every green-predicate edge.

import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { SETTING_KEYS, writeSetting } from '../settings/shared-settings.js'
import { isReceiverHealthy, ReceiverHost, type ReceiverHealthDetail, type ReceiverProbe, type ReceiverState } from './receiver-host.js'

class FakeChild {
  killed = false
  readonly exited: Promise<{ code: number | null; stderr: string }>
  private resolveExit!: (result: { code: number | null; stderr: string }) => void

  constructor(readonly program: string, readonly args: readonly string[]) {
    this.exited = new Promise((resolve) => {
      this.resolveExit = resolve
    })
  }

  kill(): void {
    this.killed = true
    this.resolveExit({ code: null, stderr: '' })
  }
}

class FakeSpawner {
  readonly children: FakeChild[] = []
  spawnAttempts = 0

  spawn(program: string, args: readonly string[]): FakeChild {
    this.spawnAttempts++
    const child = new FakeChild(program, args)
    this.children.push(child)
    return child
  }

  get last(): FakeChild {
    return this.children[this.children.length - 1]
  }
}

/** Legacy prober: the enum only, no health contract — detail must read as unknown. */
class EnumProber {
  readonly urls: string[] = []
  constructor(public reply: ReceiverProbe) {}

  async probe(url: string): Promise<ReceiverProbe> {
    this.urls.push(url)
    return this.reply
  }
}

class HealthProber {
  readonly urls: string[] = []
  constructor(public answer: { probe: ReceiverProbe; health: ReceiverHealthDetail | null }) {}

  async probe(url: string): Promise<ReceiverProbe> {
    this.urls.push(url)
    return this.answer.probe
  }

  async probeHealth(url: string): Promise<{ probe: ReceiverProbe; health: ReceiverHealthDetail | null }> {
    this.urls.push(url)
    return this.answer
  }
}

function cleanHealth(): ReceiverHealthDetail {
  return {
    endpoint: 'http://127.0.0.1:4318',
    listening: true,
    paused: false,
    checkedAt: '2026-10-10T20:00:00.000Z',
    lastAcceptedAt: '2026-10-10T19:59:00.000Z',
    failures5m: 0,
    lastError: null,
    lastCode: null,
    lastErrorAt: null,
  }
}

let store: CanonStore
let spawner: FakeSpawner
const hosts: ReceiverHost[] = []

function makeHost(prober: EnumProber | HealthProber): ReceiverHost {
  const host = new ReceiverHost({ store, program: 'kyberdash', prober, spawner })
  hosts.push(host)
  return host
}

beforeEach(() => {
  store = new CanonStore(':memory:')
  spawner = new FakeSpawner()
})

afterEach(async () => {
  for (const host of hosts.splice(0)) await host.close()
  store.close()
})

describe('ReceiverHost receiver state: listener off/on', () => {
  it('reports disabled, unknown, and unhealthy while the setting is off', async () => {
    const host = makeHost(new EnumProber('refused'))

    await host.poll()
    const state = host.getReceiverState()

    expect(state.enabled).toBe(false)
    expect(state.status).toBe('not-reachable')
    expect(state.endpoint).toBeNull()
    expect(state.hostedByUs).toBeNull()
    expect(state.paused).toBeNull()
    expect(state.failures5m).toBeNull()
    expect(state.lastAcceptedAt).toBeNull()
    expect(typeof state.checkedAt).toBe('string')
    expect(state.healthy).toBe(false)
  })

  it('reports enabled but not green when nothing listens', async () => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    const host = makeHost(new EnumProber('refused'))

    await host.poll()
    const state = host.getReceiverState()

    expect(state.enabled).toBe(true)
    expect(state.status).toBe('not-reachable')
    expect(state.listening).toBe(false)
    expect(state.hostedByUs).toBeNull()
    expect(state.healthy).toBe(false)
  })

  it('stays red when the setting is off even though a receiver answers', async () => {
    const host = makeHost(new HealthProber({ probe: 'kyberdash-receiver', health: cleanHealth() }))

    await host.poll()
    const state = host.getReceiverState()

    expect(state.enabled).toBe(false)
    expect(state.status).toBe('reachable')
    expect(state.hostedByUs).toBe(false)
    expect(state.paused).toBe(false)
    expect(state.healthy).toBe(false)
  })
})

describe('ReceiverHost receiver state: hosted and reachable', () => {
  it('is green once the receiver it started answers clean and unpaused', async () => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    const prober = new HealthProber({ probe: 'refused', health: null })
    const host = makeHost(prober)

    await host.poll()
    expect(spawner.children).toHaveLength(1)

    prober.answer = { probe: 'kyberdash-receiver', health: cleanHealth() }
    await host.poll()
    const state = host.getReceiverState()

    expect(state.status).toBe('hosted')
    expect(state.hostedByUs).toBe(true)
    expect(state.listening).toBe(true)
    expect(state.endpoint).toBe('http://127.0.0.1:4318')
    expect(state.paused).toBe(false)
    expect(state.lastAcceptedAt).toBe('2026-10-10T19:59:00.000Z')
    expect(state.failures5m).toBe(0)
    expect(state.healthy).toBe(true)
  })

  it('is green for a foreign but reachable clean receiver (accepting is what matters)', async () => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    const host = makeHost(new HealthProber({ probe: 'kyberdash-receiver', health: cleanHealth() }))

    await host.poll()
    const state = host.getReceiverState()

    expect(state.status).toBe('reachable')
    expect(state.hostedByUs).toBe(false)
    expect(state.healthy).toBe(true)
  })

  it('is red when paused, with the paused state explicit', async () => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    const host = makeHost(
      new HealthProber({ probe: 'kyberdash-receiver', health: { ...cleanHealth(), paused: true } }),
    )

    await host.poll()

    expect(host.getReceiverState().paused).toBe(true)
    expect(host.getReceiverState().healthy).toBe(false)
  })

  it('is red with window failures propagated, naming the last error', async () => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    const host = makeHost(
      new HealthProber({
        probe: 'kyberdash-receiver',
        health: {
          ...cleanHealth(),
          failures5m: 2,
          lastError: 'body exceeds the limit',
          lastCode: 'OTLP_PAYLOAD_TOO_LARGE',
          lastErrorAt: '2026-10-10T19:58:00.000Z',
        },
      }),
    )

    await host.poll()
    const state = host.getReceiverState()

    expect(state.failures5m).toBe(2)
    expect(state.lastCode).toBe('OTLP_PAYLOAD_TOO_LARGE')
    expect(state.lastError).toBe('body exceeds the limit')
    expect(state.lastErrorAt).toBe('2026-10-10T19:58:00.000Z')
    expect(state.healthy).toBe(false)
  })
})

describe('ReceiverHost receiver state: never fabricated', () => {
  it('reads detail as unknown — and unhealthy — for a legacy enum-only prober', async () => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    const host = makeHost(new EnumProber('kyberdash-receiver'))

    await host.poll()
    const state = host.getReceiverState()

    expect(state.status).toBe('reachable')
    expect(state.paused).toBeNull()
    expect(state.failures5m).toBeNull()
    expect(state.lastAcceptedAt).toBeNull()
    // Fail-closed: an unobservable window is not a clean window.
    expect(state.healthy).toBe(false)
  })

  it('stays unhealthy when an answering receiver omits the window (older build)', async () => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    const host = makeHost(
      new HealthProber({ probe: 'kyberdash-receiver', health: { paused: false } }),
    )

    await host.poll()
    const state = host.getReceiverState()

    expect(state.paused).toBe(false)
    expect(state.failures5m).toBeNull()
    expect(state.healthy).toBe(false)
  })

  it('reports port-held-by-other with no receiver facts and never green', async () => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    const host = makeHost(new HealthProber({ probe: 'other-service', health: null }))

    await host.poll()
    const state = host.getReceiverState()

    expect(state.status).toBe('port-held-by-other')
    expect(state.endpoint).toBeNull()
    expect(state.hostedByUs).toBeNull()
    expect(state.healthy).toBe(false)
  })

  it('drops stale detail when the receiver stops answering', async () => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    const prober = new HealthProber({ probe: 'kyberdash-receiver', health: cleanHealth() })
    const host = makeHost(prober)
    await host.poll()
    expect(host.getReceiverState().healthy).toBe(true)

    prober.answer = { probe: 'refused', health: null }
    await host.poll()
    const state = host.getReceiverState()

    expect(state.status).toBe('not-reachable')
    expect(state.endpoint).toBeNull()
    expect(state.paused).toBeNull()
    expect(state.healthy).toBe(false)
  })

  it('reports unknown and unhealthy on an indeterminate probe', async () => {
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
    const host = makeHost(new EnumProber('indeterminate'))

    await host.poll()

    expect(host.getReceiverState().status).toBe('unknown')
    expect(host.getReceiverState().healthy).toBe(false)
  })
})

describe('isReceiverHealthy: the approved rule', () => {
  function base(): Omit<ReceiverState, 'healthy'> {
    return {
      enabled: true,
      endpoint: 'http://127.0.0.1:4318',
      status: 'hosted',
      listening: true,
      hostedByUs: true,
      paused: false,
      checkedAt: '2026-10-10T20:00:00.000Z',
      lastAcceptedAt: '2026-10-10T19:59:00.000Z',
      failures5m: 0,
      lastError: null,
      lastCode: null,
      lastErrorAt: null,
    }
  }

  it('is green only for an enabled, accepting, unpaused receiver with a clean window', () => {
    expect(isReceiverHealthy(base())).toBe(true)
    expect(isReceiverHealthy({ ...base(), status: 'reachable', hostedByUs: false })).toBe(true)
  })

  it('is red when off, paused, failing, or not answering', () => {
    expect(isReceiverHealthy({ ...base(), enabled: false })).toBe(false)
    expect(isReceiverHealthy({ ...base(), paused: true })).toBe(false)
    expect(isReceiverHealthy({ ...base(), failures5m: 1 })).toBe(false)
    expect(isReceiverHealthy({ ...base(), status: 'not-reachable' })).toBe(false)
    expect(isReceiverHealthy({ ...base(), status: 'port-held-by-other' })).toBe(false)
    expect(isReceiverHealthy({ ...base(), status: 'unknown' })).toBe(false)
  })

  it('is red when paused or window facts are unobservable', () => {
    expect(isReceiverHealthy({ ...base(), paused: null })).toBe(false)
    expect(isReceiverHealthy({ ...base(), failures5m: null })).toBe(false)
  })
})
