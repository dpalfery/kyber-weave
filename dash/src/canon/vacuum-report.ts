// Human-readable summary of a store vacuum, mirroring the clean report's
// shape: one line per fact, nothing here touches the store.
import type { VacuumReport } from './vacuum.js'

/**
 * Render a `VacuumReport` as terminal lines.
 *
 * WHY the sizes are given in pages as well as bytes: a page count is the
 * number SQLite itself reports, and it is what an operator compares against
 * `PRAGMA freelist_count` when they want to check the claim.
 */
export function formatVacuumReport(report: VacuumReport): string {
  const lines: string[] = []
  if (report.dryRun) {
    lines.push('Dry run: nothing was written.')
    lines.push(`Store: ${report.path}`)
    lines.push(
      `Before: ${formatBytes(report.beforeBytes)} (${count(report.pageCountBefore)} pages of ${report.pageSize} bytes)`,
    )
    lines.push(
      `Reclaimable: ${formatBytes(report.reclaimableBytes)} (${count(report.freelistPages)} free pages of ${report.pageSize} bytes)`,
    )
    return `${lines.join('\n')}\n`
  }
  lines.push(`Vacuumed: ${report.path}`)
  lines.push(
    `Before: ${formatBytes(report.beforeBytes)} (${count(report.pageCountBefore)} pages of ${report.pageSize} bytes)`,
  )
  lines.push(
    `After: ${formatBytes(report.afterBytes)} (${count(report.pageCountAfter)} pages of ${report.pageSize} bytes)`,
  )
  lines.push(`Reclaimed: ${formatBytes(report.reclaimedBytes)} (${count(report.freelistPages)} pages of freelist)`)
  return `${lines.join('\n')}\n`
}

/** A page count with thousands separators, so 813331 reads as 813,331. */
function count(pages: number): string {
  return pages.toLocaleString('en-US')
}

/** A byte count in the largest unit that keeps it readable. */
function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  const units = ['KiB', 'MiB', 'GiB', 'TiB']
  let value = bytes / 1024
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit += 1
  }
  return `${value.toFixed(1)} ${units[unit]}`
}
