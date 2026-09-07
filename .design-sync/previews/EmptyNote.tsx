import { Panel, EmptyNote } from 'codeburn-desktop'

/** The default: one muted line where a panel's rows would be. */
export function InPanel() {
  return (
    <Panel title="Sessions" right="Last 30 days">
      <EmptyNote>No sessions in this range yet.</EmptyNote>
    </Panel>
  )
}

/** The same note carries the in-flight message while a section is correlating. */
export function Loading() {
  return (
    <Panel title="Yield" right="Last 30 days">
      <EmptyNote>Correlating sessions with git…</EmptyNote>
    </Panel>
  )
}

/** A filter miss quotes the query back, so the note reads as a result not a void. */
export function FilteredOut() {
  return (
    <Panel title="Sessions" right="284 hidden by filter">
      <EmptyNote>No sessions match &quot;retrieval index&quot;.</EmptyNote>
    </Panel>
  )
}

/** Long-form: the note wraps and explains why the panel is empty and what to do. */
export function Explanatory() {
  return (
    <Panel title="Pull requests" right="Last 30 days">
      <EmptyNote>
        No sessions in the last 30 days mentioned a pull request URL. Spend is attributed only
        when a transcript contains a github.com/…/pull/N link. Lifetime has 1,284 pull requests.
        Switch the period control to Life.
      </EmptyNote>
    </Panel>
  )
}

/** The unavailable-data flavour, next to a panel that did resolve. */
export function Unavailable() {
  return (
    <Panel title="Optimize" right="claude-opus-5">
      <EmptyNote>Yield data is unavailable right now.</EmptyNote>
    </Panel>
  )
}
