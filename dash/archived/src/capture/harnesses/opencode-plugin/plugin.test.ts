// Ownership and generations (D10): which instance emits a session, and which setup is current.
//
// An instance emits a session only when one of its own hooks fired for it, or when the event's
// `location.directory` equals its own directory. A setup for a directory supersedes the earlier setup
// for that same directory only; instances for other directories keep emitting.

import { describe, expect, it } from 'vitest'

import { OPENCODE_KEYS, SPAN_NAMES } from './contract.js'
import { createSetup } from './entry.js'
import {
  OWNED_DIRECTORY,
  attributesOf,
  bus,
  createHost,
  mergeBodies,
  noSleep,
  receiver,
  spansOf,
  start,
  stopPlugin,
  useTempDirectories,
  consentPathIn,
  type Receiver,
} from './testing.js'

const tempDir = useTempDirectories()
const OTHER_DIRECTORY = '/HOME/scratch/other-project'

function options(rcv: Receiver) {
  return { fetchFn: rcv.fetch, env: {}, consentPath: consentPathIn(tempDir()), sleepFn: noSleep, randomFn: () => 0 }
}

function requestSessionIds(rcv: Receiver): string[] {
  return spansOf(mergeBodies(rcv.accepted))
    .filter((span) => span.name === SPAN_NAMES.request)
    .map((span) => attributesOf(span).get(OPENCODE_KEYS.sessionId))
    .map((value) => (value as { stringValue: string }).stringValue)
}

describe('an instance emits only the sessions it owns', () => {
  it('never emits a session whose events name another directory and whose hooks never fired', async () => {
    const rcv = receiver()
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    await host.deliverEvent(bus.sessionCreated('ses_elsewhere', 1000, OTHER_DIRECTORY))
    await host.deliverEvent(bus.executionStarted('ses_elsewhere', 1010))
    await host.deliverEvent(bus.executionSucceeded('ses_elsewhere', 1020))
    await stopPlugin(cleanup)
    expect(rcv.attempts).toEqual([])
  })

  it('owns a session created in its own directory even before any of its hooks fires', async () => {
    const rcv = receiver()
    const host = createHost({ directory: OWNED_DIRECTORY })
    const cleanup = await start(createSetup(options(rcv)), host)
    await host.deliverEvent(bus.sessionCreated('ses_mine', 1000, OWNED_DIRECTORY))
    await host.deliverEvent(bus.executionStarted('ses_mine', 1010))
    await host.deliverEvent(bus.executionSucceeded('ses_mine', 1020))
    await stopPlugin(cleanup)
    expect(requestSessionIds(rcv)).toEqual(['ses_mine'])
  })

  it('a setup for another directory does not supersede this instance', async () => {
    const mine = receiver()
    const theirs = receiver()
    const hostMine = createHost({ directory: OWNED_DIRECTORY })
    const hostTheirs = createHost({ directory: OTHER_DIRECTORY })
    const cleanupMine = await start(createSetup(options(mine)), hostMine)
    const cleanupTheirs = await start(createSetup(options(theirs)), hostTheirs)
    expect(hostMine.closedSubscriptions).toBe(0)
    await hostMine.deliverEvent(bus.sessionCreated('ses_mine', 1000, OWNED_DIRECTORY))
    await hostMine.deliverEvent(bus.executionStarted('ses_mine', 1010))
    await hostMine.deliverEvent(bus.executionSucceeded('ses_mine', 1020))
    await hostTheirs.deliverEvent(bus.sessionCreated('ses_theirs', 1000, OTHER_DIRECTORY))
    await hostTheirs.deliverEvent(bus.executionStarted('ses_theirs', 1010))
    await hostTheirs.deliverEvent(bus.executionSucceeded('ses_theirs', 1020))
    await stopPlugin(cleanupMine)
    await stopPlugin(cleanupTheirs)
    expect(requestSessionIds(mine)).toEqual(['ses_mine'])
    expect(requestSessionIds(theirs)).toEqual(['ses_theirs'])
  })
})
