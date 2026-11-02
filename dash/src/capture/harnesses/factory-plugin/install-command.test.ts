// Tests for `kyberdash kyber capture plugin install --harness factory`.
//
// The verb is the one-shot install for the callers outside the Settings drawer (Squad,
// `kyber-weave update`): it installs the Factory capture plugin DIRECTORY, it keeps the
// owner's Factory settings untouched, and it never writes a configuration key.
//
// The install never turns the content consent on: it creates the consent file with the
// content off when nothing is there, and leaves the owner's own value exactly as it was.
// Only `kyberdash kyber capture plugin consent --harness factory --on` (or the owner
// editing that file by hand) records the decision to capture the prompt text.
//
// The tree, the receipt and the consent file are the ones the installer contract pins
// (`./install.test.ts`):
//
//   <factory root>/plugins/kyberdash-factory/
//   ├── .factory-plugin/plugin.json
//   └── hooks/{hooks.json,kyberdash-factory.mjs}
//
// `<factory root>` is the injected home's `.factory`, and `$FACTORY_DIR` points at the same
// directory, so the test never depends on which of the two the command resolves. Nothing
// here touches the developer's own `~/.factory` or `~/.kyberdash`.
//
// The verb refuses `--config-dir`: that flag selects the OpenCode config root, and the Factory
// harness resolves its own root (`$FACTORY_DIR`, else `~/.factory`) — the same one the Factory
// FILE source reads. A request that carries the flag is a usage error (exit 2) and writes
// nothing at all.

import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, describe, expect, it, vi } from 'vitest'

import type { PathEnv } from '../../paths.js'

/** The plugin directory Kyber owns inside the Factory root. */
const PLUGIN_DIR_NAME = 'kyberdash-factory'
/** The files Kyber owns in that directory, relative to it: the tree shape. */
const OWNED_FILES = ['.factory-plugin/plugin.json', 'hooks/hooks.json', 'hooks/kyberdash-factory.mjs']

type PluginInstallCommandResult = { exitCode: number; stdout: string; stderr: string }

type PluginInstallCommandOptions = {
  harness?: string
  preserve?: boolean
  ifInstalled?: boolean
  dryRun?: boolean
  /** The OpenCode config root. The Factory verb refuses it: exit 2, nothing written. */
  configDir?: string
  homeDir: string
  env?: PathEnv
  processEnv?: NodeJS.ProcessEnv
}

type PluginInstallCommandRunner = (options: PluginInstallCommandOptions) => PluginInstallCommandResult

/**
 * The command runner, loaded lazily so a missing module fails the test that needs it
 * rather than the whole file. The export is looked up by name: the command is the seam,
 * not the spelling of its function.
 */
async function loadRunner(): Promise<PluginInstallCommandRunner> {
  const module = (await import('./install-command.js')) as unknown as Record<string, unknown>
  for (const name of ['runFactoryPluginInstall', 'runPluginInstall']) {
    const candidate = module[name]
    if (typeof candidate === 'function') return candidate as unknown as PluginInstallCommandRunner
  }
  throw new Error('the factory plugin install command exports no runner')
}

type FactoryPluginConsentOptions = {
  harness?: string
  on?: boolean
  off?: boolean
  homeDir: string
  env?: PathEnv
}

/** The consent runner: the only path that turns the content consent on. */
async function loadConsentRunner(): Promise<
  (options: FactoryPluginConsentOptions) => PluginInstallCommandResult
> {
  const module = (await import('./install-command.js')) as unknown as Record<string, unknown>
  const candidate = module['runFactoryPluginConsent']
  if (typeof candidate !== 'function') {
    throw new Error('the factory plugin install command exports no consent runner')
  }
  return candidate as unknown as (
    options: FactoryPluginConsentOptions,
  ) => PluginInstallCommandResult
}

type Fixture = {
  /** The injected home: KyberDash's own state lives in `<home>/.kyberdash`. */
  home: string
  /** The Factory root: the home's `.factory`, which `$FACTORY_DIR` also points at. */
  root: string
  /** The plugin directory Kyber owns inside the Factory root. */
  pluginDir: string
  /** The owner's Factory settings. The command never writes it. */
  settingsFile: string
  /** The plugin's own receipt, beside the consent file. */
  receiptFile: string
  /** The content consent file: the owner's decision about the prompt text. */
  consentFile: string
}

const tempDirs: string[] = []
afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true })
  vi.unstubAllEnvs()
})

function fixture(prefix: string): Fixture {
  const home = mkdtempSync(join(tmpdir(), prefix))
  tempDirs.push(home)
  const root = join(home, '.factory')
  mkdirSync(root, { recursive: true })
  vi.stubEnv('FACTORY_DIR', root)
  return {
    home,
    root,
    pluginDir: join(root, 'plugins', PLUGIN_DIR_NAME),
    settingsFile: join(root, 'settings.json'),
    receiptFile: join(home, '.kyberdash', 'factory-plugin-receipt.json'),
    consentFile: join(home, '.kyberdash', 'factory-plugin.json'),
  }
}

/**
 * The consent file's `content` value, or undefined when there is no file. The file is
 * `{ "version": 1, "content": <boolean> }`.
 */
function consentContent(f: Fixture): boolean | undefined {
  if (!existsSync(f.consentFile)) return undefined
  const parsed: unknown = JSON.parse(readFileSync(f.consentFile, 'utf8'))
  if (typeof parsed !== 'object' || parsed === null) return undefined
  const content = (parsed as Record<string, unknown>)['content']
  return typeof content === 'boolean' ? content : undefined
}

/** The owner's own consent file, written by hand. */
function writeConsent(f: Fixture, content: boolean): void {
  mkdirSync(join(f.consentFile, '..'), { recursive: true })
  writeFileSync(f.consentFile, `${JSON.stringify({ version: 1, content }, null, 2)}\n`)
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

/** Run the install command against a fixture. */
async function run(
  f: Fixture,
  extra: Partial<PluginInstallCommandOptions> = {},
): Promise<PluginInstallCommandResult> {
  const runner = await loadRunner()
  return runner({
    harness: 'factory',
    homeDir: f.home,
    env: { platform: process.platform },
    processEnv: { FACTORY_DIR: f.root },
    ...extra,
  })
}

describe('capture plugin install --harness factory', () => {
  it('installs the Factory capture plugin directory', async () => {
    const f = fixture('factory-command-install-')
    const result = await run(f)
    expect(result.exitCode).toBe(0)
    expect(listFiles(f.pluginDir)).toEqual([...OWNED_FILES].sort())
    // The plugin's own receipt lives in KyberDash's state directory.
    expect(existsSync(f.receiptFile)).toBe(true)
    expect(result.stdout).toContain(f.pluginDir)
  })

  it('writes nothing into the Factory root but the plugin directory', async () => {
    const f = fixture('factory-command-owner-')
    const owner = '{\n  "theme": "dark"\n}\n'
    writeFileSync(f.settingsFile, owner)
    const result = await run(f)
    expect(result.exitCode).toBe(0)
    expect(readFileSync(f.settingsFile, 'utf8')).toBe(owner)
    expect(listFiles(f.root).filter((relative) => !relative.startsWith('plugins/'))).toEqual(['settings.json'])
  })

  it('rejects a missing or unsupported harness with exit 2', async () => {
    const f = fixture('factory-command-usage-')
    expect((await run(f, { harness: undefined })).exitCode).toBe(2)
    expect((await run(f, { harness: 'kilo' })).exitCode).toBe(2)
    expect((await run(f, { harness: 'factoryy' })).exitCode).toBe(2)
    // A refused request writes nothing at all.
    expect(existsSync(f.pluginDir)).toBe(false)
    expect(existsSync(join(f.home, '.kyberdash'))).toBe(false)
  })

  it('refuses --config-dir for the Factory harness with exit 2 and a message that names the flag', async () => {
    const f = fixture('factory-command-config-dir-')
    const openCodeRoot = join(f.home, 'opencode-root')

    const result = await run(f, { configDir: openCodeRoot })

    expect(result.exitCode).toBe(2)
    // The message names the flag, so the caller knows which argument was refused.
    expect(result.stderr).toContain('--config-dir')
    // A usage error, not an install: nothing is reported as installed.
    expect(result.stdout).not.toContain('factory plugin:')
  })

  it('a refused --config-dir writes nothing, and the Factory tree still lands in the Factory root', async () => {
    const f = fixture('factory-command-config-dir-nowrite-')
    const openCodeRoot = join(f.home, 'opencode-root')
    // A Factory root the harness owns, and that is NOT the home's `.factory`: the verb resolves
    // the root the Factory FILE source reads, not a caller's directory.
    const harnessRoot = join(f.home, 'harness-factory')

    const refused = await run(f, { configDir: openCodeRoot })

    expect(refused.exitCode).toBe(2)
    // Nothing at all: no plugin tree, no receipt, no consent file.
    expect(listFiles(f.root)).toEqual([])
    expect(existsSync(f.receiptFile)).toBe(false)
    expect(existsSync(f.consentFile)).toBe(false)
    expect(existsSync(join(f.home, '.kyberdash'))).toBe(false)
    // The named root never became a Factory tree.
    expect(existsSync(join(openCodeRoot, 'plugins'))).toBe(false)

    // Without the flag the tree lands in the Factory root the harness resolves itself.
    const installed = await run(f, { processEnv: { FACTORY_DIR: harnessRoot } })

    expect(installed.exitCode).toBe(0)
    expect(listFiles(join(harnessRoot, 'plugins', PLUGIN_DIR_NAME))).toEqual([...OWNED_FILES].sort())
    expect(existsSync(join(f.root, 'plugins'))).toBe(false)
    expect(existsSync(join(openCodeRoot, 'plugins'))).toBe(false)
  })

  it('refuses --config-dir whatever else the request carries', async () => {
    const f = fixture('factory-command-config-dir-modes-')
    const openCodeRoot = join(f.home, 'opencode-root')
    const modes: Partial<PluginInstallCommandOptions>[] = [
      { preserve: true },
      { ifInstalled: true },
      { dryRun: true },
    ]

    for (const mode of modes) {
      const result = await run(f, { ...mode, configDir: openCodeRoot })

      expect(result.exitCode).toBe(2)
      expect(result.stderr).toContain('--config-dir')
    }

    expect(listFiles(f.root)).toEqual([])
  })

  it('refuses a foreign plugin tree in strict mode and writes nothing', async () => {
    const f = fixture('factory-command-foreign-')
    mkdirSync(join(f.pluginDir, 'hooks'), { recursive: true })
    writeFileSync(join(f.pluginDir, 'hooks', 'kyberdash-factory.mjs'), '// owner owned\n')
    const before = listFiles(f.pluginDir)

    const result = await run(f)

    expect(result.exitCode).toBe(1)
    expect(result.stderr).toMatch(/not written by KyberDash|refus/i)
    expect(listFiles(f.pluginDir)).toEqual(before)
    expect(existsSync(f.receiptFile)).toBe(false)
  })

  it('keeps a foreign plugin tree in preserve mode, warns and exits 0', async () => {
    const f = fixture('factory-command-preserve-')
    mkdirSync(join(f.pluginDir, 'hooks'), { recursive: true })
    writeFileSync(join(f.pluginDir, 'hooks', 'kyberdash-factory.mjs'), '// owner owned\n')
    const before = listFiles(f.pluginDir)

    const result = await run(f, { preserve: true })

    expect(result.exitCode).toBe(0)
    expect(result.stderr).toMatch(/warning|not written by KyberDash/i)
    expect(listFiles(f.pluginDir)).toEqual(before)
    expect(existsSync(f.receiptFile)).toBe(false)
  })

  it('--if-installed with nothing installed writes nothing and exits 0', async () => {
    const f = fixture('factory-command-ifinst-')
    const result = await run(f, { ifInstalled: true })
    expect(result.exitCode).toBe(0)
    expect(existsSync(f.pluginDir)).toBe(false)
  })

  it('--if-installed over an installed tree keeps it intact', async () => {
    const f = fixture('factory-command-ifinst-installed-')
    await run(f)
    const before = listFiles(f.pluginDir)
    const result = await run(f, { ifInstalled: true })
    expect(result.exitCode).toBe(0)
    expect(listFiles(f.pluginDir)).toEqual(before)
  })

  it('--dry-run writes nothing and says what it would do', async () => {
    const f = fixture('factory-command-dry-')
    const result = await run(f, { dryRun: true })
    expect(result.exitCode).toBe(0)
    expect(result.stdout).toMatch(/would/i)
    expect(existsSync(f.pluginDir)).toBe(false)
    expect(existsSync(f.receiptFile)).toBe(false)
  })

  it('installing the plugin never turns the content consent on: the file is created with content off', async () => {
    const f = fixture('factory-command-consent-')
    const result = await run(f)
    expect(result.exitCode).toBe(0)
    expect(consentContent(f)).toBe(false)
  })

  it('installing the plugin leaves an owner content consent on', async () => {
    const f = fixture('factory-command-consent-on-')
    writeConsent(f, true)
    const result = await run(f)
    expect(result.exitCode).toBe(0)
    expect(consentContent(f)).toBe(true)
  })

  it('preserving the plugin leaves an owner content consent on', async () => {
    const f = fixture('factory-command-consent-preserve-')
    writeConsent(f, true)
    const result = await run(f, { preserve: true })
    expect(result.exitCode).toBe(0)
    expect(consentContent(f)).toBe(true)
  })

  it('turns the content consent on only when the owner asks for it', async () => {
    const f = fixture('factory-command-consent-verb-')
    const runner = await loadConsentRunner()
    const result = runner({
      harness: 'factory',
      on: true,
      homeDir: f.home,
      env: { platform: process.platform },
    })
    expect(result.exitCode).toBe(0)
    expect(consentContent(f)).toBe(true)
  })
})
