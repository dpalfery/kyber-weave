import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http'
import type { AddressInfo } from 'node:net'
import { mkdtemp, rm, writeFile, mkdir } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'
import { Client } from '@modelcontextprotocol/sdk/client/index.js'
import { InMemoryTransport } from '@modelcontextprotocol/sdk/inMemory.js'
import { createAnalyticsClient } from './client.js'
import { analyticsTools } from './tools.js'
import { createAnalyticsMcpServer } from './server.js'

type RecordedRequest = { method: string; pathname: string; body: unknown }

function envelope(data: unknown, error: { code: string; message: string; hint: string } | null = null): Record<string, unknown> {
  return {
    contractVersion: 1,
    scope: { from: '2026-10-17T00:00:00.000Z', to: '2026-10-24T00:00:00.000Z', timezone: 'UTC' },
    data,
    measurement: {},
    coverage: {},
    freshness: { generatedAt: '2026-10-24T12:00:00.000Z', instanceId: 'instance-1', projectionGeneration: 'generation-1' },
    evidence: [],
    pagination: {},
    error,
  }
}

function invalidRequest(message: string): Record<string, unknown> {
  return envelope(null, { code: 'invalid_request', message, hint: 'Send an object matching the operation catalog.' })
}

async function readJsonBody(req: IncomingMessage): Promise<unknown> {
  const chunks: Buffer[] = []
  for await (const chunk of req) chunks.push(typeof chunk === 'string' ? Buffer.from(chunk) : chunk as Buffer)
  const text = Buffer.concat(chunks).toString('utf8')
  if (text === '') return {}
  return JSON.parse(text) as unknown
}

/**
 * Temporary loopback analytics fixture. Binds 127.0.0.1 on an ephemeral port,
 * records every request, and answers the eight analytics routes with the shared
 * envelope. The inspect and search-sessions routes emulate the engine contract
 * (kind/query discriminators) so schema drift surfaces as invalid_request.
 */
async function startFixture(): Promise<{ url: string; requests: RecordedRequest[]; close: () => Promise<void> }> {
  const requests: RecordedRequest[] = []
  const server: Server = createServer((req: IncomingMessage, res: ServerResponse) => {
    void (async () => {
      const url = new URL(req.url ?? '/', 'http://127.0.0.1')
      const body = req.method === 'POST' ? await readJsonBody(req) : {}
      requests.push({ method: req.method ?? 'GET', pathname: url.pathname, body })
      if (url.pathname === '/api/kyber/analytics/search-sessions') {
        const query = (body as Record<string, unknown>)['query']
        if (typeof query !== 'string' || query.length === 0 || query.length > 200) {
          res.writeHead(400, { 'content-type': 'application/json' })
          res.end(JSON.stringify(invalidRequest('Search text must be between 1 and 200 characters.')))
          return
        }
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end(JSON.stringify(envelope({ items: [] })))
        return
      }
      if (url.pathname === '/api/kyber/analytics/inspect') {
        const raw = body as Record<string, unknown>
        if (raw['kind'] === 'session' && typeof raw['id'] === 'string') {
          res.writeHead(200, { 'content-type': 'application/json' })
          res.end(JSON.stringify(envelope({ id: raw['id'], label: 'visible label' })))
          return
        }
        if (raw['kind'] === 'turn' && raw['includeContent'] === true && typeof raw['sessionId'] === 'string' && Number.isInteger(raw['turnIndex']) && typeof raw['blockId'] === 'string') {
          res.writeHead(200, { 'content-type': 'application/json' })
          res.end(JSON.stringify(envelope({ content: { text: 'selected block', byteLength: 14 } })))
          return
        }
        res.writeHead(400, { 'content-type': 'application/json' })
        res.end(JSON.stringify(invalidRequest('Inspection requires an explicit target.')))
        return
      }
      const routes: Record<string, unknown> = {
        '/api/kyber/analytics/status': { value: { ok: true } },
        '/api/kyber/analytics/catalog': { version: 1 },
        '/api/kyber/analytics/query': { groups: [] },
        '/api/kyber/analytics/findings': { items: [] },
        '/api/kyber/analytics/compare': { deltas: {} },
        '/api/kyber/analytics/outcomes': { items: [] },
      }
      if (url.pathname in routes) {
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end(JSON.stringify(envelope(routes[url.pathname])))
        return
      }
      res.writeHead(404, { 'content-type': 'application/json' })
      res.end(JSON.stringify(invalidRequest('Unknown analytics route.')))
    })().catch(() => {
      res.writeHead(500, { 'content-type': 'application/json' })
      res.end(JSON.stringify(invalidRequest('Fixture error.')))
    })
  })
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
  const address = server.address() as AddressInfo
  return {
    url: `http://127.0.0.1:${address.port}`,
    requests,
    close: () => new Promise<void>((resolve, reject) => server.close((error?: Error) => error ? reject(error) : resolve())),
  }
}

const tempDirs: string[] = []

afterEach(async () => {
  await Promise.all(tempDirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })))
})

async function tempDir(prefix: string): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), prefix))
  tempDirs.push(dir)
  return dir
}

describe('analytics MCP transport and client', () => {
  it('publishes exactly eight read-only analytics tools through the installed SDK', async () => {
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair()
    const server = createAnalyticsMcpServer()
    const client = new Client({ name: 'kyberdash-test', version: '1.0.0' })
    await server.connect(serverTransport)
    await client.connect(clientTransport)
    try {
      const result = await client.listTools()
      expect(result.tools.map((tool) => tool.name).sort()).toEqual([
        'dash_catalog', 'dash_compare', 'dash_findings', 'dash_inspect',
        'dash_outcomes', 'dash_query', 'dash_search_sessions', 'dash_status',
      ])
      expect(analyticsTools).toHaveLength(8)
      for (const tool of result.tools) {
        expect(tool.inputSchema.type, tool.name).toBe('object')
        expect(tool.annotations, tool.name).toMatchObject({ readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false })
      }
    } finally {
      await client.close()
      await server.close()
    }
  })

  it('forwards each tool to its engine route and preserves the shared result envelope', async () => {
    const fixture = await startFixture()
    try {
      const client = createAnalyticsClient({ serverUrl: fixture.url })
      const names = ['dash_status', 'dash_catalog', 'dash_query', 'dash_search_sessions', 'dash_inspect', 'dash_findings', 'dash_compare', 'dash_outcomes']
      for (const name of names) {
        const args = name === 'dash_search_sessions' ? { query: 'session-a' } : name === 'dash_inspect' ? { kind: 'session', id: 'session-a' } : {}
        const result = await client.callTool(name, args)
        expect(result.structuredContent.contractVersion, name).toBe(1)
        expect(result.content, name).toHaveLength(1)
        expect(JSON.parse(result.content[0]?.text ?? '{}'), name).toEqual(result.structuredContent)
      }
      expect(fixture.requests.map((request) => request.pathname).sort()).toEqual([
        '/api/kyber/analytics/catalog', '/api/kyber/analytics/compare', '/api/kyber/analytics/findings',
        '/api/kyber/analytics/inspect', '/api/kyber/analytics/outcomes', '/api/kyber/analytics/query',
        '/api/kyber/analytics/search-sessions', '/api/kyber/analytics/status',
      ])
    } finally {
      await fixture.close()
    }
  })

  it('speaks the engine search contract so a scoped search reaches session data', async () => {
    const fixture = await startFixture()
    try {
      const client = createAnalyticsClient({ serverUrl: fixture.url })
      const result = await client.callTool('dash_search_sessions', { text: 'session-a' })
      const sent = fixture.requests.at(-1)?.body as Record<string, unknown>
      expect(sent, `MCP search body ${JSON.stringify(sent)} must carry the engine query field`).toHaveProperty('query', 'session-a')
      expect(result.structuredContent.error, JSON.stringify(result.structuredContent.error)).toBeNull()
    } finally {
      await fixture.close()
    }
  })

  it('speaks the engine inspect contract so a session read returns metadata', async () => {
    const fixture = await startFixture()
    try {
      const client = createAnalyticsClient({ serverUrl: fixture.url })
      const result = await client.callTool('dash_inspect', { sessionId: 'session-a' })
      const sent = fixture.requests.at(-1)?.body as Record<string, unknown>
      expect(sent, `MCP inspect body ${JSON.stringify(sent)} must carry the engine kind/id target`).toMatchObject({ kind: 'session', id: 'session-a' })
      expect(result.structuredContent.error, JSON.stringify(result.structuredContent.error)).toBeNull()
    } finally {
      await fixture.close()
    }
  })

  it('keeps transcript content opt-in behind an explicit bounded turn read', async () => {
    const fixture = await startFixture()
    try {
      const client = createAnalyticsClient({ serverUrl: fixture.url })
      const result = await client.callTool('dash_inspect', { sessionId: 'session-a', turnIndex: 2, blockId: 'block-2', includeContent: true })
      const sent = fixture.requests.at(-1)?.body as Record<string, unknown>
      expect(sent, `MCP content body ${JSON.stringify(sent)} must carry the engine turn target`).toMatchObject({ kind: 'turn', sessionId: 'session-a', turnIndex: 2, blockId: 'block-2', includeContent: true })
      expect(result.structuredContent.error, JSON.stringify(result.structuredContent.error)).toBeNull()
      expect(result.structuredContent.data, 'content stays behind the explicit opt-in').toMatchObject({ content: { text: 'selected block' } })
    } finally {
      await fixture.close()
    }
  })

  it('reports a missing engine receipt as a stable engine_unavailable error', async () => {
    const root = await tempDir('kyberdash-mcp-missing-')
    const stateDir = join(root, 'state')
    const configDir = join(root, 'config')
    await mkdir(stateDir)
    await mkdir(configDir)
    const client = createAnalyticsClient({ stateDir, configDir })
    await expect(client.callTool('dash_status', {})).rejects.toThrow('engine_unavailable')
  })

  it('ignores a stale receipt that points at a non-loopback URL', async () => {
    const root = await tempDir('kyberdash-mcp-stale-')
    const stateDir = join(root, 'state')
    const configDir = join(root, 'config')
    await mkdir(stateDir)
    await mkdir(configDir)
    await writeFile(join(stateDir, 'server.json'), JSON.stringify({ url: 'http://localhost:4747' }))
    await writeFile(join(configDir, 'server.json'), JSON.stringify({ url: 'http://127.0.0.1:4402' }))
    const fixture = await startFixture()
    try {
      await writeFile(join(configDir, 'server.json'), JSON.stringify({ url: fixture.url }))
      const client = createAnalyticsClient({ stateDir, configDir })
      const result = await client.callTool('dash_status', {})
      expect(result.structuredContent.error).toBeNull()
      expect(fixture.requests.map((request) => request.pathname).join(','), 'stale receipt must not divert the request').toContain('/api/kyber/analytics/status')
    } finally {
      await fixture.close()
    }
  })

  it('reports an incompatible engine contract as a stable error', async () => {
    const server: Server = createServer((_req: IncomingMessage, res: ServerResponse) => {
      res.writeHead(200, { 'content-type': 'application/json' })
      res.end(JSON.stringify({ contractVersion: 2, scope: {}, data: {}, measurement: {}, coverage: {}, freshness: {}, evidence: [], pagination: {}, error: null }))
    })
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
    try {
      const address = server.address() as AddressInfo
      const client = createAnalyticsClient({ serverUrl: `http://127.0.0.1:${address.port}` })
      await expect(client.callTool('dash_status', {})).rejects.toThrow('incompatible_engine')
    } finally {
      await new Promise<void>((resolve) => server.close(() => resolve()))
    }
  })

  it('refuses redirects with a stable error instead of following them', async () => {
    const server: Server = createServer((req: IncomingMessage, res: ServerResponse) => {
      if ((req.url ?? '').startsWith('/api/kyber/analytics/status')) {
        res.writeHead(307, { location: 'http://127.0.0.1:9/api/kyber/analytics/status' })
        res.end()
        return
      }
      res.writeHead(404, { 'content-type': 'application/json' })
      res.end('{}')
    })
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
    try {
      const address = server.address() as AddressInfo
      const client = createAnalyticsClient({ serverUrl: `http://127.0.0.1:${address.port}` })
      const error = await client.callTool('dash_status', {}).then(
        () => { throw new Error('expected the redirect to be refused') },
        (cause: unknown) => cause as Error,
      )
      expect(error.message, `redirect failure must carry a stable code, got: ${error.message}`).toMatch(/^(engine_unavailable|incompatible_engine|budget_exceeded|invalid_request|cancelled|internal_error):/)
    } finally {
      await new Promise<void>((resolve) => server.close(() => resolve()))
    }
  })
})
