// Database clean control for the Context Doctor ingest panel (issue #312).
//
// A "Wipe database" button opens a confirmation dialog — scope (one or more
// harnesses, or all), record/session counts, and the fixed disclosures: no
// backup is taken, the wipe cannot be undone, and OTLP-collected records have
// no source logs to re-ingest from. Confirming POSTs the bounded
// `cleanDatabase` client; the status line is built from counts and scope only,
// never from a server error string (the `refreshModelWindows` convention).
//
// The button is named "Wipe database" rather than "Clean database" because it
// renders in the Maintenance panel beside "Refresh data now": "clean" beside
// a refresh reads as the same kind of action, and it is not. The endpoint,
// request body and dialog are unchanged.
//
// Issue #319: re-ingest is now an explicit operator choice. The dialog carries
// an UNCHECKED "import folder history" box and a weeks window (1..52, default
// 1); only a ticked box sends `reingestWeeks`. An unconditional promise of a
// fixed 7-day re-ingest described behaviour the operator never chose — and a
// clean that silently imports history nobody asked for is the worse failure of
// the two, because it rewrites the store twice in one gesture.
import { useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'

import {
  cleanDatabase,
  KyberApiError,
  type CleanDatabaseResult,
  type KyberHarnessSummary,
} from '../../lib/kyberApi.js'
import {
  ACTION_BUTTON,
  CHECKBOX,
  DESTRUCTIVE_BUTTON,
  FIELD_LABEL,
  HELPER_TEXT,
  INSET_FIELD,
  NUMBER_FIELD,
} from './controls.js'

export interface CleanDatabaseControlProps {
  harnesses: KyberHarnessSummary[]
}

type Phase = 'idle' | 'confirm' | 'in-flight' | 'done'

/** Bounds of the re-ingest window, matching the server's accepted range. */
const MIN_REINGEST_WEEKS = 1
const MAX_REINGEST_WEEKS = 52

/**
 * A whole number of weeks inside the accepted range, or null.
 *
 * <remarks>
 * `1.5`, `''` and `53` are all windows the server refuses, so an invalid value
 * blocks the confirm rather than sending a destructive request to be told no:
 * the operator who typed it would be one click from a wipe.
 * </remarks>
 */
function parseReingestWeeks(raw: string): number | null {
  if (!/^\d+$/.test(raw.trim())) return null
  const weeks = Number(raw.trim())
  return weeks >= MIN_REINGEST_WEEKS && weeks <= MAX_REINGEST_WEEKS ? weeks : null
}

function harnessLabel(entry: KyberHarnessSummary): string {
  return entry.name && entry.name.length > 0 ? entry.name : entry.harness
}

function formatCleanStatus(result: CleanDatabaseResult): string {
  const scope = result.harnesses.length === 1 && result.harnesses[0] === '*'
    ? 'all harnesses'
    : result.harnesses.join(', ')
  const wiped = `${result.records} record${result.records === 1 ? '' : 's'} wiped (${scope})`
  if (!result.reingested || result.historyWeeks === null) {
    return `${wiped}. Re-ingestion skipped.`
  }
  return `${wiped}. Re-ingesting the last ${result.historyWeeks} week${result.historyWeeks === 1 ? '' : 's'}.`
}

function formatCleanFailure(status: number | null): string {
  if (status === 409) {
    return 'Clean did not run: a refresh or clean is already running. Try again when it finishes.'
  }
  return 'Clean failed. Nothing was confirmed wiped — check the store before retrying.'
}

export function CleanDatabaseControl({ harnesses }: CleanDatabaseControlProps) {
  const queryClient = useQueryClient()
  const [phase, setPhase] = useState<Phase>('idle')
  const [selected, setSelected] = useState<string[]>([])
  const [wipeAll, setWipeAll] = useState(false)
  const [status, setStatus] = useState<string | null>(null)
  // Re-ingest is opt-in (issue #319): the box ships unchecked, and the weeks
  // field only becomes a request once it is ticked.
  const [importHistory, setImportHistory] = useState(false)
  const [reingestWeeks, setReingestWeeks] = useState(String(MIN_REINGEST_WEEKS))
  // A render-gated `disabled` cannot stop two clicks batched before re-render,
  // so the in-flight mutex lives in a ref the handler checks synchronously.
  const cleanInFlight = useRef(false)

  const observed = harnesses.filter((entry) => (entry.sampleCount ?? 0) > 0)
  const weeks = parseReingestWeeks(reingestWeeks)
  const canConfirm =
    (wipeAll || selected.length > 0) && (!importHistory || weeks !== null)

  function closeDialog() {
    setPhase('idle')
  }

  function confirmClean() {
    if (cleanInFlight.current) return
    cleanInFlight.current = true
    setPhase('in-flight')
    const scope = wipeAll ? { all: true } : { harnesses: selected }
    void cleanDatabase(
      importHistory && weeks !== null
        ? { ...scope, reingestWeeks: weeks, confirm: true }
        : { ...scope, confirm: true },
    )
      .then((result) => {
        setStatus(formatCleanStatus(result))
        setPhase('done')
        void queryClient.invalidateQueries()
      })
      .catch((err: unknown) => {
        setStatus(formatCleanFailure(err instanceof KyberApiError ? err.status : null))
        setPhase('done')
      })
  }

  return (
    // Its own tinted block, away from the catalog refresh above it: the one
    // control here that cannot be undone must not share a row with a control
    // that can.
    <div className="mt-density-stack rounded-md border border-red-500/30 bg-red-500/5 p-chrome-sm dark:border-red-500/30 dark:bg-red-500/10">
      <button
        type="button"
        className={DESTRUCTIVE_BUTTON}
        data-testid="clean-database-button"
        disabled={phase === 'in-flight'}
        onClick={() => {
          setSelected([])
          setWipeAll(false)
          setImportHistory(false)
          setReingestWeeks(String(MIN_REINGEST_WEEKS))
          cleanInFlight.current = false
          setPhase('confirm')
        }}
      >
        Wipe database
      </button>
      {status !== null && phase === 'done' && (
        <p data-testid="clean-database-status" className={`${HELPER_TEXT} mt-density-hair`}>
          {status}
        </p>
      )}
      {phase === 'confirm' || phase === 'in-flight' ? (
        <div
          role="dialog"
          aria-modal="true"
          aria-label="Confirm database clean"
          data-testid="clean-database-dialog"
          className="mt-density-cluster flex flex-col gap-density-cluster rounded-md border border-border bg-card p-chrome"
        >
          <h4 className="text-density-xs font-semibold uppercase tracking-density text-heading">
            Confirm database clean
          </h4>
          <p className={HELPER_TEXT}>
            This wipes stored telemetry and cannot be undone. No backup is taken. OTLP-collected
            records have no source logs and will not come back; file-backed harnesses can import
            folder history again, but only for the window you choose below.
          </p>
          <fieldset className={INSET_FIELD}>
            <legend className="px-chrome-xs text-density-xs font-semibold text-heading">Scope</legend>
            <label className="flex items-start gap-density-hair text-density-xs">
              <input
                type="checkbox"
                className={`${CHECKBOX} mt-px`}
                data-testid="clean-database-wipe-all"
                checked={wipeAll}
                disabled={phase === 'in-flight'}
                onChange={(event) => setWipeAll(event.target.checked)}
              />
              <span>Wipe all harnesses</span>
            </label>
            {!wipeAll && (
              <div className="flex flex-col gap-density-hair">
                {observed.map((entry) => (
                  <label
                    key={entry.harness}
                    className="flex items-start gap-density-hair text-density-xs"
                  >
                    <input
                      type="checkbox"
                      className={`${CHECKBOX} mt-px`}
                      data-testid={`clean-database-harness-${entry.harness}`}
                      checked={selected.includes(entry.harness)}
                      disabled={phase === 'in-flight'}
                      onChange={(event) => {
                        setSelected((current) =>
                          event.target.checked
                            ? [...current, entry.harness]
                            : current.filter((h) => h !== entry.harness),
                        )
                      }}
                    />
                    <span>
                      {harnessLabel(entry)} ({entry.sampleCount} samples)
                    </span>
                  </label>
                ))}
              </div>
            )}
          </fieldset>
          <fieldset className={INSET_FIELD}>
            <legend className="px-chrome-xs text-density-xs font-semibold text-heading">
              Import folder history (optional)
            </legend>
            <label className="flex items-start gap-density-hair text-density-xs">
              <input
                type="checkbox"
                className={`${CHECKBOX} mt-px`}
                data-testid="clean-database-import-history"
                checked={importHistory}
                disabled={phase === 'in-flight'}
                onChange={(event) => setImportHistory(event.target.checked)}
              />
              <span>Import folder history after the wipe</span>
            </label>
            <label className={FIELD_LABEL} htmlFor="clean-database-import-weeks-field">
              <span className="text-muted-foreground">Weeks</span>
              <input
                id="clean-database-import-weeks-field"
                type="number"
                className={NUMBER_FIELD}
                data-testid="clean-database-import-weeks"
                value={reingestWeeks}
                min={MIN_REINGEST_WEEKS}
                max={MAX_REINGEST_WEEKS}
                disabled={phase === 'in-flight' || !importHistory}
                onChange={(event) => setReingestWeeks(event.target.value)}
              />
            </label>
            <p className={HELPER_TEXT}>
              {importHistory && weeks === null
                ? `Choose a whole number of weeks from ${MIN_REINGEST_WEEKS} to ${MAX_REINGEST_WEEKS} to confirm.`
                : `Leave unticked to wipe without importing folder history. A window is ${MIN_REINGEST_WEEKS}–${MAX_REINGEST_WEEKS} weeks.`}
            </p>
          </fieldset>
          <div className="flex flex-wrap gap-density-cluster">
            <button
              type="button"
              className={DESTRUCTIVE_BUTTON}
              data-testid="clean-database-confirm"
              disabled={!canConfirm || phase === 'in-flight'}
              onClick={confirmClean}
            >
              {phase === 'in-flight' ? 'Cleaning…' : 'Confirm clean'}
            </button>
            <button
              type="button"
              className={ACTION_BUTTON}
              data-testid="clean-database-cancel"
              disabled={phase === 'in-flight'}
              onClick={closeDialog}
            >
              Cancel
            </button>
          </div>
        </div>
      ) : null}
    </div>
  )
}
