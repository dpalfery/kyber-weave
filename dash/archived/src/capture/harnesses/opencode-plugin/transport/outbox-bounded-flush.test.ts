// Bounded flush (F5): one POST at a time, and a receiver that never answers cannot hold the host's
// cleanup past a bounded wait.

import { describe, expect, it } from 'vitest'

import { createSetup } from '../entry.js'
import {
  consentPathIn,
  createHost,
  expectGoldenWire,
  isCleanup,
  mergeBodies,
  noSleep,
  receiver,
  replayRunA,
  start,
  stopPlugin,
  useTempDirectories,
  type Receiver,
} from '../testing.js'

const tempDir = useTempDirectories()

function options(rcv: Receiver, requestTimeoutMs?: number) {
  return {
    fetchFn: rcv.fetch,
    env: {},
    consentPath: consentPathIn(tempDir()),
    sleepFn: noSleep,
    randomFn: () => 0,
    ...(requestTimeoutMs !== undefined ? { requestTimeoutMs } : {}),
  }
}

describe('Outbox bounded flush (F5)', () => {
  it('keeps at most one POST in flight while a burst of spans is delivered', async () => {
    const rcv = receiver()
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    await replayRunA(host)
    await stopPlugin(cleanup)
    expect(rcv.maxInFlight).toBeLessThanOrEqual(1)
    expectGoldenWire(mergeBodies(rcv.accepted), 'consent-off')
  })

  it('settles cleanup within a bounded wait when the receiver never answers', async () => {
    const rcv = receiver(() => 'hang')
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv, 25)), host)
    if (!isCleanup(cleanup)) throw new Error('setup returned no cleanup')
    await replayRunA(host)
    let timer: ReturnType<typeof setTimeout> | undefined
    const bound = new Promise<string>((resolve) => {
      timer = setTimeout(() => resolve('still flushing after 3s'), 3000)
    })
    try {
      const outcome = await Promise.race([stopPlugin(cleanup).then(() => 'settled'), bound])
      expect(outcome).toBe('settled')
    } finally {
      if (timer !== undefined) clearTimeout(timer)
    }
  })
})
