// Shared harness-collection world backed by `kyberdash e2e-seed` (never ~/.kyberdash/canon.db).

import { spawn, spawnSync } from 'node:child_process'
import { existsSync, lstatSync, mkdirSync, mkdtempSync, realpathSync, rmSync } from 'node:fs'
import { homedir, tmpdir } from 'node:os'
import { dirname, join, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'

export const USER_CANON = join(homedir(), '.kyberdash', 'canon.db')

export const MEASURED_HARNESS = 'claude-code'
export const HONEST_EMPTY_HARNESS = 'opencode'
export const ZERO_RECORD_HARNESS = 'droid'
/** The session derived from OpenCode run A (`e2e/fixtures/opencode/otlp-v3-runA.consent-on.json`). */
export const OPENCODE_REPLAY_SESSION_ID = 'ses_e74c398c7ffeVajzhRWZNgfR2n'

/**
 * The widest sessions page the server serves: `KyberBridgeEngine.DefaultSessionPageLimit`,
 * which `/api/kyber/sessions` also clamps `limit` to. A larger `limit` is served as this many
 * rows, so a read that must see a session it may not find on one page searches for it by id.
 */
export const SESSION_PAGE_LIMIT = 100

/**
 * The widest page a session section route serves: `KyberBridgeEngine.DefaultSectionPageLimit`,
 * which every `/api/kyber/session/{id}/<section>` route clamps `limit` to. A read that must see
 * a whole section walks its pages.
 */
export const SECTION_PAGE_LIMIT = 25

export type CollectedWorld = {
  readonly root: string
  readonly dbPath: string
}

function ensureTrailingSep(dir: string): string {
  return dir.endsWith(sep) ? dir : `${dir}${sep}`
}

function findNearestExistingAncestor(targetPath: string): string | null {
  let current = targetPath
  while (true) {
    try {
      lstatSync(current)
      return current
    } catch (error) {
      if (!(error instanceof Error && 'code' in error && error.code === 'ENOENT')) {
        return null
      }
      const parent = dirname(current)
      if (parent === current) {
        return null
      }
      current = parent
    }
  }
}

export function isIsolatedTempRoot(rootDir: string, baseTmpDir: string = tmpdir()): boolean {
  if (rootDir.trim() === '' || rootDir.includes('\0') || baseTmpDir.trim() === '' || baseTmpDir.includes('\0')) {
    return false
  }

  const resolvedTmp = resolve(baseTmpDir)
  const tmpPrefix = ensureTrailingSep(resolvedTmp)
  const resolvedTarget = resolve(rootDir)

  if (resolvedTarget === resolvedTmp || !resolvedTarget.startsWith(tmpPrefix)) {
    return false
  }
  if (resolvedTarget.includes('.kyberdash')) {
    return false
  }

  if (existsSync(resolvedTmp)) {
    let realTmp: string
    try {
      realTmp = realpathSync(resolvedTmp)
    } catch {
      return false
    }
    const realTmpPrefix = ensureTrailingSep(realTmp)

    try {
      if (lstatSync(resolvedTarget).isSymbolicLink()) {
        return false
      }
      const realTarget = realpathSync(resolvedTarget)
      if (realTarget === realTmp || !realTarget.startsWith(realTmpPrefix) || realTarget.includes('.kyberdash')) {
        return false
      }
    } catch (error) {
      if (!(error instanceof Error && 'code' in error && error.code === 'ENOENT')) {
        return false
      }
      const ancestor = findNearestExistingAncestor(resolvedTarget)
      if (ancestor === null) {
        return false
      }
      try {
        if (ancestor !== resolvedTmp && lstatSync(ancestor).isSymbolicLink()) {
          return false
        }
        const realAncestor = realpathSync(ancestor)
        const isAllowedAncestor = realAncestor === realTmp || realAncestor.startsWith(realTmpPrefix)
        if (!isAllowedAncestor || realAncestor.includes('.kyberdash')) {
          return false
        }
      } catch {
        return false
      }
    }
  }

  return true
}

export function createTempRoot(baseTmpDir: string = tmpdir()): string {
  const resolvedBase = resolve(baseTmpDir)
  const root = mkdtempSync(join(resolvedBase, 'kyber-harness-collection-'))
  if (!isIsolatedTempRoot(root, resolvedBase)) {
    throw new Error(`harness-collection temp root escaped tmpdir: ${root}`)
  }
  return root
}

export function resolveKyberdashCommand(): {
  readonly command: string
  readonly prefixArgs: readonly string[]
  readonly repoRoot: string
  readonly dashRoot: string
} {
  const e2eDir = dirname(fileURLToPath(import.meta.url))
  const dashRoot = resolve(e2eDir, '..')
  const repoRoot = resolve(dashRoot, '..')
  const explicit = process.env['KYBERDASH_BIN']?.trim()
  if (explicit) {
    return { command: explicit, prefixArgs: [], repoRoot, dashRoot }
  }

  const exeName = process.platform === 'win32' ? 'kyberdash.exe' : 'kyberdash'
  for (const config of ['Release', 'Debug']) {
    const candidate = join(repoRoot, 'dash', 'server', 'KyberDash.Cli', 'bin', config, 'net10.0', exeName)
    if (existsSync(candidate)) {
      return { command: candidate, prefixArgs: [], repoRoot, dashRoot }
    }
  }

  return {
    command: 'dotnet',
    prefixArgs: ['run', '--project', join(repoRoot, 'dash', 'server', 'KyberDash.Cli'), '-c', 'Release', '--'],
    repoRoot,
    dashRoot,
  }
}

export function runE2eSeed(worldName: 'harness-collection' | 'factory-replay', dbPath: string): void {
  const { command, prefixArgs, repoRoot } = resolveKyberdashCommand()
  const result = spawnSync(command, [...prefixArgs, 'e2e-seed', '--world', worldName, '--db', dbPath], {
    cwd: repoRoot,
    encoding: 'utf8',
    timeout: 90_000,
    killSignal: 'SIGKILL',
  })
  if (result.status !== 0) {
    throw new Error(
      `kyberdash e2e-seed (${worldName}) exited ${result.status}: ${result.stdout ?? ''}\n${result.stderr ?? ''}`,
    )
  }
}

export async function refreshTempCanon(root = createTempRoot()): Promise<CollectedWorld> {
  const dbPath = join(root, 'canon.db')
  if (resolve(dbPath) === resolve(USER_CANON)) {
    throw new Error('refusing to use the user canon.db as a harness-collection write target')
  }
  runE2eSeed('harness-collection', dbPath)
  return { root, dbPath }
}

export async function bootIsolatedWorld(
  worldName: 'harness-collection' | 'factory-replay',
  prefix: 'HC' | 'FR',
): Promise<void> {
  const root = createTempRoot()
  const dbPath = join(root, 'canon.db')
  if (resolve(dbPath) === resolve(USER_CANON)) {
    throw new Error('refusing to serve the user canon.db')
  }

  runE2eSeed(worldName, dbPath)

  const stateDir = join(root, 'state')
  mkdirSync(stateDir, { recursive: true })

  const { command, prefixArgs, repoRoot, dashRoot } = resolveKyberdashCommand()
  const dashDist = join(dashRoot, 'dist', 'dash')
  const homeDir = join(root, 'home')
  mkdirSync(homeDir, { recursive: true })

  const child = spawn(
    command,
    [...prefixArgs, 'web', '--no-open', '--port', '0', '--db', dbPath, '--state-dir', stateDir],
    {
      cwd: repoRoot,
      env: {
        ...process.env,
        HOME: homeDir,
        USERPROFILE: homeDir,
        KYBER_CANON_DB: dbPath,
        KYBERDASH_STATE_DIR: stateDir,
        KYBERDASH_DASH_DIR: process.env['KYBERDASH_DASH_DIR'] ?? dashDist,
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    },
  )

  const forwardSignal = (signal: NodeJS.Signals): void => {
    if (child.exitCode === null && child.signalCode === null) {
      child.kill(signal)
    }
  }
  process.on('SIGTERM', () => forwardSignal('SIGTERM'))
  process.on('SIGINT', () => forwardSignal('SIGINT'))

  let buffer = ''
  let stderr = ''
  let readyEmitted = false

  child.stderr.on('data', (chunk: Buffer) => {
    stderr += chunk.toString('utf8')
  })

  child.stdout.on('data', (chunk: Buffer) => {
    if (readyEmitted) return
    buffer += chunk.toString('utf8')
    let newlineAt = buffer.indexOf('\n')
    while (newlineAt !== -1) {
      const line = buffer.slice(0, newlineAt)
      buffer = buffer.slice(newlineAt + 1)
      try {
        const parsed = JSON.parse(line) as { event?: string; url?: string }
        if (parsed.event === 'kyberdash.web.listening' && typeof parsed.url === 'string') {
          readyEmitted = true
          process.stdout.write(`${prefix}_READY ${parsed.url}\n`)
          process.stdout.write(`${prefix}_DB ${dbPath}\n`)
          return
        }
      } catch {
        // Ignore non-JSON lines.
      }
      newlineAt = buffer.indexOf('\n')
    }
  })

  await new Promise<void>((resolveExit, rejectExit) => {
    child.once('error', (error) => rejectExit(error))
    child.once('exit', (code) => {
      if (!readyEmitted) {
        rejectExit(new Error(`kyberdash web exited ${code} before ready: ${buffer}\n${stderr}`))
        return
      }
      process.exitCode = code ?? 0
      resolveExit()
    })
  })
}

export type IsolatedServerHandle = {
  readonly url: string
  readonly dbPath: string
  readonly stateDir: string
  readonly rootDir: string
  stop: () => Promise<void>
}

function isProcessGroupAlive(pid: number): boolean {
  try {
    process.kill(-pid, 0)
    return true
  } catch (error) {
    return (error as NodeJS.ErrnoException).code !== 'ESRCH'
  }
}

function signalProcessGroup(pid: number, signal: NodeJS.Signals): void {
  try {
    process.kill(-pid, signal)
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== 'ESRCH') {
      throw error
    }
  }
}

async function stopProcessGroup(pid: number): Promise<void> {
  signalProcessGroup(pid, 'SIGTERM')
  const deadline = Date.now() + 5_000
  while (isProcessGroupAlive(pid) && Date.now() < deadline) {
    await new Promise<void>((resolveWait) => setTimeout(resolveWait, 100))
  }
  if (isProcessGroupAlive(pid)) {
    signalProcessGroup(pid, 'SIGKILL')
  }
}

export function cleanupIsolatedTempRoot(rootDir: string, baseTmpDir: string = tmpdir()): void {
  if (!isIsolatedTempRoot(rootDir, baseTmpDir)) {
    throw new Error(`refusing to remove directory outside isolated tmpdir: ${rootDir}`)
  }
  rmSync(resolve(rootDir), { recursive: true, force: true })
}

export async function startIsolatedServer(options?: {
  readonly startupTimeoutMs?: number
}): Promise<IsolatedServerHandle> {
  const startupTimeoutMs = options?.startupTimeoutMs ?? 90_000
  const rootDir = mkdtempSync(join(tmpdir(), 'kyber-otel-e2e-'))
  if (!isIsolatedTempRoot(rootDir)) {
    throw new Error(`isolated server temp root invalid: ${rootDir}`)
  }

  const dbPath = join(rootDir, 'canon.db')
  if (resolve(dbPath) === resolve(USER_CANON)) {
    cleanupIsolatedTempRoot(rootDir)
    throw new Error('refusing to serve the user canon.db')
  }

  const stateDir = join(rootDir, 'state')
  const homeDir = join(rootDir, 'home')
  mkdirSync(stateDir, { recursive: true })
  mkdirSync(homeDir, { recursive: true })

  const { command, prefixArgs, repoRoot, dashRoot } = resolveKyberdashCommand()
  const dashDist = join(dashRoot, 'dist', 'dash')
  if (!existsSync(join(dashDist, 'index.html'))) {
    const built = spawnSync('npm', ['run', 'build:web'], {
      cwd: dashRoot,
      encoding: 'utf8',
      timeout: 300_000,
      killSignal: 'SIGKILL',
    })
    if (built.status !== 0) {
      cleanupIsolatedTempRoot(rootDir)
      throw new Error(`build:web failed: ${built.stdout ?? ''}\n${built.stderr ?? ''}`)
    }
  }

  const child = spawn(
    command,
    [...prefixArgs, 'web', '--no-open', '--port', '0', '--db', dbPath, '--state-dir', stateDir],
    {
      cwd: repoRoot,
      env: {
        ...process.env,
        HOME: homeDir,
        USERPROFILE: homeDir,
        KYBER_CANON_DB: dbPath,
        KYBERDASH_STATE_DIR: stateDir,
        KYBERDASH_DASH_DIR: process.env['KYBERDASH_DASH_DIR'] ?? dashDist,
      },
      detached: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    },
  )

  const pid = child.pid
  const killOnExit = (): void => {
    if (pid === undefined || !isProcessGroupAlive(pid)) return
    try {
      signalProcessGroup(pid, 'SIGKILL')
    } catch {
      // Process is exiting; nothing left can act on the error.
    }
  }
  process.once('exit', killOnExit)

  let url: string
  try {
    url = await new Promise<string>((resolveUrl, rejectUrl) => {
      let stdoutBuffer = ''
      let stderrBuffer = ''
      let settled = false

      const timer = setTimeout(() => {
        if (settled) return
        settled = true
        rejectUrl(
          new Error(
            `kyberdash web did not become ready within ${startupTimeoutMs}ms: ${stdoutBuffer}\n${stderrBuffer}`,
          ),
        )
      }, startupTimeoutMs)

      const finishError = (error: Error): void => {
        if (settled) return
        settled = true
        clearTimeout(timer)
        rejectUrl(error)
      }

      child.stderr.on('data', (chunk: Buffer) => {
        stderrBuffer += chunk.toString('utf8')
      })

      child.stdout.on('data', (chunk: Buffer) => {
        if (settled) return
        stdoutBuffer += chunk.toString('utf8')
        let newlineAt = stdoutBuffer.indexOf('\n')
        while (newlineAt !== -1) {
          const line = stdoutBuffer.slice(0, newlineAt)
          stdoutBuffer = stdoutBuffer.slice(newlineAt + 1)
          try {
            const parsed = JSON.parse(line) as { event?: unknown; url?: unknown }
            if (
              parsed.event === 'kyberdash.web.listening' &&
              typeof parsed.url === 'string' &&
              parsed.url.startsWith('http://127.0.0.1:')
            ) {
              settled = true
              clearTimeout(timer)
              resolveUrl(parsed.url)
              return
            }
          } catch {
            // Ignore non-JSON log lines.
          }
          newlineAt = stdoutBuffer.indexOf('\n')
        }
      })

      child.once('error', (error) => {
        finishError(new Error(`kyberdash web spawn failed: ${String(error)}`))
      })

      child.once('exit', (code) => {
        finishError(new Error(`kyberdash web exited ${code} before ready: ${stdoutBuffer}\n${stderrBuffer}`))
      })
    })
  } catch (error) {
    process.off('exit', killOnExit)
    if (pid !== undefined) {
      await stopProcessGroup(pid)
    } else if (child.exitCode === null && child.signalCode === null) {
      child.kill('SIGKILL')
    }
    cleanupIsolatedTempRoot(rootDir)
    throw error
  }

  let stopped = false
  return {
    url,
    dbPath,
    stateDir,
    rootDir,
    stop: async () => {
      if (stopped) return
      stopped = true
      process.off('exit', killOnExit)
      if (pid !== undefined) {
        await stopProcessGroup(pid)
      } else if (child.exitCode === null && child.signalCode === null) {
        child.kill('SIGKILL')
      }
      cleanupIsolatedTempRoot(rootDir)
    },
  }
}

function isPlainRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

async function postOtlpSignal(baseUrl: string, path: '/v1/traces' | '/v1/logs', payload: unknown): Promise<void> {
  const response = await fetch(`${baseUrl}${path}`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(payload),
    signal: AbortSignal.timeout(15_000),
  })
  if (!response.ok) {
    const body = await response.text().catch(() => '')
    throw new Error(`POST ${path} failed (${response.status}): ${body.slice(0, 400)}`)
  }
}

export async function postOtlpTraces(baseUrl: string, payload: unknown): Promise<void> {
  await postOtlpSignal(baseUrl, '/v1/traces', payload)
}

export async function postOtlpLogs(baseUrl: string, payload: unknown): Promise<void> {
  await postOtlpSignal(baseUrl, '/v1/logs', payload)
}

export async function waitForOtlpIngest(
  baseUrl: string,
  expectedSessionIds: readonly string[],
  options?: {
    readonly timeoutMs?: number
    readonly pollIntervalMs?: number
    readonly expectedHarnesses?: readonly string[]
  },
): Promise<void> {
  const timeoutMs = options?.timeoutMs ?? 30_000
  const pollIntervalMs = options?.pollIntervalMs ?? 250
  const expectedHarnesses = options?.expectedHarnesses ?? []
  const deadline = Date.now() + timeoutMs

  let missingSessions: string[] = [...expectedSessionIds]
  let missingHarnesses: string[] = [...expectedHarnesses]
  let lastError: string | undefined

  while (Date.now() <= deadline) {
    try {
      // Wait for OtlpCollectorService to drain its queue before querying /api/kyber/* so
      // EnsureProjected() runs once over the complete flushed corpus without racing itself.
      const healthRes = await fetch(`${baseUrl}/healthz`, { signal: AbortSignal.timeout(5_000) })
      if (!healthRes.ok) {
        lastError = `/healthz HTTP ${healthRes.status}`
        await new Promise<void>((resolveWait) => setTimeout(resolveWait, pollIntervalMs))
        continue
      }

      const healthJson: unknown = await healthRes.json()
      const pendingCount =
        isPlainRecord(healthJson) &&
        typeof healthJson['pendingCount'] === 'number' &&
        Number.isFinite(healthJson['pendingCount'])
          ? healthJson['pendingCount']
          : 0
      if (pendingCount > 0) {
        lastError = `collector still draining (${pendingCount} pending)`
        await new Promise<void>((resolveWait) => setTimeout(resolveWait, pollIntervalMs))
        continue
      }

      const sessionsRes = await fetch(`${baseUrl}/api/kyber/sessions?limit=${SESSION_PAGE_LIMIT}`, {
        signal: AbortSignal.timeout(10_000),
      })
      const harnessesRes = await fetch(`${baseUrl}/api/kyber/harnesses`, { signal: AbortSignal.timeout(10_000) })
      const coverageRes = await fetch(`${baseUrl}/api/kyber/coverage`, { signal: AbortSignal.timeout(10_000) })

      if (sessionsRes.ok && coverageRes.ok && harnessesRes.ok) {
        const sessionsJson: unknown = await sessionsRes.json()
        const coverageJson: unknown = await coverageRes.json()
        const harnessesJson: unknown = await harnessesRes.json()

        const seenSessions = new Set<string>()
        const sessionsPage = readSessionsPage(sessionsJson)
        for (const row of sessionsPage.sessions) {
          if (typeof row['session_id'] === 'string') seenSessions.add(row['session_id'])
          if (typeof row['sessionId'] === 'string') seenSessions.add(row['sessionId'])
        }

        // The page holds at most SESSION_PAGE_LIMIT roots. Only a store larger than that can
        // hold an expected session the page does not show, so only then is each one read back
        // through the route's own search.
        if (sessionsPage.total > sessionsPage.sessions.length) {
          for (const id of expectedSessionIds) {
            if (!seenSessions.has(id) && (await sessionsPageCarriesSession(baseUrl, id))) seenSessions.add(id)
          }
        }

        const seenHarnesses = new Set<string>()
        const harnessRows =
          isPlainRecord(harnessesJson) && Array.isArray(harnessesJson['harnesses'])
            ? harnessesJson['harnesses']
            : []
        for (const row of harnessRows) {
          if (!isPlainRecord(row)) continue
          if (
            typeof row['harness'] === 'string' &&
            typeof row['sampleCount'] === 'number' &&
            Number.isFinite(row['sampleCount']) &&
            row['sampleCount'] > 0
          ) {
            seenHarnesses.add(row['harness'])
          }
        }

        const hasIngest = isPlainRecord(coverageJson) && coverageJson['ingest'] !== undefined

        missingSessions = expectedSessionIds.filter((id) => !seenSessions.has(id))
        missingHarnesses = expectedHarnesses.filter((h) => !seenHarnesses.has(h))

        if (missingSessions.length === 0 && missingHarnesses.length === 0 && hasIngest) {
          return
        }
      } else {
        lastError = `HTTP ${sessionsRes.status}/${coverageRes.status}/${harnessesRes.status}`
      }
    } catch (error) {
      lastError = error instanceof Error ? error.message : String(error)
    }

    await new Promise<void>((resolveWait) => setTimeout(resolveWait, pollIntervalMs))
  }

  throw new Error(
    `waitForOtlpIngest timed out after ${timeoutMs}ms. Missing sessions: [${missingSessions.join(', ')}]; missing harnesses: [${missingHarnesses.join(', ')}]${lastError ? `; last error: ${lastError}` : ''}`,
  )
}

/** One page of the sessions list, as `GET /api/kyber/sessions` answers it. */
type SessionsPage = {
  sessions: Array<Record<string, unknown>>
  total: number
  page: number
  limit: number
}

/** Narrows a sessions answer to its page envelope; a malformed answer reads as an empty page. */
function readSessionsPage(value: unknown): SessionsPage {
  if (!isPlainRecord(value)) return { sessions: [], total: 0, page: 1, limit: 0 }
  const rows = Array.isArray(value['sessions']) ? value['sessions'].filter(isPlainRecord) : []
  const count = (key: 'total' | 'page' | 'limit', fallback: number): number => {
    const raw = value[key]
    return typeof raw === 'number' && Number.isFinite(raw) ? raw : fallback
  }
  return {
    sessions: rows,
    total: count('total', rows.length),
    page: count('page', 1),
    limit: count('limit', rows.length),
  }
}

/**
 * Whether the sessions page carries the session `sessionId`, read through the route's own search,
 * so the answer does not depend on where the corpus ordered the row.
 */
async function sessionsPageCarriesSession(baseUrl: string, sessionId: string): Promise<boolean> {
  const response = await fetch(
    `${baseUrl}/api/kyber/sessions?q=${encodeURIComponent(sessionId)}&limit=${SESSION_PAGE_LIMIT}`,
    { signal: AbortSignal.timeout(10_000) },
  )
  if (!response.ok) return false
  const page = readSessionsPage(await response.json())
  return page.sessions.some((row) => row['session_id'] === sessionId || row['sessionId'] === sessionId)
}
