// `GET /api/kyber/jobs` and `POST /api/kyber/refresh` (issue #319 T11 RED, rule R1): the
// tray and the web UI read job status and trigger refreshes through the `kyberdash web`
// server, which owns the JobHost. The JobHost status is FLAT; this route maps it to the
// nested shape the tray UI expects:
//   { refresh: { state, lastSuccessAt, lastFailure, nextDueAt }, paused, storeGeneration, hostedElsewhere }
// Fails until the routes exist (both answer the JSON 404 catch-all today).
import { afterEach, describe, expect, it, vi } from 'vitest'

import { KyberBridge } from './bridge.js'
import { callRoute, makeDeps } from './testing.js'

// Same lock stub as clean-bridge.test.ts: a clean takes the store refresh lock, and the
// tests must never touch the real ~/.kyberdash lock file.
const { acquireStoreRefreshLockMock } = vi.hoisted(() => ({
  acquireStoreRefreshLockMock: vi.fn(async () => ({
    outcome: 'acquired' as const,
    handle: { token: 'jobs-route-test', release: async () => {}, verifyStillOwner: async () => true },
  })),
}))
vi.mock('../refresh/lock.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../refresh/lock.js')>()
  return { ...actual, acquireStoreRefreshLock: acquireStoreRefreshLockMock }
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('GET /api/kyber/jobs', () => {
  it('nests the flat host status under refresh and keeps the other fields top-level', async () => {
    const deps = makeDeps()
    deps.jobHost.status = {
      state: 'failed',
      lastSuccessAt: '2026-10-09T10:00:00.000Z',
      lastFailure: 'exit 1 at 2026-10-09T11:00:00.000Z',
      nextDueAt: '2026-10-09T11:05:00.000Z',
      paused: true,
      hostedElsewhere: false,
      lastMaintenanceFailure: 'disk full',
    }
    const { status, body } = await callRoute('GET', '/api/kyber/jobs', { deps })
    expect(status).toBe(200)
    expect(body).toMatchObject({
      refresh: {
        state: 'failed',
        lastSuccessAt: '2026-10-09T10:00:00.000Z',
        lastFailure: 'exit 1 at 2026-10-09T11:00:00.000Z',
        nextDueAt: '2026-10-09T11:05:00.000Z',
      },
      paused: true,
      hostedElsewhere: false,
    })
    expect(typeof (body as { storeGeneration: unknown }).storeGeneration).toBe('number')
    // The flat fields must not leak beside the nested ones.
    expect(body).not.toHaveProperty('state')
    expect(body).not.toHaveProperty('lastSuccessAt')
  })

  it('reports hostedElsewhere when another process holds the jobs lease', async () => {
    const deps = makeDeps()
    deps.jobHost.status = { ...deps.jobHost.status, hostedElsewhere: true, nextDueAt: null }
    const { body } = await callRoute('GET', '/api/kyber/jobs', { deps })
    expect(body).toMatchObject({ hostedElsewhere: true, refresh: { nextDueAt: null } })
  })

  it('is stable across reads when nothing changed', async () => {
    const deps = makeDeps()
    const first = (await callRoute('GET', '/api/kyber/jobs', { deps })).body as { storeGeneration: number }
    const second = (await callRoute('GET', '/api/kyber/jobs', { deps })).body as { storeGeneration: number }
    expect(typeof first.storeGeneration).toBe('number')
    expect(second.storeGeneration).toBe(first.storeGeneration)
  })

  it('changes storeGeneration after a clean, so a UI knows to drop its cached data', async () => {
    const deps = makeDeps()
    const bridge = new KyberBridge({ store: deps.store })
    try {
      const before = (await callRoute('GET', '/api/kyber/jobs', { deps, bridge })).body as { storeGeneration: number }
      const clean = await callRoute('POST', '/api/kyber/clean', {
        deps,
        bridge,
        payload: { all: true, confirm: true },
      })
      expect(clean.status).toBe(200)
      const after = (await callRoute('GET', '/api/kyber/jobs', { deps, bridge })).body as { storeGeneration: number }
      expect(after.storeGeneration).not.toBe(before.storeGeneration)
    } finally {
      bridge.close()
      deps.store.close()
    }
  })

  it.each(['POST', 'PUT', 'DELETE'])('rejects %s with 405', async (method) => {
    const { status, handled } = await callRoute(method, '/api/kyber/jobs', { deps: makeDeps(), payload: {} })
    expect(handled).toBe(true)
    expect(status).toBe(405)
  })
})

describe('POST /api/kyber/refresh', () => {
  it.each(['tray', 'web'] as const)('accepts surface %s with 202 and runs the host job without waiting for it', async (surface) => {
    const deps = makeDeps() // runNow stays pending: the job is still running when we answer
    const { status, handled } = await callRoute('POST', '/api/kyber/refresh', { deps, payload: { surface } })
    expect(handled).toBe(true)
    expect(status).toBe(202)
    expect(deps.jobHost.runNowCalls).toEqual([surface])
  })

  it('answers 409 and starts nothing when the host already reports a running job', async () => {
    const deps = makeDeps()
    deps.jobHost.status = { ...deps.jobHost.status, state: 'running' }
    const { status, body } = await callRoute('POST', '/api/kyber/refresh', { deps, payload: { surface: 'tray' } })
    expect(status).toBe(409)
    expect(body).toHaveProperty('error')
    expect(deps.jobHost.runNowCalls).toEqual([])
  })

  it.each(['declined', 'busy', 'hosted-elsewhere'] as const)('answers 409 when the host answers %s', async (outcome) => {
    const deps = makeDeps()
    deps.jobHost.nextOutcome = Promise.resolve({ outcome })
    const { status } = await callRoute('POST', '/api/kyber/refresh', { deps, payload: { surface: 'web' } })
    expect(status).toBe(409)
  })

  it.each([
    ['an unknown surface', { surface: 'cli' }],
    ['a scheduled surface (reserved for the host)', { surface: 'scheduled' }],
    ['a missing surface', {}],
    ['a non-string surface', { surface: 7 }],
    ['an array body', ['tray']],
    ['malformed JSON', '{not json'],
  ])('rejects %s with 400 and starts nothing', async (_label, payload) => {
    const deps = makeDeps()
    const { status, body } = await callRoute('POST', '/api/kyber/refresh', { deps, payload })
    expect(status).toBe(400)
    expect(body).toHaveProperty('error')
    expect(deps.jobHost.runNowCalls).toEqual([])
  })

  it.each(['GET', 'PUT', 'DELETE'])('rejects %s with 405', async (method) => {
    const { status } = await callRoute(method, '/api/kyber/refresh', { deps: makeDeps() })
    expect(status).toBe(405)
  })

  it('rejects a non-JSON content type with 415', async () => {
    const deps = makeDeps()
    const { status } = await callRoute('POST', '/api/kyber/refresh', {
      deps,
      payload: { surface: 'tray' },
      contentType: 'text/plain',
    })
    expect(status).toBe(415)
    expect(deps.jobHost.runNowCalls).toEqual([])
  })

  it('rejects an oversized body with 413', async () => {
    const deps = makeDeps()
    const { status, destroyed } = await callRoute('POST', '/api/kyber/refresh', {
      deps,
      chunks: ['x'.repeat(40_000), 'x'.repeat(40_000)],
    })
    expect(status).toBe(413)
    expect(destroyed).toBe(true)
    expect(deps.jobHost.runNowCalls).toEqual([])
  })

  it('never puts a host error string in the response body', async () => {
    const deps = makeDeps()
    deps.jobHost.nextOutcome = () => Promise.reject(new Error('spawn ENOENT /Users/secret/path/kyberdash'))
    const { raw, status } = await callRoute('POST', '/api/kyber/refresh', { deps, payload: { surface: 'tray' } })
    expect([202, 500]).toContain(status)
    expect(raw).not.toContain('secret')
    expect(raw).not.toContain('ENOENT')
  })
})
