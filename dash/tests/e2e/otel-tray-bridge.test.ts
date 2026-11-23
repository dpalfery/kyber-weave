import { createServer, type IncomingMessage, type ServerResponse } from 'node:http'
import { mkdirSync, mkdtempSync, realpathSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import {
  MAX_PROXY_RESPONSE_BYTES,
  UpstreamResponseTooLargeError,
  buildTrayViewState,
  fetchTrayBridgeState,
  isValidApiVersion,
  isValidRefreshCadenceMinutes,
  parseTrayBridgeStateSnapshot,
  readBoundedResponseBuffer,
  resolveStaticAsset,
  startTrayBridge,
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

    it('rejects null bytes, rooted/absolute paths, backslashes, and parent traversal', () => {
      expect(resolveStaticAsset(distDir, '/%00').ok).toBe(false)
      expect(resolveStaticAsset(distDir, '/index.html\0.js').ok).toBe(false)

      for (const badPath of [
        '//etc/passwd',
        '/%2fetc/passwd',
        '/C:/Windows/win.ini',
        '/..\\outside\\secret.txt',
        '/../outside/secret.txt',
        '/assets/../../outside/secret.txt',
      ]) {
        const result = resolveStaticAsset(distDir, badPath)
        expect(result.ok, badPath).toBe(false)
        if (!result.ok) {
          expect(result.statusCode, badPath).toBe(403)
        }
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
})
