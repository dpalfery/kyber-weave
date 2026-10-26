// Install, remove and report on the OpenCode capture plugin (plan D4, D15, Q2, §3.4).
//
// KyberDash owns one whole file, `<OpenCode root>/plugins/kyberdash-opencode.js`, a receipt
// holding the digest of what it wrote (`~/.kyberdash/opencode-plugin-receipt.json`), and the
// consent file. Two install modes:
//
//   strict    `capture enable`: refuses a foreign file (conflict), an owner-edited file
//             (drift) and an unreadable path with exit 1, as the Kilo installer does.
//   preserve  Squad and `kyber-weave update`: never overwrites an edited or foreign plugin,
//             nor an older one no receipt proves unmodified, and never touches an existing consent file; it keeps them, warns, and exits 0.
//             A missing consent file is created with content off (Q2).
//
// Neither mode touches opencode.jsonc. The OpenCode root is `--config-dir`, else
// `$OPENCODE_CONFIG_DIR`, else `~/.config/opencode` (the precedence Squad uses).

import { lstatSync, mkdirSync, readFileSync, rmSync } from 'node:fs'
import { dirname, isAbsolute } from 'node:path'

import { envJoin, type PathEnv } from '../../paths.js'
import { sha256Hex } from '../../receipt.js'
import { createConsentIfAbsent, readConsentState, writeContentConsent, writeFileAtomic } from './consent-write.js'
import { OPENCODE_PLUGIN_MARKER, OPENCODE_PLUGIN_SOURCE } from './generated/plugin-source.js'

export type OpencodePluginMode = 'strict' | 'preserve'

export type OpencodePluginOptions = {
  mode?: OpencodePluginMode
  dryRun?: boolean
  /** Act only when a plugin file is already present (the update path). */
  ifInstalled?: boolean
  /** `--config-dir`: the OpenCode root. Wins over `$OPENCODE_CONFIG_DIR`. */
  configDir?: string
  /** Injectable for tests; defaults to `process.env`. */
  processEnv?: NodeJS.ProcessEnv
}

export type OpencodePluginPaths = {
  root: string
  pluginFile: string
  receiptFile: string
  consentFile: string
}

export type OpencodePluginOutcome =
  | 'wrote'
  | 'would-write'
  | 'restored'
  | 'would-restore'
  | 'noop'
  | 'refused'
  | 'kept'
  | 'skipped'

/** What the install decided about the plugin file. */
export type OpencodePluginDisposition =
  | 'installed'
  | 'upgraded'
  | 'unchanged'
  | 'kept-edited'
  | 'kept-newer'
  | 'kept-foreign'
  | 'kept-unreadable'
  | 'skipped'
  | 'refused'

export type OpencodeConsentOutcome = 'created' | 'would-create' | 'kept' | 'none'

export type OpencodePluginResult = {
  exitCode: number
  outcome: OpencodePluginOutcome
  disposition: OpencodePluginDisposition
  consent: OpencodeConsentOutcome
  /** Why a strict refusal happened: `conflict` (not ours), `drift` (owner edit), `unreadable`. */
  reason?: 'conflict' | 'drift' | 'unreadable'
  out: string[]
  err: string[]
}

type Receipt = { version: 1; path: string; sha256: string }

const MARKER_PREFIX = OPENCODE_PLUGIN_MARKER.replace(/\s*v\d+\s*$/, '')

export const OPENCODE_PLUGIN_FILE_NAME = 'kyberdash-opencode.js'

/** Where the plugin, its receipt and the consent file live. */
export function opencodePluginPaths(
  home: string,
  env?: PathEnv,
  options: Pick<OpencodePluginOptions, 'configDir' | 'processEnv'> = {},
): OpencodePluginPaths {
  const processEnv = options.processEnv ?? process.env
  const fromEnv = processEnv['OPENCODE_CONFIG_DIR']
  const root =
    options.configDir !== undefined && options.configDir !== ''
      ? options.configDir
      : fromEnv !== undefined && fromEnv !== ''
        ? fromEnv
        : envJoin(env, home, '.config', 'opencode')
  return {
    root,
    pluginFile: envJoin(env, root, 'plugins', OPENCODE_PLUGIN_FILE_NAME),
    receiptFile: envJoin(env, home, '.kyberdash', 'opencode-plugin-receipt.json'),
    consentFile: envJoin(env, home, '.kyberdash', 'opencode-plugin.json'),
  }
}

type Slot = { kind: 'absent' } | { kind: 'file'; text: string } | { kind: 'unreadable' }

/**
 * Never throws: a directory, a symlink (live or dangling), any other non-regular entry or a
 * permission error is `unreadable`, so an install never writes or removes through a link.
 */
function readSlot(path: string): Slot {
  try {
    const stat = lstatSync(path, { throwIfNoEntry: false })
    if (stat === undefined) return { kind: 'absent' }
    if (!stat.isFile()) return { kind: 'unreadable' }
    return { kind: 'file', text: readFileSync(path, 'utf8') }
  } catch {
    return { kind: 'unreadable' }
  }
}

function readReceipt(path: string): Receipt | undefined {
  try {
    const parsed = JSON.parse(readFileSync(path, 'utf8')) as Partial<Receipt>
    if (parsed.version === 1 && typeof parsed.path === 'string' && typeof parsed.sha256 === 'string') {
      return parsed as Receipt
    }
  } catch {
    // a missing or unreadable receipt means nothing was recorded
  }
  return undefined
}

/**
 * Status and uninstall may be asked from a process that does not share the installer's
 * `--config-dir` or `$OPENCODE_CONFIG_DIR` (Squad installs into a custom root; the Settings
 * server and `disable` run elsewhere). When the resolved root holds no plugin and no explicit
 * `--config-dir` was given, follow the path the receipt recorded. Only a recorded path that
 * names our plugin file is followed, so a damaged receipt cannot aim a removal elsewhere.
 */
function withReceiptFallback(
  paths: OpencodePluginPaths,
  options: Pick<OpencodePluginOptions, 'configDir'>,
): OpencodePluginPaths {
  if (options.configDir !== undefined && options.configDir !== '') return paths
  if (lstatSync(paths.pluginFile, { throwIfNoEntry: false }) !== undefined) return paths
  const recorded = readReceipt(paths.receiptFile)?.path
  if (recorded === undefined || recorded === paths.pluginFile) return paths
  return isOurRecordedPlugin(recorded) ? { ...paths, pluginFile: recorded } : paths
}

/**
 * A recorded path is followed only when it has our shape (absolute, `.../plugins/kyberdash-opencode.js`)
 * and our content (a regular file, not a link, carrying the plugin marker).
 */
function isOurRecordedPlugin(recorded: string): boolean {
  if (!(isAbsolute(recorded) || /^[A-Za-z]:[\\/]/.test(recorded))) return false
  const parts = recorded.replace(/\\/g, '/').split('/')
  if (parts[parts.length - 1] !== OPENCODE_PLUGIN_FILE_NAME || parts[parts.length - 2] !== 'plugins') return false
  const slot = readSlot(recorded)
  return slot.kind === 'file' && slot.text.startsWith(MARKER_PREFIX)
}

function versionOf(text: string): number | undefined {
  const match = /^\/\/ kyberdash-opencode-plugin v(\d+)/.exec(text)
  return match === null ? undefined : Number(match[1])
}

const OUR_VERSION = versionOf(OPENCODE_PLUGIN_MARKER) ?? 0

type Classified =
  | { kind: 'absent' }
  | { kind: 'unreadable' }
  | { kind: 'foreign' }
  | { kind: 'same' }
  | { kind: 'edited' }
  | { kind: 'newer' }
  | { kind: 'older'; proven: boolean }

function classify(slot: Slot, receipt: Receipt | undefined, pluginFile: string): Classified {
  if (slot.kind !== 'file') return { kind: slot.kind }
  const text = slot.text
  if (!text.startsWith(MARKER_PREFIX)) return { kind: 'foreign' }
  if (text === OPENCODE_PLUGIN_SOURCE) return { kind: 'same' }
  // With no receipt for this path the marker is all that says the file is ours, and nothing
  // proves the owner has not edited it. Only a matching receipt whose digest still holds proves
  // an older file is the unmodified plugin we wrote.
  const hasReceipt = receipt !== undefined && receipt.path === pluginFile
  if (hasReceipt && sha256Hex(text) !== receipt.sha256) return { kind: 'edited' }
  const version = versionOf(text)
  return version !== undefined && version > OUR_VERSION
    ? { kind: 'newer' }
    : { kind: 'older', proven: hasReceipt }
}

function result(
  disposition: OpencodePluginDisposition,
  outcome: OpencodePluginOutcome,
  fields: Partial<Pick<OpencodePluginResult, 'exitCode' | 'reason' | 'consent' | 'out' | 'err'>> = {},
): OpencodePluginResult {
  return { exitCode: 0, consent: 'none', out: [], err: [], ...fields, disposition, outcome }
}

function writePlugin(paths: OpencodePluginPaths): void {
  mkdirSync(dirname(paths.pluginFile), { recursive: true })
  writeFileAtomic(paths.pluginFile, OPENCODE_PLUGIN_SOURCE)
  const receipt: Receipt = { version: 1, path: paths.pluginFile, sha256: sha256Hex(OPENCODE_PLUGIN_SOURCE) }
  writeFileAtomic(paths.receiptFile, `${JSON.stringify(receipt, null, 2)}\n`)
}

/** Preserve mode creates the consent file with content off, only when nothing is there. */
function ensureConsent(paths: OpencodePluginPaths, dryRun: boolean): OpencodeConsentOutcome {
  const state = readConsentState(paths.consentFile)
  if (state !== 'absent') return 'kept'
  if (dryRun) return 'would-create'
  return createConsentIfAbsent(paths.consentFile) === 'created' ? 'created' : 'kept'
}

export const OPENCODE_CONSENT_REMINDER =
  'harness opencode: system prompt, tool definitions and skills are captured only after OpenCode capture is turned on in KyberDash Settings'

export function installOpencodePlugin(
  home: string,
  env?: PathEnv,
  options: OpencodePluginOptions = {},
): OpencodePluginResult {
  const mode = options.mode ?? 'strict'
  const dryRun = options.dryRun === true
  const paths = opencodePluginPaths(home, env, options)
  const { pluginFile } = paths
  const slot = readSlot(pluginFile)
  const found = classify(slot, readReceipt(paths.receiptFile), pluginFile)

  if (found.kind === 'absent' && options.ifInstalled === true) {
    return result('skipped', 'skipped', {
      out: [`opencode plugin: skipped-not-installed ${pluginFile}`],
    })
  }

  const preserve = mode === 'preserve'
  const strictRefusal = (
    reason: 'conflict' | 'drift' | 'unreadable',
    message: string,
  ): OpencodePluginResult =>
    result('refused', 'refused', { exitCode: 1, reason, err: [`kyberdash capture: ${message}`] })

  switch (found.kind) {
    case 'unreadable': {
      const message = `${pluginFile} cannot be read as a file; leaving it in place`
      if (!preserve) return strictRefusal('unreadable', message)
      return result('kept-unreadable', 'kept', {
        out: [`opencode plugin: kept-unreadable ${pluginFile}`],
        err: [`kyberdash capture: warning: ${message}`],
      })
    }
    case 'foreign': {
      if (!preserve) {
        return strictRefusal('conflict', `refusing to overwrite ${pluginFile}: it was not written by KyberDash`)
      }
      return result('kept-foreign', 'kept', {
        out: [`opencode plugin: kept-foreign ${pluginFile}`],
        err: [`kyberdash capture: warning: ${pluginFile} was not written by KyberDash; leaving it in place`],
      })
    }
    case 'edited': {
      if (!preserve) {
        return strictRefusal('drift', `refusing to overwrite ${pluginFile}: it changed since install`)
      }
      return result('kept-edited', 'kept', {
        consent: ensureConsent(paths, dryRun),
        out: [`opencode plugin: kept-edited ${pluginFile}`],
        err: [`kyberdash capture: warning: ${pluginFile} was edited since install; leaving it in place`],
      })
    }
    case 'older':
      if (preserve && !found.proven) {
        return result('kept-edited', 'kept', {
          consent: ensureConsent(paths, dryRun),
          out: [`opencode plugin: kept-edited ${pluginFile}`],
          err: [
            `kyberdash capture: warning: ${pluginFile} is an older KyberDash plugin but no receipt proves it is unmodified; leaving it in place. Run \`kyberdash kyber capture plugin install --harness opencode\` or delete it to upgrade.`,
          ],
        })
      }
      return installOrUpgrade(true)
    case 'newer':
      return result('kept-newer', 'noop', {
        consent: preserve ? ensureConsent(paths, dryRun) : 'none',
        out: [`opencode plugin: kept-newer ${pluginFile}`],
      })
    case 'same':
      return result('unchanged', 'noop', {
        consent: preserve ? ensureConsent(paths, dryRun) : 'none',
        out: [`opencode plugin: unchanged ${pluginFile}`],
      })
    case 'absent':
      return installOrUpgrade(false)
  }

  function installOrUpgrade(upgrade: boolean): OpencodePluginResult {
    const disposition = upgrade ? 'upgraded' : 'installed'
    if (dryRun) {
      return result(disposition, 'would-write', {
        consent: preserve ? ensureConsent(paths, true) : 'none',
        out: [`opencode plugin: ${upgrade ? 'would-upgrade' : 'would-install'} ${pluginFile}`],
      })
    }
    writePlugin(paths)
    return result(disposition, 'wrote', {
      consent: preserve ? ensureConsent(paths, false) : 'none',
      out: [
        `opencode plugin: ${disposition} ${pluginFile}`,
        `harness opencode: restart OpenCode so it loads the ${upgrade ? 'upgraded ' : ''}capture plugin`,
      ],
    })
  }
}

export function uninstallOpencodePlugin(
  home: string,
  env?: PathEnv,
  options: Pick<OpencodePluginOptions, 'dryRun' | 'configDir' | 'processEnv'> = {},
): OpencodePluginResult {
  const paths = withReceiptFallback(opencodePluginPaths(home, env, options), options)
  const { pluginFile, receiptFile } = paths
  const slot = readSlot(pluginFile)
  if (slot.kind === 'unreadable') {
    return result('refused', 'refused', {
      exitCode: 1,
      reason: 'unreadable',
      err: [`kyberdash capture: ${pluginFile} cannot be read as a file; leaving it in place`],
    })
  }
  if (slot.kind === 'absent') {
    rmSync(receiptFile, { force: true })
    return result('skipped', 'noop', { out: ['harness opencode: capture plugin not installed; nothing to do'] })
  }
  if (!slot.text.startsWith(MARKER_PREFIX)) {
    return result('kept-foreign', 'noop', {
      out: [`harness opencode: ${pluginFile} was not written by KyberDash; leaving it in place`],
    })
  }
  // A receipt vouches only for the path it recorded. For any other file the baseline is the
  // current source, so a pristine current plugin in a new root uninstalls cleanly.
  const receipt = readReceipt(receiptFile)
  const ownsReceipt = receipt === undefined || receipt.path === pluginFile
  const recorded = receipt !== undefined && receipt.path === pluginFile ? receipt.sha256 : sha256Hex(OPENCODE_PLUGIN_SOURCE)
  if (sha256Hex(slot.text) !== recorded) {
    return result('refused', 'refused', {
      exitCode: 1,
      reason: 'drift',
      err: [`kyberdash capture: ${pluginFile} changed since install; leaving it in place. Delete it yourself to remove it.`],
    })
  }
  if (options.dryRun === true) {
    return result('skipped', 'would-restore', { out: [`harness opencode: would remove capture plugin ${pluginFile}`] })
  }
  rmSync(pluginFile)
  if (ownsReceipt) rmSync(receiptFile, { force: true })
  return result('skipped', 'restored', { out: [`harness opencode: removed capture plugin ${pluginFile}`] })
}

/** Settings switch on: content true. Settings switch off: content false. Creates the file if absent. */
export function setOpencodeContentConsent(
  home: string,
  content: boolean,
  options: Pick<OpencodePluginOptions, 'dryRun'> & { env?: PathEnv } = {},
): { out: string[] } {
  const { consentFile } = opencodePluginPaths(home, options.env)
  const word = content ? 'on' : 'off'
  if (options.dryRun === true) return { out: [`harness opencode: would set content consent ${word}`] }
  writeContentConsent(consentFile, content)
  return { out: [`harness opencode: content consent ${word}`] }
}

export type OpencodePluginState = {
  /** on: current and intact. partial: an older plugin of ours. off: absent. unknown: not ours or edited. */
  state: 'on' | 'off' | 'partial' | 'unknown'
  exists: boolean
  receipt: boolean
  unreadable: boolean
}

/** What Settings and `capture status` report. Reads two files and never throws. */
export function opencodePluginState(
  home: string,
  env?: PathEnv,
  options: Pick<OpencodePluginOptions, 'configDir' | 'processEnv'> = {},
): OpencodePluginState {
  const paths = withReceiptFallback(opencodePluginPaths(home, env, options), options)
  const receipt = readReceipt(paths.receiptFile)
  const slot = readSlot(paths.pluginFile)
  if (slot.kind === 'unreadable') return { state: 'unknown', exists: true, receipt: receipt !== undefined, unreadable: true }
  if (slot.kind === 'absent') return { state: 'off', exists: false, receipt: receipt !== undefined, unreadable: false }
  const found = classify(slot, receipt, paths.pluginFile)
  const state =
    found.kind === 'same' ? 'on' : found.kind === 'older' || found.kind === 'newer' ? 'partial' : 'unknown'
  return { state, exists: true, receipt: receipt !== undefined, unreadable: false }
}

/** Fixed-format status lines. Never echoes owner-written text. */
export function describeOpencodePluginStatus(
  home: string,
  env?: PathEnv,
  options: Pick<OpencodePluginOptions, 'configDir' | 'processEnv'> = {},
): string[] {
  const plugin = opencodePluginState(home, env, options)
  const lines: string[] = []
  if (plugin.unreadable) {
    lines.push('plugin: unreadable (not a regular file or not permitted)')
  } else if (plugin.state === 'off') {
    lines.push('plugin: not installed')
  } else if (plugin.state === 'on') {
    lines.push('plugin: installed')
  } else if (plugin.state === 'partial') {
    lines.push(`plugin: installed (different version; re-run enable to upgrade, current marker ${OPENCODE_PLUGIN_MARKER.slice(3)})`)
  } else {
    lines.push('plugin: modified since install or not written by KyberDash')
  }
  const consent = readConsentState(opencodePluginPaths(home, env, options).consentFile)
  switch (consent) {
    case 'on':
      lines.push('content consent: on (system prompt, tool definitions and skills are captured)')
      break
    case 'off':
      lines.push('content consent: off (turn OpenCode capture on in KyberDash Settings to capture the system prompt, tool definitions and skills)')
      break
    case 'absent':
      lines.push('content consent: not set (no system prompt, tool definitions or skills are captured until OpenCode capture is turned on in KyberDash Settings)')
      break
    case 'invalid':
    case 'unreadable':
      lines.push('content consent: unreadable or malformed (treated as off)')
      break
  }
  return lines
}
