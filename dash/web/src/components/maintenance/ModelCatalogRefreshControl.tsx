// Model-window catalog refresh control (issue #189).
//
// Served from the Maintenance panel: the catalog drives the context-window
// figures the findings engine measures against, so refreshing it is a
// maintenance action, not a status readout. It was rendered inside the ingest
// coverage panel until the two panels were separated, which put a mutating
// control under a heading that promised a reading of the store.
//
// The status line is built from row count, timestamp, and vendor names —
// never from a refresh error string, which can carry a remote body.

import { useState } from 'react'

import { refreshModelWindows, type ModelCatalogRefreshResult } from '../../lib/kyberApi.js'

function formatModelCatalogRefreshStatus(result: ModelCatalogRefreshResult): string {
  const failed = result.vendorsFailed.map((entry) => entry.vendor)
  const when = result.lastRefreshAt ?? ''
  if (failed.length > 0 && result.vendorsUpdated.length > 0) {
    return `Partial refresh. Failed vendors: ${failed.join(', ')}. ${result.rowCount} rows at ${when}`
  }
  if (failed.length > 0) {
    return `Refresh failed for ${failed.join(', ')}.`
  }
  return `${result.rowCount} rows at ${when}`
}

export function ModelCatalogRefreshControl() {
  const [inFlight, setInFlight] = useState(false)
  const [status, setStatus] = useState<string | null>(null)

  return (
    <div className="mt-density-cluster">
      <button
        type="button"
        data-testid="model-catalog-refresh-button"
        disabled={inFlight}
        onClick={() => {
          setInFlight(true)
          void refreshModelWindows()
            .then((result) => {
              setStatus(formatModelCatalogRefreshStatus(result))
            })
            .catch(() => {
              setStatus('Refresh failed. The model-window catalog was left unchanged.')
            })
            .finally(() => {
              setInFlight(false)
            })
        }}
      >
        Refresh model windows
      </button>
      {status !== null && (
        <p data-testid="model-catalog-refresh-status" className="text-density-xs text-muted-foreground mt-density-hair">
          {status}
        </p>
      )}
    </div>
  )
}