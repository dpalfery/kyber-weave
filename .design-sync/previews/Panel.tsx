import { Panel, ListRow, EmptyNote } from 'codeburn-desktop'

/** The canonical card: title strip + body content. */
export function Titled() {
  return (
    <Panel title="Spend by project">
      <ListRow no="01" title="kyber-weave" sub="412 sessions" value="$184.20" />
      <ListRow no="02" title="codeburn" sub="288 sessions" value="$121.75" />
      <ListRow no="03" title="agentseal-site" sub="97 sessions" value="$38.40" />
    </Panel>
  )
}

/** `right` renders a note in the head strip; `rightLink` styles it as an action. */
export function WithAction() {
  return (
    <Panel title="Recent sessions" right="See all ›" rightLink>
      <ListRow title="Refactor retrieval index" sub="Opus · 2h ago" value="$4.12" />
      <ListRow title="Fix flaky lock test" sub="Sonnet · 5h ago" value="$0.88" />
    </Panel>
  )
}

/** No `title` — a bare card with only the `.pbody` content well. */
export function Untitled() {
  return (
    <Panel>
      <p style={{ margin: 0, fontSize: 12.5 }}>
        Costs are computed locally from your session logs. Nothing is uploaded.
      </p>
    </Panel>
  )
}

/** The empty state a panel shows before any data has been scanned. */
export function Empty() {
  return (
    <Panel title="Pull requests" right="Last 30 days">
      <EmptyNote>No pull requests in this range.</EmptyNote>
    </Panel>
  )
}
