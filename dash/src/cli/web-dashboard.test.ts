import { mkdtemp, rm, writeFile } from 'fs/promises'
import { request as httpRequest } from 'http'
import { tmpdir } from 'os'
import { join } from 'path'
import type { AddressInfo } from 'net'
import type { Server } from 'http'
import { DatabaseSync } from 'node:sqlite'

import { afterAll, beforeAll, describe, expect, it } from 'vitest'

import { runWebDashboard } from './web.js'
import { KyberBridge } from '../server/bridge.js'

describe('web dashboard server: serving and the loopback guard', () => {
  let server: Server
  let base: string
  let port: number
  let dashDir: string
  const prevDashDir = process.env['KYBERDASH_DASH_DIR']

  beforeAll(async () => {
    dashDir = await mkdtemp(join(tmpdir(), 'kyberdash-web-ui-'))
    await writeFile(join(dashDir, 'index.html'), '<!doctype html><title>CodeBurn</title><script type="module" src="/app.js"></script>')
    process.env['KYBERDASH_DASH_DIR'] = dashDir
    server = await runWebDashboard({ port: 0, open: false })
    port = (server.address() as AddressInfo).port
    base = `http://127.0.0.1:${port}`
  })

  afterAll(async () => {
    await new Promise<void>((resolve) => server.close(() => resolve()))
    if (prevDashDir === undefined) delete process.env['KYBERDASH_DASH_DIR']
    else process.env['KYBERDASH_DASH_DIR'] = prevDashDir
    await rm(dashDir, { recursive: true, force: true })
  })

  it('serves the branded index with no inlined usage bootstrap', async () => {
    const res = await fetch(`${base}/`)
    expect(res.status).toBe(200)
    const html = await res.text()
    expect(html).not.toContain('__KYBERDASH_BOOTSTRAP__')
    expect(html).not.toContain('<title>CodeBurn</title>')
  })

  it('serves index.html for an unknown non-API path so the client router can take it', async () => {
    const res = await fetch(`${base}/finding/abc123`)
    expect(res.status).toBe(200)
    expect(res.headers.get('content-type')).toContain('text/html')
  })

  it('no longer serves the usage, device, share or context endpoints', async () => {
    for (const path of ['/api/usage', '/api/devices', '/api/identity', '/api/share/status', '/api/context/sessions']) {
      const res = await fetch(`${base}${path}`)
      expect(res.headers.get('content-type') ?? '', path).not.toContain('application/json')
    }
  })

  it('rejects a request whose Host is not loopback (requirement 5.5)', async () => {
    const res = await rawGet(port, '/api/kyber/meta', { host: 'evil.example' })
    expect(res.status).toBe(403)
  })

  it('rejects a cross-origin request (requirement 5.5)', async () => {
    const res = await rawGet(port, '/api/kyber/meta', { host: '127.0.0.1', origin: 'https://evil.example' })
    expect(res.status).toBe(403)
  })
})

// fetch() will not let a test forge the Host header, so the guard is exercised with node:http.
function rawGet(port: number, path: string, headers: Record<string, string>): Promise<{ status: number }> {
  return new Promise((resolve, reject) => {
    const req = httpRequest({ host: '127.0.0.1', port, path, method: 'GET', headers }, (res) => {
      res.resume()
      res.on('end', () => resolve({ status: res.statusCode ?? 0 }))
    })
    req.on('error', reject)
    req.end()
  })
}

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
      schema: { measurable: true },
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

    server = await runWebDashboard({ port: 0, open: false, kyberBridge: testBridge })
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
