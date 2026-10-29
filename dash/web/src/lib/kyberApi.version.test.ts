// The version route feeds the "KyberDash version" line in Settings. A 2xx body
// that is not `{ version: non-empty string, apiVersion: finite number }` is a
// broken engine, not data: the client must reject it, so the surface shows
// "unknown" instead of rendering `undefined`, `null`, or an empty version.

import { afterEach, describe, expect, it, vi } from 'vitest'

import { KyberApiError, fetchKyberVersion } from './kyberApi.js'

function stubFetch(status: number, body: unknown): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => {
      return new Response(JSON.stringify(body), {
        status,
        headers: { 'content-type': 'application/json' },
      })
    }),
  )
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('fetchKyberVersion', () => {
  it('resolves to the version and API contract version the engine reports', async () => {
    stubFetch(200, { version: '1.0.0', apiVersion: 1 })

    await expect(fetchKyberVersion()).resolves.toEqual({ version: '1.0.0', apiVersion: 1 })
  })

  it.each([
    ['an empty object', {}],
    ['a null version with a numeric apiVersion', { version: null, apiVersion: 1 }],
    ['an empty version string', { version: '', apiVersion: 1 }],
    ['a missing apiVersion', { version: '1.0.0' }],
    ['a non-numeric apiVersion', { version: '1.0.0', apiVersion: '1' }],
    ['a whitespace-only version', { version: '   ', apiVersion: 1 }],
    ['the 0.0.0 placeholder version', { version: '0.0.0', apiVersion: 1 }],
    ['the 0.0.0 placeholder padded with spaces', { version: ' 0.0.0 ', apiVersion: 1 }],
    ['the 0.0.0 placeholder with a trailing space', { version: '0.0.0 ', apiVersion: 1 }],
    ['a null body', null],
  ])('rejects a 2xx answer that is %s, rather than passing it through as a version', async (_label, body) => {
    stubFetch(200, body)

    await expect(fetchKyberVersion()).rejects.toBeInstanceOf(KyberApiError)
  })

  it.each([
    ['a real version padded with spaces on both sides', ' 1.2.3 ', '1.2.3'],
    ['a real version with a trailing space', '1.2.3 ', '1.2.3'],
  ])('resolves %s rather than rejecting a usable version',async (_label, version, expected) => {
    stubFetch(200, { version, apiVersion: 1 })

    // The client may return the padding or trim it; either way the real version is there.
    const answer = await fetchKyberVersion()
    expect(answer.version.trim(), 'the engine version must survive the trim').toBe(expected)
    expect(answer.apiVersion).toBe(1)
  })
})
