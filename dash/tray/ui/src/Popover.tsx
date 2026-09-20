/**
 * The popover, in the order Requirement 8.1 fixes: harness selector, session
 * panel, findings, health footer, actions.
 *
 * The order is a reading order, not a layout preference. Scope first, because
 * it conditions everything under it; then what is happening now; then what to
 * do about it; then whether any of it can be trusted; then the controls. Cost
 * appears once, inside the footer, after the token figures (R8.9, R14.2).
 */

import { useState } from 'react'

import { Actions } from './components/Actions'
import { EmptyState } from './components/EmptyState'
import { FindingsList } from './components/FindingsList'
import { HarnessSelector } from './components/HarnessSelector'
import { HealthFooter } from './components/HealthFooter'
import { SessionPanel } from './components/SessionPanel'
import { SetupState } from './components/SetupState'
import { SettingsView } from './components/SettingsView'
import { StaleBanner } from './components/StaleBanner'
import { formatAge } from './format'
import type { TrayCommands, ViewState } from './viewState'

type Props = {
  state: ViewState
  commands: TrayCommands
  /** Injected so the rendered age is deterministic in tests. */
  now?: Date
}

export function Popover({ state, commands, now = new Date() }: Props) {
  const [showSettings, setShowSettings] = useState(false)

  // R6.7: a missing or too-old CLI replaces the panels. Showing the last
  // report beneath a setup notice would be presenting earlier data as current.
  if (state.phase === 'setup' && state.setup !== undefined) {
    return <SetupState setup={state.setup} onQuit={commands.quit} />
  }

  if (showSettings) {
    return (
      <SettingsView
        settings={state.settings}
        onChange={commands.setSettings}
        onBack={() => setShowSettings(false)}
      />
    )
  }

  const { report } = state
  const busy = state.refresh.state === 'running' || state.refresh.state === 'running-elsewhere'
  const hasSession = report !== null && report.latestSession != null

  return (
    <main className="popover" data-testid="popover" data-phase={state.phase}>
      {state.phase === 'stale' && (
        <StaleBanner
          fetchedAt={state.reportFetchedAt}
          error={state.error}
          age={formatAge(state.reportFetchedAt, now)}
        />
      )}

      <HarnessSelector
        report={report}
        selected={state.settings.harness}
        onSelect={(harness) => commands.setSettings({ harness })}
      />

      {hasSession ? (
        <>
          <SessionPanel report={report} onOpenView={commands.openView} />
          <FindingsList report={report} onOpenView={commands.openView} />
        </>
      ) : (
        <EmptyState onRefreshNow={commands.refreshNow} busy={busy} />
      )}

      <HealthFooter
        report={report}
        refresh={state.refresh}
        receiver={state.receiver}
        now={now}
      />

      <Actions
        refresh={state.refresh}
        onRefreshNow={commands.refreshNow}
        onOpenDashboard={() => commands.openView('/')}
        onOpenSettings={() => setShowSettings(true)}
        onQuit={commands.quit}
      />
    </main>
  )
}
