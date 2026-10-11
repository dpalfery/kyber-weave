// The structured capture action (T4): enable or disable ONE harness and report what
// happened as events, for the settings route.
//
// WHY it wraps `runCapture` instead of reimplementing it: the CLI and the dashboard must
// make the same edits to the same files with the same refusals, so there is one engine and
// this is its in-process, structured front door. The text `runCapture` also returns is
// ignored here; the events are built beside it, never parsed from it.
//
// The whole call runs under the capture mutex (D12) so two overlapping requests cannot
// interleave their read-modify-write of a config file or the receipt.

import { classifyFsError, type FsErrorReason } from './errno.js'
import {
  DEFAULT_CAPTURE_ENDPOINT,
  isOtlpHttpEndpoint,
  runCapture,
  type CaptureEvent,
  type CaptureFileFilter,
  partialEventsOf,
} from './index.js'
import { withCaptureLock } from './lock.js'
import { probeReceiver, type HealthFetcher } from './probe.js'
import { CAPTURE_HARNESS_REGISTRY } from './harnesses/registry.js'

export type CaptureActionOutcome = 'previewed' | 'wrote' | 'noop' | 'refused'

export type CaptureActionRequest = {
  action: 'enable' | 'disable'
  harnessId: string
  dryRun: boolean
  homeDir: string
  platform?: NodeJS.Platform
  appDataDir?: string
  endpoint?: string
  fetchHealth?: HealthFetcher
  probeTimeoutMs?: number
  fileFilter?: CaptureFileFilter
}

export type CaptureActionResult = {
  outcome: CaptureActionOutcome
  events: CaptureEvent[]
  /** Set exactly when `outcome` is `refused`. */
  reason?: FsErrorReason
}

/** A refusal's reason is its first refused event's: the first thing that went wrong. */
function refusalReason(events: readonly CaptureEvent[]): FsErrorReason {
  const refused = events.find((event) => event.kind === 'refused' || event.kind === 'error')
  return refused?.reason ?? 'unreadable'
}

function outcomeOf(events: readonly CaptureEvent[], dryRun: boolean): CaptureActionOutcome {
  if (events.some((event) => event.kind === 'refused' || event.kind === 'error')) return 'refused'
  if (events.some((event) => event.kind === 'would-write' || event.kind === 'would-restore')) {
    return 'previewed'
  }
  if (!dryRun && events.some((event) => event.kind === 'wrote' || event.kind === 'restored')) {
    return 'wrote'
  }
  return 'noop'
}

export async function runCaptureAction(request: CaptureActionRequest): Promise<CaptureActionResult> {
  if (!CAPTURE_HARNESS_REGISTRY.some((writer) => writer.id === request.harnessId)) {
    throw new Error(`kyberdash capture: unknown harness "${request.harnessId}"`)
  }
  const endpoint = request.endpoint ?? DEFAULT_CAPTURE_ENDPOINT
  // The probe is read-only and only feeds text this front door discards, so it runs before
  // the lock: overlapping requests probe side by side instead of queueing N round trips
  // behind the mutex. A usage failure (bad endpoint) is refused without probing, as before.
  const receiverProbe = isOtlpHttpEndpoint(endpoint)
    ? probeReceiver(endpoint, request.fetchHealth, request.probeTimeoutMs)
    : undefined
  return withCaptureLock(async () => {
    try {
      const result = await runCapture(request.action, {
        ...(receiverProbe === undefined ? {} : { receiverProbe }),
        harnessIds: [request.harnessId],
        dryRun: request.dryRun,
        homeDir: request.homeDir,
        endpoint,
        ...(request.platform === undefined ? {} : { platform: request.platform }),
        ...(request.appDataDir === undefined ? {} : { appDataDir: request.appDataDir }),
        ...(request.fetchHealth === undefined ? {} : { fetchHealth: request.fetchHealth }),
        ...(request.probeTimeoutMs === undefined ? {} : { probeTimeoutMs: request.probeTimeoutMs }),
        ...(request.fileFilter === undefined ? {} : { fileFilter: request.fileFilter }),
      })
      // A usage failure (bad endpoint, unknown harness) exits non-zero with no events: no
      // event can say it, so it must not fall through to a serene `noop`.
      const outcome =
        result.exitCode !== 0 && result.events.length === 0
          ? 'refused'
          : outcomeOf(result.events, request.dryRun)
      return outcome === 'refused'
        ? { outcome, events: result.events, reason: refusalReason(result.events) }
        : { outcome, events: result.events }
    } catch (error) {
      // A write that the host refused (permissions, a locked file) surfaces as an errno
      // error out of the engine. It is a refusal the owner can act on, reported by code
      // alone; anything without an errno is a defect and propagates to the route's 500.
      const { code, reason } = classifyFsError(error)
      if (code === 'UNKNOWN') throw error
      return {
        outcome: 'refused',
        reason,
        // Earlier files may already be written and receipted: keep their events.
        events: [
          ...partialEventsOf(error),
          { kind: 'error', harness: request.harnessId, fileId: '', path: '', reason, code },
        ],
      }
    }
  })
}
