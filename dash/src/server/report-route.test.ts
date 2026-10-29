// `/api/kyber/report` and `/api/kyber/meta` (R5.5, R6.7, R7.1, R7.5).
//
// The tray reads these two and holds no analysis of its own (R7.5), so what is checked
// here is that the endpoint hands back the builder's document unchanged and rejects a
// request it cannot honour rather than quietly answering a different question. A scope the
// server silently corrects is worse than an error: the caller would render figures for a
// window it did not ask for and have no way to tell.

import { readFileSync } from 'node:fs'
import type { IncomingMessage, ServerResponse } from 'node:http'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { KyberBridge } from './bridge.js'
import { handleKyberRequest } from './routes.js'

// routes.ts reads its build version once, at import time, through
// `createRequire(import.meta.url)('../../package.json')`. This seam lets a test choose what
// that one require returns (or nothing at all) and then re-import routes.ts to see it.
// Every other require passes through to the real module.
const versionSeam = vi.hoisted(() => ({
  packageJson: undefined as Record<string, unknown> | undefined,
}))

vi.mock('node:module', async (importOriginal) => {
  const actual = await importOriginal<typeof import('node:module')>()
  return {
    ...actual,
    createRequire: (url: string | URL) => {
      const realRequire = actual.createRequire(url)
      const fakeRequire = (id: string): unknown =>
        id === '../../package.json' && versionSeam.packageJson !== undefined
          ? versionSeam.packageJson
          : realRequire(id)
      return fakeRequire as unknown as ReturnType<typeof actual.createRequire>
    },
  }
})

/** Re-import routes.ts with `package.json` replaced, so its import-time version constant sees it. */
async function routesWithPackageJson(packageJson: Record<string, unknown>) {
  versionSeam.packageJson = packageJson
  vi.resetModules()
  try {
    return await import('./routes.js')
  } finally {
    versionSeam.packageJson = undefined
  }
}

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
  handle: typeof handleKyberRequest = handleKyberRequest,
): { status: number; body: unknown; handled: boolean } {
  let status = 0
  let body = ''
  const req = { method } as IncomingMessage
  const res = {
    writeHead: (code: number) => { status = code },
    end: (data: string) => { body = data },
  } as unknown as ServerResponse
  const handled = handle(req, res, new URL(href, 'http://127.0.0.1:4747'), bridge)
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

  describe('when the engine has no version to report', () => {
    afterEach(() => {
      versionSeam.packageJson = undefined
    })

    it('reports the version it does carry, so the seam below is proven live', async () => {
      const { handleKyberRequest: handle } = await routesWithPackageJson({ version: '9.9.9' })
      const { status, body } = call('/api/kyber/version', 'GET', bridgeStub(), handle)
      expect(status).toBe(200)
      expect(body).toEqual({ version: '9.9.9', apiVersion: 1 })
    })

    it.each([
      { label: 'absent', packageJson: {} },
      { label: 'empty', packageJson: { version: '' } },
    ])('answers 503 with an error rather than 200 with 0.0.0 when the version is $label', async ({ packageJson }) => {
      const { handleKyberRequest: handle } = await routesWithPackageJson(packageJson)
      const { status, body } = call('/api/kyber/version', 'GET', bridgeStub(), handle)
      expect(status, `package.json ${JSON.stringify(packageJson)}`).toBe(503)
      expect(body).toMatchObject({ error: expect.any(String) })
      expect(body, 'a defaulted version must not be served').not.toHaveProperty('version')
    })
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
