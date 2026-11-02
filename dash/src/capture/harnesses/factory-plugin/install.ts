// Install, remove and report on the Factory (Droid) capture plugin (plan D3, D11, D13).
//
// KyberDash owns one whole plugin DIRECTORY under the Factory root:
//
//   <factoryRoot>/plugins/kyberdash-factory/
//   ├── .factory-plugin/plugin.json
//   └── hooks/
//       ├── hooks.json
//       └── kyberdash-factory.mjs
//
// as ONE unit with ONE receipt (`~/.kyberdash/factory-plugin-receipt.json`) that holds the
// SHA-256 digest of every file Kyber wrote. The owner's own Factory files — the user's
// `~/.factory/settings.json`, the user's `~/.factory/hooks.json` and the project's
// `.factory/hooks.json` — are never read, written, merged or restored (ADR 0026, ADR 0031).
//
// Two install modes, the OpenCode installer's semantics extended from one file to a tree:
//
//   strict    `capture enable`: refuses a foreign tree (one holding a file Kyber did not
//             write, or one whose files are not the files Kyber writes) and an owner-edited
//             tree (one Kyber wrote whose bytes no longer match the receipt) with exit 1,
//             and writes nothing at all.
//   preserve  Squad and `kyber-weave update`: keeps such a tree, warns, and exits 0.
//
// The Factory root is `$FACTORY_DIR`, else `~/.factory` — the resolution the Factory file
// provider already uses (`dash/src/providers/droid.ts`).

import { randomBytes } from 'node:crypto'
import {
  closeSync,
  constants,
  fstatSync,
  fsyncSync,
  lstatSync,
  mkdirSync,
  openSync,
  readdirSync,
  readFileSync,
  renameSync,
  rmSync,
  writeFileSync,
} from 'node:fs'
import { dirname, isAbsolute, join } from 'node:path'

import { createConsentIfAbsent, readConsentState, writeFileAtomic } from '../../consent-file.js'
import { envJoin, type PathEnv } from '../../paths.js'
import { sha256Hex } from '../../receipt.js'
import { FACTORY_PLUGIN_SOURCE } from './generated/plugin-source.js'

export type FactoryPluginMode = 'strict' | 'preserve'

export type FactoryPluginOptions = {
  mode?: FactoryPluginMode
  dryRun?: boolean
  /** Act only when a plugin tree is already installed (the update path). */
  ifInstalled?: boolean
  /** Injectable for tests; defaults to `process.env`. */
  processEnv?: NodeJS.ProcessEnv
}

export type FactoryPluginPaths = {
  /** The Factory root the plugin tree lives under. */
  root: string
  /** The plugin directory Kyber owns. */
  pluginDir: string
  /** The receipt: what Kyber wrote, and the digest of every file. */
  receiptFile: string
  /** The consent file, in KyberDash's own state directory. */
  consentFile: string
}

export type FactoryPluginOutcome =
  | 'wrote'
  | 'would-write'
  | 'restored'
  | 'would-restore'
  | 'noop'
  | 'refused'
  | 'kept'
  | 'skipped'

/** What the install decided about the plugin tree. */
export type FactoryPluginDisposition =
  | 'installed'
  | 'unchanged'
  | 'kept-edited'
  | 'kept-foreign'
  | 'kept-unreadable'
  | 'skipped'
  | 'refused'

export type FactoryConsentOutcome = 'created' | 'would-create' | 'kept' | 'none'

export type FactoryPluginResult = {
  exitCode: number
  outcome: FactoryPluginOutcome
  disposition: FactoryPluginDisposition
  consent: FactoryConsentOutcome
  /** Why a refusal happened: `conflict` (not ours), `drift` (owner edit), `unreadable`. */
  reason?: 'conflict' | 'drift' | 'unreadable'
  out: string[]
  err: string[]
}

type Receipt = { version: 1; root: string; files: { path: string; sha256: string }[] }

/** The plugin directory Kyber owns inside the Factory root. */
export const FACTORY_PLUGIN_DIR_NAME = 'kyberdash-factory'

/** The files Kyber owns, relative to the plugin directory. This is the tree shape. */
const MANIFEST_FILE = '.factory-plugin/plugin.json'
const HOOKS_FILE = 'hooks/hooks.json'
const SCRIPT_FILE = 'hooks/kyberdash-factory.mjs'
const OWNED_FILES = [MANIFEST_FILE, HOOKS_FILE, SCRIPT_FILE] as const

/**
 * The hook events the plugin registers on. `PreCompact` is not one of them: the contract names
 * no span for a compaction, so the hook would only cost a process per compaction.
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
const HOOK_COMMAND = `node \${DROID_PLUGIN_ROOT}/${SCRIPT_FILE}`

/** The manifest Factory reads from the plugin root. */
const MANIFEST_SOURCE = `${JSON.stringify(
  {
    name: FACTORY_PLUGIN_DIR_NAME,
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

/** The bytes of every file Kyber owns, keyed by its path relative to the plugin directory. */
const OWNED_CONTENT: Record<string, string> = {
  [MANIFEST_FILE]: MANIFEST_SOURCE,
  [HOOKS_FILE]: HOOKS_SOURCE,
  [SCRIPT_FILE]: FACTORY_PLUGIN_SOURCE,
}

/** The cap on one file the installer reads. Kyber's own files are far smaller. */
const MAX_FILE_BYTES = 1024 * 1024

/** The cap on the files the installer reads in one tree. */
const MAX_TREE_FILES = 64

/** Where the plugin tree, its receipt and its consent file live. */
export function factoryPluginPaths(
  home: string,
  env?: PathEnv,
  options: Pick<FactoryPluginOptions, 'processEnv'> = {},
): FactoryPluginPaths {
  const processEnv = options.processEnv ?? process.env
  const fromEnv = processEnv['FACTORY_DIR']
  const root = fromEnv !== undefined && fromEnv !== '' ? fromEnv : envJoin(env, home, '.factory')
  return {
    root,
    pluginDir: envJoin(env, root, 'plugins', FACTORY_PLUGIN_DIR_NAME),
    receiptFile: envJoin(env, home, '.kyberdash', 'factory-plugin-receipt.json'),
    consentFile: envJoin(env, home, '.kyberdash', 'factory-plugin.json'),
  }
}

// ---------------------------------------------------------------------------------------------
// The receipt

function readReceipt(path: string): Receipt | undefined {
  try {
    const parsed = JSON.parse(readFileSync(path, 'utf8')) as Partial<Receipt>
    if (parsed.version !== 1 || typeof parsed.root !== 'string' || !Array.isArray(parsed.files)) {
      return undefined
    }
    const files: { path: string; sha256: string }[] = []
    for (const entry of parsed.files) {
      if (typeof entry?.path !== 'string' || typeof entry?.sha256 !== 'string') return undefined
      files.push({ path: entry.path, sha256: entry.sha256 })
    }
    return { version: 1, root: parsed.root, files }
  } catch {
    // A missing or unreadable receipt means nothing was recorded.
    return undefined
  }
}

/** The absolute path of one owned file, the way the receipt records it. */
function ownedPath(pluginDir: string, relative: string): string {
  return join(pluginDir, ...relative.split('/'))
}

/** The digest the receipt records for one owned file, or undefined when it records none. */
function receiptDigest(receipt: Receipt, pluginDir: string, relative: string): string | undefined {
  const expected = ownedPath(pluginDir, relative)
  for (const entry of receipt.files) {
    const absolute = isAbsolute(entry.path) ? entry.path : join(receipt.root, entry.path)
    if (absolute === expected) return entry.sha256
  }
  return undefined
}

/** The receipt Kyber writes: the tree, and the digest of every file in it. */
function receiptFor(paths: FactoryPluginPaths): Receipt {
  return {
    version: 1,
    root: paths.pluginDir,
    files: OWNED_FILES.map((relative) => ({
      path: ownedPath(paths.pluginDir, relative),
      sha256: sha256Hex(OWNED_CONTENT[relative] ?? ''),
    })),
  }
}

// ---------------------------------------------------------------------------------------------
// Reading the tree

type TreeRead =
  | { kind: 'absent' }
  | { kind: 'unreadable'; detail: string }
  | { kind: 'ok'; files: { relative: string; text: string }[] }

/**
 * Read one file without following a final symlink: the file is opened once with `O_NOFOLLOW`
 * and validated through the descriptor, so there is no gap between the type check and the
 * read. A symlink is never read and never written through.
 */
function readFileNoFollow(path: string): { ok: true; text: string } | { ok: false; detail: string } {
  let fd: number | undefined
  try {
    fd = openSync(path, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0) | (constants.O_NONBLOCK ?? 0))
    const stat = fstatSync(fd)
    if (!stat.isFile()) return { ok: false, detail: `${path} is not a regular file` }
    if (stat.size > MAX_FILE_BYTES) {
      return { ok: false, detail: `${path} is larger than the ${MAX_FILE_BYTES} byte read bound` }
    }
    return { ok: true, text: readFileSync(fd, 'utf8') }
  } catch {
    return { ok: false, detail: `${path} cannot be read as a file` }
  } finally {
    if (fd !== undefined) closeSync(fd)
  }
}

/**
 * Read the plugin tree. A tree that is a symlink, a file, or that holds a symlink or an
 * unreadable file is `unreadable`, never `foreign`: an unreadable path is not evidence of
 * anything, so the installer leaves it alone in every mode.
 */
function readTree(pluginDir: string): TreeRead {
  let rootStat
  try {
    rootStat = lstatSync(pluginDir)
  } catch (error) {
    if ((error as NodeJS.ErrnoException | undefined)?.code === 'ENOENT') return { kind: 'absent' }
    return { kind: 'unreadable', detail: `${pluginDir} cannot be read` }
  }
  if (rootStat.isSymbolicLink()) return { kind: 'unreadable', detail: `${pluginDir} is a symlink` }
  if (!rootStat.isDirectory()) return { kind: 'unreadable', detail: `${pluginDir} is not a directory` }

  const files: { relative: string; text: string }[] = []
  const walk = (dir: string, prefix: string): TreeRead | undefined => {
    let entries
    try {
      entries = readdirSync(dir, { withFileTypes: true })
    } catch {
      return { kind: 'unreadable', detail: `${dir} cannot be read` }
    }
    for (const entry of entries) {
      const relative = prefix === '' ? entry.name : `${prefix}/${entry.name}`
      const path = join(dir, entry.name)
      if (entry.isSymbolicLink()) return { kind: 'unreadable', detail: `${path} is a symlink` }
      if (entry.isDirectory()) {
        const deeper = walk(path, relative)
        if (deeper !== undefined) return deeper
        continue
      }
      if (!entry.isFile()) return { kind: 'unreadable', detail: `${path} is not a regular file` }
      if (files.length >= MAX_TREE_FILES) {
        return { kind: 'unreadable', detail: `${pluginDir} holds more than ${MAX_TREE_FILES} files` }
      }
      const read = readFileNoFollow(path)
      if (!read.ok) return { kind: 'unreadable', detail: read.detail }
      files.push({ relative, text: read.text })
    }
    return undefined
  }
  const problem = walk(pluginDir, '')
  if (problem !== undefined) return problem
  return { kind: 'ok', files }
}

// ---------------------------------------------------------------------------------------------
// The classifier

type Classified =
  | { kind: 'absent' }
  | { kind: 'unreadable'; detail: string }
  | { kind: 'foreign'; detail: string }
  | { kind: 'edited'; detail: string }
  | { kind: 'same' }

/**
 * Decide what the tree is.
 *
 *   absent     nothing is installed.
 *   unreadable the path, or a file in it, cannot be read as a regular file.
 *   foreign    the tree holds a file Kyber did not write, or files that are not the files
 *              Kyber writes. Nothing proves Kyber owns it.
 *   edited     the receipt proves Kyber wrote the tree, and a file no longer matches its
 *              digest.
 *   same       every file matches the receipt (or, without one, matches what Kyber writes).
 */
function classify(paths: FactoryPluginPaths): Classified {
  const tree = readTree(paths.pluginDir)
  if (tree.kind !== 'ok') return tree

  const found = new Map(tree.files.map((file) => [file.relative, file.text]))
  const owned = new Set<string>(OWNED_FILES)
  const extra = [...found.keys()].filter((relative) => !owned.has(relative)).sort()
  if (extra.length > 0) {
    return { kind: 'foreign', detail: `${extra.join(', ')} was not written by KyberDash` }
  }

  const receipt = readReceipt(paths.receiptFile)
  const proven =
    receipt !== undefined &&
    receipt.root === paths.pluginDir &&
    OWNED_FILES.every((relative) => receiptDigest(receipt, paths.pluginDir, relative) !== undefined)

  const missing = OWNED_FILES.filter((relative) => !found.has(relative))
  if (missing.length > 0) {
    return proven
      ? { kind: 'edited', detail: `${missing.join(', ')} is missing; it changed since install` }
      : { kind: 'foreign', detail: `${missing.join(', ')} is not a file KyberDash writes` }
  }

  if (proven && receipt !== undefined) {
    for (const relative of OWNED_FILES) {
      const digest = receiptDigest(receipt, paths.pluginDir, relative)
      if (digest !== undefined && sha256Hex(found.get(relative) ?? '') !== digest) {
        return { kind: 'edited', detail: `${relative} changed since install` }
      }
    }
    return { kind: 'same' }
  }

  for (const relative of OWNED_FILES) {
    if (found.get(relative) !== OWNED_CONTENT[relative]) {
      return { kind: 'foreign', detail: `${relative} is not the file KyberDash writes` }
    }
  }
  return { kind: 'same' }
}

// ---------------------------------------------------------------------------------------------
// Writing the tree

/**
 * Write the whole tree, or nothing of it.
 *
 * Every file is staged as a temporary sibling first and renamed into place only once all of
 * them are staged, so a tree that cannot be written in full leaves no partial tree behind. The
 * receipt is written after the tree has landed, never before.
 */
function writeTree(paths: FactoryPluginPaths): void {
  const staged: { temp: string; target: string }[] = []
  try {
    for (const relative of OWNED_FILES) {
      const target = ownedPath(paths.pluginDir, relative)
      mkdirSync(dirname(target), { recursive: true })
      const temp = `${target}.${randomBytes(8).toString('hex')}.tmp`
      const fd = openSync(temp, 'wx', 0o644)
      try {
        writeFileSync(fd, OWNED_CONTENT[relative] ?? '')
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

function writeReceipt(paths: FactoryPluginPaths): void {
  writeFileAtomic(paths.receiptFile, `${JSON.stringify(receiptFor(paths), null, 2)}\n`)
}

/** The install creates the consent file with content off, only when nothing is there. */
function ensureConsent(paths: FactoryPluginPaths, dryRun: boolean): FactoryConsentOutcome {
  if (readConsentState(paths.consentFile) !== 'absent') return 'kept'
  if (dryRun) return 'would-create'
  return createConsentIfAbsent(paths.consentFile) === 'created' ? 'created' : 'kept'
}

function result(
  disposition: FactoryPluginDisposition,
  outcome: FactoryPluginOutcome,
  fields: Partial<Pick<FactoryPluginResult, 'exitCode' | 'reason' | 'consent' | 'out' | 'err'>> = {},
): FactoryPluginResult {
  return { exitCode: 0, consent: 'none', out: [], err: [], ...fields, disposition, outcome }
}

// ---------------------------------------------------------------------------------------------
// Install and uninstall

export function installFactoryPlugin(
  home: string,
  env?: PathEnv,
  options: FactoryPluginOptions = {},
): FactoryPluginResult {
  const mode = options.mode ?? 'strict'
  const dryRun = options.dryRun === true
  const paths = factoryPluginPaths(home, env, options)
  const found = classify(paths)

  if (found.kind === 'absent' && options.ifInstalled === true) {
    return result('skipped', 'skipped', {
      out: [`factory plugin: skipped-not-installed ${paths.pluginDir}`],
    })
  }

  const preserve = mode === 'preserve'
  const strictRefusal = (
    reason: 'conflict' | 'drift' | 'unreadable',
    message: string,
  ): FactoryPluginResult =>
    result('refused', 'refused', { exitCode: 1, reason, err: [`kyberdash capture: ${message}`] })

  switch (found.kind) {
    case 'unreadable': {
      const message = `refusing to overwrite ${paths.pluginDir}: ${found.detail}`
      if (!preserve) return strictRefusal('unreadable', message)
      return result('kept-unreadable', 'kept', {
        out: [`factory plugin: kept-unreadable ${paths.pluginDir}`],
        err: [`kyberdash capture: warning: ${paths.pluginDir} cannot be read as a file; leaving it in place`],
      })
    }
    case 'foreign': {
      if (!preserve) {
        return strictRefusal(
          'conflict',
          `refusing to overwrite ${paths.pluginDir}: it was not written by KyberDash (${found.detail})`,
        )
      }
      return result('kept-foreign', 'kept', {
        out: [`factory plugin: kept-foreign ${paths.pluginDir}`],
        err: [
          `kyberdash capture: warning: ${paths.pluginDir} was not written by KyberDash; leaving it in place`,
        ],
      })
    }
    case 'edited': {
      if (!preserve) {
        return strictRefusal('drift', `refusing to overwrite ${paths.pluginDir}: it changed since install`)
      }
      return result('kept-edited', 'kept', {
        consent: ensureConsent(paths, dryRun),
        out: [`factory plugin: kept-edited ${paths.pluginDir}`],
        err: [
          `kyberdash capture: warning: ${paths.pluginDir} was edited since install; leaving it in place`,
        ],
      })
    }
    case 'same':
      return result('unchanged', 'noop', {
        consent: ensureConsent(paths, dryRun),
        out: [`factory plugin: unchanged ${paths.pluginDir}`],
      })
    case 'absent': {
      if (dryRun) {
        return result('installed', 'would-write', {
          consent: ensureConsent(paths, true),
          out: [`factory plugin: would-install ${paths.pluginDir}`],
        })
      }
      writeTree(paths)
      writeReceipt(paths)
      return result('installed', 'wrote', {
        consent: ensureConsent(paths, false),
        out: [`factory plugin: installed ${paths.pluginDir}`],
      })
    }
  }
}

export function uninstallFactoryPlugin(
  home: string,
  env?: PathEnv,
  options: Pick<FactoryPluginOptions, 'dryRun' | 'processEnv'> = {},
): FactoryPluginResult {
  const paths = factoryPluginPaths(home, env, options)
  const found = classify(paths)

  if (found.kind === 'absent') {
    rmSync(paths.receiptFile, { force: true })
    return result('skipped', 'noop', { out: ['harness factory: capture plugin not installed; nothing to do'] })
  }
  if (found.kind === 'unreadable') {
    return result('refused', 'refused', {
      exitCode: 1,
      reason: 'unreadable',
      err: [`kyberdash capture: refusing to remove ${paths.pluginDir}: ${found.detail}`],
    })
  }
  if (found.kind === 'foreign') {
    return result('refused', 'refused', {
      exitCode: 1,
      reason: 'conflict',
      err: [
        `kyberdash capture: refusing to remove ${paths.pluginDir}: it was not written by KyberDash; leaving it in place`,
      ],
    })
  }
  if (found.kind === 'edited') {
    return result('refused', 'refused', {
      exitCode: 1,
      reason: 'drift',
      err: [
        `kyberdash capture: refusing to remove ${paths.pluginDir}: it changed since install; leaving it in place. Delete it yourself to remove it.`,
      ],
    })
  }
  if (options.dryRun === true) {
    return result('skipped', 'would-restore', {
      out: [`factory plugin: would remove capture plugin ${paths.pluginDir}`],
    })
  }
  rmSync(paths.pluginDir, { recursive: true, force: true })
  rmSync(paths.receiptFile, { force: true })
  return result('skipped', 'restored', { out: [`factory plugin: removed capture plugin ${paths.pluginDir}`] })
}

// ---------------------------------------------------------------------------------------------
// The state and status surface

export type FactoryPluginState = {
  /** on: the tree matches the receipt. off: nothing installed. unknown: not ours, or edited. */
  state: 'on' | 'off' | 'partial' | 'unknown'
  exists: boolean
  receipt: boolean
  unreadable: boolean
}

/** What Settings and `capture status` report. Reads the tree and the receipt; never throws. */
export function factoryPluginState(
  home: string,
  env?: PathEnv,
  options: Pick<FactoryPluginOptions, 'processEnv'> = {},
): FactoryPluginState {
  const paths = factoryPluginPaths(home, env, options)
  const receipt = readReceipt(paths.receiptFile)
  const found = classify(paths)
  if (found.kind === 'unreadable') {
    return { state: 'unknown', exists: true, receipt: receipt !== undefined, unreadable: true }
  }
  if (found.kind === 'absent') {
    return { state: 'off', exists: false, receipt: receipt !== undefined, unreadable: false }
  }
  return {
    state: found.kind === 'same' ? 'on' : 'unknown',
    exists: true,
    receipt: receipt !== undefined,
    unreadable: false,
  }
}

/** Fixed-format status lines. Never echoes owner-written text. */
export function describeFactoryPluginStatus(
  home: string,
  env?: PathEnv,
  options: Pick<FactoryPluginOptions, 'processEnv'> = {},
): string[] {
  const paths = factoryPluginPaths(home, env, options)
  const plugin = factoryPluginState(home, env, options)
  const lines: string[] = []
  if (plugin.unreadable) {
    lines.push('plugin: unreadable (not a regular directory, or a file in it is not a regular file)')
  } else if (plugin.state === 'off') {
    lines.push('plugin: not installed')
  } else if (plugin.state === 'on') {
    lines.push('plugin: installed')
  } else {
    lines.push('plugin: modified since install or not written by KyberDash')
  }
  const consent = readConsentState(paths.consentFile)
  switch (consent) {
    case 'on':
      lines.push('content consent: on (the prompt text is captured)')
      break
    case 'off':
      lines.push('content consent: off (turn Factory capture on in KyberDash Settings to capture the prompt text)')
      break
    case 'absent':
      lines.push('content consent: not set (no prompt text is captured until Factory capture is turned on)')
      break
    case 'invalid':
    case 'unreadable':
      lines.push('content consent: unreadable or malformed (treated as off)')
      break
  }
  return lines
}
