import { Panel, ListRow, StaleBanner, SwitchingBanner } from 'codeburn-desktop'

/**
 * The state it exists for: the user just switched period, the memo still
 * serves the previous answer, and the strip says so in accent rather than
 * warn — the numbers below are useful but not yet the answer for "7d".
 */
export function WhileThePeriodSettles() {
  return (
    <div className="body">
      <SwitchingBanner />
      <Panel title="Spend by model" right="7d">
        <ListRow dotColor="#2a78d6" title="claude-opus-5" sub="1,204 calls" value="$96.10" />
        <ListRow dotColor="#e87ba4" title="claude-sonnet-5" sub="8,930 calls" value="$54.32" />
        <ListRow dotColor="#eda100" title="claude-haiku-4.5" sub="21,447 calls" value="$7.88" />
      </Panel>
    </div>
  )
}

/**
 * Both strips at once, the way Models and Optimize render them: switching
 * (accent) above stale (warn), so the two never read as the same notice.
 */
export function StackedWithStaleBanner() {
  return (
    <div className="body">
      <SwitchingBanner />
      <StaleBanner error={{ kind: 'nonzero', message: 'codeburn exited 1' }} />
      <Panel title="Top projects" right="Last 30 days">
        <ListRow no="01" title="kyber-weave" sub="412 sessions" value="$184.20" />
        <ListRow no="02" title="codeburn" sub="288 sessions" value="$121.75" />
      </Panel>
    </div>
  )
}

/** The strip alone — same geometry as StaleBanner, accent rule and text. */
export function Bare() {
  return <SwitchingBanner />
}
