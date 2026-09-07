import { EmptyNote, Hint, ListRow, Panel, Sidebar, Stat, TopBar, Window } from 'codeburn-desktop'

const DETECTED = [
  { value: 'all', label: 'All providers' },
  { value: 'claude', label: 'Claude' },
  { value: 'codex', label: 'Codex' },
]

const NAV_HINTS = [
  { k: '⌘1-8', label: 'Navigate' },
  { k: '⌘,', label: 'Settings' },
  { k: '⌘R', label: 'Refresh' },
]

const noop = () => {}

/**
 * The whole shell, as the app composes it: the `.sb` rail and a `.ct` column
 * are `Window`'s two children. `Window` itself is only the `.win` flex row —
 * the split, the rounded clip and the canvas ground — so the content column is
 * assembled by the caller out of `.ct`, the top bar, a `.body`, and the footer.
 */
export function OverviewShell() {
  return (
    <Window>
      <Sidebar active="overview" onNavigate={noop} />
      <div className="ct">
        <TopBar
          title="Overview"
          scope="Last 30 days · Claude"
          period="30days"
          onPeriodChange={noop}
          customRange={null}
          onRangeSelect={noop}
          provider="claude"
          providerLabel="Claude"
          providerOptions={DETECTED}
          onProviderSelect={noop}
          configSource={null}
          onConfigSelect={noop}
        />
        <div className="body">
          <div className="stats">
            <Stat label="Spend" value="$184.20" delta="last 30 days" />
            <Stat label="Sessions" value="412" delta="14 projects" />
            <Stat label="Tokens" value="5.6M" delta="91% cache hit" />
            <Stat label="Per session" value="$0.45" delta="avg" />
          </div>
          <Panel title="Spend by project" right="See all ›" rightLink>
            <ListRow no="01" title="kyber-weave" sub="412 sessions" value="$184.20" />
            <ListRow no="02" title="codeburn" sub="288 sessions" value="$121.75" />
            <ListRow no="03" title="agentseal-site" sub="97 sessions" value="$38.40" />
          </Panel>
        </div>
        <Hint items={NAV_HINTS} right="refreshed 2m ago" />
      </div>
    </Window>
  )
}

/**
 * The same shell one destination over. Everything that identifies the screen —
 * the rail highlight, the bar title, the scope caption, the period pill and the
 * provider trigger — moves together; `Window` is unchanged between the two.
 */
export function SessionsShell() {
  return (
    <Window>
      <Sidebar active="sessions" onNavigate={noop} />
      <div className="ct">
        <TopBar
          title="Sessions"
          scope="Last 7 days · Codex"
          period="week"
          onPeriodChange={noop}
          customRange={null}
          onRangeSelect={noop}
          provider="codex"
          providerLabel="Codex"
          providerOptions={DETECTED}
          onProviderSelect={noop}
          configSource={null}
          onConfigSelect={noop}
        />
        <div className="body">
          <Panel title="Sessions" right="86 sessions · $41.08">
            <ListRow dotColor="#008300" title="Rework retrieval scoring" sub="gpt-5-3-codex · kyber-weave · 12m ago" value="$4.12" />
            <ListRow dotColor="#008300" title="Port the CLI date parser" sub="gpt-5-3-codex · codeburn · 1h ago" value="$0.94" />
            <ListRow dotColor="#008300" title="Trace the stale telemetry counter" sub="gpt-5-3-codex · codeburn · 3h ago" value="$2.61" />
            <ListRow dotColor="#8b93a1" title="Rename the ontology fixtures" sub="gpt-5-4-mini · kyber-weave · 5h ago" value="$0.18" />
          </Panel>
        </div>
        <Hint items={NAV_HINTS} right="refreshed 6m ago" />
      </div>
    </Window>
  )
}

/**
 * Not every destination takes the top bar. Settings owns its own header and
 * drops the footer strip too, so the content column here is just `.ct` and a
 * `.body` — the shell holds its shape from the rail alone.
 */
export function SettingsShell() {
  return (
    <Window>
      <Sidebar active="settings" onNavigate={noop} />
      <div className="ct">
        <div className="body">
          <Panel title="General">
            <ListRow title="Default period" value="Last 30 days" />
            <ListRow title="Currency" value="USD" />
            <ListRow title="Refresh cadence" value="Every 5 minutes" />
          </Panel>
          <Panel title="Claude config sources">
            <ListRow title="Default Claude" sub="/Users/dana/.claude" value="412 sessions" />
            <ListRow title="Claude Desktop" sub="/Users/dana/Library/Application Support/Claude" value="38 sessions" />
          </Panel>
        </div>
      </div>
    </Window>
  )
}

/**
 * First run, before anything has been scanned. The shell is fully painted and
 * every control is live — only the `.body` is empty — which is what keeps a
 * cold start reading as "nothing yet" instead of "nothing works". `scope` is
 * omitted here: with no scan behind it there is no filter summary to state, and
 * the bar simply drops the caption rather than showing an empty one.
 */
export function FirstRunShell() {
  return (
    <Window>
      <Sidebar active="overview" onNavigate={noop} />
      <div className="ct">
        <TopBar
          title="Overview"
          period="today"
          onPeriodChange={noop}
          customRange={null}
          onRangeSelect={noop}
          provider="all"
          providerLabel="All providers"
          providerOptions={DETECTED}
          onProviderSelect={noop}
          configSource={null}
          onConfigSelect={noop}
        />
        <div className="body">
          <Panel title="Overview" right="Today">
            <EmptyNote>No sessions today yet. Costs appear as soon as a coding session writes its log.</EmptyNote>
          </Panel>
        </div>
        <Hint items={NAV_HINTS} right="not refreshed yet" />
      </div>
    </Window>
  )
}
