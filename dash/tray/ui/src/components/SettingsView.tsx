/**
 * The settings of Requirement 8.8, edited one field at a time.
 *
 * Each control sends only the field it changed, because `set_settings` takes a
 * partial document: sending the whole object would let a stale copy in this
 * webview overwrite a value something else had just changed.
 */

import type { SharedSettings, TraySettings } from '../viewState'
import { CadenceField } from './CadenceField'

type Props = {
  settings: TraySettings
  /** The server-owned document; `null` means the core could not read it. */
  sharedSettings?: SharedSettings | null
  onChange: (patch: Partial<TraySettings>) => void
  onSharedChange?: (patch: Partial<SharedSettings>) => void
  onBack: () => void
}

export function SettingsView({ settings, sharedSettings = null, onChange, onSharedChange, onBack }: Props) {
  return (
    <section className="settings" data-testid="settings-view">
      <h1 className="settings__heading">Settings</h1>

      <label className="settings__row">
        <span>Window (days)</span>
        <input
          type="number"
          min={1}
          value={settings.windowDays}
          onChange={(event) => onChange({ windowDays: Number(event.target.value) })}
          data-testid="window-days"
        />
      </label>

      <label className="settings__row">
        <span>Attention at</span>
        <input
          type="number"
          min={0}
          max={100}
          value={Math.round(settings.attentionThreshold * 100)}
          onChange={(event) =>
            onChange({ attentionThreshold: Number(event.target.value) / 100 })
          }
          data-testid="attention-threshold"
        />
      </label>

      <label className="settings__row">
        <span>Critical at</span>
        <input
          type="number"
          min={0}
          max={100}
          value={Math.round(settings.criticalThreshold * 100)}
          onChange={(event) =>
            onChange({ criticalThreshold: Number(event.target.value) / 100 })
          }
          data-testid="critical-threshold"
        />
      </label>

      <label className="settings__row settings__row--toggle">
        <input
          type="checkbox"
          checked={settings.launchAtLogin}
          onChange={(event) => onChange({ launchAtLogin: event.target.checked })}
          data-testid="launch-at-login"
        />
        <span>Launch at login</span>
      </label>

      {/*
        The server owns these (R1): shown from sharedSettings and written back
        one field at a time. data-testid precedes value/checked on purpose, the
        tests read the markup in that order.
      */}
      {sharedSettings === null ? (
        <p className="settings__note" data-testid="shared-settings-unknown">
          Shared settings unknown: the server could not be read.
        </p>
      ) : (
        <>
          <label className="settings__row settings__row--toggle">
            <input
              type="checkbox"
              data-testid="shared-folder-import"
              checked={sharedSettings.folderImportScheduled}
              onChange={(event) => onSharedChange?.({ folderImportScheduled: event.target.checked })}
            />
            <span>Scheduled folder refresh</span>
          </label>
          <small className="settings__note" data-testid="folder-import-disclosure">
            When on, the next scheduled tick imports up to two weeks of folder history.
          </small>

          {/*
            The cadence is a draft, applied by an explicit control: see
            CadenceField for why a PUT per keystroke is wrong here.
          */}
          <CadenceField
            savedMinutes={sharedSettings.refreshCadenceMinutes}
            onApply={(minutes) => onSharedChange?.({ refreshCadenceMinutes: minutes })}
          />

          <label className="settings__row settings__row--toggle">
            <input
              type="checkbox"
              data-testid="shared-receiver-hosted"
              checked={sharedSettings.receiverHosted}
              onChange={(event) => onSharedChange?.({ receiverHosted: event.target.checked })}
            />
            <span>Host OTLP receiver</span>
          </label>
        </>
      )}

      <nav className="actions">
        <button type="button" onClick={onBack} data-testid="settings-back">
          Back
        </button>
      </nav>
    </section>
  )
}
