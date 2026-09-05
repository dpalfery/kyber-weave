import { Panel, ListRow } from 'codeburn-desktop'

/** Ranked rows: zero-padded `no`, title + sub line, right-aligned value. */
export function Ranked() {
  return (
    <Panel title="Top projects">
      <ListRow no="01" title="kyber-weave" sub="412 sessions" value="$184.20" />
      <ListRow no="02" title="codeburn" sub="288 sessions" value="$121.75" />
      <ListRow no="03" title="agentseal-site" sub="97 sessions" value="$38.40" />
    </Panel>
  )
}

/** `dotColor` marks the model series each row belongs to. */
export function WithSeriesDot() {
  return (
    <Panel title="Spend by model">
      <ListRow dotColor="#2a78d6" title="claude-opus-5" sub="1,204 calls" value="$96.10" />
      <ListRow dotColor="#e87ba4" title="claude-sonnet-5" sub="8,930 calls" value="$54.32" />
      <ListRow dotColor="#eda100" title="claude-haiku-4.5" sub="21,447 calls" value="$7.88" />
    </Panel>
  )
}

/** With `onClick` the row becomes a keyboard-operable button and shows a chevron. */
export function Interactive() {
  return (
    <Panel title="Sessions">
      <ListRow title="Collapsed row" sub="click to expand" value="$12.40" expanded={false} onClick={() => {}} />
      <ListRow title="Expanded row" sub="chevron rotates" value="$8.05" expanded onClick={() => {}} />
    </Panel>
  )
}

/** Without `onClick` the row is inert — no chevron, no hover affordance. */
export function Inert() {
  return (
    <Panel title="Breakdown">
      <ListRow title="Input tokens" value="1,204,880" />
      <ListRow title="Output tokens" value="318,402" />
      <ListRow title="Cache reads" value="4,006,113" />
    </Panel>
  )
}
