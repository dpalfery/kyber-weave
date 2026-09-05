import { ConnectAffordance, Panel, ProviderLogo } from 'codeburn-desktop'

const noop = () => {}

/** The Plans quota card for a provider whose CLI has never been logged in. */
export function DisconnectedInQuotaCard() {
  return (
    <Panel className="quota-card" title="Claude" right="disconnected">
      <ConnectAffordance provider="claude" connection="disconnected" onRefresh={noop} />
    </Panel>
  )
}

/** Logged in, but macOS has not been told to release the keychain item yet. */
export function KeychainAccessDenied() {
  return (
    <Panel className="quota-card" title="Codex" right="locked">
      <ConnectAffordance provider="codex" connection="accessDenied" onRefresh={noop} />
    </Panel>
  )
}

/**
 * The Settings composition: logo, provider name, and the affordance pushed to
 * the right rail, where `.set-status` shrinks it to row scale.
 */
export function InSettingsRow() {
  return (
    <Panel title="Detected providers">
      <div className="about-row">
        <ProviderLogo provider="gemini" />
        <span className="tx">Gemini</span>
        <div className="r set-status">
          <ConnectAffordance provider="gemini" connection="disconnected" onRefresh={noop} />
        </div>
      </div>
      <div className="about-row">
        <ProviderLogo provider="kimi" />
        <span className="tx">Kimi Code</span>
        <div className="r set-status">
          <ConnectAffordance provider="kimi" connection="accessDenied" onRefresh={noop} />
        </div>
      </div>
    </Panel>
  )
}

/**
 * The provider axis end to end. The status line names each CLI from
 * PROVIDER_NAMES, so all six read as distinct rows; the accessDenied copy is
 * provider-independent and is covered by KeychainAccessDenied above.
 */
export function EveryProvider() {
  return (
    <Panel title="Live quota" right="6 providers">
      <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--sp-3)' }}>
        <ConnectAffordance provider="claude" connection="disconnected" onRefresh={noop} />
        <ConnectAffordance provider="codex" connection="disconnected" onRefresh={noop} />
        <ConnectAffordance provider="gemini" connection="disconnected" onRefresh={noop} />
        <ConnectAffordance provider="copilot" connection="disconnected" onRefresh={noop} />
        <ConnectAffordance provider="antigravity" connection="disconnected" onRefresh={noop} />
        <ConnectAffordance provider="kimi" connection="disconnected" onRefresh={noop} />
      </div>
    </Panel>
  )
}
