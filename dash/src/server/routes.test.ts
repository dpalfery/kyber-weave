import type { IncomingMessage, ServerResponse } from 'node:http'
import { describe, expect, it, vi } from 'vitest'

import type { KyberBridge, ProblemRow, QuarantineRow } from './bridge.js'
import { handleKyberRequest } from './routes.js'

interface PaginatedResponseBody {
  total?: number
  data?: unknown[]
  entries?: QuarantineRow[]
  problems?: ProblemRow[]
  [key: string]: unknown
}

describe('routes pagination', () => {
  const dummyQuarantine: QuarantineRow[] = [
    {
      span_id: 'quar-1',
      source: 'copilot',
      name: 'turn-1',
      namespaces: 'gen_ai',
      reason: 'unmapped namespace',
      seen_at: 100,
    },
    {
      span_id: 'quar-2',
      source: 'copilot',
      name: 'turn-2',
      namespaces: 'gen_ai',
      reason: 'unmapped namespace',
      seen_at: 200,
    },
    {
      span_id: 'quar-3',
      source: 'gemini',
      name: 'turn-3',
      namespaces: 'gen_ai',
      reason: 'unmapped namespace',
      seen_at: 300,
    },
  ]

  const dummyProblems: ProblemRow[] = [
    {
      id: 1,
      span_id: 'prob-1',
      session_id: 'sess-1',
      severity: 'error',
      code: 'ERR_1',
      message: 'Problem 1',
      at: 100,
      timestamp: '2026-09-30T10:00:00.000Z',
      harness: 'copilot',
    },
    {
      id: 2,
      span_id: 'prob-2',
      session_id: 'sess-2',
      severity: 'warning',
      code: 'WARN_2',
      message: 'Problem 2',
      at: 200,
      timestamp: '2026-09-30T10:01:00.000Z',
      harness: 'gemini',
    },
    {
      id: 3,
      span_id: 'prob-3',
      session_id: 'sess-3',
      severity: 'error',
      code: 'ERR_3',
      message: 'Problem 3',
      at: 300,
      timestamp: '2026-09-30T10:02:00.000Z',
      harness: 'copilot',
    },
  ]

  function createMockBridge() {
    const getQuarantine = vi.fn((limit = 200, offset = 0) =>
      dummyQuarantine.slice(offset, offset + limit),
    )
    const getQuarantineCount = vi.fn(() => dummyQuarantine.length)
    const getProblems = vi.fn((limit = 200, offset = 0) =>
      dummyProblems.slice(offset, offset + limit),
    )
    const getProblemCount = vi.fn(() => dummyProblems.length)

    return {
      getQuarantine,
      getQuarantineCount,
      getProblems,
      getProblemCount,
    } as unknown as KyberBridge & {
      getQuarantine: ReturnType<typeof vi.fn>
      getQuarantineCount: ReturnType<typeof vi.fn>
      getProblems: ReturnType<typeof vi.fn>
      getProblemCount: ReturnType<typeof vi.fn>
    }
  }

  function call(
    href: string,
    bridge: KyberBridge,
    method = 'GET',
  ): { status: number; body: PaginatedResponseBody; handled: boolean } {
    let status = 0
    let body = ''
    const req = { method } as IncomingMessage
    const res = {
      writeHead: (code: number) => {
        status = code
      },
      end: (data: string) => {
        body = data
      },
    } as unknown as ServerResponse
    const handled = handleKyberRequest(
      req,
      res,
      new URL(href, 'http://127.0.0.1:4747'),
      bridge,
    )
    return {
      status,
      body: body === '' ? {} : (JSON.parse(body) as PaginatedResponseBody),
      handled,
    }
  }

  describe('GET /api/kyber/quarantine pagination', () => {
    it('parses page and limit query parameters, passing calculated offset to bridge.getQuarantine', () => {
      const bridge = createMockBridge()
      const { status, handled } = call(
        '/api/kyber/quarantine?page=2&limit=1',
        bridge,
      )

      expect(handled).toBe(true)
      expect(status).toBe(200)
      expect(bridge.getQuarantine).toHaveBeenCalledWith(1, 1)
    })

    it('returns paginated data and total count in response JSON', () => {
      const bridge = createMockBridge()
      const { body } = call('/api/kyber/quarantine?page=1&limit=2', bridge)

      expect(body.total).toBe(3)
      const items = (body.data ?? body.entries) as QuarantineRow[]
      expect(items).toBeDefined()
      expect(items).toHaveLength(2)
    })

    it('returns the 2nd item and does not return the 1st item when requested with page=2&limit=1', () => {
      const bridge = createMockBridge()
      const { body } = call('/api/kyber/quarantine?page=2&limit=1', bridge)

      const items = (body.data ?? body.entries) as QuarantineRow[]
      expect(items).toHaveLength(1)
      expect(items[0].span_id).toBe('quar-2')
      expect(items.some((i) => i.span_id === 'quar-1')).toBe(false)
    })

    it('parses direct offset and limit query parameters if provided', () => {
      const bridge = createMockBridge()
      const { body } = call('/api/kyber/quarantine?offset=2&limit=1', bridge)

      expect(bridge.getQuarantine).toHaveBeenCalledWith(1, 2)
      const items = (body.data ?? body.entries) as QuarantineRow[]
      expect(items).toHaveLength(1)
      expect(items[0].span_id).toBe('quar-3')
    })

    it('defaults limit to 200 and offset to 0 when pagination parameters are omitted', () => {
      const bridge = createMockBridge()
      const { body } = call('/api/kyber/quarantine', bridge)

      expect(bridge.getQuarantine).toHaveBeenCalledWith(200, 0)
      expect(body.total).toBe(3)
      const items = (body.data ?? body.entries) as QuarantineRow[]
      expect(items).toHaveLength(3)
    })
  })

  describe('GET /api/kyber/problems pagination', () => {
    it('parses page and limit query parameters, passing calculated offset to bridge.getProblems', () => {
      const bridge = createMockBridge()
      const { status, handled } = call(
        '/api/kyber/problems?page=2&limit=1',
        bridge,
      )

      expect(handled).toBe(true)
      expect(status).toBe(200)
      expect(bridge.getProblems).toHaveBeenCalledWith(1, 1)
    })

    it('returns paginated data and total count in response JSON', () => {
      const bridge = createMockBridge()
      const { body } = call('/api/kyber/problems?page=1&limit=2', bridge)

      expect(body.total).toBe(3)
      const items = (body.data ?? body.problems) as ProblemRow[]
      expect(items).toBeDefined()
      expect(items).toHaveLength(2)
    })

    it('returns the 2nd item and does not return the 1st item when requested with page=2&limit=1', () => {
      const bridge = createMockBridge()
      const { body } = call('/api/kyber/problems?page=2&limit=1', bridge)

      const items = (body.data ?? body.problems) as ProblemRow[]
      expect(items).toHaveLength(1)
      expect(items[0].span_id).toBe('prob-2')
      expect(items.some((i) => i.span_id === 'prob-1')).toBe(false)
    })

    it('parses direct offset and limit query parameters if provided', () => {
      const bridge = createMockBridge()
      const { body } = call('/api/kyber/problems?offset=2&limit=1', bridge)

      expect(bridge.getProblems).toHaveBeenCalledWith(1, 2)
      const items = (body.data ?? body.problems) as ProblemRow[]
      expect(items).toHaveLength(1)
      expect(items[0].span_id).toBe('prob-3')
    })

    it('defaults limit to 200 and offset to 0 when pagination parameters are omitted', () => {
      const bridge = createMockBridge()
      const { body } = call('/api/kyber/problems', bridge)

      expect(bridge.getProblems).toHaveBeenCalledWith(200, 0)
      expect(body.total).toBe(3)
      const items = (body.data ?? body.problems) as ProblemRow[]
      expect(items).toHaveLength(3)
    })
  })
})
