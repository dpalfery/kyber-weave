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

type Props = {
  harness: string
  busy: boolean
  onCleanDatabase: (scope: CleanDatabaseScope) => void
}

export function CleanDatabase({ harness, busy, onCleanDatabase }: Props) {
  const [armed, setArmed] = useState(false)
  const selected = harness !== 'all'

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
        is taken. OTLP-collected records will not come back.
      </p>
      <button
        type="button"
        onClick={() => {
          onCleanDatabase(selected ? { harness } : 'all')
          setArmed(false)
        }}
        disabled={busy}
        data-testid="clean-database-confirm"
      >
        Confirm clean{selected ? ` ${harness}` : ' all'}
      </button>
      <button type="button" onClick={() => setArmed(false)} data-testid="clean-database-cancel">
        Cancel
      </button>
    </nav>
  )
}
