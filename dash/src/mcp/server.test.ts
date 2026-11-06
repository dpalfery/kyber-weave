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
        'dash_compare_metrics_between_two_scopes', 'dash_get_run_success_or_failure_outcomes', 'dash_list_available_analytics_operations', 'dash_list_detected_context_and_tool_problems',
        'dash_list_queryable_metrics_dimensions_limits', 'dash_query_session_cost_tokens_and_duration', 'dash_read_captured_run_session_or_turn_context', 'dash_search_sessions_by_text_and_scope',
      ])
      for (const tool of result.tools) {
        expect(tool.inputSchema.type).toBe('object')
        expect(tool.inputSchema.additionalProperties).toBe(false)
        expect(tool.annotations).toMatchObject({ readOnlyHint: true, destructiveHint: false })
        expect(tool.annotations).toMatchObject({ idempotentHint: true, openWorldHint: false })
      }
      const query = result.tools.find(tool => tool.name === 'dash_query_session_cost_tokens_and_duration')
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
