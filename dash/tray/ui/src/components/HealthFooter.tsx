/**
 * Whether what is above can be trusted.
 *
 * R10.6 fixes the contents: the age of the last successful refresh, the OTLP
 * receiver's status, the quarantine count and the problem count. R10.5 adds the
 * last failure, which sits alongside the last success rather than replacing it
 * — "it last worked at" and "it last failed because" answer different questions.
 *
 * Cost lives here, once, as a secondary line after the token figures (R8.9,
 * R14.2). Never in the panels above, and never something a reader meets first.
 */

import type { ContextReport } from '../../../../src/analysis/report/types.ts'
import type { ReceiverStatus, RefreshInfo } from '../viewState'
import { formatAge, formatMeasured } from '../format'

const RECEIVER_LABELS: Record<ReceiverStatus, string> = {
  reachable: 'receiver reachable',
  'not-reachable': 'receiver not reachable',
  hosted: 'receiver hosted by KyberDash',
  // A receiver that was deliberately switched off is not a fault, and must not read as
  // one: 'not-reachable' says the network or the process is broken, which is exactly the
  // wrong thing to tell someone who turned it off on purpose.
  off: 'receiver off',
  'port-held-by-other': 'receiver port held by another process',
  unknown: 'receiver unknown',
}

type Props = {
  report: ContextReport | null
  /**
   * `null`: the core reported no refresh state at all — the age is unknown and is
   * labelled as such, never rendered as `never`.
   */
  refresh: RefreshInfo | null
  receiver: ReceiverStatus
  now: Date
}

export function HealthFooter({ report, refresh, receiver, now }: Props) {
  const coverage = report?.coverage
  const cost = report?.cost ?? []

  return (
    <footer className="health-footer" data-testid="health-footer">
      <p className="health-footer__line" data-testid="refresh-age">
        {/* WHY: `null` here means unknown, not zero — the Rust core sends `jobs: null`
            when its jobs fetch fails, so there is no refresh state at all. Rendering
            `formatAge(null)` would state "never", which claims a known last-success
            time that does not exist. The same rule governs `jobs: null` in ipc.rs and
            `refresh: null` in viewState.ts: absent is unknown, and unknown is said so
            rather than defaulted away. `never` stays for a *known* refresh state whose
            lastSuccessAt is null — that genuinely is "no success yet". */}
        {refresh == null ? (
          'Refresh status unknown'
        ) : (
          <>
            Refreshed {formatAge(refresh.lastSuccessAt, now)}
            {refresh.state === 'running' && ' · refreshing now'}
            {refresh.state === 'running-elsewhere' && ' · a refresh is already running'}
          </>
        )}
      </p>

      {refresh?.lastFailure != null && (
        <p className="health-footer__line health-footer__line--failure" data-testid="refresh-failure">
          Last refresh failed: {refresh.lastFailure}
        </p>
      )}

      <p className="health-footer__line" data-testid="receiver-status">
        {RECEIVER_LABELS[receiver]}
      </p>

      <p className="health-footer__line" data-testid="store-counts">
        {coverage?.quarantineCount ?? '—'} quarantined · {coverage?.problemCount ?? '—'} problems
      </p>

      {cost.length > 0 && (
        <p className="health-footer__line health-footer__line--cost" data-testid="cost-line">
          {cost
            .map((entry) => `${entry.basis}: ${formatMeasured(entry.amountUsd)}`)
            .join(' · ')}
        </p>
      )}
    </footer>
  )
}
