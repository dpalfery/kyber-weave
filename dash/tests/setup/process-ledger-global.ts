// Vitest globalSetup for the process ledger (storm guard T5).
//
// Cross-process design: tests run in forked workers, so the main process cannot see what
// guardedSpawn started. setup() creates a per-run temp dir and names an append-only file in it
// through KYBERDASH_SPAWN_LEDGER_FILE (plus the run start in KYBERDASH_LEDGER_RUN_STARTED_AT).
// Workers fork after globalSetup, inherit both variables, and guardedSpawn appends one JSON
// line per child. teardown() reads the file and runs the ledger teardown, which throws if any
// child, runner descendant, or PID-1 orphan outlived the run. Playwright reuses the same
// begin/end functions (see process-ledger-global-playwright.ts).
import { appendFileSync, mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { settleProcessLedger, systemLedgerDeps, teardownProcessLedger, type LedgerEntry } from './process-ledger.js'

const LEDGER_FILE_ENV = 'KYBERDASH_SPAWN_LEDGER_FILE'
const RUN_STARTED_ENV = 'KYBERDASH_LEDGER_RUN_STARTED_AT'
const GRACE_MS = 3_000
const SETTLE_MS = 3_000
const dashRoot = join(dirname(fileURLToPath(import.meta.url)), '..', '..')

/** Starts a ledger run in this process; a no-op when one is already named by the environment. */
export function beginLedgerRun(): void {
  if (process.env[LEDGER_FILE_ENV] !== undefined) return
  const dir = mkdtempSync(join(tmpdir(), 'kyberdash-ledger-'))
  const file = join(dir, 'spawns.jsonl')
  appendFileSync(file, '')
  process.env[LEDGER_FILE_ENV] = file
  process.env[RUN_STARTED_ENV] = String(Date.now())
}

function parseEntries(text: string): LedgerEntry[] {
  const entries: LedgerEntry[] = []
  for (const line of text.split('\n')) {
    if (line.trim() === '') continue
    try {
      const value: unknown = JSON.parse(line)
      if (typeof value === 'object' && value !== null) {
        const { pid, command, startedAt } = value as Record<string, unknown>
        if (typeof pid === 'number' && typeof command === 'string' && typeof startedAt === 'number') {
          entries.push({ pid, command, startedAt })
        }
      }
    } catch {
      // A torn final line from a killed worker is not an entry.
    }
  }
  return entries
}

/** Ends the ledger run: tears down survivors, removes the temp dir, throws if any survived. */
export async function endLedgerRun(): Promise<void> {
  const file = process.env[LEDGER_FILE_ENV]
  if (file === undefined) return
  const runStartedAt = Number(process.env[RUN_STARTED_ENV])
  const entries = parseEntries(readFileSync(file, 'utf8'))
  delete process.env[LEDGER_FILE_ENV]
  delete process.env[RUN_STARTED_ENV]
  const options = {
    entries,
    runnerPid: process.pid,
    runStartedAt: Number.isFinite(runStartedAt) ? runStartedAt : 0,
    repoRoot: dashRoot,
    graceMs: GRACE_MS,
  }
  try {
    // Pool workers can still be exiting when the global teardown runs; give them a bounded moment.
    await settleProcessLedger(options, systemLedgerDeps, { maxWaitMs: SETTLE_MS, pollMs: 100 })
    await teardownProcessLedger(options, systemLedgerDeps)
  } finally {
    rmSync(dirname(file), { recursive: true, force: true })
  }
}

export function setup(): void {
  beginLedgerRun()
}

export async function teardown(): Promise<void> {
  await endLedgerRun()
}
