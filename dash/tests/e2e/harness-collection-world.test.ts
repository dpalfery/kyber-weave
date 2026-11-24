import { existsSync, mkdirSync, mkdtempSync, realpathSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { createServer, type IncomingMessage, type ServerResponse } from 'node:http'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const mkdtempHook = vi.hoisted(() => ({
  recordedDirs: [] as string[],
  onCreated: undefined as ((createdDir: string) => void) | undefined,
}))

vi.mock('node:fs', async (importOriginal) => {
  const actual = await importOriginal<typeof import('node:fs')>()
  return {
    ...actual,
    mkdtempSync: ((...args: Parameters<typeof actual.mkdtempSync>) => {
      const created = actual.mkdtempSync(...args)
      mkdtempHook.recordedDirs.push(created)
      mkdtempHook.onCreated?.(created)
      return created
    }) as typeof actual.mkdtempSync,
  }
})

import {
  cleanupIsolatedTempRoot,
  createTempRoot,
  isIsolatedTempRoot,
  startIsolatedServer,
  waitForOtlpIngest,
} from '../../e2e/harness-collection-world.js'

async function startStubServer(
  handler: (req: IncomingMessage, res: ServerResponse) => void,
): Promise<{ readonly url: string; close: () => Promise<void> }> {
  const server = createServer(handler)
  await new Promise<void>((resolveListen, rejectListen) => {
    server.once('error', rejectListen)
    server.listen(0, '127.0.0.1', () => {
      server.off('error', rejectListen)
      resolveListen()
    })
  })
  const address = server.address()
  if (address === null || typeof address === 'string') {
    server.close()
    throw new Error('Stub server failed to bind an ephemeral loopback port')
  }
  return {
    url: `http://127.0.0.1:${address.port}`,
    close: async () => {
      server.closeAllConnections()
      await new Promise<void>((resolveClose) => {
        server.close(() => resolveClose())
      })
    },
  }
}

describe('harness-collection-world isolated temp root containment (Issue 1)', () => {
  let sandboxRoot: string
  let fakeTmpDir: string
  let siblingTmpDir: string
  let outsideDir: string

  beforeEach(() => {
    sandboxRoot = realpathSync(mkdtempSync(join(tmpdir(), 'kw-world-test-')))
    fakeTmpDir = join(sandboxRoot, 'tmp')
    siblingTmpDir = join(sandboxRoot, 'tmp-evil')
    outsideDir = join(sandboxRoot, 'outside')
    mkdirSync(fakeTmpDir, { recursive: true })
    mkdirSync(siblingTmpDir, { recursive: true })
    mkdirSync(outsideDir, { recursive: true })
  })

  afterEach(() => {
    rmSync(sandboxRoot, { recursive: true, force: true })
  })

  it('rejects sibling directory sharing tmpdir prefix without separator', () => {
    const siblingSecret = join(siblingTmpDir, 'important.txt')
    writeFileSync(siblingSecret, 'keep me', 'utf8')

    expect(isIsolatedTempRoot(siblingTmpDir, fakeTmpDir)).toBe(false)
    expect(() => cleanupIsolatedTempRoot(siblingTmpDir, fakeTmpDir)).toThrow()
    expect(existsSync(siblingSecret)).toBe(true)
  })

  it('rejects deleting the tmp directory itself or its parent via traversal', () => {
    const childInsideTmp = join(fakeTmpDir, 'keep-me')
    mkdirSync(childInsideTmp, { recursive: true })

    expect(isIsolatedTempRoot(fakeTmpDir, fakeTmpDir)).toBe(false)
    expect(() => cleanupIsolatedTempRoot(fakeTmpDir, fakeTmpDir)).toThrow()
    expect(existsSync(childInsideTmp)).toBe(true)

    const traversalToSibling = join(fakeTmpDir, '..', 'tmp-evil')
    expect(isIsolatedTempRoot(traversalToSibling, fakeTmpDir)).toBe(false)
    expect(() => cleanupIsolatedTempRoot(traversalToSibling, fakeTmpDir)).toThrow()
    expect(existsSync(siblingTmpDir)).toBe(true)
  })

  it('rejects paths inside tmpdir containing .kyberdash or symlinks escaping tmpdir', () => {
    const kyberdashDir = join(fakeTmpDir, '.kyberdash', 'nested')
    mkdirSync(kyberdashDir, { recursive: true })
    expect(isIsolatedTempRoot(kyberdashDir, fakeTmpDir)).toBe(false)
    expect(() => cleanupIsolatedTempRoot(kyberdashDir, fakeTmpDir)).toThrow()
    expect(existsSync(kyberdashDir)).toBe(true)

    const outsideSecretDir = join(outsideDir, 'nested')
    mkdirSync(outsideSecretDir, { recursive: true })
    const escapeLink = join(fakeTmpDir, 'escape-link')
    symlinkSync(outsideDir, escapeLink)

    expect(isIsolatedTempRoot(escapeLink, fakeTmpDir)).toBe(false)
    expect(isIsolatedTempRoot(join(escapeLink, 'nested'), fakeTmpDir)).toBe(false)
    expect(() => cleanupIsolatedTempRoot(escapeLink, fakeTmpDir)).toThrow()
    expect(() => cleanupIsolatedTempRoot(join(escapeLink, 'nested'), fakeTmpDir)).toThrow()
    expect(existsSync(outsideSecretDir)).toBe(true)
  })

  it('allows and cleans up a genuine isolated directory strictly inside tmpdir', () => {
    const created = createTempRoot(fakeTmpDir)
    expect(resolve(created).startsWith(`${resolve(fakeTmpDir)}/`) || process.platform === 'win32').toBe(true)
    expect(isIsolatedTempRoot(created, fakeTmpDir)).toBe(true)
    writeFileSync(join(created, 'marker.txt'), 'ephemeral', 'utf8')

    cleanupIsolatedTempRoot(created, fakeTmpDir)
    expect(existsSync(created)).toBe(false)
  })

  it('cleans up the created directory if createTempRoot rejects containment', () => {
    const invalidTmp = join(sandboxRoot, '.kyberdash')
    mkdirSync(invalidTmp, { recursive: true })

    mkdtempHook.recordedDirs.length = 0
    expect(() => createTempRoot(invalidTmp)).toThrow(/escaped tmpdir/)
    expect(mkdtempHook.recordedDirs).toHaveLength(1)
    const tempDir = mkdtempHook.recordedDirs[0]
    expect(tempDir).toBeDefined()
    if (tempDir !== undefined) {
      expect(tempDir.startsWith(join(invalidTmp, 'kyber-harness-collection-'))).toBe(true)
      expect(existsSync(tempDir)).toBe(false)
    }
  })

  it('recursively removes a non-empty temp root when assertValidatedTempRoot rejects containment', () => {
    const invalidTmp = join(sandboxRoot, '.kyberdash')
    mkdirSync(invalidTmp, { recursive: true })

    mkdtempHook.recordedDirs.length = 0
    mkdtempHook.onCreated = (createdDir) => {
      mkdirSync(join(createdDir, 'nested'), { recursive: true })
      writeFileSync(join(createdDir, 'nested', 'race.txt'), 'concurrent write', 'utf8')
    }
    try {
      expect(() => createTempRoot(invalidTmp)).toThrow(/escaped tmpdir/)
    } finally {
      mkdtempHook.onCreated = undefined
    }

    expect(mkdtempHook.recordedDirs).toHaveLength(1)
    const tempDir = mkdtempHook.recordedDirs[0]
    expect(tempDir).toBeDefined()
    if (tempDir !== undefined) {
      expect(tempDir.startsWith(join(invalidTmp, 'kyber-harness-collection-'))).toBe(true)
      expect(existsSync(tempDir)).toBe(false)
    }
  })
})

describe('waitForOtlpIngest collector drain check (Finding 1)', () => {
  const activeServers: Array<{ close: () => Promise<void> }> = []

  afterEach(async () => {
    vi.unstubAllGlobals()
    while (activeServers.length > 0) {
      const server = activeServers.pop()
      if (server) {
        await server.close()
      }
    }
  })

  it('does not treat missing, null, string, or negative pendingCount as drained', async () => {
    const malformedBodies: unknown[] = [{}, { pendingCount: null }, { pendingCount: '0' }, { pendingCount: -1 }, [], null]
    let pollIndex = 0
    let apiQueries = 0

    const stub = await startStubServer((req, res) => {
      const url = new URL(req.url ?? '/', 'http://127.0.0.1')
      if (url.pathname === '/healthz') {
        const body = malformedBodies[pollIndex % malformedBodies.length]
        pollIndex += 1
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end(JSON.stringify(body))
        return
      }
      apiQueries += 1
      if (url.pathname === '/api/kyber/sessions') {
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end(JSON.stringify({ sessions: [{ sessionId: 'ses-1' }], total: 1, page: 1, limit: 100 }))
        return
      }
      if (url.pathname === '/api/kyber/harnesses') {
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end(JSON.stringify({ harnesses: [{ harness: 'opencode', sampleCount: 1 }] }))
        return
      }
      if (url.pathname === '/api/kyber/coverage') {
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end(JSON.stringify({ ingest: { accepted: 1 } }))
        return
      }
      res.writeHead(404)
      res.end()
    })
    activeServers.push(stub)

    await expect(
      waitForOtlpIngest(stub.url, ['ses-1'], {
        timeoutMs: 180,
        pollIntervalMs: 15,
        expectedHarnesses: ['opencode'],
      }),
    ).rejects.toThrow(/waitForOtlpIngest timed out/)
    expect(pollIndex).toBeGreaterThan(0)
    expect(apiQueries).toBe(0)
  })

  it('treats non-finite in-memory pendingCount values (NaN, Infinity, -Infinity) as draining', async () => {
    const nonFinitePayloads = [
      { pendingCount: Number.NaN },
      { pendingCount: Number.POSITIVE_INFINITY },
      { pendingCount: Number.NEGATIVE_INFINITY },
    ]
    let pollIndex = 0
    let apiQueries = 0

    vi.stubGlobal('fetch', async (input: string | URL | Request) => {
      const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url
      const url = new URL(rawUrl)
      if (url.pathname === '/healthz') {
        const payload = nonFinitePayloads[pollIndex % nonFinitePayloads.length]
        pollIndex += 1
        return {
          ok: true,
          status: 200,
          json: async () => payload,
        } as Response
      }
      apiQueries += 1
      if (url.pathname === '/api/kyber/sessions') {
        return new Response(JSON.stringify({ sessions: [{ sessionId: 'ses-1' }], total: 1, page: 1, limit: 100 }), {
          status: 200,
        })
      }
      if (url.pathname === '/api/kyber/harnesses') {
        return new Response(JSON.stringify({ harnesses: [{ harness: 'opencode', sampleCount: 1 }] }), { status: 200 })
      }
      return new Response(JSON.stringify({ ingest: { accepted: 1 } }), { status: 200 })
    })

    await expect(
      waitForOtlpIngest('http://127.0.0.1:19999', ['ses-1'], {
        timeoutMs: 120,
        pollIntervalMs: 15,
        expectedHarnesses: ['opencode'],
      }),
    ).rejects.toThrow(/waitForOtlpIngest timed out/)
    expect(pollIndex).toBeGreaterThan(0)
    expect(apiQueries).toBe(0)
  })

  it('continues polling through malformed and positive pendingCount until a valid 0 is observed', async () => {
    const healthSequence: unknown[] = [{}, { pendingCount: -2 }, { pendingCount: '0' }, { pendingCount: 2 }, { pendingCount: 0 }]
    let healthzCalls = 0
    let observedZero = false
    let apiQueriesBeforeZero = 0

    const stub = await startStubServer((req, res) => {
      const url = new URL(req.url ?? '/', 'http://127.0.0.1')
      if (url.pathname === '/healthz') {
        const body = healthSequence[Math.min(healthzCalls, healthSequence.length - 1)]
        healthzCalls += 1
        if (typeof body === 'object' && body !== null && 'pendingCount' in body && body.pendingCount === 0) {
          observedZero = true
        }
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end(JSON.stringify(body))
        return
      }
      if (!observedZero) {
        apiQueriesBeforeZero += 1
      }
      if (url.pathname === '/api/kyber/sessions') {
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end(JSON.stringify({ sessions: [{ sessionId: 'ses-1' }], total: 1, page: 1, limit: 100 }))
        return
      }
      if (url.pathname === '/api/kyber/harnesses') {
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end(JSON.stringify({ harnesses: [{ harness: 'opencode', sampleCount: 1 }] }))
        return
      }
      if (url.pathname === '/api/kyber/coverage') {
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end(JSON.stringify({ ingest: { accepted: 1 } }))
        return
      }
      res.writeHead(404)
      res.end()
    })
    activeServers.push(stub)

    await waitForOtlpIngest(stub.url, ['ses-1'], {
      timeoutMs: 2_000,
      pollIntervalMs: 15,
      expectedHarnesses: ['opencode'],
    })
    expect(healthzCalls).toBeGreaterThanOrEqual(5)
    expect(apiQueriesBeforeZero).toBe(0)
  })
})

describe('startIsolatedServer temp root cleanup on failure (Finding 4)', () => {
  let sandboxRoot: string

  beforeEach(() => {
    sandboxRoot = realpathSync(mkdtempSync(join(tmpdir(), 'kw-start-server-test-')))
  })

  afterEach(() => {
    rmSync(sandboxRoot, { recursive: true, force: true })
  })

  it('removes the mkdtemp directory when isIsolatedTempRoot rejects the root', async () => {
    const invalidTmp = join(sandboxRoot, '.kyberdash')
    mkdirSync(invalidTmp, { recursive: true })
    const prevTmpdir = process.env['TMPDIR']
    const prevTemp = process.env['TEMP']
    const prevTmp = process.env['TMP']
    process.env['TMPDIR'] = invalidTmp
    process.env['TEMP'] = invalidTmp
    process.env['TMP'] = invalidTmp
    try {
      mkdtempHook.recordedDirs.length = 0
      await expect(startIsolatedServer({ startupTimeoutMs: 500 })).rejects.toThrow(/isolated server temp root invalid/)
      expect(mkdtempHook.recordedDirs).toHaveLength(1)
      const tempDir = mkdtempHook.recordedDirs[0]
      expect(tempDir).toBeDefined()
      if (tempDir !== undefined) {
        expect(tempDir.startsWith(join(invalidTmp, 'kyber-otel-e2e-'))).toBe(true)
        expect(existsSync(tempDir)).toBe(false)
      }
    } finally {
      if (prevTmpdir === undefined) delete process.env['TMPDIR']
      else process.env['TMPDIR'] = prevTmpdir
      if (prevTemp === undefined) delete process.env['TEMP']
      else process.env['TEMP'] = prevTemp
      if (prevTmp === undefined) delete process.env['TMP']
      else process.env['TMP'] = prevTmp
    }
  })

  it('removes the mkdtemp directory when child spawn fails during initialization', async () => {
    const validTmp = join(sandboxRoot, 'valid-tmp')
    mkdirSync(validTmp, { recursive: true })
    // Simulate a Node runtime cache directory (e.g. node-compile-cache in CI) sharing baseTmpDir.
    const runtimeCacheDir = join(validTmp, 'node-compile-cache')
    mkdirSync(runtimeCacheDir, { recursive: true })
    const prevTmpdir = process.env['TMPDIR']
    const prevTemp = process.env['TEMP']
    const prevTmp = process.env['TMP']
    const prevBin = process.env['KYBERDASH_BIN']
    process.env['TMPDIR'] = validTmp
    process.env['TEMP'] = validTmp
    process.env['TMP'] = validTmp
    process.env['KYBERDASH_BIN'] = join(sandboxRoot, 'missing-kyberdash-binary')
    try {
      mkdtempHook.recordedDirs.length = 0
      await expect(startIsolatedServer({ startupTimeoutMs: 5_000 })).rejects.toThrow(/spawn failed|exited/)
      expect(mkdtempHook.recordedDirs).toHaveLength(1)
      const tempDir = mkdtempHook.recordedDirs[0]
      expect(tempDir).toBeDefined()
      if (tempDir !== undefined) {
        expect(tempDir.startsWith(join(validTmp, 'kyber-otel-e2e-'))).toBe(true)
        expect(existsSync(tempDir)).toBe(false)
      }
      expect(existsSync(runtimeCacheDir)).toBe(true)
    } finally {
      if (prevTmpdir === undefined) delete process.env['TMPDIR']
      else process.env['TMPDIR'] = prevTmpdir
      if (prevTemp === undefined) delete process.env['TEMP']
      else process.env['TEMP'] = prevTemp
      if (prevTmp === undefined) delete process.env['TMP']
      else process.env['TMP'] = prevTmp
      if (prevBin === undefined) delete process.env['KYBERDASH_BIN']
      else process.env['KYBERDASH_BIN'] = prevBin
    }
  })
})
