import { ContextExplorer, type ExplorerProvider } from '../components/ContextExplorer'

export interface SessionsProps {
  activeHarness?: string
  onHarnessChange?: (h: ExplorerProvider) => void
}

/**
 * Sessions surface: wraps ContextExplorer so AgentSessionDashboard (and
 * TimelineView through it) is reachable without a Context tab.
 */
export function Sessions({ activeHarness, onHarnessChange }: SessionsProps = {}) {
  return (
    <div className="flex flex-col gap-4" data-testid="page-sessions">
      <ContextExplorer activeHarness={activeHarness} onHarnessChange={onHarnessChange} />
    </div>
  )
}
