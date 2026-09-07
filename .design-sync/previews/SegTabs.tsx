import { Panel, ListRow, SegTabs } from 'codeburn-desktop'

/** The CLI's `--period` vocabulary, verbatim (`PERIOD_OPTIONS` in TopBar). */
const PERIODS = [
  { value: 'today', label: 'Today' },
  { value: 'week', label: '7D' },
  { value: '30days', label: '30D' },
  { value: 'month', label: 'Month' },
  { value: 'all', label: '6M' },
  { value: 'lifetime', label: 'Life' },
]

/**
 * The period switcher the top bar owns. `value` matches one option's `value`,
 * and that pill alone gets `.on` — the accent underline, not a filled chip.
 */
export function Period() {
  return <SegTabs options={PERIODS} value="30days" onChange={() => {}} />
}

/**
 * The Models section's lens switcher. Three short labels is the control's
 * comfortable width; the `.seg` track hugs its options (`display: inline-flex`).
 */
export function ModelsLens() {
  return (
    <SegTabs
      options={[
        { value: 'model', label: 'By model' },
        { value: 'task', label: 'By task' },
        { value: 'audit', label: 'Audit' },
      ]}
      value="task"
      onChange={() => {}}
    />
  )
}

/**
 * Optimize folds each tab's headline number into its own label, so the control
 * doubles as a summary. Labels stay on one line — `.seg span` sets `nowrap`.
 */
export function TabsWithTotals() {
  return (
    <SegTabs
      options={[
        { value: 'waste', label: 'Waste $18.40' },
        { value: 'reverts', label: 'Reverts $6.12' },
        { value: 'abandoned', label: 'Abandoned $3.05' },
        { value: 'fixes', label: 'Fixes 14' },
      ]}
      value="reverts"
      onChange={() => {}}
    />
  )
}

/**
 * `style` lands on the `.seg` root. Sessions and Optimize both pass
 * `alignSelf: 'flex-start'` so the track does not stretch to the full width of
 * the column `.body` it sits in.
 */
export function SelfAligned() {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--sp-3)', width: 420 }}>
      <SegTabs
        options={[
          { value: 'cost', label: 'Cost' },
          { value: 'recent', label: 'Recent' },
          { value: 'turns', label: 'Turns' },
          { value: 'tokens', label: 'Tokens' },
        ]}
        value="recent"
        onChange={() => {}}
        style={{ alignSelf: 'flex-start' }}
      />
      <Panel title="Sessions">
        <ListRow title="Rework retrieval scoring" sub="claude-opus-5 · 12m ago" value="$4.12" />
        <ListRow title="Port the CLI date parser" sub="claude-sonnet-5 · 1h ago" value="$0.94" />
      </Panel>
    </div>
  )
}

/**
 * The empty selection. When a custom date range is active the top bar passes
 * `value=""`, which matches no option, so every pill reads as unselected.
 */
export function NoSelection() {
  return <SegTabs options={PERIODS} value="" onChange={() => {}} />
}
