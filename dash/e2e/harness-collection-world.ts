// Shared harness-collection world backed by `kyberdash e2e-seed` (never ~/.kyberdash/canon.db).

import { spawn, spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, mkdtempSync } from 'node:fs'
import { homedir, tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

export const USER_CANON = join(homedir(), '.kyberdash', 'canon.db')

export const MEASURED_HARNESS = 'claude-code'
export const HONEST_EMPTY_HARNESS = 'opencode'
export const ZERO_RECORD_HARNESS = 'droid'
/** The session derived from OpenCode run A (`e2e/fixtures/opencode/otlp-v3-runA.consent-on.json`). */
export const OPENCODE_REPLAY_SESSION_ID = 'ses_e74c398c7ffeVajzhRWZNgfR2n'

export type CollectedWorld = {
  readonly root: string
  readonly dbPath: string
}

export function createTempRoot(): string {
  const root = mkdtempSync(join(tmpdir(), 'kyber-harness-collection-'))
  if (!root.startsWith(tmpdir())) {
    throw new Error(`harness-collection temp root escaped tmpdir: ${root}`)
  }
  if (root.includes('.kyberdash')) {
    throw new Error(`harness-collection temp root must not sit under .kyberdash: ${root}`)
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
  if (dbPath === USER_CANON) {
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
  if (dbPath === USER_CANON) {
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
