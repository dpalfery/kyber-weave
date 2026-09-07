import { ErrorBoundary, Panel, ListRow, Stat } from 'codeburn-desktop'

/**
 * The stories trigger the boundary the way the app does — by rendering a child
 * that throws — rather than faking its error UI. The failure modelled here is the
 * real one: a pinned CLI sends a payload the newer renderer indexes into. React
 * logs the caught error to the console; that is the boundary working.
 */
function ModelsSection(): never {
  throw new TypeError("Cannot read properties of undefined (reading 'topModels')")
}

function SessionRow(): never {
  throw new TypeError("Cannot read properties of null (reading 'costUSD')")
}

function SessionList() {
  return (
    <Panel title="Recent sessions">
      <SessionRow />
    </Panel>
  )
}

function SessionsSection() {
  return (
    <div className="body">
      <SessionList />
    </div>
  )
}

/** Nothing threw: the boundary is invisible and renders its children verbatim. */
export function HealthyScreen() {
  return (
    <ErrorBoundary>
      <div className="body">
        <Panel title="Spend by project" right="Last 30 days">
          <ListRow no="01" title="kyber-weave" sub="412 sessions" value="$184.20" />
          <ListRow no="02" title="codeburn" sub="288 sessions" value="$121.75" />
          <ListRow no="03" title="agentseal-site" sub="97 sessions" value="$38.40" />
        </Panel>
      </div>
    </ErrorBoundary>
  )
}

/**
 * The caught state: title, the thrown message in `--bad`, the component stack in
 * a mono block, and the Reload button. One crashing section shows this instead of
 * white-screening the whole window.
 */
export function CaughtSectionCrash() {
  return (
    <ErrorBoundary>
      <ModelsSection />
    </ErrorBoundary>
  )
}

/**
 * The same boundary around a deeper tree — the throw is three components inside a
 * Panel — so `.error-stack` carries a longer component stack and scrolls at its
 * 320px cap.
 */
export function NestedSectionCrash() {
  return (
    <ErrorBoundary>
      <SessionsSection />
    </ErrorBoundary>
  )
}

/**
 * Boundaries are keyed per section in App, so a sibling that renders fine is
 * untouched by the one that crashed — the stats strip keeps its data while the
 * models panel below is replaced by the error card.
 */
export function OneSectionOfTwo() {
  return (
    <div className="body">
      <ErrorBoundary>
        <div className="stats">
          <Stat label="Spend" value="$12.48" delta="today" />
          <Stat label="Sessions" value="31" delta="across 4 projects" />
        </div>
      </ErrorBoundary>
      <ErrorBoundary>
        <ModelsSection />
      </ErrorBoundary>
    </div>
  )
}
