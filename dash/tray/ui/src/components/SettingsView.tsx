/**
 * The settings of Requirement 8.8, edited one field at a time.
 *
 * Each control sends only the field it changed, because `set_settings` takes a
 * partial document: sending the whole object would let a stale copy in this
 * webview overwrite a value something else had just changed.
 */

import type { TraySettings } from '../viewState'

type Props = {
  settings: TraySettings
  onChange: (patch: Partial<TraySettings>) => void
  onBack: () => void
}

export function SettingsView({ settings, onChange, onBack }: Props) {
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
        <span>Refresh every (minutes)</span>
        <input
          type="number"
          min={1}
          value={settings.refreshMinutes}
          onChange={(event) => onChange({ refreshMinutes: Number(event.target.value) })}
          data-testid="refresh-minutes"
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

      <label className="settings__row settings__row--toggle">
        <input
          type="checkbox"
          checked={settings.hostReceiver}
          onChange={(event) => onChange({ hostReceiver: event.target.checked })}
          data-testid="host-receiver"
        />
        <span>Host OTLP receiver</span>
      </label>

      <nav className="actions">
        <button type="button" onClick={onBack} data-testid="settings-back">
          Back
        </button>
      </nav>
    </section>
  )
}
