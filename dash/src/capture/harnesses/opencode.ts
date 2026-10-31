// OpenCode capture writer: plugin-only.
//
// OpenCode reaches KyberDash through its capture plugin (`<OpenCode root>/plugins/
// kyberdash-opencode.js`) and the consent file beside the KyberDash receipts. Settings
// "OpenCode Enabled" is `kyberdash kyber capture enable --harness opencode`, which is a
// strict plugin install plus consent on; disable is consent off plus plugin removal. The
// writer declares no config key, so nothing is written to, read from, or receipted for
// opencode.jsonc any more.
//
// Older KyberDash builds wrote `experimental.openTelemetry: true` into opencode.jsonc and
// recorded the prior value in the capture receipt. `migrateLegacyOpencodeFlag` is the
// one-time cleanup of that key: enable and disable put the receipted prior value back and
// drop the receipt entry. A value the owner changed after KyberDash wrote it is left alone
// with a warning.
//
// The plugin id and its `plugins` config keys are still DECLARED (re-exported) so capture
// owns the contract, but they are never written: local file discovery installs the plugin
// by file.

import { readFileSync, writeFileSync } from 'node:fs'

import { revertJsonEdits } from '../edit-json.js'
import type { CaptureEvent } from '../index.js'
import { envJoin, type PathEnv } from '../paths.js'
import type { CaptureReceipt, ReceiptFileRecord } from '../receipt.js'

import {
  describeOpencodePluginStatus,
  installOpencodePlugin,
  opencodePluginPaths,
  opencodePluginState,
  setOpencodeContentConsent,
  uninstallOpencodePlugin,
  type OpencodePluginResult,
} from './opencode-plugin/install.js'
import type { ManagedFilePlugin, ManagedHarnessWriter } from './registry.js'

/** Strict mode only ever kept-newer/noop/refused/wrote; fold the preserve-only outcomes away. */
function narrow(result: OpencodePluginResult) {
  const outcome = result.outcome === 'kept' || result.outcome === 'skipped' ? 'noop' : result.outcome
  return {
    exitCode: result.exitCode,
    outcome,
    ...(result.reason !== undefined ? { reason: result.reason } : {}),
    out: result.out,
    err: result.err,
  } as const
}

const opencodeFilePlugin: ManagedFilePlugin = {
  install: (home, env, options) =>
    narrow(installOpencodePlugin(home, env, { mode: 'strict', dryRun: options.dryRun, ...(options.configDir !== undefined ? { configDir: options.configDir } : {}) })),
  uninstall: (home, env, options) =>
    narrow(uninstallOpencodePlugin(home, env, { dryRun: options.dryRun, ...(options.configDir !== undefined ? { configDir: options.configDir } : {}) })),
  path: (home, env, options) => opencodePluginPaths(home, env, { ...(options?.configDir !== undefined ? { configDir: options.configDir } : {}) }).pluginFile,
  state: (home, env, options) => opencodePluginState(home, env, { ...(options?.configDir !== undefined ? { configDir: options.configDir } : {}) }),
  describeStatus: (home, env, options) =>
    describeOpencodePluginStatus(home, env, { ...(options?.configDir !== undefined ? { configDir: options.configDir } : {}) }),
  consent: {
    set: (home, env, content, options) => setOpencodeContentConsent(home, content, { dryRun: options.dryRun, env }),
  },
}

/**
 * The plugin capture contract, declared here and never written. The dash CLI installs the
 * plugin file into OpenCode's own plugin directory (see `opencodePluginPaths`). Re-exported
 * so the writer module is the single ownership point for the plugin's capture keys.
 */
export { OPENCODE_PLUGIN_CONFIG_KEYS, OPENCODE_PLUGIN_ID } from './opencode-plugin/plugin.js'

/** The key older KyberDash builds wrote into opencode.jsonc. Enable no longer writes it. */
export const OPENCODE_LEGACY_OTEL_FLAG_KEY = 'experimental.openTelemetry'

const OPENCODE_HARNESS_ID = 'opencode'
const OPENCODE_FILE_ID = 'opencode-config'

/**
 * D14: win32 keeps the .config root — the seam changes the separators, not the root,
 * because OpenCode reads this location on every OS. The file is the migration target and
 * the install-detection evidence; KyberDash writes no key in it.
 */
function opencodeConfigPath(home: string, env?: PathEnv): string {
  return envJoin(env, home, '.config', 'opencode', 'opencode.jsonc')
}

/**
 * The receipt entry an older KyberDash left for the OpenCode flag, or undefined. Only the
 * entry for the config path this call resolves counts: a recorded path is never followed
 * to another file.
 */
export function legacyOpencodeFlagRecord(
  receipt: CaptureReceipt | null,
  configPath: string,
): ReceiptFileRecord | undefined {
  return receipt?.files.find((entry) => isLegacyFlagEntry(entry, configPath))
}

/**
 * `loadCaptureReceipt` checks only the version and that `files` is an array, so an entry is
 * untrusted here: a null entry, or one without a usable `keys` map or flag record, is not a
 * legacy record. The migration only warns, so a damaged receipt must not throw.
 */
function isLegacyFlagEntry(entry: unknown, configPath: string): entry is ReceiptFileRecord {
  if (entry === null || typeof entry !== 'object') return false
  const candidate = entry as Partial<Record<keyof ReceiptFileRecord, unknown>>
  if (candidate.harnessId !== OPENCODE_HARNESS_ID || candidate.path !== configPath) return false
  const keys = candidate.keys
  if (keys === null || typeof keys !== 'object' || Array.isArray(keys)) return false
  if (!Object.hasOwn(keys, OPENCODE_LEGACY_OTEL_FLAG_KEY)) return false
  const flag = (keys as Record<string, unknown>)[OPENCODE_LEGACY_OTEL_FLAG_KEY]
  if (flag === null || typeof flag !== 'object') return false
  const prior = (flag as { prior?: unknown }).prior
  return prior !== null && typeof prior === 'object' && typeof (prior as { present?: unknown }).present === 'boolean'
}

export type LegacyKeyMigration = {
  out: string[]
  err: string[]
  events: CaptureEvent[]
  /** True when the receipt lost the legacy key; the caller saves the receipt. */
  receiptChanged: boolean
}

/** The receipt entry without the legacy key, or null when nothing else is left in it. */
function withoutLegacyKey(record: ReceiptFileRecord): ReceiptFileRecord | null {
  const keys = { ...record.keys }
  delete keys[OPENCODE_LEGACY_OTEL_FLAG_KEY]
  return Object.keys(keys).length === 0 ? null : { ...record, keys }
}

function dropLegacyKey(receipt: CaptureReceipt, record: ReceiptFileRecord): void {
  const remaining = withoutLegacyKey(record)
  const index = receipt.files.indexOf(record)
  if (remaining === null) receipt.files.splice(index, 1)
  else receipt.files[index] = remaining
}

function describeValue(value: unknown): string {
  return value === undefined ? '(absent)' : JSON.stringify(value)
}

/**
 * One-time migration of the flag older KyberDash builds wrote into opencode.jsonc (D8).
 * Puts the receipted prior value back (or removes the key when it was absent before) and
 * drops the receipt entry; comments and every other byte are kept. A value that is no
 * longer the one KyberDash wrote is the owner's: it stays, with a warning, and the entry is
 * dropped so the warning is not repeated. An unreadable or unparseable file keeps its entry
 * for a later try. The migration only warns: it never fails the plugin install it rides on.
 *
 * `receipt` is changed in place on a real run (the caller saves it when `receiptChanged`);
 * a dry run changes nothing and previews the restore.
 */
export function migrateLegacyOpencodeFlag(
  home: string,
  env: PathEnv,
  receipt: CaptureReceipt | null,
  dryRun: boolean,
): LegacyKeyMigration {
  const result: LegacyKeyMigration = { out: [], err: [], events: [], receiptChanged: false }
  const path = opencodeConfigPath(home, env)
  const record = legacyOpencodeFlagRecord(receipt, path)
  if (receipt === null || record === undefined) return result
  const key = OPENCODE_LEGACY_OTEL_FLAG_KEY
  const legacy = record.keys[key]!
  const event = (kind: 'restored' | 'would-restore'): CaptureEvent => ({
    kind,
    harness: OPENCODE_HARNESS_ID,
    fileId: OPENCODE_FILE_ID,
    path,
    changes: [{ key, before: legacy.written, ...(legacy.prior.present ? { after: legacy.prior.value } : {}) }],
  })
  // A dry run mutates nothing, so its output says what it would do, never that it did it.
  const drop = (): void => {
    if (dryRun) return
    dropLegacyKey(receipt, record)
    result.receiptChanged = true
  }

  let content: string
  try {
    content = readFileSync(path, 'utf8')
  } catch (error) {
    if ((error as NodeJS.ErrnoException | undefined)?.code === 'ENOENT') {
      result.out.push(
        `harness opencode: ${path} is gone; ${dryRun ? 'would drop' : 'dropped'} the recorded ${key} (nothing to restore)`,
      )
      drop()
      return result
    }
    const message = error instanceof Error ? error.message : String(error)
    result.err.push(
      `kyberdash capture: warning: cannot read ${path} to restore ${key} (${message}); leaving it and its record in place`,
    )
    return result
  }

  const reverted = revertJsonEdits(content, { [key]: legacy })
  if (reverted.refused !== undefined) {
    result.err.push(
      `kyberdash capture: warning: ${path}: ${reverted.refused}; leaving it and the recorded ${key} in place`,
    )
    return result
  }
  if (reverted.drift.length > 0) {
    const [entry] = reverted.drift
    result.err.push(
      `kyberdash capture: warning: ${path} ${key} is ${describeValue(entry?.current)}, not the ${describeValue(entry?.written)} an earlier KyberDash wrote; leaving it as it is and ${dryRun ? 'would no longer track' : 'no longer tracking'} it`,
    )
    drop()
    return result
  }
  if (!reverted.changed) {
    drop()
    return result
  }
  if (dryRun) {
    result.out.push(`dry-run: ${path} would restore ${key}`)
    result.events.push(event('would-restore'))
    return result
  }
  try {
    writeFileSync(path, reverted.content)
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    result.err.push(
      `kyberdash capture: warning: cannot write ${path} to restore ${key} (${message}); leaving it and its record in place`,
    )
    return result
  }
  result.out.push(`harness opencode: restored ${key} in ${path} (an earlier KyberDash wrote it)`)
  result.events.push(event('restored'))
  drop()
  return result
}

export const opencodeHarness: ManagedHarnessWriter = {
  kind: 'managed',
  id: OPENCODE_HARNESS_ID,
  displayName: 'OpenCode',
  format: 'jsonc',
  resolvePath: opencodeConfigPath,
  // Plugin-only: no config key is declared, so the engine reads, writes and receipts none.
  desiredKeys: () => ({}),
  filePlugin: opencodeFilePlugin,
}
