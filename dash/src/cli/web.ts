import { createServer, type Server } from 'http'
import { exec } from 'child_process'
import { readFile } from 'fs/promises'
import { existsSync } from 'fs'
import { join, normalize, extname, dirname, sep } from 'path'
import { fileURLToPath } from 'url'
import { AddressInfo } from 'net'
import { applyHtmlBrand, BRAND } from '../brand-overlay.js'
import { KyberBridge } from '../server/bridge.js'
import { handleKyberRequest } from '../server/routes.js'

const HERE = dirname(fileURLToPath(import.meta.url))

// Locate the built React dashboard (dist/dash). Works both when running from a
// published package, where tsup has bundled this module to dist/ and the built
// assets sit beside it, and from source, where this file is src/cli/web.ts and
// the assets are two levels up at dash/dist/dash (web/vite.config.ts's outDir).
function resolveDashDir(): string | null {
  const candidates = [
    process.env['CODEBURN_DASH_DIR'],
    join(HERE, 'dash'),
    join(HERE, '..', '..', 'dist', 'dash'),
  ].filter(Boolean) as string[]
  for (const dir of candidates) {
    if (existsSync(join(dir, 'index.html'))) return dir
  }
  return null
}

const CONTENT_TYPES: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.json': 'application/json; charset=utf-8',
  '.woff2': 'font/woff2',
  '.woff': 'font/woff',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.map': 'application/json',
}

const NOT_BUILT_PAGE =
  '<!doctype html><meta charset="utf-8">' +
  '<body style="font-family:system-ui;background:#0a0a0b;color:#e7e7ea;padding:48px;line-height:1.6">' +
  '<h2>Dashboard not built yet</h2>' +
  '<p>Build the web UI once, then reload:</p>' +
  '<pre style="background:#141417;padding:12px 16px;border-radius:8px;color:#ff8c42">cd dash &amp;&amp; npm install &amp;&amp; npm run build</pre>' +
  '<p>The CLI keeps serving the live data API in the meantime.</p></body>'

function openBrowser(url: string): void {
  const cmd = process.platform === 'darwin' ? 'open' : process.platform === 'win32' ? 'start ""' : 'xdg-open'
  try {
    // command is internal, not user-controlled — cmd is platform branch, url is localhost dashboard
    exec(`${cmd} ${url}`) // nosemgrep: javascript.lang.security.detect-child-process, detect-child-process
  } catch {
    /* user can open it manually */
  }
}

export async function runWebDashboard(opts: {
  port: number
  open: boolean
  kyberBridge?: KyberBridge
}): Promise<Server> {
  const dashDir = resolveDashDir()
  const bridge = opts.kyberBridge ?? new KyberBridge()

  const serveIndexHtml = async (res: import('http').ServerResponse, filePath: string): Promise<void> => {
    const html = applyHtmlBrand(await readFile(filePath, 'utf8'))
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' })
    res.end(html)
  }

  const server = createServer(async (req, res) => {
    try {
      const url = new URL(req.url ?? '/', 'http://localhost')

      // Loopback-only server. Reject any request not addressed to localhost
      // (defeats DNS rebinding, which would otherwise let a website you visit
      // read your local telemetry and stored context) and any cross-origin
      // request (CSRF). The stored content is unredacted, so this guard is what
      // keeps it on your machine.
      const reqHost = (req.headers.host ?? '').replace(/:\d+$/, '')
      const loopback = reqHost === '127.0.0.1' || reqHost === 'localhost' || reqHost === '::1' || reqHost === '[::1]'
      const origin = req.headers.origin
      const originOk = !origin || /^https?:\/\/(127\.0\.0\.1|localhost|\[::1\])(:\d+)?$/.test(origin)
      if (!loopback || !originOk) {
        res.writeHead(403, { 'content-type': 'text/plain' })
        res.end('Forbidden')
        return
      }

      if (handleKyberRequest(req, res, url, bridge)) return

      if (!dashDir) {
        res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' })
        res.end(NOT_BUILT_PAGE)
        return
      }

      let pathname = decodeURIComponent(url.pathname)
      if (pathname === '/' || pathname === '') pathname = '/index.html'
      const filePath = normalize(join(dashDir, pathname))
      if (filePath !== dashDir && !filePath.startsWith(dashDir + sep)) {
        res.writeHead(403)
        res.end('Forbidden')
        return
      }
      try {
        if (extname(filePath) === '.html') {
          await serveIndexHtml(res, filePath)
        } else {
          const buf = await readFile(filePath)
          res.writeHead(200, { 'content-type': CONTENT_TYPES[extname(filePath)] ?? 'application/octet-stream' })
          res.end(buf)
        }
      } catch {
        // Unknown path: serve index.html so the SPA can route it.
        await serveIndexHtml(res, join(dashDir, 'index.html'))
      }
    } catch (err) {
      res.writeHead(500, { 'content-type': 'application/json' })
      res.end(JSON.stringify({ error: err instanceof Error ? err.message : String(err) }))
    }
  })

  const port = await new Promise<number>((resolve, reject) => {
    server.once('error', (err: NodeJS.ErrnoException) => {
      if (err.code === 'EADDRINUSE') {
        server.listen(0, '127.0.0.1', () => resolve((server.address() as AddressInfo).port))
      } else {
        reject(err)
      }
    })
    server.listen(opts.port, '127.0.0.1', () => resolve((server.address() as AddressInfo).port))
  })
  // Durable handler so a post-bind socket error never crashes the process.
  server.on('error', () => {})

  const url = `http://127.0.0.1:${port}`
  if (!dashDir) {
    process.stdout.write(`\n  Dashboard UI is not built. Run: cd dash && npm install && npm run build\n`)
  }
  process.stdout.write(`\n  ${BRAND.productName} dashboard at ${url}\n  Press Ctrl+C to stop.\n\n`)
  if (opts.open) openBrowser(url)

  const onSigint = () => {
    bridge.close()
    process.exit(0)
  }
  process.on('SIGINT', onSigint)

  // Ensure bridge is cleanly closed and signal handler removed when server stops
  server.on('close', () => {
    process.off('SIGINT', onSigint)
    bridge.close()
  })

  return server
}
