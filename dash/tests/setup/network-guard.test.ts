// T1 network guard (D10). Fails closed on any fetch to the live KyberDash
// receiver at 127.0.0.1:4318 or localhost:4318.
//
// Failure this prevents: entry.test.ts ran createEntryHandlers() with the
// default OTLP sender, so spooled hook events POSTed to the developer's live
// receiver. The plugin host swallows delivery errors by design, so the test
// stayed green while it posted. Only a guard that records the hit can catch it.
//
// The global assertions below only touch the guard. They never make a request
// that could reach the live receiver: the guard is checked first, and only a
// guarded fetch is called against the live receiver.
import { describe, expect, it, vi } from 'vitest'

import {
  NETWORK_GUARD_ERROR_NAME,
  NetworkGuardError,
  createNetworkGuard,
  installNetworkGuard,
  isLiveReceiverUrl,
  isNetworkGuarded,
} from './network-guard.js'

const LIVE_URL = 'http://127.0.0.1:4318/v1/traces'

const okResponse = (): Promise<Response> => Promise.resolve(new Response(null, { status: 200 }))

describe('network guard rules', () => {
  it('names only 127.0.0.1 and localhost on port 4318 as the live receiver', () => {
    expect(isLiveReceiverUrl('http://127.0.0.1:4318/v1/traces')).toBe(true)
    expect(isLiveReceiverUrl('http://localhost:4318/v1/traces')).toBe(true)
    expect(isLiveReceiverUrl('http://127.0.0.1:4319/v1/traces')).toBe(false)
    expect(isLiveReceiverUrl('http://192.0.2.9:4318/v1/traces')).toBe(false)
    expect(isLiveReceiverUrl('not a url')).toBe(false)
  })

  it('fails a live-receiver fetch closed with the named guard error and never calls the base fetch', async () => {
    const base = vi.fn(okResponse)
    const guard = createNetworkGuard(base)

    const outcome = await guard.fetch(LIVE_URL, { method: 'POST' }).then(
      () => 'resolved',
      (error: unknown) => error,
    )

    expect(outcome).toBeInstanceOf(NetworkGuardError)
    expect((outcome as Error).name).toBe(NETWORK_GUARD_ERROR_NAME)
    expect(base).not.toHaveBeenCalled()
    expect(guard.hits).toEqual([LIVE_URL])
  })

  it('delegates every other port and host to the base fetch', async () => {
    const base = vi.fn(okResponse)
    const guard = createNetworkGuard(base)

    await guard.fetch('http://127.0.0.1:4319/v1/traces')
    await guard.fetch('http://192.0.2.9:4318/v1/traces')

    expect(base).toHaveBeenCalledTimes(2)
    expect(guard.hits).toEqual([])
  })

  it('installs onto a fetch holder and reports the install through isNetworkGuarded', () => {
    const holder = { fetch: vi.fn(okResponse) as typeof fetch }
    expect(isNetworkGuarded(holder.fetch)).toBe(false)

    installNetworkGuard(holder)

    expect(isNetworkGuarded(holder.fetch)).toBe(true)
  })
})

describe('network guard installed for every test file', () => {
  it('wraps the global fetch so a live-receiver fetch fails closed and is recorded', async () => {
    // Check the guard before any call. If it is missing, no request is made.
    expect(isNetworkGuarded(globalThis.fetch)).toBe(true)

    const guard = installNetworkGuard()
    const outcome = await globalThis.fetch(LIVE_URL, { method: 'POST' }).then(
      () => 'resolved',
      (error: unknown) => error,
    )

    expect(outcome).toBeInstanceOf(NetworkGuardError)
    expect(guard.hits).toEqual([LIVE_URL])
    // Clear the deliberate hit so the setup file's afterEach sees only real leaks.
    guard.clear()
  })
})
