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

export type KiloPluginResult = { exitCode: number; outcome: KiloPluginOutcome; out: string[]; err: string[] }

type KiloPluginReceipt = { version: 1; path: string; sha256: string }

/** Where the plugin, its receipt and its captured data live under a HOME. */
export function kiloPluginPaths(home: string, env?: PathEnv): KiloPluginPaths {
  return {
    pluginFile: envJoin(env, home, '.config', 'kilo', 'plugin', 'kyberdash-capture.js'),
    receiptFile: envJoin(env, home, '.kyberdash', 'kilo-plugin-receipt.json'),
    storeDir: envJoin(env, home, '.kyberdash', 'capture', 'kilo'),
  }
}

function readIfPresent(path: string): string | undefined {
  return existsSync(path) ? readFileSync(path, 'utf8') : undefined
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
  const existing = readIfPresent(pluginFile)
  if (existing === KILO_PLUGIN_SOURCE) {
    return { exitCode: 0, outcome: 'noop', out: [`harness kilo: capture plugin already installed (${pluginFile})`], err: [] }
  }
  if (existing !== undefined && !existing.startsWith('// kyberdash-capture-plugin')) {
    return {
      exitCode: 1,
      outcome: 'refused',
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
  const existing = readIfPresent(pluginFile)
  if (existing === undefined) {
    rmSync(receiptFile, { force: true })
    return { exitCode: 0, outcome: 'noop', out: ['harness kilo: capture plugin not installed; nothing to do'], err: [] }
  }
  const recorded = readReceipt(receiptFile)?.sha256 ?? sha256Hex(KILO_PLUGIN_SOURCE)
  if (sha256Hex(existing) !== recorded) {
    return {
      exitCode: 1,
      outcome: 'refused',
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

/** Fixed-format status lines. Never echoes owner-written text. */
export function describeKiloPluginStatus(home: string, env?: PathEnv): string[] {
  const { pluginFile, receiptFile, storeDir } = kiloPluginPaths(home, env)
  const installed = readIfPresent(pluginFile)
  const lines: string[] = []
  if (installed === undefined) {
    lines.push('plugin: not installed (run: kyberdash kyber capture enable --harness kilo)')
  } else {
    const recorded = readReceipt(receiptFile)?.sha256
    const intact = sha256Hex(installed) === (recorded ?? sha256Hex(KILO_PLUGIN_SOURCE))
    const current = installed === KILO_PLUGIN_SOURCE
    lines.push(
      !intact
        ? 'plugin: modified since install'
        : current
          ? 'plugin: installed'
          : `plugin: installed (older version; re-run enable to upgrade, marker ${KILO_PLUGIN_MARKER.slice(3)})`,
    )
  }
  const index = readIfPresent(join(storeDir, 'index.jsonl'))
  const rows = (index ?? '').split('\n').flatMap((line) => {
    try {
      const row = JSON.parse(line) as { ts?: unknown; sessionID?: unknown }
      return typeof row.ts === 'number' && typeof row.sessionID === 'string' ? [row] : []
    } catch {
      return []
    }
  })
  if (rows.length === 0) {
    lines.push('captured calls: none yet')
  } else {
    const sessions = new Set(rows.map((row) => row.sessionID)).size
    const last = Math.max(...rows.map((row) => row.ts as number))
    lines.push(`captured calls: ${rows.length} across ${sessions} sessions`)
    lines.push(`last call: ${new Date(last).toISOString()}`)
  }
  return lines
}
