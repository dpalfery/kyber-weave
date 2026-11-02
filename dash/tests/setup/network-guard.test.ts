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
  isLoopbackHost,
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

  it('covers all of 127.0.0.0/8, IPv6 loopback, v4-mapped loopback and mixed-case hosts', () => {
    for (const host of ['127.0.0.2', '127.255.255.254', '[::1]', '[::ffff:127.0.0.1]', '[::ffff:7f00:1]', 'LocalHost', '127.0.0.1.', 'localhost.']) {
      expect(isLiveReceiverUrl(`http://${host}:4318/v1/traces`), host).toBe(true)
    }
    expect(isLiveReceiverUrl('http://[::1]:4319/v1/traces')).toBe(false)
    expect(isLiveReceiverUrl('http://128.0.0.1:4318/v1/traces')).toBe(false)
    expect(isLiveReceiverUrl('http://[2001:db8::1]:4318/v1/traces')).toBe(false)
  })

  it('treats every numeric IPv4 spelling of loopback as the live receiver, through URL and as raw hosts', () => {
    const forms = ['2130706433', '0x7f000001', '0X7F000001', '0x7f.0.0.1', '0177.0.0.1', '0177.0.0.01', '127.1', '127.0.1', '0x7f.1', '017700000001', '127.0.0.0x1', '2130706434']
    for (const host of forms) {
      expect(isLiveReceiverUrl(`http://${host}:4318/v1/traces`), host).toBe(true)
      expect(isLoopbackHost(host), `raw ${host}`).toBe(true)
    }
  })

  it('does not treat other numeric IPv4 spellings as loopback', () => {
    for (const host of ['2147483648', '0x80000001', '128.1', '0200.0.0.1', '10.0.0.1', '167772161', 'example.com', '127.0.0.1.example.com']) {
      expect(isLoopbackHost(host), host).toBe(false)
    }
  })

  it('refuses 127.0.0.2 and [::1] on port 4318 without calling the base fetch', async () => {
    const base = vi.fn(okResponse)
    const guard = createNetworkGuard(base)
    for (const url of ['http://127.0.0.2:4318/v1/traces', 'http://[::1]:4318/v1/traces']) {
      const outcome = await guard.fetch(url).then(
        () => 'resolved',
        (error: unknown) => error,
      )
      expect(outcome).toBeInstanceOf(NetworkGuardError)
    }
    expect(base).not.toHaveBeenCalled()
    expect(guard.hits).toHaveLength(2)
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
