import { useEffect, useState } from 'react'
import { invoke } from '@tauri-apps/api/core'
import { listen } from '@tauri-apps/api/event'

import { Popover } from './Popover'
import { hidePopover } from './hidePopover'
import type { TraySettings, ViewState } from './viewState'

/**
 * Holds the current `ViewState` and forwards the popover's commands.
 *
 * The state arrives two ways — asked for once on mount, and pushed after that
 * by `view-state-changed`. Both, rather than either: polling from here would
 * duplicate the Rust side's own schedule, and waiting only for an event would
 * leave a popover opened between events showing nothing.
 */
export function App() {
  const [state, setState] = useState<ViewState | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [loadAttempt, setLoadAttempt] = useState(0)

  useEffect(() => {
    let cancelled = false

    void invoke<ViewState>('get_view_state')
      .then((initial) => {
        if (!cancelled) {
          setState(initial)
          setLoadError(null)
        }
      })
      .catch((error: unknown) => {
        if (!cancelled) setLoadError(errorMessage(error))
      })

    return () => {
      cancelled = true
    }
  }, [loadAttempt])

  useEffect(() => {
    let cancelled = false
    const unlisten = listen<ViewState>('view-state-changed', (event) => {
      if (!cancelled) setState(event.payload)
    })

    return () => {
      cancelled = true
      void unlisten.then((stop) => stop()).catch(() => undefined)
    }
  }, [])

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        void hidePopover()
      }
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [])

  if (loadError !== null) {
    return (
      <main className="popover popover--error" data-testid="ipc-error" role="alert">
        <p>Could not read the tray state: {loadError}</p>
        <button
          type="button"
          onClick={() => setLoadAttempt((attempt) => attempt + 1)}
          data-testid="retry-load"
        >
          Retry
        </button>
      </main>
    )
  }

  if (state === null) {
    return (
      <main className="popover popover--loading" data-testid="popover-loading">
        <p>Reading the store…</p>
      </main>
    )
  }

  return (
    <Popover
      state={state}
      actionError={actionError}
      commands={{
        refreshNow: () => invokeAction('refresh_now'),
        openView: (view) => invokeAction('open_view', { view }),
        setSettings: (patch: Partial<TraySettings>) => invokeAction('set_settings', { patch }),
        quit: () => invokeAction('quit'),
      }}
    />
  )

  function invokeAction(command: string, args?: Record<string, unknown>): void {
    const request = args === undefined ? invoke(command) : invoke(command, args)
    void request
      .then(() => setActionError(null))
      .catch((error: unknown) => setActionError(errorMessage(error)))
  }
}

function errorMessage(error: unknown): string {
  if (error instanceof Error && error.message !== '') return error.message
  if (typeof error === 'string' && error !== '') return error
  if (error !== null && typeof error === 'object' && 'message' in error) {
    const message = (error as { message?: unknown }).message
    if (typeof message === 'string' && message !== '') return message
  }
  return 'The tray did not provide an error message.'
}
