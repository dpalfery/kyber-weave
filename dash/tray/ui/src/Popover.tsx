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
import { CleanDatabase } from './components/CleanDatabase'
import { EmptyState } from './components/EmptyState'
import { FindingsList } from './components/FindingsList'
import { HarnessSelector } from './components/HarnessSelector'
import { HealthFooter } from './components/HealthFooter'
import { Maintenance } from './components/Maintenance'
import { SessionPanel } from './components/SessionPanel'
import { SetupState } from './components/SetupState'
import { SettingsView } from './components/SettingsView'
import { StaleBanner } from './components/StaleBanner'
import { formatAge } from './format'
import type { ReceiverStatus, RefreshInfo, SharedSettings, TrayCommands, ViewState } from './viewState'

/**
 * The footer's receiver line, derived from the server-owned settings document.
 *
 * The Rust core no longer serializes a `receiver` field (the server owns the
 * receiver now), so reading only that field left the footer saying "receiver
 * unknown" forever. `receiverHosted: false` is not unknown: the server answered,
 * and what it says is that it does not host the receiver here.
 */
export function receiverStatus(sharedSettings: SharedSettings | null | undefined): ReceiverStatus {
  if (sharedSettings == null) return 'unknown'
  return sharedSettings.receiverHosted ? 'hosted' : 'not-reachable'
}

const IDLE_REFRESH: RefreshInfo = { state: 'idle', lastSuccessAt: null, lastFailure: null }

type Props = {
  state: ViewState
  commands: TrayCommands
  actionError?: string | null
  /** Injected so the rendered age is deterministic in tests. */
  now?: Date
}

export function Popover({ state, commands, actionError = null, now = new Date() }: Props) {
  const [showSettings, setShowSettings] = useState(false)
  const actionErrorBanner =
    actionError === null ? null : (
      <div className="ipc-action-error" data-testid="ipc-action-error" role="alert">
        Tray action failed: {actionError}
      </div>
    )

  // R6.7: a missing or too-old CLI replaces the panels. Showing the last
  // report beneath a setup notice would be presenting earlier data as current.
  if (state.phase === 'setup' && state.setup !== undefined) {
    return (
      <>
        {actionErrorBanner}
        <SetupState setup={state.setup} onQuit={commands.quit} />
      </>
    )
  }

  if (showSettings) {
    return (
      <>
        {actionErrorBanner}
        <SettingsView
          settings={state.settings}
          sharedSettings={state.sharedSettings ?? null}
          onChange={commands.setSettings}
          onSharedChange={commands.setSharedSettings}
          onBack={() => setShowSettings(false)}
        />
      </>
    )
  }

  const { report } = state
  // The server's job state is authoritative; the legacy field only fills in
  // when the core sent none. Absent means unknown, shown as idle-and-clickable
  // because a refresh the server rejects reports through the error banner.
  const jobs = state.jobs ?? null
  const refresh = jobs?.refresh ?? state.refresh ?? IDLE_REFRESH
  const busy = refresh.state === 'running' || refresh.state === 'running-elsewhere'
  const hasSession = report !== null && report.latestSession != null

  return (
    <main className="popover" data-testid="popover" data-phase={state.phase}>
      {actionErrorBanner}
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
        refresh={jobs?.refresh ?? state.refresh ?? null}
        receiver={state.receiver ?? receiverStatus(state.sharedSettings)}
        now={now}
      />

      <CleanDatabase
        harness={state.settings.harness}
        busy={busy}
        onCleanDatabase={commands.cleanDatabase}
      />

      <Maintenance
        jobs={jobs}
        harness={state.settings.harness}
        onSetSharedSettings={commands.setSharedSettings}
        onImportFolderHistory={commands.importFolderHistory}
      />

      <Actions
        refresh={refresh}
        onRefreshNow={commands.refreshNow}
        onOpenDashboard={() => commands.openView('/')}
        onOpenSettings={() => setShowSettings(true)}
        onQuit={commands.quit}
      />
    </main>
  )
}
