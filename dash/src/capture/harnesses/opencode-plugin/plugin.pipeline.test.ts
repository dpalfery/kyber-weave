// Installed pipeline, end to end through the seam: hook, then event stream, then outbox, then the
// loopback receiver. The receiver is a recording fetch; no socket is opened.

import { describe, expect, it } from 'vitest'

import { createSetup } from './entry.js'
import {
  LOOPBACK_TRACES_URL,
  consentPathIn,
  createHost,
  expectGoldenWire,
  mergeBodies,
  noSleep,
  receiver,
  replayRunA,
  start,
  stopPlugin,
  useTempDirectories,
  type Receiver,
} from './testing.js'

const tempDir = useTempDirectories()

function options(rcv: Receiver, extra: { requestTimeoutMs?: number } = {}) {
  return { fetchFn: rcv.fetch, env: {}, consentPath: consentPathIn(tempDir()), sleepFn: noSleep, randomFn: () => 0, ...extra }
}

describe('installed pipeline delivers V3 OTLP JSON to the loopback receiver', () => {
  it('posts OTLP JSON to the loopback V3 traces endpoint as application/json', async () => {
    const rcv = receiver()
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    await replayRunA(host)
    await stopPlugin(cleanup)
    expect(rcv.attempts.length).toBeGreaterThan(0)
    expect(rcv.attempts.every((attempt) => attempt.url === LOOPBACK_TRACES_URL)).toBe(true)
    expect(rcv.attempts.every((attempt) => attempt.contentType === 'application/json')).toBe(true)
  })

  it('a refused batch is retried and every span arrives exactly once, and no hook rejects meanwhile', async () => {
    const rcv = receiver((attempt) => (attempt <= 2 ? 503 : 200))
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    await expect(replayRunA(host)).resolves.toBeUndefined()
    await stopPlugin(cleanup)
    expect(rcv.attempts.length).toBeGreaterThan(rcv.accepted.length)
    expectGoldenWire(mergeBodies(rcv.accepted), 'consent-off')
  })

  it('a hung POST is aborted at its deadline, and flush re-sends the same spans', async () => {
    const rcv = receiver((attempt) => (attempt === 1 ? 'hang' : 200))
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv, { requestTimeoutMs: 25 })), host)
    await replayRunA(host)
    await stopPlugin(cleanup)
    expect(rcv.attempts.length).toBeGreaterThan(rcv.accepted.length)
    expectGoldenWire(mergeBodies(rcv.accepted), 'consent-off')
  })
})
