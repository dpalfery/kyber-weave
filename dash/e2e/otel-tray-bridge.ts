import { spawnSync } from 'node:child_process'
import {
  closeSync,
  constants,
  existsSync,
  fstatSync,
  lstatSync,
  openSync,
  readFileSync,
  readSync,
  realpathSync,
  statSync,
} from 'node:fs'
import { createServer, type IncomingMessage, type ServerResponse } from 'node:http'
import { dirname, extname, isAbsolute, join, relative, resolve, sep } from 'node:path'
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
export const MAX_PROXY_RESPONSE_BYTES = 10 * 1024 * 1024
export const MIN_CADENCE_MINUTES = 1
export const MAX_CADENCE_MINUTES = 1440
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

export function isValidRefreshCadenceMinutes(value: unknown): value is number {
  return (
    typeof value === 'number' &&
    Number.isFinite(value) &&
    Number.isInteger(value) &&
    value >= MIN_CADENCE_MINUTES &&
    value <= MAX_CADENCE_MINUTES
  )
}

export function isValidApiVersion(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && Number.isInteger(value) && value >= 1
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
  if (
    typeof days === 'number' &&
    Number.isFinite(days) &&
    Number.isInteger(days) &&
    days >= 1 &&
    days <= MAX_WINDOW_DAYS
  ) {
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

class RequestBodyTooLargeError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'RequestBodyTooLargeError'
  }
}

export class UpstreamResponseTooLargeError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'UpstreamResponseTooLargeError'
  }
}

async function readRequestBody(req: IncomingMessage): Promise<string> {
  return new Promise<string>((resolveBody, rejectBody) => {
    const chunks: Buffer[] = []
    let totalBytes = 0
    let settled = false

    req.on('data', (chunk: Buffer) => {
      if (settled) {
        return
      }
      totalBytes += chunk.length
      if (totalBytes > MAX_BODY_BYTES) {
        settled = true
        rejectBody(new RequestBodyTooLargeError(`Request body exceeded ${MAX_BODY_BYTES} bytes`))
        req.destroy()
        return
      }
      chunks.push(chunk)
    })

    req.once('end', () => {
      if (settled) {
        return
      }
      settled = true
      resolveBody(Buffer.concat(chunks).toString('utf8'))
    })

    req.once('error', (error) => {
      if (settled) {
        return
      }
      settled = true
      rejectBody(error)
    })
  })
}

function assertValidMaxResponseBytes(raw: unknown, fieldName: string): number {
  if (
    typeof raw !== 'number' ||
    !Number.isFinite(raw) ||
    !Number.isInteger(raw) ||
    raw < 1 ||
    raw > MAX_PROXY_RESPONSE_BYTES
  ) {
    throw new Error(
      `${fieldName} must be a positive integer at most ${MAX_PROXY_RESPONSE_BYTES}, got ${String(raw)}`,
    )
  }
  return raw
}

function resolveMaxResponseBytes(raw: unknown, fieldName: string): number {
  return raw === undefined ? MAX_PROXY_RESPONSE_BYTES : assertValidMaxResponseBytes(raw, fieldName)
}

export async function readBoundedResponseBuffer(response: Response, maxBytes: number): Promise<Buffer> {
  const validatedMaxBytes = assertValidMaxResponseBytes(maxBytes, 'maxBytes')
  const contentLengthHeader = response.headers.get('content-length')
  if (contentLengthHeader !== null && contentLengthHeader.trim() !== '') {
    const declaredLength = Number(contentLengthHeader)
    if (Number.isFinite(declaredLength) && declaredLength > validatedMaxBytes) {
      await response.body?.cancel().catch(() => undefined)
      throw new UpstreamResponseTooLargeError(
        `Upstream response Content-Length (${declaredLength} bytes) exceeded ${validatedMaxBytes} byte limit`,
      )
    }
  }

  if (response.body === null) {
    return Buffer.alloc(0)
  }

  const reader = response.body.getReader()
  const chunks: Uint8Array[] = []
  let totalBytes = 0
  try {
    while (true) {
      const { done, value } = await reader.read()
      if (done) {
        break
      }
      if (value !== undefined) {
        totalBytes += value.byteLength
        if (totalBytes > validatedMaxBytes) {
          await reader.cancel().catch(() => undefined)
          throw new UpstreamResponseTooLargeError(`Upstream response exceeded ${validatedMaxBytes} byte limit`)
        }
        chunks.push(value)
      }
    }
  } finally {
    reader.releaseLock()
  }

  return Buffer.concat(chunks)
}

function sendJson(res: ServerResponse, statusCode: number, payload: unknown): void {
  const body = JSON.stringify(payload)
  res.writeHead(statusCode, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
  })
  res.end(body)
}

export type StaticAssetResolution =
  | { readonly ok: true; readonly filePath: string; readonly contentType: string }
  | { readonly ok: false; readonly statusCode: 400 | 403 | 404; readonly message: string }

function verifyNoSymlinkSegments(
  realDistDir: string,
  segments: readonly string[],
): { readonly ok: true } | { readonly ok: false; readonly statusCode: 403 | 404; readonly message: string } {
  let current = realDistDir
  for (const segment of segments) {
    current = join(current, segment)
    try {
      if (lstatSync(current).isSymbolicLink()) {
        return { ok: false, statusCode: 403, message: 'Forbidden' }
      }
    } catch (error) {
      if (error instanceof Error && 'code' in error && (error.code === 'ENOENT' || error.code === 'ENOTDIR')) {
        return { ok: false, statusCode: 404, message: 'Not found' }
      }
      return { ok: false, statusCode: 403, message: 'Forbidden' }
    }
  }
  return { ok: true }
}

export function resolveStaticAsset(distDir: string, pathname: string): StaticAssetResolution {
  if (pathname.includes('\0')) {
    return { ok: false, statusCode: 400, message: 'Bad request path' }
  }

  let decodedPath: string
  try {
    decodedPath = decodeURIComponent(pathname)
  } catch {
    return { ok: false, statusCode: 400, message: 'Bad request path' }
  }

  if (decodedPath.includes('\0')) {
    return { ok: false, statusCode: 400, message: 'Bad request path' }
  }

  if (decodedPath.includes('\\') || decodedPath.startsWith('//') || !decodedPath.startsWith('/')) {
    return { ok: false, statusCode: 403, message: 'Forbidden' }
  }

  const relativeRaw = decodedPath === '/' ? 'index.html' : decodedPath.slice(1)
  if (
    relativeRaw === '' ||
    isAbsolute(relativeRaw) ||
    /^[a-zA-Z]:/.test(relativeRaw) ||
    relativeRaw.split('/').some((seg) => seg === '..' || seg === '.')
  ) {
    return { ok: false, statusCode: 403, message: 'Forbidden' }
  }

  let realDistDir: string
  try {
    realDistDir = realpathSync(distDir)
  } catch {
    return { ok: false, statusCode: 404, message: 'Not found' }
  }

  const realDistPrefix = realDistDir.endsWith(sep) ? realDistDir : `${realDistDir}${sep}`
  const candidate = resolve(realDistDir, relativeRaw)
  if (candidate === realDistDir || !candidate.startsWith(realDistPrefix)) {
    return { ok: false, statusCode: 403, message: 'Forbidden' }
  }

  const segments = relativeRaw.split('/').filter((seg) => seg.length > 0)
  const segmentCheck = verifyNoSymlinkSegments(realDistDir, segments)
  if (!segmentCheck.ok) {
    return segmentCheck
  }

  let realCandidate: string
  try {
    realCandidate = realpathSync(candidate)
  } catch {
    return { ok: false, statusCode: 404, message: 'Not found' }
  }

  if (realCandidate === realDistDir || !realCandidate.startsWith(realDistPrefix)) {
    return { ok: false, statusCode: 403, message: 'Forbidden' }
  }

  try {
    if (!statSync(realCandidate).isFile()) {
      return { ok: false, statusCode: 404, message: 'Not found' }
    }
  } catch {
    return { ok: false, statusCode: 404, message: 'Not found' }
  }

  const ext = extname(realCandidate).toLowerCase()
  const contentType = MIME_TYPES[ext] ?? 'application/octet-stream'
  return { ok: true, filePath: realCandidate, contentType }
}

export type StaticAssetReadResult =
  | { readonly ok: true; readonly contents: Buffer; readonly contentType: string }
  | { readonly ok: false; readonly statusCode: 403 | 404; readonly message: string }

function mapStaticAssetOpenError(error: unknown): { readonly ok: false; readonly statusCode: 403 | 404; readonly message: string } {
  const code = error instanceof Error && 'code' in error ? error.code : undefined
  if (code === 'ELOOP' || code === 'EEXIST' || code === 'EACCES' || code === 'EPERM') {
    return { ok: false, statusCode: 403, message: 'Forbidden' }
  }
  return { ok: false, statusCode: 404, message: 'Not found' }
}

function readOpenedFileDescriptor(fd: number, maxBytes: number): Buffer {
  const chunks: Buffer[] = []
  let totalBytes = 0
  const chunkSize = 65_536
  while (true) {
    const chunk = Buffer.allocUnsafe(chunkSize)
    const bytesRead = readSync(fd, chunk, 0, chunkSize, null)
    if (bytesRead === 0) {
      break
    }
    totalBytes += bytesRead
    if (totalBytes > maxBytes) {
      throw new Error(`Static asset exceeded ${maxBytes} byte limit`)
    }
    chunks.push(bytesRead === chunkSize ? chunk : chunk.subarray(0, bytesRead))
  }
  return Buffer.concat(chunks, totalBytes)
}

export function readResolvedStaticAsset(
  distDir: string,
  resolved: Extract<StaticAssetResolution, { readonly ok: true }>,
): StaticAssetReadResult {
  let realDistDir: string
  try {
    realDistDir = realpathSync(distDir)
  } catch {
    return { ok: false, statusCode: 404, message: 'Not found' }
  }

  const realDistPrefix = realDistDir.endsWith(sep) ? realDistDir : `${realDistDir}${sep}`
  const targetPath = resolve(resolved.filePath)
  if (targetPath === realDistDir || !targetPath.startsWith(realDistPrefix)) {
    return { ok: false, statusCode: 403, message: 'Forbidden' }
  }

  const openFlags =
    process.platform !== 'win32'
      ? constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0)
      : constants.O_RDONLY

  let fd: number | undefined
  try {
    fd = openSync(targetPath, openFlags)
    const fdStat = fstatSync(fd)
    if (!fdStat.isFile()) {
      return { ok: false, statusCode: 404, message: 'Not found' }
    }

    // Re-verify path containment and symlink-free traversal after opening the descriptor
    // so neither a final-component swap nor a parent-directory symlink swap can leak files.
    const relPath = relative(realDistDir, targetPath)
    const segments = relPath.split(/[\\/]+/).filter((seg) => seg.length > 0)
    if (segments.length === 0 || segments.some((seg) => seg === '..' || seg === '.')) {
      return { ok: false, statusCode: 403, message: 'Forbidden' }
    }

    const segmentCheck = verifyNoSymlinkSegments(realDistDir, segments)
    if (!segmentCheck.ok) {
      return segmentCheck
    }

    const pathStat = lstatSync(targetPath)
    if (pathStat.isSymbolicLink() || pathStat.dev !== fdStat.dev || pathStat.ino !== fdStat.ino) {
      return { ok: false, statusCode: 403, message: 'Forbidden' }
    }

    const postRealPath = realpathSync(targetPath)
    if (postRealPath === realDistDir || !postRealPath.startsWith(realDistPrefix)) {
      return { ok: false, statusCode: 403, message: 'Forbidden' }
    }

    if (fdStat.size > MAX_PROXY_RESPONSE_BYTES) {
      return { ok: false, statusCode: 403, message: 'Forbidden' }
    }

    const contents = readOpenedFileDescriptor(fd, MAX_PROXY_RESPONSE_BYTES)
    return { ok: true, contents, contentType: resolved.contentType }
  } catch (error) {
    return mapStaticAssetOpenError(error)
  } finally {
    if (fd !== undefined) {
      closeSync(fd)
    }
  }
}

function serveStaticAsset(res: ServerResponse, distDir: string, pathname: string): void {
  const resolved = resolveStaticAsset(distDir, pathname)
  if (!resolved.ok) {
    res.writeHead(resolved.statusCode, { 'content-type': 'text/plain; charset=utf-8' })
    res.end(resolved.message)
    return
  }

  const asset = readResolvedStaticAsset(distDir, resolved)
  if (!asset.ok) {
    res.writeHead(asset.statusCode, { 'content-type': 'text/plain; charset=utf-8' })
    res.end(asset.message)
    return
  }

  res.writeHead(200, {
    'content-type': asset.contentType,
    'cache-control': 'no-store',
  })
  res.end(asset.contents)
}

async function fetchLoopbackJson(
  url: string,
  init?: { readonly method?: 'GET' | 'POST' | 'PUT'; readonly body?: unknown; readonly maxResponseBytes?: number },
): Promise<{
  readonly ok: boolean
  readonly status: number
  readonly data: unknown
  readonly errorMessage: string | null
}> {
  const method = init?.method ?? 'GET'
  const maxResponseBytes = resolveMaxResponseBytes(init?.maxResponseBytes, 'maxResponseBytes')
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

  const text = (await readBoundedResponseBuffer(response, maxResponseBytes)).toString('utf8')
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

export function buildTrayViewState(input: {
  readonly report: Record<string, unknown> | null
  readonly reportFetchedAt: string | null
  readonly error: string | null
  readonly jobs: Record<string, unknown> | null
  readonly sharedSettings: TraySharedSettings | null
  readonly coverage: Record<string, unknown> | null
  readonly kyberdashVersion: { version: string; apiVersion: number } | null
  readonly settings: TraySettings
}): TrayViewState {
  // Any refresh error (including initial refresh when report is still null) yields 'stale'
  // so failure states cannot masquerade as a clean empty state.
  const phase: TrayPhase = input.error !== null ? 'stale' : 'ready'
  return {
    phase,
    report: input.report,
    reportFetchedAt: input.reportFetchedAt,
    error: input.error,
    jobs: input.jobs,
    sharedSettings: input.sharedSettings,
    coverage: input.coverage,
    kyberdashVersion: input.kyberdashVersion,
    settings: { ...input.settings },
  }
}

/**
 * Starts a same-origin Node HTTP server serving the built `dash/tray/ui/dist` bundle,
 * `GET /tray/state`, and `POST /tray/invoke` while proxying `/api/kyber/*` to the isolated
 * `kyberdash` server over loopback HTTP without an `Origin` header.
 */
export async function startTrayBridge(options: {
  readonly serverUrl: string
  readonly forceBuild?: boolean
  readonly distDir?: string
  readonly maxProxyResponseBytes?: number
}): Promise<TrayBridgeHandle> {
  const serverOrigin = assertLoopbackServerOrigin(options.serverUrl)
  const distDir = options.distDir ?? ensureTrayUiBuilt({ forceRebuild: options.forceBuild })
  const maxProxyBytes = resolveMaxResponseBytes(options.maxProxyResponseBytes, 'maxProxyResponseBytes')
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

  const buildViewState = (): TrayViewState =>
    buildTrayViewState({
      report: cachedReport,
      reportFetchedAt,
      error: lastError,
      jobs,
      sharedSettings,
      coverage,
      kyberdashVersion,
      settings: traySettings,
    })

  const refreshState = async (): Promise<TrayViewState> => {
    const qs = new URLSearchParams({ days: String(traySettings.windowDays) })
    if (traySettings.harness !== 'all') {
      qs.set('harness', traySettings.harness)
    }

    try {
      const reportRes = await fetchLoopbackJson(`${serverOrigin}/api/kyber/report?${qs.toString()}`, {
        maxResponseBytes: maxProxyBytes,
      })
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
      fetchLoopbackJson(`${serverOrigin}/api/kyber/jobs`, { maxResponseBytes: maxProxyBytes }).catch(() => null),
      fetchLoopbackJson(`${serverOrigin}/api/kyber/settings`, { maxResponseBytes: maxProxyBytes }).catch(() => null),
      fetchLoopbackJson(`${serverOrigin}/api/kyber/coverage`, { maxResponseBytes: maxProxyBytes }).catch(() => null),
      fetchLoopbackJson(`${serverOrigin}/api/kyber/version`, { maxResponseBytes: maxProxyBytes }).catch(() => null),
    ])

    jobs = jobsRes?.ok && isPlainObject(jobsRes.data) ? jobsRes.data : null
    sharedSettings =
      settingsRes?.ok &&
      isPlainObject(settingsRes.data) &&
      typeof settingsRes.data['folderImportScheduled'] === 'boolean' &&
      typeof settingsRes.data['jobsPaused'] === 'boolean' &&
      isValidRefreshCadenceMinutes(settingsRes.data['refreshCadenceMinutes']) &&
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
      versionRes.data['version'].trim() !== '' &&
      isValidApiVersion(versionRes.data['apiVersion'])
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
        if (
          patch['refreshCadenceMinutes'] !== undefined &&
          !isValidRefreshCadenceMinutes(patch['refreshCadenceMinutes'])
        ) {
          throw new Error(
            `refreshCadenceMinutes must be an integer between ${MIN_CADENCE_MINUTES} and ${MAX_CADENCE_MINUTES}`,
          )
        }
        for (const boolKey of ['folderImportScheduled', 'jobsPaused', 'receiverHosted'] as const) {
          if (patch[boolKey] !== undefined && typeof patch[boolKey] !== 'boolean') {
            throw new Error(`${boolKey} must be a boolean`)
          }
        }
        const res = await fetchLoopbackJson(`${serverOrigin}/api/kyber/settings`, {
          method: 'PUT',
          body: patch,
          maxResponseBytes: maxProxyBytes,
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
          sendJson(res, 400, { ok: false, error: { kind: 'runtime', message: 'Invalid refresh query value' } })
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
          sendJson(res, 400, { ok: false, error: { kind: 'runtime', message: 'Unknown tray command' } })
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
        } catch {
          // Both the response and the log line stay static: the caught error's
          // text can embed request-controlled values (the unknown-command error
          // names the command), so it must reach neither. The failing command
          // is already recorded structurally in invocations below.
          console.error('tray invoke failed')
          const message = 'tray command failed'
          invocations.push({ cmd, args, ok: false, error: message, at: new Date().toISOString() })
          sendJson(res, 400, { ok: false, error: { kind: 'runtime', message } })
        }
        return
      }

      if (pathname.startsWith('/api/kyber/')) {
        if (pathname.includes('\0')) {
          sendJson(res, 400, { error: 'Invalid /api/kyber path' })
          return
        }
        let decodedApiPath: string
        try {
          decodedApiPath = decodeURIComponent(pathname)
        } catch {
          sendJson(res, 400, { error: 'Invalid /api/kyber path' })
          return
        }
        if (decodedApiPath.includes('\0') || decodedApiPath.split('/').some((seg) => seg === '..')) {
          sendJson(res, 400, { error: 'Invalid /api/kyber path' })
          return
        }
        const targetUrl = `${serverOrigin}${pathname}${requestUrl.search}`
        const bodyText = method === 'GET' || method === 'HEAD' ? undefined : await readRequestBody(req)
        let upstreamRes: Response
        try {
          upstreamRes = await fetch(targetUrl, {
            method,
            headers: bodyText !== undefined ? { 'content-type': 'application/json' } : {},
            ...(bodyText !== undefined ? { body: bodyText } : {}),
            signal: AbortSignal.timeout(UPSTREAM_TIMEOUT_MS),
          })
        } catch {
          console.error('tray proxy upstream request failed')
          sendJson(res, 502, { error: 'Upstream Gateway Error' })
          return
        }
        let upstreamBody: Buffer
        try {
          upstreamBody = await readBoundedResponseBuffer(upstreamRes, maxProxyBytes)
        } catch (error) {
          if (error instanceof UpstreamResponseTooLargeError) {
            sendJson(res, 502, { error: 'Upstream response exceeded size limit' })
            return
          }
          console.error('tray proxy upstream response failed')
          sendJson(res, 502, { error: 'Upstream Gateway Error' })
          return
        }
        res.writeHead(upstreamRes.status, {
          'content-type': upstreamRes.headers.get('content-type') ?? 'application/json; charset=utf-8',
          'cache-control': 'no-store',
        })
        res.end(upstreamBody.length > 0 ? upstreamBody : undefined)
        return
      }

      if (method === 'GET') {
        serveStaticAsset(res, distDir, pathname)
        return
      }

      res.writeHead(405, { 'content-type': 'text/plain; charset=utf-8' })
      res.end('Method not allowed')
    } catch (error) {
      if (error instanceof RequestBodyTooLargeError) {
        sendJson(res, 413, { ok: false, error: { kind: 'runtime', message: 'Request body too large' } })
        return
      }
      // The response carries a static reason, never the caught error's text; the
      // detail stays in the bridge log for debugging.
      console.error('tray bridge request failed:', error instanceof Error ? error.message : String(error))
      sendJson(res, 500, { ok: false, error: { kind: 'runtime', message: 'Internal Server Error' } })
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
    getState: async (opts) => fetchTrayBridgeState(bridgeUrl, { refresh: opts?.refresh, maxBytes: maxProxyBytes }),
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

function isTrayPhase(value: unknown): value is TrayPhase {
  return value === 'setup' || value === 'starting' || value === 'ready' || value === 'stale'
}

function isNullablePlainObject(value: unknown): value is Record<string, unknown> | null {
  return value === null || isPlainObject(value)
}

function isNullableString(value: unknown): value is string | null {
  return value === null || typeof value === 'string'
}

function isNonNegativeInt(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && Number.isInteger(value) && value >= 0
}

function isTraySettings(value: unknown): value is TraySettings {
  return (
    isPlainObject(value) &&
    typeof value['harness'] === 'string' &&
    typeof value['windowDays'] === 'number' &&
    Number.isFinite(value['windowDays']) &&
    Number.isInteger(value['windowDays']) &&
    value['windowDays'] >= 1 &&
    isFraction(value['attentionThreshold']) &&
    isFraction(value['criticalThreshold']) &&
    typeof value['launchAtLogin'] === 'boolean'
  )
}

function isTraySharedSettings(value: unknown): value is TraySharedSettings {
  return (
    isPlainObject(value) &&
    typeof value['folderImportScheduled'] === 'boolean' &&
    typeof value['jobsPaused'] === 'boolean' &&
    isValidRefreshCadenceMinutes(value['refreshCadenceMinutes']) &&
    typeof value['receiverHosted'] === 'boolean'
  )
}

function isKyberdashVersionRecord(value: unknown): value is { version: string; apiVersion: number } {
  return (
    isPlainObject(value) &&
    typeof value['version'] === 'string' &&
    value['version'].trim() !== '' &&
    isValidApiVersion(value['apiVersion'])
  )
}

function isTrayViewState(value: unknown): value is TrayViewState {
  if (!isPlainObject(value)) {
    return false
  }
  if (!isTrayPhase(value['phase'])) {
    return false
  }
  if (!isNullablePlainObject(value['report'])) {
    return false
  }
  if (!isNullableString(value['reportFetchedAt']) || !isNullableString(value['error'])) {
    return false
  }
  if (!isNullablePlainObject(value['jobs']) || !isNullablePlainObject(value['coverage'])) {
    return false
  }
  if (value['sharedSettings'] !== null && !isTraySharedSettings(value['sharedSettings'])) {
    return false
  }
  if (value['kyberdashVersion'] !== null && !isKyberdashVersionRecord(value['kyberdashVersion'])) {
    return false
  }
  return isTraySettings(value['settings'])
}

function isTrayInvocationRecord(value: unknown): value is TrayInvocationRecord {
  return (
    isPlainObject(value) &&
    typeof value['cmd'] === 'string' &&
    isPlainObject(value['args']) &&
    typeof value['ok'] === 'boolean' &&
    (value['error'] === undefined || typeof value['error'] === 'string') &&
    typeof value['at'] === 'string'
  )
}

function isOpenedViewRecord(value: unknown): value is OpenedViewRecord {
  return (
    isPlainObject(value) &&
    typeof value['view'] === 'string' &&
    typeof value['url'] === 'string' &&
    typeof value['at'] === 'string'
  )
}

export function parseTrayBridgeStateSnapshot(raw: unknown): TrayBridgeStateSnapshot {
  if (!isPlainObject(raw)) {
    throw new Error('Invalid /tray/state payload: expected a JSON object')
  }
  if (!isTrayViewState(raw['viewState'])) {
    throw new Error('Invalid /tray/state payload: malformed viewState')
  }
  const rawInvocations = raw['invocations']
  if (!Array.isArray(rawInvocations) || !rawInvocations.every(isTrayInvocationRecord)) {
    throw new Error('Invalid /tray/state payload: malformed invocations array')
  }
  const rawOpenedViews = raw['openedViews']
  if (!Array.isArray(rawOpenedViews) || !rawOpenedViews.every(isOpenedViewRecord)) {
    throw new Error('Invalid /tray/state payload: malformed openedViews array')
  }
  if (!isNonNegativeInt(raw['hidePopoverCount'])) {
    throw new Error('Invalid /tray/state payload: malformed hidePopoverCount')
  }
  if (!isNonNegativeInt(raw['quitCount'])) {
    throw new Error('Invalid /tray/state payload: malformed quitCount')
  }

  return {
    viewState: raw['viewState'],
    invocations: rawInvocations,
    openedViews: rawOpenedViews,
    hidePopoverCount: raw['hidePopoverCount'],
    quitCount: raw['quitCount'],
  }
}

export async function fetchTrayBridgeState(
  bridgeUrl: string,
  options?: { readonly refresh?: boolean; readonly maxBytes?: number },
): Promise<TrayBridgeStateSnapshot> {
  const origin = assertLoopbackServerOrigin(bridgeUrl)
  const suffix = options?.refresh ? '?refresh=1' : ''
  const maxBytes = resolveMaxResponseBytes(options?.maxBytes, 'maxBytes')
  const res = await fetch(`${origin}/tray/state${suffix}`, {
    signal: AbortSignal.timeout(UPSTREAM_TIMEOUT_MS),
  })
  if (!res.ok) {
    throw new Error(`GET /tray/state failed (${res.status})`)
  }
  const rawText = (await readBoundedResponseBuffer(res, maxBytes)).toString('utf8')
  let parsed: unknown
  try {
    parsed = JSON.parse(rawText)
  } catch (error) {
    throw new Error('GET /tray/state returned invalid JSON', { cause: error })
  }
  return parseTrayBridgeStateSnapshot(parsed)
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

        const rawPayload: unknown = await response.json().catch(() => null)
        const payloadObj =
          typeof rawPayload === 'object' && rawPayload !== null && !Array.isArray(rawPayload)
            ? (rawPayload as Record<string, unknown>)
            : null
        const isOk = payloadObj !== null && payloadObj['ok'] === true
        if (!response.ok || !isOk) {
          const errorObj =
            payloadObj !== null &&
            typeof payloadObj['error'] === 'object' &&
            payloadObj['error'] !== null &&
            !Array.isArray(payloadObj['error'])
              ? (payloadObj['error'] as Record<string, unknown>)
              : null
          const msg =
            errorObj !== null && typeof errorObj['message'] === 'string'
              ? errorObj['message']
              : `Tray command ${cmd} failed (${response.status})`
          throw new Error(msg)
        }

        const emittedRaw = payloadObj['emittedEvents']
        if (Array.isArray(emittedRaw)) {
          for (const emitted of emittedRaw) {
            if (
              typeof emitted === 'object' &&
              emitted !== null &&
              !Array.isArray(emitted) &&
              typeof (emitted as Record<string, unknown>)['event'] === 'string'
            ) {
              const entry = emitted as { event: string; payload?: unknown }
              dispatchEvent(entry.event, entry.payload)
            }
          }
        }

        return payloadObj['result']
      },
    }
  }, bridgeOrigin)
}
