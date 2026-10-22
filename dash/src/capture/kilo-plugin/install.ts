// Install, remove and report on the Kilo capture plugin.
//
// `capture enable kilo` owns one whole file, ~/.config/kilo/plugin/kyberdash-capture.js,
// not keys inside a config file, so it keeps its own small receipt
// (~/.kyberdash/kilo-plugin-receipt.json) holding the digest of what it wrote. `disable`
// removes the file only while that digest still matches: an owner edit is left alone and
// reported. Captured data under ~/.kyberdash/capture/kilo is never deleted here.

import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'

import { envJoin, type PathEnv } from '../paths.js'
import { sha256Hex } from '../receipt.js'
import { KILO_PLUGIN_MARKER, KILO_PLUGIN_SOURCE } from './source.js'

export type KiloPluginPaths = { pluginFile: string; receiptFile: string; storeDir: string }

export type KiloPluginOutcome = 'wrote' | 'would-write' | 'restored' | 'would-restore' | 'noop' | 'refused'

export type KiloPluginResult = {
  exitCode: number
  outcome: KiloPluginOutcome
  /** Why a refusal happened: `conflict` for a file that is not ours, `drift` for an owner edit, `unreadable` when the path cannot be read as a file. */
  reason?: 'conflict' | 'drift' | 'unreadable'
  out: string[]
  err: string[]
}

type KiloPluginReceipt = { version: 1; path: string; sha256: string }

/** The largest instant a Date can hold; anything beyond it makes `toISOString` throw. */
const MAX_DATE_MS = 8.64e15

/** Where the plugin, its receipt and its captured data live under a HOME. */
export function kiloPluginPaths(home: string, env?: PathEnv): KiloPluginPaths {
  return {
    pluginFile: envJoin(env, home, '.config', 'kilo', 'plugin', 'kyberdash-capture.js'),
    receiptFile: envJoin(env, home, '.kyberdash', 'kilo-plugin-receipt.json'),
    storeDir: envJoin(env, home, '.kyberdash', 'capture', 'kilo'),
  }
}

/** What is at a path: nothing, a file's text, or something that cannot be read as a file. */
type Slot = { kind: 'absent' } | { kind: 'file'; text: string } | { kind: 'unreadable' }

/** Never throws: a directory or a permission error is `unreadable`, not an exception. */
function readSlot(path: string): Slot {
  try {
    return existsSync(path) ? { kind: 'file', text: readFileSync(path, 'utf8') } : { kind: 'absent' }
  } catch {
    return { kind: 'unreadable' }
  }
}

function readIfPresent(path: string): string | undefined {
  const slot = readSlot(path)
  return slot.kind === 'file' ? slot.text : undefined
}

function unreadable(pluginFile: string): KiloPluginResult {
  return {
    exitCode: 1,
    outcome: 'refused',
    reason: 'unreadable',
    out: [],
    err: [`kyberdash capture: ${pluginFile} cannot be read as a file; leaving it in place`],
  }
}

function readReceipt(path: string): KiloPluginReceipt | undefined {
  try {
    const parsed = JSON.parse(readFileSync(path, 'utf8')) as Partial<KiloPluginReceipt>
    if (parsed.version === 1 && typeof parsed.path === 'string' && typeof parsed.sha256 === 'string') {
      return parsed as KiloPluginReceipt
    }
  } catch {
    // a missing or unreadable receipt means nothing was recorded
  }
  return undefined
}

export function installKiloPlugin(
  home: string,
  env?: PathEnv,
  options: { dryRun?: boolean } = {},
): KiloPluginResult {
  const { pluginFile, receiptFile } = kiloPluginPaths(home, env)
  const slot = readSlot(pluginFile)
  if (slot.kind === 'unreadable') return unreadable(pluginFile)
  const existing = slot.kind === 'file' ? slot.text : undefined
  if (existing === KILO_PLUGIN_SOURCE) {
    return { exitCode: 0, outcome: 'noop', out: [`harness kilo: capture plugin already installed (${pluginFile})`], err: [] }
  }
  if (existing !== undefined && !existing.startsWith('// kyberdash-capture-plugin')) {
    return {
      exitCode: 1,
      outcome: 'refused',
      reason: 'conflict',
      out: [],
      err: [`kyberdash capture: refusing to overwrite ${pluginFile}: it was not written by KyberDash`],
    }
  }
  if (options.dryRun) {
    return { exitCode: 0, outcome: 'would-write', out: [`harness kilo: would install capture plugin at ${pluginFile}`], err: [] }
  }
  mkdirSync(dirname(pluginFile), { recursive: true })
  writeFileSync(pluginFile, KILO_PLUGIN_SOURCE)
  mkdirSync(dirname(receiptFile), { recursive: true })
  const receipt: KiloPluginReceipt = { version: 1, path: pluginFile, sha256: sha256Hex(KILO_PLUGIN_SOURCE) }
  writeFileSync(receiptFile, `${JSON.stringify(receipt, null, 2)}\n`)
  return {
    exitCode: 0,
    outcome: 'wrote',
    out: [
      `harness kilo: installed capture plugin at ${pluginFile}`,
      'harness kilo: restart Kilo (CLI, VS Code window or desktop app) so it loads the plugin',
    ],
    err: [],
  }
}

export function uninstallKiloPlugin(
  home: string,
  env?: PathEnv,
  options: { dryRun?: boolean } = {},
): KiloPluginResult {
  const { pluginFile, receiptFile } = kiloPluginPaths(home, env)
  const slot = readSlot(pluginFile)
  if (slot.kind === 'unreadable') return unreadable(pluginFile)
  const existing = slot.kind === 'file' ? slot.text : undefined
  if (existing === undefined) {
    rmSync(receiptFile, { force: true })
    return { exitCode: 0, outcome: 'noop', out: ['harness kilo: capture plugin not installed; nothing to do'], err: [] }
  }
  const recorded = readReceipt(receiptFile)?.sha256 ?? sha256Hex(KILO_PLUGIN_SOURCE)
  if (sha256Hex(existing) !== recorded) {
    return {
      exitCode: 1,
      outcome: 'refused',
      reason: 'drift',
      out: [],
      err: [`kyberdash capture: ${pluginFile} changed since install; leaving it in place. Delete it yourself to remove it.`],
    }
  }
  if (options.dryRun) {
    return { exitCode: 0, outcome: 'would-restore', out: [`harness kilo: would remove capture plugin ${pluginFile}`], err: [] }
  }
  rmSync(pluginFile)
  rmSync(receiptFile, { force: true })
  return {
    exitCode: 0,
    outcome: 'restored',
    out: [`harness kilo: removed capture plugin ${pluginFile}`, `harness kilo: captured data kept in ${kiloPluginPaths(home, env).storeDir}`],
    err: [],
  }
}

export type KiloPluginState = {
  /** on: current and intact. partial: an older plugin of ours. off: absent. unknown: not ours or edited. */
  state: 'on' | 'off' | 'partial' | 'unknown'
  exists: boolean
  receipt: boolean
}

/** What the Settings row reports. Reads two files and never throws. */
export function kiloPluginState(home: string, env?: PathEnv): KiloPluginState {
  const { pluginFile, receiptFile } = kiloPluginPaths(home, env)
  const receipt = readReceipt(receiptFile)
  const slot = readSlot(pluginFile)
  if (slot.kind === 'unreadable') return { state: 'unknown', exists: true, receipt: receipt !== undefined }
  if (slot.kind === 'absent') return { state: 'off', exists: false, receipt: receipt !== undefined }
  const installed = slot.text
  const ours = installed.startsWith('// kyberdash-capture-plugin')
  // With no receipt the marker is all that says the file is ours; with one, its digest must hold.
  const intact = receipt === undefined || sha256Hex(installed) === receipt.sha256
  const state = !ours || !intact ? 'unknown' : installed === KILO_PLUGIN_SOURCE ? 'on' : 'partial'
  return { state, exists: true, receipt: receipt !== undefined }
}

/** Fixed-format status lines. Never echoes owner-written text. */
export function describeKiloPluginStatus(home: string, env?: PathEnv): string[] {
  const { pluginFile, storeDir } = kiloPluginPaths(home, env)
  const lines: string[] = []
  const plugin = kiloPluginState(home, env)
  if (plugin.state === 'off') {
    lines.push('plugin: not installed')
  } else if (plugin.state === 'partial') {
    lines.push(`plugin: installed (older version; re-run enable to upgrade, marker ${KILO_PLUGIN_MARKER.slice(3)})`)
  } else if (plugin.state === 'on') {
    lines.push('plugin: installed')
  } else {
    // unknown: not ours, edited since install, or not readable as a file. Never echo its text.
    lines.push(
      readSlot(pluginFile).kind === 'unreadable'
        ? 'plugin: unreadable (not a regular file or not permitted)'
        : 'plugin: modified since install',
    )
  }
  const index = readIfPresent(join(storeDir, 'index.jsonl'))
  const rows = (index ?? '').split('\n').flatMap((line) => {
    try {
      const row = JSON.parse(line) as { ts?: unknown; sessionID?: unknown }
      // A timestamp outside the Date range cannot be formatted, so it is not a usable row.
      return typeof row.ts === 'number' && Math.abs(row.ts) <= MAX_DATE_MS && typeof row.sessionID === 'string'
        ? [row as { ts: number; sessionID: string }]
        : []
    } catch {
      return []
    }
  })
  if (rows.length === 0) {
    lines.push('captured calls: none yet')
  } else {
    const sessions = new Set(rows.map((row) => row.sessionID)).size
    const last = Math.max(...rows.map((row) => row.ts))
    lines.push(`captured calls: ${rows.length} across ${sessions} sessions`)
    lines.push(`last call: ${new Date(last).toISOString()}`)
  }
  return lines
}
