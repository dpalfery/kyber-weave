// The Factory (Droid) marketplace route: the install contract Factory actually loads.
//
// Factory does not discover a plugin from a directory under its own root. It loads one from
// a MARKETPLACE: a directory holding `.factory-plugin/marketplace.json`, whose `plugins[]`
// entries name a plugin and the RELATIVE path of its payload. The plugin's identity is
// `<plugin>@<marketplace>`, and the marketplace's registered name is the basename of the
// directory that was added — so the tree's directory name is load-bearing.
//
// These cases pin that route end to end, through an INJECTED harness CLI: the test asserts
// the argv the installer would run, not the effect on a real Factory, so no test needs a
// real `droid` on the machine.
//
//   droid plugin marketplace add <marketplace root>
//   droid plugin install kyberdash-factory@kyberdash --scope user
//
// and the uninstall, which removes the harness-side state FIRST:
//
//   droid plugin uninstall kyberdash-factory@kyberdash --scope user
//   droid plugin marketplace remove kyberdash
//
// The harness's CLI is the only interface to the harness's state: the owner's
// `~/.factory/settings.json` is never opened, and the harness's own plugin cache is the
// harness's file.

import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import { sha256Hex } from '../../receipt.js'
import { installFactoryPlugin, uninstallFactoryPlugin } from './install.js'
import {
  FACTORY_MARKETPLACE_NAME,
  FACTORY_PLUGIN_IDENTITY,
  parseMarketplaceList,
  parsePluginList,
  readFactoryHarnessRegistry,
  readFactoryHarnessResidue,
  readFactoryHarnessState,
  resolveFactoryHarnessCli,
  writeMarketplaceTree,
  type FactoryHarnessRegistry,
} from './marketplace.js'
import { fakeCommandLines, fakeHarness, type FakeHarness } from './testing.js'
import { allowRealSpawns, resetSpawnAllowance } from '../../../../tests/setup/spawn-guard.js'

/** The marketplace tree root, relative to the injected home. */
const MARKETPLACE_RELATIVE = join('.kyberdash', 'marketplaces', FACTORY_MARKETPLACE_NAME)

/** Every file Kyber owns in the marketplace tree, relative to the tree root. */
const OWNED_FILES = [
  '.factory-plugin/marketplace.json',
  'kyberdash-factory/.factory-plugin/plugin.json',
  'kyberdash-factory/hooks/hooks.json',
  'kyberdash-factory/hooks/kyberdash-factory.mjs',
]

/** The harness commands the installer may run, and nothing else. */
const READ_COMMANDS = ['plugin marketplace list', 'plugin list --scope user']

const dirs: string[] = []
afterEach(() => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true })
  resetSpawnAllowance()
})

function temp(prefix: string): string {
  const dir = mkdtempSync(join(tmpdir(), prefix))
  dirs.push(dir)
  return dir
}

type Fixture = {
  /** The injected home: KyberDash's own state lives in `<home>/.kyberdash`. */
  home: string
  /** The Factory root. KyberDash writes nothing under it. */
  root: string
  /** The marketplace tree root. */
  marketplaceRoot: string
  /** The plugin's own receipt, beside the consent file. */
  receiptFile: string
}

/** A temp home and a temp Factory root. The expected paths are computed, not read back. */
function fixture(prefix: string): Fixture {
  const home = temp(`${prefix}home-`)
  const root = temp(`${prefix}root-`)
  return {
    home,
    root,
    marketplaceRoot: join(home, MARKETPLACE_RELATIVE),
    receiptFile: join(home, '.kyberdash', 'factory-plugin-receipt.json'),
  }
}

/** Every file under `root`, as sorted relative posix-style paths. */
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

/** The install, with the harness CLI injected. */
function install(f: Fixture, fake: FakeHarness, extra: Record<string, unknown> = {}) {
  return installFactoryPlugin(f.home, undefined, {
    mode: 'strict',
    processEnv: { FACTORY_DIR: f.root },
    harnessCli: fake.harness,
    ...extra,
  })
}

/** The uninstall, with the harness CLI injected. */
function uninstall(f: Fixture, fake: FakeHarness, extra: Record<string, unknown> = {}) {
  return uninstallFactoryPlugin(f.home, undefined, {
    processEnv: { FACTORY_DIR: f.root },
    harnessCli: fake.harness,
    ...extra,
  })
}

describe('the marketplace install route', () => {
  it('runs "plugin marketplace add <root>" and then "plugin install <identity> --scope user", in that order', () => {
    const f = fixture('factory-marketplace-add-')
    const fake = fakeHarness()

    const result = install(f, fake)

    expect(result.exitCode).toBe(0)
    const mutating = fakeCommandLines(fake).filter((line) => !READ_COMMANDS.includes(line))
    expect(mutating).toEqual([
      `plugin marketplace add ${f.marketplaceRoot}`,
      `plugin install ${FACTORY_PLUGIN_IDENTITY} --scope user`,
    ])
  })

  it('reads the harness state only through "plugin marketplace list" and "plugin list --scope user"', () => {
    const f = fixture('factory-marketplace-read-')
    const fake = fakeHarness()

    install(f, fake)

    expect(fakeCommandLines(fake)).toEqual([
      ...READ_COMMANDS,
      `plugin marketplace add ${f.marketplaceRoot}`,
      `plugin install ${FACTORY_PLUGIN_IDENTITY} --scope user`,
    ])
  })

  it('writes the marketplace tree under KyberDash\'s own state directory, and nothing under the Factory root', () => {
    const f = fixture('factory-marketplace-tree-')
    const fake = fakeHarness()

    install(f, fake)

    expect(listFiles(f.marketplaceRoot)).toEqual([...OWNED_FILES].sort())
    expect(listFiles(f.root)).toEqual([])
  })

  it('gives the marketplace manifest a relative source for the plugin payload', () => {
    const f = fixture('factory-marketplace-manifest-')
    const fake = fakeHarness()

    install(f, fake)

    const manifest = JSON.parse(
      readFileSync(join(f.marketplaceRoot, '.factory-plugin', 'marketplace.json'), 'utf8'),
    ) as { name?: unknown; plugins?: { name?: unknown; source?: unknown }[] }
    expect(manifest.name).toBe(FACTORY_MARKETPLACE_NAME)
    expect(manifest.plugins).toHaveLength(1)
    expect(manifest.plugins?.[0]?.name).toBe('kyberdash-factory')
    // The source is relative: Factory rejects an absolute source, and a source that escapes
    // the marketplace directory.
    expect(manifest.plugins?.[0]?.source).toBe('./kyberdash-factory')
  })

  it('names the marketplace, the plugin identity, the tree root and a digest per owned file in the receipt', () => {
    const f = fixture('factory-marketplace-receipt-')
    const fake = fakeHarness()

    install(f, fake)

    const receipt = JSON.parse(readFileSync(f.receiptFile, 'utf8')) as {
      version: number
      marketplace: string
      plugin: string
      root: string
      files: { path: string; sha256: string }[]
    }
    expect(receipt.version).toBe(1)
    expect(receipt.marketplace).toBe(FACTORY_MARKETPLACE_NAME)
    expect(receipt.plugin).toBe(FACTORY_PLUGIN_IDENTITY)
    expect(receipt.root).toBe(f.marketplaceRoot)
    expect(receipt.files).toHaveLength(OWNED_FILES.length)
    // Every owned file is digested, the marketplace manifest included.
    for (const relative of OWNED_FILES) {
      const absolute = join(f.marketplaceRoot, ...relative.split('/'))
      const entry = receipt.files.find((file) => file.path === absolute)
      expect(entry, `the receipt names ${relative}`).toBeDefined()
      expect(entry?.sha256).toMatch(/^[0-9a-f]{64}$/)
      expect(entry?.sha256).toBe(sha256Hex(readFileSync(absolute)))
    }
  })

  it('runs "plugin uninstall <identity> --scope user" and then "plugin marketplace remove kyberdash", and only then removes the tree and the receipt', () => {
    const f = fixture('factory-marketplace-uninstall-')
    const fake = fakeHarness()
    install(f, fake)
    const before = fakeCommandLines(fake).length

    const result = uninstall(f, fake)

    expect(result.exitCode).toBe(0)
    expect(fakeCommandLines(fake).slice(before)).toEqual([
      ...READ_COMMANDS,
      `plugin uninstall ${FACTORY_PLUGIN_IDENTITY} --scope user`,
      `plugin marketplace remove ${FACTORY_MARKETPLACE_NAME}`,
      // The registry is read again after the teardown, because the residue is what the
      // teardown was meant to remove and did not.
      ...READ_COMMANDS,
    ])
    expect(existsSync(f.marketplaceRoot)).toBe(false)
    expect(existsSync(f.receiptFile)).toBe(false)
  })

  it('never opens the owner\'s Factory settings file', () => {
    const f = fixture('factory-marketplace-settings-')
    const fake = fakeHarness()
    const owner = '{\n  "theme": "dark"\n}\n'
    mkdirSync(f.root, { recursive: true })
    writeFileSync(join(f.root, 'settings.json'), owner)
    // The file cannot be read at all: an install that needed to read it would fail.
    chmodSync(join(f.root, 'settings.json'), 0o000)
    try {
      const result = install(f, fake)
      expect(result.exitCode).toBe(0)
    } finally {
      chmodSync(join(f.root, 'settings.json'), 0o600)
    }
    expect(readFileSync(join(f.root, 'settings.json'), 'utf8')).toBe(owner)
    expect(statSync(join(f.root, 'settings.json')).isFile()).toBe(true)
  })

  it('leaves the owner\'s settings file byte-identical across the whole install and uninstall cycle', () => {
    const f = fixture('factory-marketplace-settings-cycle-')
    const fake = fakeHarness()
    mkdirSync(f.root, { recursive: true })
    // The owner's own registry: a marketplace and a plugin of their own, and our plugin
    // named as disabled. None of it is ours, and none of it may change.
    const owner = `${JSON.stringify(
      {
        extraKnownMarketplaces: { other: { source: { source: 'local', path: '/somewhere/else' } } },
        enabledPlugins: { 'kyberdash-factory@kyberdash': false },
      },
      null,
      2,
    )}\n`
    const settings = join(f.root, 'settings.json')
    writeFileSync(settings, owner)

    install(f, fake)
    uninstall(f, fake)

    expect(readFileSync(settings, 'utf8')).toBe(owner)
  })

  it('never writes the harness\'s own plugin cache or its installed-plugin record', () => {
    const f = fixture('factory-marketplace-cache-')
    const fake = fakeHarness()

    install(f, fake)

    // The harness's cache lives under the Factory root, and KyberDash writes nothing there.
    expect(listFiles(f.root)).toEqual([])
  })
})

describe('the harness-side state vocabulary', () => {
  it('reports plugin-installed when the harness holds the plugin identity', () => {
    const fake = fakeHarness()
    fake.plugins.add(FACTORY_PLUGIN_IDENTITY)

    const report = readFactoryHarnessState(fake.harness, { treeWritten: true })

    expect(report.state).toBe('plugin-installed')
    expect(report.cause).toBeUndefined()
  })

  it('reports marketplace-registered when the harness holds the marketplace but not the plugin', () => {
    const fake = fakeHarness()
    fake.marketplaces.add(FACTORY_MARKETPLACE_NAME)

    const report = readFactoryHarnessState(fake.harness, { treeWritten: true })

    expect(report.state).toBe('marketplace-registered')
    expect(report.cause).toBeUndefined()
  })

  it('reports marketplace-tree-written when the tree landed and the harness holds neither', () => {
    const fake = fakeHarness()

    const report = readFactoryHarnessState(fake.harness, { treeWritten: true })

    expect(report.state).toBe('marketplace-tree-written')
    expect(report.cause).toBeUndefined()
  })

  it('reports unknown with the named cause when the droid CLI is missing, and never "installed"', () => {
    const fake = fakeHarness({ unavailable: 'the droid CLI was not found on PATH' })

    const report = readFactoryHarnessState(fake.harness, { treeWritten: true })

    expect(report.state).toBe('unknown')
    expect(report.cause).toBe('the droid CLI was not found on PATH')
    expect(report.state).not.toBe('plugin-installed')
  })

  it('reports unknown with the named cause when the harness answers with output KyberDash cannot understand', () => {
    const fake = fakeHarness()
    fake.harness.run = () => ({ exitCode: 0, stdout: '', stderr: '' })

    const report = readFactoryHarnessState(fake.harness, { treeWritten: true })

    expect(report.state).toBe('unknown')
    expect(report.cause).toMatch(/could not understand/)
  })

  it('reports unknown with the named cause when the harness command fails, quoting its stderr', () => {
    const fake = fakeHarness()
    fake.failNext('Error: Unable to resolve plugin settings')

    const report = readFactoryHarnessState(fake.harness, { treeWritten: true })

    expect(report.state).toBe('unknown')
    expect(report.cause).toMatch(/Unable to resolve plugin settings/)
  })

  it('does not mistake a plugin of the same name in another marketplace for the plugin identity', () => {
    const fake = fakeHarness()
    fake.harness.run = (argv) =>
      argv[1] === 'list'
        ? { exitCode: 0, stdout: 'kyberdash-factory@other  Enabled  1.0.0\n', stderr: '' }
        : { exitCode: 0, stdout: 'other  /somewhere/other\n', stderr: '' }

    const report = readFactoryHarnessState(fake.harness, { treeWritten: true })

    expect(report.state).toBe('marketplace-tree-written')
  })
})

describe('the install reports the harness it used', () => {
  it('names the executable the install drove the harness through', () => {
    const f = fixture('factory-marketplace-executable-')
    const fake = fakeHarness({ executable: '/opt/bin/droid' })

    const result = install(f, fake)

    expect(result.exitCode).toBe(0)
    expect(result.out.join('\n')).toContain('/opt/bin/droid')
  })

  it('reports a missing harness CLI as unknown, and writes nothing', () => {
    const f = fixture('factory-marketplace-missing-')
    const fake = fakeHarness({ unavailable: 'the droid CLI was not found on PATH' })

    const result = install(f, fake)

    expect(result.exitCode).toBe(1)
    expect(result.err.join('\n')).toMatch(/was not found on PATH/)
    expect(existsSync(f.marketplaceRoot)).toBe(false)
    expect(existsSync(f.receiptFile)).toBe(false)
  })
})

describe('a name the harness already holds', () => {
  it('refuses a marketplace of the same name, and writes nothing', () => {
    const f = fixture('factory-marketplace-collision-')
    const fake = fakeHarness()
    fake.marketplaces.add(FACTORY_MARKETPLACE_NAME)

    const result = install(f, fake)

    expect(result.exitCode).toBe(1)
    expect(result.err.join('\n')).toContain(FACTORY_MARKETPLACE_NAME)
    // The refusal names the command that removes the entry it collides with.
    expect(result.err.join('\n')).toContain('droid plugin marketplace remove kyberdash')
    expect(existsSync(f.marketplaceRoot)).toBe(false)
    expect(existsSync(f.receiptFile)).toBe(false)
  })

  it('keeps a marketplace of the same name with a warning in preserve mode', () => {
    const f = fixture('factory-marketplace-collision-preserve-')
    const fake = fakeHarness()
    fake.marketplaces.add(FACTORY_MARKETPLACE_NAME)

    const result = install(f, fake, { mode: 'preserve' })

    expect(result.exitCode).toBe(0)
    expect(result.err.join('\n')).toMatch(/warning/)
    expect(result.err.join('\n')).toContain('droid plugin marketplace remove kyberdash')
    expect(existsSync(f.marketplaceRoot)).toBe(false)
  })

  it('refuses the plugin identity the harness holds from a marketplace KyberDash does not own', () => {
    const f = fixture('factory-plugin-collision-')
    const fake = fakeHarness()
    fake.marketplaces.add(FACTORY_MARKETPLACE_NAME)
    fake.plugins.add(FACTORY_PLUGIN_IDENTITY)

    const result = install(f, fake)

    expect(result.exitCode).toBe(1)
    expect(result.err.join('\n')).toContain(FACTORY_PLUGIN_IDENTITY)
    // The refusal names the command that removes the entry it collides with.
    expect(result.err.join('\n')).toContain('droid plugin uninstall kyberdash-factory@kyberdash --scope user')
    expect(existsSync(f.marketplaceRoot)).toBe(false)
  })

  it('does not treat a plugin of the same name in another marketplace as a conflict', () => {
    const f = fixture('factory-other-marketplace-plugin-')
    const fake = fakeHarness()
    fake.plugins.add('kyberdash-factory@other')

    const result = install(f, fake)

    expect(result.exitCode).toBe(0)
    expect(existsSync(f.marketplaceRoot)).toBe(true)
  })

  it('reports the conflict with the registry, the entry and the command that removes it', () => {
    const fake = fakeHarness()
    fake.marketplaces.add(FACTORY_MARKETPLACE_NAME)

    const report = readFactoryHarnessState(fake.harness, { treeWritten: false })

    expect(report.state).toBe('conflict')
    expect(report.conflict?.registry).toBe('extraKnownMarketplaces')
    expect(report.conflict?.entry).toBe(FACTORY_MARKETPLACE_NAME)
    expect(report.conflict?.command).toBe('droid plugin marketplace remove kyberdash')
    // A conflict is not a state the install may proceed from.
    expect(report.readable).toBe(false)
  })

  it('decides the conflict by the path the harness registered the marketplace from', () => {
    const fake = fakeHarness()
    fake.harness.run = (argv) =>
      argv[1] === 'marketplace'
        ? {
            exitCode: 0,
            stdout: `MARKETPLACE  SOURCE  PLUGINS  DISPLAY NAME\n${FACTORY_MARKETPLACE_NAME}  local:/somewhere/else/kyberdash  1  —\n`,
            stderr: '',
          }
        : { exitCode: 0, stdout: 'PLUGIN  STATUS  SOURCE  VERSION\n', stderr: '' }

    const ours = readFactoryHarnessState(fake.harness, {
      treeWritten: true,
      marketplaceRoot: '/somewhere/else/kyberdash',
    })
    const theirs = readFactoryHarnessState(fake.harness, {
      treeWritten: true,
      marketplaceRoot: '/kyberdash/marketplaces/kyberdash',
    })

    // The same registered path is ours when it is our tree, and a collision when it is not.
    expect(ours.state).toBe('marketplace-registered')
    expect(ours.conflict).toBeUndefined()
    expect(theirs.state).toBe('conflict')
    expect(theirs.conflict?.entry).toBe(FACTORY_MARKETPLACE_NAME)
  })
})

describe('the harness-side snapshot the receipt records', () => {
  it('snapshots the registered marketplaces and the installed plugins with their statuses', () => {
    const fake = fakeHarness()
    fake.marketplaces.add(FACTORY_MARKETPLACE_NAME)
    fake.plugins.add(FACTORY_PLUGIN_IDENTITY)

    const read = readFactoryHarnessRegistry(fake.harness)

    expect(read.ok).toBe(true)
    if (!read.ok) return
    expect(read.registry.marketplaces.map((entry) => entry.name)).toEqual([FACTORY_MARKETPLACE_NAME])
    expect(read.registry.marketplaces[0]?.source).toContain('/kyberdash')
    expect(read.registry.plugins.map((entry) => entry.identity)).toEqual([FACTORY_PLUGIN_IDENTITY])
    expect(read.registry.plugins[0]?.status).toBe('Enabled')
  })

  it('reports the harness\'s own answer when the registry cannot be read', () => {
    const fake = fakeHarness({ unavailable: 'the droid CLI was not found on PATH' })

    const read = readFactoryHarnessRegistry(fake.harness)

    expect(read.ok).toBe(false)
    if (read.ok) return
    expect(read.cause).toBe('the droid CLI was not found on PATH')
  })
})

describe('the residue the harness\'s own teardown leaves', () => {
  const before: FactoryHarnessRegistry = {
    marketplaces: [{ name: FACTORY_MARKETPLACE_NAME, source: 'local:/kyberdash/marketplaces/kyberdash' }],
    plugins: [{ identity: FACTORY_PLUGIN_IDENTITY, status: 'Enabled' }],
  }

  it('reports the marketplace registration the harness\'s uninstall left behind', () => {
    const fake = fakeHarness()
    fake.marketplaces.add(FACTORY_MARKETPLACE_NAME)

    const residue = readFactoryHarnessResidue(fake.harness, before)

    expect(residue).toHaveLength(1)
    expect(residue[0]?.registry).toBe('extraKnownMarketplaces')
    expect(residue[0]?.entry).toBe(FACTORY_MARKETPLACE_NAME)
    expect(residue[0]?.command).toBe('droid plugin marketplace remove kyberdash')
  })

  it('reports the plugin the harness\'s uninstall left behind', () => {
    const fake = fakeHarness()
    fake.plugins.add(FACTORY_PLUGIN_IDENTITY)

    const residue = readFactoryHarnessResidue(fake.harness, before)

    expect(residue).toHaveLength(1)
    expect(residue[0]?.registry).toBe('enabledPlugins')
    expect(residue[0]?.entry).toBe(FACTORY_PLUGIN_IDENTITY)
    expect(residue[0]?.command).toBe('droid plugin uninstall kyberdash-factory@kyberdash --scope user')
  })

  it('reports the owner\'s disabled entry the harness\'s uninstall deleted, with the exact restoration', () => {
    const fake = fakeHarness()
    const disabled: FactoryHarnessRegistry = {
      marketplaces: [],
      plugins: [{ identity: FACTORY_PLUGIN_IDENTITY, status: 'Disabled' }],
    }

    const residue = readFactoryHarnessResidue(fake.harness, disabled)

    expect(residue).toHaveLength(1)
    expect(residue[0]?.registry).toBe('enabledPlugins')
    expect(residue[0]?.entry).toBe(FACTORY_PLUGIN_IDENTITY)
    // The harness has no command that restores a disabled entry, so the residue names the
    // exact edit the owner makes instead.
    expect(residue[0]?.command).toContain('"kyberdash-factory@kyberdash": false')
    expect(residue[0]?.command).toContain('enabledPlugins')
  })

  it('reports nothing when the harness\'s own teardown cleaned up after itself', () => {
    const fake = fakeHarness()

    expect(readFactoryHarnessResidue(fake.harness, before)).toEqual([])
  })
})

/**
 * The harness prints its list output as a table whose columns are sized to the content, and
 * a cell that does not fit is truncated with the overflow printed on the lines below. These
 * are the REAL outputs, captured from Factory Droid CLI 1.1.0 on 2026-11-02.
 */
describe('the harness prints a wrapped table', () => {
  // The REAL output, captured from Factory Droid CLI 1.1.0 on 2026-11-02: two
  // marketplaces, one with a long name, the harness truncating both cells and printing the
  // overflow on the lines below.
  const WRAPPED_MARKETPLACE_LIST = [
    'MARKETPLACE                            SOURCE                                  PLUGINS  DISPLAY NAME',
    'a-very-long-marketplace-directory-nam  local:/var/folders/gg/jnm7dkgs7w1dhkb3        0  m',
    'e-that-will-not-fit                    j8wm4gg00000gn/T/tmp.Q5N8ItWjCd/a-very',
    '                                       -long-marketplace-directory-name-that-',
    '                                       will-not-fit',
    'kyberdash                              local:/var/folders/gg/jnm7dkgs7w1dhkb3        0  —',
    '                                       j8wm4gg00000gn/T/tmp.Q5N8ItWjCd/kyberd',
    '                                       ash',
    '',
  ].join('\n')

  it('reads the whole SOURCE cell when the harness wraps it', () => {
    const registry = parseMarketplaceList(WRAPPED_MARKETPLACE_LIST)
    const kyberdash = registry?.find((entry) => entry.name === 'kyberdash')

    expect(kyberdash?.source).toBe(
      'local:/var/folders/gg/jnm7dkgs7w1dhkb3j8wm4gg00000gn/T/tmp.Q5N8ItWjCd/kyberdash',
    )
  })

  it('reads the whole marketplace name and source when both wrap', () => {
    const registry = parseMarketplaceList(WRAPPED_MARKETPLACE_LIST)

    expect(registry).toHaveLength(2)
    expect(registry?.[0]?.name).toBe(
      'a-very-long-marketplace-directory-name-that-will-not-fit',
    )
    expect(registry?.[0]?.source).toBe(
      'local:/var/folders/gg/jnm7dkgs7w1dhkb3j8wm4gg00000gn/T/tmp.Q5N8ItWjCd/a-very-long-marketplace-directory-name-that-will-not-fit',
    )
    expect(registry?.[1]?.name).toBe('kyberdash')
  })

  it('does not mistake a wrapped continuation line for a row of its own', () => {
    // Six of the eight lines below the header are continuations of the two rows.
    expect(parseMarketplaceList(WRAPPED_MARKETPLACE_LIST)).toHaveLength(2)
  })

  it('reads a plugin identity the harness wrapped', () => {
    const registry = parsePluginList(
      [
        'PLUGIN                       STATUS   SOURCE  VERSION',
        'a-very-long-plugin-identit  Enabled  user    mvz6nbvc',
        'y@kyberdash',
        '',
      ].join('\n'),
    )

    expect(registry).toHaveLength(1)
    expect(registry?.[0]?.identity).toBe('a-very-long-plugin-identity@kyberdash')
    expect(registry?.[0]?.status).toBe('Enabled')
  })

  it('decides the ownership by the whole source when the harness wrapped it', () => {
    const fake = fakeHarness()
    fake.harness.run = (argv) =>
      argv[1] === 'marketplace'
        ? { exitCode: 0, stdout: WRAPPED_MARKETPLACE_LIST, stderr: '' }
        : { exitCode: 0, stdout: 'PLUGIN  STATUS  SOURCE  VERSION\n', stderr: '' }

    const ours = readFactoryHarnessState(fake.harness, {
      treeWritten: true,
      marketplaceRoot: '/var/folders/gg/jnm7dkgs7w1dhkb3j8wm4gg00000gn/T/tmp.Q5N8ItWjCd/kyberdash',
    })
    const theirs = readFactoryHarnessState(fake.harness, {
      treeWritten: true,
      marketplaceRoot: '/kyberdash/marketplaces/kyberdash',
    })

    // A wrapped source that is OUR tree is not a collision; the same output against a
    // different root is.
    expect(ours.state).toBe('marketplace-registered')
    expect(ours.conflict).toBeUndefined()
    expect(theirs.state).toBe('conflict')
  })
})

describe('the driver that runs the harness', () => {
  it('refuses to start the real CLI when the spawn guard forbids it', () => {
    // The guard's deny mode is set for every test file, so the driver must not reach the
    // developer's own Factory installation.
    const cli = resolveFactoryHarnessCli({ processEnv: { ...process.env } })

    expect(cli.unavailable).toMatch(/spawn guard/i)
    expect(cli.run(['plugin', 'list'])).toMatchObject({ exitCode: 127 })
  })

  it('refuses when the injected environment carries the deny flag', () => {
    const cli = resolveFactoryHarnessCli({
      processEnv: { PATH: '/usr/bin:/bin', KYBERDASH_SPAWN_GUARD: 'deny' },
    })

    expect(cli.unavailable).toMatch(/spawn guard/i)
  })

  it('reports a timeout as a timeout, and not as a missing executable', () => {
    // A real child, because only a real child can time out. It sleeps past the cap.
    allowRealSpawns('the driver timeout case needs a real child that outlives the cap')
    const dir = temp('factory-driver-timeout-')
    const script = join(dir, 'droid')
    writeFileSync(script, '#!/bin/sh\nsleep 5\n')
    chmodSync(script, 0o755)

    const cli = resolveFactoryHarnessCli({
      processEnv: { PATH: dir },
      timeoutMs: 250,
    })

    const answer = cli.run(['plugin', 'list'])

    expect(answer.exitCode).toBe(124)
    expect(answer.stderr).toMatch(/timed out after 250 ms/)
  })

  it('bounds the command\'s output, not one stream of it', () => {
    const dir = temp('factory-driver-output-')
    const script = join(dir, 'droid')
    writeFileSync(script, '#!/bin/sh\necho aaaaaaaaaaaaaaaaaaaa\necho bbbbbbbbbbbbbbbbbbbb 1>&2\n')
    chmodSync(script, 0o755)
    allowRealSpawns('the driver output case needs a real child that writes to both streams')

    const cli = resolveFactoryHarnessCli({ processEnv: { PATH: dir }, maxOutputBytes: 4096 })

    const answer = cli.run(['plugin', 'list'])

    // Both streams arrived, and neither exceeded the cap: the cap is on the command.
    expect(answer.stdout).toContain('aaaa')
    expect(answer.stderr).toContain('bbbb')
    expect(answer.exitCode).toBe(0)
  })
})

describe('the tree writer', () => {
  /**
   * The tree writer's failure, provoked FOR REAL: a file cannot be renamed onto a directory,
   * so the second file of the tree cannot land where it must go and the filesystem's own
   * errno is what fails. Nothing is mocked, so the failure is the one an owner hits.
   */
  function writeTreeThatFails(root: string): NodeJS.ErrnoException | undefined {
    const blocked = join(root, 'kyberdash-factory', '.factory-plugin', 'plugin.json')
    mkdirSync(blocked, { recursive: true })
    writeFileSync(join(blocked, 'owner.txt'), "// the owner's own file\n")
    try {
      writeMarketplaceTree(root)
      return undefined
    } catch (error) {
      return error as NodeJS.ErrnoException
    }
  }

  it('leaves no staged sibling behind when a rename fails', () => {
    const root = temp('factory-marketplace-staged-')

    const thrown = writeTreeThatFails(root)

    // The writer must not swallow the I/O failure.
    expect(thrown, 'the writer must not swallow the I/O failure').toBeDefined()
    expect(thrown?.code, "the failure is the filesystem's own errno").toMatch(/^EISDIR$|^ENOTEMPTY$/)

    // A staged sibling left behind is a file KyberDash did not write, and the classifier
    // reads any such file as a foreign tree: the owner's own tree could then neither be
    // installed nor uninstalled, and only a manual deletion cleared it.
    expect(
      listFiles(root).filter((relative) => relative.endsWith('.tmp')),
      'no staged file is left behind',
    ).toEqual([])
  })

  it('leaves no part of the tree behind when a rename fails', () => {
    const root = temp('factory-marketplace-partial-')

    const thrown = writeTreeThatFails(root)

    expect(thrown, 'the writer must not swallow the I/O failure').toBeDefined()
    // The tree is written whole or not at all: a file that was renamed into place before the
    // failure is part of the tree too, and half a tree is not a tree.
    expect(
      listFiles(root).filter((relative) => OWNED_FILES.includes(relative)),
      'no file of the tree is left behind',
    ).toEqual([])
  })
})
