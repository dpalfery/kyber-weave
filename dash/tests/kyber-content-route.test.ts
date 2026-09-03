import type { AddressInfo } from 'net'
import type { Server } from 'http'
import { tmpdir } from 'os'
import { join } from 'path'
import { DatabaseSync } from 'node:sqlite'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'

import { runWebDashboard } from '../src/web-dashboard.js'
import {
  CONTENT_RESPONSE_BUDGET,
  KyberBridge,
  MAX_STRING_LENGTH,
  type SessionContentResult,
} from '../kyber/server/bridge.js'
import { CanonStore } from '../kyber/canon/store.js'
import type { CanonicalRecord, ContentPart } from '../kyber/canon/types.js'

describe('Backend Contract Tests: GET /api/kyber/session/:id/content', () => {
  let server: Server
  let base: string
  let canonDb: DatabaseSync
  let sessionsDb: DatabaseSync
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

    canonDb = new DatabaseSync(':memory:')
    sessionsDb = new DatabaseSync(':memory:')

    testBridge = new KyberBridge({
      store,
      canonDb,
      sessionsDb,
      ratesPath: join(tmpdir(), 'nonexistent-rates.json'),
    })

    server = await runWebDashboard({
      period: 'today',
      provider: 'all',
      project: [],
      exclude: [],
      port: 0,
      open: false,
      kyberBridge: testBridge,
    })
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
})
