// Dashboard port setting: a focused display-layer control over the shared
// `dashboardPort` (`GET|PUT /api/kyber/settings`).
//
// It reads the persisted port, writes it back through the one API, and says out loud
// that the new port applies on the next engine restart — it never claims the running
// server moved. UI pixels and input validation live here; persistence and semantic
// validation belong to the engine, which refuses 0 and out-of-range ports even if a
// value slips past the check below.

import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { fetchKyberSettings, updateKyberSettings } from '../../lib/kyberApi.js'

/** A TCP port is 1..65535. 0 would mean "pick a free one", never a storable choice. */
const PORT_MIN = 1
const PORT_MAX = 65535

/**
 * The port the field holds, or null when the text cannot be one. Strict on purpose:
 * a request the browser knows is invalid never leaves the browser — the save control
 * blocks instead, rather than letting the operator discover a bad field from a 400.
 */
function parsePort(raw: string): number | null {
  if (!/^\d+$/.test(raw.trim())) return null
  const port = Number(raw.trim())
  return Number.isSafeInteger(port) && port >= PORT_MIN && port <= PORT_MAX ? port : null
}

export function DashboardPortSetting() {
  const queryClient = useQueryClient()
  const settings = useQuery({
    queryKey: ['kyber-settings'],
    queryFn: fetchKyberSettings,
    retry: false,
    refetchOnWindowFocus: false,
  })
  const [draft, setDraft] = useState<string | null>(null)
  const [savedPort, setSavedPort] = useState<number | null>(null)

  const save = useMutation({
    mutationFn: (port: number) => updateKyberSettings({ dashboardPort: port }),
    onSuccess: (next) => {
      setSavedPort(next.dashboardPort)
      setDraft(null)
    },
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: ['kyber-settings'] })
    },
  })

  if (settings.isError) {
    return (
      <p role="alert" data-testid="dashboard-port-unknown" className="text-xs text-foreground">
        The dashboard port could not be read, so no port control is shown.
      </p>
    )
  }
  if (settings.isPending || settings.data === undefined) {
    return <p className="text-xs text-muted-foreground">Reading dashboard port…</p>
  }

  const current = settings.data.dashboardPort
  // A server that predates this setting answers without `dashboardPort`. Seeding the draft
  // with `String(undefined)` would show a permanent validation error for a server that is
  // simply older, so treat a non-number the same as an unreadable setting.
  if (typeof current !== 'number') {
    return (
      <p role="alert" data-testid="dashboard-port-unknown" className="text-xs text-foreground">
        The dashboard port could not be read, so no port control is shown.
      </p>
    )
  }
  const text = draft ?? String(current)
  const parsed = parsePort(text)
  const unchanged = parsed === current
  // A save that failed keeps the draft the operator typed: clearing it would destroy the
  // evidence of what was asked for, and the error below already says it did not land.
  const failed = save.isError

  return (
    <section
      data-testid="dashboard-port-setting"
      aria-label="Dashboard port"
      className="rounded-md border border-border bg-card px-3 py-2.5"
    >
      <label className="text-sm font-medium text-foreground" htmlFor="dashboard-port-input">
        Dashboard port
      </label>
      <p className="mt-0.5 text-xs text-muted-foreground">
        The loopback port this dashboard is served on. Saving applies on the next engine restart —
        this page stays where it is until then.
      </p>
      <div className="mt-2 flex items-center gap-2">
        <input
          id="dashboard-port-input"
          // `type="text"` with a digits-only pattern, not `type="number"`: a number input
          // admits `e`, `+`, `-`, `.` and spinner steps that fight `parsePort` below. Let
          // the validator be the single source of truth. `inputMode` keeps mobile numeric.
          type="text"
          inputMode="numeric"
          pattern="[0-9]*"
          data-testid="dashboard-port-input"
          className="w-28 rounded-md border border-border bg-background px-2 py-1 text-xs text-foreground disabled:cursor-not-allowed disabled:opacity-60"
          value={text}
          min={PORT_MIN}
          max={PORT_MAX}
          disabled={save.isPending}
          aria-invalid={parsed === null}
          aria-describedby={parsed === null ? 'dashboard-port-error' : failed ? 'dashboard-port-save-error' : undefined}
          onChange={(event) => {
            setDraft(event.target.value)
            setSavedPort(null)
          }}
        />
        <button
          type="button"
          data-testid="dashboard-port-save"
          className="rounded-md border border-border px-2.5 py-1 text-xs font-medium text-foreground hover:bg-interactive-secondary disabled:cursor-not-allowed disabled:opacity-60"
          disabled={save.isPending || parsed === null || unchanged}
          onClick={() => {
            // Guarded as well as disabled: a second click while the write is in flight
            // must not queue a second PUT carrying the same value.
            if (save.isPending || parsed === null || unchanged) return
            save.mutate(parsed)
          }}
        >
          Save
        </button>
      </div>
      {parsed === null && (
        <p
          id="dashboard-port-error"
          role="alert"
          data-testid="dashboard-port-error"
          className="mt-1 text-xs text-foreground"
        >
          Enter a whole-number port from {PORT_MIN} to {PORT_MAX}. Nothing was saved.
        </p>
      )}
      {failed && (
        <p
          id="dashboard-port-save-error"
          role="alert"
          data-testid="dashboard-port-save-error"
          className="mt-1 text-xs text-foreground"
        >
          The dashboard port could not be saved. Nothing was changed.
        </p>
      )}
      {savedPort !== null && !failed && (
        <p data-testid="dashboard-port-restart-notice" role="status" className="mt-1 text-xs text-foreground">
          Dashboard port {savedPort} saved. Restart the engine to serve there; this page stays on the
          current port until then.
        </p>
      )}
    </section>
  )
}
