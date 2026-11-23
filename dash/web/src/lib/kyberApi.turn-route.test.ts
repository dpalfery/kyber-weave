// Contract for the web client's per-turn read: one turn, one bounded request.
//
// The turn page reads a single turn's counters, so `fetchKyberSessionTurns`
// asks the additive route `GET /api/kyber/session/:id/turn/:index` and takes
// the turn index as a required argument. The whole-payload fallback — the
// read of a session's whole turn list when no index was given — is retired,
// so no caller can reach the session payload through this module. Fixtures
// are inline; fetch is mocked per test so nothing reaches a server (same
// pattern as `kyberApi.capture.test.ts`).

import { readFileSync } from 'node:fs'

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

// The widened route's served shape: the turn row rides under `turn`, beside
// the session and index identity and the turn's stored context (absent here).
const PER_TURN_BODY = {
  sessionId: 'sess-1',
  turnIndex: 1,
  turn: { index: 1, input: 900, output: 130, fresh: 50, cache_read: 860 },
  context: null,
}

type TurnsFetcher = (sessionId: string, turnIndex: number) => Promise<unknown>

function fetchOneTurn(sessionId: string, turnIndex: number): Promise<unknown> {
  return (fetchKyberSessionTurns as unknown as TurnsFetcher)(sessionId, turnIndex)
}

/**
 * The response's counters, tolerating the key shape the client chooses: the
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

describe('fetchKyberSessionTurns per-turn route', () => {
  it('requests the bounded per-turn route, not a whole-payload read', async () => {
    const calls = stubFetch(200, PER_TURN_BODY)

    await fetchOneTurn('sess-1', 1)

    expect(calls).toHaveLength(1)
    expect(calls[0]!.url).toBe('/api/kyber/session/sess-1/turn/1')
  })

  it('encodes the session id in the per-turn path', async () => {
    const calls = stubFetch(200, PER_TURN_BODY)

    await fetchOneTurn('sess/1', 0)

    expect(calls).toHaveLength(1)
    expect(calls[0]!.url).toBe('/api/kyber/session/sess%2F1/turn/0')
  })

  it('carries the same counters the turn page renders for that turn', async () => {
    stubFetch(200, PER_TURN_BODY)

    const figures = figuresOf(await fetchOneTurn('sess-1', 1))

    expect(figures).toMatchObject({ input: 900, output: 130, fresh: 50 })
    expect(figures?.cacheRead ?? figures?.cache_read).toBe(860)
  })

  it('has no whole-payload fallback: the client reads no session turn list', () => {
    // The fallback answered a call with no turn index by reading a whole turn
    // list off the session. The per-turn route replaced it, and the fallback
    // must not survive beside it — a caller with no index has nothing to read.
    // Read as text because the rule is about the module's requests, not about
    // any one render.
    const source = readFileSync(new URL('./kyberApi.ts', import.meta.url), 'utf8')
    const start = source.indexOf('export async function fetchKyberSessionTurns')
    expect(start).toBeGreaterThan(-1)
    const body = source.slice(start, source.indexOf('\n}', start))

    expect(body).not.toContain('/charts')
    expect(body).not.toContain('body.turns')
    expect(body).not.toContain('turnIndex?')
  })

  it('offers no bare-session read: the turn index is required', () => {
    // A call with the session id alone was the fallback's shape. The type
    // surface no longer accepts it, so this assignment must not compile.
    // @ts-expect-error — a bare session id is not a per-turn read.
    const bareRead: (sessionId: string) => Promise<unknown> = fetchKyberSessionTurns

    expect(bareRead).toBeTypeOf('function')
  })
})
