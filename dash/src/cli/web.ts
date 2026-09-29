import { createServer, type Server } from 'http'
import { execFile } from 'child_process'
import { readFile } from 'fs/promises'
import { existsSync } from 'fs'
import { createRequire } from 'node:module'
import { join, normalize, extname, dirname, sep, posix as posixPath } from 'path'
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

const EMBEDDED_WEB_ASSET_KEY = 'web.json'
const EMBEDDED_WEB_ASSET_FORMAT = 'kyberdash-web/1'

interface EmbeddedWebAsset {
  format: string
  files: Record<string, string>
}

function isEmbeddedWebAsset(value: unknown): value is EmbeddedWebAsset {
  if (typeof value !== 'object' || value === null) return false
  const candidate = value as { format?: unknown; files?: unknown }
  return (
    candidate.format === EMBEDDED_WEB_ASSET_FORMAT &&
    typeof candidate.files === 'object' &&
    candidate.files !== null
  )
}

// Normalizes a raw request path (or a packed-asset key) into the form used as a key in
// the embedded-asset map: POSIX separators, no leading slash, '.' segments collapsed. A
// '..' segment that would escape the root is left in an unresolved (and therefore
// unmatchable) form by `posix.normalize` rather than resolved against the real
// filesystem, so a traversal attempt can only miss the map - it can never address a file
// outside it, because the map holds only real relative paths written by
// `pack-sea-web.mjs`.
function normalizeEmbeddedPath(raw: string): string {
  const posixLike = raw.replace(/\\/g, '/')
  const normalized = posixPath.normalize(posixLike).replace(/^\/+/, '')
  return normalized === '.' ? '' : normalized
}

function parseEmbeddedWebAssets(raw: string): Map<string, Buffer> | null {
  let parsed: unknown
  try {
    parsed = JSON.parse(raw)
  } catch {
    return null
  }
  if (!isEmbeddedWebAsset(parsed)) return null

  const files = new Map<string, Buffer>()
  for (const [key, value] of Object.entries(parsed.files)) {
    if (typeof value !== 'string') continue
    files.set(normalizeEmbeddedPath(key), Buffer.from(value, 'base64'))
  }
  return files
}

type StaticSource = { kind: 'dir'; dir: string } | { kind: 'embedded'; files: Map<string, Buffer> }

// Locate the static source for the built React dashboard. The order is:
//
// 1. `KYBERDASH_DASH_DIR`, an explicit override that always wins.
// 2. The `web.json` SEA asset embedded at release-build time (D1-A). Parsed once here,
//    at server start, not per request.
// 3. The two on-disk candidates a non-SEA process can have: a published package, where
//    tsup has bundled this module to dist/ and the built assets sit beside it, and a
//    source checkout, where this file is src/cli/web.ts and the assets are two levels up
//    at dash/dist/dash (web/vite.config.ts's outDir).
//
// A SEA binary has no source tree beside it, so step 3 does not apply once step 1 has
// been ruled out: a released binary with no embedded dashboard gets the "not built" page
// rather than an accidental match against an unrelated directory that happens to sit next
// to the installed executable.
//
// `isSea` and `getEmbeddedAsset` are passed in rather than imported statically here: `web.ts`
// is imported by test files that mock the `node:sea` builtin at the top of the file, and a
// static top-level `import '../sea.js'` would resolve `node:sea` during module linking, before
// those tests' own mock functions exist. `runWebDashboard` loads `../sea.js` with a dynamic
// `import()` instead, deferring that resolution until the function actually runs.
function resolveStaticSource(
  isSea: () => boolean,
  getEmbeddedAsset: (key: string) => string | undefined,
): StaticSource | null {
  const override = process.env['KYBERDASH_DASH_DIR']
  if (override) {
    if (existsSync(join(override, 'index.html'))) {
      return { kind: 'dir', dir: override }
    }
    // The override is set but rejected: without this, an operator who set a
    // typo'd or not-yet-built KYBERDASH_DASH_DIR gets a dashboard served from
    // the embedded asset (or a dev candidate) with no sign their override did
    // nothing. Logged once here, at server start, not per request — same as
    // the two SEA-branch causes below.
    console.warn(
      `${BRAND.cliName}: KYBERDASH_DASH_DIR is set to "${override}", but no usable index.html was found there`,
    )
  }

  if (isSea()) {
    const raw = getEmbeddedAsset(EMBEDDED_WEB_ASSET_KEY)
    if (raw === undefined) {
      // Distinct from the malformed case below: this SEA binary was built with no
      // web.json asset embedded at all (e.g. pack-sea-web.mjs did not run), so there is
      // nothing to parse. Logged once here, at server start, not per request.
      console.warn(
        `${BRAND.cliName}: running as a packaged binary, but no "${EMBEDDED_WEB_ASSET_KEY}" dashboard asset is embedded`,
      )
      return null
    }
    const files = parseEmbeddedWebAssets(raw)
    if (!files || !files.has('index.html')) {
      // Distinct from the missing-asset case above: a web.json asset is present, but it
      // either failed to parse (bad JSON, wrong format string, missing/invalid `files`)
      // or parsed without an `index.html` entry. Either way, the embedded asset itself is
      // the problem, not its absence.
      console.warn(
        `${BRAND.cliName}: embedded "${EMBEDDED_WEB_ASSET_KEY}" dashboard asset is present but malformed (failed to parse or missing index.html)`,
      )
      return null
    }
    return { kind: 'embedded', files }
  }

  const candidates = [join(HERE, 'dash'), join(HERE, '..', '..', 'dist', 'dash')]
  for (const dir of candidates) {
    if (existsSync(join(dir, 'index.html'))) return { kind: 'dir', dir }
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

function notBuiltPage(hintHtml: string): string {
  return (
    '<!doctype html><meta charset="utf-8">' +
    '<body style="font-family:system-ui;background:#0a0a0b;color:#e7e7ea;padding:48px;line-height:1.6">' +
    '<h2>Dashboard not built yet</h2>' +
    hintHtml +
    '<p>The CLI keeps serving the live data API in the meantime.</p></body>'
  )
}

// Two hints for two causes of "no static source found" (resolveStaticSource()
// returned null): a source checkout or published package that has not run its web
// build, versus a released SEA binary with no `web.json` asset embedded. The SEA case
// has no source tree to build, so its hint must not tell the operator to run `npm`.
const DEV_NOT_BUILT_HINT_HTML =
  '<p>Build the web UI once, then reload:</p>' +
  '<pre style="background:#141417;padding:12px 16px;border-radius:8px;color:#ff8c42">cd dash &amp;&amp; npm install &amp;&amp; npm run build</pre>'
const SEA_NOT_BUILT_HINT_HTML =
  '<p>This build has no web dashboard embedded. Install a release that includes it, ' +
  'or set <code>KYBERDASH_DASH_DIR</code> to a built <code>dist/dash</code> directory.</p>'

const DEV_NOT_BUILT_HINT_TEXT = 'Dashboard UI is not built. Run: cd dash && npm install && npm run build'
const SEA_NOT_BUILT_HINT_TEXT = 'Dashboard UI is not embedded in this build. Install a release that includes it.'

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

  // Dynamic import, not a static top-level one: see the comment on resolveStaticSource.
  const { runningAsSea, embeddedTextAsset } = await import('../sea.js')

  // Resolved once, at server start: the embedded-asset case parses `web.json` here
  // rather than per request (D1-A).
  const staticSource = resolveStaticSource(runningAsSea, embeddedTextAsset)
  const bridge = opts.kyberBridge ?? new KyberBridge()
  const writeStdout = opts.writeStdout ?? ((text: string) => { process.stdout.write(text) })

  const serveIndexHtml = async (res: import('http').ServerResponse, filePath: string): Promise<void> => {
    const html = applyHtmlBrand(await readFile(filePath, 'utf8'))
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' })
    res.end(html)
  }

  const serveEmbedded = (res: import('http').ServerResponse, files: Map<string, Buffer>, key: string): void => {
    const buf = files.get(key)
    if (buf === undefined) {
      // Unknown path, including any traversal attempt: the map holds only real
      // relative paths written by pack-sea-web.mjs, so a miss can only mean the SPA
      // should route this client-side. It never reaches a file outside the map.
      const indexHtml = applyHtmlBrand(files.get('index.html')!.toString('utf8'))
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' })
      res.end(indexHtml)
      return
    }
    if (extname(key) === '.html') {
      const html = applyHtmlBrand(buf.toString('utf8'))
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' })
      res.end(html)
    } else {
      res.writeHead(200, { 'content-type': CONTENT_TYPES[extname(key)] ?? 'application/octet-stream' })
      res.end(buf)
    }
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

      if (!staticSource) {
        res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' })
        res.end(notBuiltPage(runningAsSea() ? SEA_NOT_BUILT_HINT_HTML : DEV_NOT_BUILT_HINT_HTML))
        return
      }

      if (staticSource.kind === 'embedded') {
        let pathname = decodeURIComponent(url.pathname)
        if (pathname === '/' || pathname === '') pathname = 'index.html'
        serveEmbedded(res, staticSource.files, normalizeEmbeddedPath(pathname))
        return
      }

      const dashDir = staticSource.dir
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
  if (!staticSource) {
    writeStdout(`\n  ${runningAsSea() ? SEA_NOT_BUILT_HINT_TEXT : DEV_NOT_BUILT_HINT_TEXT}\n`)
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
