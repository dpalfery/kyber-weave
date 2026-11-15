export type HarnessJobStatus = 'ok' | 'partial' | 'failed' | 'unchanged' | 'unavailable'

export type HarnessJobRow = {
  harnessId: string
  units: number
  changed: number
  skipped: number
  created: number
  updated: number
  problems: number
  status: HarnessJobStatus
  diagnostic?: string
}

export type RefreshDerivedSummary = {
  sessions: number
  runs: number
  executions: number
  rollups: number
}

export type RefreshReport = {
  historyWeeks: number
  commandStartedAt: string
  rows: HarnessJobRow[]
  derived: RefreshDerivedSummary
  failedJobs: number
  derivationFailed: boolean
  exitCode: 0 | 1
}

export const REFRESH_TABLE_HEADER =
  'Harness                 Units  Changed  Skipped  New  Updated  Problems  Status'

const HARNESS_WIDTH = 23

function numeric(value: number, width: number): string {
  return String(value).padStart(width)
}

export function formatHarnessRow(row: HarnessJobRow): string {
  return [
    row.harnessId.padEnd(HARNESS_WIDTH),
    numeric(row.units, 6),
    numeric(row.changed, 9),
    numeric(row.skipped, 9),
    numeric(row.created, 4),
    numeric(row.updated, 9),
    numeric(row.problems, 10),
    `  ${row.status}`,
  ].join('')
}

export function formatRefreshReport(report: RefreshReport): string {
  const lines = [REFRESH_TABLE_HEADER, ...report.rows.map(formatHarnessRow)]
  lines.push(
    `Derived: ${report.derived.sessions} sessions, ${report.derived.runs} runs, ${report.derived.executions} executions, ${report.derived.rollups} harness rollups`,
  )
  if (report.derivationFailed) {
    lines.push('Derivation failed.')
  } else if (report.failedJobs === 0) {
    lines.push('Refresh completed.')
  } else {
    const noun = report.failedJobs === 1 ? 'job' : 'jobs'
    lines.push(`Refresh completed with ${report.failedJobs} failed harness ${noun}.`)
  }
  return `${lines.join('\n')}\n`
}

export function formatRefreshDiagnostics(rows: readonly HarnessJobRow[]): string {
  return rows
    .filter((row) => (row.status === 'failed' || row.status === 'partial') && row.diagnostic)
    .map((row) => `${row.harnessId}: ${row.diagnostic}`)
    .join('\n')
}
