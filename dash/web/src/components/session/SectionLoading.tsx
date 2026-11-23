/**
 * One section's in-flight state: a status line, not a figure. A section that
 * has not answered never renders a value that could read as data.
 */
export function SectionLoading({ testId, label }: { testId: string; label: string }) {
  return (
    <div
      data-testid={testId}
      role="status"
      aria-live="polite"
      className="rounded-lg border border-border bg-card p-6 text-xs text-tertiary-foreground"
    >
      Loading {label}…
    </div>
  )
}
