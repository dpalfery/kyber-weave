import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'
import { createAnalyticsClient } from './client.js'
import { analyticsTools } from './tools.js'

type AnalyticsClient = ReturnType<typeof createAnalyticsClient>

export function createAnalyticsMcpServer(options: { client?: AnalyticsClient } = {}): McpServer {
  const client = options.client ?? createAnalyticsClient()
  const server = new McpServer({ name: 'kyberdash-analytics', version: '1.0.0' })
  for (const tool of analyticsTools) {
    server.registerTool(tool.name, {
      description: tool.description,
      inputSchema: tool.inputSchema,
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    }, async (args, extra) => {
      // Enforce refinements (e.g. dash_inspect mode/target pairing) that the
      // registered plain shape cannot express; a throw becomes an MCP error.
      (tool.validationSchema ?? tool.inputSchema).parse(args)
      return client.callTool(tool.name, args, { signal: extra.signal })
    })
  }
  return server
}
