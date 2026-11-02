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
import { basename, dirname, isAbsolute, join, normalize, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

import {
  settleProcessLedger,
  systemLedgerDeps,
  teardownProcessLedger,
  type LedgerEntry,
  type ProcessLedgerDeps,
} from './process-ledger.js'

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

const LEDGER_DIR_PREFIX = 'kyberdash-ledger-'

/**
 * The ledger file comes from the environment, so it is trusted only when it is the file
 * beginLedgerRun creates: absolute, unchanged by normalize (no ..), and directly inside a
 * kyberdash-ledger- directory under the OS temp directory. The same rule spawn-guard.ts applies
 * to appends (duplicated here: that module is not edited from this one).
 */
export function isLedgerPath(file: string): boolean {
  if (!isAbsolute(file) || normalize(file) !== file) return false
  const dir = dirname(file)
  return basename(dir).startsWith(LEDGER_DIR_PREFIX) && resolve(dirname(dir)) === resolve(tmpdir())
}

function removeDir(dir: string): void {
  rmSync(dir, { recursive: true, force: true })
}

/**
 * Ends the ledger run: tears down survivors, removes the temp dir, throws if any survived.
 * Fails closed on the path: a ledger file that fails isLedgerPath is warned about, never read,
 * and nothing is deleted. A missing variable, a missing file, or an invalid path still runs the
 * descendant and PID-1 orphan scans with an empty ledger: scanning and warning is safer than a
 * silent pass. Only the validated ledger directory is ever removed (via the injectable remove).
 */
export async function endLedgerRun(
  deps: ProcessLedgerDeps = systemLedgerDeps,
  warn: (message: string) => void = (message) => void process.stderr.write(message),
  remove: (dir: string) => void = removeDir,
): Promise<void> {
  const raw = process.env[LEDGER_FILE_ENV]
  const runStartedAt = Number(process.env[RUN_STARTED_ENV])
  let file: string | undefined
  if (raw === undefined) {
    warn(`process ledger: ${LEDGER_FILE_ENV} is not set; no spawn records, scanning descendants and orphans only\n`)
  } else if (isLedgerPath(raw)) {
    file = raw
  } else {
    warn(`process ledger: ${LEDGER_FILE_ENV}=${raw} is not a ledger file path; not read, nothing deleted, scanning only\n`)
  }
  // The scans and the cleanup must run whatever the read does. A missing file is an empty ledger
  // with a warning; any other read error is kept and thrown, chained with any survivor report.
  let entries: LedgerEntry[] = []
  let readError: unknown
  if (file !== undefined) {
    try {
      entries = parseEntries(readFileSync(file, 'utf8'))
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') {
        warn(`process ledger: ${file} is missing; scanning descendants and orphans only\n`)
      } else {
        readError = error
      }
    }
  }
  delete process.env[LEDGER_FILE_ENV]
  delete process.env[RUN_STARTED_ENV]
  const options = {
    entries,
    runnerPid: process.pid,
    // Unknown run start: fall back to this process's own start, the earliest the run could be.
    runStartedAt: Number.isFinite(runStartedAt) ? runStartedAt : Date.now() - process.uptime() * 1000,
    repoRoot: dashRoot,
    graceMs: GRACE_MS,
  }
  let teardownError: unknown
  try {
    // Pool workers can still be exiting when the global teardown runs; give them a bounded moment.
    await settleProcessLedger(options, deps, { maxWaitMs: SETTLE_MS, pollMs: 100 })
    await teardownProcessLedger(options, deps)
  } catch (error) {
    teardownError = error
  } finally {
    if (file !== undefined) remove(dirname(file))
  }
  if (teardownError !== undefined && readError !== undefined) {
    const describe = (error: unknown): string => (error instanceof Error ? error.message : String(error))
    throw new Error(`${describe(teardownError)}\nledger read failed: ${describe(readError)}`, { cause: readError })
  }
  if (teardownError !== undefined) throw teardownError
  if (readError !== undefined) throw readError
}

export function setup(): void {
  beginLedgerRun()
}

export async function teardown(): Promise<void> {
  await endLedgerRun()
}
