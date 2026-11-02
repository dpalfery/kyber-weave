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
import { delimiter, dirname, join } from 'node:path'

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
 *
 * `SubagentStop` is not one of them either. Factory's documented payload for that event
 * carries `task_name`, `task_result`, `task_error` and `stop_hook_active` and no child
 * session id, so the mapper yields no span for it (D54) and a registration would buy
 * nothing while costing a process per subagent stop.
 */
const HOOK_EVENTS = [
  'SessionStart',
  'UserPromptSubmit',
  'PreToolUse',
  'PostToolUse',
  'Stop',
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
  Object.fromEntries(
    HOOK_EVENTS.map((event) => [event, [{ hooks: [{ type: 'command', command: HOOK_COMMAND }] }]]),
  ),
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

// ── The harness-side state ───────────────────────────────────────────────────────────────────

/**
 * The harness-side install state, as the harness's own CLI reports it (D47).
 *
 *   marketplace-tree-written  the tree landed; the harness does not have it yet.
 *   marketplace-registered    the harness registered the marketplace; the plugin is not
 *                             installed.
 *   plugin-installed          the plugin is installed and enabled.
 *   unknown                   the harness's own state cannot be read — the CLI is missing,
 *                             or its output is not understood.
 */
export type FactoryHarnessState =
  | 'marketplace-tree-written'
  | 'marketplace-registered'
  | 'plugin-installed'
  | 'unknown'

/** The state, and why it is `unknown` when it is. */
export type FactoryHarnessStateReport = {
  state: FactoryHarnessState
  /** The named cause of an `unknown` state; absent for every other state. */
  cause?: string
  /**
   * Whether the harness's own state could be read at all. A readable harness that holds
   * nothing is `unknown` too — the vocabulary has no word for "not installed" — but it is
   * not a failure to read, and the install may proceed.
   */
  readable: boolean
}

/** The marketplace's name as a token, so a path that merely contains it is not a match. */
const MARKETPLACE_TOKEN = new RegExp(`(^|\\s)${FACTORY_MARKETPLACE_NAME}(\\s|$)`)

/** The plugin's identity as a token, for the same reason. */
const IDENTITY_TOKEN = new RegExp(`(^|\\s)${FACTORY_PLUGIN_IDENTITY}(\\s|$)`)

/**
 * Whether the harness's marketplace list names KyberDash's marketplace. `undefined` means
 * the output was not understood, which is `unknown` and never "not registered".
 */
function marketplaceIsRegistered(stdout: string): boolean | undefined {
  const text = stdout.trim()
  if (text === '') return undefined
  if (/no marketplace/i.test(text)) return false
  return text.split('\n').some((line) => MARKETPLACE_TOKEN.test(line))
}

/**
 * Whether the harness's plugin list names KyberDash's plugin identity. A plugin of the
 * same name in another marketplace is a different identity and is not a match.
 */
function pluginIsInstalled(stdout: string): boolean | undefined {
  const text = stdout.trim()
  if (text === '') return undefined
  if (/no plugins installed/i.test(text)) return false
  return text.split('\n').some((line) => IDENTITY_TOKEN.test(line))
}

/** The harness's own stderr, quoted so the reason a command failed is visible. */
function quoteStderr(stderr: string): string {
  const text = stderr.trim()
  return text === '' ? '' : `: ${text.split('\n')[0]}`
}

/**
 * Read the harness-side install state.
 *
 * The harness's registry is read through the harness's OWN commands and nothing else: the
 * marketplace list and the user-scope plugin list. A command that fails, or an output that
 * is not understood, is `unknown` with the named cause — never a state that claims the
 * plugin is installed.
 */
export function readFactoryHarnessState(
  harness: FactoryHarnessCli,
  options: { treeWritten: boolean },
): FactoryHarnessStateReport {
  if (harness.unavailable !== undefined) {
    return { state: 'unknown', cause: harness.unavailable, readable: false }
  }
  const marketplaces = harness.run(['plugin', 'marketplace', 'list'])
  if (marketplaces.exitCode !== 0) {
    return {
      state: 'unknown',
      cause: `the ${FACTORY_HARNESS_EXECUTABLE} command "plugin marketplace list" failed with exit ${marketplaces.exitCode}${quoteStderr(marketplaces.stderr)}`,
      readable: false,
    }
  }
  const plugins = harness.run(['plugin', 'list', '--scope', 'user'])
  if (plugins.exitCode !== 0) {
    return {
      state: 'unknown',
      cause: `the ${FACTORY_HARNESS_EXECUTABLE} command "plugin list --scope user" failed with exit ${plugins.exitCode}${quoteStderr(plugins.stderr)}`,
      readable: false,
    }
  }
  const registered = marketplaceIsRegistered(marketplaces.stdout)
  if (registered === undefined) {
    return {
      state: 'unknown',
      cause: `the ${FACTORY_HARNESS_EXECUTABLE} command "plugin marketplace list" produced output KyberDash could not understand`,
      readable: false,
    }
  }
  const installed = pluginIsInstalled(plugins.stdout)
  if (installed === undefined) {
    return {
      state: 'unknown',
      cause: `the ${FACTORY_HARNESS_EXECUTABLE} command "plugin list --scope user" produced output KyberDash could not understand`,
      readable: false,
    }
  }
  if (installed) return { state: 'plugin-installed', readable: true }
  if (registered) return { state: 'marketplace-registered', readable: true }
  if (options.treeWritten) return { state: 'marketplace-tree-written', readable: true }
  return {
    state: 'unknown',
    cause: 'nothing is installed: the Factory harness holds no marketplace and no plugin',
    readable: true,
  }
}

/**
 * The harness commands that move the harness to `plugin-installed` from the state it is in,
 * in the order they must run. An empty list means the harness already holds the plugin.
 */
export function marketplaceInstallCommands(
  state: FactoryHarnessState,
  marketplaceRoot: string,
): string[][] {
  if (state === 'plugin-installed') return []
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
 */
export function marketplaceUninstallCommands(state: FactoryHarnessState): string[][] {
  const commands: string[][] = []
  if (state === 'plugin-installed') {
    commands.push(['plugin', 'uninstall', FACTORY_PLUGIN_IDENTITY, '--scope', 'user'])
  }
  if (state === 'plugin-installed' || state === 'marketplace-registered') {
    commands.push(['plugin', 'marketplace', 'remove', FACTORY_MARKETPLACE_NAME])
  }
  return commands
}
