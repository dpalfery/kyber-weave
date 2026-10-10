// JobHost: the one place every background job runs (architecture rule R1).
//
// The tray and the web UI are display layers; the `kyberdash web` server owns the
// scheduled refresh, the maintenance pass, and the manual/import/clean children.
// This is the port of the tray's scheduler.rs: same cadence rule, same "exit 3 means
// the store is busy, not broken" distinction, same one-line failure summary.
//
// Jobs run as child processes of the CLI (`kyberdash dash refresh ...`) so that a
// crash or a long import cannot take the web server down, and so the refresh lock
// inside the child keeps arbitrating with terminal-started refreshes.

import { spawn } from 'child_process'

import type { CanonStore } from '../canon/store.js'
import { SETTING_KEYS, readSetting } from '../settings/shared-settings.js'
import { acquireJobsLease } from './lease.js'
import type { JobsLease } from './lease.js'

/** Exit code `dash refresh` uses for "another refresh holds the lock; nothing was written". */
export const REFRESH_BUSY_EXIT_CODE = 3

const MINUTE_MS = 60_000
const FAILURE_SUMMARY_MAX = 200
const STDERR_CAP = 8192

export type ChildResult = { readonly code: number | null; readonly stderr: string }

export type JobChild = {
  readonly exited: Promise<ChildResult>
  kill(): void
}

export type JobSpawner = {
  spawn(program: string, args: readonly string[]): JobChild
}

export type JobClock = {
  now(): number
  setInterval(fn: () => void, everyMs: number): unknown
  clearInterval(id: unknown): void
}

export type JobKind = 'import' | 'clean'
export type ManualTrigger = 'web' | 'tray'

export type JobOutcome =
  | { readonly outcome: 'succeeded' }
  | { readonly outcome: 'busy' }
  | { readonly outcome: 'failed'; readonly reason: string }
  | { readonly outcome: 'cancelled' }
  /** Another job is already running here. */
  | { readonly outcome: 'declined' }
  /** Another process holds jobs.lock, so this host must not run anything. */
  | { readonly outcome: 'hosted-elsewhere' }

export type JobState = 'idle' | 'running' | 'running-elsewhere' | 'failed'

export type JobHostStatus = {
  readonly state: JobState
  readonly lastSuccessAt: string | null
  readonly lastFailure: string | null
  readonly nextDueAt: string | null
  readonly paused: boolean
  readonly hostedElsewhere: boolean
  readonly lastMaintenanceFailure: string | null
}

export type JobHostOptions = {
  readonly store: CanonStore
  readonly stateDir: string
  readonly program: string
  /**
   * Arguments between the program and the job's own argv, for a `program` that is an
   * interpreter rather than the CLI itself - `node <entry script>`. Empty by default so a
   * packaged executable, which needs no wrapper, spawns exactly the job's argv.
   */
  readonly programArgs?: readonly string[]
  readonly clock?: JobClock
  readonly spawner?: JobSpawner
  readonly tickIntervalMs?: number
  readonly maintenance: () => void | Promise<void>
}

const DEFAULT_TICK_INTERVAL_MS = 30_000

const realClock: JobClock = {
  now: () => Date.now(),
  setInterval: (fn, everyMs) => {
    const id = setInterval(fn, everyMs)
    // A host must never be the reason the process stays alive; close() is the exit path.
    id.unref()
    return id
  },
  clearInterval: (id) => { clearInterval(id as NodeJS.Timeout) },
}

/** Spawns a real child, keeping only the tail of stderr: that is all a failure summary needs. */
export const nodeSpawner: JobSpawner = {
  spawn(program, args) {
    const child = spawn(program, [...args], { stdio: ['ignore', 'ignore', 'pipe'] })
    let stderr = ''
    child.stderr.on('data', (chunk: Buffer) => {
      stderr = (stderr + chunk.toString('utf-8')).slice(-STDERR_CAP)
    })
    const exited = new Promise<ChildResult>((resolve) => {
      // 'error' (e.g. ENOENT) means the child never ran; report it as the reason.
      child.once('error', (err) => { resolve({ code: null, stderr: err.message }) })
      child.once('close', (code) => { resolve({ code, stderr }) })
    })
    return { exited, kill: () => { child.kill() } }
  },
}

/** The footer shows this, so it must be one readable line, not someone else's stack trace. */
function summarize(stderr: string, fallback: string): string {
  const line = stderr.split('\n').map(l => l.trim()).find(l => l !== '')
  return line === undefined ? fallback : line.slice(0, FAILURE_SUMMARY_MAX)
}

export class JobHost {
  private readonly store: CanonStore
  private readonly stateDir: string
  private readonly program: string
  private readonly programArgs: readonly string[]
  private readonly clock: JobClock
  private readonly spawner: JobSpawner
  private readonly tickIntervalMs: number
  private readonly maintenance: () => void | Promise<void>

  private lease: JobsLease | null = null
  private intervalId: unknown = null
  private closed = false
  private tickInFlight: Promise<void> | null = null
  private maintenanceRunning = false

  /** Reserved synchronously before any await so two requests cannot both start a child. */
  private busy = false
  private activeChild: JobChild | null = null
  private activeDone: Promise<unknown> = Promise.resolve()

  private refreshState: Exclude<JobState, 'running'> = 'idle'
  private lastRunAt: number | null = null
  private lastSuccessAt: string | null = null
  private lastFailure: string | null = null
  private lastMaintenanceFailure: string | null = null

  constructor(options: JobHostOptions) {
    this.store = options.store
    this.stateDir = options.stateDir
    this.program = options.program
    this.programArgs = options.programArgs ?? []
    this.clock = options.clock ?? realClock
    this.spawner = options.spawner ?? nodeSpawner
    this.tickIntervalMs = options.tickIntervalMs ?? DEFAULT_TICK_INTERVAL_MS
    this.maintenance = options.maintenance
  }

  /** Runs the first tick (which is the start-up refresh) and arms the interval. */
  async start(): Promise<void> {
    if (this.closed || this.intervalId !== null) return
    this.intervalId = this.clock.setInterval(() => { void this.tick() }, this.tickIntervalMs)
    await this.tick()
  }

  /**
   * One scheduling pass. Settings are re-read here every time - never cached - so a
   * change made in the web UI or the tray applies on the next tick without a restart.
   */
  tick(): Promise<void> {
    // An overlapping tick joins the one in flight rather than racing it for the lease.
    this.tickInFlight ??= this.runTick().finally(() => { this.tickInFlight = null })
    return this.tickInFlight
  }

  private async runTick(): Promise<void> {
    if (this.closed) return
    if (!(await this.ensureLease()) || this.closed) return

    await this.runMaintenance()

    // Pause gates only this scheduled refresh: maintenance, manual jobs and the
    // receiver all keep working, so "pause" never freezes the UI's own actions.
    if (this.closed || this.isPaused() || this.busy || !this.isDue()) return
    this.launch(['dash', 'refresh', '--trigger', 'scheduled'], 'refresh')
  }

  async runNow(trigger: ManualTrigger): Promise<JobOutcome> {
    return this.runManual(['dash', 'refresh', '--trigger', trigger], 'refresh')
  }

  /** `extraArgs` carries the job's own flags (e.g. clean's scope), which the caller validates. */
  async runJob(kind: JobKind, extraArgs: readonly string[] = []): Promise<JobOutcome> {
    return this.runManual(['dash', kind, ...extraArgs], kind)
  }

  getStatus(): JobHostStatus {
    const paused = this.isPaused()
    return {
      state: this.busy ? 'running' : this.refreshState,
      lastSuccessAt: this.lastSuccessAt,
      lastFailure: this.lastFailure,
      nextDueAt: this.nextDueAt(paused),
      paused,
      hostedElsewhere: this.lease === null && !this.closed && this.leaseChecked,
      lastMaintenanceFailure: this.lastMaintenanceFailure,
    }
  }

  async close(): Promise<void> {
    this.closed = true
    if (this.intervalId !== null) {
      this.clock.clearInterval(this.intervalId)
      this.intervalId = null
    }
    this.activeChild?.kill()
    await this.activeDone
    const lease = this.lease
    this.lease = null
    await lease?.release()
  }

  // True once a lease attempt has been made and lost, so a host that has not started
  // yet does not claim another process is hosting.
  private leaseChecked = false

  private async ensureLease(): Promise<boolean> {
    if (this.lease !== null) return true
    const lease = await acquireJobsLease(this.stateDir)
    this.leaseChecked = true
    if (lease === null) return false
    if (this.closed) {
      await lease.release()
      return false
    }
    this.lease = lease
    return true
  }

  private async runManual(args: readonly string[], kind: 'refresh' | JobKind): Promise<JobOutcome> {
    if (this.closed || this.busy) return { outcome: 'declined' }
    this.busy = true
    let leased: boolean
    try { leased = await this.ensureLease() }
    catch (err) { this.busy = false; throw err }
    if (!leased) {
      this.busy = false
      return { outcome: 'hosted-elsewhere' }
    }
    return this.launch(args, kind, true)
  }

  /**
   * Starts a child and records its outcome. `reserved` means the caller already set
   * `busy` (manual runs reserve before awaiting the lease).
   */
  private launch(args: readonly string[], kind: 'refresh' | JobKind, reserved = false): Promise<JobOutcome> {
    if (!reserved) {
      if (this.busy) return Promise.resolve({ outcome: 'declined' })
      this.busy = true
    }
    let child: JobChild
    try {
      child = this.spawner.spawn(this.program, [...this.programArgs, ...args])
    } catch (err) {
      this.busy = false
      return Promise.resolve(this.record(kind, { code: null, stderr: err instanceof Error ? err.message : String(err) }))
    }
    this.activeChild = child
    const done = child.exited.then((result) => {
      this.activeChild = null
      this.busy = false
      return this.record(kind, result)
    })
    this.activeDone = done
    return done
  }

  private record(kind: 'refresh' | JobKind, result: ChildResult): JobOutcome {
    // A child we killed on close is a shutdown, not a failed job: it must not
    // overwrite the last success or put a cancellation in the failure footer.
    if (this.closed) return { outcome: 'cancelled' }

    const now = this.clock.now()
    const isRefresh = kind === 'refresh'
    if (isRefresh) this.lastRunAt = now
    const stamp = new Date(now).toISOString()

    if (result.code === 0) {
      if (isRefresh) {
        this.refreshState = 'idle'
        this.lastSuccessAt = stamp
      }
      return { outcome: 'succeeded' }
    }
    if (result.code === REFRESH_BUSY_EXIT_CODE && isRefresh) {
      // Nothing was written; the store is simply busy elsewhere, which is not a failure.
      this.refreshState = 'running-elsewhere'
      return { outcome: 'busy' }
    }
    const reason = summarize(result.stderr, result.code === null ? 'terminated' : `exit ${result.code}`)
    if (isRefresh) {
      // The last success time survives the failure.
      this.refreshState = 'failed'
      this.lastFailure = `${reason} at ${stamp}`
    }
    return { outcome: 'failed', reason }
  }

  private async runMaintenance(): Promise<void> {
    if (this.maintenanceRunning) return
    this.maintenanceRunning = true
    try {
      await this.maintenance()
      this.lastMaintenanceFailure = null
    } catch (err) {
      // Maintenance is housekeeping; a failure is surfaced in status, never thrown into the tick.
      this.lastMaintenanceFailure = err instanceof Error ? err.message : String(err)
    } finally {
      this.maintenanceRunning = false
    }
  }

  private isPaused(): boolean {
    return readSetting(this.store, SETTING_KEYS.jobsPaused) === 'on'
  }

  private cadenceMs(): number {
    return readSetting(this.store, SETTING_KEYS.refreshCadenceMinutes) * MINUTE_MS
  }

  /** Never-run is due immediately, so the start-up refresh needs no special case. */
  private isDue(): boolean {
    if (this.lastRunAt === null) return true
    const elapsed = this.clock.now() - this.lastRunAt
    // A clock that went backwards is not a reason to refresh early.
    return elapsed >= this.cadenceMs()
  }

  private nextDueAt(paused: boolean): string | null {
    if (paused || this.closed || (this.leaseChecked && this.lease === null)) return null
    const due = this.lastRunAt === null ? this.clock.now() : this.lastRunAt + this.cadenceMs()
    return new Date(due).toISOString()
  }
}
