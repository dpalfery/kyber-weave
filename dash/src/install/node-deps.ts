/**
 * The real implementations of the installer's ports.
 *
 * Kept apart from `menubar.ts` so the policy — what is refused, what is
 * restored — is testable without a network, a signed bundle or `/Applications`.
 * Everything here is mechanism.
 */

import { spawn } from 'node:child_process'
import { createRequire } from 'node:module'
import { constants } from 'node:fs'
import { access, mkdir, mkdtemp, readFile, rename, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { getConfigDir } from '../config.js'
import { EXPECTED_TEAM_ID, type CommandResult, type InstallDeps, type InstallFs, type Verifier } from './types.js'

async function run(command: string, args: string[]): Promise<CommandResult> {
  return new Promise((resolve) => {
    // No shell: every argument here can contain a user's own path, and a shell
    // would give those paths meaning they must not have.
    const child = spawn(command, args, { shell: false })
    let stdout = ''
    let stderr = ''
    child.stdout?.on('data', (chunk: Buffer) => {
      stdout += chunk.toString()
    })
    child.stderr?.on('data', (chunk: Buffer) => {
      stderr += chunk.toString()
    })
    child.on('error', (error) => resolve({ code: 127, stdout, stderr: error.message }))
    child.on('close', (code) => resolve({ code: code ?? 1, stdout, stderr }))
  })
}

/**
 * `codesign` prints `TeamIdentifier=XXXX` among its fields, and `not set` when
 * the bundle is ad-hoc signed. Reading the field rather than trusting the exit
 * code is what makes Requirement 12.6's team check mean anything.
 */
function parseTeamId(output: string): string | undefined {
  const match = /TeamIdentifier=(\S+)/.exec(output)
  if (match === null) return undefined
  const value = match[1]!
  return value === 'not' || value === 'not set' ? undefined : value
}

export const nodeVerifier: Verifier = {
  async codesign(appPath: string) {
    const verify = await run('codesign', ['--verify', '--deep', '--strict', appPath])
    if (verify.code !== 0) {
      return { ok: false, detail: verify.stderr.trim() || `codesign exited ${verify.code}` }
    }
    // A second call: --verify does not print the signing identity.
    const display = await run('codesign', ['--display', '--verbose=4', appPath])
    return {
      ok: true,
      teamId: parseTeamId(`${display.stdout}\n${display.stderr}`),
    }
  },

  async spctl(appPath: string) {
    const assess = await run('spctl', ['--assess', '--type', 'execute', '--verbose', appPath])
    return assess.code === 0
      ? { ok: true }
      : { ok: false, detail: assess.stderr.trim() || `spctl exited ${assess.code}` }
  },
}

export const nodeFs: InstallFs = {
  exists: async (path) =>
    access(path)
      .then(() => true)
      .catch(() => false),
  isWritable: async (path) =>
    access(path, constants.W_OK)
      .then(() => true)
      .catch(() => false),
  mkdirp: async (path) => {
    await mkdir(path, { recursive: true })
  },
  writeFile: async (path, data) => {
    await writeFile(path, data)
  },
  readFile: async (path) => readFile(path, 'utf-8'),
  rename: async (from, to) => {
    await rename(from, to)
  },
  remove: async (path) => {
    await rm(path, { recursive: true, force: true })
  },
  unzip: async (archive, into) => {
    // `ditto -xk` rather than `unzip`: it is what the release job packs with,
    // and it preserves the resource forks and extended attributes a signed
    // bundle needs to stay verifiable.
    const result = await run('ditto', ['-xk', archive, into])
    if (result.code !== 0) {
      throw new Error(result.stderr.trim() || `ditto exited ${result.code}`)
    }
  },
  mkdtemp: async (prefix) => mkdtemp(join(tmpdir(), prefix)),
}

/** The installer, wired to this machine. */
export function nodeInstallDeps(overrides: Partial<InstallDeps> = {}): InstallDeps {
  const platform =
    process.platform === 'darwin' ? 'darwin' : process.platform === 'win32' ? 'win32' : 'linux'

  return {
    platform,
    arch: process.arch,
    version: kyberdashVersion(),
    // `process.execPath` is node itself; argv[1] is the CLI the tray must find.
    kyberdashPath: process.argv[1] ?? process.execPath,
    configDir: getConfigDir(),
    homeDir: process.env['HOME'] ?? process.env['USERPROFILE'] ?? '',
    env: process.env,
    fetch: async (url: string) => {
      const response = await fetch(url)
      return {
        ok: response.ok,
        status: response.status,
        body: new Uint8Array(await response.arrayBuffer()),
      }
    },
    verify: nodeVerifier,
    fs: nodeFs,
    run,
    now: () => new Date(),
    log: (message: string) => process.stderr.write(`${message}\n`),
    ...overrides,
  }
}

/** The running CLI's version, which is the release the tray comes from (R12.5). */
const PACKAGE_VERSION = String(
  (createRequire(import.meta.url)('../../package.json') as { version?: string }).version ?? '0.0.0',
)

function kyberdashVersion(): string {
  // Read at call time, so a test can point the installer at another release
  // without the module having cached a version at import.
  return process.env['KYBERDASH_VERSION'] ?? PACKAGE_VERSION
}

export { EXPECTED_TEAM_ID }
