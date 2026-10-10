// Pi capture writer (P2.5): status-only (G1-Q1 = (a), @dpalfery/pi-statusline).
//
// Pi's OTLP export is an extension the user already loaded. This writer reads
// `~/.pi/agent/settings.json` packages and `observme.yaml` and reports which
// one is present. It stays pending so `enable` and `disable` write nothing:
// they must not edit Pi agent config and they must not leave a receipt.

import { existsSync, readFileSync } from 'node:fs'
import { join } from 'node:path'

import type { PendingHarnessWriter } from './registry.js'

/** The collector extension Pi loads through agent packages. */
const COLLECTOR_PACKAGE = '@dpalfery/pi-statusline'

function piAgentDir(home: string): string {
  return join(home, '.pi', 'agent')
}

/** True when settings.json lists the collector package. Unreadable settings count as absent. */
function collectorLoaded(home: string): boolean {
  const path = join(piAgentDir(home), 'settings.json')
  if (!existsSync(path)) return false
  let parsed: unknown
  try {
    parsed = JSON.parse(readFileSync(path, 'utf8'))
  } catch {
    return false
  }
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) return false
  const packages = (parsed as { packages?: unknown }).packages
  return Array.isArray(packages) && packages.includes(COLLECTOR_PACKAGE)
}

/** True when an ObservMe config file is present. Contents are not interpreted. */
function observMeLoaded(home: string): boolean {
  return existsSync(join(piAgentDir(home), 'observme.yaml'))
}

/**
 * Which OTLP extension this home has loaded. Both at once is a warning: two
 * exporters would describe the same session. Neither is reported as none, so
 * status never falls back to "not yet supported".
 */
export function describePiStatus(home: string): readonly string[] {
  const collector = collectorLoaded(home)
  const observMe = observMeLoaded(home)
  const lines: string[] = []
  if (collector) lines.push(`  extension: collector (${COLLECTOR_PACKAGE})`)
  if (observMe) lines.push('  extension: ObservMe (observme.yaml)')
  if (collector && observMe) {
    lines.push('  warn: both the collector and ObservMe are loaded')
  }
  if (!collector && !observMe) lines.push('  extension: none')
  return lines
}

export const piHarness: PendingHarnessWriter = {
  kind: 'pending',
  id: 'pi',
  displayName: 'Pi',
  reason: 'status-only; the loaded Pi OTLP extension is reported and no config is written',
  resolvePath: (home: string) => join(home, '.pi', 'agent', 'settings.json'),
  format: 'json',
  describeStatus: describePiStatus,
}
