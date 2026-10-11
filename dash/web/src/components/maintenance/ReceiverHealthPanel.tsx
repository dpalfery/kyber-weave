// Live OTLP receiver health, rendered at the very top of the Status panel.
//
// This is a DISPLAY layer: every fact comes from `GET /api/kyber/coverage`
// → `receiver`, and `healthy` is the authoritative saber state. The panel
// never re-derives liveness from ingest counts — historical ingest coverage
// (canonical records per source, below) and live receiver health (this
// panel) are separate sections with separate labels, so 0 canonical records
// never reads as "nothing is arriving".
//
// Colour rule (user-approved): green only when the receiver is enabled and
// actively listening, unpaused, healthy, has exactly zero failures in the
// last 5 minutes (`failures5m`), and the probe is fresh (120s). Red when
// off, paused, failing, or when health is unavailable/stale. State is always
// named in text as well as colour. The latest error reads from the flat
// `lastErrorAt` / `lastCode` / `lastError` fields when available.

import type { KyberCoverage, KyberReceiverHealth } from '../../lib/kyberApi.js'
import { formatCoverageAgo } from './CoverageIngestPanel.js'

export type CollectorState = 'live' | 'down'

export interface CollectorVerdict {
  state: CollectorState
  /** Accessible label: always names the state, never colour alone. */
  label: string
  /** One line saying why, for the detail list. */
  reason: string
}

/** How long a successful probe stays trustworthy without a fresh check. */
export const RECEIVER_STALE_AFTER_MS = 120_000

/**
 * Pure verdict over the coverage payload. Green (`live`) only when the
 * receiver is enabled, `healthy === true`, the listener is up, the receiver
 * is explicitly unpaused (`paused === false`), and the 5-minute failure
 * count (`failures5m`) is exactly 0 with a fresh `checkedAt`. Anything else —
 * including a missing `receiver` key (servers predating the probe) or a
 * stale/missing `checkedAt` — is `down` (red).
 */
export function collectorVerdict(
  coverage: KyberCoverage | undefined | null,
  nowMs: number = Date.now(),
): CollectorVerdict {
  const receiver: KyberReceiverHealth | null | undefined = coverage?.receiver ?? null
  if (receiver === null || receiver === undefined) {
    return {
      state: 'down',
      label: 'Collector down — receiver health unavailable',
      reason: 'This server did not report live receiver health, so nothing about the collector is known from this page.',
    }
  }
  if (receiver.enabled !== true) {
    return {
      state: 'down',
      label: 'Collector down — receiver not enabled',
      reason: 'The engine reports the receiver is not enabled, so it is not accepting batches.',
    }
  }
  if (receiver.healthy !== true) {
    return {
      state: 'down',
      label: 'Collector down',
      reason: describeDown(receiver),
    }
  }
  if (receiver.paused !== false) {
    return {
      state: 'down',
      label: 'Collector down — receiver paused',
      reason: 'The engine reports the receiver is paused: it is not accepting batches.',
    }
  }
  if (receiver.listening !== true) {
    return {
      state: 'down',
      label: 'Collector down — listener not accepting',
      reason: 'The engine reports the receiver is not listening.',
    }
  }
  const errors = receiver.failures5m ?? null
  if (errors !== 0) {
    return {
      state: 'down',
      label: 'Collector down — recent errors',
      reason:
        errors === null
          ? 'The 5-minute error total is unknown, so a clean bill of health cannot be claimed.'
          : `The engine reports ${errors} failed OTLP request(s) or persistence error(s) in the last 5 minutes.`,
    }
  }
  const checkedAt = typeof receiver.checkedAt === 'string' ? Date.parse(receiver.checkedAt) : NaN
  if (!Number.isFinite(checkedAt)) {
    return {
      state: 'down',
      label: 'Collector down — health check unknown',
      reason: 'The engine reports healthy but no check timestamp, so the reading cannot be trusted as current.',
    }
  }
  if (nowMs - checkedAt > RECEIVER_STALE_AFTER_MS) {
    return {
      state: 'down',
      label: 'Collector down — health reading stale',
      reason: `The last probe at ${receiver.checkedAt} is older than ${RECEIVER_STALE_AFTER_MS / 1000}s, so it is not current.`,
    }
  }
  if (checkedAt > nowMs) {
    return {
      state: 'down',
      label: 'Collector down — health reading not current',
      reason: 'The probe timestamp is in the future (clock skew), so it cannot vouch for the present.',
    }
  }
  return {
    state: 'live',
    label: 'Collector live — receiver accepting',
    reason: 'The engine reports the receiver is listening, not paused, with no errors in the last 5 minutes.',
  }
}

function describeDown(receiver: KyberReceiverHealth): string {
  if (receiver.enabled === false) return 'The engine reports the receiver is not enabled, so it is not accepting batches.'
  if (receiver.paused === true) return 'The engine reports the receiver is paused: it is not accepting batches.'
  if (receiver.listening === false) return 'The engine reports the receiver is not listening.'
  const errors = receiver.failures5m ?? null
  if (typeof errors === 'number' && errors > 0) {
    return `The engine reports ${errors} failed OTLP request(s) or persistence error(s) in the last 5 minutes.`
  }
  const status = typeof receiver.status === 'string' && receiver.status.length > 0 ? receiver.status : null
  if (status !== null && status !== 'unknown') return `The engine reports receiver status "${status}".`
  return 'The engine does not report a healthy receiver.'
}

function textOf(value: unknown): string | null {
  if (typeof value !== 'string') return null
  const trimmed = value.trim()
  return trimmed.length > 0 ? trimmed : null
}

export function ReceiverHealthPanel({ coverage, nowMs }: { coverage: KyberCoverage; nowMs?: number }) {
  const verdict = collectorVerdict(coverage, nowMs)
  const receiver = coverage.receiver ?? null
  const live = verdict.state === 'live'
  const errorsTotal = receiver?.failures5m ?? null
  const lastCode = textOf(receiver?.lastCode)
  const lastMessage = textOf(receiver?.lastError)
  const lastAt = textOf(receiver?.lastErrorAt)
  const endpoint = textOf(receiver?.endpoint) ?? 'endpoint unknown'
  const paused = receiver?.paused === true
  const listening = receiver?.listening === true ? 'listening' : receiver?.listening === false ? 'not listening' : 'listener state unknown'
  const lastAccepted =
    textOf(receiver?.lastAcceptedAt) !== null
      ? `${receiver!.lastAcceptedAt} (${formatCoverageAgo(receiver!.lastAcceptedAt as string, nowMs ?? Date.now())})`
      : 'no accepted batch recorded'

  return (
    <section
      className="rounded-lg border border-border bg-card p-chrome"
      data-testid="receiver-health-panel"
      aria-label="Live receiver health"
    >
      <div
        data-testid="collector-indicator"
        data-state={verdict.state}
        role="status"
        aria-live="polite"
        className={`flex items-center gap-density-cluster rounded-md border p-chrome-sm ${
          live
            ? 'border-green-600/40 bg-green-500/10 dark:border-green-500/40 dark:bg-green-500/10'
            : 'border-red-600/40 bg-red-500/10 dark:border-red-500/40 dark:bg-red-500/10'
        }`}
      >
        <span
          aria-hidden="true"
          data-testid="collector-dot"
          className={`inline-block h-5 w-5 shrink-0 rounded-full ${live ? 'bg-green-600' : 'bg-red-600'}`}
        />
        <div className="min-w-0">
          <p data-testid="collector-label" className="text-density-sm font-bold text-foreground">
            {verdict.label}
          </p>
          <p data-testid="collector-reason" className="text-density-xs text-muted-foreground">
            {verdict.reason}
          </p>
        </div>
      </div>
      <dl className="mt-density-cluster flex flex-col gap-density-hair text-density-xs">
        <div className="flex flex-wrap items-baseline justify-between gap-density-cluster">
          <dt className="text-muted-foreground">Endpoint</dt>
          <dd data-testid="receiver-endpoint" className="font-mono">
            {endpoint}
          </dd>
        </div>
        <div className="flex flex-wrap items-baseline justify-between gap-density-cluster">
          <dt className="text-muted-foreground">Listener</dt>
          <dd data-testid="receiver-listener">
            {listening}
            {paused ? ' · paused' : ''}
          </dd>
        </div>
        <div className="flex flex-wrap items-baseline justify-between gap-density-cluster">
          <dt className="text-muted-foreground">Last accepted batch</dt>
          <dd data-testid="receiver-last-accepted">{lastAccepted}</dd>
        </div>
        <div className="flex flex-wrap items-baseline justify-between gap-density-cluster">
          <dt className="text-muted-foreground">Errors in last 5 minutes</dt>
          <dd data-testid="receiver-errors-5m">{errorsTotal === null ? 'unknown' : String(errorsTotal)}</dd>
        </div>
        {(lastCode !== null || lastMessage !== null || lastAt !== null) && (
          <div className="flex flex-col gap-density-hair">
            <dt className="text-muted-foreground">Latest error</dt>
            <dd data-testid="receiver-last-error" className="rounded border border-border p-chrome-sm font-mono text-[11px]">
              {[lastAt, lastCode, lastMessage].filter((part) => part !== null).join(' · ')}
            </dd>
          </div>
        )}
      </dl>
      <p data-testid="receiver-coverage-note" className="mt-density-cluster text-density-xs text-muted-foreground">
        Live receiver health above; historical ingest coverage (canonical records per source) follows below. A zero
        canonical count does not mean nothing is arriving — the live indicator is the current truth.
      </p>
    </section>
  )
}
