// Process ledger teardown (storm guard T5). Failure this prevents: a test run leaves children
// behind. They outlive the runner, get re-parented to PID 1, and nothing reaps them; that is how
// the 536-node storm reached the machine. The teardown names every survivor, kills it
// (SIGTERM, then SIGKILL after a grace period), and throws so the run fails.
import {
  spawnSync,
  type SpawnSyncOptionsWithStringEncoding,
  type SpawnSyncReturns,
} from 'node:child_process'

export type ProcessRow = {
  readonly pid: number
  readonly ppid: number
  readonly startedAt: number
  readonly command: string
}

export type LedgerEntry = { readonly pid: number; readonly command: string; readonly startedAt: number }

export type LedgerSignal = 'SIGTERM' | 'SIGKILL'

export type ProcessLedgerDeps = {
  readonly listProcesses: () => readonly ProcessRow[]
  readonly kill: (pid: number, signal: LedgerSignal) => void
  readonly sleep: (ms: number) => Promise<void>
  /** Platform seam. On 'win32' there is no ps: only ledger entries are checked, by isAlive. */
  readonly platform?: NodeJS.Platform
  /** Liveness probe by pid; required for the win32 path, which has no process table. */
  readonly isAlive?: (pid: number) => boolean
}

export type TeardownOptions = {
  readonly entries: readonly LedgerEntry[]
  readonly runnerPid: number
  readonly runStartedAt: number
  readonly repoRoot: string
  readonly graceMs: number
}

type Survivor = { readonly pid: number; readonly command: string; readonly killable: boolean }

/**
 * The process table the scans read. On Windows there is none, so the descendant and PID-1
 * orphan scans are skipped (the PID-1 re-parenting they look for does not exist there) and
 * only the ledger's own entries are checked, by pid. The command cannot be verified there, so
 * the recorded one stands in; a reused pid is a risk the platform leaves us.
 */
function scanTable(options: TeardownOptions, deps: ProcessLedgerDeps): readonly ProcessRow[] {
  if (deps.platform !== 'win32') return deps.listProcesses()
  const isAlive = deps.isAlive
  // Silently returning no survivors would pass a run that left children behind.
  if (isAlive === undefined) throw new Error("process ledger: platform 'win32' requires deps.isAlive")
  return options.entries
    .filter((entry) => isAlive(entry.pid))
    .map((entry) => ({ pid: entry.pid, ppid: 0, startedAt: entry.startedAt, command: entry.command }))
}

function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

function findSurvivors(options: TeardownOptions, rows: readonly ProcessRow[]): Map<number, Survivor> {
  const found = new Map<number, Survivor>()
  const add = (row: ProcessRow, killable = true): void => {
    if (row.pid !== options.runnerPid && !found.has(row.pid)) {
      found.set(row.pid, { pid: row.pid, command: row.command, killable })
    }
  }

  const recorded = new Map(options.entries.map((entry) => [entry.pid, entry.command]))
  for (const row of rows) {
    // A reused pid with another command is not the ledger's child.
    if (recorded.get(row.pid) === row.command) add(row)
  }

  const childrenOf = new Map<number, ProcessRow[]>()
  for (const row of rows) {
    const siblings = childrenOf.get(row.ppid)
    if (siblings === undefined) childrenOf.set(row.ppid, [row])
    else siblings.push(row)
  }
  const seen = new Set<number>([options.runnerPid])
  const queue = [options.runnerPid]
  for (let next = queue.shift(); next !== undefined; next = queue.shift()) {
    for (const child of childrenOf.get(next) ?? []) {
      if (seen.has(child.pid)) continue
      seen.add(child.pid)
      add(child)
      queue.push(child.pid)
    }
  }

  // A PID-1 orphan must name the root on a path boundary (a bare prefix like dash-foo is another
  // project). It is killed only when linked to this run: its command is a ledger command, or it is
  // a test-runner process from the root's node_modules. ps output carries no environment, so no
  // env marker can be checked. Anything else (a developer's own detached engine) is reported,
  // never signalled.
  const root = options.repoRoot.replace(/[\\/]+$/, '')
  const namesRoot = new RegExp(`${escapeRegExp(root)}(?=$|[\\\\/\\s"'])`)
  const runnerTools = new RegExp(`${escapeRegExp(root)}[\\\\/]node_modules[\\\\/](?:vitest|@?playwright)`)
  const ledgerCommands = new Set(options.entries.map((entry) => entry.command))
  for (const row of rows) {
    if (row.ppid !== 1 || row.startedAt < options.runStartedAt || !namesRoot.test(row.command)) continue
    add(row, ledgerCommands.has(row.command) || runnerTools.test(row.command))
  }
  return found
}

function signal(deps: ProcessLedgerDeps, pid: number, name: LedgerSignal): void {
  try {
    deps.kill(pid, name)
  } catch (error) {
    // ESRCH: it exited between the scan and the signal, which is the outcome we want.
    if ((error as NodeJS.ErrnoException).code !== 'ESRCH') throw error
  }
}

export type SettleOptions = { readonly maxWaitMs: number; readonly pollMs: number }

/**
 * Waits, on the injected clock and for at most maxWaitMs, until nothing would be a survivor.
 * The vitest pool is closed just before the global teardown runs, and a worker can still be
 * exiting at that instant. Anything gone within the window is not a leftover; anything still
 * there after it is, and teardownProcessLedger then reports and kills it.
 */
export async function settleProcessLedger(
  options: TeardownOptions,
  deps: ProcessLedgerDeps,
  settle: SettleOptions,
): Promise<void> {
  for (let waited = 0; waited < settle.maxWaitMs; waited += settle.pollMs) {
    if (findSurvivors(options, scanTable(options, deps)).size === 0) return
    await deps.sleep(settle.pollMs)
  }
}

/** Kills and reports every process the run left behind; resolves only when there were none. */
export async function teardownProcessLedger(options: TeardownOptions, deps: ProcessLedgerDeps): Promise<void> {
  const survivors = findSurvivors(options, scanTable(options, deps))
  if (survivors.size === 0) return

  const killable = [...survivors.values()].filter((survivor) => survivor.killable)
  for (const { pid } of killable) signal(deps, pid, 'SIGTERM')
  await deps.sleep(options.graceMs)

  const alive = new Map(scanTable(options, deps).map((row) => [row.pid, row.command]))
  for (const { pid, command } of killable) {
    if (alive.get(pid) === command) signal(deps, pid, 'SIGKILL')
  }

  const lines = [...survivors.values()].map(
    (survivor) => `  pid ${survivor.pid}: ${survivor.command}${survivor.killable ? '' : ' (unlinked orphan, not killed)'}`,
  )
  throw new Error(`process ledger: ${survivors.size} process(es) outlived the run and were killed:\n${lines.join('\n')}`)
}

// ps -o lstart prints "Thu Oct 30 12:00:00 2026" (five tokens, local time) under the C locale.
const PS_ROW = /^\s*(\d+)\s+(\d+)\s+(\S+)\s+(\w{3}\s+\w{3}\s+\d+\s+\d{2}:\d{2}:\d{2}\s+\d{4})\s+(.*\S)\s*$/

type RunPs = (file: string, args: readonly string[], options: SpawnSyncOptionsWithStringEncoding) => SpawnSyncReturns<string>

function listSystemProcesses(platform: NodeJS.Platform, run: RunPs): readonly ProcessRow[] {
  // Windows has no ps; the table stays empty and the ledger falls back to per-pid checks.
  if (platform === 'win32') return []
  const result = run('ps', ['-axo', 'pid=,ppid=,stat=,lstart=,command='], {
    encoding: 'utf8',
    env: { ...process.env, LC_ALL: 'C' },
    maxBuffer: 64 * 1024 * 1024,
    timeout: 10_000,
    killSignal: 'SIGKILL',
  })
  if (result.status !== 0) throw new Error(`process ledger: ps failed: ${result.stderr || result.error?.message || 'no output'}`)
  const rows: ProcessRow[] = []
  for (const line of result.stdout.split('\n')) {
    const match = PS_ROW.exec(line)
    if (match === null) continue
    const [, pid, ppid, stat, started, command] = match
    const startedAt = Date.parse(started ?? '')
    // ps itself is a child of the runner for the instant it takes; it is not a survivor.
    if (Number.isNaN(startedAt) || Number(pid) === result.pid) continue
    // A zombie has exited and only awaits its parent's wait(); nothing is running to kill.
    // macOS shows a zombie's command as "(node)"; the stat column is the reliable signal.
    if (stat?.startsWith('Z') || command?.endsWith('<defunct>')) continue
    rows.push({ pid: Number(pid), ppid: Number(ppid), startedAt, command: command ?? '' })
  }
  return rows
}

/** The real deps, with the platform and the ps runner injectable so Windows is testable anywhere. */
export function createSystemLedgerDeps(
  platform: NodeJS.Platform = process.platform,
  run: RunPs = spawnSync,
): ProcessLedgerDeps {
  return {
    platform,
    listProcesses: () => listSystemProcesses(platform, run),
    kill: (pid, name) => {
      process.kill(pid, name)
    },
    sleep: (ms) => new Promise<void>((resolve) => setTimeout(resolve, ms)),
    isAlive: (pid) => {
      try {
        process.kill(pid, 0)
        return true
      } catch (error) {
        // EPERM means the pid exists but is not ours to signal: alive.
        return (error as NodeJS.ErrnoException).code === 'EPERM'
      }
    },
  }
}

export const systemLedgerDeps: ProcessLedgerDeps = createSystemLedgerDeps()
