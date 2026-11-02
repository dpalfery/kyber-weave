import { Client } from '@modelcontextprotocol/sdk/client/index.js'
import { InMemoryTransport } from '@modelcontextprotocol/sdk/inMemory.js'
import { describe, expect, it, vi } from 'vitest'
import { createAnalyticsClient } from './client.js'
import { createAnalyticsMcpServer } from './server.js'

const contentRequest = {
  mode: 'content',
  target: { kind: 'turn', sessionId: 'session-a', turnIndex: 2 },
  representation: 'captured_parts',
  byteLimit: 16_384,
  cursor: 'opaque-page-cursor',
}

const contextEnvelope = {
  contractVersion: 1 as const,
  scope: {},
  data: {
    target: contentRequest.target,
    representation: 'captured_parts',
    chunks: [{
      partId: 'part-opaque',
      blockId: 'block-opaque',
      sessionId: 'session-a',
      turnIndex: 2,
      spanId: 'span-model-input',
      origin: 'turn',
      text: '雪🙂é'.repeat(1_800),
      byteOffset: 0,
      complete: true,
      role: 'user',
      order: 0,
      resident: true,
    }],
  },
  measurement: {},
  coverage: { completeness: 'captured_only', gaps: [{ code: 'not_captured', reason: 'A provider response was not retained.' }] },
  freshness: {},
  evidence: [],
  pagination: { nextCursor: null, hasMore: false },
  error: null,
}

function asRecord(value: unknown): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new Error('Expected MCP structured content to be an object.')
  }
  return value as Record<string, unknown>
}

describe('dash_inspect MCP complete-context contract', () => {
  it('publishes the new manifest/content request on the existing tool and rejects injected fields', async () => {
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair()
    const server = createAnalyticsMcpServer()
    const client = new Client({ name: 'context-contract-test', version: '1.0.0' })
    await server.connect(serverTransport)
    await client.connect(clientTransport)
    try {
      const listed = await client.listTools()
      const inspect = listed.tools.find((tool) => tool.name === 'dash_inspect')
      expect(inspect).toBeDefined()
      expect(listed.tools.filter((tool) => tool.name.startsWith('dash_'))).toHaveLength(8)
      expect(inspect?.inputSchema.properties).toMatchObject({
        mode: expect.objectContaining({ enum: ['manifest', 'content'] }),
        target: expect.any(Object),
        representation: expect.any(Object),
        byteLimit: expect.any(Object),
        cursor: expect.any(Object),
      })
      expect(inspect?.inputSchema.additionalProperties).toBe(false)
      expect(inspect?.inputSchema.properties).not.toHaveProperty('includeContent')
      expect(inspect?.inputSchema.properties).not.toHaveProperty('fullSession')
    } finally {
      await client.close()
      await server.close()
    }
  })

  it('forwards the explicit request unchanged and preserves text, source gaps and pagination', async () => {
    const requests: unknown[] = []
    const fetchImpl: typeof fetch = async (_input, init) => {
      const request = JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>
      requests.push(request)
      if (request['mode'] === 'manifest') {
        return Response.json({
          ...contextEnvelope,
          data: { target: request['target'], representation: request['representation'] ?? 'captured_parts', items: [{ partId: 'part-opaque', blockId: 'block-opaque', origin: 'turn', role: 'user' }] },
        })
      }
      return Response.json(contextEnvelope)
    }
    const analyticsClient = createAnalyticsClient({ serverUrl: 'http://127.0.0.1:4747', fetchImpl })
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair()
    const server = createAnalyticsMcpServer({ client: analyticsClient })
    const client = new Client({ name: 'context-content-test', version: '1.0.0' })
    await server.connect(serverTransport)
    await client.connect(clientTransport)
    try {
      const result = await client.callTool({ name: 'dash_inspect', arguments: contentRequest })
      expect(result.isError).not.toBe(true)
      expect(requests).toEqual([contentRequest])
      const content = asRecord(result.structuredContent)
      expect(content).toEqual(contextEnvelope)
      expect(JSON.stringify(result)).toContain('雪🙂é'.repeat(1_800))
      expect(JSON.stringify(result)).toContain('not_captured')
      expect(asRecord(content['pagination'])).toMatchObject({ nextCursor: null, hasMore: false })

      const manifestRequest = { mode: 'manifest', target: { kind: 'turn', sessionId: 'session-a', turnIndex: 2 } }
      const manifest = await client.callTool({ name: 'dash_inspect', arguments: manifestRequest })
      expect(manifest.isError).not.toBe(true)
      expect(requests).toEqual([contentRequest, manifestRequest])
      expect(asRecord(manifest.structuredContent)).toMatchObject({ data: { items: [{ partId: 'part-opaque', role: 'user' }] } })
    } finally {
      await client.close()
      await server.close()
    }
  })

  it('keeps the historical session inspection argument valid', async () => {
    const fetchImpl = vi.fn<typeof fetch>(async () => Response.json({ ...contextEnvelope, data: { id: 'session-a', label: 'legacy session' } }))
    const analyticsClient = createAnalyticsClient({ serverUrl: 'http://127.0.0.1:4747', fetchImpl })
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair()
    const server = createAnalyticsMcpServer({ client: analyticsClient })
    const client = new Client({ name: 'legacy-inspect-test', version: '1.0.0' })
    await server.connect(serverTransport)
    await client.connect(clientTransport)
    try {
      const result = await client.callTool({ name: 'dash_inspect', arguments: { sessionId: 'session-a' } })
      expect(fetchImpl).toHaveBeenCalledTimes(1)
      expect(asRecord(result.structuredContent)).toMatchObject({ data: { id: 'session-a', label: 'legacy session' } })
    } finally {
      await client.close()
      await server.close()
    }
  })

  // The registered wire schema is the plain object, because the SDK turns a
  // refined schema into an empty JSON Schema. Cross-field rules therefore run
  // in the handler, and this pins that they still reject bad arguments before
  // anything reaches the analytics server.
  it('rejects cross-field violations the registered plain shape cannot express', async () => {
    const fetchImpl = vi.fn<typeof fetch>(async () => Response.json(contextEnvelope))
    const analyticsClient = createAnalyticsClient({ serverUrl: 'http://127.0.0.1:4747', fetchImpl })
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair()
    const server = createAnalyticsMcpServer({ client: analyticsClient })
    const client = new Client({ name: 'inspect-refinement-test', version: '1.0.0' })
    await server.connect(serverTransport)
    await client.connect(clientTransport)
    try {
      const missingTarget = await client.callTool({ name: 'dash_inspect', arguments: { mode: 'content' } })
      expect(missingTarget.isError).toBe(true)
      expect(JSON.stringify(missingTarget.content)).toContain('mode and target must be supplied together.')

      const assembledRun = await client.callTool({
        name: 'dash_inspect',
        arguments: { mode: 'content', target: { kind: 'run', runId: 'run-a' }, representation: 'assembled_turn' },
      })
      expect(assembledRun.isError).toBe(true)
      expect(JSON.stringify(assembledRun.content)).toContain('assembled_turn is available only for a turn target.')

      expect(fetchImpl).not.toHaveBeenCalled()
    } finally {
      await client.close()
      await server.close()
    }
  })
})
