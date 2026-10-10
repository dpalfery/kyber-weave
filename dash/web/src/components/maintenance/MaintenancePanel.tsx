// Maintenance panel (issue #319, architecture rule R1).
//
// This is a DISPLAY layer: it renders the state `GET /api/kyber/jobs` and
// `GET /api/kyber/settings` report, and issues the bounded maintenance calls.
// It schedules nothing, imports nothing and decides nothing — the JobHost on
// the server owns all of that. Every fact on screen therefore comes from a
// fetch, and a fetch that failed renders "unknown" rather than a default:
// substituting "off", "0 records" or "healthy" for an unreadable read is the
// dashboard inventing server state it never received.
//
// Two consequences shape the controls here:
//   - Pause/Resume reads `paused` from the JOBS payload (what the host is
//     actually doing), not from the settings echo, and the label derives from
//     it. Pausing stops SCHEDULED jobs only, so the disclosure says so out
//     loud — otherwise a paused host reads as a frozen dashboard.
//   - Nothing is disabled while paused. Pause gates the scheduler, not the
//     operator: manual refresh, folder import and clean must keep working.

import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import {
  fetchKyberJobs,
  fetchKyberSettings,
  importKyberHistory,
  KyberApiError,
  requestKyberRefresh,
  updateKyberSettings,
  type KyberHarnessSummary,
} from '../../lib/kyberApi.js'

export interface MaintenancePanelProps {
  /** Harnesses the store holds, used to narrow a one-off folder import. */
  harnesses: KyberHarnessSummary[]
}

/** The import window bounds, matching the server's accepted range. */
const MIN_WEEKS = 1
const MAX_WEEKS = 52

/**
 * A whole number of weeks inside the server's accepted range, or null.
 *
 * <remarks>
 * Strict on purpose: `1.5` and `''` are not a window the server would accept,
 * and a request the browser knows is invalid should never leave the browser —
 * the confirm and import controls block instead, rather than letting the
 * operator destroy data to discover a bad field.
 * </remarks>
 */
function parseWeeks(raw: string): number | null {
  if (!/^\d+$/.test(raw.trim())) return null
  const weeks = Number(raw.trim())
  return weeks >= MIN_WEEKS && weeks <= MAX_WEEKS ? weeks : null
}

/**
 * The refresh cadence in minutes, or null when the field cannot be one. The
 * range is the server's own (a day is the top of a useful cadence), and it is
 * deliberately not the import-window range: the two numbers mean different
 * things and sharing one validator would reject a legitimate cadence.
 */
function parseCadence(raw: string): number | null {
  if (!/^\d+$/.test(raw.trim())) return null
  const minutes = Number(raw.trim())
  return minutes >= 1 && minutes <= 1440 ? minutes : null
}

/** One labelled server fact, or "unknown" when the read failed. */
function Fact({ id, label, value }: { id: string; label: string; value: string }) {
  return (
    <p data-testid={id} className="text-density-xs text-muted-foreground">
      {label}: {value}
    </p>
  )
}

export function MaintenancePanel({ harnesses }: MaintenancePanelProps) {
  const queryClient = useQueryClient()
  const [importWeeks, setImportWeeks] = useState('1')
  const [importHarnesses, setImportHarnesses] = useState<string[]>([])
  const [cadence, setCadence] = useState('')
  const [refreshStatus, setRefreshStatus] = useState<string | null>(null)
  const [refreshError, setRefreshError] = useState(false)
  const [importStatus, setImportStatus] = useState<string | null>(null)
  const [importError, setImportError] = useState(false)
  const [settingsStatus, setSettingsStatus] = useState<string | null>(null)
  const [settingsError, setSettingsError] = useState(false)


  const jobs = useQuery({ queryKey: ['kyber-jobs'], queryFn: fetchKyberJobs })
  const settings = useQuery({ queryKey: ['kyber-settings'], queryFn: fetchKyberSettings })

  // A settings read that has not landed (still loading, or failed) is UNKNOWN,
  // not false. The toggle therefore renders unchecked-indeterminate and
  // disabled rather than claiming an "off" the server never reported; the
  // `indeterminate` DOM property is what a checkbox uses for that third state
  // and has no JSX attribute, so it is applied here.
  const settingsKnown = settings.data !== undefined
  const scheduledToggle = useRef<HTMLInputElement | null>(null)
  useEffect(() => {
    const node = scheduledToggle.current
    if (node === null) return
    node.indeterminate = !settingsKnown
  }, [settingsKnown])

  // Every mutation re-reads the state it changed, so the panel never renders an
  // optimistic guess of what the host did.
  const settle = (): void => {
    void queryClient.invalidateQueries({ queryKey: ['kyber-jobs'] })
    void queryClient.invalidateQueries({ queryKey: ['kyber-settings'] })
  }

  const refresh = useMutation({
    mutationFn: () => requestKyberRefresh('web'),
    onSuccess: () => {
      setRefreshError(false)
      setRefreshStatus('Refresh requested. The host runs it in the background.')
    },
    onError: (error: unknown) => {
      setRefreshError(true)
      // 409 is the host saying a run is already in flight — never the remote
      // body, which can carry a pid and a server path.
      setRefreshStatus(
        error instanceof KyberApiError && error.status === 409
          ? 'A refresh is already running.'
          : 'Refresh could not be started.',
      )
    },
    onSettled: settle,
  })

  const runImport = useMutation({
    mutationFn: (weeks: number) => importKyberHistory({ weeks, harnesses: importHarnesses }),
    onSuccess: () => {
      setImportError(false)
      setImportStatus('Folder-history import requested.')
    },
    onError: () => {
      setImportError(true)
      setImportStatus('Folder-history import could not be started.')
    },
    onSettled: settle,
  })

  const writeSetting = useMutation({
    mutationFn: (update: Parameters<typeof updateKyberSettings>[0]) => updateKyberSettings(update),
    onSuccess: () => {
      setSettingsError(false)
      setSettingsStatus('Setting saved.')
    },
    onError: () => {
      setSettingsError(true)
      setSettingsStatus('Setting could not be saved.')
    },
    onSettled: settle,
  })

  const weeks = parseWeeks(importWeeks)
  const paused = jobs.data?.paused === true
  const cadenceMinutes = parseCadence(cadence)
  const observedHarnesses = harnesses.filter((entry) => (entry.sampleCount ?? 0) > 0)

  return (
    <section
      className="rounded-lg border border-border bg-card p-chrome"
      data-testid="maintenance-panel"
      aria-label="Maintenance"
    >
      <h3 className="text-density-xs font-semibold uppercase tracking-density text-heading">
        Maintenance
      </h3>

      {/* Job state, verbatim from GET /api/kyber/jobs. A failed read says so
          rather than showing a healthy default. */}
      <div className="mt-density-cluster">
        <Fact
          id="maintenance-jobs-refresh-state"
          label="Scheduled refresh"
          value={jobs.data?.refresh.state ?? 'unknown'}
        />
        <Fact
          id="maintenance-jobs-last-success"
          label="Last successful refresh"
          value={jobs.data?.refresh.lastSuccessAt ?? 'unknown'}
        />
        <Fact
          id="maintenance-jobs-last-failure"
          label="Last failure"
          value={jobs.data?.refresh.lastFailure ?? 'unknown'}
        />
        <Fact
          id="maintenance-jobs-next-due"
          label="Next due"
          value={jobs.data?.refresh.nextDueAt ?? 'unknown'}
        />
        <Fact
          id="maintenance-jobs-store-generation"
          label="Store generation"
          value={
            jobs.data === undefined ? 'unknown' : String(jobs.data.storeGeneration)
          }
        />
        <Fact
          id="maintenance-jobs-paused"
          label="Scheduled jobs"
          value={jobs.data === undefined ? 'unknown' : paused ? 'paused' : 'not paused'}
        />
        <Fact
          id="maintenance-jobs-hosted-elsewhere"
          label="Jobs"
          value={
            jobs.data === undefined
              ? 'unknown'
              : jobs.data.hostedElsewhere
                ? 'hosted elsewhere (another process holds the lease)'
                : 'Not hosted elsewhere — this host runs them'
          }
        />
      </div>

      <div className="mt-density-cluster flex flex-wrap gap-density-cluster">
        <button
          type="button"
          data-testid="maintenance-pause-button"
          onClick={() => writeSetting.mutate({ jobsPaused: !paused })}
        >
          {paused ? 'Resume scheduled jobs' : 'Pause scheduled jobs'}
        </button>
        <button
          type="button"
          data-testid="maintenance-refresh-button"
          onClick={() => refresh.mutate()}
        >
          Refresh data now
        </button>
      </div>
      <p
        data-testid="maintenance-pause-disclosure"
        className="text-density-xs text-muted-foreground mt-density-hair"
      >
        Pausing stops only scheduled jobs. Manual refresh, OTLP collection and retention pruning
        continue, and so does every control on this page.
      </p>
      <p
        data-testid="maintenance-refresh-status"
        className="text-density-xs text-muted-foreground mt-density-hair"
        role={refreshError ? 'alert' : 'status'}
      >
        {refreshStatus ?? 'No refresh requested from this page yet.'}
      </p>

      {/* A one-off import. It is deliberately separate from the scheduled
          toggle below: "import this once" is not "keep importing". */}
      <fieldset className="mt-density-cluster">
        <legend className="text-density-xs font-semibold">Import folder history</legend>
        <label className="flex items-center gap-density-hair text-density-xs mt-density-hair">
          Weeks
          <input
            type="number"
            data-testid="maintenance-import-weeks"
            value={importWeeks}
            min={MIN_WEEKS}
            max={MAX_WEEKS}
            onChange={(event) => setImportWeeks(event.target.value)}
          />
        </label>
        <div className="mt-density-hair flex flex-col gap-density-hair">
          {observedHarnesses.map((entry) => (
            <label
              key={entry.harness}
              className="flex items-center gap-density-hair text-density-xs"
            >
              <input
                type="checkbox"
                data-testid={`maintenance-import-harness-${entry.harness}`}
                checked={importHarnesses.includes(entry.harness)}
                onChange={(event) =>
                  setImportHarnesses((current) =>
                    event.target.checked
                      ? [...current, entry.harness]
                      : current.filter((h) => h !== entry.harness),
                  )
                }
              />
              {entry.name && entry.name.length > 0 ? entry.name : entry.harness}
            </label>
          ))}
        </div>
        <button
          type="button"
          className="mt-density-hair"
          data-testid="maintenance-import-button"
          disabled={weeks === null || runImport.isPending}
          onClick={() => {
            if (weeks === null) return
            runImport.mutate(weeks)
          }}
        >
          Import folder history
        </button>
        <p
          data-testid="maintenance-import-status"
          className="text-density-xs text-muted-foreground mt-density-hair"
          role={importError ? 'alert' : importStatus ? 'status' : undefined}
        >
          {importStatus ??
            `Weeks must be a whole number from ${MIN_WEEKS} to ${MAX_WEEKS}. No harness selected means all of them.`}
        </p>
      </fieldset>

      {/* The shared settings read back over the same API the tray uses. */}
      <div className="mt-density-cluster">
        <Fact
          id="maintenance-settings-folder-import-scheduled"
          label="Scheduled folder import"
          value={
            settings.data === undefined
              ? 'unknown'
              : settings.data.folderImportScheduled
                ? 'scheduled'
                : 'off (not scheduled)'
          }
        />
        <Fact
          id="maintenance-settings-refresh-cadence"
          label="Refresh cadence"
          value={
            settings.data === undefined
              ? 'unknown'
              : `${settings.data.refreshCadenceMinutes} minutes`
          }
        />
        <Fact
          id="maintenance-settings-receiver-hosted"
          label="OTLP receiver"
          value={
            settings.data === undefined
              ? 'unknown'
              : settings.data.receiverHosted
                ? 'hosted here'
                : 'not hosted by this process'
          }
        />
      </div>

      <fieldset className="mt-density-cluster">
        <legend className="text-density-xs font-semibold">Scheduled maintenance</legend>
        <label className="flex items-center gap-density-hair text-density-xs mt-density-hair">
          <input
            ref={scheduledToggle}
            type="checkbox"
            data-testid="maintenance-folder-import-toggle"
            checked={settingsKnown ? settings.data.folderImportScheduled : false}
            disabled={!settingsKnown}
            aria-describedby="maintenance-folder-import-unknown-hint"
            onChange={(event) => writeSetting.mutate({ folderImportScheduled: event.target.checked })}
          />
          Import folder history on a schedule
        </label>
        <p
          id="maintenance-folder-import-unknown-hint"
          data-testid="maintenance-folder-import-unknown-hint"
          className="text-density-xs text-muted-foreground mt-density-hair"
        >
          {settingsKnown
            ? 'Reflects the scheduled folder-import setting read back from the host.'
            : 'Scheduled folder import is unknown: the settings could not be read, so this is disabled rather than showing a default. See the fact above.'}
        </p>
        <p
          data-testid="maintenance-folder-import-disclosure"
          className="text-density-xs text-muted-foreground mt-density-hair"
        >
          Turning this on does not import the whole window now: the host catches up at its next
          scheduled run, which imports up to two weeks and keeps a rolling window.
        </p>
        <label className="flex items-center gap-density-hair text-density-xs mt-density-hair">
          Refresh cadence (minutes)
          <input
            type="number"
            data-testid="maintenance-cadence-input"
            value={settingsKnown ? cadence : ''}
            placeholder={settingsKnown ? String(settings.data.refreshCadenceMinutes) : ''}
            min={1}
            max={1440}
            disabled={!settingsKnown}
            onChange={(event) => setCadence(event.target.value)}
          />
        </label>
        <button
          type="button"
          className="mt-density-hair"
          data-testid="maintenance-cadence-apply"
          disabled={!settingsKnown || cadenceMinutes === null}
          onClick={() => {
            if (cadenceMinutes === null) return
            writeSetting.mutate({ refreshCadenceMinutes: cadenceMinutes })
            setCadence('')
          }}
        >
          Apply cadence
        </button>
        <p
          data-testid="maintenance-settings-status"
          className="text-density-xs text-muted-foreground mt-density-hair"
          role={settingsError ? 'alert' : settingsStatus ? 'status' : undefined}
        >
          {settingsStatus ?? 'No setting change requested from this page yet.'}
        </p>
      </fieldset>
    </section>
  )
}
