import { EventEmitter } from 'node:events'
import { mkdtemp } from 'node:fs/promises'
import type { IncomingMessage, ServerResponse } from 'node:http'
import type { Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest'

import { dataOf, makeAnalyticsSource, makeService, makeSession } from '../analysis/query/fixtures/analytics-source.js'
import { runWebDashboard } from '../cli/web.js'
import type { JobHost } from '../jobs/host.js'
import { handleAnalyticsRequest, type AnalyticsService } from './analytics-routes.js'
import { removeTempStateDir, stopServer } from './testing.js'
import { useAbsentLiveReceiver } from '../../tests/setup/live-receiver-stub.js'

useAbsentLiveReceiver()

const WRAPPERS = [
  { operation: 'status', method: 'GET', path: '/api/kyber/analytics/status', request: {} },
  { operation: 'catalog', method: 'GET', path: '/api/kyber/analytics/catalog', request: {} },
  {
    operation: 'query',
    method: 'POST',
    path: '/api/kyber/analytics/query',
    request: { catalogVersion: 1, metrics: ['input_tokens'], limit: 25 },
  },
  {
    operation: 'search-sessions',
    method: 'POST',
    path: '/api/kyber/analytics/search-sessions',
    request: { query: 'build', limit: 25 },
  },
  {
    operation: 'inspect',
    method: 'POST',
    path: '/api/kyber/analytics/inspect',
    request: { sessionId: 'session-a' },
  },
  { operation: 'findings', method: 'POST', path: '/api/kyber/analytics/findings', request: {} },
  { operation: 'compare', method: 'POST', path: '/api/kyber/analytics/compare', request: {} },
  { operation: 'outcomes', method: 'POST', path: '/api/kyber/analytics/outcomes', request: {} },
] as const

function statusFor(error: unknown): number {
  if (error === null) return 200
  if (typeof error !== 'object' || error === null || !('code' in error)) return 500
  const code = (error as { code: string }).code
  if (code === 'invalid_request' || code === 'unsupported_query') return 400
  if (code === 'not_found') return 404
  if (code === 'stale_cursor') return 409
  if (code === 'budget_exceeded') return 413
  if (code === 'engine_unavailable' || code === 'incompatible_engine' || code === 'cancelled') return 503
  if (code === 'internal_error') return 500
  return 200
}



describe('analytics HTTP and engine parity', () => {
  let server: Server
  let base: string
  let stateDir: string
  let hostClose: Promise<void> | undefined
  const service = makeService()

  beforeAll(async () => {
    stateDir = await mkdtemp(join(tmpdir(), 'kyberdash-analytics-parity-'))
    const options = {
      port: 0,
      open: false,
      writeStdout: () => {},
      stateDir,
      singleInstance: false,
      analyticsService: service,
      closeJobHost: (host: JobHost) => {
        hostClose = host.close()
        return hostClose
      },
    }
    server = await runWebDashboard(options as Parameters<typeof runWebDashboard>[0])
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  })

  afterAll(async () => {
    await stopServer(server, () => hostClose)
    await removeTempStateDir(stateDir)
  })

  it.each(WRAPPERS)('$method $path returns the exact shared engine envelope', async ({
    operation,
    method,
    path,
    request,
  }) => {
    const execute = vi.spyOn(service, 'execute')
    const expected = await service.execute(operation, request)
    execute.mockClear()

    const response = await fetch(`${base}${path}`, {
      method,
      ...(method === 'POST'
        ? { headers: { 'content-type': 'application/json' }, body: JSON.stringify(request) }
        : {}),
    })
    const actual = await response.json()

    expect(execute).toHaveBeenCalledTimes(1)
    expect(execute.mock.calls[0]?.[0]).toBe(operation)
    expect(execute.mock.calls[0]?.[1]).toEqual(request)
    expect(actual).toEqual(expected)
    expect(response.status).toBe(statusFor(expected.error))
  })
})

describe('existing report, findings and turn values survive the scoped wrappers', () => {
  it('preserves the canonical finding mechanism, confidence, rank and evidence references', async () => {
    const finding = {
      id: 'finding-1',
      mechanism: 'Repeated schema loading was observed.',
      confidence: 'high',
      outcomeRiskCaveat: 'Review before changing instructions.',
      rankScore: 12,
      evidenceLinks: [{ sessionId: 'session-a', turnIndex: 1 }, { sessionId: 'session-b', turnIndex: 2 }],
      measurementClass: 'deterministic',
    }
    const source = makeAnalyticsSource({ readFindings: async () => [finding] })
    const result = await makeService(source).execute('findings', {})

    // WHY field-by-field: the scoped wrapper must carry the same values the
    // legacy findings list serves, never a reworded or rescored copy.
    expect(dataOf(result).items[0]).toEqual(expect.objectContaining(finding))
    expect(result.evidence).toContainEqual(expect.objectContaining({ findingId: 'finding-1' }))
  })

  it('keeps session inspection metadata-only so a scoped read cannot leak a transcript', async () => {
    const source = makeAnalyticsSource({
      readSession: async () => ({
        id: 'session-a',
        label: 'visible label',
        turns: [{ index: 0, blocks: [{ id: 'block-a', text: 'private turn text' }] }],
      }),
    })
    const result = await makeService(source).execute('inspect', { kind: 'session', id: 'session-a' })

    expect(result.error).toBeNull()
    expect(dataOf(result)).toMatchObject({ id: 'session-a', label: 'visible label' })
    expect(JSON.stringify(result)).not.toContain('private turn text')
    expect(source.readTurnPage).not.toHaveBeenCalled()
  })

  it('returns the exact bounded turn block the canonical reader measured', async () => {
    const source = makeAnalyticsSource({
      readTurnPage: async () => ({ text: 'selected block', nextCursor: null, byteLength: 14 }),
    })
    const result = await makeService(source).execute('inspect', {
      kind: 'turn',
      sessionId: 'session-a',
      turnIndex: 2,
      includeContent: true,
      blockId: 'block-2',
      byteLimit: 16384,
    })

    expect(dataOf(result).content).toMatchObject({ text: 'selected block', byteLength: 14 })
    expect(source.readTurnPage).toHaveBeenCalledWith(
      expect.objectContaining({ sessionId: 'session-a', turnIndex: 2, blockId: 'block-2' }),
    )
  })

  it('keeps a measured zero eligible while an absent counter stays unavailable in the scoped query', async () => {
    const source = makeAnalyticsSource({
      candidateSessionIds: async () => ['zero-measured', 'missing-counters'],
      readSessionSummaries: async () => [
        makeSession({ id: 'zero-measured', inputTokens: 0 }),
        makeSession({ id: 'missing-counters', inputTokens: null }),
      ],
    })
    const result = await makeService(source).execute('query', {
      catalogVersion: 1,
      scope: { from: '2026-10-17T00:00:00.000Z', to: '2026-10-24T00:00:00.000Z', timezone: 'UTC' },
      metrics: [{ id: 'input_tokens', operator: 'sum' }],
      groupBy: [],
    })

    expect(result.error).toBeNull()
    expect(dataOf(result).groups[0]).toMatchObject({
      contributorCount: 2,
      metrics: { input_tokens: { value: 0, eligibleCount: 1, unavailableCount: 1 } },
    })
  })

  it('serves the same findings items over HTTP as the shared engine computed', async () => {
    const finding = {
      id: 'finding-1',
      mechanism: 'Repeated schema loading was observed.',
      confidence: 'high',
      rankScore: 12,
      evidenceLinks: [{ sessionId: 'session-a', turnIndex: 1 }],
      measurementClass: 'deterministic',
    }
    const scopedSource = makeAnalyticsSource({ readFindings: async () => [finding] })
    const scopedService = makeService(scopedSource)
    const dir = await mkdtemp(join(tmpdir(), 'kyberdash-analytics-findings-'))
    let scopedHostClose: Promise<void> | undefined
    const scoped = await runWebDashboard({
      port: 0,
      open: false,
      writeStdout: () => {},
      stateDir: dir,
      singleInstance: false,
      analyticsService: scopedService,
      closeJobHost: (host: JobHost) => {
        scopedHostClose = host.close()
        return scopedHostClose
      },
    } as Parameters<typeof runWebDashboard>[0])
    try {
      const url = `http://127.0.0.1:${(scoped.address() as AddressInfo).port}`
      const response = await fetch(`${url}/api/kyber/analytics/findings`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({}),
      })
      const body = (await response.json()) as { data: { items: Array<Record<string, unknown>> } }

      expect(response.status).toBe(200)
      expect(body.data.items[0]).toEqual(expect.objectContaining({ id: 'finding-1', mechanism: finding.mechanism }))
    } finally {
      await stopServer(scoped, () => scopedHostClose)
      await removeTempStateDir(dir)
    }
  })
})

describe('legacy routes keep their shapes beside the new scoped wrappers', () => {
  let server: Server
  let base: string
  let stateDir: string
  let hostClose: Promise<void> | undefined
  const service = makeService()

  beforeAll(async () => {
    stateDir = await mkdtemp(join(tmpdir(), 'kyberdash-analytics-compat-'))
    const options = {
      port: 0,
      open: false,
      writeStdout: () => {},
      stateDir,
      singleInstance: false,
      analyticsService: service,
      closeJobHost: (host: JobHost) => {
        hostClose = host.close()
        return hostClose
      },
    }
    server = await runWebDashboard(options as Parameters<typeof runWebDashboard>[0])
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  })

  afterAll(async () => {
    await stopServer(server, () => hostClose)
    await removeTempStateDir(stateDir)
  })

  it('keeps the report document on its schemaVersion while analytics answers carry contractVersion 1', async () => {
    const legacy = await fetch(`${base}/api/kyber/report`)
    const legacyBody = (await legacy.json()) as Record<string, unknown>
    const scoped = await fetch(`${base}/api/kyber/analytics/status`)
    const scopedBody = (await scoped.json()) as Record<string, unknown>

    expect(legacy.status).toBe(200)
    expect(legacyBody).toMatchObject({ schemaVersion: 1 })
    expect(legacyBody).not.toHaveProperty('contractVersion')
    expect(scopedBody.contractVersion).toBe(1)
  })

  it('keeps the legacy findings page without a contractVersion beside the scoped findings envelope', async () => {
    const legacy = await fetch(`${base}/api/kyber/findings`)
    const legacyBody = (await legacy.json()) as Record<string, unknown>
    const scoped = await fetch(`${base}/api/kyber/analytics/findings`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({}),
    })
    const scopedBody = (await scoped.json()) as Record<string, unknown>

    expect(legacy.status).toBe(200)
    expect(legacyBody).toHaveProperty('findings')
    expect(legacyBody).toHaveProperty('total')
    expect(legacyBody).not.toHaveProperty('contractVersion')
    expect(scopedBody.contractVersion).toBe(1)
  })

  it('keeps the legacy turn figures route answering while scoped inspection uses its own path', async () => {
    const legacy = await fetch(`${base}/api/kyber/session/missing-session/turn/0`)
    const scoped = await fetch(`${base}/api/kyber/analytics/inspect`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ kind: 'session', id: 'missing-session' }),
    })
    const scopedBody = (await scoped.json()) as Record<string, unknown>

    // The legacy turn surface keeps its own not-found body; the scoped
    // wrapper carries the stable analytics envelope for the same unknown id.
    expect(legacy.status).toBe(404)
    expect(scoped.status).toBe(404)
    expect(scopedBody).toMatchObject({ contractVersion: 1, error: { code: 'not_found' } })
  })

  it('proves the new endpoints are additive: legacy GET compare never equals the scoped POST envelope', async () => {
    const legacy = await fetch(`${base}/api/kyber/compare`)
    const legacyBody = await legacy.json()
    const scoped = await fetch(`${base}/api/kyber/analytics/compare`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({}),
    })
    const scopedBody = await scoped.json()

    expect(legacy.status).toBe(200)
    expect(legacyBody).not.toEqual(scopedBody)
    expect(scopedBody).toMatchObject({ contractVersion: 1 })
  })
})

describe('an unwired analytics engine fails parity with a stable 503 (RED seam)', () => {
  function callDirect(
    method: string,
    path: string,
    service: AnalyticsService | undefined,
    chunks: string[] = [],
  ): Promise<{ status: number; body: unknown; handled: boolean }> {
    let markWritten: () => void = () => {}
    const written = new Promise<void>((resolve) => {
      markWritten = resolve
    })
    let statusCode = 200
    let raw = ''
    const res = {
      writeHead(status: number) {
        statusCode = status
      },
      end(data?: string) {
        if (data !== undefined) raw += data
        markWritten()
      },
    } as unknown as ServerResponse
    const req = new EventEmitter() as unknown as IncomingMessage
    req.method = method
    req.headers = method === 'POST' ? { 'content-type': 'application/json' } : {}
    queueMicrotask(() => {
      for (const chunk of chunks) req.emit('data', chunk)
      req.emit('end')
    })
    const handled = handleAnalyticsRequest(req, res, new URL(`http://127.0.0.1:4747${path}`), service)
    return (handled ? written : Promise.resolve()).then(() => ({
      status: statusCode,
      body: raw === '' ? undefined : JSON.parse(raw) as unknown,
      handled,
    }))
  }

  it.each(WRAPPERS)('$method $path without a service answers 503 engine_unavailable, never the engine envelope', async ({
    method,
    path,
  }) => {
    const expected = {
      contractVersion: 1,
      data: null,
      error: { code: 'engine_unavailable' },
    }
    const result = await callDirect(method, path, undefined, method === 'POST' ? ['{}'] : [])

    expect(result.handled).toBe(true)
    expect(result.status).toBe(503)
    expect(result.body).toMatchObject(expected)
  })

  it('leaves the legacy report usable while the analytics engine is absent', async () => {
    const dir = await mkdtemp(join(tmpdir(), 'kyberdash-analytics-absent-'))
    let bareHostClose: Promise<void> | undefined
    const bare = await runWebDashboard({
      port: 0,
      open: false,
      writeStdout: () => {},
      stateDir: dir,
      singleInstance: false,
      closeJobHost: (host: JobHost) => {
        bareHostClose = host.close()
        return bareHostClose
      },
    } as Parameters<typeof runWebDashboard>[0])
    try {
      const url = `http://127.0.0.1:${(bare.address() as AddressInfo).port}`
      const legacy = await fetch(`${url}/api/kyber/report`)

      expect(legacy.status).toBe(200)
    } finally {
      await stopServer(bare, () => bareHostClose)
      await removeTempStateDir(dir)
    }
  })
})
