// Tests for the KyberDash Factory (Droid) capture plugin installer.
//
// The module under test is `./install.ts`, which does not exist yet: this file is the
// contract the implementation is written against, and the run that proves it is a RED
// run whose only failure is the missing module.
//
// The contract mirrors the OpenCode installer at `../opencode-plugin/install.ts` — its
// mode names (`strict` / `preserve`), its classifier states, its refusal messages and
// its receipt shape — with one deliberate difference: the owned unit is a whole
// MARKETPLACE TREE, not a single file, and it is installed through the harness's OWN
// commands rather than by dropping a directory where the harness looks.
//
//   <home>/.kyberdash/marketplaces/kyberdash/
//   ├── .factory-plugin/marketplace.json
//   └── kyberdash-factory/
//       ├── .factory-plugin/plugin.json
//       └── hooks/
//           ├── hooks.json
//           └── kyberdash-factory.mjs
//
// The receipt and the consent file live in KyberDash's own state directory, `~/.kyberdash`,
// exactly as the OpenCode installer puts them there. Both are injected through the `home`
// argument, so a test never touches the developer's real `~/.factory` or `~/.kyberdash`.
//
// The receipt grows from the OpenCode one-file receipt to the whole tree:
//
//   { "version": 1, "marketplace": "kyberdash", "plugin": "kyberdash-factory@kyberdash",
//     "root": "<marketplace tree>", "files": [ { "path": ..., "sha256": ... } ] }
//
// The plugin bytes are a committed generated bundle, so these tests pin the tree SHAPE and
// the digest behaviour, never the exact future bytes.

import { chmodSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, statSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { isAbsolute, join } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { sha256Hex } from '../../receipt.js'
import { describeFactoryPluginStatus, factoryPluginPaths, installFactoryPlugin, uninstallFactoryPlugin } from './install.js'
import { FACTORY_MARKETPLACE_CONTENT, FACTORY_MARKETPLACE_FILES } from './marketplace.js'
import { fakeCommandLines, fakeHarness, type FakeHarness } from './testing.js'

/** The marketplace the tree is registered as. Pinned as a literal, not read from the module. */
const MARKETPLACE_NAME = 'kyberdash'
/** The plugin's identity in the harness: `<plugin>@<marketplace>`. */
const PLUGIN_IDENTITY = 'kyberdash-factory@kyberdash'

/** The marketplace tree Kyber owns, relative to the injected home. */
const MARKETPLACE_RELATIVE = join('.kyberdash', 'marketplaces', MARKETPLACE_NAME)
/** The files Kyber owns, relative to the marketplace tree root. This is the tree shape. */
const OWNED_FILES = [
  '.factory-plugin/marketplace.json',
  'kyberdash-factory/.factory-plugin/plugin.json',
  'kyberdash-factory/hooks/hooks.json',
  'kyberdash-factory/hooks/kyberdash-factory.mjs',
]

/** A symlink test is meaningless on Windows, where the installer cannot create one. */
const POSIX_ONLY = process.platform === 'win32'

/** The receipt the installer writes: the marketplace tree, not one file. */
type FactoryReceipt = {
  version: number
  marketplace: string
  plugin: string
  root: string
  files: { path: string; sha256: string }[]
}

const dirs: string[] = []
afterEach(() => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true })
  vi.restoreAllMocks()
})
function temp(prefix: string): string {
  const dir = mkdtempSync(join(tmpdir(), prefix))
  dirs.push(dir)
  return dir
}

/** No FACTORY_DIR unless a test injects one: tests never read the real process env. */
const NO_ENV: NodeJS.ProcessEnv = {}

type Fixture = {
  /** The injected home: KyberDash's state directory is `<home>/.kyberdash`. */
  home: string
  /** The Factory root the installer resolves from `$FACTORY_DIR`. KyberDash writes nothing there. */
  root: string
  /** The environment that points the installer at `root`. */
  env: NodeJS.ProcessEnv
  /** The marketplace tree Kyber owns. */
  pluginDir: string
  receiptFile: string
  consentFile: string
  stateDir: string
  /** The fake harness CLI the install and the uninstall are driven through. */
  fake: FakeHarness
}

/**
 * A temp home and a temp Factory root, wired together by `$FACTORY_DIR`. The expected
 * paths are computed here, not read back from the installer, so the tests pin where the
 * files go instead of agreeing with whatever the installer does.
 */
function fixture(prefix: string): Fixture {
  const home = temp(`${prefix}home-`)
  const root = temp(`${prefix}root-`)
  return {
    home,
    root,
    env: { FACTORY_DIR: root },
    pluginDir: join(home, MARKETPLACE_RELATIVE),
    receiptFile: join(home, '.kyberdash', 'factory-plugin-receipt.json'),
    consentFile: join(home, '.kyberdash', 'factory-plugin.json'),
    stateDir: join(home, '.kyberdash'),
    fake: fakeHarness(),
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

/** The bytes of every file under `root`, keyed by relative path: "nothing changed" as one value. */
function snapshot(root: string): Record<string, string> {
  const files: Record<string, string> = {}
  for (const relative of listFiles(root)) files[relative] = readFileSync(join(root, ...relative.split('/')), 'utf8')
  return files
}

function readReceipt(path: string): FactoryReceipt {
  return JSON.parse(readFileSync(path, 'utf8')) as FactoryReceipt
}

/**
 * The receipt may record absolute paths or paths relative to `root`; both name the same
 * file, and the tests pin WHICH files and WHICH digests, not that spelling.
 */
function resolvedReceiptPaths(receipt: FactoryReceipt): string[] {
  return receipt.files.map((entry) => (isAbsolute(entry.path) ? entry.path : join(receipt.root, entry.path))).sort()
}

function expectedPaths(pluginDir: string): string[] {
  return OWNED_FILES.map((relative) => join(pluginDir, ...relative.split('/'))).sort()
}

/** The strict-mode install, driven through the fixture's fake harness. */
const strict = (f: Fixture, extra: Record<string, unknown> = {}) =>
  installFactoryPlugin(f.home, undefined, {
    mode: 'strict',
    processEnv: f.env,
    harnessCli: f.fake.harness,
    ...extra,
  })

// ── The tree the PREVIOUS route wrote ───────────────────────────────────────────────────────

/** The plugin directory the previous route wrote into the Factory root. */
const LEGACY_PLUGIN_DIR_NAME = 'kyberdash-factory'

/** The files the previous route owned, relative to its plugin directory. */
const LEGACY_FILES = [
  '.factory-plugin/plugin.json',
  'hooks/hooks.json',
  'hooks/kyberdash-factory.mjs',
]

/**
 * The tree the PREVIOUS route wrote into the Factory root, with the receipt that route
 * wrote beside it: `{ version, root, files }`, with no marketplace and no plugin identity.
 *
 * The bytes are synthetic: what the proof needs is a digest per owned file, not the old
 * route's exact bytes.
 */
function legacyInstall(f: Fixture): string {
  const pluginDir = join(f.root, 'plugins', LEGACY_PLUGIN_DIR_NAME)
  const files: { path: string; sha256: string }[] = []
  for (const relative of LEGACY_FILES) {
    const absolute = join(pluginDir, ...relative.split('/'))
    mkdirSync(join(absolute, '..'), { recursive: true })
    const text = `// ${relative}\n`
    writeFileSync(absolute, text)
    files.push({ path: absolute, sha256: sha256Hex(text) })
  }
  mkdirSync(f.stateDir, { recursive: true })
  writeFileSync(
    f.receiptFile,
    `${JSON.stringify({ version: 1, root: pluginDir, files }, null, 2)}\n`,
  )
  return pluginDir
}

describe('factoryPluginPaths', () => {
  it('resolves the Factory root from $FACTORY_DIR, not from the home directory', () => {
    const paths = factoryPluginPaths('/home/me', { platform: 'linux' }, { processEnv: { FACTORY_DIR: '/env/factory' } })
    expect(paths.root).toBe('/env/factory')
  })

  it('falls back to ~/.factory when FACTORY_DIR is not set', () => {
    const paths = factoryPluginPaths('/home/me', { platform: 'linux' }, { processEnv: NO_ENV })
    expect(paths.root).toBe('/home/me/.factory')
  })

  it('keeps the marketplace tree, the receipt and the consent file in KyberDash\'s own state directory', () => {
    const paths = factoryPluginPaths('/home/me', { platform: 'linux' }, { processEnv: NO_ENV })
    expect(paths.pluginDir).toBe('/home/me/.kyberdash/marketplaces/kyberdash')
    expect(paths.marketplaceRoot).toBe('/home/me/.kyberdash/marketplaces/kyberdash')
    expect(paths.receiptFile).toBe('/home/me/.kyberdash/factory-plugin-receipt.json')
    expect(paths.consentFile).toBe('/home/me/.kyberdash/factory-plugin.json')
  })
})

describe('installFactoryPlugin: strict mode', () => {
  it('writes the whole marketplace tree and a receipt listing every file with its digest', () => {
    const f = fixture('factory-install-')
    const result = strict(f)
    expect(result.exitCode).toBe(0)
    expect(result.outcome).toBe('wrote')
    expect(result.disposition).toBe('installed')
    expect(listFiles(f.pluginDir)).toEqual([...OWNED_FILES].sort())

    const receipt = readReceipt(f.receiptFile)
    expect(receipt.version).toBe(1)
    expect(receipt.marketplace).toBe(MARKETPLACE_NAME)
    expect(receipt.plugin).toBe(PLUGIN_IDENTITY)
    expect(receipt.root).toBe(f.pluginDir)
    expect(receipt.files).toHaveLength(OWNED_FILES.length)
    expect(resolvedReceiptPaths(receipt)).toEqual(expectedPaths(f.pluginDir))
    for (const entry of receipt.files) {
      const absolute = isAbsolute(entry.path) ? entry.path : join(receipt.root, entry.path)
      expect(entry.sha256).toMatch(/^[0-9a-f]{64}$/)
      expect(entry.sha256).toBe(sha256Hex(readFileSync(absolute)))
    }
  })

  it('leaves nothing but the receipt, the consent file and the marketplace tree in the state directory', () => {
    const f = fixture('factory-state-')
    strict(f)
    const names = readdirSync(f.stateDir).sort()
    expect(names).toContain('factory-plugin-receipt.json')
    expect(names.filter((name) => !['factory-plugin-receipt.json', 'factory-plugin.json', 'marketplaces'].includes(name))).toEqual([])
  })

  it('refuses a tree holding a file Kyber did not write, and writes nothing', () => {
    const f = fixture('factory-foreign-')
    mkdirSync(join(f.pluginDir, 'kyberdash-factory', 'hooks'), { recursive: true })
    writeFileSync(join(f.pluginDir, 'kyberdash-factory', 'hooks', 'kyberdash-factory.mjs'), '// owner owned\n')
    writeFileSync(join(f.pluginDir, 'kyberdash-factory', 'hooks', 'owner-notes.md'), 'mine\n')
    const before = snapshot(f.pluginDir)

    const result = strict(f)
    expect(result.exitCode).toBe(1)
    expect(result.reason).toBe('conflict')
    expect(result.err.join('\n')).toMatch(/not written by KyberDash/i)
    expect(snapshot(f.pluginDir)).toEqual(before)
    expect(existsSync(f.receiptFile)).toBe(false)
  })

  it('refuses a tree whose files do not match what Kyber writes, and writes nothing', () => {
    const f = fixture('factory-mismatch-')
    mkdirSync(join(f.pluginDir, '.factory-plugin'), { recursive: true })
    mkdirSync(join(f.pluginDir, 'kyberdash-factory', '.factory-plugin'), { recursive: true })
    mkdirSync(join(f.pluginDir, 'kyberdash-factory', 'hooks'), { recursive: true })
    writeFileSync(join(f.pluginDir, '.factory-plugin', 'marketplace.json'), '{"owner":"not kyberdash"}\n')
    writeFileSync(join(f.pluginDir, 'kyberdash-factory', '.factory-plugin', 'plugin.json'), '{"owner":"not kyberdash"}\n')
    writeFileSync(join(f.pluginDir, 'kyberdash-factory', 'hooks', 'hooks.json'), '{"owner":"not kyberdash"}\n')
    writeFileSync(join(f.pluginDir, 'kyberdash-factory', 'hooks', 'kyberdash-factory.mjs'), 'export default {}\n')
    const before = snapshot(f.pluginDir)

    const result = strict(f)
    expect(result.exitCode).toBe(1)
    expect(result.err.join('\n')).toMatch(/refus/i)
    expect(snapshot(f.pluginDir)).toEqual(before)
    expect(existsSync(f.receiptFile)).toBe(false)
  })

  it('refuses a tree Kyber wrote whose bytes no longer match the receipt digest', () => {
    const f = fixture('factory-drift-')
    strict(f)
    const script = join(f.pluginDir, 'kyberdash-factory', 'hooks', 'kyberdash-factory.mjs')
    writeFileSync(script, `${readFileSync(script, 'utf8')}// owner tweak\n`)
    const before = snapshot(f.pluginDir)
    const receiptBefore = readFileSync(f.receiptFile, 'utf8')

    const result = strict(f)
    expect(result.exitCode).toBe(1)
    expect(result.reason).toBe('drift')
    expect(result.err.join('\n')).toMatch(/changed since install|edited since install/i)
    expect(snapshot(f.pluginDir)).toEqual(before)
    expect(readFileSync(f.receiptFile, 'utf8')).toBe(receiptBefore)
  })

  it('is idempotent when the tree already matches the receipt and the harness holds the plugin', () => {
    const f = fixture('factory-idempotent-')
    strict(f)
    const before = snapshot(f.pluginDir)
    const receiptBefore = readFileSync(f.receiptFile, 'utf8')

    const again = strict(f)
    expect(again.exitCode).toBe(0)
    expect(again.outcome).toBe('noop')
    expect(again.disposition).toBe('unchanged')
    expect(again.err).toEqual([])
    expect(snapshot(f.pluginDir)).toEqual(before)
    expect(readFileSync(f.receiptFile, 'utf8')).toBe(receiptBefore)
  })

  it('writes nothing on a dry run and says what it would do', () => {
    const f = fixture('factory-dry-')
    const result = strict(f, { dryRun: true })
    expect(result.exitCode).toBe(0)
    expect(result.outcome).toBe('would-write')
    expect(result.out.join('\n')).toMatch(/would/i)
    expect(existsSync(f.pluginDir)).toBe(false)
    expect(existsSync(f.receiptFile)).toBe(false)
    expect(existsSync(f.consentFile)).toBe(false)
    expect(existsSync(f.stateDir)).toBe(false)
    expect(listFiles(f.root)).toEqual([])
  })

  it('writes nothing on a dry run over an installed tree', () => {
    const f = fixture('factory-dry-installed-')
    strict(f)
    const before = snapshot(f.pluginDir)
    const receiptBefore = readFileSync(f.receiptFile, 'utf8')

    const dry = strict(f, { dryRun: true })
    expect(dry.exitCode).toBe(0)
    expect(snapshot(f.pluginDir)).toEqual(before)
    expect(readFileSync(f.receiptFile, 'utf8')).toBe(receiptBefore)
  })

  it('--if-installed writes nothing when the harness\'s own registry reports no plugin', () => {
    const f = fixture('factory-ifinst-')
    const result = strict(f, { ifInstalled: true })
    expect(result.exitCode).toBe(0)
    expect(result.disposition).toBe('skipped')
    expect(existsSync(f.pluginDir)).toBe(false)
    expect(existsSync(f.stateDir)).toBe(false)
  })

  it('--if-installed over an installed plugin keeps the tree intact', () => {
    const f = fixture('factory-ifinst2-')
    strict(f)
    const before = snapshot(f.pluginDir)

    const again = strict(f, { ifInstalled: true })
    expect(again.exitCode).toBe(0)
    expect(snapshot(f.pluginDir)).toEqual(before)
  })
})

describe('installFactoryPlugin: preserve mode', () => {
  const preserve = (f: Fixture, extra: Record<string, unknown> = {}) =>
    installFactoryPlugin(f.home, undefined, {
      mode: 'preserve',
      processEnv: f.env,
      harnessCli: f.fake.harness,
      ...extra,
    })

  it('writes the marketplace tree and a receipt holding every file digest', () => {
    const f = fixture('factory-preserve-')
    const result = preserve(f)
    expect(result.exitCode).toBe(0)
    expect(listFiles(f.pluginDir)).toEqual([...OWNED_FILES].sort())
    const receipt = readReceipt(f.receiptFile)
    expect(receipt.version).toBe(1)
    expect(receipt.marketplace).toBe(MARKETPLACE_NAME)
    expect(receipt.plugin).toBe(PLUGIN_IDENTITY)
    expect(receipt.root).toBe(f.pluginDir)
    expect(resolvedReceiptPaths(receipt)).toEqual(expectedPaths(f.pluginDir))
  })

  it('keeps a tree Kyber did not write, warns, and exits 0', () => {
    const f = fixture('factory-preserve-foreign-')
    mkdirSync(join(f.pluginDir, 'kyberdash-factory', 'hooks'), { recursive: true })
    writeFileSync(join(f.pluginDir, 'kyberdash-factory', 'hooks', 'kyberdash-factory.mjs'), '// owner owned\n')
    const before = snapshot(f.pluginDir)

    const result = preserve(f)
    expect(result.exitCode).toBe(0)
    expect(result.outcome).toBe('kept')
    expect(result.disposition).toBe('kept-foreign')
    expect(result.err.length).toBeGreaterThan(0)
    expect(result.err.join('\n')).toMatch(/not written by KyberDash/i)
    expect(snapshot(f.pluginDir)).toEqual(before)
    expect(existsSync(f.receiptFile)).toBe(false)
  })

  it('keeps a tree Kyber wrote that the owner edited, warns, and exits 0', () => {
    const f = fixture('factory-preserve-edited-')
    installFactoryPlugin(f.home, undefined, { mode: 'strict', processEnv: f.env, harnessCli: f.fake.harness })
    const script = join(f.pluginDir, 'kyberdash-factory', 'hooks', 'kyberdash-factory.mjs')
    writeFileSync(script, `${readFileSync(script, 'utf8')}// owner tweak\n`)
    const before = snapshot(f.pluginDir)
    const receiptBefore = readFileSync(f.receiptFile, 'utf8')

    const result = preserve(f)
    expect(result.exitCode).toBe(0)
    expect(result.outcome).toBe('kept')
    expect(result.disposition).toBe('kept-edited')
    expect(result.err.length).toBeGreaterThan(0)
    expect(result.err.join('\n')).toMatch(/changed since install|edited since install/i)
    expect(snapshot(f.pluginDir)).toEqual(before)
    expect(readFileSync(f.receiptFile, 'utf8')).toBe(receiptBefore)
  })
})

describe('uninstallFactoryPlugin', () => {
  const uninstall = (f: Fixture, extra: Record<string, unknown> = {}) =>
    uninstallFactoryPlugin(f.home, undefined, {
      processEnv: f.env,
      harnessCli: f.fake.harness,
      ...extra,
    })

  it('removes the tree and the receipt while every digest still matches', () => {
    const f = fixture('factory-uninstall-')
    installFactoryPlugin(f.home, undefined, { mode: 'strict', processEnv: f.env, harnessCli: f.fake.harness })
    const result = uninstall(f)
    expect(result.exitCode).toBe(0)
    expect(result.outcome).toBe('restored')
    expect(existsSync(f.pluginDir)).toBe(false)
    expect(existsSync(f.receiptFile)).toBe(false)
  })

  it('leaves an edited tree in place and exits 1', () => {
    const f = fixture('factory-uninstall-edited-')
    installFactoryPlugin(f.home, undefined, { mode: 'strict', processEnv: f.env, harnessCli: f.fake.harness })
    const script = join(f.pluginDir, 'kyberdash-factory', 'hooks', 'kyberdash-factory.mjs')
    writeFileSync(script, `${readFileSync(script, 'utf8')}// owner tweak\n`)

    const result = uninstall(f)
    expect(result.exitCode).toBe(1)
    expect(result.reason).toBe('drift')
    expect(listFiles(f.pluginDir)).toEqual([...OWNED_FILES].sort())
    expect(existsSync(f.receiptFile)).toBe(true)
  })

  it('never removes a file Kyber did not write', () => {
    const f = fixture('factory-uninstall-foreign-')
    installFactoryPlugin(f.home, undefined, { mode: 'strict', processEnv: f.env, harnessCli: f.fake.harness })
    const ownerFile = join(f.pluginDir, 'kyberdash-factory', 'hooks', 'owner-notes.md')
    writeFileSync(ownerFile, 'mine\n')

    uninstall(f)
    expect(existsSync(ownerFile)).toBe(true)
    expect(readFileSync(ownerFile, 'utf8')).toBe('mine\n')
  })

  it('is a noop when nothing is installed, and a dry run removes nothing', () => {
    const f = fixture('factory-uninstall-none-')
    expect(uninstall(f).exitCode).toBe(0)

    installFactoryPlugin(f.home, undefined, { mode: 'strict', processEnv: f.env, harnessCli: f.fake.harness })
    const dry = uninstall(f, { dryRun: true })
    expect(dry.exitCode).toBe(0)
    expect(dry.outcome).toBe('would-restore')
    expect(existsSync(f.pluginDir)).toBe(true)
  })
})

describe('the owner\'s own Factory files are never written', () => {
  const OWNER_SETTINGS = '{\n  "theme": "dark"\n}\n'
  const OWNER_HOOKS = '{\n  "hooks": { "beforePrompt": ["./my-hook.sh"] }\n}\n'
  const PROJECT_HOOKS = '{\n  "hooks": { "afterTool": ["./project-hook.sh"] }\n}\n'

  /** The owner's settings, the owner's hooks and the project's hooks, byte for byte. */
  function ownerFiles(f: Fixture, project: string): () => void {
    return () => {
      expect(readFileSync(join(f.root, 'settings.json'), 'utf8')).toBe(OWNER_SETTINGS)
      expect(readFileSync(join(f.root, 'hooks.json'), 'utf8')).toBe(OWNER_HOOKS)
      expect(readFileSync(join(project, '.factory', 'hooks.json'), 'utf8')).toBe(PROJECT_HOOKS)
    }
  }

  it('leaves the settings, the hooks and the project hooks byte-identical through install, enable, disable and uninstall', () => {
    const f = fixture('factory-owner-')
    const project = temp('factory-owner-project-')
    mkdirSync(join(project, '.factory'), { recursive: true })
    writeFileSync(join(f.root, 'settings.json'), OWNER_SETTINGS)
    writeFileSync(join(f.root, 'hooks.json'), OWNER_HOOKS)
    writeFileSync(join(project, '.factory', 'hooks.json'), PROJECT_HOOKS)
    // The project hooks file is found through the working directory, so the installer is
    // given a working directory that holds one.
    vi.spyOn(process, 'cwd').mockReturnValue(project)
    const check = ownerFiles(f, project)

    // enable: the strict install.
    installFactoryPlugin(f.home, undefined, { mode: 'strict', processEnv: f.env, harnessCli: f.fake.harness })
    check()
    // disable: the uninstall.
    uninstallFactoryPlugin(f.home, undefined, { processEnv: f.env, harnessCli: f.fake.harness })
    check()
    // the preserve path (squad / kyber-weave update).
    installFactoryPlugin(f.home, undefined, { mode: 'preserve', processEnv: f.env, harnessCli: f.fake.harness })
    check()
    uninstallFactoryPlugin(f.home, undefined, { processEnv: f.env, harnessCli: f.fake.harness })
    check()
  })

  it('writes nothing into the Factory root at all', () => {
    const f = fixture('factory-owner-root-')
    writeFileSync(join(f.root, 'settings.json'), OWNER_SETTINGS)
    writeFileSync(join(f.root, 'hooks.json'), OWNER_HOOKS)

    installFactoryPlugin(f.home, undefined, { mode: 'strict', processEnv: f.env, harnessCli: f.fake.harness })
    expect(listFiles(f.root)).toEqual(['hooks.json', 'settings.json'])
    // The ONE thing KyberDash removes from the Factory root is the tree the PREVIOUS route
    // wrote there, and only when the receipt that route wrote proves it is ours; the cases
    // above pin that, and they are the only deletion under the root.
  })
})

describe('the installer never writes through a symlink', () => {
  it.skipIf(POSIX_ONLY)('refuses a symlinked file in the tree and leaves its target untouched', () => {
    const f = fixture('factory-symlink-file-')
    // A real tree first, so the only anomaly is the link: the tree holds the bytes the
    // installer would write, so nothing but the symlink can make it refuse.
    installFactoryPlugin(f.home, undefined, { mode: 'strict', processEnv: f.env, harnessCli: f.fake.harness })
    const script = join(f.pluginDir, 'kyberdash-factory', 'hooks', 'kyberdash-factory.mjs')
    const precious = join(temp('factory-symlink-target-'), 'precious.txt')
    writeFileSync(precious, readFileSync(script, 'utf8'))
    rmSync(script)
    symlinkSync(precious, script)
    const mtimeBefore = statSync(precious).mtimeMs

    const result = installFactoryPlugin(f.home, undefined, { mode: 'strict', processEnv: f.env, harnessCli: f.fake.harness })
    expect(result.exitCode).toBe(1)
    // The link was not replaced by a regular file, and its target was not written through.
    expect(lstatSync(script).isSymbolicLink()).toBe(true)
    expect(statSync(precious).mtimeMs).toBe(mtimeBefore)
  })

  it.skipIf(POSIX_ONLY)('refuses a marketplace tree that is a symlink and leaves its target untouched', () => {
    const f = fixture('factory-symlink-dir-')
    const preciousDir = temp('factory-symlink-dir-target-')
    writeFileSync(join(preciousDir, 'precious.txt'), 'PRECIOUS')
    mkdirSync(join(f.stateDir, 'marketplaces'), { recursive: true })
    symlinkSync(preciousDir, f.pluginDir)

    const result = installFactoryPlugin(f.home, undefined, { mode: 'strict', processEnv: f.env, harnessCli: f.fake.harness })
    expect(result.exitCode).toBe(1)
    expect(lstatSync(f.pluginDir).isSymbolicLink()).toBe(true)
    expect(readFileSync(join(preciousDir, 'precious.txt'), 'utf8')).toBe('PRECIOUS')
    expect(existsSync(f.receiptFile)).toBe(false)
  })

  it.skipIf(POSIX_ONLY)('keeps a symlinked path in preserve mode and writes nothing through it', () => {
    const f = fixture('factory-symlink-preserve-')
    const precious = join(temp('factory-symlink-preserve-target-'), 'precious.txt')
    writeFileSync(precious, 'PRECIOUS')
    mkdirSync(join(f.pluginDir, 'kyberdash-factory', 'hooks'), { recursive: true })
    symlinkSync(precious, join(f.pluginDir, 'kyberdash-factory', 'hooks', 'kyberdash-factory.mjs'))

    const result = installFactoryPlugin(f.home, undefined, { mode: 'preserve', processEnv: f.env, harnessCli: f.fake.harness })
    expect(result.exitCode).toBe(0)
    expect(result.err.length).toBeGreaterThan(0)
    expect(readFileSync(precious, 'utf8')).toBe('PRECIOUS')
    expect(existsSync(f.receiptFile)).toBe(false)
  })
})

describe('writes are atomic and the receipt lands last', () => {
  it('leaves no temporary file behind and every file is complete', () => {
    const f = fixture('factory-atomic-ok-')
    installFactoryPlugin(f.home, undefined, { mode: 'strict', processEnv: f.env, harnessCli: f.fake.harness })
    expect(listFiles(f.pluginDir)).toEqual([...OWNED_FILES].sort())
    expect(() => JSON.parse(readFileSync(join(f.pluginDir, '.factory-plugin', 'marketplace.json'), 'utf8'))).not.toThrow()
    expect(() => JSON.parse(readFileSync(join(f.pluginDir, 'kyberdash-factory', '.factory-plugin', 'plugin.json'), 'utf8'))).not.toThrow()
    expect(() => JSON.parse(readFileSync(join(f.pluginDir, 'kyberdash-factory', 'hooks', 'hooks.json'), 'utf8'))).not.toThrow()
    expect(readFileSync(join(f.pluginDir, 'kyberdash-factory', 'hooks', 'kyberdash-factory.mjs'), 'utf8').length).toBeGreaterThan(0)
  })

  it.skipIf(process.getuid?.() === 0)('writes no receipt when a file of the tree could not land', () => {
    const f = fixture('factory-atomic-fail-')
    // The tree cannot be written in full: one of the directories cannot be written into.
    const blocked = join(f.pluginDir, 'kyberdash-factory', 'hooks')
    mkdirSync(blocked, { recursive: true })
    chmodSync(blocked, 0o500)
    try {
      let exitCode = 0
      try {
        exitCode = installFactoryPlugin(f.home, undefined, { mode: 'strict', processEnv: f.env, harnessCli: f.fake.harness }).exitCode
      } catch {
        // A failed write may be reported by throwing, as the OpenCode installer does.
        exitCode = 1
      }
      expect(exitCode).not.toBe(0)
      expect(existsSync(f.receiptFile)).toBe(false)
      expect(listFiles(f.pluginDir).filter((relative) => relative.endsWith('.tmp'))).toEqual([])
    } finally {
      chmodSync(blocked, 0o700)
    }
  })
})

describe('the tree the previous route wrote into the Factory root', () => {
  const uninstall = (f: Fixture, extra: Record<string, unknown> = {}) =>
    uninstallFactoryPlugin(f.home, undefined, {
      processEnv: f.env,
      harnessCli: f.fake.harness,
      ...extra,
    })

  it('the uninstall removes it when the receipt proves the tree is KyberDash\'s own', () => {
    const f = fixture('factory-legacy-')
    const legacyDir = legacyInstall(f)

    const result = uninstall(f)

    expect(result.exitCode).toBe(0)
    expect(existsSync(legacyDir)).toBe(false)
    expect(result.out.join('\n')).toContain(legacyDir)
  })

  it('the uninstall leaves it in place and names the exact command when the receipt does not prove it', () => {
    const f = fixture('factory-legacy-edited-')
    const legacyDir = legacyInstall(f)
    // The owner edited a file, so the tree is no longer the one KyberDash wrote.
    writeFileSync(join(legacyDir, 'hooks', 'hooks.json'), '// owner edit\n')

    const result = uninstall(f)

    expect(result.exitCode).toBe(0)
    expect(existsSync(legacyDir)).toBe(true)
    expect(result.err.join('\n')).toContain(`rm -rf "${legacyDir}"`)
  })

  it('the uninstall reads a receipt in the previous route\'s shape without failing', () => {
    const f = fixture('factory-legacy-receipt-')
    const legacyDir = legacyInstall(f)

    const result = uninstall(f)

    expect(result.exitCode).toBe(0)
    expect(result.outcome).toBe('restored')
    expect(existsSync(legacyDir)).toBe(false)
  })

  it('the install removes it before it writes its own receipt, which would otherwise lose the proof', () => {
    const f = fixture('factory-legacy-install-')
    const legacyDir = legacyInstall(f)

    const result = installFactoryPlugin(f.home, undefined, {
      mode: 'strict',
      processEnv: f.env,
      harnessCli: f.fake.harness,
    })

    expect(result.exitCode).toBe(0)
    expect(existsSync(legacyDir)).toBe(false)
    // The receipt the install leaves is the marketplace one, not the previous route's.
    expect(readReceipt(f.receiptFile).marketplace).toBe(MARKETPLACE_NAME)
  })
})

describe("the plugin's hooks file", () => {
  /** The published hooks file, read as it lands in the tree. */
  function publishedHooks(f: Fixture): Record<string, unknown> {
    installFactoryPlugin(f.home, undefined, {
      mode: 'strict',
      processEnv: f.env,
      harnessCli: f.fake.harness,
    })
    const bytes = readFileSync(join(f.pluginDir, 'kyberdash-factory', 'hooks', 'hooks.json'), 'utf8')
    return JSON.parse(bytes) as Record<string, unknown>
  }

  it('is keyed directly by event name, with no top-level hooks wrapper', () => {
    const f = fixture('factory-hooks-shape-')
    const hooks = publishedHooks(f)

    // Factory documents a plugin's standalone hooks.json as keyed by event name; the
    // `hooks` wrapper is the idiom for the `hooks` key inside settings.json.
    expect(Object.keys(hooks)).not.toContain('hooks')
    expect(Object.keys(hooks)).toContain('SessionStart')
    for (const key of Object.keys(hooks)) {
      expect(key).toMatch(/^[A-Z][A-Za-z]+$/)
    }
  })

  it('registers only the events that produce a span', () => {
    const f = fixture('factory-hooks-events-')
    const hooks = publishedHooks(f)

    // `SubagentStop` produces no span: its documented payload carries no child session id,
    // so a registration would cost a process per subagent stop for no output.
    expect(Object.keys(hooks)).toEqual([
      'SessionStart',
      'UserPromptSubmit',
      'PreToolUse',
      'PostToolUse',
      'Stop',
      'SessionEnd',
    ])
  })
})

describe('the upgrade path', () => {
  /** The file the bundle's bytes are read from. */
  const SCRIPT = join('kyberdash-factory', 'hooks', 'kyberdash-factory.mjs')

  /**
   * Put the tree back to the bundle KyberDash wrote BEFORE, and keep the receipt proving
   * it: the bytes no longer match the current bundle, and nothing proves the owner edited
   * them. That is an upgrade, not an owner edit and not `unchanged`.
   */
  function supersede(f: Fixture): void {
    const script = join(f.pluginDir, SCRIPT)
    writeFileSync(script, '// the bundle KyberDash wrote before\n')
    const receipt = readReceipt(f.receiptFile)
    for (const entry of receipt.files) {
      const absolute = isAbsolute(entry.path) ? entry.path : join(receipt.root, entry.path)
      entry.sha256 = sha256Hex(readFileSync(absolute))
    }
    writeFileSync(f.receiptFile, `${JSON.stringify(receipt, null, 2)}\n`)
  }

  it('rewrites the tree with the current bundle and refreshes the harness\'s cached copy', () => {
    const f = fixture('factory-upgrade-')
    strict(f)
    supersede(f)
    const before = fakeCommandLines(f.fake).length

    const result = strict(f)

    expect(result.exitCode).toBe(0)
    expect(result.disposition).toBe('upgraded')
    expect(result.out.join('\n')).toContain(`factory plugin: upgraded ${f.pluginDir}`)
    // The tree holds the CURRENT bundle again.
    expect(readFileSync(join(f.pluginDir, SCRIPT), 'utf8')).not.toContain('wrote before')
    // The receipt describes the tree that is there now.
    const receipt = readReceipt(f.receiptFile)
    for (const entry of receipt.files) {
      const absolute = isAbsolute(entry.path) ? entry.path : join(receipt.root, entry.path)
      expect(entry.sha256).toBe(sha256Hex(readFileSync(absolute)))
    }
    // The harness's CACHED COPY is refreshed with the command that re-copies it. The
    // harness refuses `plugin install` for a plugin that is already installed, so the
    // update command is the only one that can do it.
    const commands = fakeCommandLines(f.fake).slice(before)
    expect(commands).toContain(`plugin update ${PLUGIN_IDENTITY} --scope user`)
    expect(commands).not.toContain(`plugin install ${PLUGIN_IDENTITY} --scope user`)
  })

  it('never runs a harness command when the tree is current and the harness holds the plugin', () => {
    const f = fixture('factory-current-')
    strict(f)
    const before = fakeCommandLines(f.fake).length

    const result = strict(f)

    expect(result.exitCode).toBe(0)
    expect(result.disposition).toBe('unchanged')
    // A routine update that finds everything current mutates nothing. The registry is
    // READ, because the harness's own view is the install's evidence.
    const commands = fakeCommandLines(f.fake).slice(before)
    expect(commands.filter((line) => !line.startsWith('plugin marketplace list') && !line.startsWith('plugin list'))).toEqual([])
  })

  it('says what a dry run would do over a superseded tree', () => {
    const f = fixture('factory-upgrade-dry-')
    strict(f)
    supersede(f)

    const result = strict(f, { dryRun: true })

    expect(result.exitCode).toBe(0)
    expect(result.out.join('\n')).toContain(`factory plugin: would-upgrade ${f.pluginDir}`)
  })

  it('writes the receipt that proves a current tree it cannot prove, so the next upgrade is possible', () => {
    const f = fixture('factory-receipt-repair-')
    strict(f)
    // The receipt is gone. The tree holds exactly what KyberDash writes, but nothing
    // proves KyberDash wrote it, and without the receipt the NEXT bundle change would
    // make the tree foreign — refused, forever.
    rmSync(f.receiptFile, { force: true })

    const result = strict(f)

    expect(result.exitCode).toBe(0)
    expect(existsSync(f.receiptFile)).toBe(true)
    expect(readReceipt(f.receiptFile).root).toBe(f.pluginDir)
    // And the tree is still upgradeable: a later bundle is recognised as an upgrade.
    supersede(f)
    expect(strict(f).disposition).toBe('upgraded')
  })

  it('leaves an owner-edited tree alone: that is drift, not an upgrade', () => {
    const f = fixture('factory-upgrade-edited-')
    strict(f)
    // The bytes changed and the receipt still records the OLD digest, so the owner edited
    // the tree and it is not KyberDash's to overwrite.
    writeFileSync(join(f.pluginDir, SCRIPT), '// the owner edited this\n')
    const before = fakeCommandLines(f.fake).length

    const result = strict(f)

    expect(result.exitCode).toBe(1)
    expect(result.reason).toBe('drift')
    expect(fakeCommandLines(f.fake).slice(before)).toEqual([])
  })

  it('reports a superseded tree as an older version, not as an owner edit', () => {
    const f = fixture('factory-status-upgrade-')
    strict(f)
    supersede(f)

    const lines = describeFactoryPluginStatus(f.home, undefined, { processEnv: f.env })

    // The tree is KyberDash's own, the owner never touched it, and it holds the bundle
    // KyberDash wrote before: that is an OLD VERSION, not an owner edit and not a tree
    // KyberDash did not write. The wording says so, so it cannot drift back.
    expect(lines).toContain('plugin: installed (an older version than this build)')
    expect(lines).not.toContain('plugin: modified since install or not written by KyberDash')
  })
})

describe('the tree is gone and the harness already holds the plugin', () => {
  /** The commands that CHANGE the harness's own state; the list commands only read it. */
  const mutating = (fake: FakeHarness): string[] =>
    fakeCommandLines(fake).filter(
      (line) => !line.startsWith('plugin marketplace list') && !line.startsWith('plugin list'),
    )

  /**
   * The harness holds KyberDash's own marketplace, registered from KyberDash's own tree,
   * and the plugin is installed and enabled — but the tree itself is gone.
   */
  function harnessHoldsThePlugin(f: Fixture): void {
    f.fake.marketplaces.add(MARKETPLACE_NAME)
    f.fake.sources.set(MARKETPLACE_NAME, f.pluginDir)
    f.fake.plugins.add(PLUGIN_IDENTITY)
    f.fake.statuses.set(PLUGIN_IDENTITY, 'Enabled')
  }

  it('refreshes the harness\'s cached copy, because the tree it runs was written again', () => {
    const f = fixture('factory-absent-refresh-')
    harnessHoldsThePlugin(f)
    expect(existsSync(f.pluginDir)).toBe(false)

    const result = strict(f)

    expect(result.exitCode).toBe(0)
    // The tree was written again, so the copy the harness RUNS is stale until the harness
    // re-copies it. Without `plugin update` the harness keeps running the bundle it
    // cached while KyberDash reports a successful install.
    expect(mutating(f.fake)).toContain(`plugin update ${PLUGIN_IDENTITY} --scope user`)
    expect(result.disposition).toMatch(/^(installed|upgraded)$/)
    expect(result.outcome).toBe('wrote')
    expect(listFiles(f.pluginDir)).toEqual([...OWNED_FILES].sort())
    expect(existsSync(f.receiptFile)).toBe(true)
  })

  it('refreshes the harness\'s cached copy under --if-installed too', () => {
    const f = fixture('factory-absent-refresh-ifinst-')
    harnessHoldsThePlugin(f)
    expect(existsSync(f.pluginDir)).toBe(false)

    const result = strict(f, { ifInstalled: true })

    expect(result.exitCode).toBe(0)
    // `--if-installed` is the update path: the harness already holds the plugin, so the
    // install refreshes the copy the harness runs.
    expect(mutating(f.fake)).toContain(`plugin update ${PLUGIN_IDENTITY} --scope user`)
    expect(result.disposition).toMatch(/^(installed|upgraded)$/)
    expect(result.outcome).toBe('wrote')
    expect(listFiles(f.pluginDir)).toEqual([...OWNED_FILES].sort())
    expect(existsSync(f.receiptFile)).toBe(true)
  })
})

describe('the plugin KyberDash installed itself', () => {
  /**
   * The receipt KyberDash wrote: it names the marketplace, the plugin identity and the
   * tree, with a digest per owned file. The tree itself is gone; the receipt is the proof
   * that the tree was KyberDash's own.
   */
  function writeOwnReceipt(f: Fixture): void {
    mkdirSync(f.stateDir, { recursive: true })
    const files = FACTORY_MARKETPLACE_FILES.map((relative) => ({
      path: join(f.pluginDir, ...relative.split('/')),
      sha256: sha256Hex(FACTORY_MARKETPLACE_CONTENT[relative] ?? ''),
    }))
    writeFileSync(
      f.receiptFile,
      `${JSON.stringify(
        {
          version: 1,
          marketplace: MARKETPLACE_NAME,
          plugin: PLUGIN_IDENTITY,
          root: f.pluginDir,
          files,
        },
        null,
        2,
      )}\n`,
    )
  }

  it('is not a conflict when the receipt names the marketplace and the plugin identity', () => {
    const f = fixture('factory-own-plugin-')
    writeOwnReceipt(f)
    // The harness holds the plugin, and no marketplace named `kyberdash` is registered:
    // the registration went with the tree.
    f.fake.plugins.add(PLUGIN_IDENTITY)
    f.fake.statuses.set(PLUGIN_IDENTITY, 'Enabled')

    const result = strict(f)

    // The plugin KyberDash installed is KyberDash's own: the receipt names the
    // marketplace it came from and the identity it carries.
    expect(result.reason).not.toBe('conflict')
    expect(result.exitCode).toBe(0)
    expect(result.err.join('\n')).not.toContain('droid plugin uninstall')
    const commands = fakeCommandLines(f.fake).filter(
      (line) => !line.startsWith('plugin marketplace list') && !line.startsWith('plugin list'),
    )
    // The marketplace is registered FIRST, because `plugin update` fails without a
    // registration, and the plugin's cached copy is refreshed after it.
    expect(commands).toEqual([
      `plugin marketplace add ${f.pluginDir}`,
      `plugin update ${PLUGIN_IDENTITY} --scope user`,
    ])
    expect(listFiles(f.pluginDir)).toEqual([...OWNED_FILES].sort())
  })
})

describe('a name the harness holds that KyberDash does not own', () => {
  it('refuses with the conflict reason, not as an unreadable state', () => {
    const f = fixture('factory-conflict-reason-')
    // A marketplace named `kyberdash` registered from somewhere else: the harness resolves
    // the plugin identity through the registered name, so installing against it would
    // install somebody else's payload.
    f.fake.marketplaces.add(MARKETPLACE_NAME)

    const result = strict(f)

    expect(result.exitCode).toBe(1)
    expect(result.reason).toBe('conflict')
    expect(result.err.join('\n')).toContain('droid plugin marketplace remove kyberdash')
    expect(existsSync(f.pluginDir)).toBe(false)
  })

  it('keeps the name with a warning in preserve mode, and writes nothing', () => {
    const f = fixture('factory-conflict-preserve-')
    f.fake.marketplaces.add(MARKETPLACE_NAME)

    const result = installFactoryPlugin(f.home, undefined, {
      mode: 'preserve',
      processEnv: f.env,
      harnessCli: f.fake.harness,
    })

    expect(result.exitCode).toBe(0)
    expect(result.disposition).toBe('kept-conflict')
    expect(existsSync(f.pluginDir)).toBe(false)
  })

  it('never runs a harness command against a name it does not own', () => {
    const f = fixture('factory-conflict-command-')
    f.fake.marketplaces.add(MARKETPLACE_NAME)
    const before = fakeCommandLines(f.fake).length

    strict(f)

    const commands = fakeCommandLines(f.fake).slice(before)
    expect(commands).not.toContain(`plugin marketplace remove ${MARKETPLACE_NAME}`)
    expect(commands).not.toContain(`plugin install ${PLUGIN_IDENTITY} --scope user`)
  })

  it('the uninstall removes KyberDash\'s own tree and leaves the name it does not own', () => {
    const f = fixture('factory-conflict-uninstall-')
    // A tree KyberDash wrote, and then the marketplace of the same name taken over by
    // somebody else: the harness's SOURCE column says the registration is not ours.
    strict(f)
    f.fake.sources.set(MARKETPLACE_NAME, '/somewhere/else/kyberdash')
    const before = fakeCommandLines(f.fake).length

    const result = uninstallFactoryPlugin(f.home, undefined, {
      processEnv: f.env,
      harnessCli: f.fake.harness,
    })

    expect(result.exitCode).toBe(0)
    // The harness's commands are not run against the name that is not ours. The registry is
    // READ, because the harness's own view is the install's evidence.
    const commands = fakeCommandLines(f.fake).slice(before)
    expect(commands).not.toContain(`plugin marketplace remove ${MARKETPLACE_NAME}`)
    expect(commands).not.toContain(`plugin uninstall ${PLUGIN_IDENTITY} --scope user`)
    // KyberDash's own tree is KyberDash's, and the receipt proves it.
    expect(existsSync(f.pluginDir)).toBe(false)
    expect(existsSync(f.receiptFile)).toBe(false)
    expect(result.out.join('\n')).toContain('left alone')
  })
})

describe('the residue the harness\'s own teardown leaves', () => {
  it('reports what the teardown did not remove, with the exact command that clears it', () => {
    const f = fixture('factory-residue-')
    strict(f)
    // The harness's uninstall leaves the marketplace registration behind.
    f.fake.harness.run = ((original: FakeHarness['harness']['run']) => (argv: readonly string[]) => {
      if (argv[1] === 'marketplace' && argv[2] === 'remove') {
        return { exitCode: 0, stdout: '', stderr: '' }
      }
      return original(argv)
    })(f.fake.harness.run)

    const result = uninstallFactoryPlugin(f.home, undefined, {
      processEnv: f.env,
      harnessCli: f.fake.harness,
    })

    expect(result.exitCode).toBe(0)
    const out = result.out.join('\n')
    expect(out).toContain('factory residue:')
    expect(out).toContain('droid plugin marketplace remove kyberdash')
  })

  it('reports nothing when the harness\'s own teardown cleaned up after itself', () => {
    const f = fixture('factory-residue-none-')
    strict(f)

    const result = uninstallFactoryPlugin(f.home, undefined, {
      processEnv: f.env,
      harnessCli: f.fake.harness,
    })

    expect(result.exitCode).toBe(0)
    expect(result.out.join('\n')).not.toContain('factory residue:')
  })
})
