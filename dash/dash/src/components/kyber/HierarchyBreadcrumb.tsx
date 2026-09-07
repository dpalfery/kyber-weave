import { cn } from '../../lib/utils'

export interface HierarchyBreadcrumbProps {
  harness?: string | null
  runId?: string | null
  executionId?: string | null
  turnIndex?: number | null
  itemKey?: string | null
  onSelectAll?: () => void
  onSelectHarness?: (harness: string) => void
  onSelectRun?: (runId: string) => void
  onSelectExecution?: (executionId: string) => void
  onSelectTurn?: (turnIndex: number) => void
  onSelectItem?: (itemKey: string) => void
  className?: string
}

export function HierarchyBreadcrumb({
  harness,
  runId,
  executionId,
  turnIndex,
  itemKey,
  onSelectAll,
  onSelectHarness,
  onSelectRun,
  onSelectExecution,
  onSelectTurn,
  onSelectItem,
  className,
}: HierarchyBreadcrumbProps) {
  const levels: Array<{
    level: number
    label: string
    shortLabel?: string
    isCurrent: boolean
    onClick?: () => void
    tag: string
    testId: string
  }> = []

  // Level 1: All Harnesses
  const isAllCurrent = !harness && !runId && !executionId && turnIndex === undefined && !itemKey
  levels.push({
    level: 1,
    label: 'All Harnesses',
    shortLabel: 'All',
    isCurrent: isAllCurrent,
    onClick: isAllCurrent ? undefined : onSelectAll,
    tag: 'Workspace',
    testId: 'breadcrumb-all',
  })

  // Level 2: Harness
  if (harness) {
    const isHarnessCurrent = !runId && !executionId && turnIndex === undefined && !itemKey
    levels.push({
      level: 2,
      label: harness,
      isCurrent: isHarnessCurrent,
      onClick: isHarnessCurrent ? undefined : () => onSelectHarness?.(harness),
      tag: 'Harness',
      testId: 'breadcrumb-harness',
    })
  }

  // Level 3: Run
  if (runId) {
    const isRunCurrent = !executionId && turnIndex === undefined && !itemKey
    const shortRun = runId.length > 10 ? `${runId.slice(0, 8)}…` : runId
    levels.push({
      level: 3,
      label: `Run ${shortRun}`,
      shortLabel: shortRun,
      isCurrent: isRunCurrent,
      onClick: isRunCurrent ? undefined : () => onSelectRun?.(runId),
      tag: 'Run',
      testId: 'breadcrumb-run',
    })
  }

  // Level 4: AgentExecution
  if (executionId) {
    const isExecCurrent = turnIndex === undefined && !itemKey
    const shortExec = executionId.length > 10 ? `${executionId.slice(0, 8)}…` : executionId
    levels.push({
      level: 4,
      label: `Execution ${shortExec}`,
      shortLabel: shortExec,
      isCurrent: isExecCurrent,
      onClick: isExecCurrent ? undefined : () => onSelectExecution?.(executionId),
      tag: 'Execution',
      testId: 'breadcrumb-execution',
    })
  }

  // Level 5: Turn
  if (turnIndex !== undefined && turnIndex !== null) {
    const isTurnCurrent = !itemKey
    levels.push({
      level: 5,
      label: `Turn ${turnIndex}`,
      isCurrent: isTurnCurrent,
      onClick: isTurnCurrent ? undefined : () => onSelectTurn?.(turnIndex),
      tag: 'Turn',
      testId: 'breadcrumb-turn',
    })
  }

  // Level 6: ContextItem
  if (itemKey) {
    levels.push({
      level: 6,
      label: itemKey,
      isCurrent: true,
      onClick: () => onSelectItem?.(itemKey),
      tag: 'Item',
      testId: 'breadcrumb-item',
    })
  }

  return (
    <nav
      aria-label="Breadcrumb"
      className={cn('flex items-center gap-1 text-xs text-muted-foreground', className)}
      data-testid="hierarchy-breadcrumb"
    >
      <ol className="flex items-center gap-1 flex-wrap">
        {levels.map((item, idx) => (
          <li key={item.level} className="flex items-center gap-1">
            {idx > 0 && (
              <span className="text-tertiary-foreground/60 select-none px-0.5" aria-hidden="true">
                /
              </span>
            )}
            {item.isCurrent ? (
              <span
                aria-current="page"
                data-testid={item.testId}
                className="flex items-center gap-1 rounded bg-interactive-secondary px-2 py-0.5 font-medium text-foreground shadow-xs"
              >
                <span className="text-[10px] uppercase tracking-wider text-primary font-semibold">
                  {item.tag}:
                </span>
                <span className="truncate max-w-[200px]" title={item.label}>
                  {item.label}
                </span>
              </span>
            ) : item.onClick ? (
              <button
                type="button"
                onClick={item.onClick}
                data-testid={item.testId}
                className="flex items-center gap-1 rounded px-1.5 py-0.5 transition-colors hover:bg-interactive-secondary hover:text-foreground text-tertiary-foreground"
              >
                <span className="text-[9.5px] uppercase tracking-wider text-tertiary-foreground/80 font-medium">
                  {item.tag}:
                </span>
                <span className="truncate max-w-[150px]" title={item.label}>
                  {item.label}
                </span>
              </button>
            ) : (
              <span
                data-testid={item.testId}
                className="flex items-center gap-1 px-1.5 py-0.5 text-tertiary-foreground"
              >
                <span className="text-[9.5px] uppercase tracking-wider text-tertiary-foreground/80 font-medium">
                  {item.tag}:
                </span>
                <span className="truncate max-w-[150px]" title={item.label}>
                  {item.label}
                </span>
              </span>
            )}
          </li>
        ))}
      </ol>
    </nav>
  )
}
