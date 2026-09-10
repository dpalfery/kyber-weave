import { Stat } from 'codeburn-desktop'

/**
 * The canonical composition: a `.stats` grid of `.panel.stat` cards, as the
 * expanded session detail renders it. Four across is the grid's own template.
 */
export function SessionDetail() {
  return (
    <div className="stats">
      <Stat label="Cost" value="$4.12" delta="this session" />
      <Stat label="Calls" value="1,204" delta="API calls" />
      <Stat label="Turns" value="86" delta="assistant turns" />
      <Stat label="Saved" value="$1.38" delta="vs baseline" />
    </div>
  )
}

/** Token counters: compact values, and a computed delta on the cache card. */
export function TokenBreakdown() {
  return (
    <div className="stats">
      <Stat label="Input" value="1.2M" delta="tokens sent" />
      <Stat label="Output" value="318K" delta="tokens generated" />
      <Stat label="Cache read" value="4.0M" delta="91% hit" />
      <Stat label="Cache write" value="212K" delta="tokens cached" />
    </div>
  )
}

/** `delta` omitted — the card collapses to label strip + value, no second line. */
export function NoDelta() {
  return (
    <div className="stats">
      <Stat label="Projects" value="14" />
      <Stat label="Models" value="3" />
      <Stat label="Sessions" value="412" />
      <Stat label="Providers" value="2" />
    </div>
  )
}

/** `value` is a ReactNode: a trailing `<small>` is the unit/qualifier idiom. */
export function ValueWithUnit() {
  return (
    <div className="stats">
      <Stat label="Month to date" value={<>$184.20 <small>USD</small></>} delta="1 – 4 Sep" />
      <Stat label="Projected" value={<>$412.80 <small>est</small></>} delta="$228.60 to go" />
      <Stat label="Per session" value={<>$0.45 <small>avg</small></>} delta="412 sessions" />
      <Stat label="Burn rate" value={<>$46.05 <small>/day</small></>} delta="last 7 days" />
    </div>
  )
}
