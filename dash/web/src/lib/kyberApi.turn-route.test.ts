// RED contract for the web client's per-turn read (change A, client half):
// `fetchKyberSessionTurns` requests the whole session payload to read one
// turn's counters. The change repoints it at the new additive route
// `GET /api/kyber/session/:id/turn/:index`, so the turn page stops paying for
// the whole blob. Fixtures are inline; fetch is mocked per test so nothing
// reaches a server (same pattern as `kyberApi.capture.test.ts`).
//
// Assumption, stated plainly: per the task contract ("the client's
// `fetchKyberSessionTurns` targets the new route") GREEN keeps the
// `fetchKyberSessionTurns` name and adds the turn index as a second
// parameter, so the call below typechecks today (an extra argument is
// ignored at runtime) and resolves to the per-turn URL after GREEN. If GREEN
// instead introduces a differently named fetcher, repoint this file at it —
// the URL shape asserted here is fixed by the task contract either way.

import { afterEach, describe, expect, it, vi } from 'vitest'

import { fetchKyberSessionTurns } from './kyberApi.js'

type FetchCall = { url: string; init: RequestInit | undefined }

function stubFetch(status: number, body: unknown): FetchCall[] {
  const calls: FetchCall[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, init })
      return new Response(JSON.stringify(body), {
        status,
        headers: { 'content-type': 'application/json' },
      })
    }),
  )
  return calls
}

afterEach(() => {
  vi.unstubAllGlobals()
})

const PER_TURN_BODY = {
  session_id: 'sess-1',
  turn_index: 1,
  input: 900,
  output: 130,
  fresh: 50,
  cache_read: 860,
}

type TurnsFetcher = (sessionId: string, turnIndex?: number) => Promise<unknown>

function fetchOneTurn(sessionId: string, turnIndex: number): Promise<unknown> {
  return (fetchKyberSessionTurns as unknown as TurnsFetcher)(sessionId, turnIndex)
}

/**
 * The response's counters, tolerating the key shape GREEN chooses: the
 * detail row spells the fourth counter `cache_read`, the page's
 * `TurnTokenFigures` spells it `cacheRead`. Values are the contract.
 */
function figuresOf(value: unknown): Record<string, unknown> | undefined {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return undefined
  const body = value as Record<string, unknown>
  const nested = body.turn ?? body.figures
  const source =
    typeof nested === 'object' && nested !== null ? (nested as Record<string, unknown>) : body
  return typeof source.input === 'number' ? source : undefined
}

describe('fetchKyberSessionTurns per-turn route (change A)', () => {
  it('requests the bounded per-turn route, not the whole-payload detail route', async () => {
    // RED: today this GETs `/api/kyber/session/sess-1` (the whole payload).
    // After GREEN it GETs the per-turn route for the requested index.
    const calls = stubFetch(200, PER_TURN_BODY)

    await fetchOneTurn('sess-1', 1)

    expect(calls).toHaveLength(1)
    expect(calls[0]!.url).toBe('/api/kyber/session/sess-1/turn/1')
  })

  it('encodes the session id in the per-turn path', async () => {
    // RED for the same reason: the per-turn path must carry the id the way
    // every other session route does (`encodeURIComponent`), never raw.
    const calls = stubFetch(200, PER_TURN_BODY)

    await fetchOneTurn('sess/1', 0)

    expect(calls).toHaveLength(1)
    expect(calls[0]!.url).toBe('/api/kyber/session/sess%2F1/turn/0')
  })

  it('carries the same counters the turn page renders for that turn', async () => {
    // RED: today the stubbed per-turn body has no `turns` key, so the current
    // implementation resolves `[]`. After GREEN the resolved value carries
    // the same four counters the detail row holds — the numbers
    // `turnRowFigures` maps for the inspector header.
    stubFetch(200, PER_TURN_BODY)

    const figures = figuresOf(await fetchOneTurn('sess-1', 1))

    expect(figures).toMatchObject({ input: 900, output: 130, fresh: 50 })
    expect(figures?.cacheRead ?? figures?.cache_read).toBe(860)
  })
})
