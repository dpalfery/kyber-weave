import { Panel, ListRow, CliErrorText } from 'codeburn-desktop'

/**
 * The default tone: one 12px red line where a panel's rows would be. This is the
 * whole component — it is only ever read inside a `.pbody`, so it is composed in
 * the Panel it really appears in.
 */
export function InPanelBody() {
  return (
    <Panel title="Detected providers" right="Settings">
      <CliErrorText error={{ kind: 'nonzero', message: 'codeburn exited with code 1' }} />
    </Panel>
  )
}

/**
 * `cold: true` swaps the copy for the indexing line and drops it to `--mut2`.
 * The panel around it stays calm because nothing is actually broken.
 */
export function ColdIndexing() {
  return (
    <Panel title="Plan pacing" right="Last 30 days">
      <CliErrorText error={{ kind: 'timeout', message: 'codeburn plans produced no output for 45000ms', cold: true }} />
    </Panel>
  )
}

/**
 * The amber tone: a `nonzero` whose stderr mentions permission is rewritten to
 * the short actionable line rather than echoing the CLI.
 */
export function PermissionDenied() {
  return (
    <Panel title="Device scan" right="3 paired devices">
      <CliErrorText error={{ kind: 'nonzero', message: 'Cursor permission denied: Full Disk Access required' }} />
    </Panel>
  )
}

/**
 * All three tones in one body — muted / amber / red — which is the component's
 * only visual axis. `not-found` shares the muted tone but carries the install
 * copy instead of the raw stderr.
 */
export function ToneScale() {
  return (
    <Panel title="Tones">
      <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--sp-2)' }}>
        <CliErrorText error={{ kind: 'not-found', message: 'codeburn CLI not found' }} />
        <CliErrorText error={{ kind: 'nonzero', message: 'permission denied while reading ~/.claude/projects' }} />
        <CliErrorText error={{ kind: 'bad-json', message: 'codeburn produced output that was not valid JSON' }} />
      </div>
    </Panel>
  )
}

/**
 * The Spend idiom: the section's main data resolved, one secondary request did
 * not. The text sits under the rows it failed to extend, so the panel keeps its
 * last-good content instead of being replaced by a `CliErrorPanel`.
 */
export function BesideLastGoodData() {
  return (
    <Panel title="Spend by project" right="Last 30 days">
      <ListRow no="01" title="kyber-weave" sub="412 sessions" value="$184.20" />
      <ListRow no="02" title="codeburn" sub="288 sessions" value="$121.75" />
      <ListRow no="03" title="agentseal-site" sub="97 sessions" value="$38.40" />
      <div style={{ paddingTop: 'var(--sp-3)' }}>
        <CliErrorText error={{ kind: 'too-large', message: 'codeburn flow returned 41.7 MB, over the 32 MB read limit' }} />
      </div>
    </Panel>
  )
}
