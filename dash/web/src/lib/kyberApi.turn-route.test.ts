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

import { fetchKyberSessionTurn, fetchKyberSessionTurns, fetchTurnContent } from './kyberApi.js'

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

  it('surfaces a faulted projection as the sections’ failure state, not a malformed error', async () => {
    // A faulted turn-figures read answers 200 with the sections' marker and no
    // turn row. The read resolves with the marker — the page renders the failure
    // state the sections render — and carries no figures, never a neighbour's.
    stubFetch(200, { sessionId: 'sess-1', turnIndex: 1, projection_status: 'PROJECTION_FAILED' })

    const section = await fetchKyberSessionTurn('sess-1', 1)

    expect(section.projection_status).toBe('PROJECTION_FAILED')
    expect(section.turn).toBeUndefined()

    const row = await fetchOneTurn('sess-1', 1)
    expect(figuresOf(row)).toBeUndefined()
  })

  it('carries the fault marker through the content route with inert transport fields', async () => {
    // Same engine convention on the content route: identity plus the marker, no
    // content fields. The parser keeps the marker and fills the required arrays
    // with nothing, so a consumer renders the failure instead of an empty turn.
    stubFetch(200, { sessionId: 'sess-1', turnIndex: 1, projection_status: 'PROJECTION_FAILED' })

    const content = await fetchTurnContent('sess-1', 1)

    expect(content.projection_status).toBe('PROJECTION_FAILED')
    expect(content.blocks).toEqual([])
    expect(content.parts).toEqual([])
    expect(content.assembledText).toBe('')
  })

  it('reads a marker other than PROJECTION_FAILED as no fault on either turn route', async () => {
    // One fault contract: only the exact sections' marker names a faulted read.
    stubFetch(200, { sessionId: 'sess-1', turnIndex: 1, projection_status: 'PENDING' })

    await expect(fetchKyberSessionTurn('sess-1', 1)).rejects.toThrow(/malformed/i)
    await expect(fetchTurnContent('sess-1', 1)).rejects.toThrow(/malformed/i)
  })

  it.each([
    ['blocks is null', { sessionId: 'sess-1', turnIndex: 1, blocks: null, parts: [], assembledText: '' }],
    ['parts is missing', { sessionId: 'sess-1', turnIndex: 1, blocks: [], assembledText: '' }],
    ['assembledText is missing', { sessionId: 'sess-1', turnIndex: 1, blocks: [], parts: [] }],
  ])('refuses a content body the page cannot render: %s', async (_why, body) => {
    stubFetch(200, body)
    await expect(fetchTurnContent('sess-1', 1)).rejects.toThrow(/malformed/i)
  })
})
