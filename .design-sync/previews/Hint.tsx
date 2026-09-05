import { Panel, ListRow, Hint } from 'codeburn-desktop'

/**
 * The window footer strip as the shell renders it: keycap hints on the left,
 * the refreshed-at stamp right-aligned. It sits under the section body, so the
 * `border-top` reads as a rule rather than a floating line.
 */
export function WindowFooter() {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--sp-3)' }}>
      <Panel title="Top projects">
        <ListRow no="01" title="kyber-weave" sub="412 sessions" value="$184.20" />
        <ListRow no="02" title="codeburn" sub="288 sessions" value="$121.75" />
      </Panel>
      <Hint
        items={[
          { k: '⌘1-8', label: 'Navigate' },
          { k: '⌘,', label: 'Settings' },
          { k: '⌘R', label: 'Refresh' },
        ]}
        right="refreshed 2m ago"
      />
    </div>
  )
}

/** Settings uses a shorter item set and spends the `right` slot on a caveat. */
export function SettingsFooter() {
  return (
    <Hint
      items={[
        { k: '⌘1-8', label: 'Navigate' },
        { k: '⌘R', label: 'Refresh' },
      ]}
      right="pairing uses mutual TLS · approve-style, no PIN"
    />
  )
}

/** `right` omitted — the keycaps sit alone, left-aligned, nothing pushed out. */
export function KeycapsOnly() {
  return (
    <Hint
      items={[
        { k: '↑↓', label: 'Move' },
        { k: '⏎', label: 'Expand session' },
        { k: 'Esc', label: 'Collapse' },
      ]}
    />
  )
}

/** `k` is optional per item: labels without a keycap read as a status strip. */
export function LabelsWithoutKeycaps() {
  return (
    <Hint
      items={[
        { label: 'Local scan only' },
        { label: '2 providers detected' },
        { k: '⌘R', label: 'Refresh' },
      ]}
      right="$184.20 month to date"
    />
  )
}
