/**
 * What the tray installer needs from the world.
 *
 * Every effect is a port rather than a direct call, because the steps worth
 * testing are the ones that must not happen: a checksum mismatch that installs
 * anyway, a codesign failure that is logged and ignored, a half-replaced app
 * left behind by a failure. None of those can be exercised against the real
 * network, the real `/Applications`, or a real signed bundle.
 */

/** The Apple team this repository's macOS artifacts are signed with (R12.6). */
export const EXPECTED_TEAM_ID = 'J2UNNQ466J'

/** Where the tray install is recorded (R12.11). */
export const TRAY_RECORD_FILE = 'tray.json'

export type InstallPlatform = 'darwin' | 'win32' | 'linux'

export type FetchResult = {
  ok: boolean
  status: number
  body: Uint8Array
}

/** A `codesign` / `spctl` verdict. */
export type VerifyResult = {
  ok: boolean
  /** The team id `codesign` reported, when it reported one. */
  teamId?: string
  /** What to tell the user when `ok` is false. */
  detail?: string
}

export type Verifier = {
  /** `codesign --verify --deep --strict`, reporting the signing team. */
  codesign(appPath: string): Promise<VerifyResult>
  /** `spctl --assess --type execute`. */
  spctl(appPath: string): Promise<VerifyResult>
}

export type CommandResult = {
  code: number
  stdout: string
  stderr: string
}

/**
 * The filesystem operations the installer performs. Narrow on purpose: a port
 * that could do anything would not constrain the implementation.
 */
export type InstallFs = {
  exists(path: string): Promise<boolean>
  isWritable(path: string): Promise<boolean>
  mkdirp(path: string): Promise<void>
  writeFile(path: string, data: Uint8Array | string): Promise<void>
  readFile(path: string): Promise<string>
  rename(from: string, to: string): Promise<void>
  remove(path: string): Promise<void>
  /** Unpacks a zip; `ditto -xk` on macOS. */
  unzip(archive: string, into: string): Promise<void>
  mkdtemp(prefix: string): Promise<string>
}

export type InstallDeps = {
  platform: InstallPlatform
  arch: string
  /** The running CLI's version, which selects the release to install (R12.5). */
  version: string
  /** The running CLI's own path, recorded so a GUI can find it again (R6.6). */
  kyberdashPath: string
  /** `~/.kyberdash`. */
  configDir: string
  homeDir: string
  env: Record<string, string | undefined>
  fetch(url: string): Promise<FetchResult>
  verify: Verifier
  fs: InstallFs
  run(command: string, args: string[]): Promise<CommandResult>
  now(): Date
  log(message: string): void
}

/** What `tray.json` records (R12.11). */
export type TrayRecord = {
  path: string
  version: string
  installedAt: string
  platform: InstallPlatform
  /** Second in the tray's resolution order, ahead of PATH (R6.6). */
  kyberdashPath: string
}

export type InstallOptions = {
  force?: boolean
  update?: boolean
  version?: string
}

/**
 * A failure that names the step it failed at.
 *
 * Requirement 15.6 wants the step named, because "install failed" does not tell
 * the user whether to check their network, their signing chain or their disk.
 */
export class InstallError extends Error {
  constructor(
    readonly step: string,
    message: string,
  ) {
    super(`${step}: ${message}`)
    this.name = 'InstallError'
  }
}
