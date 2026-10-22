// Ingest coverage readout (issues #189/#198/#199), now rendered by the Status
// panel in `DashboardTools` rather than by Context Doctor.
//
// This is a DISPLAY layer: it reads the single `GET /api/kyber/coverage`
// payload — per-source counts under the display names the server supplies (the
// stored id rides along only as a `data-raw-source` audit attribute, never as
// the shown label), the last-received line, verbatim quarantine reasons, and
// partial-checkpoint state. Empty log ⇒ unknown; null window ⇒ unknown;
// zero-data keeps its verbatim reason.
//
// It is READ-ONLY by construction: no mutation is issued from here. The
// catalog refresh and the destructive clean live in the Maintenance panel, so
// a surface called "Status" cannot wipe the database behind the word.

import { formatCoverageWindow } from '../analysis/ScorecardMatrix.js'
import type { KyberCoverage, KyberCoverageCheckpoint } from '../../lib/kyberApi.js'

export interface CoverageIngestPanelProps {
  coverage: KyberCoverage
}

/**
 * Relative age of an ISO timestamp for the last-received line. A pure
 * formatter over a recorded timestamp — never a claim about whether the
 * receiver process is still running.
 */
export function formatCoverageAgo(iso: string, nowMs: number = Date.now()): string {
  const then = Date.parse(iso)
  if (!Number.isFinite(then)) return 'unknown age'
  const deltaMs = nowMs - then
  // A timestamp in the future (clock skew, NTP correction) is data the panel
  // cannot place — folding it into `just now` would claim fresh arrival for a
  // time that has not happened yet.
  if (deltaMs < 0) return 'in the future (clock skew)'
  const minutes = Math.floor(deltaMs / 60000)
  if (minutes < 1) return 'just now'
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.floor(minutes / 60)
  if (hours < 48) return `${hours}h ago`
  return `${Math.floor(hours / 24)}d ago`
}

/**
 * Type guard for the coverage prop union: the full payload carries `ingest`,
 * the refresh slice does not. The ingest panel renders only for the full
 * payload; anything reading refresh facts accepts either shape.
 */
export function isFullCoverage(
  coverage: KyberCoverage | { refresh: KyberCoverage['refresh'] } | undefined,
): coverage is KyberCoverage {
  return coverage !== undefined && 'ingest' in coverage
}

export function CoverageIngestPanel({ coverage }: CoverageIngestPanelProps) {
  const { refresh, ingest, quarantineByReason, checkpoints } = coverage
  const partialUnits =
    checkpoints === null ? [] : checkpoints.filter((unit) => unit.lastStatus === 'partial')
  const zeroRecordByReason = zeroRecordReasons(checkpoints)
  const zeroRecordTotal = zeroRecordByReason.reduce((sum, [, count]) => sum + count, 0)
  const receiverLine =
    ingest.status === 'unknown'
      ? 'no receiver activity recorded — receiver status is not observable from this page'
      : ingest.lastReceivedAt === null
        ? 'last received unknown (no receiver timestamp recorded)'
        : `last received ${ingest.lastReceivedAt} (${formatCoverageAgo(ingest.lastReceivedAt)})`

  return (
    <section
      className="rounded-lg border border-border bg-card p-chrome"
      data-testid="coverage-ingest-panel"
      aria-label="Ingest coverage"
    >
      <h3 className="text-density-xs font-semibold uppercase tracking-density text-heading">
        Ingest coverage
      </h3>
      <p className="text-density-xs text-muted-foreground mt-density-hair leading-density" data-testid="coverage-window">
        {formatCoverageWindow(refresh)}
      </p>
      <p className="text-density-xs text-muted-foreground mt-density-hair leading-density" data-testid="coverage-last-received">
        {receiverLine}
      </p>
      {ingest.status === 'known' && (
        <ul className="mt-density-cluster flex flex-col gap-density-hair">
          {ingest.sources.map((entry) => (
            <li
              key={entry.source}
              data-testid="coverage-source-row"
              data-raw-source={entry.source}
              className="flex flex-wrap items-baseline justify-between gap-density-cluster text-density-xs"
            >
              <span data-testid="coverage-source-label">{entry.display || entry.source}</span>
              <span className="text-muted-foreground">
                {entry.recordCount} records · {entry.kind}
              </span>
            </li>
          ))}
        </ul>
      )}
      {quarantineByReason.length > 0 && (
        <ul className="mt-density-cluster flex flex-col gap-density-hair">
          {quarantineByReason.map((entry) => (
            <li
              key={entry.reason}
              data-testid="coverage-quarantine-row"
              className="flex flex-wrap items-baseline justify-between gap-density-cluster text-density-xs"
            >
              <span>{entry.reason}</span>
              <span>{entry.count}</span>
            </li>
          ))}
        </ul>
      )}
      {checkpoints === null ? (
        <p
          className="text-density-xs text-muted-foreground mt-density-hair leading-density"
          data-testid="coverage-checkpoints-unknown"
        >
          checkpoint status not observable from this page
        </p>
      ) : (
        partialUnits.length > 0 && (
          <div className="mt-density-cluster text-density-xs" data-testid="coverage-partial">
            <p>
              {partialUnits.length} source{partialUnits.length === 1 ? '' : 's'} partial (problems
              recorded, coverage incomplete)
            </p>
            <ul className="mt-density-hair flex flex-col gap-density-hair">
              {partialUnits.map((unit) => (
                <li
                  key={`${unit.harnessId}:${unit.sourceKey}`}
                  data-testid="coverage-partial-unit"
                  className="text-muted-foreground"
                >
                  {unit.harnessId}: {unit.recordCount} records
                </li>
              ))}
            </ul>
          </div>
        )
      )}
      {zeroRecordTotal > 0 && (
        <div className="mt-density-cluster text-density-xs" data-testid="coverage-zero-record">
          <p>
            {zeroRecordTotal} source{zeroRecordTotal === 1 ? '' : 's'} discovered but not ingested
          </p>
          <ul className="mt-density-hair flex flex-col gap-density-hair">
            {zeroRecordByReason.map(([reason, count]) => (
              <li
                key={reason}
                data-testid="coverage-zero-record-reason"
                className="flex flex-wrap items-baseline justify-between gap-density-cluster text-muted-foreground"
              >
                <span>{reason}</span>
                <span>{count}</span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </section>
  )
}

/**
 * Zero-record checkpoints grouped by their persisted reason (issue #196).
 * Only `ok`/`unchanged` units that carry a reason count: failed and partial
 * units already surface through their own status, and a unit with no reason
 * is never given an invented one.
 */
function zeroRecordReasons(
  checkpoints: readonly KyberCoverageCheckpoint[] | null,
): [string, number][] {
  if (checkpoints === null) return []
  const counts = new Map<string, number>()
  for (const unit of checkpoints) {
    if (unit.recordCount !== 0 || unit.lastErrorCode === null) continue
    if (unit.lastStatus !== 'ok' && unit.lastStatus !== 'unchanged') continue
    counts.set(unit.lastErrorCode, (counts.get(unit.lastErrorCode) ?? 0) + 1)
  }
  return [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
}