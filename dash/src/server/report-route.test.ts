// `/api/kyber/report` and `/api/kyber/meta` (R5.5, R6.7, R7.1, R7.5).
//
// The tray reads these two and holds no analysis of its own (R7.5), so what is checked
// here is that the endpoint hands back the builder's document unchanged and rejects a
// request it cannot honour rather than quietly answering a different question. A scope the
// server silently corrects is worse than an error: the caller would render figures for a
// window it did not ask for and have no way to tell.

import { readFileSync } from 'node:fs'
import type { IncomingMessage, ServerResponse } from 'node:http'
import { describe, expect, it } from 'vitest'

import type { KyberBridge } from './bridge.js'
import { handleKyberRequest } from './routes.js'

function bridgeStub(): KyberBridge {
  return {
    listSessions: () => [],
    listFindings: () => [],
    listHarnessRollups: () => [],
    getQuarantine: () => [],
    getProblems: () => [],
    getSessionContent: () => undefined,
    getMeta: () => ({ spanCount: 0, quarantinedCount: 0 }),
  } as unknown as KyberBridge
}

function call(
  href: string,
  method = 'GET',
  bridge: KyberBridge = bridgeStub(),
): { status: number; body: unknown; handled: boolean } {
  let status = 0
  let body = ''
  const req = { method } as IncomingMessage
  const res = {
    writeHead: (code: number) => { status = code },
    end: (data: string) => { body = data },
  } as unknown as ServerResponse
  const handled = handleKyberRequest(req, res, new URL(href, 'http://127.0.0.1:4747'), bridge)
  return { status, body: body === '' ? undefined : JSON.parse(body), handled }
}

describe('GET /api/kyber/report', () => {
  it('returns the builder document for the default scope', () => {
    const { status, body } = call('/api/kyber/report')
    expect(status).toBe(200)
    expect(body).toMatchObject({ schemaVersion: 1, scope: { days: 7 } })
  })

  it('carries the query scope through to the document', () => {
    const { body } = call('/api/kyber/report?days=3&harness=codex&session=s1&run=r1')
    expect((body as { scope: unknown }).scope).toEqual({
      days: 3,
      harness: 'codex',
      sessionId: 's1',
      runId: 'r1',
    })
  })

  it('excludes detection by default, because it walks the filesystem', () => {
    const { body } = call('/api/kyber/report')
    expect(body).not.toHaveProperty('detection')
    expect(body).toHaveProperty('coverage')
    expect(body).toHaveProperty('cost')
  })

  it('filters to the sections asked for', () => {
    const { body } = call('/api/kyber/report?sections=findings,cost')
    expect(body).toHaveProperty('findings')
    expect(body).toHaveProperty('cost')
    expect(body).not.toHaveProperty('coverage')
    expect(body).not.toHaveProperty('harnesses')
  })

  it('rejects an unknown section rather than returning a report missing it', () => {
    const { status, body } = call('/api/kyber/report?sections=findings,nonsense')
    expect(status).toBe(400)
    expect(String((body as { error: string }).error)).toContain('nonsense')
  })

  it('rejects a non-positive or non-integer days rather than defaulting it', () => {
    for (const bad of ['0', '-1', '2.5', 'seven']) {
      const { status } = call(`/api/kyber/report?days=${bad}`)
      expect(status, `days=${bad}`).toBe(400)
    }
  })

  it('accepts an absent days and uses the 7-day default (R11.11)', () => {
    expect((call('/api/kyber/report?days=').body as { scope: { days: number } }).scope.days).toBe(7)
  })

  it('rejects a method other than GET', () => {
    const { status } = call('/api/kyber/report', 'POST')
    expect(status).toBe(405)
  })
})

describe('GET /api/kyber/meta', () => {
  it('carries version and apiVersion so a client can check it understands this server', () => {
    const { status, body } = call('/api/kyber/meta')
    expect(status).toBe(200)
    expect(typeof (body as { version: string }).version).toBe('string')
    expect((body as { apiVersion: number }).apiVersion).toBe(1)
  })

  it('keeps the store figures it already served', () => {
    expect(call('/api/kyber/meta').body).toMatchObject({ spanCount: 0, quarantinedCount: 0 })
  })

  it('rejects a method other than GET', () => {
    expect(call('/api/kyber/meta', 'POST').status).toBe(405)
  })
})

describe('GET /api/kyber/version', () => {
  it('returns exactly two keys: the build version and the REST contract version', () => {
    const { status, body } = call('/api/kyber/version')
    expect(status).toBe(200)
    expect(Object.keys(body as Record<string, unknown>).sort()).toEqual(['apiVersion', 'version'])
  })

  it('reports the version from dash/package.json, the same build identity /meta carries', () => {
    const pkg = JSON.parse(readFileSync(new URL('../../package.json', import.meta.url), 'utf8')) as {
      version: string
    }
    expect(call('/api/kyber/version').body).toMatchObject({ version: pkg.version })
  })

  it('reports the REST contract version (REPORT_SCHEMA_VERSION, currently 1)', () => {
    expect(call('/api/kyber/version').body).toMatchObject({ apiVersion: 1 })
  })

  it('answers without reading the store, so a missing or broken database cannot block it', () => {
    // Every property access on the bridge throws, so any store read (getMeta included) fails the test.
    const storeless = new Proxy(
      {},
      {
        get(_target, prop) {
          throw new Error(`store read '${String(prop)}' must not run for /api/kyber/version`)
        },
      },
    ) as unknown as KyberBridge
    const { status, body } = call('/api/kyber/version', 'GET', storeless)
    expect(status).toBe(200)
    expect(body).toEqual({ version: expect.any(String), apiVersion: 1 })
  })

  it('rejects a method other than GET with 405 for POST, PUT and DELETE', () => {
    for (const method of ['POST', 'PUT', 'DELETE']) {
      const { status } = call('/api/kyber/version', method)
      expect(status, method).toBe(405)
    }
  })
})

describe('the existing routing rules still hold', () => {
  it('claims every /api/kyber path, so none falls through to the SPA', () => {
    expect(call('/api/kyber/report').handled).toBe(true)
    expect(call('/api/kyber/meta').handled).toBe(true)
    expect(call('/api/kyber/version').handled).toBe(true)
  })

  it('404s an unknown /api/kyber path instead of serving HTML', () => {
    const { status, handled } = call('/api/kyber/not-a-route')
    expect(handled).toBe(true)
    expect(status).toBe(404)
  })

  it('does not claim a path outside the API', () => {
    expect(call('/index.html').handled).toBe(false)
  })
})
