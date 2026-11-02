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

import { isDeepStrictEqual } from 'node:util'

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
  /**
   * Settings-only opt-in for disable with no receipt: remove only this
   * harness's declared OTEL keys that are present, leaving every unrelated
   * key alone. Defaults to false; the CLI never sets it.
   */
  removeUnreceiptedKeys?: boolean
  /** Exact preview shown by Settings; the locked write refuses if it changed. */
  expectedPreviewEvents?: CaptureEvent[]
  homeDir: string
  platform?: NodeJS.Platform
  appDataDir?: string
  endpoint?: string
  fetchHealth?: HealthFetcher
  probeTimeoutMs?: number
  fileFilter?: CaptureFileFilter
  /** OpenCode config root override, forwarded to the file plugin. */
  opencodeConfigDir?: string
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

/**
 * The client's read shape (`readEvent`/`readEvents` in `kyberApi.ts`): at most 64 events,
 * each carrying at most 64 changes. The confirm echo is bounded to that shape, so the
 * engine's fresh preview must be bounded the same way before comparison — otherwise a
 * preview with more changes than the client keeps can never equal its own echo, and every
 * confirm of a large preview ends in a spurious PREVIEW_CHANGED.
 */
const PREVIEW_EVENTS_MAX = 64
const PREVIEW_CHANGES_MAX = 64

function boundedPreviewEvents(events: readonly CaptureEvent[]): CaptureEvent[] {
  return events.slice(0, PREVIEW_EVENTS_MAX).map((event) =>
    event.changes === undefined
      ? { ...event }
      : { ...event, changes: event.changes.slice(0, PREVIEW_CHANGES_MAX) },
  )
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
  if (
    request.action === 'disable' &&
    request.removeUnreceiptedKeys === true &&
    !request.dryRun &&
    request.expectedPreviewEvents === undefined
  ) {
    return {
      outcome: 'refused',
      reason: 'drift',
      events: [
        {
          kind: 'refused',
          harness: request.harnessId,
          fileId: 'preview',
          path: '',
          reason: 'drift',
          code: 'PREVIEW_REQUIRED',
        },
      ],
    }
  }
  if (
    request.expectedPreviewEvents !== undefined &&
    (request.action !== 'disable' || request.removeUnreceiptedKeys !== true || request.dryRun)
  ) {
    throw new Error('kyberdash capture: expected preview events are only valid on a confirmed Settings disable')
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
      const runOptions = {
        ...(receiverProbe === undefined ? {} : { receiverProbe }),
        harnessIds: [request.harnessId],
        removeUnreceiptedKeys: request.removeUnreceiptedKeys,
        homeDir: request.homeDir,
        endpoint,
        ...(request.platform === undefined ? {} : { platform: request.platform }),
        ...(request.appDataDir === undefined ? {} : { appDataDir: request.appDataDir }),
        ...(request.fetchHealth === undefined ? {} : { fetchHealth: request.fetchHealth }),
        ...(request.probeTimeoutMs === undefined ? {} : { probeTimeoutMs: request.probeTimeoutMs }),
        ...(request.fileFilter === undefined ? {} : { fileFilter: request.fileFilter }),
        ...(request.opencodeConfigDir === undefined ? {} : { opencodeConfigDir: request.opencodeConfigDir }),
      }
      if (request.expectedPreviewEvents !== undefined) {
        const preview = await runCapture('disable', { ...runOptions, dryRun: true })
        if (preview.exitCode !== 0 && preview.events.length === 0) {
          // The preview run failed before it could describe anything (a usage failure
          // such as a bad endpoint). That is neither drift nor an unreadable config, so
          // name it rather than let the empty event list wear the 'unreadable' costume
          // and report "config could not be read" for a config nobody read.
          return {
            outcome: 'refused',
            reason: 'unreadable',
            events: [
              {
                kind: 'refused',
                harness: request.harnessId,
                fileId: 'preview',
                path: '',
                reason: 'unreadable',
                code: 'PREVIEW_UNAVAILABLE',
              },
            ],
          }
        }
        if (
          preview.exitCode !== 0 ||
          !isDeepStrictEqual(boundedPreviewEvents(preview.events), request.expectedPreviewEvents)
        ) {
          const events = [...preview.events]
          if (preview.exitCode === 0) {
            events.push({
              kind: 'refused',
              harness: request.harnessId,
              fileId: 'preview',
              path: '',
              reason: 'drift',
              code: 'PREVIEW_CHANGED',
            })
          }
          return {
            outcome: 'refused',
            reason: refusalReason(events),
            events,
          }
        }
      }
      const result = await runCapture(request.action, {
        ...runOptions,
        dryRun: request.dryRun,
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
