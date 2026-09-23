import { createServer, type Server } from 'http'
import { execFile } from 'child_process'
import { readFile } from 'fs/promises'
import { existsSync } from 'fs'
import { createRequire } from 'node:module'
import { join, normalize, extname, dirname, sep } from 'path'
import { fileURLToPath } from 'url'
import { AddressInfo } from 'net'
import { applyHtmlBrand, BRAND } from '../brand-overlay.js'
import { REPORT_SCHEMA_VERSION } from '../analysis/report/types.js'
import { KyberBridge } from '../server/bridge.js'
import { handleKyberRequest } from '../server/routes.js'
import { formatValidViewForms, matchesViewPath } from '../server/view-paths.js'

const KYBERDASH_VERSION = String(
  (createRequire(import.meta.url)('../../package.json') as { version?: string }).version ?? '0.0.0',
)

const HERE = dirname(fileURLToPath(import.meta.url))

// Locate the built React dashboard (dist/dash). Works both when running from a
// published package, where tsup has bundled this module to dist/ and the built
// assets sit beside it, and from source, where this file is src/cli/web.ts and
// the assets are two levels up at dash/dist/dash (web/vite.config.ts's outDir).
function resolveDashDir(): string | null {
  const candidates = [
    process.env['KYBERDASH_DASH_DIR'],
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
  // execFile, not exec: the arguments go to the process as an argv array, so no
  // shell parses them and there is no command string for a URL to break out of.
  // This matters because the URL is no longer purely internal — `--view` puts a
  // user-supplied path into it (runWebDashboard validates it against
  // matchesViewPath first, but this must not depend on a check made elsewhere).
  // On Windows `start` is a cmd builtin rather than an executable, so cmd is the
  // program; the empty string is the window title `start` expects before a URL.
  const [program, args] =
    process.platform === 'darwin'
      ? ['open', [url]]
      : process.platform === 'win32'
        ? ['cmd', ['/c', 'start', '', url]]
        : ['xdg-open', [url]]
  try {
    execFile(program!, args as string[], () => {
      /* a browser that fails to launch is not an error the server should raise */
    })
  } catch {
    /* user can open it manually */
  }
}

export class UnknownViewError extends Error {
  readonly validForms: string

  constructor(view: string, validForms: string) {
    super(`unknown view "${view}". Valid forms: ${validForms}`)
    this.name = 'UnknownViewError'
    this.validForms = validForms
  }
}

function joinViewUrl(origin: string, view: string | undefined): string {
  if (view === undefined || view.trim() === '' || view.trim() === '/') return origin
  return `${origin}/${view.trim().replace(/^\/+/, '')}`
}

export async function runWebDashboard(opts: {
  port: number
  open: boolean
  view?: string
  kyberBridge?: KyberBridge
  openUrl?: (url: string) => void
  writeStdout?: (text: string) => void
}): Promise<Server> {
  if (opts.view !== undefined && !matchesViewPath(opts.view)) {
    throw new UnknownViewError(opts.view, formatValidViewForms())
  }

  const dashDir = resolveDashDir()
  const bridge = opts.kyberBridge ?? new KyberBridge()
  const writeStdout = opts.writeStdout ?? ((text: string) => { process.stdout.write(text) })

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
      // The detail goes to the operator's terminal, not down the wire: an error
      // message from the static-file path names real paths on this machine, and
      // the dashboard has no use for it beyond knowing the request failed.
      console.error(`${BRAND.cliName}: dashboard request failed:`, err)
      res.writeHead(500, { 'content-type': 'application/json' })
      res.end(JSON.stringify({ error: 'internal server error' }))
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
  // First, and machine-readable: the tray supervisor reads this line and
  // nowhere else, so a human banner printed first would look like a hang (R5.7).
  writeStdout(
    `${JSON.stringify({
      event: 'kyberdash.web.listening',
      url,
      pid: process.pid,
      version: KYBERDASH_VERSION,
      apiVersion: REPORT_SCHEMA_VERSION,
    })}\n`,
  )
  if (!dashDir) {
    writeStdout(`\n  Dashboard UI is not built. Run: cd dash && npm install && npm run build\n`)
  }
  writeStdout(`\n  ${BRAND.productName} dashboard at ${url}\n  Press Ctrl+C to stop.\n\n`)
  if (opts.open) (opts.openUrl ?? openBrowser)(joinViewUrl(url, opts.view))

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
