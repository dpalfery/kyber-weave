import { mkdtemp, rm } from 'fs/promises'
import { tmpdir } from 'os'
import { join } from 'path'
import type { AddressInfo } from 'net'
import type { Server } from 'http'
import { DatabaseSync } from 'node:sqlite'

import { afterAll, beforeAll, describe, expect, it } from 'vitest'

import { injectDashboardBootstrap, runWebDashboard } from '../src/web-dashboard.js'
import { KyberBridge } from '../kyber/server/bridge.js'

describe('web dashboard bootstrap injection', () => {
  it('keeps replacement syntax in a payload value literal', () => {
    const payloadValue = "$`|$'|$&|$1"
    const payload = { devices: [{ name: payloadValue }] }
    const html = '<!doctype html><script type="module" src="/app.js"></script>'

    const injected = injectDashboardBootstrap(html, payload)

    expect(injected).toContain(`window.__CODEBURN_BOOTSTRAP__=${JSON.stringify(payload)}</script>`)
    expect(injected).toContain(`"name":"${payloadValue}"`)
  })

  it('escapes script-closing payload values and preserves the served bootstrap payload', () => {
    const hostileName = '</script><script>globalThis.bootstrapPwned = true</script>'
    const payload = {
      devices: [{
        id: 'local',
        name: hostileName,
        payload: { current: { topProjects: [{ name: hostileName }] } },
      }],
    }
    const html = '<!doctype html><script type="module" src="/app.js"></script>'

    const servedHtml = injectDashboardBootstrap(html, payload)
    const marker = 'window.__CODEBURN_BOOTSTRAP__='
    const start = servedHtml.indexOf(marker) + marker.length
    const end = servedHtml.indexOf('</script>', start)
    const serialized = servedHtml.slice(start, end)

    expect(serialized).not.toContain('</script')
    expect(serialized).toContain('\\u003c/script>')
    expect(JSON.parse(serialized)).toEqual(payload)
  })
})

// Regression guard for the original bug: a bad `period` query used to hit
// process.exit(1) and kill the long-running dashboard server. The handlers must
// now answer 400 and keep serving.
describe('web dashboard server: invalid query returns 400 without exiting', () => {
  let server: Server
  let base: string
  let homeDir: string
  let cacheDir: string
  const prevHome = process.env['HOME']
  const prevCache = process.env['CODEBURN_CACHE_DIR']

  beforeAll(async () => {
    homeDir = await mkdtemp(join(tmpdir(), 'codeburn-web-home-'))
    cacheDir = await mkdtemp(join(tmpdir(), 'codeburn-web-cache-'))
    process.env['HOME'] = homeDir
    process.env['CODEBURN_CACHE_DIR'] = cacheDir
    server = await runWebDashboard({
      period: 'today', provider: 'all', project: [], exclude: [], port: 0, open: false,
    })
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  })

  afterAll(async () => {
    await new Promise<void>((resolve) => server.close(() => resolve()))
    if (prevHome === undefined) delete process.env['HOME']
    else process.env['HOME'] = prevHome
    if (prevCache === undefined) delete process.env['CODEBURN_CACHE_DIR']
    else process.env['CODEBURN_CACHE_DIR'] = prevCache
    await rm(homeDir, { recursive: true, force: true })
    await rm(cacheDir, { recursive: true, force: true })
  })

  it('answers 400 for an invalid /api/usage period and keeps serving', async () => {
    const bad = await fetch(`${base}/api/usage?period=garbage`)
    expect(bad.status).toBe(400)
    expect((await bad.json() as { error: string }).error).toMatch(/Unknown period "garbage"/)

    // The bug was process.exit; if it regressed, this test process would die.
    // A successful follow-up request proves the server survived the bad one.
    const ok = await fetch(`${base}/api/usage?period=today`)
    expect(ok.status).toBe(200)
    const payload = await ok.json() as { history: { timeline?: { bucketMinutes: number; points: unknown[] } } }
    expect(payload.history.timeline?.bucketMinutes).toBe(15)
    expect(Array.isArray(payload.history.timeline?.points)).toBe(true)
  })

  it('answers 400 for an invalid /api/devices period', async () => {
    const bad = await fetch(`${base}/api/devices?period=garbage`)
    expect(bad.status).toBe(400)
    expect((await bad.json() as { error: string }).error).toMatch(/Unknown period "garbage"/)
  })
})

describe('web dashboard server: /api/kyber/* routes', () => {
  let server: Server
  let base: string
  let canonDb: DatabaseSync
  let sessionsDb: DatabaseSync
  let testBridge: KyberBridge

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

    const testSessionPayload = {
      id: 'sess-hermetic-1',
      harness: 'copilot',
      label: 'Hermetic Test Session',
      context: { measurable: true },
      tools: [
        { name: 'test_tool', server: 'built-in', total_schema_cost: 120, invocations: 1 },
      ],
      summary: {
        turn_count: 3,
        request_count: 3,
        total_input: 1200,
        total_output: 400,
        cost: { usd: 0.05, basis: 'published_rates' },
        models: ['gpt-4o'],
      },
      turns: [
        {
          index: 0,
          input: 1000,
          fresh: 200,
          buckets: {
            system_prompt: 100,
            tool_definitions: 200,
            instruction_context: 300,
            conversation_history: 100,
            tool_result_content: 100,
          },
        },
      ],
      timeline: {
        spanId: 'span-root-1',
        children: [],
      },
    }

    canonDb
      .prepare('INSERT INTO session VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)')
      .run(
        'sess-hermetic-1',
        'copilot',
        'Hermetic Test Session',
        0,
        null,
        'hermetic-agent',
        'kyber-repo',
        'main',
        '2026-09-03T10:00:00Z',
        '2026-09-03T10:05:00Z',
        JSON.stringify(testSessionPayload)
      )

    canonDb
      .prepare('INSERT INTO quarantine VALUES (?, ?, ?, ?, ?, ?)')
      .run('quar-1', 'copilot', 'test_span', '["custom"]', 'reason', 1725360000)

    canonDb
      .prepare(
        'INSERT INTO problem (session_id, span_id, severity, code, message, at, harness) VALUES (?, ?, ?, ?, ?, ?, ?)'
      )
      .run('sess-hermetic-1', 'prob-1', 'warning', 'test_code', 'test problem message', 1725360000, 'copilot')

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

  it('GET /api/kyber/sessions returns sessions list with correct headers', async () => {
    const res = await fetch(`${base}/api/kyber/sessions`)
    expect(res.status).toBe(200)
    expect(res.headers.get('content-type')).toContain('application/json')
    expect(res.headers.get('cache-control')).toBe('no-store')
    const body = (await res.json()) as { sessions: Array<{ session_id: string; harness: string }> }
    expect(Array.isArray(body.sessions)).toBe(true)
    expect(body.sessions.length).toBeGreaterThan(0)
    expect(body.sessions[0].session_id).toBe('sess-hermetic-1')
  })

  it('GET /api/kyber/session/:id and ?id=... return session payload or 404/400', async () => {
    // 400 on missing id
    const missing = await fetch(`${base}/api/kyber/session`)
    expect(missing.status).toBe(400)
    expect(missing.headers.get('content-type')).toContain('application/json')
    expect(missing.headers.get('cache-control')).toBe('no-store')
    expect(((await missing.json()) as { error: string }).error).toBe('Missing session id')

    // 404 on nonexistent id
    const notFound = await fetch(`${base}/api/kyber/session/non-existent-xyz`)
    expect(notFound.status).toBe(404)
    expect(((await notFound.json()) as { error: string }).error).toBe('Session not found')

    // Query param style
    const notFoundQ = await fetch(`${base}/api/kyber/session?id=non-existent-xyz`)
    expect(notFoundQ.status).toBe(404)

    // Valid session by path param
    const hitPath = await fetch(`${base}/api/kyber/session/sess-hermetic-1`)
    expect(hitPath.status).toBe(200)
    expect(hitPath.headers.get('content-type')).toContain('application/json')
    expect(hitPath.headers.get('cache-control')).toBe('no-store')
    const p1 = (await hitPath.json()) as { id: string; harness: string }
    expect(p1.id).toBe('sess-hermetic-1')
    expect(p1.harness).toBe('copilot')

    // Valid session by query param
    const hitQuery = await fetch(`${base}/api/kyber/session?id=sess-hermetic-1`)
    expect(hitQuery.status).toBe(200)
    const p2 = await hitQuery.json()
    expect(p2).toEqual(p1)
  })

  it('returns HTTP 405 Method Not Allowed with no-store cache control for non-GET requests', async () => {
    const postRes = await fetch(`${base}/api/kyber/sessions`, { method: 'POST' })
    expect(postRes.status).toBe(405)
    expect(postRes.headers.get('content-type')).toContain('application/json')
    expect(postRes.headers.get('cache-control')).toBe('no-store')
    expect(await postRes.json()).toEqual({ error: 'Method Not Allowed' })

    const putRes = await fetch(`${base}/api/kyber/session/sess-hermetic-1`, { method: 'PUT' })
    expect(putRes.status).toBe(405)
    expect(putRes.headers.get('cache-control')).toBe('no-store')
    expect(await putRes.json()).toEqual({ error: 'Method Not Allowed' })

    const deleteRes = await fetch(`${base}/api/kyber/compare`, { method: 'DELETE' })
    expect(deleteRes.status).toBe(405)
    expect(deleteRes.headers.get('cache-control')).toBe('no-store')
    expect(await deleteRes.json()).toEqual({ error: 'Method Not Allowed' })

    const postContext = await fetch(`${base}/api/kyber/context`, { method: 'POST' })
    expect(postContext.status).toBe(405)
    expect(postContext.headers.get('cache-control')).toBe('no-store')
    expect(await postContext.json()).toEqual({ error: 'Method Not Allowed' })
  })

  it('GET /api/kyber/compare returns comparison table', async () => {
    const res = await fetch(`${base}/api/kyber/compare`)
    expect(res.status).toBe(200)
    expect(res.headers.get('content-type')).toContain('application/json')
    expect(res.headers.get('cache-control')).toBe('no-store')
    const body = (await res.json()) as { harnesses: string[]; rows: unknown[] }
    expect(Array.isArray(body.harnesses)).toBe(true)
    expect(Array.isArray(body.rows)).toBe(true)
  })

  it('GET /api/kyber/quarantine returns quarantine entries', async () => {
    const res = await fetch(`${base}/api/kyber/quarantine`)
    expect(res.status).toBe(200)
    expect(res.headers.get('content-type')).toContain('application/json')
    expect(res.headers.get('cache-control')).toBe('no-store')
    const body = (await res.json()) as { entries: unknown[] }
    expect(Array.isArray(body.entries)).toBe(true)
    expect(body.entries.length).toBe(1)
  })

  it('GET /api/kyber/problems returns recorded problems', async () => {
    const res = await fetch(`${base}/api/kyber/problems`)
    expect(res.status).toBe(200)
    expect(res.headers.get('content-type')).toContain('application/json')
    expect(res.headers.get('cache-control')).toBe('no-store')
    const body = (await res.json()) as { problems: unknown[] }
    expect(Array.isArray(body.problems)).toBe(true)
    expect(body.problems.length).toBe(1)
  })

  it('GET /api/kyber/meta returns metadata', async () => {
    const res = await fetch(`${base}/api/kyber/meta`)
    expect(res.status).toBe(200)
    expect(res.headers.get('content-type')).toContain('application/json')
    expect(res.headers.get('cache-control')).toBe('no-store')
    const body = (await res.json()) as { span_count: number; tokenizer: unknown; rates: unknown }
    expect(typeof body.span_count).toBe('number')
    expect(body.tokenizer).toBeDefined()
    expect(body.rates).toBeDefined()
  })

  it('backward-compatible endpoints /context, /schema, /timeline return JSON', async () => {
    const ctx = await fetch(`${base}/api/kyber/context`)
    expect(ctx.status).toBe(200)
    expect(ctx.headers.get('content-type')).toContain('application/json')
    expect(ctx.headers.get('cache-control')).toBe('no-store')
    const ctxBody = (await ctx.json()) as { measurable?: boolean }
    expect(ctxBody).toBeDefined()
    expect(ctxBody.measurable).toBe(true)

    const schema = await fetch(`${base}/api/kyber/schema`)
    expect(schema.status).toBe(200)
    expect(schema.headers.get('content-type')).toContain('application/json')
    expect(schema.headers.get('cache-control')).toBe('no-store')
    const schemaBody = (await schema.json()) as { measurable?: boolean }
    expect(schemaBody).toBeDefined()
    expect(schemaBody.measurable).toBe(true)

    const timeline = await fetch(`${base}/api/kyber/timeline`)
    expect(timeline.status).toBe(200)
    expect(timeline.headers.get('content-type')).toContain('application/json')
    expect(timeline.headers.get('cache-control')).toBe('no-store')
    const timelineBody = (await timeline.json()) as { spanId?: string; children?: unknown[] }
    expect(timelineBody).toBeDefined()
    expect(Array.isArray(timelineBody.children)).toBe(true)
  })

  it('unrecognized /api/kyber/* paths return 404 JSON, never SPA HTML', async () => {
    const res = await fetch(`${base}/api/kyber/unknown-endpoint-xyz`)
    expect(res.status).toBe(404)
    expect(res.headers.get('content-type')).toContain('application/json')
    expect(res.headers.get('cache-control')).toBe('no-store')
    const body = (await res.json()) as { error: string }
    expect(body.error).toBe('Not found')
  })
})
