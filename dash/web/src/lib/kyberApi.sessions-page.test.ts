// The paged sessions contracts (Task T3): the client requests the server's
// page envelope — never the whole list — and validates its shape.
// Synthetic data only; the envelopes below are built to the validator's shape.
// The client is destructured from the module namespace because this suite
// runs first, before the bindings it names exist: every unimplemented one
// reads as a property the module does not have yet (the RED classifier
// reads TS2339).
import { afterEach, describe, expect, it, vi } from 'vitest'

import * as kyberApi from './kyberApi.js'
import type { KyberSessionSummary } from './kyberApi.js'

const { fetchKyberSessionPage, fetchKyberSubagentPage, KYBER_SESSIONS_PAGE_LIMIT, KYBER_SUBAGENTS_PAGE_LIMIT } =
  kyberApi

function session(id: string, harness = 'codex', label = `${id} label`): KyberSessionSummary {
  return { session_id: id, sessionId: id, harness, label, started: '2026-03-01T12:00:00.000Z' }
}

/** The page envelope the validator accepts, built to its shape. */
function envelope(rows: KyberSessionSummary[], extra: { total?: number; page?: number; limit?: number } = {}) {
  return {
    entries: rows,
    data: rows,
    total: extra.total ?? rows.length,
    page: extra.page ?? 1,
    limit: extra.limit ?? 100,
  }
}

function jsonResponse(payload: unknown, status = 200): Response {
  return { ok: status >= 200 && status < 300, status, json: async () => payload } as unknown as Response
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('fetchKyberSessionPage', () => {
  it('requests page one with the sessions page limit and no harness when unscoped', async () => {
    const fetchMock = vi.fn(async (_url: string) => jsonResponse(envelope([session('sess-1')])))
    vi.stubGlobal('fetch', fetchMock)

    const page = await fetchKyberSessionPage({})

    expect(fetchMock).toHaveBeenCalledExactlyOnceWith('/api/kyber/sessions?page=1&limit=100')
    expect(page).toEqual(envelope([session('sess-1')]))
    expect(KYBER_SESSIONS_PAGE_LIMIT).toBe(100)
  })

  it('carries the harness scope on the query string', async () => {
    const fetchMock = vi.fn(async (_url: string) => jsonResponse(envelope([])))
    vi.stubGlobal('fetch', fetchMock)

    await fetchKyberSessionPage({ page: 1, harness: 'claude-code' })

    expect(fetchMock).toHaveBeenCalledExactlyOnceWith('/api/kyber/sessions?page=1&limit=100&harness=claude-code')
  })

  it('carries the search text on the query string and omits it when empty', async () => {
    const fetchMock = vi.fn(async (_url: string) => jsonResponse(envelope([])))
    vi.stubGlobal('fetch', fetchMock)

    await fetchKyberSessionPage({ page: 2, q: 'deploy' })
    await fetchKyberSessionPage({ page: 2, q: '' })

    expect(fetchMock.mock.calls[0]![0]).toBe('/api/kyber/sessions?page=2&limit=100&q=deploy')
    expect(fetchMock.mock.calls[1]![0]).not.toContain('q=')
  })

  it('names page and limit beyond page one', async () => {
    const fetchMock = vi.fn(async (_url: string) => jsonResponse(envelope([], { page: 3, limit: 50 })))
    vi.stubGlobal('fetch', fetchMock)

    await fetchKyberSessionPage({ page: 3, limit: 50 })

    expect(fetchMock).toHaveBeenCalledExactlyOnceWith('/api/kyber/sessions?page=3&limit=50')
  })

  it('rejects a non-2xx answer as a KyberApiError', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse({ error: 'down' }, 503)))
    await expect(fetchKyberSessionPage({})).rejects.toMatchObject({ status: 503, path: '/api/kyber/sessions?page=1&limit=100' })
  })

  it.each([
    ['data is missing', { sessions: [session('sess-1')], total: 1, page: 1, limit: 100 }],
    ['total is missing', { data: [session('sess-1')], page: 1, limit: 100 }],
    ['page is missing', { data: [session('sess-1')], total: 1, limit: 100 }],
    ['limit is missing', { data: [session('sess-1')], total: 1, page: 1 }],
    ['total is not a number', { data: [session('sess-1')], total: '1', page: 1, limit: 100 }],
    ['total is negative', { data: [session('sess-1')], total: -1, page: 1, limit: 100 }],
    ['total is fractional', { data: [session('sess-1')], total: 1.5, page: 1, limit: 100 }],
    ['a row has no session id', { data: [{ harness: 'codex' }], total: 1, page: 1, limit: 100 }],
    ['a row has an empty session id', { data: [{ session_id: '', harness: 'codex' }], total: 1, page: 1, limit: 100 }],
    ['a row has no harness', { data: [{ session_id: 'sess-1' }], total: 1, page: 1, limit: 100 }],
    ['a row has an empty harness', { data: [{ session_id: 'sess-1', harness: '' }], total: 1, page: 1, limit: 100 }],
    ['a row has a blank harness', { data: [{ session_id: 'sess-1', harness: '  ' }], total: 1, page: 1, limit: 100 }],
    ['a row has a blank session id', { data: [{ session_id: ' ', harness: 'codex' }], total: 1, page: 1, limit: 100 }],
    ['data is not an array', { data: { 0: session('sess-1') }, total: 1, page: 1, limit: 100 }],
    ['a row is not an object', { data: ['sess-1'], total: 1, page: 1, limit: 100 }],
    ['the body is not an object', null],
  ])('refuses a malformed envelope: %s', async (_why, body) => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(body)))
    await expect(fetchKyberSessionPage({})).rejects.toThrow(/malformed/i)
  })
})

describe('fetchKyberSubagentPage', () => {
  it('requests the subagent route paged by the subagent page limit', async () => {
    const fetchMock = vi.fn(async (_url: string) => jsonResponse(envelope([], { limit: 25 })))
    vi.stubGlobal('fetch', fetchMock)

    const page = await fetchKyberSubagentPage('sess-parent-001', 2)

    expect(fetchMock).toHaveBeenCalledExactlyOnceWith('/api/kyber/session/sess-parent-001/subagents?page=2&limit=25')
    expect(page.limit).toBe(KYBER_SUBAGENTS_PAGE_LIMIT)
    expect(KYBER_SUBAGENTS_PAGE_LIMIT).toBe(25)
  })

  it('encodes the parent id', async () => {
    const fetchMock = vi.fn(async (_url: string) => jsonResponse(envelope([])))
    vi.stubGlobal('fetch', fetchMock)

    await fetchKyberSubagentPage('sess/a b', 1)

    expect(fetchMock).toHaveBeenCalledExactlyOnceWith('/api/kyber/session/sess%2Fa%20b/subagents?page=1&limit=25')
  })

  it('refuses a malformed envelope', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse({ subagents: [] })))
    await expect(fetchKyberSubagentPage('sess-1', 1)).rejects.toThrow(/malformed/i)
  })
})
