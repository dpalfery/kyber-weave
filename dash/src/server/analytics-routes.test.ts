import { EventEmitter } from 'node:events'
import { mkdtemp, rm } from 'node:fs/promises'
import type { IncomingMessage, ServerResponse } from 'node:http'
import type { Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'

import type { AnalyticsEnvelope } from '../analysis/query/service.js'
import { makeAnalyticsSource, makeService } from '../analysis/query/fixtures/analytics-source.js'
import { runWebDashboard } from '../cli/web.js'
import { handleAnalyticsRequest, type AnalyticsService } from './analytics-routes.js'

const POST_ROUTES = [
  '/api/kyber/analytics/query',
  '/api/kyber/analytics/search-sessions',
  '/api/kyber/analytics/inspect',
  '/api/kyber/analytics/findings',
  '/api/kyber/analytics/compare',
  '/api/kyber/analytics/outcomes',
] as const

const EIGHT = [
  { method: 'GET', path: '/api/kyber/analytics/status', operation: 'status', request: {} },
  { method: 'GET', path: '/api/kyber/analytics/catalog', operation: 'catalog', request: {} },
  {
    method: 'POST',
    path: '/api/kyber/analytics/query',
    operation: 'query',
    request: { catalogVersion: 1, metrics: ['input_tokens'], limit: 25 },
  },
  {
    method: 'POST',
    path: '/api/kyber/analytics/search-sessions',
    operation: 'search-sessions',
    request: { query: 'session-a', limit: 25 },
  },
  {
    method: 'POST',
    path: '/api/kyber/analytics/inspect',
    operation: 'inspect',
    request: { sessionId: 'session-a' },
  },
  { method: 'POST', path: '/api/kyber/analytics/findings', operation: 'findings', request: {} },
  { method: 'POST', path: '/api/kyber/analytics/compare', operation: 'compare', request: {} },
  { method: 'POST', path: '/api/kyber/analytics/outcomes', operation: 'outcomes', request: {} },
] as const

type MockResponse = {
  statusCode: number
  headers: Record<string, string | string[] | undefined>
  body: string
  written: Promise<void>
}

function mockResponse(): { res: ServerResponse; state: MockResponse } {
  let markWritten: () => void = () => {}
  const written = new Promise<void>((resolve) => {
    markWritten = resolve
  })
  const state: MockResponse = { statusCode: 200, headers: {}, body: '', written }
  const res = {
    writeHead(status: number, headers?: Record<string, string>) {
      state.statusCode = status
      state.headers = { ...state.headers, ...(headers ?? {}) }
    },
    end(data?: string) {
      if (data !== undefined) state.body += data
      markWritten()
    },
  } as unknown as ServerResponse
  return { res, state }
}

function mockRequest(method: string, headers: Record<string, string>, chunks: Array<string | Buffer>): IncomingMessage {
  const req = new EventEmitter() as unknown as IncomingMessage
  req.method = method
  req.headers = headers
  ;(req as unknown as { resume: () => void }).resume = () => {}
  queueMicrotask(() => {
    for (const chunk of chunks) req.emit('data', chunk)
    req.emit('end')
  })
  return req
}

async function callAnalyticsDirect(
  method: string,
  path: string,
  service: AnalyticsService | undefined,
  options: { headers?: Record<string, string>; chunks?: Array<string | Buffer> } = {},
): Promise<{ status: number; headers: Record<string, string | string[] | undefined>; body: unknown; raw: string; handled: boolean }> {
  const { res, state } = mockResponse()
  const headers = options.headers ?? (method === 'POST' ? { 'content-type': 'application/json' } : {})
  const req = mockRequest(method, headers, options.chunks ?? [])
  const handled = handleAnalyticsRequest(req, res, new URL(`http://127.0.0.1:4747${path}`), service)
  if (handled) await state.written
  let body: unknown
  try {
    body = state.body === '' ? undefined : JSON.parse(state.body)
  } catch {
    body = undefined
  }
  return { status: state.statusCode, headers: state.headers, body, raw: state.body, handled }
}

function stubService(envelope: AnalyticsEnvelope): AnalyticsService {
  return { execute: async () => envelope }
}

function envelopeWith(code: string | null): AnalyticsEnvelope {
  return {
    contractVersion: 1,
    scope: {},
    data: code === null ? { groups: [], items: [], deltas: {}, content: {} } : null,
    measurement: {},
    coverage: {},
    freshness: {},
    evidence: [],
    pagination: {},
    error: code === null ? null : { code, message: 'stabilized message', hint: 'stabilized hint' },
  }
}

describe('analytics HTTP routes', () => {
  let server: Server
  let base: string
  let stateDir: string
  let source: ReturnType<typeof makeAnalyticsSource>

  beforeAll(async () => {
    stateDir = await mkdtemp(join(tmpdir(), 'kyberdash-analytics-http-'))
    source = makeAnalyticsSource()
    const options = {
      port: 0,
      open: false,
      writeStdout: () => {},
      stateDir,
      singleInstance: false,
      analyticsService: makeService(source),
    }
    // RED seam for the server wrapper: the analytics service is injected instead of
    // opening a second store or deriving results inside the HTTP layer.
    server = await runWebDashboard(options as Parameters<typeof runWebDashboard>[0])
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  })

  afterAll(async () => {
    if (server?.listening) await new Promise<void>((resolve) => server.close(() => resolve()))
    if (stateDir) await rm(stateDir, { recursive: true, force: true })
  })

  it('serves status and catalog as versioned analytics envelopes without changing legacy meta', async () => {
    for (const path of ['/api/kyber/analytics/status', '/api/kyber/analytics/catalog']) {
      const response = await fetch(`${base}${path}`)
      const body = (await response.json()) as Record<string, unknown>

      expect(response.status, path).toBe(200)
      expect(response.headers.get('cache-control'), path).toBe('no-store')
      expect(body).toMatchObject({
        contractVersion: 1,
        scope: expect.any(Object),
        measurement: expect.any(Object),
        coverage: expect.any(Object),
        freshness: expect.any(Object),
        evidence: expect.any(Object),
        pagination: expect.any(Object),
        error: null,
      })
      expect(body).toHaveProperty('data')
    }

    const legacyMeta = await fetch(`${base}/api/kyber/meta`)
    const legacyBody = (await legacyMeta.json()) as Record<string, unknown>
    expect(legacyMeta.status).toBe(200)
    expect(legacyBody.apiVersion).toBeDefined()
  })

  it.each(POST_ROUTES)('%s accepts bounded JSON and returns the common envelope', async (path) => {
    const request = path.endsWith('/query')
      ? { catalogVersion: 1, metrics: ['input_tokens'], limit: 25 }
      : path.endsWith('/search-sessions')
        ? { query: 'session-a', limit: 25 }
        : path.endsWith('/inspect')
          ? { sessionId: 'session-a' }
          : {}
    const response = await fetch(`${base}${path}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(request),
    })
    const body = (await response.json()) as Record<string, unknown>

    expect(response.headers.get('content-type')).toContain('application/json')
    expect(response.status).toBeGreaterThanOrEqual(200)
    expect(response.status).toBeLessThan(600)
    expect(body).toMatchObject({
      contractVersion: 1,
      scope: expect.any(Object),
      measurement: expect.any(Object),
      coverage: expect.any(Object),
      freshness: expect.any(Object),
      evidence: expect.any(Object),
      pagination: expect.any(Object),
    })
    expect(body).toHaveProperty('data')
    expect(body.error === null || typeof body.error === 'object').toBe(true)
  })

  it('rejects unknown request fields before the HTTP adapter reads analytics data', async () => {
    const response = await fetch(`${base}/api/kyber/analytics/query`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ catalogVersion: 1, metrics: ['input_tokens'], joins: ['records'] }),
    })
    const body = (await response.json()) as Record<string, unknown>

    expect(response.status).toBe(400)
    expect(body).toMatchObject({
      contractVersion: 1,
      error: { code: 'invalid_request' },
    })
    expect(source.candidateSessionIds).not.toHaveBeenCalled()
    expect(source.readSessionSummaries).not.toHaveBeenCalled()
  })

  it('returns a safe internal error when the source throws and keeps the server usable', async () => {
    source.readFindings.mockRejectedValueOnce(new Error('SQLITE_BUSY /Users/secret/canon.db'))
    const failed = await fetch(`${base}/api/kyber/analytics/findings`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({}),
    })
    const body = (await failed.json()) as Record<string, unknown>
    const serialized = JSON.stringify(body)

    expect(failed.status).toBe(500)
    expect(body).toMatchObject({ contractVersion: 1, error: { code: 'internal_error' } })
    expect(serialized).not.toContain('SQLITE_BUSY')
    expect(serialized).not.toContain('/Users/secret')

    const alive = await fetch(`${base}/api/kyber/analytics/status`)
    expect(alive.status).toBe(200)
  })

  it('does not route a GET to a read-specification POST operation', async () => {
    const response = await fetch(`${base}/api/kyber/analytics/query`)

    expect(response.status).toBe(405)
    expect(response.headers.get('allow')).toContain('POST')
  })
})

describe('every analytics route speaks the shared versioned envelope', () => {
  let server: Server
  let base: string
  let stateDir: string

  beforeAll(async () => {
    stateDir = await mkdtemp(join(tmpdir(), 'kyberdash-analytics-envelope-'))
    const options = {
      port: 0,
      open: false,
      writeStdout: () => {},
      stateDir,
      singleInstance: false,
      analyticsService: makeService(),
    }
    server = await runWebDashboard(options as Parameters<typeof runWebDashboard>[0])
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  })

  afterAll(async () => {
    if (server?.listening) await new Promise<void>((resolve) => server.close(() => resolve()))
    if (stateDir) await rm(stateDir, { recursive: true, force: true })
  })

  it.each(EIGHT)('$method $path carries contractVersion 1, the R1 identity and every required key', async ({
    method,
    path,
    request,
  }) => {
    const response = await fetch(`${base}${path}`, {
      method,
      ...(method === 'POST'
        ? { headers: { 'content-type': 'application/json' }, body: JSON.stringify(request) }
        : {}),
    })
    const body = (await response.json()) as Record<string, unknown>

    // WHY contractVersion plus the R1 identity: every transport shares one
    // frozen contract, so a client can pin the version and the engine that
    // produced it instead of trusting an unversioned payload.
    expect(response.headers.get('content-type')).toContain('application/json')
    expect(response.headers.get('cache-control')).toBe('no-store')
    expect(body.contractVersion).toBe(1)
    for (const key of ['scope', 'data', 'measurement', 'coverage', 'freshness', 'evidence', 'pagination', 'error']) {
      expect(body, path).toHaveProperty(key)
    }
    expect(body.freshness).toMatchObject({
      generatedAt: '2026-10-24T12:00:00.000Z',
      instanceId: 'instance-1',
      projectionGeneration: 'generation-1',
    })
    expect(body.scope).toMatchObject({ from: expect.any(String), to: expect.any(String), timezone: 'UTC' })
  })
})

describe('analytics routes enforce their GET/POST allowlist', () => {
  it.each(EIGHT)('$method $path rejects the opposite method with a 405 allow header', async ({ method, path }) => {
    const opposite = method === 'GET' ? 'POST' : 'GET'
    const result = await callAnalyticsDirect(opposite, path, makeService(), {
      headers: opposite === 'POST' ? { 'content-type': 'application/json' } : {},
      chunks: opposite === 'POST' ? ['{}'] : [],
    })

    expect(result.handled).toBe(true)
    expect(result.status).toBe(405)
    expect(result.headers.allow).toContain(method)
  })

  it('leaves a non-analytics path unclaimed so legacy routing still answers it', async () => {
    const result = await callAnalyticsDirect('GET', '/api/kyber/report', makeService())

    expect(result.handled).toBe(false)
  })
})

describe('analytics routes bound their bodies and results', () => {
  let server: Server
  let base: string
  let stateDir: string

  beforeAll(async () => {
    stateDir = await mkdtemp(join(tmpdir(), 'kyberdash-analytics-bounds-'))
    const options = {
      port: 0,
      open: false,
      writeStdout: () => {},
      stateDir,
      singleInstance: false,
      analyticsService: makeService(),
    }
    server = await runWebDashboard(options as Parameters<typeof runWebDashboard>[0])
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  })

  afterAll(async () => {
    if (server?.listening) await new Promise<void>((resolve) => server.close(() => resolve()))
    if (stateDir) await rm(stateDir, { recursive: true, force: true })
  })

  it('refuses a body larger than 65536 bytes with a 413 budget error and closes the connection', async () => {
    const oversized = JSON.stringify({ query: 'x'.repeat(70_000) })
    const result = await callAnalyticsDirect('POST', '/api/kyber/analytics/search-sessions', makeService(), {
      headers: { 'content-type': 'application/json' },
      chunks: [oversized],
    })
    const body = result.body as Record<string, unknown>

    expect(result.status).toBe(413)
    expect(body).toMatchObject({ contractVersion: 1, data: null, error: { code: 'budget_exceeded' } })
    expect(result.headers.connection).toContain('close')
  })

  it('rejects a body that is not valid JSON before the service runs', async () => {
    const result = await callAnalyticsDirect('POST', '/api/kyber/analytics/query', makeService(), {
      headers: { 'content-type': 'application/json' },
      chunks: ['{not json'],
    })
    const body = result.body as Record<string, unknown>

    expect(result.status).toBe(400)
    expect(body).toMatchObject({ contractVersion: 1, error: { code: 'invalid_request' } })
  })

  it('forwards a body whose multi-byte text straddles chunk boundaries intact', async () => {
    const request = { query: '雪🙂é session' }
    const raw = Buffer.from(JSON.stringify(request), 'utf8')
    // WHY a mid-character split: per-chunk string decoding corrupts exactly
    // here, replacing the split character. Byte 10 is the first byte of 雪
    // (`{"query":"` is ten ASCII bytes), so cutting at 11 lands inside it.
    const split = 11
    const seen: Array<{ operation: string; request: unknown }> = []
    const service: AnalyticsService = {
      execute: async (operation, body) => {
        seen.push({ operation, request: body })
        return envelopeWith(null)
      },
    }
    const result = await callAnalyticsDirect('POST', '/api/kyber/analytics/search-sessions', service, {
      headers: { 'content-type': 'application/json' },
      chunks: [raw.subarray(0, split), raw.subarray(split)],
    })

    expect(result.status).toBe(200)
    expect(seen).toEqual([{ operation: 'search-sessions', request }])
  })

  it('rejects a JSON array body because every analytics request is an object', async () => {
    const result = await callAnalyticsDirect('POST', '/api/kyber/analytics/findings', makeService(), {
      headers: { 'content-type': 'application/json' },
      chunks: ['[]'],
    })

    expect(result.status).toBe(400)
    expect(result.body).toMatchObject({ contractVersion: 1, error: { code: 'invalid_request' } })
  })

  it('rejects a POST without an application/json content type', async () => {
    const response = await fetch(`${base}/api/kyber/analytics/query`, {
      method: 'POST',
      headers: { 'content-type': 'text/plain' },
      body: JSON.stringify({ catalogVersion: 1, metrics: ['input_tokens'] }),
    })
    const body = (await response.json()) as Record<string, unknown>

    expect(response.status).toBe(400)
    expect(body).toMatchObject({ contractVersion: 1, error: { code: 'invalid_request' } })
  })

  it('rejects an overlong metadata search before reading session summaries', async () => {
    const response = await fetch(`${base}/api/kyber/analytics/search-sessions`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ query: 'x'.repeat(201) }),
    })
    const body = (await response.json()) as Record<string, unknown>

    expect(response.status).toBe(400)
    expect(body).toMatchObject({ contractVersion: 1, error: { code: 'invalid_request' } })
  })

  it('bounds search results to twenty-five rows by default for the R1 session-a fixture', async () => {
    const response = await fetch(`${base}/api/kyber/analytics/search-sessions`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ query: 'session-a' }),
    })
    const body = (await response.json()) as Record<string, unknown>

    // The R1 session-a summary carries no label, and search matches labels
    // literally without scanning content: zero rows is the correct measured
    // answer here, and the bound is still the default twenty-five.
    expect(response.status).toBe(200)
    expect(body.pagination).toMatchObject({ limit: 25, returnedCount: 0 })
  })
})

describe('analytics routes map stable error codes to transport statuses', () => {
  let server: Server
  let base: string
  let stateDir: string

  beforeAll(async () => {
    stateDir = await mkdtemp(join(tmpdir(), 'kyberdash-analytics-status-'))
    const options = {
      port: 0,
      open: false,
      writeStdout: () => {},
      stateDir,
      singleInstance: false,
      analyticsService: makeService(),
    }
    server = await runWebDashboard(options as Parameters<typeof runWebDashboard>[0])
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  })

  afterAll(async () => {
    if (server?.listening) await new Promise<void>((resolve) => server.close(() => resolve()))
    if (stateDir) await rm(stateDir, { recursive: true, force: true })
  })

  it.each([
    { code: 'invalid_request', status: 400 },
    { code: 'unsupported_query', status: 400 },
    { code: 'not_found', status: 404 },
    { code: 'stale_cursor', status: 409 },
    { code: 'budget_exceeded', status: 413 },
    { code: 'engine_unavailable', status: 503 },
    { code: 'incompatible_engine', status: 503 },
    { code: 'cancelled', status: 503 },
    { code: 'internal_error', status: 500 },
  ])('maps $code to HTTP $status without changing the envelope identity', async ({ code, status }) => {
    const result = await callAnalyticsDirect('POST', '/api/kyber/analytics/query', stubService(envelopeWith(code)), {
      headers: { 'content-type': 'application/json' },
      chunks: ['{}'],
    })
    const body = result.body as Record<string, unknown>

    expect(result.status).toBe(status)
    expect(body).toMatchObject({ contractVersion: 1, error: { code } })
  })

  it('answers 500 for an error envelope with an unrecognized code instead of masking it as success', async () => {
    // WHY 500 and not 200: an error envelope answered with 200 reads as
    // success to any client that checks the status before parsing the body.
    // Recognized client-error codes keep their 4xx mappings above; anything
    // else is a server-side surprise, and success stays 200.
    for (const code of ['content_unavailable', 'future_code_xyz']) {
      const result = await callAnalyticsDirect('POST', '/api/kyber/analytics/findings', stubService(envelopeWith(code)), {
        headers: { 'content-type': 'application/json' },
        chunks: ['{}'],
      })
      const body = result.body as Record<string, unknown>

      expect(result.status, code).toBe(500)
      expect(body).toMatchObject({ contractVersion: 1, error: { code } })
    }
    const ok = await callAnalyticsDirect('POST', '/api/kyber/analytics/findings', stubService(envelopeWith(null)), {
      headers: { 'content-type': 'application/json' },
      chunks: ['{}'],
    })

    expect(ok.status).toBe(200)
  })

  it('answers 503 engine_unavailable when the shared service is absent', async () => {
    const result = await callAnalyticsDirect('GET', '/api/kyber/analytics/status', undefined)
    const body = result.body as Record<string, unknown>

    expect(result.handled).toBe(true)
    expect(result.status).toBe(503)
    expect(body).toMatchObject({ contractVersion: 1, data: null, error: { code: 'engine_unavailable' } })
  })

  it('logs a handler throw and still answers a safe 500 without leaking the cause', async () => {
    const errors: unknown[][] = []
    const original = console.error
    console.error = (...args: unknown[]) => { errors.push(args) }
    try {
      const failing: AnalyticsService = {
        execute: async () => { throw new Error('SQLITE_BUSY /Users/secret/canon.db') },
      }
      const result = await callAnalyticsDirect('POST', '/api/kyber/analytics/query', failing, {
        headers: { 'content-type': 'application/json' },
        chunks: ['{}'],
      })
      const body = result.body as Record<string, unknown>

      expect(result.status).toBe(500)
      expect(body).toMatchObject({ contractVersion: 1, error: { code: 'internal_error' } })
      expect(JSON.stringify(body)).not.toContain('SQLITE_BUSY')
      expect(errors.length).toBeGreaterThan(0)
    } finally {
      console.error = original
    }
  })

  it('does not write headers twice when the handler throws after headers went out', async () => {
    const writes: string[] = []
    const req = mockRequest('POST', { 'content-type': 'application/json' }, ['{}'])
    const res = {
      headersSent: true,
      writableEnded: false,
      writeHead: () => { writes.push('writeHead') },
      end: () => { writes.push('end') },
    } as unknown as ServerResponse
    const failing: AnalyticsService = {
      execute: async () => { throw new Error('boom after partial write') },
    }
    const errors: unknown[][] = []
    const original = console.error
    console.error = (...args: unknown[]) => { errors.push(args) }
    try {
      const handled = handleAnalyticsRequest(req, res, new URL('http://127.0.0.1:4747/api/kyber/analytics/query'), failing)

      expect(handled).toBe(true)
      await new Promise((resolve) => setTimeout(resolve, 50))
      expect(writes).not.toContain('writeHead')
      expect(errors.length).toBeGreaterThan(0)
    } finally {
      console.error = original
    }
  })

  it('resolves an unknown session through the service as a 404 that keeps the R1 scope', async () => {
    const response = await fetch(`${base}/api/kyber/analytics/inspect`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ kind: 'session', id: 'missing-session' }),
    })
    const body = (await response.json()) as Record<string, unknown>

    expect(response.status).toBe(404)
    expect(body).toMatchObject({ contractVersion: 1, error: { code: 'not_found' } })
    expect(body.scope).toMatchObject({ timezone: 'UTC' })
  })
})

describe('legacy kyber routes keep their shapes beside the analytics routes', () => {
  let server: Server
  let base: string
  let stateDir: string

  beforeAll(async () => {
    stateDir = await mkdtemp(join(tmpdir(), 'kyberdash-analytics-legacy-'))
    const options = {
      port: 0,
      open: false,
      writeStdout: () => {},
      stateDir,
      singleInstance: false,
      analyticsService: makeService(),
    }
    server = await runWebDashboard(options as Parameters<typeof runWebDashboard>[0])
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  })

  afterAll(async () => {
    if (server?.listening) await new Promise<void>((resolve) => server.close(() => resolve()))
    if (stateDir) await rm(stateDir, { recursive: true, force: true })
  })

  it('keeps serving meta with its apiVersion beside versioned analytics envelopes', async () => {
    const response = await fetch(`${base}/api/kyber/meta`)
    const body = (await response.json()) as Record<string, unknown>

    expect(response.status).toBe(200)
    expect(body.apiVersion).toBe(1)
    expect(typeof body.version).toBe('string')
    expect(body).not.toHaveProperty('contractVersion')
  })

  it('keeps serving the report document with its schemaVersion rather than the analytics envelope', async () => {
    const response = await fetch(`${base}/api/kyber/report`)
    const body = (await response.json()) as Record<string, unknown>

    expect(response.status).toBe(200)
    expect(body).toMatchObject({ schemaVersion: 1, scope: { days: 7 } })
    expect(body).not.toHaveProperty('contractVersion')
  })

  it('keeps serving the paged legacy findings list without a contractVersion', async () => {
    const response = await fetch(`${base}/api/kyber/findings`)
    const body = (await response.json()) as Record<string, unknown>

    expect(response.status).toBe(200)
    expect(body).toHaveProperty('findings')
    expect(body).toHaveProperty('total')
    expect(body).not.toHaveProperty('contractVersion')
  })

  it('keeps the legacy GET compare table distinct from the analytics compare envelope', async () => {
    const legacy = await fetch(`${base}/api/kyber/compare`)
    const legacyBody = (await legacy.json()) as Record<string, unknown>
    const scoped = await fetch(`${base}/api/kyber/analytics/compare`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({}),
    })
    const scopedBody = (await scoped.json()) as Record<string, unknown>

    expect(legacy.status).toBe(200)
    expect(legacyBody).not.toHaveProperty('contractVersion')
    expect(scopedBody.contractVersion).toBe(1)
    expect(scopedBody).toHaveProperty('data')
  })
})
