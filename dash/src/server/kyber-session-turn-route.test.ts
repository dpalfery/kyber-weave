import type { AddressInfo } from 'net'
import type { Server } from 'http'
import { tmpdir } from 'os'
import { join } from 'path'
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest'

import { runWebDashboard } from '../cli/web.js'
import { CanonStore } from '../canon/store.js'
import type { CanonicalRecord, ContentPart } from '../canon/types.js'
import { KyberBridge } from './bridge.js'
import { useAbsentLiveReceiver } from '../../tests/setup/live-receiver-stub.js'

useAbsentLiveReceiver()

// RED contract for the bounded per-turn read (change A): a new additive route
// `GET /api/kyber/session/:id/turn/:index` serving one turn's measured figures.
//
// The problem it fixes: `fetchKyberSessionTurns` pulls the whole session
// payload (up to 10,933 turn rows) to read one turn's counters, which
// `TurnDetail` renders via `findSessionTurnRow` / `turnRowFigures`.
//
// Observed dispatch order (routes.ts, must be preserved): the turn-content
// route (`parseTurnContentPath`, ~line 852) runs first, then the
// session-content route (~line 885), then the summary route (~line 933), and
// finally the detail prefix `/api/kyber/session/` (~line 967) catches
// everything else. Until GREEN lands, the new path falls through to that
// detail handler, which treats `sess-turns/turn/1` as a session id and answers
// 404 `{ error: 'Session not found' }` — every RED failure below is that
// fall-through, never a fixture typo. GREEN must insert the new route before
// the detail prefix (natural home: beside the turn-content route). It cannot
// shadow `/turn/:index/content`: `parseTurnContentPath` only matches paths
// ending in `/content`, so the two are disjoint in both directions.
//
// Sibling error-shape choice: the content route maps every null from
// `assembleTurnContent` — unknown session AND unknown turn — to the single
// 404 `{ error: 'Turn content not found' }`. The new figures route pins that
// same body for both cases, so the turn surface keeps one not-found shape.
// The detail route's `{ error: 'Session not found' }` is rejected as the
// alternative because the figures reader cannot distinguish "no such session"
// from "no such turn", and the existing turn-scoped route already collapses
// both to one body.

const TURNS: Array<Record<string, unknown>> = [
  {
    index: 0,
    spanId: 'span-turn-0',
    model: 'm0',
    input: 850,
    output: 120,
    fresh: 40,
    cache_read: 810,
    cache_creation: 5,
  },
  {
    index: 1,
    spanId: 'span-turn-1',
    model: 'm1',
    input: 900,
    output: 130,
    fresh: 50,
    cache_read: 860,
    cache_creation: 6,
  },
  {
    index: 2,
    spanId: 'span-turn-2',
    model: 'm2',
    input: 700,
    output: 90,
    fresh: 30,
    cache_read: 640,
    cache_creation: 4,
  },
]

// A blob the bounded read must never return: 50 KB of notes plus problems
// beside three small turn rows, so a whole-payload select is observable both
// in the SQL tap and in the response body.
const HOSTILE_NOTES = 'x'.repeat(50_000)

function turnRecord(spanId: string, text: string, timestamp: string): CanonicalRecord {
  return {
    spanId,
    traceId: 'trace-turns-001',
    parentSpanId: null,
    source: 'copilot',
    harness: 'copilot',
    sessionId: 'sess-turns',
    name: 'llm_request',
    op: 'llm.invoke',
    kind: 'client',
    timestamp,
    durationMs: 100,
    status: 'ok',
    tokens: {
      freshInput: 100,
      cacheRead: 0,
      cacheCreation: 0,
      output: 10,
      reportedInput: 100,
      reportedOutput: 10,
    },
    content: {},
    parts: [{ part: 'conversation_history', text } as ContentPart],
    cost: { basis: 'unknown', status: 'no_rate' },
  }
}

/**
 * The four counters the new route serves, tolerating the key shape GREEN
 * chooses: the detail row spells the fourth `cache_read`, the page's
 * `TurnTokenFigures` spells it `cacheRead`, and GREEN may wrap the figures in
 * a `turn`/`figures` envelope. Values are the contract; the envelope is not.
 */
function figuresOf(body: Record<string, unknown>): Record<string, unknown> | undefined {
  const nested = body.turn ?? body.figures
  const source =
    typeof nested === 'object' && nested !== null ? (nested as Record<string, unknown>) : body
  return typeof source.input === 'number' ? source : undefined
}

function figureValue(figures: Record<string, unknown>, ...keys: string[]): unknown {
  for (const key of keys) {
    if (figures[key] !== undefined) return figures[key]
  }
  return undefined
}

describe('GET /api/kyber/session/:id/turn/:index (bounded per-turn figures)', () => {
  let server: Server
  let base: string
  let store: CanonStore
  let bridge: KyberBridge

  beforeAll(async () => {
    store = new CanonStore(':memory:')
    store.upsertMany([
      turnRecord('span-turn-0', 'hello turn zero', '2026-09-04T12:00:00.000Z'),
      turnRecord('span-turn-1', 'hello turn one', '2026-09-04T12:01:00.000Z'),
    ])
    store.upsertSession({
      sessionId: 'sess-turns',
      harness: 'copilot',
      label: 'Turn Figures Session',
      started: '2026-09-04T12:00:00.000Z',
      ended: '2026-09-04T12:10:00.000Z',
      payload: {
        id: 'sess-turns',
        harness: 'copilot',
        summary: { turn_count: 3, request_count: 3, total_input: 2450, total_output: 340 },
        problems: Array.from({ length: 25 }, (_, index) => ({ code: `P${index}`, message: 'y'.repeat(40) })),
        turns: TURNS,
        notes: HOSTILE_NOTES,
      },
    })
    bridge = new KyberBridge({ store, ratesPath: join(tmpdir(), 'nonexistent-turn-rates.json') })
    server = await runWebDashboard({ port: 0, open: false, kyberBridge: bridge, writeStdout: () => {} })
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  })

  afterAll(async () => {
    await new Promise<void>((resolve) => server.close(() => resolve()))
    bridge.close()
    store.close()
  })

  function assertKyberHeaders(res: Response): void {
    expect(res.headers.get('content-type')).toContain('application/json')
    expect(res.headers.get('content-type')).toContain('charset=utf-8')
    expect(res.headers.get('cache-control')).toBe('no-store')
  }

  function sessionStatements(calls: unknown[][]): string[] {
    return calls
      .map((args) => args[0])
      .filter((sql): sql is string => typeof sql === 'string')
      .filter((sql) => sql.includes('FROM session'))
  }

  /** A whole-payload select: the bare `payload` column or `*` off `session`. */
  function selectsWholePayload(sql: string): boolean {
    return /SELECT\s+payload\b/i.test(sql) || /SELECT\s+\*/i.test(sql)
  }

  it('serves the one turn figures matching the detail payload turn row', async () => {
    // RED: the route does not exist, so this falls through to the detail
    // handler and 404s. After GREEN it answers 200 with turn 1's counters.
    const detailRes = await fetch(`${base}/api/kyber/session/sess-turns`)
    expect(detailRes.status).toBe(200)
    const detail = (await detailRes.json()) as { turns: Array<Record<string, unknown>> }
    const row = detail.turns.find((turn) => turn.index === 1)
    expect(row).toBeDefined()

    const res = await fetch(`${base}/api/kyber/session/sess-turns/turn/1`)
    expect(res.status).toBe(200)
    assertKyberHeaders(res)
    const body = (await res.json()) as Record<string, unknown>
    const figures = figuresOf(body)
    expect(figures, 'the response must carry the turn figures').toBeDefined()
    expect(figures!.input).toBe(row!.input)
    expect(figures!.output).toBe(row!.output)
    expect(figures!.fresh).toBe(row!.fresh)
    expect(figureValue(figures!, 'cacheRead', 'cache_read')).toBe(row!.cache_read)
    // Bounded: one turn's figures, never the turns array or the notes blob.
    expect(body).not.toHaveProperty('turns')
    expect(JSON.stringify(body).length).toBeLessThan(HOSTILE_NOTES.length)
  })

  it('answers 404 with the sibling shape for an unknown session id', async () => {
    // RED: today the detail fall-through answers `{ error: 'Session not
    // found' }`. The sibling turn-content route answers unknown sessions with
    // `{ error: 'Turn content not found' }` (pinned below); the new route
    // must match it.
    const res = await fetch(`${base}/api/kyber/session/sess-does-not-exist/turn/0`)
    expect(res.status).toBe(404)
    assertKyberHeaders(res)
    expect(await res.json()).toEqual({ error: 'Turn content not found' })
  })

  it('answers 404 with the sibling shape for an out-of-range turn index', async () => {
    // RED for the same fall-through reason. The shape matches what the
    // existing content route does for a bad index (a null assemble read maps
    // to `{ error: 'Turn content not found' }`), so the figures route keeps
    // one not-found body with its sibling.
    const res = await fetch(`${base}/api/kyber/session/sess-turns/turn/9999`)
    expect(res.status).toBe(404)
    assertKyberHeaders(res)
    expect(await res.json()).toEqual({ error: 'Turn content not found' })
  })

  it('reads the turn without selecting the session whole payload blob', async () => {
    // RED: the fall-through serves the detail route, whose
    // `getSessionPayload` issues `SELECT payload FROM session`. After GREEN
    // no `FROM session` statement may return the blob. The pin holds for the
    // read path as it exists and assumes no v22 table names: it forbids
    // returning the blob, not any one implementation (a single-row
    // `json_extract` and the `session_turn` split both satisfy it).
    const spy = vi.spyOn(store.getDatabase(), 'prepare')
    try {
      const res = await fetch(`${base}/api/kyber/session/sess-turns/turn/1`)
      expect(res.status).toBe(200)
    } finally {
      const calls = [...spy.mock.calls]
      spy.mockRestore()
      const probed = sessionStatements(calls)
      expect(probed.length).toBeGreaterThan(0)
      expect(probed.filter(selectsWholePayload)).toEqual([])
    }
  })

  it('leaves the sibling turn-content route working and unaffected', async () => {
    // Characterization: passes today and must keep passing after GREEN. The
    // content route still owns `/turn/:index/content` — the new figures route
    // must not shadow it.
    const res = await fetch(`${base}/api/kyber/session/sess-turns/turn/0/content`)
    expect(res.status).toBe(200)
    assertKyberHeaders(res)
    const body = (await res.json()) as { blocks: unknown[]; assembledText: string }
    expect(Array.isArray(body.blocks)).toBe(true)
    expect(body.blocks.length).toBeGreaterThan(0)
    expect(body.assembledText).toContain('hello turn zero')
  })

  it('keeps the sibling 404 shape for an unknown turn-content path', async () => {
    // Characterization of the exact shape A2 mirrors: unknown session and
    // unknown turn both map to `{ error: 'Turn content not found' }` on the
    // content route, so the figures route matching it is consistent, not new.
    const res = await fetch(`${base}/api/kyber/session/sess-does-not-exist/turn/0/content`)
    expect(res.status).toBe(404)
    expect(await res.json()).toEqual({ error: 'Turn content not found' })
  })
})
