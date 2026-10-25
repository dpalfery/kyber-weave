import type { Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest'
import type { AnalyticsEnvelope } from '../analysis/query/service.js'
import { runWebDashboard } from '../cli/web.js'
import type { JobHost } from '../jobs/host.js'
import type { AnalyticsService } from './analytics-routes.js'
import { stopServer } from './testing.js'

const inspectRequest = {
  mode: 'content',
  target: { kind: 'turn', sessionId: 'session-a', turnIndex: 2 },
  representation: 'captured_parts',
  byteLimit: 16_384,
  cursor: 'opaque-page-cursor',
}

const expected: AnalyticsEnvelope = {
  contractVersion: 1,
  scope: {},
  data: {
    groups: [],
    items: [],
    deltas: {},
    content: {},
    target: inspectRequest.target,
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

function sessionResult(): AnalyticsEnvelope {
  return { ...expected, data: { groups: [], items: [], deltas: {}, content: {}, id: 'session-a', label: 'legacy session metadata' } }
}

describe('analytics HTTP complete-context parity', () => {
  let server: Server
  let base: string
  let execute: ReturnType<typeof vi.fn<AnalyticsService['execute']>>
  let hostClose: Promise<void> | undefined

  beforeAll(async () => {
    const service = {
      execute: vi.fn<AnalyticsService['execute']>(async () => expected),
    } satisfies AnalyticsService
    execute = service.execute
    server = await runWebDashboard({
      port: 0,
      open: false,
      writeStdout: () => {},
      stateDir: undefined,
      singleInstance: false,
      analyticsService: service,
      closeJobHost: (host: JobHost) => {
        hostClose = host.close()
        return hostClose
      },
    } as Parameters<typeof runWebDashboard>[0])
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  })

  afterAll(async () => {
    await stopServer(server, () => hostClose)
  })

  it('forwards the new request and returns the exact bounded context envelope', async () => {
    const response = await fetch(`${base}/api/kyber/analytics/inspect`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(inspectRequest),
    })
    const actual = await response.json()

    expect(execute).toHaveBeenCalledWith('inspect', inspectRequest, expect.objectContaining({ signal: expect.any(AbortSignal) }))
    expect(response.status).toBe(200)
    expect(Buffer.byteLength(JSON.stringify(actual), 'utf8')).toBeLessThanOrEqual(65_536)
    expect(actual).toEqual(expected)
    expect(JSON.stringify(actual)).toContain('not_captured')
    const actualChunks = (actual as AnalyticsEnvelope).data?.['chunks']
    expect(Array.isArray(actualChunks)).toBe(true)
    const firstChunk = Array.isArray(actualChunks) ? actualChunks[0] : undefined
    expect(typeof firstChunk).toBe('object')
    if (typeof firstChunk === 'object' && firstChunk !== null) {
      expect(Buffer.byteLength(String(firstChunk['text']), 'utf8')).toBeLessThanOrEqual(16_384)
    }
  })

  it('preserves the legacy metadata-only request and lets engine validation reject injected fields', async () => {
    execute.mockResolvedValueOnce(sessionResult())
    const legacyRequest = { sessionId: 'session-a' }
    const legacy = await fetch(`${base}/api/kyber/analytics/inspect`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(legacyRequest),
    })
    expect(await legacy.json()).toEqual(sessionResult())
    expect(execute).toHaveBeenLastCalledWith('inspect', legacyRequest, expect.objectContaining({ signal: expect.any(AbortSignal) }))

    const rejected: AnalyticsEnvelope = {
      ...expected,
      data: null,
      error: { code: 'invalid_request', message: 'Unknown inspection field.', hint: 'Use the documented inspection request.' },
    }
    execute.mockResolvedValueOnce(rejected)
    const invalidRequest = { ...inspectRequest, fullSession: true }
    const invalid = await fetch(`${base}/api/kyber/analytics/inspect`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(invalidRequest),
    })
    expect(execute).toHaveBeenLastCalledWith('inspect', invalidRequest, expect.objectContaining({ signal: expect.any(AbortSignal) }))
    expect(invalid.status).toBe(400)
    expect(await invalid.json()).toEqual(rejected)
  })
})
