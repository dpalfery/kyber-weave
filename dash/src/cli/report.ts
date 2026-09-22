// `kyberdash report` — the non-interactive context report (R11, design C3).
//
// Arguments are validated before the store opens so a typo cannot create a
// canon.db as a side-effect of failing. The store is opened read-only: this
// command diagnoses, it does not ingest, and it does not talk to the network
// (R11.12, R11.13). Detection is included here and not on the tray because it
// walks provider directories; the builder receives that walk as a callback so
// the REST path can skip it.

import { existsSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { createRequire } from 'node:module'

const requireForSqlite = createRequire(import.meta.url)
const { DatabaseSync } = requireForSqlite('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}
type Database = import('node:sqlite').DatabaseSync

import { Command, InvalidArgumentError } from 'commander'

import { buildContextReport } from '../analysis/report/build.js'
import {
  DEFAULT_FINDING_LIMIT,
  DEFAULT_WINDOW_DAYS,
  type ReportScope,
  type ReportSection,
} from '../analysis/report/types.js'
import { renderMarkdown } from '../analysis/report/render-markdown.js'
import { renderText, shouldColorText } from '../analysis/report/render-text.js'
import { collectDoctorReport } from './doctor.js'
import { HARNESS_DESCRIPTORS, PROVIDER_DISPOSITIONS } from '../refresh/registry.js'
import type { ProviderDisposition } from '../refresh/types.js'
import { KyberBridge } from '../server/bridge.js'

const { version: KYBERDASH_VERSION } = createRequire(import.meta.url)('../../package.json') as {
  version: string
}

const FORMATS = ['text', 'markdown', 'json'] as const
type ReportFormat = (typeof FORMATS)[number]

const CLI_SECTIONS: readonly ReportSection[] = [
  'coverage',
  'detection',
  'findings',
  'harnesses',
  'latestSession',
  'cost',
]

const HARNESS_IDS = HARNESS_DESCRIPTORS.map((entry) => entry.harnessId)

export type ReportCliOptions = {
  format?: string
  harness?: string
  session?: string
  run?: string
  days?: number
  limit?: number
  db?: string
}

function resolveDbPath(dbPath?: string): string {
  return dbPath ?? join(homedir(), '.kyberdash', 'canon.db')
}

function parsePositiveInteger(flag: string): (value: string) => number {
  return (value: string) => {
    if (!/^[1-9][0-9]*$/.test(value)) {
      throw new InvalidArgumentError(`${flag} must be a positive integer`)
    }
    const parsed = Number(value)
    if (!Number.isSafeInteger(parsed)) {
      throw new InvalidArgumentError(`${flag} exceeds a safe integer`)
    }
    return parsed
  }
}

function editDistance(a: string, b: string): number {
  const left = a.toLowerCase()
  const right = b.toLowerCase()
  const rows = left.length + 1
  const cols = right.length + 1
  const dp: number[][] = Array.from({ length: rows }, (_, i) => {
    const row = new Array<number>(cols)
    row[0] = i
    return row
  })
  for (let j = 0; j < cols; j++) dp[0]![j] = j
  for (let i = 1; i < rows; i++) {
    for (let j = 1; j < cols; j++) {
      const cost = left[i - 1] === right[j - 1] ? 0 : 1
      dp[i]![j] = Math.min(dp[i - 1]![j]! + 1, dp[i]![j - 1]! + 1, dp[i - 1]![j - 1]! + cost)
    }
  }
  return dp[left.length]![right.length]!
}

function nearestHarness(input: string, names: readonly string[]): string | undefined {
  const lower = input.toLowerCase()
  const prefixed = names.filter((name) => name.toLowerCase().startsWith(lower)).sort()
  if (prefixed[0] !== undefined) return prefixed[0]
  let best: { name: string; distance: number } | undefined
  for (const name of names) {
    const distance = editDistance(lower, name)
    if (best === undefined || distance < best.distance) best = { name, distance }
  }
  return best !== undefined && best.distance > 0 && best.distance <= 3 ? best.name : undefined
}

function assertHarness(value: string | undefined): void {
  if (value === undefined) return
  if (HARNESS_IDS.includes(value)) return
  const hint = nearestHarness(value, HARNESS_IDS)
  const suggestion = hint === undefined ? '' : ` Did you mean "${hint}"?`
  process.stderr.write(
    `kyberdash report: unknown harness "${value}". Valid values: ${HARNESS_IDS.join(', ')}.${suggestion}\n`,
  )
  process.exit(2)
}

function assertFormat(value: string): asserts value is ReportFormat {
  if ((FORMATS as readonly string[]).includes(value)) return
  process.stderr.write(
    `kyberdash report: unknown format "${value}". Valid values: ${FORMATS.join(', ')}.\n`,
  )
  process.exit(2)
}

function harnessIdsForProvider(disposition: ProviderDisposition | undefined): string[] {
  if (disposition === undefined || disposition.kind === 'excluded') return []
  if (disposition.kind === 'alias-of') return [disposition.harnessId]
  return [...disposition.harnessIds]
}

async function detectionFromDoctor(): Promise<Array<{ harness: string; detected: boolean; probedPaths: string[] }>> {
  const doctor = await collectDoctorReport(undefined, { sampleLimit: 0 })
  const rows: Array<{ harness: string; detected: boolean; probedPaths: string[] }> = []
  for (const provider of doctor.providers) {
    const detected = provider.candidatesFound > 0 || provider.probePaths.some((path) => path.exists)
    const probedPaths = provider.probePaths.map((path) => path.path)
    for (const harness of harnessIdsForProvider(PROVIDER_DISPOSITIONS[provider.provider])) {
      rows.push({ harness, detected, probedPaths })
    }
  }
  return rows
}

function openStoreReadOnly(path: string): { bridge: KyberBridge; close: () => void } {
  if (!existsSync(path)) {
    process.stderr.write(`kyberdash report: cannot open store ${path}\n`)
    process.exit(1)
  }
  let db: Database
  try {
    db = new DatabaseSync(path, { readOnly: true })
    db.prepare('SELECT 1').get()
  } catch (err) {
    const reason = err instanceof Error ? err.message : String(err)
    process.stderr.write(`kyberdash report: cannot read store ${path}: ${reason}\n`)
    process.exit(1)
  }
  const bridge = new KyberBridge({ canonPath: path, canonDb: db })
  return {
    bridge,
    close: () => {
      bridge.close()
    },
  }
}

export async function runContextReport(opts: ReportCliOptions): Promise<void> {
  const format = opts.format ?? 'text'
  assertFormat(format)
  assertHarness(opts.harness)

  const scope: ReportScope = { days: opts.days ?? DEFAULT_WINDOW_DAYS }
  if (opts.harness !== undefined) scope.harness = opts.harness
  if (opts.session !== undefined) scope.sessionId = opts.session
  if (opts.run !== undefined) scope.runId = opts.run

  const dbPath = resolveDbPath(opts.db)
  const { bridge, close } = openStoreReadOnly(dbPath)
  try {
    const detection = await detectionFromDoctor()
    const report = buildContextReport(bridge, scope, {
      sections: CLI_SECTIONS,
      findingLimit: opts.limit ?? DEFAULT_FINDING_LIMIT,
      kyberdashVersion: KYBERDASH_VERSION,
      storePath: dbPath,
      detection: () => detection,
    })
    if (format === 'json') {
      process.stdout.write(`${JSON.stringify(report, null, 2)}\n`)
    } else if (format === 'markdown') {
      process.stdout.write(renderMarkdown(report))
    } else {
      process.stdout.write(renderText(report, { color: shouldColorText() }))
    }
  } finally {
    close()
  }
}

export function registerReportCommand(program: Command): void {
  const report = program
    .command('report', { isDefault: true })
    .description('Print a non-interactive context diagnosis of the store')
    .option('--format <format>', 'Output format: text, markdown, json', 'text')
    .option('--harness <id>', 'Canonical harness id to scope the report')
    .option('--session <id>', 'Session id to scope the report')
    .option('--run <id>', 'Run id to scope the report')
    .option('--days <n>', 'Window in whole days (default: 7)', parsePositiveInteger('--days'), DEFAULT_WINDOW_DAYS)
    .option('--limit <n>', 'Maximum findings (default: 5)', parsePositiveInteger('--limit'), DEFAULT_FINDING_LIMIT)
    .option('--db <path>', 'Path to canon.db')
  report.exitOverride((error) => {
    if (error.code === 'commander.invalidArgument') {
      process.stderr.write(`${error.message}\n`)
      process.exit(2)
    }
    process.exit(error.exitCode)
  })
  report.action(async (opts: ReportCliOptions) => {
    await runContextReport(opts)
  })
}
