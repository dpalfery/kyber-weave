// Human-readable summary of a database clean (issue #312), mirroring the
// refresh report's shape: one scope line, one counts line, one re-ingest
// line. Nothing here touches the store; it only renders a `CleanReport`.
import type { CleanReport } from './clean.js'

/** Render a `CleanReport` as terminal lines. */
export function formatCleanReport(report: CleanReport): string {
  const scope = report.harnesses.length === 1 && report.harnesses[0] === '*'
    ? 'all harnesses'
    : report.harnesses.join(', ')
  const lines = [
    `Cleaned: ${scope}`,
    `Wiped: ${report.wipe.records} records, ${report.wipe.provenance} provenance rows, ${report.wipe.checkpoints} checkpoints`,
  ]
  if (report.reingested) {
    lines.push(`Re-ingested: last ${report.historyWeeks} week${report.historyWeeks === 1 ? '' : 's'}`)
  } else {
    lines.push('Re-ingested: skipped (--no-reingest)')
  }
  return `${lines.join('\n')}\n`
}
