import { createHash } from 'node:crypto'
import { basename, extname, join } from 'node:path'

import { beforeEach, describe, expect, it, vi } from 'vitest'

import { installTray, MACOS_APP_NAME, readTrayRecord, trayRecordPath } from './menubar.js'
import { checksumFor, releaseOrigin, trayArtifactName } from './origin.js'
import { EXPECTED_TEAM_ID, InstallError, type InstallDeps, type InstallFs } from './types.js'

const VERSION = '0.9.23'
const ORIGIN = 'http://127.0.0.1:9999/release'

function sha256(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex')
}

/** An in-memory filesystem; every path the installer touches is observable. */
function fakeFs() {
  const files = new Map<string, string>()
  const dirs = new Set<string>(['/Applications'])
  const writable = new Set<string>(['/Applications'])
  let tempCounter = 0

  const fs: InstallFs = {
    exists: async (path) => files.has(path) || dirs.has(path),
    isWritable: async (path) => writable.has(path),
    mkdirp: async (path) => {
      dirs.add(path)
    },
    writeFile: async (path, data) => {
      files.set(path, typeof data === 'string' ? data : `<${data.byteLength} bytes>`)
    },
    readFile: async (path) => {
      const found = files.get(path)
      if (found === undefined) throw new Error(`ENOENT: ${path}`)
      return found
    },
    rename: async (from, to) => {
      const payload = files.get(from) ?? (dirs.has(from) ? '<dir>' : undefined)
      if (payload === undefined) throw new Error(`ENOENT: ${from}`)
      files.delete(from)
      dirs.delete(from)
      files.set(to, payload)
    },
    remove: async (path) => {
      files.delete(path)
      dirs.delete(path)
    },
    trash: async (path, homeDir) => {
      // Mirrors nodeFs.trash: needs a home, creates the Trash, never overwrites.
      if (homeDir === '') throw new Error('no home directory to find the Trash in')
      dirs.add(`${homeDir}/.Trash`)
      const ext = extname(path)
      const stem = `${basename(path, ext)} 1`
      let to = `${homeDir}/.Trash/${stem}${ext}`
      for (let n = 2; files.has(to); n += 1) to = `${homeDir}/.Trash/${stem} ${n}${ext}`
      const payload = files.get(path) ?? '<dir>'
      files.delete(path)
      dirs.delete(path)
      files.set(to, payload)
      return to
    },
    unzip: async (_archive, into) => {
      // A real `ditto -xk` leaves the bundle beside the archive.
      files.set(join(into, MACOS_APP_NAME), '<app bundle>')
    },
    mkdtemp: async (prefix) => {
      const path = `/tmp/${prefix}${(tempCounter += 1)}`
      dirs.add(path)
      return path
    },
  }

  return { fs, files, dirs, writable }
}

type Harness = ReturnType<typeof harness>

function harness(overrides: Partial<InstallDeps> = {}) {
  const { fs, files, dirs, writable } = fakeFs()
  const payload = new TextEncoder().encode('tray-artifact-bytes')
  // Linux has no artifact; the harness still needs a name to script the fetch.
  const platform = overrides.platform ?? 'darwin'
  const artifact =
    platform === 'linux' ? 'unused' : trayArtifactName(platform, overrides.arch ?? 'arm64')
  const manifest = `${sha256(payload)}  ${artifact}\n`

  const logs: string[] = []
  const commands: Array<{ command: string; args: string[] }> = []
  let running = false

  const deps: InstallDeps = {
    platform: 'darwin',
    arch: 'arm64',
    version: VERSION,
    kyberdashPath: '/Users/dev/.local/bin/kyberdash',
    configDir: '/Users/dev/.kyberdash',
    homeDir: '/Users/dev',
    env: { KYBER_WEAVE_RELEASE_ORIGIN: ORIGIN },
    fetch: vi.fn(async (url: string) => {
      if (url.endsWith('SHA256SUMS.txt')) {
        return { ok: true, status: 200, body: new TextEncoder().encode(manifest) }
      }
      if (url.endsWith(artifact)) {
        return { ok: true, status: 200, body: payload }
      }
      return { ok: false, status: 404, body: new Uint8Array() }
    }),
    verify: {
      codesign: vi.fn(async () => ({ ok: true, teamId: EXPECTED_TEAM_ID })),
      spctl: vi.fn(async () => ({ ok: true })),
    },
    fs,
    run: vi.fn(async (command: string, args: string[]) => {
      commands.push({ command, args })
      // A tiny process table: `open` starts the tray, a quit or kill stops it.
      if (command === 'open') running = true
      if (command === 'pkill' || args.includes('--quit')) running = false
      if (command === 'pgrep') return { code: running ? 0 : 1, stdout: '', stderr: '' }
      return { code: 0, stdout: '', stderr: '' }
    }),
    now: () => new Date('2026-09-19T12:00:00.000Z'),
    sleep: async () => {},
    log: (message: string) => logs.push(message),
    ...overrides,
  }

  return { deps, files, dirs, writable, logs, commands, payload, artifact, manifest }
}

/** Where a default-writable /Applications install lands. */
function installedApp(): string {
  return join('/Applications', MACOS_APP_NAME)
}

describe('release origin (R12.5, R12.10)', () => {
  it('pins the release to the running version', () => {
    expect(releaseOrigin('1.2.3', {})).toBe(
      'https://github.com/dpalfery/kyber-weave/releases/download/v1.2.3',
    )
  })

  it('honours KYBER_WEAVE_RELEASE_ORIGIN, trailing slash and all', () => {
    expect(releaseOrigin('1.2.3', { KYBER_WEAVE_RELEASE_ORIGIN: 'http://127.0.0.1:9/r/' })).toBe(
      'http://127.0.0.1:9/r',
    )
    // An empty override is not an override.
    expect(releaseOrigin('1.2.3', { KYBER_WEAVE_RELEASE_ORIGIN: '  ' })).toContain('github.com')
  })

  it('names one artifact per platform and architecture', () => {
    expect(trayArtifactName('darwin', 'arm64')).toBe('kyberdash-tray-darwin-arm64.zip')
    expect(trayArtifactName('darwin', 'x64')).toBe('kyberdash-tray-darwin-x64.zip')
    expect(trayArtifactName('win32', 'x64')).toBe('kyberdash-tray-win-x64-setup.exe')
  })
})

describe('checksum manifest', () => {
  it('reads the shasum format, with or without a binary marker or a path', () => {
    const hex = 'a'.repeat(64)
    expect(checksumFor(`${hex}  kyberdash-tray-darwin-arm64.zip`, 'kyberdash-tray-darwin-arm64.zip')).toBe(hex)
    expect(checksumFor(`${hex} *dist/kyberdash-tray-darwin-arm64.zip`, 'kyberdash-tray-darwin-arm64.zip')).toBe(hex)
    expect(checksumFor(`${hex}  other.zip`, 'kyberdash-tray-darwin-arm64.zip')).toBeNull()
    expect(checksumFor('', 'anything.zip')).toBeNull()
  })
})

describe('kyberdash menubar on macOS', () => {
  let h: Harness

  beforeEach(() => {
    h = harness()
  })

  it('installs, records tray.json and launches', async () => {
    const record = await installTray(h.deps)

    expect(record?.path).toBe(installedApp())
    expect(record?.version).toBe(VERSION)
    expect(record?.platform).toBe('darwin')
    // R12.11 / R6.6: the tray resolves the CLI from this before PATH.
    expect(record?.kyberdashPath).toBe('/Users/dev/.local/bin/kyberdash')

    const written = JSON.parse(h.files.get(trayRecordPath('/Users/dev/.kyberdash'))!) as Record<
      string,
      unknown
    >
    expect(written.kyberdashPath).toBe('/Users/dev/.local/bin/kyberdash')
    expect(written.installedAt).toBe('2026-09-19T12:00:00.000Z')

    expect(h.commands).toContainEqual({ command: 'open', args: [installedApp()] })
  })

  /** R12.6: an artifact that does not match the manifest is never installed. */
  it('refuses an artifact whose checksum does not match', async () => {
    const tampered = harness({
      fetch: vi.fn(async (url: string) => {
        if (url.endsWith('SHA256SUMS.txt')) {
          return {
            ok: true,
            status: 200,
            body: new TextEncoder().encode(`${'b'.repeat(64)}  kyberdash-tray-darwin-arm64.zip\n`),
          }
        }
        return { ok: true, status: 200, body: new TextEncoder().encode('tray-artifact-bytes') }
      }),
    })

    await expect(installTray(tampered.deps)).rejects.toThrow(/checksum/)
    expect(tampered.files.has(installedApp())).toBe(false)
  })

  it('refuses an artifact the manifest does not list', async () => {
    const missing = harness({
      fetch: vi.fn(async () => ({
        ok: true,
        status: 200,
        body: new TextEncoder().encode('# nothing here\n'),
      })),
    })

    await expect(installTray(missing.deps)).rejects.toThrow(/not listed/)
  })

  it('refuses a bundle that fails codesign', async () => {
    const unsigned = harness({
      verify: {
        codesign: vi.fn(async () => ({ ok: false, detail: 'code object is not signed at all' })),
        spctl: vi.fn(async () => ({ ok: true })),
      },
    })

    await expect(installTray(unsigned.deps)).rejects.toThrow(/codesign/)
    expect(unsigned.files.has(installedApp())).toBe(false)
    expect(unsigned.deps.verify.spctl).not.toHaveBeenCalled()
  })

  /** R12.6 names the team, so another valid Developer ID is still refused. */
  it('refuses a bundle signed by another team', async () => {
    const wrongTeam = harness({
      verify: {
        codesign: vi.fn(async () => ({ ok: true, teamId: 'ABCDE12345' })),
        spctl: vi.fn(async () => ({ ok: true })),
      },
    })

    await expect(installTray(wrongTeam.deps)).rejects.toThrow(
      new RegExp(`team-id.*${EXPECTED_TEAM_ID}`),
    )
    expect(wrongTeam.files.has(installedApp())).toBe(false)
  })

  it('refuses a bundle Gatekeeper will not assess', async () => {
    const refused = harness({
      verify: {
        codesign: vi.fn(async () => ({ ok: true, teamId: EXPECTED_TEAM_ID })),
        spctl: vi.fn(async () => ({ ok: false, detail: 'rejected (source=no usable signature)' })),
      },
    })

    await expect(installTray(refused.deps)).rejects.toThrow(/spctl/)
    expect(refused.files.has(installedApp())).toBe(false)
  })

  /**
   * The signing work exists precisely to stop stripping quarantine, so an
   * installer that strips it has defeated its own purpose.
   */
  it('never strips the quarantine attribute', async () => {
    await installTray(h.deps)

    const ranXattr = h.commands.some(
      ({ command, args }) =>
        command.includes('xattr') || args.some((arg) => arg.includes('com.apple.quarantine')),
    )
    expect(ranXattr).toBe(false)
  })

  /** A failed install fails with the reason; the old version never comes back. */
  it('fails with the reason and does not restore the old app when the install fails', async () => {
    const existing = harness()
    existing.files.set(installedApp(), '<old app bundle>')
    const move = existing.deps.fs.rename
    existing.deps.fs.rename = vi.fn(async (from: string, to: string) => {
      if (from.startsWith('/tmp/')) throw new Error('disk full')
      await move(from, to)
    })

    await expect(installTray(existing.deps, { force: true })).rejects.toThrow(
      /install: disk full \(the previous app is in the Trash/,
    )
    expect(existing.files.has(installedApp())).toBe(false)
    expect(existing.logs.join('\n')).not.toContain('restored')
  })

  it('retries a failed launch and keeps the new app', async () => {
    let opens = 0
    const flaky = harness({
      run: vi.fn(async (command: string) => {
        if (command === 'open') {
          opens += 1
          return opens < 3
            ? { code: 1, stdout: '', stderr: 'LSOpenURLsWithRole failed' }
            : { code: 0, stdout: '', stderr: '' }
        }
        return { code: command === 'pgrep' ? (opens >= 3 ? 0 : 1) : 0, stdout: '', stderr: '' }
      }),
    })
    flaky.files.set(installedApp(), '<old app bundle>')

    await expect(installTray(flaky.deps, { force: true })).resolves.not.toBeNull()
    expect(opens).toBe(3)
    expect(flaky.files.get(installedApp())).toBe('<app bundle>')
  })

  it('never rolls back when the launch keeps failing, and says where the app is', async () => {
    const broken = harness({
      run: vi.fn(async (command: string) =>
        command === 'open'
          ? { code: 1, stdout: '', stderr: 'boom' }
          : { code: command === 'pgrep' ? 1 : 0, stdout: '', stderr: '' },
      ),
    })
    broken.files.set(installedApp(), '<old app bundle>')

    await expect(installTray(broken.deps, { force: true })).rejects.toThrow(
      /launch: exited 1: boom \(after 3 attempts/,
    )
    expect(broken.files.get(installedApp())).toBe('<app bundle>')
    expect(broken.files.has(trayRecordPath('/Users/dev/.kyberdash'))).toBe(true)
  })

  it('fails the launch when the opened tray is not running', async () => {
    const dead = harness({
      run: vi.fn(async (command: string) => ({ code: command === 'pgrep' ? 1 : 0, stdout: '', stderr: '' })),
    })
    dead.files.set(installedApp(), '<old app bundle>')

    await expect(installTray(dead.deps, { force: true })).rejects.toThrow(/not running/)
    expect(dead.files.get(installedApp())).toBe('<app bundle>')
  })

  it('quits first, moves the old app to the Trash, then installs the new one', async () => {
    const h2 = harness()
    h2.files.set(installedApp(), '<old app bundle>')

    await installTray(h2.deps, { force: true })

    expect(h2.files.get('/Users/dev/.Trash/KyberDash 1.app')).toBe('<old app bundle>')
    expect(h2.files.get(installedApp())).toBe('<app bundle>')
    const quitAt = h2.commands.findIndex(({ args }) => args.includes('--quit'))
    const openAt = h2.commands.findIndex(({ command }) => command === 'open')
    expect(quitAt).toBeGreaterThanOrEqual(0)
    expect(quitAt).toBeLessThan(openAt)
  })

  it('moves a stale copy in the other Applications folder to the Trash', async () => {
    const h2 = harness()
    const stale = join('/Users/dev/Applications', MACOS_APP_NAME)
    h2.files.set(installedApp(), '<old app bundle>')
    h2.files.set(stale, '<stale bundle>')

    await installTray(h2.deps, { force: true })

    expect(h2.files.has(stale)).toBe(false)
    expect(h2.logs.join('\n')).toContain('stale tray')
  })

  it('names the quit step when pgrep itself fails', async () => {
    const broken = harness({
      run: vi.fn(async (command: string) => {
        if (command === 'pgrep') throw new Error('spawn pgrep ENOENT')
        return { code: 0, stdout: '', stderr: '' }
      }),
    })
    await expect(installTray(broken.deps, { force: true })).rejects.toThrow(/quit: could not stop/)
  })

  it('names the record step when tray.json cannot be written', async () => {
    const h3 = harness()
    const write = h3.deps.fs.writeFile
    h3.deps.fs.writeFile = vi.fn(async (path: string, data: Uint8Array | string) => {
      if (path.endsWith('tray.json')) throw new Error('read-only')
      await write(path, data)
    })
    await expect(installTray(h3.deps, { force: true })).rejects.toThrow(
      /record: read-only \(the new tray is installed at/,
    )
  })

  it('keeps both copies when two same-named apps are trashed', async () => {
    const h4 = harness()
    h4.files.set(installedApp(), '<old app bundle>')
    h4.files.set(join('/Users/dev/Applications', MACOS_APP_NAME), '<stale bundle>')

    await installTray(h4.deps, { force: true })

    const trashed = [...h4.files.keys()].filter((path) => path.startsWith('/Users/dev/.Trash/'))
    expect(trashed).toHaveLength(2)
  })

  it('refuses to replace a tray that will not stop', async () => {
    const stuck = harness({
      run: vi.fn(async () => ({ code: 0, stdout: '', stderr: '' })),
    })
    stuck.files.set(installedApp(), '<old app bundle>')

    await expect(installTray(stuck.deps, { force: true })).rejects.toThrow(/quit:.*would not stop/)
    expect(stuck.files.get(installedApp())).toBe('<old app bundle>')
  })

  /** R15.6: the message says which step, so the user knows what to check. */
  it('names the failing step', async () => {
    const offline = harness({
      fetch: vi.fn(async () => ({ ok: false, status: 503, body: new Uint8Array() })),
    })

    const error = await installTray(offline.deps).catch((caught: unknown) => caught)
    expect(error).toBeInstanceOf(InstallError)
    expect((error as InstallError).step).toBe('checksums')
    expect((error as InstallError).message).toMatch(/503/)
  })

  it('falls back to ~/Applications when /Applications is not writable', async () => {
    const managed = harness()
    managed.writable.delete('/Applications')

    const record = await installTray(managed.deps)
    expect(record?.path).toBe(join('/Users/dev/Applications', MACOS_APP_NAME))
  })

  it('leaves an existing install alone without --force', async () => {
    h.files.set(
      trayRecordPath('/Users/dev/.kyberdash'),
      JSON.stringify({ path: installedApp(), version: '0.9.1' }),
    )

    const record = await installTray(h.deps)
    expect(record?.version).toBe('0.9.1')
    expect(h.deps.fetch).not.toHaveBeenCalled()
    expect(h.logs.join('\n')).toContain('already installed')
  })
})

describe('kyberdash menubar --update', () => {
  /** R15.3: nothing recorded means nothing to update, and exit 0. */
  it('does nothing when no install is recorded', async () => {
    const h = harness()

    const record = await installTray(h.deps, { update: true })

    expect(record).toBeNull()
    expect(h.deps.fetch).not.toHaveBeenCalled()
    expect(h.logs.join('\n')).toContain('nothing to update')
  })

  it('quits the running tray, replaces it, and relaunches', async () => {
    const h = harness()
    h.files.set(installedApp(), '<old app bundle>')
    h.files.set(
      trayRecordPath('/Users/dev/.kyberdash'),
      JSON.stringify({ path: installedApp(), version: '0.9.1', kyberdashPath: '/x' }),
    )

    await installTray(h.deps, { update: true })

    const quit = h.commands.find(({ args }) => args.includes('--quit'))
    expect(quit?.command).toContain('kyberdash-tray')
    // The quit must come before the relaunch.
    const quitAt = h.commands.findIndex(({ args }) => args.includes('--quit'))
    const openAt = h.commands.findIndex(({ command }) => command === 'open')
    expect(quitAt).toBeLessThan(openAt)

  })

  it('continues when the running tray will not confirm the quit', async () => {
    let opened = false
    const h = harness({
      run: vi.fn(async (command: string, args: string[]) => {
        if (args.includes('--quit')) throw new Error('no such process')
        if (command === 'pgrep') return { code: opened ? 0 : 1, stdout: '', stderr: '' }
        if (command === 'open') opened = true
        return { code: 0, stdout: '', stderr: '' }
      }),
    })
    h.files.set(installedApp(), '<old app bundle>')
    h.files.set(
      trayRecordPath('/Users/dev/.kyberdash'),
      JSON.stringify({ path: installedApp(), version: '0.9.1', kyberdashPath: '/x' }),
    )

    await expect(installTray(h.deps, { update: true })).resolves.not.toBeNull()
    expect(h.logs.join('\n')).toContain('did not confirm the quit')
  })

  it('installs the explicit version when requested', async () => {
    const h = harness()
    const customVersion = '0.1.7-rc.13'
    const record = await installTray(h.deps, { version: customVersion })

    expect(record?.version).toBe(customVersion)
    expect(h.deps.fetch).toHaveBeenCalledWith(
      expect.stringContaining(trayArtifactName('darwin', 'arm64')),
    )
  })

  it('updates with the explicit version when requested', async () => {
    const h = harness()
    h.files.set(installedApp(), '<old app bundle>')
    h.files.set(
      trayRecordPath('/Users/dev/.kyberdash'),
      JSON.stringify({ path: installedApp(), version: '0.9.1', kyberdashPath: '/x' }),
    )

    const customVersion = '0.1.7-rc.13'
    const record = await installTray(h.deps, { update: true, version: customVersion })

    expect(record?.version).toBe(customVersion)
  })
})

describe('kyberdash menubar on Windows', () => {
  it('runs the NSIS installer silently', async () => {
    const h = harness({
      platform: 'win32',
      arch: 'x64',
      homeDir: 'C:\\Users\\dev',
      env: { KYBER_WEAVE_RELEASE_ORIGIN: ORIGIN, LOCALAPPDATA: 'C:\\Users\\dev\\AppData\\Local' },
    })

    const record = await installTray(h.deps)

    const installer = h.commands.find(({ command }) => command.endsWith('.exe'))
    expect(installer?.args).toEqual(['/S'])
    expect(record?.platform).toBe('win32')
    expect(record?.kyberdashPath).toBe('/Users/dev/.local/bin/kyberdash')
  })

  it('fails naming the step when the installer exits non-zero', async () => {
    const h = harness({
      platform: 'win32',
      arch: 'x64',
      env: { KYBER_WEAVE_RELEASE_ORIGIN: ORIGIN },
      run: vi.fn(async () => ({ code: 2, stdout: '', stderr: 'user cancelled' })),
    })

    const error = await installTray(h.deps).catch((caught: unknown) => caught)
    expect((error as InstallError).step).toBe('install')
    expect((error as InstallError).message).toContain('user cancelled')
  })
})

describe('kyberdash menubar on Linux (R12.8)', () => {
  it('refuses with the platform message', async () => {
    const h = harness({ platform: 'linux' })

    const error = await installTray(h.deps).catch((caught: unknown) => caught)
    expect((error as InstallError).step).toBe('platform')
    expect((error as InstallError).message).toMatch(/macOS and Windows only/)
    expect(h.deps.fetch).not.toHaveBeenCalled()
  })
})

describe('readTrayRecord', () => {
  it('treats an absent or unreadable record as no install', async () => {
    const h = harness()
    expect(await readTrayRecord(h.deps)).toBeNull()

    h.files.set(trayRecordPath('/Users/dev/.kyberdash'), '{ not json')
    expect(await readTrayRecord(h.deps)).toBeNull()

    h.files.set(trayRecordPath('/Users/dev/.kyberdash'), '{"version":"1"}')
    expect(await readTrayRecord(h.deps)).toBeNull()
  })
})
