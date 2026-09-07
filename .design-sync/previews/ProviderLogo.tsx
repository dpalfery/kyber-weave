import type { CSSProperties } from 'react'

import { Panel, ProviderLogo } from 'codeburn-desktop'

/** Every id `SINGLE_LOGOS` resolves to one bundled mark for, in source order. */
const SINGLE_IDS = [
  'antigravity', 'claude', 'cline', 'codewhale', 'crush', 'cursor-agent',
  'devin', 'droid', 'forge', 'gemini', 'goose', 'hermes',
  'ibm-bob', 'kilo-code', 'kimi', 'kiro', 'mistral-vibe', 'mux',
  'openclaw', 'pi', 'roo-code', 'vercel-gateway', 'warp', 'zcode',
  'zed', 'zerostack',
]

/** The ids that ship a light/dark pair; the theme decides which one is shown. */
const THEMED_IDS = ['codex', 'copilot', 'cursor', 'grok', 'opencode', 'qwen']

/** The app derives a display name by title-casing the id (`Splash`, `Spend`). */
function providerLabel(id: string): string {
  return id.split(/[-\s]+/).filter(Boolean).map(part => part.charAt(0).toUpperCase() + part.slice(1)).join(' ')
}

const CELL: CSSProperties = {
  display: 'flex', flexDirection: 'column', alignItems: 'center',
  gap: 'var(--sp-1)', width: 74, fontSize: 10, color: 'var(--mut)', textAlign: 'center',
}

const GRID: CSSProperties = {
  display: 'flex', flexWrap: 'wrap', gap: 'var(--sp-3)', alignItems: 'flex-start',
}

/**
 * The whole single-mark set at 26px with its derived label. Marks are drawn to
 * different bounding boxes upstream, so the strip is deliberately not normalised
 * beyond the shared 4px radius.
 */
export function AllSingleMarks() {
  return (
    <Panel title="Bundled provider marks" right={`${SINGLE_IDS.length} ids`}>
      <div style={GRID}>
        {SINGLE_IDS.map(id => (
          <span key={id} style={CELL}>
            <ProviderLogo provider={id} size={26} />
            {providerLabel(id)}
          </span>
        ))}
      </div>
    </Panel>
  )
}

/**
 * The six themed ids. Each renders *both* marks and lets CSS pick — `.pl-light`
 * shows here, `.pl-dark` takes over under `prefers-color-scheme: dark` or
 * `:root[data-theme='dark']`, so only one is ever visible.
 */
export function ThemedPairs() {
  return (
    <Panel title="Light / dark pairs" right={`${THEMED_IDS.length} ids`}>
      <div style={GRID}>
        {THEMED_IDS.map(id => (
          <span key={id} style={CELL}>
            <ProviderLogo provider={id} size={26} />
            {providerLabel(id)}
          </span>
        ))}
      </div>
    </Panel>
  )
}

/**
 * An unknown id never renders nothing: it falls back to a `.provider-mono`
 * badge — the uppercased first character on `--fill` inside `--line`, sized and
 * scaled off the same `size` prop. Also covers a lowercased display name that
 * never matched a key.
 */
export function MonogramFallback() {
  return (
    <Panel title="Unmapped ids">
      <div style={GRID}>
        {['codebuff', 'aider', 'continue', 'grok build', 'windsurf'].map(id => (
          <span key={id} style={CELL}>
            <ProviderLogo provider={id} size={26} />
            {providerLabel(id)}
          </span>
        ))}
      </div>
    </Panel>
  )
}

/**
 * The sizes actually asked for in the app: 14 in the Sessions provider filter,
 * 15 in the Splash indexing strip, the 16 default in Settings, and 26 where a
 * mark carries a row on its own. A monogram tracks the same scale.
 */
export function SizeScale() {
  return (
    <Panel title="Size prop">
      <div style={{ display: 'flex', gap: 'var(--sp-5)', alignItems: 'flex-end' }}>
        {[14, 15, 16, 26].map(size => (
          <span key={size} style={{ ...CELL, width: 'auto' }}>
            <span style={{ display: 'inline-flex', gap: 'var(--sp-2)', alignItems: 'flex-end' }}>
              <ProviderLogo provider="claude" size={size} />
              <ProviderLogo provider="codex" size={size} />
              <ProviderLogo provider="aider" size={size} />
            </span>
            {size}px
          </span>
        ))}
      </div>
    </Panel>
  )
}

/**
 * The Settings composition the mark was drawn for: a `.card` per detected
 * provider, logo then display name then the detected-cost status.
 */
export function InSettingsRows() {
  const detected = [
    { id: 'claude', cost: '$184.20' },
    { id: 'codex', cost: '$61.08' },
    { id: 'cursor', cost: '$24.90' },
    { id: 'gemini', cost: '$7.44' },
    { id: 'codebuff', cost: '$1.02' },
  ]
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--sp-2)' }}>
      {detected.map(entry => (
        <div className="card" key={entry.id}>
          <div className="set-prov-head">
            <ProviderLogo provider={entry.id} />
            <span className="set-prov-name">{providerLabel(entry.id)}</span>
            <span className="set-status"><span className="set-dot ok" />Detected · {entry.cost}</span>
          </div>
        </div>
      ))}
    </div>
  )
}
