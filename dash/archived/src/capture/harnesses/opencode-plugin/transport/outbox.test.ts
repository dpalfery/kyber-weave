// Outbox delivery mechanics (T5), driven through the setup seam and the recording receiver. The
// outbox keeps its contract: a refused batch is halved on 429, and a transport that throws
// synchronously is absorbed and the batch re-sent. Every span still arrives once, in order.

import { describe, expect, it } from 'vitest'

import { createSetup } from '../entry.js'
import {
  consentPathIn,
  createHost,
  expectGoldenWire,
  mergeBodies,
  noSleep,
  receiver,
  replayRunA,
  spansOf,
  start,
  stopPlugin,
  useTempDirectories,
  type Receiver,
} from '../testing.js'

const tempDir = useTempDirectories()

function options(rcv: Receiver) {
  return { fetchFn: rcv.fetch, env: {}, consentPath: consentPathIn(tempDir()), sleepFn: noSleep, randomFn: () => 0 }
}

describe('Outbox delivery mechanics through the setup seam (T5)', () => {
  it('halves the batch a 429 refused and still delivers every span once, in order', async () => {
    const rcv = receiver((attempt) => (attempt === 1 ? 429 : 200))
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    await replayRunA(host)
    await stopPlugin(cleanup)
    expect(rcv.attempts.length).toBeGreaterThan(1)
    const refused = spansOf(rcv.attempts[0]?.body ?? { resourceSpans: [] }).length
    const retried = spansOf(rcv.attempts[1]?.body ?? { resourceSpans: [] }).length
    expect(retried).toBeLessThanOrEqual(Math.max(1, Math.floor(refused / 2)))
    expectGoldenWire(mergeBodies(rcv.accepted), 'consent-off')
  })

  it('absorbs a transport that throws synchronously and re-sends the batch', async () => {
    const rcv = receiver((attempt) => (attempt === 1 ? 'throwSync' : 200))
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    await expect(replayRunA(host)).resolves.toBeUndefined()
    await stopPlugin(cleanup)
    expectGoldenWire(mergeBodies(rcv.accepted), 'consent-off')
  })
})
