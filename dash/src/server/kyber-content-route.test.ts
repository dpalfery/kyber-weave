import type { AddressInfo } from 'net'
import type { Server } from 'http'
import { tmpdir } from 'os'
import { join } from 'path'
import { DatabaseSync } from 'node:sqlite'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'

import { runWebDashboard } from '../cli/web.js'
import {
  CONTENT_RESPONSE_BUDGET,
  KyberBridge,
  MAX_STRING_LENGTH,
  type SessionContentResult,
  type TurnContentResult,
} from './bridge.js'
import { CanonStore } from '../canon/store.js'
import type { CanonicalRecord, ContentPart } from '../canon/types.js'

describe('Backend Contract Tests: GET /api/kyber/session/:id/content', () => {
  let server: Server
  let base: string
  let canonDb: DatabaseSync
  let store: CanonStore
  let testBridge: KyberBridge

  // Longer than `_clip`'s 2,000-character leaf cap — the reason this route exists.
  const SYSTEM_PROMPT =
    'You are a diagnostic coding agent.\nFollow the repository rules exactly.\n' +
    'x'.repeat(11_000)
  const TOOL_DEFINITION = '{"name":"read_file","description":"Read a file from the workspace"}'
  const CONVERSATION = 'user: why is the inspector showing a stub mid-sentence?'
  const HUGE_TOOL_RESULT = 'R'.repeat(CONTENT_RESPONSE_BUDGET + 50_000)

  const tokens = () => ({
    freshInput: 1000,
    cacheRead: 0,
    cacheCreation: 0,
    output: 100,
    reportedInput: 1000,
    reportedOutput: 100,
  })

  function turn(
    spanId: string,
    parts: ContentPart[],
    over: Partial<CanonicalRecord> = {},
  ): CanonicalRecord {
    return {
      spanId,
      traceId: 'trace-content-001',
      parentSpanId: null,
      source: 'copilot',
      harness: 'copilot',
      sessionId: 'sess-content-001',
      name: 'llm_request',
      op: 'llm.invoke',
      kind: 'client',
      timestamp: '2026-09-03T10:00:00.000Z',
      durationMs: 100,
      status: 'ok',
      tokens: tokens(),
      content: {},
      parts,
      cost: { basis: 'unknown', status: 'no_rate' },
      ...over,
    }
  }

  // Issue #216 / T3: claude-desktop file-synth records use `codeburn/…`
  // provenance and `synth:` span ids; the route must surface stored parts
  // the same way Copilot fixtures already do.
  function claudeDesktopTurn(
    spanId: string,
    parts: ContentPart[],
    over: Partial<CanonicalRecord> = {},
  ): CanonicalRecord {
    return turn(spanId, parts, {
      source: 'codeburn/claude-desktop',
      harness: 'claude-desktop',
      ...over,
    })
  }

  const CD_CONVERSATION = 'user: why is the claude-desktop inspector empty?'
  const CD_TOOL_RESULT = 'tool result: README.md contents for the inspector fixture'

  beforeAll(async () => {
    store = new CanonStore(':memory:')
    store.upsertMany([
      turn('span-prompt', [
        { part: 'system_prompt', text: SYSTEM_PROMPT, tokens: 5800 },
      ]),
      turn(
        'span-tools',
        [
          { part: 'tool_definitions', text: TOOL_DEFINITION, tokens: 24, server: 'built-in' },
          { part: 'conversation_history', text: CONVERSATION, tokens: 14 },
        ],
        { timestamp: '2026-09-03T10:01:00.000Z' },
      ),
      turn(
        'span-huge',
        [{ part: 'tool_result_content', text: HUGE_TOOL_RESULT }],
        { timestamp: '2026-09-03T10:02:00.000Z' },
      ),
    ])
    store.upsertSession({
      sessionId: 'sess-content-001',
      harness: 'copilot',
      label: 'Content drill-down',
      payload: { id: 'sess-content-001', harness: 'copilot' },
    })
    // Session whose payload carries explicit turn descriptors: one current
    // 0-based `index` row and one legacy 1-based `turn` row (issue #184).
    store.upsertMany([
      turn('span-m0', [{ part: 'system_prompt', text: 'mixed zero', tokens: 2 }], {
        sessionId: 'sess-mixed-001',
        timestamp: '2026-09-03T11:00:00.000Z',
      }),
      turn('span-m1', [{ part: 'system_prompt', text: 'mixed one', tokens: 2 }], {
        sessionId: 'sess-mixed-001',
        timestamp: '2026-09-03T11:01:00.000Z',
      }),
    ])
    store.upsertSession({
      sessionId: 'sess-mixed-001',
      harness: 'copilot',
      label: 'Mixed turn descriptors',
      payload: {
        id: 'sess-mixed-001',
        harness: 'copilot',
        turns: [
          { index: 0, spanId: 'span-m0', model: 'm' },
          { turn: 2, spanId: 'span-m1', model: 'm' },
        ],
      },
    })
    // Payload rows whose legacy identity disagrees with array position
    // (issue #184 review): explicit identity must win over position.
    store.upsertMany([
      turn('span-pa', [{ part: 'system_prompt', text: 'precedence A', tokens: 1 }], {
        sessionId: 'sess-precedence-001',
        timestamp: '2026-09-03T12:00:00.000Z',
      }),
      turn('span-pb', [{ part: 'system_prompt', text: 'precedence B', tokens: 1 }], {
        sessionId: 'sess-precedence-001',
        timestamp: '2026-09-03T12:01:00.000Z',
      }),
    ])
    store.upsertSession({
      sessionId: 'sess-precedence-001',
      harness: 'copilot',
      label: 'Precedence descriptors',
      payload: {
        id: 'sess-precedence-001',
        harness: 'copilot',
        turns: [
          { turn: 2, spanId: 'span-pa', model: 'm' },
          { turn: 1, spanId: 'span-pb', model: 'm' },
        ],
      },
    })
    // Identity-bearing payload shorter than the record pool (issue #184
    // review): an unmatched identity must 404, not fall through to a
    // positional record match.
    store.upsertMany([
      turn('span-x0', [{ part: 'system_prompt', text: 'cross X0', tokens: 1 }], {
        sessionId: 'sess-cross-001',
        timestamp: '2026-09-03T13:00:00.000Z',
      }),
      turn('span-x1', [{ part: 'system_prompt', text: 'cross X1', tokens: 1 }], {
        sessionId: 'sess-cross-001',
        timestamp: '2026-09-03T13:01:00.000Z',
      }),
      turn('span-x2', [{ part: 'system_prompt', text: 'cross X2', tokens: 1 }], {
        sessionId: 'sess-cross-001',
        timestamp: '2026-09-03T13:02:00.000Z',
      }),
    ])
    store.upsertSession({
      sessionId: 'sess-cross-001',
      harness: 'copilot',
      label: 'Cross-source descriptors',
      payload: {
        id: 'sess-cross-001',
        harness: 'copilot',
        turns: [
          { index: 0, spanId: 'span-x0', model: 'm' },
          { index: 1, spanId: 'span-x1', model: 'm' },
        ],
      },
    })
    // Payload rows without any identity (issue #184 review): the canonical
    // positional lookup stays available as the only option.
    store.upsertMany([
      turn('span-q0', [{ part: 'system_prompt', text: 'noident Q0', tokens: 1 }], {
        sessionId: 'sess-noidentity-001',
        timestamp: '2026-09-03T14:00:00.000Z',
      }),
      turn('span-q1', [{ part: 'system_prompt', text: 'noident Q1', tokens: 1 }], {
        sessionId: 'sess-noidentity-001',
        timestamp: '2026-09-03T14:01:00.000Z',
      }),
    ])
    store.upsertSession({
      sessionId: 'sess-noidentity-001',
      harness: 'copilot',
      label: 'Identity-free descriptors',
      payload: {
        id: 'sess-noidentity-001',
        harness: 'copilot',
        turns: [{ spanId: 'span-q0', model: 'm' }],
      },
    })
    // Issue #216 / T3: claude-desktop synth spans with stored parts must
    // surface through the turn content route (happy path). A second session
    // with empty parts pins #184 honest empty — named session/turn/span,
    // no fabricated assembledText.
    store.upsertMany([
      claudeDesktopTurn(
        'synth:claude-desktop:sess-cd-parts-001:turn-0',
        [
          { part: 'conversation_history', text: CD_CONVERSATION, tokens: 12 },
          { part: 'tool_result_content', text: CD_TOOL_RESULT, tokens: 8 },
        ],
        {
          sessionId: 'sess-cd-parts-001',
          traceId: 'synth:claude-desktop:sess-cd-parts-001',
          timestamp: '2026-10-02T10:00:00.000Z',
        },
      ),
    ])
    store.upsertSession({
      sessionId: 'sess-cd-parts-001',
      harness: 'claude-desktop',
      label: 'Claude Desktop with parts',
      payload: { id: 'sess-cd-parts-001', harness: 'claude-desktop' },
    })
    store.upsertMany([
      claudeDesktopTurn(
        'synth:claude-desktop:sess-cd-empty-001:turn-0',
        [],
        {
          sessionId: 'sess-cd-empty-001',
          traceId: 'synth:claude-desktop:sess-cd-empty-001',
          timestamp: '2026-10-02T11:00:00.000Z',
        },
      ),
    ])
    store.upsertSession({
      sessionId: 'sess-cd-empty-001',
      harness: 'claude-desktop',
      label: 'Claude Desktop empty parts',
      payload: { id: 'sess-cd-empty-001', harness: 'claude-desktop' },
    })

    canonDb = new DatabaseSync(':memory:')

    testBridge = new KyberBridge({
      store,
      canonDb,
      ratesPath: join(tmpdir(), 'nonexistent-rates.json'),
    })

    server = await runWebDashboard({ port: 0, open: false, kyberBridge: testBridge, writeStdout: () => {} })
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  })

  afterAll(async () => {
    await new Promise<void>((resolve) => server.close(() => resolve()))
    store.close()
  })

  function assertStandardKyberHeaders(res: Response) {
    const contentType = res.headers.get('content-type')
    expect(contentType).toBeDefined()
    expect(contentType).toContain('application/json')
    expect(contentType).toContain('charset=utf-8')
    expect(res.headers.get('cache-control')).toBe('no-store')
  }

  describe('GET /api/kyber/session/:id/content', () => {
    it('returns a system prompt longer than 2000 characters untruncated', async () => {
      expect(SYSTEM_PROMPT.length).toBeGreaterThan(MAX_STRING_LENGTH)

      const res = await fetch(`${base}/api/kyber/session/sess-content-001/content?span=span-prompt`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as SessionContentResult
      expect(body.sessionId).toBe('sess-content-001')
      expect(body.spanId).toBe('span-prompt')
      expect(body.parts).toHaveLength(1)
      expect(body.parts[0].part).toBe('system_prompt')
      expect(body.parts[0].text).toBe(SYSTEM_PROMPT)
      expect(body.parts[0].text.length).toBe(SYSTEM_PROMPT.length)
      expect(body.parts[0].text).not.toContain('[truncated')
      expect(body.parts[0].truncated).toBeUndefined()
      expect(body.parts[0].tokens).toBe(5800)
    })

    it('filters content to one span', async () => {
      const res = await fetch(`${base}/api/kyber/session/sess-content-001/content?span=span-tools`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as SessionContentResult
      expect(body.spanId).toBe('span-tools')
      expect(body.parts.map((p) => p.part)).toEqual(['tool_definitions', 'conversation_history'])
      expect(body.parts.every((p) => p.spanId === 'span-tools')).toBe(true)
      expect(body.parts[0].text).toBe(TOOL_DEFINITION)
      expect(body.parts[0].server).toBe('built-in')
      expect(body.parts[1].text).toBe(CONVERSATION)
    })

    it('filters content to one canonical bucket', async () => {
      const res = await fetch(
        `${base}/api/kyber/session/sess-content-001/content?part=tool_definitions`,
      )
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as SessionContentResult
      expect(body.spanId).toBeUndefined()
      expect(body.parts).toHaveLength(1)
      expect(body.parts[0].part).toBe('tool_definitions')
      expect(body.parts[0].text).toBe(TOOL_DEFINITION)
      expect(body.parts[0].server).toBe('built-in')
      expect(body.parts[0].tokens).toBe(24)
    })

    it('flags truncation and reports totalLength when the response budget is exceeded', async () => {
      const res = await fetch(`${base}/api/kyber/session/sess-content-001/content?span=span-huge`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as SessionContentResult
      expect(body.parts).toHaveLength(1)
      const part = body.parts[0]
      expect(part.part).toBe('tool_result_content')
      expect(part.truncated).toBe(true)
      expect(part.totalLength).toBe(HUGE_TOOL_RESULT.length)
      expect(part.text.length).toBe(CONTENT_RESPONSE_BUDGET)
      expect(part.text).toBe(HUGE_TOOL_RESULT.slice(0, CONTENT_RESPONSE_BUDGET))
      expect(part.tokens).toBeUndefined()
    })
  })

  describe('Edge cases and error handling', () => {
    it('returns HTTP 404 JSON for an unknown session', async () => {
      const res = await fetch(`${base}/api/kyber/session/nonexistent-xyz-999/content`)
      expect(res.status).toBe(404)
      assertStandardKyberHeaders(res)
      const body = (await res.json()) as { error: string }
      expect(body).toEqual({ error: 'Session not found' })
    })

    it('returns HTTP 405 Method Not Allowed JSON with no-store for non-GET methods', async () => {
      const url = `${base}/api/kyber/session/sess-content-001/content`
      for (const method of ['POST', 'PUT', 'DELETE', 'PATCH']) {
        const res = await fetch(url, { method })
        expect(res.status).toBe(405)
        assertStandardKyberHeaders(res)
        const body = (await res.json()) as { error: string }
        expect(body).toEqual({ error: 'Method Not Allowed' })
      }
    })
  })

  describe('GET /api/kyber/session/:id/turn/:index/content (Task G1 / Decision D14)', () => {
    it('returns assembled turn content with blocks, parts, and full assembledText', async () => {
      const res = await fetch(`${base}/api/kyber/session/sess-content-001/turn/0/content`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as TurnContentResult
      expect(body.sessionId).toBe('sess-content-001')
      expect(body.turnIndex).toBe(0)
      expect(body.spanId).toBe('span-prompt')
      expect(body.blocks).toBeDefined()
      expect(body.parts).toBeDefined()
      expect(body.assembledText).toBeDefined()

      const sysBlock = body.blocks.find((b) => b.key === 'system_prompt')
      expect(sysBlock).toBeDefined()
      expect(sysBlock?.text).toBe(SYSTEM_PROMPT)
      expect(body.assembledText).toContain(SYSTEM_PROMPT)
    })

    it('returns parts with tools, user messages, and conversation history for turn 1', async () => {
      const res = await fetch(`${base}/api/kyber/session/sess-content-001/turn/1/content`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as TurnContentResult
      expect(body.spanId).toBe('span-tools')
      expect(body.parts.some((p) => p.part === 'tool_definitions')).toBe(true)
      expect(body.parts.some((p) => p.part === 'user_messages' || p.part === 'conversation_history')).toBe(true)

      const toolPart = body.parts.find((p) => p.part === 'tool_definitions')
      expect(toolPart?.text).toBe(TOOL_DEFINITION)
      expect(toolPart?.server).toBe('built-in')

      const convPart = body.parts.find((p) => p.part === 'user_messages' || p.part === 'conversation_history')
      expect(convPart?.text).toContain('why is the inspector showing a stub mid-sentence?')
    })

    it('supports ?turn=1 query param on /session/:id/content as alias', async () => {
      const res = await fetch(`${base}/api/kyber/session/sess-content-001/content?turn=1`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as TurnContentResult
      expect(body.spanId).toBe('span-tools')
      expect(body.turnIndex).toBe(1)
    })

    it('returns 404 for nonexistent turn index', async () => {
      const res = await fetch(`${base}/api/kyber/session/sess-content-001/turn/999/content`)
      expect(res.status).toBe(404)
      assertStandardKyberHeaders(res)
    })

    describe('strict 0-based turn resolution (issue #184)', () => {
      it('returns 404 for a turn index equal to the turn count instead of the last turn', async () => {
        // sess-content-001 holds 3 turns (indices 0-2). The 1-based
        // `turns[turnIndex - 1]` fallback serves the last turn with 200 here.
        const res = await fetch(`${base}/api/kyber/session/sess-content-001/turn/3/content`)
        expect(res.status).toBe(404)
        assertStandardKyberHeaders(res)
      })

      it('resolves a 0-based index to exactly that span', async () => {
        const res = await fetch(`${base}/api/kyber/session/sess-content-001/turn/2/content`)
        expect(res.status).toBe(200)
        const body = (await res.json()) as TurnContentResult
        expect(body.turnIndex).toBe(2)
        expect(body.spanId).toBe('span-huge')
      })

      it('resolves a legacy 1-based `turn` descriptor via `turn - 1`', async () => {
        const res = await fetch(`${base}/api/kyber/session/sess-mixed-001/turn/1/content`)
        expect(res.status).toBe(200)
        const body = (await res.json()) as TurnContentResult
        expect(body.spanId).toBe('span-m1')
        expect(body.assembledText).toContain('mixed one')
      })

      it('returns 404 past the end of a session with legacy descriptors', async () => {
        // Two payload turns: valid 0-based indices are 0 and 1.
        const res = await fetch(`${base}/api/kyber/session/sess-mixed-001/turn/2/content`)
        expect(res.status).toBe(404)
        assertStandardKyberHeaders(res)
      })

      it('prefers explicit legacy identity over array position (issue #184 review)', async () => {
        // Payload order is [{turn: 2}, {turn: 1}]: transport 0 is the second
        // row (span-pb), transport 1 the first (span-pa). A positional match
        // would serve span-pa for both.
        const first = await fetch(`${base}/api/kyber/session/sess-precedence-001/turn/0/content`)
        expect(first.status).toBe(200)
        const firstBody = (await first.json()) as TurnContentResult
        expect(firstBody.spanId).toBe('span-pb')
        expect(firstBody.assembledText).toContain('precedence B')

        const second = await fetch(`${base}/api/kyber/session/sess-precedence-001/turn/1/content`)
        expect(second.status).toBe(200)
        const secondBody = (await second.json()) as TurnContentResult
        expect(secondBody.spanId).toBe('span-pa')
        expect(secondBody.assembledText).toContain('precedence A')
      })

      it('returns 404 when an identity-bearing payload misses, without falling through to records (issue #184 review)', async () => {
        // Two identified payload turns but three records: transport 2 matches
        // no identity, and must not resolve positionally to span-x2.
        const res = await fetch(`${base}/api/kyber/session/sess-cross-001/turn/2/content`)
        expect(res.status).toBe(404)
        assertStandardKyberHeaders(res)
      })

      it('resolves identified payload turns while the cross-source guard stands', async () => {
        const res = await fetch(`${base}/api/kyber/session/sess-cross-001/turn/1/content`)
        expect(res.status).toBe(200)
        const body = (await res.json()) as TurnContentResult
        expect(body.spanId).toBe('span-x1')
        expect(body.assembledText).toContain('cross X1')
      })

      it('preserves the canonical positional lookup for identity-free payload rows (issue #184 review)', async () => {
        // One identity-free payload row, two records: transport 1 has no
        // payload answer, so the positional record lookup still serves span-q1.
        const res = await fetch(`${base}/api/kyber/session/sess-noidentity-001/turn/1/content`)
        expect(res.status).toBe(200)
        const body = (await res.json()) as TurnContentResult
        expect(body.spanId).toBe('span-q1')
        expect(body.assembledText).toContain('noident Q1')
      })
    })

    it('flags budget truncation when turn content exceeds budget param', async () => {
      const res = await fetch(`${base}/api/kyber/session/sess-content-001/turn/2/content?budget=100`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as TurnContentResult
      expect(body.truncated).toBe(true)
      expect(body.totalLength).toBeGreaterThan(100)
      expect(body.assembledText.length).toBeLessThanOrEqual(100)
    })

    describe('claude-desktop / synth: turn content (issue #216 / T3)', () => {
      it('returns measurable conversation and tool-result parts for a synth span that stored them', async () => {
        const res = await fetch(`${base}/api/kyber/session/sess-cd-parts-001/turn/0/content`)
        expect(res.status).toBe(200)
        assertStandardKyberHeaders(res)

        const body = (await res.json()) as TurnContentResult
        expect(body.sessionId).toBe('sess-cd-parts-001')
        expect(body.turnIndex).toBe(0)
        expect(body.spanId).toBe('synth:claude-desktop:sess-cd-parts-001:turn-0')
        expect(body.parts.length).toBeGreaterThan(0)
        expect(
          body.parts.some((p) => p.part === 'conversation_history' || p.part === 'user_messages'),
        ).toBe(true)
        expect(body.parts.some((p) => p.part === 'tool_result_content')).toBe(true)

        const convBlock = body.blocks.find((b) => b.key === 'conversation_history')
        expect(convBlock?.text).toContain('why is the claude-desktop inspector empty?')
        const toolBlock = body.blocks.find((b) => b.key === 'tool_result_content')
        expect(toolBlock?.text).toBe(CD_TOOL_RESULT)
        expect(body.assembledText.trim().length).toBeGreaterThan(0)
        expect(body.assembledText).toContain('why is the claude-desktop inspector empty?')
        expect(body.assembledText).toContain(CD_TOOL_RESULT)
      })

      it('keeps #184 honest empty when a synth span stored no parts (no fabricated assembledText)', async () => {
        const res = await fetch(`${base}/api/kyber/session/sess-cd-empty-001/turn/0/content`)
        expect(res.status).toBe(200)
        assertStandardKyberHeaders(res)

        const body = (await res.json()) as TurnContentResult
        // Named whereabouts so an audit can tell honest absence from a
        // mis-resolved neighbor (issue #184).
        expect(body.sessionId).toBe('sess-cd-empty-001')
        expect(body.turnIndex).toBe(0)
        expect(body.spanId).toBe('synth:claude-desktop:sess-cd-empty-001:turn-0')
        expect(body.parts).toEqual([])
        expect(body.assembledText).toBe('')
        for (const block of body.blocks) {
          expect(block.text).toBe('')
          expect(block.parts).toEqual([])
        }
        // No fabricated conversation or tool-result text from counters alone.
        expect(body.assembledText).not.toContain('user:')
        expect(body.assembledText).not.toContain('assistant:')
      })
    })
  })
})
