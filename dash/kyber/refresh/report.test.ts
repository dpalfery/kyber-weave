import { describe, expect, it } from 'vitest'

import { formatRefreshReport, REFRESH_TABLE_HEADER, type RefreshReport } from './report.js'

const report: RefreshReport = {
  historyWeeks: 2,
  commandStartedAt: '2026-09-12T00:00:00.000Z',
  rows: [
    {
      harnessId: 'antigravity',
      units: 24,
      changed: 1,
      skipped: 23,
      created: 18,
      updated: 0,
      problems: 0,
      status: 'ok',
    },
    {
      harnessId: 'antigravity-cli',
      units: 475,
      changed: 2,
      skipped: 473,
      created: 31,
      updated: 4,
      problems: 1,
      status: 'partial',
      diagnostic: '1 unit recorded validation problems',
    },
    {
      harnessId: 'antigravity-ide',
      units: 2,
      changed: 0,
      skipped: 2,
      created: 0,
      updated: 0,
      problems: 0,
      status: 'unchanged',
    },
    {
      harnessId: 'pi',
      units: 0,
      changed: 0,
      skipped: 0,
      created: 0,
      updated: 0,
      problems: 0,
      status: 'unavailable',
    },
  ],
  derived: { sessions: 49, runs: 47, executions: 63, rollups: 38 },
  failedJobs: 0,
  derivationFailed: false,
  exitCode: 0,
}

describe('formatRefreshReport', () => {
  it('prints a stable header, one row per harness, and a derived summary without local paths', () => {
    const text = formatRefreshReport(report)
    expect(text.split('\n')[0]).toBe(REFRESH_TABLE_HEADER)
    expect(text).toContain('antigravity')
    expect(text).toContain('unavailable')
    expect(text).toContain('unchanged')
    expect(text).toContain('partial')
    expect(text).toContain('Derived: 49 sessions, 47 runs, 63 executions, 38 harness rollups')
    expect(text).not.toMatch(/\/Users\/|\/home\/|~\/\./)
    expect(text).toMatchSnapshot()
  })

  it('names failed harness jobs on the completion line', () => {
    const text = formatRefreshReport({ ...report, failedJobs: 1, exitCode: 1 })
    expect(text).toContain('Refresh completed with 1 failed harness job.')
  })
})
