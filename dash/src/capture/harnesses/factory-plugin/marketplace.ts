// The Factory (Droid) marketplace route (plan D43, D44, D47).
//
// Factory loads a plugin from a MARKETPLACE, not from a directory under its own root. A
// marketplace is a directory that holds `.factory-plugin/marketplace.json`, and the
// manifest's `plugins[]` entries name a plugin and the RELATIVE path of its payload. The
// plugin's identity is `<plugin>@<marketplace>`, and the marketplace's REGISTERED name is
// the basename of the directory that was added — not the `name` field in the manifest — so
// the tree's directory name is load-bearing.
//
// KyberDash owns exactly one tree, under its own state directory:
//
//   ~/.kyberdash/marketplaces/kyberdash/
//   ├── .factory-plugin/marketplace.json
//   └── kyberdash-factory/
//       ├── .factory-plugin/plugin.json
//       └── hooks/
//           ├── hooks.json
//           └── kyberdash-factory.mjs
//
// and installs it through the harness's OWN commands, in this order:
//
//   droid plugin marketplace add <marketplace root>
//   droid plugin install kyberdash-factory@kyberdash --scope user
//
// The uninstall runs the harness's own uninstall and marketplace removal FIRST, and only
// then removes the Kyber-owned tree and the receipt.
//
// The harness's CLI is the only interface to the harness's state (D43, D48): the owner's
// `~/.factory/settings.json` is never opened, and the harness's own plugin cache and its
// `installed_plugins` record are the harness's files, never KyberDash's.

import { spawnSync } from 'node:child_process'
import { randomBytes } from 'node:crypto'
import {
  accessSync,
  closeSync,
  constants,
  fsyncSync,
  mkdirSync,
  openSync,
  renameSync,
  rmSync,
  writeFileSync,
} from 'node:fs'
import { delimiter, dirname, join, resolve } from 'node:path'

import { FACTORY_PLUGIN_SOURCE } from './generated/plugin-source.js'
import type { HarnessCli } from '../registry.js'

// ── The names the harness knows the unit by ───────────────────────────────────────────────────

/** The marketplace KyberDash registers. The tree's directory basename IS the registered name. */
export const FACTORY_MARKETPLACE_NAME = 'kyberdash'

/** The plugin's directory inside the marketplace tree. */
export const FACTORY_PLUGIN_NAME = 'kyberdash-factory'

/** The plugin's identity in the harness: `<plugin>@<marketplace>`. */
export const FACTORY_PLUGIN_IDENTITY = `${FACTORY_PLUGIN_NAME}@${FACTORY_MARKETPLACE_NAME}`

/** The marketplace manifest, relative to the marketplace tree root. */
export const FACTORY_MARKETPLACE_MANIFEST = '.factory-plugin/marketplace.json'

/** The plugin manifest, relative to the marketplace tree root. */
export const FACTORY_PLUGIN_MANIFEST = `${FACTORY_PLUGIN_NAME}/.factory-plugin/plugin.json`

/** The plugin's hooks file, relative to the marketplace tree root. */
export const FACTORY_HOOKS_FILE = `${FACTORY_PLUGIN_NAME}/hooks/hooks.json`

/** The plugin's hook script, relative to the marketplace tree root. */
export const FACTORY_SCRIPT_FILE = `${FACTORY_PLUGIN_NAME}/hooks/kyberdash-factory.mjs`

/** Every file Kyber owns in the marketplace tree, relative to the tree root. */
export const FACTORY_MARKETPLACE_FILES = [
  FACTORY_MARKETPLACE_MANIFEST,
  FACTORY_PLUGIN_MANIFEST,
  FACTORY_HOOKS_FILE,
  FACTORY_SCRIPT_FILE,
] as const

// ── The bytes of the tree ─────────────────────────────────────────────────────────────────────

/**
 * The hook events the plugin registers on. `PreCompact` is not one of them: the contract
 * names no span for a compaction, so the hook would only cost a process per compaction.
 */
const HOOK_EVENTS = [
  'SessionStart',
  'UserPromptSubmit',
  'PreToolUse',
  'PostToolUse',
  'Stop',
  'SubagentStop',
  'SessionEnd',
] as const

/**
 * The command the hooks file runs. Factory expands `${DROID_PLUGIN_ROOT}` to the plugin root
 * before the command runs, so the script is found wherever the plugin was installed. The
 * script carries no shebang (its first line is the ownership marker), so the command names
 * the interpreter.
 */
const HOOK_COMMAND = `node \${DROID_PLUGIN_ROOT}/${FACTORY_SCRIPT_FILE.split('/').slice(1).join('/')}`

/** The manifest Factory reads from the plugin root. */
const PLUGIN_MANIFEST_SOURCE = `${JSON.stringify(
  {
    name: FACTORY_PLUGIN_NAME,
    version: '1.0.0',
    description:
      'KyberDash capture plugin for Factory (Droid): reports session, turn and tool spans to the local KyberDash receiver.',
  },
  null,
  2,
)}\n`

/** The hooks file: one command per hook event, in the shape Factory's plugin loader reads. */
const HOOKS_SOURCE = `${JSON.stringify(
  {
    hooks: Object.fromEntries(
      HOOK_EVENTS.map((event) => [event, [{ hooks: [{ type: 'command', command: HOOK_COMMAND }] }]]),
    ),
  },
  null,
  2,
)}\n`

/**
 * The marketplace manifest. The `source` is RELATIVE: Factory rejects an absolute source
 * and a source that escapes the marketplace directory, so the payload must live inside the
 * tree, and the tree's directory name is what the harness registers.
 */
const MARKETPLACE_MANIFEST_SOURCE = `${JSON.stringify(
  {
    name: FACTORY_MARKETPLACE_NAME,
    description: 'KyberDash capture plugins for Factory (Droid).',
    plugins: [{ name: FACTORY_PLUGIN_NAME, source: `./${FACTORY_PLUGIN_NAME}` }],
  },
  null,
  2,
)}\n`

/** The bytes of every file in the marketplace tree, keyed by its path relative to the root. */
export const FACTORY_MARKETPLACE_CONTENT: Record<string, string> = {
  [FACTORY_MARKETPLACE_MANIFEST]: MARKETPLACE_MANIFEST_SOURCE,
  [FACTORY_PLUGIN_MANIFEST]: PLUGIN_MANIFEST_SOURCE,
  [FACTORY_HOOKS_FILE]: HOOKS_SOURCE,
  [FACTORY_SCRIPT_FILE]: FACTORY_PLUGIN_SOURCE,
}

// ── The tree writer ───────────────────────────────────────────────────────────────────────────

/**
 * Write the whole marketplace tree, or nothing of it.
 *
 * Every file is staged as a temporary sibling first and renamed into place only once all of
 * them are staged, so a tree that cannot be written in full leaves no partial tree behind.
 * The receipt is written by the caller, after this returned.
 */
export function writeMarketplaceTree(root: string): void {
  const staged: { temp: string; target: string }[] = []
  try {
    for (const relative of FACTORY_MARKETPLACE_FILES) {
      const target = join(root, ...relative.split('/'))
      mkdirSync(dirname(target), { recursive: true })
      const temp = `${target}.${randomBytes(8).toString('hex')}.tmp`
      const fd = openSync(temp, 'wx', 0o644)
      try {
        writeFileSync(fd, FACTORY_MARKETPLACE_CONTENT[relative] ?? '')
        fsyncSync(fd)
      } finally {
        closeSync(fd)
      }
      staged.push({ temp, target })
    }
  } catch (error) {
    for (const entry of staged) rmSync(entry.temp, { force: true })
    throw error
  }
  for (const entry of staged) renameSync(entry.temp, entry.target)
}

// ── The harness command driver ───────────────────────────────────────────────────────────────

/**
 * The harness's CLI, as KyberDash reaches it. A test injects a fake that records the argv
 * and answers with a canned result, so no test needs a real `droid` on the machine.
 */
export type FactoryHarnessCli = HarnessCli

/** One harness command's outcome. */
export type DroidCommandResult = {
  /** The harness's exit code. A command that could not run reports a non-zero code. */
  exitCode: number
  /** The harness's standard output. */
  stdout: string
  /** The harness's standard error. */
  stderr: string
}

/** The cap on one harness command's runtime. The harness's own plugin verbs are quick. */
export const DEFAULT_COMMAND_TIMEOUT_MS = 30_000

/** The cap on the bytes one harness command's output may contribute. */
export const DEFAULT_MAX_OUTPUT_BYTES = 256 * 1024

/** The executable the driver looks for on PATH. */
export const FACTORY_HARNESS_EXECUTABLE = 'droid'

/**
 * The spawn guard's deny mode, which the test setup sets for every test file.
 *
 * The driver honours it: under deny mode no child process starts at all, so a test that
 * forgets to inject a fake fails loudly with a named cause instead of running the real CLI
 * against the developer's own Factory installation.
 */
const SPAWN_GUARD_ENV = 'KYBERDASH_SPAWN_GUARD'

export type FactoryHarnessCliOptions = {
  /** The environment PATH is read from; defaults to `process.env`. */
  processEnv?: NodeJS.ProcessEnv
  /** The cap on one command's runtime. */
  timeoutMs?: number
  /** The cap on the bytes one command's output may contribute. */
  maxOutputBytes?: number
}

/**
 * Find the harness's executable on PATH. The name is fixed, so a resolved path is the only
 * thing the caller has to report; nothing else is read from the environment.
 */
function resolveExecutable(name: string, processEnv: NodeJS.ProcessEnv): string | undefined {
  const pathValue = processEnv['PATH'] ?? processEnv['Path'] ?? ''
  const candidates = process.platform === 'win32' ? [name, `${name}.cmd`, `${name}.exe`] : [name]
  for (const directory of pathValue.split(delimiter)) {
    if (directory === '') continue
    for (const candidate of candidates) {
      const full = join(directory, candidate)
      try {
        accessSync(full, constants.X_OK)
        return full
      } catch {
        // Not here; keep looking along PATH.
      }
    }
  }
  return undefined
}

/** The harness's CLI, when PATH holds none: every command answers "not reachable". */
function unreachableHarness(cause: string): FactoryHarnessCli {
  return {
    executable: undefined,
    unavailable: cause,
    run: () => ({ exitCode: 127, stdout: '', stderr: cause }),
  }
}

/**
 * Resolve the harness's CLI from PATH and bind a driver to it.
 *
 * The driver runs one command at a time, with a bounded runtime and a bounded output
 * capture, and never throws: a command that could not run is a result with a non-zero exit
 * code, which the caller reports as `unknown` rather than as a success.
 */
export function resolveFactoryHarnessCli(
  options: FactoryHarnessCliOptions = {},
): FactoryHarnessCli {
  const processEnv = options.processEnv ?? process.env
  const timeoutMs = options.timeoutMs ?? DEFAULT_COMMAND_TIMEOUT_MS
  const maxOutputBytes = options.maxOutputBytes ?? DEFAULT_MAX_OUTPUT_BYTES
  if (processEnv[SPAWN_GUARD_ENV] === 'deny') {
    return unreachableHarness(
      'the spawn guard forbids starting a child process, so the Factory harness\'s state cannot be read or changed',
    )
  }
  const executable = resolveExecutable(FACTORY_HARNESS_EXECUTABLE, processEnv)
  if (executable === undefined) {
    return unreachableHarness(
      `the ${FACTORY_HARNESS_EXECUTABLE} CLI was not found on PATH, so the Factory harness's state cannot be read or changed`,
    )
  }
  return {
    executable,
    unavailable: undefined,
    run: (argv: readonly string[]) => {
      const result = spawnSync(executable, [...argv], {
        timeout: timeoutMs,
        maxBuffer: maxOutputBytes,
        encoding: 'utf8',
        stdio: ['ignore', 'pipe', 'pipe'],
        windowsHide: true,
      })
      if (result.error !== undefined) {
        return { exitCode: 127, stdout: '', stderr: result.error.message }
      }
      if (result.status === null) {
        return {
          exitCode: 124,
          stdout: '',
          stderr: `the ${FACTORY_HARNESS_EXECUTABLE} command timed out after ${timeoutMs} ms`,
        }
      }
      return { exitCode: result.status, stdout: result.stdout ?? '', stderr: result.stderr ?? '' }
    },
  }
}

// ── The harness's registry, as its own commands report it ───────────────────────────────────

/** The owner's settings registry that holds the registered marketplaces. */
export const FACTORY_MARKETPLACE_REGISTRY = 'extraKnownMarketplaces'

/** The owner's settings registry that holds the enabled plugins. */
export const FACTORY_PLUGIN_REGISTRY = 'enabledPlugins'

/** One marketplace the harness reports. */
export type FactoryMarketplaceEntry = {
  /** The name the marketplace is registered under. */
  name: string
  /**
   * The source the harness registered it from: `local:<path>` for a local marketplace,
   * and a URL for a remote one. The source is what tells a marketplace that merely shares
   * KyberDash's name apart from KyberDash's own.
   */
  source: string
}

/** One installed plugin the harness reports. */
export type FactoryPluginEntry = {
  /** The plugin's identity in the harness: `<plugin>@<marketplace>`. */
  identity: string
  /** The harness's own status word: `Enabled`, `Disabled`, … */
  status: string
}

/** The harness's registry, as its own list commands report it. */
export type FactoryHarnessRegistry = {
  /** The registered marketplaces, with the source each was registered from. */
  marketplaces: FactoryMarketplaceEntry[]
  /** The installed plugins, with their status. */
  plugins: FactoryPluginEntry[]
}

/** The registry of a harness that holds nothing. */
function emptyRegistry(): FactoryHarnessRegistry {
  return { marketplaces: [], plugins: [] }
}

/** The harness pads the columns of its list output with runs of spaces. */
const COLUMN_SEPARATOR = /\s{2,}/

/**
 * The rows of one of the harness's list commands, with the header row dropped.
 *
 * `undefined` means the output was not understood at all, which is `unknown` and never
 * "holds nothing"; an empty array means the harness answered that it holds nothing of
 * that kind.
 */
function listRows(stdout: string, header: string, empty: RegExp): string[][] | undefined {
  const text = stdout.trim()
  if (text === '') return undefined
  if (empty.test(text)) return []
  const rows = text
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line !== '')
    .map((line) => line.split(COLUMN_SEPARATOR).map((cell) => cell.trim()))
  if (rows[0]?.[0]?.toUpperCase() === header) rows.shift()
  return rows
}

/**
 * The marketplaces the harness reports, with the source each was registered from.
 *
 * The SOURCE column of `droid plugin marketplace list` carries the registered path, so a
 * marketplace KyberDash did not register is told apart from KyberDash's own by the
 * harness's own output.
 */
export function parseMarketplaceList(stdout: string): FactoryMarketplaceEntry[] | undefined {
  const rows = listRows(stdout, 'MARKETPLACE', /no marketplace/i)
  if (rows === undefined) return undefined
  const entries: FactoryMarketplaceEntry[] = []
  for (const row of rows) {
    const name = row[0]
    if (name === undefined || name === '') continue
    entries.push({ name, source: row[1] ?? '' })
  }
  return entries
}

/**
 * The installed plugins the harness reports, with their status.
 *
 * A plugin of the same name in another marketplace is a different identity, and only
 * `<plugin>@<marketplace>` is the identity KyberDash owns.
 */
export function parsePluginList(stdout: string): FactoryPluginEntry[] | undefined {
  const rows = listRows(stdout, 'PLUGIN', /no plugins installed/i)
  if (rows === undefined) return undefined
  const entries: FactoryPluginEntry[] = []
  for (const row of rows) {
    const identity = row[0]
    if (identity === undefined || identity === '') continue
    entries.push({ identity, status: row[1] ?? '' })
  }
  return entries
}

/** The outcome of reading the harness's registry. */
export type FactoryHarnessRegistryRead =
  | { ok: true; registry: FactoryHarnessRegistry }
  | { ok: false; cause: string }

/**
 * The harness's registry, read through the harness's OWN commands and nothing else.
 *
 * The marketplaces come from `droid plugin marketplace list` and the installed plugins
 * from `droid plugin list --scope user`. The owner's settings file is never opened: the
 * harness's own list commands are the only view of the harness's state (D48), and this
 * registry is the state the receipt records (D45).
 */
export function readFactoryHarnessRegistry(harness: FactoryHarnessCli): FactoryHarnessRegistryRead {
  if (harness.unavailable !== undefined) return { ok: false, cause: harness.unavailable }
  const marketplaces = harness.run(['plugin', 'marketplace', 'list'])
  if (marketplaces.exitCode !== 0) {
    return {
      ok: false,
      cause: `the ${FACTORY_HARNESS_EXECUTABLE} command "plugin marketplace list" failed with exit ${marketplaces.exitCode}${quoteStderr(marketplaces.stderr)}`,
    }
  }
  const registered = parseMarketplaceList(marketplaces.stdout)
  if (registered === undefined) {
    return {
      ok: false,
      cause: `the ${FACTORY_HARNESS_EXECUTABLE} command "plugin marketplace list" produced output KyberDash could not understand`,
    }
  }
  const plugins = harness.run(['plugin', 'list', '--scope', 'user'])
  if (plugins.exitCode !== 0) {
    return {
      ok: false,
      cause: `the ${FACTORY_HARNESS_EXECUTABLE} command "plugin list --scope user" failed with exit ${plugins.exitCode}${quoteStderr(plugins.stderr)}`,
    }
  }
  const installed = parsePluginList(plugins.stdout)
  if (installed === undefined) {
    return {
      ok: false,
      cause: `the ${FACTORY_HARNESS_EXECUTABLE} command "plugin list --scope user" produced output KyberDash could not understand`,
    }
  }
  return { ok: true, registry: { marketplaces: registered, plugins: installed } }
}

// ── Who owns a name ─────────────────────────────────────────────────────────────────────────

/** What KyberDash knows about the marketplace it registered. */
export type FactoryOwnership = {
  /**
   * Whether KyberDash wrote its own marketplace tree. A tree KyberDash wrote is a
   * marketplace KyberDash registered: it is the proof of ownership when the harness's own
   * output names no source to compare against.
   */
  treeWritten: boolean
  /**
   * The marketplace tree's root, when the caller knows it. With it, the harness's own
   * SOURCE column decides ownership: a marketplace registered from another path is a
   * different marketplace that happens to share the name (D46).
   */
  marketplaceRoot?: string
}

/** The local path a marketplace source names, or undefined when the source is not local. */
function localSourcePath(source: string): string | undefined {
  const text = source.trim()
  if (text === '') return undefined
  const path = text.startsWith('local:') ? text.slice('local:'.length).trim() : text
  if (path === '') return undefined
  // A remote marketplace is a URL, not a place on this machine.
  if (/^[a-z][a-z0-9+.-]*:\/\//i.test(path)) return undefined
  return path
}

/** Whether two paths name the same place, whatever the trailing separator says. */
function samePath(left: string, right: string): boolean {
  return resolve(left) === resolve(right)
}

/**
 * Whether the marketplace the harness registered under KyberDash's name is KyberDash's own.
 *
 * The harness's own SOURCE column decides it whenever the caller knows the tree root: a
 * marketplace registered from another path is a different marketplace that happens to
 * share the name. Without the root there is nothing to compare, and the tree KyberDash
 * wrote is the proof of ownership.
 */
function marketplaceIsOurs(entry: FactoryMarketplaceEntry, ownership: FactoryOwnership): boolean {
  const source = entry.source.trim()
  if (source === '') return ownership.treeWritten
  if (ownership.marketplaceRoot === undefined) return ownership.treeWritten
  const path = localSourcePath(source)
  if (path === undefined) return false
  return samePath(path, ownership.marketplaceRoot)
}

/**
 * Whether the plugin identity the harness holds came from KyberDash's own marketplace.
 *
 * The identity names the marketplace it came from, so the marketplace decides. The
 * harness's marketplace removal does not remove the plugin, so the identity can also
 * stand alone: ours when KyberDash wrote the tree it came from.
 */
function pluginIsOurs(
  entry: FactoryPluginEntry,
  registry: FactoryHarnessRegistry,
  ownership: FactoryOwnership,
): boolean {
  const marketplace = registry.marketplaces.find((row) => row.name === FACTORY_MARKETPLACE_NAME)
  if (marketplace !== undefined) return marketplaceIsOurs(marketplace, ownership)
  return ownership.treeWritten
}

// ── The conflict ────────────────────────────────────────────────────────────────────────────

/** A name the harness holds that KyberDash wants and does not own. */
export type FactoryHarnessConflict = {
  /** The owner's settings registry the colliding entry lives in. */
  registry: typeof FACTORY_MARKETPLACE_REGISTRY | typeof FACTORY_PLUGIN_REGISTRY
  /** The entry that collides with the name KyberDash wants. */
  entry: string
  /** The harness's own command that removes the entry. */
  command: string
  /** Why the entry collides, in fixed words. */
  detail: string
}

/** The source a marketplace was registered from, in words the report can carry. */
function sourceText(source: string): string {
  const text = source.trim()
  return text === '' ? 'an unnamed source' : text
}

/**
 * The names the harness holds that KyberDash does not own, if there are any.
 *
 * The harness resolves a plugin identity through the REGISTERED MARKETPLACE NAME, so a
 * marketplace named `kyberdash` that KyberDash did not register is a marketplace whose
 * payload the harness would install under KyberDash's name. The names are KyberDash's and
 * the harness's registry is keyed by name, so two owners cannot share one (D46).
 */
function detectConflicts(
  registry: FactoryHarnessRegistry,
  ownership: FactoryOwnership,
): FactoryHarnessConflict[] {
  const conflicts: FactoryHarnessConflict[] = []
  const marketplace = registry.marketplaces.find((row) => row.name === FACTORY_MARKETPLACE_NAME)
  if (marketplace !== undefined && !marketplaceIsOurs(marketplace, ownership)) {
    conflicts.push({
      registry: FACTORY_MARKETPLACE_REGISTRY,
      entry: FACTORY_MARKETPLACE_NAME,
      detail: `the harness holds a marketplace named "${FACTORY_MARKETPLACE_NAME}" registered from ${sourceText(marketplace.source)} that KyberDash did not register`,
      command: `droid plugin marketplace remove ${FACTORY_MARKETPLACE_NAME}`,
    })
  }
  const plugin = registry.plugins.find((row) => row.identity === FACTORY_PLUGIN_IDENTITY)
  if (plugin !== undefined && !pluginIsOurs(plugin, registry, ownership)) {
    conflicts.push({
      registry: FACTORY_PLUGIN_REGISTRY,
      entry: FACTORY_PLUGIN_IDENTITY,
      detail: `the harness holds the plugin "${FACTORY_PLUGIN_IDENTITY}" installed from a marketplace KyberDash did not register`,
      command: `droid plugin uninstall ${FACTORY_PLUGIN_IDENTITY} --scope user`,
    })
  }
  return conflicts
}

/** The harness's own stderr, quoted so the reason a command failed is visible. */
function quoteStderr(stderr: string): string {
  const text = stderr.trim()
  return text === '' ? '' : `: ${text.split('\n')[0]}`
}

// ── The harness-side state ───────────────────────────────────────────────────────────────────

/**
 * The harness-side install state, as the harness's own CLI reports it (D47).
 *
 *   marketplace-tree-written  the tree landed; the harness does not have it yet.
 *   marketplace-registered    the harness registered the marketplace; the plugin is not
 *                             installed.
 *   plugin-installed          the plugin is installed and enabled.
 *   conflict                  the harness holds a name KyberDash wants, and it does not
 *                             belong to KyberDash (D46).
 *   unknown                   the harness's own state cannot be read — the CLI is missing,
 *                             or its output is not understood.
 */
export type FactoryHarnessState =
  | 'marketplace-tree-written'
  | 'marketplace-registered'
  | 'plugin-installed'
  | 'conflict'
  | 'unknown'

/** The state, and why it is `unknown` or a `conflict` when it is. */
export type FactoryHarnessStateReport = {
  state: FactoryHarnessState
  /** The named cause of an `unknown` or a `conflict` state; absent for every other state. */
  cause?: string
  /**
   * Whether the install may proceed against the harness's own state.
   *
   * It is false when the harness's state could not be read, and false when the harness
   * holds a name KyberDash wants that KyberDash does not own. A collision must be
   * refused before anything is written, and this is the one signal the installer acts
   * on, so the two cases share it; the `state` says which of the two it was.
   *
   * A readable harness that holds nothing is `unknown` too — the vocabulary has no word
   * for "not installed" — but it is not a failure to read, and the install may proceed.
   */
  readable: boolean
  /** The harness's registry, as its own list commands report it. */
  registry: FactoryHarnessRegistry
  /** Every name the harness holds that KyberDash does not own; empty for every other state. */
  conflicts: FactoryHarnessConflict[]
  /** The first of the colliding names; absent when there is none. */
  conflict?: FactoryHarnessConflict
}

/**
 * Read the harness-side install state.
 *
 * The harness's registry is read through the harness's OWN commands and nothing else: the
 * marketplace list and the user-scope plugin list. A command that fails, or an output that
 * is not understood, is `unknown` with the named cause — never a state that claims the
 * plugin is installed. A name the harness holds that KyberDash does not own is a
 * `conflict`, and the install may not proceed against it.
 */
export function readFactoryHarnessState(
  harness: FactoryHarnessCli,
  options: { treeWritten: boolean; marketplaceRoot?: string },
): FactoryHarnessStateReport {
  const ownership: FactoryOwnership = {
    treeWritten: options.treeWritten,
    ...(options.marketplaceRoot === undefined ? {} : { marketplaceRoot: options.marketplaceRoot }),
  }
  const read = readFactoryHarnessRegistry(harness)
  if (!read.ok) {
    return {
      state: 'unknown',
      cause: read.cause,
      readable: false,
      registry: emptyRegistry(),
      conflicts: [],
    }
  }
  const registry = read.registry
  const conflicts = detectConflicts(registry, ownership)
  if (conflicts.length > 0) {
    return {
      state: 'conflict',
      cause: conflicts.map((row) => `${row.detail}; remove it with: ${row.command}`).join('; '),
      readable: false,
      registry,
      conflicts,
      conflict: conflicts[0],
    }
  }
  const installed = registry.plugins.some((row) => row.identity === FACTORY_PLUGIN_IDENTITY)
  const registered = registry.marketplaces.some((row) => row.name === FACTORY_MARKETPLACE_NAME)
  if (installed) return { state: 'plugin-installed', readable: true, registry, conflicts: [] }
  if (registered) return { state: 'marketplace-registered', readable: true, registry, conflicts: [] }
  if (options.treeWritten) {
    return { state: 'marketplace-tree-written', readable: true, registry, conflicts: [] }
  }
  return {
    state: 'unknown',
    cause: 'nothing is installed: the Factory harness holds no marketplace and no plugin',
    readable: true,
    registry,
    conflicts: [],
  }
}

// ── The residue the harness's own teardown leaves ──────────────────────────────────────────

/**
 * What the harness's own teardown left behind in the owner's settings file.
 *
 * The residue is REPORTED and never patched: KyberDash never writes the owner's settings
 * file (D48), so the report names the registry, the entry and the exact command that
 * clears it.
 */
export type FactoryHarnessResidue = {
  /** The owner's settings registry the residue lives in. */
  registry: typeof FACTORY_MARKETPLACE_REGISTRY | typeof FACTORY_PLUGIN_REGISTRY
  /** The entry the harness left behind, or removed. */
  entry: string
  /** What the harness left behind, in fixed words. */
  detail: string
  /**
   * The exact command that clears the residue. The one residue the harness has no command
   * for — the owner's own disabled entry, which the harness's uninstall deletes outright
   * instead of restoring it to `false` — carries the exact edit the owner makes instead,
   * because KyberDash never writes that file.
   */
  command: string
}

/** Whether the harness's own status word says the plugin is enabled. */
function isEnabled(status: string): boolean {
  return status.trim().toLowerCase() === 'enabled'
}

/**
 * The residue the harness's own uninstall and marketplace removal left behind.
 *
 * `before` is the harness-side state the install found: the registered marketplaces and
 * the installed plugins with their status, read through the harness's own list commands
 * (D45). The residue is what the teardown was meant to remove and did not:
 *
 *   - the marketplace KyberDash registered, still registered;
 *   - the plugin KyberDash installed, still installed and still enabled — a plugin the
 *     owner disabled afterwards is the owner's own state, not residue;
 *   - the owner's own DISABLED entry for that plugin, which the harness's uninstall
 *     deletes outright instead of restoring it to `false`.
 *
 * The teardown refuses to act on a name KyberDash does not own, so a marketplace named
 * `kyberdash` the harness still holds at this point is KyberDash's own.
 *
 * The third is the one the harness has no command for: `droid plugin install` re-adds the
 * entry as ENABLED, and there is no enable or disable command, so the owner restores the
 * disabled entry by editing their own settings file — which KyberDash never does (D48).
 */
export function readFactoryHarnessResidue(
  harness: FactoryHarnessCli,
  before: FactoryHarnessRegistry,
): FactoryHarnessResidue[] {
  const read = readFactoryHarnessRegistry(harness)
  if (!read.ok) return []
  const now = read.registry
  const residue: FactoryHarnessResidue[] = []

  const marketplace = now.marketplaces.find((row) => row.name === FACTORY_MARKETPLACE_NAME)
  if (marketplace !== undefined) {
    residue.push({
      registry: FACTORY_MARKETPLACE_REGISTRY,
      entry: FACTORY_MARKETPLACE_NAME,
      detail: 'the harness still registers the marketplace KyberDash added',
      command: `droid plugin marketplace remove ${FACTORY_MARKETPLACE_NAME}`,
    })
  }

  const plugin = now.plugins.find((row) => row.identity === FACTORY_PLUGIN_IDENTITY)
  const hadPlugin = before.plugins.find((row) => row.identity === FACTORY_PLUGIN_IDENTITY)
  // A plugin the owner disabled after the install is the owner's own state, not residue.
  if (plugin !== undefined && isEnabled(plugin.status)) {
    residue.push({
      registry: FACTORY_PLUGIN_REGISTRY,
      entry: FACTORY_PLUGIN_IDENTITY,
      detail: 'the harness still holds the plugin KyberDash installed',
      command: `droid plugin uninstall ${FACTORY_PLUGIN_IDENTITY} --scope user`,
    })
  }
  if (plugin === undefined && hadPlugin !== undefined && !isEnabled(hadPlugin.status)) {
    residue.push({
      registry: FACTORY_PLUGIN_REGISTRY,
      entry: FACTORY_PLUGIN_IDENTITY,
      detail:
        "the harness's uninstall removed the owner's disabled entry instead of restoring it to false",
      command: `add "${FACTORY_PLUGIN_IDENTITY}": false to the "${FACTORY_PLUGIN_REGISTRY}" object in the owner's Factory settings file`,
    })
  }
  return residue
}

/**
 * The harness commands that move the harness to `plugin-installed` from the state it is in,
 * in the order they must run. An empty list means the harness already holds the plugin.
 *
 * A conflict is never acted on: the installer refuses before it writes anything, and a
 * marketplace KyberDash does not own must never be installed against.
 */
export function marketplaceInstallCommands(
  state: FactoryHarnessState,
  marketplaceRoot: string,
): string[][] {
  if (state === 'plugin-installed') return []
  if (state === 'conflict') return []
  const commands: string[][] = []
  if (state !== 'marketplace-registered') {
    commands.push(['plugin', 'marketplace', 'add', marketplaceRoot])
  }
  commands.push(['plugin', 'install', FACTORY_PLUGIN_IDENTITY, '--scope', 'user'])
  return commands
}

/**
 * The harness commands that remove the harness-side state, in the order they must run: the
 * plugin first, then the marketplace it came from.
 *
 * A conflict is never acted on either: the entry that holds KyberDash's name belongs to
 * somebody else, and the owner's registry must be left exactly as it was found (D46).
 */
export function marketplaceUninstallCommands(state: FactoryHarnessState): string[][] {
  if (state === 'conflict') return []
  const commands: string[][] = []
  if (state === 'plugin-installed') {
    commands.push(['plugin', 'uninstall', FACTORY_PLUGIN_IDENTITY, '--scope', 'user'])
  }
  if (state === 'plugin-installed' || state === 'marketplace-registered') {
    commands.push(['plugin', 'marketplace', 'remove', FACTORY_MARKETPLACE_NAME])
  }
  return commands
}
