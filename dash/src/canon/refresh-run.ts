// The refresh run log.
//
// A refresh either succeeded, failed, or is still going. The data-health footer and the
// report's coverage section have to say which, and keep showing the last *successful*
// refresh even while a later one has failed (R10.5) — so a single "last refreshed"
// timestamp is not enough. One row per run answers all three questions, and answers them
// after a crash too: a row with no `completed_at` whose pid is gone is a run that died,
// which is a different thing from one still in progress.
//
// Rows are small and bounded by how often a human or the tray refreshes, so they are kept
// rather than rolled up. The history is what makes "it has failed every time since
// Tuesday" answerable.

/** How a refresh was started. Recorded so a failing cadence is distinguishable from a failing person. */
export type RefreshTrigger = 'cli' | 'tray' | 'scheduled'

export type RefreshRunStatus = 'running' | 'success' | 'failure'

export const REFRESH_TRIGGERS: readonly RefreshTrigger[] = ['cli', 'tray', 'scheduled']

export type RefreshRunRow = {
  id: string
  startedAt: string
  completedAt: string | null
  status: RefreshRunStatus
  /** The process that holds (or held) the refresh lock, so a stale row names its owner. */
  pid: number
  trigger: RefreshTrigger
  /** One line: what the run did, or why it failed. */
  summary: string | null
}

export const REFRESH_RUN_SQL = `
-- Refresh run log (spec: kyberdash-context-surfaces, R10.3-R10.5).
CREATE TABLE IF NOT EXISTS refresh_run (
  id TEXT PRIMARY KEY,
  started_at TEXT NOT NULL,
  completed_at TEXT,
  status TEXT NOT NULL,
  pid INTEGER NOT NULL,
  trigger TEXT NOT NULL,
  summary TEXT
);
-- The three questions the footer asks are all "most recent X", so both indexes are
-- descending on start time: one over everything, one narrowed by status.
CREATE INDEX IF NOT EXISTS refresh_run_by_started_at ON refresh_run (started_at DESC);
CREATE INDEX IF NOT EXISTS refresh_run_by_status ON refresh_run (status, started_at DESC);
`
