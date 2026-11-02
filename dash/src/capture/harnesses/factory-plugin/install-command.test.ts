// RED tests for `kyberdash kyber capture plugin install --harness factory`.
//
// The module under test is `./install-command.ts`, which does not exist yet: this file is
// the contract the implementation is written against, and the run that proves it is a RED
// run whose only failure is the missing module.
//
// The verb is the one-shot install for the callers outside the Settings drawer (Squad,
// `kyber-weave update`): it installs the Factory capture plugin DIRECTORY, it keeps the
// owner's Factory settings untouched, and it never writes a configuration key.
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
})
