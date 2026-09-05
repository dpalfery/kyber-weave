import { Panel, ListRow, StaleBanner } from 'codeburn-desktop'

/**
 * The real composition: a muted strip directly above the section body it
 * qualifies. The data below is last-good and still worth reading, so the
 * banner never takes the visual weight of an error panel.
 */
export function AboveLastGoodData() {
  return (
    <div className="body">
      <StaleBanner error={{ kind: 'nonzero', message: 'codeburn exited 1' }} />
      <Panel title="Spend by project" right="Last 30 days">
        <ListRow no="01" title="kyber-weave" sub="412 sessions" value="$184.20" />
        <ListRow no="02" title="codeburn" sub="288 sessions" value="$121.75" />
        <ListRow no="03" title="agentseal-site" sub="97 sessions" value="$38.40" />
      </Panel>
    </div>
  )
}

/** A slow scan that outlived its budget — the poll gave up, the table did not. */
export function ScanTimedOut() {
  return (
    <div className="body">
      <StaleBanner error={{ kind: 'timeout', message: 'codeburn models timed out after 20s' }} />
      <Panel title="Spend by model">
        <ListRow dotColor="#2a78d6" title="claude-opus-5" sub="1,204 calls" value="$96.10" />
        <ListRow dotColor="#e87ba4" title="claude-sonnet-5" sub="8,930 calls" value="$54.32" />
        <ListRow dotColor="#eda100" title="claude-haiku-4.5" sub="21,447 calls" value="$7.88" />
      </Panel>
    </div>
  )
}

/** Long CLI stderr wraps to a second line and keeps the left warn rule. */
export function LongCliMessage() {
  return (
    <div className="body">
      <StaleBanner error={{ kind: 'bad-json', message: 'unexpected token at position 4193 while parsing `codeburn sessions --json` output' }} />
      <Panel title="Recent sessions">
        <ListRow title="Refactor retrieval index" sub="claude-opus-5 · 2h ago" value="$4.12" />
        <ListRow title="Fix flaky lock test" sub="claude-sonnet-5 · 5h ago" value="$0.88" />
      </Panel>
    </div>
  )
}

/** The strip on its own — 11.5px muted text behind a 2px warn rule. */
export function Bare() {
  return <StaleBanner error={{ kind: 'not-found', message: 'codeburn CLI not found on PATH' }} />
}
