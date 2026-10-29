import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js'
import { createAnalyticsClient } from '../mcp/client.js'
import { createAnalyticsMcpServer } from '../mcp/server.js'

/**
 * Process-signal shutdown wiring with a matching detach (F10): registers
 * named one-shot SIGTERM/SIGINT handlers that run `onShutdown`, and returns
 * a detach that removes exactly those registrations. Detaching is
 * idempotent. The named handlers matter — anonymous ones could never be
 * unregistered, leaking a listener per in-process restart.
 */
export function attachShutdownSignals(onShutdown: () => void): () => void {
  const onSigterm = (): void => {
    onShutdown()
  }
  const onSigint = (): void => {
    onShutdown()
  }
  process.once('SIGTERM', onSigterm)
  process.once('SIGINT', onSigint)
  let detached = false
  return () => {
    if (detached) return
    detached = true
    process.removeListener('SIGTERM', onSigterm)
    process.removeListener('SIGINT', onSigint)
  }
}

export async function runMcpCommand(options: { serverUrl?: string; stateDir?: string }): Promise<void> {
  const client = createAnalyticsClient({ ...(options.serverUrl === undefined ? {} : { serverUrl: options.serverUrl }), ...(options.stateDir === undefined ? {} : { stateDir: options.stateDir }) })
  const server = createAnalyticsMcpServer({ client })
  const transport = new StdioServerTransport()
  // A pending promise alone does not keep Node alive. The stdio transport uses
  // listeners on streams that may be unreferenced by a spawned CLI child, so retain
  // a tiny command-local handle until EOF, a signal, or EPIPE closes the transport.
  const keepAlive = setInterval(() => {}, 1_000)
  let closing = false
  let finish: (() => void) | undefined
  const closed = new Promise<void>((resolve) => { finish = resolve })
  const detachSignals = attachShutdownSignals(() => {
    void close().finally(() => {
      process.exitCode = 0
    })
  })
  const close = async (): Promise<void> => {
    if (closing) return
    closing = true
    detachSignals()
    try {
      await Promise.race([server.close(), new Promise<void>((resolve) => setTimeout(resolve, 2_000).unref())])
    } finally {
      clearInterval(keepAlive)
      finish?.()
    }
  }
  process.stdout.on('error', (error: NodeJS.ErrnoException) => { if (error.code === 'EPIPE') void close() })
  transport.onclose = () => { void close() }
  await server.connect(transport)
  await closed
}
