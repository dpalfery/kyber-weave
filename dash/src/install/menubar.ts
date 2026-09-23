/**
 * `kyberdash menubar` — installs, updates and launches the tray.
 *
 * The shape of this module is set by what must not happen. A download that
 * fails its checksum must not be installed (12.6). A macOS bundle that fails
 * `codesign`, carries the wrong team, or is refused by `spctl` must not be
 * installed, and its quarantine attribute must not be stripped to make it run
 * (12.6) — stripping quarantine is precisely the workaround the signing work
 * exists to remove. And any failure after the existing app has been moved aside
 * must put it back (15.5), because a user who runs an update and is left with
 * no tray at all is worse off than one whose update simply failed.
 */

import { createHash } from 'node:crypto'
import { join } from 'node:path'

import { artifactUrl, CHECKSUM_FILE, checksumFor, releaseOrigin, trayArtifactName } from './origin.js'
import {
  EXPECTED_TEAM_ID,
  InstallError,
  TRAY_RECORD_FILE,
  type InstallDeps,
  type InstallOptions,
  type TrayRecord,
} from './types.js'

/** How long a `--quit` is given before the replace goes ahead anyway (D7). */
export const QUIT_TIMEOUT_MS = 10_000

export const MACOS_APP_NAME = 'KyberDash.app'

export function trayRecordPath(configDir: string): string {
  return join(configDir, TRAY_RECORD_FILE)
}

export async function readTrayRecord(deps: InstallDeps): Promise<TrayRecord | null> {
  try {
    const text = await deps.fs.readFile(trayRecordPath(deps.configDir))
    const parsed = JSON.parse(text) as Partial<TrayRecord>
    if (typeof parsed.path !== 'string' || parsed.path === '') return null
    return parsed as TrayRecord
  } catch {
    // Absent or unreadable both mean "no install we know of", which is what
    // `--update` treats as nothing to do.
    return null
  }
}

function sha256(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex')
}

/**
 * Installs, or updates, the tray.
 *
 * Returns the recorded install. Throws [`InstallError`] naming the step it
 * failed at; the caller turns that into an exit code and a message.
 */
export async function installTray(
  deps: InstallDeps,
  options: InstallOptions = {},
): Promise<TrayRecord | null> {
  if (deps.platform === 'linux') {
    // Requirement 12.8. Named as deferred work rather than as a defect, because
    // a Linux tray is a decision this specification did not take.
    throw new InstallError(
      'platform',
      'the KyberDash tray ships for macOS and Windows only; see docs/todo for the Linux tray',
    )
  }

  const existing = await readTrayRecord(deps)

  if (options.update === true && existing === null) {
    // Requirement 15.3: an update with nothing installed is not a failure, and
    // must not install a tray the user never asked for.
    deps.log('kyberdash: no tray install recorded; nothing to update')
    return null
  }

  if (existing !== null && options.force !== true && options.update !== true) {
    deps.log(`kyberdash: the tray is already installed at ${existing.path}`)
    return existing
  }

  const origin = releaseOrigin(deps.version, deps.env)
  const artifact = trayArtifactName(deps.platform, deps.arch)

  const payload = await download(deps, origin, artifact)
  const staging = await deps.fs.mkdtemp('kyberdash-tray-')

  if (deps.platform === 'darwin') {
    return installMacos(deps, { payload, staging, artifact, existing })
  }
  return installWindows(deps, { payload, staging, artifact })
}

/**
 * Fetches, through the port, turning any transport failure into a named step.
 *
 * A `fetch` that rejects — DNS, a refused connection, TLS — would otherwise
 * escape as a bare "fetch failed", which is exactly the message Requirement
 * 15.6 exists to prevent.
 */
async function fetchStep(
  deps: InstallDeps,
  step: string,
  url: string,
): Promise<Uint8Array> {
  let response
  try {
    response = await deps.fetch(url)
  } catch (error) {
    throw new InstallError(step, `could not reach ${url}: ${describe(error)}`)
  }
  if (!response.ok) {
    throw new InstallError(step, `could not read ${url} (HTTP ${response.status})`)
  }
  return response.body
}

/** Fetches the artifact and refuses it unless the manifest's hash matches. */
async function download(
  deps: InstallDeps,
  origin: string,
  artifact: string,
): Promise<Uint8Array> {
  const manifestUrl = artifactUrl(origin, CHECKSUM_FILE)
  const manifest = await fetchStep(deps, 'checksums', manifestUrl)

  const expected = checksumFor(new TextDecoder().decode(manifest), artifact)
  if (expected === null) {
    throw new InstallError('checksums', `${artifact} is not listed in ${CHECKSUM_FILE}`)
  }

  const url = artifactUrl(origin, artifact)
  const response = { body: await fetchStep(deps, 'download', url) }

  const actual = sha256(response.body)
  if (actual !== expected) {
    // Never install an artifact that does not match. The manifest is the only
    // thing standing between a mirror and an arbitrary binary.
    throw new InstallError(
      'checksum',
      `${artifact} does not match ${CHECKSUM_FILE} (expected ${expected}, got ${actual})`,
    )
  }
  return response.body
}

type MacosInstall = {
  payload: Uint8Array
  staging: string
  artifact: string
  existing: TrayRecord | null
}

async function installMacos(deps: InstallDeps, plan: MacosInstall): Promise<TrayRecord> {
  const archive = join(plan.staging, plan.artifact)
  await deps.fs.writeFile(archive, plan.payload)
  await deps.fs.unzip(archive, plan.staging)

  const staged = join(plan.staging, MACOS_APP_NAME)

  // Verification happens on the staged bundle, before anything is moved into
  // place: a bundle that fails here never reaches /Applications.
  const signature = await deps.verify.codesign(staged)
  if (!signature.ok) {
    throw new InstallError('codesign', signature.detail ?? 'the bundle is not validly signed')
  }
  if (signature.teamId !== EXPECTED_TEAM_ID) {
    throw new InstallError(
      'team-id',
      `expected ${EXPECTED_TEAM_ID}, got ${signature.teamId ?? 'none'}`,
    )
  }
  const assessment = await deps.verify.spctl(staged)
  if (!assessment.ok) {
    throw new InstallError('spctl', assessment.detail ?? 'Gatekeeper refused the bundle')
  }

  const target = join(await applicationsDir(deps), MACOS_APP_NAME)
  const backup = `${target}.backup`
  let backedUp = false

  if (plan.existing !== null || (await deps.fs.exists(target))) {
    await quitRunningTray(deps, target)
  }

  try {
    if (await deps.fs.exists(target)) {
      await deps.fs.rename(target, backup)
      backedUp = true
    }
    await deps.fs.rename(staged, target)
  } catch (error) {
    await restore(deps, backedUp, backup, target)
    throw new InstallError('install', describe(error))
  }

  const record = await recordInstall(deps, target)

  try {
    // The relaunch is part of the install: a tray that is installed but not
    // running has not replaced the one that was quit.
    await launch(deps, target)
  } catch (error) {
    await restore(deps, backedUp, backup, target)
    throw new InstallError('launch', describe(error))
  }

  // Only now is the old copy not needed (D7).
  if (backedUp) await deps.fs.remove(backup)
  return record
}

async function installWindows(
  deps: InstallDeps,
  plan: { payload: Uint8Array; staging: string; artifact: string },
): Promise<TrayRecord> {
  const installer = join(plan.staging, plan.artifact)
  await deps.fs.writeFile(installer, plan.payload)

  // `/S` is NSIS's silent switch; the installer is per-user, so it needs no
  // elevation and shows no dialog during an update.
  const result = await deps.run(installer, ['/S'])
  if (result.code !== 0) {
    throw new InstallError(
      'install',
      `the installer exited ${result.code}${result.stderr === '' ? '' : `: ${result.stderr.trim()}`}`,
    )
  }

  const target = join(
    deps.env['LOCALAPPDATA'] ?? join(deps.homeDir, 'AppData', 'Local'),
    'KyberDash',
    'kyberdash-tray.exe',
  )
  const record = await recordInstall(deps, target)
  await launch(deps, target)
  return record
}

/**
 * `/Applications` when it is writable, `~/Applications` otherwise.
 *
 * A managed Mac often has a read-only `/Applications`, and failing there would
 * leave the user with no way to install a tray they are entitled to run.
 */
async function applicationsDir(deps: InstallDeps): Promise<string> {
  if (await deps.fs.isWritable('/Applications')) return '/Applications'
  const fallback = join(deps.homeDir, 'Applications')
  await deps.fs.mkdirp(fallback)
  return fallback
}

/** Asks a running tray to quit, and waits — but not forever (D7). */
async function quitRunningTray(deps: InstallDeps, target: string): Promise<void> {
  const binary =
    deps.platform === 'darwin' ? join(target, 'Contents', 'MacOS', 'kyberdash-tray') : target
  try {
    await withTimeout(deps.run(binary, ['--quit']), QUIT_TIMEOUT_MS)
  } catch {
    // A tray that will not quit, or was not running, must not stop the update;
    // the rename replaces it either way.
    deps.log('kyberdash: the running tray did not confirm the quit; continuing')
  }
}

async function withTimeout<T>(work: Promise<T>, ms: number): Promise<T> {
  let timer: NodeJS.Timeout | undefined
  try {
    return await Promise.race([
      work,
      new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error(`timed out after ${ms}ms`)), ms)
      }),
    ])
  } finally {
    if (timer !== undefined) clearTimeout(timer)
  }
}

async function restore(
  deps: InstallDeps,
  backedUp: boolean,
  backup: string,
  target: string,
): Promise<void> {
  if (!backedUp) return
  try {
    if (await deps.fs.exists(target)) await deps.fs.remove(target)
    await deps.fs.rename(backup, target)
    deps.log('kyberdash: restored the previous tray after a failed install')
  } catch (error) {
    // Reported rather than swallowed: the user now has a backup on disk and
    // needs to know where it is.
    deps.log(`kyberdash: could not restore ${backup}: ${describe(error)}`)
  }
}

async function recordInstall(deps: InstallDeps, target: string): Promise<TrayRecord> {
  const record: TrayRecord = {
    path: target,
    version: deps.version,
    installedAt: deps.now().toISOString(),
    platform: deps.platform,
    // R12.11 / R6.6: the tray resolves the CLI from here before falling back to
    // PATH, because a GUI launched at login does not inherit the shell's.
    kyberdashPath: deps.kyberdashPath,
  }
  await deps.fs.mkdirp(deps.configDir)
  await deps.fs.writeFile(trayRecordPath(deps.configDir), `${JSON.stringify(record, null, 2)}\n`)
  return record
}

async function launch(deps: InstallDeps, target: string): Promise<void> {
  const result =
    deps.platform === 'darwin'
      ? await deps.run('open', [target])
      : await deps.run(target, [])
  if (result.code !== 0) {
    throw new Error(`exited ${result.code}`)
  }
}

function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}
