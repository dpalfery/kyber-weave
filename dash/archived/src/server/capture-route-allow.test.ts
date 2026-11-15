// The 405 names the methods the capture route does accept (RFC 9110 requires an Allow header).
// A separate file: capture-route.test.ts is a pinned contract and is not edited.

import type { IncomingMessage, ServerResponse } from 'node:http'
import { describe, expect, it, vi } from 'vitest'

import { handleCaptureRequest } from './capture-routes.js'
import type { KyberRouteDeps } from './routes.js'

describe('capture route 405', () => {
  it('sends Allow: GET, POST', () => {
    const sendJson = vi.fn()
    const handled = handleCaptureRequest(
      { method: 'PUT' } as IncomingMessage,
      {} as ServerResponse,
      new URL('http://localhost:4747/api/kyber/capture'),
      { store: {} } as unknown as KyberRouteDeps,
      { sendJson } as never,
    )
    expect(handled).toBe(true)
    expect(sendJson).toHaveBeenCalledWith(expect.anything(), 405, { error: 'Method Not Allowed' }, { Allow: 'GET, POST' })
  })
})
