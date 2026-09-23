/**
 * What the popover can do, last in reading order (R8.1).
 *
 * Refresh now is disabled while a refresh is running — the tray's own or a
 * terminal's (R10.2, R10.3) — and says which, because a button that silently
 * does nothing reads as broken.
 */

import type { RefreshInfo } from '../viewState'

type Props = {
  refresh: RefreshInfo
  onRefreshNow: () => void
  onOpenDashboard: () => void
  onOpenSettings: () => void
  onQuit: () => void
}

function refreshLabel(refresh: RefreshInfo): string {
  if (refresh.state === 'running') return 'Refreshing…'
  if (refresh.state === 'running-elsewhere') return 'Refresh running elsewhere'
  return 'Refresh now'
}

export function Actions({
  refresh,
  onRefreshNow,
  onOpenDashboard,
  onOpenSettings,
  onQuit,
}: Props) {
  const busy = refresh.state === 'running' || refresh.state === 'running-elsewhere'

  return (
    <nav className="actions" data-testid="actions">
      <button
        type="button"
        onClick={onRefreshNow}
        disabled={busy}
        data-testid="refresh-now"
      >
        {refreshLabel(refresh)}
      </button>
      <button type="button" onClick={onOpenDashboard} data-testid="open-dashboard">
        Open dashboard
      </button>
      <button type="button" onClick={onOpenSettings} data-testid="open-settings">
        Settings
      </button>
      <button type="button" onClick={onQuit} data-testid="quit">
        Quit
      </button>
    </nav>
  )
}
