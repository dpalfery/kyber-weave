import { mkdir, mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import {
  EXPECTED_BUNDLE_ID,
  installMacMenubarApp,
  installMenubarApp,
  type MacInstallHooks,
} from '../src/menubar-installer.js'

// `installMacMenubarApp` is normally gated on `process.platform === 'darwin'` and a real
// `sw_vers` read. CI runs on Linux, where neither call would survive; the installer
// honours an environment opt-out so the same suite can exercise the refusal path on a
// Linux runner. The platform check inside the function reads this env var verbatim.
const FORCE_MAC_INSTALL_ENV = 'CODEBURN_FORCE_MAC_INSTALL'

function asset(name: string) {
  return { name, browser_download_url: `https://example.test/${name}` }
}

const VERSION = '0.9.19'
const ZIP_NAME = `CodeBurnMenubar-v${VERSION}.zip`
const ZIP_URL = `https://github.com/getagentseal/codeburn/releases/download/mac-v${VERSION}/${ZIP_NAME}`
const CHECKSUM_URL = `${ZIP_URL}.sha256`
const ZIP_BYTES = 'zip-bytes'

/**
 * Builds a fake `.app` directory with `CFBundleIdentifier` set to the production value.
 * Tests use `unpack` to copy this into the staging directory in lieu of running `/usr/bin/ditto`,
 * so the bundle id is read by the stub `readBundleIdentifier` hook rather than by PlistBuddy.
 */
async function buildFakeApp(appPath: string): Promise<void> {
  await mkdir(join(appPath, 'Contents'), { recursive: true })
  await writeFile(
    join(appPath, 'Contents', 'Info.plist'),
    `<?xml version="1.0" encoding="UTF-8"?>
<plist version="1.0">
<dict><key>CFBundleIdentifier</key><string>${EXPECTED_BUNDLE_ID}</string></dict>
</plist>`,
  )
}

/** Stand-in for the asset-download response surface; matches the existing test helper. */
function httpResponse(status: number, body?: string, headers: Record<string, string> = {}) {
  const ok = status >= 200 && status < 300
  return {
    ok,
    status,
    headers: { get: (name: string) => headers[name.toLowerCase()] ?? null },
    body: body === undefined ? null : new Response(body).body,
    text: async () => body ?? '',
  }
}

async function sha256(text: string): Promise<string> {
  const { createHash } = await import('node:crypto')
  return createHash('sha256').update(Buffer.from(text)).digest('hex')
}

async function fileExists(path: string): Promise<boolean> {
  try { await stat(path); return true } catch { return false }
}

/** SHA256 of `bytes`, used for both the digest returned in `.sha256` and the expected value. */
async function digestFor(bytes: string): Promise<string> {
  return sha256(bytes)
}

describe('installMacMenubarApp - R13.4 verification gate', () => {
  let sandbox: string
  let homeSbx: string
  let stagingSbx: string
  let fakeAppSource: string
  let installedAppPath: string
  let logs: string[]
  let originalForceMac: string | undefined

  beforeEach(async () => {
    sandbox = await mkdtemp(join(tmpdir(), 'menubar-mac-'))
    homeSbx = await mkdtemp(join(tmpdir(), 'menubar-mac-home-'))
    stagingSbx = await mkdtemp(join(tmpdir(), 'menubar-mac-stage-'))
    fakeAppSource = join(sandbox, 'src', 'CodeBurnMenubar.app')
    installedAppPath = join(homeSbx, 'Applications', 'CodeBurnMenubar.app')
    logs = []
    await buildFakeApp(fakeAppSource)
    originalForceMac = process.env[FORCE_MAC_INSTALL_ENV]
    process.env[FORCE_MAC_INSTALL_ENV] = '1'
  })

  afterEach(async () => {
    await rm(sandbox, { recursive: true, force: true })
    await rm(homeSbx, { recursive: true, force: true })
    await rm(stagingSbx, { recursive: true, force: true })
    if (originalForceMac === undefined) delete process.env[FORCE_MAC_INSTALL_ENV]
    else process.env[FORCE_MAC_INSTALL_ENV] = originalForceMac
  })

  /**
   * Returns a hooks bundle every test starts from. Tests layer overrides on top.
   * The `unpack` hook paints the pre-built fake app into the staging directory the way
   * `/usr/bin/ditto -x -k` would in production - so the seam stays faithful to the
   * call site even though `ditto` is mac-only.
   */
  function defaultHooks(overrides: Partial<MacInstallHooks> = {}, goodChecksum = true): MacInstallHooks {
    const fetchImpl = async (url: string) => {
      const expectedDigest = await digestFor(goodChecksum ? ZIP_BYTES : `${ZIP_BYTES}-tampered`)
      if (url.endsWith('.sha256')) {
        return httpResponse(200, `${expectedDigest}  ${ZIP_NAME}`)
      }
      return httpResponse(200, ZIP_BYTES)
    }
    return {
      homedir: () => homeSbx,
      stagingDir: stagingSbx,
      log: (message: string) => { logs.push(message) },
      resolveCliPath: async () => '/usr/local/bin/kyber-weave',
      unpack: async (_archivePath: string, destDir: string) => {
        await mkdir(destDir, { recursive: true })
        const { execFileSync } = await import('node:child_process')
        execFileSync('cp', ['-R', fakeAppSource, join(destDir, 'CodeBurnMenubar.app')])
      },
      readBundleIdentifier: async () => EXPECTED_BUNDLE_ID,
      // Tests should override verifySignature to control pass/fail; the default passes so a
      // happy-path test can use the bundle verbatim.
      verifySignature: async () => {},
      clearQuarantine: async () => {},
      launch: async () => {},
      isAppRunning: async () => false,
      killRunningApp: async () => {},
      fetchOptions: { fetchImpl },
      ...overrides,
    }
  }

  // --------- happy path: signature passes, bundle lands in ~/Applications

  it('places the app at ~/Applications when SHA256 and signature verification both pass', async () => {
    const result = await installMacMenubarApp({
      cliVersion: VERSION,
      mac: defaultHooks(),
    })

    expect(result.installedPath).toBe(installedAppPath)
    expect(result.launched).toBe(true)
    expect(await fileExists(installedAppPath)).toBe(true)
    const infoPlist = await readFile(join(installedAppPath, 'Contents', 'Info.plist'), 'utf8')
    expect(infoPlist).toContain(EXPECTED_BUNDLE_ID)
    expect(logs).toContain(`Downloading ${ZIP_NAME}...`)
    expect(logs).toContain('Verifying checksum...')
    expect(logs).toContain('Verifying app bundle...')
    expect(logs).toContain('Launching CodeBurn Menubar...')
  })

  // --------- R13.4: refused on bad SHA256

  it('refuses to install when SHA256 verification fails and leaves no file in place', async () => {
    const hooks = defaultHooks({}, false)
    await expect(installMacMenubarApp({
      cliVersion: VERSION,
      mac: hooks,
    })).rejects.toThrow(/Checksum mismatch/)

    // R13.4: a failed verification MUST NOT place the bundle. ~/Applications must remain
    // untouched.
    expect(await fileExists(installedAppPath)).toBe(false)
    // The verify-checksum step is observable in the logs; the verify-app step must not have
    // run because the failure happened upstream of it.
    expect(logs).toContain('Verifying checksum...')
    expect(logs).not.toContain('Verifying app bundle...')
    expect(logs).not.toContain('Launching CodeBurn Menubar...')
  })

  // --------- R13.4: refused on bad code signature

  it('refuses to install when the code signature is invalid and leaves no file in place', async () => {
    const signatureError = new Error(
      'CodeBurnMenubar.app: invalid signature (code or signature have been modified)',
    )
    const hooks = defaultHooks({
      verifySignature: async () => { throw signatureError },
    })
    await expect(installMacMenubarApp({
      cliVersion: VERSION,
      mac: hooks,
    })).rejects.toThrow(/invalid signature/)

    // The bundle must NOT have been placed - this is the core of R13.4. In the real flow,
    // `--verify --deep --strict` runs only after a successful SHA256 match, so this case
    // simulates a tampered or self-signed bundle arriving with a matching digest.
    expect(await fileExists(installedAppPath)).toBe(false)
    // Verify-app logged before codesign threw; verify-checksum passed (digest matches) so
    // the bad signature is the only credible reason for the throw.
    expect(logs).toContain('Verifying app bundle...')
    expect(logs).not.toContain('Launching CodeBurn Menubar...')
  })

  // --------- R13.4: refused on unexpected bundle id (identity check happens before signing)

  it('refuses to install when the bundle id does not match the expected value', async () => {
    const hooks = defaultHooks({
      readBundleIdentifier: async () => 'com.evil.codeburn-menubar',
    })
    await expect(installMacMenubarApp({
      cliVersion: VERSION,
      mac: hooks,
    })).rejects.toThrow(/Unexpected menubar bundle id/)

    expect(await fileExists(installedAppPath)).toBe(false)
    expect(logs).not.toContain('Launching CodeBurn Menubar...')
  })

  // --------- dispatcher: installMenubarApp picks the mac path on darwin

  it('installMenubarApp routes macOS install through installMacMenubarApp', async () => {
    const result = await installMenubarApp({
      platform: 'darwin',
      cliVersion: VERSION,
      mac: defaultHooks(),
    })

    expect(result.installedPath).toBe(installedAppPath)
    expect(await fileExists(installedAppPath)).toBe(true)
  })
})

/**
 * Cleanup contract: when the caller does NOT supply a stagingDir hook, the install path is
 * responsible for removing the temporary staging directory regardless of whether the install
 * succeeded or was refused. R13.4 doesn't constrain this, but a left-behind scratch dir on
 * every failed install is a separate failure mode that the verifier must not regress.
 */
describe('installMacMenubarApp - staging tmp cleanup', () => {
  let sandbox: string
  let homeSbx: string
  let fakeAppSource: string
  let originalForceMac: string | undefined

  beforeEach(async () => {
    sandbox = await mkdtemp(join(tmpdir(), 'menubar-mac-cleanup-'))
    homeSbx = await mkdtemp(join(tmpdir(), 'menubar-mac-home-'))
    fakeAppSource = join(sandbox, 'src', 'CodeBurnMenubar.app')
    await buildFakeApp(fakeAppSource)
    originalForceMac = process.env[FORCE_MAC_INSTALL_ENV]
    process.env[FORCE_MAC_INSTALL_ENV] = '1'
  })

  afterEach(async () => {
    await rm(sandbox, { recursive: true, force: true })
    await rm(homeSbx, { recursive: true, force: true })
    if (originalForceMac === undefined) delete process.env[FORCE_MAC_INSTALL_ENV]
    else process.env[FORCE_MAC_INSTALL_ENV] = originalForceMac
  })

  it('removes the staging tmp dir after the install was refused on a bad checksum', async () => {
    const { readdir } = await import('node:fs/promises')
    await expect(installMacMenubarApp({
      cliVersion: VERSION,
      mac: {
        homedir: () => homeSbx,
        log: () => {},
        resolveCliPath: async () => '/usr/local/bin/kyber-weave',
        unpack: async (_archivePath: string, destDir: string) => {
          await mkdir(destDir, { recursive: true })
          const { execFileSync } = await import('node:child_process')
          execFileSync('cp', ['-R', fakeAppSource, join(destDir, 'CodeBurnMenubar.app')])
        },
        readBundleIdentifier: async () => EXPECTED_BUNDLE_ID,
        verifySignature: async () => {},
        clearQuarantine: async () => {},
        launch: async () => {},
        isAppRunning: async () => false,
        killRunningApp: async () => {},
        fetchOptions: {
          fetchImpl: async (url: string) => {
            const badDigest = await digestFor(`${ZIP_BYTES}-tampered`)
            if (url.endsWith('.sha256')) {
              return httpResponse(200, `${badDigest}  ${ZIP_NAME}`)
            }
            return httpResponse(200, ZIP_BYTES)
          },
        },
      },
    })).rejects.toThrow(/Checksum mismatch/)

    // tmpdir() is shared, so we cannot assert "no codeburn-menubar-* dirs remain" globally,
    // but we can assert the install did not leak a folder next to the app or staging sbx.
    const tmpEntries = await readdir(tmpdir())
    expect(tmpEntries.some(e => e.startsWith('codeburn-menubar-'))).toBe(false)
  })
})

/**
 * Source contract for the verification gate: the production `defaultVerifySignature` must
 * shell out to `codesign --verify --deep --strict` exactly as documented, otherwise the
 * wirings around it (R13.4 wording, log line, hook ordering) silently drift. If a future
 * refactor renames the binary or drops `--strict`, this test must fail first.
 */
describe('installMacMenubarApp signature gate source contract', () => {
  it('matches the documented production command at the call-site', async () => {
    const { readFile } = await import('node:fs/promises')
    const source = await readFile(
      new URL('../src/menubar-installer.ts', import.meta.url).pathname,
      'utf8',
    )
    expect(source).toContain("'/usr/bin/codesign'")
    expect(source).toContain("'--verify'")
    expect(source).toContain("'--deep'")
    expect(source).toContain("'--strict'")
  })
})

/** Touch a couple of imports so tree-shaking keeps the file in the public test surface. */
void asset
void CHECKSUM_URL
void ZIP_URL
