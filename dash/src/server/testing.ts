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
import type { IncomingMessage, ServerResponse } from 'node:http'

import { CanonStore } from '../canon/store.js'
import type { JobHostStatus, JobKind, JobOutcome, ManualTrigger } from '../jobs/host.js'
import type { KyberBridge } from './bridge.js'
import { handleKyberRequest } from './routes.js'

export type RouteDeps = {
  store: CanonStore
  jobHost: FakeJobHost
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

export function makeDeps(): RouteDeps {
  return { store: new CanonStore(':memory:'), jobHost: new FakeJobHost() }
}

type MockRes = {
  statusCode: number
  headers: Record<string, string>
  body: string
  writeHead(status: number, headers?: Record<string, string>): void
  end(data?: string): void
}

function makeMockRes(): MockRes {
  const res: MockRes = {
    statusCode: 200,
    headers: {},
    body: '',
    writeHead(status, headers) {
      res.statusCode = status
      res.headers = { ...res.headers, ...(headers ?? {}) }
    },
    end(data) {
      if (data) res.body += data
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
  await new Promise((resolve) => setTimeout(resolve, 50))
  let body: unknown
  try {
    body = res.body === '' ? undefined : JSON.parse(res.body)
  } catch {
    body = undefined
  }
  return { status: res.statusCode, body, raw: res.body, handled, destroyed }
}
