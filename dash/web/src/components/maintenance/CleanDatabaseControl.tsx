// Database clean control for the Context Doctor ingest panel (issue #312).
//
// A "Clean database" button opens a confirmation dialog — scope (one or more
// harnesses, or all), record/session counts, and the fixed disclosures: no
// backup is taken, the wipe cannot be undone, and OTLP-collected records have
// no source logs to re-ingest from. Confirming POSTs the bounded
// `cleanDatabase` client; the status line is built from counts and scope only,
// never from a server error string (the `refreshModelWindows` convention).
import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'

import {
  cleanDatabase,
  KyberApiError,
  type CleanDatabaseResult,
  type KyberHarnessSummary,
} from '../../lib/kyberApi.js'

export interface CleanDatabaseControlProps {
  harnesses: KyberHarnessSummary[]
}

type Phase = 'idle' | 'confirm' | 'in-flight' | 'done'

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

  const observed = harnesses.filter((entry) => (entry.sampleCount ?? 0) > 0)
  const canConfirm = wipeAll || selected.length > 0

  function closeDialog() {
    setPhase('idle')
  }

  function confirmClean() {
    setPhase('in-flight')
    void cleanDatabase(
      wipeAll ? { all: true, confirm: true } : { harnesses: selected, confirm: true },
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
    <div className="mt-density-cluster">
      <button
        type="button"
        data-testid="clean-database-button"
        disabled={phase === 'in-flight'}
        onClick={() => {
          setSelected([])
          setWipeAll(false)
          setPhase('confirm')
        }}
      >
        Clean database
      </button>
      {status !== null && phase === 'done' && (
        <p data-testid="clean-database-status" className="text-density-xs text-muted-foreground mt-density-hair">
          {status}
        </p>
      )}
      {phase === 'confirm' || phase === 'in-flight' ? (
        <div
          role="dialog"
          aria-modal="true"
          aria-label="Confirm database clean"
          data-testid="clean-database-dialog"
          className="rounded-lg border border-border bg-card p-chrome mt-density-cluster"
        >
          <h4 className="text-density-xs font-semibold">Confirm database clean</h4>
          <p className="text-density-xs text-muted-foreground mt-density-hair leading-density">
            This wipes stored telemetry and cannot be undone. No backup is taken. OTLP-collected
            records have no source logs and will not come back; file-backed harnesses re-ingest
            the last 7 days.
          </p>
          <fieldset className="mt-density-cluster">
            <legend className="text-density-xs font-semibold">Scope</legend>
            <label className="flex items-center gap-density-hair text-density-xs mt-density-hair">
              <input
                type="checkbox"
                data-testid="clean-database-wipe-all"
                checked={wipeAll}
                disabled={phase === 'in-flight'}
                onChange={(event) => setWipeAll(event.target.checked)}
              />
              Wipe all harnesses
            </label>
            {!wipeAll && (
              <div className="mt-density-hair flex flex-col gap-density-hair">
                {observed.map((entry) => (
                  <label
                    key={entry.harness}
                    className="flex items-center gap-density-hair text-density-xs"
                  >
                    <input
                      type="checkbox"
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
                    {harnessLabel(entry)} ({entry.sampleCount} samples)
                  </label>
                ))}
              </div>
            )}
          </fieldset>
          <div className="mt-density-cluster flex gap-density-cluster">
            <button
              type="button"
              data-testid="clean-database-confirm"
              disabled={!canConfirm || phase === 'in-flight'}
              onClick={confirmClean}
            >
              {phase === 'in-flight' ? 'Cleaning…' : 'Confirm clean'}
            </button>
            <button
              type="button"
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
