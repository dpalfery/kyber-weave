

export const NOT_MEASURABLE = 'not measurable'

/**
 * Renders the R10.2 unavailable-metric cell. Never renders zero;
 * the pinned phrase is the contract.
 */
export function NotMeasurable({
  reason,
  testId = 'not-measurable',
}: {
  reason?: string
  testId?: string
}) {
  return (
    <span data-testid={testId} className="text-tertiary-foreground">
      {NOT_MEASURABLE}
      {reason ? ` — ${reason}` : ''}
    </span>
  )
}
