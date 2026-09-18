// T8 — API filter contract against a temporary refreshed canon.db.

import { rmSync } from 'node:fs'
import type { IncomingMessage, ServerResponse } from 'node:http'
import { afterEach, describe, expect, it } from 'vitest'

import { isNotMeasurable, type MetricAvailability } from '../src/canon/types.js'
import { KyberBridge } from '../src/server/bridge.js'
import { handleKyberRequest } from '../src/server/routes.js'
import {
  POPULATED_HARNESSES,
  USER_CANON,
  refreshTempCanon,
  type RefreshedWorld,
} from '../e2e/refresh-filters-world.js'

const worlds: RefreshedWorld[] = []

afterEach(() => {
  while (worlds.length > 0) {
    const world = worlds.pop()!
    world.store.close()
    rmSync(world.root, { recursive: true, force: true })
  }
})

type JsonResponse = { status: number; body: string }

function invoke(bridge: KyberBridge, href: string): JsonResponse {
  let status = 0
  let body = ''
  const req = { method: 'GET' } as IncomingMessage
  const res = {
    writeHead: (code: number) => {
      status = code
    },
    end: (data: string) => {
      body = data
    },
  } as unknown as ServerResponse
  const handled = handleKyberRequest(req, res, new URL(href), bridge)
  expect(handled).toBe(true)
  return { status, body }
}

function json(bridge: KyberBridge, href: string): unknown {
  const response = invoke(bridge, href)
  expect(response.status).toBe(200)
  return JSON.parse(response.body) as unknown
}

describe('T8 API filters against a temporary refreshed DB', () => {
  it('keeps split harness runs independent and never aliases another identity', async () => {
    const world = await refreshTempCanon()
    worlds.push(world)
    expect(world.dbPath).not.toBe(USER_CANON)
    expect(world.dbPath).not.toContain('.kyberdash')

    const bridge = new KyberBridge({ canonPath: world.dbPath, store: world.store })

    const list = json(bridge, 'http://127.0.0.1/api/kyber/harnesses') as {
      harnesses: Array<{
        harness: string
        sampleCount: number
        cacheHitRate: number | null
        contextPressureMedian: number | null
        measurability: Record<string, MetricAvailability>
        payload?: { reason?: string }
      }>
    }

    const ids = list.harnesses.map((row) => row.harness)
    expect(ids).not.toContain('gemini')
    for (const harness of POPULATED_HARNESSES) {
      expect(ids).toContain(harness)
    }

    const allRuns = json(bridge, 'http://127.0.0.1/api/kyber/runs') as {
      runs: Array<{ runId: string; harness: string }>
    }
    const populatedRuns = allRuns.runs.filter((run) =>
      (POPULATED_HARNESSES as readonly string[]).includes(run.harness),
    )
    expect(populatedRuns.length).toBeGreaterThan(0)

    const perHarnessCounts = new Map<string, number>()
    for (const harness of POPULATED_HARNESSES) {
      const filtered = json(
        bridge,
        `http://127.0.0.1/api/kyber/runs?harness=${encodeURIComponent(harness)}`,
      ) as { runs: Array<{ runId: string; harness: string }> }
      expect(filtered.runs.length, `${harness} should have stored runs`).toBeGreaterThan(0)
      expect(filtered.runs.every((run) => run.harness === harness)).toBe(true)
      perHarnessCounts.set(harness, filtered.runs.length)

      const foreign = POPULATED_HARNESSES.filter((other) => other !== harness)
      for (const other of foreign) {
        expect(filtered.runs.some((run) => run.harness === other)).toBe(false)
      }

      const detail = invoke(bridge, `http://127.0.0.1/api/kyber/harness/${encodeURIComponent(harness)}`)
      expect(detail.status).toBe(200)
      const rollup = JSON.parse(detail.body) as {
        harness: string
        sampleCount: number
        cacheHitRate: number | null
        contextPressureMedian: number | null
        measurability: Record<string, MetricAvailability>
      }
      expect(rollup.harness).toBe(harness)
      expect(rollup.sampleCount).toBeGreaterThan(0)
    }

    const summed = [...perHarnessCounts.values()].reduce((acc, n) => acc + n, 0)
    expect(allRuns.runs.length).toBe(summed)

    const empty = list.harnesses.find((row) => row.harness === 'cline')
    expect(empty).toBeDefined()
    expect(empty!.sampleCount).toBe(0)
    expect(empty!.cacheHitRate).toBeNull()
    expect(empty!.contextPressureMedian).toBeNull()
    const emptyCache = empty!.measurability['cache_hit_rate']
    expect(isNotMeasurable(emptyCache)).toBe(true)
    if (isNotMeasurable(emptyCache)) {
      expect(emptyCache.reason.length).toBeGreaterThan(0)
    }

    for (const row of list.harnesses) {
      if (row.sampleCount > 0) continue
      expect(row.cacheHitRate).toBeNull()
      const metric = row.measurability['cache_hit_rate'] ?? row.measurability['context_pressure']
      expect(metric, `${row.harness} empty KPI needs a reason`).toBeDefined()
      if (metric !== undefined && isNotMeasurable(metric)) {
        expect(metric.reason.length).toBeGreaterThan(0)
      } else if (typeof metric === 'string') {
        expect(metric).not.toBe('measured')
      }
    }

    const refresh = invoke(bridge, 'http://127.0.0.1/api/kyber/refresh')
    expect(refresh.status).toBe(404)
    expect(JSON.parse(refresh.body)).toEqual({ error: 'Not found' })
  })
})
