import { cn } from '../../lib/utils'

export interface BaselineSelectProps {
  value?: string
  onChange?: (baseline: string) => void
  options?: Array<{ id: string; label: string }>
  className?: string
  disabled?: boolean
}

export const DEFAULT_BASELINES = [
  { id: 'none', label: 'No Baseline (Absolute)' },
  { id: 'previous_run', label: 'Previous Run' },
  { id: 'harness_median', label: 'Harness Median' },
  { id: 'workspace_median', label: 'Workspace Median (All Harnesses)' },
  { id: 'task_family_baseline', label: 'First Run in Task Family' },
]

export function BaselineSelect({
  value = 'none',
  onChange,
  options = DEFAULT_BASELINES,
  className,
  disabled = false,
}: BaselineSelectProps) {
  return (
    <div
      className={cn('flex items-center gap-1.5 text-xs text-muted-foreground', className)}
      data-testid="baseline-select-container"
    >
      <label htmlFor="baseline-select" className="text-[11px] font-medium uppercase tracking-wider text-tertiary-foreground">
        Baseline:
      </label>
      <select
        id="baseline-select"
        value={value}
        onChange={(e) => onChange?.(e.target.value)}
        disabled={disabled}
        data-testid="baseline-select"
        className={cn(
          'rounded-md border border-border bg-card px-2.5 py-1 text-xs text-foreground outline-none transition-colors',
          'hover:bg-interactive-secondary/50 focus:border-primary',
          disabled && 'opacity-50 cursor-not-allowed',
        )}
      >
        {options.map((opt) => (
          <option key={opt.id} value={opt.id}>
            {opt.label}
          </option>
        ))}
      </select>
    </div>
  )
}
