import { describe, expect, it } from 'vitest'
import { createAnalyticsClient, resolveAnalyticsServerUrl } from './client.js'
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

describe('analytics MCP engine client', () => {
  it('rejects non-literal loopback and URL components outside the endpoint contract', async () => {
    for (const url of [
      'http://localhost:4747', 'http://127.0.0.2:4747', 'https://127.0.0.1:4747',
      'http://user@127.0.0.1:4747', 'http://127.0.0.1:4747/?x=1',
      'http://127.0.0.1:4747/#x',
    ]) {
      expect(() => createAnalyticsClient({ serverUrl: url })).toThrow()
    }
  })

  it('uses the engine analytics routes and preserves the shared result envelope', async () => {
    const requests: Array<{ url: string; method: string; body?: string }> = []
    const fetchImpl: typeof fetch = async (input, init) => {
      requests.push({ url: String(input), method: init?.method ?? 'GET', body: init?.body?.toString() })
      return Response.json({ contractVersion: 1, scope: {}, data: {}, measurement: {}, coverage: {}, freshness: {}, evidence: [], pagination: {}, error: null })
    }
    const client = createAnalyticsClient({ serverUrl: 'http://127.0.0.1:4747', fetchImpl })
    for (const name of ['dash_status', 'dash_catalog', 'dash_query', 'dash_search_sessions', 'dash_inspect', 'dash_findings', 'dash_compare', 'dash_outcomes']) {
      await client.callTool(name, {})
    }
    expect(requests.map(({ url, method }) => [new URL(url).pathname, method])).toEqual([
      ['/api/kyber/analytics/status', 'GET'], ['/api/kyber/analytics/catalog', 'GET'],
      ['/api/kyber/analytics/query', 'POST'], ['/api/kyber/analytics/search-sessions', 'POST'],
      ['/api/kyber/analytics/inspect', 'POST'], ['/api/kyber/analytics/findings', 'POST'],
      ['/api/kyber/analytics/compare', 'POST'], ['/api/kyber/analytics/outcomes', 'POST'],
    ])
    const result = await client.callTool('dash_query', { windowDays: 7, limit: 25 })
    expect(result.structuredContent).toMatchObject({
      contractVersion: 1,
      scope: expect.any(Object),
      data: expect.any(Object),
      measurement: expect.any(Object),
      coverage: expect.any(Object),
      freshness: expect.any(Object),
      evidence: expect.any(Array),
      pagination: expect.any(Object),
      error: null,
    })
  })

  it('prefers the explicit URL and discovers the selected state directory before standard config', async () => {
    const root = await mkdtemp(join(tmpdir(), 'kyberdash-mcp-discovery-'))
    try {
      const state = join(root, 'state')
      const config = join(root, 'config')
      await mkdir(state)
      await mkdir(config)
      await writeFile(join(state, 'server.json'), JSON.stringify({ url: 'http://127.0.0.1:4401' }))
      await writeFile(join(config, 'server.json'), JSON.stringify({ url: 'http://127.0.0.1:4402' }))
      expect(await resolveAnalyticsServerUrl({ serverUrl: 'http://127.0.0.1:4400', stateDir: state, configDir: config })).toBe('http://127.0.0.1:4400')
      expect(await resolveAnalyticsServerUrl({ stateDir: state, configDir: config })).toBe('http://127.0.0.1:4401')
      await rm(join(state, 'server.json'))
      expect(await resolveAnalyticsServerUrl({ stateDir: state, configDir: config })).toBe('http://127.0.0.1:4402')
    } finally {
      await rm(root, { recursive: true, force: true })
    }
  })

  it('surfaces domain error envelopes as tool errors and propagates cancellation to fetch', async () => {
    let observedSignal: AbortSignal | undefined
    const client = createAnalyticsClient({
      serverUrl: 'http://127.0.0.1:4747',
      fetchImpl: async (_input, init) => {
        observedSignal = init?.signal ?? undefined
        return Response.json({ contractVersion: 1, scope: {}, data: null, measurement: {}, coverage: {}, freshness: {}, evidence: [], pagination: {}, error: { code: 'not_measurable', message: 'No denominator' } })
      },
    })
    const controller = new AbortController()
    const result = await client.callTool('dash_query', {}, { signal: controller.signal })
    expect(result.isError).toBe(true)
    expect(result.structuredContent).toMatchObject({ error: { code: 'not_measurable' } })
    expect(observedSignal?.aborted).toBe(false)
    controller.abort()
    expect(observedSignal?.aborted).toBe(true)
  })

  it('translates windowDays into the engine scope window for every tool that advertises it', async () => {
    const bodies: Record<string, unknown>[] = []
    const fetchImpl: typeof fetch = async (_input, init) => {
      bodies.push(JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>)
      return Response.json({ contractVersion: 1, scope: {}, data: {}, measurement: {}, coverage: {}, freshness: {}, evidence: [], pagination: {}, error: null })
    }
    const client = createAnalyticsClient({
      serverUrl: 'http://127.0.0.1:4747',
      fetchImpl,
      now: () => new Date('2026-10-25T12:00:00.000Z'),
    })
    await client.callTool('dash_search_sessions', { text: 'session-a', windowDays: 30 })
    await client.callTool('dash_findings', { windowDays: 30 })
    await client.callTool('dash_outcomes', { windowDays: 30 })
    await client.callTool('dash_query', { windowDays: 30, limit: 25 })

    expect(bodies).toHaveLength(4)
    for (const body of bodies) {
      expect(body).not.toHaveProperty('windowDays')
      expect(body).toMatchObject({ scope: { from: '2026-09-25T12:00:00.000Z', to: '2026-10-25T12:00:00.000Z' } })
    }
    expect(bodies[0]).toMatchObject({ query: 'session-a' })
    expect(bodies[3]).toMatchObject({ limit: 25, catalogVersion: 1 })
  })

  it('lets an explicit scope range win over windowDays instead of overwriting it', async () => {
    const bodies: Record<string, unknown>[] = []
    const fetchImpl: typeof fetch = async (_input, init) => {
      bodies.push(JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>)
      return Response.json({ contractVersion: 1, scope: {}, data: {}, measurement: {}, coverage: {}, freshness: {}, evidence: [], pagination: {}, error: null })
    }
    const client = createAnalyticsClient({
      serverUrl: 'http://127.0.0.1:4747',
      fetchImpl,
      now: () => new Date('2026-10-25T12:00:00.000Z'),
    })
    await client.callTool('dash_findings', {
      windowDays: 30,
      scope: { from: '2026-10-01T00:00:00.000Z', to: '2026-10-02T00:00:00.000Z' },
    })

    expect(bodies).toHaveLength(1)
    expect(bodies[0]).not.toHaveProperty('windowDays')
    expect(bodies[0]).toMatchObject({ scope: { from: '2026-10-01T00:00:00.000Z', to: '2026-10-02T00:00:00.000Z' } })
  })
})
