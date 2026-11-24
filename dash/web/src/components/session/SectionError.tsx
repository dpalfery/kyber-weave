import { cn } from '../../lib/utils.js'

/**
 * One section's failure, stated as what is known: the route did not answer,
 * and here is the message it failed with. The other sections keep rendering
 * their own states; this card never claims more than its own route.
 */
export function SectionError({
  testId,
  label,
  message,
  onRetry,
  className,
}: {
  testId: string
  label: string
  message?: string | null
  onRetry?: () => void
  className?: string
}) {
  return (
    <div
      data-testid={testId}
      role="alert"
      className={cn('rounded-lg border border-red-500/30 bg-red-500/10 p-4 text-xs space-y-2', className)}
    >
      <div className="font-semibold text-red-600 dark:text-red-400">{label} could not be loaded.</div>
      {message ? <p className="text-tertiary-foreground">{message}</p> : null}
      {onRetry ? (
        <button
          type="button"
          onClick={onRetry}
          className="rounded bg-interactive-secondary px-3 py-1 font-medium text-foreground border border-border hover:bg-card transition-colors"
        >
          Retry
        </button>
      ) : null}
    </div>
  )
}
