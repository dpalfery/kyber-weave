// F10 regression: the MCP stdio lifecycle leaked one stdout `error`
// listener per in-process restart (anonymous EPIPE guard, never removed),
// and a `server.connect` rejection skipped `close()` entirely — leaving the
// keep-alive ticking and the signal handlers registered with no owner left
// to clean up. These tests drive failure, EOF, and close through fakes over
// the real `serveMcpConnection`, pinning exact listener/timer cleanup.

import { describe, expect, it } from 'vitest'

import { serveMcpConnection, type McpClock } from './mcp.js'

type FakeTransport = {
  onclose?: (() => void) | undefined
}

function fakeClock(): McpClock & { cleared: unknown[]; handles: unknown[] } {
  const cleared: unknown[] = []
  const handles: unknown[] = []
  let next = 0
  return {
    cleared,
    handles,
    setInterval: (_handler: (...args: never[]) => void, _ms: number): unknown => {
      next += 1
      handles.push(next)
      return next
    },
    clearInterval: (id: unknown): void => {
      cleared.push(id)
    },
  }
}

function signalCounts(): Record<'SIGINT' | 'SIGTERM', number> {
  return {
    SIGINT: process.listenerCount('SIGINT'),
    SIGTERM: process.listenerCount('SIGTERM'),
  }
}

describe('F10 MCP connection lifecycle', () => {
  it('cleans up signals, stdout guard, and keep-alive when connect fails', async () => {
    const clock = fakeClock()
    const beforeSignals = signalCounts()
    const beforeStdoutErrors = process.stdout.listenerCount('error')
    const transport: FakeTransport = {}
    let serverClosed = false

    await expect(
      serveMcpConnection({
        server: {
          connect: async () => {
            throw new Error('simulated stdio connect failure')
          },
          close: async () => {
            serverClosed = true
          },
        },
        transport,
        clock,
      }),
    ).rejects.toThrow(/simulated stdio connect failure/)

    // Nothing to close over: the failure path released everything itself.
    expect(serverClosed).toBe(false)
    expect(signalCounts()).toEqual(beforeSignals)
    expect(process.stdout.listenerCount('error')).toBe(beforeStdoutErrors)
    expect(clock.cleared).toEqual(clock.handles)
    expect(clock.handles).toHaveLength(1)
  })

  it('closes once on EOF and releases every registration it added', async () => {
    const clock = fakeClock()
    const beforeSignals = signalCounts()
    const beforeStdoutErrors = process.stdout.listenerCount('error')
    const transport: FakeTransport = {}
    let serverCloses = 0

    const served = serveMcpConnection({
      server: {
        connect: async () => {},
        close: async () => {
          serverCloses += 1
        },
      },
      transport,
      clock,
    })
    // The connection holds its registrations while open.
    expect(process.listenerCount('SIGINT')).toBe(beforeSignals.SIGINT + 1)
    expect(process.listenerCount('SIGTERM')).toBe(beforeSignals.SIGTERM + 1)
    expect(process.stdout.listenerCount('error')).toBe(beforeStdoutErrors + 1)

    // EOF: the transport fires onclose, close runs once, serve resolves.
    transport.onclose?.()
    await served

    expect(serverCloses).toBe(1)
    expect(signalCounts()).toEqual(beforeSignals)
    expect(process.stdout.listenerCount('error')).toBe(beforeStdoutErrors)
    expect(clock.cleared).toEqual(clock.handles)
  })

  it('tolerates a second EOF after close resolved', async () => {
    const clock = fakeClock()
    const transport: FakeTransport = {}
    let serverCloses = 0
    const served = serveMcpConnection({
      server: {
        connect: async () => {},
        close: async () => {
          serverCloses += 1
        },
      },
      transport,
      clock,
    })
    transport.onclose?.()
    await served
    transport.onclose?.()
    await served
    expect(serverCloses).toBe(1)
  })
})
