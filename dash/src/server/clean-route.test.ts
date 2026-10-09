// `POST /api/kyber/clean` (issue #312, plan T7 RED): the web dash reaches the
// central `cleanDatabase` module over HTTP. The route validates the body
// (scope, confirmation, window), delegates to `bridge.cleanDatabase()`, and
// answers 200/400/405/409 with the repo's `{ error }` envelope. The bridge
// method does not exist yet — every test below fails until T7 lands it.
import { EventEmitter } from 'node:events'
import type { IncomingMessage, ServerResponse } from 'node:http'
import { describe, expect, it } from 'vitest'

import type { KyberBridge } from './bridge.js'
import { handleKyberRequest } from './routes.js'

type MockRes = {
  statusCode: number
  headers: Record<string, string>
  body: string
  writeHead(status: number, headers: Record<string, string>): void
  end(data?: string): void
}

function makeMockRes(): MockRes {
  const res: MockRes = {
    statusCode: 200,
    headers: {},
    body: '',
    writeHead(status: number, headers: Record<string, string>) {
      res.statusCode = status
      res.headers = { ...res.headers, ...headers }
    },
    end(data?: string) {
      if (data) res.body += data
    },
  }
  return res
}

function bridgeStub(overrides: Partial<KyberBridge> = {}): KyberBridge {
  return {
    cleanDatabase: async () => ({
      harnesses: ['pi'],
      wipe: { harnesses: ['pi'], records: 1, provenance: 1, checkpoints: 1 },
      reingested: true,
      historyWeeks: 1,
    }),
    ...overrides,
  } as unknown as KyberBridge
}

async function postClean(
  bridge: KyberBridge,
  payload: unknown,
): Promise<{ status: number; body: unknown; handled: boolean }> {
  const req = new EventEmitter() as unknown as IncomingMessage
  req.method = 'POST'
  const res = makeMockRes()
  const handled = handleKyberRequest(
    req,
    res as unknown as ServerResponse,
    new URL('http://localhost:4747/api/kyber/clean'),
    bridge,
  )
  req.emit('data', typeof payload === 'string' ? payload : JSON.stringify(payload))
  req.emit('end')
  await new Promise((resolve) => setTimeout(resolve, 50))
  return { status: res.statusCode, body: res.body === '' ? undefined : JSON.parse(res.body), handled }
}

function getClean(bridge: KyberBridge): { status: number; handled: boolean } {
  const req = { method: 'GET' } as unknown as IncomingMessage
  const res = makeMockRes()
  const handled = handleKyberRequest(
    req,
    res as unknown as ServerResponse,
    new URL('http://localhost:4747/api/kyber/clean'),
    bridge,
  )
  return { status: res.statusCode, handled }
}

describe('POST /api/kyber/clean (issue #312)', () => {
  it('cleans one harness and returns the summary', async () => {
    const seen: unknown[] = []
    const bridge = bridgeStub({
      cleanDatabase: (async (input: unknown) => {
        seen.push(input)
        return {
          harnesses: ['pi'],
          wipe: { harnesses: ['pi'], records: 3, provenance: 1, checkpoints: 1 },
          reingested: true,
          historyWeeks: 1,
        }
      }) as never,
    })

    const { status, body, handled } = await postClean(bridge, {
      harnesses: ['pi'],
      confirm: true,
    })

    expect(handled).toBe(true)
    expect(status).toBe(200)
    expect(seen).toEqual([{ harnesses: ['pi'], confirm: true }])
    expect(body).toMatchObject({ harnesses: ['pi'], reingested: true, historyWeeks: 1 })
  })

  it('cleans all and forwards an explicit window', async () => {
    const seen: unknown[] = []
    const bridge = bridgeStub({
      cleanDatabase: (async (input: unknown) => {
        seen.push(input)
        return { harnesses: ['*'], wipe: { harnesses: ['*'], records: 0, provenance: 0, checkpoints: 0 }, reingested: true, historyWeeks: 4 }
      }) as never,
    })

    const { status } = await postClean(bridge, { all: true, confirm: true, reingestWeeks: 4 })

    expect(status).toBe(200)
    expect(seen).toEqual([{ all: true, confirm: true, reingestWeeks: 4 }])
  })

  it('rejects GET with 405', () => {
    const { status, handled } = getClean(bridgeStub())
    expect(handled).toBe(true)
    expect(status).toBe(405)
  })

  it.each([
    ['missing confirmation', { harnesses: ['pi'] }],
    ['missing scope', { confirm: true }],
    ['both scopes', { all: true, harnesses: ['pi'], confirm: true }],
    ['bad window', { all: true, confirm: true, reingestWeeks: 0 }],
    ['oversized window', { all: true, confirm: true, reingestWeeks: 53 }],
    ['absurd window', { all: true, confirm: true, reingestWeeks: Number.MAX_SAFE_INTEGER }],
    ['malformed body', '{not json'],
  ])('rejects %s with 400', async (_label, payload) => {
    const { status, body } = await postClean(bridgeStub(), payload)
    expect(status).toBe(400)
    expect(body).toHaveProperty('error')
  })

  it('maps a busy lock to 409 without leaking the holder', async () => {
    const bridge = bridgeStub({
      cleanDatabase: (async () => {
        const err = new Error('a refresh or clean is already running — held by pid 4242') as Error & { code?: string }
        err.code = 'CLEAN_BUSY'
        throw err
      }) as never,
    })

    const { status, body } = await postClean(bridge, { all: true, confirm: true })

    expect(status).toBe(409)
    expect(body).toHaveProperty('error')
    expect(JSON.stringify(body)).not.toContain('4242')
  })

  it('maps a clean failure to 500 with a bounded message', async () => {
    const bridge = bridgeStub({
      cleanDatabase: (async () => {
        throw new Error('boom')
      }) as never,
    })

    const { status, body } = await postClean(bridge, { all: true, confirm: true })

    expect(status).toBe(500)
    expect(body).toEqual({ error: 'Clean failed' })
  })

  it('rejects an oversized body with 413 and never wipes (F2)', async () => {
    let calls = 0
    const bridge = bridgeStub({
      cleanDatabase: (async () => {
        calls += 1
        return {
          harnesses: ['*'],
          wipe: { harnesses: ['*'], records: 0, provenance: 0, checkpoints: 0 },
          reingested: true,
          historyWeeks: 1,
        }
      }) as never,
    })
    const req = new EventEmitter() as unknown as IncomingMessage
    let destroyed = false
    ;(req as unknown as { destroy(): void }).destroy = () => {
      destroyed = true
    }
    req.method = 'POST'
    const res = makeMockRes()
    const handled = handleKyberRequest(
      req,
      res as unknown as ServerResponse,
      new URL('http://localhost:4747/api/kyber/clean'),
      bridge,
    )
    req.emit('data', 'x'.repeat(40_000))
    req.emit('data', 'x'.repeat(40_000))
    req.emit('end')
    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(handled).toBe(true)
    expect(res.statusCode).toBe(413)
    expect(JSON.parse(res.body)).toHaveProperty('error')
    expect(calls).toBe(0)
    expect(destroyed).toBe(true)
  })

  it('answers 400 when the request stream errors without wiping (F2)', async () => {
    let calls = 0
    const bridge = bridgeStub({
      cleanDatabase: (async () => {
        calls += 1
        return {
          harnesses: ['*'],
          wipe: { harnesses: ['*'], records: 0, provenance: 0, checkpoints: 0 },
          reingested: true,
          historyWeeks: 1,
        }
      }) as never,
    })
    const req = new EventEmitter() as unknown as IncomingMessage
    req.method = 'POST'
    ;(req as unknown as { destroy(): void }).destroy = () => {}
    const res = makeMockRes()
    const handled = handleKyberRequest(
      req,
      res as unknown as ServerResponse,
      new URL('http://localhost:4747/api/kyber/clean'),
      bridge,
    )
    req.emit('error', new Error('socket hang up'))
    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(handled).toBe(true)
    expect(res.statusCode).toBe(400)
    expect(calls).toBe(0)
  })
})
