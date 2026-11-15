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
    // No automatic re-ingest any more: a wipe leaves the store without folder history
    // until someone asks for it again, with `dash import-history` or `dash clean
    // --reingest-weeks`. Naming the opt-in here, rather than the flag that used to force
    // it off, keeps the line true for every caller of cleanDatabase (A6).
    lines.push('Re-ingested: none (opt in with --reingest-weeks <n>)')
  }
  return `${lines.join('\n')}\n`
}
