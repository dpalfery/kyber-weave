/**
 * The database clean control (issue #312): clean-all plus clean-the-currently
 * selected harness, behind an inline two-step confirm. The tray holds no
 * clean logic — confirming only invokes `clean_database` with the scope, and
 * the `dash clean` child does the pausing, wiping, and resuming.
 *
 * No dialog plugin exists in this app, so the confirm is two buttons in the
 * actions row: `Clean database` arms, `Confirm clean` fires, `Cancel`
 * stands down. The disclosures ride on the armed state because the wipe
 * cannot be undone and takes no backup.
 */

import { useState } from 'react'

import type { CleanDatabaseScope } from '../viewState'
import { MAX_WEEKS, MIN_WEEKS, WEEKS_ERROR, parseWeeks } from '../weeks'

type Props = {
  harness: string
  busy: boolean
  onCleanDatabase: (scope: CleanDatabaseScope, importWeeks?: number) => void
}

export function CleanDatabase({ harness, busy, onCleanDatabase }: Props) {
  const [armed, setArmed] = useState(false)
  const [importAfter, setImportAfter] = useState(false)
  const [weeksText, setWeeksText] = useState('1')
  const [weeksInvalid, setWeeksInvalid] = useState(false)
  const selected = harness !== 'all'

  function disarm() {
    setArmed(false)
    setImportAfter(false)
    setWeeksText('1')
    setWeeksInvalid(false)
  }

  function confirm() {
    const scope: CleanDatabaseScope = selected ? { harness } : 'all'
    if (!importAfter) {
      onCleanDatabase(scope)
      disarm()
      return
    }
    const weeks = parseWeeks(weeksText)
    // Blocked before the wipe: an irreversible clean must not run on a request
    // whose import half is already known to be invalid.
    if (weeks === null) {
      setWeeksInvalid(true)
      return
    }
    onCleanDatabase(scope, weeks)
    disarm()
  }

  if (!armed) {
    return (
      <nav className="actions" data-testid="clean-database">
        <button
          type="button"
          onClick={() => setArmed(true)}
          disabled={busy}
          data-testid="clean-database-arm"
        >
          Clean database
        </button>
      </nav>
    )
  }

  return (
    <nav className="actions" data-testid="clean-database">
      <p data-testid="clean-database-disclosure">
        Wiping {selected ? `the ${harness} harness` : 'all harnesses'} cannot be undone. No backup
        is taken. OTLP-collected records will not come back. Folder history is imported only
        if you tick the box below.
      </p>
      <label className="settings__row settings__row--toggle">
        <input
          type="checkbox"
          checked={importAfter}
          onChange={(event) => setImportAfter(event.target.checked)}
          data-testid="clean-database-import"
        />
        <span>Import folder history</span>
      </label>
      <label className="settings__row">
        <span>Weeks</span>
        <input
          type="number"
          min={MIN_WEEKS}
          max={MAX_WEEKS}
          step={1}
          value={weeksText}
          disabled={!importAfter}
          onChange={(event) => setWeeksText(event.target.value)}
          data-testid="clean-database-import-weeks"
        />
      </label>
      {weeksInvalid && (
        <p role="alert" data-testid="clean-database-import-error">
          {WEEKS_ERROR}
        </p>
      )}
      <button
        type="button"
        onClick={confirm}
        disabled={busy}
        data-testid="clean-database-confirm"
      >
        Confirm clean{selected ? ` ${harness}` : ' all'}
      </button>
      <button type="button" onClick={disarm} data-testid="clean-database-cancel">
        Cancel
      </button>
    </nav>
  )
}
