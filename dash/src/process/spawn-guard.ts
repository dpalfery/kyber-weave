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
  if (typeof pid !== 'number') return
  const entry: LedgerEntry = { pid, command: [request.program, ...request.args].join(' '), startedAt: Date.now() }
  ledger.push(entry)
  const file = process.env.KYBERDASH_SPAWN_LEDGER_FILE
  if (file === undefined || file === '') return
  try {
    appendFileSync(file, `${JSON.stringify(entry)}\n`)
  } catch {
    // The ledger is a safety net; a missing file must not break the spawn it observes.
  }
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
  const parsed = Number(raw)
  // An unreadable depth cannot be shown to be shallow, so it is refused.
  return Number.isInteger(parsed) && parsed >= 0 ? parsed : null
}

function refusalReason(request: GuardedSpawnRequest, env: NodeJS.ProcessEnv, depth: number | null): string | null {
  const role = env.KYBERDASH_SPAWN_ROLE
  if (depth === null) return `KYBERDASH_SPAWN_DEPTH=${env.KYBERDASH_SPAWN_DEPTH ?? ''} is not a depth`
  if (depth >= MAX_DEPTH) return `calling process is at depth ${depth}, the limit is ${MAX_DEPTH - 1}`
  if (role === 'job' || role === 'receiver') return `a ${role} process never spawns`
  if (role === 'engine' && request.kind === 'engine') return 'an engine never spawns an engine'
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
  const childEnv: NodeJS.ProcessEnv = {
    ...(request.options?.env ?? env),
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
  if (role === 'job' || role === 'receiver') {
    throw new SpawnedChildMayNotHostError(`a ${role} child process may not host the dashboard`)
  }
  if (depth === null || depth >= MAX_DEPTH) {
    throw new SpawnedChildMayNotHostError(
      `a child process at spawn depth ${env.KYBERDASH_SPAWN_DEPTH ?? ''} may not host the dashboard`,
    )
  }
}

/** True when this process was spawned as an engine, job or receiver child. */
export function isSpawnedChildRole(env: NodeJS.ProcessEnv = process.env): boolean {
  const role = env.KYBERDASH_SPAWN_ROLE
  return role === 'engine' || role === 'job' || role === 'receiver'
}
