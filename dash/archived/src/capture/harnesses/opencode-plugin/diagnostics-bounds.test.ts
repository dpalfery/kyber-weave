// Per-session state and the foreign-event buffer are bounded (oldest dropped). The bounds are pinned by
// behaviour: a flood must not stop a later, complete session from emitting its exact golden spans.

import { describe, expect, it } from 'vitest'

import { OPENCODE_KEYS } from './contract.js'
import { createSetup } from './entry.js'
import {
  OWNED_SESSION_ID,
  attributesOf,
  bus,
  canonicalText,
  consentPathIn,
  createHost,
  mergeBodies,
  noSleep,
  readGolden,
  receiver,
  replayRunA,
  spansOf,
  start,
  stopPlugin,
  useTempDirectories,
  type OtlpSpan,
  type Receiver,
} from './testing.js'

const tempDir = useTempDirectories()

function options(rcv: Receiver) {
  return { fetchFn: rcv.fetch, env: {}, consentPath: consentPathIn(tempDir()), sleepFn: noSleep, randomFn: () => 0 }
}

/** The spans of the owned session, as they reached the receiver. */
function ownedSpans(rcv: Receiver): OtlpSpan[] {
  return spansOf(mergeBodies(rcv.accepted)).filter(
    (span) =>
      canonicalText(attributesOf(span).get(OPENCODE_KEYS.sessionId)) === canonicalText({ stringValue: OWNED_SESSION_ID }),
  )
}

describe('per-session state and the foreign buffer are bounded (F2)', () => {
  it('a flood of owned sessions that never finish does not stop a later complete session emitting its golden spans', async () => {
    const rcv = receiver()
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    for (let index = 0; index < 1000; index += 1) {
      const id = `ses_flood_${index}`
      await host.deliverEvent(bus.sessionCreated(id, 1000 + index))
      await host.deliverEvent(bus.executionStarted(id, 1000 + index))
    }
    await replayRunA(host)
    await stopPlugin(cleanup)
    expect(canonicalText(ownedSpans(rcv))).toBe(canonicalText(spansOf(readGolden('consent-off'))))
  })

  it('a flood of events for unowned sessions is dropped and the owned session still emits its golden spans', async () => {
    const rcv = receiver()
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    for (let index = 0; index < 5000; index += 1) {
      await host.deliverEvent(bus.executionStarted(`ses_unowned_${index}`, 2000 + index))
    }
    await replayRunA(host)
    await stopPlugin(cleanup)
    expect(JSON.stringify(rcv.attempts.map((attempt) => attempt.body))).not.toContain('ses_unowned_')
    expect(canonicalText(ownedSpans(rcv))).toBe(canonicalText(spansOf(readGolden('consent-off'))))
  })
})
