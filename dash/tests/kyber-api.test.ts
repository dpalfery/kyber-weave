import type { AddressInfo } from 'net'
import type { Server } from 'http'
import { tmpdir } from 'os'
import { join } from 'path'
import { DatabaseSync } from 'node:sqlite'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'

import { runWebDashboard } from '../src/web-dashboard.js'
import { KyberBridge } from '../kyber/server/bridge.js'

describe('Backend Contract Tests: /api/kyber/* Endpoints', () => {
  let server: Server
  let base: string
  let canonDb: DatabaseSync
  let sessionsDb: DatabaseSync
  let testBridge: KyberBridge

  const copilotPayload = {
    id: 'sess-copilot-001',
    harness: 'copilot',
    label: 'Copilot Test Session',
    is_subagent: false,
    parent_session: null,
    agent_name: 'test-copilot-agent',
    repo: 'kyber-weave',
    branch: 'main',
    started: '2026-09-03T10:00:00.000Z',
    ended: '2026-09-03T10:15:00.000Z',
    summary: {
      turn_count: 4,
      request_count: 4,
      total_input: 4000,
      total_output: 1200,
      total_cache_read: 800,
      total_cache_creation: 200,
      schema_tokens_per_turn: 150,
      cost: { usd: 0.12, basis: 'published_rates', status: 'ok' },
      models: ['gpt-4o'],
      duration_ms: 900000,
    },
    context: {
      measurable: true,
      contextLimit: 200000,
      residualTotal: 50,
      derivedCounts: true,
      turns: [
        {
          index: 0,
          input: 1000,
          fresh: 250,
          buckets: {
            system_prompt: 150,
            tool_definitions: 200,
            instruction_context: 350,
            conversation_history: 150,
            tool_result_content: 150,
          },
        },
        {
          index: 1,
          input: 2000,
          fresh: 300,
          buckets: {
            system_prompt: 150,
            tool_definitions: 200,
            instruction_context: 500,
            conversation_history: 550,
            tool_result_content: 600,
          },
        },
      ],
    },
    tools: [
      {
        name: 'read_file',
        server: 'built-in',
        total_schema_cost: 150,
        invocations: 3,
      },
      {
        name: 'run_command',
        server: 'shell-tool',
        total_schema_cost: 250,
        invocations: 0,
      },
    ],
    turns: [
      {
        index: 0,
        input: 1000,
        output: 300,
        fresh: 250,
        cache_read: 200,
        cache_creation: 50,
        model: 'gpt-4o',
      },
    ],
    timeline: {
      spanId: 'span-root-copilot',
      parentId: null,
      name: 'Copilot Test Session',
      kind: 'session',
      startMs: 0,
      durationMs: 900000,
      cost: { basis: 'published_rates', status: 'ok', value: 0.12, currency: 'USD' },
      children: [
        {
          spanId: 'span-child-1',
          parentId: 'span-root-copilot',
          name: 'read_file',
          kind: 'tool',
          startMs: 1000,
          durationMs: 50,
          cost: { basis: 'published_rates', status: 'ok', value: 0.001, currency: 'USD' },
          children: [],
        },
      ],
    },
  }

  const geminiPayload = {
    id: 'sess-gemini-002',
    harness: 'gemini',
    label: 'Gemini Test Session',
    is_subagent: false,
    parent_session: null,
    agent_name: 'test-gemini-agent',
    repo: 'kyber-weave',
    branch: 'main',
    started: '2026-09-03T09:00:00.000Z',
    ended: '2026-09-03T09:10:00.000Z',
    summary: {
      turn_count: 2,
      request_count: 2,
      total_input: 2000,
      total_output: 600,
      cost: { usd: 0.06, basis: 'published_rates', status: 'ok' },
      models: ['gemini-1.5-pro'],
      duration_ms: 600000,
    },
    timeline: {
      spanId: 'span-root-gemini',
      children: [],
    },
  }

  beforeAll(async () => {
    canonDb = new DatabaseSync(':memory:')
    sessionsDb = new DatabaseSync(':memory:')

    canonDb.exec(`
      CREATE TABLE session (
        session_id TEXT PRIMARY KEY,
        harness TEXT NOT NULL,
        label TEXT,
        is_subagent INTEGER DEFAULT 0,
        parent_session TEXT,
        agent_name TEXT,
        repo TEXT,
        branch TEXT,
        started TEXT,
        ended TEXT,
        payload TEXT
      );
      CREATE TABLE records (
        span_id TEXT PRIMARY KEY,
        trace_id TEXT,
        parent_span_id TEXT,
        harness TEXT,
        source TEXT,
        name TEXT,
        timestamp TEXT,
        op TEXT
      );
      CREATE TABLE quarantine (
        span_id TEXT PRIMARY KEY,
        source TEXT,
        name TEXT,
        namespaces TEXT,
        reason TEXT,
        seen_at INTEGER
      );
      CREATE TABLE problem (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT,
        span_id TEXT,
        severity TEXT,
        code TEXT,
        message TEXT,
        at INTEGER,
        harness TEXT
      );
    `)

    sessionsDb.exec(`
      CREATE TABLE session (
        session_id TEXT PRIMARY KEY,
        harness TEXT NOT NULL,
        label TEXT,
        is_subagent INTEGER DEFAULT 0,
        parent_session TEXT,
        agent_name TEXT,
        repo TEXT,
        branch TEXT,
        started TEXT,
        ended TEXT,
        payload TEXT
      );
      CREATE TABLE quarantine (
        span_id TEXT PRIMARY KEY,
        source TEXT,
        name TEXT,
        namespaces TEXT,
        reason TEXT,
        seen_at INTEGER
      );
      CREATE TABLE problem (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT,
        span_id TEXT,
        severity TEXT,
        code TEXT,
        message TEXT,
        at INTEGER,
        harness TEXT
      );
    `)

    // Seed session in canonDb
    canonDb
      .prepare('INSERT INTO session VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)')
      .run(
        'sess-copilot-001',
        'copilot',
        'Copilot Test Session',
        0,
        null,
        'test-copilot-agent',
        'kyber-weave',
        'main',
        '2026-09-03T10:00:00.000Z',
        '2026-09-03T10:15:00.000Z',
        JSON.stringify(copilotPayload)
      )

    // Seed session in sessionsDb
    sessionsDb
      .prepare('INSERT INTO session VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)')
      .run(
        'sess-gemini-002',
        'gemini',
        'Gemini Test Session',
        0,
        null,
        'test-gemini-agent',
        'kyber-weave',
        'main',
        '2026-09-03T09:00:00.000Z',
        '2026-09-03T09:10:00.000Z',
        JSON.stringify(geminiPayload)
      )

    // Seed quarantine records
    canonDb
      .prepare('INSERT INTO quarantine VALUES (?, ?, ?, ?, ?, ?)')
      .run('quar-101', 'copilot', 'unmapped_span', '["custom.namespace"]', 'Namespace unmapped', 1725360000)

    sessionsDb
      .prepare('INSERT INTO quarantine VALUES (?, ?, ?, ?, ?, ?)')
      .run('quar-102', 'gemini', 'gemini_attr_span', '["unknown"]', 'Malformed attribute', 1725360100)

    // Seed problem records
    canonDb
      .prepare(
        'INSERT INTO problem (session_id, span_id, severity, code, message, at, harness) VALUES (?, ?, ?, ?, ?, ?, ?)'
      )
      .run(
        'sess-copilot-001',
        'span-prob-1',
        'error',
        'invalid_tokens',
        'Input tokens sum exceeds recorded total',
        1725360000,
        'copilot'
      )

    sessionsDb
      .prepare(
        'INSERT INTO problem (session_id, span_id, severity, code, message, at, harness) VALUES (?, ?, ?, ?, ?, ?, ?)'
      )
      .run(
        'sess-gemini-002',
        'span-prob-2',
        'warning',
        'basis_diff',
        'Cost basis calculation discrepancy',
        1725360100,
        'gemini'
      )

    testBridge = new KyberBridge({
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
  })

  function assertStandardKyberHeaders(res: Response) {
    const contentType = res.headers.get('content-type')
    expect(contentType).toBeDefined()
    expect(contentType).toContain('application/json')
    expect(contentType).toContain('charset=utf-8')
    expect(res.headers.get('cache-control')).toBe('no-store')
  }

  describe('GET /api/kyber/sessions', () => {
    it('returns sessions list with proper headers and complete schema', async () => {
      const res = await fetch(`${base}/api/kyber/sessions`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as { sessions: Array<Record<string, unknown>> }
      expect(Array.isArray(body.sessions)).toBe(true)
      expect(body.sessions.length).toBe(2)

      const copilotSession = body.sessions.find((s) => s.session_id === 'sess-copilot-001')
      expect(copilotSession).toBeDefined()
      expect(copilotSession?.harness).toBe('copilot')
      expect(copilotSession?.label).toBe('Copilot Test Session')
      expect(copilotSession?.agent_name).toBe('test-copilot-agent')
      expect(copilotSession?.repo).toBe('kyber-weave')
      expect(copilotSession?.branch).toBe('main')
      expect(copilotSession?.turn_count).toBe(4)
      expect(copilotSession?.cost_usd).toBe(0.12)
      expect(copilotSession?.total_input).toBe(4000)

      const geminiSession = body.sessions.find((s) => s.session_id === 'sess-gemini-002')
      expect(geminiSession).toBeDefined()
      expect(geminiSession?.harness).toBe('gemini')
    })

    it('respects limit query parameter', async () => {
      const res = await fetch(`${base}/api/kyber/sessions?limit=1`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as { sessions: Array<Record<string, unknown>> }
      expect(body.sessions.length).toBe(1)
    })

    it('filters sessions by harness parameter', async () => {
      const resCopilot = await fetch(`${base}/api/kyber/sessions?harness=copilot`)
      expect(resCopilot.status).toBe(200)
      const bodyCopilot = (await resCopilot.json()) as { sessions: Array<{ harness: string }> }
      expect(bodyCopilot.sessions.length).toBe(1)
      expect(bodyCopilot.sessions[0].harness).toBe('copilot')

      const resGemini = await fetch(`${base}/api/kyber/sessions?harness=gemini`)
      expect(resGemini.status).toBe(200)
      const bodyGemini = (await resGemini.json()) as { sessions: Array<{ harness: string }> }
      expect(bodyGemini.sessions.length).toBe(1)
      expect(bodyGemini.sessions[0].harness).toBe('gemini')
    })
  })

  describe('GET /api/kyber/session/:id', () => {
    it('returns full session payload object by path parameter', async () => {
      const res = await fetch(`${base}/api/kyber/session/sess-copilot-001`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as typeof copilotPayload
      expect(body.id).toBe('sess-copilot-001')
      expect(body.harness).toBe('copilot')
      expect(body.label).toBe('Copilot Test Session')
      expect(body.summary.cost.usd).toBe(0.12)
      expect(Array.isArray(body.tools)).toBe(true)
      expect(body.tools.length).toBe(2)
      expect(body.context.measurable).toBe(true)
      expect(body.timeline.spanId).toBe('span-root-copilot')
    })

    it('returns full session payload object by query parameter (?id=...)', async () => {
      const res = await fetch(`${base}/api/kyber/session?id=sess-copilot-001`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as typeof copilotPayload
      expect(body.id).toBe('sess-copilot-001')
      expect(body.harness).toBe('copilot')
    })

    it('handles url-encoded session identifiers correctly', async () => {
      const encodedId = encodeURIComponent('sess-copilot-001')
      const res = await fetch(`${base}/api/kyber/session/${encodedId}`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as typeof copilotPayload
      expect(body.id).toBe('sess-copilot-001')
    })
  })

  describe('GET /api/kyber/compare', () => {
    it('returns comparison matrix with harnesses, rows, and problems', async () => {
      const res = await fetch(`${base}/api/kyber/compare`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as {
        harnesses: string[]
        rows: Array<{ label?: string; metric?: string }>
        problems: unknown[]
      }
      expect(Array.isArray(body.harnesses)).toBe(true)
      expect(body.harnesses).toContain('copilot')
      expect(body.harnesses).toContain('gemini')
      expect(Array.isArray(body.rows)).toBe(true)
      expect(body.rows.length).toBeGreaterThan(0)
      expect(Array.isArray(body.problems)).toBe(true)
    })
  })

  describe('GET /api/kyber/quarantine', () => {
    it('returns quarantine entries array with correct fields', async () => {
      const res = await fetch(`${base}/api/kyber/quarantine`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as {
        entries: Array<{
          span_id: string
          source: string
          name: string
          namespaces: string
          reason: string
          seen_at: number
        }>
      }
      expect(Array.isArray(body.entries)).toBe(true)
      expect(body.entries.length).toBe(2)

      const quar1 = body.entries.find((e) => e.span_id === 'quar-101')
      expect(quar1).toBeDefined()
      expect(quar1?.source).toBe('copilot')
      expect(quar1?.reason).toBe('Namespace unmapped')

      const quar2 = body.entries.find((e) => e.span_id === 'quar-102')
      expect(quar2).toBeDefined()
      expect(quar2?.source).toBe('gemini')
    })

    it('respects limit parameter on quarantine endpoint', async () => {
      const res = await fetch(`${base}/api/kyber/quarantine?limit=1`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as { entries: unknown[] }
      expect(body.entries.length).toBe(1)
    })
  })

  describe('GET /api/kyber/problems', () => {
    it('returns recorded problems array with complete structure', async () => {
      const res = await fetch(`${base}/api/kyber/problems`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as {
        problems: Array<{
          id: number
          session_id: string
          span_id: string
          severity: string
          code: string
          message: string
          at: number
          harness: string
        }>
      }
      expect(Array.isArray(body.problems)).toBe(true)
      expect(body.problems.length).toBe(2)

      const p1 = body.problems.find((p) => p.span_id === 'span-prob-1')
      expect(p1).toBeDefined()
      expect(p1?.severity).toBe('error')
      expect(p1?.code).toBe('invalid_tokens')
      expect(p1?.message).toBe('Input tokens sum exceeds recorded total')
      expect(p1?.harness).toBe('copilot')

      const p2 = body.problems.find((p) => p.span_id === 'span-prob-2')
      expect(p2).toBeDefined()
      expect(p2?.severity).toBe('warning')
      expect(p2?.code).toBe('basis_diff')
      expect(p2?.harness).toBe('gemini')
    })

    it('respects limit parameter on problems endpoint', async () => {
      const res = await fetch(`${base}/api/kyber/problems?limit=1`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as { problems: unknown[] }
      expect(body.problems.length).toBe(1)
    })
  })

  describe('GET /api/kyber/meta', () => {
    it('returns metadata containing span_count, tokenizer, and rates', async () => {
      const res = await fetch(`${base}/api/kyber/meta`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as {
        span_count: number
        tokenizer: { kind: string }
        rates: { credit_usd: number }
        harnesses: Record<string, unknown>
        sources: unknown[]
      }
      expect(typeof body.span_count).toBe('number')
      expect(body.tokenizer).toBeDefined()
      expect(typeof body.tokenizer.kind).toBe('string')
      expect(body.rates).toBeDefined()
      expect(typeof body.rates.credit_usd).toBe('number')
      expect(body.harnesses).toBeDefined()
      expect(Array.isArray(body.sources)).toBe(true)
    })
  })

  describe('Backward-compatible endpoints (/context, /schema, /timeline)', () => {
    it('GET /api/kyber/context returns context analysis JSON', async () => {
      const res = await fetch(`${base}/api/kyber/context?id=sess-copilot-001`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as {
        measurable: boolean
        contextLimit: number
        turns: unknown[]
      }
      expect(body.measurable).toBe(true)
      expect(body.contextLimit).toBe(200000)
      expect(Array.isArray(body.turns)).toBe(true)
      expect(body.turns.length).toBeGreaterThan(0)
    })

    it('GET /api/kyber/schema returns tool schema analysis JSON', async () => {
      const res = await fetch(`${base}/api/kyber/schema?id=sess-copilot-001`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as {
        measurable: boolean
        ranked: Array<{ name: string; server: string; cost: number; invoked: boolean }>
        byServer: Record<string, number>
        tools: unknown[]
      }
      expect(body.measurable).toBe(true)
      expect(Array.isArray(body.ranked)).toBe(true)
      expect(body.ranked.length).toBe(2)
      expect(body.byServer['built-in']).toBe(150)
      expect(body.byServer['shell-tool']).toBe(250)
      expect(Array.isArray(body.tools)).toBe(true)
    })

    it('GET /api/kyber/timeline returns execution timeline root tree node', async () => {
      const res = await fetch(`${base}/api/kyber/timeline?id=sess-copilot-001`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as {
        spanId: string
        name: string
        children: Array<{ spanId: string; name: string }>
        cost: { basis: string; value: number }
      }
      expect(body.spanId).toBe('span-root-copilot')
      expect(body.name).toBe('Copilot Test Session')
      expect(Array.isArray(body.children)).toBe(true)
      expect(body.children.length).toBe(1)
      expect(body.children[0].spanId).toBe('span-child-1')
      expect(body.cost.value).toBe(0.12)
    })

    it('backward-compatible endpoints default to first available session when no id is given', async () => {
      const res = await fetch(`${base}/api/kyber/context`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as { measurable: boolean }
      expect(body.measurable).toBe(true)
    })
  })

  describe('Edge cases and error handling', () => {
    it('returns HTTP 404 JSON for nonexistent session ID', async () => {
      // By path
      const resPath = await fetch(`${base}/api/kyber/session/nonexistent-xyz-999`)
      expect(resPath.status).toBe(404)
      assertStandardKyberHeaders(resPath)
      const bodyPath = (await resPath.json()) as { error: string }
      expect(bodyPath).toEqual({ error: 'Session not found' })

      // By query parameter
      const resQuery = await fetch(`${base}/api/kyber/session?id=nonexistent-xyz-999`)
      expect(resQuery.status).toBe(404)
      assertStandardKyberHeaders(resQuery)
      const bodyQuery = (await resQuery.json()) as { error: string }
      expect(bodyQuery).toEqual({ error: 'Session not found' })
    })

    it('returns HTTP 400 JSON for missing session ID', async () => {
      // Direct /api/kyber/session with no id
      const res1 = await fetch(`${base}/api/kyber/session`)
      expect(res1.status).toBe(400)
      assertStandardKyberHeaders(res1)
      const body1 = (await res1.json()) as { error: string }
      expect(body1).toEqual({ error: 'Missing session id' })

      // Empty path parameter /api/kyber/session/
      const res2 = await fetch(`${base}/api/kyber/session/`)
      expect(res2.status).toBe(400)
      assertStandardKyberHeaders(res2)
      const body2 = (await res2.json()) as { error: string }
      expect(body2).toEqual({ error: 'Missing session id' })

      // Empty query parameter /api/kyber/session?id=
      const res3 = await fetch(`${base}/api/kyber/session?id=`)
      expect(res3.status).toBe(400)
      assertStandardKyberHeaders(res3)
      const body3 = (await res3.json()) as { error: string }
      expect(body3).toEqual({ error: 'Missing session id' })

      // Whitespace query parameter
      const res4 = await fetch(`${base}/api/kyber/session?id=%20%20`)
      expect(res4.status).toBe(400)
      assertStandardKyberHeaders(res4)
      const body4 = (await res4.json()) as { error: string }
      expect(body4).toEqual({ error: 'Missing session id' })
    })

    it('returns HTTP 405 Method Not Allowed JSON with no-store for non-GET methods', async () => {
      const endpoints = [
        { url: `${base}/api/kyber/sessions`, methods: ['POST', 'PUT', 'DELETE', 'PATCH'] },
        { url: `${base}/api/kyber/session/sess-copilot-001`, methods: ['POST', 'PUT', 'DELETE', 'PATCH'] },
        { url: `${base}/api/kyber/compare`, methods: ['POST', 'PUT', 'DELETE'] },
        { url: `${base}/api/kyber/quarantine`, methods: ['POST', 'PUT', 'DELETE'] },
        { url: `${base}/api/kyber/problems`, methods: ['POST', 'PUT', 'DELETE'] },
        { url: `${base}/api/kyber/meta`, methods: ['POST', 'PUT', 'DELETE'] },
        { url: `${base}/api/kyber/context`, methods: ['POST', 'PUT', 'DELETE'] },
        { url: `${base}/api/kyber/schema`, methods: ['POST', 'PUT', 'DELETE'] },
        { url: `${base}/api/kyber/timeline`, methods: ['POST', 'PUT', 'DELETE'] },
      ]

      for (const { url, methods } of endpoints) {
        for (const method of methods) {
          const res = await fetch(url, { method })
          expect(res.status).toBe(405)
          assertStandardKyberHeaders(res)
          const body = (await res.json()) as { error: string }
          expect(body).toEqual({ error: 'Method Not Allowed' })
        }
      }
    })

    it('returns HTTP 404 JSON (never HTML) for unrecognized /api/kyber/* paths', async () => {
      const unrecognizedPaths = [
        `${base}/api/kyber`,
        `${base}/api/kyber/`,
        `${base}/api/kyber/non-existent-route`,
        `${base}/api/kyber/sub/route/does/not/exist`,
        `${base}/api/kyber/foo?bar=baz`,
      ]

      for (const path of unrecognizedPaths) {
        const res = await fetch(path)
        expect(res.status).toBe(404)
        assertStandardKyberHeaders(res)
        const body = (await res.json()) as { error: string }
        expect(body).toEqual({ error: 'Not found' })
      }
    })
  })
})
