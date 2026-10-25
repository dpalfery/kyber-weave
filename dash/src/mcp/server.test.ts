import { describe, expect, it } from 'vitest'
import { Client } from '@modelcontextprotocol/sdk/client/index.js'
import { InMemoryTransport } from '@modelcontextprotocol/sdk/inMemory.js'
import { createAnalyticsMcpServer } from './server.js'

describe('analytics MCP server', () => {
  it('publishes the eight read-only analytics tools through the installed SDK', async () => {
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair()
    const server = createAnalyticsMcpServer()
    const client = new Client({ name: 'kyberdash-test', version: '1.0.0' })
    await server.connect(serverTransport)
    await client.connect(clientTransport)
    try {
      const result = await client.listTools()
      expect(result.tools.map(tool => tool.name).sort()).toEqual([
        'dash_catalog', 'dash_compare', 'dash_findings', 'dash_inspect',
        'dash_outcomes', 'dash_query', 'dash_search_sessions', 'dash_status',
      ])
      for (const tool of result.tools) {
        expect(tool.inputSchema.type).toBe('object')
        expect(tool.inputSchema.additionalProperties).toBe(false)
        expect(tool.annotations).toMatchObject({ readOnlyHint: true, destructiveHint: false })
        expect(tool.annotations).toMatchObject({ idempotentHint: true, openWorldHint: false })
      }
      const query = result.tools.find(tool => tool.name === 'dash_query')
      expect(query?.inputSchema.properties).toHaveProperty('windowDays')
      expect(query?.inputSchema.properties).toHaveProperty('metrics')
      expect(query?.inputSchema.properties).toHaveProperty('groups')
      expect(query?.inputSchema.properties).not.toHaveProperty('url')
    } finally {
      await client.close()
      await server.close()
    }
  })
})
