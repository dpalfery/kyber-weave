import { createServer, type IncomingMessage, type ServerResponse } from 'node:http'
import { mkdirSync, mkdtempSync, realpathSync, rmSync, symlinkSync, truncateSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const fsHooks = vi.hoisted(() => ({
  fstatSizeOverride: undefined as number | undefined,
  openSyncError: undefined as Error | undefined,
}))

vi.mock('node:fs', async (importOriginal) => {
  const actual = await importOriginal<typeof import('node:fs')>()
  return {
    ...actual,
    openSync: ((...args: Parameters<typeof actual.openSync>) => {
      if (fsHooks.openSyncError !== undefined) {
        throw fsHooks.openSyncError
      }
      return actual.openSync(...args)
    }) as typeof actual.openSync,
    fstatSync: ((...args: Parameters<typeof actual.fstatSync>) => {
      const stat = actual.fstatSync(...args)
      if (fsHooks.fstatSizeOverride !== undefined && typeof stat.size === 'number') {
        const overriddenSize = fsHooks.fstatSizeOverride
        return new Proxy(stat, {
          get(target, prop, receiver) {
            if (prop === 'size') {
              return overriddenSize
            }
            const value = Reflect.get(target, prop, receiver)
            return typeof value === 'function' ? value.bind(target) : value
          },
        })
      }
      return stat
    }) as typeof actual.fstatSync,
  }
})

import {
  MAX_PROXY_RESPONSE_BYTES,
  PathTraversalError,
  StaticAssetTooLargeError,
  UpstreamResponseTooLargeError,
  buildTrayViewState,
  fetchTrayBridgeState,
  isValidApiVersion,
  isValidRefreshCadenceMinutes,
  mapStaticAssetOpenError,
  parseTrayBridgeStateSnapshot,
  readBoundedResponseBuffer,
  readResolvedStaticAsset,
  resolveStaticAsset,
  serveStaticAsset,
  startTrayBridge,
  verifyNoSymlinkSegments,
  type TrayBridgeHandle,
  type TraySettings,
} from '../../e2e/otel-tray-bridge.js'

const BASE_SETTINGS: TraySettings = {
  harness: 'all',
  windowDays: 7,
  attentionThreshold: 0.7,
  criticalThreshold: 0.9,
  launchAtLogin: false,
}

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

describe('otel-tray-bridge hardening (Issues 4, 5, 6, 7, 8)', () => {
  let sandboxRoot: string
  let distDir: string
  let outsideDir: string
  const activeBridges: TrayBridgeHandle[] = []
  const activeServers: Array<{ close: () => Promise<void> }> = []

  beforeEach(() => {
    sandboxRoot = realpathSync(mkdtempSync(join(tmpdir(), 'kw-bridge-test-')))
    distDir = join(sandboxRoot, 'dist')
    outsideDir = join(sandboxRoot, 'outside')
    mkdirSync(join(distDir, 'assets'), { recursive: true })
    mkdirSync(outsideDir, { recursive: true })
    writeFileSync(join(distDir, 'index.html'), '<!doctype html><html><body>tray</body></html>', 'utf8')
    writeFileSync(join(distDir, 'assets', 'app.js'), 'console.log("ok")', 'utf8')
    writeFileSync(join(outsideDir, 'secret.txt'), 'SECRET_OUTSIDE_DIST', 'utf8')
  })

  afterEach(async () => {
    fsHooks.fstatSizeOverride = undefined
    fsHooks.openSyncError = undefined
    while (activeBridges.length > 0) {
      const bridge = activeBridges.pop()
      if (bridge) {
        await bridge.stop()
      }
    }
    while (activeServers.length > 0) {
      const server = activeServers.pop()
      if (server) {
        await server.close()
      }
    }
    rmSync(sandboxRoot, { recursive: true, force: true })
  })

  describe('Issue 4: static asset path canonicalization and symlink rejection', () => {
    it('serves valid files under canonicalized distDir', () => {
      const rootRes = resolveStaticAsset(distDir, '/')
      expect(rootRes.ok).toBe(true)
      if (rootRes.ok) {
        expect(rootRes.filePath).toBe(join(distDir, 'index.html'))
      }

      const assetRes = resolveStaticAsset(distDir, '/assets/app.js')
      expect(assetRes.ok).toBe(true)
      if (assetRes.ok) {
        expect(assetRes.filePath).toBe(join(distDir, 'assets', 'app.js'))
      }
    })

    it('rejects null bytes, rooted/absolute paths, win32 backslashes, and parent traversal', () => {
      expect(resolveStaticAsset(distDir, '/%00').ok).toBe(false)
      expect(resolveStaticAsset(distDir, '/index.html\0.js').ok).toBe(false)

      for (const badPath of [
        '//etc/passwd',
        '/%2fetc/passwd',
        '/C:/Windows/win.ini',
        '/../outside/secret.txt',
        '/assets/../../outside/secret.txt',
      ]) {
        const result = resolveStaticAsset(distDir, badPath)
        expect(result.ok, badPath).toBe(false)
        if (!result.ok) {
          expect(result.statusCode, badPath).toBe(403)
        }
      }

      const backslashTraversal = resolveStaticAsset(distDir, '/..\\outside\\secret.txt')
      expect(backslashTraversal.ok).toBe(false)
      if (!backslashTraversal.ok) {
        expect(backslashTraversal.statusCode).toBe(process.platform === 'win32' ? 403 : 404)
      }
    })

    it('rejects symlinks inside distDir pointing outside or inside distDir', () => {
      symlinkSync(join(outsideDir, 'secret.txt'), join(distDir, 'leak.txt'))
      symlinkSync(outsideDir, join(distDir, 'dir-link'))
      symlinkSync(join(distDir, 'index.html'), join(distDir, 'internal-link.html'))

      for (const symlinkPath of ['/leak.txt', '/dir-link/secret.txt', '/dir-link/missing.txt', '/internal-link.html']) {
        const result = resolveStaticAsset(distDir, symlinkPath)
        expect(result.ok, symlinkPath).toBe(false)
        if (!result.ok) {
          expect(result.statusCode, symlinkPath).toBe(403)
        }
      }
    })

    it('returns 403 over HTTP when requesting a symlinked asset via startTrayBridge', async () => {
      symlinkSync(join(outsideDir, 'secret.txt'), join(distDir, 'leak.txt'))
      const stub = await startStubServer((_req, res) => {
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end('{}')
      })
      activeServers.push(stub)

      const bridge = await startTrayBridge({ serverUrl: stub.url, distDir })
      activeBridges.push(bridge)

      const leakRes = await fetch(`${bridge.url}/leak.txt`)
      expect(leakRes.status).toBe(403)
      expect(await leakRes.text()).not.toContain('SECRET_OUTSIDE_DIST')
    })
  })

  describe('Issue 5: first-refresh failure reports stale instead of ready', () => {
    it('buildTrayViewState sets phase to stale whenever error is non-null even with null report', () => {
      const failedInitial = buildTrayViewState({
        report: null,
        reportFetchedAt: null,
        error: 'HTTP 500 from /api/kyber/report',
        jobs: null,
        sharedSettings: null,
        coverage: null,
        kyberdashVersion: null,
        settings: BASE_SETTINGS,
      })
      expect(failedInitial.phase).toBe('stale')
      expect(failedInitial.report).toBeNull()
      expect(failedInitial.error).toBe('HTTP 500 from /api/kyber/report')

      const healthyEmpty = buildTrayViewState({
        report: {},
        reportFetchedAt: '2026-11-23T00:00:00.000Z',
        error: null,
        jobs: null,
        sharedSettings: null,
        coverage: null,
        kyberdashVersion: null,
        settings: BASE_SETTINGS,
      })
      expect(healthyEmpty.phase).toBe('ready')
    })

    it('startTrayBridge reports phase=stale on initial refresh failure and recovers on refresh_now', async () => {
      let failReport = true
      const stub = await startStubServer((req, res) => {
        const url = new URL(req.url ?? '/', 'http://127.0.0.1')
        if (url.pathname === '/api/kyber/report') {
          if (failReport) {
            res.writeHead(500, { 'content-type': 'application/json' })
            res.end(JSON.stringify({ error: 'database unavailable' }))
            return
          }
          res.writeHead(200, { 'content-type': 'application/json' })
          res.end(JSON.stringify({ latestSession: null, scope: { days: 7 } }))
          return
        }
        if (url.pathname === '/api/kyber/refresh' && req.method === 'POST') {
          res.writeHead(200, { 'content-type': 'application/json' })
          res.end(JSON.stringify({ ok: true }))
          return
        }
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end('{}')
      })
      activeServers.push(stub)

      const bridge = await startTrayBridge({ serverUrl: stub.url, distDir })
      activeBridges.push(bridge)

      const initialSnapshot = await bridge.getState()
      expect(initialSnapshot.viewState.phase).toBe('stale')
      expect(initialSnapshot.viewState.report).toBeNull()
      expect(initialSnapshot.viewState.error).toContain('database unavailable')

      const getViewRes = await fetch(`${bridge.url}/tray/invoke`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ cmd: 'get_view_state', args: {} }),
      })
      expect(getViewRes.status).toBe(200)
      const getViewPayload = (await getViewRes.json()) as { result: { phase: string } }
      expect(getViewPayload.result.phase).toBe('stale')

      failReport = false
      const refreshInvokeRes = await fetch(`${bridge.url}/tray/invoke`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ cmd: 'refresh_now', args: {} }),
      })
      expect(refreshInvokeRes.status).toBe(200)
      const recovered = await bridge.getState()
      expect(recovered.viewState.phase).toBe('ready')
      expect(recovered.viewState.error).toBeNull()
    })
  })

  describe('Issue 6: refreshCadenceMinutes and apiVersion validation', () => {
    it('validates refreshCadenceMinutes and apiVersion bounds strictly', () => {
      expect(isValidRefreshCadenceMinutes(1)).toBe(true)
      expect(isValidRefreshCadenceMinutes(15)).toBe(true)
      expect(isValidRefreshCadenceMinutes(1440)).toBe(true)
      for (const bad of [Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY, -1, 0, 1441, 1.5, '15', null, undefined]) {
        expect(isValidRefreshCadenceMinutes(bad), String(bad)).toBe(false)
      }

      expect(isValidApiVersion(1)).toBe(true)
      expect(isValidApiVersion(2)).toBe(true)
      for (const bad of [Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY, -1, 0, 1.5, '1', null, undefined]) {
        expect(isValidApiVersion(bad), String(bad)).toBe(false)
      }
    })

    it('ignores invalid upstream refreshCadenceMinutes/apiVersion and rejects invalid set_shared_settings mutations', async () => {
      let forwardedSettingsWrites = 0
      const stub = await startStubServer((req, res) => {
        const url = new URL(req.url ?? '/', 'http://127.0.0.1')
        if (url.pathname === '/api/kyber/settings' && req.method === 'GET') {
          res.writeHead(200, { 'content-type': 'application/json' })
          res.end(
            JSON.stringify({
              folderImportScheduled: false,
              jobsPaused: false,
              refreshCadenceMinutes: 999999,
              receiverHosted: false,
            }),
          )
          return
        }
        if (url.pathname === '/api/kyber/settings' && req.method === 'PUT') {
          forwardedSettingsWrites += 1
          res.writeHead(200, { 'content-type': 'application/json' })
          res.end('{}')
          return
        }
        if (url.pathname === '/api/kyber/version') {
          res.writeHead(200, { 'content-type': 'application/json' })
          res.end(JSON.stringify({ version: '1.2.3', apiVersion: 0 }))
          return
        }
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end('{}')
      })
      activeServers.push(stub)

      const bridge = await startTrayBridge({ serverUrl: stub.url, distDir })
      activeBridges.push(bridge)

      const snapshot = await bridge.getState()
      expect(snapshot.viewState.sharedSettings).toBeNull()
      expect(snapshot.viewState.kyberdashVersion).toBeNull()

      for (const invalidCadence of [0, -5, 1441, 1.5, '15', null]) {
        const res = await fetch(`${bridge.url}/tray/invoke`, {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({
            cmd: 'set_shared_settings',
            args: { patch: { refreshCadenceMinutes: invalidCadence } },
          }),
        })
        expect(res.status, String(invalidCadence)).toBe(400)
      }
      expect(forwardedSettingsWrites).toBe(0)
    })
  })

  describe('Issue 7: bounded proxy response reading', () => {
    it('rejects oversized streamed responses and Content-Length headers', async () => {
      const tooLargeHeader = new Response('short', { headers: { 'content-length': '2048' } })
      await expect(readBoundedResponseBuffer(tooLargeHeader, 1024)).rejects.toBeInstanceOf(
        UpstreamResponseTooLargeError,
      )

      const stream = new ReadableStream<Uint8Array>({
        start(controller) {
          controller.enqueue(new Uint8Array(600))
          controller.enqueue(new Uint8Array(600))
          controller.close()
        },
      })
      const streamedTooLarge = new Response(stream)
      await expect(readBoundedResponseBuffer(streamedTooLarge, 1024)).rejects.toBeInstanceOf(
        UpstreamResponseTooLargeError,
      )
    })

    it('returns HTTP 502 from /api/kyber/* proxy when upstream exceeds maxProxyResponseBytes', async () => {
      const stub = await startStubServer((req, res) => {
        const url = new URL(req.url ?? '/', 'http://127.0.0.1')
        if (url.pathname === '/api/kyber/huge') {
          res.writeHead(200, { 'content-type': 'application/json' })
          res.end('x'.repeat(2048))
          return
        }
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end('{}')
      })
      activeServers.push(stub)

      expect(MAX_PROXY_RESPONSE_BYTES).toBeGreaterThanOrEqual(10 * 1024 * 1024)
      const bridge = await startTrayBridge({
        serverUrl: stub.url,
        distDir,
        maxProxyResponseBytes: 1024,
      })
      activeBridges.push(bridge)

      const proxyRes = await fetch(`${bridge.url}/api/kyber/huge`)
      expect(proxyRes.status).toBe(502)
    })
  })

  describe('Issue 8: getState shape validation', () => {
    it('rejects malformed snapshot payloads instead of casting blindly', () => {
      const valid = {
        viewState: {
          phase: 'ready',
          report: null,
          reportFetchedAt: null,
          error: null,
          jobs: null,
          sharedSettings: null,
          coverage: null,
          kyberdashVersion: null,
          settings: BASE_SETTINGS,
        },
        invocations: [],
        openedViews: [],
        hidePopoverCount: 0,
        quitCount: 0,
      }
      expect(parseTrayBridgeStateSnapshot(valid)).toEqual(valid)

      for (const malformed of [
        null,
        [],
        {},
        { ...valid, viewState: null },
        { ...valid, viewState: { ...valid.viewState, phase: 'invalid' } },
        { ...valid, viewState: { ...valid.viewState, settings: { ...BASE_SETTINGS, windowDays: 0 } } },
        { ...valid, invocations: 'bad' },
        { ...valid, openedViews: [{ view: 123, url: 'x', at: 'now' }] },
        { ...valid, hidePopoverCount: -1 },
        { ...valid, quitCount: Number.NaN },
      ]) {
        expect(() => parseTrayBridgeStateSnapshot(malformed)).toThrow(/Invalid \/tray\/state payload/)
      }
    })

    it('rejects malformed or non-JSON /tray/state HTTP payloads via fetchTrayBridgeState', async () => {
      let mode: 'invalid-json' | 'bad-shape' = 'invalid-json'
      const stub = await startStubServer((_req, res) => {
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end(mode === 'invalid-json' ? '{broken' : JSON.stringify({ viewState: { phase: 'ready' } }))
      })
      activeServers.push(stub)

      await expect(fetchTrayBridgeState(stub.url)).rejects.toThrow(/invalid JSON/i)
      mode = 'bad-shape'
      await expect(fetchTrayBridgeState(stub.url)).rejects.toThrow(/Invalid \/tray\/state payload/)
    })
  })

  describe('Finding 2: fetchTrayBridgeState and maxBytes cap enforcement', () => {
    it('rejects Infinity, NaN, non-numbers, non-positive, non-integer, and oversized maxBytes in fetchTrayBridgeState', async () => {
      const validSnapshot = {
        viewState: {
          phase: 'ready',
          report: null,
          reportFetchedAt: null,
          error: null,
          jobs: null,
          sharedSettings: null,
          coverage: null,
          kyberdashVersion: null,
          settings: BASE_SETTINGS,
        },
        invocations: [],
        openedViews: [],
        hidePopoverCount: 0,
        quitCount: 0,
      }
      const stub = await startStubServer((_req, res) => {
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end(JSON.stringify(validSnapshot))
      })
      activeServers.push(stub)

      for (const badMaxBytes of [
        Number.POSITIVE_INFINITY,
        Number.NEGATIVE_INFINITY,
        Number.NaN,
        -1,
        0,
        1.5,
        MAX_PROXY_RESPONSE_BYTES + 1,
        '1024' as unknown as number,
      ]) {
        await expect(fetchTrayBridgeState(stub.url, { maxBytes: badMaxBytes }), String(badMaxBytes)).rejects.toThrow(
          /maxBytes must be a positive integer/,
        )
      }

      const okSnapshot = await fetchTrayBridgeState(stub.url)
      expect(okSnapshot.viewState.phase).toBe('ready')
    })

    it('rejects invalid or oversized maxBytes in startTrayBridge and readBoundedResponseBuffer', async () => {
      const stub = await startStubServer((_req, res) => {
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end('{}')
      })
      activeServers.push(stub)

      for (const badMaxBytes of [
        Number.POSITIVE_INFINITY,
        Number.NaN,
        -1,
        0,
        1.5,
        MAX_PROXY_RESPONSE_BYTES + 1,
      ]) {
        await expect(
          startTrayBridge({ serverUrl: stub.url, distDir, maxProxyResponseBytes: badMaxBytes }),
          String(badMaxBytes),
        ).rejects.toThrow(/maxProxyResponseBytes must be a positive integer/)

        await expect(
          readBoundedResponseBuffer(new Response('ok'), badMaxBytes),
          String(badMaxBytes),
        ).rejects.toThrow(/maxBytes must be a positive integer/)
      }
    })

    it('enforces the 10 MiB default cap in fetchTrayBridgeState when maxBytes is omitted', async () => {
      const stub = await startStubServer((_req, res) => {
        res.writeHead(200, {
          'content-type': 'application/json',
          'content-length': String(MAX_PROXY_RESPONSE_BYTES + 1),
        })
        res.end('{}')
      })
      activeServers.push(stub)

      await expect(fetchTrayBridgeState(stub.url)).rejects.toBeInstanceOf(UpstreamResponseTooLargeError)
    })
  })

  describe('Finding 3: static asset read TOCTOU protection', () => {
    it('reads a verified regular file inside distDir via readResolvedStaticAsset', () => {
      const resolved = resolveStaticAsset(distDir, '/assets/app.js')
      expect(resolved.ok).toBe(true)
      if (!resolved.ok) return

      const asset = readResolvedStaticAsset(distDir, resolved)
      expect(asset.ok).toBe(true)
      if (asset.ok) {
        expect(asset.contents.toString('utf8')).toBe('console.log("ok")')
      }
    })

    it('rejects target file swapped to an external or internal symlink after resolveStaticAsset', () => {
      const resolved = resolveStaticAsset(distDir, '/assets/app.js')
      expect(resolved.ok).toBe(true)
      if (!resolved.ok) return

      rmSync(join(distDir, 'assets', 'app.js'))
      symlinkSync(join(outsideDir, 'secret.txt'), join(distDir, 'assets', 'app.js'))

      const externalSwap = readResolvedStaticAsset(distDir, resolved)
      expect(externalSwap.ok).toBe(false)
      if (!externalSwap.ok) {
        expect(externalSwap.statusCode).toBe(403)
      }

      rmSync(join(distDir, 'assets', 'app.js'))
      symlinkSync(join(distDir, 'index.html'), join(distDir, 'assets', 'app.js'))

      const internalSwap = readResolvedStaticAsset(distDir, resolved)
      expect(internalSwap.ok).toBe(false)
      if (!internalSwap.ok) {
        expect(internalSwap.statusCode).toBe(403)
      }
    })

    it('rejects intermediate directory swapped to an external symlink after resolveStaticAsset', () => {
      writeFileSync(join(outsideDir, 'app.js'), 'SECRET_VIA_DIR_SWAP', 'utf8')
      const resolved = resolveStaticAsset(distDir, '/assets/app.js')
      expect(resolved.ok).toBe(true)
      if (!resolved.ok) return

      rmSync(join(distDir, 'assets'), { recursive: true, force: true })
      symlinkSync(outsideDir, join(distDir, 'assets'))

      const dirSwap = readResolvedStaticAsset(distDir, resolved)
      expect(dirSwap.ok).toBe(false)
      if (!dirSwap.ok) {
        expect(dirSwap.statusCode).toBe(403)
      }
    })

    it('returns 404 when resolved asset is replaced with a directory or removed before read', () => {
      const resolvedForDir = resolveStaticAsset(distDir, '/assets/app.js')
      expect(resolvedForDir.ok).toBe(true)
      if (resolvedForDir.ok) {
        rmSync(join(distDir, 'assets', 'app.js'))
        mkdirSync(join(distDir, 'assets', 'app.js'))
        const dirResult = readResolvedStaticAsset(distDir, resolvedForDir)
        expect(dirResult.ok).toBe(false)
        if (!dirResult.ok) {
          expect(dirResult.statusCode).toBe(404)
        }
      }

      const resolvedMissing = resolveStaticAsset(distDir, '/index.html')
      expect(resolvedMissing.ok).toBe(true)
      if (resolvedMissing.ok) {
        rmSync(join(distDir, 'index.html'))
        const missingResult = readResolvedStaticAsset(distDir, resolvedMissing)
        expect(missingResult.ok).toBe(false)
        if (!missingResult.ok) {
          expect(missingResult.statusCode).toBe(404)
        }
      }
    })

    it('returns 403 for symlinked dir and 404 for directory paths over HTTP via startTrayBridge', async () => {
      symlinkSync(outsideDir, join(distDir, 'dir-link'))
      const stub = await startStubServer((_req, res) => {
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end('{}')
      })
      activeServers.push(stub)

      const bridge = await startTrayBridge({ serverUrl: stub.url, distDir })
      activeBridges.push(bridge)

      const symlinkDirRes = await fetch(`${bridge.url}/dir-link/secret.txt`)
      expect(symlinkDirRes.status).toBe(403)
      expect(await symlinkDirRes.text()).not.toContain('SECRET_OUTSIDE_DIST')

      const dirRes = await fetch(`${bridge.url}/assets`)
      expect(dirRes.status).toBe(404)
    })
  })

  describe('PR #662 fixes: 413 size-cap handling, POSIX backslash filenames, and verifyNoSymlinkSegments traversal checks', () => {
    it('returns 413 Payload Too Large when fstatSync size exceeds maxBytes or readSync exceeds maxBytes', () => {
      writeFileSync(join(distDir, 'exact.txt'), '0123456789abcdef', 'utf8')
      writeFileSync(join(distDir, 'over-fstat.txt'), '0123456789abcdefX', 'utf8')

      const exactResolved = resolveStaticAsset(distDir, '/exact.txt')
      expect(exactResolved.ok).toBe(true)
      if (!exactResolved.ok) return
      const exactResult = readResolvedStaticAsset(distDir, exactResolved, { maxBytes: 16 })
      expect(exactResult.ok).toBe(true)

      const overFstatResolved = resolveStaticAsset(distDir, '/over-fstat.txt')
      expect(overFstatResolved.ok).toBe(true)
      if (!overFstatResolved.ok) return
      const fstatOverResult = readResolvedStaticAsset(distDir, overFstatResolved, { maxBytes: 16 })
      expect(fstatOverResult).toEqual({
        ok: false,
        statusCode: 413,
        message: 'Payload Too Large',
      })

      // Simulate a concurrent growth race where fstatSync sees <= maxBytes but readSync reads > maxBytes.
      fsHooks.fstatSizeOverride = 4
      try {
        const readOverResult = readResolvedStaticAsset(distDir, overFstatResolved, { maxBytes: 16 })
        expect(readOverResult).toEqual({
          ok: false,
          statusCode: 413,
          message: 'Payload Too Large',
        })
      } finally {
        fsHooks.fstatSizeOverride = undefined
      }
    })

    it('maps StaticAssetTooLargeError and ERR_STATIC_ASSET_TOO_LARGE explicitly to 413 in mapStaticAssetOpenError', () => {
      const capError = new StaticAssetTooLargeError('too large')
      expect(capError.code).toBe('ERR_STATIC_ASSET_TOO_LARGE')
      expect(mapStaticAssetOpenError(capError)).toEqual({
        ok: false,
        statusCode: 413,
        message: 'Payload Too Large',
      })

      const codedError = Object.assign(new Error('cap exceeded'), { code: 'ERR_STATIC_ASSET_TOO_LARGE' })
      expect(mapStaticAssetOpenError(codedError)).toEqual({
        ok: false,
        statusCode: 413,
        message: 'Payload Too Large',
      })
    })

    it('returns HTTP 413 Payload Too Large when serving an oversized static asset over startTrayBridge', async () => {
      const oversizedPath = join(distDir, 'oversized.bin')
      writeFileSync(oversizedPath, '', 'utf8')
      truncateSync(oversizedPath, MAX_PROXY_RESPONSE_BYTES + 1)

      const stub = await startStubServer((_req, res) => {
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end('{}')
      })
      activeServers.push(stub)

      const bridge = await startTrayBridge({ serverUrl: stub.url, distDir })
      activeBridges.push(bridge)

      const oversizedRes = await fetch(`${bridge.url}/oversized.bin`)
      expect(oversizedRes.status).toBe(413)
      expect(await oversizedRes.text()).toBe('Payload Too Large')
    })

    it('treats backslash as a filename character on POSIX across resolveStaticAsset, readResolvedStaticAsset, and HTTP', async () => {
      if (process.platform === 'win32') {
        for (const winBackslashPath of ['/foo\\bar.js', '/foo%5Cbar.js']) {
          const winResult = resolveStaticAsset(distDir, winBackslashPath)
          expect(winResult, winBackslashPath).toEqual({
            ok: false,
            statusCode: 403,
            message: 'Forbidden',
          })
        }
        return
      }

      const posixBackslashPath = join(distDir, 'foo\\bar.js')
      writeFileSync(posixBackslashPath, 'console.log("backslash-ok")', 'utf8')
      const nestedPosixBackslashPath = join(distDir, 'assets', 'nested\\asset.js')
      writeFileSync(nestedPosixBackslashPath, 'console.log("nested-backslash-ok")', 'utf8')

      for (const rawPath of ['/foo\\bar.js', '/foo%5Cbar.js']) {
        const resolved = resolveStaticAsset(distDir, rawPath)
        expect(resolved.ok, rawPath).toBe(true)
        if (!resolved.ok) return
        expect(resolved.filePath).toBe(posixBackslashPath)
        expect(resolved.contentType).toBe('application/javascript; charset=utf-8')

        const readResult = readResolvedStaticAsset(distDir, resolved)
        expect(readResult.ok, rawPath).toBe(true)
        if (readResult.ok) {
          expect(readResult.contents.toString('utf8')).toBe('console.log("backslash-ok")')
        }
      }

      const stub = await startStubServer((_req, res) => {
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end('{}')
      })
      activeServers.push(stub)

      const bridge = await startTrayBridge({ serverUrl: stub.url, distDir })
      activeBridges.push(bridge)

      const topLevelRes = await fetch(`${bridge.url}/foo%5Cbar.js`)
      expect(topLevelRes.status).toBe(200)
      expect(topLevelRes.headers.get('content-type')).toBe('application/javascript; charset=utf-8')
      expect(await topLevelRes.text()).toBe('console.log("backslash-ok")')

      const nestedRes = await fetch(`${bridge.url}/assets/nested%5Casset.js`)
      expect(nestedRes.status).toBe(200)
      expect(nestedRes.headers.get('content-type')).toBe('application/javascript; charset=utf-8')
      expect(await nestedRes.text()).toBe('console.log("nested-backslash-ok")')
    })

    it('maps present-but-unusable IO errors (EBUSY, EMFILE, ENFILE, EIO, EISDIR) to 403 Forbidden and missing errors to 404', async () => {
      for (const forbiddenCode of [
        'ERR_PATH_TRAVERSAL',
        'ELOOP',
        'EEXIST',
        'EACCES',
        'EPERM',
        'EBUSY',
        'EMFILE',
        'ENFILE',
        'EIO',
        'EISDIR',
      ]) {
        const codedError = Object.assign(new Error(`simulated ${forbiddenCode}`), { code: forbiddenCode })
        expect(mapStaticAssetOpenError(codedError), forbiddenCode).toEqual({
          ok: false,
          statusCode: 403,
          message: 'Forbidden',
        })
      }

      for (const missingCode of ['ENOENT', 'ENOTDIR']) {
        const codedError = Object.assign(new Error(`simulated ${missingCode}`), { code: missingCode })
        expect(mapStaticAssetOpenError(codedError), missingCode).toEqual({
          ok: false,
          statusCode: 404,
          message: 'Not found',
        })
      }

      const resolved = resolveStaticAsset(distDir, '/assets/app.js')
      expect(resolved.ok).toBe(true)
      if (!resolved.ok) return

      const stub = await startStubServer((_req, res) => {
        res.writeHead(200, { 'content-type': 'application/json' })
        res.end('{}')
      })
      activeServers.push(stub)

      const bridge = await startTrayBridge({ serverUrl: stub.url, distDir })
      activeBridges.push(bridge)

      for (const ioCode of ['EBUSY', 'EMFILE', 'ENFILE', 'EIO', 'EISDIR']) {
        fsHooks.openSyncError = Object.assign(new Error(`openSync ${ioCode}`), { code: ioCode })
        try {
          expect(readResolvedStaticAsset(distDir, resolved), ioCode).toEqual({
            ok: false,
            statusCode: 403,
            message: 'Forbidden',
          })

          const httpRes = await fetch(`${bridge.url}/assets/app.js`)
          expect(httpRes.status, ioCode).toBe(403)
          expect(await httpRes.text(), ioCode).toBe('Forbidden')
        } finally {
          fsHooks.openSyncError = undefined
        }
      }
    })

    it('catches PathTraversalError thrown by resolveStaticAsset inside serveStaticAsset and responds 403 instead of 500', async () => {
      const stub = await startStubServer((_req, res) => {
        try {
          serveStaticAsset(res, distDir, '/assets/app.js', () => {
            throw new PathTraversalError('simulated resolve traversal')
          })
        } catch {
          res.writeHead(500, { 'content-type': 'text/plain; charset=utf-8' })
          res.end('Internal Server Error')
        }
      })
      activeServers.push(stub)

      const res = await fetch(`${stub.url}/assets/app.js`)
      expect(res.status).toBe(403)
      expect(await res.text()).toBe('Forbidden')
    })

    it('rejects . and .. traversal segments directly inside verifyNoSymlinkSegments', () => {
      expect(verifyNoSymlinkSegments(distDir, ['assets', 'app.js'])).toEqual({ ok: true })

      for (const badSegments of [
        ['.'],
        ['..'],
        ['assets', '..'],
        ['assets', '..', 'index.html'],
        ['assets', '.'],
        [''],
        ['a/b'],
      ]) {
        let caught: unknown
        try {
          verifyNoSymlinkSegments(distDir, badSegments)
        } catch (error) {
          caught = error
        }
        expect(caught, JSON.stringify(badSegments)).toBeInstanceOf(PathTraversalError)
        expect((caught as PathTraversalError).code, JSON.stringify(badSegments)).toBe('ERR_PATH_TRAVERSAL')
      }

      expect(mapStaticAssetOpenError(new PathTraversalError('traversal'))).toEqual({
        ok: false,
        statusCode: 403,
        message: 'Forbidden',
      })
    })
  })
})
