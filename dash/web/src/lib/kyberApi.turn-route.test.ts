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

  it.each([
    ['negative', -1],
    ['fractional', 1.5],
  ])('refuses a %s turn index on both turn routes', async (_why, turnIndex) => {
    stubFetch(200, { sessionId: 'sess-1', turnIndex, parts: [] })
    await expect(fetchTurnContent('sess-1', 1)).rejects.toThrow(/malformed/i)

    stubFetch(200, { sessionId: 'sess-1', turnIndex, projection_status: 'PROJECTION_FAILED' })
    await expect(fetchTurnContent('sess-1', 1)).rejects.toThrow(/malformed/i)
    await expect(fetchKyberSessionTurn('sess-1', 1)).rejects.toThrow(/malformed/i)
  })

  it.each([
    ['negative', -5],
    ['fractional', 2.5],
  ])('drops a %s served token count instead of rendering it', async (_why, tokens) => {
    stubFetch(200, { sessionId: 'sess-1', turnIndex: 1, parts: [{ part: 'system_prompt', text: 'sys', tokens }] })
    const content = await fetchTurnContent('sess-1', 1)
    expect(content.parts[0]?.tokens).toBeUndefined()
  })

  it('keeps the tokenizer label beside a text-derived part count', async () => {
    stubFetch(200, {
      sessionId: 'sess-1',
      turnIndex: 1,
      parts: [
        {
          part: 'system_prompt',
          text: 'sys',
          tokens: 12,
          tokenizer: 'o200k_base',
          tokenizerIsModelOwn: false,
        },
      ],
    })

    const content = await fetchTurnContent('sess-1', 1)

    expect(content.parts[0]).toMatchObject({
      tokens: 12,
      tokenizer: 'o200k_base',
      tokenizerIsModelOwn: false,
    })
    expect(content.blocks[0]).toMatchObject({
      tokens: 12,
      tokenizer: 'o200k_base',
      tokenizerIsModelOwn: false,
    })
  })

  it('rejects a content part that carries half a tokenizer label', async () => {
    stubFetch(200, {
      sessionId: 'sess-1',
      turnIndex: 1,
      parts: [{ part: 'system_prompt', text: 'sys', tokens: 12, tokenizer: 'o200k_base' }],
    })

    await expect(fetchTurnContent('sess-1', 1)).rejects.toThrow(/incomplete tokenizer label/i)
  })

  it('reads a marker other than PROJECTION_FAILED as no fault on either turn route', async () => {
    // One fault contract: only the exact sections' marker names a faulted read.
    stubFetch(200, { sessionId: 'sess-1', turnIndex: 1, projection_status: 'PENDING' })

    await expect(fetchKyberSessionTurn('sess-1', 1)).rejects.toThrow(/malformed/i)
    await expect(fetchTurnContent('sess-1', 1)).rejects.toThrow(/malformed/i)
  })

  it.each([
    ['parts is missing', { sessionId: 'sess-1', turnIndex: 1, blocks: [], assembledText: '' }],
    ['parts is null', { sessionId: 'sess-1', turnIndex: 1, blocks: [], parts: null, assembledText: '' }],
    ['a parts entry is not an object', { sessionId: 'sess-1', turnIndex: 1, parts: ['nope'] }],
    ['a parts entry part is not a string', { sessionId: 'sess-1', turnIndex: 1, parts: [{ part: 7, text: 'x' }] }],
    ['a parts entry text is not a string', { sessionId: 'sess-1', turnIndex: 1, parts: [{ part: 'system_prompt', text: null }] }],
  ])('refuses a content body the page cannot render: %s', async (_why, body) => {
    stubFetch(200, body)
    await expect(fetchTurnContent('sess-1', 1)).rejects.toThrow(/malformed/i)
  })

  it.each([
    ['blocks is null', { sessionId: 'sess-1', turnIndex: 1, blocks: null, parts: [], assembledText: '' }],
    ['assembledText is missing', { sessionId: 'sess-1', turnIndex: 1, blocks: [], parts: [] }],
  ])('builds the view from parts regardless of the body’s blocks: %s', async (_why, body) => {
    // Blocks and assembled text are the web’s derivation of the served parts, not
    // fields the engine owes. Whichever shape the body carries them in, the parser
    // builds the same view from `parts` alone — one contract.
    stubFetch(200, body)

    const content = await fetchTurnContent('sess-1', 1)

    // A turn whose parts were cut to nothing still answers the five canonical
    // blocks, empty, so the rail keeps its shape.
    expect(content.blocks.map((b) => [b.key, b.text, b.parts.length])).toEqual([
      ['system_prompt', '', 0],
      ['tool_definitions', '', 0],
      ['instruction_context', '', 0],
      ['conversation_history', '', 0],
      ['tool_result_content', '', 0],
    ])
    expect(content.parts).toEqual([])
    expect(content.assembledText).toBe('')
  })

  it('builds blocks, labels, and assembled text from the real engine shape', async () => {
    // EXACTLY the served shape of the C# engine’s content route — no blocks, no
    // assembledText on the wire. The budgeted parts it kept, the turn’s span and
    // model, the raw-packet pointer, and the parts-count truncation flag are all
    // the route sends; everything the turn page renders is built from them.
    stubFetch(200, {
      sessionId: 'sess-1',
      turnIndex: 0,
      spanId: 'turn-span-1',
      model: 'claude-sonnet-4-6',
      rawPacket: { spanAttributesPath: '/api/kyber/span/turn-span-1/attributes' },
      content: null,
      turn: { turnIndex: 0, index: 0, spanId: 'turn-span-1', timestamp: '2026-11-23T10:00:00.000Z' },
      truncated: true,
      budget: 32768,
      totalBytes: 857,
      parts: [
        { spanId: 'turn-span-1', part: 'system_prompt', text: 'Be helpful.', tokens: 12 },
        { spanId: 'turn-span-1', part: 'conversation_history', text: 'user: hello\nassistant: hi', tokens: 40 },
        { spanId: 'turn-span-1', part: 'mystery_blob', text: 'blob bytes', server: 'x', truncated: true },
      ],
    })

    const content = await fetchTurnContent('sess-1', 0)

    // Identity, span, model, and the packet pointer pass through as served.
    expect(content.sessionId).toBe('sess-1')
    expect(content.turnIndex).toBe(0)
    expect(content.spanId).toBe('turn-span-1')
    expect(content.model).toBe('claude-sonnet-4-6')
    expect(content.rawPacket).toEqual({ spanAttributesPath: '/api/kyber/span/turn-span-1/attributes' })
    expect(content.truncated).toBe(true)

    // The conversation history split into labelled parts with proportional tokens;
    // the unknown kind keeps its own label, server, and flags.
    expect(content.parts.map((p) => [p.id, p.part, p.label, p.text])).toEqual([
      ['part-sys-0', 'system_prompt', 'System Prompt', 'Be helpful.'],
      ['part-user-1', 'user_messages', 'User Messages', 'hello'],
      ['part-asst-2', 'assistant_turns', 'Assistant Turns', 'hi'],
      ['part-other-3', 'mystery_blob', 'mystery blob', 'blob bytes'],
    ])
    expect(content.parts[1]).toMatchObject({ tokens: 20 })
    expect(content.parts[2]).toMatchObject({ tokens: 20 })
    expect(content.parts[3]).toMatchObject({ server: 'x', truncated: true })

    // The five canonical blocks, built from those parts.
    expect(content.blocks.map((b) => [b.key, b.text, b.tokens])).toEqual([
      ['system_prompt', 'Be helpful.', 12],
      ['tool_definitions', '', undefined],
      ['instruction_context', '', undefined],
      ['conversation_history', 'hello\n\nhi', 40],
      ['tool_result_content', '', undefined],
    ])

    // Assembled text heads each non-blank block; the unknown kind is a part, not a block.
    expect(content.assembledText).toBe(
      '=== SYSTEM PROMPT ===\nBe helpful.\n\n=== CONVERSATION HISTORY ===\nhello\n\nhi',
    )
  })
})
