import { CliErrorPanel } from 'codeburn-desktop'

/**
 * `kind: 'not-found'` is the only branch with its own body: two paragraphs that
 * name the `subject` the section wanted and spell out the install command in the
 * mono/accent code style. This is what every section renders on a fresh machine.
 */
export function CliMissing() {
  return (
    <div className="body">
      <CliErrorPanel error={{ kind: 'not-found', message: 'codeburn CLI not found' }} subject="spend" />
    </div>
  )
}

/**
 * `cold: true` outranks the kind. The first-run cache hydration is still walking
 * the session logs, so the timeout is a "not ready yet" and is painted muted —
 * never red.
 */
export function ColdHydration() {
  return (
    <div className="body">
      <CliErrorPanel
        error={{ kind: 'timeout', message: 'codeburn status produced no output for 45000ms', cold: true }}
        subject="your usage"
      />
    </div>
  )
}

/**
 * A `nonzero` whose stderr matches permission/Full Disk Access/EACCES is
 * re-titled and dropped to amber — the user can fix this one themselves, so it
 * does not get the failure colour.
 */
export function PermissionDenied() {
  return (
    <div className="body">
      <CliErrorPanel
        error={{ kind: 'nonzero', message: 'Cursor permission denied: grant Full Disk Access' }}
        subject="sessions"
      />
    </div>
  )
}

/**
 * The catch-all: any other kind keeps the raw CLI stderr verbatim under
 * "Couldn't read data", in red. `subject` is unused on this branch.
 */
export function ReadFailed() {
  return (
    <div className="body">
      <CliErrorPanel
        error={{ kind: 'nonzero', message: 'codeburn exited with code 1' }}
        subject="model usage"
      />
    </div>
  )
}

/**
 * The remaining kinds stacked in one column — they all land on the red
 * catch-all, so the message is the only thing that varies. Long stderr wraps
 * inside the `.pbody` without widening the card.
 */
export function RemainingKinds() {
  return (
    <div className="body">
      <CliErrorPanel error={{ kind: 'bad-json', message: 'codeburn produced output that was not valid JSON' }} subject="pull requests" />
      <CliErrorPanel error={{ kind: 'timeout', message: 'codeburn sessions produced no output for 20000ms' }} subject="sessions" />
      <CliErrorPanel error={{ kind: 'too-large', message: 'codeburn optimize returned 48.2 MB, over the 32 MB read limit' }} subject="optimize findings" />
      <CliErrorPanel error={{ kind: 'bad-args', message: 'unknown flag --provider for codeburn compare' }} subject="model comparisons" />
    </div>
  )
}
