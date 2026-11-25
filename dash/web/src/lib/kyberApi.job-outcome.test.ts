// The import and refresh routes answer with the job's outcome, not an empty
// 2xx. The client must hand that outcome back so the panel can say what really
// happened: a 2xx body without a known outcome is a broken reply, not success.

import { afterEach, describe, expect, it, vi } from 'vitest'

import { importKyberHistory, requestKyberRefresh } from './kyberApi.js'

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

describe('importKyberHistory', () => {
  it('resolves to the succeeded outcome the host reports', async () => {
    stubFetch(200, { outcome: 'succeeded', failedUnits: 0 })

    await expect(importKyberHistory({ weeks: 1 })).resolves.toEqual({
      outcome: 'succeeded',
      failedUnits: 0,
    })
  })

  it('resolves to the partial outcome with the count of failed source units', async () => {
    stubFetch(200, { outcome: 'partial', failedUnits: 3 })

    await expect(importKyberHistory({ weeks: 4 })).resolves.toEqual({
      outcome: 'partial',
      failedUnits: 3,
    })
  })

  it('rejects a 2xx reply that carries no known outcome', async () => {
    stubFetch(202, {})

    await expect(importKyberHistory({ weeks: 1 })).rejects.toThrow()
  })
})

describe('requestKyberRefresh', () => {
  it('resolves to the partial outcome with the count of failed source units', async () => {
    stubFetch(200, { outcome: 'partial', failedUnits: 2 })

    await expect(requestKyberRefresh('web')).resolves.toEqual({
      outcome: 'partial',
      failedUnits: 2,
    })
  })

  it('rejects a 2xx reply that carries no known outcome', async () => {
    stubFetch(200, { outcome: 'maybe' })

    await expect(requestKyberRefresh('web')).rejects.toThrow()
  })
})
