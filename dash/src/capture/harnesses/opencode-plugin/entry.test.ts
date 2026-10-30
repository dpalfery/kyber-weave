// The OpenCode 2.x loadable entry (entry.ts): the default export and the `setup` seam.
//
// `setup` registers the V2 hook set on the host, subscribes to the event stream, and returns a
// cleanup. It must never fail the host: refused registrations, throwing subscriptions and inputs that
// name no session are all absorbed.

import { describe, expect, it } from 'vitest'

import entry, { createSetup } from './entry.js'
import { OPENCODE_PLUGIN_ID } from './plugin.js'
import {
  createHost,
  isCleanup,
  noSleep,
  receiver,
  start,
  stopPlugin,
  useTempDirectories,
  consentPathIn,
} from './testing.js'

const tempDir = useTempDirectories()
const SESSION_HOOKS = ['context', 'model.request', 'prompt']
const TOOL_HOOKS = ['execute.after', 'execute.before']

function options(rcv = receiver()) {
  return { fetchFn: rcv.fetch, env: {}, consentPath: consentPathIn(tempDir()), sleepFn: noSleep, randomFn: () => 0 }
}

describe('default export matches the 2.x loader schema { id, setup }', () => {
  it('has exactly the keys id and setup, with the plugin id', () => {
    expect(Object.keys(entry).sort()).toEqual(['id', 'setup'])
    expect(entry.id).toBe(OPENCODE_PLUGIN_ID)
    expect(typeof entry.setup).toBe('function')
  })
})

describe('setup registers the V2 hook set and one event subscription', () => {
  it('registers prompt, context and model.request on the session (never experimental.idle), and execute.before/after on tools', async () => {
    const host = createHost()
    await start(createSetup(options()), host)
    expect([...host.sessionHooks].sort()).toEqual(SESSION_HOOKS)
    expect([...host.toolHooks].sort()).toEqual(TOOL_HOOKS)
    expect(host.subscriptions).toBe(1)
  })

  it('returns a cleanup that stops the event subscription', async () => {
    const host = createHost()
    const cleanup = await start(createSetup(options()), host)
    expect(isCleanup(cleanup)).toBe(true)
    if (!isCleanup(cleanup)) return
    await cleanup()
    expect(host.closedSubscriptions).toBe(1)
  })

  it('still returns a cleanup on a host with no session, tool or event surface', async () => {
    const cleanup = await start(createSetup(options()), createHost({ bare: true }))
    expect(isCleanup(cleanup)).toBe(true)
    await stopPlugin(cleanup)
  })
})

describe('setup never fails the host', () => {
  it('resolves to a cleanup when the session surface refuses registration', async () => {
    const cleanup = await start(createSetup(options()), createHost({ rejectSessionHook: true }))
    expect(isCleanup(cleanup)).toBe(true)
  })

  it('resolves to a cleanup when event subscription throws', async () => {
    const cleanup = await start(createSetup(options()), createHost({ throwOnSubscribe: true }))
    expect(isCleanup(cleanup)).toBe(true)
  })

  it('ignores hook inputs that name no session and never rejects', async () => {
    const rcv = receiver()
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    await expect(host.deliverSession('prompt', {})).resolves.toBeUndefined()
    await expect(host.deliverSession('context', null)).resolves.toBeUndefined()
    await expect(host.deliverSession('model.request', 42)).resolves.toBeUndefined()
    await expect(host.deliverTool('execute.before', {})).resolves.toBeUndefined()
    await stopPlugin(cleanup)
    expect(rcv.attempts).toEqual([])
  })
})
