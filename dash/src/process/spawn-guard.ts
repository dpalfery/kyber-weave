// The one chokepoint every child process passes through (storm guard).
//
// Failure this prevents: a spawner re-ran the current entry script. Under vitest that
// re-entered the suite, which spawned again, and the storm reached 536 node processes.
// The guard refuses before the spawn happens, so no process starts. Two independent
// rules apply:
//   - test mode: KYBERDASH_SPAWN_GUARD=deny refuses every spawn unless a test opted in
//     with allowRealSpawns(reason). Unset means allow, the production default.
//   - lineage: a child is stamped with its role and depth. A job or receiver never
//     spawns, an engine spawns only jobs, and nothing spawns from depth 2 or deeper.
//     These hold even after allowRealSpawns.
import { spawn, type ChildProcess, type SpawnOptions } from 'node:child_process'
import { appendFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { basename, dirname, isAbsolute, normalize, resolve } from 'node:path'

export type SpawnKind = 'job' | 'receiver' | 'engine' | 'tool'

export type GuardedSpawnRequest = {
  readonly kind: SpawnKind
  readonly program: string
  readonly args: readonly string[]
  readonly options?: SpawnOptions
}

/** The shape of child_process.spawn that the guard wraps; tests substitute a recording one. */
export type SpawnFn<T = ChildProcess | undefined> = (
  program: string,
  args: readonly string[],
  options: SpawnOptions,
) => T

/**
 * The production spawn function: the one place a self-spawn reaches child_process. Callers take it
 * as their default and tests inject a recording one in its place.
 */
export const defaultSpawn: SpawnFn<ChildProcess | undefined> = (program, args, options) =>
  spawn(program, [...args], options)

export class SpawnRefusedError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'SpawnRefusedError'
  }
}

export class SpawnedChildMayNotHostError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'SpawnedChildMayNotHostError'
  }
}

const MAX_DEPTH = 2

/** What each spawned role may spawn. A role absent from this table, and a role mapped to [], spawns nothing. */
const SPAWNABLE_BY_ROLE: Readonly<Record<string, readonly SpawnKind[]>> = {
  job: [],
  receiver: [],
  tool: [],
  engine: ['job', 'receiver'],
}

/** Variables a child must keep even when the caller hands it an env of its own. */
const INHERITED_ENV_KEYS = [
  'KYBERDASH_SPAWN_GUARD',
  'KYBERDASH_SPAWN_LEDGER_FILE',
  'KYBERDASH_LEDGER_RUN_STARTED_AT',
] as const

let allowReason: string | null = null
const hits: string[] = []
const ledger: LedgerEntry[] = []

/** A child guardedSpawn started: what the process ledger looks for at the end of a run. */
export type LedgerEntry = { readonly pid: number; readonly command: string; readonly startedAt: number }

/** Every child this process started through guardedSpawn. */
export function spawnLedgerEntries(): readonly LedgerEntry[] {
  return [...ledger]
}

/**
 * Records a started child in memory and, when KYBERDASH_SPAWN_LEDGER_FILE names a file (a test
 * run's cross-process ledger), appends it there. Recording never fails the spawn.
 */
function recordSpawn(request: GuardedSpawnRequest, child: unknown): void {
  const pid = (child as { readonly pid?: unknown } | null | undefined)?.pid
  // NaN, 0, a negative or a fractional pid would corrupt the ledger the teardown kills by.
  if (typeof pid !== 'number' || !Number.isSafeInteger(pid) || pid <= 0) return
  const entry: LedgerEntry = { pid, command: [request.program, ...request.args].join(' '), startedAt: Date.now() }
  ledger.push(entry)
  const file = process.env.KYBERDASH_SPAWN_LEDGER_FILE
  if (file === undefined || file === '' || !isLedgerPath(file)) return
  try {
    appendFileSync(file, `${JSON.stringify(entry)}\n`)
  } catch (error) {
    // The ledger is a safety net: a file that is gone (the run ended) must not break the spawn it
    // observes. Any other failure (EPERM, EACCES, a full disk) means the ledger is blind, so say so.
    const code = (error as NodeJS.ErrnoException).code
    if (code === 'ENOENT' || code === 'ENOTDIR') return
    if (!ledgerFailureReported) {
      ledgerFailureReported = true
      process.stderr.write(`spawn guard: cannot append to the process ledger ${file}: ${code ?? String(error)}\n`)
    }
  }
}

const LEDGER_DIR_PREFIX = 'kyberdash-ledger-'
let ledgerFailureReported = false

/**
 * The ledger file is an append path taken from the environment, so it is only honoured when it is
 * the file the ledger's own setup creates: absolute, free of `..`, and directly inside a
 * kyberdash-ledger- directory under the OS temp directory.
 */
function isLedgerPath(file: string): boolean {
  if (!isAbsolute(file) || normalize(file) !== file) return false
  const dir = dirname(file)
  return basename(dir).startsWith(LEDGER_DIR_PREFIX) && resolve(dirname(dir)) === resolve(tmpdir())
}

/** Opts the current test in to real spawns. Cleared by the setup file after each test. */
export function allowRealSpawns(reason: string): void {
  if (reason.trim() === '') throw new Error('allowRealSpawns requires a reason')
  allowReason = reason
}

/** Ends the opt-in; called by the setup file after each test. */
export function resetSpawnAllowance(): void {
  allowReason = null
}

export function spawnGuardHits(): readonly string[] {
  return [...hits]
}

export function clearSpawnGuardHits(): void {
  hits.length = 0
}

function ownDepth(env: NodeJS.ProcessEnv): number | null {
  const raw = env.KYBERDASH_SPAWN_DEPTH
  if (raw === undefined || raw === '') return 0
  const text = raw.trim()
  // An unreadable depth cannot be shown to be shallow, so it is refused. Number('   ') is 0 and
  // Number('0x1') is 1, so the text must be plain digits before it is converted.
  if (!/^\d+$/.test(text)) return null
  const parsed = Number(text)
  return Number.isSafeInteger(parsed) ? parsed : null
}

function refusalReason(request: GuardedSpawnRequest, env: NodeJS.ProcessEnv, depth: number | null): string | null {
  const role = env.KYBERDASH_SPAWN_ROLE
  if (depth === null) return `KYBERDASH_SPAWN_DEPTH=${env.KYBERDASH_SPAWN_DEPTH ?? ''} is not a depth`
  if (depth >= MAX_DEPTH) return `calling process is at depth ${depth}, the limit is ${MAX_DEPTH - 1}`
  if (role !== undefined && role !== '') {
    const allowed = SPAWNABLE_BY_ROLE[role]
    // Allowlist: a role that is not listed (including a mistyped one) may spawn nothing.
    if (allowed === undefined) return `a process with role ${role} never spawns`
    if (!allowed.includes(request.kind)) {
      return allowed.length === 0 ? `a ${role} process never spawns` : `a ${role} process never spawns a ${request.kind}`
    }
  }
  if (env.KYBERDASH_SPAWN_GUARD === 'deny' && allowReason === null) {
    return 'spawn guard is in deny mode and the test did not call allowRealSpawns'
  }
  return null
}

/**
 * Runs spawnFn only when the guard allows it, with the role and depth stamped into the
 * child environment. Throws SpawnRefusedError, and records a hit, otherwise.
 */
export function guardedSpawn<T>(spawnFn: SpawnFn<T>, request: GuardedSpawnRequest): T {
  const env = process.env
  const depth = ownDepth(env)
  const reason = refusalReason(request, env, depth)
  if (reason !== null) {
    const description = `${request.kind} ${request.program} ${request.args.join(' ')}`.trim()
    hits.push(description)
    throw new SpawnRefusedError(`spawn guard refused ${description}: ${reason}`)
  }
  const callerEnv = request.options?.env
  const inherited: NodeJS.ProcessEnv = {}
  // A caller's own env must not turn the guard off in the child by leaving the variable out.
  if (callerEnv !== undefined) {
    for (const key of INHERITED_ENV_KEYS) {
      const parentValue = env[key]
      if (parentValue === undefined) continue
      const callerValue = callerEnv[key]
      // An empty, whitespace-only or non-string value (a JavaScript caller can pass any) counts as omitted: it would otherwise blank the variable.
      const omitted = typeof callerValue !== 'string' || callerValue.trim() === ''
      // Deny mode can only be tightened, never loosened, by a caller's env.
      const weakensDeny = key === 'KYBERDASH_SPAWN_GUARD' && parentValue === 'deny' && callerValue !== 'deny'
      if (omitted || weakensDeny) inherited[key] = parentValue
    }
  }
  const childEnv: NodeJS.ProcessEnv = {
    ...(callerEnv ?? env),
    ...inherited,
    KYBERDASH_SPAWN_ROLE: request.kind,
    KYBERDASH_SPAWN_DEPTH: String((depth ?? 0) + 1),
  }
  const child = spawnFn(request.program, request.args, { ...request.options, env: childEnv })
  recordSpawn(request, child)
  return child
}

/**
 * Throws SpawnedChildMayNotHostError when this process is a job or receiver child, or sits at
 * the depth limit. An engine child may host: hosting the dashboard is what an engine is for.
 */
export function assertMayHost(env: NodeJS.ProcessEnv = process.env): void {
  const role = env.KYBERDASH_SPAWN_ROLE
  const depth = ownDepth(env)
  if (role === 'job' || role === 'receiver' || role === 'tool') {
    throw new SpawnedChildMayNotHostError(`a ${role} child process may not host the dashboard`)
  }
  if (depth === null || depth >= MAX_DEPTH) {
    throw new SpawnedChildMayNotHostError(
      `a child process at spawn depth ${env.KYBERDASH_SPAWN_DEPTH ?? ''} may not host the dashboard`,
    )
  }
}

/** True when this process was spawned as an engine, job, receiver or tool child. */
export function isSpawnedChildRole(env: NodeJS.ProcessEnv = process.env): boolean {
  const role = env.KYBERDASH_SPAWN_ROLE
  return role === 'engine' || role === 'job' || role === 'receiver' || role === 'tool'
}
