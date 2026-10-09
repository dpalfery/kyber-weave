// P2.8 — GET/POST model-catalog routes (RED). Exercises the live server router
// without importing the catalog module (not present on this tip).

import type { AddressInfo } from 'node:net'
import type { Server } from 'node:http'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'

import { runWebDashboard } from '../cli/web.js'
import { KyberBridge } from './bridge.js'
import { CanonStore } from '../canon/store.js'

describe('P2.8: /api/kyber/model-catalog', () => {
  let server: Server
  let base: string
  let store: CanonStore

  beforeAll(async () => {
    store = new CanonStore(':memory:')
    server = await runWebDashboard({
      port: 0,
      open: false,
      writeStdout: () => {},
      kyberBridge: new KyberBridge({
        store,
        canonDb: new DatabaseSync(':memory:'),
        ratesPath: join(tmpdir(), 'nonexistent-rates-p28.json'),
      }),
    })
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  })

  afterAll(async () => {
    await new Promise<void>((resolve) => server.close(() => resolve()))
    store.close()
  })

  it('GET reports an empty catalog without writing rows', async () => {
    const res = await fetch(`${base}/api/kyber/model-catalog`)
    expect(res.status).toBe(200)

    const body = (await res.json()) as {
      rowCount: number
      lastRefreshAt: string | null
      vendors: Record<
        string,
        {
          documentationUrl: string
          status: string
          rowCount: number
          lastRefreshAt: string | null
          error?: string
        }
      >
    }

    expect(body.rowCount).toBe(0)
    expect(body.lastRefreshAt).toBeNull()
    expect(body.vendors['synth-vendor-a'].documentationUrl).toMatch(/^https:\/\//)
    expect(body.vendors['synth-vendor-b'].documentationUrl).toMatch(/^https:\/\//)
    expect(body.vendors['synth-vendor-a'].status).toBe('unknown')
    expect(body.vendors['synth-vendor-a'].rowCount).toBe(0)
    expect(body.vendors['synth-vendor-b'].status).toBe('unknown')

    const again = (await (await fetch(`${base}/api/kyber/model-catalog`)).json()) as { rowCount: number }
    expect(again.rowCount).toBe(0)
  })

  it('POST /refresh updates vendors and performs exactly one derived rebuild after a success', async () => {
    const before = await (await fetch(`${base}/api/kyber/model-catalog`)).json()
    expect((before as { rowCount: number }).rowCount).toBe(0)

    const res = await fetch(`${base}/api/kyber/model-catalog/refresh`, { method: 'POST' })
    expect(res.status).toBe(200)

    const body = (await res.json()) as {
      rowCount: number
      derivedRebuildCount: number
      vendorsUpdated: string[]
    }

    expect(body.derivedRebuildCount).toBe(1)
    expect(body.vendorsUpdated.length).toBeGreaterThan(0)
    expect(body.rowCount).toBeGreaterThan(0)

    const after = await (await fetch(`${base}/api/kyber/model-catalog`)).json()
    expect((after as { lastRefreshAt: string | null }).lastRefreshAt).not.toBeNull()
  })

  it('rejects methods other than GET on the catalog snapshot route', async () => {
    const res = await fetch(`${base}/api/kyber/model-catalog`, { method: 'PUT' })
    expect(res.status).toBe(405)
  })

  it('rejects methods other than POST on the refresh route', async () => {
    const res = await fetch(`${base}/api/kyber/model-catalog/refresh`, { method: 'GET' })
    expect(res.status).toBe(405)
  })
})
