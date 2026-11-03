// Install, remove and report on the Factory (Droid) capture plugin (plan D3, D11, D13, D43,
// D44, D47).
//
// KyberDash owns one whole MARKETPLACE TREE under its own state directory:
//
//   <home>/.kyberdash/marketplaces/kyberdash/
//   ├── .factory-plugin/marketplace.json
//   └── kyberdash-factory/
//       ├── .factory-plugin/plugin.json
//       └── hooks/
//           ├── hooks.json
//           └── kyberdash-factory.mjs
//
// as ONE unit with ONE receipt (`~/.kyberdash/factory-plugin-receipt.json`) that names the
// marketplace, the plugin's identity and the tree root, and holds the SHA-256 digest of
// every file Kyber wrote — the marketplace manifest included.
//
// The tree is not the installation: Factory loads a plugin from a marketplace, so the
// install drives the harness's OWN commands, in this order:
//
//   droid plugin marketplace add <marketplace root>
//   droid plugin install kyberdash-factory@kyberdash --scope user
//
// and the uninstall drives the harness's own uninstall and marketplace removal FIRST, and
// only then removes the Kyber-owned tree and the receipt. The harness's CLI is the only
// interface to the harness's state: the owner's own Factory files — the user's
// `~/.factory/settings.json`, the user's `~/.factory/hooks.json` and the project's
// `.factory/hooks.json` — are never read, written, merged or restored (ADR 0026, ADR 0031,
// D48), and the harness's own plugin cache is the harness's file (D44).
//
// Two install modes, the OpenCode installer's semantics extended from one file to a tree:
//
//   strict    `capture enable`: refuses a foreign tree (one holding a file Kyber did not
//             write, or one whose files are not the files Kyber writes), an owner-edited
//             tree (one Kyber wrote whose bytes no longer match the receipt) and a harness
//             whose registry cannot be read, with exit 1, and writes nothing at all.
//   preserve  Squad and `kyber-weave update`: keeps such a tree, warns, and exits 0.
//
// The Factory root (`$FACTORY_DIR`, else `~/.factory`) is named for READING only: nothing
// is written under it any more. The ONE thing that is removed from it is the plugin tree
// the PREVIOUS route wrote there — KyberDash's own artifact from its own earlier route —
// and only when the receipt that route wrote proves the tree is still KyberDash's.

import {
  closeSync,
  constants,
  fstatSync,
  lstatSync,
  openSync,
  readdirSync,
  readFileSync,
  rmSync,
} from 'node:fs'
import { isAbsolute, basename, dirname, join, sep } from 'node:path'

import { createConsentIfAbsent, readConsentState, writeFileAtomic } from '../../consent-file.js'
import { envJoin, type PathEnv } from '../../paths.js'
import { sha256Hex } from '../../receipt.js'
import {
  FACTORY_HARNESS_EXECUTABLE,
  FACTORY_MARKETPLACE_CONTENT,
  FACTORY_MARKETPLACE_FILES,
  FACTORY_MARKETPLACE_NAME,
  FACTORY_PLUGIN_IDENTITY,
  marketplaceInstallCommands,
  marketplaceUninstallCommands,
  readFactoryHarnessResidue,
  readFactoryHarnessState,
  resolveFactoryHarnessCli,
  writeMarketplaceTree,
  type FactoryHarnessCli,
  type FactoryHarnessStateReport,
} from './marketplace.js'

export type FactoryPluginMode = 'strict' | 'preserve'

export type FactoryPluginOptions = {
  mode?: FactoryPluginMode
  dryRun?: boolean
  /**
   * Act only when the harness's own registry reports the plugin installed (the update
   * path). The harness's registry is the install's evidence; the tree on disk is not.
   */
  ifInstalled?: boolean
  /** Injectable for tests; defaults to `process.env`. */
  processEnv?: NodeJS.ProcessEnv
  /**
   * The harness's CLI, as the install reaches it. A test injects a fake that records the
   * argv; production resolves `droid` from PATH.
   */
  harnessCli?: FactoryHarnessCli
}

export type FactoryPluginPaths = {
  /** The Factory root. Named for reading only: KyberDash writes nothing under it. */
  root: string
  /** The Kyber-owned marketplace tree: the unit the receipt digests. */
  marketplaceRoot: string
  /** The marketplace tree Kyber owns. The tree's directory name IS the registered name. */
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
  | 'upgraded'
  | 'unchanged'
  | 'kept-conflict'
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

type Receipt = {
  version: 1
  /**
   * The marketplace the tree is registered as, and the plugin's identity in the harness.
   * A receipt the PREVIOUS route wrote carries neither: that route wrote a plugin tree
   * into the Factory root, and the harness never registered it.
   */
  marketplace?: string
  plugin?: string
  /** The tree root. */
  root: string
  /** Every file Kyber owns, and its digest. */
  files: { path: string; sha256: string }[]
}

/**
 * The plugin directory the PREVIOUS route wrote into the Factory root. That route wrote
 * the tree straight into the harness's own directory, where the harness never loaded it.
 */
const LEGACY_PLUGIN_DIR_NAME = 'kyberdash-factory'

/** The files the previous route owned, relative to its plugin directory. */
const LEGACY_OWNED_FILES = [
  '.factory-plugin/plugin.json',
  'hooks/hooks.json',
  'hooks/kyberdash-factory.mjs',
]

/** The cap on one file the installer reads. Kyber's own files are far smaller. */
const MAX_FILE_BYTES = 1024 * 1024

/** The cap on the files the installer reads in one tree. */
const MAX_TREE_FILES = 64

/** Where the marketplace tree, its receipt and its consent file live. */
export function factoryPluginPaths(
  home: string,
  env?: PathEnv,
  options: Pick<FactoryPluginOptions, 'processEnv'> = {},
): FactoryPluginPaths {
  const processEnv = options.processEnv ?? process.env
  const fromEnv = processEnv['FACTORY_DIR']
  const root = fromEnv !== undefined && fromEnv !== '' ? fromEnv : envJoin(env, home, '.factory')
  const marketplaceRoot = envJoin(env, home, '.kyberdash', 'marketplaces', FACTORY_MARKETPLACE_NAME)
  return {
    root,
    marketplaceRoot,
    pluginDir: marketplaceRoot,
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
    // The previous route's receipt named a tree and its digests, and no marketplace and no
    // plugin identity: the tree it wrote was never registered with the harness. Both are
    // optional here so that receipt is READ rather than refused.
    if (parsed.marketplace !== undefined && typeof parsed.marketplace !== 'string') return undefined
    if (parsed.plugin !== undefined && typeof parsed.plugin !== 'string') return undefined
    const files: { path: string; sha256: string }[] = []
    for (const entry of parsed.files) {
      if (typeof entry?.path !== 'string' || typeof entry?.sha256 !== 'string') return undefined
      files.push({ path: entry.path, sha256: entry.sha256 })
    }
    return {
      version: 1,
      ...(parsed.marketplace !== undefined ? { marketplace: parsed.marketplace } : {}),
      ...(parsed.plugin !== undefined ? { plugin: parsed.plugin } : {}),
      root: parsed.root,
      files,
    }
  } catch {
    // A missing or unreadable receipt means nothing was recorded.
    return undefined
  }
}

/** A receipt the PREVIOUS route wrote: it names a tree and its digests, and no marketplace. */
type LegacyReceipt = {
  version: 1
  marketplace?: undefined
  plugin?: undefined
  root: string
  files: { path: string; sha256: string }[]
}

function isLegacyReceipt(receipt: Receipt | undefined): receipt is LegacyReceipt {
  return receipt !== undefined && receipt.marketplace === undefined && receipt.plugin === undefined
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

/** The receipt Kyber writes: the marketplace, the identity, the tree, and every digest. */
function receiptFor(paths: FactoryPluginPaths): Receipt {
  return {
    version: 1,
    marketplace: FACTORY_MARKETPLACE_NAME,
    plugin: FACTORY_PLUGIN_IDENTITY,
    root: paths.pluginDir,
    files: FACTORY_MARKETPLACE_FILES.map((relative) => ({
      path: ownedPath(paths.pluginDir, relative),
      sha256: sha256Hex(FACTORY_MARKETPLACE_CONTENT[relative] ?? ''),
    })),
  }
}

/**
 * The plugin identity the receipt proves is KyberDash's own, or undefined when the receipt
 * proves nothing of the sort.
 *
 * The receipt is the proof that survives the harness's own teardown: it names the
 * marketplace KyberDash registered, the identity it installed and the tree it describes.
 * Every part of that has to hold — a receipt that names another tree, another marketplace
 * or no identity at all proves nothing, and an arbitrary plugin identity must never be
 * treated as ours.
 */
function provenPluginIdentity(
  receipt: Receipt | undefined,
  paths: FactoryPluginPaths,
): string | undefined {
  if (receipt === undefined || isLegacyReceipt(receipt)) return undefined
  if (receipt.root !== paths.pluginDir) return undefined
  if (receipt.marketplace !== FACTORY_MARKETPLACE_NAME) return undefined
  if (receipt.plugin !== FACTORY_PLUGIN_IDENTITY) return undefined
  return receipt.plugin
}

// ---------------------------------------------------------------------------------------------
// The names the status surface shows

/** The cap on an echoed name, so one status line stays one line. */
const MAX_DISPLAY_NAME_CHARS = 200

/**
 * A name as the status surface shows it.
 *
 * The names this module echoes come out of the OWNER's tree, and a file name may hold a
 * newline or a control character. A control character (C0, DEL, C1), a line or paragraph
 * separator, or a BiDi control is replaced with a space, so a crafted name cannot break or
 * spoof the line-based status surface, and the name is capped so one line stays
 * one line. The name itself is kept, so the owner can still recognise the file a refusal is
 * about.
 */
function displayName(name: string): string {
  const cleaned = name.replace(/[\u0000-\u001f\u007f-\u009f\u061c\u200e\u200f\u2028\u2029\u202a-\u202e\u2066-\u2069]/g, ' ')
  return cleaned.length > MAX_DISPLAY_NAME_CHARS ? `${cleaned.slice(0, MAX_DISPLAY_NAME_CHARS)}…` : cleaned
}

/** The names as the status surface shows them, joined as one line reads them. */
function displayNames(names: readonly string[]): string {
  return names.map((name) => displayName(name)).join(', ')
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
    if (!stat.isFile()) return { ok: false, detail: `${displayName(path)} is not a regular file` }
    if (stat.size > MAX_FILE_BYTES) {
      return {
        ok: false,
        detail: `${displayName(path)} is larger than the ${MAX_FILE_BYTES} byte read bound`,
      }
    }
    return { ok: true, text: readFileSync(fd, 'utf8') }
  } catch {
    return { ok: false, detail: `${displayName(path)} cannot be read as a file` }
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
    return { kind: 'unreadable', detail: `${displayName(pluginDir)} cannot be read` }
  }
  if (rootStat.isSymbolicLink()) {
    return { kind: 'unreadable', detail: `${displayName(pluginDir)} is a symlink` }
  }
  if (!rootStat.isDirectory()) {
    return { kind: 'unreadable', detail: `${displayName(pluginDir)} is not a directory` }
  }

  const files: { relative: string; text: string }[] = []
  const walk = (dir: string, prefix: string): TreeRead | undefined => {
    let entries
    try {
      entries = readdirSync(dir, { withFileTypes: true })
    } catch {
      return { kind: 'unreadable', detail: `${displayName(dir)} cannot be read` }
    }
    for (const entry of entries) {
      const relative = prefix === '' ? entry.name : `${prefix}/${entry.name}`
      const path = join(dir, entry.name)
      if (entry.isSymbolicLink()) {
        return { kind: 'unreadable', detail: `${displayName(path)} is a symlink` }
      }
      if (entry.isDirectory()) {
        const deeper = walk(path, relative)
        if (deeper !== undefined) return deeper
        continue
      }
      if (!entry.isFile()) {
        return { kind: 'unreadable', detail: `${displayName(path)} is not a regular file` }
      }
      if (files.length >= MAX_TREE_FILES) {
        return {
          kind: 'unreadable',
          detail: `${displayName(pluginDir)} holds more than ${MAX_TREE_FILES} files`,
        }
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
  | { kind: 'upgrade'; detail: string }
  | { kind: 'same'; receiptProven: boolean }

/**
 * Decide what the tree is.
 *
 *   absent     nothing is installed.
 *   unreadable the path, or a file in it, cannot be read as a regular file.
 *   foreign    the tree holds a file Kyber did not write, or files that are not the files
 *              Kyber writes. Nothing proves Kyber owns it.
 *   edited     the receipt proves Kyber wrote the tree, and a file no longer matches its
 *              digest.
 *   upgrade    the receipt proves Kyber wrote the tree, the owner has not touched it, and
 *              its content is the bundle KyberDash wrote BEFORE — the bundle has been
 *              superseded (D65).
 *   same       every file matches the receipt (or, without one, matches what Kyber writes).
 */
function classify(paths: FactoryPluginPaths): Classified {
  const tree = readTree(paths.pluginDir)
  if (tree.kind !== 'ok') return tree

  const found = new Map(tree.files.map((file) => [file.relative, file.text]))
  const owned = new Set<string>(FACTORY_MARKETPLACE_FILES)
  const extra = [...found.keys()].filter((relative) => !owned.has(relative)).sort()
  if (extra.length > 0) {
    return { kind: 'foreign', detail: `${displayNames(extra)} was not written by KyberDash` }
  }

  const receipt = readReceipt(paths.receiptFile)
  const proven =
    receipt !== undefined &&
    receipt.root === paths.pluginDir &&
    FACTORY_MARKETPLACE_FILES.every(
      (relative) => receiptDigest(receipt, paths.pluginDir, relative) !== undefined,
    )

  const missing = FACTORY_MARKETPLACE_FILES.filter((relative) => !found.has(relative))
  if (missing.length > 0) {
    return proven
      ? { kind: 'edited', detail: `${displayNames(missing)} is missing; it changed since install` }
      : { kind: 'foreign', detail: `${displayNames(missing)} is not a file KyberDash writes` }
  }

  if (proven && receipt !== undefined) {
    for (const relative of FACTORY_MARKETPLACE_FILES) {
      const digest = receiptDigest(receipt, paths.pluginDir, relative)
      if (digest !== undefined && sha256Hex(found.get(relative) ?? '') !== digest) {
        return { kind: 'edited', detail: `${displayName(relative)} changed since install` }
      }
    }
    // The tree is KyberDash's and the owner has not touched it. Is its content the CURRENT
    // bundle? If it is not, the bundle KyberDash wrote has been superseded and the tree is
    // an UPGRADE — not an owner edit, and not `unchanged` (D65).
    const superseded = FACTORY_MARKETPLACE_FILES.filter(
      (relative) => found.get(relative) !== FACTORY_MARKETPLACE_CONTENT[relative],
    )
    if (superseded.length > 0) {
      return {
        kind: 'upgrade',
        detail: `${displayNames(superseded)} holds the bundle KyberDash wrote before, not the current one`,
      }
    }
    return { kind: 'same', receiptProven: true }
  }

  for (const relative of FACTORY_MARKETPLACE_FILES) {
    if (found.get(relative) !== FACTORY_MARKETPLACE_CONTENT[relative]) {
      return { kind: 'foreign', detail: `${displayName(relative)} is not the file KyberDash writes` }
    }
  }
  // The tree holds exactly what KyberDash writes, but nothing proves KyberDash wrote it.
  return { kind: 'same', receiptProven: false }
}

// ---------------------------------------------------------------------------------------------
// The tree the previous route wrote

/** Where the previous route wrote its plugin tree: `<Factory root>/plugins/kyberdash-factory`. */
function legacyPluginDir(paths: FactoryPluginPaths): string {
  return join(paths.root, 'plugins', LEGACY_PLUGIN_DIR_NAME)
}

/**
 * Whether a path is a plugin directory of the shape the previous route wrote:
 * `<something>/plugins/kyberdash-factory`. The receipt names the tree to remove, and the
 * shape check keeps a receipt that was edited by hand from naming an arbitrary directory.
 */
function isLegacyPluginPath(path: string): boolean {
  return basename(dirname(path)) === 'plugins' && basename(path) === LEGACY_PLUGIN_DIR_NAME
}

/** The path of a file relative to a root, or undefined when the file is not under that root. */
function relativeTo(root: string, absolute: string): string | undefined {
  const prefix = root.endsWith(sep) ? root : `${root}${sep}`
  if (!absolute.startsWith(prefix)) return undefined
  return absolute.slice(prefix.length).split(sep).join('/')
}

type LegacyTree =
  | { kind: 'absent' }
  | { kind: 'proven'; path: string }
  | { kind: 'unproven'; path: string; detail: string }

/**
 * Decide what the tree the previous route wrote is.
 *
 * The proof is the receipt that route wrote: it recorded a digest for every file it
 * wrote, so a tree whose files all match those digests is KyberDash's own artifact and
 * nothing else. A tree the receipt does not describe, and a tree whose bytes no longer
 * match, are the owner's to remove.
 */
function classifyLegacyTree(paths: FactoryPluginPaths, receipt: Receipt | undefined): LegacyTree {
  const recorded = isLegacyReceipt(receipt) && isLegacyPluginPath(receipt.root) ? receipt.root : undefined
  const path = recorded ?? legacyPluginDir(paths)
  const tree = readTree(path)
  if (tree.kind === 'absent') return { kind: 'absent' }
  if (tree.kind === 'unreadable') return { kind: 'unproven', path, detail: tree.detail }
  if (receipt === undefined || !isLegacyReceipt(receipt)) {
    return { kind: 'unproven', path, detail: 'no receipt describes this tree' }
  }
  const digests = new Map<string, string>()
  for (const entry of receipt.files) {
    const absolute = isAbsolute(entry.path) ? entry.path : join(receipt.root, entry.path)
    const relative = relativeTo(receipt.root, absolute)
    if (relative === undefined) {
      return { kind: 'unproven', path, detail: 'the receipt names a file outside the tree' }
    }
    digests.set(relative, entry.sha256)
  }
  for (const relative of LEGACY_OWNED_FILES) {
    if (!digests.has(relative)) {
      return { kind: 'unproven', path, detail: `the receipt does not record ${relative}` }
    }
  }
  const found = new Map(tree.files.map((file) => [file.relative, file.text]))
  if (found.size !== digests.size) {
    return { kind: 'unproven', path, detail: 'the tree holds a file the receipt does not record' }
  }
  for (const [relative, text] of found) {
    const digest = digests.get(relative)
    if (digest === undefined || sha256Hex(text) !== digest) {
      return { kind: 'unproven', path, detail: `${displayName(relative)} changed since it was installed` }
    }
  }
  return { kind: 'proven', path }
}

/** What the previous route's tree costs the caller: what to print, and what to remove. */
type LegacyOutcome = { out: string[]; err: string[] }

/**
 * Remove the tree the previous route wrote, when the receipt proves it is KyberDash's.
 *
 * A tree the receipt does not prove is the owner's: it is reported with the exact command
 * that removes it and left where it is.
 */
function clearLegacyTree(
  paths: FactoryPluginPaths,
  receipt: Receipt | undefined,
  platform: NodeJS.Platform | undefined,
  dryRun: boolean,
): LegacyOutcome {
  const tree = classifyLegacyTree(paths, receipt)
  if (tree.kind === 'absent') return { out: [], err: [] }
  if (tree.kind === 'unproven') {
    const command = platform === 'win32' ? `rmdir /s /q "${tree.path}"` : `rm -rf "${tree.path}"`
    return {
      out: [],
      err: [
        `kyberdash capture: warning: ${tree.path} was left in place (${tree.detail}); remove it yourself with: ${command}`,
      ],
    }
  }
  if (!dryRun) rmSync(tree.path, { recursive: true, force: true })
  return {
    out: [
      dryRun
        ? `factory plugin: would remove the previous install's plugin tree ${tree.path}`
        : `factory plugin: removed the previous install's plugin tree ${tree.path}`,
    ],
    err: [],
  }
}

// ---------------------------------------------------------------------------------------------
// Writing the tree

/**
 * Write the whole marketplace tree, or nothing of it.
 *
 * The tree writer stages every file as a temporary sibling and renames them into place only
 * once all of them are staged, so a tree that cannot be written in full leaves no partial
 * tree behind. The receipt is written after the tree has landed, never before.
 */
function writeTree(paths: FactoryPluginPaths): void {
  writeMarketplaceTree(paths.pluginDir)
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
  const harness = options.harnessCli ?? resolveFactoryHarnessCli({ processEnv: options.processEnv })
  const found = classify(paths)
  const preserve = mode === 'preserve'
  /** The receipt on disk, read before this install can overwrite it. */
  const receipt = readReceipt(paths.receiptFile)
  /** The plugin identity the receipt proves is KyberDash's own, when it proves one. */
  const provenIdentity = provenPluginIdentity(receipt, paths)
  /** What the previous route's tree costs this install: nothing until it is cleared. */
  let legacyLines: LegacyOutcome = { out: [], err: [] }

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
    case 'absent':
      break
  }

  // The harness's own registry is the install's evidence, and it is read through the
  // harness's own commands and nothing else. The tree root travels with the read because
  // the harness's SOURCE column is what decides whether a marketplace named `kyberdash`
  // is KyberDash's own (D46). The receipt's proven identity travels with it because the
  // harness removes a marketplace registration WITHOUT removing the plugin, so the plugin
  // can outlive the registration and is still KyberDash's own.
  const harnessState = readFactoryHarnessState(harness, {
    treeWritten: found.kind !== 'absent',
    marketplaceRoot: paths.pluginDir,
    ...(provenIdentity === undefined ? {} : { ownedPluginIdentity: provenIdentity }),
  })

  if (options.ifInstalled === true && harnessState.state !== 'plugin-installed') {
    // A CONFLICT is not an absence: the harness holds the name KyberDash wants, so the
    // skip must not report it as "not installed" — the owner would never learn that the
    // name has to be cleared. The cause names the exact command that removes the entry,
    // and it goes to STDOUT because the callers of this path (the updater and the Squad
    // deploy step) relay stdout and nothing else. A conflict is never acted on.
    if (harnessState.state === 'conflict') {
      const message = harnessState.cause ?? 'the harness holds a name KyberDash does not own'
      return result('kept-conflict', 'kept', {
        out: [`factory plugin: kept-conflict ${paths.pluginDir}`, `factory plugin: conflict ${message}`],
      })
    }
    return result('skipped', 'skipped', {
      out: [`factory plugin: skipped-not-installed ${paths.pluginDir}`],
    })
  }

  if (!harnessState.readable) {
    // A CONFLICT is a name the harness holds that KyberDash does not own, and it is
    // reported as a conflict: the registry, the entry and the exact command that removes
    // it are named. An unreadable state is a different thing and says so.
    if (harnessState.state === 'conflict') {
      const message = harnessState.cause ?? 'the harness holds a name KyberDash does not own'
      if (!preserve) return strictRefusal('conflict', message)
      return result('kept-conflict', 'kept', {
        out: [`factory plugin: kept-conflict ${paths.pluginDir}`],
        err: [`kyberdash capture: warning: ${message}`],
      })
    }
    const message = `the Factory harness's state could not be read: ${harnessState.cause ?? 'unknown cause'}; nothing was written`
    if (!preserve) return strictRefusal('unreadable', message)
    return result('kept-unreadable', 'kept', {
      out: [`factory plugin: kept-unreadable ${paths.pluginDir}`],
      err: [`kyberdash capture: warning: ${message}`],
    })
  }

  if (dryRun) {
    const legacy = clearLegacyTree(paths, receipt, env?.platform, true)
    if (found.kind === 'upgrade') {
      return result('upgraded', 'would-write', {
        consent: ensureConsent(paths, true),
        out: [`factory plugin: would-upgrade ${paths.pluginDir}`, ...legacy.out],
        err: legacy.err,
      })
    }
    return result('installed', 'would-write', {
      consent: ensureConsent(paths, true),
      out: [`factory plugin: would-install ${paths.pluginDir}`, ...legacy.out],
      err: legacy.err,
    })
  }

  if (found.kind === 'absent') {
    // The tree the PREVIOUS route wrote into the Factory root goes first: the receipt that
    // route wrote is the proof that the tree is KyberDash's, and the receipt written below
    // would lose that proof.
    const legacy = clearLegacyTree(paths, receipt, env?.platform, false)
    // The tree lands before the receipt, and the receipt before the harness is asked to
    // take the tree: a receipt never describes a tree that is not there.
    writeTree(paths)
    writeReceipt(paths)
    legacyLines = legacy
  }

  if (found.kind === 'upgrade') {
    // The tree KyberDash wrote is superseded: the tree and its receipt are rewritten with
    // the current bundle, and the harness's CACHED COPY — the copy that actually runs — is
    // refreshed by the harness's own command below (D65).
    writeTree(paths)
    writeReceipt(paths)
  } else if (found.kind === 'same' && !found.receiptProven) {
    // The tree holds exactly what KyberDash writes, but nothing proves KyberDash wrote it,
    // so the receipt that proves it — and makes the NEXT upgrade possible — is written now
    // (D67).
    writeReceipt(paths)
  }

  // The harness's CACHED COPY — the copy that actually runs — goes stale in two cases:
  // the tree's content changed, and the tree was just written while the harness already
  // held the plugin. The harness state was read BEFORE the tree was written, because the
  // registry is the harness's own state and not the tree's.
  const upgraded = found.kind === 'upgrade'
  const refresh =
    upgraded || (found.kind === 'absent' && harnessState.state === 'plugin-installed')

  if (!refresh && found.kind === 'same' && harnessState.state === 'plugin-installed') {
    // The tree is current and the harness already holds the plugin: there is nothing to do,
    // and a routine update mutates nothing.
    return result('unchanged', 'noop', {
      consent: ensureConsent(paths, false),
      out: [`factory plugin: unchanged ${paths.pluginDir}`],
    })
  }

  const commands = marketplaceInstallCommands(harnessState, paths.pluginDir, { refresh })
  for (const argv of commands) {
    const answer = harness.run(argv)
    if (answer.exitCode !== 0) {
      const message = `the Factory harness refused "${argv.join(' ')}" (exit ${answer.exitCode})${answer.stderr.trim() === '' ? '' : `: ${answer.stderr.trim().split('\n')[0]}`}`
      if (!preserve) return strictRefusal('unreadable', message)
      return result('kept-unreadable', 'kept', {
        out: [`factory plugin: kept-unreadable ${paths.pluginDir}`, ...legacyLines.out],
        err: [`kyberdash capture: warning: ${message}`, ...legacyLines.err],
      })
    }
  }

  const executable = harness.executable ?? FACTORY_HARNESS_EXECUTABLE
  // The wording names what happened to the TREE: an upgrade of the tree reads `upgraded`,
  // and a tree that was written fresh reads `installed` even when the harness's cached
  // copy was refreshed with it.
  return result(upgraded ? 'upgraded' : 'installed', 'wrote', {
    consent: ensureConsent(paths, false),
    out: [
      `factory plugin: ${upgraded ? 'upgraded' : 'installed'} ${paths.pluginDir}`,
      `factory marketplace: ${FACTORY_MARKETPLACE_NAME}`,
      `factory plugin identity: ${FACTORY_PLUGIN_IDENTITY}`,
      `factory harness: ${executable}`,
      ...legacyLines.out,
    ],
    err: legacyLines.err,
  })
}

export function uninstallFactoryPlugin(
  home: string,
  env?: PathEnv,
  options: Pick<FactoryPluginOptions, 'dryRun' | 'processEnv' | 'harnessCli'> = {},
): FactoryPluginResult {
  const paths = factoryPluginPaths(home, env, options)
  const harness = options.harnessCli ?? resolveFactoryHarnessCli({ processEnv: options.processEnv })
  const found = classify(paths)
  /** The receipt on disk, read before this uninstall can remove it. */
  const receipt = readReceipt(paths.receiptFile)
  /** The plugin identity the receipt proves is KyberDash's own, when it proves one. */
  const provenIdentity = provenPluginIdentity(receipt, paths)

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
    const legacy = clearLegacyTree(paths, receipt, env?.platform, true)
    return result('skipped', 'would-restore', {
      out: [`factory plugin: would remove capture plugin ${paths.pluginDir}`, ...legacy.out],
      err: legacy.err,
    })
  }

  // The harness-side state goes first: the harness's own uninstall, then the harness's own
  // marketplace removal. The Kyber-owned tree and the receipt follow.
  //
  // The receipt's proven identity travels with the read, because the receipt is the proof
  // that survives the harness's own teardown: the harness removes a marketplace
  // registration WITHOUT removing the plugin, so a plugin the receipt proves is ours is
  // ours here too, and the uninstall removes it instead of leaving it capturing.
  const harnessState = readFactoryHarnessState(harness, {
    treeWritten: found.kind !== 'absent',
    marketplaceRoot: paths.pluginDir,
    ...(provenIdentity === undefined ? {} : { ownedPluginIdentity: provenIdentity }),
  })
  if (!harnessState.readable) {
    // A conflict is not an unreadable state: the entry that holds KyberDash's name belongs
    // to somebody else, so the harness's commands are not run against it — but KyberDash's
    // OWN tree is KyberDash's, and the receipt proves it, so it is still removed.
    if (harnessState.state === 'conflict') {
      const message = harnessState.cause ?? 'the harness holds a name KyberDash does not own'
      rmSync(paths.pluginDir, { recursive: true, force: true })
      rmSync(paths.receiptFile, { force: true })
      return result('skipped', 'restored', {
        out: [
          `factory plugin: removed capture plugin ${paths.pluginDir}`,
          `factory harness: left alone (${message})`,
        ],
      })
    }
    const message = `the Factory harness's state could not be read: ${harnessState.cause ?? 'unknown cause'}; nothing was removed`
    return result('refused', 'refused', {
      exitCode: 1,
      reason: 'unreadable',
      err: [`kyberdash capture: ${message}`],
    })
  }
  // The registry as it stands BEFORE the teardown: the residue is what the teardown was
  // meant to remove and did not, and the owner's own disabled entry is only visible before
  // the harness's uninstall deletes it.
  const before = harnessState.registry
  for (const argv of marketplaceUninstallCommands(harnessState)) {
    const answer = harness.run(argv)
    if (answer.exitCode !== 0) {
      return result('refused', 'refused', {
        exitCode: 1,
        reason: 'unreadable',
        err: [
          `kyberdash capture: the Factory harness refused "${argv.join(' ')}" (exit ${answer.exitCode})${answer.stderr.trim() === '' ? '' : `: ${answer.stderr.trim().split('\n')[0]}`}`,
        ],
      })
    }
  }

  rmSync(paths.pluginDir, { recursive: true, force: true })
  // The tree the previous route wrote into the Factory root is KyberDash's own artifact
  // from its own earlier route: it is removed when the receipt proves it is ours, and
  // reported with the exact command when it does not.
  const legacy = clearLegacyTree(paths, receipt, env?.platform, false)
  rmSync(paths.receiptFile, { force: true })
  // The residue the harness's own teardown left behind is REPORTED, never patched: the
  // owner's settings file is never written by KyberDash (D48, D59).
  const residue = readFactoryHarnessResidue(harness, before)
  return result('skipped', 'restored', {
    out: [
      `factory plugin: removed capture plugin ${paths.pluginDir}`,
      ...legacy.out,
      ...residue.map((row) => `factory residue: ${row.detail} — ${row.command}`),
    ],
    err: legacy.err,
  })
}

// ---------------------------------------------------------------------------------------------
// The state and status surface

export type FactoryPluginState = {
  /**
   * on: the tree matches the receipt. off: nothing installed. partial: an older plugin of
   * ours. unknown: not ours, or edited.
   */
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
  // The receipt the PREVIOUS route wrote describes that route's tree in the Factory root,
  // not the marketplace tree: it is not this route's receipt.
  const recorded = receipt !== undefined && !isLegacyReceipt(receipt)
  const found = classify(paths)
  if (found.kind === 'unreadable') {
    return { state: 'unknown', exists: true, receipt: recorded, unreadable: true }
  }
  if (found.kind === 'absent') {
    return { state: 'off', exists: false, receipt: recorded, unreadable: false }
  }
  return {
    // A tree KyberDash wrote and the owner never touched, holding the bundle KyberDash
    // wrote before, is an OLDER VERSION of ours — not an owner edit and not a foreign tree.
    state: found.kind === 'same' ? 'on' : found.kind === 'upgrade' ? 'partial' : 'unknown',
    exists: true,
    receipt: recorded,
    unreadable: false,
  }
}

/**
 * The harness-side install state, read through the harness's OWN commands and nothing else.
 *
 * The caller supplies the harness CLI: reading it spawns the harness, so a caller that must
 * not spawn — a status read, for instance — does not call this.
 */
export function factoryHarnessState(
  home: string,
  env?: PathEnv,
  options: Pick<FactoryPluginOptions, 'processEnv' | 'harnessCli'> = {},
): FactoryHarnessStateReport {
  const paths = factoryPluginPaths(home, env, options)
  const harness = options.harnessCli ?? resolveFactoryHarnessCli({ processEnv: options.processEnv })
  // The receipt's proven identity travels with the read, so this reports the same
  // ownership the install does: a plugin the receipt proves is ours is ours here too.
  const provenIdentity = provenPluginIdentity(readReceipt(paths.receiptFile), paths)
  return readFactoryHarnessState(harness, {
    treeWritten: classify(paths).kind !== 'absent',
    ...(provenIdentity === undefined ? {} : { ownedPluginIdentity: provenIdentity }),
  })
}

/** Fixed-format status lines. Never echoes owner-written text. */
export function describeFactoryPluginStatus(
  home: string,
  env?: PathEnv,
  options: Pick<FactoryPluginOptions, 'processEnv' | 'harnessCli'> = {},
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
  } else if (plugin.state === 'partial') {
    lines.push('plugin: installed (an older version than this build)')
  } else {
    lines.push('plugin: modified since install or not written by KyberDash')
  }
  lines.push(`factory marketplace: ${FACTORY_MARKETPLACE_NAME}`)
  lines.push(`factory plugin identity: ${FACTORY_PLUGIN_IDENTITY}`)
  if (options.harnessCli !== undefined) {
    const harness = factoryHarnessState(home, env, options)
    lines.push(
      harness.cause === undefined
        ? `factory harness: ${harness.state}`
        : `factory harness: ${harness.state} (${harness.cause})`,
    )
  }
  const consent = readConsentState(paths.consentFile)
  switch (consent) {
    case 'on':
      lines.push('content consent: on (the prompt text is captured)')
      break
    case 'off':
      lines.push(
        'content consent: off (the Factory capture switch does not record the prompt text; turn it on with kyberdash kyber capture plugin consent --harness factory --on)',
      )
      break
    case 'absent':
      lines.push(
        'content consent: not set (no prompt text is captured; the Factory capture switch does not record it)',
      )
      break
    case 'invalid':
    case 'unreadable':
      lines.push('content consent: unreadable or malformed (treated as off)')
      break
  }
  return lines
}
