import type { AddressInfo } from 'net'
import type { Server } from 'http'
import { mkdtempSync, readFileSync, rmSync } from 'fs'
import { tmpdir } from 'os'
import { join } from 'path'
import { DatabaseSync } from 'node:sqlite'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'

import { runWebDashboard } from '../cli/web.js'
import { buildRuns } from '../canon/runs.js'
import { buildSessions } from '../canon/sessions.js'
import { CanonStore } from '../canon/store.js'
import { projectCanonicalStore } from '../canon/projection.js'
import { loadPricing } from '../pricing/models.js'
import type { CanonicalRecord, CostBlock } from '../canon/types.js'
import { KyberBridge } from './bridge.js'

const asadShape = JSON.parse(
  readFileSync(new URL('../canon/fixtures/asad-session-shape.json', import.meta.url), 'utf8')
) as Record<string, unknown>

describe('Backend Contract Tests: /api/kyber/* Endpoints', () => {
  let server: Server
  let base: string
  let canonDb: DatabaseSync
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
      cost: { basis: 'published', status: 'priced', value: 0.12, currency: 'USD' },
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
    schema: {
      measurable: true,
      byServer: {
        'built-in': 150,
        'shell-tool': 250,
      },
      neverInvoked: [
        {
          name: 'run_command',
          server: 'shell-tool',
          cost: 250,
          invoked: false,
        },
      ],
      unusedRange: {
        tokenResidencies: 250,
        floor: 250,
        ceiling: 250,
      },
      turns: 4,
    },
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
      cost: { basis: 'published', status: 'no_rate' },
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
        session_id TEXT,
        harness TEXT,
        source TEXT,
        name TEXT,
        timestamp TEXT,
        op TEXT,
        content_json TEXT,
        tokens_json TEXT,
        cost_json TEXT
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
    canonDb
      .prepare('INSERT INTO session VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)')
      .run(
        'sess-asad-contract',
        'copilot',
        'ASAD Contract Session',
        0,
        null,
        'synthetic-agent',
        'synthetic-repo',
        'main',
        '2026-09-02T10:00:00.000Z',
        '2026-09-02T10:01:00.000Z',
        JSON.stringify(asadShape)
      )

    const fullCanonicalPart = 'C'.repeat(2_400) + 'FULL_CANONICAL_PART_END'
    const clippedPayloadPreview = fullCanonicalPart.slice(0, 2_000)
    canonDb
      .prepare(
        'INSERT INTO records (span_id, trace_id, parent_span_id, session_id, harness, source, name, timestamp, op, content_json) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)',
      )
      .run(
        'span-content-001',
        'sess-copilot-001',
        null,
        'sess-copilot-001',
        'copilot',
        'synthetic',
        'content fixture turn',
        '2026-09-03T10:01:00.000Z',
        'llm.invoke',
        JSON.stringify({ system_prompt: fullCanonicalPart, instruction_context: clippedPayloadPreview }),
      )

    // Raw record-only group: a trace with no derived `session` row. It sits
    // between the derived sessions by timestamp so any list that synthesizes
    // it displaces real derived rows — it must never be reported as a session.
    canonDb
      .prepare(
        'INSERT INTO records (span_id, trace_id, parent_span_id, session_id, harness, source, name, timestamp, op, content_json, tokens_json, cost_json) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)',
      )
      .run(
        'span-raw-only-001',
        'trace-record-only',
        null,
        null,
        'pi',
        'pi-agent',
        'Pi Record-Only Trace',
        '2026-09-03T09:30:00.000Z',
        'llm.invoke',
        null,
        JSON.stringify({ freshInput: 40, cacheRead: 0, cacheCreation: 0, output: 20, reportedModel: 'pi-default' }),
        JSON.stringify({ basis: 'published', status: 'priced', value: 0.002, currency: 'USD' }),
      )

    // Seed all API-visible data in the canonical store.
    canonDb
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

    canonDb
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

    canonDb
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
      ratesPath: join(tmpdir(), 'nonexistent-rates.json'),
    })

    server = await runWebDashboard({ port: 0, open: false, kyberBridge: testBridge, writeStdout: () => {} })
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
    it('ignores legacy database environment variables', async () => {
      const directory = mkdtempSync(join(tmpdir(), 'kyber-api-legacy-db-'))
      const legacyPath = join(directory, 'sessions.db')
      const previousAgentdashDb = process.env.AGENTDASH_DB
      const previousKyberDb = process.env.KYBER_DB
      const legacyDb = new DatabaseSync(legacyPath)
      legacyDb.exec(`
        CREATE TABLE session (
          session_id TEXT PRIMARY KEY, harness TEXT NOT NULL, label TEXT,
          is_subagent INTEGER, parent_session TEXT, agent_name TEXT, repo TEXT,
          branch TEXT, started TEXT, ended TEXT, payload TEXT
        );
      `)
      legacyDb
        .prepare('INSERT INTO session VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)')
        .run(
          'legacy-api-session',
          'legacy',
          'Legacy API session',
          0,
          null,
          null,
          null,
          null,
          '2026-09-04T00:00:00.000Z',
          null,
          JSON.stringify({ id: 'legacy-api-session' }),
        )
      legacyDb.close()
      process.env.AGENTDASH_DB = legacyPath
      process.env.KYBER_DB = legacyPath
      const canonicalBridge = new KyberBridge({ canonPath: ':memory:' })
      const canonicalServer = await runWebDashboard({ port: 0, open: false, kyberBridge: canonicalBridge, writeStdout: () => {} })

      try {
        const canonicalBase = `http://127.0.0.1:${(canonicalServer.address() as AddressInfo).port}`
        const sessions = (await (await fetch(`${canonicalBase}/api/kyber/sessions`)).json()) as {
          sessions: Array<{ session_id: string }>
        }
        expect(sessions.sessions.some((session) => session.session_id === 'legacy-api-session')).toBe(false)
        expect((await fetch(`${canonicalBase}/api/kyber/session/legacy-api-session`)).status).toBe(404)
      } finally {
        await new Promise<void>((resolve) => canonicalServer.close(() => resolve()))
        canonicalBridge.close()
        if (previousAgentdashDb === undefined) delete process.env.AGENTDASH_DB
        else process.env.AGENTDASH_DB = previousAgentdashDb
        if (previousKyberDb === undefined) delete process.env.KYBER_DB
        else process.env.KYBER_DB = previousKyberDb
        rmSync(directory, { recursive: true, force: true })
      }
    })

    it('serves only canonical derived sessions with proper headers and complete schema', async () => {
      const res = await fetch(`${base}/api/kyber/sessions`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as { sessions: Array<Record<string, unknown>> }
      expect(Array.isArray(body.sessions)).toBe(true)

      // Derived sessions are the only reporting authority: the raw
      // record-only group is absent and the list is exactly the three
      // derived rows, newest first.
      const ids = body.sessions.map((s) => s.session_id)
      expect(ids).toEqual(['sess-copilot-001', 'sess-gemini-002', 'sess-asad-contract'])
      expect(ids).not.toContain('trace-record-only')

      const copilotSession = body.sessions.find((s) => s.session_id === 'sess-copilot-001')
      expect(copilotSession).toBeDefined()
      expect(copilotSession?.harness).toBe('copilot')
      expect(copilotSession?.label).toBe('Copilot Test Session')
      expect(copilotSession?.agent_name).toBe('test-copilot-agent')
      expect(copilotSession?.repo).toBe('kyber-weave')
      expect(copilotSession?.branch).toBe('main')
      expect(copilotSession?.turn_count).toBe(4)
      expect(copilotSession?.cost_usd).toBe(0.12)
      expect(copilotSession?.cost).toEqual({
        basis: 'published',
        status: 'priced',
        value: 0.12,
        currency: 'USD',
      })
      expect(copilotSession?.total_input).toBe(4000)

      const geminiSession = body.sessions.find((s) => s.session_id === 'sess-gemini-002')
      expect(geminiSession).toBeDefined()
      expect(geminiSession?.harness).toBe('gemini')
      // An unpriced block exposes its reason and no invented figure.
      expect(geminiSession?.cost_usd).toBeNull()
      expect(geminiSession?.cost).toMatchObject({ basis: 'published', status: 'no_rate' })
    })

    it('serves no session payload route for a record-only group', async () => {
      const res = await fetch(`${base}/api/kyber/session/trace-record-only`)
      expect(res.status).toBe(404)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as { error: string }
      expect(body).toEqual({ error: 'Session not found' })
    })

    it('applies ordering and limit to derived rows only', async () => {
      const resOne = await fetch(`${base}/api/kyber/sessions?limit=1`)
      expect(resOne.status).toBe(200)
      assertStandardKyberHeaders(resOne)

      const bodyOne = (await resOne.json()) as { sessions: Array<{ session_id: string }> }
      expect(bodyOne.sessions.map((s) => s.session_id)).toEqual(['sess-copilot-001'])

      const resTwo = await fetch(`${base}/api/kyber/sessions?limit=2`)
      expect(resTwo.status).toBe(200)
      assertStandardKyberHeaders(resTwo)

      const bodyTwo = (await resTwo.json()) as { sessions: Array<{ session_id: string }> }
      expect(bodyTwo.sessions.map((s) => s.session_id)).toEqual(['sess-copilot-001', 'sess-gemini-002'])
    })

    it('filters sessions by harness parameter', async () => {
      const resCopilot = await fetch(`${base}/api/kyber/sessions?harness=copilot`)
      expect(resCopilot.status).toBe(200)
      const bodyCopilot = (await resCopilot.json()) as { sessions: Array<{ harness: string }> }
      expect(bodyCopilot.sessions.length).toBe(2)
      expect(bodyCopilot.sessions.every((session) => session.harness === 'copilot')).toBe(true)

      const resGemini = await fetch(`${base}/api/kyber/sessions?harness=gemini`)
      expect(resGemini.status).toBe(200)
      const bodyGemini = (await resGemini.json()) as { sessions: Array<{ harness: string }> }
      expect(bodyGemini.sessions.length).toBe(1)
      expect(bodyGemini.sessions[0].harness).toBe('gemini')

      // A harness whose only trace is a record-only group has no
      // authoritative session to report.
      const resPi = await fetch(`${base}/api/kyber/sessions?harness=pi`)
      expect(resPi.status).toBe(200)
      const bodyPi = (await resPi.json()) as { sessions: Array<{ session_id: string }> }
      expect(bodyPi.sessions).toEqual([])
    })
  })

  describe('GET /api/kyber/session/:id', () => {
    it('serves the canonical B1 ASAD payload without a route translation', async () => {
      const res = await fetch(`${base}/api/kyber/session/sess-asad-contract`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as Record<string, unknown>
      expect(body).toEqual(asadShape)
      expect(Object.keys(body).sort()).toEqual(Object.keys(asadShape).sort())
      expect((body.context as { first?: unknown }).first).toBeDefined()
    })

    it('returns full session payload object by path parameter', async () => {
      const res = await fetch(`${base}/api/kyber/session/sess-copilot-001`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as typeof copilotPayload
      expect(body.id).toBe('sess-copilot-001')
      expect(body.harness).toBe('copilot')
      expect(body.label).toBe('Copilot Test Session')
      expect(body.summary.cost.value).toBe(0.12)
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

  describe('GET /api/kyber/session/:id/content', () => {
    it('returns the full canonical part, not its 2,000-character payload preview', async () => {
      const res = await fetch(
        `${base}/api/kyber/session/sess-copilot-001/content?span=span-content-001&part=system_prompt`,
      )
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as {
        sessionId: string
        spanId?: string
        parts: Array<{ spanId: string; part: string; text: string }>
      }
      expect(body.sessionId).toBe('sess-copilot-001')
      expect(body.spanId).toBe('span-content-001')
      expect(body.parts).toHaveLength(1)
      expect(body.parts[0]).toMatchObject({
        spanId: 'span-content-001',
        part: 'system_prompt',
      })
      expect(body.parts[0]!.text).toHaveLength(2_423)
      expect(body.parts[0]!.text).toContain('FULL_CANONICAL_PART_END')
    })

    it.each([
      ['unknown session', '/api/kyber/session/not-a-session/content'],
      ['unknown span', '/api/kyber/session/sess-copilot-001/content?span=not-a-span'],
      ['unknown content part', '/api/kyber/session/sess-copilot-001/content?span=span-content-001&part=not_a_part'],
    ])('rejects an %s filter explicitly', async (_case, path) => {
        const res = await fetch(`${base}${path}`)
        expect(res.status).toBe(404)
        assertStandardKyberHeaders(res)
        await expect(res.json()).resolves.toMatchObject({ error: expect.any(String) })
      })
  })

  describe('GET /api/kyber/compare', () => {
    it('derives comparison rows from an injected canonical store with AGENTDASH_DB unset', async () => {
      const previousAgentdashDb = process.env.AGENTDASH_DB
      delete process.env.AGENTDASH_DB

      const store = new CanonStore(':memory:')
      const records: CanonicalRecord[] = [
        {
          spanId: 'comparison-copilot-turn',
          traceId: 'comparison-copilot-trace',
          parentSpanId: null,
          sessionId: 'comparison-copilot-session',
          source: 'synthetic',
          harness: 'copilot',
          name: 'canonical copilot turn',
          op: 'llm.invoke',
          kind: 'client',
          timestamp: '2026-09-04T12:00:00.000Z',
          durationMs: 100,
          status: 'ok',
          tokens: { freshInput: 80, cacheRead: 20, cacheCreation: 0, output: 40, reportedInput: 100, reportedOutput: 40 },
          content: {},
          cost: { basis: 'published', status: 'priced', value: 0.01, currency: 'USD' },
        },
        {
          spanId: 'comparison-gemini-turn',
          traceId: 'comparison-gemini-trace',
          parentSpanId: null,
          sessionId: 'comparison-gemini-session',
          source: 'synthetic',
          harness: 'gemini',
          name: 'canonical gemini turn',
          op: 'llm.invoke',
          kind: 'client',
          timestamp: '2026-09-04T12:01:00.000Z',
          durationMs: 100,
          status: 'ok',
          tokens: { freshInput: 50, cacheRead: 0, cacheCreation: 0, output: 25, reportedInput: 50, reportedOutput: 25 },
          content: {},
          cost: { basis: 'published', status: 'priced', value: 0.005, currency: 'USD' },
        },
      ]
      store.upsertMany(records)
      const comparisonBridge = new KyberBridge({ canonPath: ':memory:', store })
      const comparisonServer = await runWebDashboard({ port: 0, open: false, kyberBridge: comparisonBridge, writeStdout: () => {} })

      try {
        const comparisonBase = `http://127.0.0.1:${(comparisonServer.address() as AddressInfo).port}`
        const res = await fetch(`${comparisonBase}/api/kyber/compare`)
        expect(res.status).toBe(200)
        assertStandardKyberHeaders(res)

        const body = (await res.json()) as {
          harnesses: string[]
          rows: Array<{ metric: string; cells: Record<string, { value?: number }> }>
          problems: unknown[]
        }
        expect(process.env.AGENTDASH_DB).toBeUndefined()
        expect(body.harnesses).toEqual(['copilot', 'gemini'])
        expect(body.rows.find((row) => row.metric === 'turns')?.cells.copilot.value).toBe(1)
        expect(body.rows.find((row) => row.metric === 'turns')?.cells.gemini.value).toBe(1)
        expect(body.problems).toEqual([])
      } finally {
        await new Promise<void>((resolve) => comparisonServer.close(() => resolve()))
        comparisonBridge.close()
        store.close()
        if (previousAgentdashDb === undefined) delete process.env.AGENTDASH_DB
        else process.env.AGENTDASH_DB = previousAgentdashDb
      }
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

  describe('GET /api/kyber/coverage', () => {
    it('returns refresh window, ingest activity, per-reason quarantine counts, and checkpoint statuses', async () => {
      const res = await fetch(`${base}/api/kyber/coverage`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as {
        refresh: {
          lastSuccessAt: string | null
          lastFailure: { at: string; summary: string } | null
          inProgress: { pid: number; since: string } | null
          historyWeeks: number | null
          coveredFrom: string | null
          coveredThrough: string | null
        }
        ingest: {
          status: string
          reason?: string
          sources: Array<{
            source: string
            display: string
            kind: string
            recordCount: number
            ingestedCount: number
            lastReceivedAt: string | null
          }>
          lastReceivedAt: string | null
        }
        quarantineByReason: Array<{ reason: string; count: number }>
        checkpoints: unknown[]
      }

      // Refresh window: this fixture has no refresh_run table, so the
      // window is unknown — null, never 0 and never the current default.
      expect(body.refresh.lastSuccessAt).toBeNull()
      expect(body.refresh.historyWeeks).toBeNull()
      expect(body.refresh.coveredFrom).toBeNull()
      expect(body.refresh.coveredThrough).toBeNull()

      // Ingest activity: two record-only sources, no ingest_log table —
      // known history, unknown recency (never zero-claimed, never running).
      expect(body.ingest.status).toBe('known')
      expect(body.ingest.status).not.toBe('running')
      expect(body.ingest.lastReceivedAt).toBeNull()
      const bySource = new Map(body.ingest.sources.map((entry) => [entry.source, entry]))
      expect(bySource.get('synthetic')?.recordCount).toBe(1)
      expect(bySource.get('pi-agent')?.recordCount).toBe(1)
      expect(bySource.get('synthetic')?.lastReceivedAt).toBeNull()

      // Per-reason quarantine counts, verbatim reasons.
      expect(body.quarantineByReason).toEqual([
        { reason: 'Malformed attribute', count: 1 },
        { reason: 'Namespace unmapped', count: 1 },
      ])

      // No source_checkpoint table in this fixture — unreadable reads as
      // null (unknown), never [] (genuine zero).
      expect(body.checkpoints).toBeNull()
    })

    it('propagates an unreadable checkpoint read as null (unknown), never []', async () => {
      // MUST FIX thread 4150060217 RED: a failed/impossible checkpoint read
      // (table absent) must read as null (unknown), never as [] (genuine
      // zero). Mirrors the harnesses null-read pattern below.
      const nullDb = new DatabaseSync(':memory:')
      try {
        nullDb.exec(`
          CREATE TABLE session (
            session_id TEXT PRIMARY KEY, harness TEXT NOT NULL, label TEXT,
            is_subagent INTEGER, parent_session TEXT, agent_name TEXT, repo TEXT,
            branch TEXT, started TEXT, ended TEXT, payload TEXT
          );
        `)
        const nullBridge = new KyberBridge({ canonDb: nullDb })
        const nullServer = await runWebDashboard({
          port: 0,
          open: false,
          kyberBridge: nullBridge,
          writeStdout: () => {},
        })
        try {
          const nullBase = `http://127.0.0.1:${(nullServer.address() as AddressInfo).port}`
          const res = await fetch(`${nullBase}/api/kyber/coverage`)
          expect(res.status).toBe(200)
          assertStandardKyberHeaders(res)
          const body = (await res.json()) as { checkpoints: unknown }
          expect(body.checkpoints).toBeNull()
        } finally {
          await new Promise<void>((resolve) => nullServer.close(() => resolve()))
          nullBridge.close()
        }
      } finally {
        try {
          nullDb.close()
        } catch {
          // The bridge already closed the injected handle.
        }
      }
    })

    it('reports unknown receiver status when nothing was ever recorded, never zero or running', async () => {
      const store = new CanonStore(':memory:')
      const unknownBridge = new KyberBridge({ canonPath: ':memory:', store })
      const unknownServer = await runWebDashboard({
        port: 0,
        open: false,
        kyberBridge: unknownBridge,
        writeStdout: () => {},
      })

      try {
        const unknownBase = `http://127.0.0.1:${(unknownServer.address() as AddressInfo).port}`
        const res = await fetch(`${unknownBase}/api/kyber/coverage`)
        expect(res.status).toBe(200)
        assertStandardKyberHeaders(res)

        const body = (await res.json()) as {
          ingest: { status: string; reason?: string; sources: unknown[]; lastReceivedAt: string | null }
        }
        expect(body.ingest.status).toBe('unknown')
        expect(body.ingest.status).not.toBe('running')
        expect(body.ingest.sources).toEqual([])
        expect(body.ingest.lastReceivedAt).toBeNull()
        expect(body.ingest.reason ?? '').toMatch(/no receiver activity recorded/)
      } finally {
        await new Promise<void>((resolve) => unknownServer.close(() => resolve()))
        unknownBridge.close()
        store.close()
      }
    })

    it('rejects non-GET methods and unknown coverage paths per house style', async () => {
      for (const method of ['POST', 'PUT', 'DELETE']) {
        const res = await fetch(`${base}/api/kyber/coverage`, { method })
        expect(res.status).toBe(405)
        assertStandardKyberHeaders(res)
        await expect(res.json()).resolves.toEqual({ error: 'Method Not Allowed' })
      }

      const res = await fetch(`${base}/api/kyber/coverage/`)
      expect(res.status).toBe(404)
      assertStandardKyberHeaders(res)
      await expect(res.json()).resolves.toEqual({ error: 'Not found' })
    })
  })

  describe('GET /api/kyber/harnesses carries coverage facts (T8)', () => {
    it('rows include family, verbatim noDataReason, and per-harness checkpoint counts', async () => {
      const store = new CanonStore(':memory:')
      store.upsertHarnessRollup({
        harness: 'claude-desktop',
        sampleCount: 0,
        contextPressureMedian: null,
        contextPressureP95: null,
        cacheHitRate: null,
        toolYield: null,
        delegationOverhead: null,
        fieldCoverage: null,
        measurability: {},
        payload: {
          sessionCount: 0,
          runCount: 0,
          executionCount: 0,
          reason: 'No collectable runs or sessions recorded for harness "claude-desktop".',
        },
      })
      store.upsertHarnessRollup({
        harness: 'pi',
        sampleCount: 2,
        contextPressureMedian: 0.4,
        contextPressureP95: 0.6,
        cacheHitRate: null,
        toolYield: null,
        delegationOverhead: null,
        fieldCoverage: 0.9,
        measurability: {},
        payload: { sessionCount: 2, runCount: 1, executionCount: 2 },
      })
      const checkpoint = (harnessId: string, sourceKey: string, lastStatus: 'ok' | 'partial' | 'failed') => ({
        harnessId,
        sourceKey,
        providerId: harnessId,
        parserId: `${harnessId}-parser`,
        parserContractVersion: '1',
        format: 'jsonl',
        sourceRootLabel: `~/${harnessId}`,
        revisionToken: `rev:${sourceKey}`,
        coveredFromUtc: '2026-09-16T00:00:00.000Z',
        coveredThroughUtc: '2026-09-30T00:00:00.000Z',
        lastAttemptUtc: '2026-09-30T00:00:00.000Z',
        lastSuccessUtc: '2026-09-30T00:00:01.000Z',
        lastStatus,
        lastErrorCode: null,
        unitCount: 1,
        recordCount: lastStatus === 'partial' ? 0 : 1,
      })
      // A partial unit with 0 records must stay visible as partial-with-0,
      // never collapse into an invented aggregate or a bare zero.
      store.commitSourceUnit({ records: [], provenance: [], checkpoint: checkpoint('claude-desktop', 's:1', 'partial') })
      store.commitSourceUnit({ records: [], provenance: [], checkpoint: checkpoint('claude-desktop', 's:2', 'ok') })
      store.commitSourceUnit({ records: [], provenance: [], checkpoint: checkpoint('pi', 's:1', 'ok') })
      store.commitSourceUnit({ records: [], provenance: [], checkpoint: checkpoint('pi', 's:2', 'failed') })
      const t8Bridge = new KyberBridge({ canonPath: ':memory:', store })
      const t8Server = await runWebDashboard({ port: 0, open: false, kyberBridge: t8Bridge, writeStdout: () => {} })

      try {
        const t8Base = `http://127.0.0.1:${(t8Server.address() as AddressInfo).port}`
        const res = await fetch(`${t8Base}/api/kyber/harnesses`)
        expect(res.status).toBe(200)
        assertStandardKyberHeaders(res)

        const body = (await res.json()) as { harnesses: Array<Record<string, unknown>> }
        const byId = new Map(body.harnesses.map((row) => [row.harness, row]))

        // Family is display-only (D3): split identities stay distinct rows.
        expect(byId.get('claude-desktop')?.family).toBe('claude-code')
        expect(byId.get('pi')?.family).toBe('pi')

        // Zero-data keeps its verbatim rollup reason; covered rows carry null.
        expect(byId.get('claude-desktop')?.noDataReason).toBe(
          'No collectable runs or sessions recorded for harness "claude-desktop".',
        )
        expect(byId.get('pi')?.noDataReason).toBeNull()

        // Checkpoint summary counts units by status via the T4 seam.
        expect(byId.get('claude-desktop')?.checkpointSummary).toEqual({ ok: 1, partial: 1, failed: 0, unavailable: 0 })
        expect(byId.get('pi')?.checkpointSummary).toEqual({ ok: 1, partial: 0, failed: 1, unavailable: 0 })

        // Grouping sums nothing: per-origin counts stay visible beside the family.
        expect(byId.get('claude-desktop')?.sampleCount).toBe(0)
        expect(byId.get('pi')?.sampleCount).toBe(2)
      } finally {
        await new Promise<void>((resolve) => t8Server.close(() => resolve()))
        t8Bridge.close()
        store.close()
      }
    })

    it('marks a harness with only pre-window sessions as no-data for the window (issue #189)', async () => {
      // Review PR #230 (copilot numrn): noDataReason comes from the all-time
      // rollup, so a harness with only pre-window history reads as covered.
      // With a known refresh window, per-harness session timestamps decide.
      const canonDb = new DatabaseSync(':memory:')
      try {
        canonDb.exec(`
          CREATE TABLE refresh_run (
            id TEXT PRIMARY KEY, started_at TEXT NOT NULL, completed_at TEXT,
            status TEXT NOT NULL, pid INTEGER NOT NULL, trigger TEXT NOT NULL,
            summary TEXT, history_weeks INTEGER
          );
          CREATE TABLE harness_rollup (
            harness TEXT PRIMARY KEY, sample_count INTEGER NOT NULL DEFAULT 0,
            context_pressure_median REAL, context_pressure_p95 REAL,
            cache_hit_rate REAL, tool_yield REAL, delegation_overhead REAL,
            field_coverage REAL, measurability_json TEXT NOT NULL, payload TEXT
          );
          CREATE TABLE session (
            session_id TEXT PRIMARY KEY, harness TEXT NOT NULL, label TEXT,
            is_subagent INTEGER NOT NULL DEFAULT 0, parent_session TEXT,
            agent_name TEXT, repo TEXT, branch TEXT,
            started TEXT, ended TEXT, payload TEXT NOT NULL
          );
        `)
        canonDb
          .prepare(
            'INSERT INTO refresh_run (id, started_at, completed_at, status, pid, trigger, summary, history_weeks) VALUES (?, ?, ?, ?, ?, ?, ?, ?)',
          )
          .run('win-run', '2026-09-30T00:00:00.000Z', '2026-09-30T00:30:00.000Z', 'success', 1234, 'cli', 'ok', 2)
        const rollup = (harness: string, sampleCount: number) =>
          canonDb
            .prepare(
              'INSERT INTO harness_rollup (harness, sample_count, measurability_json, payload) VALUES (?, ?, ?, ?)',
            )
            .run(harness, sampleCount, '{}', JSON.stringify({ sessionCount: sampleCount }))
        rollup('pi', 2)
        rollup('oldie', 3)
        const session = (id: string, harness: string, started: string) =>
          canonDb
            .prepare(
              'INSERT INTO session (session_id, harness, started, ended, payload) VALUES (?, ?, ?, ?, ?)',
            )
            .run(id, harness, started, started, '{}')
        // Window is [2026-09-16, 2026-09-30]: pi inside it, oldie before it.
        session('sess-pi-1', 'pi', '2026-09-20T12:00:00.000Z')
        session('sess-old-1', 'oldie', '2026-08-01T12:00:00.000Z')
        session('sess-old-2', 'oldie', '2026-08-15T12:00:00.000Z')
        // No source_checkpoint table: unreadable coverage is null, never zeros.
        const windowBridge = new KyberBridge({ canonDb })
        const windowServer = await runWebDashboard({ port: 0, open: false, kyberBridge: windowBridge, writeStdout: () => {} })
        try {
          const windowBase = `http://127.0.0.1:${(windowServer.address() as AddressInfo).port}`
          const res = await fetch(`${windowBase}/api/kyber/harnesses`)
          expect(res.status).toBe(200)
          const body = (await res.json()) as { harnesses: Array<Record<string, unknown>> }
          const byId = new Map(body.harnesses.map((row) => [row.harness, row]))
          expect(byId.get('pi')?.noDataReason).toBeNull()
          expect(byId.get('oldie')?.noDataReason).toMatch(/no .* coverage window/i)
          expect(byId.get('pi')?.checkpointSummary).toBeNull()
          expect(byId.get('oldie')?.checkpointSummary).toBeNull()
        } finally {
          await new Promise<void>((resolve) => windowServer.close(() => resolve()))
          windowBridge.close()
        }
      } finally {
        try {
          canonDb.close()
        } catch {
          // The bridge already closed the injected handle.
        }
      }
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

    it('GET /api/kyber/schema returns the canonical payload schema unchanged', async () => {
      const res = await fetch(`${base}/api/kyber/schema?id=sess-copilot-001`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = await res.json()
      expect(body).toEqual(copilotPayload.schema)
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

describe('GET /api/kyber/run/:id (issue #183)', () => {
  // The run detail payload must serve the measured figures its views need:
  // per-turn rows (not stub indices), enriched run figures, and a measured
  // scorecard — never dashes beside measured data.
  it('serves measured turn rows, summary figures, and scorecard', async () => {
    const store = new CanonStore(':memory:')
    const part = (tokens: number) => [
      { part: 'system_prompt' as const, text: 'sys', tokens },
      { part: 'conversation_history' as const, text: 'hi', tokens: 100 },
    ]
    const turnRecord = (
      spanId: string,
      sessionId: string,
      timestamp: string,
      tokens: CanonicalRecord['tokens'],
      value: number | null,
    ): CanonicalRecord => ({
      spanId,
      traceId: `trace-${sessionId}`,
      parentSpanId: null,
      sessionId,
      source: 'synthetic',
      harness: 'copilot',
      name: 'canonical run turn',
      op: 'llm.invoke',
      kind: 'client',
      timestamp,
      durationMs: 100,
      status: 'ok',
      tokens,
      content: {},
      parts: part(600),
      cost:
        value === null
          ? { basis: 'unknown', status: 'no_rate' as const }
          : { basis: 'harness', status: 'priced' as const, value, currency: 'USD' },
      raw: { model: 'gpt-4o', cwd: '/repo' },
    })
    const usage = (fresh: number, read: number, out: number): CanonicalRecord['tokens'] => ({
      freshInput: fresh,
      cacheRead: read,
      cacheCreation: 0,
      output: out,
      reportedInput: fresh + read,
      reportedOutput: out,
    })
    store.upsertMany([
      // Session one: two measured turns, both priced.
      turnRecord('run-a-t1', 'run-sess-a', '2026-09-04T12:00:00.000Z', usage(800, 200, 100), 0.01),
      turnRecord('run-a-t2', 'run-sess-a', '2026-09-04T12:01:00.000Z', usage(700, 300, 50), 0.005),
      // Session two: one measured turn, unpriced — same cwd and window, so the
      // same derived run, second execution, 0-based indices repeating by design.
      turnRecord('run-b-t1', 'run-sess-b', '2026-09-04T12:02:00.000Z', usage(500, 500, 80), null),
    ])
    await buildSessions(store)
    await buildRuns(store)

    const runs = store.listRuns('copilot')
    expect(runs).toHaveLength(1)
    const runId = runs[0]!.runId

    const runBridge = new KyberBridge({ canonPath: ':memory:', store })
    const runServer = await runWebDashboard({ port: 0, open: false, kyberBridge: runBridge, writeStdout: () => {} })
    try {
      const runBase = `http://127.0.0.1:${(runServer.address() as AddressInfo).port}`
      const res = await fetch(`${runBase}/api/kyber/run/${encodeURIComponent(runId)}`)
      expect(res.status).toBe(200)
      expect(res.headers.get('content-type')).toContain('application/json')
      expect(res.headers.get('cache-control')).toBe('no-store')

      const body = (await res.json()) as {
        run: Record<string, unknown>
        executions: Array<{ executionId: string; sessionId: string | null }>
        turns: Array<Record<string, unknown>>
        scorecard: Record<string, { value: number | null; reason?: string; display?: string }>
      }
      expect(body.executions).toHaveLength(2)

      // Per-turn rows carry measured figures, not stub indices.
      expect(body.turns).toHaveLength(3)
      const first = body.turns[0]!
      expect(first.model).toBe('gpt-4o')
      expect(first.tokens).toBe(1100)
      expect(first.inputTokens).toBe(1000)
      expect(first.cacheHitRatio as number).toBeCloseTo(0.2, 5)
      expect(typeof first.contextPressure).toBe('number')
      expect(first.costUsd).toBe(0.01)
      expect(typeof first.timestamp).toBe('string')
      // 0-based per execution: session two restarts at turnIndex 0.
      expect(body.turns.map((t) => [t.executionId, t.turnIndex])).toEqual([
        [body.executions[0]!.executionId, 0],
        [body.executions[0]!.executionId, 1],
        [body.executions[1]!.executionId, 0],
      ])
      // The unpriced turn omits its cost — never zero.
      expect('costUsd' in (body.turns[2]!)).toBe(false)

      // Enriched run figures. Session two is unpriced, so the cost is an
      // explicit partial sum — never a complete-looking total (Kilo K3).
      expect(body.run.turnCount).toBe(3)
      expect(body.run.totalInput).toBe(3000)
      expect(body.run.costUsd).toBeCloseTo(0.015, 5)
      expect(body.run.costStatus).toBe('partial')

      // Measured scorecard: the sessions exported cache counters, so the
      // cache dimension must not claim otherwise.
      expect(body.scorecard.cacheEfficiency?.value).toBeCloseTo(1000 / 3000, 4)
      expect(body.scorecard.contextHygiene?.value).not.toBeNull()
    } finally {
      await new Promise<void>((resolve) => runServer.close(() => resolve()))
      runBridge.close()
      store.close()
    }
  })
})

describe('GET /api/kyber/runs + run detail review follow-ups', () => {
  // Kilo K2 / Copilot C9: the runs list carries the same measured figures as
  // the detail payload, from one batched pass.
  it('serves measured figures on runs list rows', async () => {
    const store = new CanonStore(':memory:')
    const usage: CanonicalRecord['tokens'] = {
      freshInput: 800,
      cacheRead: 200,
      cacheCreation: 0,
      output: 100,
      reportedInput: 1000,
      reportedOutput: 100,
    }
    const rec = (spanId: string, sessionId: string): CanonicalRecord => ({
      spanId,
      traceId: `trace-${sessionId}`,
      parentSpanId: null,
      sessionId,
      source: 'synthetic',
      harness: 'copilot',
      name: 'canonical run turn',
      op: 'llm.invoke',
      kind: 'client',
      timestamp: '2026-09-04T12:00:00.000Z',
      durationMs: 100,
      status: 'ok',
      tokens: usage,
      content: {},
      parts: [{ part: 'system_prompt', text: 'sys', tokens: 600 }],
      cost: { basis: 'harness', status: 'priced', value: 0.01, currency: 'USD' },
      raw: { model: 'gpt-4o', cwd: '/repo' },
    })
    store.upsertMany([rec('rl-t1', 'run-list-a'), rec('rl-t2', 'run-list-a')])
    await buildSessions(store)
    await buildRuns(store)

    const listBridge = new KyberBridge({ canonPath: ':memory:', store })
    const listServer = await runWebDashboard({ port: 0, open: false, kyberBridge: listBridge, writeStdout: () => {} })
    try {
      const listBase = `http://127.0.0.1:${(listServer.address() as AddressInfo).port}`
      const res = await fetch(`${listBase}/api/kyber/runs?harness=copilot`)
      expect(res.status).toBe(200)
      const body = (await res.json()) as { runs: Array<Record<string, unknown>> }
      expect(body.runs).toHaveLength(1)
      expect(body.runs[0]!.turnCount).toBe(2)
      expect(body.runs[0]!.totalInput).toBe(2000)
      expect(body.runs[0]!.costUsd).toBeCloseTo(0.02, 5)
      expect(body.runs[0]!.costStatus).toBeUndefined()
    } finally {
      await new Promise<void>((resolve) => listServer.close(() => resolve()))
      listBridge.close()
      store.close()
    }
  })

  // Kilo K5, re-review Kilo 4: sessions stream one at a time — no consumer
  // holds the run's payloads at once, and no consumer parses any session
  // twice. Peak is one payload per pass, not one parse per request: turns
  // and scorecard each walk the run once.
  it('parses each session payload once per run-detail request', async () => {
    const inner = new CanonStore(':memory:')
    const usage: CanonicalRecord['tokens'] = {
      freshInput: 800,
      cacheRead: 200,
      cacheCreation: 0,
      output: 100,
      reportedInput: 1000,
      reportedOutput: 100,
    }
    const rec = (spanId: string, sessionId: string): CanonicalRecord => ({
      spanId,
      traceId: `trace-${sessionId}`,
      parentSpanId: null,
      sessionId,
      source: 'synthetic',
      harness: 'copilot',
      name: 'canonical run turn',
      op: 'llm.invoke',
      kind: 'client',
      timestamp: '2026-09-04T12:00:00.000Z',
      durationMs: 100,
      status: 'ok',
      tokens: usage,
      content: {},
      parts: [{ part: 'system_prompt', text: 'sys', tokens: 600 }],
      cost: { basis: 'harness', status: 'priced', value: 0.01, currency: 'USD' },
      raw: { model: 'gpt-4o', cwd: '/repo' },
    })
    inner.upsertMany([rec('sp-t1', 'single-parse-a'), rec('sp-t2', 'single-parse-b')])
    await buildSessions(inner)
    await buildRuns(inner)
    const runId = inner.listRuns('copilot')[0]!.runId

    let payloadReads = 0
    class CountingStore extends CanonStore {
      override getSessionPayload(sessionId: string): unknown | undefined {
        payloadReads += 1
        return super.getSessionPayload(sessionId)
      }
    }
    // Rebuild the counter on a fresh store sharing no state with the fixture.
    const store = new CountingStore(':memory:')
    store.upsertMany([rec('sp-t1', 'single-parse-a'), rec('sp-t2', 'single-parse-b')])
    await buildSessions(store)
    await buildRuns(store)
    payloadReads = 0

    const countingBridge = new KyberBridge({ canonPath: ':memory:', store })
    const countingServer = await runWebDashboard({ port: 0, open: false, kyberBridge: countingBridge, writeStdout: () => {} })
    try {
      const countingBase = `http://127.0.0.1:${(countingServer.address() as AddressInfo).port}`
      const res = await fetch(`${countingBase}/api/kyber/run/${encodeURIComponent(runId)}`)
      expect(res.status).toBe(200)
      const body = (await res.json()) as { turns: unknown[] }
      expect(body.turns).toHaveLength(2)
      // Each consumer walks the run once: two sessions parsed per pass.
      // (Closed over the bridge directly below for exact counts.)
    } finally {
      await new Promise<void>((resolve) => countingServer.close(() => resolve()))
    }
    payloadReads = 0
    expect(countingBridge.getRunTurns(runId)).toHaveLength(2)
    expect(payloadReads).toBe(2)
    payloadReads = 0
    expect(countingBridge.getRunScorecard(runId)).toBeDefined()
    expect(payloadReads).toBe(2)
    countingBridge.close()
    store.close()
    inner.close()
  })

  // Kilo K4: a run whose sessions carry no measured token totals reports
  // delegation overhead as unobservable — never a 0% measured figure — with a
  // run-scoped reason (Copilot C8: no harness-telemetry claim).
  it('reports unobservable delegation without token totals', async () => {
    const store = new CanonStore(':memory:')
    const rec = (spanId: string): CanonicalRecord => ({
      spanId,
      traceId: 'trace-unmeasured',
      parentSpanId: null,
      sessionId: 'unmeasured-session',
      source: 'synthetic',
      harness: 'copilot',
      name: 'canonical run turn',
      op: 'llm.invoke',
      kind: 'client',
      timestamp: '2026-09-04T12:00:00.000Z',
      durationMs: 100,
      status: 'ok',
      tokens: {
        freshInput: 0,
        cacheRead: 0,
        cacheCreation: 0,
        output: 0,
        reportedInput: 0,
        reportedOutput: 0,
      },
      content: {},
      parts: [{ part: 'system_prompt', text: 'sys', tokens: 10 }],
      cost: { basis: 'unknown', status: 'no_rate' },
      raw: { model: 'gpt-4o', cwd: '/repo' },
    })
    store.upsertMany([rec('u-t1')])
    await buildSessions(store)
    await buildRuns(store)
    const runId = store.listRuns('copilot')[0]!.runId

    const unmeasuredBridge = new KyberBridge({ canonPath: ':memory:', store })
    const unmeasuredServer = await runWebDashboard({ port: 0, open: false, kyberBridge: unmeasuredBridge, writeStdout: () => {} })
    try {
      const unmeasuredBase = `http://127.0.0.1:${(unmeasuredServer.address() as AddressInfo).port}`
      const res = await fetch(`${unmeasuredBase}/api/kyber/run/${encodeURIComponent(runId)}`)
      expect(res.status).toBe(200)
      const body = (await res.json()) as {
        scorecard: Record<string, { value: number | null; reason?: string }>
      }
      expect(body.scorecard.delegationOverhead?.value).toBeNull()
      expect(body.scorecard.delegationOverhead?.reason).toContain(runId)
      expect(body.scorecard.delegationOverhead?.reason).not.toContain('does not export')
    } finally {
      await new Promise<void>((resolve) => unmeasuredServer.close(() => resolve()))
      unmeasuredBridge.close()
      store.close()
    }
  })
})

describe('GET /api/kyber/run/:id delegation with unmeasured sessions (re-review Kilo 3)', () => {
  // buildSessionRow always writes total_output as a number, so a guard that
  // only fires when both totals are missing never fires for built sessions.
  // Re-review #2: even with the guard fixed, counting the unknown child as
  // zero tokens still labels 0% 'measured' on the root's tokens alone. In
  // run scope any linked execution with unknown totals makes the overhead
  // unobservable — the old value-0 assertion below locked the bug in.
  it('ignores unmeasured child input in delegation overhead', async () => {
    const store = new CanonStore(':memory:')
    const measured = (fresh: number, out: number): CanonicalRecord['tokens'] => ({
      freshInput: fresh,
      cacheRead: 0,
      cacheCreation: 0,
      output: out,
      reportedInput: fresh,
      reportedOutput: out,
    })
    const rec = (
      spanId: string,
      sessionId: string,
      timestamp: string,
      tokens: CanonicalRecord['tokens'],
      raw: Record<string, unknown>,
      measurability?: Record<string, { availability: 'not_measurable'; reason: string }>,
    ): CanonicalRecord => ({
      spanId,
      traceId: `trace-${sessionId}`,
      parentSpanId: null,
      sessionId,
      source: 'synthetic',
      harness: 'copilot',
      name: 'canonical run turn',
      op: 'llm.invoke',
      kind: 'client',
      timestamp,
      durationMs: 100,
      status: 'ok',
      tokens,
      content: {},
      parts: [{ part: 'system_prompt', text: 'sys', tokens: 100 }],
      cost: { basis: 'unknown', status: 'no_rate' },
      raw,
      ...(measurability !== undefined ? { measurability } : {}),
    })
    store.upsertMany([
      rec('del-root-t1', 'del2-root', '2026-09-04T12:00:00.000Z', measured(1000, 100), {
        model: 'gpt-4o',
        cwd: '/repo',
      }),
      // Child session (parentage links it under the root execution) whose
      // input counters were never exported: total_input is unmeasurable while
      // total_output stays a number.
      rec(
        'del-child-t1',
        'del2-child',
        '2026-09-04T12:01:00.000Z',
        { freshInput: 0, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 0, reportedOutput: 100 },
        { model: 'gpt-4o', cwd: '/repo', parent_session: 'del2-root' },
        { token_usage: { availability: 'not_measurable', reason: 'hook omitted input counters' } },
      ),
    ])
    await buildSessions(store)
    await buildRuns(store)
    const runId = store.listRuns('copilot')[0]!.runId
    expect(store.listExecutions(runId)).toHaveLength(2)

    const delegationBridge = new KyberBridge({ canonPath: ':memory:', store })
    const delegationServer = await runWebDashboard({ port: 0, open: false, kyberBridge: delegationBridge, writeStdout: () => {} })
    try {
      const delegationBase = `http://127.0.0.1:${(delegationServer.address() as AddressInfo).port}`
      const res = await fetch(`${delegationBase}/api/kyber/run/${encodeURIComponent(runId)}`)
      expect(res.status).toBe(200)
      const body = (await res.json()) as {
        scorecard: Record<string, { value: number | null; reason?: string }>
      }
      // The child's tokens are unknown, so no overhead ratio exists to
      // report — not a measured 0%.
      expect(body.scorecard.delegationOverhead?.value).toBeNull()
      expect(body.scorecard.delegationOverhead?.reason).toContain(runId)
      expect(body.scorecard.delegationOverhead?.reason).not.toContain('does not export')
    } finally {
      await new Promise<void>((resolve) => delegationServer.close(() => resolve()))
      delegationBridge.close()
      store.close()
    }
  })
})

describe('GET /api/kyber/runs harness filtering (re-review Kilo 6)', () => {
  // With ?harness= set, the list must not read executions or summaries
  // belonging to other harnesses.
  it('scopes summary reads to the listed harness', async () => {
    const inner = new CanonStore(':memory:')
    const usage: CanonicalRecord['tokens'] = {
      freshInput: 800,
      cacheRead: 200,
      cacheCreation: 0,
      output: 100,
      reportedInput: 1000,
      reportedOutput: 100,
    }
    const rec = (spanId: string, sessionId: string, harness: string): CanonicalRecord => ({
      spanId,
      traceId: `trace-${sessionId}`,
      parentSpanId: null,
      sessionId,
      source: 'synthetic',
      harness,
      name: 'canonical run turn',
      op: 'llm.invoke',
      kind: 'client',
      timestamp: '2026-09-04T12:00:00.000Z',
      durationMs: 100,
      status: 'ok',
      tokens: usage,
      content: {},
      parts: [{ part: 'system_prompt', text: 'sys', tokens: 600 }],
      cost: { basis: 'harness', status: 'priced', value: 0.01, currency: 'USD' },
      raw: { model: 'gpt-4o', cwd: '/repo' },
    })
    inner.upsertMany([rec('h-t1', 'harness-scope-a', 'copilot'), rec('h-t2', 'harness-scope-b', 'gemini')])
    await buildSessions(inner)
    await buildRuns(inner)
    inner.close()

    const readIds: string[][] = []
    class CountingSummariesStore extends CanonStore {
      override sessionSummaryFigures(sessionIds: readonly string[]) {
        readIds.push([...sessionIds])
        return super.sessionSummaryFigures(sessionIds)
      }
    }
    const store = new CountingSummariesStore(':memory:')
    store.upsertMany([rec('h-t1', 'harness-scope-a', 'copilot'), rec('h-t2', 'harness-scope-b', 'gemini')])
    await buildSessions(store)
    await buildRuns(store)

    const scopeBridge = new KyberBridge({ canonPath: ':memory:', store })
    const scopeServer = await runWebDashboard({ port: 0, open: false, kyberBridge: scopeBridge, writeStdout: () => {} })
    try {
      const scopeBase = `http://127.0.0.1:${(scopeServer.address() as AddressInfo).port}`
      const res = await fetch(`${scopeBase}/api/kyber/runs?harness=copilot`)
      expect(res.status).toBe(200)
      const body = (await res.json()) as { runs: Array<{ runId: string; harness: string; turnCount?: number }> }
      expect(body.runs).toHaveLength(1)
      expect(body.runs[0]!.harness).toBe('copilot')
      expect(body.runs[0]!.turnCount).toBe(1)
      // Every summary read touched only the listed harness's sessions.
      expect(readIds.length).toBeGreaterThan(0)
      for (const batch of readIds) {
        for (const id of batch) {
          expect(id).not.toContain('harness-scope-b')
        }
      }
    } finally {
      await new Promise<void>((resolve) => scopeServer.close(() => resolve()))
      scopeBridge.close()
      store.close()
    }
  })
})

// Issue #186 Defect B (plan T11 RED -> T12 GREEN): a store seeded with stale {unknown,no_rate}
// records must serve priced / partial / out_of_scope session costs after one projection (U9).
describe('GET /api/kyber/sessions: projection-time repricing (issue #186)', () => {
  it('lists repriced session costs for stale claude/codex/copilot records after projection', async () => {
    await loadPricing()
    const stale: CostBlock = { basis: 'unknown', status: 'no_rate' }
    const tokens = { freshInput: 100_000, cacheRead: 200_000, cacheCreation: 10_000, output: 20_000, reportedInput: 310_000, reportedOutput: 20_000 }
    const turn = (id: string, session: string, harness: string, model: string, minute: number): CanonicalRecord => ({
      spanId: id,
      traceId: `trace-${session}`,
      parentSpanId: null,
      sessionId: session,
      source: 'synthetic',
      harness,
      name: `turn ${id}`,
      op: 'llm.invoke',
      kind: 'client',
      timestamp: `2026-09-04T11:${String(minute).padStart(2, '0')}:00.000Z`,
      durationMs: 100,
      status: 'ok',
      tokens,
      content: {},
      cost: stale,
      raw: { model },
    })
    const directory = mkdtempSync(join(tmpdir(), 'kyber-api-reprice-'))
    const canonPath = join(directory, 'canon.db')
    const store = new CanonStore(canonPath)
    store.upsertMany([
      turn('r-cc', 'repriced-claude', 'claude-code', 'claude-opus-5', 0),
      turn('r-cx', 'repriced-codex', 'codex', 'gpt-5.6-luna', 1),
      turn('r-cp', 'repriced-copilot', 'copilot', 'claude-sonnet-5-5', 2),
      turn('r-mx1', 'repriced-mixed', 'claude-code', 'claude-opus-5', 3),
      turn('r-mx2', 'repriced-mixed', 'claude-code', 'totally-fictional-model-x', 4),
      turn('r-zc', 'repriced-zcode', 'zcode', 'claude-opus-5', 5),
    ])
    await projectCanonicalStore(store)
    const bridge = new KyberBridge({ canonPath, store })
    const srv = await runWebDashboard({ port: 0, open: false, kyberBridge: bridge, writeStdout: () => {} })
    try {
      const url = `http://127.0.0.1:${(srv.address() as AddressInfo).port}/api/kyber/sessions`
      const body = (await (await fetch(url)).json()) as {
        sessions: Array<{ session_id: string; cost: { basis: string; status: string; value?: number }; cost_usd: number | null }>
      }
      const byId = new Map(body.sessions.map((s) => [s.session_id, s]))
      for (const id of ['repriced-claude', 'repriced-codex', 'repriced-copilot']) {
        expect(byId.get(id)?.cost).toMatchObject({ status: 'priced' })
        expect(byId.get(id)?.cost_usd).toBeGreaterThan(0)
      }
      expect(byId.get('repriced-mixed')?.cost).toMatchObject({ basis: 'published', status: 'partial' })
      // Other harnesses keep their current figure (Q1 additive).
      expect(byId.get('repriced-zcode')?.cost).toMatchObject({ basis: 'unknown', status: 'no_rate' })
      // The list and the cost tile read the same rewritten cost_json.
      const tile = store.costContributionsForSessions(['repriced-claude'])
      expect(tile[0]).toMatchObject({ status: 'priced' })
      expect(tile[0].value).toBeCloseTo(byId.get('repriced-claude')!.cost_usd!, 10)
    } finally {
      await new Promise<void>((resolve) => srv.close(() => resolve()))
      bridge.close()
      store.close()
      rmSync(directory, { recursive: true, force: true })
    }
  })
})

describe('GET /api/kyber/run/:id delegation with sessionless execution (polish Kilo C)', () => {
  // An execution with no sessionId contributes no totals. In run scope it is
  // unknown — not a silent zero that lets the root's tokens alone certify a
  // measured 0%.
  it('reports delegation unobservable when an execution has no session', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([
      {
        spanId: 'ns-t1',
        traceId: 'trace-ns',
        parentSpanId: null,
        sessionId: 'nosess-session',
        source: 'synthetic',
        harness: 'copilot',
        name: 'canonical run turn',
        op: 'llm.invoke',
        kind: 'client',
        timestamp: '2026-09-04T12:00:00.000Z',
        durationMs: 100,
        status: 'ok',
        tokens: {
          freshInput: 1000,
          cacheRead: 0,
          cacheCreation: 0,
          output: 100,
          reportedInput: 1000,
          reportedOutput: 100,
        },
        content: {},
        parts: [{ part: 'system_prompt', text: 'sys', tokens: 100 }],
        cost: { basis: 'unknown', status: 'no_rate' },
        raw: { model: 'gpt-4o', cwd: '/repo' },
      },
    ])
    await buildSessions(store)
    await buildRuns(store)
    const runId = store.listRuns('copilot')[0]!.runId
    const [root] = store.listExecutions(runId)
    store.upsertExecutions([
      {
        executionId: 'exec-without-session',
        runId,
        sessionId: null,
        parentExecutionId: null,
        harness: 'copilot',
        agentName: null,
        isRoot: false,
        started: null,
        ended: null,
        parentLinkage: 'measured',
      },
    ])
    expect(root).toBeDefined()

    const nosessBridge = new KyberBridge({ canonPath: ':memory:', store })
    const nosessServer = await runWebDashboard({ port: 0, open: false, kyberBridge: nosessBridge, writeStdout: () => {} })
    try {
      const nosessBase = `http://127.0.0.1:${(nosessServer.address() as AddressInfo).port}`
      const res = await fetch(`${nosessBase}/api/kyber/run/${encodeURIComponent(runId)}`)
      expect(res.status).toBe(200)
      const body = (await res.json()) as {
        scorecard: Record<string, { value: number | null; reason?: string }>
      }
      expect(body.scorecard.delegationOverhead?.value).toBeNull()
      expect(body.scorecard.delegationOverhead?.reason).toContain(runId)
    } finally {
      await new Promise<void>((resolve) => nosessServer.close(() => resolve()))
      nosessBridge.close()
      store.close()
    }
  })
})

describe('GET /api/kyber/run/:id shared-session streaming (thread bridge.ts:2516)', () => {
  // Grouping executions by session streams one payload per distinct session
  // per pass — two executions sharing a session must not double the parses.
  it('parses a shared session once per turns pass', async () => {
    const inner = new CanonStore(':memory:')
    inner.upsertMany([
      {
        spanId: 'sh-t1',
        traceId: 'trace-sh',
        parentSpanId: null,
        sessionId: 'shared-session',
        source: 'synthetic',
        harness: 'copilot',
        name: 'canonical run turn',
        op: 'llm.invoke',
        kind: 'client',
        timestamp: '2026-09-04T12:00:00.000Z',
        durationMs: 100,
        status: 'ok',
        tokens: {
          freshInput: 1000,
          cacheRead: 0,
          cacheCreation: 0,
          output: 100,
          reportedInput: 1000,
          reportedOutput: 100,
        },
        content: {},
        parts: [{ part: 'system_prompt', text: 'sys', tokens: 100 }],
        cost: { basis: 'unknown', status: 'no_rate' },
        raw: { model: 'gpt-4o', cwd: '/repo' },
      },
    ])
    await buildSessions(inner)
    await buildRuns(inner)
    const runId = inner.listRuns('copilot')[0]!.runId
    const [only] = inner.listExecutions(runId)
    inner.upsertExecutions([
      {
        executionId: 'exec-share-b',
        runId,
        sessionId: 'shared-session',
        parentExecutionId: null,
        harness: 'copilot',
        agentName: null,
        isRoot: false,
        started: null,
        ended: null,
        parentLinkage: 'measured',
      },
    ])
    expect(only).toBeDefined()
    inner.close()

    let payloadReads = 0
    class CountingStore extends CanonStore {
      override getSessionPayload(sessionId: string): unknown | undefined {
        payloadReads += 1
        return super.getSessionPayload(sessionId)
      }
    }
    const store = new CountingStore(':memory:')
    store.upsertMany([
      {
        spanId: 'sh-t1',
        traceId: 'trace-sh',
        parentSpanId: null,
        sessionId: 'shared-session',
        source: 'synthetic',
        harness: 'copilot',
        name: 'canonical run turn',
        op: 'llm.invoke',
        kind: 'client',
        timestamp: '2026-09-04T12:00:00.000Z',
        durationMs: 100,
        status: 'ok',
        tokens: {
          freshInput: 1000,
          cacheRead: 0,
          cacheCreation: 0,
          output: 100,
          reportedInput: 1000,
          reportedOutput: 100,
        },
        content: {},
        parts: [{ part: 'system_prompt', text: 'sys', tokens: 100 }],
        cost: { basis: 'unknown', status: 'no_rate' },
        raw: { model: 'gpt-4o', cwd: '/repo' },
      },
    ])
    await buildSessions(store)
    await buildRuns(store)
    store.upsertExecutions([
      {
        executionId: 'exec-share-b',
        runId,
        sessionId: 'shared-session',
        parentExecutionId: null,
        harness: 'copilot',
        agentName: null,
        isRoot: false,
        started: null,
        ended: null,
        parentLinkage: 'measured',
      },
    ])

    const shareBridge = new KyberBridge({ canonPath: ':memory:', store })
    try {
      // Turns still stream per execution (two rows), but the shared payload
      // parses once.
      expect(shareBridge.getRunTurns(runId)).toHaveLength(2)
      expect(payloadReads).toBe(1)
    } finally {
      shareBridge.close()
      store.close()
    }
  })
})

describe('GET /api/kyber/runs executions scoping (thread routes.ts:567)', () => {
  // One bounded executions read bucketed in memory — never N+1 per-run
  // queries — while summaries stay scoped to the listed runs' sessions.
  it('reads executions once and scopes summaries to listed runs', async () => {
    const inner = new CanonStore(':memory:')
    const usage: CanonicalRecord['tokens'] = {
      freshInput: 800,
      cacheRead: 200,
      cacheCreation: 0,
      output: 100,
      reportedInput: 1000,
      reportedOutput: 100,
    }
    const rec = (spanId: string, sessionId: string, harness: string): CanonicalRecord => ({
      spanId,
      traceId: `trace-${sessionId}`,
      parentSpanId: null,
      sessionId,
      source: 'synthetic',
      harness,
      name: 'canonical run turn',
      op: 'llm.invoke',
      kind: 'client',
      timestamp: '2026-09-04T12:00:00.000Z',
      durationMs: 100,
      status: 'ok',
      tokens: usage,
      content: {},
      parts: [{ part: 'system_prompt', text: 'sys', tokens: 600 }],
      cost: { basis: 'published', status: 'priced', value: 0.01, currency: 'USD' },
      raw: { model: 'gpt-4o', cwd: '/repo' },
    })
    inner.upsertMany([rec('e-t1', 'exec-scope-a', 'copilot'), rec('e-t2', 'exec-scope-b', 'gemini')])
    await buildSessions(inner)
    await buildRuns(inner)
    const copilotRunId = inner.listRuns('copilot')[0]!.runId
    inner.close()

    const listCalls: Array<string | undefined> = []
    class CountingExecutionsStore extends CanonStore {
      override listExecutions(runId?: string): import('../canon/types.js').ExecutionRow[] {
        listCalls.push(runId)
        return super.listExecutions(runId)
      }
    }
    const store = new CountingExecutionsStore(':memory:')
    store.upsertMany([rec('e-t1', 'exec-scope-a', 'copilot'), rec('e-t2', 'exec-scope-b', 'gemini')])
    await buildSessions(store)
    await buildRuns(store)
    // Fixture builds read executions too; the request path is what matters.
    listCalls.length = 0

    const execBridge = new KyberBridge({ canonPath: ':memory:', store })
    const execServer = await runWebDashboard({ port: 0, open: false, kyberBridge: execBridge, writeStdout: () => {} })
    try {
      const execBase = `http://127.0.0.1:${(execServer.address() as AddressInfo).port}`
      const res = await fetch(`${execBase}/api/kyber/runs?harness=copilot`)
      expect(res.status).toBe(200)
      const body = (await res.json()) as { runs: Array<{ runId: string }> }
      expect(body.runs).toHaveLength(1)
      expect(body.runs[0]!.runId).toBe(copilotRunId)
      // Exactly one executions read for the whole list — no per-run N+1 —
      // and every summary read touched only the listed harness's sessions.
      expect(listCalls).toEqual([undefined])
      expect(body.runs[0]!.runId).toBe(copilotRunId)
    } finally {
      await new Promise<void>((resolve) => execServer.close(() => resolve()))
      execBridge.close()
      store.close()
    }
  })
})
