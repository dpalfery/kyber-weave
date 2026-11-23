import { spawnSync } from 'node:child_process'
import { existsSync, readFileSync, statSync } from 'node:fs'
import { createServer, type IncomingMessage, type ServerResponse } from 'node:http'
import { dirname, extname, join, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import type { Page } from '@playwright/test'

export type TrayPhase = 'setup' | 'starting' | 'ready' | 'stale'

export type TraySettings = {
  harness: string
  windowDays: number
  attentionThreshold: number
  criticalThreshold: number
  launchAtLogin: boolean
}

export type TraySharedSettings = {
  folderImportScheduled: boolean
  jobsPaused: boolean
  refreshCadenceMinutes: number
  receiverHosted: boolean
}

export type TrayViewState = {
  phase: TrayPhase
  report: Record<string, unknown> | null
  reportFetchedAt: string | null
  error: string | null
  jobs: Record<string, unknown> | null
  sharedSettings: TraySharedSettings | null
  coverage: Record<string, unknown> | null
  kyberdashVersion: { version: string; apiVersion: number } | null
  settings: TraySettings
}

export type TrayInvocationRecord = {
  readonly cmd: string
  readonly args: Record<string, unknown>
  readonly ok: boolean
  readonly error?: string
  readonly at: string
}

export type OpenedViewRecord = {
  readonly view: string
  readonly url: string
  readonly at: string
}

export type TrayBridgeStateSnapshot = {
  readonly viewState: TrayViewState
  readonly invocations: readonly TrayInvocationRecord[]
  readonly openedViews: readonly OpenedViewRecord[]
  readonly hidePopoverCount: number
  readonly quitCount: number
}

export type TrayBridgeHandle = {
  readonly url: string
  readonly serverUrl: string
  readonly distDir: string
  getState: (options?: { readonly refresh?: boolean }) => Promise<TrayBridgeStateSnapshot>
  refreshState: () => Promise<TrayViewState>
  stop: () => Promise<void>
}

type EmittedEvent = {
  readonly event: string
  readonly payload: unknown
}

const DEFAULT_TRAY_SETTINGS: TraySettings = {
  harness: 'all',
  windowDays: 7,
  attentionThreshold: 0.7,
  criticalThreshold: 0.9,
  launchAtLogin: false,
}

const MAX_BODY_BYTES = 1_048_576
const MAX_HISTORY_WEEKS = 52
const MAX_WINDOW_DAYS = 3650
const TRAY_BUILD_TIMEOUT_MS = 120_000
const UPSTREAM_TIMEOUT_MS = 30_000
const HARNESS_ID_RE = /^[a-z0-9][a-z0-9_-]{0,63}$/

const TRAY_COMMANDS = new Set<string>([
  'get_view_state',
  'refresh_now',
  'open_view',
  'set_settings',
  'quit',
  'hide_popover',
  'clean_database',
  'import_folder_history',
  'set_shared_settings',
])

const MIME_TYPES: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.mjs': 'application/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.txt': 'text/plain; charset=utf-8',
}

function getRepoLayout(): {
  readonly repoRoot: string
  readonly dashRoot: string
  readonly trayUiRoot: string
  readonly trayUiNodeModules: string
  readonly trayUiDist: string
  readonly viewPathsFile: string
} {
  const e2eDir = dirname(fileURLToPath(import.meta.url))
  const dashRoot = resolve(e2eDir, '..')
  const repoRoot = resolve(dashRoot, '..')
  const trayUiRoot = join(dashRoot, 'tray', 'ui')
  return {
    repoRoot,
    dashRoot,
    trayUiRoot,
    trayUiNodeModules: join(trayUiRoot, 'node_modules'),
    trayUiDist: join(trayUiRoot, 'dist'),
    viewPathsFile: join(dashRoot, 'web', 'src', 'lib', 'view-paths.json'),
  }
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function assertLoopbackServerOrigin(serverUrl: string): string {
  let parsed: URL
  try {
    parsed = new URL(serverUrl)
  } catch (error) {
    throw new Error(`Invalid KyberDash server URL: ${serverUrl}`, { cause: error })
  }
  if (parsed.protocol !== 'http:') {
    throw new Error(`Refusing non-http KyberDash server URL: ${serverUrl}`)
  }
  const host = parsed.hostname.toLowerCase()
  if (host !== '127.0.0.1' && host !== 'localhost' && host !== '::1' && host !== '[::1]') {
    throw new Error(`Refusing non-loopback KyberDash server URL: ${serverUrl}`)
  }
  return parsed.origin
}

function validateHarnessId(raw: unknown, fieldName: string): string {
  if (typeof raw !== 'string') {
    throw new Error(`${fieldName} must be a string`)
  }
  const trimmed = raw.trim()
  if (!HARNESS_ID_RE.test(trimmed)) {
    throw new Error(`${fieldName} must match ${HARNESS_ID_RE.source}, got "${raw}"`)
  }
  return trimmed
}

function validateWeeks(raw: unknown, fieldName: string): number {
  if (typeof raw !== 'number' || !Number.isInteger(raw) || raw < 1 || raw > MAX_HISTORY_WEEKS) {
    throw new Error(`${fieldName} must be an integer between 1 and ${MAX_HISTORY_WEEKS}`)
  }
  return raw
}

function isFraction(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 && value <= 1
}

function applyPartialTraySettings(current: TraySettings, patch: Record<string, unknown>): TraySettings {
  // Mirrors Rust TraySettings::apply_partial: drops transient invalid keystrokes from live inputs.
  const next: TraySettings = { ...current }

  if (typeof patch['harness'] === 'string') {
    const trimmed = patch['harness'].trim()
    if (trimmed === 'all') {
      next.harness = 'all'
    } else if (HARNESS_ID_RE.test(trimmed)) {
      next.harness = trimmed
    }
  }

  const days = patch['windowDays']
  if (typeof days === 'number' && Number.isInteger(days) && days >= 1 && days <= MAX_WINDOW_DAYS) {
    next.windowDays = days
  }

  if (isFraction(patch['attentionThreshold'])) {
    next.attentionThreshold = patch['attentionThreshold']
  }

  if (isFraction(patch['criticalThreshold'])) {
    next.criticalThreshold = patch['criticalThreshold']
  }

  if (typeof patch['launchAtLogin'] === 'boolean') {
    next.launchAtLogin = patch['launchAtLogin']
  }

  if (next.attentionThreshold > next.criticalThreshold) {
    const swapped = next.attentionThreshold
    next.attentionThreshold = next.criticalThreshold
    next.criticalThreshold = swapped
  }

  return next
}

function loadViewPathPatterns(): string[] {
  const { viewPathsFile } = getRepoLayout()
  const raw = readFileSync(viewPathsFile, 'utf8')
  const parsed: unknown = JSON.parse(raw)
  if (!Array.isArray(parsed) || !parsed.every((item): item is string => typeof item === 'string')) {
    throw new Error(`Invalid view-paths.json at ${viewPathsFile}`)
  }
  return parsed
}

function splitViewQuery(value: string): { readonly path: string; readonly query: string } {
  const trimmed = value.trim()
  const qIndex = trimmed.indexOf('?')
  if (qIndex < 0) {
    return { path: trimmed, query: '' }
  }
  return { path: trimmed.slice(0, qIndex), query: trimmed.slice(qIndex + 1) }
}

function normalizeViewPath(path: string): string {
  return path.replace(/^\/+|\/+$/g, '')
}

function viewPathMatches(viewPath: string, patternPath: string): boolean {
  const normalizedView = normalizeViewPath(viewPath)
  const normalizedPattern = normalizeViewPath(patternPath)
  if (normalizedPattern === '') {
    return normalizedView === ''
  }
  if (normalizedView === '') {
    return false
  }
  const actualSegments = normalizedView.split('/')
  const patternSegments = normalizedPattern.split('/')
  if (actualSegments.length !== patternSegments.length) {
    return false
  }
  return patternSegments.every((segment, index) => {
    const actual = actualSegments[index] ?? ''
    if (segment.startsWith(':')) {
      return actual.length > 0
    }
    return actual === segment
  })
}

function viewQueryMatches(actualQuery: string, expectedQuery: string): boolean {
  if (expectedQuery === '') {
    return actualQuery === ''
  }
  const expectedKeys = expectedQuery
    .split('&')
    .filter((entry) => entry.length > 0)
    .map((entry) => entry.split('=')[0] ?? '')
  const actualParams = new URLSearchParams(actualQuery)
  for (const key of expectedKeys) {
    const val = actualParams.get(key)
    if (val === null || val.trim() === '') {
      return false
    }
  }
  return true
}

function resolveOpenViewUrl(serverOrigin: string, view: string, patterns: readonly string[]): string {
  if (view.includes('://') || view.startsWith('//') || view.split('/').some((seg) => seg === '..')) {
    throw new Error(`refusing a view that is not a dashboard route: ${view}`)
  }
  const { path, query } = splitViewQuery(view)
  const matched = patterns.some((pattern) => {
    const expected = splitViewQuery(pattern)
    return viewPathMatches(path, expected.path) && viewQueryMatches(query, expected.query)
  })
  if (!matched) {
    throw new Error(`refusing a view that is not a dashboard route: ${view}`)
  }
  const trimmed = view.trim().replace(/^\/+/, '')
  return trimmed === '' ? serverOrigin : `${serverOrigin}/${trimmed}`
}

/**
 * Translates either the Tray UI's `{ scope: 'all' | { harness }, importWeeks? }` IPC args or
 * `{ kind: 'all' | 'harnesses', harnesses?, reingestWeeks? }` into the `/api/kyber/clean` payload
 * (`{ scope, all? / harnesses?, confirm: true, reingestWeeks? }`).
 */
export function translateCleanDatabaseScope(rawArgs: Record<string, unknown>): Record<string, unknown> {
  const weeksRaw = rawArgs['importWeeks'] ?? rawArgs['reingestWeeks']
  const weeks = weeksRaw === undefined ? undefined : validateWeeks(weeksRaw, 'reingestWeeks')

  const kindRaw = rawArgs['kind']
  if (kindRaw === 'all') {
    return {
      scope: 'all',
      all: true,
      confirm: true,
      ...(weeks !== undefined ? { reingestWeeks: weeks } : {}),
    }
  }
  if (kindRaw === 'harnesses') {
    const harnessesRaw = rawArgs['harnesses']
    if (!Array.isArray(harnessesRaw) || harnessesRaw.length === 0) {
      throw new Error('clean_database with kind "harnesses" requires a non-empty harnesses array')
    }
    const harnesses = harnessesRaw.map((entry, idx) => validateHarnessId(entry, `harnesses[${idx}]`))
    return {
      scope: harnesses.length === 1 ? { harness: harnesses[0] } : 'harnesses',
      harnesses,
      confirm: true,
      ...(weeks !== undefined ? { reingestWeeks: weeks } : {}),
    }
  }
  if (kindRaw !== undefined) {
    throw new Error(`unknown clean_database kind: ${String(kindRaw)}`)
  }

  const scope = rawArgs['scope']
  if (scope === 'all') {
    return {
      scope: 'all',
      all: true,
      confirm: true,
      ...(weeks !== undefined ? { reingestWeeks: weeks } : {}),
    }
  }

  if (isPlainObject(scope)) {
    if ('kind' in scope) {
      return translateCleanDatabaseScope({
        ...scope,
        ...(weeks !== undefined ? { reingestWeeks: weeks } : {}),
      })
    }
    const harness = validateHarnessId(scope['harness'], 'scope.harness')
    return {
      scope: { harness },
      harnesses: [harness],
      confirm: true,
      ...(weeks !== undefined ? { reingestWeeks: weeks } : {}),
    }
  }

  throw new Error('clean scope must be "all", { harness }, or { kind, harnesses }')
}

/**
 * Verifies `dash/tray/ui/node_modules` readiness before running a bounded Tray UI production build
 * when `dash/tray/ui/dist/index.html` is absent (or `forceRebuild` is true).
 */
export function ensureTrayUiBuilt(options?: { readonly forceRebuild?: boolean }): string {
  const { repoRoot, trayUiNodeModules, trayUiDist } = getRepoLayout()
  const distIndex = join(trayUiDist, 'index.html')

  if (!options?.forceRebuild && existsSync(distIndex)) {
    return trayUiDist
  }

  if (!existsSync(trayUiNodeModules) || !existsSync(join(trayUiNodeModules, '@tauri-apps', 'api', 'package.json'))) {
    throw new Error(
      `Missing Tray UI dependencies at ${trayUiNodeModules}. Install them first with: npm --prefix dash/tray/ui ci --no-audit --no-fund`,
    )
  }

  const built = spawnSync('npm', ['--prefix', 'dash/tray/ui', 'run', 'build'], {
    cwd: repoRoot,
    encoding: 'utf8',
    timeout: TRAY_BUILD_TIMEOUT_MS,
    killSignal: 'SIGKILL',
  })

  if (built.error !== undefined || built.status !== 0 || !existsSync(distIndex)) {
    const detail = built.error ? built.error.message : `exit ${built.status ?? 'null'} (signal ${built.signal ?? 'none'})`
    throw new Error(
      `Tray UI build failed (${detail}):\n${built.stdout ?? ''}\n${built.stderr ?? ''}`,
    )
  }

  return trayUiDist
}

async function readRequestBody(req: IncomingMessage): Promise<string> {
  return new Promise<string>((resolveBody, rejectBody) => {
    const chunks: Buffer[] = []
    let totalBytes = 0

    req.on('data', (chunk: Buffer) => {
      totalBytes += chunk.length
      if (totalBytes > MAX_BODY_BYTES) {
        rejectBody(new Error(`Request body exceeded ${MAX_BODY_BYTES} bytes`))
        req.destroy()
        return
      }
      chunks.push(chunk)
    })

    req.once('end', () => {
      resolveBody(Buffer.concat(chunks).toString('utf8'))
    })

    req.once('error', (error) => {
      rejectBody(error)
    })
  })
}

function sendJson(res: ServerResponse, statusCode: number, payload: unknown): void {
  const body = JSON.stringify(payload)
  res.writeHead(statusCode, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
  })
  res.end(body)
}

function serveStaticAsset(res: ServerResponse, distDir: string, pathname: string): void {
  let decodedPath: string
  try {
    decodedPath = decodeURIComponent(pathname)
  } catch {
    res.writeHead(400, { 'content-type': 'text/plain; charset=utf-8' })
    res.end('Bad request path')
    return
  }

  const relativePath = decodedPath === '/' ? 'index.html' : decodedPath.replace(/^\/+/, '')
  const candidate = resolve(distDir, relativePath)
  if (candidate !== distDir && !candidate.startsWith(distDir + sep)) {
    res.writeHead(403, { 'content-type': 'text/plain; charset=utf-8' })
    res.end('Forbidden')
    return
  }

  if (!existsSync(candidate) || !statSync(candidate).isFile()) {
    res.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' })
    res.end('Not found')
    return
  }

  const ext = extname(candidate).toLowerCase()
  const contentType = MIME_TYPES[ext] ?? 'application/octet-stream'
  const contents = readFileSync(candidate)
  res.writeHead(200, {
    'content-type': contentType,
    'cache-control': 'no-store',
  })
  res.end(contents)
}

async function fetchLoopbackJson(url: string, init?: { readonly method?: 'GET' | 'POST' | 'PUT'; readonly body?: unknown }): Promise<{
  readonly ok: boolean
  readonly status: number
  readonly data: unknown
  readonly errorMessage: string | null
}> {
  const method = init?.method ?? 'GET'
  // Loopback server-to-server request intentionally omits any browser Origin header
  // so DashHttp.IsAllowedMutation permits local mutations.
  const headers: Record<string, string> = {}
  let serializedBody: string | undefined
  if (init?.body !== undefined) {
    headers['content-type'] = 'application/json'
    serializedBody = JSON.stringify(init.body)
  }

  const response = await fetch(url, {
    method,
    headers,
    ...(serializedBody !== undefined ? { body: serializedBody } : {}),
    signal: AbortSignal.timeout(UPSTREAM_TIMEOUT_MS),
  })

  const text = await response.text()
  let parsed: unknown = null
  if (text.length > 0) {
    try {
      parsed = JSON.parse(text)
    } catch {
      parsed = null
    }
  }

  let errorMessage: string | null = null
  if (!response.ok) {
    if (isPlainObject(parsed) && typeof parsed['error'] === 'string' && parsed['error'].trim() !== '') {
      errorMessage = parsed['error']
    } else if (isPlainObject(parsed) && typeof parsed['message'] === 'string' && parsed['message'].trim() !== '') {
      errorMessage = parsed['message']
    } else {
      errorMessage = `HTTP ${response.status} from ${url}`
    }
  }

  return { ok: response.ok, status: response.status, data: parsed, errorMessage }
}

/**
 * Starts a same-origin Node HTTP server serving the built `dash/tray/ui/dist` bundle,
 * `GET /tray/state`, and `POST /tray/invoke` while proxying `/api/kyber/*` to the isolated
 * `kyberdash` server over loopback HTTP without an `Origin` header.
 */
export async function startTrayBridge(options: {
  readonly serverUrl: string
  readonly forceBuild?: boolean
}): Promise<TrayBridgeHandle> {
  const serverOrigin = assertLoopbackServerOrigin(options.serverUrl)
  const distDir = ensureTrayUiBuilt({ forceRebuild: options.forceBuild })
  const viewPathPatterns = loadViewPathPatterns()

  let traySettings: TraySettings = { ...DEFAULT_TRAY_SETTINGS }
  let cachedReport: Record<string, unknown> | null = null
  let reportFetchedAt: string | null = null
  let lastError: string | null = null
  let jobs: Record<string, unknown> | null = null
  let sharedSettings: TraySharedSettings | null = null
  let coverage: Record<string, unknown> | null = null
  let kyberdashVersion: { version: string; apiVersion: number } | null = null

  const invocations: TrayInvocationRecord[] = []
  const openedViews: OpenedViewRecord[] = []
  let hidePopoverCount = 0
  let quitCount = 0

  const buildViewState = (): TrayViewState => {
    const phase: TrayPhase = lastError !== null && cachedReport !== null ? 'stale' : 'ready'
    return {
      phase,
      report: cachedReport,
      reportFetchedAt,
      error: lastError,
      jobs,
      sharedSettings,
      coverage,
      kyberdashVersion,
      settings: { ...traySettings },
    }
  }

  const refreshState = async (): Promise<TrayViewState> => {
    const qs = new URLSearchParams({ days: String(traySettings.windowDays) })
    if (traySettings.harness !== 'all') {
      qs.set('harness', traySettings.harness)
    }

    try {
      const reportRes = await fetchLoopbackJson(`${serverOrigin}/api/kyber/report?${qs.toString()}`)
      if (!reportRes.ok || !isPlainObject(reportRes.data)) {
        lastError = reportRes.errorMessage ?? `Unexpected /api/kyber/report response (${reportRes.status})`
        jobs = null
        sharedSettings = null
        coverage = null
        kyberdashVersion = null
        return buildViewState()
      }

      cachedReport = reportRes.data
      reportFetchedAt = new Date().toISOString()
      lastError = null
    } catch (error) {
      // viewState.error feeds the snapshot served over HTTP, so it carries a static
      // reason while the caught text stays in the bridge log for debugging.
      console.error('tray report refresh failed:', error instanceof Error ? error.message : String(error))
      lastError = 'report refresh failed'
      jobs = null
      sharedSettings = null
      coverage = null
      kyberdashVersion = null
      return buildViewState()
    }

    const [jobsRes, settingsRes, coverageRes, versionRes] = await Promise.all([
      fetchLoopbackJson(`${serverOrigin}/api/kyber/jobs`).catch(() => null),
      fetchLoopbackJson(`${serverOrigin}/api/kyber/settings`).catch(() => null),
      fetchLoopbackJson(`${serverOrigin}/api/kyber/coverage`).catch(() => null),
      fetchLoopbackJson(`${serverOrigin}/api/kyber/version`).catch(() => null),
    ])

    jobs = jobsRes?.ok && isPlainObject(jobsRes.data) ? jobsRes.data : null
    sharedSettings =
      settingsRes?.ok &&
      isPlainObject(settingsRes.data) &&
      typeof settingsRes.data['folderImportScheduled'] === 'boolean' &&
      typeof settingsRes.data['jobsPaused'] === 'boolean' &&
      typeof settingsRes.data['refreshCadenceMinutes'] === 'number' &&
      typeof settingsRes.data['receiverHosted'] === 'boolean'
        ? {
            folderImportScheduled: settingsRes.data['folderImportScheduled'],
            jobsPaused: settingsRes.data['jobsPaused'],
            refreshCadenceMinutes: settingsRes.data['refreshCadenceMinutes'],
            receiverHosted: settingsRes.data['receiverHosted'],
          }
        : null
    coverage = coverageRes?.ok && isPlainObject(coverageRes.data) ? coverageRes.data : null
    kyberdashVersion =
      versionRes?.ok &&
      isPlainObject(versionRes.data) &&
      typeof versionRes.data['version'] === 'string' &&
      typeof versionRes.data['apiVersion'] === 'number'
        ? { version: versionRes.data['version'], apiVersion: versionRes.data['apiVersion'] }
        : null

    return buildViewState()
  }

  await refreshState()

  const handleInvokeCommand = async (
    cmd: string,
    args: Record<string, unknown>,
  ): Promise<{ readonly result: unknown; readonly emittedEvents: readonly EmittedEvent[] }> => {
    switch (cmd) {
      case 'get_view_state': {
        return { result: buildViewState(), emittedEvents: [] }
      }

      case 'refresh_now': {
        const res = await fetchLoopbackJson(`${serverOrigin}/api/kyber/refresh`, {
          method: 'POST',
          body: { surface: 'tray' },
        })
        if (!res.ok) {
          throw new Error(res.errorMessage ?? `refresh_now failed (${res.status})`)
        }
        const state = await refreshState()
        return {
          result: null,
          emittedEvents: [{ event: 'view-state-changed', payload: state }],
        }
      }

      case 'open_view': {
        const view = args['view']
        if (typeof view !== 'string') {
          throw new Error('open_view requires a string view')
        }
        const resolvedUrl = resolveOpenViewUrl(serverOrigin, view, viewPathPatterns)
        openedViews.push({
          view,
          url: resolvedUrl,
          at: new Date().toISOString(),
        })
        return { result: null, emittedEvents: [] }
      }

      case 'set_settings': {
        const patch = args['patch']
        if (!isPlainObject(patch)) {
          throw new Error('set_settings patch must be a JSON object')
        }
        const prevHarness = traySettings.harness
        const prevWindowDays = traySettings.windowDays
        traySettings = applyPartialTraySettings(traySettings, patch)
        const state =
          traySettings.harness !== prevHarness || traySettings.windowDays !== prevWindowDays
            ? await refreshState()
            : buildViewState()
        return {
          result: state,
          emittedEvents: [{ event: 'view-state-changed', payload: state }],
        }
      }

      case 'set_shared_settings': {
        const patch = args['patch']
        if (!isPlainObject(patch)) {
          throw new Error('set_shared_settings patch must be a JSON object')
        }
        const res = await fetchLoopbackJson(`${serverOrigin}/api/kyber/settings`, {
          method: 'PUT',
          body: patch,
        })
        if (!res.ok) {
          throw new Error(res.errorMessage ?? `set_shared_settings failed (${res.status})`)
        }
        const state = await refreshState()
        return {
          result: null,
          emittedEvents: [{ event: 'view-state-changed', payload: state }],
        }
      }

      case 'clean_database': {
        const body = translateCleanDatabaseScope(args)
        const res = await fetchLoopbackJson(`${serverOrigin}/api/kyber/clean`, {
          method: 'POST',
          body,
        })
        if (!res.ok) {
          throw new Error(res.errorMessage ?? `clean_database failed (${res.status})`)
        }
        const state = await refreshState()
        return {
          result: null,
          emittedEvents: [{ event: 'view-state-changed', payload: state }],
        }
      }

      case 'import_folder_history': {
        const weeks = validateWeeks(args['weeks'], 'weeks')
        const body: Record<string, unknown> = { weeks }
        if (args['harness'] !== undefined) {
          body['harness'] = validateHarnessId(args['harness'], 'harness')
        }
        const res = await fetchLoopbackJson(`${serverOrigin}/api/kyber/import-history`, {
          method: 'POST',
          body,
        })
        if (!res.ok) {
          throw new Error(res.errorMessage ?? `import_folder_history failed (${res.status})`)
        }
        const state = await refreshState()
        return {
          result: null,
          emittedEvents: [{ event: 'view-state-changed', payload: state }],
        }
      }

      case 'hide_popover': {
        hidePopoverCount += 1
        return { result: null, emittedEvents: [] }
      }

      case 'quit': {
        quitCount += 1
        return { result: null, emittedEvents: [] }
      }

      default:
        throw new Error(`unknown tray command: ${cmd}`)
    }
  }

  const snapshot = (): TrayBridgeStateSnapshot => ({
    viewState: buildViewState(),
    invocations: [...invocations],
    openedViews: [...openedViews],
    hidePopoverCount,
    quitCount,
  })

  const httpServer = createServer(async (req, res) => {
    try {
      const requestUrl = new URL(req.url ?? '/', 'http://127.0.0.1')
      const { pathname, searchParams } = requestUrl
      const method = (req.method ?? 'GET').toUpperCase()

      if (method === 'GET' && pathname === '/tray/state') {
        const refreshParam = searchParams.get('refresh')
        if (
          refreshParam !== null &&
          refreshParam !== '0' &&
          refreshParam !== '1' &&
          refreshParam !== 'true' &&
          refreshParam !== 'false'
        ) {
          sendJson(res, 400, { ok: false, error: { kind: 'runtime', message: `Invalid refresh query value: ${refreshParam}` } })
          return
        }
        if (refreshParam === '1' || refreshParam === 'true') {
          await refreshState()
        }
        sendJson(res, 200, snapshot())
        return
      }

      if (method === 'POST' && pathname === '/tray/invoke') {
        const rawBody = await readRequestBody(req)
        let parsed: unknown
        try {
          parsed = JSON.parse(rawBody)
        } catch {
          sendJson(res, 400, { ok: false, error: { kind: 'runtime', message: 'Invalid JSON request body' } })
          return
        }

        if (!isPlainObject(parsed) || typeof parsed['cmd'] !== 'string') {
          sendJson(res, 400, { ok: false, error: { kind: 'runtime', message: 'Expected { cmd: string, args?: object }' } })
          return
        }

        const cmd = parsed['cmd']
        if (!TRAY_COMMANDS.has(cmd)) {
          sendJson(res, 400, { ok: false, error: { kind: 'runtime', message: `Unknown tray command: ${cmd}` } })
          return
        }

        const rawArgs = parsed['args']
        if (rawArgs !== undefined && !isPlainObject(rawArgs)) {
          sendJson(res, 400, { ok: false, error: { kind: 'runtime', message: 'args must be an object' } })
          return
        }
        const args = rawArgs ?? {}

        try {
          const outcome = await handleInvokeCommand(cmd, args)
          invocations.push({ cmd, args, ok: true, at: new Date().toISOString() })
          sendJson(res, 200, { ok: true, result: outcome.result, emittedEvents: outcome.emittedEvents })
        } catch (error) {
          // Never reflect backend error internals in an HTTP response, and never
          // interpolate request-controlled values into the log line; the failing
          // command is already recorded structurally in invocations below.
          console.error('tray invoke failed:', error instanceof Error ? error.message : String(error))
          const message = 'tray command failed'
          invocations.push({ cmd, args, ok: false, error: message, at: new Date().toISOString() })
          sendJson(res, 400, { ok: false, error: { kind: 'runtime', message } })
        }
        return
      }

      if (pathname.startsWith('/api/kyber/')) {
        let decodedApiPath: string
        try {
          decodedApiPath = decodeURIComponent(pathname)
        } catch {
          sendJson(res, 400, { error: 'Invalid /api/kyber path' })
          return
        }
        if (decodedApiPath.split('/').some((seg) => seg === '..')) {
          sendJson(res, 400, { error: 'Invalid /api/kyber path' })
          return
        }
        const targetUrl = `${serverOrigin}${pathname}${requestUrl.search}`
        const bodyText = method === 'GET' || method === 'HEAD' ? undefined : await readRequestBody(req)
        const upstreamRes = await fetch(targetUrl, {
          method,
          headers: bodyText !== undefined ? { 'content-type': 'application/json' } : {},
          ...(bodyText !== undefined ? { body: bodyText } : {}),
          signal: AbortSignal.timeout(UPSTREAM_TIMEOUT_MS),
        })
        const upstreamBody = await upstreamRes.text()
        res.writeHead(upstreamRes.status, {
          'content-type': upstreamRes.headers.get('content-type') ?? 'application/json; charset=utf-8',
          'cache-control': 'no-store',
        })
        res.end(upstreamBody)
        return
      }

      if (method === 'GET') {
        serveStaticAsset(res, distDir, pathname)
        return
      }

      res.writeHead(405, { 'content-type': 'text/plain; charset=utf-8' })
      res.end('Method not allowed')
    } catch (error) {
      // The response carries a static reason, never the caught error's text; the
      // detail stays in the bridge log for debugging.
      console.error('tray bridge request failed:', error instanceof Error ? error.message : String(error))
      sendJson(res, 500, { ok: false, error: { kind: 'runtime', message: 'internal bridge error' } })
    }
  })

  await new Promise<void>((resolveListen, rejectListen) => {
    httpServer.once('error', rejectListen)
    httpServer.listen(0, '127.0.0.1', () => {
      httpServer.off('error', rejectListen)
      resolveListen()
    })
  })

  const address = httpServer.address()
  if (address === null || typeof address === 'string') {
    httpServer.closeAllConnections()
    httpServer.close()
    throw new Error('Tray bridge failed to bind an ephemeral loopback TCP port')
  }

  const bridgeUrl = `http://127.0.0.1:${address.port}`

  const closeOnExit = (): void => {
    try {
      httpServer.closeAllConnections()
      httpServer.close()
    } catch {
      // Process is exiting; ignore close failures.
    }
  }
  process.once('exit', closeOnExit)

  let stopped = false
  return {
    url: bridgeUrl,
    serverUrl: serverOrigin,
    distDir,
    getState: async (opts) => {
      const suffix = opts?.refresh ? '?refresh=1' : ''
      const res = await fetch(`${bridgeUrl}/tray/state${suffix}`, {
        signal: AbortSignal.timeout(UPSTREAM_TIMEOUT_MS),
      })
      if (!res.ok) {
        throw new Error(`GET /tray/state failed (${res.status})`)
      }
      return (await res.json()) as TrayBridgeStateSnapshot
    },
    refreshState,
    stop: async () => {
      if (stopped) {
        return
      }
      stopped = true
      process.off('exit', closeOnExit)
      httpServer.closeAllConnections()
      await new Promise<void>((resolveClose) => {
        httpServer.close(() => resolveClose())
      })
    },
  }
}

/**
 * Installs a complete `@tauri-apps/api` v2 `window.__TAURI_INTERNALS__` +
 * `window.__TAURI_EVENT_PLUGIN_INTERNALS__` bridge in the Playwright page before page scripts run.
 */
export async function installTrayTauriBridge(page: Page, bridgeUrl: string): Promise<void> {
  const bridgeOrigin = assertLoopbackServerOrigin(bridgeUrl)

  await page.addInitScript((origin: string) => {
    type CallbackEntry = {
      readonly fn: (payload: unknown) => void
      readonly once: boolean
    }

    type InvokeResponse =
      | {
          readonly ok: true
          readonly result: unknown
          readonly emittedEvents?: ReadonlyArray<{ readonly event: string; readonly payload: unknown }>
        }
      | {
          readonly ok: false
          readonly error?: { readonly kind?: string; readonly message?: string }
        }

    type TauriWindow = Window & {
      isTauri?: boolean
      __TAURI_INTERNALS__?: {
        metadata: {
          currentWindow: { label: string }
          currentWebview: { label: string; windowLabel: string }
        }
        convertFileSrc: (filePath: string, protocol?: string) => string
        transformCallback: (callback?: (payload: unknown) => void, once?: boolean) => number
        unregisterCallback: (id: number) => void
        runCallback: (id: number, data: unknown) => void
        invoke: (cmd: string, args?: Record<string, unknown>, options?: unknown) => Promise<unknown>
      }
      __TAURI_EVENT_PLUGIN_INTERNALS__?: {
        unregisterListener: (event: string, eventId: number) => void
      }
    }

    const callbacks = new Map<number, CallbackEntry>()
    const listenersByEvent = new Map<string, Set<number>>()
    let nextCallbackId = 1

    const dispatchEvent = (eventName: string, payload: unknown): void => {
      const ids = listenersByEvent.get(eventName)
      if (!ids) {
        return
      }
      for (const id of [...ids]) {
        const entry = callbacks.get(id)
        if (!entry) {
          ids.delete(id)
          continue
        }
        if (entry.once) {
          callbacks.delete(id)
          ids.delete(id)
        }
        entry.fn({ event: eventName, id, payload })
      }
    }

    const unregisterListener = (eventName: string, eventId: number): void => {
      const ids = listenersByEvent.get(eventName)
      if (ids) {
        ids.delete(eventId)
        if (ids.size === 0) {
          listenersByEvent.delete(eventName)
        }
      }
      callbacks.delete(eventId)
    }

    const w = window as TauriWindow
    w.isTauri = true

    w.__TAURI_EVENT_PLUGIN_INTERNALS__ = {
      unregisterListener,
    }

    w.__TAURI_INTERNALS__ = {
      metadata: {
        currentWindow: { label: 'popover' },
        currentWebview: { label: 'popover', windowLabel: 'popover' },
      },
      convertFileSrc: (filePath: string, protocol = 'asset') => {
        const normalized = filePath.replace(/^\/+/, '')
        return `${protocol}://localhost/${normalized}`
      },
      transformCallback: (callback?: (payload: unknown) => void, once = false) => {
        const id = nextCallbackId
        nextCallbackId += 1
        if (typeof callback === 'function') {
          callbacks.set(id, { fn: callback, once })
        }
        return id
      },
      unregisterCallback: (id: number) => {
        callbacks.delete(id)
      },
      runCallback: (id: number, data: unknown) => {
        const entry = callbacks.get(id)
        if (!entry) {
          return
        }
        if (entry.once) {
          callbacks.delete(id)
        }
        entry.fn(data)
      },
      invoke: async (cmd: string, args: Record<string, unknown> = {}) => {
        if (cmd === 'plugin:event|listen') {
          const eventName = args['event']
          const handlerId = args['handler']
          if (typeof eventName !== 'string' || typeof handlerId !== 'number') {
            throw new Error('plugin:event|listen requires string event and numeric handler')
          }
          let set = listenersByEvent.get(eventName)
          if (!set) {
            set = new Set<number>()
            listenersByEvent.set(eventName, set)
          }
          set.add(handlerId)
          return handlerId
        }

        if (cmd === 'plugin:event|unlisten') {
          const eventName = args['event']
          const eventId = args['eventId']
          if (typeof eventName === 'string' && typeof eventId === 'number') {
            unregisterListener(eventName, eventId)
          }
          return null
        }

        if (cmd === 'plugin:event|emit' || cmd === 'plugin:event|emit_to') {
          const eventName = args['event']
          if (typeof eventName === 'string') {
            dispatchEvent(eventName, args['payload'])
          }
          return null
        }

        const invokeEndpoint = window.location.origin === origin ? '/tray/invoke' : `${origin}/tray/invoke`
        const response = await fetch(invokeEndpoint, {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ cmd, args }),
        })

        const payload = (await response.json()) as InvokeResponse
        if (!response.ok || !payload.ok) {
          const msg = !payload.ok && payload.error?.message ? payload.error.message : `Tray command ${cmd} failed (${response.status})`
          throw new Error(msg)
        }

        for (const emitted of payload.emittedEvents ?? []) {
          dispatchEvent(emitted.event, emitted.payload)
        }

        return payload.result
      },
    }
  }, bridgeOrigin)
}
