/**
 * Which release to install, and what it is called there.
 *
 * Requirement 12.5 pins the release to the running CLI's own version rather
 * than to "latest": a `kyberdash` that installs a tray from a different release
 * pairs a CLI with a tray built against another REST contract, which is exactly
 * what the tray's `apiVersion` gate exists to catch. 12.10 allows the origin to
 * be redirected, which is how the installer is tested against a local server.
 */

import type { InstallPlatform } from './types.js'

export const DEFAULT_RELEASE_HOST = 'https://github.com/dpalfery/kyber-weave/releases/download'

/** The checksum manifest every release publishes. */
export const CHECKSUM_FILE = 'SHA256SUMS.txt'

/**
 * The release origin for a version.
 *
 * `KYBER_WEAVE_RELEASE_ORIGIN` replaces the whole prefix, so a test server does
 * not have to imitate GitHub's path layout.
 */
export function releaseOrigin(version: string, env: Record<string, string | undefined>): string {
  const override = env['KYBER_WEAVE_RELEASE_ORIGIN']?.trim()
  if (override !== undefined && override !== '') {
    return override.replace(/\/+$/, '')
  }
  return `${DEFAULT_RELEASE_HOST}/v${version}`
}

/**
 * The artifact for a platform and architecture.
 *
 * macOS ships one zip per architecture because the bundle is signed per
 * architecture; Windows ships one per-user NSIS installer.
 */
export function trayArtifactName(platform: InstallPlatform, arch: string): string {
  if (platform === 'darwin') {
    const slice = arch === 'arm64' ? 'arm64' : 'x64'
    return `kyberdash-tray-darwin-${slice}.zip`
  }
  if (platform === 'win32') {
    return 'kyberdash-tray-win-x64-setup.exe'
  }
  throw new Error(`no tray artifact for platform ${platform}`)
}

export function artifactUrl(origin: string, artifact: string): string {
  return `${origin.replace(/\/+$/, '')}/${artifact}`
}

/**
 * Finds one file's SHA-256 in a `SHA256SUMS.txt`.
 *
 * The format is `<hex>  <name>`, the same one `shasum -c` reads. A name that is
 * absent returns null rather than throwing, so the caller can report "this
 * release has no such asset" rather than a parse error.
 */
export function checksumFor(manifest: string, artifact: string): string | null {
  for (const line of manifest.split('\n')) {
    const trimmed = line.trim()
    if (trimmed === '') continue
    const match = /^([0-9a-fA-F]{64})\s+\*?(.+)$/.exec(trimmed)
    if (match === null) continue
    const [, hex, name] = match
    // Manifests sometimes carry a path; compare the basename.
    const basename = name!.split(/[\\/]/).pop()
    if (basename === artifact) return hex!.toLowerCase()
  }
  return null
}
