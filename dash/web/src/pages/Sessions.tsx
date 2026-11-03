import { ContextExplorer } from '../components/ContextExplorer.js'

export interface SessionsProps {
  /**
   * The harness scope held by the shell's one selector. Sessions renders no
   * selector of its own; it filters by whatever the shell is holding.
   */
  activeHarness?: string
  /** Reports the session opened here so the shell can carry it to Context Doctor. */
  onSelectSession?: (sessionId: string | undefined) => void
}

/**
 * Sessions surface: wraps ContextExplorer so AgentSessionDashboard (and
 * TimelineView through it) is reachable without a Context tab.
 */
export function Sessions({ activeHarness, onSelectSession }: SessionsProps = {}) {
  return (
    <div className="flex flex-col gap-4" data-testid="page-sessions">
      <ContextExplorer activeHarness={activeHarness} onSelectSession={onSelectSession} />
    </div>
  )
}
