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
  await serveMcpConnection({ server, transport })
}

/** A clock seam for the stdio keep-alive, so tests can prove it is cleared. */
export type McpClock = {
  setInterval(handler: (...args: never[]) => void, ms: number): unknown
  clearInterval(id: unknown): void
}

/**
 * The stdio MCP connection lifecycle (F10): the keep-alive, the signal
 * handlers, the EPIPE guard, and EOF all funnel into one `close`, and a
 * `connect` failure cleans up everything `close` would have — a rejected
 * startup must not leave the keep-alive ticking or the process handlers
 * registered with nobody left to call close. Transport-typed generically so
 * tests can drive failure, EOF, and close with fakes over the real code.
 */
export async function serveMcpConnection<TTransport extends { onclose?: (() => void) | undefined }>(deps: {
  server: { connect(transport: TTransport): Promise<void>; close(): Promise<void> }
  transport: TTransport
  clock?: McpClock
}): Promise<void> {
  const { server, transport } = deps
  const clock: McpClock = deps.clock ?? {
    setInterval: (handler, ms) => setInterval(handler, ms),
    clearInterval: (id) => clearInterval(id as NodeJS.Timeout),
  }
  // A pending promise alone does not keep Node alive. The stdio transport uses
  // listeners on streams that may be unreferenced by a spawned CLI child, so retain
  // a tiny command-local handle until EOF, a signal, or EPIPE closes the transport.
  const keepAlive = clock.setInterval(() => {}, 1_000)
  let closing = false
  let finish: (() => void) | undefined
  const closed = new Promise<void>((resolve) => { finish = resolve })
  const detachSignals = attachShutdownSignals(() => {
    void close().finally(() => {
      process.exitCode = 0
    })
  })
  // Named so `close()` (and the connect-failure path below) removes exactly
  // this registration (F10): the anonymous EPIPE guard leaked one stdout
  // listener per in-process restart.
  const onStdoutError = (error: NodeJS.ErrnoException): void => {
    if (error.code === 'EPIPE') void close()
  }
  const close = async (): Promise<void> => {
    if (closing) return
    closing = true
    detachSignals()
    process.stdout.removeListener('error', onStdoutError)
    try {
      await Promise.race([server.close(), new Promise<void>((resolve) => setTimeout(resolve, 2_000).unref())])
    } finally {
      clock.clearInterval(keepAlive)
      finish?.()
    }
  }
  process.stdout.on('error', onStdoutError)
  transport.onclose = () => { void close() }
  try {
    await server.connect(transport)
  } catch (error) {
    // Startup failed before anything could call close: release the
    // keep-alive, the signal handlers, and the stdout guard here, or the
    // failed start leaks all three with no owner left to clean up.
    detachSignals()
    process.stdout.removeListener('error', onStdoutError)
    clock.clearInterval(keepAlive)
    throw error
  }
  await closed
}
