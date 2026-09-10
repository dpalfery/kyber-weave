import { useEffect, useState, type ReactNode } from 'react'
import { invoke } from '@tauri-apps/api/core'
import { openUrl } from '@tauri-apps/plugin-opener'
import type { CurrencyState } from '../lib/currency'
import { CURRENCY_CODES } from '../lib/currency'
import { homePath, TRAY_BADGE_SUPPORTED } from '../lib/platform'
import type { CliStatus } from './SetupState'
import { DropMenu } from './DropMenu'
import { ChevronDown, ChevronRight } from './Icons'

/// Preferences that have no home in the popover proper. Deliberately small: the mac app has
/// no settings window at all, so everything here is a Windows/Linux need (login item, tray
/// text) or a convenience the footer already offers in a smaller form.

export type ThemeChoice = 'system' | 'light' | 'dark'

const GITHUB_URL = 'https://github.com/getagentseal/codeburn'

type Props = {
  onBack: () => void
  version: string
  currency: CurrencyState
  onCurrency: (code: string) => void
  themeChoice: ThemeChoice
  onThemeChoice: (t: ThemeChoice) => void
  trayBadge: boolean
  onTrayBadge: (on: boolean) => void
  cliStatus: CliStatus | null
  onCheckCli: () => void
  cliChecking: boolean
  onQuit: () => void
}

export function SettingsPanel({
  onBack, version, currency, onCurrency, themeChoice, onThemeChoice, trayBadge, onTrayBadge,
  cliStatus, onCheckCli, cliChecking, onQuit,
}: Props) {
  const [loginItem, setLoginItem] = useState<boolean | null>(null)
  const [loginError, setLoginError] = useState<string | null>(null)

  // No CLI probe here: App owns the gate and probes it on mount, so the panel only ever
  // displays what that probe found. Its own probe could otherwise fail transiently and drop
  // a working app onto the setup screen.
  useEffect(() => {
    invoke<boolean>('launch_at_login').then(setLoginItem).catch(() => setLoginItem(false))
  }, [])

  const toggleLogin = async () => {
    if (loginItem === null) return
    setLoginError(null)
    try {
      setLoginItem(await invoke<boolean>('set_launch_at_login', { enabled: !loginItem }))
    } catch (err) {
      setLoginError(err instanceof Error ? err.message : String(err))
    }
  }

  return (
    <section className="settings">
      <div className="settings-head">
        <button type="button" className="btn btn-icon" onClick={onBack} aria-label="Back">
          <ChevronRight size={11} style={{ transform: 'rotate(180deg)' }} />
        </button>
        <span className="settings-title">Settings</span>
      </div>

      <div className="settings-group">
        <div className="settings-group-label">General</div>
        <Row label="Launch at login" hint="Start CodeBurn in the tray when you sign in.">
          <Toggle on={loginItem === true} disabled={loginItem === null} onToggle={toggleLogin} />
        </Row>
        {loginError && <div className="settings-error">{loginError}</div>}
        {TRAY_BADGE_SUPPORTED && (
          <Row label="Show today's cost in the tray" hint="A second tray icon carrying the number, next to the logo.">
            <Toggle on={trayBadge} onToggle={() => onTrayBadge(!trayBadge)} />
          </Row>
        )}
      </div>

      <div className="settings-group">
        <div className="settings-group-label">Appearance</div>
        <Row label="Theme">
          <div className="segmented">
            {(['system', 'light', 'dark'] as ThemeChoice[]).map(t => (
              <button
                key={t}
                type="button"
                className={`segment ${themeChoice === t ? 'segment-active' : ''}`}
                aria-pressed={themeChoice === t}
                onClick={() => onThemeChoice(t)}
              >
                {t === 'system' ? 'System' : t === 'light' ? 'Light' : 'Dark'}
              </button>
            ))}
          </div>
        </Row>
        <Row label="Currency" hint={`Shared with the CLI via ${homePath('.config', 'codeburn', 'config.json')}.`}>
          <DropMenu
            label={<><span>{currency.code}</span><ChevronDown size={10} /></>}
            items={CURRENCY_CODES.map(c => ({ id: c, label: c, checked: c === currency.code }))}
            columns={3}
            align="right"
            onSelect={onCurrency}
          />
        </Row>
      </div>

      <div className="settings-group">
        <div className="settings-group-label">Data source</div>
        <Row
          label="CodeBurn CLI"
          hint={cliStatus?.found ? `Version ${cliStatus.version ?? '?'} · ${cliStatus.program}` : 'Not found on this machine.'}
        >
          <button type="button" className="btn" onClick={onCheckCli} disabled={cliChecking}>
            {cliChecking ? 'Checking…' : 'Check again'}
          </button>
        </Row>
      </div>

      <div className="settings-group">
        <div className="settings-group-label">About</div>
        <Row label={`CodeBurn Desktop ${version ? `v${version}` : ''}`} hint="Tracks AI coding spend from local session logs. Nothing leaves this machine except the Claude usage check.">
          <button type="button" className="btn" onClick={() => openUrl(GITHUB_URL)}>GitHub</button>
        </Row>
        <Row label="Quit CodeBurn" hint="Removes the tray icon until you launch it again.">
          <button type="button" className="btn" onClick={onQuit}>Quit</button>
        </Row>
      </div>
    </section>
  )
}

function Row({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <div className="settings-row">
      <div className="settings-row-text">
        <div className="settings-row-label">{label}</div>
        {hint && <div className="settings-row-hint">{hint}</div>}
      </div>
      <div className="settings-row-control">{children}</div>
    </div>
  )
}

function Toggle({ on, disabled = false, onToggle }: { on: boolean; disabled?: boolean; onToggle: () => void }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={on}
      className={`toggle ${on ? 'toggle-on' : ''}`}
      disabled={disabled}
      onClick={onToggle}
    >
      <span className="toggle-knob" />
    </button>
  )
}
