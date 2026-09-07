import { ProviderPop } from 'codeburn-desktop'

/** The top bar's option set: the aggregate sentinel, then each detected CLI. */
const DETECTED = [
  { value: 'all', label: 'All providers' },
  { value: 'claude', label: 'Claude' },
  { value: 'codex', label: 'Codex' },
  { value: 'cursor', label: 'Cursor' },
  { value: 'copilot', label: 'Copilot' },
  { value: 'gemini', label: 'Gemini' },
]

/**
 * Where it actually lives: the right end of the `.bar`, after the spacer. The
 * default selection is the `all` sentinel, which has no logo art — the
 * `.provider-mono` initial tile is the deliberate fallback.
 */
export function InTheTopBar() {
  return (
    <div className="bar">
      <div className="t">Overview</div>
      <span className="scope">Last 30 days · All providers</span>
      <div className="sp" />
      <ProviderPop value="all" label="All providers" options={DETECTED} onSelect={() => {}} />
    </div>
  )
}

/**
 * A single provider selected. The trigger carries that provider's logo, so the
 * scope of every number on the screen is legible without reading the label.
 */
export function ProviderSelected() {
  return (
    <div style={{ display: 'flex', gap: 'var(--sp-3)', alignItems: 'center' }}>
      <ProviderPop value="claude" label="Claude" options={DETECTED} onSelect={() => {}} />
    </div>
  )
}

/**
 * The logo axis. `codex`, `cursor` and `copilot` ship light/dark logo pairs
 * (CSS picks one), `gemini` a single mark — the trigger geometry is identical
 * either way.
 */
export function LogosByProvider() {
  return (
    <div style={{ display: 'flex', gap: 'var(--sp-3)', alignItems: 'center', flexWrap: 'wrap' }}>
      <ProviderPop value="codex" label="Codex" options={DETECTED} onSelect={() => {}} />
      <ProviderPop value="cursor" label="Cursor" options={DETECTED} onSelect={() => {}} />
      <ProviderPop value="copilot" label="Copilot" options={DETECTED} onSelect={() => {}} />
      <ProviderPop value="gemini" label="Gemini" options={DETECTED} onSelect={() => {}} />
    </div>
  )
}

/**
 * The trigger text comes from the matching option's `label`, never from the
 * `label` prop (kept only for API compatibility). Give the options the richer
 * strings and the trigger shows them, ellipsising past the trigger width.
 */
export function LabelFromOptions() {
  return (
    <div style={{ display: 'flex', gap: 'var(--sp-3)', alignItems: 'center' }}>
      <ProviderPop
        value="claude"
        label="Claude"
        options={[
          { value: 'all', label: 'All providers · $184.20' },
          { value: 'claude', label: 'Claude · 412 sessions' },
          { value: 'codex', label: 'Codex · 97 sessions' },
        ]}
        onSelect={() => {}}
      />
    </div>
  )
}
