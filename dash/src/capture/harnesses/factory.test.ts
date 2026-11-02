// RED tests for the Factory (Droid) capture registry row.
//
// Factory (Droid) is captured by a plugin, not by configuration keys: KyberDash owns a
// whole plugin DIRECTORY in the Factory root (`<factory root>/plugins/kyberdash-factory`)
// and declares no key at all. `enable` installs the plugin and `disable` removes it, and
// neither reads, writes nor receipts the owner's `settings.json`.
//
// The row is reached through the capture registry — the seam the CLI, the API route and the
// Settings drawer all use — so this file states what the row must DO, not how the module
// that implements it is shaped. The tree it installs is the one the installer contract pins
// (`./factory-plugin/install.test.ts`).
//
// Every path is under a temporary home. The Factory root is that home's `.factory`, and
// `$FACTORY_DIR` points at the same directory, so the test never depends on WHICH of the
// two ways the writer resolves the root, and the developer's own `~/.factory` and
// `~/.kyberdash` are never read or written.

import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, describe, expect, it, vi } from 'vitest'

import { isPluginOnly, runCapture, type PluginOnlyWriter } from '../index.js'
import type { PathEnv } from '../paths.js'
import { harnessWriterFor, type CaptureHarnessWriter } from './registry.js'

/** The plugin directory Kyber owns inside the Factory root. */
const PLUGIN_DIR_NAME = 'kyberdash-factory'
/** The files Kyber owns in that directory, relative to it: the tree shape. */
const OWNED_FILES = ['.factory-plugin/plugin.json', 'hooks/hooks.json', 'hooks/kyberdash-factory.mjs']

const HOST = process.platform
const pathEnv = (): PathEnv => ({ platform: HOST })

/** The receiver is never reached: the health probe is injected and always refuses. */
const NO_LISTENER = async (): Promise<{ status: number; body: string }> => {
  throw new Error('connect ECONNREFUSED 127.0.0.1:4318 (synthetic)')
}

type Fixture = {
  /** The injected home: KyberDash's own state lives in `<home>/.kyberdash`. */
  home: string
  /** The Factory root: the home's `.factory`, which `$FACTORY_DIR` also points at. */
  root: string
  /** The plugin directory Kyber owns inside the Factory root. */
  pluginDir: string
  /** The owner's Factory settings. KyberDash never writes it. */
  settingsFile: string
}

const tempDirs: string[] = []
afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true })
  vi.unstubAllEnvs()
})

function tempDir(prefix: string): string {
  const dir = mkdtempSync(join(tmpdir(), prefix))
  tempDirs.push(dir)
  return dir
}

function fixture(prefix: string): Fixture {
  const home = tempDir(prefix)
  const root = join(home, '.factory')
  mkdirSync(root, { recursive: true })
  // `$FACTORY_DIR` points at the home's `.factory`, so the two ways a writer may resolve
  // the Factory root name the same directory.
  vi.stubEnv('FACTORY_DIR', root)
  return {
    home,
    root,
    pluginDir: join(root, 'plugins', PLUGIN_DIR_NAME),
    settingsFile: join(root, 'settings.json'),
  }
}

/** Every file under a directory, as sorted relative posix-style paths. */
function listFiles(root: string): string[] {
  if (!existsSync(root)) return []
  const found: string[] = []
  const walk = (dir: string, prefix: string): void => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const relative = prefix === '' ? entry.name : `${prefix}/${entry.name}`
      if (entry.isDirectory()) walk(join(dir, entry.name), relative)
      else found.push(relative)
    }
  }
  walk(root, '')
  return found.sort()
}

/** The writer the registry holds for Factory. */
function factoryWriter(): CaptureHarnessWriter {
  const writer = harnessWriterFor('factory')
  if (writer === undefined) throw new Error('no capture writer is registered for factory')
  return writer
}

/** The Factory writer, which must be a managed writer that owns a plugin and no key. */
function factoryPluginWriter(): PluginOnlyWriter {
  const writer = factoryWriter()
  if (!isPluginOnly(writer)) throw new Error('the factory writer is not a plugin-only managed writer')
  return writer
}

/** The consent file's text, or '' when there is none. */
function consentText(f: Fixture): string {
  const path = join(f.home, '.kyberdash', 'factory-plugin.json')
  return existsSync(path) ? readFileSync(path, 'utf8') : ''
}

describe('the Factory (Droid) capture writer', () => {
  it('is registered as a managed writer named Factory (Droid)', () => {
    const writer = factoryWriter()
    expect(writer.id).toBe('factory')
    expect(writer.kind).toBe('managed')
    expect(writer.displayName).toBe('Factory (Droid)')
  })

  it('declares no configuration key: the plugin is the whole surface', () => {
    const writer = factoryPluginWriter()
    expect(writer.desiredKeys('http://127.0.0.1:4318')).toEqual({})
    expect(writer.configFiles ?? []).toEqual([])
    expect(writer.ensureIfAbsentKeys?.('http://127.0.0.1:4318') ?? {}).toEqual({})
  })

  it('resolves the Factory settings file under the home', () => {
    const f = fixture('factory-writer-home-')
    expect(factoryWriter().resolvePath(f.home, pathEnv())).toBe(f.settingsFile)
  })

  it('resolves the Factory settings file under $FACTORY_DIR, not under the home', () => {
    const f = fixture('factory-writer-env-')
    const elsewhere = tempDir('factory-root-')
    vi.stubEnv('FACTORY_DIR', elsewhere)
    expect(factoryWriter().resolvePath(f.home, pathEnv())).toBe(join(elsewhere, 'settings.json'))
  })

  it('owns a plugin directory inside the Factory root', () => {
    const f = fixture('factory-writer-plugin-')
    expect(factoryPluginWriter().filePlugin.path(f.home, pathEnv())).toBe(f.pluginDir)
  })

  it('installs the plugin tree and leaves the owner settings file byte-identical', () => {
    const f = fixture('factory-writer-install-')
    const owner = '{\n  "theme": "dark"\n}\n'
    writeFileSync(f.settingsFile, owner)

    const result = factoryPluginWriter().filePlugin.install(f.home, pathEnv(), { dryRun: false })

    expect(result.exitCode).toBe(0)
    expect(listFiles(f.pluginDir)).toEqual([...OWNED_FILES].sort())
    expect(readFileSync(f.settingsFile, 'utf8')).toBe(owner)
  })

  it('removes the plugin tree again and leaves the owner settings file byte-identical', () => {
    const f = fixture('factory-writer-uninstall-')
    const owner = '{\n  "theme": "dark"\n}\n'
    writeFileSync(f.settingsFile, owner)
    const writer = factoryPluginWriter()
    writer.filePlugin.install(f.home, pathEnv(), { dryRun: false })

    const removed = writer.filePlugin.uninstall(f.home, pathEnv(), { dryRun: false })

    expect(removed.exitCode).toBe(0)
    expect(existsSync(f.pluginDir)).toBe(false)
    expect(readFileSync(f.settingsFile, 'utf8')).toBe(owner)
  })

  it('enables and disables through the engine without receipting a configuration key', async () => {
    const f = fixture('factory-writer-engine-')
    const owner = '{\n  "theme": "dark"\n}\n'
    writeFileSync(f.settingsFile, owner)

    const enabled = await runCapture('enable', {
      homeDir: f.home,
      platform: HOST,
      harnessIds: ['factory'],
      fetchHealth: NO_LISTENER,
    })

    expect(enabled.exitCode).toBe(0)
    expect(existsSync(f.pluginDir)).toBe(true)
    expect(readFileSync(f.settingsFile, 'utf8')).toBe(owner)
    // The plugin keeps its own receipt, so the key receipt holds no Factory entry.
    const keyReceipt = join(f.home, '.kyberdash', 'capture-receipt.json')
    expect(existsSync(keyReceipt) ? readFileSync(keyReceipt, 'utf8') : '').not.toContain('factory')

    const disabled = await runCapture('disable', {
      homeDir: f.home,
      platform: HOST,
      harnessIds: ['factory'],
      fetchHealth: NO_LISTENER,
    })

    expect(disabled.exitCode).toBe(0)
    expect(existsSync(f.pluginDir)).toBe(false)
    expect(readFileSync(f.settingsFile, 'utf8')).toBe(owner)
  })

  it('turns the capture consent on with enable and off with disable', async () => {
    const f = fixture('factory-writer-consent-')
    await runCapture('enable', {
      homeDir: f.home,
      platform: HOST,
      harnessIds: ['factory'],
      fetchHealth: NO_LISTENER,
    })
    expect(consentText(f)).toMatch(/true/i)

    await runCapture('disable', {
      homeDir: f.home,
      platform: HOST,
      harnessIds: ['factory'],
      fetchHealth: NO_LISTENER,
    })
    expect(consentText(f)).not.toMatch(/true/i)
  })
})
