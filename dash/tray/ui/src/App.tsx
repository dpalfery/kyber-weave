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

  useEffect(() => {
    let cancelled = false

    void invoke<ViewState>('get_view_state').then((initial) => {
      if (!cancelled) setState(initial)
    })

    const unlisten = listen<ViewState>('view-state-changed', (event) => {
      setState(event.payload)
    })

    return () => {
      cancelled = true
      void unlisten.then((stop) => stop())
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
      commands={{
        refreshNow: () => void invoke('refresh_now'),
        openView: (view) => void invoke('open_view', { view }),
        setSettings: (patch: Partial<TraySettings>) => void invoke('set_settings', { patch }),
        quit: () => void invoke('quit'),
      }}
    />
  )
}
