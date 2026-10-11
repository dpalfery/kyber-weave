// `GET|POST /api/kyber/capture`: the harness-OTel capture surface the Settings drawer drives.
//
// WHY the logic lives in the engine, reached over this route (ADR 0033): the web dashboard
// is a display layer and may not spawn the CLI or hold feature logic. This route calls the
// same capture core the `kyberdash kyber capture` command runs, in-process, and shapes the
// result for the wire. routes.ts owns only the mount and hands in its body helpers, so the
// 64KB cap, the content-type check and the fixed error bodies are the ones every other
// mutating route uses.
//
// What this adds on top of the CLI, and why it is the route's job (D5, D7, D20): a real
// enable turns the shared `receiver.hosted` setting on, because the CLI has no store; a
// disable never touches it, because other exporters may still post to the port and stopping
// the receiver silently would stop collection the user did not ask to stop.
//
// A response names only keys KyberDash manages. Config file paths do appear in events and
// status files (the drawer shows them); this surface is loopback-only. A host error is
// reduced to its errno code, and text copied from the owner's config is never echoed.

import { homedir } from 'node:os'
import type { IncomingMessage, ServerResponse } from 'node:http'

import { runCaptureAction, type CaptureActionResult } from '../capture/actions.js'
import { CAPTURE_HARNESS_REGISTRY } from '../capture/harnesses/registry.js'
import { DEFAULT_CAPTURE_ENDPOINT } from '../capture/index.js'
import { probeReceiver, type HealthFetcher } from '../capture/probe.js'
import {
  buildCaptureStatus,
  createApplicabilityFilter,
  type CaptureHarnessStatus,
  type StatusContext,
} from '../capture/status.js'
import type { InstallState } from '../capture/detect.js'
import { SETTING_KEYS, readSetting, writeSetting } from '../settings/shared-settings.js'
import type { KyberRouteDeps } from './routes.js'

/** Answers "is this harness installed?" for one id; replaces filesystem detection. */
export type CaptureInstallProbe = (harnessId: string) => {
  installed: InstallState
  evidence: readonly string[]
}

/** The seam a host (the web server, a test) points the capture engine at. */
export type CaptureRouteSettings = {
  /** Every harness config path resolves from here; tests use a temporary directory. */
  homeDir: string
  /** Windows application-data root for the VS Code settings paths. */
  appDataDir?: string
  platform?: NodeJS.Platform
  fetchHealth?: HealthFetcher
  /** Deadline for the receiver liveness probe. */
  probeTimeoutMs?: number
  installProbe?: CaptureInstallProbe
}

/** The body helpers routes.ts already owns, handed in so this module never imports it at runtime. */
export type CaptureRouteIo = {
  sendJson: (res: ServerResponse, status: number, body: unknown, headers?: Record<string, string>) => void
  requireJsonContentType: (req: IncomingMessage) => boolean
  serveJsonObjectBody: (
    req: IncomingMessage,
    res: ServerResponse,
    onBody: (body: Record<string, unknown>) => void | Promise<void>,
  ) => void
}

const CAPTURE_PATH = '/api/kyber/capture'
const ERR_NOT_FOUND = 'Not found'
const ERR_METHOD = 'Method Not Allowed'
const ERR_CONTENT_TYPE = 'Content-Type must be application/json'
const ERR_INVALID = 'Invalid capture payload'
/** Fixed: a failure body must never carry a path, a key or a host error message. */
const ERR_FAILED = 'Capture failed'
const NOT_HOSTED_DETAIL =
  'this KyberDash server does not host the OTLP receiver; liveness was not probed'

const MANAGED_IDS: readonly string[] = CAPTURE_HARNESS_REGISTRY.filter(
  (writer) => writer.kind === 'managed',
).map((writer) => writer.id)

function statusContext(settings: CaptureRouteSettings): StatusContext {
  return {
    home: settings.homeDir,
    env: { platform: settings.platform ?? process.platform, appData: settings.appDataDir },
    endpoint: DEFAULT_CAPTURE_ENDPOINT,
    installProbe: settings.installProbe,
  }
}

async function receiverView(
  hosted: boolean,
  settings: CaptureRouteSettings,
): Promise<{ hosted: boolean; liveness: string; detail: string }> {
  if (!hosted) return { hosted, liveness: 'unknown', detail: NOT_HOSTED_DETAIL }
  const probe = await probeReceiver(DEFAULT_CAPTURE_ENDPOINT, settings.fetchHealth, settings.probeTimeoutMs)
  return { hosted, liveness: probe.state, detail: probe.detail }
}

type CaptureRequest = { action: 'enable' | 'disable'; harness: string; dryRun: boolean }

/** Exactly `{ action, harness, dryRun }`: every key required, none extra, a managed harness only. */
function parseCaptureRequest(body: Record<string, unknown>): CaptureRequest | undefined {
  const keys = Object.keys(body).sort()
  if (keys.length !== 3 || keys[0] !== 'action' || keys[1] !== 'dryRun' || keys[2] !== 'harness') {
    return undefined
  }
  const { action, harness, dryRun } = body
  if (action !== 'enable' && action !== 'disable') return undefined
  if (typeof harness !== 'string' || !MANAGED_IDS.includes(harness)) return undefined
  if (typeof dryRun !== 'boolean') return undefined
  return { action, harness, dryRun }
}

const REFUSAL_WORDING: Record<string, string> = {
  drift: 'drifted since enable; no files were changed',
  conflict: 'has a conflicting setting outside the managed block',
  unparseable: 'config could not be parsed',
  unreadable: 'config could not be read',
  busy: 'config is busy',
}

function refusalMessage(request: CaptureRequest, result: CaptureActionResult): string {
  const partial = result.events.some((event) => event.kind === 'wrote' || event.kind === 'restored')
  if (partial) {
    return `${request.action} refused: ${request.harness} stopped partway; some files were already changed`
  }
  const wording = REFUSAL_WORDING[result.reason ?? 'unreadable'] ?? REFUSAL_WORDING['unreadable']!
  return `${request.action} refused: ${request.harness} ${wording}`
}

async function answerGet(
  res: ServerResponse,
  store: KyberRouteDeps['store'],
  settings: CaptureRouteSettings,
  io: CaptureRouteIo,
): Promise<void> {
  const ctx = statusContext(settings)
  const hosted = readSetting(store, SETTING_KEYS.receiverHosted) === 'on'
  const harnesses = buildCaptureStatus(ctx)
  io.sendJson(res, 200, {
    platform: ctx.env.platform,
    receiver: await receiverView(hosted, settings),
    harnesses,
  })
}

async function answerPost(
  res: ServerResponse,
  store: KyberRouteDeps['store'],
  settings: CaptureRouteSettings,
  request: CaptureRequest,
  io: CaptureRouteIo,
): Promise<void> {
  const ctx = statusContext(settings)
  const result = await runCaptureAction({
    action: request.action,
    harnessId: request.harness,
    dryRun: request.dryRun,
    homeDir: settings.homeDir,
    platform: ctx.env.platform,
    ...(settings.appDataDir === undefined ? {} : { appDataDir: settings.appDataDir }),
    ...(settings.fetchHealth === undefined ? {} : { fetchHealth: settings.fetchHealth }),
    ...(settings.probeTimeoutMs === undefined ? {} : { probeTimeoutMs: settings.probeTimeoutMs }),
    fileFilter: createApplicabilityFilter(CAPTURE_HARNESS_REGISTRY, undefined, ctx.env.platform),
  })

  // D5: a real enable that wrote something makes sure the receiver is hosted, after the
  // config write succeeded. The config is already on disk, so a failing setting write is
  // reported in the receiver block (200), not as a 5xx that would misreport the state.
  // A partial write (an earlier file written, a later one refused) counts as written: the
  // receipt already records that file, so the harness needs somewhere to send.
  const wroteSomething = result.events.some((event) => event.kind === 'wrote')
  let hosted = readSetting(store, SETTING_KEYS.receiverHosted) === 'on'
  let enabled = false
  let enableFailed = false
  if (request.action === 'enable' && !request.dryRun && (result.outcome !== 'refused' || wroteSomething)) {
    if (!hosted) {
      try {
        writeSetting(store, SETTING_KEYS.receiverHosted, 'on')
        hosted = true
      } catch (error) {
        console.error('[KyberRoutes] capture could not turn the receiver on:', error)
        enableFailed = true
      }
    }
    enabled = hosted
  }

  if (result.outcome === 'refused') {
    io.sendJson(res, 409, {
      error: refusalMessage(request, result),
      outcome: 'refused',
      reason: result.reason ?? 'unreadable',
      events: result.events,
    })
    return
  }

  const statuses = buildCaptureStatus(ctx)
  const harnessState = statuses.find((status) => status.id === request.harness) as CaptureHarnessStatus
  // D7: only reported, never acted on. Disable never stops the receiver.
  const lastManagedHarnessDisabled =
    request.action === 'disable' &&
    !request.dryRun &&
    result.outcome === 'wrote' &&
    statuses
      .filter((status) => status.mode === 'managed')
      .every((status) => status.state !== 'on' && status.state !== 'partial')

  io.sendJson(res, 200, {
    action: request.action,
    harness: request.harness,
    dryRun: request.dryRun,
    outcome: result.outcome,
    events: result.events,
    harnessState,
    receiver: {
      hosted,
      willEnable: request.action === 'enable' && request.dryRun,
      enabled,
      enableFailed,
      lastManagedHarnessDisabled,
    },
  })
}

function answerFailure(res: ServerResponse, error: unknown, io: CaptureRouteIo): void {
  console.error('[KyberRoutes] capture request failed:', error)
  if (res.headersSent || res.writableEnded) return
  io.sendJson(res, 500, { error: ERR_FAILED })
}

/**
 * Serve `/api/kyber/capture`. Returns true when the path is this route's, whatever the
 * answer. Without a store the route is simply not mounted (the settings route's rule): the
 * receiver switch it needs lives there.
 */
export function handleCaptureRequest(
  req: IncomingMessage,
  res: ServerResponse,
  url: URL,
  deps: KyberRouteDeps | undefined,
  io: CaptureRouteIo,
): boolean {
  if (url.pathname !== CAPTURE_PATH) return false
  const store = deps?.store
  if (store === undefined) {
    io.sendJson(res, 404, { error: ERR_NOT_FOUND })
    return true
  }
  if (req.method !== 'GET' && req.method !== 'POST') {
    io.sendJson(res, 405, { error: ERR_METHOD }, { Allow: 'GET, POST' })
    return true
  }
  const settings: CaptureRouteSettings = deps?.capture ?? { homeDir: homedir() }

  if (req.method === 'GET') {
    answerGet(res, store, settings, io).catch((error: unknown) => answerFailure(res, error, io))
    return true
  }

  if (!io.requireJsonContentType(req)) {
    io.sendJson(res, 415, { error: ERR_CONTENT_TYPE })
    return true
  }
  io.serveJsonObjectBody(req, res, async (body) => {
    const request = parseCaptureRequest(body)
    if (request === undefined) {
      io.sendJson(res, 400, { error: ERR_INVALID })
      return
    }
    try {
      await answerPost(res, store, settings, request, io)
    } catch (error) {
      answerFailure(res, error, io)
    }
  })
  return true
}
