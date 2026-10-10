import { createServer, type Server } from 'http'
import { randomBytes } from 'crypto'
import { execFile } from 'child_process'
import { readFile } from 'fs/promises'
import { closeSync, existsSync, fchmodSync, lstatSync, mkdirSync, openSync, readFileSync, renameSync, rmSync, writeFileSync } from 'fs'
import { createRequire } from 'node:module'
import { join, normalize, extname, dirname, sep, posix as posixPath } from 'path'
import { homedir } from 'node:os'
import { fileURLToPath } from 'url'
import { AddressInfo } from 'net'
import { applyHtmlBrand, BRAND } from '../brand-overlay.js'
import { REPORT_SCHEMA_VERSION } from '../analysis/report/types.js'
import { KyberBridge } from '../server/bridge.js'
import {
  handleKyberRequest,
  ERR_SERVICES_STARTING as ROUTE_ERR_SERVICES_STARTING,
} from '../server/routes.js'
import { CanonStore } from '../canon/store.js'
import { resolveCanonDbPath } from '../canon/paths.js'
import { JobHost, type JobSpawner } from '../jobs/host.js'
import { ReceiverHost, type ReceiverProber } from '../jobs/receiver-host.js'
import { runMaintenancePass } from '../refresh/folder-import.js'
import { formatValidViewForms, matchesViewPath } from '../server/view-paths.js'
import { currentSpawnRuntime, resolveJobSpawnTarget } from './spawn-target.js'

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

/**
 * How often the receiver host re-probes the OTLP port. Short enough that a receiver which
 * dies is replaced promptly, long enough that a machine with no receiver is not probing
 * every second for the whole day.
 */
const RECEIVER_POLL_MS = 10_000

/** `server.json`: how the tray finds a running server without parsing its stdout. */
const SERVER_FILE = 'server.json'

/**
 * Routes that cannot answer at all until the JobHost is up: they report or drive jobs.
 * A fixed 503 during the (short) start-up window says "not yet", where the routes' own
 * unmounted answer would say "does not exist" about a server that is right there.
 */
const HOSTED_ONLY_ROUTES: ReadonlySet<string> = new Set([
  '/api/kyber/jobs',
  '/api/kyber/refresh',
  '/api/kyber/import-history',
])

/** Fixed body for that window — never a host or lease error string. Imported from the routes, so there is one wording. */
const ERR_SERVICES_STARTING = ROUTE_ERR_SERVICES_STARTING

/** The body of a published `server.json`, as far as ownership goes. */
type ServerRecord = { pid: number; url?: string }

/**
 * What `server.json` currently says, or `null` when it is absent, unreadable, corrupt or
 * of a shape this version does not recognise. Same failure handling as
 * `readLockHolder`: a body nobody can parse is no evidence of an owner.
 */
function readServerRecord(path: string): ServerRecord | null {
  try {
    const parsed = JSON.parse(readFileSync(path, 'utf-8')) as Partial<ServerRecord>
    if (typeof parsed?.pid !== 'number' || !Number.isFinite(parsed.pid)) return null
    return { pid: parsed.pid, ...(typeof parsed.url === 'string' ? { url: parsed.url } : {}) }
  } catch {
    return null
  }
}

/**
 * The state dir, created if it is missing. `null` on success, the failing errno code on
 * failure - a caller reports the code and carries on rather than refusing to serve.
 *
 * `recursive` because the parent may be missing too (`--state-dir` under a home that has
 * no `.kyberdash`), and 0o700 because this directory holds the pid and loopback port of a
 * running server plus the user's stored context.
 */
function ensureStateDir(dir: string): string | null {
  try {
    mkdirSync(dir, { recursive: true, mode: 0o700 })
    return null
  } catch (err) {
    return (err as NodeJS.ErrnoException).code ?? 'EUNKNOWN'
  }
}

/** The one line an operator sees when the attach point could not be published. */
function publishWarning(code: string): string {
  return `${BRAND.cliName}: could not publish ${SERVER_FILE} (${code}): the tray cannot attach to this server`
}

/**
 * Publishes `server.json`, and NEVER throws.
 *
 * A missing attach point is worth one line of stderr; it is not worth a dead dashboard.
 * The dashboard itself is reachable over the loopback URL the listening line carries - the
 * tray launches a server by that line and can attach to a running one by it too - so an
 * unwritable state dir degrades tray attach and nothing else. The earlier contract closed
 * the socket on a publish failure, which turned "the tray cannot find me" into "the server
 * is gone", and broke the self-update smoke run outright: its temp HOME has no
 * `~/.kyberdash` until something creates it.
 *
 * Race-free by construction: the body goes to a fresh temp file in the SAME directory
 * (so the swap is atomic and cannot cross a filesystem), and `rename` replaces the
 * directory entry - it never follows a symlink, so even a link planted between the check
 * below and the rename cannot redirect the write somewhere else. The `lstat` is therefore
 * a courtesy, not the safety argument: it only decides whether to warn about a link.
 *
 * 0o600 at exclusive creation, and chmod'd on the handle rather than the path, so a
 * leftover left world-readable is tightened and no path is re-resolved to do it.
 */
function publishServerRecord(path: string, body: string): void {
  let linked = false
  try {
    linked = lstatSync(path).isSymbolicLink()
  } catch {
    /* absent, or unreadable - the write below is what decides */
  }
  if (linked) {
    console.error(`${BRAND.cliName}: ${path} is a symbolic link; not publishing server.json through it`)
    return
  }
  const temp = join(dirname(path), `.${SERVER_FILE}.${process.pid}.${randomBytes(6).toString('hex')}.tmp`)
  try {
    const fd = openSync(temp, 'wx', 0o600)
    try {
      writeFileSync(fd, body)
      fchmodSync(fd, 0o600)
    } finally {
      closeSync(fd)
    }
    renameSync(temp, path)
  } catch (err) {
    rmSync(temp, { force: true })
    console.error(publishWarning((err as NodeJS.ErrnoException).code ?? 'EUNKNOWN'))
  }
}

/**
 * Whether `pid` is a live process other than this one. Mirrors
 * `refresh/lock.ts`'s `pidLooksAlive`, including the EPERM-means-already-alive case on
 * POSIX and signal 0 as an existence test on Windows. Our own pid is deliberately not
 * "alive" here: a file carrying it is this process's own, not a foreign server's.
 */
function pidLooksAlive(pid: number): boolean {
  if (!Number.isInteger(pid) || pid <= 0 || pid === process.pid) return false
  try {
    process.kill(pid, 0)
    return true
  } catch (err) {
    return (err as NodeJS.ErrnoException).code === 'EPERM'
  }
}

/**
 * The injectable seams of the hosted services (architecture rule R1). Every one is
 * optional and every production default is the real thing, so the tests can run the whole
 * lifecycle - spawn, publish, close - without a child process, a real port, or a real home
 * directory.
 */
export type WebDashboardServices = {
  /** The store the hosts read settings from. Defaults to a read-write canon.db. */
  store?: CanonStore
  /** Where server.json and jobs.lock live. Defaults to ~/.kyberdash. */
  stateDir?: string
  /**
   * The executable the hosts spawn, used verbatim with no wrapper arguments. Defaults to
   * this process's own CLI re-run in the way this process was launched - see
   * `resolveJobSpawnTarget`.
   */
  program?: string
  jobSpawner?: JobSpawner
  receiverSpawner?: JobSpawner
  receiverProber?: ReceiverProber
  /**
   * The periodic maintenance pass the JobHost runs on its first tick. Defaults to the
   * central module's own; injectable so a test can hold the tick open and prove the
   * listening line does not wait for it.
   */
  maintenance?: () => Promise<void>
}

/**
 * The hosted background services for one state directory, shared by every server in this
 * process that points at it.
 *
 * Two hosts over one state dir would be a bug even in principle - the jobs lease is meant
 * to make the second one stand down - but in-process the lease serializes instead of
 * standing down, so the second `JobHost.start()` would wait out the first one's entire
 * lease lifetime and the server would never finish starting. Sharing one host pair per
 * state dir states the rule directly: at most one host, per state dir, per process.
 */
type HostedServices = {
  readonly store: CanonStore
  readonly jobHost: JobHost
  readonly receiverHost: ReceiverHost
  /** The seams this pair was built from, so a caller that injects different ones cannot be handed another's hosts. */
  readonly seams: HostedSeams
  receiverTimer?: NodeJS.Timeout
  receiverRetry?: NodeJS.Timeout
  refs: number
}

/** Everything a host pair is built from, compared by identity to decide reuse. */
type HostedSeams = {
  readonly store: CanonStore
  readonly program: string
  readonly programArgs: readonly string[]
  readonly jobSpawner: JobSpawner | undefined
  readonly receiverSpawner: JobSpawner | undefined
  readonly receiverProber: ReceiverProber | undefined
  readonly maintenance: (() => Promise<void>) | undefined
}

function sameSeams(a: HostedSeams, b: HostedSeams): boolean {
  return a.store === b.store
    && a.program === b.program
    && a.programArgs.length === b.programArgs.length
    && a.programArgs.every((arg, index) => arg === b.programArgs[index])
    && a.jobSpawner === b.jobSpawner
    && a.receiverSpawner === b.receiverSpawner
    && a.receiverProber === b.receiverProber
    && a.maintenance === b.maintenance
}

const hostedServices = new Map<string, HostedServices>()

/**
 * Pairs still being started, keyed by state dir. A pair is published only once its hosts
 * are up, so this - not the published map - is what two servers racing to start over one
 * state dir join, and it is the only thing that stops a second host pair (a second poll
 * timer, a second lease attempt) from being built underneath the first.
 */
const hostedServicesStarting = new Map<string, Promise<HostedServices>>()

/**
 * The default store is one handle per process, not one per server: a second handle on the
 * same canon.db is a second SQLite connection to the same file for no benefit.
 */
let defaultStore: CanonStore | null = null
function resolveHostStore(injected: CanonStore | undefined): CanonStore {
  if (injected !== undefined) return injected
  defaultStore ??= new CanonStore(resolveCanonDbPath())
  return defaultStore
}

/**
 * A poll that reports a backoff delay schedules its own retry, so a receiver that keeps
 * failing to start is not retried on every tick. Exactly one retry is ever pending.
 */
function startReceiverPolling(services: HostedServices): void {
  const poll = (): void => {
    void services.receiverHost.poll().then((delay) => {
      if (services.receiverRetry !== undefined) {
        clearTimeout(services.receiverRetry)
        services.receiverRetry = undefined
      }
      if (delay === null || delay <= 0) return
      services.receiverRetry = setTimeout(poll, delay)
      // Never hold the event loop open for a retry; shutdown is the exit path.
      services.receiverRetry.unref()
    }, () => { /* a failed poll is not a reason to stop polling */ })
  }
  poll()
  services.receiverTimer = setInterval(poll, RECEIVER_POLL_MS)
  services.receiverTimer.unref()
}

/**
 * The host pair for `stateDir`, built on first use and torn down when the last server
 * using it closes. A caller that injects different seams for the same directory gets its
 * own pair rather than another's: those seams belong to the caller that injected them.
 */
async function acquireHostedServices(
  opts: WebDashboardServices,
  isSea: boolean,
): Promise<{
  services: HostedServices
  release: () => void
}> {
  const stateDir = opts.stateDir ?? join(homedir(), '.kyberdash')
  const seams: HostedSeams = {
    store: resolveHostStore(opts.store),
    // Default to re-running THIS process's own CLI, so a source checkout
    // (`tsx src/launcher.ts web`) runs the same code rather than a `kyberdash` that may
    // not be on PATH - and in a SEA, where process.argv[1] is the unexpanded argv0, to
    // the binary itself. An injected program is taken at face value with no wrapper
    // arguments: its argv is the caller's.
    ...(opts.program === undefined
      ? resolveJobSpawnTarget(currentSpawnRuntime(isSea))
      : { program: opts.program, programArgs: [] as readonly string[] }),
    jobSpawner: opts.jobSpawner,
    receiverSpawner: opts.receiverSpawner,
    receiverProber: opts.receiverProber,
    maintenance: opts.maintenance,
  }

  for (;;) {
    const existing = hostedServices.get(stateDir)
    if (existing !== undefined && sameSeams(existing.seams, seams)) return share(stateDir, existing)
    const pending = hostedServicesStarting.get(stateDir)
    if (pending === undefined) {
      // We are the ones starting it, so its failure is ours to raise.
      return share(stateDir, await startHostedPair(stateDir, seams))
    }
    // Another server in this process is already building a pair for this state dir. Wait
    // for it rather than starting a second one: two poll timers and two leases over one
    // state dir is the situation this sharing exists to prevent. Its failure is reported
    // to whoever asked for that pair, not to us - we loop and try again, and the retry
    // builds a fresh pair if it came up with different seams than ours.
    await pending.catch(() => { /* not our failure to raise */ })
  }
}

/** Registers `services` as the shared pair for `stateDir` until the last server lets it go. */
function share(stateDir: string, services: HostedServices): {
  services: HostedServices
  release: () => void
} {
  services.refs += 1
  let released = false
  return {
    services,
    release: () => {
      if (released) return
      released = true
      services.refs -= 1
      if (services.refs > 0) return
      if (services.receiverTimer !== undefined) clearInterval(services.receiverTimer)
      if (services.receiverRetry !== undefined) clearTimeout(services.receiverRetry)
      if (hostedServices.get(stateDir) === services) hostedServices.delete(stateDir)
      void services.jobHost.close()
      void services.receiverHost.close()
      // The process-singleton default store is closed with the last pair that used it.
      // It is not one caller's store: a second server in this process sharing the same
      // state dir still holds a reference, so this only fires when the final share goes
      // back, and an injected store is never touched.
      if (services.seams.store === defaultStore) {
        defaultStore = null
        services.store.close()
      }
    },
  }
}

/**
 * Builds a pair for `stateDir` and starts it. The pair is published to `hostedServices`
 * only once both hosts are up: an entry that appears before `jobHost.start()` resolves is
 * an entry a second caller would reuse, and a pair whose start rejected holds a jobs
 * lease it never released while looking perfectly shareable. On rejection nothing is
 * published, both hosts are closed (which releases any lease `start()` took and clears
 * the tick interval it armed), and the caller sees the error.
 */
function startHostedPair(stateDir: string, seams: HostedSeams): Promise<HostedServices> {
  const services = buildServices(stateDir, seams)
  const pending = (async (): Promise<HostedServices> => {
    try {
      // The first tick launches the scheduled refresh (and the first maintenance pass), and
      // the first receiver poll decides whether this process hosts `kyberdash otel`. Both
      // belong to the pair, not to one server: a second server over the same state dir must
      // not arm a second poll timer against the same host.
      await services.jobHost.start()
      startReceiverPolling(services)
    } catch (err) {
      await services.jobHost.close()
      await services.receiverHost.close()
      throw err
    }
    hostedServices.set(stateDir, services)
    return services
  })()
  hostedServicesStarting.set(stateDir, pending)
  const forget = (): void => { if (hostedServicesStarting.get(stateDir) === pending) hostedServicesStarting.delete(stateDir) }
  void pending.then(forget, forget)
  return pending
}

function buildServices(stateDir: string, seams: HostedSeams): HostedServices {
  const { store, program, programArgs } = seams
  return {
    store,
    seams,
    refs: 0,
    jobHost: new JobHost({
      store,
      stateDir,
      program,
      programArgs,
      ...(seams.jobSpawner === undefined ? {} : { spawner: seams.jobSpawner }),
      // The maintenance pass is the central module's, not a copy of it: expiry purging and
      // reprojection are engine work and must not be re-implemented at the call site.
      maintenance: seams.maintenance ?? (() => runMaintenancePass(store, new Date())),
    }),
    receiverHost: new ReceiverHost({
      store,
      program,
      programArgs,
      ...(seams.receiverSpawner === undefined ? {} : { spawner: seams.receiverSpawner }),
      ...(seams.receiverProber === undefined ? {} : { prober: seams.receiverProber }),
    }),
  }
}

export async function runWebDashboard(opts: WebDashboardServices & {
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

  // The hosted background services (rule R1). The server owns them: the tray and the web
  // UI are display layers, so the schedule, the maintenance pass and every manual job run
  // from here and nowhere else. One pair per state dir per process, shared by every server
  // in this process that points at the same place.
  //
  // Started AFTER the socket is bound and the listening line is out (see below), so
  // `hosted` is null while the pair is coming up. Routes that need the job host answer
  // 503 in that window rather than reaching for a host that does not exist yet.
  let hosted: HostedServices | null = null
  let releaseHosted = (): void => {}
  const stateDir = opts.stateDir ?? join(homedir(), '.kyberdash')
  // The state dir is created here, not left to whichever writer gets there first. It is
  // routinely absent on a fresh account - a test HOME, a CI smoke run, or a machine where
  // the store lives elsewhere (KYBER_CANON_DB) - and every writer in this process then
  // fails on ENOENT for a directory that should simply exist. 0o700: this directory holds
  // the pid and port of a loopback server and the user's stored context.
  //
  // Best-effort: a directory we cannot create is reported once, and the server still runs
  // (the lease takes the same directory and degrades to hosting no jobs if it cannot).
  const stateDirProblem = ensureStateDir(stateDir)
  // Read-write: the hosts write settings, and the maintenance pass purges and reprojects.
  // The store is resolved up front (it is one handle per process, shared by every server),
  // so the settings routes work from the first accepted connection.
  const store = resolveHostStore(opts.store)

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

      // The ONE shared API: both surfaces reach the same store and the same JobHost.
      const current = hosted
      if (current === null && HOSTED_ONLY_ROUTES.has(url.pathname)) {
        res.writeHead(503, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' })
        res.end(JSON.stringify({ error: ERR_SERVICES_STARTING }))
        return
      }
      // The store is always passed, so the settings routes answer from the first accepted
      // connection; only the JobHost is absent while the pair is coming up, and the job
      // routes answer 503 for that on their own account.
      if (handleKyberRequest(req, res, url, bridge, { store, jobHost: current?.jobHost })) return

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
    // A server that never bound never served, so it must not keep a host pair alive: the
    // share taken above goes back rather than leaving a lease held by a process with no
    // dashboard.
  }).catch((err: unknown) => {
    // Nothing was bound, so nothing was ever holding a host pair: there is no share to
    // give back yet (the pair is taken below, after the bind).
    throw err
  })
  // Durable handler so a post-bind socket error never crashes the process.
  server.on('error', () => {})

  // server.json is written once the server is listening, so the tray never reads a URL
  // that is not yet accepting connections. It carries the same `apiVersion` the tray checks
  // against `/meta`, so a tray can decide whether it understands this server before it
  // renders anything.
  const url = `http://127.0.0.1:${port}`
  // Publish the attach point. Only over a file that is not a live other server's: the
  // jobs lease stands a second `kyberdash web` process down, but it does not stop that
  // process reaching this line, and a blind overwrite would hand the tray the standing
  // process's URL while destroying the pid that owns it. A dead holder's leftover is
  // overwritten without comment - that is the normal recovery case.
  const serverFilePath = join(stateDir, SERVER_FILE)
  const existing = readServerRecord(serverFilePath)
  const ownedByLiveOtherProcess = existing !== null && existing.pid !== process.pid && pidLooksAlive(existing.pid)
  // Deliberately no log on the skip below: a second server over one state dir is a
  // supported configuration, and a warning there would be noise on a normal day.
  if (!ownedByLiveOtherProcess) {
    if (stateDirProblem === null) {
      publishServerRecord(serverFilePath, JSON.stringify({ pid: process.pid, url, apiVersion: REPORT_SCHEMA_VERSION }))
    } else {
      console.error(publishWarning(stateDirProblem))
    }
  }

  // First, and machine-readable: the tray supervisor reads this line and nowhere else,
  // and nowhere else than this line does it time out — the supervisor kills a server
  // that has not announced itself. So it comes BEFORE the hosted services: `JobHost.
  // start()` awaits its first tick, and that tick runs the maintenance pass, which on a
  // large canon.db can take far longer than the supervisor's patience. Binding and
  // announcing first is what keeps a slow first pass from reading as a hung server.
  writeStdout(
    `${JSON.stringify({
      event: 'kyberdash.web.listening',
      url,
      pid: process.pid,
      version: KYBERDASH_VERSION,
      apiVersion: REPORT_SCHEMA_VERSION,
    })}\n`,
  )

  const onSigint = () => {
    bridge.close()
    process.exit(0)
  }

  // Shutdown, in one place: release this server's share of the hosted services (the last
  // one out stops both hosts, which kill their children and release the jobs lease),
  // remove OUR published server.json so no tray looks for a dead URL, and close the
  // bridge. Removed by identity, not by path: a second process over the same state dir
  // can have published and then stood down, and a path delete from either one would strip
  // the other's attach point. An unreadable, missing or foreign body is left alone.
  server.on('close', () => {
    process.off('SIGINT', onSigint)
    const published = readServerRecord(serverFilePath)
    if (published !== null && published.pid === process.pid && (published.url === undefined || published.url === url)) {
      rmSync(serverFilePath, { force: true })
    }
    releaseHosted()
    bridge.close()
  })

  // Only now, with the socket bound, the attach point published and the line out, are the
  // hosts built. A failure here is a real failure to start: the socket is closed (which
  // removes our server.json through the handler above), and the caller sees the error -
  // which is what `web-registry.test.ts` pins. A pair publishes itself only once its
  // hosts are up, so a rejected start leaves no shareable entry and no held lease.
  try {
    const acquired = await acquireHostedServices(opts, runningAsSea())
    hosted = acquired.services
    releaseHosted = acquired.release
  } catch (err) {
    await new Promise<void>((resolve) => server.close(() => resolve()))
    throw err
  }

  process.on('SIGINT', onSigint)

  if (!staticSource) {
    writeStdout(`\n  ${runningAsSea() ? SEA_NOT_BUILT_HINT_TEXT : DEV_NOT_BUILT_HINT_TEXT}\n`)
  }
  writeStdout(`\n  ${BRAND.productName} dashboard at ${url}\n  Press Ctrl+C to stop.\n\n`)
  if (opts.open) (opts.openUrl ?? openBrowser)(joinViewUrl(url, opts.view))

  return server
}
