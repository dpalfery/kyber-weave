/**
 * Pause/Resume of scheduled jobs, and the manual folder history import.
 *
 * Architecture rule R1: this is display only. The paused flag and job state
 * come from `ViewState.jobs`; the buttons forward intent. Pause stops ONLY
 * scheduled jobs, so nothing here (or elsewhere in the popover) is disabled
 * by it - a greyed-out Import while paused would strand a user with stale data.
 */

import { useState } from 'react'

import type { ImportFolderHistoryRequest, JobsInfo, SharedSettings } from '../viewState'
import { MAX_WEEKS, MIN_WEEKS, WEEKS_ERROR, parseWeeks } from '../weeks'

type Props = {
  /** `null` is unknown: the core could not read the server's job state. */
  jobs: JobsInfo | null
  harness: string
  onSetSharedSettings: (patch: Partial<SharedSettings>) => void
  onImportFolderHistory: (request: ImportFolderHistoryRequest) => void
}

function stateLabel(jobs: JobsInfo | null): string {
  if (jobs === null) return 'Job state unknown'
  if (jobs.paused) return 'Scheduled jobs: paused'
  return 'Scheduled jobs: running'
}

export function Maintenance({ jobs, harness, onSetSharedSettings, onImportFolderHistory }: Props) {
  const [weeksText, setWeeksText] = useState('1')
  const [weeksInvalid, setWeeksInvalid] = useState(false)

  const paused = jobs?.paused ?? false

  function importHistory() {
    const weeks = parseWeeks(weeksText)
    if (weeks === null) {
      setWeeksInvalid(true)
      return
    }
    setWeeksInvalid(false)
    // The harness field is left out for "all" so the server sees an unscoped import.
    onImportFolderHistory(harness === 'all' ? { weeks } : { weeks, harness })
  }

  return (
    <section className="maintenance" data-testid="maintenance">
      <p
        className="maintenance__state"
        data-testid="jobs-state"
        data-paused={jobs === null ? 'unknown' : String(jobs.paused)}
      >
        {stateLabel(jobs)}
      </p>

      {jobs?.paused === true && (
        <p className="maintenance__note" data-testid="jobs-paused-state">
          Only scheduled jobs are stopped. Manual actions, OTLP ingest and retention purge continue.
        </p>
      )}

      <div className="actions">
        {/* Disabled when unknown: toggling blind could invert the real state. */}
        <button
          type="button"
          onClick={() => onSetSharedSettings({ jobsPaused: !paused })}
          disabled={jobs === null}
          data-testid="pause-toggle"
        >
          {paused ? 'Resume' : 'Pause'}
        </button>
      </div>

      <div className="maintenance__import">
        <label className="settings__row">
          <span>Import weeks</span>
          <input
            type="number"
            min={MIN_WEEKS}
            max={MAX_WEEKS}
            step={1}
            value={weeksText}
            onChange={(event) => setWeeksText(event.target.value)}
            aria-invalid={weeksInvalid}
            data-testid="import-weeks"
          />
        </label>
        {weeksInvalid && (
          <p className="maintenance__error" role="alert" data-testid="import-weeks-error">
            {WEEKS_ERROR}
          </p>
        )}
        <div className="actions">
          <button type="button" onClick={importHistory} data-testid="import-folder-history">
            Import folder history
          </button>
        </div>
      </div>
    </section>
  )
}
