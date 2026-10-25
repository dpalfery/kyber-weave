// Shared test kit for the shared-API routes (architecture rule R1, issue #319 T11).
//
// The routes under test live at /api/kyber/* and are reached by both the tray and the web
// UI. They need two collaborators beyond the bridge: the canon store (shared settings and
// the store generation) and the JobHost (job status and manual runs). The tests assume
// handleKyberRequest takes them as a FIFTH argument:
//
//   handleKyberRequest(req, res, url, bridge, { store, jobHost })
//
// where `jobHost` is structurally `Pick<JobHost, 'getStatus' | 'runNow' | 'runJob'>`.
// Everything here is in memory; nothing touches the real home directory.
import { EventEmitter } from 'node:events'
import { closeSync, openSync } from 'node:fs'
import { rm } from 'node:fs/promises'
import type { IncomingMessage, Server, ServerResponse } from 'node:http'
import { join } from 'node:path'

import { CanonStore } from '../canon/store.js'
import type { JobHostStatus, JobKind, JobOutcome, ManualTrigger } from '../jobs/host.js'
import { JOBS_LOCK_FILE } from '../jobs/lease.js'
import type { KyberBridge } from './bridge.js'
import type { CaptureRouteSettings } from './capture-routes.js'
import { handleKyberRequest } from './routes.js'

export type RouteDeps = {
  store: CanonStore
  jobHost: FakeJobHost
  /** Where the capture route reads and writes harness configs; a temporary HOME in tests. */
  capture?: CaptureRouteSettings
}

export class FakeJobHost {
  status: JobHostStatus = {
    state: 'idle',
    lastSuccessAt: null,
    lastFailure: null,
    nextDueAt: null,
    paused: false,
    hostedElsewhere: false,
    lastMaintenanceFailure: null,
    lastSyncFailure: null,
  }
  readonly runNowCalls: ManualTrigger[] = []
  readonly runJobCalls: Array<{ kind: JobKind; args: readonly string[] }> = []
  /** What runNow/runJob settle with; a pending promise models a job that is still running. */
  nextOutcome: Promise<JobOutcome> | (() => Promise<JobOutcome>) = new Promise<JobOutcome>(() => {})

  getStatus(): JobHostStatus {
    return this.status
  }

  runNow(trigger: ManualTrigger): Promise<JobOutcome> {
    this.runNowCalls.push(trigger)
    return typeof this.nextOutcome === 'function' ? this.nextOutcome() : this.nextOutcome
  }

  runJob(kind: JobKind, args: readonly string[] = []): Promise<JobOutcome> {
    this.runJobCalls.push({ kind, args })
    return typeof this.nextOutcome === 'function' ? this.nextOutcome() : this.nextOutcome
  }
}

export function makeDeps(options: { capture?: CaptureRouteSettings } = {}): RouteDeps {
  return {
    store: new CanonStore(':memory:'),
    jobHost: new FakeJobHost(),
    ...(options.capture === undefined ? {} : { capture: options.capture }),
  }
}

type MockRes = {
  statusCode: number
  headers: Record<string, string>
  body: string
  /**
   * Resolves once the handler has written the response body.
   *
   * WHY a promise and not a wait: the body handlers are asynchronous (a clean wipes,
   * projects and re-ingests before it answers; a job trigger answers from a grace
   * timer), so a fixed number of milliseconds raced the handler's own work and lost
   * whenever the work was slower than the wait. Awaiting the answer makes the test
   * depend on the route finishing, not on the machine being fast enough.
   */
  written: Promise<void>
  writeHead(status: number, headers?: Record<string, string>): void
  end(data?: string): void
}

function makeMockRes(): MockRes {
  let markWritten: () => void = () => {}
  const written = new Promise<void>((resolve) => {
    markWritten = resolve
  })
  const res: MockRes = {
    statusCode: 200,
    headers: {},
    body: '',
    written,
    writeHead(status, headers) {
      res.statusCode = status
      res.headers = { ...res.headers, ...(headers ?? {}) }
    },
    end(data) {
      if (data) res.body += data
      markWritten()
    },
  }
  return res
}

export type CallResult = { status: number; body: unknown; raw: string; handled: boolean; destroyed: boolean }

export async function callRoute(
  method: string,
  path: string,
  options: {
    bridge?: KyberBridge
    deps: RouteDeps
    payload?: unknown
    /** Defaults to application/json; pass null to send no content-type header. */
    contentType?: string | null
    chunks?: string[]
  },
): Promise<CallResult> {
  const req = new EventEmitter() as unknown as IncomingMessage
  req.method = method
  const contentType = options.contentType === undefined ? 'application/json' : options.contentType
  req.headers = contentType === null ? {} : { 'content-type': contentType }
  let destroyed = false
  ;(req as unknown as { destroy(): void }).destroy = () => {
    destroyed = true
  }
  const res = makeMockRes()
  // The fifth parameter does not exist yet (RED); the cast keeps the test file compiling.
  const handler = handleKyberRequest as unknown as (
    ...args: unknown[]
  ) => boolean
  const handled = handler(
    req,
    res as unknown as ServerResponse,
    new URL(`http://localhost:4747${path}`),
    options.bridge ?? ({} as unknown as KyberBridge),
    options.deps,
  )
  const chunks =
    options.chunks ??
    (options.payload === undefined
      ? []
      : [typeof options.payload === 'string' ? options.payload : JSON.stringify(options.payload)])
  for (const chunk of chunks) req.emit('data', chunk)
  req.emit('end')
  // Wait for the answer itself. A handler that took the request owes a response, so
  // this resolves when the body is written - which is after the route's own work
  // (the clean, the projection, the grace window) has finished. A handler that declined
  // the request owes no answer, and there is nothing to wait for.
  if (handled) await res.written
  let body: unknown
  try {
    body = res.body === '' ? undefined : JSON.parse(res.body)
  } catch {
    body = undefined
  }
  return { status: res.statusCode, body, raw: res.body, handled, destroyed }
}

/**
 * Whether a path is there, by trying to OPEN it rather than by stat-then-use. A stat
 * followed by an operation on the same path is a race, and here the other writer is a
 * teardown on a later turn of the event loop.
 */
export function probeExists(path: string): boolean {
  try {
    closeSync(openSync(path, 'r'))
    return true
  } catch {
    return false
  }
}

/**
 * Quiesce and close an HTTP server, then await the job-host teardown promise if provided.
 */
export async function stopServer(
  server: Server | undefined,
  getHostClose?: () => Promise<void> | undefined,
): Promise<void> {
  if (server?.listening) {
    server.closeAllConnections?.()
    await new Promise<void>((resolve) => server.close(() => resolve()))
  }
  const hostClose = getHostClose?.()
  if (hostClose) await hostClose
}

/**
 * Wait for any pending lock file unlink from the job-host teardown, then recursively
 * remove the temporary state directory with backstop retries.
 */
export async function removeTempStateDir(dir: string | undefined): Promise<void> {
  if (!dir) return
  const lock = join(dir, JOBS_LOCK_FILE)
  const deadline = Date.now() + 3_000
  while (probeExists(lock) && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 25))
  }
  await rm(dir, { recursive: true, force: true, maxRetries: 10, retryDelay: 50 })
}

