import { TopBar } from 'codeburn-desktop'

/** The top bar's option set: the aggregate sentinel, then each detected CLI. */
const DETECTED = [
  { value: 'all', label: 'All providers' },
  { value: 'claude', label: 'Claude' },
  { value: 'codex', label: 'Codex' },
  { value: 'cursor', label: 'Cursor' },
]

/** Two Claude config folders on this machine, as the overview reports them.
 *  A label is the folder's own name; `~/.claude` alone gets "Default Claude". */
const CLAUDE_CONFIGS = {
  selectedId: 'claude-config:6f2a',
  options: [
    { id: 'claude-config:9c14', label: 'Default Claude', path: '/Users/dana/.claude' },
    { id: 'claude-config:6f2a', label: 'claude-work', path: '/Users/dana/.claude-work' },
  ],
}

const noop = () => {}

/**
 * The default bar. `title` names the destination, `scope` restates the active
 * filters as one caption, and the spacer pushes period + provider right.
 */
export function Overview() {
  return (
    <TopBar
      title="Overview"
      scope="Today · All providers"
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
  )
}

/**
 * Narrowed to one provider over a longer window. The scope caption is built
 * from the same two facts the controls show — period label, then provider.
 */
export function ScopedToProvider() {
  return (
    <TopBar
      title="Models"
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
  )
}

/**
 * A `customRange` takes over from the period pills: the bar passes `""` to the
 * SegTabs so nothing reads as selected, and the calendar trigger turns `.on`
 * and grows a formatted label instead of showing the bare icon.
 */
export function CustomRange() {
  return (
    <TopBar
      title="Spend"
      scope="Apr 1 – 30 · Codex"
      period="30days"
      onPeriodChange={noop}
      customRange={{ from: '2026-04-01', to: '2026-04-30' }}
      onRangeSelect={noop}
      provider="codex"
      providerLabel="Codex"
      providerOptions={DETECTED}
      onProviderSelect={noop}
      configSource={null}
      onConfigSelect={noop}
    />
  )
}

/**
 * Passing `claudeConfigs` appends a fourth control — the Claude config-source
 * picker — and the active folder is echoed as the third term of the scope
 * caption. Omit the prop and the bar has three controls, as above.
 */
export function WithClaudeConfigs() {
  return (
    <TopBar
      title="Spend"
      scope="Today · Claude · claude-work"
      period="today"
      onPeriodChange={noop}
      customRange={null}
      onRangeSelect={noop}
      provider="claude"
      providerLabel="Claude"
      providerOptions={DETECTED}
      onProviderSelect={noop}
      claudeConfigs={CLAUDE_CONFIGS}
      configSource="claude-config:6f2a"
      onConfigSelect={noop}
    />
  )
}

/**
 * Combined scope reports unfiltered all-device usage, so the caption spends its
 * second term on "Combined" rather than a provider name. Lifetime is the widest
 * period pill set the bar carries.
 */
export function CombinedScope() {
  return (
    <TopBar
      title="Compare"
      scope="Lifetime · Combined"
      period="lifetime"
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
  )
}
