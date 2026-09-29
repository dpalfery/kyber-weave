// Tests for embedded dashboard serving from SEA assets. Mocks node:sea to provide
// a kyberdash-web/1 asset. RED: fails on the unchanged web.ts, which does not
// read embedded assets.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mkdtemp, rm, writeFile } from 'fs/promises'
import { tmpdir } from 'os'
import { join } from 'path'
import type { AddressInfo } from 'net'
import type { Server } from 'http'

import { runWebDashboard } from './web.js'
import { KyberBridge } from '../server/bridge.js'
import { BRAND } from '../brand-overlay.js'

// Mock node:sea before importing the module under test
const mockGetAsset = vi.fn((_key: string): string | Buffer | undefined => undefined)
const mockIsSea = vi.fn(() => false)

vi.mock('node:sea', () => ({
  isSea: mockIsSea,
  getAsset: mockGetAsset,
}))

// Create a minimal kyberdash-web/1 fixture: index.html with sentinel + an asset
function createWebAsset(): string {
  const files: Record<string, string> = {
    'index.html': '<!doctype html><html><head><title>Test</title></head><body id="SENTINEL-ABC123">Test Dashboard</body></html>',
    'assets/app-abc123.js': 'console.log("app");',
  }

  // Convert files to base64
  const filesMap: Record<string, string> = {}
  for (const [path, content] of Object.entries(files)) {
    // Use POSIX path as key
    const posixPath = path.replace(/\\/g, '/')
    filesMap[posixPath] = Buffer.from(content).toString('base64')
  }

  // Create the asset format with sorted keys
  const asset = {
    format: 'kyberdash-web/1',
    files: filesMap,
  }

  return JSON.stringify(asset)
}

describe('embedded web dashboard (SEA asset)', () => {
  let server: Server
  let port: number
  let base: string
  let tempHome: string

  beforeEach(async () => {
    // Create a temp HOME for isolation
    tempHome = await mkdtemp(join(tmpdir(), 'kyberdash-sea-test-'))

    // Set up mocks for this test
    mockIsSea.mockReturnValue(true)
    mockGetAsset.mockImplementation((key: string) => {
      if (key === 'web.json') {
        return Buffer.from(createWebAsset(), 'utf8')
      }
      return undefined
    })

    const testBridge = new KyberBridge({
      ratesPath: join(tempHome, 'rates.json'),
    })

    server = await runWebDashboard({
      port: 0,
      open: false,
      kyberBridge: testBridge,
      writeStdout: () => {},
    })
    port = (server.address() as AddressInfo).port
    base = `http://127.0.0.1:${port}`
  })

  afterEach(async () => {
    await new Promise<void>((resolve) => server.close(() => resolve()))
    if (tempHome) {
      await rm(tempHome, { recursive: true, force: true })
    }
    mockIsSea.mockReset()
    mockGetAsset.mockReset()
  })

  it('GET / returns 200 with sentinel in branded HTML', async () => {
    const res = await fetch(`${base}/`)
    expect(res.status).toBe(200)
    expect(res.headers.get('content-type')).toContain('text/html')
    const html = await res.text()
    expect(html).toContain('SENTINEL-ABC123')
    expect(html).toContain(BRAND.htmlTitle)
  })

  it('GET /assets/app-abc123.js serves the asset with JavaScript content type', async () => {
    const res = await fetch(`${base}/assets/app-abc123.js`)
    expect(res.status).toBe(200)
    expect(res.headers.get('content-type')).toContain('text/javascript')
    const content = await res.text()
    expect(content).toContain('console.log("app")')
  })

  it('GET /deep/client/route returns index.html for SPA routing', async () => {
    const res = await fetch(`${base}/deep/client/route`)
    expect(res.status).toBe(200)
    expect(res.headers.get('content-type')).toContain('text/html')
    const html = await res.text()
    expect(html).toContain('SENTINEL-ABC123')
  })

  it('GET /..%2f..%2fpackage.json returns index, rejects path traversal', async () => {
    const res = await fetch(`${base}/..%2f..%2fpackage.json`)
    expect(res.status).toBe(200)
    expect(res.headers.get('content-type')).toContain('text/html')
    const html = await res.text()
    // Should serve index, not a real file
    expect(html).toContain('SENTINEL-ABC123')
    // Should not contain package.json content (security check)
    expect(html).not.toContain('"private"')
  })
})

describe('embedded dashboard: SEA without web.json', () => {
  let tempHome: string

  beforeEach(async () => {
    tempHome = await mkdtemp(join(tmpdir(), 'kyberdash-no-asset-'))
    mockIsSea.mockReturnValue(true)
    mockGetAsset.mockReturnValue(undefined)
  })

  afterEach(async () => {
    if (tempHome) {
      await rm(tempHome, { recursive: true, force: true })
    }
    mockIsSea.mockReset()
    mockGetAsset.mockReset()
  })

  it('stdout hint does not mention npm install', async () => {
    let stdout = ''
    const testBridge = new KyberBridge({
      ratesPath: join(tempHome, 'rates.json'),
    })

    const server = await runWebDashboard({
      port: 0,
      open: false,
      kyberBridge: testBridge,
      writeStdout: (text: string) => {
        stdout += text
      },
    })

    try {
      // Check the stdout hint
      const lines = stdout.split('\n')
      const hintLine = lines.find((line) => line.includes('Dashboard UI'))
      // In SEA without web.json, hint should NOT say to run npm install
      expect(hintLine).not.toContain('npm install')
    } finally {
      await new Promise<void>((resolve) => server.close(() => resolve()))
    }
  })
})

describe('embedded dashboard: KYBERDASH_DASH_DIR override', () => {
  let tempHome: string

  beforeEach(async () => {
    tempHome = await mkdtemp(join(tmpdir(), 'kyberdash-override-'))
  })

  afterEach(async () => {
    if (tempHome) {
      await rm(tempHome, { recursive: true, force: true })
    }
    mockIsSea.mockReset()
    mockGetAsset.mockReset()
  })

  it('KYBERDASH_DASH_DIR override wins over embedded asset', async () => {
    const dashDir = await mkdtemp(join(tmpdir(), 'override-dash-'))
    try {
      await writeFile(join(dashDir, 'index.html'), '<!doctype html><title>Override</title><body>OVERRIDE-WINS</body>')

      const prevEnv = process.env['KYBERDASH_DASH_DIR']
      process.env['KYBERDASH_DASH_DIR'] = dashDir

      try {
        mockIsSea.mockReturnValue(true)
        mockGetAsset.mockReturnValue(Buffer.from(createWebAsset(), 'utf8'))

        const testBridge = new KyberBridge({
          ratesPath: join(tempHome, 'rates.json'),
        })

        const server = await runWebDashboard({
          port: 0,
          open: false,
          kyberBridge: testBridge,
          writeStdout: () => {},
        })

        try {
          const port = (server.address() as AddressInfo).port
          const base = `http://127.0.0.1:${port}`
          const res = await fetch(`${base}/`)
          expect(res.status).toBe(200)
          const html = await res.text()
          expect(html).toContain('OVERRIDE-WINS')
          expect(html).not.toContain('SENTINEL-ABC123')
        } finally {
          await new Promise<void>((resolve) => server.close(() => resolve()))
        }
      } finally {
        if (prevEnv === undefined) delete process.env['KYBERDASH_DASH_DIR']
        else process.env['KYBERDASH_DASH_DIR'] = prevEnv
      }
    } finally {
      await rm(dashDir, { recursive: true, force: true })
    }
  })

  it('KYBERDASH_DASH_DIR set to a directory with no index.html warns and falls through to the embedded asset', async () => {
    const rejectedDir = await mkdtemp(join(tmpdir(), 'override-no-index-'))
    try {
      // Deliberately no index.html written here: this is the rejection this test covers.

      const prevEnv = process.env['KYBERDASH_DASH_DIR']
      process.env['KYBERDASH_DASH_DIR'] = rejectedDir

      try {
        mockIsSea.mockReturnValue(true)
        mockGetAsset.mockReturnValue(Buffer.from(createWebAsset(), 'utf8'))

        const testBridge = new KyberBridge({
          ratesPath: join(tempHome, 'rates.json'),
        })

        const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {})

        const server = await runWebDashboard({
          port: 0,
          open: false,
          kyberBridge: testBridge,
          writeStdout: () => {},
        })

        try {
          // The rejected override must not silently vanish: an operator who set a
          // typo'd or not-yet-built KYBERDASH_DASH_DIR needs to see exactly what
          // path was tried and rejected, distinct from the two SEA-branch warnings.
          expect(warnSpy).toHaveBeenCalledWith(expect.stringContaining(rejectedDir))
          expect(warnSpy.mock.calls.some(([msg]) => typeof msg === 'string' && msg.startsWith(BRAND.cliName))).toBe(
            true,
          )

          // Falls through to the next resolution step (the embedded SEA asset)
          // exactly as before: precedence and behavior are unchanged, only the
          // missing log line was added.
          const port = (server.address() as AddressInfo).port
          const base = `http://127.0.0.1:${port}`
          const res = await fetch(`${base}/`)
          expect(res.status).toBe(200)
          const html = await res.text()
          expect(html).toContain('SENTINEL-ABC123')
        } finally {
          warnSpy.mockRestore()
          await new Promise<void>((resolve) => server.close(() => resolve()))
        }
      } finally {
        if (prevEnv === undefined) delete process.env['KYBERDASH_DASH_DIR']
        else process.env['KYBERDASH_DASH_DIR'] = prevEnv
      }
    } finally {
      await rm(rejectedDir, { recursive: true, force: true })
    }
  })
})
