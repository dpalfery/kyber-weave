import type { AddressInfo } from 'net'
import type { Server } from 'http'
import { tmpdir } from 'os'
import { join } from 'path'
import { DatabaseSync } from 'node:sqlite'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'

import { runWebDashboard } from '../src/web-dashboard.js'
import { KyberBridge } from '../kyber/server/bridge.js'
import { CanonStore } from '../kyber/canon/store.js'
import type { CanonicalRecord } from '../kyber/canon/types.js'

// Timeline nodes used to carry each span's attribute map inline, which made the
// derived session payload a second, uncompressed copy of the whole corpus —
// 266 MB of one 264 MB payload, and nearly all of the 995 MB the session table
// held. The map still has to be reachable (R9.2), so the inspector asks for the
// one span it is showing and pays a primary-key seek and an inflate for it.
describe('GET /api/kyber/span/:spanId/attributes', () => {
  let server: Server
  let base: string
  let store: CanonStore

  const ATTRIBUTES = {
    'gen_ai.operation.name': 'chat',
    'gen_ai.request.model': 'claude-opus-5',
    'gen_ai.system': 'anthropic',
    'kyber.prompt': 'x'.repeat(20_000),
  }

  function record(spanId: string, raw: unknown): CanonicalRecord {
    return {
      spanId,
      traceId: 'trace-attrs',
      parentSpanId: null,
      source: 'claude-code',
      harness: 'claude-code',
      sessionId: 'sess-attrs',
      name: 'llm_request',
      op: 'llm.invoke',
      kind: 'client',
      timestamp: '2026-09-03T10:00:00.000Z',
      durationMs: 100,
      status: 'ok',
      tokens: {
        freshInput: 100,
        cacheRead: 0,
        cacheCreation: 0,
        output: 10,
        reportedInput: 100,
        reportedOutput: 10,
      },
      content: { system_prompt: 'hello' },
      cost: { basis: 'unknown', status: 'no_rate' },
      raw,
    }
  }

  beforeAll(async () => {
    store = new CanonStore(':memory:')
    store.upsertMany([record('span-attrs', ATTRIBUTES), record('span-bare', null)])

    server = await runWebDashboard({
      period: 'today',
      provider: 'all',
      project: [],
      exclude: [],
      port: 0,
      open: false,
      kyberBridge: new KyberBridge({
        store,
        canonDb: new DatabaseSync(':memory:'),
        sessionsDb: new DatabaseSync(':memory:'),
        ratesPath: join(tmpdir(), 'nonexistent-rates.json'),
      }),
    })
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  })

  afterAll(async () => {
    await new Promise<void>((resolve) => server.close(() => resolve()))
    store.close()
  })

  it('returns the span attribute map from compressed storage', async () => {
    const res = await fetch(`${base}/api/kyber/span/span-attrs/attributes`)

    expect(res.status).toBe(200)
    const body = (await res.json()) as { spanId: string; attributes: Record<string, unknown> }
    expect(body.spanId).toBe('span-attrs')
    expect(body.attributes['gen_ai.request.model']).toBe('claude-opus-5')
  })

  it('does not clip the map — this route exists to show what the harness emitted', async () => {
    const res = await fetch(`${base}/api/kyber/span/span-attrs/attributes`)
    const body = (await res.json()) as { attributes: Record<string, unknown> }

    expect(String(body.attributes['kyber.prompt'])).toHaveLength(20_000)
  })

  it('404s for a span it does not hold', async () => {
    const res = await fetch(`${base}/api/kyber/span/nosuchspan/attributes`)

    expect(res.status).toBe(404)
  })

  it('404s for a span that recorded no raw payload', async () => {
    const res = await fetch(`${base}/api/kyber/span/span-bare/attributes`)

    expect(res.status).toBe(404)
  })

  it('rejects a non-GET method', async () => {
    const res = await fetch(`${base}/api/kyber/span/span-attrs/attributes`, { method: 'POST' })

    expect(res.status).toBe(405)
  })
})
